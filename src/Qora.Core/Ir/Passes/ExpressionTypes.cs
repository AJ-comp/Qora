namespace Qora.Ir.Passes;

/// <summary>
/// The type and value shape of one classical expression. <see cref="Type"/> is the scalar type, while
/// <see cref="IsArray"/> distinguishes a whole <c>T[]</c> value from one <c>T</c> element.
/// </summary>
internal readonly record struct QValueShape(
    QType Type,
    bool IsArray,
    int? KnownLength = null)
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
        TypeOf(expression, scope, semanticModel: null, callableById);

    internal static QValueShape? TypeOf(
        HirExpression? expression,
        HirSemanticModel semanticModel,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById) =>
        TypeOf(expression, scope: null, semanticModel, callableById);

    private static QValueShape? TypeOf(
        HirExpression? expression,
        Scope? scope,
        HirSemanticModel? semanticModel,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById) =>
        expression switch
        {
            null or HirMissingExpression =>
                null,

            HirMeasurementExpression =>
                new QValueShape(QType.Bit, IsArray: false),

            HirArrayCreationExpression allocation =>
                new QValueShape(
                    allocation.ElementType,
                    IsArray: true,
                    allocation.Length),

            HirArrayLiteralExpression literal =>
                ArrayTypeOf(literal, scope, semanticModel, callableById),

            HirIntegerLiteralExpression =>
                new QValueShape(QType.Int, IsArray: false),

            HirLiteralExpression literal =>
                LiteralType(literal.Text),

            HirNameExpression name =>
                NameType(name, scope, semanticModel),

            HirMemberAccessExpression
            {
                Receiver: { } receiver,
                MemberName: "Count",
            } when TypeOf(receiver, scope, semanticModel, callableById) is { IsArray: true } =>
                new QValueShape(QType.Int, IsArray: false),

            HirUnaryExpression unary =>
                UnaryType(unary, scope, semanticModel, callableById),

            HirBinaryExpression binary =>
                BinaryType(binary, scope, semanticModel, callableById),

            HirIndexExpression index
                when TypeOf(index.Receiver, scope, semanticModel, callableById) is
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

    /// <summary>
    /// The scalar operand type used by arithmetic and comparison operators. This is source-language
    /// typing, not a MIR lowering decision. Arithmetic on two bits promotes them to <c>int</c>; a
    /// comparison may compare two bits directly.
    /// </summary>
    internal static QType CommonScalarOperandType(
        QType left,
        QType right,
        bool isComparison)
    {
        if (left == QType.Qubit || right == QType.Qubit)
            throw new ArgumentException("a classical operator cannot consume a qubit");
        if (left == right)
            return !isComparison && left == QType.Bit
                ? QType.Int
                : left;
        if (left == QType.Float || right == QType.Float)
            return QType.Float;
        if (left == QType.Angle || right == QType.Angle)
            return QType.Angle;
        return QType.Int;
    }

    private static QValueShape? NameType(
        HirNameExpression name,
        Scope? scope,
        HirSemanticModel? semanticModel)
    {
        if (name.Name is "pi" or "tau" or "euler")
            return new QValueShape(QType.Float, IsArray: false);
        if (name.Name is "true" or "false")
            return new QValueShape(QType.Bit, IsArray: false);

        var symbol = semanticModel is null
            ? scope!.Lookup(name.Name)
            : semanticModel.FindReferencedSymbol(name.Id);
        return symbol is
        {
            Type: { } type,
        }
            ? new QValueShape(
                type,
                symbol.IsArray,
                type == QType.Qubit
                    ? symbol.RegisterSize
                    : symbol.ArrayLength)
            : null;
    }

    internal static QValueShape LiteralType(string text) =>
        text is "true" or "false"
            ? new QValueShape(QType.Bit, IsArray: false)
            : text is "pi" or "tau" or "euler"
              || text.Contains('.')
                ? new QValueShape(QType.Float, IsArray: false)
                : new QValueShape(QType.Int, IsArray: false);

    private static QValueShape? UnaryType(
        HirUnaryExpression unary,
        Scope? scope,
        HirSemanticModel? semanticModel,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById)
    {
        var operand = TypeOf(
            unary.Operand,
            scope,
            semanticModel,
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
        Scope? scope,
        HirSemanticModel? semanticModel,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById)
    {
        var left = TypeOf(binary.Left, scope, semanticModel, callableById);
        var right = TypeOf(binary.Right, scope, semanticModel, callableById);
        if (left is not { } lhs || right is not { } rhs)
            return null;

        if (lhs.IsArray || rhs.IsArray)
        {
            if (binary.Operator is
                    HirBinaryOperator.Equal or HirBinaryOperator.NotEqual
                && lhs.IsArray
                && rhs.IsArray
                && lhs.Type != QType.Qubit
                && lhs.Type == rhs.Type)
            {
                return new QValueShape(QType.Bit, IsArray: false);
            }

            // Preserve the offending shape so the boundary diagnostic can report array-vs-scalar.
            return lhs.IsArray ? lhs : rhs;
        }
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

        return new QValueShape(
            CommonScalarOperandType(
                lhs.Type,
                rhs.Type,
                isComparison: false),
            IsArray: false);
    }

    private static QValueShape? ArrayTypeOf(
        HirArrayLiteralExpression literal,
        Scope? scope,
        HirSemanticModel? semanticModel,
        IReadOnlyDictionary<HirNodeId, HirCallable> callableById)
    {
        QType? elementType = null;
        foreach (var element in literal.Elements)
        {
            if (TypeOf(element, scope, semanticModel, callableById) is not
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
            ? new QValueShape(
                type,
                IsArray: true,
                literal.Elements.Count)
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
        return CommonScalarOperandType(
            left,
            right,
            isComparison: false);
    }
}
