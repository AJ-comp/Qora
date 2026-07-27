using System.Text;

namespace Qora.Ir;

/// <summary>
/// Renders source-shaped Qora HIR as a readable tree. Used by the CLI's <c>--stages</c> mode and tooling
/// that wants to inspect the front-end result without exposing MIR-only transformation state.
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

    private static string PrintParam(QParam p)
    {
        var value = p.Type switch
        {
            QType.Qubit when p.RegisterSize is int n => $"Qubit[{n}] {p.Name}",
            QType.Qubit when p.IsQubitArray => $"Qubit[] {p.Name}",
            QType.Qubit => $"Qubit {p.Name}",
            _ when p.IsArray => $"{p.Type.ToString().ToLowerInvariant()}[] {p.Name}",
            _ => $"{p.Type} {p.Name}",
        };
        return ModePrefix(p.Ownership, p.Access) + value;
    }

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
                    sb.AppendLine($"{indent}QGate(modifiers=[{string.Join(",", g.Modifiers)}], name={g.Name}, args=[{string.Join(", ", g.Args.Select(PrintArg))}])");
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
                    sb.AppendLine($"{indent}QFor({f.Var} in {QNodes.Render(f.From)}..{QNodes.Render(f.To)})");
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
            }
        }
    }

    private static string PrintArg(QArg arg)
    {
        var value = arg switch
        {
            QQubitArg q => $"{q.Reg}[{QNodes.Render(q.Index)}]",
            QTextArg t => QNodes.Render(t.Tree),
            _ => string.Empty,
        };
        return ModePrefix(arg.Ownership, arg.Access) + value;
    }

    private static string ModePrefix(QOwnershipMode ownership, QAccessMode access) =>
        (ownership, access) switch
        {
            (QOwnershipMode.Borrowed, QAccessMode.Mutable) => "var ",
            (QOwnershipMode.Moved, QAccessMode.ReadOnly) => "move ",
            (QOwnershipMode.Moved, QAccessMode.Mutable) => "move var ",
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

}
