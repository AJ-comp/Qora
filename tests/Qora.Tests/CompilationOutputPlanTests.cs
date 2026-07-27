using Qora.Compiler;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class CompilationOutputPlanTests
{
    [Fact]
    public void PlanCopiesAndFreezesTheExactRequestedBackendSet()
    {
        var requested = new List<TargetBackend>
        {
            TargetBackend.OpenQasm,
            TargetBackend.OpenQasm,
        };
        var plan = new CompilationOutputPlan(
            produceMir: false,
            requested);

        requested.Clear();

        Assert.False(plan.ProduceMir);
        Assert.Equal(
            new[] { TargetBackend.OpenQasm },
            plan.Targets);
        Assert.True(plan.Requests(TargetBackend.OpenQasm));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CompilationOutputPlan(
                produceMir: false,
                new[] { (TargetBackend)int.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => plan.Requests((TargetBackend)int.MaxValue));
    }

    [Fact]
    public void OptionsAlwaysOwnAnExplicitImmutableOutputPlan()
    {
        var defaults = new CompilationOptions();

        Assert.Same(
            CompilationOutputPlan.Default,
            defaults.OutputPlan);
        Assert.True(defaults.OutputPlan.ProduceMir);
        Assert.Equal(
            new[] { TargetBackend.OpenQasm },
            defaults.OutputPlan.Targets);

        var hirOnly = new CompilationOptions(
            outputPlan: CompilationOutputPlan.HirOnly);
        Assert.Same(
            CompilationOutputPlan.HirOnly,
            hirOnly.OutputPlan);
        Assert.False(hirOnly.OutputPlan.ProduceMir);
        Assert.Empty(hirOnly.OutputPlan.Targets);
    }

    [Fact]
    public void CompilerBuildsExactlyTheRequestedOutputKinds()
    {
        var source = "operation Main() { use q = Qubit[1]; H(q[0]); }";

        var hirOnly = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(hirOnly.Succeeded);
        Assert.NotNull(hirOnly.Hir.EffectAnalysis);
        Assert.NotNull(hirOnly.Hir.ConjugationLowered);
        Assert.Null(hirOnly.Mir);
        Assert.Empty(hirOnly.Targets.Artifacts);

        var mirOnly = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));
        Assert.True(mirOnly.Succeeded);
        Assert.NotNull(mirOnly.Mir);
        Assert.Empty(mirOnly.Targets.Artifacts);
        Assert.Null(mirOnly.Hir.AdjointMaterialized);

        var openQasmOnly = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: false,
                    new[] { TargetBackend.OpenQasm })));
        Assert.True(openQasmOnly.Succeeded);
        Assert.Null(openQasmOnly.Mir);
        Assert.NotNull(openQasmOnly.Targets.OpenQasm);
        Assert.NotNull(openQasmOnly.Hir.AdjointMaterialized);
    }

    [Fact]
    public void SuccessfulCompilationCannotPublishAnEmptyHirHistory()
    {
        var source = QoraCompiler.Compile("operation Main() { }");
        var builder = new HirPipelineBuilder(source.Id, source.Revision);
        var emptyHir = builder.Build();

        var error = Assert.Throws<ArgumentException>(
            () => new Compilation(
                source.Id,
                source.Revision,
                source.Session,
                source.ParentRevision,
                new CompilationOptions(
                    outputPlan: CompilationOutputPlan.HirOnly),
                source.Sources,
                emptyHir,
                null,
                new CrossStageLinks(emptyHir, null),
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                Array.Empty<CompilationDiagnostic>()));
        Assert.Contains("resolved HIR", error.Message);
    }

    [Fact]
    public void SuccessfulCompilationMustExactlyMatchItsRequestedOutputs()
    {
        var source = QoraCompiler.Compile("operation Main() { }");
        Assert.True(source.Succeeded);

        var hirOnly = Reassemble(
            source,
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly),
            mir: null,
            new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
            Array.Empty<CompilationDiagnostic>());
        Assert.True(hirOnly.Succeeded);
        Assert.Null(hirOnly.Mir);
        Assert.Empty(hirOnly.Targets.Artifacts);

        Assert.Throws<ArgumentException>(
            () => Reassemble(
                source,
                new CompilationOptions(
                    outputPlan: CompilationOutputPlan.HirOnly),
                source.Mir,
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                Array.Empty<CompilationDiagnostic>()));

        Assert.Throws<ArgumentException>(
            () => Reassemble(
                source,
                new CompilationOptions(
                    outputPlan: new CompilationOutputPlan(
                        produceMir: true,
                        Array.Empty<TargetBackend>())),
                source.Mir,
                source.Targets,
                Array.Empty<CompilationDiagnostic>()));

        Assert.Throws<ArgumentException>(
            () => Reassemble(
                source,
                new CompilationOptions(),
                source.Mir,
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                Array.Empty<CompilationDiagnostic>()));
    }

    [Fact]
    public void FailedCompilationMayRetainOnlyRequestedPartialMir()
    {
        var source = QoraCompiler.Compile("operation Main() { }");
        var mir = Assert.IsType<MirSnapshot>(source.Mir);
        var diagnostic = new CompilationDiagnostic(
            CompilationStage.HirAnalysis,
            new QoraError("adversarial failure", "QTEST"),
            new DiagnosticOrigin.Hir(
                source.Hir.Specialized!.Id));

        var partial = Reassemble(
            source,
            new CompilationOptions(),
            mir,
            new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
            new[] { diagnostic });

        Assert.False(partial.Succeeded);
        Assert.Same(mir, partial.Mir);
        Assert.Empty(partial.Targets.Artifacts);

        Assert.Throws<ArgumentException>(
            () => Reassemble(
                source,
                new CompilationOptions(
                    outputPlan: CompilationOutputPlan.HirOnly),
                mir,
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                new[] { diagnostic }));

        Assert.Throws<ArgumentException>(
            () => Reassemble(
                source,
                new CompilationOptions(),
                mir,
                source.Targets,
                new[] { diagnostic }));
    }

    private static Compilation Reassemble(
        Compilation source,
        CompilationOptions options,
        MirSnapshot? mir,
        TargetArtifactSet targets,
        IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        var links = new CrossStageLinks(
            source.Hir,
            mir?.Links);
        return new Compilation(
            source.Id,
            source.Revision,
            source.Session,
            source.ParentRevision,
            options,
            source.Sources,
            source.Hir,
            mir,
            links,
            targets,
            diagnostics);
    }
}
