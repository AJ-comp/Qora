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
        Assert.Empty(typeof(MirQubitId).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitVersion).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitKey).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitParameter).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitFromUse).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitAfterInstruction).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitPhi).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirControlFlowEdge).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitPhiInput).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitAccess).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitRef).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirQubitAccessRef).GetConstructors(publicInstance));
    }

    [Fact]
    public void SnapshotIdentityIsBoundToTheExactCompilationAndHirOrigin()
    {
        var compilation = CompilationId.New();
        var compilationRevision = new CompilationRevision(7);
        var snapshotId =
            new MirSnapshotId(compilation, compilationRevision, revision: 5);
        var hir = HirFixtureFor(
            snapshotId,
            "First",
            "Second");
        var firstHirCallable = CallableId(hir, "First");
        var secondHirCallable = CallableId(hir, "Second");
        var program = ProgramWithTwoCallables(
            snapshotId,
            firstHirCallable,
            secondHirCallable);
        var linkBuilder = new MirCrossStageLinksBuilder(
            snapshotId,
            hir.Snapshot,
            hir.Semantics);
        linkBuilder.LinkCallable(
            firstHirCallable,
            hir.Semantics.Model.FindSymbol(firstHirCallable)!.Id,
            new MirCallableId(0));
        linkBuilder.LinkCallable(
            secondHirCallable,
            hir.Semantics.Model.FindSymbol(secondHirCallable)!.Id,
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
            "First",
            "Second");
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
    public void CallableHirLinksRejectDuplicateValuesEvenWithCompleteCoverage()
    {
        var snapshotId = MirTestContext.Create().SnapshotId;
        var hir = HirFixtureFor(
            snapshotId,
            "First",
            "Second",
            "Third");
        var firstHirCallable = CallableId(hir, "First");
        var secondHirCallable = CallableId(hir, "Second");
        var thirdHirCallable = CallableId(hir, "Third");
        var program = ProgramWithTwoCallables(
            snapshotId,
            firstHirCallable,
            secondHirCallable);
        var linkBuilder = new MirCrossStageLinksBuilder(
            snapshotId,
            hir.Snapshot,
            hir.Semantics);
        linkBuilder.LinkCallable(
            firstHirCallable,
            hir.Semantics.Model.FindSymbol(firstHirCallable)!.Id,
            new MirCallableId(0));
        linkBuilder.LinkCallable(
            secondHirCallable,
            hir.Semantics.Model.FindSymbol(secondHirCallable)!.Id,
            new MirCallableId(1));
        var error = Assert.Throws<InvalidOperationException>(
            () => linkBuilder.LinkCallable(
                thirdHirCallable,
                hir.Semantics.Model.FindSymbol(thirdHirCallable)!.Id,
                new MirCallableId(1)));

        Assert.Contains("registered more than once", error.Message);
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
            links.CallablesByHirCallable,
            callableEdges,
            links.ValuesBySymbol,
            links.SymbolsByValue,
            links.StoragesBySymbol,
            links.SymbolsByStorage,
            links.QubitsBySymbol,
            links.SymbolsByQubit,
            links.SymbolDispositions,
            links.CallableProvenance,
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
            links.CallablesByHirCallable,
            links.CallablesBySymbol,
            valuesBySymbol,
            symbolsByValue,
            links.StoragesBySymbol,
            links.SymbolsByStorage,
            links.QubitsBySymbol,
            links.SymbolsByQubit,
            links.SymbolDispositions,
            links.CallableProvenance,
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
        var deadDeclaration = semanticProgram.Callables
            .SelectMany(callable => callable.Body)
            .OfType<HirVariableDeclarationStatement>()
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
        var snapshot = SnapshotWithTwoCallables();
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
            new MirQubitRef(
                snapshotId,
                firstCallable,
                new MirQubitId(0),
                new MirQubitVersion(0)),
            new MirQubitRef(
                snapshotId,
                secondCallable,
                new MirQubitId(0),
                new MirQubitVersion(0)));

        Assert.Equal(
            "First",
            snapshot.Structure.RequireCallable(
                new MirCallableRef(snapshotId, firstCallable)).Name);
        Assert.Equal(
            "Second",
            snapshot.Structure.RequireCallable(
                new MirCallableRef(snapshotId, secondCallable)).Name);
        var firstHirCallable = snapshot.Links.CallablesByHirCallable
            .Single(pair => pair.Value.Callable == firstCallable)
            .Key
            .NodeId;
        var secondHirCallable = snapshot.Links.CallablesByHirCallable
            .Single(pair => pair.Value.Callable == secondCallable)
            .Key
            .NodeId;
        Assert.Equal(
            firstHirCallable,
            snapshot.Origins.ResolveHir(
                snapshot.Structure.RequireBlock(firstBlock).Origin).HirCallableId);
        Assert.Equal(
            secondHirCallable,
            snapshot.Origins.ResolveHir(
                snapshot.Structure.RequireBlock(secondBlock).Origin).HirCallableId);
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
        var snapshot = SnapshotWithTwoCallables();
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
        var firstQubit = Assert.Single(firstCallable.Qubits.OfType<MirQubitFromUse>());
        var secondQubit = Assert.Single(secondCallable.Qubits.OfType<MirQubitFromUse>());

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
            new MirQubitRef(first.Id, firstCallable.Id, firstQubit.Key);
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

    private static MirSnapshot SnapshotWithTwoCallables()
    {
        var snapshotId = MirTestContext.Create().SnapshotId;
        var hir = HirFixtureFor(snapshotId, "First", "Second");
        var program = ProgramWithTwoCallables(
            snapshotId,
            CallableId(hir, "First"),
            CallableId(hir, "Second"));
        return Snapshot(program, hir);
    }

    private static MirSnapshot Snapshot(
        MirProgram program,
        HirFixture hir)
    {
        var snapshotId = program.SnapshotId;
        var linkBuilder = new MirCrossStageLinksBuilder(
            snapshotId,
            hir.Snapshot,
            hir.Semantics);
        foreach (var callable in program.Callables)
        {
            var origin = program.Origins.ResolveHir(callable.Origin);
            var callableId = origin.HirCallableId!.Value;
            linkBuilder.LinkCallable(
                callableId,
                hir.Semantics.Model.FindSymbol(callableId)!.Id,
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
        params string[] callableNames)
    {
        var hir = new HirTestFactory(
            new SourceDocumentRef(
                snapshotId.CompilationId,
                snapshotId.CompilationRevision,
                new SourceDocumentId(0)));
        var callables = callableNames
            .Select(name => hir.Callable(name))
            .ToArray();
        var program = hir.PublishProgram(callables);
        var builder = hir.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(program);
        var validation = builder.ValidateSnapshot(snapshot);
        Assert.True(
            validation.IsAccepted,
            string.Join(Environment.NewLine, validation.Diagnostics.Select(error => error.Message)));
        return new HirFixture(
            snapshot,
            validation);
    }

    private static HirNodeId CallableId(
        HirFixture fixture,
        string name) =>
        fixture.Snapshot.Program.Callables
            .Single(callable => callable.Name == name)
            .Id;

    private sealed record HirFixture(
        HirSnapshot Snapshot,
        HirSemanticArtifact Semantics);

    private static MirProgram ProgramWithTwoCallables(
        MirSnapshotId snapshotId,
        HirNodeId firstHirCallable,
        HirNodeId secondHirCallable)
    {
        var context = MirTestContext.For(snapshotId);
        return context.Program(
            new MirCallableId(0),
            new[]
            {
                Callable(new MirCallableId(0), context.Origin(0), "First", "10"),
                Callable(new MirCallableId(1), context.Origin(1), "Second", "20"),
            },
            (firstHirCallable, firstHirCallable),
            (secondHirCallable, secondHirCallable));
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
            returnType: null,
            Array.Empty<IMirParameter>(),
            blockId,
            new[] { block },
            new[] { value },
            Array.Empty<MirArrayStorage>(),
            source);
    }
}
