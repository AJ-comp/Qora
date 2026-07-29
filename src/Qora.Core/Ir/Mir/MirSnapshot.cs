using Qora.Compiler;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

/// <summary>
/// Identifies one immutable MIR generation inside one exact compilation snapshot.
/// </summary>
public readonly record struct MirSnapshotId
{
    public MirSnapshotId(
        CompilationId compilationId,
        CompilationRevision compilationRevision,
        int revision)
    {
        if (compilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "a MIR snapshot requires a non-empty compilation identity",
                nameof(compilationId));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));

        CompilationId = compilationId;
        CompilationRevision = compilationRevision;
        Revision = revision;
    }

    public CompilationId CompilationId { get; }
    public CompilationRevision CompilationRevision { get; }
    public int Revision { get; }

    public override string ToString() =>
        $"{CompilationId}@{CompilationRevision}/m{Revision}";
}

/// <summary>
/// Identifies the lowering contract which produced a MIR snapshot. A new profile must be introduced
/// whenever lowering changes facts that MIR analyses can observe.
/// </summary>
public enum MirLoweringProfile
{
    CanonicalV1,
}

/// <summary>
/// The pass milestone owned by one immutable MIR snapshot. HIR lowering creates the root snapshot;
/// any MIR transformation that changes the program allocates a greater revision and points back to its
/// exact parent.
/// </summary>
public enum MirStage
{
    Lowered,
    InverseRequestsInjected,
    AdjointsMaterialized,
}

/// <summary>
/// One immutable MIR artifact tied to the exact HIR snapshot and lowering profile which produced it.
/// Callable-local structural indexes and analyses are owned by this snapshot rather than by a mutable
/// global model.
/// </summary>
public sealed class MirSnapshot
{
    internal MirSnapshot(
        MirSnapshotId id,
        MirLoweringProfile profile,
        MirProgram program,
        HirSemanticArtifact loweringSource,
        MirStage stage = MirStage.Lowered,
        MirSnapshot? parent = null,
        MirSafetyFacts? safety = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loweringSource);

        if (loweringSource.Phase != HirSemanticPhase.EffectAnalysis
            || !loweringSource.IsAccepted)
        {
            throw new ArgumentException(
                "MIR lowering requires an accepted final HIR effect-analysis artifact.",
                nameof(loweringSource));
        }
        if (id.CompilationId != loweringSource.SourceId.CompilationId
            || id.CompilationRevision != loweringSource.SourceId.CompilationRevision)
            throw new ArgumentException(
                "the MIR snapshot and its HIR origin must belong to the same compilation snapshot",
                nameof(loweringSource));
        if (id != program.SnapshotId)
            throw new ArgumentException(
                $"MIR snapshot identity {id} disagrees with program identity {program.SnapshotId}",
                nameof(program));
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "unknown MIR lowering profile");
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown MIR stage");
        safety ??= MirSafetyFacts.Empty(id);
        if (safety.SnapshotId != id)
            throw new ArgumentException(
                "MIR safety facts belong to a different snapshot",
                nameof(safety));
        if (parent is null)
        {
            if (stage != MirStage.Lowered)
                throw new ArgumentException(
                    "a non-lowered MIR snapshot requires an exact parent snapshot",
                    nameof(parent));
        }
        else
        {
            if (stage == MirStage.Lowered)
                throw new ArgumentException(
                    "a transformed MIR snapshot cannot declare the lowering stage",
                    nameof(stage));
            if (parent.Id.CompilationId != id.CompilationId
                || parent.Id.CompilationRevision != id.CompilationRevision
                || parent.Id.Revision == int.MaxValue
                || id.Revision != parent.Id.Revision + 1)
            {
                throw new ArgumentException(
                    "a MIR transformation must be the immediate next revision of its exact parent",
                    nameof(parent));
            }
            if (parent.Profile != profile)
                throw new ArgumentException(
                    "a MIR transformation cannot change its lowering profile",
                    nameof(profile));
            if (!IsDirectStageSuccessor(parent.Stage, stage))
            {
                throw new ArgumentException(
                    $"MIR stage {stage} cannot directly follow {parent.Stage}; "
                    + "inverse requests must be injected before their adjoints are materialized",
                    nameof(stage));
            }
            if (!ReferenceEquals(
                    parent.LoweringSource,
                    loweringSource))
                throw new ArgumentException(
                    "a MIR transformation must retain its parent's exact final HIR artifact",
                    nameof(loweringSource));
        }

        QoraMirVerifier.VerifyOrThrow(program);

        Id = id;
        Profile = profile;
        Stage = stage;
        Parent = parent;
        Program = program;
        Safety = safety;
        LoweringSource = loweringSource;
        foreach (var obligation in safety.UnprovenBounds)
            VerifyBoundsSite(
                obligation.Site,
                program.RequireInstruction(
                    obligation.Site.Instruction));
        Analyses = new MirAnalysisStore(this);
    }

    private static void VerifyBoundsSite(
        MirIndexedAccessSite site,
        MirInstruction instruction)
    {
        var matches = (site.Kind, instruction) switch
        {
            (MirIndexedAccessKind.ArrayLoad, MirArrayLoad) =>
                site.Ordinal == 0,
            (MirIndexedAccessKind.ArrayStore, MirArrayStore) =>
                site.Ordinal == 0,
            (MirIndexedAccessKind.Measurement, MirMeasure
            {
                Qubit.Index: not null,
            }) =>
                site.Ordinal == 0,
            (MirIndexedAccessKind.QubitOperand, MirQuantumApply apply)
                when site.Ordinal < apply.Operands.Count =>
                apply.Operands[site.Ordinal] is MirQubitCallOperand
                {
                    Qubit.Index: not null,
                },
            _ => false,
        };
        if (!matches)
        {
            throw new ArgumentException(
                $"MIR bounds obligation site {site} does not identify an indexed access");
        }
    }

    private static bool IsDirectStageSuccessor(
        MirStage parent,
        MirStage child) =>
        (parent, child) switch
        {
            (MirStage.Lowered, MirStage.InverseRequestsInjected) => true,
            (MirStage.InverseRequestsInjected, MirStage.AdjointsMaterialized) => true,
            _ => false,
        };

    public MirSnapshotId Id { get; }
    public HirSnapshotId LoweredFrom => LoweringSource.SourceId;
    public MirLoweringProfile Profile { get; }
    public MirStage Stage { get; }
    public MirSnapshot? Parent { get; }
    public MirProgram Program { get; }
    public MirSafetyFacts Safety { get; }
    public MirOriginTable Origins => Program.Origins;
    internal HirSemanticArtifact LoweringSource { get; }
    public MirAnalysisStore Analyses { get; }

    public bool DescendsFrom(MirSnapshotId ancestor)
    {
        for (MirSnapshot? current = this; current is not null; current = current.Parent)
            if (current.Id == ancestor)
                return true;
        return false;
    }
}
