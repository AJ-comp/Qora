using System.Text.RegularExpressions;

namespace Qora.Ir.Passes;

/// <summary>
/// Monomorphization — specializes GENERIC operations (a symbolic register size, <c>Qubit[n] q</c>) into
/// concrete per-size copies, one per distinct size actually used at a call site. Modeled on C++ templates
/// / Rust generics: the size is bound from the actual register argument at each call, substituted through
/// the body (register sizes, <c>for</c> bounds, and any expression that uses <c>n</c>), and the call is
/// rewritten to the specialization. Runs right before <see cref="NameMangler"/>/<see cref="QasmEmitter"/>,
/// so once it is done EVERY operation is concrete (no <see cref="QParam.SizeParam"/>) and the emitter and
/// <see cref="Inverter"/> never see a symbolic size — <c>Adjoint HAll(q)</c> works for free because the
/// call name is rewritten to the concrete specialization before inversion runs.
///
/// A generic op that is never called simply vanishes (an uninstantiated template emits no def). Qora
/// forbids recursion (QSEM011), so the specialization recursion always terminates.
/// </summary>
public static class Monomorphizer
{
    public static QProgram Run(QProgram program)
    {
        static bool IsGeneric(QOperation o) => o.Params.Any(p => p.SizeParam is not null);

        var generic = program.Operations.Where(IsGeneric).ToDictionary(o => o.Name);
        if (generic.Count == 0) return program;   // no generics — nothing to specialize

        var concrete = program.Operations.Where(o => !IsGeneric(o)).ToList();
        var allNames = new HashSet<string>(program.Operations.Select(o => o.Name));
        var specs = new Dictionary<string, QOperation>();        // specialization name -> op
        var specNameByKey = new Dictionary<string, string>();    // "Op|n=4" -> specialization name

        // Rewrite a body, tracking each in-scope register's concrete size, replacing every call to a
        // generic op with a call to its (created-on-demand) size specialization.
        List<QStmt> Rewrite(IReadOnlyList<QStmt> stmts, Dictionary<string, int> regs)
        {
            var outp = new List<QStmt>(stmts.Count);
            foreach (var s in stmts)
            {
                switch (s)
                {
                    case QUse u:
                        regs[u.Name] = u.Size;
                        outp.Add(u);
                        break;
                    case QGate g when generic.ContainsKey(g.Name):
                        outp.Add(SpecializeCall(g, regs));
                        break;
                    case QIf i:
                        outp.Add(i with { Then = Rewrite(i.Then, regs), Else = Rewrite(i.Else, regs) });
                        break;
                    case QFor f:
                        outp.Add(f with { Body = Rewrite(f.Body, regs) });
                        break;
                    case QWhile w:
                        outp.Add(w with { Body = Rewrite(w.Body, regs) });
                        break;
                    case QRepeat r:
                        outp.Add(r with { Body = Rewrite(r.Body, regs) });
                        break;
                    case QConjugate c:
                        outp.Add(c with { Within = Rewrite(c.Within, regs), Apply = Rewrite(c.Apply, regs) });
                        break;
                    default:
                        outp.Add(s);
                        break;
                }
            }
            return outp;
        }

        // Bind the callee's size symbol(s) from the caller's register argument sizes, then rewrite the
        // call to the matching specialization (creating it on first use).
        QGate SpecializeCall(QGate g, Dictionary<string, int> regs)
        {
            var callee = generic[g.Name];
            var bindings = new Dictionary<string, int>();
            for (int i = 0; i < callee.Params.Count && i < g.Args.Count; i++)
            {
                var p = callee.Params[i];
                if (p.SizeParam is null) continue;
                if (g.Args[i] is QTextArg ta && regs.TryGetValue(ta.Text.Trim(), out var sz))
                    bindings[p.SizeParam] = sz;
            }
            if (bindings.Count == 0) return g;   // size unresolved (malformed / caught upstream) — leave as-is

            var key = g.Name + "|" + string.Join(",", bindings.OrderBy(b => b.Key).Select(b => $"{b.Key}={b.Value}"));
            if (!specNameByKey.TryGetValue(key, out var specName))
            {
                specName = MakeName(g.Name, bindings);
                specNameByKey[key] = specName;
                specs[specName] = Specialize(callee, bindings, specName);   // may create further (nested) specs
            }
            return g with { Name = specName };
        }

        string MakeName(string baseName, Dictionary<string, int> bindings)
        {
            var name = baseName + "__sz" + string.Join("_", bindings.OrderBy(b => b.Key).Select(b => b.Value));
            // Keep the specialization name distinct from every existing/earlier op name; any residual
            // collision in the EMITTED (mangled) namespace is auto-resolved later by NameMangler.
            while (allNames.Contains(name)) name += "_";
            allNames.Add(name);
            return name;
        }

        QOperation Specialize(QOperation callee, Dictionary<string, int> bindings, string specName)
        {
            // params: each generic register gets its concrete size; the size symbol is gone. Every param
            // is re-minted (fresh Id) — sibling specializations of one generic would otherwise share Ids.
            var newParams = callee.Params
                .Select(p => p.SizeParam is not null && bindings.TryGetValue(p.SizeParam, out var k)
                    ? p with { Id = QNodeIds.Next(), RegisterSize = k, SizeParam = null }
                    : p with { Id = QNodeIds.Next() })
                .ToList();

            // body: substitute every size symbol with its value, then rewrite nested generic calls using
            // this specialization's now-concrete register sizes.
            var body = callee.Body;
            foreach (var (sym, val) in bindings) body = SubstituteSize(body, sym, val);

            var regs = new Dictionary<string, int>();
            foreach (var p in newParams)
                if (p.Type == QType.Qubit && p.RegisterSize is int rs) regs[p.Name] = rs;

            // Diagnostics on the specialization should read as the original op (`Foo`), not `Foo__sz3`.
            // ReId: the rewritten body is a `with`-copy of the generic body, so sibling specializations
            // would share statement Ids. No lineage recording — this runs BEFORE the semantic model.
            return new QOperation(specName, newParams, ReId.Run(Rewrite(body, regs)), callee.Namespace)
            {
                Span = callee.Span,
                DisplayName = callee.DisplayName ?? callee.Name,
            };
        }

        var outOps = new List<QOperation>(concrete.Count + specs.Count);
        foreach (var o in concrete)
        {
            var regs = new Dictionary<string, int>();
            foreach (var p in o.Params)
                if (p.Type == QType.Qubit && p.RegisterSize is int rs) regs[p.Name] = rs;
            outOps.Add(o with { Body = Rewrite(o.Body, regs) });
        }
        outOps.AddRange(specs.Values);   // Rewrite populated `specs` (incl. nested) as a side effect above
        return program with { Operations = outOps };
    }

    /// <summary>
    /// Replace whole-word occurrences of a size symbol with its concrete value in every text-bearing field
    /// of a statement subtree (for-bounds, gate args, indices, decl/assign RHS, conditions). Word-boundary
    /// matched so <c>n</c> is not replaced inside <c>count</c>.
    /// </summary>
    private static IReadOnlyList<QStmt> SubstituteSize(IReadOnlyList<QStmt> stmts, string sym, int val)
    {
        var rx = new Regex($@"\b{Regex.Escape(sym)}\b");
        var repl = val.ToString();
        string Sub(string s) => rx.Replace(s, repl);

        QArg SubArg(QArg a) => a switch
        {
            QQubitArg q => new QQubitArg(q.Reg, Sub(q.Index)),
            QTextArg t => new QTextArg(Sub(t.Text), t.HasCall),
            _ => a,
        };
        QExpr SubExpr(QExpr e) => e switch
        {
            QText t => new QText(Sub(t.Text), t.HasCall),
            _ => e,   // QMeasure has no size text
        };
        QCond SubCond(QCond c) => new QCond(Sub(c.Text), c.HasCall);

        List<QStmt> Walk(IReadOnlyList<QStmt> ss) => ss.Select<QStmt, QStmt>(s => s switch
        {
            QGate g => g with { Args = g.Args.Select(SubArg).ToList() },
            QFor f => f with { From = Sub(f.From), To = Sub(f.To), Body = Walk(f.Body) },
            QIf i => i with { Cond = SubCond(i.Cond), Then = Walk(i.Then), Else = Walk(i.Else) },
            QWhile w => w with { Cond = SubCond(w.Cond), Body = Walk(w.Body) },
            QRepeat r => r with { Until = SubCond(r.Until), Body = Walk(r.Body) },
            QDecl d => d with { Value = SubExpr(d.Value) },
            QAssign a => a with { Value = SubExpr(a.Value) },
            QConjugate c => c with { Within = Walk(c.Within), Apply = Walk(c.Apply) },
            _ => s,   // QUse size is a concrete literal already (and a generic op cannot `use`)
        }).ToList();

        return Walk(stmts);
    }
}
