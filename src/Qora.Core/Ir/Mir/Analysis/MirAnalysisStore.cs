using System.Collections.Concurrent;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// The canonical analysis facade for one exact <see cref="MirSnapshot"/>. Results are computed lazily,
/// cached for the life of the snapshot, and share the same callable-level dependency instances.
/// No query can accidentally supply a different program or revision.
/// </summary>
public sealed class MirAnalysisStore
{
    private readonly MirSnapshot _snapshot;
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirControlFlowSnapshot>> _controlFlow =
        new();
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirStorageProvenanceSnapshot>>
        _storageProvenance = new();
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirMemoryStateSnapshot>> _memoryState =
        new();
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirPathConditionSnapshot>> _pathConditions =
        new();
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirScalarValueAvailabilitySnapshot>>
        _scalarAvailability = new();
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirWitnessAvailabilitySnapshot>>
        _witnessAvailability = new();
    private readonly Lazy<MirEffectSnapshot> _effects;

    internal MirAnalysisStore(MirSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _effects = NewLazy(
            () => MirEffectAnalysis.AnalyzeVerified(
                Program,
                callable => StorageProvenance(Local(callable)),
                callable => PathConditions(Local(callable))));
    }

    public MirSnapshotId SnapshotId => _snapshot.Id;
    public MirEffectSnapshot Effects => _effects.Value;

    public MirControlFlowSnapshot ControlFlow(MirCallableRef reference)
    {
        var source = RequireCallable(reference);
        var callable = reference.Callable;
        return _controlFlow.GetOrAdd(
            callable,
            _ => NewLazy(
                () => MirControlFlowAnalysis.AnalyzeUnchecked(Program, source))).Value;
    }

    public MirStorageProvenanceSnapshot StorageProvenance(MirCallableRef reference)
    {
        var source = RequireCallable(reference);
        var callable = reference.Callable;
        return _storageProvenance.GetOrAdd(
            callable,
            _ => NewLazy(
                () => MirStorageProvenanceAnalysis.AnalyzeUnchecked(Program, source))).Value;
    }

    public MirMemoryStateSnapshot MemoryState(MirCallableRef reference)
    {
        var source = RequireCallable(reference);
        var callable = reference.Callable;
        return _memoryState.GetOrAdd(
            callable,
            _ => NewLazy(
                () => MirMemoryStateAnalysis.AnalyzeVerified(
                    Program,
                    source,
                    ControlFlow(reference),
                    StorageProvenance(reference)))).Value;
    }

    public MirPathConditionSnapshot PathConditions(MirCallableRef reference)
    {
        var source = RequireCallable(reference);
        var callable = reference.Callable;
        return _pathConditions.GetOrAdd(
            callable,
            _ => NewLazy(
                () => MirPathConditionAnalysis.AnalyzeVerified(
                    Program,
                    source,
                    ControlFlow(reference)))).Value;
    }

    public MirScalarValueAvailabilitySnapshot ScalarAvailability(MirCallableRef reference)
    {
        var source = RequireCallable(reference);
        var callable = reference.Callable;
        return _scalarAvailability.GetOrAdd(
            callable,
            _ => NewLazy(
                () => MirScalarValueAvailabilityAnalysis.AnalyzeVerified(
                    Program,
                    source,
                    ControlFlow(reference),
                    MemoryState(reference)))).Value;
    }

    public MirWitnessAvailabilitySnapshot WitnessAvailability(MirCallableRef reference)
    {
        var source = RequireCallable(reference);
        var callable = reference.Callable;
        return _witnessAvailability.GetOrAdd(
            callable,
            _ => NewLazy(
                () => MirWitnessAvailabilityAnalysis.AnalyzeVerified(
                    Program,
                    Effects,
                    source,
                    ControlFlow(reference),
                    MemoryState(reference),
                    ScalarAvailability(reference)))).Value;
    }

    private MirProgram Program => _snapshot.Program;

    private MirCallable RequireCallable(MirCallableRef reference) =>
        _snapshot.Structure.RequireCallable(reference);

    private MirCallableRef Local(MirCallableId callable) =>
        new(_snapshot.Id, callable);

    private static Lazy<T> NewLazy<T>(Func<T> factory) =>
        new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
}
