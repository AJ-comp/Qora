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
/// The pass milestone owned by one immutable MIR snapshot. HIR lowering creates the root snapshot;
/// any MIR transformation that changes the program allocates a greater revision.
/// </summary>
public enum MirStage
{
    Lowered,
    InverseRequestsInjected,
    AdjointsMaterialized,
}

/// <summary>
/// One immutable MIR artifact tied to the exact HIR snapshot which produced it. Callable-local
/// structural indexes and analyses are owned by this snapshot rather than by a mutable global model.
/// </summary>
public sealed class MirSnapshot
{
    internal MirSnapshot(
        MirProgram program,
        HirSemanticArtifact loweringSource,
        MirStage stage = MirStage.Lowered,
        MirSnapshot? transformationSource = null,
        IEnumerable<MirBoundsObligation>? unprovenBounds = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loweringSource);
        var id = program.SnapshotId;

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
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown MIR stage");
        unprovenBounds ??= Array.Empty<MirBoundsObligation>();
        if (transformationSource is null)
        {
            if (stage != MirStage.Lowered)
                throw new ArgumentException(
                    "a non-lowered MIR snapshot requires its exact transformation source",
                    nameof(transformationSource));
        }
        else
        {
            if (stage == MirStage.Lowered)
                throw new ArgumentException(
                    "a transformed MIR snapshot cannot declare the lowering stage",
                    nameof(stage));
            if (transformationSource.Id.CompilationId != id.CompilationId
                || transformationSource.Id.CompilationRevision != id.CompilationRevision
                || transformationSource.Id.Revision == int.MaxValue
                || id.Revision != transformationSource.Id.Revision + 1)
            {
                throw new ArgumentException(
                    "a MIR transformation must be the immediate next revision of its exact source",
                    nameof(transformationSource));
            }
            if (!IsDirectStageSuccessor(transformationSource.Stage, stage))
            {
                throw new ArgumentException(
                    $"MIR stage {stage} cannot directly follow {transformationSource.Stage}; "
                    + "inverse requests must be injected before their adjoints are materialized",
                    nameof(stage));
            }
            if (!ReferenceEquals(
                    transformationSource.LoweringSource,
                    loweringSource))
                throw new ArgumentException(
                    "a MIR transformation must retain its source's exact final HIR artifact",
                    nameof(loweringSource));
        }

        QoraMirVerifier.VerifyOrThrow(program);

        Stage = stage;
        Program = program;
        UnprovenBounds = MirCollections.Freeze(unprovenBounds);
        LoweringSource = loweringSource;
        Analyses = new MirAnalysisStore(this);
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

    public MirSnapshotId Id => Program.SnapshotId;
    public HirSnapshotId LoweredFrom => LoweringSource.SourceId;
    public MirStage Stage { get; }
    public MirProgram Program { get; }
    public IReadOnlyList<MirBoundsObligation> UnprovenBounds { get; }
    public MirOriginTable Origins => Program.Origins;
    internal HirSemanticArtifact LoweringSource { get; }
    public MirAnalysisStore Analyses { get; }
}
