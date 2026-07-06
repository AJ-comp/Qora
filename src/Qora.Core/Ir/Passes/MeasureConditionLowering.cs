using System.Text.RegularExpressions;

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
/// by QSEM005. Temp names (<c>__m0</c>, …) are minted per operation, skipping any name the operation already
/// declares (so they never collide as QSEM015); per-op uniqueness suffices because mangler scopes are per-op
/// (the entry op is the global scope, each def its own), and the NameMangler resolves any remaining clash.
/// </summary>
public static class MeasureConditionLowering
{
    // `M(` with a word boundary before it — never matches `Measure(` (M followed by 'e') or a suffix like
    // `fooM(` (no boundary), and no user op can be named `M`. Groups: 1 = register, 2 = index.
    private static readonly Regex Measurement = new(@"\bM\((\w+)\[([^\]]*)\]\)", RegexOptions.Compiled);

    public static QProgram Run(QProgram program)
    {
        var ops = program.Operations.Select(op =>
        {
            // A temp must not reuse a name already DECLARED in the op, or the two same-scope declarations
            // would collide as QSEM015 and wrongly reject a valid program. `taken` seeds with the op's
            // declared names and grows as temps are minted, so every `__mN` is fresh.
            var taken = DeclaredNames(op);
            var n = 0;
            string Fresh() { string name; do name = $"__m{n++}"; while (!taken.Add(name)); return name; }
            return op with { Body = Lower(op.Body, Fresh) };
        }).ToList();
        return program with { Operations = ops };
    }

    /// <summary>Every name DECLARED anywhere in the operation (params, <c>use</c> registers, decls, loop
    /// variables) — the set a synthetic temp must avoid to not collide (QSEM015).</summary>
    private static HashSet<string> DeclaredNames(QOperation op)
    {
        var names = new HashSet<string>(op.Params.Select(p => p.Name));
        void Walk(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: names.Add(u.Name); break;
                    case QDecl d: names.Add(d.Name); break;
                    case QFor f: names.Add(f.Var); Walk(f.Body); break;
                    case QIf i: Walk(i.Then); Walk(i.Else); break;
                    case QWhile w: Walk(w.Body); break;
                    case QRepeat r: Walk(r.Body); break;
                    case QConjugate c: Walk(c.Within); Walk(c.Apply); break;
                }
        }
        Walk(op.Body);
        return names;
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
    /// replace it with that bit's name. Returns the temp declarations and the rewritten condition (with
    /// <see cref="QCond.HasCall"/> recomputed — still set if a non-measurement call remains, for QSEM005).</summary>
    private static (List<QDecl> Temps, QCond Cond) Extract(QCond cond, QSpan? span, Func<string> fresh)
    {
        if (!cond.HasCall) return (new List<QDecl>(), cond);

        var temps = new List<QDecl>();
        var text = Measurement.Replace(cond.Text, m =>
        {
            var name = fresh();
            temps.Add(new QDecl(false, QType.Bit, name,
                new QMeasure(new QQubitArg(m.Groups[1].Value, m.Groups[2].Value))) { Span = span });
            return name;
        });

        if (temps.Count == 0) return (new List<QDecl>(), cond);   // HasCall was some non-M call; leave it for QSEM005

        // A remaining `name(` is a non-measurement call the validator must still reject; otherwise the
        // condition is now call-free.
        var stillHasCall = Regex.IsMatch(text, @"[A-Za-z_]\w*\s*\(");
        return (temps, new QCond(text, stillHasCall));
    }
}
