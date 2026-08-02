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
    public void SnapshotOwnershipComesFromItsExactHirAndTransformationSources()
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
        var snapshot = MirSnapshot.CreateLowered(
            program,
            hir.FinalHir);

        Assert.Equal(MirStage.Lowered, snapshot.Stage);
        Assert.Null(snapshot.TransformationSource);
        Assert.Same(hir.FinalHir, snapshot.LoweringSource);
        Assert.Same(program, snapshot.Program);

        var transformedProgram = CopyProgram(program);
        var transformed = MirSnapshot.CreateTransformed(
            transformedProgram,
            MirStage.InverseRequestsInjected,
            snapshot);

        Assert.Equal(MirStage.InverseRequestsInjected, transformed.Stage);
        Assert.Same(snapshot, transformed.TransformationSource);
        Assert.Same(snapshot.LoweringSource, transformed.LoweringSource);
        Assert.Same(transformedProgram, transformed.Program);
        Assert.Same(
            snapshot.Program.RequireCallable(new MirCallableId(0)).Origin,
            transformed.Program.RequireCallable(new MirCallableId(0)).Origin);

        Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                program,
                MirStage.InverseRequestsInjected,
                snapshot));
        Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                CopyProgram(program),
                MirStage.AdjointsMaterialized,
                snapshot));
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
        QoraMirVerifier.VerifyOrThrow(
            transformedProgram,
            source.LoweringSource.Source);

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                transformedProgram,
                MirStage.InverseRequestsInjected,
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
            origin: new MirGeneratedOrigin(
                sourceCallable.Origin,
                "replacement callable"));
        var transformedProgram = new MirProgram(
            source.Program.EntryPoint,
            new[] { reboundCallable });
        QoraMirVerifier.VerifyOrThrow(
            transformedProgram,
            source.LoweringSource.Source);

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                transformedProgram,
                MirStage.InverseRequestsInjected,
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
            retainedCallable.Id,
            new[] { retainedCallable });
        var afterDeletion = MirSnapshot.CreateTransformed(
            deletionProgram,
            MirStage.InverseRequestsInjected,
            source);
        var replacement = CopyCallable(
            deletedCallable,
            origin: new MirGeneratedOrigin(
                deletedCallable.Origin,
                "replacement after deletion"));
        var reuseProgram = new MirProgram(
            retainedCallable.Id,
            new[] { retainedCallable, replacement });
        QoraMirVerifier.VerifyOrThrow(
            reuseProgram,
            source.LoweringSource.Source);

        var error = Assert.Throws<ArgumentException>(
            () => MirSnapshot.CreateTransformed(
                reuseProgram,
                MirStage.AdjointsMaterialized,
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
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { new MirInstructionSite(main.Id, call.Id) });
        var materialized = MirAdjointMaterializer.Run(injected);
        var output = Assert.IsType<MirSnapshot>(materialized.Output);

        Assert.Same(source, injected.TransformationSource);
        Assert.Same(injected, output.TransformationSource);
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

        var hirStructure = snapshot.LoweringSource.Source.Structure;
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
            first.LoweringSource.SourceId.CompilationId);
        Assert.Equal(
            secondCompilation.Revision,
            second.LoweringSource.SourceId.CompilationRevision);
        Assert.NotSame(first.LoweringSource, second.LoweringSource);
        Assert.NotSame(first.LoweringSource, unrelated.LoweringSource);

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
            first.LoweringSource.Source.SourceMap.Find(
                firstInstruction.Origin.SourceHirOrigin.HirNodeId),
            firstInstruction.Origin.SourceHirOrigin.Span);
        Assert.Equal(
            second.LoweringSource.Source.SourceMap.Find(
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
        return Snapshot(program, hir);
    }

    private static MirSnapshot SnapshotWithEveryIdentityKind()
    {
        var hir = HirFixtureFor("Main");
        var hirCallableId = CallableId(hir, "Main");
        var origin = new MirHirOrigin(
            hirCallableId,
            hir.FinalHir.Source.SourceMap.Find(hirCallableId));
        var blockId = new MirBlockId(0);
        var constantInstructionId = new MirInstructionId(0);
        var arrayInstructionId = new MirInstructionId(1);
        var qubitInstructionId = new MirInstructionId(2);
        var scalarValueId = new MirValueId(0);
        var arrayValueId = new MirValueId(1);
        var storageId = new MirStorageId(0);
        var qubit = new MirQubitFromUse(
            new MirQubitId(0),
            "q",
            length: 1,
            origin);
        var block = new MirBlock(
            blockId,
            Array.Empty<MirValueId>(),
            new MirInstruction[]
            {
                new MirConstant(
                    constantInstructionId,
                    scalarValueId,
                    "0",
                    origin),
                new MirArrayCreate(
                    arrayInstructionId,
                    arrayValueId,
                    storageId,
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
            blockId,
            new[] { block },
            new[]
            {
                new MirValue(
                    scalarValueId,
                    MirType.Scalar(QType.Int),
                    MirValueDefinition.InstructionResultAt(constantInstructionId),
                    origin),
                new MirValue(
                    arrayValueId,
                    MirType.Array(QType.Int, knownLength: 1),
                    MirValueDefinition.InstructionResultAt(arrayInstructionId),
                    origin),
            },
            new[] { new MirArrayStorage(storageId, "values", origin) },
            origin);
        var program = new MirProgram(
            callable.Id,
            new[] { callable });
        return MirSnapshot.CreateLowered(
            program,
            hir.FinalHir);
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
                    renumbered.Id,
                    new[] { renumbered });
            }

            case "block":
            {
                var renumbered = block with { Id = new MirBlockId(1) };
                return ProgramWith(
                    CopyCallable(
                        callable,
                        entryBlock: renumbered.Id,
                        blocks: new[] { renumbered }));
            }

            case "instruction":
            {
                var sourceConstant = Assert.IsType<MirConstant>(block.Instructions[0]);
                var renumberedInstructionId = new MirInstructionId(3);
                var instructions = block.Instructions.ToArray();
                instructions[0] = sourceConstant with { Id = renumberedInstructionId };
                var values = callable.Values
                    .Select(value => value.Id == sourceConstant.Result
                        ? value with
                        {
                            Definition = MirValueDefinition.InstructionResultAt(
                                renumberedInstructionId),
                        }
                        : value)
                    .ToArray();
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } },
                        values: values));
            }

            case "SSA value":
            {
                var sourceConstant = Assert.IsType<MirConstant>(block.Instructions[0]);
                var renumberedValueId = new MirValueId(2);
                var instructions = block.Instructions.ToArray();
                instructions[0] = sourceConstant with { Result = renumberedValueId };
                var values = callable.Values
                    .Select(value => value.Id == sourceConstant.Result
                        ? value with { Id = renumberedValueId }
                        : value)
                    .ToArray();
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } },
                        values: values));
            }

            case "storage":
            {
                var sourceCreate = Assert.IsType<MirArrayCreate>(block.Instructions[1]);
                var renumberedStorageId = new MirStorageId(1);
                var instructions = block.Instructions.ToArray();
                instructions[1] = sourceCreate with { Storage = renumberedStorageId };
                var storage = Assert.Single(callable.Storages);
                return ProgramWith(
                    CopyCallable(
                        callable,
                        blocks: new[] { block with { Instructions = instructions } },
                        storages: new[] { storage with { Id = renumberedStorageId } }));
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
            callable.Id,
            new[] { callable });

    private static MirCallable CopyCallable(
        MirCallable source,
        MirCallableId? id = null,
        MirBlockId? entryBlock = null,
        IReadOnlyList<MirBlock>? blocks = null,
        IReadOnlyList<MirValue>? values = null,
        IReadOnlyList<MirArrayStorage>? storages = null,
        MirOrigin? origin = null) =>
        new(
            id ?? source.Id,
            source.Name,
            source.ReturnType,
            source.Parameters,
            entryBlock ?? source.EntryBlock,
            blocks ?? source.Blocks,
            values ?? source.Values,
            storages ?? source.Storages,
            origin ?? source.Origin);

    private static MirSnapshot Snapshot(
        MirProgram program,
        HirFixture hir)
    {
        return MirSnapshot.CreateLowered(
            program,
            hir.FinalHir);
    }

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
        var firstOrigin = new MirHirOrigin(
            firstHirCallable,
            finalHir.Source.SourceMap.Find(firstHirCallable));
        var secondOrigin = new MirHirOrigin(
            secondHirCallable,
            finalHir.Source.SourceMap.Find(secondHirCallable));
        return context.Program(
            new MirCallableId(0),
            new[]
            {
                Callable(new MirCallableId(0), firstOrigin, "First", "10"),
                Callable(new MirCallableId(1), secondOrigin, "Second", "20"),
            });
    }

    private static MirCallable Callable(
        MirCallableId id,
        MirOrigin source,
        string name,
        string constantText)
    {
        var blockId = new MirBlockId(0);
        var instructionId = new MirInstructionId(0);
        var valueId = new MirValueId(0);
        var constant = new MirConstant(
            instructionId,
            valueId,
            constantText,
            source);
        var value = new MirValue(
            valueId,
            MirType.Scalar(QType.Int),
            MirValueDefinition.InstructionResultAt(instructionId),
            Origin: source);
        var block = new MirBlock(
            blockId,
            Array.Empty<MirValueId>(),
            new MirInstruction[] { constant },
            new MirReturn(Value: null, source),
            source);

        return new MirCallable(
            id,
            name,
            returnType: null,
            Array.Empty<IMirParameter>(),
            blockId,
            new[] { block },
            new[] { value },
            Array.Empty<MirArrayStorage>(),
            source);
    }
}
