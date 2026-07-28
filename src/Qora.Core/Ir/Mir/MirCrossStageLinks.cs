using System.Collections.Frozen;
using System.Collections.Immutable;
using Qora.Compiler;
using Qora.Ir.Passes;

namespace Qora.Ir.Mir;

/// <summary>
/// A semantic symbol is meaningful only inside the exact HIR semantic snapshot which allocated it.
/// </summary>
public readonly record struct HirSymbolRef(
    HirSemanticArtifactId SemanticArtifact,
    SymbolId Symbol);

/// <summary>
/// The primary MIR representation required for one HIR semantic symbol. Additional value links may exist
/// for array loads, but every symbol has exactly one primary disposition or an explicit non-lowered reason.
/// </summary>
public enum MirSymbolLoweringDisposition
{
    Callable,
    ScalarValue,
    ArrayStorage,
    Qubit,
    NotLoweredNamespace,
    NotLoweredBuiltin,
    NotLoweredUnreachable,
}

/// <summary>
/// Explains whether one concrete MIR entity is backed by at least one HIR symbol or was introduced only
/// to represent an intermediate SSA/CFG computation. The map is total over each MIR entity space, so
/// removing both sides of a symbol link cannot silently turn a source value into an anonymous temporary.
/// </summary>
public enum MirEntityOriginKind
{
    SourceSymbol,
    CompilerTemporary,
}

/// <summary>
/// The authoritative origin of one MIR callable. Source callables retain their exact HIR declaration
/// and symbol, while callables synthesized by a MIR pass point to the MIR callable from which they were
/// derived. A synthesized callable is deliberately not inserted into the HIR symbol maps.
/// </summary>
public abstract record MirCallableProvenance;

public sealed record MirLoweredCallableProvenance(
    HirNodeRef Callable,
    HirSymbolRef Symbol)
    : MirCallableProvenance;

public enum MirCallableSynthesisKind
{
    Inverse,
}

public sealed record MirSynthesizedCallableProvenance(
    MirCallableRef DerivedFrom,
    MirCallableSynthesisKind Kind)
    : MirCallableProvenance;

/// <summary>The exact HIR callable/node pair reached by resolving one MIR origin.</summary>
public readonly record struct MirResolvedHirOrigin(
    HirNodeRef Callable,
    HirNodeRef Node,
    SourceSpan? Span);

/// <summary>
/// Immutable direct relationships created at the HIR-to-MIR lowering boundary. Symbol/value links are
/// many-to-many: several source bindings may denote the same SSA value, while one binding naturally
/// acquires several SSA versions over time.
/// </summary>
public sealed class MirCrossStageLinks
{
    internal MirCrossStageLinks(
        MirSnapshotId mirSnapshot,
        HirSnapshot loweredFrom,
        HirSemanticArtifact symbolsFrom,
        MirOriginTable origins,
        IReadOnlyDictionary<HirNodeRef, MirCallableRef> callablesByHirCallable,
        IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirCallableRef>> callablesBySymbol,
        IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirValueRef>> valuesBySymbol,
        IReadOnlyDictionary<MirValueRef, IReadOnlyList<HirSymbolRef>> symbolsByValue,
        IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirStorageRef>> storagesBySymbol,
        IReadOnlyDictionary<MirStorageRef, IReadOnlyList<HirSymbolRef>> symbolsByStorage,
        IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirQubitRef>> qubitsBySymbol,
        IReadOnlyDictionary<MirQubitRef, IReadOnlyList<HirSymbolRef>> symbolsByQubit,
        IReadOnlyDictionary<HirSymbolRef, MirSymbolLoweringDisposition> symbolDispositions,
        IReadOnlyDictionary<MirCallableRef, MirCallableProvenance> callableProvenance,
        IReadOnlyDictionary<MirValueRef, MirEntityOriginKind> valueOrigins,
        IReadOnlyDictionary<MirStorageRef, MirEntityOriginKind> storageOrigins,
        IReadOnlyDictionary<MirQubitRef, MirEntityOriginKind> qubitOrigins)
    {
        ArgumentNullException.ThrowIfNull(loweredFrom);
        ArgumentNullException.ThrowIfNull(symbolsFrom);
        if (mirSnapshot.CompilationId != loweredFrom.Id.CompilationId
            || mirSnapshot.CompilationRevision != loweredFrom.Id.CompilationRevision
            || loweredFrom.Id.CompilationId != symbolsFrom.SourceId.CompilationId
            || loweredFrom.Id.CompilationRevision != symbolsFrom.SourceId.CompilationRevision)
        {
            throw new ArgumentException(
                "MIR, HIR, and semantic links cannot cross compilation snapshots",
                nameof(loweredFrom));
        }

        MirSnapshot = mirSnapshot;
        LoweredFromSnapshot = loweredFrom;
        SymbolsFromArtifact = symbolsFrom;
        Origins = origins ?? throw new ArgumentNullException(nameof(origins));
        CallablesByHirCallable = Freeze(callablesByHirCallable);
        if (CallablesByHirCallable.Values.Distinct().Count()
            != CallablesByHirCallable.Count)
        {
            throw new ArgumentException(
                "Each MIR callable must be linked from exactly one HIR callable.",
                nameof(callablesByHirCallable));
        }
        CallablesBySymbol = FreezeNested(callablesBySymbol);
        ValuesBySymbol = FreezeNested(valuesBySymbol);
        SymbolsByValue = FreezeNested(symbolsByValue);
        StoragesBySymbol = FreezeNested(storagesBySymbol);
        SymbolsByStorage = FreezeNested(symbolsByStorage);
        QubitsBySymbol = FreezeNested(qubitsBySymbol);
        SymbolsByQubit = FreezeNested(symbolsByQubit);
        SymbolDispositions = Freeze(symbolDispositions);
        CallableProvenance = Freeze(callableProvenance);
        ValueOrigins = Freeze(valueOrigins);
        StorageOrigins = Freeze(storageOrigins);
        QubitOrigins = Freeze(qubitOrigins);

        if (origins.SnapshotId != mirSnapshot)
            throw new ArgumentException(
                "the origin table belongs to a different MIR snapshot",
                nameof(origins));
        ValidateHirEndpoints();
        ValidateMirEndpoints();
        ValidateBidirectionalMaps();
    }

    public MirSnapshotId MirSnapshot { get; }
    internal HirSnapshot LoweredFromSnapshot { get; }
    internal HirSemanticArtifact SymbolsFromArtifact { get; }
    public HirSnapshotId LoweredFrom => LoweredFromSnapshot.Id;
    public HirSemanticArtifactId SymbolsFrom => SymbolsFromArtifact.Id;
    public MirOriginTable Origins { get; }
    public IReadOnlyDictionary<HirNodeRef, MirCallableRef> CallablesByHirCallable { get; }
    public IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirCallableRef>> CallablesBySymbol { get; }
    public IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirValueRef>> ValuesBySymbol { get; }
    public IReadOnlyDictionary<MirValueRef, IReadOnlyList<HirSymbolRef>> SymbolsByValue { get; }
    public IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirStorageRef>> StoragesBySymbol { get; }
    public IReadOnlyDictionary<MirStorageRef, IReadOnlyList<HirSymbolRef>> SymbolsByStorage { get; }
    public IReadOnlyDictionary<HirSymbolRef, IReadOnlyList<MirQubitRef>> QubitsBySymbol { get; }
    public IReadOnlyDictionary<MirQubitRef, IReadOnlyList<HirSymbolRef>> SymbolsByQubit { get; }
    public IReadOnlyDictionary<HirSymbolRef, MirSymbolLoweringDisposition> SymbolDispositions { get; }
    public IReadOnlyDictionary<MirCallableRef, MirCallableProvenance> CallableProvenance { get; }
    public IReadOnlyDictionary<MirValueRef, MirEntityOriginKind> ValueOrigins { get; }
    public IReadOnlyDictionary<MirStorageRef, MirEntityOriginKind> StorageOrigins { get; }
    public IReadOnlyDictionary<MirQubitRef, MirEntityOriginKind> QubitOrigins { get; }

    public MirResolvedHirOrigin ResolveOrigin(MirOriginRef origin)
    {
        MirReferenceValidation.RequireSnapshot(
            MirSnapshot,
            origin.Snapshot,
            nameof(origin));
        var hir = Origins.ResolveHir(origin);
        return new MirResolvedHirOrigin(
            new HirNodeRef(LoweredFrom, hir.HirCallableId!.Value),
            new HirNodeRef(LoweredFrom, hir.HirNodeId!.Value),
            hir.Span);
    }

    public IReadOnlyList<HirSymbolRef> SymbolsFor(MirValueRef value)
    {
        Require(value.Snapshot, nameof(value));
        return SymbolsByValue.GetValueOrDefault(value)
            ?? Array.Empty<HirSymbolRef>();
    }

    public IReadOnlyList<HirSymbolRef> SymbolsFor(MirStorageRef storage)
    {
        Require(storage.Snapshot, nameof(storage));
        return SymbolsByStorage.GetValueOrDefault(storage)
            ?? Array.Empty<HirSymbolRef>();
    }

    public IReadOnlyList<HirSymbolRef> SymbolsFor(MirQubitRef qubit)
    {
        Require(qubit.Snapshot, nameof(qubit));
        return SymbolsByQubit.GetValueOrDefault(qubit)
            ?? Array.Empty<HirSymbolRef>();
    }

    /// <summary>
    /// Rebinds an additive MIR transformation to a fresh snapshot identity. Existing source links keep
    /// their local entity IDs, while entities introduced by the pass are explicitly classified as
    /// compiler temporaries. Synthesized callables receive MIR-to-MIR provenance rather than pretending
    /// to be additional lowerings of one HIR declaration.
    /// </summary>
    internal MirCrossStageLinks CloneForAdditiveTransformation(
        MirProgram transformedProgram,
        MirOriginTable transformedOrigins,
        IReadOnlyDictionary<
            MirCallableId,
            (MirCallableId DerivedFrom, MirCallableSynthesisKind Kind)> synthesizedCallables)
    {
        ArgumentNullException.ThrowIfNull(transformedProgram);
        ArgumentNullException.ThrowIfNull(transformedOrigins);
        ArgumentNullException.ThrowIfNull(synthesizedCallables);
        var targetSnapshot = transformedProgram.SnapshotId;
        if (targetSnapshot != transformedOrigins.SnapshotId)
            throw new ArgumentException(
                "the transformed MIR program and origin table belong to different snapshots",
                nameof(transformedOrigins));
        if (targetSnapshot.CompilationId != MirSnapshot.CompilationId
            || targetSnapshot.CompilationRevision != MirSnapshot.CompilationRevision)
        {
            throw new ArgumentException(
                "an additive MIR transformation cannot cross compilation snapshots",
                nameof(transformedProgram));
        }
        if (MirSnapshot.Revision == int.MaxValue
            || targetSnapshot.Revision != MirSnapshot.Revision + 1)
        {
            throw new ArgumentException(
                "an additive MIR transformation must target the immediate next snapshot revision",
                nameof(transformedProgram));
        }

        MirCallableRef Callable(MirCallableRef reference) =>
            new(targetSnapshot, reference.Callable);
        MirValueRef Value(MirValueRef reference) =>
            new(targetSnapshot, reference.Callable, reference.Value);
        MirStorageRef Storage(MirStorageRef reference) =>
            new(targetSnapshot, reference.Callable, reference.Storage);
        MirQubitRef Qubit(MirQubitRef reference) =>
            new(
                targetSnapshot,
                reference.Callable,
                reference.Id,
                reference.Version);

        var callablesByHirCallable = CallablesByHirCallable.ToDictionary(
            pair => pair.Key,
            pair => Callable(pair.Value));
        var callablesBySymbol = CallablesBySymbol.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirCallableRef>)pair.Value.Select(Callable).ToArray());
        var valuesBySymbol = ValuesBySymbol.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirValueRef>)pair.Value.Select(Value).ToArray());
        var symbolsByValue = SymbolsByValue.ToDictionary(
            pair => Value(pair.Key),
            pair => pair.Value);
        var storagesBySymbol = StoragesBySymbol.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirStorageRef>)pair.Value.Select(Storage).ToArray());
        var symbolsByStorage = SymbolsByStorage.ToDictionary(
            pair => Storage(pair.Key),
            pair => pair.Value);
        var qubitsBySymbol = QubitsBySymbol.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirQubitRef>)pair.Value.Select(Qubit).ToArray());
        var symbolsByQubit = SymbolsByQubit.ToDictionary(
            pair => Qubit(pair.Key),
            pair => pair.Value);

        var callableProvenance = CallableProvenance.ToDictionary(
            pair => Callable(pair.Key),
            pair => (MirCallableProvenance)(pair.Value switch
            {
                MirLoweredCallableProvenance lowered => lowered,
                MirSynthesizedCallableProvenance synthesized =>
                    new MirSynthesizedCallableProvenance(
                        Callable(synthesized.DerivedFrom),
                        synthesized.Kind),
                _ => throw new InvalidOperationException(
                    $"unknown MIR callable provenance {pair.Value.GetType().Name}"),
            }));
        foreach (var (callable, synthesis) in synthesizedCallables)
        {
            var reference = new MirCallableRef(targetSnapshot, callable);
            if (!callableProvenance.TryAdd(
                    reference,
                    new MirSynthesizedCallableProvenance(
                        new MirCallableRef(targetSnapshot, synthesis.DerivedFrom),
                        synthesis.Kind)))
            {
                throw new ArgumentException(
                    $"MIR transformation registered callable {callable} more than once",
                    nameof(synthesizedCallables));
            }
        }

        var valueOrigins = ValueOrigins.ToDictionary(
            pair => Value(pair.Key),
            pair => pair.Value);
        var storageOrigins = StorageOrigins.ToDictionary(
            pair => Storage(pair.Key),
            pair => pair.Value);
        var qubitOrigins = QubitOrigins.ToDictionary(
            pair => Qubit(pair.Key),
            pair => pair.Value);
        foreach (var callable in transformedProgram.Callables)
        {
            foreach (var value in callable.Values)
                valueOrigins.TryAdd(
                    new MirValueRef(targetSnapshot, callable.Id, value.Id),
                    MirEntityOriginKind.CompilerTemporary);
            foreach (var storage in callable.Storages)
                storageOrigins.TryAdd(
                    new MirStorageRef(targetSnapshot, callable.Id, storage.Id),
                    MirEntityOriginKind.CompilerTemporary);
            foreach (var qubit in callable.Qubits)
                qubitOrigins.TryAdd(
                    new MirQubitRef(targetSnapshot, callable.Id, qubit.Key),
                    MirEntityOriginKind.CompilerTemporary);
        }

        return new MirCrossStageLinks(
            targetSnapshot,
            LoweredFromSnapshot,
            SymbolsFromArtifact,
            transformedOrigins,
            callablesByHirCallable,
            callablesBySymbol,
            valuesBySymbol,
            symbolsByValue,
            storagesBySymbol,
            symbolsByStorage,
            qubitsBySymbol,
            symbolsByQubit,
            SymbolDispositions,
            callableProvenance,
            valueOrigins,
            storageOrigins,
            qubitOrigins);
    }

    private void ValidateMirEndpoints()
    {
        foreach (var reference in CallablesByHirCallable.Values)
            Require(reference.Snapshot, nameof(CallablesByHirCallable));
        foreach (var references in CallablesBySymbol.Values)
            foreach (var reference in references)
                Require(reference.Snapshot, nameof(CallablesBySymbol));
        foreach (var references in ValuesBySymbol.Values)
            foreach (var reference in references)
                Require(reference.Snapshot, nameof(ValuesBySymbol));
        foreach (var reference in SymbolsByValue.Keys)
            Require(reference.Snapshot, nameof(SymbolsByValue));
        foreach (var references in StoragesBySymbol.Values)
            foreach (var reference in references)
                Require(reference.Snapshot, nameof(StoragesBySymbol));
        foreach (var reference in SymbolsByStorage.Keys)
            Require(reference.Snapshot, nameof(SymbolsByStorage));
        foreach (var references in QubitsBySymbol.Values)
            foreach (var reference in references)
                Require(reference.Snapshot, nameof(QubitsBySymbol));
        foreach (var reference in SymbolsByQubit.Keys)
            Require(reference.Snapshot, nameof(SymbolsByQubit));
        foreach (var (reference, provenance) in CallableProvenance)
        {
            Require(reference.Snapshot, nameof(CallableProvenance));
            switch (provenance)
            {
                case MirLoweredCallableProvenance lowered:
                    Require(lowered.Callable.Snapshot, nameof(CallableProvenance));
                    Require(lowered.Symbol, nameof(CallableProvenance));
                    break;
                case MirSynthesizedCallableProvenance synthesized:
                    Require(synthesized.DerivedFrom.Snapshot, nameof(CallableProvenance));
                    if (!Enum.IsDefined(synthesized.Kind))
                        throw new ArgumentException(
                            $"MIR callable {reference} has unknown synthesis kind {synthesized.Kind}.",
                            nameof(CallableProvenance));
                    break;
                default:
                    throw new ArgumentException(
                        $"MIR callable {reference} has unknown provenance type "
                        + $"{provenance?.GetType().Name ?? "<null>"}.",
                        nameof(CallableProvenance));
            }
        }
        foreach (var reference in ValueOrigins.Keys)
            Require(reference.Snapshot, nameof(ValueOrigins));
        foreach (var reference in StorageOrigins.Keys)
            Require(reference.Snapshot, nameof(StorageOrigins));
        foreach (var reference in QubitOrigins.Keys)
            Require(reference.Snapshot, nameof(QubitOrigins));
    }

    private void ValidateHirEndpoints()
    {
        foreach (var reference in CallablesByHirCallable.Keys)
            Require(reference.Snapshot, nameof(CallablesByHirCallable));
        foreach (var reference in CallablesBySymbol.Keys)
            Require(reference, nameof(CallablesBySymbol));
        foreach (var reference in ValuesBySymbol.Keys)
            Require(reference, nameof(ValuesBySymbol));
        foreach (var references in SymbolsByValue.Values)
            foreach (var reference in references)
                Require(reference, nameof(SymbolsByValue));
        foreach (var reference in StoragesBySymbol.Keys)
            Require(reference, nameof(StoragesBySymbol));
        foreach (var references in SymbolsByStorage.Values)
            foreach (var reference in references)
                Require(reference, nameof(SymbolsByStorage));
        foreach (var reference in QubitsBySymbol.Keys)
            Require(reference, nameof(QubitsBySymbol));
        foreach (var references in SymbolsByQubit.Values)
            foreach (var reference in references)
                Require(reference, nameof(SymbolsByQubit));
        foreach (var reference in SymbolDispositions.Keys)
        {
            Require(reference, nameof(SymbolDispositions));
            if (!Enum.IsDefined(SymbolDispositions[reference]))
            {
                throw new ArgumentException(
                    $"MIR symbol {reference} declares unknown lowering disposition " +
                    $"{SymbolDispositions[reference]}.",
                    nameof(SymbolDispositions));
            }
        }
    }

    private void ValidateBidirectionalMaps()
    {
        VerifySymmetry(ValuesBySymbol, SymbolsByValue, "HIR symbol ↔ MIR value");
        VerifySymmetry(StoragesBySymbol, SymbolsByStorage, "HIR symbol ↔ MIR storage");
        VerifySymmetry(QubitsBySymbol, SymbolsByQubit, "HIR symbol ↔ MIR qubit");
    }

    private void Require(MirSnapshotId actual, string parameter) =>
        MirReferenceValidation.RequireSnapshot(MirSnapshot, actual, parameter);

    private void Require(HirSnapshotId actual, string parameter)
    {
        if (actual != LoweredFrom)
            throw new ArgumentException(
                $"HIR reference belongs to snapshot {actual}; expected {LoweredFrom}",
                parameter);
    }

    private void Require(HirSymbolRef actual, string parameter)
    {
        if (actual.SemanticArtifact != SymbolsFrom)
            throw new ArgumentException(
                $"HIR symbol belongs to semantic artifact {actual.SemanticArtifact}; "
                + $"expected {SymbolsFrom}",
                parameter);
    }

    internal void VerifyAgainst(MirStructuralIndex structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        MirReferenceValidation.RequireSnapshot(
            MirSnapshot,
            structure.SnapshotId,
            nameof(structure));

        foreach (var reference in CallablesByHirCallable.Values)
            structure.RequireCallable(reference);
        foreach (var references in CallablesBySymbol.Values)
            foreach (var reference in references)
                structure.RequireCallable(reference);
        foreach (var (reference, provenance) in CallableProvenance)
        {
            structure.RequireCallable(reference);
            if (provenance is MirSynthesizedCallableProvenance synthesized)
                structure.RequireCallable(synthesized.DerivedFrom);
        }
        foreach (var references in ValuesBySymbol.Values)
            foreach (var reference in references)
                structure.RequireValue(reference);
        foreach (var reference in SymbolsByValue.Keys)
            structure.RequireValue(reference);
        foreach (var references in StoragesBySymbol.Values)
            foreach (var reference in references)
                structure.RequireStorage(reference);
        foreach (var reference in SymbolsByStorage.Keys)
            structure.RequireStorage(reference);
        foreach (var references in QubitsBySymbol.Values)
            foreach (var reference in references)
                structure.RequireQubit(reference);
        foreach (var reference in SymbolsByQubit.Keys)
            structure.RequireQubit(reference);
        foreach (var reference in ValueOrigins.Keys)
            structure.RequireValue(reference);
        foreach (var reference in StorageOrigins.Keys)
            structure.RequireStorage(reference);
        foreach (var reference in QubitOrigins.Keys)
            structure.RequireQubit(reference);

        var structuralCallables = structure.Callables.ToHashSet();
        if (!CallableProvenance.Keys.ToHashSet().SetEquals(structuralCallables))
            throw new ArgumentException(
                "MIR callable provenance must cover every MIR callable exactly once.",
                nameof(structure));

        foreach (var reference in structuralCallables)
        {
            var visited = new HashSet<MirCallableRef>();
            var current = reference;
            while (CallableProvenance[current]
                is MirSynthesizedCallableProvenance synthesized)
            {
                if (!visited.Add(current))
                {
                    throw new ArgumentException(
                        $"MIR callable provenance contains a synthesis cycle at {current}.",
                        nameof(structure));
                }
                current = synthesized.DerivedFrom;
            }
        }

        var loweredCallables = CallableProvenance
            .Where(pair => pair.Value is MirLoweredCallableProvenance)
            .ToDictionary(pair => pair.Key, pair => (MirLoweredCallableProvenance)pair.Value);
        if (loweredCallables.Count != CallablesByHirCallable.Count)
            throw new ArgumentException(
                "Every HIR callable link must have exactly one lowered-callable provenance entry.",
                nameof(structure));
        foreach (var (hirCallable, callable) in CallablesByHirCallable)
        {
            if (!loweredCallables.TryGetValue(callable, out var provenance)
                || provenance.Callable != hirCallable)
            {
                throw new ArgumentException(
                    $"HIR callable link {hirCallable} -> {callable} disagrees with callable provenance.",
                    nameof(structure));
            }
        }

        VerifyEntityCoverage(
            structure.Values,
            ValueOrigins,
            SymbolsByValue,
            "value");
        VerifyEntityCoverage(
            structure.Storages,
            StorageOrigins,
            SymbolsByStorage,
            "storage");
        VerifyEntityCoverage(
            structure.Qubits,
            QubitOrigins,
            SymbolsByQubit,
            "qubit");
    }

    internal void VerifyAgainst(HirCompilation hir, HirLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(hir);
        ArgumentNullException.ThrowIfNull(lineage);

        var loweredFrom = hir.Find(LoweredFrom);
        if (!ReferenceEquals(loweredFrom, LoweredFromSnapshot))
        {
            throw new ArgumentException(
                $"MIR lowering source {LoweredFrom} is detached from the HIR history.",
                nameof(hir));
        }
        var semanticBasis = hir.FindSemantics(
            SymbolsFrom.Source,
            SymbolsFrom.Phase);
        if (!ReferenceEquals(semanticBasis, SymbolsFromArtifact))
        {
            throw new ArgumentException(
                $"MIR semantic source {SymbolsFrom} is detached from the HIR history.",
                nameof(hir));
        }
        if (!lineage.IsAncestor(loweredFrom.Id, semanticBasis.SourceId))
            throw new ArgumentException(
                $"MIR semantic source {semanticBasis.SourceId} is not an ancestor of " +
                $"lowering source {loweredFrom.Id}.",
                nameof(lineage));

        var semanticSymbols = semanticBasis.Model.ScopeGraph
            ?? throw new ArgumentException(
                $"MIR semantic source {SymbolsFrom} has no HIR scope graph.",
                nameof(hir));
        VerifySymbolDispositions(semanticSymbols);

        var expectedCallableEdges =
            new HashSet<(HirSymbolRef Symbol, MirCallableRef Callable)>();
        foreach (var (hirCallable, callable) in CallablesByHirCallable)
        {
            if (loweredFrom.Structure.RequireKind(hirCallable.NodeId) != HirNodeKind.Callable)
                throw new ArgumentException(
                    $"MIR callable {callable} originates from non-callable HIR node {hirCallable}.",
                    nameof(hir));

            var semanticCallableId = lineage.ResolveNodeId(
                LoweredFrom,
                semanticBasis.SourceId,
                hirCallable.NodeId);
            var symbol = semanticBasis.Model.FindSymbol(semanticCallableId)
                ?? throw new ArgumentException(
                    $"HIR callable {hirCallable} has no symbol in semantic source {SymbolsFrom}.",
                    nameof(hir));
            var symbolRef = new HirSymbolRef(SymbolsFrom, symbol.Id);
            expectedCallableEdges.Add((symbolRef, callable));
            if (!CallablesBySymbol.TryGetValue(symbolRef, out var linked)
                || !linked.Contains(callable))
            {
                throw new ArgumentException(
                    $"HIR callable {hirCallable} and symbol {symbolRef} disagree about MIR callable " +
                    $"{callable}.",
                    nameof(hir));
            }
            if (!CallableProvenance.TryGetValue(
                    callable,
                    out var provenance)
                || provenance is not MirLoweredCallableProvenance lowered
                || lowered.Callable != hirCallable
                || lowered.Symbol != symbolRef)
            {
                throw new ArgumentException(
                    $"MIR callable {callable} does not preserve the exact HIR callable/symbol "
                    + "provenance resolved from its lowering source.",
                    nameof(hir));
            }
        }

        var actualCallableEdges = CallablesBySymbol
            .SelectMany(
                pair => pair.Value.Select(
                    callable => (Symbol: pair.Key, Callable: callable)))
            .ToHashSet();
        if (!actualCallableEdges.SetEquals(expectedCallableEdges))
        {
            throw new ArgumentException(
                "HIR symbol-to-callable links do not exactly match the links resolved from "
                + "HIR callables.",
                nameof(hir));
        }

        foreach (var symbol in AllSymbolRefs())
            if (semanticSymbols.FindSymbol(symbol.Symbol) is null)
                throw new ArgumentException(
                    $"MIR links name unknown semantic symbol {symbol}.",
                    nameof(hir));

        foreach (var origin in Origins.Origins)
        {
            var source = Origins.ResolveHir(origin.Id);
            if (loweredFrom.Structure.RequireKind(source.HirCallableId!.Value)
                != HirNodeKind.Callable)
            {
                throw new ArgumentException(
                    $"MIR origin {origin.Id} names non-callable HIR owner " +
                    $"{source.HirCallableId}.",
                    nameof(hir));
            }
            var sourceNodeId = source.HirNodeId!.Value;
            var owner = loweredFrom.Structure.RequireOwningCallable(sourceNodeId);
            if (owner != source.HirCallableId)
            {
                throw new ArgumentException(
                    $"MIR origin {origin.Id} assigns HIR node {sourceNodeId} to callable " +
                    $"{source.HirCallableId}, but its structural owner is {owner}.",
                    nameof(hir));
            }
            if (source.Span != loweredFrom.SourceMap.Find(sourceNodeId))
            {
                throw new ArgumentException(
                    $"MIR origin {origin.Id} does not preserve the exact HIR source span.",
                    nameof(hir));
            }
        }
    }

    private void VerifySymbolDispositions(HirScopeGraph semanticSymbols)
    {
        var expectedSymbols = semanticSymbols.AllSymbols
            .Select(symbol => new HirSymbolRef(SymbolsFrom, symbol.Id))
            .ToHashSet();
        if (!expectedSymbols.SetEquals(SymbolDispositions.Keys))
        {
            throw new ArgumentException(
                "MIR symbol dispositions must cover every HIR semantic symbol exactly once.",
                nameof(SymbolDispositions));
        }

        foreach (var symbol in semanticSymbols.AllSymbols)
        {
            var reference = new HirSymbolRef(SymbolsFrom, symbol.Id);
            var actual = SymbolDispositions[reference];
            var structuralExpectation = ExpectedDisposition(symbol);
            if (actual != structuralExpectation
                && actual != MirSymbolLoweringDisposition.NotLoweredUnreachable)
            {
                throw new ArgumentException(
                    $"MIR symbol {reference} has disposition {actual}; expected "
                    + $"{structuralExpectation} or an explicit non-lowering reason.",
                    nameof(SymbolDispositions));
            }
            if (actual == MirSymbolLoweringDisposition.NotLoweredUnreachable
                && structuralExpectation is MirSymbolLoweringDisposition.Callable
                    or MirSymbolLoweringDisposition.NotLoweredNamespace
                    or MirSymbolLoweringDisposition.NotLoweredBuiltin)
            {
                throw new ArgumentException(
                    $"MIR symbol {reference} cannot use {actual} for {structuralExpectation}.",
                    nameof(SymbolDispositions));
            }

            var hasCallable =
                CallablesBySymbol.TryGetValue(reference, out var callables)
                && callables.Count > 0;
            var hasValue =
                ValuesBySymbol.TryGetValue(reference, out var values)
                && values.Count > 0;
            var hasStorage =
                StoragesBySymbol.TryGetValue(reference, out var storages)
                && storages.Count > 0;
            var hasQubit =
                QubitsBySymbol.TryGetValue(reference, out var qubits)
                && qubits.Count > 0;

            var primaryPresent = actual switch
            {
                MirSymbolLoweringDisposition.Callable => hasCallable,
                MirSymbolLoweringDisposition.ScalarValue => hasValue,
                MirSymbolLoweringDisposition.ArrayStorage => hasStorage,
                MirSymbolLoweringDisposition.Qubit => hasQubit,
                MirSymbolLoweringDisposition.NotLoweredNamespace
                    or MirSymbolLoweringDisposition.NotLoweredBuiltin
                    or MirSymbolLoweringDisposition.NotLoweredUnreachable =>
                    !hasCallable && !hasValue && !hasStorage && !hasQubit,
                _ => false,
            };
            if (!primaryPresent)
            {
                throw new ArgumentException(
                    $"MIR symbol {reference} does not satisfy its {actual} lowering disposition.",
                    nameof(SymbolDispositions));
            }
        }
    }

    internal static MirSymbolLoweringDisposition ExpectedDisposition(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.Kind switch
        {
            SymbolKind.Namespace =>
                MirSymbolLoweringDisposition.NotLoweredNamespace,
            SymbolKind.BuiltinGate or SymbolKind.BuiltinFunction =>
                MirSymbolLoweringDisposition.NotLoweredBuiltin,
            SymbolKind.Callable =>
                MirSymbolLoweringDisposition.Callable,
            _ when symbol.Type == QType.Qubit =>
                MirSymbolLoweringDisposition.Qubit,
            _ when symbol.IsArray =>
                MirSymbolLoweringDisposition.ArrayStorage,
            _ => MirSymbolLoweringDisposition.ScalarValue,
        };
    }

    private IEnumerable<HirSymbolRef> AllSymbolRefs() =>
        CallablesBySymbol.Keys
            .Concat(ValuesBySymbol.Keys)
            .Concat(SymbolsByValue.Values.SelectMany(symbols => symbols))
            .Concat(StoragesBySymbol.Keys)
            .Concat(SymbolsByStorage.Values.SelectMany(symbols => symbols))
            .Concat(QubitsBySymbol.Keys)
            .Concat(SymbolsByQubit.Values.SelectMany(symbols => symbols))
            .Concat(SymbolDispositions.Keys)
            .Distinct();

    private static void VerifyEntityCoverage<TReference>(
        IReadOnlyCollection<TReference> structuralEntities,
        IReadOnlyDictionary<TReference, MirEntityOriginKind> origins,
        IReadOnlyDictionary<TReference, IReadOnlyList<HirSymbolRef>> sourceSymbols,
        string entityName)
        where TReference : notnull
    {
        var structural = structuralEntities.ToHashSet();
        if (!structural.SetEquals(origins.Keys))
        {
            throw new ArgumentException(
                $"MIR {entityName} origin dispositions must cover every structural {entityName} "
                + "exactly once.",
                nameof(origins));
        }

        foreach (var (entity, origin) in origins)
        {
            var hasSource =
                sourceSymbols.TryGetValue(entity, out var symbols)
                && symbols.Count > 0;
            var valid = origin switch
            {
                MirEntityOriginKind.SourceSymbol => hasSource,
                MirEntityOriginKind.CompilerTemporary => !hasSource,
                _ => false,
            };
            if (!valid)
            {
                throw new ArgumentException(
                    $"MIR {entityName} {entity} does not satisfy its {origin} origin disposition.",
                    nameof(origins));
            }
        }
    }

    private static IReadOnlyDictionary<TKey, TValue> Freeze<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> source)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ToFrozenDictionary();
    }

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> FreezeNested<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> source)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ToFrozenDictionary(
            pair => pair.Key,
            pair =>
            {
                ArgumentNullException.ThrowIfNull(pair.Value);
                var values = pair.Value.ToImmutableArray();
                if (values.Distinct().Count() != values.Length)
                    throw new ArgumentException(
                        $"Cross-stage link key {pair.Key} contains a duplicate endpoint.",
                        nameof(source));
                return (IReadOnlyList<TValue>)values;
            });
    }

    private static void VerifySymmetry<TLeft, TRight>(
        IReadOnlyDictionary<TLeft, IReadOnlyList<TRight>> forward,
        IReadOnlyDictionary<TRight, IReadOnlyList<TLeft>> reverse,
        string relation)
        where TLeft : notnull
        where TRight : notnull
    {
        foreach (var (left, rights) in forward)
            foreach (var right in rights)
                if (!reverse.TryGetValue(right, out var lefts) || !lefts.Contains(left))
                    throw new ArgumentException(
                        $"{relation} link {left} → {right} has no reverse edge.");

        foreach (var (right, lefts) in reverse)
            foreach (var left in lefts)
                if (!forward.TryGetValue(left, out var rights) || !rights.Contains(right))
                    throw new ArgumentException(
                        $"{relation} link {right} → {left} has no forward edge.");
    }
}

internal sealed class MirCrossStageLinksBuilder
{
    private readonly MirSnapshotId _mirSnapshot;
    private readonly HirSnapshot _loweredFrom;
    private readonly HirSemanticArtifact _symbolsFrom;
    private readonly HirScopeGraph _scopeGraph;
    private readonly Dictionary<HirNodeRef, MirCallableRef> _callablesByHirCallable = new();
    private readonly BiMultiMap<HirSymbolRef, MirCallableRef> _callables = new();
    private readonly BiMultiMap<HirSymbolRef, MirValueRef> _values = new();
    private readonly BiMultiMap<HirSymbolRef, MirStorageRef> _storages = new();
    private readonly BiMultiMap<HirSymbolRef, MirQubitRef> _qubits = new();
    private readonly Dictionary<HirSymbolRef, MirSymbolLoweringDisposition> _dispositions = new();
    private readonly Dictionary<MirCallableRef, MirCallableProvenance> _callableProvenance = new();
    private readonly Dictionary<MirValueRef, MirEntityOriginKind> _valueOrigins = new();
    private readonly Dictionary<MirStorageRef, MirEntityOriginKind> _storageOrigins = new();
    private readonly Dictionary<MirQubitRef, MirEntityOriginKind> _qubitOrigins = new();

    public MirCrossStageLinksBuilder(
        MirSnapshotId mirSnapshot,
        HirSnapshot loweredFrom,
        HirSemanticArtifact symbolsFrom)
    {
        ArgumentNullException.ThrowIfNull(loweredFrom);
        ArgumentNullException.ThrowIfNull(symbolsFrom);
        if (mirSnapshot.CompilationId != loweredFrom.Id.CompilationId
            || mirSnapshot.CompilationRevision != loweredFrom.Id.CompilationRevision
            || loweredFrom.Id.CompilationId != symbolsFrom.SourceId.CompilationId
            || loweredFrom.Id.CompilationRevision != symbolsFrom.SourceId.CompilationRevision)
        {
            throw new ArgumentException(
                "cross-stage links cannot cross compilation snapshots: " +
                $"MIR={mirSnapshot}, HIR={loweredFrom}, semantics={symbolsFrom}");
        }

        _mirSnapshot = mirSnapshot;
        _loweredFrom = loweredFrom;
        _symbolsFrom = symbolsFrom;
        _scopeGraph = symbolsFrom.Model.ScopeGraph
            ?? throw new ArgumentException(
                "MIR lowering requires a sealed HIR scope graph.",
                nameof(symbolsFrom));
        foreach (var symbol in _scopeGraph.AllSymbols)
        {
            var disposition = MirCrossStageLinks.ExpectedDisposition(symbol);
            if (disposition is MirSymbolLoweringDisposition.NotLoweredNamespace
                or MirSymbolLoweringDisposition.NotLoweredBuiltin)
                RecordDisposition(symbol.Id, disposition);
        }
    }

    public void LinkCallable(
        HirNodeId hirCallableId,
        SymbolId symbol,
        MirCallableId callable)
    {
        var hirCallable = new HirNodeRef(_loweredFrom.Id, hirCallableId);
        var reference = new MirCallableRef(_mirSnapshot, callable);
        if (!_callablesByHirCallable.TryAdd(hirCallable, reference))
            throw new InvalidOperationException(
                $"HIR callable {hirCallable} was lowered more than once");
        if (!_callableProvenance.TryAdd(
                reference,
                new MirLoweredCallableProvenance(hirCallable, Symbol(symbol))))
        {
            throw new InvalidOperationException(
                $"MIR callable {reference} was registered more than once");
        }
        RecordDisposition(symbol, MirSymbolLoweringDisposition.Callable);
        _callables.Add(Symbol(symbol), reference);
    }

    public void LinkValue(
        SymbolId symbol,
        MirCallableId callable,
        MirValueId value)
    {
        var reference = new MirValueRef(_mirSnapshot, callable, value);
        if (!_valueOrigins.ContainsKey(reference))
            _valueOrigins.Add(reference, MirEntityOriginKind.SourceSymbol);
        else
            _valueOrigins[reference] = MirEntityOriginKind.SourceSymbol;
        if (RequireSymbol(symbol).IsArray is false)
            RecordDisposition(symbol, MirSymbolLoweringDisposition.ScalarValue);
        _values.Add(Symbol(symbol), reference);
    }

    public void RegisterTemporaryValue(
        MirCallableId callable,
        MirValueId value)
    {
        var reference = new MirValueRef(_mirSnapshot, callable, value);
        if (!_valueOrigins.TryAdd(reference, MirEntityOriginKind.CompilerTemporary))
            throw new InvalidOperationException(
                $"MIR value {reference} was registered more than once.");
    }

    public void LinkStorage(
        SymbolId symbol,
        MirCallableId callable,
        MirStorageId storage)
    {
        RecordDisposition(symbol, MirSymbolLoweringDisposition.ArrayStorage);
        var reference = new MirStorageRef(_mirSnapshot, callable, storage);
        if (!_storageOrigins.TryAdd(reference, MirEntityOriginKind.SourceSymbol))
            throw new InvalidOperationException(
                $"MIR storage {reference} was registered more than once.");
        _storages.Add(Symbol(symbol), reference);
    }

    public void LinkQubit(
        SymbolId symbol,
        MirCallableId callable,
        MirQubitKey qubit)
    {
        RecordDisposition(symbol, MirSymbolLoweringDisposition.Qubit);
        var reference = new MirQubitRef(_mirSnapshot, callable, qubit);
        if (!_qubitOrigins.TryAdd(reference, MirEntityOriginKind.SourceSymbol))
            throw new InvalidOperationException(
                $"MIR qubit {reference} was registered more than once.");
        _qubits.Add(Symbol(symbol), reference);
    }

    /// <summary>
    /// Records that a source declaration was deliberately omitted because no CFG path can reach it.
    /// This is a lowering decision, not an inference from a missing link: unclassified omissions remain
    /// errors when the immutable cross-stage map is verified.
    /// </summary>
    public void MarkUnreachable(SymbolId symbol)
    {
        if (_dispositions.ContainsKey(Symbol(symbol)))
            return;
        RecordDisposition(symbol, MirSymbolLoweringDisposition.NotLoweredUnreachable);
    }

    public MirCrossStageLinks Build(MirOriginTable origins) =>
        new(
            _mirSnapshot,
            _loweredFrom,
            _symbolsFrom,
            origins,
            _callablesByHirCallable.ToFrozenDictionary(),
            _callables.Forward(),
            _values.Forward(),
            _values.Reverse(),
            _storages.Forward(),
            _storages.Reverse(),
            _qubits.Forward(),
            _qubits.Reverse(),
            _dispositions,
            _callableProvenance,
            _valueOrigins,
            _storageOrigins,
            _qubitOrigins);

    private HirSymbolRef Symbol(SymbolId symbol) =>
        new(_symbolsFrom.Id, symbol);

    private Symbol RequireSymbol(SymbolId symbol) =>
        _scopeGraph.FindSymbol(symbol)
        ?? throw new InvalidOperationException(
            $"HIR semantic symbol {symbol} does not belong to {_symbolsFrom.Id}.");

    private void RecordDisposition(
        SymbolId symbol,
        MirSymbolLoweringDisposition disposition)
    {
        var semanticSymbol = RequireSymbol(symbol);
        var expected = MirCrossStageLinks.ExpectedDisposition(semanticSymbol);
        if (disposition != expected
            && disposition != MirSymbolLoweringDisposition.NotLoweredUnreachable)
        {
            throw new InvalidOperationException(
                $"MIR lowering classified HIR symbol {symbol} as {disposition}; expected {expected}.");
        }
        if (disposition == MirSymbolLoweringDisposition.NotLoweredUnreachable
            && expected is MirSymbolLoweringDisposition.Callable
                or MirSymbolLoweringDisposition.NotLoweredNamespace
                or MirSymbolLoweringDisposition.NotLoweredBuiltin)
        {
            throw new InvalidOperationException(
                $"HIR symbol {symbol} cannot be omitted as unreachable.");
        }

        var reference = Symbol(symbol);
        if (_dispositions.TryGetValue(reference, out var existing)
            && existing != disposition)
        {
            throw new InvalidOperationException(
                $"HIR symbol {symbol} was classified as both {existing} and {disposition}.");
        }
        _dispositions[reference] = disposition;
    }

    private sealed class BiMultiMap<TLeft, TRight>
        where TLeft : notnull
        where TRight : notnull
    {
        private readonly Dictionary<TLeft, HashSet<TRight>> _forward = new();
        private readonly Dictionary<TRight, HashSet<TLeft>> _reverse = new();

        public void Add(TLeft left, TRight right)
        {
            AddTo(_forward, left, right);
            AddTo(_reverse, right, left);
        }

        public IReadOnlyDictionary<TLeft, IReadOnlyList<TRight>> Forward() =>
            Freeze(_forward);

        public IReadOnlyDictionary<TRight, IReadOnlyList<TLeft>> Reverse() =>
            Freeze(_reverse);

        private static void AddTo<TKey, TValue>(
            Dictionary<TKey, HashSet<TValue>> map,
            TKey key,
            TValue value)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var values))
            {
                values = new HashSet<TValue>();
                map.Add(key, values);
            }
            values.Add(value);
        }

        private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> Freeze<TKey, TValue>(
            Dictionary<TKey, HashSet<TValue>> source)
            where TKey : notnull =>
            source.ToFrozenDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TValue>)pair.Value.ToImmutableArray());
    }
}

/// <summary>The complete, exact result of one HIR-to-MIR lowering.</summary>
internal sealed record MirLoweringResult(
    MirProgram Program,
    MirCrossStageLinks Links,
    MirSafetyFacts Safety)
{
    public MirSnapshot CreateSnapshot(
        MirLoweringProfile profile = MirLoweringProfile.CanonicalV1) =>
        new(
            Links.MirSnapshot,
            profile,
            Program,
            Links,
            safety: Safety);
}
