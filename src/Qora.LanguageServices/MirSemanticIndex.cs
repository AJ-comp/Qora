using System.Collections.Frozen;
using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.LanguageServices;

/// <summary>
/// IDE-facing HIR-to-MIR queries for one exact compiler snapshot.
///
/// Internal maps use dense IDs, but public queries accept the exact immutable objects owned by
/// <see cref="HirArtifact"/> and <see cref="Mir"/>. A same-valued object from another compilation
/// revision is therefore rejected instead of silently resolving to this revision's entity.
/// </summary>
public sealed class MirSemanticIndex
{
    private readonly FrozenDictionary<HirNodeId, MirCallableId> _callablesByHirDeclaration;
    private readonly FrozenDictionary<SymbolId, IReadOnlyList<MirCallableId>> _callablesBySymbol;
    private readonly FrozenDictionary<MirCallableId, IReadOnlyList<SymbolId>> _symbolsByCallable;
    private readonly FrozenDictionary<MirCallableId, MirCallableSemanticIndex> _byCallable;
    private readonly IReadOnlyList<MirCallableSemanticIndex> _callableIndexes;

    internal MirSemanticIndex(
        Compilation compilation,
        HirSemanticArtifact hirArtifact,
        MirSnapshot mir,
        IReadOnlyDictionary<HirNodeId, MirCallableId> callablesByHirDeclaration,
        IReadOnlyDictionary<SymbolId, IReadOnlyList<MirCallableId>> callablesBySymbol,
        IReadOnlyDictionary<MirCallableId, MirCallableSemanticIndex> byCallable)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        HirArtifact = hirArtifact ?? throw new ArgumentNullException(nameof(hirArtifact));
        Mir = mir ?? throw new ArgumentNullException(nameof(mir));
        ArgumentNullException.ThrowIfNull(callablesByHirDeclaration);
        ArgumentNullException.ThrowIfNull(callablesBySymbol);
        ArgumentNullException.ThrowIfNull(byCallable);

        if (!ReferenceEquals(
                compilation.Hir.EffectAnalysis,
                hirArtifact)
            || !ReferenceEquals(
                compilation.Mir,
                mir))
        {
            throw new ArgumentException(
                "The semantic index inputs are not the exact final HIR and MIR "
                + "owned by the Compilation.");
        }

        _callablesByHirDeclaration =
            callablesByHirDeclaration.ToFrozenDictionary();
        _callablesBySymbol = FreezeNested(callablesBySymbol);
        _byCallable = byCallable.ToFrozenDictionary();
        _ = hirArtifact.Model.ScopeGraph
            ?? throw new ArgumentException(
                "The semantic index requires a sealed HIR scope graph.",
                nameof(hirArtifact));
        _symbolsByCallable = Reverse(_callablesBySymbol);

        if (_byCallable.Count != mir.Program.Callables.Count)
        {
            throw new ArgumentException(
                "The callable indexes do not exactly cover the completed MIR program.",
                nameof(byCallable));
        }

        var callableIndexes =
            new List<MirCallableSemanticIndex>(mir.Program.Callables.Count);
        foreach (var callable in mir.Program.Callables)
        {
            if (!_byCallable.TryGetValue(callable.Id, out var callableIndex)
                || callableIndex is null
                || !ReferenceEquals(callableIndex.Callable, callable))
            {
                throw new ArgumentException(
                    $"The semantic index for MIR callable {callable.Id} does not own "
                    + "the exact callable object from the completed MIR program.",
                    nameof(byCallable));
            }

            callableIndexes.Add(callableIndex);
        }

        _callableIndexes = Array.AsReadOnly(callableIndexes.ToArray());
    }

    public HirSemanticArtifact HirArtifact { get; }
    public HirSemanticArtifactId HirArtifactId => HirArtifact.Id;
    public MirSnapshot Mir { get; }

    /// <summary>Every completed MIR callable with its source-query scope.</summary>
    public IReadOnlyList<MirCallableSemanticIndex> Callables => _callableIndexes;

    public MirCallableSemanticIndex Callable(MirCallable callable)
    {
        RequireCallable(callable);
        return _byCallable[callable.Id];
    }

    /// <summary>
    /// Resolves one callable declaration in the exact final HIR artifact.
    /// Final HIR specialization gives every specialization its own declaration object.
    /// </summary>
    public MirCallable CallableFor(HirCallable declaration)
    {
        RequireHirCallable(declaration);
        if (!_callablesByHirDeclaration.TryGetValue(declaration.Id, out var callable))
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaration),
                declaration.Id,
                "The HIR callable was not lowered into this MIR snapshot.");
        }
        return Mir.Program.RequireCallable(callable);
    }

    public IReadOnlyList<MirCallable> CallablesFor(Symbol symbol)
    {
        RequireSymbol(symbol);
        return Array.AsReadOnly(
            (_callablesBySymbol.GetValueOrDefault(symbol.Id)
                ?? Array.Empty<MirCallableId>())
            .Select(callableId => Mir.Program.RequireCallable(callableId))
            .ToArray());
    }

    public IReadOnlyList<Symbol> SymbolsFor(MirCallable callable)
    {
        RequireCallable(callable);
        return Array.AsReadOnly(
            (_symbolsByCallable.GetValueOrDefault(callable.Id)
                ?? Array.Empty<SymbolId>())
            .Select(symbolId => RequireSymbol(symbolId))
            .ToArray());
    }

    /// <summary>
    /// A callable is compiler-generated when the completed MIR contains it but no source symbol links to it.
    /// </summary>
    public bool IsCompilerGenerated(MirCallable callable) =>
        SymbolsFor(callable).Count == 0;

    private void RequireSymbol(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!ReferenceEquals(Graph.FindSymbol(symbol.Id), symbol))
        {
            throw new ArgumentException(
                "The symbol does not belong to this HIR semantic artifact.",
                nameof(symbol));
        }
    }

    private void RequireCallable(MirCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        if (!Mir.Program.ContainsCallable(callable))
        {
            throw new ArgumentException(
                "The callable does not belong to this MIR snapshot.",
                nameof(callable));
        }
    }

    private void RequireHirCallable(HirCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        if (!ReferenceEquals(
                HirArtifact.Source.Structure.FindNode(callable.Id),
                callable))
        {
            throw new ArgumentException(
                "The callable does not belong to this HIR artifact.",
                nameof(callable));
        }
    }

    private HirScopeGraph Graph =>
        HirArtifact.Model.ScopeGraph
        ?? throw new InvalidOperationException(
            "The semantic index has no sealed HIR scope graph.");

    private Symbol RequireSymbol(SymbolId symbol) =>
        Graph.FindSymbol(symbol)
        ?? throw new InvalidOperationException(
            $"HIR symbol {symbol} no longer belongs to the semantic artifact.");

    private static FrozenDictionary<TKey, IReadOnlyList<TValue>> FreezeNested<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> source)
        where TKey : notnull =>
        source.ToFrozenDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TValue>)Array.AsReadOnly(pair.Value.ToArray()));

    private static FrozenDictionary<MirCallableId, IReadOnlyList<SymbolId>> Reverse(
        IReadOnlyDictionary<SymbolId, IReadOnlyList<MirCallableId>> forward)
    {
        var reverse = new Dictionary<MirCallableId, HashSet<SymbolId>>();
        foreach (var (symbol, callables) in forward)
        {
            foreach (var callable in callables)
            {
                if (!reverse.TryGetValue(callable, out var symbols))
                {
                    symbols = new HashSet<SymbolId>();
                    reverse.Add(callable, symbols);
                }

                symbols.Add(symbol);
            }
        }

        return reverse.ToFrozenDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SymbolId>)Array.AsReadOnly(
                pair.Value.OrderBy(symbol => symbol.Value).ToArray()));
    }
}

/// <summary>
/// Source-symbol queries for the local entity spaces of one exact MIR callable.
/// </summary>
public sealed class MirCallableSemanticIndex
{
    private readonly HirScopeGraph _graph;
    private readonly FrozenSet<SymbolId> _symbolIds;
    private readonly FrozenDictionary<SymbolId, IReadOnlyList<MirValueId>> _valuesBySymbol;
    private readonly FrozenDictionary<MirValueId, IReadOnlyList<SymbolId>> _symbolsByValue;
    private readonly FrozenDictionary<SymbolId, IReadOnlyList<MirStorageId>> _storagesBySymbol;
    private readonly FrozenDictionary<MirStorageId, IReadOnlyList<SymbolId>> _symbolsByStorage;
    private readonly FrozenDictionary<SymbolId, IReadOnlyList<MirQubitKey>> _qubitsBySymbol;
    private readonly FrozenDictionary<MirQubitKey, IReadOnlyList<SymbolId>> _symbolsByQubit;
    private readonly FrozenSet<SymbolId> _unreachableSymbols;
    private readonly IReadOnlyList<Symbol> _symbolList;
    private readonly IReadOnlyList<Symbol> _unreachableSymbolList;

    internal MirCallableSemanticIndex(
        HirScopeGraph graph,
        MirCallable callable,
        IEnumerable<SymbolId> symbols,
        IReadOnlyDictionary<SymbolId, IReadOnlyList<MirValueId>> valuesBySymbol,
        IReadOnlyDictionary<MirValueId, IReadOnlyList<SymbolId>> symbolsByValue,
        IReadOnlyDictionary<SymbolId, IReadOnlyList<MirStorageId>> storagesBySymbol,
        IReadOnlyDictionary<MirStorageId, IReadOnlyList<SymbolId>> symbolsByStorage,
        IReadOnlyDictionary<SymbolId, IReadOnlyList<MirQubitKey>> qubitsBySymbol,
        IReadOnlyDictionary<MirQubitKey, IReadOnlyList<SymbolId>> symbolsByQubit,
        IEnumerable<SymbolId> unreachableSymbols)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Callable = callable ?? throw new ArgumentNullException(nameof(callable));
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(unreachableSymbols);

        _graph = graph;
        _symbolIds = symbols.ToFrozenSet();

        _valuesBySymbol = FreezeNested(valuesBySymbol);
        _symbolsByValue = FreezeNested(symbolsByValue);
        _storagesBySymbol = FreezeNested(storagesBySymbol);
        _symbolsByStorage = FreezeNested(symbolsByStorage);
        _qubitsBySymbol = FreezeNested(qubitsBySymbol);
        _symbolsByQubit = FreezeNested(symbolsByQubit);
        _unreachableSymbols = unreachableSymbols.ToFrozenSet();
        _symbolList = Array.AsReadOnly(
            _symbolIds
                .OrderBy(symbol => symbol.Value)
                .Select(ResolveSymbol)
                .ToArray());
        _unreachableSymbolList = Array.AsReadOnly(
            _unreachableSymbols
                .OrderBy(symbol => symbol.Value)
                .Select(ResolveSymbol)
                .ToArray());
    }

    public MirCallable Callable { get; }

    /// <summary>Every exact HIR symbol whose declaring scope belongs to this callable.</summary>
    public IReadOnlyList<Symbol> Symbols => _symbolList;

    public IReadOnlyList<Symbol> UnreachableSymbols => _unreachableSymbolList;

    public IReadOnlyList<MirValue> ValuesFor(Symbol symbol)
    {
        RequireSymbol(symbol);
        return Array.AsReadOnly(
            (_valuesBySymbol.GetValueOrDefault(symbol.Id)
                ?? Array.Empty<MirValueId>())
            .Select(valueId => Callable.RequireValue(valueId))
            .ToArray());
    }

    public IReadOnlyList<Symbol> SymbolsFor(MirValue value)
    {
        RequireValue(value);
        return ResolveSymbols(
            _symbolsByValue.GetValueOrDefault(value.Id)
            ?? Array.Empty<SymbolId>());
    }

    public IReadOnlyList<MirArrayStorage> StoragesFor(Symbol symbol)
    {
        RequireSymbol(symbol);
        return Array.AsReadOnly(
            (_storagesBySymbol.GetValueOrDefault(symbol.Id)
                ?? Array.Empty<MirStorageId>())
            .Select(storageId => Callable.RequireStorage(storageId))
            .ToArray());
    }

    public IReadOnlyList<Symbol> SymbolsFor(MirArrayStorage storage)
    {
        RequireStorage(storage);
        return ResolveSymbols(
            _symbolsByStorage.GetValueOrDefault(storage.Id)
            ?? Array.Empty<SymbolId>());
    }

    public IReadOnlyList<MirQubit> QubitsFor(Symbol symbol)
    {
        RequireSymbol(symbol);
        return Array.AsReadOnly(
            (_qubitsBySymbol.GetValueOrDefault(symbol.Id)
                ?? Array.Empty<MirQubitKey>())
            .Select(qubitKey => Callable.RequireQubit(qubitKey))
            .ToArray());
    }

    public IReadOnlyList<Symbol> SymbolsFor(MirQubit qubit)
    {
        RequireQubit(qubit);
        return ResolveSymbols(
            _symbolsByQubit.GetValueOrDefault(qubit.Key)
            ?? Array.Empty<SymbolId>());
    }

    public bool IsUnreachable(Symbol symbol)
    {
        RequireSymbol(symbol);
        return _unreachableSymbols.Contains(symbol.Id);
    }

    public bool IsCompilerGenerated(MirValue value) =>
        SymbolsFor(value).Count == 0;

    public bool IsCompilerGenerated(MirArrayStorage storage) =>
        SymbolsFor(storage).Count == 0;

    public bool IsCompilerGenerated(MirQubit qubit) =>
        SymbolsFor(qubit).Count == 0;

    private IReadOnlyList<Symbol> ResolveSymbols(IEnumerable<SymbolId> symbols) =>
        Array.AsReadOnly(symbols.Select(ResolveSymbol).ToArray());

    private Symbol ResolveSymbol(SymbolId symbol)
    {
        if (!_symbolIds.Contains(symbol))
        {
            throw new InvalidOperationException(
                $"HIR symbol {symbol} does not belong to MIR callable {Callable.Id}.");
        }

        return _graph.FindSymbol(symbol)
            ?? throw new InvalidOperationException(
                $"HIR symbol {symbol} no longer belongs to the semantic scope graph.");
    }

    private void RequireSymbol(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!_symbolIds.Contains(symbol.Id)
            || !ReferenceEquals(_graph.FindSymbol(symbol.Id), symbol))
        {
            throw new ArgumentException(
                $"The symbol does not belong to MIR callable {Callable.Id}.",
                nameof(symbol));
        }
    }

    private void RequireValue(MirValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Callable.ContainsValue(value))
        {
            throw new ArgumentException(
                $"The value does not belong to MIR callable {Callable.Id}.",
                nameof(value));
        }
    }

    private void RequireStorage(MirArrayStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!Callable.ContainsStorage(storage))
        {
            throw new ArgumentException(
                $"The storage does not belong to MIR callable {Callable.Id}.",
                nameof(storage));
        }
    }

    private void RequireQubit(MirQubit qubit)
    {
        ArgumentNullException.ThrowIfNull(qubit);
        if (!Callable.ContainsQubit(qubit))
        {
            throw new ArgumentException(
                $"The qubit does not belong to MIR callable {Callable.Id}.",
                nameof(qubit));
        }
    }

    private static FrozenDictionary<TKey, IReadOnlyList<TValue>> FreezeNested<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> source)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ToFrozenDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TValue>)Array.AsReadOnly(pair.Value.ToArray()));
    }
}
