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

    public sealed record Mir(
        MirSnapshotId Snapshot,
        MirOriginRef? Location = null) : DiagnosticOrigin;

    public sealed record Target(
        TargetBackend Backend,
        TargetDiagnosticInput Input) : DiagnosticOrigin;
}

/// <summary>
/// The exact compiler-stage input on which a target diagnostic was derived. This is a union rather than
/// a mandatory HIR reference because each backend is free to consume HIR or MIR.
/// </summary>
public abstract record TargetDiagnosticInput
{
    private TargetDiagnosticInput()
    {
    }

    public sealed record Hir(HirSnapshotId Snapshot) : TargetDiagnosticInput;

    public sealed record Mir(
        MirSnapshotId Snapshot,
        MirOriginRef? Location = null) : TargetDiagnosticInput;
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
