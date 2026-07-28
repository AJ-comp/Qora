using System.Text;

namespace Qora.Ir;

/// <summary>
/// Renders source-shaped Qora HIR as a readable tree. Used by the CLI's <c>--stages</c> mode and tooling
/// that wants to inspect the front-end result without exposing MIR-only transformation state.
/// </summary>
public static class HirPrinter
{
    /// <summary>The whole authoritative declaration tree.</summary>
    public static string Print(HirProgram? program)
    {
        if (program is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var import in program.Imports)
            sb.AppendLine($"HirImport {import.Display}");
        foreach (var declaration in program.Declarations)
            PrintDeclaration(declaration, sb, string.Empty);
        return sb.ToString().TrimEnd();
    }

    private static void PrintDeclaration(
        HirDeclaration declaration,
        StringBuilder sb,
        string indent)
    {
        switch (declaration)
        {
            case HirCallable callable:
                sb.AppendLine(
                    $"{indent}HirCallable {callable.Name}"
                    + $"({string.Join(", ", callable.Parameters.Select(PrintParam))})");
                PrintBody(callable.Body, sb, indent + "  ");
                sb.AppendLine();
                return;

            case HirNamespaceDeclaration @namespace:
                sb.AppendLine($"{indent}HirNamespace {@namespace.Name}");
                foreach (var open in @namespace.OpenDirectives)
                    sb.AppendLine($"{indent}  HirOpen {open.Target}");
                foreach (var nested in @namespace.Declarations)
                    PrintDeclaration(nested, sb, indent + "  ");
                return;

            default:
                throw new InvalidOperationException(
                    $"QINTERNAL: {nameof(HirPrinter)} does not handle declaration "
                    + $"`{declaration.GetType().Name}`.");
        }
    }

    private static string PrintParam(HirParameter p)
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

    private static void PrintBody(IReadOnlyList<HirStatement> stmts, StringBuilder sb, string indent)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case HirQubitDeclarationStatement u:
                    sb.AppendLine($"{indent}HirQubitDeclaration(name={u.Name}, size={u.Size})");
                    break;
                case HirCallStatement g:
                    sb.AppendLine($"{indent}HirCall(modifiers=[{string.Join(",", g.Modifiers)}], name={g.Call.Name}, args=[{string.Join(", ", g.Call.Arguments.Select(PrintArg))}])");
                    break;
                case HirVariableDeclarationStatement d:
                    sb.AppendLine($"{indent}HirVariableDeclaration(const={d.IsConst}, type={d.Type?.ToString() ?? "?"}{(d.IsArray ? "[]" : "")}, name={d.Name}, value={PrintExpr(d.Value)})");
                    break;
                case HirAssignmentStatement a:
                    sb.AppendLine($"{indent}HirAssignment({PrintExpr(a.Target)} = {PrintExpr(a.Value)})");
                    break;
                case HirReturnStatement r:
                    sb.AppendLine($"{indent}HirReturn({PrintExpr(r.Value)})");
                    break;
                case HirIfStatement i:
                    sb.AppendLine($"{indent}HirIf(cond=\"{HirExpressions.Render(i.Condition)}\")");
                    sb.AppendLine($"{indent}  then:");
                    PrintBody(i.Then, sb, indent + "    ");
                    if (i.Else.Count > 0)
                    {
                        sb.AppendLine($"{indent}  else:");
                        PrintBody(i.Else, sb, indent + "    ");
                    }
                    break;
                case HirForStatement f:
                    sb.AppendLine($"{indent}HirFor({f.Variable} in {HirExpressions.Render(f.From)}..{HirExpressions.Render(f.To)})");
                    PrintBody(f.Body, sb, indent + "  ");
                    break;
                case HirWhileStatement w:
                    sb.AppendLine($"{indent}HirWhile(cond=\"{HirExpressions.Render(w.Condition)}\")");
                    PrintBody(w.Body, sb, indent + "  ");
                    break;
                case HirRepeatStatement r:
                    sb.AppendLine($"{indent}HirRepeat(until=\"{HirExpressions.Render(r.Until)}\")");
                    PrintBody(r.Body, sb, indent + "  ");
                    break;
            }
        }
    }

    private static string PrintArg(HirArgument arg) =>
        ModePrefix(arg.Ownership, arg.Access)
        + HirExpressions.Render(arg.Expression);

    private static string ModePrefix(QOwnershipMode ownership, QAccessMode access) =>
        (ownership, access) switch
        {
            (QOwnershipMode.Borrowed, QAccessMode.Mutable) => "var ",
            (QOwnershipMode.Moved, QAccessMode.ReadOnly) => "move ",
            (QOwnershipMode.Moved, QAccessMode.Mutable) => "move var ",
            _ => string.Empty,
        };

    private static string PrintExpr(HirExpression expr) => expr switch
    {
        HirMeasurementExpression measurement =>
            $"HirMeasurement({HirExpressions.Render(measurement.Target)})",
        _ => HirExpressions.Render(expr),
    };

}
