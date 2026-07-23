using System.Text;

namespace Qora.Ir;

/// <summary>
/// Renders Qora IR as a readable tree — the "what did the compiler actually build" view. Used by the
/// CLI's <c>--stages</c> mode (and any tooling that wants to show the pipeline: the VS Code stages
/// panel, docs). The format matches the compiler walkthrough in <c>docs/adjoint-pipeline.html</c>:
/// <code>
/// QOperation Outer(Qubit[2] q, Bit b)
///   QDecl(const=False, type=Int, name=k, value=2)
///   QGate(functors=[Adjoint], name=S, args=[q[0]])
///   QFor(i in 1..0, step=-1)
///     ...
/// </code>
/// </summary>
public static class IrPrinter
{
    /// <summary>The whole program, one operation per block.</summary>
    public static string Print(QProgram? program)
    {
        if (program is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var op in program.Operations)
        {
            sb.AppendLine($"QOperation {op.Name}({string.Join(", ", op.Params.Select(PrintParam))})");
            PrintBody(op.Body, sb, "  ");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The synthesized inverse of every operation the program needs — stage 3 of the pipeline, the part
    /// no source line wrote. Mirrors the emitter exactly: the set is closed TRANSITIVELY (an inverse
    /// body's plain calls become <c>Adjoint</c> calls needing their own inverses) and each block is
    /// headed by the same uniquified def name the QASM will contain, so the stages panel's inverse
    /// column maps one-to-one onto the emitted defs. Non-invertible targets are listed with the reason.
    /// </summary>
    public static string PrintInverses(QProgram? program)
    {
        if (program is null) return string.Empty;

        var ops = program.Operations.Select(o => o.Name).ToHashSet();
        var opByName = new Dictionary<string, QOperation>();
        foreach (var o in program.Operations) opByName.TryAdd(o.Name, o);

        // transitive closure over Adjoint references — same worklist the emitter runs.
        var inverter = new Inverter(program.Operations);
        var inverses = new Dictionary<string, IReadOnlyList<QStmt>>();
        var adjOrder = new List<string>();
        var notInvertible = new Dictionary<string, string>();
        var seen = new HashSet<string>();
        var worklist = new Queue<string>();
        void Enqueue(IReadOnlyList<QStmt> body)
        {
            var refs = new HashSet<string>();
            CollectAdjointRefs(body, ops, refs);
            foreach (var r in refs) if (seen.Add(r)) worklist.Enqueue(r);
        }
        foreach (var op in program.Operations) Enqueue(op.Body);
        while (worklist.Count > 0)
        {
            var name = worklist.Dequeue();
            if (!opByName.ContainsKey(name)) continue;
            if (inverter.TryInvertOperation(name, out var inverse, out var reason))
            {
                inverses[name] = inverse;
                adjOrder.Add(name);
                Enqueue(inverse);
            }
            else
            {
                notInvertible[name] = reason;
            }
        }
        if (adjOrder.Count == 0 && notInvertible.Count == 0) return string.Empty;

        // same uniquify rule as the emitter, so the shown def names match the QASM.
        var adjNames = new Dictionary<string, string>();
        foreach (var name in adjOrder)
        {
            var candidate = name + "__adj";
            while (ops.Contains(candidate) || adjNames.ContainsValue(candidate)) candidate += "_";
            adjNames[name] = candidate;
        }

        var sb = new StringBuilder();
        foreach (var name in adjOrder)
        {
            sb.AppendLine($"def {adjNames[name]}  (inverse of {name}):");
            PrintBody(inverses[name], sb, "  ");
            sb.AppendLine();
        }
        foreach (var (name, reason) in notInvertible.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"inverse of {name}: (not invertible: {reason})");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string PrintParam(QParam p) => p.Type switch
    {
        QType.Qubit when p.RegisterSize is int n => $"Qubit[{n}] {p.Name}",
        QType.Qubit when p.IsQubitArray => $"Qubit[] {p.Name}",
        QType.Qubit => $"Qubit {p.Name}",
        _ when p.IsArray => $"{p.Type.ToString().ToLowerInvariant()}[] {p.Name}",
        _ => $"{p.Type} {p.Name}",
    };

    private static void PrintBody(IReadOnlyList<QStmt> stmts, StringBuilder sb, string indent)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QUse u:
                    sb.AppendLine($"{indent}QUse(name={u.Name}, size={u.Size})");
                    break;
                case QGate g:
                    sb.AppendLine($"{indent}QGate(functors=[{string.Join(",", g.Functors)}], name={g.Name}, args=[{string.Join(", ", g.Args.Select(PrintArg))}])");
                    break;
                case QDecl d:
                    sb.AppendLine($"{indent}QDecl(const={d.IsConst}, type={d.Type?.ToString() ?? "?"}{(d.IsArray ? "[]" : "")}, name={d.Name}, value={PrintExpr(d.Value)})");
                    break;
                case QAssign a:
                    sb.AppendLine($"{indent}QAssign({a.Name}{(a.Index is null ? "" : $"[{QNodes.Render(a.Index)}]")} = {PrintExpr(a.Value)})");
                    break;
                case QReturn r:
                    sb.AppendLine($"{indent}QReturn({PrintExpr(r.Value)})");
                    break;
                case QBreak:
                    sb.AppendLine($"{indent}QBreak");
                    break;
                case QIf i:
                    sb.AppendLine($"{indent}QIf(cond=\"{QNodes.Render(i.Cond.Tree)}\")");
                    sb.AppendLine($"{indent}  then:");
                    PrintBody(i.Then, sb, indent + "    ");
                    if (i.Else.Count > 0)
                    {
                        sb.AppendLine($"{indent}  else:");
                        PrintBody(i.Else, sb, indent + "    ");
                    }
                    break;
                case QFor f:
                    sb.AppendLine($"{indent}QFor({f.Var} in {QNodes.Render(f.From)}..{QNodes.Render(f.To)}{(f.Step is null ? string.Empty : $", step={QNodes.Render(f.Step)}")})");
                    PrintBody(f.Body, sb, indent + "  ");
                    break;
                case QWhile w:
                    sb.AppendLine($"{indent}QWhile(cond=\"{QNodes.Render(w.Cond.Tree)}\")");
                    PrintBody(w.Body, sb, indent + "  ");
                    break;
                case QRepeat r:
                    sb.AppendLine($"{indent}QRepeat(until=\"{QNodes.Render(r.Until.Tree)}\")");
                    PrintBody(r.Body, sb, indent + "  ");
                    break;
                case QConjugate c:
                    sb.AppendLine($"{indent}QConjugate");
                    sb.AppendLine($"{indent}  within:");
                    PrintBody(c.Within, sb, indent + "    ");
                    sb.AppendLine($"{indent}  apply:");
                    PrintBody(c.Apply, sb, indent + "    ");
                    break;
            }
        }
    }

    private static string PrintArg(QArg arg) => arg switch
    {
        QQubitArg q => $"{q.Reg}[{QNodes.Render(q.Index)}]",
        QTextArg t => QNodes.Render(t.Tree),
        _ => string.Empty,
    };

    private static string PrintExpr(QExpr expr) => expr switch
    {
        QMeasure m => $"QMeasure({QNodes.RegOf(m.Target)}{(QNodes.IndexOf(m.Target) is { } mi ? $"[{QNodes.Render(mi)}]" : string.Empty)})",
        QText t => QNodes.Render(t.Tree),
        QArrayLiteral literal => $"[{string.Join(", ", literal.Elements.Select(PrintExpr))}]",
        QArrayNew allocation => $"new {allocation.ElementType.ToString().ToLowerInvariant()}[{allocation.Length}]",
        _ => string.Empty,
    };

    private static void CollectAdjointRefs(IReadOnlyList<QStmt> stmts, HashSet<string> ops, HashSet<string> into)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QGate g when g.Functors.FirstOrDefault() == "Adjoint" && ops.Contains(g.Name):
                    into.Add(g.Name);
                    break;
                case QIf i:
                    CollectAdjointRefs(i.Then, ops, into);
                    CollectAdjointRefs(i.Else, ops, into);
                    break;
                case QFor f:
                    CollectAdjointRefs(f.Body, ops, into);
                    break;
                case QWhile w:
                    CollectAdjointRefs(w.Body, ops, into);
                    break;
                case QRepeat r:
                    CollectAdjointRefs(r.Body, ops, into);
                    break;
                case QConjugate c:
                    CollectAdjointRefs(c.Within, ops, into);
                    CollectAdjointRefs(c.Apply, ops, into);
                    break;
            }
        }
    }
}
