using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class CompilationArchitectureTests
{
    [Fact]
    public void ParserStopsAtSyntaxAndCompilerOwnsLaterStages()
    {
        var syntax = QoraParser.Parse("operation Main() {}");
        Assert.True(syntax.Succeeded);
        Assert.NotNull(syntax.Ast);

        var compilation = QoraCompiler.Compile("operation Main() {}");
        Assert.True(compilation.Succeeded);
        Assert.NotNull(compilation.Hir.Resolved);
        Assert.NotNull(compilation.Mir);
        Assert.NotNull(compilation.Targets.OpenQasm);
    }

    [Fact]
    public void EmptySourceIsARejectedCompilationNotASuccessWithoutArtifacts()
    {
        var compilation = QoraCompiler.Compile(string.Empty);

        Assert.False(compilation.Succeeded);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(CompilationStage.HirPreflight, diagnostic.Stage);
        Assert.Equal("QSEM040", diagnostic.Error.Code);
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);

        var hir = new HirTestFactory();
        var validation = QoraValidator.Validate(
            hir.PublishProgram(Array.Empty<HirCallable>()),
            out var model);
        Assert.Equal("QSEM040", Assert.Single(validation).Code);
        Assert.Null(model);
    }

    [Fact]
    public void AFunctionNamedMainCanNeverReplaceTheOperationEntryPoint()
    {
        var compilation = QoraCompiler.Compile(
            """
            function Main(): int {
                return 1;
            }

            operation Foo() {
            }
            """);

        Assert.True(compilation.Succeeded);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var entry = mir.Program.EntryPoint;
        Assert.Equal(MirCallableKind.Operation, entry.Kind);
        Assert.Equal("Foo", entry.Name);
    }

    [Fact]
    public void FunctionsWithoutAnyOperationAreAUserDiagnosticNotAnInternalMirFailure()
    {
        var compilation = QoraCompiler.Compile(
            """
            function Main(): int {
                return 1;
            }
            """);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics);
        Assert.Equal(CompilationStage.HirValidation, error.Stage);
        Assert.Equal("QSEM040", error.Error.Code);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Error.Code == "QORA0000");
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }

    [Fact]
    public void HirGenerationsAreChronologicalAndAnalysisDoesNotMutateValidationFacts()
    {
        var compilation = QoraCompiler.Compile(
            "function one(): int { return 1; }\n" +
            "operation Main() { var x: int = one(); }");
        Assert.True(compilation.Succeeded);

        var snapshots = compilation.Hir.Snapshots;
        Assert.NotEmpty(snapshots);
        Assert.Equal(
            Enumerable.Range(0, snapshots.Count),
            snapshots.Select(snapshot => snapshot.Id.Revision.Value));

        for (var index = 1; index < snapshots.Count; index++)
            Assert.Equal(snapshots[index - 1].Id, snapshots[index].Parent);

        var specialized = compilation.Hir.Require(HirStage.Specialized);
        var validated = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);
        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        Assert.Same(
            analyzed.Program,
            specialized.Program);
        Assert.Equal(specialized.Id, validated.SourceId);
        Assert.Equal(specialized.Id, analyzed.SourceId);
        Assert.NotSame(validated.Model, analyzed.Model);

        var main = specialized.Program.Callables.Single(operation => operation.Name == "Main");
        Assert.Null(validated.Model.FindOpEffects(main.Id));
        Assert.NotNull(analyzed.Model.FindOpEffects(main.Id));

        Assert.Throws<InvalidOperationException>(
            () => EffectAnalysis.Run(analyzed.Program, analyzed.Model));
    }

    [Fact]
    public void SemanticOperationsRequireExactBuilderOwnedArtifacts()
    {
        var firstHir = new HirTestFactory();
        var firstProgram = firstHir.PublishProgram(
            new[] { firstHir.Callable("Main") });
        var secondHir = new HirTestFactory();
        var secondProgram = secondHir.PublishProgram(
            new[] { secondHir.Callable("Main") });
        var first = firstHir.CreatePipelineBuilder();
        var second = secondHir.CreatePipelineBuilder();
        var firstSnapshot = first.PublishLowered(firstProgram);
        var secondSnapshot = second.PublishLowered(secondProgram);

        Assert.Throws<ArgumentException>(
            () => first.ValidateSnapshot(secondSnapshot));

        first.Alias(HirStage.ImportsExpanded, firstSnapshot);
        first.Alias(HirStage.Resolved, firstSnapshot);
        first.Alias(HirStage.Specialized, firstSnapshot);
        var validationArtifact = first.ValidateSnapshot(firstSnapshot);
        Assert.True(validationArtifact.IsAccepted);
        Assert.Equal(firstSnapshot.Id, validationArtifact.SourceId);
        Assert.Throws<InvalidOperationException>(
            () => first.ValidateSnapshot(firstSnapshot));

        Assert.Throws<ArgumentException>(
            () => second.AnalyzeEffects(validationArtifact));

        var analyzed = first.AnalyzeEffects(validationArtifact);
        Assert.Equal(HirSemanticPhase.EffectAnalysis, analyzed.Phase);
        Assert.Equal(firstSnapshot.Id, analyzed.SourceId);
        Assert.Throws<InvalidOperationException>(
            () => first.AnalyzeEffects(validationArtifact));
        Assert.Throws<InvalidOperationException>(
            () => first.AnalyzeEffects(analyzed));
    }

    [Fact]
    public void RecompileKeepsLogicalIdentityAndAdvancesEverySnapshotRevision()
    {
        var first = QoraCompiler.Compile("operation Main() {}");
        var second = QoraCompiler.Recompile(
            first,
            "operation Main() { var x: int = 1; }");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Revision.Value + 1, second.Revision.Value);
        Assert.Equal(first.Revision, second.ParentRevision);
        Assert.Same(first.Session, second.Session);
        Assert.All(
            second.Hir.Snapshots,
            snapshot =>
            {
                Assert.Equal(second.Id, snapshot.Id.CompilationId);
                Assert.Equal(second.Revision, snapshot.Id.CompilationRevision);
            });
        Assert.Same(
            second.Hir.EffectAnalysis,
            Assert.IsType<MirSnapshot>(second.Mir).HirArtifact);
        Assert.Equal(
            first.Sources.Entry.DocumentId,
            second.Sources.Entry.DocumentId);
        Assert.NotEqual(first.Sources.Entry, second.Sources.Entry);
        Assert.False(second.Sources.Contains(first.Sources.Entry));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => second.Sources.Imports.Outgoing(first.Sources.Entry));
    }

    [Fact]
    public void RecompileBranchesReceiveDistinctSessionIssuedRevisions()
    {
        var root = QoraCompiler.Compile("operation Main() {}");

        var left = QoraCompiler.Recompile(
            root,
            "operation Main() { var value: int = 1; }");
        var right = QoraCompiler.Recompile(
            root,
            "operation Main() { var value: int = 2; }");

        Assert.Equal(root.Id, left.Id);
        Assert.Equal(root.Id, right.Id);
        Assert.NotEqual(left.Revision, right.Revision);
        Assert.Equal(root.Revision, left.ParentRevision);
        Assert.Equal(root.Revision, right.ParentRevision);
        Assert.NotEqual(left.Sources.Entry, right.Sources.Entry);
        Assert.NotSame(left.Mir, right.Mir);
        Assert.NotEqual(
            left.Hir.Snapshots.Select(snapshot => snapshot.Id),
            right.Hir.Snapshots.Select(snapshot => snapshot.Id));
    }

    [Fact]
    public void HirCompilationRejectsALineageBuiltFromDetachedSnapshotObjects()
    {
        var compilation = QoraCompiler.Compile("operation Main() {}");
        Assert.True(compilation.Succeeded);

        var detachedSnapshots = compilation.Hir.Snapshots
            .Select(snapshot => new HirSnapshot(
                snapshot.Id,
                snapshot.ProducedBy,
                snapshot.Program,
                snapshot.Parent))
            .ToArray();
        var detachedLineage = new HirLineage(
            detachedSnapshots,
            Array.Empty<(
                HirSnapshotId Source,
                HirSnapshotId Target,
                NodeDerivation Derivation)>());
        var milestones = compilation.Hir.Milestones.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Id);

        var error = Assert.Throws<ArgumentException>(
            () => new HirCompilation(
                compilation.Hir.Snapshots,
                milestones,
                compilation.Hir.SemanticArtifacts.Values,
                detachedLineage));
        Assert.Contains("detached", error.Message);
    }

    [Fact]
    public void ConstructionAuthorityNeverIssuesOneIdentityToDifferentNodeKinds()
    {
        var hir = new HirTestFactory();
        var call = hir.Apply("X");
        var callable = hir.Callable(
            "Main",
            body: new HirStatement[] { call });
        var program = hir.PublishProgram(new[] { callable });
        var builder = hir.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(program);

        Assert.NotEqual(program.Id, callable.Id);
        Assert.NotEqual(callable.Id, call.Id);
        Assert.NotEqual(program.Id, call.Id);
        Assert.Null(snapshot.Structure.OwningCallableOf(program.Id));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => snapshot.Structure.RequireOwningCallable(program.Id));
        Assert.Equal(
            callable.Id,
            snapshot.Structure.RequireOwningCallable(call.Id));
    }

    [Fact]
    public void ImplicitSameIdLineageRejectsAChangedOwningCallable()
    {
        var hir = new HirTestFactory();
        var statement = hir.Variable(
            "value",
            hir.Integer(1),
            QType.Int);
        var parentProgram = hir.PublishProgram(
            new[]
            {
                hir.Callable(
                    "First",
                    body: new HirStatement[] { statement }),
                hir.Callable("Second"),
            });
        var builder = hir.CreatePipelineBuilder();
        var parent = builder.PublishLowered(parentProgram);
        var moved = hir.MoveFirstStatementToSecondCallable(parent);
        _ = builder.Advance(
            HirStage.ImportsExpanded,
            moved);

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("owning callable", error.Message);
    }

    [Fact]
    public void ImplicitSameIdLineageRejectsAChangedStructuralParent()
    {
        var hir = new HirTestFactory();
        var statement = hir.Variable(
            "value",
            hir.Integer(1),
            QType.Int);
        var branch = hir.If(
            hir.Literal("true"),
            Array.Empty<HirStatement>());
        var program = hir.PublishProgram(
            new[]
            {
                hir.Callable(
                    "Main",
                    body: new HirStatement[]
                    {
                        statement,
                        branch,
                    }),
            });
        var builder = hir.CreatePipelineBuilder();
        var parent = builder.PublishLowered(program);
        var moved = hir.MoveFirstStatementIntoFollowingIf(parent);
        _ = builder.Advance(
            HirStage.ImportsExpanded,
            moved);

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("structural parent", error.Message);
    }

    [Fact]
    public void ImplicitSameIdLineageRejectsAChangedSourceSpan()
    {
        var hir = new HirTestFactory();
        var declaration = hir.Variable(
            "value",
            hir.Integer(1, spanStart: 0, spanEnd: 1),
            QType.Int);
        var program = hir.PublishProgram(
            new[]
            {
                hir.Callable(
                    "Main",
                    body: new HirStatement[] { declaration }),
            });
        var builder = hir.CreatePipelineBuilder();
        var parent = builder.PublishLowered(program);
        var changed = hir.ChangeFirstIntegerSourceSpan(
            parent,
            spanStart: 2,
            spanEnd: 3);
        _ = builder.Advance(
            HirStage.ImportsExpanded,
            changed);

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("source span", error.Message);
    }

    [Fact]
    public void PipelineRejectsARewriteQualifiedByAStaleSnapshot()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[] { hir.Callable("Main") });
        var builder = hir.CreatePipelineBuilder();
        var lowered = builder.PublishLowered(program);
        var staleRewrite = hir.RewriteProgramRoot(lowered);
        var current = builder.Advance(
            HirStage.ImportsExpanded,
            hir.RewriteProgramRoot(lowered));

        var error = Assert.Throws<ArgumentException>(
            () => builder.Advance(
                HirStage.Resolved,
                staleRewrite));

        Assert.Contains(staleRewrite.Source.ToString(), error.Message);
        Assert.Contains(current.Id.ToString(), error.Message);
    }

    [Fact]
    public void PipelineRejectsAliasingAStageToAnOlderExactSnapshot()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[] { hir.Callable("Main") });
        var builder = hir.CreatePipelineBuilder();
        var lowered = builder.PublishLowered(program);
        var current = builder.Advance(
            HirStage.ImportsExpanded,
            hir.RewriteProgramRoot(lowered));

        var error = Assert.Throws<ArgumentException>(
            () => builder.Alias(
                HirStage.Resolved,
                lowered));

        Assert.Contains("latest exact snapshot", error.Message);
        Assert.Same(
            current,
            builder.Alias(
                HirStage.Resolved,
                current));
    }

    [Fact]
    public void MirAndTargetDeclareTheirExactSources()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; H(q[0]); }");
        Assert.True(compilation.Succeeded);

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var target = Assert.IsType<OpenQasmArtifact>(compilation.Targets.OpenQasm);
        Assert.Same(analyzed, mir.HirArtifact);
        Assert.Same(mir, target.Source);
    }

    [Fact]
    public void CoreArtifactsDoNotRetainLanguageServiceIndexes()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { var x: int = 1; }");
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);

        Assert.Null(typeof(Compilation).GetProperty("Links"));
        Assert.Null(typeof(MirSnapshot).GetProperty("Links"));
        Assert.DoesNotContain(
            typeof(Compilation).GetProperties(),
            property => property.PropertyType.Name.Contains(
                "SemanticIndex",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(MirSnapshot).GetProperties(),
            property => property.PropertyType.Name.Contains(
                "SemanticIndex",
                StringComparison.Ordinal));
        Assert.Same(
            compilation.Hir.EffectAnalysis,
            mir.HirArtifact);
    }

    [Fact]
    public void MirAnalysisStoreReturnsOneCanonicalDependencyInstance()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; H(q[0]); }");
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var callable = Assert.Single(mir.Program.Callables);

        Assert.Same(
            mir.Analyses.ControlFlow(callable),
            mir.Analyses.ControlFlow(callable.Id));
        Assert.Same(
            mir.Analyses.MemoryState(callable),
            mir.Analyses.MemoryState(callable.Id));
        Assert.Same(mir.Analyses.CallGraph, mir.Analyses.CallGraph);
        Assert.Same(mir.Analyses.Effects, mir.Analyses.Effects);
    }

    [Fact]
    public void RejectedStageKeepsItsOwnSemanticFactsWithoutTargetArtifact()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { var xs: int[] = [1]; var x: int = xs[2]; }");
        Assert.False(compilation.Succeeded);

        var rejected = compilation.Hir.Require(HirStage.Resolved);
        Assert.Equal(
            rejected.Id,
            Assert.IsType<HirSemanticArtifact>(
                compilation.Hir.ResolvedValidation).SourceId);
        Assert.Null(compilation.Hir.EffectAnalysis);
        Assert.Null(compilation.Mir);
        Assert.Null(compilation.Targets.OpenQasm);
    }

    [Fact]
    public void TargetArtifactSetUsesOneFrozenBackendMap()
    {
        var compilation = QoraCompiler.Compile("operation Main() { }");
        var openQasm = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var input = new List<ITargetArtifact> { openQasm };

        var targets = new TargetArtifactSet(input);
        input.Clear();

        Assert.Single(targets.Artifacts);
        Assert.Same(
            openQasm,
            targets.Artifacts[TargetBackend.OpenQasm]);
        Assert.Same(openQasm, targets.Find(TargetBackend.OpenQasm));
        Assert.Same(openQasm, targets.OpenQasm);

        Assert.Throws<ArgumentException>(
            () => new TargetArtifactSet(
                new ITargetArtifact[] { openQasm, openQasm }));
        Assert.Throws<ArgumentException>(
            () => new TargetArtifactSet(
                new ITargetArtifact[]
                {
                    new StubTargetArtifact(
                        (TargetBackend)int.MaxValue,
                        openQasm.Source),
                }));
        Assert.Throws<ArgumentException>(
            () => new TargetArtifactSet(
                new ITargetArtifact[]
                {
                    new StubTargetArtifact(
                        TargetBackend.OpenQasm,
                        openQasm.Source),
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => targets.Find((TargetBackend)int.MaxValue));
    }

    [Fact]
    public void CompilationRejectsMirOwnedByAnotherHirArtifact()
    {
        var mirOnly = new CompilationOutputPlan(
            produceMir: true,
            Array.Empty<TargetBackend>());
        var options = new CompilationOptions(outputPlan: mirOnly);
        var source = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }",
            options);
        var unrelated = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }",
            options);
        var unrelatedMir = Assert.IsType<MirSnapshot>(unrelated.Mir);

        Assert.Throws<ArgumentException>(
            () => new Compilation(
                source.Id,
                source.Revision,
                source.Session,
                source.ParentRevision,
                source.Options,
                source.Sources,
                source.Hir,
                unrelatedMir,
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                Array.Empty<CompilationDiagnostic>()));
    }

    [Fact]
    public void CompilationRejectsTargetArtifactProducedFromAnotherMir()
    {
        var source = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        var unrelated = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        var unrelatedArtifact = Assert.IsType<OpenQasmArtifact>(
            unrelated.Targets.OpenQasm);

        Assert.Throws<ArgumentException>(
            () => new Compilation(
                source.Id,
                source.Revision,
                source.Session,
                source.ParentRevision,
                source.Options,
                source.Sources,
                source.Hir,
                source.Mir,
                new TargetArtifactSet(new[] { unrelatedArtifact }),
                Array.Empty<CompilationDiagnostic>()));
    }

    [Fact]
    public void MirDiagnosticsRequireTheExactCanonicalMirSnapshot()
    {
        var mirOnly = new CompilationOutputPlan(
            produceMir: true,
            Array.Empty<TargetBackend>());
        var options = new CompilationOptions(outputPlan: mirOnly);
        var source = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }",
            options);
        var unrelated = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }",
            options);
        var sourceMir = Assert.IsType<MirSnapshot>(source.Mir);
        var unrelatedMir = Assert.IsType<MirSnapshot>(unrelated.Mir);

        Assert.Throws<ArgumentException>(
            () => new Compilation(
                source.Id,
                source.Revision,
                source.Session,
                source.ParentRevision,
                source.Options,
                source.Sources,
                source.Hir,
                sourceMir,
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                new[]
                {
                    new CompilationDiagnostic(
                        CompilationStage.MirLowering,
                        new QoraError("MIR diagnostic", "QTEST"),
                        new DiagnosticOrigin.Mir(unrelatedMir)),
                }));
    }

    [Fact]
    public void TargetDiagnosticsRequireTheExactCanonicalMirInput()
    {
        var source = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        Assert.True(source.Succeeded);
        var mir = Assert.IsType<MirSnapshot>(source.Mir);

        Compilation WithTargetDiagnostic(MirSnapshot input) =>
            new(
                source.Id,
                source.Revision,
                source.Session,
                source.ParentRevision,
                source.Options,
                source.Sources,
                source.Hir,
                mir,
                new TargetArtifactSet(Array.Empty<ITargetArtifact>()),
                new[]
                {
                    new CompilationDiagnostic(
                        CompilationStage.OpenQasm,
                        new QoraError("target diagnostic", "QTEST"),
                        new DiagnosticOrigin.Target(
                            TargetBackend.OpenQasm,
                            input)),
                });

        var fromMir = WithTargetDiagnostic(mir);
        Assert.False(fromMir.Succeeded);

        var unrelated = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        var staleMir = Assert.IsType<MirSnapshot>(unrelated.Mir);
        Assert.Throws<ArgumentException>(
            () => WithTargetDiagnostic(staleMir));
    }

    private sealed record StubTargetArtifact(
        TargetBackend Backend,
        MirSnapshot Source) : ITargetArtifact;
}
