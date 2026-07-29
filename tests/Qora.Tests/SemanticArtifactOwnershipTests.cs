using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class SemanticArtifactOwnershipTests
{
    [Fact]
    public void HirToMirConsumesOnlyTheCanonicalFinalEffectArtifact()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { var value: int = 1; value = value + 1; }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(compilation.Succeeded);
        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);

        var mir = compilation.Hir.ToMir();

        Assert.Equal(analyzed.SourceId, mir.LoweredFrom);
        Assert.Same(analyzed.Source, mir.LoweringSource.Source);
        Assert.Same(analyzed, mir.LoweringSource);
        Assert.Equal(0, mir.Id.Revision);
        Assert.Equal(MirStage.Lowered, mir.Stage);
        Assert.Null(mir.Parent);
        Assert.Single(mir.Program.Callables);
    }

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
    public void HirToMirRejectsACompilationWithoutFinalEffectAnalysis()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[] { hir.Callable("Main") });
        var builder = hir.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(program);
        builder.Alias(HirStage.ImportsExpanded, snapshot);
        builder.Alias(HirStage.Resolved, snapshot);
        _ = builder.ValidateSnapshot(snapshot);
        builder.Alias(HirStage.Specialized, snapshot);
        var compilation = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => compilation.ToMir());

        Assert.Contains("final effect-analyzed HIR", error.Message);
    }

    [Fact]
    public void EffectAnalysisAndMirLoweringRequireTheFinalSpecializedHir()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[] { hir.Callable("Main") });
        var builder = hir.CreatePipelineBuilder();
        var resolved = builder.PublishLowered(program);
        builder.Alias(HirStage.ImportsExpanded, resolved);
        builder.Alias(HirStage.Resolved, resolved);
        var resolvedValidation = builder.ValidateSnapshot(resolved);
        var premature = Assert.Throws<InvalidOperationException>(
            () => builder.AnalyzeEffects(resolvedValidation));
        Assert.Contains("latest canonical specialized HIR", premature.Message);

        var specialization = hir.RewriteProgramRoot(resolved);
        var specialized = builder.Advance(
            HirStage.Specialized,
            specialization);
        var specializedValidation = builder.ValidateSnapshot(specialized);
        var finalEffect = builder.AnalyzeEffects(specializedValidation);
        var owner = builder.Build();

        var mir = owner.ToMir();

        Assert.Equal(finalEffect.SourceId, mir.LoweredFrom);
        Assert.Same(finalEffect.Source, mir.LoweringSource.Source);
        Assert.Same(finalEffect, mir.LoweringSource);
    }

    [Fact]
    public void FinalEffectAnalysisSealsTheHirPipeline()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[] { hir.Callable("Main") });
        var builder = hir.CreatePipelineBuilder();
        var specialized = builder.PublishLowered(program);
        builder.Alias(HirStage.ImportsExpanded, specialized);
        builder.Alias(HirStage.Resolved, specialized);
        builder.Alias(HirStage.Specialized, specialized);
        var validation = builder.ValidateSnapshot(specialized);
        var unpublishedRewrite = hir.RewriteProgramRoot(specialized);

        var finalEffect = builder.AnalyzeEffects(validation);
        var owner = builder.Build();

        Assert.Same(finalEffect, owner.EffectAnalysis);
        Assert.Same(finalEffect.Source, owner.ToMir().LoweringSource.Source);

        var rewriteError = Assert.Throws<InvalidOperationException>(
            () => builder.BeginRewrite(specialized, "late-pass"));
        var advanceError = Assert.Throws<InvalidOperationException>(
            () => builder.Advance(HirStage.Specialized, unpublishedRewrite));
        var aliasError = Assert.Throws<InvalidOperationException>(
            () => builder.Alias(HirStage.Specialized, specialized));
        var validationError = Assert.Throws<InvalidOperationException>(
            () => builder.ValidateSnapshot(specialized));

        Assert.Contains("sealed after final effect analysis", rewriteError.Message);
        Assert.Contains("sealed after final effect analysis", advanceError.Message);
        Assert.Contains("sealed after final effect analysis", aliasError.Message);
        Assert.Contains("sealed after final effect analysis", validationError.Message);
    }

    [Fact]
    public void CanonicalHirMilestonesCannotRunBackward()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[] { hir.Callable("Main") });
        var builder = hir.CreatePipelineBuilder();
        var lowered = builder.PublishLowered(program);
        builder.Alias(HirStage.Resolved, lowered);
        _ = builder.Advance(
            HirStage.ImportsExpanded,
            hir.RewriteProgramRoot(lowered));

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
        var hir = new HirTestFactory();
        var invalid = InvalidMainWithParameter(hir);
        var builder = hir.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(invalid);
        builder.Alias(HirStage.Resolved, snapshot);
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

        var hirFactory = new HirTestFactory(shell.Sources.Entry);
        var invalid = InvalidMainWithParameter(hirFactory);
        var builder = hirFactory.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(invalid);
        builder.Alias(HirStage.ImportsExpanded, snapshot);
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

        var hirFactory = new HirTestFactory(shell.Sources.Entry);
        var invalid = InvalidMainWithParameter(hirFactory);
        var builder = hirFactory.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(invalid);
        builder.Alias(HirStage.ImportsExpanded, snapshot);
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
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                Array.Empty<CompilationDiagnostic>()));

        Assert.Contains("rejected resolved-HIR validation", error.Message);
    }

    private static HirProgram InvalidMainWithParameter(
        HirTestFactory hir) =>
        hir.PublishProgram(
            new[]
            {
                hir.Callable(
                    "Main",
                    parameters: new[]
                    {
                        hir.Parameter("value", QType.Int),
                    }),
            });
}
