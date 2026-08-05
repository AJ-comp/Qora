using Qora.Ir.Mir;

namespace Qora.Compiler;

/// <summary>Options that affect one compilation snapshot.</summary>
public sealed record CompilationOptions
{
    public CompilationOptions(
        string? baseDirectory = null,
        string? sourcePath = null,
        CompilationOutputPlan? outputPlan = null)
    {
        BaseDirectory = baseDirectory;
        SourcePath = sourcePath;
        OutputPlan = outputPlan ?? CompilationOutputPlan.Default;
    }

    public string? BaseDirectory { get; }
    public string? SourcePath { get; }
    public CompilationOutputPlan OutputPlan { get; }
}

/// <summary>
/// The immutable owner of every stage produced for one source snapshot. Each subsystem retains its own
/// model and declares the exact artifact from which it was produced.
/// </summary>
public sealed class Compilation
{
    private readonly IReadOnlyList<CompilationDiagnostic> _diagnostics;

    internal Compilation(
        CompilationId id,
        CompilationRevision revision,
        CompilationSession session,
        CompilationRevision? parentRevision,
        CompilationOptions options,
        SourceSetSnapshot sources,
        HirCompilation hir,
        MirSnapshot? mir,
        TargetArtifactSet targets,
        IEnumerable<CompilationDiagnostic> diagnostics)
    {
        if (id.Value == Guid.Empty)
            throw new ArgumentException(
                "A Compilation requires a non-empty identity.",
                nameof(id));

        Id = id;
        Revision = revision;
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ParentRevision = parentRevision;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
        Hir = hir ?? throw new ArgumentNullException(nameof(hir));
        Mir = mir;
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = Array.AsReadOnly(diagnostics.ToArray());

        if (session.Id != id)
        {
            throw new ArgumentException(
                "Compilation identity does not belong to its revision-allocation session.",
                nameof(session));
        }
        if (parentRevision is { } parent && parent.Value >= revision.Value)
        {
            throw new ArgumentException(
                "A Compilation parent revision must precede its child revision.",
                nameof(parentRevision));
        }

        var outputPlan = Options.OutputPlan;
        if (mir is not null && !outputPlan.RequiresMir)
        {
            throw new ArgumentException(
                "Compilation carries an unsolicited MIR snapshot.",
                nameof(mir));
        }

        foreach (var backend in targets.Artifacts.Keys)
        {
            if (!outputPlan.Targets.Contains(backend))
            {
                throw new ArgumentException(
                    $"Compilation carries an unsolicited {backend} target artifact.",
                    nameof(targets));
            }
        }

        foreach (var artifact in targets.Artifacts.Values)
        {
            if (mir is null || !ReferenceEquals(artifact.Source, mir))
            {
                throw new ArgumentException(
                    $"{artifact.Backend} source is not this Compilation's canonical MIR snapshot.",
                    nameof(targets));
            }
        }

        if (_diagnostics.Count > 0 && targets.Artifacts.Count > 0)
        {
            throw new ArgumentException(
                "A failed Compilation cannot carry successful target artifacts.",
                nameof(targets));
        }

        if (_diagnostics.Count == 0)
        {
            VerifySuccessfulHirGoal(hir, outputPlan);

            if (outputPlan.RequiresMir != (mir is not null))
            {
                throw new ArgumentException(
                    outputPlan.RequiresMir
                        ? "A successful Compilation is missing its requested MIR snapshot."
                        : "A successful Compilation carries an unsolicited MIR snapshot.",
                    nameof(mir));
            }

            var producedTargets = targets.Artifacts.Keys;
            if (!outputPlan.Targets.SetEquals(producedTargets))
            {
                var missing = outputPlan.Targets.Except(producedTargets);
                var unsolicited = producedTargets.Except(outputPlan.Targets);
                throw new ArgumentException(
                    "A successful Compilation does not exactly match its requested target set. " +
                    $"Missing: [{string.Join(", ", missing)}]; " +
                    $"unsolicited: [{string.Join(", ", unsolicited)}].",
                    nameof(targets));
            }
        }

        if (sources.CompilationId != id
            || sources.CompilationRevision != revision)
        {
            throw new ArgumentException(
                "The source set belongs to a different Compilation snapshot.",
                nameof(sources));
        }

        foreach (var snapshot in hir.Snapshots)
        {
            if (snapshot.Id.CompilationId != id
                || snapshot.Id.CompilationRevision != revision)
            {
                throw new ArgumentException(
                    $"HIR snapshot {snapshot.Id} belongs to a different Compilation snapshot.",
                    nameof(hir));
            }
            if (!hir.Lineage.Contains(snapshot.Id))
                throw new ArgumentException(
                    $"HIR lineage does not contain snapshot {snapshot.Id}.",
                    nameof(hir));
            foreach (var span in snapshot.SourceMap.Spans.Values)
            {
                var document = sources.FindDocument(span.Document);
                if (document is null)
                    throw new ArgumentException(
                        $"HIR snapshot {snapshot.Id} contains a span from unknown source document " +
                        $"{span.Document}.",
                        nameof(hir));
                if (span.End > document.Text.Length)
                    throw new ArgumentException(
                        $"HIR snapshot {snapshot.Id} contains out-of-range source span {span}.",
                        nameof(hir));
            }
        }

        if (mir is not null)
        {
            if (!ReferenceEquals(
                    mir.HirArtifact,
                    hir.EffectAnalysis))
            {
                throw new ArgumentException(
                    "MIR must consume this Compilation's canonical final HIR effect artifact.",
                    nameof(mir));
            }
        }

        foreach (var diagnostic in _diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostic.Error);
            ArgumentNullException.ThrowIfNull(diagnostic.Origin);
            if (diagnostic.Error.Span is { } diagnosticSpan)
            {
                var diagnosticDocument = sources.FindDocument(diagnosticSpan.Document)
                    ?? throw new ArgumentException(
                        $"Diagnostic span names unknown source document {diagnosticSpan.Document}.",
                        nameof(diagnostics));
                if (diagnosticSpan.End > diagnosticDocument.Text.Length)
                    throw new ArgumentException(
                        $"Diagnostic contains out-of-range source span {diagnosticSpan}.",
                        nameof(diagnostics));
            }

            var validStageOrigin = diagnostic.Stage switch
            {
                CompilationStage.Syntax
                    or CompilationStage.ImportExpansion
                    or CompilationStage.HirPreflight =>
                    diagnostic.Origin is DiagnosticOrigin.Source,
                CompilationStage.HirResolution
                    or CompilationStage.HirValidation
                    or CompilationStage.HirAnalysis =>
                    diagnostic.Origin is DiagnosticOrigin.Hir,
                CompilationStage.MirLowering =>
                    diagnostic.Origin is DiagnosticOrigin.Hir
                    or DiagnosticOrigin.Mir,
                CompilationStage.MirAnalysis =>
                    diagnostic.Origin is DiagnosticOrigin.Mir,
                CompilationStage.OpenQasm =>
                    diagnostic.Origin is DiagnosticOrigin.Target,
                _ => false,
            };
            if (!validStageOrigin)
            {
                throw new ArgumentException(
                    $"Compilation stage {diagnostic.Stage} cannot own diagnostic origin " +
                    $"{diagnostic.Origin.GetType().Name}.",
                    nameof(diagnostics));
            }

            switch (diagnostic.Origin)
            {
                case DiagnosticOrigin.Source source:
                    if (!sources.Contains(source.Document))
                        throw new ArgumentException(
                            $"Diagnostic source {source.Document} is not present in this source set.",
                            nameof(diagnostics));
                    if (diagnostic.Error.Span is { } sourceSpan
                        && sourceSpan.Document != source.Document)
                    {
                        throw new ArgumentException(
                            $"Source diagnostic origin {source.Document} disagrees with its error span " +
                            $"{sourceSpan.Document}.",
                            nameof(diagnostics));
                    }
                    break;

                case DiagnosticOrigin.Hir source:
                    if (hir.Find(source.Snapshot) is null)
                        throw new ArgumentException(
                            $"Diagnostic source {source.Snapshot} is not present in this HIR history.",
                            nameof(diagnostics));
                    break;

                case DiagnosticOrigin.Mir source:
                    if (mir is null || !ReferenceEquals(source.Snapshot, mir))
                        throw new ArgumentException(
                            "Diagnostic source is not this Compilation's MIR snapshot.",
                            nameof(diagnostics));
                    break;

                case DiagnosticOrigin.Target source:
                    if (!Enum.IsDefined(source.Backend))
                    {
                        throw new ArgumentException(
                            $"Diagnostic target origin {source} is not owned by this Compilation.",
                            nameof(diagnostics));
                    }
                    if (!outputPlan.Targets.Contains(source.Backend))
                    {
                        throw new ArgumentException(
                            $"Diagnostic was produced by unsolicited target backend {source.Backend}.",
                            nameof(diagnostics));
                    }
                    if (mir is null || !ReferenceEquals(source.Input, mir))
                    {
                        throw new ArgumentException(
                            "Diagnostic target MIR input is not owned by this Compilation.",
                            nameof(diagnostics));
                    }
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown diagnostic origin type {diagnostic.Origin.GetType().Name}.",
                        nameof(diagnostics));
            }
        }

        VerifyValidationDiagnosticProjection(hir, _diagnostics);
    }

    public CompilationId Id { get; }
    public CompilationRevision Revision { get; }
    public CompilationSession Session { get; }
    public CompilationRevision? ParentRevision { get; }
    public CompilationOptions Options { get; }
    public SourceSetSnapshot Sources { get; }
    public HirCompilation Hir { get; }
    public MirSnapshot? Mir { get; }
    public TargetArtifactSet Targets { get; }
    public IReadOnlyList<CompilationDiagnostic> Diagnostics => _diagnostics;
    public bool Succeeded => Diagnostics.Count == 0;

    private static void VerifySuccessfulHirGoal(
        HirCompilation hir,
        CompilationOutputPlan outputPlan)
    {
        switch (outputPlan.HirGoal)
        {
            case HirCompilationGoal.EffectAnalyzedCanonical:
                _ = hir.Resolved
                    ?? throw new ArgumentException(
                        "A successful Compilation is missing resolved HIR.",
                        nameof(hir));
                var resolvedValidation = hir.ResolvedValidation
                    ?? throw new ArgumentException(
                        "A successful Compilation is missing resolved-HIR validation facts.",
                        nameof(hir));
                if (!resolvedValidation.IsAccepted)
                    throw new ArgumentException(
                        "A successful Compilation cannot use rejected resolved-HIR validation facts.",
                        nameof(hir));
                _ = hir.Specialized
                    ?? throw new ArgumentException(
                        "A successful Compilation is missing canonical specialized HIR.",
                        nameof(hir));
                var specializedValidation = hir.SpecializedValidation
                    ?? throw new ArgumentException(
                        "A successful Compilation is missing specialized-HIR validation facts.",
                        nameof(hir));
                if (!specializedValidation.IsAccepted)
                    throw new ArgumentException(
                        "A successful Compilation cannot use rejected specialized-HIR validation facts.",
                        nameof(hir));
                var effectAnalysis = hir.EffectAnalysis
                    ?? throw new ArgumentException(
                        "A successful Compilation is missing HIR effect-analysis facts.",
                        nameof(hir));
                if (!effectAnalysis.IsAccepted
                    || effectAnalysis.ValidationBasis != specializedValidation.Id)
                {
                    throw new ArgumentException(
                        "A successful Compilation requires effect analysis derived from its exact "
                        + "accepted specialized-HIR validation artifact.",
                        nameof(hir));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outputPlan),
                    outputPlan.HirGoal,
                    "unknown HIR compilation goal");
        }
    }

    private static void VerifyValidationDiagnosticProjection(
        HirCompilation hir,
        IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        var validationDiagnostics = diagnostics
            .Where(diagnostic =>
                diagnostic.Stage == CompilationStage.HirValidation
                && diagnostic.Origin is DiagnosticOrigin.Hir)
            .ToArray();

        foreach (var diagnostic in validationDiagnostics)
        {
            var source = ((DiagnosticOrigin.Hir)diagnostic.Origin).Snapshot;
            if (hir.FindSemantics(source, HirSemanticPhase.Validation) is null)
            {
                throw new ArgumentException(
                    $"HIR validation diagnostic for {source} has no exact validation artifact.",
                    nameof(diagnostics));
            }
        }

        foreach (var artifact in hir.SemanticArtifacts.Values
                     .Where(artifact => artifact.Phase == HirSemanticPhase.Validation))
        {
            var projected = validationDiagnostics
                .Where(diagnostic =>
                    ((DiagnosticOrigin.Hir)diagnostic.Origin).Snapshot == artifact.SourceId)
                .Select(diagnostic => diagnostic.Error)
                .ToArray();
            if (!artifact.Diagnostics.SequenceEqual(projected))
            {
                throw new ArgumentException(
                    $"Compilation diagnostics do not exactly project validation outcome "
                    + $"{artifact.Id}.",
                    nameof(diagnostics));
            }
        }
    }
}
