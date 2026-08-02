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
    private readonly MirControlFlowSnapshot _cfg;
    private readonly MirMemoryStateSnapshot _memory;
    private readonly MirScalarValueAvailabilitySnapshot _scalars;

    internal MirWitnessAvailabilitySnapshot(
        MirEffectSnapshot effects,
        MirControlFlowSnapshot cfg,
        MirMemoryStateSnapshot memory,
        MirScalarValueAvailabilitySnapshot scalars)
    {
        _effects = effects;
        _cfg = cfg;
        _memory = memory;
        _scalars = scalars;
    }

    public MirCallableId Callable => _cfg.Callable;

    internal bool IsFor(
        MirProgram program,
        MirEffectSnapshot effects,
        MirCallableId callable) =>
        ReferenceEquals(_effects, effects)
        && _cfg.IsFor(program, callable);

    internal void EnsureFor(
        MirProgram program,
        MirEffectSnapshot effects,
        MirCallableId callable)
    {
        if (!IsFor(program, effects, callable))
            throw new InvalidOperationException(
                $"the MIR witness analysis does not belong to callable {callable} "
                + "and the supplied dependency analyses");
    }

    public MirWitnessAvailability CheckBeforeInstruction(
        MirInstructionSite effect,
        MirInstructionId target) =>
        Check(effect, _cfg.PointBeforeInstruction(target));

    public MirWitnessAvailability CheckAtTerminator(
        MirInstructionSite effect,
        MirBlockId target) =>
        Check(effect, _cfg.TerminatorPoint(target));

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
            var availability = _memory.CheckAtLocation(
                array.InputState,
                point.Block,
                point.InstructionIndex);
            requiresIterationLocalPlacement |= availability.RequiresSameIteration;
            if (!availability.IsAvailable)
                issues.Add(new MirWitnessIssue(
                    MirWitnessIssueKind.ArrayStateUnavailable,
                    array.InputState,
                    availability));
        }

        return new MirWitnessAvailability(
            issues
                .DistinctBy(issue => (issue.Kind, issue.Value))
                .ToArray(),
            rematerializations
                .DistinctBy(availability => availability.Value)
                .ToArray(),
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
        QoraMirVerifier.VerifyOrThrow(program);
        effects.EnsureFor(program);

        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        var cfg = MirControlFlowAnalysis.Analyze(program, callableId);
        var memory = MirMemoryStateAnalysis.Analyze(program, callableId);
        return AnalyzeVerified(
            program,
            effects,
            callable,
            cfg,
            memory,
            new MirScalarValueAvailabilitySnapshot(
                callable,
                cfg,
                memory));
    }

    /// <summary>
    /// Joins exact effect, CFG, memory, and scalar snapshots already owned by one
    /// <see cref="MirAnalysisStore"/>.
    /// </summary>
    internal static MirWitnessAvailabilitySnapshot AnalyzeVerified(
        MirProgram program,
        MirEffectSnapshot effects,
        MirCallable callable,
        MirControlFlowSnapshot cfg,
        MirMemoryStateSnapshot memory,
        MirScalarValueAvailabilitySnapshot scalars)
    {
        effects.EnsureFor(program);
        cfg.EnsureFor(program, callable.Id);
        memory.EnsureFor(program, callable.Id);
        if (!scalars.IsFor(program, callable.Id))
            throw new InvalidOperationException(
                $"MIR scalar-availability snapshot does not belong to {callable.Id} " +
                "in the requested MIR program");

        return new MirWitnessAvailabilitySnapshot(
            effects,
            cfg,
            memory,
            scalars);
    }
}
