using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class MirSnapshotTests
{
    [Fact]
    public void SnapshotAuthorityCannotBeConstructedThroughThePublicApi()
    {
        const System.Reflection.BindingFlags publicInstance =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance;

        Assert.Empty(typeof(MirProgram).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirSnapshot).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirOriginTable).GetConstructors(publicInstance));
    }

    [Fact]
    public void SnapshotIdentityIsBoundToTheExactCompilationAndHirOrigin()
    {
        var compilation = CompilationId.New();
        var compilationRevision = new CompilationRevision(7);
        var snapshotId =
            new MirSnapshotId(compilation, compilationRevision, revision: 5);
        var program = ProgramWithTwoCallables(snapshotId);
        var hir = HirFixtureFor(
            snapshotId,
            (10, "First"),
            (20, "Second"));
        var linkBuilder = new MirCrossStageLinksBuilder(
            snapshotId,
            hir.Snapshot,
            hir.Semantics);
        linkBuilder.LinkCallable(
            10,
            hir.Semantics.Model.FindSymbol(10)!.Id,
            new MirCallableId(0));
        linkBuilder.LinkCallable(
            20,
            hir.Semantics.Model.FindSymbol(20)!.Id,
            new MirCallableId(1));
        foreach (var callable in program.Callables)
            foreach (var value in callable.Values)
                linkBuilder.RegisterTemporaryValue(callable.Id, value.Id);
        var links = linkBuilder.Build(program.Origins);

        var snapshot = new MirSnapshot(
            snapshotId,
            MirLoweringProfile.CanonicalV1,
            program,
            links);

        Assert.Equal(compilation, snapshot.Id.CompilationId);
        Assert.Equal(compilationRevision, snapshot.Id.CompilationRevision);
        Assert.Equal(hir.Snapshot.Id, snapshot.LoweredFrom);
        Assert.Same(program, snapshot.Program);
        Assert.Equal(snapshot.Id, snapshot.Analyses.SnapshotId);

        var unrelatedCompilation = CompilationId.New();
        var unrelatedSnapshotId =
            new MirSnapshotId(unrelatedCompilation, compilationRevision, revision: 5);
        var unrelatedHir = HirFixtureFor(
            unrelatedSnapshotId,
            (10, "First"),
            (20, "Second"));
        Assert.Throws<ArgumentException>(
            () => new MirCrossStageLinksBuilder(
                snapshotId,
                unrelatedHir.Snapshot,
                unrelatedHir.Semantics));
        Assert.Throws<ArgumentException>(
            () => new MirSnapshot(
                new MirSnapshotId(compilation, compilationRevision, revision: 4),
                MirLoweringProfile.CanonicalV1,
                program,
                links));
    }

    [Fact]
    public void CallableOperationLinksRejectDuplicateValuesEvenWithCompleteCoverage()
    {
        var program = ProgramWithTwoCallables();
        var snapshotId = program.SnapshotId;
        var hir = HirFixtureFor(
            snapshotId,
            (10, "First"),
            (20, "Second"),
            (30, "Third"));
        var linkBuilder = new MirCrossStageLinksBuilder(
            snapshotId,
            hir.Snapshot,
            hir.Semantics);
        linkBuilder.LinkCallable(
            10,
            hir.Semantics.Model.FindSymbol(10)!.Id,
            new MirCallableId(0));
        linkBuilder.LinkCallable(
            20,
            hir.Semantics.Model.FindSymbol(20)!.Id,
            new MirCallableId(1));
        linkBuilder.LinkCallable(
            30,
            hir.Semantics.Model.FindSymbol(30)!.Id,
            new MirCallableId(1));

        var error = Assert.Throws<ArgumentException>(
            () => linkBuilder.Build(program.Origins));

        Assert.Contains("exactly one HIR operation", error.Message);
    }

    [Fact]
    public void CallableSymbolLinksRejectExtraMismatchedEdge()
    {
        var compilation = QoraCompiler.Compile(
            """
            operation First() { }
            operation Main() { }
            """);
        Assert.True(compilation.Succeeded);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var links = mir.Links;
        var callableEdges = links.CallablesBySymbol.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var symbolLinks = callableEdges.ToArray();
        Assert.Equal(2, symbolLinks.Length);
        var first = symbolLinks[0];
        var second = symbolLinks[1];
        callableEdges[first.Key] = first.Value
            .Append(Assert.Single(second.Value))
            .ToArray();

        var adversarial = new MirCrossStageLinks(
            links.MirSnapshot,
            links.LoweredFromSnapshot,
            links.SymbolsFromArtifact,
            links.Origins,
            links.CallablesByHirOperation,
            callableEdges,
            links.ValuesBySymbol,
            links.SymbolsByValue,
            links.StoragesBySymbol,
            links.SymbolsByStorage,
            links.QubitsBySymbol,
            links.SymbolsByQubit,
            links.SymbolDispositions,
            links.ValueOrigins,
            links.StorageOrigins,
            links.QubitOrigins);

        var error = Assert.Throws<ArgumentException>(
            () => adversarial.VerifyAgainst(compilation.Hir, compilation.Links.Hir));

        Assert.Contains("do not exactly match", error.Message);
    }

    [Fact]
    public void EveryMirValueKeepsEitherItsCompleteSourceLinkOrAnExplicitTemporaryOrigin()
    {
        var compilation = QoraCompiler.Compile(
            """
            operation Main() {
                var x: int = 1;
                x = x + 1;
            }
            """);
        Assert.True(compilation.Succeeded);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var links = mir.Links;
        var sourceBacked = Assert.Single(
            links.ValuesBySymbol,
            pair => pair.Value.Count > 1);
        var sourceSymbol = sourceBacked.Key;
        var removedValue = sourceBacked.Value[^1];

        var valuesBySymbol = links.ValuesBySymbol.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirValueRef>)pair.Value.ToArray());
        valuesBySymbol[sourceSymbol] = valuesBySymbol[sourceSymbol]
            .Where(value => value != removedValue)
            .ToArray();
        var symbolsByValue = links.SymbolsByValue
            .Where(pair => pair.Key != removedValue)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<HirSymbolRef>)pair.Value.ToArray());

        var adversarial = new MirCrossStageLinks(
            links.MirSnapshot,
            links.LoweredFromSnapshot,
            links.SymbolsFromArtifact,
            links.Origins,
            links.CallablesByHirOperation,
            links.CallablesBySymbol,
            valuesBySymbol,
            symbolsByValue,
            links.StoragesBySymbol,
            links.SymbolsByStorage,
            links.QubitsBySymbol,
            links.SymbolsByQubit,
            links.SymbolDispositions,
            links.ValueOrigins,
            links.StorageOrigins,
            links.QubitOrigins);

        var error = Assert.Throws<ArgumentException>(
            () => new MirSnapshot(
                mir.Id,
                mir.Profile,
                mir.Program,
                adversarial));

        Assert.Contains("SourceSymbol", error.Message);
    }

    [Fact]
    public void UnreachableDeclarationsHaveAnExplicitNonLoweringDisposition()
    {
        var compilation = QoraCompiler.Compile(
            """
            function f(): int {
                return 1;
                var dead: int = 2;
            }

            operation Main() {
                var result: int = f();
            }
            """);

        Assert.True(
            compilation.Succeeded,
            string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error.Message)));
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var semanticProgram = mir.Links.SymbolsFromArtifact.Program;
        var deadDeclaration = semanticProgram.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QDecl>()
            .Single(declaration => declaration.Name == "dead");
        var dead = Assert.IsType<Symbol>(
            mir.Links.SymbolsFromArtifact.Model.FindSymbol(deadDeclaration.Id));
        var deadRef = new HirSymbolRef(mir.Links.SymbolsFrom, dead.Id);

        Assert.Equal(
            MirSymbolLoweringDisposition.NotLoweredUnreachable,
            mir.Links.SymbolDispositions[deadRef]);
        Assert.False(mir.Links.ValuesBySymbol.ContainsKey(deadRef));
    }

    [Fact]
    public void CompositeReferencesKeepEqualLocalIdsSeparateAcrossCallables()
    {
        var snapshot = Snapshot(ProgramWithTwoCallables());
        var snapshotId = snapshot.Id;
        var firstCallable = new MirCallableId(0);
        var secondCallable = new MirCallableId(1);
        var firstBlock = new MirBlockRef(snapshotId, firstCallable, new MirBlockId(0));
        var secondBlock = new MirBlockRef(snapshotId, secondCallable, new MirBlockId(0));
        var firstInstruction =
            new MirInstructionRef(snapshotId, firstCallable, new MirInstructionId(0));
        var secondInstruction =
            new MirInstructionRef(snapshotId, secondCallable, new MirInstructionId(0));
        var firstValue = new MirValueRef(snapshotId, firstCallable, new MirValueId(0));
        var secondValue = new MirValueRef(snapshotId, secondCallable, new MirValueId(0));

        Assert.NotEqual(firstBlock, secondBlock);
        Assert.NotEqual(firstInstruction, secondInstruction);
        Assert.NotEqual(firstValue, secondValue);
        Assert.NotEqual(
            new MirStorageRef(snapshotId, firstCallable, new MirStorageId(0)),
            new MirStorageRef(snapshotId, secondCallable, new MirStorageId(0)));
        Assert.NotEqual(
            new MirQubitResourceRef(snapshotId, firstCallable, new MirQubitResourceId(0)),
            new MirQubitResourceRef(snapshotId, secondCallable, new MirQubitResourceId(0)));

        Assert.Equal(
            "First",
            snapshot.Structure.RequireCallable(
                new MirCallableRef(snapshotId, firstCallable)).Name);
        Assert.Equal(
            "Second",
            snapshot.Structure.RequireCallable(
                new MirCallableRef(snapshotId, secondCallable)).Name);
        Assert.Equal(
            10,
            snapshot.Origins.ResolveHir(
                snapshot.Structure.RequireBlock(firstBlock).Origin).HirOperationId);
        Assert.Equal(
            20,
            snapshot.Origins.ResolveHir(
                snapshot.Structure.RequireBlock(secondBlock).Origin).HirOperationId);
        Assert.Equal(
            "10",
            Assert.IsType<MirConstant>(
                snapshot.Structure.RequireInstruction(firstInstruction)).Constant.Text);
        Assert.Equal(
            "20",
            Assert.IsType<MirConstant>(
                snapshot.Structure.RequireInstruction(secondInstruction)).Constant.Text);
        Assert.Equal(
            new MirInstructionLocation(firstBlock, 0),
            snapshot.Structure.RequireInstructionLocation(firstInstruction));
        Assert.Equal(
            new MirInstructionLocation(secondBlock, 0),
            snapshot.Structure.RequireInstructionLocation(secondInstruction));
        Assert.Equal(new MirCallableId(0), firstValue.Callable);
        Assert.Equal(new MirCallableId(1), secondValue.Callable);
        Assert.Equal(
            new MirInstructionId(0),
            snapshot.Structure.RequireValue(firstValue).Definition.Instruction);
        Assert.Equal(
            new MirInstructionId(0),
            snapshot.Structure.RequireValue(secondValue).Definition.Instruction);
    }

    [Fact]
    public async Task AnalysisStoreCachesResultsAndReusesOneCfgPerCallable()
    {
        var snapshot = Snapshot(ProgramWithTwoCallables());
        var callable = new MirCallableRef(snapshot.Id, new MirCallableId(0));
        var analyses = snapshot.Analyses;

        var cfg = analyses.ControlFlow(callable);
        var provenance = analyses.StorageProvenance(callable);
        var memory = analyses.MemoryState(callable);
        var paths = analyses.PathConditions(callable);
        var scalars = analyses.ScalarAvailability(callable);
        var effects = analyses.Effects;
        var witnesses = analyses.WitnessAvailability(callable);

        Assert.Same(cfg, analyses.ControlFlow(callable));
        Assert.Same(provenance, analyses.StorageProvenance(callable));
        Assert.Same(memory, analyses.MemoryState(callable));
        Assert.Same(paths, analyses.PathConditions(callable));
        Assert.Same(scalars, analyses.ScalarAvailability(callable));
        Assert.Same(effects, analyses.Effects);
        Assert.Same(witnesses, analyses.WitnessAvailability(callable));

        Assert.Same(cfg, memory.ControlFlow);
        Assert.Same(provenance, memory.StorageProvenance);
        Assert.Same(cfg, paths.ControlFlow);
        Assert.Same(cfg, scalars.ControlFlow);
        Assert.Same(memory, scalars.MemoryState);
        Assert.Same(cfg, witnesses.ControlFlow);
        Assert.Same(memory, witnesses.MemoryState);
        Assert.Same(scalars, witnesses.ScalarAvailability);

        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => analyses.WitnessAvailability(callable))));
        Assert.All(concurrent, result => Assert.Same(witnesses, result));
    }

    [Fact]
    public void ExactReferencesRejectEqualDenseIdsFromOtherRevisionsAndCompilations()
    {
        const string source = """
            operation Main() {
                use q = Qubit[1];
                var values: int[] = [1];
                values[0] = 2;
                X(q[0]);
            }
            """;
        var firstCompilation = QoraCompiler.Compile(source);
        var secondCompilation = QoraCompiler.Recompile(firstCompilation, source);
        var unrelatedCompilation = QoraCompiler.Compile(source);
        var first = Assert.IsType<MirSnapshot>(firstCompilation.Mir);
        var second = Assert.IsType<MirSnapshot>(secondCompilation.Mir);
        var unrelated = Assert.IsType<MirSnapshot>(unrelatedCompilation.Mir);

        Assert.Equal(first.Id.CompilationId, second.Id.CompilationId);
        Assert.NotEqual(first.Id.CompilationRevision, second.Id.CompilationRevision);
        Assert.NotEqual(first.Id.CompilationId, unrelated.Id.CompilationId);

        var firstCallable = Assert.Single(first.Program.Callables);
        var secondCallable = Assert.Single(second.Program.Callables);
        var firstBlock = Assert.Single(
            firstCallable.Blocks,
            block => block.Instructions.Count > 0);
        var secondBlock = Assert.Single(
            secondCallable.Blocks,
            block => block.Instructions.Count > 0);
        var firstInstruction = firstBlock.Instructions[0];
        var secondInstruction = secondBlock.Instructions[0];
        var firstValue = firstCallable.Values.First(value => value.Type.IsArray);
        var secondValue = secondCallable.Values.First(value => value.Type.IsArray);
        var firstStorage = Assert.Single(firstCallable.Storages);
        var secondStorage = Assert.Single(secondCallable.Storages);
        var firstQubit = Assert.Single(firstCallable.Qubits);
        var secondQubit = Assert.Single(secondCallable.Qubits);

        Assert.Equal(firstCallable.Id, secondCallable.Id);
        Assert.Equal(firstBlock.Id, secondBlock.Id);
        Assert.Equal(firstInstruction.Id, secondInstruction.Id);
        Assert.Equal(firstValue.Id, secondValue.Id);
        Assert.Equal(firstStorage.Id, secondStorage.Id);
        Assert.Equal(firstQubit.Id, secondQubit.Id);
        Assert.Equal(firstInstruction.Origin.Value, secondInstruction.Origin.Value);
        Assert.NotEqual(firstInstruction.Origin, secondInstruction.Origin);

        var staleCallable = new MirCallableRef(first.Id, firstCallable.Id);
        var staleBlock = new MirBlockRef(first.Id, firstCallable.Id, firstBlock.Id);
        var staleInstruction =
            new MirInstructionRef(first.Id, firstCallable.Id, firstInstruction.Id);
        var staleValue = new MirValueRef(first.Id, firstCallable.Id, firstValue.Id);
        var staleStorage =
            new MirStorageRef(first.Id, firstCallable.Id, firstStorage.Id);
        var staleQubit =
            new MirQubitResourceRef(first.Id, firstCallable.Id, firstQubit.Id);
        var currentCallable = new MirCallableRef(second.Id, secondCallable.Id);
        var currentBlock =
            new MirBlockRef(second.Id, secondCallable.Id, secondBlock.Id);

        Assert.Throws<ArgumentException>(
            () => second.Structure.RequireCallable(staleCallable));
        Assert.Throws<ArgumentException>(
            () => second.Structure.RequireBlock(staleBlock));
        Assert.Throws<ArgumentException>(
            () => second.Structure.RequireInstruction(staleInstruction));
        Assert.Throws<ArgumentException>(
            () => second.Structure.RequireValue(staleValue));
        Assert.Throws<ArgumentException>(
            () => second.Structure.RequireStorage(staleStorage));
        Assert.Throws<ArgumentException>(
            () => second.Structure.RequireQubit(staleQubit));
        Assert.Throws<ArgumentException>(
            () => unrelated.Structure.RequireCallable(staleCallable));

        Assert.Throws<ArgumentException>(
            () => second.Analyses.ControlFlow(staleCallable));
        var cfg = second.Analyses.ControlFlow(currentCallable);
        Assert.Throws<ArgumentException>(() => cfg.IsReachable(staleBlock));
        Assert.Throws<ArgumentException>(
            () => cfg.PointBeforeInstruction(staleInstruction));
        Assert.Throws<ArgumentException>(
            () => cfg.IsValueAvailableAtTerminator(staleValue, currentBlock));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.PathConditions(currentCallable).ConditionFor(staleBlock));

        Assert.Throws<ArgumentException>(
            () => second.Links.SymbolsFor(staleValue));
        Assert.Throws<ArgumentException>(
            () => second.Links.SymbolsFor(staleStorage));
        Assert.Throws<ArgumentException>(
            () => second.Links.SymbolsFor(staleQubit));

        Assert.Throws<ArgumentException>(
            () => second.Origins.Require(firstInstruction.Origin));
        Assert.Throws<ArgumentException>(
            () => second.Links.ResolveOrigin(firstInstruction.Origin));
        var staleEffect = Assert.Single(first.Analyses.Effects.Effects);
        Assert.Throws<ArgumentException>(
            () => second.Analyses.Effects.EffectAt(staleEffect.Site));
    }

    private static MirSnapshot Snapshot(MirProgram program)
    {
        var snapshotId = program.SnapshotId;
        var hirOperations = program.Callables
            .Select(callable =>
            {
                var origin = program.Origins.ResolveHir(callable.Origin);
                return (origin.HirOperationId!.Value, callable.Name);
            })
            .ToArray();
        var hir = HirFixtureFor(snapshotId, hirOperations);
        var linkBuilder = new MirCrossStageLinksBuilder(
            snapshotId,
            hir.Snapshot,
            hir.Semantics);
        foreach (var callable in program.Callables)
        {
            var origin = program.Origins.ResolveHir(callable.Origin);
            var operationId = origin.HirOperationId!.Value;
            linkBuilder.LinkCallable(
                operationId,
                hir.Semantics.Model.FindSymbol(operationId)!.Id,
                callable.Id);
            foreach (var value in callable.Values)
                linkBuilder.RegisterTemporaryValue(callable.Id, value.Id);
        }
        var links = linkBuilder.Build(program.Origins);
        return new MirSnapshot(
            snapshotId,
            MirLoweringProfile.CanonicalV1,
            program,
            links);
    }

    private static HirFixture HirFixtureFor(
        MirSnapshotId snapshotId,
        params (int Id, string Name)[] operations)
    {
        var program = new QProgram(
            operations
                .Select(operation =>
                    new QOperation(
                        operation.Name,
                        Array.Empty<QParam>(),
                        Array.Empty<QStmt>())
                    {
                        Id = operation.Id,
                    })
                .ToArray());
        var builder = new HirPipelineBuilder(
            snapshotId.CompilationId,
            snapshotId.CompilationRevision);
        var snapshot = builder.Advance(HirStage.Lowered, program);
        var validation = builder.ValidateSnapshot(snapshot);
        Assert.True(
            validation.IsAccepted,
            string.Join(Environment.NewLine, validation.Diagnostics.Select(error => error.Message)));
        return new HirFixture(
            snapshot,
            validation);
    }

    private sealed record HirFixture(
        HirSnapshot Snapshot,
        HirSemanticArtifact Semantics);

    private static MirProgram ProgramWithTwoCallables(
        MirSnapshotId? snapshotId = null)
    {
        var context = snapshotId is MirSnapshotId id
            ? MirTestContext.For(id)
            : MirTestContext.Create();
        return context.Program(
            new[]
            {
                Callable(new MirCallableId(0), context.Origin(0), "First", "10"),
                Callable(new MirCallableId(1), context.Origin(1), "Second", "20"),
            },
            (10, 10),
            (20, 20));
    }

    private static MirCallable Callable(
        MirCallableId id,
        MirOriginRef source,
        string name,
        string constantText)
    {
        var blockId = new MirBlockId(0);
        var instructionId = new MirInstructionId(0);
        var valueId = new MirValueId(0);
        var constant = new MirConstant(
            instructionId,
            valueId,
            new MirConstantValue(QType.Int, constantText),
            source);
        var value = new MirValue(
            valueId,
            MirType.Scalar(QType.Int),
            MirValueDefinition.InstructionResultAt(blockId, instructionId),
            Origin: source);
        var block = new MirBlock(
            blockId,
            Array.Empty<MirBlockArgument>(),
            new MirInstruction[] { constant },
            new MirReturn(Value: null, source),
            source);

        return new MirCallable(
            id,
            name,
            MirCallableKind.Operation,
            ReturnType: null,
            Array.Empty<MirParameter>(),
            blockId,
            new[] { block },
            new[] { value },
            Array.Empty<MirArrayStorage>(),
            Array.Empty<MirQubitResource>(),
            source);
    }
}
