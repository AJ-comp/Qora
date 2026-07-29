using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.LanguageServices;

/// <summary>
/// A single-use collector. Core invokes it on the compilation worker thread, and the language-service
/// session publishes its immutable result only after that compile call has completed.
/// </summary>
internal sealed class MirSemanticIndexCollector : IMirLoweringTraceSink
{
    private static readonly IComparer<SymbolId> SymbolComparer =
        Comparer<SymbolId>.Create((left, right) => left.Value.CompareTo(right.Value));
    private static readonly IComparer<MirCallableId> CallableComparer =
        Comparer<MirCallableId>.Create((left, right) => left.Value.CompareTo(right.Value));
    private static readonly IComparer<MirValueId> ValueComparer =
        Comparer<MirValueId>.Create((left, right) => left.Value.CompareTo(right.Value));
    private static readonly IComparer<MirStorageId> StorageComparer =
        Comparer<MirStorageId>.Create((left, right) => left.Value.CompareTo(right.Value));
    private static readonly IComparer<MirQubitKey> QubitComparer =
        Comparer<MirQubitKey>.Create(
            (left, right) =>
            {
                var identity = left.Id.Value.CompareTo(right.Id.Value);
                return identity != 0
                    ? identity
                    : left.Version.Value.CompareTo(right.Version.Value);
            });

    private readonly Dictionary<HirNodeId, MirCallableId> _callablesByDeclaration = new();
    private readonly BiMultiMap<SymbolId, MirCallableId> _callables = new();
    private readonly Dictionary<MirCallableId, MutableCallableIndex> _byCallable = new();
    private readonly HashSet<SymbolId> _unreachable = new();
    private bool _built;

    public void LinkCallable(
        HirNodeId declaration,
        SymbolId symbol,
        MirCallableId callable)
    {
        RequireOpen();
        if (_callablesByDeclaration.TryGetValue(declaration, out var existing)
            && existing != callable)
        {
            throw new InvalidOperationException(
                $"HIR callable {declaration} was linked to both {existing} and {callable}.");
        }

        _callablesByDeclaration[declaration] = callable;
        _callables.Add(symbol, callable);
        _ = Mutable(callable);
    }

    public void LinkValue(
        SymbolId symbol,
        MirCallableId callable,
        MirValueId value)
    {
        RequireOpen();
        Mutable(callable).Values.Add(symbol, value);
    }

    public void LinkStorage(
        SymbolId symbol,
        MirCallableId callable,
        MirStorageId storage)
    {
        RequireOpen();
        Mutable(callable).Storages.Add(symbol, storage);
    }

    public void LinkQubit(
        SymbolId symbol,
        MirCallableId callable,
        MirQubitKey qubit)
    {
        RequireOpen();
        Mutable(callable).Qubits.Add(symbol, qubit);
    }

    public void MarkUnreachable(SymbolId symbol)
    {
        RequireOpen();
        _unreachable.Add(symbol);
    }

    public MirSemanticIndex? Build(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        RequireOpen();
        _built = true;

        if (compilation.Mir is not { } mir)
        {
            if (HasTraceData())
            {
                throw new InvalidOperationException(
                    "MIR lowering produced trace events but the completed Compilation has no MIR.");
            }
            return null;
        }

        var hirArtifact = compilation.Hir.EffectAnalysis
            ?? throw new InvalidOperationException(
                "A Compilation with MIR has no final HIR effect-analysis artifact.");
        var graph = hirArtifact.Model.ScopeGraph
            ?? throw new InvalidOperationException(
                "The final HIR artifact has no sealed scope graph.");

        ValidateOwners(compilation, hirArtifact, mir);

        var completedCallables = mir.Program.Callables.ToDictionary(callable => callable.Id);
        ValidateCallableLinks(hirArtifact, graph, completedCallables);

        var mutableByCallable = completedCallables.Keys.ToDictionary(
            callable => callable,
            callable => _byCallable.GetValueOrDefault(callable) ?? new MutableCallableIndex());

        foreach (var (callableId, links) in _byCallable)
        {
            if (!completedCallables.TryGetValue(callableId, out var callable))
            {
                throw new InvalidOperationException(
                    $"The lowering trace names missing MIR callable {callableId}.");
            }
            ValidateCallableEntities(graph, callable, links);
        }

        foreach (var symbol in graph.AllSymbols)
        {
            if (FindOwningCallableSymbol(graph, symbol) is not { } owner)
                continue;

            foreach (var callable in _callables.ValuesFor(owner))
            {
                if (!mutableByCallable.TryGetValue(callable, out var links))
                {
                    throw new InvalidOperationException(
                        $"HIR symbol {symbol.Id} resolves to missing MIR callable {callable}.");
                }
                links.Symbols.Add(symbol.Id);
            }
        }

        foreach (var symbolId in _unreachable)
        {
            var symbol = graph.FindSymbol(symbolId)
                ?? throw new InvalidOperationException(
                    $"The lowering trace names missing HIR symbol {symbolId}.");
            var owner = FindOwningCallableSymbol(graph, symbol)
                ?? throw new InvalidOperationException(
                    $"Unreachable HIR symbol {symbolId} has no enclosing callable.");
            var ownerCallables = _callables.ValuesFor(owner);
            if (ownerCallables.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Unreachable HIR symbol {symbolId} has no lowered callable owner.");
            }

            foreach (var callable in ownerCallables)
            {
                var links = mutableByCallable[callable];
                if (links.Values.ContainsLeft(symbolId)
                    || links.Storages.ContainsLeft(symbolId)
                    || links.Qubits.ContainsLeft(symbolId))
                {
                    throw new InvalidOperationException(
                        $"Unreachable HIR symbol {symbolId} was also linked to a MIR entity.");
                }
                links.Unreachable.Add(symbolId);
            }
        }

        ValidateTotalSymbolCoverage(graph, mutableByCallable);

        var byCallable = completedCallables.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var links = mutableByCallable[pair.Key];
                return new MirCallableSemanticIndex(
                    graph,
                    pair.Value,
                    links.Symbols,
                    links.Values.FreezeForward(ValueComparer),
                    links.Values.FreezeReverse(SymbolComparer),
                    links.Storages.FreezeForward(StorageComparer),
                    links.Storages.FreezeReverse(SymbolComparer),
                    links.Qubits.FreezeForward(QubitComparer),
                    links.Qubits.FreezeReverse(SymbolComparer),
                    links.Unreachable);
            });

        return new MirSemanticIndex(
            compilation,
            hirArtifact,
            mir,
            _callablesByDeclaration,
            _callables.FreezeForward(CallableComparer),
            byCallable);
    }

    private void ValidateCallableLinks(
        HirSemanticArtifact hirArtifact,
        HirScopeGraph graph,
        IReadOnlyDictionary<MirCallableId, MirCallable> completedCallables)
    {
        var expectedEdges = new HashSet<(SymbolId Symbol, MirCallableId Callable)>();
        foreach (var (declaration, callable) in _callablesByDeclaration)
        {
            if (!completedCallables.ContainsKey(callable))
            {
                throw new InvalidOperationException(
                    $"HIR callable {declaration} links to missing MIR callable {callable}.");
            }
            if (!hirArtifact.Source.Structure.Contains(declaration)
                || hirArtifact.Source.Structure.RequireKind(declaration) != HirNodeKind.Callable)
            {
                throw new InvalidOperationException(
                    $"The lowering trace names non-callable HIR node {declaration}.");
            }

            var symbol = graph.FindDeclaration(declaration)
                ?? throw new InvalidOperationException(
                    $"HIR callable {declaration} has no semantic symbol.");
            if (symbol.Kind != SymbolKind.Callable
                || !_callables.Contains(symbol.Id, callable))
            {
                throw new InvalidOperationException(
                    $"HIR callable {declaration}, symbol {symbol.Id}, and MIR callable {callable} disagree.");
            }
            expectedEdges.Add((symbol.Id, callable));
        }

        var finalDeclarations = hirArtifact.Program.Callables
            .Select(callable => callable.Id)
            .ToHashSet();
        if (!finalDeclarations.SetEquals(_callablesByDeclaration.Keys))
        {
            throw new InvalidOperationException(
                "MIR callable lowering traces do not exactly cover the final HIR callable declarations.");
        }

        if (_callablesByDeclaration.Values.Distinct().Count()
            != _callablesByDeclaration.Count)
        {
            throw new InvalidOperationException(
                "Distinct final HIR callable declarations were linked to the same source MIR callable.");
        }

        var actualEdges = _callables.Forward
            .SelectMany(
                pair => pair.Value.Select(
                    callable => (Symbol: pair.Key, Callable: callable)))
            .ToHashSet();
        if (!actualEdges.SetEquals(expectedEdges))
        {
            throw new InvalidOperationException(
                "HIR callable declaration, symbol, and source MIR callable links do not form one exact edge set.");
        }

        foreach (var (symbol, callables) in _callables.Forward)
        {
            if (graph.FindSymbol(symbol) is not { Kind: SymbolKind.Callable })
            {
                throw new InvalidOperationException(
                    $"The lowering trace links non-callable HIR symbol {symbol} as a callable.");
            }
            foreach (var callable in callables)
            {
                if (!completedCallables.ContainsKey(callable))
                {
                    throw new InvalidOperationException(
                        $"HIR symbol {symbol} links to missing MIR callable {callable}.");
                }
            }
        }
    }

    private void ValidateCallableEntities(
        HirScopeGraph graph,
        MirCallable callable,
        MutableCallableIndex links)
    {
        var values = callable.Values.Select(value => value.Id).ToHashSet();
        var storages = callable.Storages.Select(storage => storage.Id).ToHashSet();
        var qubits = callable.Qubits.Select(qubit => qubit.Key).ToHashSet();

        ValidateEndpoints(
            graph,
            links.Values.Forward,
            values,
            callable.Id,
            "value",
            static symbol =>
                symbol.Kind is not SymbolKind.Namespace
                    and not SymbolKind.Callable
                    and not SymbolKind.BuiltinGate
                    and not SymbolKind.BuiltinFunction
                && symbol.Type != QType.Qubit);
        ValidateEndpoints(
            graph,
            links.Storages.Forward,
            storages,
            callable.Id,
            "storage",
            static symbol => symbol.Type != QType.Qubit && symbol.IsArray);
        ValidateEndpoints(
            graph,
            links.Qubits.Forward,
            qubits,
            callable.Id,
            "qubit",
            static symbol => symbol.Type == QType.Qubit);
    }

    private void ValidateEndpoints<TEntity>(
        HirScopeGraph graph,
        IReadOnlyDictionary<SymbolId, HashSet<TEntity>> links,
        IReadOnlySet<TEntity> completedEntities,
        MirCallableId callable,
        string entityName,
        Func<Symbol, bool> acceptsSymbol)
        where TEntity : notnull
    {
        foreach (var (symbolId, entities) in links)
        {
            var symbol = graph.FindSymbol(symbolId)
                ?? throw new InvalidOperationException(
                    $"The lowering trace names missing HIR symbol {symbolId}.");
            if (!acceptsSymbol(symbol))
            {
                throw new InvalidOperationException(
                    $"HIR symbol {symbolId} cannot lower to a MIR {entityName}.");
            }

            var owner = FindOwningCallableSymbol(graph, symbol)
                ?? throw new InvalidOperationException(
                    $"HIR symbol {symbolId} linked to MIR {entityName} has no enclosing callable.");
            if (!_callables.Contains(owner, callable))
            {
                throw new InvalidOperationException(
                    $"HIR symbol {symbolId} belongs to callable symbol {owner}, not MIR callable {callable}.");
            }

            foreach (var entity in entities)
            {
                if (!completedEntities.Contains(entity))
                {
                    throw new InvalidOperationException(
                        $"The lowering trace names missing MIR {entityName} {entity} in {callable}.");
                }
            }
        }
    }

    private void ValidateTotalSymbolCoverage(
        HirScopeGraph graph,
        IReadOnlyDictionary<MirCallableId, MutableCallableIndex> byCallable)
    {
        var valueSymbols = byCallable.Values
            .SelectMany(links => links.Values.Forward.Keys)
            .ToHashSet();
        var storageSymbols = byCallable.Values
            .SelectMany(links => links.Storages.Forward.Keys)
            .ToHashSet();
        var qubitSymbols = byCallable.Values
            .SelectMany(links => links.Qubits.Forward.Keys)
            .ToHashSet();

        foreach (var symbol in graph.AllSymbols)
        {
            var hasCallable = _callables.ContainsLeft(symbol.Id);
            var hasValue = valueSymbols.Contains(symbol.Id);
            var hasStorage = storageSymbols.Contains(symbol.Id);
            var hasQubit = qubitSymbols.Contains(symbol.Id);
            var isUnreachable = _unreachable.Contains(symbol.Id);

            if (symbol.Kind is SymbolKind.Namespace
                or SymbolKind.BuiltinGate
                or SymbolKind.BuiltinFunction)
            {
                if (hasCallable
                    || hasValue
                    || hasStorage
                    || hasQubit
                    || isUnreachable)
                {
                    throw new InvalidOperationException(
                        $"Non-lowered HIR symbol {symbol.Id} was linked to MIR.");
                }
                continue;
            }

            if (symbol.Kind == SymbolKind.Callable)
            {
                if (!hasCallable
                    || hasValue
                    || hasStorage
                    || hasQubit
                    || isUnreachable)
                {
                    throw new InvalidOperationException(
                        $"HIR callable symbol {symbol.Id} has no exact callable-only MIR lowering.");
                }
                continue;
            }

            var owner = FindOwningCallableSymbol(graph, symbol)
                ?? throw new InvalidOperationException(
                    $"HIR symbol {symbol.Id} has no enclosing callable.");
            var ownerCallables = _callables.ValuesFor(owner);
            if (ownerCallables.Count == 0)
            {
                throw new InvalidOperationException(
                    $"HIR symbol {symbol.Id} has no lowered callable owner.");
            }

            if (isUnreachable)
            {
                if (hasCallable || hasValue || hasStorage || hasQubit)
                {
                    throw new InvalidOperationException(
                        $"Unreachable HIR symbol {symbol.Id} was also linked to MIR.");
                }
                continue;
            }

            foreach (var callable in ownerCallables)
            {
                var links = byCallable[callable];
                var primaryPresent = symbol switch
                {
                    { Type: QType.Qubit } =>
                        links.Qubits.ContainsLeft(symbol.Id),
                    { IsArray: true } =>
                        links.Storages.ContainsLeft(symbol.Id),
                    _ =>
                        links.Values.ContainsLeft(symbol.Id),
                };
                if (!primaryPresent)
                {
                    var expected = symbol.Type == QType.Qubit
                        ? "qubit"
                        : symbol.IsArray
                            ? "storage"
                            : "value";
                    throw new InvalidOperationException(
                        $"HIR symbol {symbol.Id} has no primary MIR {expected} in callable {callable}.");
                }
            }
        }
    }

    private static void ValidateOwners(
        Compilation compilation,
        HirSemanticArtifact hirArtifact,
        MirSnapshot mir)
    {
        if (hirArtifact.Phase != HirSemanticPhase.EffectAnalysis
            || !hirArtifact.IsAccepted)
        {
            throw new InvalidOperationException(
                "A MIR semantic index requires an accepted final HIR effect-analysis artifact.");
        }
        if (hirArtifact.SourceId.CompilationId != compilation.Id
            || hirArtifact.SourceId.CompilationRevision != compilation.Revision
            || mir.Id.CompilationId != compilation.Id
            || mir.Id.CompilationRevision != compilation.Revision
            || mir.LoweredFrom != hirArtifact.SourceId)
        {
            throw new InvalidOperationException(
                "The completed HIR and MIR do not belong to the same Compilation revision.");
        }
    }

    private static SymbolId? FindOwningCallableSymbol(
        HirScopeGraph graph,
        Symbol symbol)
    {
        if (symbol.Kind == SymbolKind.Callable)
            return symbol.Id;

        for (var scope = graph.FindScope(symbol.DeclaringScopeId);
             scope is not null;
             scope = scope.ParentScopeId is { } parent
                 ? graph.FindScope(parent)
                 : null)
        {
            if (scope.Kind == HirScopeKind.Callable
                && scope.DeclaringSymbolId is { } callable)
            {
                return callable;
            }
        }

        return null;
    }

    private MutableCallableIndex Mutable(MirCallableId callable)
    {
        if (!_byCallable.TryGetValue(callable, out var links))
            _byCallable.Add(callable, links = new MutableCallableIndex());
        return links;
    }

    private bool HasTraceData() =>
        _callablesByDeclaration.Count != 0
        || _callables.Count != 0
        || _byCallable.Count != 0
        || _unreachable.Count != 0;

    private void RequireOpen()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "A MIR semantic index collector cannot be reused after publication.");
        }
    }

    private sealed class MutableCallableIndex
    {
        public HashSet<SymbolId> Symbols { get; } = new();
        public BiMultiMap<SymbolId, MirValueId> Values { get; } = new();
        public BiMultiMap<SymbolId, MirStorageId> Storages { get; } = new();
        public BiMultiMap<SymbolId, MirQubitKey> Qubits { get; } = new();
        public HashSet<SymbolId> Unreachable { get; } = new();
    }

    private sealed class BiMultiMap<TLeft, TRight>
        where TLeft : notnull
        where TRight : notnull
    {
        private readonly Dictionary<TLeft, HashSet<TRight>> _forward = new();
        private readonly Dictionary<TRight, HashSet<TLeft>> _reverse = new();

        public int Count => _forward.Count;
        public IReadOnlyDictionary<TLeft, HashSet<TRight>> Forward => _forward;

        public void Add(TLeft left, TRight right)
        {
            AddTo(_forward, left, right);
            AddTo(_reverse, right, left);
        }

        public bool Contains(TLeft left, TRight right) =>
            _forward.TryGetValue(left, out var rights) && rights.Contains(right);

        public bool ContainsLeft(TLeft left) =>
            _forward.TryGetValue(left, out var rights) && rights.Count != 0;

        public IReadOnlyList<TRight> ValuesFor(TLeft left) =>
            _forward.TryGetValue(left, out var rights)
                ? rights.ToArray()
                : Array.Empty<TRight>();

        public IReadOnlyDictionary<TLeft, IReadOnlyList<TRight>> FreezeForward(
            IComparer<TRight> comparer) =>
            _forward.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TRight>)Array.AsReadOnly(
                    pair.Value.OrderBy(value => value, comparer).ToArray()));

        public IReadOnlyDictionary<TRight, IReadOnlyList<TLeft>> FreezeReverse(
            IComparer<TLeft> comparer) =>
            _reverse.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TLeft>)Array.AsReadOnly(
                    pair.Value.OrderBy(value => value, comparer).ToArray()));

        private static void AddTo<TKey, TValue>(
            Dictionary<TKey, HashSet<TValue>> map,
            TKey key,
            TValue value)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var values))
                map.Add(key, values = new HashSet<TValue>());
            values.Add(value);
        }
    }
}
