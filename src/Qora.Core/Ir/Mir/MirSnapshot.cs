using Qora.Compiler;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

/// <summary>
/// One immutable MIR artifact tied to the exact final HIR semantic artifact which produced it.
/// Callable-local structural indexes and analyses are owned by this snapshot rather than by a
/// mutable global model.
/// </summary>
public sealed class MirSnapshot
{
    internal static MirSnapshot CreateLowered(MirProgram program) => new(program);

    private MirSnapshot(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var hirArtifact = program.HirArtifact;

        if (!hirArtifact.IsReadyForMirLowering)
        {
            throw new ArgumentException(
                "MIR lowering requires a HIR artifact ready for MIR lowering.",
                nameof(program));
        }
        QoraMirVerifier.VerifyOrThrow(program);

        Program = program;
        HirArtifact = hirArtifact;
        Analyses = new MirAnalysisStore(program);
    }

    public MirProgram Program { get; }
    internal HirSemanticArtifact HirArtifact { get; }
    public MirAnalysisStore Analyses { get; }
}
