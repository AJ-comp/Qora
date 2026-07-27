using Qora.Ir.Passes;

namespace Qora.Compiler;

/// <summary>
/// Read-only textual views over exact, snapshot-qualified compilation artifacts.
/// Report consumers never supply a detached HIR tree and semantic model pair, so a report cannot
/// accidentally combine facts from one HIR generation with nodes from another.
/// </summary>
public static class CompilationReports
{
    /// <summary>Render the symbol and scope view attached to one exact HIR semantic artifact.</summary>
    public static string FormatSymbols(HirSemanticArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return SymbolTableBuilder.Format(artifact.Program, artifact.Model);
    }

    /// <summary>
    /// Render automatic-uncomputation verdicts from an effect-analysis artifact.
    /// A validation-only artifact has no quantum effect history and is rejected instead of producing an
    /// apparently valid empty report.
    /// </summary>
    public static string FormatUncompute(HirSemanticArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Phase != HirSemanticPhase.EffectAnalysis)
        {
            throw new ArgumentException(
                "An uncompute report requires an effect-analysis HIR artifact.",
                nameof(artifact));
        }

        return UncomputeReport.Format(artifact.Program, artifact.Model);
    }
}
