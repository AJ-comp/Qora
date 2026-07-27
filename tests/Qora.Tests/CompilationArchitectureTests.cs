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

        var validation = QoraValidator.Validate(
            new QProgram(Array.Empty<QOperation>()),
            out var model);
        Assert.Equal("QSEM040", Assert.Single(validation).Code);
        Assert.Null(model);
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

        var main = specialized.Program.Operations.Single(operation => operation.Name == "Main");
        Assert.False(validated.Model.WasEffectAnalyzed(main.Id));
        Assert.True(analyzed.Model.WasEffectAnalyzed(main.Id));

        Assert.Throws<InvalidOperationException>(
            () => validated.Model.AddUnprovenIndex(
                new UnprovenIndex(
                    main.Name,
                    "xs",
                    "i",
                    LoopBound: null,
                    Span: null)));
        Assert.Throws<InvalidOperationException>(
            () => EffectAnalysis.Run(analyzed.Program, analyzed.Model));
    }

    [Fact]
    public void SemanticOperationsRequireExactBuilderOwnedArtifacts()
    {
        var program = new QProgram(
            new[]
            {
                new QOperation(
                    "Main",
                    Array.Empty<QParam>(),
                    Array.Empty<QStmt>()),
            });
        var revision = new CompilationRevision(0);
        var first = new HirPipelineBuilder(CompilationId.New(), revision);
        var second = new HirPipelineBuilder(CompilationId.New(), revision);
        var firstSnapshot = first.Advance(HirStage.Resolved, program);
        var secondSnapshot = second.Advance(HirStage.Resolved, program);

        Assert.Throws<ArgumentException>(
            () => first.ValidateSnapshot(secondSnapshot));

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
        Assert.Throws<ArgumentException>(
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
        Assert.Equal(second.Revision, second.Mir!.Id.CompilationRevision);
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
        Assert.NotEqual(left.Mir!.Id, right.Mir!.Id);
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
    public void ImplicitSameIdLineageRejectsAChangedNodeKind()
    {
        var sharedId = QNodeIds.Next();
        var parentProgram = new QProgram(
            new[]
            {
                new QOperation(
                    "Before",
                    Array.Empty<QParam>(),
                    Array.Empty<QStmt>())
                {
                    Id = sharedId,
                },
            });
        var targetOperation = new QOperation(
            "After",
            Array.Empty<QParam>(),
            new QStmt[]
            {
                new QGate(
                    Array.Empty<string>(),
                    "X",
                    Array.Empty<QArg>())
                {
                    Id = sharedId,
                },
            });
        var targetProgram = new QProgram(
            new[] { targetOperation })
        {
            Id = parentProgram.Id,
        };
        var builder = new HirPipelineBuilder(
            CompilationId.New(),
            new CompilationRevision(0));
        var parent = builder.Advance(HirStage.Lowered, parentProgram);
        var target = builder.Advance(
            HirStage.MeasurementLowered,
            targetProgram,
            derivations: new[]
            {
                new NodeDerivation(sharedId, targetOperation.Id),
            });
        Assert.Null(parent.Structure.OwningOperationOf(parentProgram.Id));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => parent.Structure.RequireOwningOperation(parentProgram.Id));

        var error = Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.Contains("changes kind", error.Message);
    }

    [Fact]
    public void ImplicitSameIdLineageRejectsAChangedOwningOperation()
    {
        var statement = new QDecl(
            false,
            QType.Int,
            "value",
            new QText(new QNumLit(1)));
        var first = new QOperation(
            "First",
            Array.Empty<QParam>(),
            new QStmt[] { statement });
        var second = new QOperation(
            "Second",
            Array.Empty<QParam>(),
            Array.Empty<QStmt>());
        var parentProgram = new QProgram(new[] { first, second });
        var movedProgram = parentProgram with
        {
            Operations = new[]
            {
                first with { Body = Array.Empty<QStmt>() },
                second with { Body = new QStmt[] { statement } },
            },
        };
        var builder = new HirPipelineBuilder(
            CompilationId.New(),
            new CompilationRevision(0));
        _ = builder.Advance(HirStage.Lowered, parentProgram);
        _ = builder.Advance(HirStage.MeasurementLowered, movedProgram);

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("owning operation", error.Message);
    }

    [Fact]
    public void MirAndTargetDeclareTheirExactHirSources()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; H(q[0]); }");
        Assert.True(compilation.Succeeded);

        var conjugated = compilation.Hir.Require(HirStage.ConjugationLowered);
        var materialized = compilation.Hir.Require(HirStage.AdjointMaterialized);
        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        Assert.Equal(conjugated.Id, compilation.Mir!.LoweredFrom);
        Assert.Equal(materialized.Id, compilation.Targets.OpenQasm!.Source);
        Assert.Equal(analyzed.Id, compilation.Mir.Links.SymbolsFrom);
        Assert.Equal(analyzed.Id, compilation.Targets.OpenQasm.SemanticBasis);
        Assert.Same(compilation.Mir.Links, compilation.Links.Mir);
    }

    [Fact]
    public void CopiedHirNodesResolveThroughCompilationLineage()
    {
        var compilation = QoraCompiler.Compile(
            """
            operation Flip(q: Qubit) {
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                Adjoint Flip(q[0]);
            }
            """);
        Assert.True(compilation.Succeeded);

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var materialized = compilation.Hir.Require(HirStage.AdjointMaterialized);
        var original = analyzed.Program.Operations.Single(operation => operation.Name == "Flip");
        var inverse = materialized.Program.Operations.Single(
            operation => operation.Name.StartsWith("Flip__adj", StringComparison.Ordinal));
        var materializedMain = materialized.Program.Operations.Single(operation => operation.Name == "Main");
        var inverseCall = materializedMain.Body.OfType<QGate>().Single();
        Assert.Equal(inverse.Id, inverseCall.CalleeOpId);
        Assert.Equal(inverse.Name, inverseCall.Name);
        Assert.NotEqual(original.Body.Single().Id, inverse.Body.Single().Id);
        Assert.Equal(
            original.Body.Single().Id,
            compilation.Links.Hir.ResolveNodeId(
                materialized.Id,
                analyzed.SourceId,
                inverse.Body.Single().Id));
    }

    [Fact]
    public void NestedPassLocalCopiesNormalizeToNodesThatExistInTheParentSnapshot()
    {
        static QGate Gate(string name) =>
            new(Array.Empty<string>(), name, new QArg[] { new QQubitArg("q", "0") });

        var original = new QProgram(
            new[]
            {
                new QOperation(
                    "Main",
                    Array.Empty<QParam>(),
                    new QStmt[]
                    {
                        new QUse("q", 1),
                        new QConjugate(
                            Within: new QStmt[]
                            {
                                new QConjugate(
                                    Within: new QStmt[] { Gate("X") },
                                    Apply: new QStmt[] { Gate("H") }),
                            },
                            Apply: new QStmt[] { Gate("Z") }),
                    }),
            });
        var lowered = ConjugationLowering.Run(original);
        Assert.Empty(lowered.Errors);

        var compilationId = CompilationId.New();
        var builder = new HirPipelineBuilder(
            compilationId,
            new CompilationRevision(0));
        var parent = builder.Advance(HirStage.Lowered, original);
        var target = builder.Advance(
            HirStage.ConjugationLowered,
            lowered.Program,
            lowered.Derivations);
        var lineage = builder.Build().Lineage;

        static IEnumerable<QStmt> Descendants(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                yield return statement;
                var nested = statement switch
                {
                    QIf branch => branch.Then.Concat(branch.Else),
                    QFor loop => loop.Body,
                    QWhile loop => loop.Body,
                    QRepeat loop => loop.Body,
                    QConjugate conjugation => conjugation.Within.Concat(conjugation.Apply),
                    _ => Array.Empty<QStmt>(),
                };
                foreach (var child in Descendants(nested.ToArray()))
                    yield return child;
            }
        }

        var copied = target.Program.Operations
            .SelectMany(operation => Descendants(operation.Body))
            .Where(statement => !parent.Structure.Contains(statement.Id))
            .ToArray();
        Assert.NotEmpty(copied);
        Assert.All(
            copied,
            statement =>
            {
                var sourceId = lineage.ResolveNodeId(
                    target.Id,
                    parent.Id,
                    statement.Id);
                Assert.True(parent.Structure.Contains(sourceId));
            });
    }

    [Fact]
    public void HirSymbolsAndSsaValuesHaveManyToManyCrossStageLinks()
    {
        var compilation = QoraCompiler.Compile(
            """
            operation Main() {
                var x: int = 1;
                var y: int = x;
                var z: int = y + 1;
            }
            """);
        Assert.True(compilation.Succeeded);

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var model = analyzed.Model;
        var declarations = Assert.Single(analyzed.Program.Operations).Body
            .OfType<QDecl>()
            .ToDictionary(declaration => declaration.Name);
        var x = model.FindSymbol(declarations["x"].Id)!;
        var y = model.FindSymbol(declarations["y"].Id)!;
        var links = Assert.IsType<MirCrossStageLinks>(compilation.Links.Mir);
        var xRef = new HirSymbolRef(links.SymbolsFrom, x.Id);
        var yRef = new HirSymbolRef(links.SymbolsFrom, y.Id);

        var xValue = Assert.Single(links.ValuesBySymbol[xRef]);
        var yValue = Assert.Single(links.ValuesBySymbol[yRef]);
        Assert.Equal(xValue, yValue);
        Assert.Contains(xRef, links.SymbolsByValue[xValue]);
        Assert.Contains(yRef, links.SymbolsByValue[xValue]);
    }

    [Fact]
    public void MirAnalysisStoreReturnsOneCanonicalDependencyInstance()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; H(q[0]); }");
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var callable = Assert.Single(mir.Program.Callables);
        var callableRef = new MirCallableRef(mir.Id, callable.Id);

        Assert.Same(
            mir.Analyses.ControlFlow(callableRef),
            mir.Analyses.ControlFlow(callableRef));
        Assert.Same(
            mir.Analyses.MemoryState(callableRef),
            mir.Analyses.MemoryState(callableRef));
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
                    new StubTargetArtifact((TargetBackend)int.MaxValue),
                }));
        Assert.Throws<ArgumentException>(
            () => new TargetArtifactSet(
                new ITargetArtifact[]
                {
                    new StubTargetArtifact(TargetBackend.OpenQasm),
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => targets.Find((TargetBackend)int.MaxValue));
    }

    [Fact]
    public void TargetDiagnosticsCanNameExactHirOrMirInputsWithoutForcingOneBackendShape()
    {
        var source = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        Assert.True(source.Succeeded);
        var mir = Assert.IsType<MirSnapshot>(source.Mir);

        Compilation WithTargetDiagnostic(TargetDiagnosticInput input) =>
            new(
                source.Id,
                source.Revision,
                source.Session,
                source.ParentRevision,
                source.Options,
                source.Sources,
                source.Hir,
                mir,
                source.Links,
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

        var fromHir = WithTargetDiagnostic(
            new TargetDiagnosticInput.Hir(
                source.Hir.AdjointMaterialized!.Id));
        var fromMir = WithTargetDiagnostic(
            new TargetDiagnosticInput.Mir(mir.Id));
        Assert.False(fromHir.Succeeded);
        Assert.False(fromMir.Succeeded);

        var staleMir = new MirSnapshotId(
            source.Id,
            source.Revision,
            mir.Id.Revision + 1);
        Assert.Throws<ArgumentException>(
            () => WithTargetDiagnostic(
                new TargetDiagnosticInput.Mir(staleMir)));
    }

    private sealed record StubTargetArtifact(
        TargetBackend Backend) : ITargetArtifact;
}
