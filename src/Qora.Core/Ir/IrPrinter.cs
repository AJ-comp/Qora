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
    /// The synthesized inverse of every operation the program applies <c>Adjoint</c> to — stage 3 of the
    /// pipeline, the part no source line wrote. Ops that cannot be inverted are listed with the reason
    /// (the validator reports them as QSEM001 anyway; this keeps the stages view self-explanatory).
    /// </summary>
    public static string PrintInverses(QProgram? program)
    {
        if (program is null) return string.Empty;

        var ops = program.Operations.Select(o => o.Name).ToHashSet();
        var adjointed = new HashSet<string>();
        foreach (var op in program.Operations)
            CollectAdjointRefs(op.Body, ops, adjointed);
        if (adjointed.Count == 0) return string.Empty;

        var inverter = new Inverter(program.Operations);
        var sb = new StringBuilder();
        foreach (var name in adjointed.OrderBy(n => n))
        {
            if (inverter.TryInvertOperation(name, out var inverse, out var reason))
            {
                sb.AppendLine($"inverse of {name}:");
                PrintBody(inverse, sb, "  ");
            }
            else
            {
                sb.AppendLine($"inverse of {name}: (not invertible: {reason})");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string PrintParam(QParam p) => p.Type switch
    {
        QType.Qubit when p.RegisterSize is int n => $"Qubit[{n}] {p.Name}",
        QType.Qubit => $"Qubit {p.Name}",
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
                    sb.AppendLine($"{indent}QDecl(const={d.IsConst}, type={d.Type?.ToString() ?? "?"}, name={d.Name}, value={PrintExpr(d.Value)})");
                    break;
                case QAssign a:
                    sb.AppendLine($"{indent}QAssign({a.Name} = {PrintExpr(a.Value)})");
                    break;
                case QIf i:
                    sb.AppendLine($"{indent}QIf(cond=\"{i.Cond.Text}\")");
                    sb.AppendLine($"{indent}  then:");
                    PrintBody(i.Then, sb, indent + "    ");
                    if (i.Else.Count > 0)
                    {
                        sb.AppendLine($"{indent}  else:");
                        PrintBody(i.Else, sb, indent + "    ");
                    }
                    break;
                case QFor f:
                    sb.AppendLine($"{indent}QFor({f.Var} in {f.From}..{f.To}{(f.Step is null ? string.Empty : $", step={f.Step}")})");
                    PrintBody(f.Body, sb, indent + "  ");
                    break;
                case QWhile w:
                    sb.AppendLine($"{indent}QWhile(cond=\"{w.Cond.Text}\")");
                    PrintBody(w.Body, sb, indent + "  ");
                    break;
                case QRepeat r:
                    sb.AppendLine($"{indent}QRepeat(until=\"{r.Until.Text}\")");
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
        QQubitArg q => $"{q.Reg}[{q.Index}]",
        QTextArg t => t.Text,
        _ => string.Empty,
    };

    private static string PrintExpr(QExpr expr) => expr switch
    {
        QMeasure m => m.Target is null ? "QMeasure()" : $"QMeasure({m.Target.Reg}[{m.Target.Index}])",
        QText t => t.Text,
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
