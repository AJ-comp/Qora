namespace Qora.Ir.Mir.Analysis;

public enum MirWitnessIssueKind
{
    ScalarValueUnavailable,
    PathPredicateUnavailable,
    ArrayStateUnavailable,
}

/// <summary>
/// One unavailable replay input. Array issues retain the memory-state verdict so diagnostics can name
/// the exact store, mutable call, or ownership transfer which destroyed the required contents.
/// </summary>
public sealed record MirWitnessIssue(
    MirWitnessIssueKind Kind,
    MirValueId Value,
    MirMemoryStateAvailability? Memory = null);

/// <summary>
/// Availability of the exact classical inputs used by one forward quantum instruction at a proposed
/// later point. This is deliberately not an overall cleanup-safety verdict: physical reversibility,
/// qubit dependency ordering, and adjoint materialization remain separate analyses.
/// </summary>
public sealed record MirWitnessAvailability(
    IReadOnlyList<MirWitnessIssue> Issues,
    IReadOnlyList<MirScalarValueAvailability> Rematerializations,
    bool RequiresIterationLocalPlacement,
    bool RequiresBoundsRevalidation)
{
    private IReadOnlyList<MirWitnessIssue> _issues = MirCollections.Freeze(Issues);
    private IReadOnlyList<MirScalarValueAvailability> _rematerializations =
        MirCollections.Freeze(Rematerializations);

    public IReadOnlyList<MirWitnessIssue> Issues
    {
        get => _issues;
        init => _issues = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirScalarValueAvailability> Rematerializations
    {
        get => _rematerializations;
        init => _rematerializations = MirCollections.Freeze(value);
    }

    public bool AllWitnessesAvailable => Issues.Count == 0;
}

/// <summary>
/// Query object which joins quantum-effect facts with scalar SSA and destructive-memory availability.
/// A source variable assignment never invalidates an older scalar <see cref="MirValueId"/>; an array
/// mutation can invalidate an older state even when that state's SSA definition still dominates.
/// </summary>
public sealed class MirWitnessAvailabilitySnapshot
{
    private readonly MirEffectSnapshot _effects;
    private readonly MirScalarValueAvailabilitySnapshot _scalars;

    internal MirWitnessAvailabilitySnapshot(
        MirEffectSnapshot effects,
        MirScalarValueAvailabilitySnapshot scalars)
    {
        _effects = effects;
        _scalars = scalars;
    }

    public MirCallableId Callable => _scalars.Callable;
    private MirControlFlowSnapshot ControlFlow => _scalars.ControlFlow;
    private MirMemoryStateSnapshot MemoryState => _scalars.MemoryState;

    public MirWitnessAvailability CheckBeforeInstruction(
        MirInstructionSite effect,
        MirInstructionId target) =>
        Check(effect, ControlFlow.PointBeforeInstruction(target));

    public MirWitnessAvailability CheckAtTerminator(
        MirInstructionSite effect,
        MirBlockId target) =>
        Check(effect, ControlFlow.TerminatorPoint(target));

    private MirWitnessAvailability Check(
        MirInstructionSite site,
        MirProgramPoint point)
    {
        var effect = _effects.EffectAt(site);
        if (site.Callable != Callable
            || effect is null)
            throw new ArgumentOutOfRangeException(
                nameof(site),
                site,
                $"effect site {site} does not belong to callable {Callable}");

        var issues = new List<MirWitnessIssue>();
        var rematerializations = new List<MirScalarValueAvailability>();
        foreach (var witness in effect.ClassicalWitnesses)
        {
            var availability = _scalars.Check(witness.Value, point);
            if (!availability.CanSupplyValue)
                issues.Add(new MirWitnessIssue(
                    MirWitnessIssueKind.ScalarValueUnavailable,
                    witness.Value));
            else if (availability.Kind == MirScalarValueAvailabilityKind.Rematerializable)
                rematerializations.Add(availability);
        }

        // PathCondition is a Boolean expression, not a flat conjunction. Availability needs every
        // distinct SSA leaf which a later guard reconstruction may evaluate, while control-flow
        // consumers must preserve the expression's All/Any shape.
        foreach (var predicate in effect.PathCondition.Predicates
                     .DistinctBy(predicate => predicate.Condition))
        {
            var availability = _scalars.Check(predicate.Condition, point);
            if (!availability.CanSupplyValue)
                issues.Add(new MirWitnessIssue(
                    MirWitnessIssueKind.PathPredicateUnavailable,
                    predicate.Condition));
            else if (availability.Kind == MirScalarValueAvailabilityKind.Rematerializable)
                rematerializations.Add(availability);
        }

        var requiresIterationLocalPlacement =
            effect.ExecutionMultiplicity == MirExecutionMultiplicity.LoopCarried;
        foreach (var array in effect.ArrayStates)
        {
            var availability = MemoryState.Check(array.InputState, point);
            requiresIterationLocalPlacement |= availability.RequiresSameIteration;
            if (!availability.IsAvailable)
                issues.Add(new MirWitnessIssue(
                    MirWitnessIssueKind.ArrayStateUnavailable,
                    array.InputState,
                    availability));
        }

        return new MirWitnessAvailability(
            MirCollections.Freeze(
                issues.DistinctBy(issue => (issue.Kind, issue.Value))),
            MirCollections.Freeze(
                rematerializations.DistinctBy(availability => availability.Value)),
            requiresIterationLocalPlacement,
            RequiresBoundsRevalidation: effect.Qubits.Any(
                qubit => qubit.Access.Index is not null));
    }
}

internal static class MirWitnessAvailabilityAnalysis
{
    internal static MirWitnessAvailabilitySnapshot Analyze(
        MirProgram program,
        MirEffectSnapshot effects,
        MirCallableId callableId)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(effects);

        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        var cfg = MirControlFlowAnalysis.AnalyzeUnchecked(program, callable);
        var provenance = MirStorageProvenanceAnalysis.AnalyzeUnchecked(
            program,
            callable);
        var memory = MirMemoryStateAnalysis.AnalyzeVerified(
            cfg,
            provenance);
        var scalars = MirScalarValueAvailabilityAnalysis.AnalyzeVerified(memory);
        return AnalyzeVerified(effects, scalars);
    }

    /// <summary>
    /// Joins exact effect, CFG, memory, and scalar snapshots already owned by one
    /// <see cref="MirAnalysisStore"/>.
    /// </summary>
    internal static MirWitnessAvailabilitySnapshot AnalyzeVerified(
        MirEffectSnapshot effects,
        MirScalarValueAvailabilitySnapshot scalars)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(scalars);
        effects.EnsureFor(scalars.ControlFlow.SourceProgram);

        return new MirWitnessAvailabilitySnapshot(
            effects,
            scalars);
    }
}
