namespace Qora.Ir.Passes;

/// <summary>
/// The type and value shape of one classical expression. <see cref="Type"/> is the scalar type, while
/// <see cref="IsArray"/> distinguishes a whole <c>T[]</c> value from one <c>T</c> element.
/// </summary>
internal readonly record struct QValueShape(
    QType Type,
    bool IsArray)
{
    public override string ToString() =>
        Type.ToString().ToLowerInvariant()
        + (IsArray ? "[]" : string.Empty);
}

/// <summary>
/// The single type reader for the unified HIR expression tree. User-function calls follow the resolved
/// <see cref="HirCallExpression.CalleeId"/> reference; a spelling is never used to recover a missing
/// semantic link.
/// </summary>
internal static class ExpressionTypes
{
    internal static bool TryGetFunction(
        HirCallExpression call,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById,
        out HirCallable function)
    {
        function = null!;
        if (call.CalleeId is not HirNodeId callableId)
            return false;

        if (!callableById.TryGetValue(callableId, out var callable))
            throw new InvalidOperationException(
                $"QINTERNAL: function call `{call.Name}` carries dangling CalleeId {callableId}");

        if (!callable.IsFunction)
            return false;

        function = callable;
        return true;
    }

    internal static QValueShape? TypeOf(
        HirExpression? expression,
        Scope scope,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById) =>
        expression switch
        {
            null or HirMissingExpression =>
                null,

            HirMeasurementExpression =>
                new QValueShape(QType.Bit, IsArray: false),

            HirArrayCreationExpression allocation =>
                new QValueShape(allocation.ElementType, IsArray: true),

            HirArrayLiteralExpression literal =>
                ArrayTypeOf(literal, scope, callableById),

            HirIntegerLiteralExpression =>
                new QValueShape(QType.Int, IsArray: false),

            HirLiteralExpression literal =>
                LiteralType(literal.Text),

            HirNameExpression name =>
                NameType(name.Name, scope),

            HirMemberAccessExpression
            {
                Receiver: { } receiver,
                MemberName: "Count",
            } when TypeOf(receiver, scope, callableById) is { IsArray: true } =>
                new QValueShape(QType.Int, IsArray: false),

            HirUnaryExpression unary =>
                UnaryType(unary, scope, callableById),

            HirBinaryExpression binary =>
                BinaryType(binary, scope, callableById),

            HirIndexExpression index
                when TypeOf(index.Receiver, scope, callableById) is
                {
                    IsArray: true,
                } array =>
                new QValueShape(array.Type, IsArray: false),

            HirCallExpression call
                when call.CalleeId is not null
                && TryGetFunction(
                    call,
                    callableById,
                    out var function)
                && function.ReturnType is { } returns =>
                new QValueShape(returns, IsArray: false),

            HirCallExpression call
                when call.CalleeId is null
                && QoraGates.Functions.TryGetValue(
                    call.Name,
                    out var builtin) =>
                new QValueShape(builtin.Returns, IsArray: false),

            _ =>
                null,
        };

    /// <summary>
    /// Assignment compatibility for a declared value contract. Shape must agree. Established contextual
    /// scalar conversions remain bit-to-int, int-to-float, float-to-angle, and integer literal 0/1 to bit.
    /// </summary>
    internal static bool CanAssign(
        QValueShape target,
        QValueShape source,
        HirExpression value)
    {
        if (target.IsArray != source.IsArray)
            return false;
        if (target.Type == source.Type)
            return true;
        if (target.Type == QType.Qubit || source.Type == QType.Qubit)
            return false;

        return (target.Type, source.Type) switch
        {
            (QType.Int, QType.Bit) =>
                true,
            (QType.Float, QType.Int) =>
                true,
            (QType.Angle, QType.Float) =>
                true,
            (QType.Bit, QType.Int) =>
                value is HirIntegerLiteralExpression { Value: 0 or 1 },
            _ =>
                false,
        };
    }

    private static QValueShape? NameType(
        string name,
        Scope scope)
    {
        if (name is "pi" or "tau" or "euler")
            return new QValueShape(QType.Float, IsArray: false);
        if (name is "true" or "false")
            return new QValueShape(QType.Bit, IsArray: false);

        return scope.Lookup(name) is
        {
            Type: { } type,
        } symbol
            ? new QValueShape(type, symbol.IsArray)
            : null;
    }

    private static QValueShape LiteralType(string text) =>
        text is "true" or "false"
            ? new QValueShape(QType.Bit, IsArray: false)
            : text is "pi" or "tau" or "euler"
              || text.Contains('.')
                ? new QValueShape(QType.Float, IsArray: false)
                : new QValueShape(QType.Int, IsArray: false);

    private static QValueShape? UnaryType(
        HirUnaryExpression unary,
        Scope scope,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById)
    {
        var operand = TypeOf(
            unary.Operand,
            scope,
            callableById);
        if (operand is not { } scalar)
            return null;
        if (scalar.IsArray)
            return scalar;

        if (unary.Operator == HirUnaryOperator.LogicalNot)
            return new QValueShape(QType.Bit, IsArray: false);

        return unary.Operator == HirUnaryOperator.Negate
               && scalar.Type == QType.Bit
            ? new QValueShape(QType.Int, IsArray: false)
            : scalar;
    }

    private static QValueShape? BinaryType(
        HirBinaryExpression binary,
        Scope scope,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById)
    {
        var left = TypeOf(binary.Left, scope, callableById);
        var right = TypeOf(binary.Right, scope, callableById);
        if (left is not { } lhs || right is not { } rhs)
            return null;

        // Preserve the offending shape so the boundary diagnostic can report array-vs-scalar.
        if (lhs.IsArray)
            return lhs;
        if (rhs.IsArray)
            return rhs;
        if (lhs.Type == QType.Qubit || rhs.Type == QType.Qubit)
            return null;

        if (binary.Operator is
            HirBinaryOperator.Equal
            or HirBinaryOperator.NotEqual
            or HirBinaryOperator.LessThan
            or HirBinaryOperator.LessThanOrEqual
            or HirBinaryOperator.GreaterThan
            or HirBinaryOperator.GreaterThanOrEqual
            or HirBinaryOperator.LogicalAnd
            or HirBinaryOperator.LogicalOr)
        {
            return new QValueShape(QType.Bit, IsArray: false);
        }

        if (lhs.Type == QType.Float || rhs.Type == QType.Float)
            return new QValueShape(QType.Float, IsArray: false);
        if (lhs.Type == QType.Angle || rhs.Type == QType.Angle)
            return new QValueShape(QType.Angle, IsArray: false);

        return new QValueShape(QType.Int, IsArray: false);
    }

    private static QValueShape? ArrayTypeOf(
        HirArrayLiteralExpression literal,
        Scope scope,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById)
    {
        QType? elementType = null;
        foreach (var element in literal.Elements)
        {
            if (TypeOf(element, scope, callableById) is not
                {
                    IsArray: false,
                } elementShape)
            {
                return null;
            }

            elementType = elementType is null
                ? elementShape.Type
                : CommonNumericType(
                    elementType.Value,
                    elementShape.Type);
            if (elementType is null)
                return null;
        }

        return elementType is { } type
            ? new QValueShape(type, IsArray: true)
            : null;
    }

    private static QType? CommonNumericType(
        QType left,
        QType right)
    {
        if (left == right)
            return left;
        if (left == QType.Qubit || right == QType.Qubit)
            return null;
        if (left == QType.Float || right == QType.Float)
            return QType.Float;
        if (left == QType.Angle || right == QType.Angle)
            return QType.Angle;
        return QType.Int;
    }
}
