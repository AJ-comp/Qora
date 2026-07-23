namespace Qora.Ir.Passes;

/// <summary>
/// Desugars a measurement written INSIDE a condition into the two-step form the target (OpenQASM 3) needs:
/// a hoisted <c>bit</c> declaration measures the qubit, and the condition tests that bit.
///
/// <code>
///   if (M(q[0]) == 1) { … }          →   bit __m0 = M(q[0]);
///                                        if (__m0 == 1) { … }
/// </code>
///
/// OpenQASM has no measurement EXPRESSION (<c>measure</c> is a statement), so a measurement can never sit in
/// a condition/angle/value slot. Rather than reject it (the old QSEM005), we lower it here — a source
/// convenience (Q#-style <c>if M(q) == One</c>) that still emits valid OQ3. The grammar already parses a call
/// in a condition; this pass runs BEFORE validation, so the validator only ever sees the desugared form.
///
/// A COMMON pass, though OpenQASM motivated it: the desugared shape is the IR invariant "a condition is a
/// call-free classical expression", and every tree checker (guard reading, bounds proofs, QSEM005) leans
/// on it — any future backend receives the same canonical form, so this pass outlives the constraint.
///
/// Placement mirrors the loop's evaluation point:
/// <list type="bullet">
///   <item><b>if</b> — the temp declarations go immediately before the <c>if</c>.</item>
///   <item><b>while</b> — the condition is re-evaluated every iteration, so the temp is declared before the
///         loop (first check) AND re-measured at the END of the body (subsequent checks).</item>
///   <item><b>repeat…until</b> — <c>until</c> runs after the body, so the temp measurement goes at the END of
///         the body (where <c>until</c> can see it).</item>
/// </list>
/// Only <c>M(reg[index])</c> — the one registered measurement (no user op may be named <c>M</c>, QSEM013) — is
/// extracted; any OTHER call left in a condition keeps <see cref="QCond.HasCall"/> set and is still rejected
/// by QSEM005.
///
/// Temp names are minted as <see cref="HoistName"/> PLACEHOLDERS carrying the base <c>__mN</c>. A user can
/// never write a placeholder (its <c>#</c> is not a legal identifier character), so a synthetic temp can
/// never share a spelling with a user name — the <see cref="NameMangler"/> prettifies each placeholder back
/// to <c>__mN</c> (or <c>__mN_</c> only if the user genuinely declared that name too). This closes a masking
/// hole: a temp literally named <c>__m0</c> used to become a real declaration BEFORE validation, so a user's
/// undeclared <c>__m0 = …</c> silently bound to it and its <c>QSEM025</c> was lost; now the user's <c>__m0</c>
/// stays undeclared and is reported. Uniqueness is the placeholder's uid, so no name-scanning is needed.
/// </summary>
public static class MeasureConditionLowering
{
    public static QProgram Run(QProgram program)
    {
        var uid = 0;   // program-wide: makes each placeholder a distinct spelling by construction
        var ops = program.Operations.Select(op =>
        {
            var n = 0;   // per-op base counter, so the mangler emits __m0, __m1, … within each scope
            string Fresh() => HoistName.Make($"__m{n++}", uid++);
            return op with { Body = Lower(op.Body, Fresh) };
        }).ToList();
        return program with { Operations = ops };
    }

    private static List<QStmt> Lower(IReadOnlyList<QStmt> stmts, Func<string> fresh)
    {
        var result = new List<QStmt>();
        foreach (var s in stmts)
        {
            switch (s)
            {
                case QIf i:
                {
                    var (temps, cond) = Extract(i.Cond, i.Span, fresh);
                    result.AddRange(temps);                                   // measure before the branch
                    result.Add(new QIf(cond, Lower(i.Then, fresh), Lower(i.Else, fresh)) { Span = i.Span });
                    break;
                }
                case QWhile w:
                {
                    var body = Lower(w.Body, fresh);
                    var (temps, cond) = Extract(w.Cond, w.Span, fresh);
                    if (temps.Count > 0)
                    {
                        result.AddRange(temps);                              // first check: measure before the loop
                        var reMeasure = temps.Select(d => (QStmt)new QAssign(d.Name, d.Value) { Span = w.Span });
                        body = body.Concat(reMeasure).ToList();              // each iteration: re-measure at the end
                    }
                    result.Add(new QWhile(cond, body) { Span = w.Span });
                    break;
                }
                case QRepeat r:
                {
                    var body = Lower(r.Body, fresh);
                    var (temps, cond) = Extract(r.Until, r.Span, fresh);
                    if (temps.Count > 0) body = body.Concat(temps).ToList();  // until sees body-local names
                    result.Add(new QRepeat(body, cond) { Span = r.Span });
                    break;
                }
                case QFor f:
                    result.Add(f with { Body = Lower(f.Body, fresh) });
                    break;
                case QConjugate c:
                    result.Add(c with { Within = Lower(c.Within, fresh), Apply = Lower(c.Apply, fresh) });
                    break;
                default:
                    result.Add(s);
                    break;
            }
        }
        return result;
    }

    /// <summary>Pull every <c>M(reg[index])</c> out of a condition into a fresh <c>bit</c> declaration and
    /// replace its node with that bit's name — one structural rewrite of the condition tree, left to right.
    /// A non-measurement call node is left in place, so <see cref="QCond.HasCall"/> (derived from the tree)
    /// stays set and the validator still rejects it (QSEM005).</summary>
    private static (List<QDecl> Temps, QCond Cond) Extract(QCond cond, QSpan? span, Func<string> fresh)
    {
        if (cond.Tree is null || !cond.HasCall) return (new List<QDecl>(), cond);

        var temps = new List<QDecl>();
        QNode Rewrite(QNode node) => node switch
        {
            // only the registered measurement of an indexed target extracts (`M(q)` whole-register stays —
            // it has no two-step lowering — and keeps HasCall set for QSEM005).
            QCallNode { Name: QoraGates.Measurement, Args: [QIndexNode { Base: QNameRef reg, Index: { } idx }] } =>
                Hoist(reg.Name, idx),
            QBinOp b => b with { Left = Rewrite(b.Left), Right = Rewrite(b.Right) },
            QUnary u => u with { Operand = Rewrite(u.Operand) },
            QMember m => m with { Base = Rewrite(m.Base) },
            QIndexNode i => i with { Base = Rewrite(i.Base), Index = Rewrite(i.Index) },
            QCallNode c => c with { Args = c.Args.Select(Rewrite).ToList() },
            _ => node,
        };
        QNode Hoist(string reg, QNode index)
        {
            var name = fresh();
            temps.Add(new QDecl(false, QType.Bit, name,
                new QMeasure(new QIndexNode(new QNameRef(reg), index))) { Span = span });
            return new QNameRef(name);
        }

        var tree = Rewrite(cond.Tree);
        return temps.Count == 0
            ? (new List<QDecl>(), cond)          // HasCall was some non-M call; leave it for QSEM005
            : (temps, new QCond(tree));
    }
}
