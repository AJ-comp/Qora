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
        Assert.Empty(typeof(MirHirOrigin).GetConstructors(publicInstance));
        Assert.Empty(typeof(MirGeneratedOrigin).GetConstructors(publicInstance));
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
    }

    [Fact]
    public void SnapshotOwnershipComesFromExactHirArtifact()
    {
        var hir = HirFixtureFor(
            "First",
            "Second");
        var firstHirCallable = CallableId(hir, "First");
        var secondHirCallable = CallableId(hir, "Second");
        var program = ProgramWithTwoCallables(
            firstHirCallable,
            secondHirCallable,
            hir.FinalHir);
        var snapshot = MirSnapshot.CreateLowered(program);

        Assert.Same(hir.FinalHir, snapshot.HirArtifact);
        Assert.Same(program, snapshot.Program);
    }

    [Fact]
    public void ProgramRejectsOriginsFromDifferentHirArtifacts()
    {
        var firstHir = HirFixtureFor("Main");
        var secondHir = HirFixtureFor("Other");
        var firstHirCallableId = CallableId(firstHir, "Main");
        var secondHirCallableId = CallableId(secondHir, "Other");
        var firstOrigin = MirOrigin.FromHirNode(
            firstHir.FinalHir,
            firstHir.FinalHir.Source.Structure.RequireNode(firstHirCallableId));
        var secondOrigin = MirOrigin.FromHirNode(
            secondHir.FinalHir,
            secondHir.FinalHir.Source.Structure.RequireNode(secondHirCallableId));
        var entryPoint = Callable(
            new MirCallableId(0),
            firstOrigin,
            "Main",
            "1");
        var foreignCallable = Callable(
            new MirCallableId(1),
            secondOrigin,
            "Other",
            "2");

        var error = Assert.Throws<ArgumentException>(
            () => new MirProgram(
                entryPoint,
                new[] { entryPoint, foreignCallable }));

        Assert.Equal("callables", error.ParamName);
        Assert.Contains("different HIR artifacts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CallableLocalIdsRemainIsolatedByTheirOwningCallable()
    {
        var snapshot = SnapshotWithTwoCallables();
        var firstCallable = snapshot.Program.RequireCallable(new MirCallableId(0));
        var secondCallable = snapshot.Program.RequireCallable(new MirCallableId(1));
        var blockId = MirTestContext.BlockId(0);
        var instructionId = new MirInstructionId(0);
        var valueId = new MirValueId(0);
        var firstBlock = firstCallable.RequireBlock(blockId);
        var secondBlock = secondCallable.RequireBlock(blockId);
        var firstInstruction = firstCallable.RequireInstruction(instructionId);
        var secondInstruction = secondCallable.RequireInstruction(instructionId);
        var firstValue = firstCallable.RequireValue(valueId);
        var secondValue = secondCallable.RequireValue(valueId);

        Assert.Equal("First", firstCallable.Name);
        Assert.Equal("Second", secondCallable.Name);
        Assert.Same(firstCallable, snapshot.Program.RequireCallable(firstCallable));
        Assert.Same(secondCallable, snapshot.Program.RequireCallable(secondCallable));

        Assert.NotSame(firstBlock, secondBlock);
        Assert.NotSame(firstInstruction, secondInstruction);
        Assert.NotSame(firstValue, secondValue);
        Assert.True(firstCallable.ContainsValue(firstValue));
        Assert.False(firstCallable.ContainsValue(secondValue));

        var hirStructure = snapshot.HirArtifact.Source.Structure;
        var firstHirCallable = hirStructure.RequireOwningCallable(
            firstBlock.Origin.SourceHirOrigin.HirNodeId);
        var secondHirCallable = hirStructure.RequireOwningCallable(
            secondBlock.Origin.SourceHirOrigin.HirNodeId);
        Assert.NotEqual(firstHirCallable, secondHirCallable);
        Assert.Equal(
            "10",
            Assert.IsType<MirConstant>(firstInstruction).Text);
        Assert.Equal(
            "20",
            Assert.IsType<MirConstant>(secondInstruction).Text);

        var firstLocation = firstCallable.RequireInstructionLocation(instructionId);
        var secondLocation = secondCallable.RequireInstructionLocation(instructionId);
        Assert.Same(firstBlock, firstLocation.Block);
        Assert.Same(secondBlock, secondLocation.Block);
        Assert.Equal(0, firstLocation.Index);
        Assert.Equal(0, secondLocation.Index);
        Assert.Same(
            firstInstruction,
            snapshot.Program.RequireInstruction(
                new MirInstructionSite(firstCallable.Id, instructionId)));
        Assert.Same(
            secondInstruction,
            snapshot.Program.RequireInstruction(
                new MirInstructionSite(secondCallable.Id, instructionId)));
        Assert.Equal(
            new MirInstructionId(0),
            firstCallable.DefinitionOf(firstValue.Id).Instruction);
        Assert.Equal(
            new MirInstructionId(0),
            secondCallable.DefinitionOf(secondValue.Id).Instruction);
    }

    [Fact]
    public async Task AnalysisStoreCachesResultsAndReusesOneCfgPerCallable()
    {
        var snapshot = SnapshotWithTwoCallables();
        var callable = snapshot.Program.RequireCallable(new MirCallableId(0));
        var analyses = snapshot.Analyses;

        var cfg = analyses.ControlFlow(callable);
        var regions = analyses.ControlRegions(callable);
        var provenance = analyses.StorageProvenance(callable);
        var memory = analyses.MemoryState(callable);
        var paths = analyses.PathConditions(callable);
        var scalars = analyses.ScalarAvailability(callable);
        var bounds = analyses.Bounds(callable);
        var callGraph = analyses.CallGraph;
        var effects = analyses.Effects;
        var witnesses = analyses.WitnessAvailability(callable);

        Assert.Same(cfg, analyses.ControlFlow(callable));
        Assert.Same(regions, analyses.ControlRegions(callable));
        Assert.Same(provenance, analyses.StorageProvenance(callable));
        Assert.Same(memory, analyses.MemoryState(callable));
        Assert.Same(paths, analyses.PathConditions(callable));
        Assert.Same(scalars, analyses.ScalarAvailability(callable));
        Assert.Same(bounds, analyses.Bounds(callable));
        Assert.Same(callGraph, analyses.CallGraph);
        Assert.Same(effects, analyses.Effects);
        Assert.Same(witnesses, analyses.WitnessAvailability(callable));

        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => analyses.WitnessAvailability(callable))));
        Assert.All(concurrent, result => Assert.Same(witnesses, result));
    }

    [Fact]
    public void OwnerLocalLookupsRejectForeignObjectsWithEqualIds()
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

        Assert.Equal(
            firstCompilation.Id,
            first.HirArtifact.SourceId.CompilationId);
        Assert.Equal(
            secondCompilation.Revision,
            second.HirArtifact.SourceId.CompilationRevision);
        Assert.NotSame(first.HirArtifact, second.HirArtifact);
        Assert.NotSame(first.HirArtifact, unrelated.HirArtifact);

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
        Assert.NotSame(firstInstruction.Origin, secondInstruction.Origin);

        Assert.False(second.Program.ContainsCallable(firstCallable));
        Assert.False(secondCallable.ContainsValue(firstValue));
        Assert.False(secondCallable.ContainsStorage(firstStorage));
        Assert.False(secondCallable.ContainsQubit(firstQubit));

        Assert.Throws<ArgumentException>(
            () => second.Program.RequireCallable(firstCallable));
        Assert.Throws<ArgumentException>(
            () => unrelated.Program.RequireCallable(firstCallable));
        Assert.Throws<ArgumentException>(() => secondCallable.RequireStorage(firstStorage));
        Assert.Throws<ArgumentException>(() => secondCallable.RequireQubit(firstQubit));

        Assert.Same(secondCallable, second.Program.RequireCallable(firstCallable.Id));
        Assert.Same(secondBlock, secondCallable.RequireBlock(firstBlock.Id));
        Assert.Same(
            secondInstruction,
            secondCallable.RequireInstruction(firstInstruction.Id));
        Assert.Same(secondValue, secondCallable.RequireValue(firstValue.Id));
        Assert.Same(secondStorage, secondCallable.RequireStorage(firstStorage.Id));
        Assert.Same(secondQubit, secondCallable.RequireQubit(firstQubit.Key));

        Assert.Throws<ArgumentException>(
            () => second.Analyses.ControlFlow(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.ControlRegions(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.StorageProvenance(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.MemoryState(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.PathConditions(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.ScalarAvailability(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.WitnessAvailability(firstCallable));
        Assert.Throws<ArgumentException>(
            () => second.Analyses.Bounds(firstCallable));
        var firstCfg = first.Analyses.ControlFlow(firstCallable);
        var secondCfg = second.Analyses.ControlFlow(secondCallable);
        var foreignPoint = firstCfg.TerminatorPoint(firstBlock.Id);
        Assert.Throws<InvalidOperationException>(
            () => secondCfg.IsValueAvailableAt(secondValue.Id, foreignPoint));

        var firstInstructionSite = new MirInstructionSite(
            firstCallable.Id,
            firstInstruction.Id);
        var secondInstructionSite = new MirInstructionSite(
            secondCallable.Id,
            secondInstruction.Id);
        Assert.NotSame(first.Analyses.Effects, second.Analyses.Effects);
        Assert.Same(
            firstInstruction,
            first.Analyses.Effects.RequireInstruction(firstInstructionSite));
        Assert.Same(
            secondInstruction,
            second.Analyses.Effects.RequireInstruction(secondInstructionSite));

        Assert.Equal(
            first.HirArtifact.Source.SourceMap.Find(
                firstInstruction.Origin.SourceHirOrigin.HirNodeId),
            firstInstruction.Origin.SourceHirOrigin.Span);
        Assert.Equal(
            second.HirArtifact.Source.SourceMap.Find(
                secondInstruction.Origin.SourceHirOrigin.HirNodeId),
            secondInstruction.Origin.SourceHirOrigin.Span);
    }

    private static MirSnapshot SnapshotWithTwoCallables()
    {
        var hir = HirFixtureFor("First", "Second");
        var program = ProgramWithTwoCallables(
            CallableId(hir, "First"),
            CallableId(hir, "Second"),
            hir.FinalHir);
        return Snapshot(program);
    }

    private static MirSnapshot Snapshot(MirProgram program) =>
        MirSnapshot.CreateLowered(program);

    private static HirFixture HirFixtureFor(
        params string[] callableNames)
    {
        var hir = new HirTestFactory(
            new SourceDocumentRef(
                CompilationId.New(),
                new CompilationRevision(0),
                new SourceDocumentId(0)));
        var callables = callableNames
            .Select(name => hir.Callable(name))
            .ToArray();
        var program = hir.PublishProgram(callables);
        var builder = hir.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(program);
        builder.Alias(HirStage.ImportsExpanded, snapshot);
        builder.Alias(HirStage.Resolved, snapshot);
        builder.Alias(HirStage.Specialized, snapshot);
        var validation = builder.ValidateSnapshot(snapshot);
        Assert.True(
            validation.IsAccepted,
            string.Join(Environment.NewLine, validation.Diagnostics.Select(error => error.Message)));
        var finalHir = builder.AnalyzeEffects(validation);
        return new HirFixture(
            snapshot,
            finalHir);
    }

    private static HirNodeId CallableId(
        HirFixture fixture,
        string name) =>
        fixture.Snapshot.Program.Callables
            .Single(callable => callable.Name == name)
            .Id;

    private sealed record HirFixture(
        HirSnapshot Snapshot,
        HirSemanticArtifact FinalHir);

    private static MirProgram ProgramWithTwoCallables(
        HirNodeId firstHirCallable,
        HirNodeId secondHirCallable,
        HirSemanticArtifact finalHir)
    {
        var context = MirTestContext.Create();
        var firstOrigin = MirOrigin.FromHirNode(
            finalHir,
            finalHir.Source.Structure.RequireNode(firstHirCallable));
        var secondOrigin = MirOrigin.FromHirNode(
            finalHir,
            finalHir.Source.Structure.RequireNode(secondHirCallable));
        var firstCallable = Callable(new MirCallableId(0), firstOrigin, "First", "10");
        var secondCallable = Callable(new MirCallableId(1), secondOrigin, "Second", "20");
        return context.Program(firstCallable, new[] { firstCallable, secondCallable });
    }

    private static MirCallable Callable(
        MirCallableId id,
        MirOrigin source,
        string name,
        string constantText)
    {
        var blockId = MirTestContext.BlockId(0);
        var instructionId = new MirInstructionId(0);
        var valueId = new MirValueId(0);
        var value = new MirValue(
            valueId,
            MirType.Scalar(QType.Int),
            source);
        var constant = new MirConstant(
            instructionId,
            value,
            constantText,
            source);
        var block = new MirBlock(
            blockId,
            Array.Empty<MirValue>(),
            new MirInstruction[] { constant },
            new MirReturn(Value: null, source),
            source);

        return new MirCallable(
            id,
            name,
            kind: MirCallableKind.Operation,
            returnType: null,
            Array.Empty<IMirParameter>(),
            new[] { block },
            source);
    }

}
