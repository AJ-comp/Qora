using Qora.Ir.Mir;

namespace Qora.Compiler;

/// <summary>The stage that produced a diagnostic.</summary>
public enum CompilationStage
{
    Syntax,
    ImportExpansion,
    HirPreflight,
    HirResolution,
    HirValidation,
    HirAnalysis,
    MirLowering,
    MirAnalysis,
    OpenQasm,
}

/// <summary>
/// Typed provenance for one diagnostic. Each variant carries the identity owned by its compiler stage,
/// so source, HIR, MIR, and target failures cannot be mixed through a nullable HIR-only field.
/// </summary>
public abstract record DiagnosticOrigin
{
    private DiagnosticOrigin()
    {
    }

    public sealed record Source(SourceDocumentRef Document) : DiagnosticOrigin;

    public sealed record Hir(HirSnapshotId Snapshot) : DiagnosticOrigin;

    public sealed record Mir : DiagnosticOrigin
    {
        public Mir(
            MirSnapshot snapshot,
            MirOrigin? location = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (location is not null
                && !ReferenceEquals(
                    location.SourceHirOrigin.HirArtifact,
                    snapshot.HirArtifact))
            {
                throw new ArgumentException(
                    "The MIR diagnostic location belongs to another HIR artifact.",
                    nameof(location));
            }

            Snapshot = snapshot;
            Location = location;
        }

        public MirSnapshot Snapshot { get; }
        public MirOrigin? Location { get; }
    }

    public sealed record Target : DiagnosticOrigin
    {
        public Target(
            TargetBackend backend,
            MirSnapshot input,
            MirOrigin? location = null)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (location is not null
                && !ReferenceEquals(
                    location.SourceHirOrigin.HirArtifact,
                    input.HirArtifact))
            {
                throw new ArgumentException(
                    "The target diagnostic location belongs to another HIR artifact.",
                    nameof(location));
            }

            Backend = backend;
            Input = input;
            Location = location;
        }

        public TargetBackend Backend { get; }
        public MirSnapshot Input { get; }
        public MirOrigin? Location { get; }
    }
}

public enum TargetBackend
{
    OpenQasm,
}

/// <summary>One diagnostic together with the exact compiler artifact that produced it.</summary>
public sealed record CompilationDiagnostic(
    CompilationStage Stage,
    QoraError Error,
    DiagnosticOrigin Origin);
