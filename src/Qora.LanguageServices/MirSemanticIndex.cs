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
    private readonly FrozenDictionary<SymbolId, Symbol> _symbols;
    private readonly FrozenDictionary<MirCallableId, MirCallable> _callables;
    private readonly FrozenDictionary<HirNodeId, HirCallable> _hirCallables;
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

        CompilationId = compilation.Id;
        CompilationRevision = compilation.Revision;
        if (hirArtifact.Id.Source.CompilationId != CompilationId
            || hirArtifact.Id.Source.CompilationRevision != CompilationRevision
            || mir.Id.CompilationId != CompilationId
            || mir.Id.CompilationRevision != CompilationRevision
            || mir.LoweredFrom != hirArtifact.SourceId)
        {
            throw new ArgumentException(
                "The semantic index inputs do not belong to one compilation revision.");
        }

        _callablesByHirDeclaration =
            callablesByHirDeclaration.ToFrozenDictionary();
        _callablesBySymbol = FreezeNested(callablesBySymbol);
        _byCallable = byCallable.ToFrozenDictionary();
        _symbols = hirArtifact.Model.ScopeGraph?.Symbols.ToFrozenDictionary()
            ?? throw new ArgumentException(
                "The semantic index requires a sealed HIR scope graph.",
                nameof(hirArtifact));
        _callables = mir.Program.Callables.ToFrozenDictionary(callable => callable.Id);
        _hirCallables =
            hirArtifact.Program.Callables.ToFrozenDictionary(callable => callable.Id);
        _symbolsByCallable = Reverse(_callablesBySymbol);
        _callableIndexes = Array.AsReadOnly(
            mir.Program.Callables.Select(callable => _byCallable[callable.Id]).ToArray());

        if (!_callables.Keys.ToHashSet().SetEquals(_byCallable.Keys))
        {
            throw new ArgumentException(
                "The callable indexes do not exactly cover the completed MIR program.",
                nameof(byCallable));
        }
    }

    public CompilationId CompilationId { get; }
    public CompilationRevision CompilationRevision { get; }
    public HirSemanticArtifact HirArtifact { get; }
    public HirSemanticArtifactId HirArtifactId => HirArtifact.Id;
    public MirSnapshot Mir { get; }
    public MirSnapshotId MirSnapshotId => Mir.Id;

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
        return _callables[callable];
    }

    public IReadOnlyList<MirCallable> CallablesFor(Symbol symbol)
    {
        RequireSymbol(symbol);
        return Array.AsReadOnly(
            (_callablesBySymbol.GetValueOrDefault(symbol.Id)
                ?? Array.Empty<MirCallableId>())
            .Select(callable => _callables[callable])
            .ToArray());
    }

    public IReadOnlyList<Symbol> SymbolsFor(MirCallable callable)
    {
        RequireCallable(callable);
        return Array.AsReadOnly(
            (_symbolsByCallable.GetValueOrDefault(callable.Id)
                ?? Array.Empty<SymbolId>())
            .Select(symbol => _symbols[symbol])
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
        if (!_symbols.TryGetValue(symbol.Id, out var owned)
            || !ReferenceEquals(owned, symbol))
        {
            throw new ArgumentException(
                "The symbol does not belong to this HIR semantic artifact.",
                nameof(symbol));
        }
    }

    private void RequireCallable(MirCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        if (!_callables.TryGetValue(callable.Id, out var owned)
            || !ReferenceEquals(owned, callable))
        {
            throw new ArgumentException(
                "The callable does not belong to this MIR snapshot.",
                nameof(callable));
        }
    }

    private void RequireHirCallable(HirCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        if (!_hirCallables.TryGetValue(callable.Id, out var owned)
            || !ReferenceEquals(owned, callable))
        {
            throw new ArgumentException(
                "The callable does not belong to this HIR artifact.",
                nameof(callable));
        }
    }

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
                    reverse.Add(callable, symbols = new HashSet<SymbolId>());
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
    private readonly FrozenDictionary<SymbolId, Symbol> _symbols;
    private readonly FrozenDictionary<MirValueId, MirValue> _values;
    private readonly FrozenDictionary<MirStorageId, MirArrayStorage> _storages;
    private readonly FrozenDictionary<MirQubitKey, MirQubit> _qubits;
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

        var symbolIds = symbols.ToHashSet();
        _symbols = symbolIds.ToFrozenDictionary(
            symbol => symbol,
            symbol => graph.FindSymbol(symbol)
                ?? throw new ArgumentException(
                    $"HIR symbol {symbol} does not belong to the semantic scope graph.",
                    nameof(symbols)));
        _values = callable.Values.ToFrozenDictionary(value => value.Id);
        _storages = callable.Storages.ToFrozenDictionary(storage => storage.Id);
        _qubits = callable.Qubits.ToFrozenDictionary(qubit => qubit.Key);

        _valuesBySymbol = FreezeNested(valuesBySymbol);
        _symbolsByValue = FreezeNested(symbolsByValue);
        _storagesBySymbol = FreezeNested(storagesBySymbol);
        _symbolsByStorage = FreezeNested(symbolsByStorage);
        _qubitsBySymbol = FreezeNested(qubitsBySymbol);
        _symbolsByQubit = FreezeNested(symbolsByQubit);
        _unreachableSymbols = unreachableSymbols.ToFrozenSet();
        _symbolList = Array.AsReadOnly(
            _symbols.Values.OrderBy(symbol => symbol.Id.Value).ToArray());
        _unreachableSymbolList = Array.AsReadOnly(
            _unreachableSymbols
                .OrderBy(symbol => symbol.Value)
                .Select(symbol => _symbols[symbol])
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
            .Select(value => _values[value])
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
            .Select(storage => _storages[storage])
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
            .Select(qubit => _qubits[qubit])
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
        Array.AsReadOnly(symbols.Select(symbol => _symbols[symbol]).ToArray());

    private void RequireSymbol(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!_symbols.TryGetValue(symbol.Id, out var owned)
            || !ReferenceEquals(owned, symbol))
        {
            throw new ArgumentException(
                $"The symbol does not belong to MIR callable {Callable.Id}.",
                nameof(symbol));
        }
    }

    private void RequireValue(MirValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_values.TryGetValue(value.Id, out var owned)
            || !ReferenceEquals(owned, value))
        {
            throw new ArgumentException(
                $"The value does not belong to MIR callable {Callable.Id}.",
                nameof(value));
        }
    }

    private void RequireStorage(MirArrayStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!_storages.TryGetValue(storage.Id, out var owned)
            || !ReferenceEquals(owned, storage))
        {
            throw new ArgumentException(
                $"The storage does not belong to MIR callable {Callable.Id}.",
                nameof(storage));
        }
    }

    private void RequireQubit(MirQubit qubit)
    {
        ArgumentNullException.ThrowIfNull(qubit);
        if (!_qubits.TryGetValue(qubit.Key, out var owned)
            || !ReferenceEquals(owned, qubit))
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
