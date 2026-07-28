namespace Qora.Ir.Passes;

/// <summary>
/// Stable identity of one semantic symbol inside a <see cref="HirScopeGraph"/> snapshot. This is
/// deliberately distinct from a HIR declaration node Id: a namespace can merge declarations from several
/// files and synthetic/built-in symbols have no declaring HIR node at all. Compiler stages may rebuild the
/// graph after structural rewrites; cross-stage references therefore use the preserved declaration node Id
/// (<see cref="Symbol.DeclarationNodeId"/> / <see cref="HirCallExpression.CalleeId"/>), not a graph-local
/// SymbolId.
/// </summary>
public readonly record struct SymbolId(int Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Stable identity of one HIR name-resolution scope inside a semantic-model snapshot.</summary>
public readonly record struct ScopeId(int Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Where a semantic symbol came from.</summary>
public enum SymbolOrigin
{
    Source,
    Synthetic,
    Builtin,
}

/// <summary>
/// The role of one node in the HIR scope graph. <see cref="Type"/> and
/// <see cref="GenericParameters"/> reserve the structural distinction needed by future nominal types and
/// generics without pretending that either feature exists in the language today.
/// </summary>
public enum HirScopeKind
{
    Program,
    Namespace,
    Type,
    Callable,
    Block,
    Loop,
    Condition,
    GenericParameters,
}

/// <summary>
/// A non-containment route consulted by a role-specific name lookup. The lexical/program hierarchy always
/// uses <see cref="Scope.ParentScopeId"/>; imports and future inheritance relationships must never replace
/// that one parent.
/// </summary>
public enum HirScopeEdgeKind
{
    Import,
    BaseType,
    Interface,
    Extension,
}

/// <summary>One typed, directed lookup edge between two HIR scopes.</summary>
public readonly record struct HirScopeEdge(
    ScopeId SourceScopeId,
    ScopeId TargetScopeId,
    HirScopeEdgeKind Kind);

/// <summary>
/// The stable source role that introduced a scope. A statement node can introduce several scopes, so its
/// node Id alone is insufficient: an <c>if</c>, for example, has a condition, then branch, and else branch.
/// </summary>
public enum HirScopeSiteRole
{
    CallableBody,
    IfCondition,
    IfThen,
    IfElse,
    ForBinder,
    ForBody,
    WhileCondition,
    WhileBody,
    RepeatBody,
    RepeatCondition,
}

/// <summary>A stable HIR node-and-role key for the scope introduced at that source site.</summary>
public readonly record struct HirScopeSite(HirNodeId OwnerNodeId, HirScopeSiteRole Role);

/// <summary>
/// One name-resolution environment in the unified HIR scope graph.
///
/// <see cref="ParentScopeId"/> is containment only:
/// program → namespace → type/callable → lexical block. Imports and future base/interface lookup are
/// separate typed edges. <see cref="DeclaringSymbolId"/> is set only when a declaration introduces this
/// environment (a namespace, type, or callable); ordinary block/loop/condition scopes leave it null.
///
/// Bindings are role-aware and may contain more than one SymbolId for the same spelling. This is required
/// for a namespace and a callable such as <c>A</c> and <c>A()</c> to coexist. Ordinary value lookup filters
/// those declaration roles out, so connecting a callable scope to its namespace parent cannot make a
/// callable or namespace appear to be a local value.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, List<SymbolId>> _bindings =
        new(StringComparer.Ordinal);
    private readonly HirScopeGraph _graph;

    public ScopeId Id { get; }
    public HirScopeKind Kind { get; }
    public ScopeId? ParentScopeId { get; }
    public SymbolId? DeclaringSymbolId { get; }
    internal HirScopeGraph Graph => _graph;

    internal Scope(
        HirScopeGraph graph,
        ScopeId id,
        HirScopeKind kind,
        ScopeId? parentScopeId,
        SymbolId? declaringSymbolId)
    {
        _graph = graph;
        Id = id;
        Kind = kind;
        ParentScopeId = parentScopeId;
        DeclaringSymbolId = declaringSymbolId;
    }

    /// <summary>Direct containment children, in construction order.</summary>
    public IReadOnlyList<ScopeId> ChildScopeIds => _graph.ChildrenOf(Id);

    /// <summary>Typed non-containment lookup edges leaving this scope.</summary>
    public IReadOnlyList<HirScopeEdge> LookupEdges => _graph.LookupEdgesFrom(Id);

    /// <summary>
    /// Resolve the nearest value binding. Namespace, type, operation, and built-in callable symbols are
    /// deliberately excluded even though their scopes now share this same graph.
    /// </summary>
    public Symbol? Lookup(string name) => LookupValue(name);

    /// <summary>Resolve a value in this exact scope only.</summary>
    public Symbol? LookupLocal(string name) =>
        LocalCandidates(name).FirstOrDefault(HirScopeGraph.IsValueSymbol);

    /// <summary>Resolve a value through the containment parent chain.</summary>
    public Symbol? LookupValue(string name)
    {
        for (Scope? current = this; current is not null; current = current.ParentScope)
            if (current.LookupLocal(name) is { } symbol)
                return symbol;
        return null;
    }

    /// <summary>Every direct binding with this spelling, preserving declaration order.</summary>
    public IReadOnlyList<Symbol> LookupLocalAll(string name) =>
        HirCollections.Freeze(LocalCandidates(name));

    /// <summary>Resolve an already-bound identity in this graph snapshot.</summary>
    internal Symbol GetSymbol(SymbolId id) =>
        _graph.FindSymbol(id)
        ?? throw new InvalidOperationException(
            $"QINTERNAL: semantic symbol {id} does not belong to this HIR scope graph");

    internal Scope? ParentScope =>
        ParentScopeId is { } parentId ? _graph.FindScope(parentId) : null;

    internal IEnumerable<Scope> ChildScopes =>
        ChildScopeIds.Select(childId => _graph.FindScope(childId)!);

    internal IEnumerable<Scope> ScopeTree()
    {
        yield return this;
        foreach (var child in ChildScopes)
            foreach (var descendant in child.ScopeTree())
                yield return descendant;
    }

    /// <summary>Every symbol declared directly in this scope, including role-sharing declarations.</summary>
    public IReadOnlyList<Symbol> LocalSymbols =>
        HirCollections.Freeze(
            _bindings.Values.SelectMany(ids => ids)
                .Select(id => _graph.FindSymbol(id)!)
                .Where(symbol => symbol is not null));

    /// <summary>This scope's symbols plus every containment descendant's symbols.</summary>
    public IReadOnlyList<Symbol> AllSymbols() =>
        HirCollections.Freeze(
            LocalSymbols.Concat(ChildScopes.SelectMany(child => child.AllSymbols())));

    /// <summary>
    /// Add a lexical/value declaration. Unlike namespace members, lexical declarations must have a unique
    /// spelling inside their exact scope.
    /// </summary>
    internal bool TryAdd(Symbol symbol) => _graph.TryRegisterLexical(this, symbol);

    internal bool HasBinding(string name) => _bindings.ContainsKey(name);

    internal void Bind(Symbol symbol)
    {
        _graph.RequireOpen();
        if (!_bindings.TryGetValue(symbol.SourceName, out var ids))
            _bindings[symbol.SourceName] = ids = new List<SymbolId>();
        ids.Add(symbol.Id);
    }

    private IEnumerable<Symbol> LocalCandidates(string name) =>
        _bindings.TryGetValue(name, out var ids)
            ? ids.Select(id => _graph.FindSymbol(id)!).Where(symbol => symbol is not null)
            : Enumerable.Empty<Symbol>();
}

/// <summary>
/// The single HIR name-resolution graph. Program, namespace, callable, and lexical scopes live in one
/// containment tree, while imports and future inheritance/member-search routes are typed side edges.
///
/// Authority is intentionally split by fact, not duplicated:
/// <list type="bullet">
/// <item><see cref="Symbol.DeclaringScopeId"/> says where a symbol is declared.</item>
/// <item><see cref="Scope.ParentScopeId"/> says which scope lexically/programmatically contains a scope.</item>
/// <item><see cref="Scope.DeclaringSymbolId"/> joins a namespace/type/callable declaration to the
/// environment it introduces.</item>
/// </list>
/// All child, member, declaration, namespace-path, and source-site maps are derived indexes owned here.
/// </summary>
public sealed class HirScopeGraph
{
    private readonly Dictionary<SymbolId, Symbol> _symbols = new();
    private readonly Dictionary<ScopeId, Scope> _scopes = new();
    private readonly Dictionary<HirNodeId, SymbolId> _symbolByDeclaration = new();
    private readonly Dictionary<ScopeId, List<ScopeId>> _childrenByParent = new();
    private readonly Dictionary<(SymbolId Symbol, HirScopeKind Kind), ScopeId> _ownedScopes = new();
    private readonly Dictionary<string, ScopeId> _namespaceByPath =
        new(StringComparer.Ordinal);
    private readonly Dictionary<HirScopeSite, ScopeId> _scopeBySite = new();
    private readonly Dictionary<ScopeId, List<HirScopeEdge>> _lookupEdges = new();
    private readonly Dictionary<HirNodeId, Scope> _callableScopeByDeclaration = new();
    private IReadOnlyDictionary<SymbolId, Symbol>? _symbolsView;
    private IReadOnlyDictionary<ScopeId, Scope>? _scopesView;
    private IReadOnlyDictionary<HirNodeId, Scope>? _callableScopesView;
    private bool _isSealed;
    private int _nextSymbolId;
    private int _nextScopeId;

    public Scope RootScope { get; }
    public bool IsSealed => _isSealed;

    internal HirScopeGraph()
    {
        RootScope = new Scope(
            this,
            NextScopeId(),
            HirScopeKind.Program,
            null,
            null);
        _scopes.Add(RootScope.Id, RootScope);
        _namespaceByPath[string.Empty] = RootScope.Id;
    }

    /// <summary>Every semantic symbol registered in this graph snapshot.</summary>
    public IReadOnlyList<Symbol> AllSymbols => HirCollections.Freeze(Symbols.Values);

    /// <summary>Every semantic symbol, keyed by its semantic identity.</summary>
    public IReadOnlyDictionary<SymbolId, Symbol> Symbols
    {
        get
        {
            RequireSealedPublicView();
            return _symbolsView!;
        }
    }

    /// <summary>Every HIR scope, keyed by its semantic identity.</summary>
    public IReadOnlyDictionary<ScopeId, Scope> Scopes
    {
        get
        {
            RequireSealedPublicView();
            return _scopesView!;
        }
    }

    /// <summary>Every callable body scope keyed by its declaration node Id.</summary>
    public IReadOnlyDictionary<HirNodeId, Scope> CallableScopes
    {
        get
        {
            RequireSealedPublicView();
            return _callableScopesView!;
        }
    }

    public Symbol? FindSymbol(SymbolId id) =>
        _symbols.TryGetValue(id, out var symbol) ? symbol : null;

    public Scope? FindScope(ScopeId id) =>
        _scopes.TryGetValue(id, out var scope) ? scope : null;

    public Symbol? FindDeclaration(HirNodeId declarationNodeId) =>
        _symbolByDeclaration.TryGetValue(declarationNodeId, out var id)
            ? FindSymbol(id)
            : null;

    public Scope? FindScope(HirScopeSite site) =>
        _scopeBySite.TryGetValue(site, out var id) ? FindScope(id) : null;

    internal Scope RequireScope(HirScopeSite site) =>
        FindScope(site)
        ?? throw new InvalidOperationException(
            $"QINTERNAL: HIR scope graph has no scope for site {site}");

    public Scope? FindCallableScope(HirNodeId declarationNodeId) =>
        _callableScopeByDeclaration.TryGetValue(declarationNodeId, out var scope)
            ? scope
            : null;

    public Scope? FindOwnedScope(SymbolId symbolId, HirScopeKind kind) =>
        _ownedScopes.TryGetValue((symbolId, kind), out var id) ? FindScope(id) : null;

    public Scope? FindNamespaceScope(string namespacePath) =>
        _namespaceByPath.TryGetValue(namespacePath, out var id) ? FindScope(id) : null;

    public Symbol? FindNamespaceSymbol(string namespacePath) =>
        FindNamespaceScope(namespacePath)?.DeclaringSymbolId is { } symbolId
            ? FindSymbol(symbolId)
            : null;

    /// <summary>Every direct containment child, in construction order.</summary>
    public IReadOnlyList<ScopeId> ChildrenOf(ScopeId parentId) =>
        _childrenByParent.TryGetValue(parentId, out var children)
            ? HirCollections.Freeze(children)
            : HirCollections.Freeze(Array.Empty<ScopeId>());

    public IReadOnlyList<HirScopeEdge> LookupEdgesFrom(ScopeId sourceScopeId) =>
        _lookupEdges.TryGetValue(sourceScopeId, out var edges)
            ? HirCollections.Freeze(edges)
            : HirCollections.Freeze(Array.Empty<HirScopeEdge>());

    /// <summary>
    /// Find one direct declaration by scope, spelling, and optional role. The optional kind is important:
    /// namespace and callable symbols may intentionally share the same scope/name slot.
    /// </summary>
    public Symbol? LookupMember(ScopeId scopeId, string name, SymbolKind? kind = null)
    {
        var scope = FindScope(scopeId);
        if (scope is null) return null;
        return scope.LookupLocalAll(name)
            .FirstOrDefault(symbol => kind is null || symbol.Kind == kind);
    }

    /// <summary>Every direct member matching one scope/name, in declaration order.</summary>
    public IReadOnlyList<Symbol> LookupMembers(ScopeId scopeId, string name) =>
        FindScope(scopeId)?.LookupLocalAll(name) ?? Array.Empty<Symbol>();

    /// <summary>Resolve a canonical dotted callable spelling from the program scope.</summary>
    public Symbol? LookupCallable(string name)
    {
        var segments = Segments(name);
        if (segments.Length == 0) return null;

        var owner = RootScope;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (LookupMember(owner.Id, segments[i], SymbolKind.Namespace) is not { } namespaceSymbol
                || FindOwnedScope(namespaceSymbol.Id, HirScopeKind.Namespace) is not { } next)
                return null;
            owner = next;
        }

        return LookupMember(owner.Id, segments[^1], SymbolKind.Callable);
    }

    /// <summary>
    /// Search a bare callable through namespace containment: current namespace, each containing namespace,
    /// then the program scope. Import edges are deliberately a separate candidate set.
    /// </summary>
    public Symbol? LookupCallableOutward(ScopeId namespaceScopeId, string name)
    {
        for (var scope = FindScope(namespaceScopeId);
             scope is not null;
             scope = scope.ParentScope)
        {
            if (scope.Kind is not (HirScopeKind.Namespace or HirScopeKind.Program))
                continue;
            if (LookupMember(scope.Id, name, SymbolKind.Callable) is { } callable)
                return callable;
        }
        return null;
    }

    /// <summary>Direct namespaces imported by this exact namespace scope. Imports are non-transitive.</summary>
    public IReadOnlyList<Scope> ImportedScopes(ScopeId sourceScopeId) =>
        HirCollections.Freeze(
            LookupEdgesFrom(sourceScopeId)
                .Where(edge => edge.Kind == HirScopeEdgeKind.Import)
                .Select(edge => FindScope(edge.TargetScopeId))
                .OfType<Scope>());

    /// <summary>The canonical dotted name derived from declaration-scope containment.</summary>
    public string QualifiedName(Symbol symbol)
    {
        var segments = new Stack<string>();
        if (symbol.SourceName.Length > 0) segments.Push(symbol.SourceName);

        for (var scope = FindScope(symbol.DeclaringScopeId);
             scope is not null && scope.Id != RootScope.Id;
             scope = scope.ParentScope)
        {
            if (scope.DeclaringSymbolId is { } declaringId
                && FindSymbol(declaringId) is { SourceName.Length: > 0 } owner)
                segments.Push(owner.SourceName);
        }

        return string.Join(".", segments);
    }

    /// <summary>The canonical dotted name of a namespace/type/callable scope.</summary>
    public string QualifiedName(Scope scope)
    {
        var segments = new Stack<string>();
        for (Scope? current = scope;
             current is not null && current.Id != RootScope.Id;
             current = current.ParentScope)
        {
            if (current.DeclaringSymbolId is { } declaringId
                && FindSymbol(declaringId) is { SourceName.Length: > 0 } owner)
                segments.Push(owner.SourceName);
        }
        return string.Join(".", segments);
    }

    /// <summary>
    /// The nearest declaration whose introduced scope contains <paramref name="scopeId"/>. This derives the
    /// old "enclosing callable" fact from the scope graph instead of copying it onto every local symbol.
    /// </summary>
    public Symbol? FindEnclosingSymbol(ScopeId scopeId)
    {
        for (var scope = FindScope(scopeId); scope is not null; scope = scope.ParentScope)
            if (scope.DeclaringSymbolId is { } symbolId)
                return FindSymbol(symbolId);
        return null;
    }

    /// <summary>The namespace/program scope in which a symbol is directly declared.</summary>
    public Scope? FindDeclaringNamespace(Symbol symbol)
    {
        for (var scope = FindScope(symbol.DeclaringScopeId);
             scope is not null;
             scope = scope.ParentScope)
            if (scope.Kind is HirScopeKind.Namespace or HirScopeKind.Program)
                return scope;
        return null;
    }

    internal Scope GetOrAddNamespace(
        string namespacePath,
        SymbolOrigin origin = SymbolOrigin.Source)
    {
        RequireOpen();
        if (_namespaceByPath.TryGetValue(namespacePath, out var existing))
            return _scopes[existing];

        var owner = RootScope;
        var prefix = new List<string>();
        var segments = Segments(namespacePath);
        if (namespacePath.Length > 0 && segments.Length == 0)
            throw new ArgumentException(
                $"`{namespacePath}` is not a valid dotted namespace path",
                nameof(namespacePath));

        foreach (var segment in segments)
        {
            prefix.Add(segment);
            var path = string.Join(".", prefix);
            if (_namespaceByPath.TryGetValue(path, out var known))
            {
                owner = _scopes[known];
                continue;
            }

            var created = CreateSymbol(
                owner.Id,
                segment,
                SymbolKind.Namespace,
                origin: origin);
            RegisterDeclaredMember(created);
            owner = CreateScope(
                HirScopeKind.Namespace,
                owner.Id,
                created.Id);
            _namespaceByPath[path] = owner.Id;
        }
        return owner;
    }

    internal void RegisterDeclaredMember(Symbol symbol)
    {
        RequireOpen();
        RegisterSymbol(symbol);
        _scopes[symbol.DeclaringScopeId].Bind(symbol);
    }

    /// <summary>
    /// Bind one concrete HIR declaration node to its semantic symbol. This relationship is graph-owned
    /// because it is not one-to-one: repeated namespace blocks have distinct declaration node Ids while
    /// all naming the same merged namespace symbol.
    /// </summary>
    internal void BindDeclaration(HirNodeId declarationNodeId, SymbolId symbolId)
    {
        RequireOpen();
        if (!_symbols.ContainsKey(symbolId))
            throw new InvalidOperationException(
                $"QINTERNAL: declaration node {declarationNodeId} names unknown semantic symbol {symbolId}");

        if (_symbolByDeclaration.TryGetValue(declarationNodeId, out var existing))
        {
            if (existing == symbolId) return;
            throw new InvalidOperationException(
                $"QINTERNAL: declaration node {declarationNodeId} is already bound to semantic symbol {existing}");
        }

        _symbolByDeclaration.Add(declarationNodeId, symbolId);
    }

    internal Scope CreateScope(
        HirScopeKind kind,
        ScopeId parentScopeId,
        SymbolId? declaringSymbolId = null,
        HirScopeSite? site = null)
    {
        RequireOpen();
        if (!_scopes.ContainsKey(parentScopeId))
            throw new InvalidOperationException(
                $"QINTERNAL: HIR scope names unknown parent {parentScopeId}");
        if (declaringSymbolId is { } declarationId)
        {
            var declaration = FindSymbol(declarationId)
                ?? throw new InvalidOperationException(
                    $"QINTERNAL: HIR scope names unknown declaring symbol {declarationId}");
            if (declaration.DeclaringScopeId != parentScopeId)
                throw new InvalidOperationException(
                    $"QINTERNAL: symbol {declarationId} is declared in scope {declaration.DeclaringScopeId}, not parent scope {parentScopeId}");
            if (_ownedScopes.ContainsKey((declarationId, kind)))
                throw new InvalidOperationException(
                    $"QINTERNAL: symbol {declarationId} already introduces a {kind} scope");

            var expectedDeclarationKind = kind switch
            {
                HirScopeKind.Namespace => SymbolKind.Namespace,
                HirScopeKind.Callable => SymbolKind.Callable,
                _ => (SymbolKind?)null,
            };
            if (expectedDeclarationKind is { } expected
                && declaration.Kind != expected)
                throw new InvalidOperationException(
                    $"QINTERNAL: a {kind} scope must be introduced by a {expected} symbol, but {declarationId} is {declaration.Kind}");
        }

        var scope = new Scope(
            this,
            NextScopeId(),
            kind,
            parentScopeId,
            declaringSymbolId);
        _scopes.Add(scope.Id, scope);
        if (!_childrenByParent.TryGetValue(parentScopeId, out var children))
            _childrenByParent[parentScopeId] = children = new List<ScopeId>();
        children.Add(scope.Id);

        if (declaringSymbolId is { } symbolId)
            _ownedScopes.Add((symbolId, kind), scope.Id);
        if (site is { } sourceSite)
            BindScopeSite(sourceSite, scope);
        return scope;
    }

    internal void BindScopeSite(HirScopeSite site, Scope scope)
    {
        RequireOpen();
        if (_scopeBySite.TryGetValue(site, out var existing))
        {
            if (existing == scope.Id) return;
            throw new InvalidOperationException(
                $"QINTERNAL: HIR scope site {site} is already bound to scope {existing}");
        }
        _scopeBySite.Add(site, scope.Id);
    }

    internal void RegisterCallableScope(HirNodeId declarationNodeId, Scope scope)
    {
        RequireOpen();
        if (scope.Kind != HirScopeKind.Callable)
            throw new InvalidOperationException(
                $"QINTERNAL: declaration {declarationNodeId} was bound to non-callable scope {scope.Id}");
        if (scope.DeclaringSymbolId is not { } declaringSymbolId
            || FindSymbol(declaringSymbolId) is not { Kind: SymbolKind.Callable } declaration
            || declaration.DeclarationNodeId != declarationNodeId)
            throw new InvalidOperationException(
                $"QINTERNAL: callable scope {scope.Id} is not introduced by operation declaration {declarationNodeId}");
        if (!_callableScopeByDeclaration.TryAdd(declarationNodeId, scope))
            throw new InvalidOperationException(
                $"QINTERNAL: declaration {declarationNodeId} already has a callable scope");
    }

    internal void AddLookupEdge(
        ScopeId sourceScopeId,
        ScopeId targetScopeId,
        HirScopeEdgeKind kind)
    {
        RequireOpen();
        if (!_scopes.ContainsKey(sourceScopeId) || !_scopes.ContainsKey(targetScopeId))
            throw new InvalidOperationException(
                $"QINTERNAL: lookup edge {sourceScopeId} -> {targetScopeId} names an unknown HIR scope");
        if (!_lookupEdges.TryGetValue(sourceScopeId, out var edges))
            _lookupEdges[sourceScopeId] = edges = new List<HirScopeEdge>();
        var edge = new HirScopeEdge(sourceScopeId, targetScopeId, kind);
        if (!edges.Contains(edge)) edges.Add(edge);
    }

    internal bool TryRegisterLexical(Scope scope, Symbol symbol)
    {
        RequireOpen();
        if (symbol.DeclaringScopeId != scope.Id)
            throw new InvalidOperationException(
                $"QINTERNAL: symbol `{symbol.SourceName}` declares scope {symbol.DeclaringScopeId}, but was inserted into {scope.Id}");
        if (scope.HasBinding(symbol.SourceName)) return false;
        RegisterSymbol(symbol);
        scope.Bind(symbol);
        return true;
    }

    internal static bool IsValueSymbol(Symbol symbol) => symbol.Kind is
        SymbolKind.Parameter
        or SymbolKind.Register
        or SymbolKind.MeasureBit
        or SymbolKind.Var
        or SymbolKind.Const
        or SymbolKind.LoopVar;

    /// <summary>
    /// Mint one immutable symbol through this graph's private identity authority. Callers describe the
    /// declaration; they never choose or coordinate SymbolIds themselves.
    /// </summary>
    internal Symbol CreateSymbol(
        ScopeId declaringScopeId,
        string name,
        SymbolKind kind,
        QType? type = null,
        bool isConst = false,
        string? constValue = null,
        SourceSpan? declSpan = null,
        int? registerSize = null,
        bool isArray = false,
        int? arrayLength = null,
        HirNodeId? declarationNodeId = null,
        SymbolOrigin origin = SymbolOrigin.Source,
        QOwnershipMode parameterOwnership = QOwnershipMode.Borrowed,
        QAccessMode parameterAccess = QAccessMode.ReadOnly,
        Bound? foldedBound = null,
        bool? foldedBoolean = null,
        bool monoSized = false) =>
        new(
            NextSymbolId(),
            declaringScopeId,
            name,
            kind,
            type,
            isConst,
            constValue,
            declSpan,
            registerSize,
            isArray,
            arrayLength,
            declarationNodeId,
            origin,
            parameterOwnership,
            parameterAccess,
            foldedBound,
            foldedBoolean,
            monoSized);

    private void RegisterSymbol(Symbol symbol)
    {
        if (!_scopes.ContainsKey(symbol.DeclaringScopeId))
            throw new InvalidOperationException(
                $"QINTERNAL: symbol `{symbol.SourceName}` names unknown declaring scope {symbol.DeclaringScopeId}");
        if (!_symbols.TryAdd(symbol.Id, symbol))
            throw new InvalidOperationException(
                $"QINTERNAL: duplicate semantic SymbolId {symbol.Id}");

        if (symbol.DeclarationNodeId is HirNodeId declarationId)
            BindDeclaration(declarationId, symbol.Id);
    }

    /// <summary>
    /// Publish the completed graph as one immutable semantic snapshot. Public aggregate views are created
    /// exactly once here; allowing them to cache during construction would let an early caller retain a
    /// permanently incomplete dictionary after later registrations.
    /// </summary>
    internal void Seal()
    {
        if (_isSealed) return;

        var symbolsView = HirCollections.Freeze(_symbols);
        var scopesView = HirCollections.Freeze(_scopes);
        var callableScopesView = HirCollections.Freeze(_callableScopeByDeclaration);

        foreach (var symbol in _symbols.Values)
            symbol.Seal();

        _symbolsView = symbolsView;
        _scopesView = scopesView;
        _callableScopesView = callableScopesView;
        _isSealed = true;
    }

    internal void RequireOpen()
    {
        if (_isSealed)
            throw new InvalidOperationException(
                "QINTERNAL: HIR scope graph is sealed by an immutable semantic artifact");
    }

    private void RequireSealedPublicView()
    {
        if (!_isSealed)
            throw new InvalidOperationException(
                "QINTERNAL: aggregate HIR scope views are unavailable until the graph is published");
    }

    private static string[] Segments(string name)
    {
        if (name.Length == 0) return Array.Empty<string>();
        var segments = name.Split('.');
        return segments.Any(segment =>
            segment.Length == 0
            || !string.Equals(segment, segment.Trim(), StringComparison.Ordinal))
            ? Array.Empty<string>()
            : segments;
    }

    private SymbolId NextSymbolId() => new(checked(_nextSymbolId++));

    private ScopeId NextScopeId() => new(checked(_nextScopeId++));
}
