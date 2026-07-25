namespace Qora.Ir.Passes;

/// <summary>
/// Stable identity of one semantic symbol inside a <see cref="ProgramSymbolGraph"/> snapshot. This is
/// deliberately distinct from a QIR declaration node Id: a namespace can merge declarations from several
/// files and synthetic/built-in symbols have no declaring QNode at all. Compiler stages may rebuild the
/// graph after structural rewrites; cross-stage references therefore use the preserved declaration node Id
/// (<see cref="Symbol.DeclarationNodeId"/> / <see cref="QCallNode.CalleeOpId"/>), not a graph-local SymbolId.
/// </summary>
public readonly record struct SymbolId(int Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Stable identity of one lexical scope inside a semantic-model snapshot.</summary>
public readonly record struct ScopeId(int Value)
{
    public override string ToString() => Value.ToString();
}

internal static class SemanticIds
{
    private static int _nextSymbol;
    private static int _nextScope;

    internal static SymbolId NextSymbol() =>
        new(System.Threading.Interlocked.Increment(ref _nextSymbol));

    internal static ScopeId NextScope() =>
        new(System.Threading.Interlocked.Increment(ref _nextScope));
}

/// <summary>Where a semantic symbol came from.</summary>
public enum SymbolOrigin
{
    Source,
    Synthetic,
    Builtin,
}

/// <summary>
/// The program declaration graph. <see cref="Symbol.OwnerSymbolId"/> is the one authoritative ownership
/// edge; child and name indexes here are derived when a symbol is registered and are not independently
/// writable. Namespace/type/callable lookup uses this graph. Local visibility remains the responsibility
/// of <see cref="Scope"/>.
///
/// A member name is role-aware. A namespace and callable with the same spelling may coexist under one
/// owner: an intermediate path segment selects the namespace, while a final call segment selects the
/// callable. This preserves <c>A()</c> alongside <c>A.B()</c> without flattening either declaration.
/// </summary>
public sealed class ProgramSymbolGraph
{
    private readonly Dictionary<SymbolId, Symbol> _symbols = new();
    private readonly Dictionary<int, SymbolId> _symbolByDeclaration = new();
    private readonly Dictionary<SymbolId, List<SymbolId>> _symbolsByOwner = new();
    private readonly Dictionary<(SymbolId Owner, string Name), List<SymbolId>> _membersByName = new();
    private readonly Dictionary<string, SymbolId> _namespaceByPath =
        new(StringComparer.Ordinal);

    public Symbol RootSymbol { get; }

    internal ProgramSymbolGraph()
    {
        RootSymbol = new Symbol(
            string.Empty,
            SymbolKind.Namespace,
            origin: SymbolOrigin.Synthetic);
        Register(RootSymbol, declaredMember: false);
        _namespaceByPath[string.Empty] = RootSymbol.Id;
    }

    /// <summary>Every semantic symbol registered in this graph snapshot.</summary>
    public IEnumerable<Symbol> AllSymbols => _symbols.Values;

    /// <summary>Every semantic symbol, keyed by its semantic identity.</summary>
    public IReadOnlyDictionary<SymbolId, Symbol> Symbols => _symbols;

    /// <summary>Find a symbol by semantic identity.</summary>
    public Symbol? FindSymbol(SymbolId id) =>
        _symbols.TryGetValue(id, out var symbol) ? symbol : null;

    /// <summary>Find a source declaration's semantic symbol.</summary>
    public Symbol? FindDeclaration(int declarationNodeId) =>
        _symbolByDeclaration.TryGetValue(declarationNodeId, out var id)
            ? FindSymbol(id)
            : null;

    /// <summary>
    /// Find one direct member by owner, spelling, and optional role. The optional kind is important:
    /// namespace and callable symbols may intentionally share the same owner/name slot.
    /// </summary>
    public Symbol? LookupMember(SymbolId ownerId, string name, SymbolKind? kind = null)
    {
        if (!_membersByName.TryGetValue((ownerId, name), out var ids)) return null;
        foreach (var id in ids)
            if (FindSymbol(id) is { } symbol && (kind is null || symbol.Kind == kind))
                return symbol;
        return null;
    }

    /// <summary>Every direct member matching one owner/name, in declaration order.</summary>
    public IReadOnlyList<Symbol> LookupMembers(SymbolId ownerId, string name) =>
        _membersByName.TryGetValue((ownerId, name), out var ids)
            ? ids.Select(id => _symbols[id]).ToList()
            : Array.Empty<Symbol>();

    /// <summary>All symbols semantically owned by <paramref name="ownerId"/>.</summary>
    public IReadOnlyList<Symbol> ChildrenOf(SymbolId ownerId) =>
        _symbolsByOwner.TryGetValue(ownerId, out var ids)
            ? ids.Select(id => _symbols[id]).ToList()
            : Array.Empty<Symbol>();

    /// <summary>
    /// Find the namespace represented by a canonical dotted path. The path index is a derived shortcut
    /// over the authoritative <see cref="Symbol.OwnerSymbolId"/> chain.
    /// </summary>
    public Symbol? FindNamespace(string namespacePath) =>
        _namespaceByPath.TryGetValue(namespacePath, out var id) ? FindSymbol(id) : null;

    /// <summary>Resolve a canonical callable spelling from the global symbol.</summary>
    public Symbol? LookupCallable(string name)
    {
        var segments = Segments(name);
        if (segments.Length == 0) return null;

        var owner = RootSymbol;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (LookupMember(owner.Id, segments[i], SymbolKind.Namespace) is not { } next)
                return null;
            owner = next;
        }

        return LookupMember(owner.Id, segments[^1], SymbolKind.Operation);
    }

    /// <summary>
    /// Search a bare callable through the namespace ownership chain: current namespace, each containing
    /// namespace, then the global symbol. Opened namespaces are deliberately handled by Resolver as a
    /// separate candidate set.
    /// </summary>
    public Symbol? LookupCallableOutward(SymbolId namespaceId, string name)
    {
        Symbol? owner = FindSymbol(namespaceId);
        while (owner is not null)
        {
            if (LookupMember(owner.Id, name, SymbolKind.Operation) is { } callable)
                return callable;
            owner = owner.OwnerSymbolId is { } parent ? FindSymbol(parent) : null;
        }
        return null;
    }

    /// <summary>The canonical dotted name derived from the authoritative owner chain.</summary>
    public string QualifiedName(Symbol symbol)
    {
        var segments = new Stack<string>();
        Symbol? current = symbol;
        while (current is not null && current.Id != RootSymbol.Id)
        {
            if (current.SourceName.Length > 0) segments.Push(current.SourceName);
            current = current.OwnerSymbolId is { } owner ? FindSymbol(owner) : null;
        }
        return string.Join(".", segments);
    }

    internal Symbol GetOrAddNamespace(string namespacePath, SymbolOrigin origin = SymbolOrigin.Source)
    {
        if (_namespaceByPath.TryGetValue(namespacePath, out var existing))
            return _symbols[existing];

        var owner = RootSymbol;
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
                owner = _symbols[known];
                continue;
            }

            var created = new Symbol(
                segment,
                SymbolKind.Namespace,
                ownerSymbolId: owner.Id,
                origin: origin);
            Register(created, declaredMember: true);
            _namespaceByPath[path] = created.Id;
            owner = created;
        }
        return owner;
    }

    internal void RegisterDeclaredMember(Symbol symbol) => Register(symbol, declaredMember: true);

    /// <summary>
    /// Register a parameter/local/block declaration. Its owner relationship is indexed for semantic
    /// navigation, but it is not a namespace/type member and therefore cannot be reached through a dot.
    /// </summary>
    internal void RegisterLexical(Symbol symbol) => Register(symbol, declaredMember: false);

    private void Register(Symbol symbol, bool declaredMember)
    {
        if (symbol.OwnerSymbolId is { } declaredOwnerId && !_symbols.ContainsKey(declaredOwnerId))
            throw new InvalidOperationException(
                $"QINTERNAL: symbol `{symbol.SourceName}` names unknown owner {declaredOwnerId}");

        if (!_symbols.TryAdd(symbol.Id, symbol))
            throw new InvalidOperationException($"QINTERNAL: duplicate semantic SymbolId {symbol.Id}");

        if (symbol.DeclarationNodeId is int declarationId)
        {
            if (!_symbolByDeclaration.TryAdd(declarationId, symbol.Id))
                throw new InvalidOperationException(
                    $"QINTERNAL: declaration node {declarationId} has more than one semantic symbol");
        }

        if (symbol.OwnerSymbolId is not { } ownerId) return;
        if (!_symbolsByOwner.TryGetValue(ownerId, out var children))
            _symbolsByOwner[ownerId] = children = new List<SymbolId>();
        children.Add(symbol.Id);

        if (!declaredMember) return;
        var key = (ownerId, symbol.SourceName);
        if (!_membersByName.TryGetValue(key, out var members))
            _membersByName[key] = members = new List<SymbolId>();
        members.Add(symbol.Id);
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
}
