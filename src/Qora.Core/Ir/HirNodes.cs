namespace Qora.Ir;

/// <summary>
/// A declaration that participates in the program's authoritative containment tree.
/// Namespace and callable ownership are represented by parent/child placement, never by copied path fields.
/// </summary>
public abstract class HirDeclaration : HirNode
{
    internal HirDeclaration(HirNodeStamp stamp)
        : base(stamp)
    {
    }
}

/// <summary>The immutable root of one source-shaped HIR tree.</summary>
public sealed class HirProgram : HirNode
{
    private readonly DeclarationIndex _index;

    internal HirProgram(
        HirNodeStamp stamp,
        IReadOnlyList<HirDeclaration> declarations,
        IReadOnlyList<HirImportDirective> imports)
        : base(stamp)
    {
        Declarations = HirCollections.Freeze(declarations);
        Imports = HirCollections.Freeze(imports);
        _index = new DeclarationIndex(Declarations);
    }

    /// <summary>The only authoritative top-level declaration relation.</summary>
    public IReadOnlyList<HirDeclaration> Declarations { get; }
    public IReadOnlyList<HirImportDirective> Imports { get; }

    /// <summary>All callables in declaration-tree order, derived from <see cref="Declarations"/>.</summary>
    public IReadOnlyList<HirCallable> Callables => _index.Callables;

    /// <summary>Every declared namespace path, derived from nested namespace declarations.</summary>
    public IReadOnlySet<string> NamespacePaths => _index.NamespacePaths;

    /// <summary>
    /// Direct open directives grouped by their containing namespace path. Repeated namespace blocks are
    /// merged in declaration-tree order; the namespace nodes remain the only authoritative owners.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HirOpenDirective>>
        OpenDirectivesByNamespace =>
        _index.OpenDirectivesByNamespace;

    /// <summary>The namespace path that structurally contains this exact callable occurrence.</summary>
    public string NamespaceOf(HirCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        if (!_index.CallableById.TryGetValue(callable.Id, out var indexed)
            || !ReferenceEquals(indexed.Callable, callable))
        {
            throw new ArgumentException(
                "The callable is not an exact occurrence in this HIR program.",
                nameof(callable));
        }
        return indexed.NamespacePath;
    }

    /// <summary>The namespace path that structurally contains the callable identity.</summary>
    public string NamespaceOf(HirNodeId callableId) =>
        _index.CallableById.TryGetValue(callableId, out var indexed)
            ? indexed.NamespacePath
            : throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                "the HIR program contains no callable with this identity");

    public HirCallable? EntryCallable =>
        Callables.FirstOrDefault(callable =>
            !callable.IsFunction
            && callable.Name == "Main"
            && NamespaceOf(callable).Length == 0)
        ?? Callables.FirstOrDefault(callable => !callable.IsFunction);

    internal override IEnumerable<HirNode> Children()
    {
        foreach (var import in Imports)
            yield return import;
        foreach (var declaration in Declarations)
            yield return declaration;
    }

    private sealed class DeclarationIndex
    {
        internal DeclarationIndex(
            IReadOnlyList<HirDeclaration> declarations)
        {
            var callables = new List<HirCallable>();
            var callableById =
                new Dictionary<
                    HirNodeId,
                    (HirCallable Callable, string NamespacePath)>();
            var namespacePaths = new HashSet<string>(StringComparer.Ordinal);
            var opens =
                new Dictionary<string, List<HirOpenDirective>>(
                    StringComparer.Ordinal);

            void Visit(
                IReadOnlyList<HirDeclaration> nested,
                string namespacePath)
            {
                foreach (var declaration in nested)
                {
                    switch (declaration)
                    {
                        case HirCallable callable:
                            callables.Add(callable);
                            if (!callableById.TryAdd(
                                    callable.Id,
                                    (callable, namespacePath)))
                            {
                                throw new ArgumentException(
                                    $"Callable identity {callable.Id} occurs more than once "
                                    + "in the declaration tree.",
                                    nameof(declarations));
                            }
                            break;

                        case HirNamespaceDeclaration @namespace:
                            var childPath = namespacePath.Length == 0
                                ? @namespace.Name
                                : $"{namespacePath}.{@namespace.Name}";
                            namespacePaths.Add(childPath);
                            if (@namespace.OpenDirectives.Count > 0)
                            {
                                if (!opens.TryGetValue(
                                        childPath,
                                        out var directives))
                                {
                                    opens.Add(
                                        childPath,
                                        directives = new List<HirOpenDirective>());
                                }
                                directives.AddRange(@namespace.OpenDirectives);
                            }
                            Visit(@namespace.Declarations, childPath);
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"QINTERNAL: declaration-tree indexing does not handle "
                                + $"`{declaration.GetType().Name}`.");
                    }
                }
            }

            Visit(declarations, string.Empty);
            Callables = HirCollections.Freeze(callables);
            CallableById = HirCollections.Freeze(callableById);
            NamespacePaths = HirCollections.FreezeSet(namespacePaths);
            OpenDirectivesByNamespace = HirCollections.Freeze(
                opens.Select(pair =>
                    new KeyValuePair<
                        string,
                        IReadOnlyList<HirOpenDirective>>(
                        pair.Key,
                        HirCollections.Freeze(pair.Value))));
        }

        internal IReadOnlyList<HirCallable> Callables { get; }
        internal IReadOnlyDictionary<
            HirNodeId,
            (HirCallable Callable, string NamespacePath)> CallableById { get; }
        internal IReadOnlySet<string> NamespacePaths { get; }
        internal IReadOnlyDictionary<
            string,
            IReadOnlyList<HirOpenDirective>> OpenDirectivesByNamespace { get; }
    }
}

/// <summary>
/// One source namespace segment. A dotted source declaration such as <c>namespace A.B</c> is represented
/// as an <c>A</c> node containing a <c>B</c> node, so containment is never reconstructed from strings.
/// </summary>
public sealed class HirNamespaceDeclaration : HirDeclaration
{
    internal HirNamespaceDeclaration(
        HirNodeStamp stamp,
        string name,
        IReadOnlyList<HirOpenDirective> openDirectives,
        IReadOnlyList<HirDeclaration> declarations)
        : base(stamp)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('.'))
        {
            throw new ArgumentException(
                "A HIR namespace declaration name must be one non-empty segment.",
                nameof(name));
        }

        Name = name;
        OpenDirectives = HirCollections.Freeze(openDirectives);
        Declarations = HirCollections.Freeze(declarations);
    }

    public string Name { get; }
    public IReadOnlyList<HirOpenDirective> OpenDirectives { get; }
    public IReadOnlyList<HirDeclaration> Declarations { get; }

    internal override IEnumerable<HirNode> Children()
    {
        foreach (var directive in OpenDirectives)
            yield return directive;
        foreach (var declaration in Declarations)
            yield return declaration;
    }
}

/// <summary>One <c>open Target;</c> directive inside a namespace block.</summary>
public sealed class HirOpenDirective : HirNode
{
    internal HirOpenDirective(
        HirNodeStamp stamp,
        string target)
        : base(stamp)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public string Target { get; }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

/// <summary>One quoted source import declaration.</summary>
public sealed class HirImportDirective : HirNode
{
    internal HirImportDirective(
        HirNodeStamp stamp,
        string target)
        : base(stamp)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public string Target { get; }
    public string Display => $"\"{Target}\"";

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

/// <summary>An operation or a pure value-returning function declaration.</summary>
public sealed class HirCallable : HirDeclaration, ICallableSig
{
    internal HirCallable(
        HirNodeStamp stamp,
        string name,
        IReadOnlyList<HirParameter> parameters,
        HirBlock body,
        bool isFunction = false,
        QType? returnType = null,
        string? displayName = null)
        : base(stamp)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('.'))
        {
            throw new ArgumentException(
                "A HIR callable declaration name must be one non-empty local segment.",
                nameof(name));
        }

        Name = name;
        Parameters = HirCollections.Freeze(parameters);
        Body = body ?? throw new ArgumentNullException(nameof(body));
        IsFunction = isFunction;
        ReturnType = returnType;
        DisplayName = displayName;
    }

    public string Name { get; }
    public IReadOnlyList<HirParameter> Parameters { get; }
    public HirBlock Body { get; }
    public bool IsFunction { get; }
    public QType? ReturnType { get; }
    public string? DisplayName { get; }
    public string CalleeName => DisplayName ?? Name;

    bool ICallableSig.IsBuiltin => false;
    IReadOnlyList<IParamSpec> ICallableSig.Parameters => Parameters;

    internal override IEnumerable<HirNode> Children()
    {
        foreach (var parameter in Parameters)
            yield return parameter;
        yield return Body;
    }
}

/// <summary>A callable parameter declaration and its ownership/access contract.</summary>
public sealed class HirParameter : HirNode, IParamSpec
{
    internal HirParameter(
        HirNodeStamp stamp,
        string name,
        QType type,
        int? registerSize,
        bool isArray,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly)
        : base(stamp)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type;
        RegisterSize = registerSize;
        IsArray = isArray || registerSize is not null;
        Ownership = ownership;
        Access = access;
    }

    public string Name { get; }
    public QType Type { get; }
    public int? RegisterSize { get; }
    public bool IsArray { get; }
    public bool IsQubitArray => Type == QType.Qubit && IsArray;
    public QOwnershipMode Ownership { get; }
    public QAccessMode Access { get; }
    public bool NeedsMonoSizing =>
        (IsQubitArray || Type == QType.Bit && IsArray)
        && RegisterSize is null;
    public bool QubitBroadcast => false;

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

/// <summary>An identity-bearing lexical statement block.</summary>
public sealed class HirBlock : HirNode, IReadOnlyList<HirStatement>
{
    private readonly IReadOnlyList<HirStatement> _statements;

    internal HirBlock(
        HirNodeStamp stamp,
        IReadOnlyList<HirStatement> statements)
        : base(stamp)
    {
        _statements = HirCollections.Freeze(statements);
    }

    public IReadOnlyList<HirStatement> Statements => _statements;
    public int Count => _statements.Count;
    public HirStatement this[int index] => _statements[index];
    public IEnumerator<HirStatement> GetEnumerator() => _statements.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    internal override IEnumerable<HirNode> Children() => _statements;
}

public abstract class HirStatement : HirNode
{
    internal HirStatement(HirNodeStamp stamp)
        : base(stamp)
    {
    }
}

/// <summary><c>use q = Qubit[Size];</c></summary>
public sealed class HirQubitDeclarationStatement : HirStatement
{
    internal HirQubitDeclarationStatement(
        HirNodeStamp stamp,
        string name,
        int size)
        : base(stamp)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Size = size;
    }

    public string Name { get; }
    public int Size { get; }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

/// <summary>
/// A source call statement. The call expression is shared with value calls, so name resolution,
/// specialization, expression identity, and argument traversal use one path.
/// </summary>
public sealed class HirCallStatement : HirStatement
{
    internal HirCallStatement(
        HirNodeStamp stamp,
        IReadOnlyList<QGateModifier> modifiers,
        HirCallExpression call)
        : base(stamp)
    {
        Modifiers = HirCollections.Freeze(modifiers);
        Call = call ?? throw new ArgumentNullException(nameof(call));
    }

    public IReadOnlyList<QGateModifier> Modifiers { get; }
    public HirCallExpression Call { get; }
    public string Name => Call.Name;

    internal override IEnumerable<HirNode> Children()
    {
        yield return Call;
    }
}

public sealed class HirVariableDeclarationStatement : HirStatement
{
    internal HirVariableDeclarationStatement(
        HirNodeStamp stamp,
        bool isConst,
        QType? type,
        string name,
        HirExpression value,
        bool isArray = false)
        : base(stamp)
    {
        IsConst = isConst;
        Type = type;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsArray = isArray;
    }

    public bool IsConst { get; }
    public QType? Type { get; }
    public string Name { get; }
    public HirExpression Value { get; }
    public bool IsArray { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Value;
    }
}

public sealed class HirAssignmentStatement : HirStatement
{
    internal HirAssignmentStatement(
        HirNodeStamp stamp,
        HirExpression target,
        HirExpression value)
        : base(stamp)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The assignable expression occurrence. Today this is a name or indexed access; keeping it as an
    /// expression lets future field and property assignments use the same tree without adding parallel
    /// name/index/member fields.
    /// </summary>
    public HirExpression Target { get; }
    public HirExpression Value { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Target;
        yield return Value;
    }
}

public sealed class HirIfStatement : HirStatement
{
    internal HirIfStatement(
        HirNodeStamp stamp,
        HirExpression condition,
        HirBlock then,
        HirBlock @else)
        : base(stamp)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Then = then ?? throw new ArgumentNullException(nameof(then));
        Else = @else ?? throw new ArgumentNullException(nameof(@else));
    }

    public HirExpression Condition { get; }
    public HirBlock Then { get; }
    public HirBlock Else { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Condition;
        yield return Then;
        yield return Else;
    }
}

public sealed class HirReturnStatement : HirStatement
{
    internal HirReturnStatement(
        HirNodeStamp stamp,
        HirExpression value)
        : base(stamp)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public HirExpression Value { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Value;
    }
}

public sealed class HirForStatement : HirStatement
{
    internal HirForStatement(
        HirNodeStamp stamp,
        string variable,
        HirExpression from,
        HirExpression to,
        HirBlock body)
        : base(stamp)
    {
        Variable = variable ?? throw new ArgumentNullException(nameof(variable));
        From = from ?? throw new ArgumentNullException(nameof(from));
        To = to ?? throw new ArgumentNullException(nameof(to));
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    public string Variable { get; }
    public HirExpression From { get; }
    public HirExpression To { get; }
    public HirBlock Body { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return From;
        yield return To;
        yield return Body;
    }
}

public sealed class HirWhileStatement : HirStatement
{
    internal HirWhileStatement(
        HirNodeStamp stamp,
        HirExpression condition,
        HirBlock body)
        : base(stamp)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    public HirExpression Condition { get; }
    public HirBlock Body { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Condition;
        yield return Body;
    }
}

public sealed class HirRepeatStatement : HirStatement
{
    internal HirRepeatStatement(
        HirNodeStamp stamp,
        HirBlock body,
        HirExpression until)
        : base(stamp)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Until = until ?? throw new ArgumentNullException(nameof(until));
    }

    public HirBlock Body { get; }
    public HirExpression Until { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Body;
        yield return Until;
    }
}

/// <summary>
/// One call argument. The expression is type-neutral; qubit/classical interpretation belongs to the
/// semantic model rather than to parallel syntax classes.
/// </summary>
public sealed class HirArgument : HirNode
{
    internal HirArgument(
        HirNodeStamp stamp,
        HirExpression expression,
        QOwnershipMode ownership,
        QAccessMode access)
        : base(stamp)
    {
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        Ownership = ownership;
        Access = access;
    }

    public HirExpression Expression { get; }
    public QOwnershipMode Ownership { get; }
    public QAccessMode Access { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Expression;
    }
}

public enum HirUnaryOperator
{
    Negate,
    LogicalNot,
}

public enum HirBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
}

/// <summary>
/// The one expression abstraction used by initializers, assignments, conditions, bounds, arguments,
/// indexes, measurements, and array elements.
/// </summary>
public abstract class HirExpression : HirNode
{
    internal HirExpression(HirNodeStamp stamp)
        : base(stamp)
    {
    }

    internal HirExpression Add(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.Add, this, right);

    internal HirExpression Subtract(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.Subtract, this, right);

    internal HirExpression Multiply(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.Multiply, this, right);

    internal HirExpression Divide(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.Divide, this, right);

    internal HirExpression EqualTo(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.Equal, this, right);

    internal HirExpression NotEqualTo(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.NotEqual, this, right);

    internal HirExpression LessThan(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.LessThan, this, right);

    internal HirExpression LessThanOrEqual(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.LessThanOrEqual, this, right);

    internal HirExpression GreaterThan(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.GreaterThan, this, right);

    internal HirExpression GreaterThanOrEqual(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.GreaterThanOrEqual, this, right);

    internal HirExpression LogicalAnd(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.LogicalAnd, this, right);

    internal HirExpression LogicalOr(HirExpression right) =>
        CreationSession.Binary(HirBinaryOperator.LogicalOr, this, right);

    internal HirExpression Negate() =>
        CreationSession.Unary(HirUnaryOperator.Negate, this);

    internal HirExpression LogicalNot() =>
        CreationSession.Unary(HirUnaryOperator.LogicalNot, this);

    internal HirExpression Member(string member) =>
        CreationSession.Member(this, member);

    internal HirExpression At(HirExpression index) =>
        CreationSession.Index(this, index);

    internal HirCallExpression Call(params HirExpression[] arguments)
    {
        var args = arguments
            .Select(argument => CreationSession.Argument(argument))
            .ToArray();
        return CreationSession.Call(this, args);
    }
}

public sealed class HirMissingExpression : HirExpression
{
    internal HirMissingExpression(HirNodeStamp stamp)
        : base(stamp)
    {
    }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

public sealed class HirIntegerLiteralExpression : HirExpression
{
    internal HirIntegerLiteralExpression(
        HirNodeStamp stamp,
        long value)
        : base(stamp)
    {
        Value = value;
    }

    public long Value { get; }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

public sealed class HirLiteralExpression : HirExpression
{
    internal HirLiteralExpression(
        HirNodeStamp stamp,
        string text)
        : base(stamp)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Text { get; }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

public sealed class HirNameExpression : HirExpression
{
    internal HirNameExpression(
        HirNodeStamp stamp,
        string name)
        : base(stamp)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('.'))
        {
            throw new ArgumentException(
                "A HIR name expression must contain one non-empty segment; "
                + "qualified names use member-access nodes.",
                nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}

public sealed class HirUnaryExpression : HirExpression
{
    internal HirUnaryExpression(
        HirNodeStamp stamp,
        HirUnaryOperator @operator,
        HirExpression operand)
        : base(stamp)
    {
        Operator = @operator;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    public HirUnaryOperator Operator { get; }
    public HirExpression Operand { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Operand;
    }
}

public sealed class HirBinaryExpression : HirExpression
{
    internal HirBinaryExpression(
        HirNodeStamp stamp,
        HirBinaryOperator @operator,
        HirExpression left,
        HirExpression right)
        : base(stamp)
    {
        Operator = @operator;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }

    public HirBinaryOperator Operator { get; }
    public HirExpression Left { get; }
    public HirExpression Right { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Left;
        yield return Right;
    }
}

public sealed class HirMemberAccessExpression : HirExpression
{
    internal HirMemberAccessExpression(
        HirNodeStamp stamp,
        HirExpression receiver,
        string member)
        : base(stamp)
    {
        Receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        if (string.IsNullOrWhiteSpace(member)
            || member.Contains('.'))
        {
            throw new ArgumentException(
                "A HIR member-access name must contain one non-empty segment.",
                nameof(member));
        }

        MemberName = member;
    }

    public HirExpression Receiver { get; }
    public string MemberName { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Receiver;
    }
}

public sealed class HirIndexExpression : HirExpression
{
    internal HirIndexExpression(
        HirNodeStamp stamp,
        HirExpression receiver,
        HirExpression index)
        : base(stamp)
    {
        Receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        Index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public HirExpression Receiver { get; }
    public HirExpression Index { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Receiver;
        yield return Index;
    }
}

public sealed class HirCallExpression : HirExpression
{
    internal HirCallExpression(
        HirNodeStamp stamp,
        HirExpression callee,
        IReadOnlyList<HirArgument> arguments,
        HirNodeId? calleeId = null)
        : base(stamp)
    {
        Callee = callee ?? throw new ArgumentNullException(nameof(callee));
        Arguments = HirCollections.Freeze(arguments);
        CalleeId = calleeId;
    }

    public HirExpression Callee { get; }
    public IReadOnlyList<HirArgument> Arguments { get; }
    public HirNodeId? CalleeId { get; }
    public string Name =>
        HirExpressions.QualifiedNameOf(Callee)
        ?? HirExpressions.Render(Callee);

    internal override IEnumerable<HirNode> Children()
    {
        yield return Callee;
        foreach (var argument in Arguments)
            yield return argument;
    }
}

public sealed class HirMeasurementExpression : HirExpression
{
    internal HirMeasurementExpression(
        HirNodeStamp stamp,
        HirExpression target)
        : base(stamp)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public HirExpression Target { get; }

    internal override IEnumerable<HirNode> Children()
    {
        yield return Target;
    }
}

public sealed class HirArrayLiteralExpression : HirExpression
{
    internal HirArrayLiteralExpression(
        HirNodeStamp stamp,
        IReadOnlyList<HirExpression> elements)
        : base(stamp)
    {
        Elements = HirCollections.Freeze(elements);
    }

    public IReadOnlyList<HirExpression> Elements { get; }

    internal override IEnumerable<HirNode> Children() => Elements;
}

public sealed class HirArrayCreationExpression : HirExpression
{
    internal HirArrayCreationExpression(
        HirNodeStamp stamp,
        QType elementType,
        int length)
        : base(stamp)
    {
        ElementType = elementType;
        Length = length;
    }

    public QType ElementType { get; }
    public int Length { get; }

    internal override IEnumerable<HirNode> Children() =>
        Array.Empty<HirNode>();
}
