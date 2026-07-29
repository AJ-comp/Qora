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
        var snapshot = new MirSnapshot(
            snapshotId,
            MirLoweringProfile.CanonicalV1,
            program,
            hir.FinalHir);

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
            () => new MirSnapshot(
                snapshotId,
                MirLoweringProfile.CanonicalV1,
                program,
                unrelatedHir.FinalHir));
        Assert.Throws<ArgumentException>(
            () => new MirSnapshot(
                new MirSnapshotId(compilation, compilationRevision, revision: 4),
                MirLoweringProfile.CanonicalV1,
                program,
                hir.FinalHir));
    }

    [Fact]
    public void CallableLocalIdsRemainIsolatedByTheirOwningCallable()
    {
        var snapshot = SnapshotWithTwoCallables();
        var firstCallable = snapshot.Program.RequireCallable(new MirCallableId(0));
        var secondCallable = snapshot.Program.RequireCallable(new MirCallableId(1));
        var blockId = new MirBlockId(0);
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
        Assert.True(firstCallable.ContainsBlock(firstBlock));
        Assert.True(firstCallable.ContainsInstruction(firstInstruction));
        Assert.True(firstCallable.ContainsValue(firstValue));
        Assert.False(firstCallable.ContainsBlock(secondBlock));
        Assert.False(firstCallable.ContainsInstruction(secondInstruction));
        Assert.False(firstCallable.ContainsValue(secondValue));
        Assert.Throws<ArgumentException>(() => firstCallable.RequireBlock(secondBlock));
        Assert.Throws<ArgumentException>(
            () => firstCallable.RequireInstruction(secondInstruction));
        Assert.Throws<ArgumentException>(() => firstCallable.RequireValue(secondValue));

        var firstHirCallable = snapshot.Origins.ResolveHir(
            firstBlock.Origin).HirCallableId;
        var secondHirCallable = snapshot.Origins.ResolveHir(
            secondBlock.Origin).HirCallableId;
        Assert.NotNull(firstHirCallable);
        Assert.NotNull(secondHirCallable);
        Assert.NotEqual(firstHirCallable, secondHirCallable);
        Assert.Equal(
            "10",
            Assert.IsType<MirConstant>(firstInstruction).Constant.Text);
        Assert.Equal(
            "20",
            Assert.IsType<MirConstant>(secondInstruction).Constant.Text);

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
            firstValue.Definition.Instruction);
        Assert.Equal(
            new MirInstructionId(0),
            secondValue.Definition.Instruction);
    }

    [Fact]
    public async Task AnalysisStoreCachesResultsAndReusesOneCfgPerCallable()
    {
        var snapshot = SnapshotWithTwoCallables();
        var callable = snapshot.Program.RequireCallable(new MirCallableId(0));
        var analyses = snapshot.Analyses;

        var cfg = analyses.ControlFlow(callable);
        var provenance = analyses.StorageProvenance(callable);
        var memory = analyses.MemoryState(callable);
        var paths = analyses.PathConditions(callable);
        var scalars = analyses.ScalarAvailability(callable);
        var effects = analyses.Effects;
        var witnesses = analyses.WitnessAvailability(callable);

        Assert.Same(cfg, analyses.ControlFlow(callable.Id));
        Assert.Same(provenance, analyses.StorageProvenance(callable.Id));
        Assert.Same(memory, analyses.MemoryState(callable.Id));
        Assert.Same(paths, analyses.PathConditions(callable.Id));
        Assert.Same(scalars, analyses.ScalarAvailability(callable.Id));
        Assert.Same(effects, analyses.Effects);
        Assert.Same(witnesses, analyses.WitnessAvailability(callable.Id));

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

        Assert.False(second.Program.ContainsCallable(firstCallable));
        Assert.False(secondCallable.ContainsBlock(firstBlock));
        Assert.False(secondCallable.ContainsInstruction(firstInstruction));
        Assert.False(secondCallable.ContainsValue(firstValue));
        Assert.False(secondCallable.ContainsStorage(firstStorage));
        Assert.False(secondCallable.ContainsQubit(firstQubit));

        Assert.Throws<ArgumentException>(
            () => second.Program.RequireCallable(firstCallable));
        Assert.Throws<ArgumentException>(
            () => unrelated.Program.RequireCallable(firstCallable));
        Assert.Throws<ArgumentException>(() => secondCallable.RequireBlock(firstBlock));
        Assert.Throws<ArgumentException>(
            () => secondCallable.RequireInstruction(firstInstruction));
        Assert.Throws<ArgumentException>(() => secondCallable.RequireValue(firstValue));
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
        var firstCfg = first.Analyses.ControlFlow(firstCallable);
        var secondCfg = second.Analyses.ControlFlow(secondCallable);
        var foreignPoint = firstCfg.TerminatorPoint(firstBlock.Id);
        Assert.Throws<InvalidOperationException>(
            () => secondCfg.IsValueAvailableAt(secondValue.Id, foreignPoint));
        Assert.Throws<InvalidOperationException>(
            () => first.Analyses.Effects.EnsureFor(second.Program));

        Assert.Throws<ArgumentException>(
            () => second.Origins.Require(firstInstruction.Origin));
        Assert.Throws<ArgumentException>(
            () => second.Origins.ResolveHir(firstInstruction.Origin));
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
        return new MirSnapshot(
            snapshotId,
            MirLoweringProfile.CanonicalV1,
            program,
            hir.FinalHir);
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
