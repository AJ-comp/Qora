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
    public void SnapshotOwnershipComesFromExactHirAndPreviousSnapshot()
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

        Assert.Equal(MirStage.Lowered, snapshot.Stage);
        Assert.Null(snapshot.PreviousSnapshot);
        Assert.Same(hir.FinalHir, snapshot.HirArtifact);
        Assert.Same(program, snapshot.Program);

        var transformedProgram = CopyProgram(program);
        var transformed = MirSnapshot.CreateTransformed(
            transformedProgram,
            snapshot);

        Assert.Equal(MirStage.InverseRequestsInjected, transformed.Stage);
        Assert.Same(snapshot, transformed.PreviousSnapshot);
        Assert.Same(snapshot.HirArtifact, transformed.HirArtifact);
        Assert.Same(transformedProgram, transformed.Program);
        Assert.Same(
            snapshot.Program.RequireCallable(new MirCallableId(0)).Origin,
            transformed.Program.RequireCallable(new MirCallableId(0)).Origin);

        Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                program,
                snapshot));
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
    public void TransformedSnapshotRejectsAProgramFromAnotherHirArtifact()
    {
        var source = SnapshotWithEveryIdentityKind();
        var unrelated = SnapshotWithEveryIdentityKind();

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(unrelated.Program, source));

        Assert.Equal("program", error.ParamName);
        Assert.Contains("source HIR artifact", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("callable")]
    [InlineData("block")]
    [InlineData("instruction")]
    [InlineData("SSA value")]
    [InlineData("storage")]
    [InlineData("qubit version")]
    public void TransformedSnapshotRejectsRenumberedEntityIdentity(
        string entityKind)
    {
        var source = SnapshotWithEveryIdentityKind();
        var transformedProgram = RenumberIdentity(
            source.Program,
            entityKind);
        QoraMirVerifier.VerifyOrThrow(transformedProgram);

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                transformedProgram,
                source));

        Assert.Equal("program", error.ParamName);
        Assert.Contains(entityKind, error.Message, StringComparison.Ordinal);
        Assert.Contains("renumber", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformedSnapshotRejectsRebindingAnExistingIdentity()
    {
        var source = SnapshotWithEveryIdentityKind();
        var sourceCallable = Assert.Single(source.Program.Callables);
        var reboundCallable = CopyCallable(
            sourceCallable,
            origin: MirOrigin.GeneratedFrom(
                sourceCallable.Origin,
                "replacement callable"));
        var transformedProgram = new MirProgram(
            reboundCallable,
            new[] { reboundCallable });
        QoraMirVerifier.VerifyOrThrow(transformedProgram);

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                transformedProgram,
                source));

        Assert.Equal("program", error.ParamName);
        Assert.Contains("rebind callable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletedIdentityCannotBeReusedByALaterTransformation()
    {
        var source = SnapshotWithTwoCallables();
        var retainedCallable = source.Program.RequireCallable(new MirCallableId(0));
        var deletedCallable = source.Program.RequireCallable(new MirCallableId(1));
        var deletionProgram = new MirProgram(
            retainedCallable,
            new[] { retainedCallable });
        var afterDeletion = MirSnapshot.CreateTransformed(
            deletionProgram,
            source);
        var replacement = CopyCallable(
            deletedCallable,
            origin: MirOrigin.GeneratedFrom(
                deletedCallable.Origin,
                "replacement after deletion"));
        var reuseProgram = new MirProgram(
            retainedCallable,
            new[] { retainedCallable, replacement });
        QoraMirVerifier.VerifyOrThrow(reuseProgram);

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                reuseProgram,
                afterDeletion));

        Assert.Equal("program", error.ParamName);
        Assert.Contains("reuse deleted callable identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdjointTransformsSatisfySnapshotIdentityContinuity()
    {
        const string sourceText =
            """
            operation Worker(q: Qubit) {
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                var values: int[] = [0];
                values[0] = 1;
                Worker(q[0]);
            }
            """;
        var compilation = QoraCompiler.Compile(sourceText);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(
                    diagnostic => diagnostic.Error.Message)));
        var source = Assert.IsType<MirSnapshot>(compilation.Mir);
        var worker = source.Program.Callables.Single(callable => callable.Name == "Worker");
        var main = source.Program.Callables.Single(callable => callable.Name == "Main");
        var call = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Single(apply =>
                apply.Target is MirUserCallableTarget target
                && target.Callable == worker.Id);
        var store = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirArrayStore>()
            .Single();
        Assert.NotSame(store.Origin, store.Result.Origin);
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { new MirInstructionSite(main.Id, call.Id) });
        var materialized = MirAdjointMaterializer.Run(injected);
        var output = Assert.IsType<MirSnapshot>(materialized.Output);
        var transformedMain = injected.Program.RequireCallable(main.Id);
        var transformedStore = Assert.IsType<MirArrayStore>(
            transformedMain.RequireInstruction(store.Id));

        Assert.Same(source, injected.PreviousSnapshot);
        Assert.Same(injected, output.PreviousSnapshot);
        Assert.Same(store.Origin, transformedStore.Origin);
        Assert.Same(store.Result.Origin, transformedStore.Result.Origin);
        Assert.All(
            source.Program.Callables,
            callable => Assert.True(output.Program.ContainsCallable(callable.Id)));
        var inverseId = Assert.Single(materialized.Inverses).Value;
        Assert.True(inverseId.Value > source.Program.Callables.Max(callable => callable.Id.Value));
        Assert.IsType<MirGeneratedOrigin>(output.Program.RequireCallable(inverseId).Origin);
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
            firstCallable.DefinitionOf(firstValue).Instruction);
        Assert.Equal(
            new MirInstructionId(0),
            secondCallable.DefinitionOf(secondValue).Instruction);
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

    private static MirSnapshot SnapshotWithEveryIdentityKind()
    {
        var hir = HirFixtureFor("Main");
        var hirCallableId = CallableId(hir, "Main");
        var origin = MirOrigin.FromHirNode(
            hir.FinalHir,
            hir.FinalHir.Source.Structure.RequireNode(hirCallableId));
        var blockId = MirTestContext.BlockId(0);
        var constantInstructionId = new MirInstructionId(0);
        var arrayInstructionId = new MirInstructionId(1);
        var qubitInstructionId = new MirInstructionId(2);
        var scalarValueId = new MirValueId(0);
        var arrayValueId = new MirValueId(1);
        var storageId = new MirStorageId(0);
        var scalarValue = new MirValue(
            scalarValueId,
            MirType.Scalar(QType.Int),
            origin);
        var arrayValue = new MirValue(
            arrayValueId,
            MirType.Array(QType.Int, knownLength: 1),
            origin);
        var storage = new MirArrayStorage(
            storageId,
            "values",
            origin);
        var qubit = new MirQubitFromUse(
            new MirQubitId(0),
            "q",
            length: 1,
            origin);
        var block = new MirBlock(
            blockId,
            Array.Empty<MirValue>(),
            new MirInstruction[]
            {
                new MirConstant(
                    constantInstructionId,
                    scalarValue,
                    "0",
                    origin),
                new MirArrayCreate(
                    arrayInstructionId,
                    arrayValue,
                    storage,
                    MirArrayInitialization.ZeroInitialized,
                    Array.Empty<MirValueId>(),
                    origin),
                new MirQubitAllocate(
                    qubitInstructionId,
                    qubit,
                    origin),
            },
            new MirReturn(Value: null, origin),
            origin);
        var callable = new MirCallable(
            new MirCallableId(0),
            "Main",
            returnType: null,
            Array.Empty<IMirParameter>(),
            block,
            new[] { block },
            origin);
        var program = new MirProgram(
            callable,
            new[] { callable });
        return MirSnapshot.CreateLowered(program);
    }

    private static MirProgram RenumberIdentity(
        MirProgram source,
        string entityKind)
    {
        var callable = Assert.Single(source.Callables);
        var block = Assert.Single(callable.Blocks);

        switch (entityKind)
        {
            case "callable":
            {
                var renumbered = CopyCallable(
                    callable,
                    id: new MirCallableId(1));
                return new MirProgram(
                    renumbered,
                    new[] { renumbered });
            }

            case "block":
            {
                var renumbered = block with { Id = MirTestContext.BlockId(1) };
                return ProgramWith(
                    CopyCallable(
                        callable,
                        entryBlock: renumbered,
                        blocks: new[] { renumbered }));
            }

            case "instruction":
            {
                var sourceConstant = Assert.IsType<MirConstant>(block.Instructions[0]);
                var renumberedInstructionId = new MirInstructionId(3);
                var clonedResult = CloneValue(sourceConstant.Result);
                var instructions = block.Instructions.ToArray();
                instructions[0] = new MirConstant(
                    renumberedInstructionId,
                    clonedResult,
                    sourceConstant.Text,
                    sourceConstant.Origin);
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } }));
            }

            case "SSA value":
            {
                var sourceConstant = Assert.IsType<MirConstant>(block.Instructions[0]);
                var renumberedValue = new MirValue(
                    new MirValueId(2),
                    sourceConstant.Result.Type,
                    sourceConstant.Result.Origin);
                var instructions = block.Instructions.ToArray();
                instructions[0] = new MirConstant(
                    sourceConstant.Id,
                    renumberedValue,
                    sourceConstant.Text,
                    sourceConstant.Origin);
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } }));
            }

            case "storage":
            {
                var sourceCreate = Assert.IsType<MirArrayCreate>(block.Instructions[1]);
                var renumberedStorage = new MirArrayStorage(
                    new MirStorageId(1),
                    sourceCreate.Storage.Name,
                    sourceCreate.Storage.Origin);
                var instructions = block.Instructions.ToArray();
                instructions[1] = new MirArrayCreate(
                    sourceCreate.Id,
                    CloneValue(sourceCreate.Result),
                    renumberedStorage,
                    sourceCreate.Initialization,
                    sourceCreate.Elements,
                    sourceCreate.Origin);
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } }));
            }

            case "qubit version":
            {
                var sourceAllocation = Assert.IsType<MirQubitAllocate>(block.Instructions[2]);
                var renumberedQubit = new MirQubitFromUse(
                    new MirQubitId(1),
                    sourceAllocation.Result.Name,
                    sourceAllocation.Result.Length,
                    sourceAllocation.Result.Origin);
                var instructions = block.Instructions.ToArray();
                instructions[2] = new MirQubitAllocate(
                    sourceAllocation.Id,
                    renumberedQubit,
                    sourceAllocation.Origin);
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } }));
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(entityKind),
                    entityKind,
                    "unknown MIR entity kind");
        }
    }

    private static MirProgram ProgramWith(MirCallable callable) =>
        new(
            callable,
            new[] { callable });

    private static MirCallable CopyCallable(
        MirCallable source,
        MirCallableId? id = null,
        MirBlock? entryBlock = null,
        IReadOnlyList<MirBlock>? blocks = null,
        MirOrigin? origin = null)
    {
        var copiedBlocks = blocks ?? source.Blocks;
        var copiedEntryBlock = entryBlock
            ?? copiedBlocks.Single(block => block.Id == source.EntryBlock.Id);
        return new MirCallable(
            id ?? source.Id,
            source.Name,
            source.ReturnType,
            source.Parameters,
            copiedEntryBlock,
            copiedBlocks,
            origin ?? source.Origin);
    }

    private static MirSnapshot Snapshot(MirProgram program) =>
        MirSnapshot.CreateLowered(program);

    private static MirProgram CopyProgram(MirProgram source) =>
        new(
            source.EntryPoint,
            source.Callables);

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
            returnType: null,
            Array.Empty<IMirParameter>(),
            block,
            new[] { block },
            source);
    }

    private static MirValue CloneValue(MirValue source) =>
        new(
            source.Id,
            source.Type,
            source.Origin);
}
