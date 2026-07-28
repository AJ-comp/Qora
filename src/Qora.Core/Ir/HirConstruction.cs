using Qora.Compiler;

namespace Qora.Ir;

/// <summary>
/// Stable identity of one HIR occurrence inside a single compilation revision.
/// The identity is intentionally distinct from semantic <c>SymbolId</c>, MIR IDs, and target IDs.
/// A node keeps this identity while a pass rewrites the same logical occurrence; a copied or synthesized
/// occurrence receives a fresh identity from the revision-bound construction authority.
/// </summary>
public readonly record struct HirNodeId
{
    internal HirNodeId(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"h{Value}";
}

/// <summary>
/// Common immutable surface of every source-shaped HIR node. Construction ownership is deliberately
/// internal: callers can inspect a published node, but only a lowering or rewrite session can create one.
/// </summary>
public abstract class HirNode
{
    internal HirNode(HirNodeStamp stamp)
    {
        Core = stamp.Core ?? throw new ArgumentNullException(nameof(stamp));
        CreationSession = stamp.Session ?? throw new ArgumentNullException(nameof(stamp));
        Id = stamp.Id;
        Span = stamp.Span;
    }

    public HirNodeId Id { get; }
    public SourceSpan? Span { get; }

    internal HirConstructionCore Core { get; }
    internal HirConstructionSession CreationSession { get; }

    /// <summary>
    /// Enumerates the authoritative child relation. The snapshot arena derives membership, parents,
    /// owning callables, and source maps from this one relation instead of maintaining a second tree.
    /// </summary>
    internal abstract IEnumerable<HirNode> Children();
}

/// <summary>Internal construction coordinates stamped into one immutable HIR node.</summary>
internal readonly record struct HirNodeStamp(
    HirConstructionCore Core,
    HirConstructionSession Session,
    HirNodeId Id,
    SourceSpan? Span);

/// <summary>
/// The one ID and revision authority shared by every document and every HIR rewrite in one compilation
/// revision. Sessions are short lived; this core survives across them so independently lowered imports
/// and later pass results can never accidentally mint colliding identities.
/// </summary>
internal sealed class HirConstructionCore
{
    private readonly object _gate = new();
    private readonly HashSet<SourceDocumentRef> _registeredDocuments = new();
    private readonly HashSet<SourceDocumentRef> _loweringStarted = new();
    private int _nextNode;
    private SourceSetSnapshot? _sources;

    public HirConstructionCore(
        CompilationId compilationId,
        CompilationRevision compilationRevision)
    {
        if (compilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "HIR construction requires a non-empty compilation identity.",
                nameof(compilationId));
        CompilationId = compilationId;
        CompilationRevision = compilationRevision;
    }

    public CompilationId CompilationId { get; }
    public CompilationRevision CompilationRevision { get; }

    public void RegisterDocument(SourceDocumentRef document)
    {
        RequireRevision(document, nameof(document));
        lock (_gate)
        {
            if (_sources is not null)
                throw new InvalidOperationException(
                    "The HIR source set is already sealed; no additional document can be registered.");
            if (!_registeredDocuments.Add(document))
                throw new InvalidOperationException(
                    $"Source document {document} is already registered for HIR construction.");
        }
    }

    public void BindSourceSet(SourceSetSnapshot sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.CompilationId != CompilationId
            || sources.CompilationRevision != CompilationRevision)
        {
            throw new ArgumentException(
                "A HIR construction core cannot bind a source set from another compilation revision.",
                nameof(sources));
        }
        lock (_gate)
        {
            if (_sources is not null && !ReferenceEquals(_sources, sources))
                throw new InvalidOperationException(
                    "The HIR construction core is already bound to another source-set snapshot.");

            foreach (var document in _registeredDocuments)
                if (!sources.Contains(document))
                {
                    throw new ArgumentException(
                        $"Registered HIR document {document} is absent from the final source set.",
                        nameof(sources));
                }
            foreach (var document in sources.Documents.Select(snapshot => snapshot.Ref))
                if (!_registeredDocuments.Contains(document))
                {
                    throw new ArgumentException(
                        $"Source-set document {document} was never registered with HIR construction.",
                        nameof(sources));
                }

            _sources = sources;
        }
    }

    public AstToHirLoweringSession BeginLowering(SourceDocumentRef document)
    {
        RequireRevision(document, nameof(document));
        lock (_gate)
        {
            if (_sources is not null)
                throw new InvalidOperationException(
                    "The HIR source set is sealed; no additional document lowering can begin.");
            if (!_registeredDocuments.Contains(document))
                throw new ArgumentException(
                    "The document must be registered before its syntax can be lowered.",
                    nameof(document));
            if (!_loweringStarted.Add(document))
                throw new InvalidOperationException(
                    $"HIR lowering already began for source document {document}.");
        }
        return new AstToHirLoweringSession(this, document);
    }

    public HirRewriteSession BeginRewrite(
        HirSnapshot source,
        string passName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.ConstructionCore, this))
            throw new ArgumentException(
                "A HIR rewrite session can only read a snapshot owned by its construction authority.",
                nameof(source));
        if (string.IsNullOrWhiteSpace(passName))
            throw new ArgumentException(
                "A HIR rewrite session requires a pass name.",
                nameof(passName));
        return new HirRewriteSession(this, source, passName);
    }

    internal HirNodeId NextNodeId() =>
        new(checked(Interlocked.Increment(ref _nextNode) - 1));

    internal void ValidateSpan(SourceSpan? span, string parameterName)
    {
        if (span is not { } source)
            return;
        RequireRevision(source.Document, parameterName);
        lock (_gate)
        {
            if (!_registeredDocuments.Contains(source.Document))
                throw new ArgumentException(
                    $"HIR source span refers to unregistered document {source.Document}.",
                    parameterName);
            if (_sources is not null && !_sources.Contains(source.Document))
                throw new ArgumentException(
                    $"HIR source span refers to a document outside the sealed source set: {source.Document}.",
                    parameterName);
        }
    }

    private void RequireRevision(
        SourceDocumentRef document,
        string parameterName)
    {
        if (document.CompilationId != CompilationId
            || document.CompilationRevision != CompilationRevision)
        {
            throw new ArgumentException(
                "HIR construction cannot mix documents from different compilation revisions.",
                parameterName);
        }
    }
}

/// <summary>
/// Short-lived authority used while one HIR tree or rewrite result is being assembled. It owns no global
/// state and becomes unusable after publication. Fluent expression methods delegate back to this exact
/// session, which prevents an ambient/AsyncLocal builder from leaking across compilations.
/// </summary>
internal abstract class HirConstructionSession
{
    private bool _sealed;

    protected HirConstructionSession(HirConstructionCore core)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
    }

    internal HirConstructionCore Core { get; }
    internal bool IsSealed => _sealed;

    internal void RequireActive()
    {
        if (_sealed)
            throw new InvalidOperationException(
                "This HIR construction session has already published its result.");
    }

    internal void RequireOwned(HirNode node, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(node);
        RequireActive();
        if (!ReferenceEquals(node.Core, Core))
            throw new ArgumentException(
                "HIR nodes from different compilation revisions cannot be composed.",
                parameterName);
        ValidateConsumableNode(node, parameterName);
    }

    protected HirNodeStamp Fresh(SourceSpan? span = null)
    {
        RequireActive();
        Core.ValidateSpan(span, nameof(span));
        ValidateSessionSpan(span, nameof(span));
        return new HirNodeStamp(Core, this, Core.NextNodeId(), span);
    }

    protected HirNodeStamp Preserve(HirNode source)
    {
        RequireOwned(source, nameof(source));
        Core.ValidateSpan(source.Span, nameof(source));
        ValidateSessionSpan(source.Span, nameof(source));
        return new HirNodeStamp(Core, this, source.Id, source.Span);
    }

    protected virtual void ValidateConsumableNode(
        HirNode node,
        string parameterName)
    {
    }

    protected virtual void ValidateSessionSpan(
        SourceSpan? span,
        string parameterName)
    {
    }

    internal HirNodeStamp FreshStamp(SourceSpan? span = null) => Fresh(span);

    internal virtual HirNodeStamp PreserveStamp(HirNode source) =>
        Preserve(source);

    internal virtual HirNodeStamp DeriveStamp(
        HirNode source,
        SourceSpan? span = null) =>
        throw new InvalidOperationException(
            "Only a HIR rewrite session can copy an existing HIR occurrence.");

    internal virtual HirNodeStamp SynthesizeStamp(
        HirNode source,
        string reason,
        SourceSpan? span = null) =>
        throw new InvalidOperationException(
            "Only a HIR rewrite session can synthesize a HIR occurrence.");

    internal void Seal()
    {
        RequireActive();
        _sealed = true;
    }

    internal HirExpression Missing(SourceSpan? span = null) =>
        new HirMissingExpression(Fresh(span));

    internal HirExpression Integer(
        long value,
        SourceSpan? span = null) =>
        new HirIntegerLiteralExpression(Fresh(span), value);

    internal HirExpression Literal(
        string text,
        SourceSpan? span = null) =>
        new HirLiteralExpression(Fresh(span), text);

    internal HirExpression Name(
        string name,
        SourceSpan? span = null) =>
        new HirNameExpression(Fresh(span), name);

    internal HirExpression Unary(
        HirUnaryOperator @operator,
        HirExpression operand,
        SourceSpan? span = null)
    {
        RequireOwned(operand, nameof(operand));
        return new HirUnaryExpression(
            Fresh(span ?? SpanOf(operand)),
            @operator,
            operand);
    }

    internal HirExpression Binary(
        HirBinaryOperator @operator,
        HirExpression left,
        HirExpression right,
        SourceSpan? span = null)
    {
        RequireOwned(left, nameof(left));
        RequireOwned(right, nameof(right));
        return new HirBinaryExpression(
            Fresh(span ?? SpanOf(left, right)),
            @operator,
            left,
            right);
    }

    internal HirExpression Member(
        HirExpression receiver,
        string member,
        SourceSpan? span = null)
    {
        RequireOwned(receiver, nameof(receiver));
        return new HirMemberAccessExpression(
            Fresh(span ?? SpanOf(receiver)),
            receiver,
            member);
    }

    internal HirExpression Index(
        HirExpression receiver,
        HirExpression index,
        SourceSpan? span = null)
    {
        RequireOwned(receiver, nameof(receiver));
        RequireOwned(index, nameof(index));
        return new HirIndexExpression(
            Fresh(span ?? SpanOf(receiver, index)),
            receiver,
            index);
    }

    internal HirCallExpression Call(
        HirExpression callee,
        IReadOnlyList<HirArgument> arguments,
        SourceSpan? span = null)
    {
        RequireOwned(callee, nameof(callee));
        RequireAllOwned(arguments, nameof(arguments));
        return new HirCallExpression(
            Fresh(span ?? SpanOf(new HirNode[] { callee }.Concat(arguments))),
            callee,
            arguments);
    }

    internal HirArgument Argument(
        HirExpression expression,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly,
        SourceSpan? span = null)
    {
        RequireOwned(expression, nameof(expression));
        return new HirArgument(
            Fresh(span ?? expression.Span),
            expression,
            ownership,
            access);
    }

    internal HirExpression Measurement(
        HirExpression target,
        SourceSpan? span = null)
    {
        RequireOwned(target, nameof(target));
        return new HirMeasurementExpression(
            Fresh(span ?? target.Span),
            target);
    }

    internal HirExpression ArrayLiteral(
        IReadOnlyList<HirExpression> elements,
        SourceSpan? span = null)
    {
        RequireAllOwned(elements, nameof(elements));
        return new HirArrayLiteralExpression(
            Fresh(span ?? SpanOf(elements)),
            elements);
    }

    internal HirExpression ArrayCreation(
        QType elementType,
        int length,
        SourceSpan? span = null) =>
        new HirArrayCreationExpression(
            Fresh(span),
            elementType,
            length);

    internal HirExpression QualifiedName(
        string name,
        SourceSpan? span = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Missing(span);
        var segments = name.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HirExpression expression = Name(segments[0], span);
        for (var index = 1; index < segments.Length; index++)
            expression = Member(expression, segments[index], span);
        return expression;
    }

    internal HirBlock Block(
        IReadOnlyList<HirStatement> statements,
        SourceSpan? span = null)
    {
        RequireAllOwned(statements, nameof(statements));
        return new HirBlock(Fresh(span), statements);
    }

    internal HirParameter Parameter(
        string name,
        QType type,
        int? registerSize = null,
        bool isArray = false,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly,
        SourceSpan? span = null) =>
        new(
            Fresh(span),
            name,
            type,
            registerSize,
            isArray,
            ownership,
            access);

    internal HirCallable Callable(
        string name,
        IReadOnlyList<HirParameter> parameters,
        HirBlock body,
        bool isFunction = false,
        QType? returnType = null,
        string? displayName = null,
        SourceSpan? span = null)
    {
        RequireAllOwned(parameters, nameof(parameters));
        RequireOwned(body, nameof(body));
        return new HirCallable(
            Fresh(span),
            name,
            parameters,
            body,
            isFunction,
            returnType,
            displayName);
    }

    internal HirNamespaceDeclaration Namespace(
        string name,
        IReadOnlyList<HirOpenDirective> openDirectives,
        IReadOnlyList<HirDeclaration> declarations,
        SourceSpan? span = null)
    {
        RequireAllOwned(openDirectives, nameof(openDirectives));
        RequireAllOwned(declarations, nameof(declarations));
        return new HirNamespaceDeclaration(
            Fresh(span),
            name,
            openDirectives,
            declarations);
    }

    internal HirImportDirective Import(
        string target,
        SourceSpan? span = null) =>
        new(Fresh(span), target);

    internal HirOpenDirective Open(
        string target,
        SourceSpan? span = null) =>
        new(Fresh(span), target);

    internal HirProgram Program(
        IReadOnlyList<HirDeclaration> declarations,
        IReadOnlyList<HirImportDirective>? imports = null,
        SourceSpan? span = null)
    {
        RequireAllOwned(declarations, nameof(declarations));
        if (imports is not null)
            RequireAllOwned(imports, nameof(imports));
        return new HirProgram(
            Fresh(span),
            declarations,
            imports ?? Array.Empty<HirImportDirective>());
    }

    internal HirQubitDeclarationStatement QubitDeclaration(
        string name,
        int size,
        SourceSpan? span = null) =>
        new(Fresh(span), name, size);

    internal HirCallExpression Call(
        string name,
        IReadOnlyList<HirArgument> arguments,
        HirNodeId? calleeId = null,
        SourceSpan? span = null)
    {
        var callee = QualifiedName(name, span);
        RequireAllOwned(arguments, nameof(arguments));
        return new HirCallExpression(
            Fresh(span),
            callee,
            arguments,
            calleeId);
    }

    internal HirCallStatement CallStatement(
        IReadOnlyList<QGateModifier> modifiers,
        HirCallExpression call,
        SourceSpan? span = null)
    {
        RequireOwned(call, nameof(call));
        return new HirCallStatement(
            Fresh(span),
            modifiers,
            call);
    }

    internal HirVariableDeclarationStatement VariableDeclaration(
        bool isConst,
        QType? type,
        string name,
        HirExpression value,
        bool isArray = false,
        SourceSpan? span = null)
    {
        RequireOwned(value, nameof(value));
        return new HirVariableDeclarationStatement(
            Fresh(span),
            isConst,
            type,
            name,
            value,
            isArray);
    }

    internal HirAssignmentStatement Assignment(
        HirExpression target,
        HirExpression value,
        SourceSpan? span = null)
    {
        RequireOwned(target, nameof(target));
        RequireOwned(value, nameof(value));
        return new HirAssignmentStatement(
            Fresh(span),
            target,
            value);
    }

    internal HirIfStatement If(
        HirExpression condition,
        HirBlock then,
        HirBlock @else,
        SourceSpan? span = null)
    {
        RequireOwned(condition, nameof(condition));
        RequireOwned(then, nameof(then));
        RequireOwned(@else, nameof(@else));
        return new HirIfStatement(
            Fresh(span),
            condition,
            then,
            @else);
    }

    internal HirReturnStatement Return(
        HirExpression value,
        SourceSpan? span = null)
    {
        RequireOwned(value, nameof(value));
        return new HirReturnStatement(
            Fresh(span),
            value);
    }

    internal HirForStatement For(
        string variable,
        HirExpression from,
        HirExpression to,
        HirBlock body,
        SourceSpan? span = null)
    {
        RequireOwned(from, nameof(from));
        RequireOwned(to, nameof(to));
        RequireOwned(body, nameof(body));
        return new HirForStatement(
            Fresh(span),
            variable,
            from,
            to,
            body);
    }

    internal HirWhileStatement While(
        HirExpression condition,
        HirBlock body,
        SourceSpan? span = null)
    {
        RequireOwned(condition, nameof(condition));
        RequireOwned(body, nameof(body));
        return new HirWhileStatement(
            Fresh(span),
            condition,
            body);
    }

    internal HirRepeatStatement Repeat(
        HirBlock body,
        HirExpression until,
        SourceSpan? span = null)
    {
        RequireOwned(body, nameof(body));
        RequireOwned(until, nameof(until));
        return new HirRepeatStatement(
            Fresh(span),
            body,
            until);
    }

    internal void RequireAllOwned<T>(
        IEnumerable<T> nodes,
        string parameterName)
        where T : HirNode
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var node in nodes)
            RequireOwned(node, parameterName);
    }

    private static SourceSpan? SpanOf(params HirNode[] nodes) =>
        SpanOf((IEnumerable<HirNode>)nodes);

    private static SourceSpan? SpanOf(IEnumerable<HirNode> nodes)
    {
        var spans = nodes
            .Select(node => node.Span)
            .Where(span => span is not null)
            .Select(span => span!.Value)
            .ToArray();
        if (spans.Length == 0)
            return null;
        var document = spans[0].Document;
        if (spans.Any(span => span.Document != document))
            return null;
        return new SourceSpan(
            document,
            spans.Min(span => span.Start),
            spans.Max(span => span.End));
    }
}

/// <summary>Construction session for one source document's AST-to-HIR lowering.</summary>
internal sealed class AstToHirLoweringSession : HirConstructionSession
{
    internal AstToHirLoweringSession(
        HirConstructionCore core,
        SourceDocumentRef document)
        : base(core)
    {
        Document = document;
    }

    public SourceDocumentRef Document { get; }

    protected override void ValidateConsumableNode(
        HirNode node,
        string parameterName)
    {
        if (!ReferenceEquals(node.CreationSession, this))
        {
            throw new ArgumentException(
                "AST-to-HIR lowering cannot compose occurrences minted by another document session.",
                parameterName);
        }
    }

    protected override void ValidateSessionSpan(
        SourceSpan? span,
        string parameterName)
    {
        if (span is { } source && source.Document != Document)
        {
            throw new ArgumentException(
                "AST-to-HIR lowering cannot stamp an occurrence with another document's source span.",
                parameterName);
        }
    }

    public HirProgram Publish(HirProgram root)
    {
        RequireOwned(root, nameof(root));
        Seal();
        return root;
    }
}

/// <summary>
/// Construction session for one structural HIR pass. It is the only component allowed to preserve an
/// identity in a rewritten node, copy a subtree with fresh identities, or synthesize a new occurrence.
/// The resulting lineage facts are emitted automatically when the session is published.
/// </summary>
internal sealed partial class HirRewriteSession : HirConstructionSession
{
    private readonly List<NodeDerivation> _derivations = new();
    private readonly List<NodeSynthesis> _syntheses = new();
    private readonly Dictionary<HirNode, HirNodeIntroduction> _adoptedNodes =
        new(ReferenceEqualityComparer.Instance);

    internal HirRewriteSession(
        HirConstructionCore core,
        HirSnapshot source,
        string passName)
        : base(core)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        PassName = passName;
    }

    public HirSnapshot Source { get; }
    public string PassName { get; }

    protected override void ValidateConsumableNode(
        HirNode node,
        string parameterName)
    {
        if (ReferenceEquals(node.CreationSession, this))
            return;
        if (ReferenceEquals(Source.Structure.FindNode(node.Id), node))
            return;
        if (_adoptedNodes.ContainsKey(node))
            return;

        throw new ArgumentException(
            "A HIR rewrite can consume only an exact occurrence from its source snapshot, "
            + "a node minted by this rewrite, or a subtree explicitly adopted from an imported "
            + "source document.",
            parameterName);
    }

    /// <summary>
    /// Adopts one immutable subtree lowered from another document in the same compilation revision.
    /// This is the only exception to the exact-source/current-session ownership rule. Every adopted
    /// occurrence receives its source-introduction classification automatically when a reachable result
    /// is published.
    /// </summary>
    public void AdoptImportedSubtree(
        HirNode root,
        SourceDocumentRef document)
    {
        ArgumentNullException.ThrowIfNull(root);
        RequireActive();
        if (!ReferenceEquals(root.Core, Core))
        {
            throw new ArgumentException(
                "An imported HIR subtree must belong to this compilation revision.",
                nameof(root));
        }
        if (ReferenceEquals(root.CreationSession, this)
            || ReferenceEquals(Source.Structure.FindNode(root.Id), root))
        {
            throw new ArgumentException(
                "Only a foreign document subtree absent from the rewrite source can be adopted.",
                nameof(root));
        }
        if (!root.CreationSession.IsSealed)
        {
            throw new ArgumentException(
                "An imported HIR subtree must already have been published by its document lowering.",
                nameof(root));
        }

        var active = new HashSet<HirNode>(ReferenceEqualityComparer.Instance);
        var pending = new List<(HirNode Node, HirNodeIntroduction Introduction)>();

        void Visit(HirNode node)
        {
            if (!ReferenceEquals(node.Core, Core))
            {
                throw new ArgumentException(
                    $"Imported HIR node {node.Id} belongs to another construction authority.",
                    nameof(root));
            }
            if (!node.CreationSession.IsSealed)
            {
                throw new ArgumentException(
                    $"Imported HIR node {node.Id} belongs to an unpublished construction session.",
                    nameof(root));
            }
            if (ReferenceEquals(Source.Structure.FindNode(node.Id), node)
                || ReferenceEquals(node.CreationSession, this))
            {
                throw new ArgumentException(
                    $"Imported HIR subtree crosses into a node already owned by this rewrite history: "
                    + $"{node.Id}.",
                    nameof(root));
            }
            if (_adoptedNodes.ContainsKey(node)
                || pending.Any(item => ReferenceEquals(item.Node, node)))
            {
                throw new ArgumentException(
                    $"Imported HIR node {node.Id} was adopted more than once.",
                    nameof(root));
            }
            if (!active.Add(node))
            {
                throw new ArgumentException(
                    $"Imported HIR node {node.Id} forms a cycle.",
                    nameof(root));
            }
            if (node.Span is not { } span || span.Document != document)
            {
                throw new ArgumentException(
                    $"Imported HIR node {node.Id} does not carry a source span from {document}.",
                    nameof(root));
            }

            pending.Add(
                (
                    node,
                    new HirNodeIntroduction(
                        node.Id,
                        document,
                        HirNodeIntroductionKind.ImportedDocument)));
            foreach (var child in node.Children())
                Visit(child);
            active.Remove(node);
        }

        Visit(root);
        foreach (var (node, introduction) in pending)
            _adoptedNodes.Add(node, introduction);
    }

    internal override HirNodeStamp PreserveStamp(HirNode source)
    {
        RequireExactSourceOccurrence(source, "preserved");
        return base.PreserveStamp(source);
    }

    internal override HirNodeStamp DeriveStamp(
        HirNode source,
        SourceSpan? span = null)
    {
        RequireOwned(source, nameof(source));
        RequireExactSourceOccurrence(source, "copied");
        var stamp = FreshStamp(span ?? source.Span);
        _derivations.Add(new NodeDerivation(source.Id, stamp.Id));
        return stamp;
    }

    internal override HirNodeStamp SynthesizeStamp(
        HirNode source,
        string reason,
        SourceSpan? span = null)
    {
        RequireOwned(source, nameof(source));
        RequireExactSourceOccurrence(source, "synthesis origin");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A synthesized HIR occurrence requires a reason.",
                nameof(reason));
        var stamp = FreshStamp(span ?? source.Span);
        _syntheses.Add(new NodeSynthesis(source.Id, stamp.Id, reason));
        return stamp;
    }

    public HirRewriteResult Publish(HirProgram root)
    {
        RequireOwned(root, nameof(root));
        var reachable = new HashSet<HirNode>(
            DescendantsAndSelf(root),
            ReferenceEqualityComparer.Instance);
        var reachableIds = reachable
            .Select(node => node.Id)
            .ToHashSet();
        var derivations = _derivations
            .Where(derivation => reachableIds.Contains(derivation.DerivedNodeId))
            .ToArray();
        var syntheses = _syntheses
            .Where(synthesis => reachableIds.Contains(synthesis.SynthesizedNodeId))
            .ToArray();
        var introductions = _adoptedNodes
            .Where(pair => reachable.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        Seal();
        return new HirRewriteResult(
            Source.Id,
            root,
            derivations,
            syntheses,
            introductions);
    }

    private void RequireExactSourceOccurrence(
        HirNode source,
        string role)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(Source.Structure.FindNode(source.Id), source))
        {
            throw new ArgumentException(
                $"A {role} HIR node must be the exact occurrence owned by the rewrite "
                + "session's source snapshot.",
                nameof(source));
        }
    }

    private static IEnumerable<HirNode> DescendantsAndSelf(HirNode root)
    {
        yield return root;
        foreach (var child in root.Children())
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
    }
}

/// <summary>Immutable output of one HIR rewrite session.</summary>
internal sealed record HirRewriteResult(
    HirSnapshotId Source,
    HirProgram Root,
    IReadOnlyList<NodeDerivation> Derivations,
    IReadOnlyList<NodeSynthesis> Syntheses,
    IReadOnlyList<HirNodeIntroduction> Introductions);
