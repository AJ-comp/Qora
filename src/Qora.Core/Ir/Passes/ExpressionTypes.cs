namespace Qora.Ir.Passes;

/// <summary>
/// The type and value shape of one classical expression. <see cref="Type"/> is the scalar type, while
/// <see cref="IsArray"/> says whether the expression denotes the whole <c>T[]</c> container rather than one
/// <c>T</c> value. Keeping both facts together prevents a bare array reference from being mistaken for its
/// element type.
/// </summary>
internal readonly record struct QValueShape(QType Type, bool IsArray)
{
    public override string ToString() =>
        Type.ToString().ToLowerInvariant() + (IsArray ? "[]" : string.Empty);
}

/// <summary>
/// The single expression-type reader used by symbol construction and semantic checks. A user function's
/// call node carries the declaring operation's stable Id, so its type follows
/// <c>QCallNode.CalleeOpId -&gt; QOperation.ReturnType</c>. Built-in functions remain target-independent
/// language primitives and read their return type from <see cref="QoraGates.Functions"/>.
/// </summary>
internal static class ExpressionTypes
{
    /// <summary>
    /// Resolve a user-function call exclusively through the operation identity installed by
    /// <see cref="Resolver"/>. A spelling is never used to recover a missing semantic reference.
    /// </summary>
    internal static bool TryGetFunction(
        QCallNode call,
        IReadOnlyDictionary<int, QOperation> opById,
        out QOperation function)
    {
        function = null!;
        if (call.CalleeOpId is not int operationId)
            return false;
        if (!opById.TryGetValue(operationId, out var operation))
            throw new InvalidOperationException(
                $"QINTERNAL: function call `{call.Name}` carries dangling CalleeOpId {operationId}");

        if (operation is not { IsFunction: true })
            return false;

        function = operation;
        return true;
    }

    internal static QValueShape? TypeOf(
        QExpr value,
        Scope scope,
        IReadOnlyDictionary<int, QOperation> opById) => value switch
    {
        QMeasure => new(QType.Bit, IsArray: false),
        QText text => TypeOf(text.Tree, scope, opById),
        QArrayNew allocation => new(allocation.ElementType, IsArray: true),
        QArrayLiteral literal => ArrayTypeOf(literal, scope, opById),
        _ => null,
    };

    internal static QValueShape? TypeOf(
        QNode? node,
        Scope scope,
        IReadOnlyDictionary<int, QOperation> opById) => node switch
    {
        null => null,
        QNumLit => new(QType.Int, IsArray: false),
        QLit literal => LiteralType(literal.Text),
        QNameRef name => NameType(name.Name, scope),
        QMember { Base: { } owner, Member: "Count" }
            when TypeOf(owner, scope, opById) is { IsArray: true }
            => new(QType.Int, IsArray: false),
        QUnary unary => UnaryType(unary, scope, opById),
        QBinOp binary => BinaryType(binary, scope, opById),
        QIndexNode index => TypeOf(index.Base, scope, opById) is
            { IsArray: true } array
            ? new(array.Type, IsArray: false)
            : null,
        QCallNode call when call.CalleeOpId is not null
                            && TryGetFunction(call, opById, out var function)
                            && function.ReturnType is { } returns
            => new(returns, IsArray: false),
        QCallNode call when call.CalleeOpId is null
                            && QoraGates.Functions.TryGetValue(call.Name, out var builtin)
            => new(builtin.Returns, IsArray: false),
        _ => null,
    };

    /// <summary>
    /// Assignment compatibility for a declared callable/value contract. Qora historically keeps ordinary
    /// scalar declarations loose, so this deliberately records only the established safe/contextual conversions:
    /// bit to int, int to float, a real expression to angle, and the literal 0 or 1 in a bit slot. Shape
    /// must always agree; a whole array is never one scalar value.
    /// </summary>
    internal static bool CanAssign(QValueShape target, QValueShape source, QExpr value)
    {
        if (target.IsArray != source.IsArray) return false;
        if (target.Type == source.Type) return true;
        if (target.Type == QType.Qubit || source.Type == QType.Qubit) return false;

        return (target.Type, source.Type) switch
        {
            (QType.Int, QType.Bit) => true,
            (QType.Float, QType.Int) => true,
            (QType.Angle, QType.Float) => true,
            (QType.Bit, QType.Int) => value is
                QText { Tree: QNumLit { Value: 0 or 1 } },
            _ => false,
        };
    }

    private static QValueShape? NameType(string name, Scope scope)
    {
        if (name is "pi" or "tau" or "euler") return new(QType.Float, IsArray: false);
        if (name is "true" or "false") return new(QType.Bit, IsArray: false);
        return scope.Lookup(name) is { Type: { } type } symbol
            ? new(type, symbol.IsArray)
            : null;
    }

    private static QValueShape LiteralType(string text) =>
        text is "true" or "false"
            ? new(QType.Bit, IsArray: false)
            : text is "pi" or "tau" or "euler" || text.Contains('.')
                ? new(QType.Float, IsArray: false)
                : new(QType.Int, IsArray: false);

    private static QValueShape? UnaryType(QUnary unary, Scope scope,
        IReadOnlyDictionary<int, QOperation> opById)
    {
        var operand = TypeOf(unary.Operand, scope, opById);
        if (operand is not { } scalar) return null;
        if (scalar.IsArray) return scalar;   // preserve the offending shape for the boundary diagnostic
        if (unary.Op == "!") return new(QType.Bit, IsArray: false);
        return unary.Op == "-" && scalar.Type == QType.Bit
            ? new(QType.Int, IsArray: false)
            : scalar;
    }

    private static QValueShape? BinaryType(QBinOp binary, Scope scope,
        IReadOnlyDictionary<int, QOperation> opById)
    {
        var left = TypeOf(binary.Left, scope, opById);
        var right = TypeOf(binary.Right, scope, opById);
        if (left is not { } lhs || right is not { } rhs) return null;
        if (lhs.IsArray) return lhs;   // preserve the offending shape for the boundary diagnostic
        if (rhs.IsArray) return rhs;
        if (lhs.Type == QType.Qubit || rhs.Type == QType.Qubit) return null;

        if (binary.Op is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||")
            return new(QType.Bit, IsArray: false);

        if (lhs.Type == QType.Float || rhs.Type == QType.Float)
            return new(QType.Float, IsArray: false);
        if (lhs.Type == QType.Angle || rhs.Type == QType.Angle)
            return new(QType.Angle, IsArray: false);
        return new(QType.Int, IsArray: false);
    }

    private static QValueShape? ArrayTypeOf(QArrayLiteral literal, Scope scope,
        IReadOnlyDictionary<int, QOperation> opById)
    {
        QType? elementType = null;
        foreach (var element in literal.Elements)
        {
            if (TypeOf(element, scope, opById) is not
                { IsArray: false } elementShape)
                return null;
            elementType = elementType is null
                ? elementShape.Type
                : CommonNumericType(elementType.Value, elementShape.Type);
            if (elementType is null) return null;
        }

        return elementType is { } type ? new(type, IsArray: true) : null;
    }

    private static QType? CommonNumericType(QType left, QType right)
    {
        if (left == right) return left;
        if (left == QType.Qubit || right == QType.Qubit) return null;
        if (left == QType.Float || right == QType.Float) return QType.Float;
        if (left == QType.Angle || right == QType.Angle) return QType.Angle;
        return QType.Int;
    }
}
