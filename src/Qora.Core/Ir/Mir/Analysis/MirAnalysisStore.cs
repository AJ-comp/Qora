using System.Collections.Concurrent;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// The canonical analysis facade for one exact <see cref="MirSnapshot"/>. Results are computed lazily,
/// cached for the life of the snapshot, and share the same callable-level dependency instances.
/// No query can accidentally supply a different program or revision.
/// </summary>
public sealed class MirAnalysisStore
{
    private readonly MirProgram _program;
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirControlFlowSnapshot>> _controlFlow =
        new();
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirControlRegionSnapshot>> _controlRegions =
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
    private readonly ConcurrentDictionary<MirCallableId, Lazy<MirBoundsSnapshot>> _bounds = new();
    private readonly Lazy<MirEffectSnapshot> _effects;
    private readonly Lazy<MirCallGraph> _callGraph;

    internal MirAnalysisStore(MirProgram program)
    {
        _program = program ?? throw new ArgumentNullException(nameof(program));
        _callGraph = NewLazy(() => MirCallGraphAnalysis.AnalyzeVerified(Program));
        _effects = NewLazy(
            () => MirEffectAnalysis.AnalyzeVerified(
                CallGraph,
                callable => StorageProvenance(callable),
                callable => PathConditions(callable)));
    }

    public MirEffectSnapshot Effects => _effects.Value;
    public MirCallGraph CallGraph => _callGraph.Value;

    public MirControlFlowSnapshot ControlFlow(MirCallableId callable) =>
        ControlFlow(Program.RequireCallable(callable));

    public MirControlFlowSnapshot ControlFlow(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _controlFlow.GetOrAdd(
            source.Id,
            _ => NewLazy(
                () => MirControlFlowAnalysis.AnalyzeUnchecked(Program, source))).Value;
    }

    public MirStorageProvenanceSnapshot StorageProvenance(MirCallableId callable) =>
        StorageProvenance(Program.RequireCallable(callable));

    public MirStorageProvenanceSnapshot StorageProvenance(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _storageProvenance.GetOrAdd(
            source.Id,
            _ => NewLazy(
                () => MirStorageProvenanceAnalysis.AnalyzeUnchecked(Program, source))).Value;
    }

    /// <summary>
    /// Natural-loop and structured-region facts derived from the canonical CFG for this exact snapshot.
    /// The result never stores a second executable tree; target lowerings remain consumers of MIR blocks.
    /// </summary>
    public MirControlRegionSnapshot ControlRegions(MirCallableId callable) =>
        ControlRegions(Program.RequireCallable(callable));

    public MirControlRegionSnapshot ControlRegions(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _controlRegions.GetOrAdd(
            source.Id,
            _ => NewLazy(() => MirControlRegionAnalysis.AnalyzeVerified(ControlFlow(source)))).Value;
    }

    public MirMemoryStateSnapshot MemoryState(MirCallableId callable) =>
        MemoryState(Program.RequireCallable(callable));

    public MirMemoryStateSnapshot MemoryState(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _memoryState.GetOrAdd(
            source.Id,
            _ => NewLazy(
                () => MirMemoryStateAnalysis.AnalyzeVerified(
                    ControlFlow(source),
                    StorageProvenance(source)))).Value;
    }

    public MirPathConditionSnapshot PathConditions(MirCallableId callable) =>
        PathConditions(Program.RequireCallable(callable));

    public MirPathConditionSnapshot PathConditions(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _pathConditions.GetOrAdd(
            source.Id,
            _ => NewLazy(() => MirPathConditionAnalysis.AnalyzeVerified(ControlFlow(source)))).Value;
    }

    public MirScalarValueAvailabilitySnapshot ScalarAvailability(MirCallableId callable) =>
        ScalarAvailability(Program.RequireCallable(callable));

    public MirScalarValueAvailabilitySnapshot ScalarAvailability(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _scalarAvailability.GetOrAdd(
            source.Id,
            _ => NewLazy(() => MirScalarValueAvailabilityAnalysis.AnalyzeVerified(MemoryState(source)))).Value;
    }

    public MirWitnessAvailabilitySnapshot WitnessAvailability(MirCallableId callable) =>
        WitnessAvailability(Program.RequireCallable(callable));

    public MirWitnessAvailabilitySnapshot WitnessAvailability(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _witnessAvailability.GetOrAdd(
            source.Id,
            _ => NewLazy(
                () => MirWitnessAvailabilityAnalysis.AnalyzeVerified(
                    Effects,
                    ScalarAvailability(source)))).Value;
    }

    public MirBoundsSnapshot Bounds(MirCallableId callable) =>
        Bounds(Program.RequireCallable(callable));

    public MirBoundsSnapshot Bounds(MirCallable callable)
    {
        var source = Program.RequireCallable(callable);
        return _bounds.GetOrAdd(
            source.Id,
            _ => NewLazy(
                () => MirBoundsAnalysis.AnalyzeVerified(
                    PathConditions(source),
                    StorageProvenance(source)))).Value;
    }

    private MirProgram Program => _program;

    private static Lazy<T> NewLazy<T>(Func<T> factory) =>
        new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
}
