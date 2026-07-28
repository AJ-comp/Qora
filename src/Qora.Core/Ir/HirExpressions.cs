using System.Globalization;

namespace Qora.Ir;

/// <summary>
/// Canonical queries over the unified HIR expression tree. Rendering is derived from structure and never
/// stored as a second spelling ledger.
/// </summary>
public static class HirExpressions
{
    public static string Render(HirExpression? expression) => expression switch
    {
        null or HirMissingExpression => string.Empty,
        HirIntegerLiteralExpression integer =>
            integer.Value.ToString(CultureInfo.InvariantCulture),
        HirLiteralExpression literal => literal.Text,
        HirNameExpression name => name.Name,
        HirMemberAccessExpression member =>
            $"{Render(member.Receiver)} . {member.MemberName}",
        HirUnaryExpression
        {
            Operator: HirUnaryOperator.LogicalNot,
        } unary =>
            $"! ( {Render(unary.Operand)} )",
        HirUnaryExpression unary =>
            $"{Token(unary.Operator)} {Render(unary.Operand)}",
        HirBinaryExpression binary =>
            $"{Render(binary.Left)} {Token(binary.Operator)} {Render(binary.Right)}",
        HirIndexExpression index =>
            $"{Render(index.Receiver)} [ {Render(index.Index)} ]",
        HirCallExpression call =>
            $"{call.Name}({string.Join(", ", call.Arguments.Select(argument => Render(argument.Expression)))})",
        HirMeasurementExpression measurement =>
            $"{QoraGates.Measurement}({Render(measurement.Target)})",
        HirArrayLiteralExpression literal =>
            $"[{string.Join(", ", literal.Elements.Select(Render))}]",
        HirArrayCreationExpression creation =>
            $"new {creation.ElementType.ToString().ToLowerInvariant()}[{creation.Length}]",
        _ => string.Empty,
    };

    public static string? QualifiedNameOf(HirExpression expression) => expression switch
    {
        HirNameExpression name => name.Name,
        HirMemberAccessExpression member
            when QualifiedNameOf(member.Receiver) is { } prefix =>
            $"{prefix}.{member.MemberName}",
        _ => null,
    };

    public static string Token(HirUnaryOperator @operator) => @operator switch
    {
        HirUnaryOperator.Negate => "-",
        HirUnaryOperator.LogicalNot => "!",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
    };

    public static string Token(HirBinaryOperator @operator) => @operator switch
    {
        HirBinaryOperator.Add => "+",
        HirBinaryOperator.Subtract => "-",
        HirBinaryOperator.Multiply => "*",
        HirBinaryOperator.Divide => "/",
        HirBinaryOperator.Equal => "==",
        HirBinaryOperator.NotEqual => "!=",
        HirBinaryOperator.LessThan => "<",
        HirBinaryOperator.LessThanOrEqual => "<=",
        HirBinaryOperator.GreaterThan => ">",
        HirBinaryOperator.GreaterThanOrEqual => ">=",
        HirBinaryOperator.LogicalAnd => "&&",
        HirBinaryOperator.LogicalOr => "||",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
    };

    public static HirUnaryOperator ParseUnaryOperator(string token) => token switch
    {
        "-" => HirUnaryOperator.Negate,
        "!" => HirUnaryOperator.LogicalNot,
        _ => throw new ArgumentException(
            $"Unknown HIR unary operator `{token}`.",
            nameof(token)),
    };

    public static HirBinaryOperator ParseBinaryOperator(string token) => token switch
    {
        "+" => HirBinaryOperator.Add,
        "-" => HirBinaryOperator.Subtract,
        "*" => HirBinaryOperator.Multiply,
        "/" => HirBinaryOperator.Divide,
        "==" => HirBinaryOperator.Equal,
        "!=" => HirBinaryOperator.NotEqual,
        "<" => HirBinaryOperator.LessThan,
        "<=" => HirBinaryOperator.LessThanOrEqual,
        ">" => HirBinaryOperator.GreaterThan,
        ">=" => HirBinaryOperator.GreaterThanOrEqual,
        "&&" => HirBinaryOperator.LogicalAnd,
        "||" => HirBinaryOperator.LogicalOr,
        _ => throw new ArgumentException(
            $"Unknown HIR binary operator `{token}`.",
            nameof(token)),
    };

    public static bool ContainsCall(HirExpression? expression) =>
        CallsIn(expression).Any();

    public static IEnumerable<HirCallExpression> CallsIn(HirExpression? expression)
    {
        if (expression is null)
            yield break;
        if (expression is HirCallExpression call)
            yield return call;
        foreach (var child in expression.Children().OfType<HirExpression>())
            foreach (var nested in CallsIn(child))
                yield return nested;
        foreach (var argument in expression.Children().OfType<HirArgument>())
            foreach (var nested in CallsIn(argument.Expression))
                yield return nested;
    }

    public static string RegisterNameOf(HirExpression target) => target switch
    {
        HirIndexExpression
        {
            Receiver: HirNameExpression name,
        } => name.Name,
        HirNameExpression name => name.Name,
        _ => string.Empty,
    };

    public static HirExpression? IndexOf(HirExpression target) =>
        target is HirIndexExpression index ? index.Index : null;

    /// <summary>
    /// Returns the root binding written by a source assignment. This is a structural query over the
    /// authoritative target expression, not a second name stored on the statement.
    /// </summary>
    public static string? AssignmentNameOf(HirExpression target) => target switch
    {
        HirNameExpression name => name.Name,
        HirIndexExpression
        {
            Receiver: HirNameExpression name,
        } => name.Name,
        _ => null,
    };

    /// <summary>The index expression of a simple indexed assignment, or null for a whole binding.</summary>
    public static HirExpression? AssignmentIndexOf(HirExpression target) =>
        target is HirIndexExpression index ? index.Index : null;

    /// <summary>Every expression subtree directly owned by a statement.</summary>
    public static IEnumerable<HirExpression> DirectExpressionSites(
        HirStatement statement) =>
        statement.Children().OfType<HirExpression>();

    /// <summary>Every expression occurrence in a node subtree, in pre-order.</summary>
    public static IEnumerable<HirExpression> DescendantsAndSelf(HirNode node)
    {
        if (node is HirExpression expression)
            yield return expression;
        foreach (var child in node.Children())
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
    }
}
