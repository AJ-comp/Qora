namespace Qora.Ir.Passes;

/// <summary>
/// What an integer expression folds to. <see cref="BoundNum"/> means every leaf resolved and the value is
/// definite. <see cref="ArrayLengthBound"/> retains a linear form over one array whose concrete length is
/// not known yet. A null result means that the expression cannot be proved at compile time.
/// </summary>
internal abstract record Bound;

internal sealed record BoundNum(long Value) : Bound;

internal sealed record ArrayLengthBound(
    SymbolId ArraySymbolId,
    long Coeff,
    long Offset,
    bool IsOverflowFree = true) : Bound;

/// <summary>
/// The canonical compile-time integer folder over the unified HIR expression tree. Names resolve through
/// the lexical scope, so const values and symbolic array lengths retain their declaration identity.
/// </summary>
internal static class BoundFolder
{
    internal static Bound? Fold(
        HirExpression? expression,
        Scope scope) =>
        expression switch
        {
            HirIntegerLiteralExpression integer =>
                new BoundNum(integer.Value),

            // `<array>.Count`: a concrete array length is a number; an unsized parameter stays symbolic.
            HirMemberAccessExpression
            {
                Receiver: HirNameExpression array,
                MemberName: "Count",
            } =>
                scope.Lookup(array.Name) is { IsArray: true } symbol
                    ? (symbol.Type == QType.Qubit
                        ? symbol.RegisterSize
                        : symbol.ArrayLength) is int length
                        ? new BoundNum(length)
                        : new ArrayLengthBound(symbol.Id, 1, 0)
                    : null,

            // A const reads the value folded at its declaration. It may itself retain a symbolic Count.
            HirNameExpression name =>
                scope.Lookup(name.Name) is
                    {
                        IsConst: true,
                        FoldedBound: { } folded,
                    }
                    ? folded
                    : null,

            HirUnaryExpression
            {
                Operator: HirUnaryOperator.Negate,
            } unary =>
                Apply(
                    new BoundNum(0),
                    HirBinaryOperator.Subtract,
                    Fold(unary.Operand, scope)),

            HirBinaryExpression binary
                when binary.Operator is
                    HirBinaryOperator.Add
                    or HirBinaryOperator.Subtract
                    or HirBinaryOperator.Multiply
                    or HirBinaryOperator.Divide =>
                Apply(
                    Fold(binary.Left, scope),
                    binary.Operator,
                    Fold(binary.Right, scope)),

            _ => null,
        };

    private static Bound? Apply(
        Bound? left,
        HirBinaryOperator @operator,
        Bound? right)
    {
        if (left is null || right is null)
            return null;

        // Overflow does not produce a folded value. Returning null keeps validation conservative.
        try
        {
            checked
            {
                return (left, right, @operator) switch
                {
                    (BoundNum a, BoundNum b, HirBinaryOperator.Add) =>
                        new BoundNum(a.Value + b.Value),
                    (BoundNum a, BoundNum b, HirBinaryOperator.Subtract) =>
                        new BoundNum(a.Value - b.Value),
                    (BoundNum a, BoundNum b, HirBinaryOperator.Multiply) =>
                        new BoundNum(a.Value * b.Value),
                    (BoundNum a, BoundNum { Value: not 0 } b, HirBinaryOperator.Divide) =>
                        new BoundNum(a.Value / b.Value),

                    (ArrayLengthBound a, BoundNum b, HirBinaryOperator.Add) =>
                        Normalize(
                            a.ArraySymbolId,
                            a.Coeff,
                            a.Offset + b.Value,
                            a.IsOverflowFree),
                    (BoundNum a, ArrayLengthBound b, HirBinaryOperator.Add) =>
                        Normalize(
                            b.ArraySymbolId,
                            b.Coeff,
                            a.Value + b.Offset,
                            b.IsOverflowFree),
                    (ArrayLengthBound a, BoundNum b, HirBinaryOperator.Subtract) =>
                        Normalize(
                            a.ArraySymbolId,
                            a.Coeff,
                            a.Offset - b.Value,
                            a.IsOverflowFree),
                    (BoundNum a, ArrayLengthBound b, HirBinaryOperator.Subtract) =>
                        Normalize(
                            b.ArraySymbolId,
                            -b.Coeff,
                            a.Value - b.Offset,
                            b.IsOverflowFree),
                    (ArrayLengthBound a, BoundNum b, HirBinaryOperator.Multiply) =>
                        Normalize(
                            a.ArraySymbolId,
                            a.Coeff * b.Value,
                            a.Offset * b.Value,
                            a.IsOverflowFree),
                    (BoundNum a, ArrayLengthBound b, HirBinaryOperator.Multiply) =>
                        Normalize(
                            b.ArraySymbolId,
                            a.Value * b.Coeff,
                            a.Value * b.Offset,
                            b.IsOverflowFree),

                    (ArrayLengthBound a, ArrayLengthBound b, HirBinaryOperator.Add)
                        when a.ArraySymbolId == b.ArraySymbolId =>
                        Normalize(
                            a.ArraySymbolId,
                            a.Coeff + b.Coeff,
                            a.Offset + b.Offset,
                            a.IsOverflowFree && b.IsOverflowFree),
                    (ArrayLengthBound a, ArrayLengthBound b, HirBinaryOperator.Subtract)
                        when a.ArraySymbolId == b.ArraySymbolId =>
                        Normalize(
                            a.ArraySymbolId,
                            a.Coeff - b.Coeff,
                            a.Offset - b.Offset,
                            a.IsOverflowFree && b.IsOverflowFree),

                    _ => null,
                };
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static Bound Normalize(
        SymbolId arraySymbolId,
        long coefficient,
        long offset,
        bool operandsOverflowFree)
    {
        // Algebraic cancellation must not hide an overflow that occurs earlier in source evaluation.
        var first = new System.Numerics.BigInteger(coefficient) + offset;
        var last =
            new System.Numerics.BigInteger(coefficient) * int.MaxValue
            + offset;
        var overflowFree =
            operandsOverflowFree
            && System.Numerics.BigInteger.Min(first, last) >= long.MinValue
            && System.Numerics.BigInteger.Max(first, last) <= long.MaxValue;

        return coefficient == 0 && overflowFree
            ? new BoundNum(offset)
            : new ArrayLengthBound(
                arraySymbolId,
                coefficient,
                offset,
                overflowFree);
    }

    /// <summary>
    /// True when monomorphization is the stage that can supply the missing qubit-register length.
    /// </summary>
    internal static bool DefersToUnsizedQubit(
        Bound? bound,
        Scope scope) =>
        bound is ArrayLengthBound array
        && scope.GetSymbol(array.ArraySymbolId).MonoSized;
}

/// <summary>
/// The canonical compile-time boolean folder. A null result means that runtime state may determine the
/// condition, so control-flow analyses must keep both paths.
/// </summary>
internal static class BooleanFolder
{
    internal static bool? Fold(
        HirExpression? expression,
        Scope scope)
    {
        switch (expression)
        {
            case HirLiteralExpression { Text: "true" }:
            case HirNameExpression { Name: "true" }:
                return true;

            case HirLiteralExpression { Text: "false" }:
            case HirNameExpression { Name: "false" }:
                return false;

            case HirNameExpression name
                when scope.Lookup(name.Name) is
                {
                    IsConst: true,
                    FoldedBoolean: { } constant,
                }:
                return constant;

            case HirUnaryExpression
            {
                Operator: HirUnaryOperator.LogicalNot,
            } unary:
                return Fold(unary.Operand, scope) is { } operand
                    ? !operand
                    : null;

            case HirBinaryExpression
            {
                Operator: HirBinaryOperator.LogicalAnd,
            } and:
            {
                var left = Fold(and.Left, scope);
                var right = Fold(and.Right, scope);
                if (left == false || right == false)
                    return false;
                return left == true && right == true
                    ? true
                    : null;
            }

            case HirBinaryExpression
            {
                Operator: HirBinaryOperator.LogicalOr,
            } or:
            {
                var left = Fold(or.Left, scope);
                var right = Fold(or.Right, scope);
                if (left == true || right == true)
                    return true;
                return left == false && right == false
                    ? false
                    : null;
            }

            case HirBinaryExpression comparison
                when comparison.Operator is
                    HirBinaryOperator.Equal
                    or HirBinaryOperator.NotEqual
                    or HirBinaryOperator.LessThan
                    or HirBinaryOperator.LessThanOrEqual
                    or HirBinaryOperator.GreaterThan
                    or HirBinaryOperator.GreaterThanOrEqual
                && BoundFolder.Fold(comparison.Left, scope) is BoundNum left
                && BoundFolder.Fold(comparison.Right, scope) is BoundNum right:
                return comparison.Operator switch
                {
                    HirBinaryOperator.Equal => left.Value == right.Value,
                    HirBinaryOperator.NotEqual => left.Value != right.Value,
                    HirBinaryOperator.LessThan => left.Value < right.Value,
                    HirBinaryOperator.LessThanOrEqual => left.Value <= right.Value,
                    HirBinaryOperator.GreaterThan => left.Value > right.Value,
                    HirBinaryOperator.GreaterThanOrEqual => left.Value >= right.Value,
                    _ => null,
                };

            case HirBinaryExpression comparison
                when comparison.Operator is
                    HirBinaryOperator.Equal
                    or HirBinaryOperator.NotEqual:
            {
                var left = Fold(comparison.Left, scope);
                var right = Fold(comparison.Right, scope);
                if (left is null || right is null)
                    return null;
                return comparison.Operator == HirBinaryOperator.Equal
                    ? left == right
                    : left != right;
            }

            default:
                return null;
        }
    }
}
