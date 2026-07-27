using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class SemanticArtifactOwnershipTests
{
    [Fact]
    public void SemanticModelCannotBeAttachedToAnotherExactHirSnapshot()
    {
        var first = QoraCompiler.Compile(
            "operation Main() { var first: int = 1; }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        var second = QoraCompiler.Compile(
            "operation Main() { var second: float = 2.0; }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        var firstResolved = Assert.IsType<HirSnapshot>(first.Hir.Resolved);
        var secondModel = Assert.IsType<HirSemanticArtifact>(
            second.Hir.ResolvedValidation).Model;

        var error = Assert.Throws<ArgumentException>(
            () => new HirSemanticArtifact(
                firstResolved,
                secondModel));

        Assert.Contains("exact snapshot and phase", error.Message);
    }

    [Fact]
    public void SemanticModelCannotBeRepublishedAsAnotherPhase()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { var value: int = 1; }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(compilation.Succeeded);

        var specialized = Assert.IsType<HirSnapshot>(compilation.Hir.Specialized);
        var effectModel = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis).Model;

        var error = Assert.Throws<ArgumentException>(
            () => new HirSemanticArtifact(
                specialized,
                effectModel));

        Assert.Contains("exact snapshot and phase", error.Message);
    }

    [Fact]
    public void SnapshotBoundModelCannotBePublishedBeforeItsPhaseIsSealed()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() {}",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(compilation.Succeeded);
        var resolved = Assert.IsType<HirSnapshot>(compilation.Hir.Resolved);
        var unfinished = new HirSemanticModel(resolved);

        var error = Assert.Throws<ArgumentException>(
            () => new HirSemanticArtifact(
                resolved,
                unfinished));

        Assert.Contains("complete and seal", error.Message);
    }

    [Fact]
    public void HirLoweringContextRejectsARegisteredValidationArtifact()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { var value: int = 1; }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(compilation.Succeeded);

        var current = Assert.IsType<HirSnapshot>(
            compilation.Hir.Specialized);
        var validation = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);

        var error = Assert.Throws<ArgumentException>(
            () => new HirSemanticContext(
                compilation.Hir,
                current,
                validation));

        Assert.Contains("exact accepted canonical effect-analysis", error.Message);
    }

    [Fact]
    public void HirLoweringContextRejectsAnOlderRegisteredEffectArtifact()
    {
        var builder = new HirPipelineBuilder(
            CompilationId.New(),
            new CompilationRevision(0));
        var program = new QProgram(
            new[]
            {
                new QOperation(
                    "Main",
                    Array.Empty<QParam>(),
                    Array.Empty<QStmt>()),
            });

        var resolved = builder.Advance(HirStage.Resolved, program);
        var resolvedValidation = builder.ValidateSnapshot(resolved);
        var oldEffect = builder.AnalyzeEffects(resolvedValidation);
        var specialized = builder.Advance(
            HirStage.Specialized,
            program with { Operations = program.Operations });
        var specializedValidation = builder.ValidateSnapshot(specialized);
        _ = builder.AnalyzeEffects(specializedValidation);
        var owner = builder.Build();

        var error = Assert.Throws<ArgumentException>(
            () => new HirSemanticContext(
                owner,
                specialized,
                oldEffect));

        Assert.Contains("exact accepted canonical effect-analysis", error.Message);
    }

    [Fact]
    public void CanonicalHirMilestonesCannotRunBackward()
    {
        var builder = new HirPipelineBuilder(
            CompilationId.New(),
            new CompilationRevision(0));
        var program = new QProgram(
            new[]
            {
                new QOperation(
                    "Main",
                    Array.Empty<QParam>(),
                    Array.Empty<QStmt>()),
            });

        _ = builder.Advance(HirStage.Resolved, program);
        _ = builder.Advance(
            HirStage.Lowered,
            program with { Operations = program.Operations });

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("precedes canonical predecessor", error.Message);
    }

    [Fact]
    public void EffectArtifactCannotBeSealedBeforeTheWholeEffectPassCompletes()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(compilation.Succeeded);

        var validation = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);
        var unfinishedEffects = validation.Model.ForkForEffectAnalysis();

        var error = Assert.Throws<InvalidOperationException>(
            unfinishedEffects.SealEffectAnalysisArtifact);

        Assert.Contains("coherent summary", error.Message);
    }

    [Fact]
    public void EffectArtifactKeepsItsExactAcceptedValidationBasis()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() {}",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(compilation.Succeeded);

        var validation = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);
        var effects = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);

        Assert.True(validation.IsAccepted);
        Assert.Equal(validation.Id, effects.ValidationBasis);
        Assert.Same(validation, effects.ValidationBasisArtifact);
    }

    [Fact]
    public void RejectedValidationCannotStartEffectAnalysis()
    {
        var builder = new HirPipelineBuilder(
            CompilationId.New(),
            new CompilationRevision(0));
        var snapshot = builder.Advance(
            HirStage.Resolved,
            InvalidMainWithParameter());
        var rejected = builder.ValidateSnapshot(snapshot);

        Assert.False(rejected.IsAccepted);
        Assert.Equal(HirValidationStatus.Rejected, rejected.ValidationOutcome.Status);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "QSEM010");

        var error = Assert.Throws<ArgumentException>(
            () => builder.AnalyzeEffects(rejected));
        Assert.Contains("accepted validation", error.Message);
        Assert.Throws<InvalidOperationException>(
            rejected.Model.ForkForEffectAnalysis);
    }

    [Fact]
    public void RejectedValidationDiagnosticsCannotBeDiscardedFromCompilation()
    {
        var shell = QoraCompiler.Compile(
            "operation Main() {}",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(shell.Succeeded);

        var builder = new HirPipelineBuilder(shell.Id, shell.Revision);
        var snapshot = builder.Advance(
            HirStage.Lowered,
            InvalidMainWithParameter());
        builder.Alias(HirStage.ImportsExpanded, snapshot);
        builder.Alias(HirStage.MeasurementLowered, snapshot);
        builder.Alias(HirStage.Resolved, snapshot);
        var rejected = builder.ValidateSnapshot(snapshot);
        builder.Alias(HirStage.Specialized, snapshot);
        var hir = builder.Build();

        Assert.False(rejected.IsAccepted);
        var decoy = new CompilationDiagnostic(
            CompilationStage.HirPreflight,
            new QoraError("decoy failure", "QTEST"),
            new DiagnosticOrigin.Source(shell.Sources.Entry));

        var error = Assert.Throws<ArgumentException>(
            () => new Compilation(
                shell.Id,
                shell.Revision,
                shell.Session,
                shell.ParentRevision,
                shell.Options,
                shell.Sources,
                hir,
                mir: null,
                new CrossStageLinks(hir, mir: null),
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                new[] { decoy }));

        Assert.Contains("exactly project validation outcome", error.Message);
    }

    [Fact]
    public void RejectedValidationCannotMasqueradeAsSuccessfulHirGoal()
    {
        var shell = QoraCompiler.Compile(
            "operation Main() {}",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(shell.Succeeded);

        var builder = new HirPipelineBuilder(shell.Id, shell.Revision);
        var snapshot = builder.Advance(
            HirStage.Lowered,
            InvalidMainWithParameter());
        builder.Alias(HirStage.ImportsExpanded, snapshot);
        builder.Alias(HirStage.MeasurementLowered, snapshot);
        builder.Alias(HirStage.Resolved, snapshot);
        _ = builder.ValidateSnapshot(snapshot);
        builder.Alias(HirStage.Specialized, snapshot);
        var hir = builder.Build();

        var error = Assert.Throws<ArgumentException>(
            () => new Compilation(
                shell.Id,
                shell.Revision,
                shell.Session,
                shell.ParentRevision,
                shell.Options,
                shell.Sources,
                hir,
                mir: null,
                new CrossStageLinks(hir, mir: null),
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                Array.Empty<CompilationDiagnostic>()));

        Assert.Contains("rejected resolved-HIR validation", error.Message);
    }

    private static QProgram InvalidMainWithParameter() =>
        new(
            new[]
            {
                new QOperation(
                    "Main",
                    new[] { new QParam("value", QType.Int, null) },
                    Array.Empty<QStmt>()),
            });
}
