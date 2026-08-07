using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class QoraMirTests
{
    [Fact]
    public void ScalarReassignmentCreatesDistinctSsaValuesAndEffectsKeepTheForwardWitness()
    {
        var snapshot = CompileSnapshot("""
            operation FlipIf(flag: int, target: Qubit) {
                if (flag == 1) {
                    X(target);
                }
            }

            operation Main() {
                use register = Qubit[1];
                var flag: int = 1;
                FlipIf(flag, register[0]);
                flag = 0;
                FlipIf(flag, register[0]);
            }
            """);
        var program = snapshot.Program;
        var effects = snapshot.Analyses.Effects;

        var flipIf = Callable(program, "FlipIf");
        var main = Callable(program, "Main");
        var calls = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Where(call => call.Target is MirUserCallableTarget target
                && target.Callable == flipIf.Id)
            .ToArray();
        Assert.Equal(2, calls.Length);

        var firstQubitResult = Assert.Single(calls[0].QubitResults);
        var secondQubitInput = Assert.IsType<MirQubitCallOperand>(
            calls[1].Operands[1]);
        Assert.Equal(firstQubitResult.Key, secondQubitInput.Qubit.Qubit);
        Assert.Equal(
            2,
            Assert.Single(calls[1].QubitResults).Version.Value);

        var firstInput = Assert.IsType<MirClassicalCallOperand>(calls[0].Operands[0]).Value;
        var secondInput = Assert.IsType<MirClassicalCallOperand>(calls[1].Operands[0]).Value;
        Assert.NotEqual(firstInput, secondInput);

        var firstWitness = Assert.Single(
            EffectFor(effects, main.Id, calls[0].Id).ClassicalWitnesses,
            witness => witness.Role == MirClassicalWitnessRole.CallOperand);
        var secondWitness = Assert.Single(
            EffectFor(effects, main.Id, calls[1].Id).ClassicalWitnesses,
            witness => witness.Role == MirClassicalWitnessRole.CallOperand);
        Assert.Equal(firstInput, firstWitness.Value);
        Assert.Equal(secondInput, secondWitness.Value);
        Assert.NotEqual(firstWitness.Value, secondWitness.Value);
    }

    [Fact]
    public void IfMergeUsesABlockArgumentAndTheFollowingCallReadsThatPhiValue()
    {
        var snapshot = CompileSnapshot("""
            operation FlipIf(flag: int, target: Qubit) {
                if (flag == 1) {
                    X(target);
                }
            }

            operation Main() {
                use register = Qubit[1];
                var flag: int = 0;
                if (1 == 1) {
                    flag = 1;
                } else {
                    flag = 2;
                }
                FlipIf(flag, register[0]);
            }
            """);
        var program = snapshot.Program;

        var flipIf = Callable(program, "FlipIf");
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget target
                && target.Callable == flipIf.Id);
        var mergedInput = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        var mergedValue = Assert.IsType<MirValue>(main.FindValue(mergedInput));
        var mergedDefinition = main.DefinitionOf(mergedValue.Id);

        Assert.Equal(MirValueDefinitionKind.BlockArgument, mergedDefinition.Kind);
        var mergeBlockId = Assert.IsType<MirBlockId>(mergedDefinition.Block);
        var mergeBlock = Assert.IsType<MirBlock>(main.FindBlock(mergeBlockId));
        var blockArgument = Assert.Single(
            mergeBlock.Arguments,
            argument => argument.Id == mergedInput);
        var incoming = IncomingArguments(main, mergeBlock.Id);
        Assert.Equal(2, incoming.Count);
        Assert.All(incoming, arguments => Assert.Equal(mergeBlock.Arguments.Count, arguments.Count));
        var argumentIndex = blockArgumentIndex(mergeBlock, blockArgument);
        Assert.Equal(
            2,
            incoming.Select(arguments => arguments[argumentIndex]).Distinct().Count());
        static int blockArgumentIndex(MirBlock block, MirValue argument) =>
            block.Arguments.ToList().FindIndex(candidate => candidate.Id == argument.Id);
    }

    [Fact]
    public void WhileAndForHeadersCarryLoopValuesThroughBackedgeArguments()
    {
        var snapshot = CompileSnapshot("""
            operation WhileLoop() {
                var value: int = 0;
                while (value < 2) {
                    value = value + 1;
                }
            }

            operation ForLoop() {
                var total: int = 0;
                for index in 0..2 {
                    total = total + index;
                }
            }
            """);
        var program = snapshot.Program;

        AssertLoopHasBackedgeArguments(Callable(program, "WhileLoop"));
        AssertLoopHasBackedgeArguments(Callable(program, "ForLoop"));
    }

    [Fact]
    public void ArrayStoreAndMutableBorrowedCallProduceNewStatesWithOriginalStorageProvenance()
    {
        var (program, effects) = CompileMir("""
            operation Touch(var values: int[], target: Qubit) {
                values[0] = values[0] + 1;
                X(target);
            }

            operation Main() {
                use register = Qubit[1];
                var values: int[] = [1];
                values[0] = 2;
                Touch(var values, register[0]);
            }
            """);

        var touch = Callable(program, "Touch");
        var main = Callable(program, "Main");
        var create = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirArrayCreate>());
        var store = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirArrayStore>());
        Assert.Equal(create.Result.Id, store.Array);
        Assert.NotEqual(store.Array, store.Result.Id);

        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget target
                && target.Callable == touch.Id);
        var inputState = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        Assert.Equal(store.Result.Id, inputState);
        var transition = Assert.Single(call.MutableArrayResults);
        Assert.Equal(0, transition.OperandIndex);
        Assert.NotEqual(inputState, transition.Result.Id);

        var arrayEffect = Assert.Single(
            EffectFor(effects, main.Id, call.Id).ArrayStates);
        Assert.Equal(inputState, arrayEffect.InputState);
        Assert.Equal(transition.Result.Id, arrayEffect.OutputState);
        Assert.True(arrayEffect.Storage.IsComplete);
        Assert.Equal(
            new[] { create.Storage.Id },
            arrayEffect.Storage.PossibleStorages);
    }

    [Fact]
    public void ShadowedArraysWithTheSameSpellingHaveDistinctStorageIdentity()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                var values: int[] = [1];
                if (1 == 1) {
                    var values: int[] = [2];
                    values[0] = 3;
                }
                values[0] = 4;
            }
            """);
        var program = snapshot.Program;

        var main = Callable(program, "Main");
        var storages = main.Storages
            .Where(storage => storage.Name == "values")
            .OrderBy(storage => storage.Id.Value)
            .ToArray();
        Assert.Equal(2, storages.Length);
        Assert.NotEqual(storages[0].Id, storages[1].Id);

        var creates = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirArrayCreate>()
            .ToArray();
        Assert.Equal(2, creates.Length);
        Assert.Equal(2, creates.Select(create => create.Storage).Distinct().Count());
    }

    [Fact]
    public void ShadowedScalarAssignmentsKeepIndependentSsaIdentity()
    {
        var compilation = Compiler.Compile("""
            operation Observe(value: int, target: Qubit) {
                X(target);
            }

            operation Main() {
                use q = Qubit[1];
                var value: int = 1;
                Observe(value, q[0]);

                if (true) {
                    var value: int = value + 1;
                    Observe(value, q[0]);

                    value = 3;
                    Observe(value, q[0]);
                }

                value = 4;
                Observe(value, q[0]);
            }
            """);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        var snapshot = Assert.IsType<MirSnapshot>(compilation.Mir);
        var program = snapshot.Program;
        var observe = Callable(program, "Observe");
        var main = Callable(program, "Main");
        var calls = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Where(instruction =>
                instruction.Target is MirUserCallableTarget target
                && target.Callable == observe.Id)
            .ToArray();
        Assert.Equal(4, calls.Length);

        var values = calls
            .Select(call =>
                Assert.IsType<MirClassicalCallOperand>(
                    call.Operands[0]).Value)
            .ToArray();
        Assert.Equal(4, values.Distinct().Count());
    }

    [Fact]
    public void UserCallTargetsCallableIdAndKeepsTheExactDynamicQubitIndexValue()
    {
        var snapshot = CompileSnapshot("""
            operation Apply(target: Qubit) {
                X(target);
            }

            operation Main() {
                use register = Qubit[2];
                var index: int = 1;
                if (0 <= index && index < register.Count) {
                    Apply(register[index]);
                }
            }
            """);
        var program = snapshot.Program;
        var effects = snapshot.Analyses.Effects;

        var apply = Callable(program, "Apply");
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget);
        var target = Assert.IsType<MirUserCallableTarget>(call.Target);
        Assert.Equal(apply.Id, target.Callable);

        var qubitOperand = Assert.IsType<MirQubitCallOperand>(Assert.Single(call.Operands));
        var index = Assert.IsType<MirValueId>(qubitOperand.Qubit.Index);
        _ = Assert.IsType<MirValue>(main.FindValue(index));

        var witness = Assert.Single(
            EffectFor(effects, main.Id, call.Id).ClassicalWitnesses,
            candidate => candidate.Role == MirClassicalWitnessRole.QubitIndex);
        Assert.Equal(index, witness.Value);
    }

    [Fact]
    public void HirCallableNodeIdsArePreservedAsMirCallableIds()
    {
        var compilation = Compiler.Compile("""
            operation Main() {
                use target = Qubit[1];
                Apply(target[0]);
            }

            operation Apply(target: Qubit) {
                X(target);
            }
            """);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));

        var hirArtifact = compilation.Hir.EffectAnalysis!;
        var mirSnapshot = Assert.IsType<MirSnapshot>(compilation.Mir);

        foreach (var hirCallable in hirArtifact.Program.Callables)
        {
            var mirCallable = Assert.Single(
                mirSnapshot.Program.Callables,
                candidate => candidate.Name == hirCallable.Name);
            Assert.Equal(
                hirCallable.Id.Value,
                mirCallable.Id.Value);
        }

        Assert.Equal(
            hirArtifact.Program.EntryCallable!.Id.Value,
            mirSnapshot.Program.EntryPoint.Id.Value);
    }

    [Fact]
    public void MirIdentityConstructionRejectsNegativeValuesImmediately()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirCallableId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirInstructionId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirValueId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirStorageId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirQubitId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirQubitVersion(-1));
    }

    [Fact]
    public void MirBlockIdAllocatorAssignsSequentialNonNegativeValues()
    {
        var allocator = new MirBlockId.Allocator();

        Assert.Equal(0, allocator.Allocate().Value);
        Assert.Equal(1, allocator.Allocate().Value);
    }

    [Fact]
    public void MirQubitParameterArrayConstructionRejectsZeroLengthImmediately()
    {
        var context = MirTestContext.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MirQubitParameter.Array(
                new MirQubitId(0),
                "register",
                length: 0,
                QOwnershipMode.Borrowed,
                context.Origin()));
    }

    [Fact]
    public void MirQubitFromUseConstructionRejectsZeroLengthImmediately()
    {
        var context = MirTestContext.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MirQubitFromUse(
                new MirQubitId(0),
                "register",
                length: 0,
                context.Origin()));
    }

    [Fact]
    public void MirQubitAfterInstructionConstructionRejectsVersionZeroImmediately()
    {
        var context = MirTestContext.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MirQubitAfterInstruction(
                new MirQubitId(0),
                new MirQubitVersion(0),
                context.Origin()));
    }

    [Fact]
    public void MirQubitPhiConstructionRejectsVersionZeroImmediately()
    {
        var context = MirTestContext.Create();
        var qubit = MirQubitParameter.Single(
            new MirQubitId(0),
            "target",
            QOwnershipMode.Borrowed,
            context.Origin());
        var input = new MirQubitPhiInput(
            new MirControlFlowEdge(MirTestContext.BlockId(0), successorOrdinal: 0),
            qubit);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MirQubitPhi(
                new MirQubitVersion(0),
                new[] { input },
                context.Origin()));

        Assert.Throws<ArgumentException>(
            () => new MirQubitPhi(
                new MirQubitVersion(1),
                Array.Empty<MirQubitPhiInput>(),
                context.Origin()));
    }

    [Fact]
    public void MirQubitPhiAndBlockConstructionRejectInvalidPhiShapesImmediately()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use target = Qubit[1];
                if (1 == 1) {
                    X(target[0]);
                } else {
                    H(target[0]);
                }
                Z(target[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var merge = Assert.Single(callable.Blocks, block => block.QubitPhis.Count != 0);
        var phi = Assert.Single(merge.QubitPhis);

        Assert.Throws<ArgumentException>(() => phi with
        {
            Inputs = Array.Empty<MirQubitPhiInput>(),
        });

        Assert.Throws<ArgumentException>(() => phi with
        {
            Inputs = phi.Inputs.Append(phi.Inputs[0]).ToArray(),
        });

        var firstInput = phi.Inputs[0];
        var differentQubit = MirQubitParameter.Single(
            new MirQubitId(phi.Id.Value + 1),
            "different",
            QOwnershipMode.Borrowed,
            phi.Origin);
        Assert.Throws<ArgumentException>(() => phi with
        {
            Inputs = phi.Inputs
                .Select((input, index) => index == 0
                    ? new MirQubitPhiInput(input.Edge, differentQubit)
                    : input)
                .ToArray(),
        });

        var secondPhi = new MirQubitPhi(
            new MirQubitVersion(phi.Version.Value + 1),
            phi.Inputs,
            phi.Origin);
        Assert.Throws<ArgumentException>(() => merge with
        {
            QubitPhis = new[] { phi, secondPhi },
        });
    }

    [Fact]
    public void QuantumApplyConstructionRejectsInvalidQubitResultsImmediately()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use control = Qubit[1];
                use target = Qubit[1];
                CNOT(control[0], target[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var apply = Assert.Single(
            callable.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        var result = Assert.Single(apply.QubitResults);

        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            apply.Operands,
            new[] { result, result },
            apply.MutableArrayResults,
            apply.Functors,
            apply.Origin));

        var unrelatedResult = new MirQubitAfterInstruction(
            new MirQubitId(callable.Qubits.Max(qubit => qubit.Id.Value) + 1),
            new MirQubitVersion(1),
            apply.Origin);
        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            apply.Operands,
            new[] { unrelatedResult },
            apply.MutableArrayResults,
            apply.Functors,
            apply.Origin));
    }

    [Fact]
    public void QuantumApplyConstructionRejectsInvalidMutableArrayResultsImmediately()
    {
        var (program, _) = CompileMir("""
            operation Touch(var values: int[], target: Qubit) {
                values[0] = 1;
                X(target);
            }

            operation Main() {
                use target = Qubit[1];
                var values: int[] = [0];
                Touch(var values, target[0]);
            }
            """);
        var main = Callable(program, "Main");
        var apply = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        var result = Assert.Single(apply.MutableArrayResults);
        var scalarResult = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirConstant>()
            .First()
            .Result;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MirMutableArrayResult(-1, result.Result));
        Assert.Throws<ArgumentException>(
            () => new MirMutableArrayResult(0, scalarResult));
        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            apply.Operands,
            apply.QubitResults,
            new[] { result, result },
            apply.Functors,
            apply.Origin));

        var missingOperand = new MirMutableArrayResult(
            apply.Operands.Count,
            result.Result);
        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            apply.Operands,
            apply.QubitResults,
            new[] { missingOperand },
            apply.Functors,
            apply.Origin));

        var qubitOperandIndex = apply.Operands
            .Select((operand, index) => (operand, index))
            .Single(item => item.operand is MirQubitCallOperand)
            .index;
        var nonClassicalOperand = new MirMutableArrayResult(
            qubitOperandIndex,
            result.Result);
        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            apply.Operands,
            apply.QubitResults,
            new[] { nonClassicalOperand },
            apply.Functors,
            apply.Origin));

        var mutableOperand = Assert.IsType<MirClassicalCallOperand>(
            apply.Operands[result.OperandIndex]);
        var readOnlyOperands = apply.Operands.ToArray();
        readOnlyOperands[result.OperandIndex] = new MirClassicalCallOperand(
            mutableOperand.Value,
            mutableOperand.Ownership,
            QAccessMode.ReadOnly);
        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            readOnlyOperands,
            apply.QubitResults,
            apply.MutableArrayResults,
            apply.Functors,
            apply.Origin));

        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            new MirBuiltinGateTarget("X"),
            apply.Operands,
            apply.QubitResults,
            apply.MutableArrayResults,
            apply.Functors,
            apply.Origin));
    }

    [Fact]
    public void MeasureConstructionRejectsInvalidResultsImmediately()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use target = Qubit[1];
                var measured: bit = M(target[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var measure = Assert.Single(
            callable.Blocks.SelectMany(block => block.Instructions).OfType<MirMeasure>());
        var nonBitResult = new MirValue(
            new MirValueId(callable.Values.Max(value => value.Id.Value) + 1),
            MirType.Scalar(QType.Int),
            measure.Result.Origin);

        Assert.Throws<ArgumentException>(() => new MirMeasure(
            measure.Id,
            nonBitResult,
            measure.Qubit,
            measure.QubitResult,
            measure.Origin));

        var unrelatedQubitResult = new MirQubitAfterInstruction(
            new MirQubitId(callable.Qubits.Max(qubit => qubit.Id.Value) + 1),
            new MirQubitVersion(1),
            measure.QubitResult.Origin);
        Assert.Throws<ArgumentException>(() => new MirMeasure(
            measure.Id,
            measure.Result,
            measure.Qubit,
            unrelatedQubitResult,
            measure.Origin));
        Assert.Throws<ArgumentNullException>(() => new MirMeasure(
            measure.Id,
            measure.Result,
            null!,
            measure.QubitResult,
            measure.Origin));
    }

    [Fact]
    public void ArrayCreateConstructionRejectsInvalidLocalShapeImmediately()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                var values: int[] = [1];
            }
            """);
        var callable = Assert.Single(program.Callables);
        var instructions = callable.Blocks.SelectMany(block => block.Instructions).ToArray();
        var create = Assert.Single(instructions.OfType<MirArrayCreate>());
        var scalarResult = Assert.Single(instructions.OfType<MirConstant>()).Result;

        Assert.Throws<ArgumentException>(() => new MirArrayCreate(
            create.Id,
            scalarResult,
            create.Storage,
            create.Initialization,
            create.Elements,
            create.Origin));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirArrayCreate(
            create.Id,
            create.Result,
            create.Storage,
            (MirArrayInitialization)int.MaxValue,
            create.Elements,
            create.Origin));
        Assert.Throws<ArgumentException>(() => new MirArrayCreate(
            create.Id,
            create.Result,
            create.Storage,
            MirArrayInitialization.ExplicitElements,
            Array.Empty<MirValueId>(),
            create.Origin));
        Assert.Throws<ArgumentException>(() => new MirArrayCreate(
            create.Id,
            create.Result,
            create.Storage,
            MirArrayInitialization.ZeroInitialized,
            create.Elements,
            create.Origin));
    }

    [Fact]
    public void ClassicalInstructionConstructionRejectsInvalidLocalResultShapesImmediately()
    {
        var context = MirTestContext.Create();
        var origin = context.Origin();
        var integer = new MirValue(
            new MirValueId(0),
            MirType.Scalar(QType.Int),
            origin);
        var bit = new MirValue(
            new MirValueId(1),
            MirType.Scalar(QType.Bit),
            origin);
        var array = new MirValue(
            new MirValueId(2),
            MirType.Array(QType.Int, knownLength: 1),
            origin);

        Assert.Throws<ArgumentException>(
            () => new MirConstant(new MirInstructionId(0), array, "[1]", origin));
        Assert.Throws<ArgumentException>(() => new MirUnary(
            new MirInstructionId(1),
            integer,
            MirUnaryOperator.LogicalNot,
            bit.Id,
            origin));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirUnary(
            new MirInstructionId(2),
            integer,
            (MirUnaryOperator)int.MaxValue,
            integer.Id,
            origin));
        Assert.Throws<ArgumentException>(() => new MirBinary(
            new MirInstructionId(3),
            integer,
            MirBinaryOperator.Equal,
            integer.Id,
            integer.Id,
            origin));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirBinary(
            new MirInstructionId(4),
            integer,
            (MirBinaryOperator)int.MaxValue,
            integer.Id,
            integer.Id,
            origin));
        Assert.Throws<ArgumentException>(() => new MirConvert(
            new MirInstructionId(5),
            array,
            integer.Id,
            origin));
        Assert.Throws<ArgumentException>(() => new MirArrayLength(
            new MirInstructionId(6),
            bit,
            array.Id,
            origin));
        Assert.Throws<ArgumentException>(() => new MirArrayLoad(
            new MirInstructionId(7),
            array,
            array.Id,
            integer.Id,
            origin));
        Assert.Throws<ArgumentException>(() => new MirArrayStore(
            new MirInstructionId(8),
            integer,
            array.Id,
            integer.Id,
            integer.Id,
            origin));
    }

    [Fact]
    public void VerifierRejectsANegateResultWhoseTypeDiffersFromItsOperand()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                var value: int = 1;
                var negated: int = -value;
            }
            """);
        var callable = Assert.Single(program.Callables);
        var unary = Assert.Single(
            callable.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirUnary>());
        var wrongResult = new MirValue(
            unary.Result.Id,
            MirType.Scalar(QType.Float),
            unary.Result.Origin);
        var malformedUnary = new MirUnary(
            unary.Id,
            wrongResult,
            unary.Operator,
            unary.Operand,
            unary.Origin);
        var malformed = ReplaceInstruction(program, callable, malformedUnary);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR075");

        Assert.Contains("expected its operand type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAnArithmeticResultWhoseTypeDiffersFromItsOperands()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                var left: int = 1;
                var right: int = 2;
                var sum: int = left + right;
            }
            """);
        var callable = Assert.Single(program.Callables);
        var binary = Assert.Single(
            callable.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirBinary>(),
            instruction => instruction.Operator == MirBinaryOperator.Add);
        var wrongResult = new MirValue(
            binary.Result.Id,
            MirType.Scalar(QType.Float),
            binary.Result.Origin);
        var malformedBinary = new MirBinary(
            binary.Id,
            wrongResult,
            binary.Operator,
            binary.Left,
            binary.Right,
            binary.Origin);
        var malformed = ReplaceInstruction(program, callable, malformedBinary);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR076");

        Assert.Contains("expected its operand type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirEntityConstructionRejectsNullOriginsImmediately()
    {
        var context = MirTestContext.Create();
        var origin = context.Origin();
        var value = new MirValue(
            new MirValueId(0),
            MirType.Scalar(QType.Int),
            origin);

        Assert.Throws<ArgumentNullException>(
            () => new MirConstant(new MirInstructionId(0), value, "0", null!));
        Assert.Throws<ArgumentNullException>(() => new MirReturn(null, null!));
        Assert.Throws<ArgumentNullException>(() => new MirQubitCallOperand(null!));
        Assert.Throws<ArgumentNullException>(() => new MirQubitFromUse(
            new MirQubitId(0),
            "target",
            length: 1,
            null!));

        var block = new MirBlock(
            MirTestContext.BlockId(0),
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirReturn(null, origin),
            origin);
        Assert.Throws<ArgumentNullException>(() => block with { Origin = null! });
    }

    [Fact]
    public void MirCallContractsRejectUnknownOwnershipAndAccessModesImmediately()
    {
        var context = MirTestContext.Create();
        var origin = context.Origin();
        var value = new MirValue(
            new MirValueId(0),
            MirType.Scalar(QType.Int),
            origin);

        Assert.Throws<ArgumentOutOfRangeException>(() => new MirClassicalCallOperand(
            value.Id,
            (QOwnershipMode)int.MaxValue,
            QAccessMode.ReadOnly));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirClassicalCallOperand(
            value.Id,
            QOwnershipMode.Borrowed,
            (QAccessMode)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirClassicalParameter.Scalar(
            "value",
            value,
            (QOwnershipMode)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirClassicalParameter.Scalar(
            "value",
            value,
            access: (QAccessMode)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirQubitParameter.Single(
            new MirQubitId(0),
            "target",
            (QOwnershipMode)int.MaxValue,
            origin));

        var qubit = MirQubitParameter.Single(
            new MirQubitId(1),
            "target",
            QOwnershipMode.Borrowed,
            origin);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirQubitCallOperand(
            new MirQubitAccess(qubit),
            access: (QAccessMode)int.MaxValue));
    }

    [Fact]
    public void MirProgramConstructionRejectsDuplicateCallableIdsImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() {}");
        var callable = Assert.Single(snapshot.Program.Callables);

        var error = Assert.Throws<ArgumentException>(
            () => new MirProgram(
                snapshot.Program.EntryPoint,
                new[] { callable, callable }));

        Assert.Contains(
            callable.Id.ToString(),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MirProgramConstructionRejectsANullCallableImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() {}");
        var callable = snapshot.Program.EntryPoint;

        var error = Assert.Throws<ArgumentException>(
            () => new MirProgram(
                callable,
                new MirCallable[] { callable, null! }));

        Assert.Contains("null", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MirCallableConstructionRejectsANullOwnedElementImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() {}");
        var callable = snapshot.Program.EntryPoint;

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: callable.EntryBlock,
                blocks: new MirBlock[] { callable.EntryBlock, null! }));

        Assert.Contains("null", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MirCallableConstructionRejectsInvalidCallableContractsImmediately()
    {
        var snapshot = CompileSnapshot(
            "function Value(input: int): int { return input; } operation Main() {}");
        var function = Callable(snapshot.Program, "Value");
        var parameter = Assert.Single(
            function.Parameters.OfType<MirClassicalParameter>());

        Assert.Throws<ArgumentException>(
            () => CopyCallable(function, name: " "));
        AssertInvalidParameter(
            MirClassicalParameter.Scalar(
                parameter.Name,
                parameter.Value,
                parameter.Ownership,
                QAccessMode.Mutable),
            "borrowed and read-only");
        AssertInvalidParameter(
            MirClassicalParameter.Scalar(
                parameter.Name,
                parameter.Value,
                QOwnershipMode.Moved,
                parameter.Access),
            "borrowed and read-only");
        AssertInvalidParameter(
            MirQubitParameter.Single(
                new MirQubitId(0),
                "q",
                QOwnershipMode.Borrowed,
                function.Origin),
            "cannot be a qubit");
        AssertInvalidParameter(
            new UnknownMirParameter("unknown"),
            "unsupported MIR parameter type");

        void AssertInvalidParameter(
            IMirParameter invalidParameter,
            string expectedMessage)
        {
            var error = Assert.Throws<ArgumentException>(
                () => CopyCallable(
                    function,
                    parameters: new[] { invalidParameter }));
            Assert.Contains(
                expectedMessage,
                error.Message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MirBlockConstructionRejectsANullTerminatorImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() {}");
        var block = snapshot.Program.EntryPoint.EntryBlock;

        Assert.Throws<ArgumentNullException>(
            () => new MirBlock(
                block.Id,
                block.Arguments,
                block.Instructions,
                null!,
                block.Origin,
                block.QubitPhis));
        Assert.Throws<ArgumentNullException>(
            () => block with { Terminator = null! });
    }

    [Fact]
    public void MirCallableConstructionRejectsDuplicateBlockIdsImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() {}");
        var callable = snapshot.Program.EntryPoint;
        var entryBlock = callable.EntryBlock;

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: entryBlock,
                blocks: new[] { entryBlock, entryBlock }));

        Assert.Contains("block identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsDuplicateInstructionIdsImmediately()
    {
        var snapshot = CompileSnapshot("function Value(): int { return 1; } operation Main() {}");
        var callable = Callable(snapshot.Program, "Value");
        var entryBlock = callable.EntryBlock;
        var instruction = Assert.Single(entryBlock.Instructions);
        var duplicateEntryBlock = entryBlock with
        {
            Instructions = new[] { instruction, instruction },
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: duplicateEntryBlock,
                blocks: new[] { duplicateEntryBlock }));

        Assert.Contains("instruction identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsDuplicateValueIdsImmediately()
    {
        var snapshot = CompileSnapshot("function Value(): int { return 1; } operation Main() {}");
        var callable = Callable(snapshot.Program, "Value");
        var entryBlock = callable.EntryBlock;
        var instruction = Assert.Single(entryBlock.Instructions);
        var duplicateInstruction = instruction with
        {
            Id = new MirInstructionId(instruction.Id.Value + 1),
        };
        var duplicateEntryBlock = entryBlock with
        {
            Instructions = new[] { instruction, duplicateInstruction },
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: duplicateEntryBlock,
                blocks: new[] { duplicateEntryBlock }));

        Assert.Contains("SSA value identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsDuplicateStorageIdsImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() { var values: int[] = [1]; }");
        var callable = snapshot.Program.EntryPoint;
        var entryBlock = callable.EntryBlock;
        var allocation = Assert.Single(entryBlock.Instructions.OfType<MirArrayCreate>());
        var duplicateResult = new MirValue(
            new MirValueId(callable.Values.Max(value => value.Id.Value) + 1),
            allocation.Result.Type,
            allocation.Result.Origin);
        var duplicateAllocation = allocation with
        {
            Id = new MirInstructionId(
                entryBlock.Instructions.Max(instruction => instruction.Id.Value) + 1),
            Result = duplicateResult,
        };
        var duplicateEntryBlock = entryBlock with
        {
            Instructions = entryBlock.Instructions.Append(duplicateAllocation).ToArray(),
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: duplicateEntryBlock,
                blocks: new[] { duplicateEntryBlock }));

        Assert.Contains("array storage identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableDerivesValuesAndStoragesFromTheirDefinitionSites()
    {
        var snapshot = CompileSnapshot("""
            function Choose(condition: int, input: int[]): int {
                var local: int[] = [1];
                var result: int = 0;
                if (condition > 0) {
                    result = input[0];
                }
                return result;
            }

            operation Main() {}
            """);
        var callable = Callable(snapshot.Program, "Choose");

        var definingValues = callable.Parameters
            .OfType<MirClassicalParameter>()
            .Select(parameter => parameter.Value)
            .Concat(callable.Blocks.SelectMany(block => block.Arguments))
            .Concat(callable.Blocks
                .SelectMany(block => block.Instructions)
                .SelectMany(instruction => instruction.Results))
            .ToArray();
        var definingStorages = callable.Parameters
            .OfType<MirClassicalParameter>()
            .Where(parameter => parameter.Storage is not null)
            .Select(parameter => parameter.Storage!)
            .Concat(callable.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirArrayCreate>()
                .Select(allocation => allocation.Storage))
            .ToArray();

        Assert.Contains(callable.Blocks, block => block.Arguments.Count > 0);
        Assert.Equal(2, definingStorages.Length);
        Assert.Equal(definingValues.Length, callable.Values.Count);
        Assert.All(
            definingValues,
            expected => Assert.Contains(
                callable.Values,
                actual => ReferenceEquals(actual, expected)));
        Assert.Equal(definingStorages.Length, callable.Storages.Count);
        Assert.All(
            definingStorages,
            expected => Assert.Contains(
                callable.Storages,
                actual => ReferenceEquals(actual, expected)));
    }

    [Fact]
    public void MirProgramConstructionRejectsEntitiesSharedByDifferentCallables()
    {
        var snapshot = CompileSnapshot("""
            operation First(input: int[], q: Qubit) {
                X(q);
                var values: int[] = [1];
            }

            operation Second(input: int[], q: Qubit) {
                X(q);
                var values: int[] = [2];
            }

            operation Main() {}
            """);
        var first = Callable(snapshot.Program, "First");
        var second = Callable(snapshot.Program, "Second");
        var firstParameter = Assert.Single(first.Parameters.OfType<MirClassicalParameter>());
        var firstApply = Assert.Single(
            first.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        var secondApply = Assert.Single(
            second.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        var firstAllocation = Assert.Single(
            first.Blocks.SelectMany(block => block.Instructions).OfType<MirArrayCreate>());
        var secondAllocation = Assert.Single(
            second.Blocks.SelectMany(block => block.Instructions).OfType<MirArrayCreate>());

        AssertRejected(
            CopyCallable(
                second,
                parameters: new IMirParameter[]
                {
                    firstParameter,
                    Assert.Single(second.Parameters.OfType<MirQubitParameter>()),
                }),
            "parameter");
        AssertRejected(
            CopyCallable(
                second,
                entryBlock: first.EntryBlock,
                blocks: new[] { first.EntryBlock }),
            "block");
        AssertRejected(
            ReplaceInstruction(second, secondAllocation, firstAllocation),
            "instruction");
        AssertRejected(
            ReplaceInstruction(
                second,
                secondAllocation,
                secondAllocation with { Result = firstAllocation.Result }),
            "SSA value");
        AssertRejected(
            ReplaceInstruction(
                second,
                secondAllocation,
                secondAllocation with { Storage = firstAllocation.Storage }),
            "array storage");
        AssertRejected(
            ReplaceInstruction(
                second,
                secondApply,
                new MirQuantumApply(
                    secondApply.Id,
                    secondApply.Target,
                    secondApply.Operands,
                    new[] { Assert.Single(firstApply.QubitResults) },
                    secondApply.MutableArrayResults,
                    secondApply.Functors,
                    secondApply.Origin)),
            "qubit version");

        void AssertRejected(
            MirCallable rewrittenSecond,
            string role)
        {
            var callables = snapshot.Program.Callables
                .Select(callable => ReferenceEquals(callable, second)
                    ? rewrittenSecond
                    : callable)
                .ToArray();
            var error = Assert.Throws<ArgumentException>(
                () => new MirProgram(snapshot.Program.EntryPoint, callables));
            Assert.Contains(
                $"{role} object is owned by both",
                error.Message,
                StringComparison.Ordinal);
        }

        static MirCallable ReplaceInstruction(
            MirCallable callable,
            MirInstruction original,
            MirInstruction replacement)
        {
            var blocks = callable.Blocks
                .Select(block => block.Instructions.Contains(original)
                    ? block with
                    {
                        Instructions = block.Instructions
                            .Select(instruction => ReferenceEquals(instruction, original)
                                ? replacement
                                : instruction)
                            .ToArray(),
                    }
                    : block)
                .ToArray();
            return CopyCallable(callable, blocks: blocks);
        }
    }

    [Fact]
    public void MirCallableConstructionRejectsDuplicateQubitKeysImmediately()
    {
        var snapshot = CompileSnapshot("operation Main() { use q = Qubit[1]; }");
        var callable = snapshot.Program.EntryPoint;
        var entryBlock = callable.EntryBlock;
        var allocation = Assert.Single(entryBlock.Instructions.OfType<MirQubitAllocate>());
        var nextInstructionId = new MirInstructionId(
            entryBlock.Instructions.Max(instruction => instruction.Id.Value) + 1);
        var duplicateAllocation = allocation with { Id = nextInstructionId };
        var duplicateEntryBlock = entryBlock with
        {
            Instructions = entryBlock.Instructions
                .Append(duplicateAllocation)
                .ToArray(),
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: duplicateEntryBlock,
                blocks: new[] { duplicateEntryBlock }));

        Assert.Contains("qubit version identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QubitAllocationsRemainAtTheirSourceOrderDeclarationPoints()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                use first = Qubit[1];
                X(first[0]);
                use second = Qubit[1];
                X(second[0]);
            }
            """);
        var quantumInstructions = snapshot.Program.EntryPoint.EntryBlock.Instructions
            .Where(instruction => instruction is MirQubitAllocate or MirQuantumApply)
            .ToArray();

        Assert.Collection(
            quantumInstructions,
            instruction => Assert.IsType<MirQubitAllocate>(instruction),
            instruction => Assert.IsType<MirQuantumApply>(instruction),
            instruction => Assert.IsType<MirQubitAllocate>(instruction),
            instruction => Assert.IsType<MirQuantumApply>(instruction));
    }

    [Fact]
    public void NestedExpressionTermsKeepTheirExactHirOrigins()
    {
        var compilation = Compiler.Compile("""
            function f(value: int): int {
                return value;
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var y: int = f(xs[1]);
            }
            """);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));

        var hirMain = compilation.Hir.Specialized!.Program.Callables
            .Single(callable => callable.Name == "Main");
        var declaration = Assert.Single(
            hirMain.Body.Statements
                .OfType<HirVariableDeclarationStatement>(),
            statement => statement.Name == "y");
        var callExpression =
            Assert.IsType<HirCallExpression>(declaration.Value);
        var loadExpression =
            Assert.IsType<HirIndexExpression>(
                Assert.Single(callExpression.Arguments).Expression);
        var indexExpression =
            Assert.IsType<HirIntegerLiteralExpression>(
                loadExpression.Index);

        var snapshot = Assert.IsType<MirSnapshot>(compilation.Mir);
        var main = Callable(snapshot.Program, "Main");
        var instructions = main.Blocks
            .SelectMany(block => block.Instructions)
            .ToArray();
        var load = Assert.Single(instructions.OfType<MirArrayLoad>());
        var call = Assert.Single(instructions.OfType<MirPureCall>());
        var index = Assert.Single(
            instructions.OfType<MirConstant>(),
            constant => constant.Text == "1");
        var operand = Assert.IsType<MirClassicalCallOperand>(
            Assert.Single(call.Operands));

        Assert.Equal(load.Result.Id, operand.Value);
        Assert.Equal(loadExpression.Id, OriginNode(load.Origin));
        Assert.Equal(callExpression.Id, OriginNode(call.Origin));
        Assert.Equal(indexExpression.Id, OriginNode(index.Origin));
        Assert.Equal(
            loadExpression.Id,
            OriginNode(
                load.Result.Origin));
        Assert.Equal(
            callExpression.Id,
            OriginNode(
                call.Result.Origin));
        Assert.NotEqual(declaration.Id, OriginNode(load.Origin));
        Assert.NotEqual(declaration.Id, OriginNode(call.Origin));

        HirNodeId OriginNode(MirOrigin origin) =>
            origin.SourceHirOrigin.HirNodeId;
    }

    [Fact]
    public void QuantumWritesAdvanceOnlyTheWrittenQubitAndFollowingInstructionsReadThatVersion()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                use control = Qubit[1];
                use target = Qubit[1];
                CNOT(control[0], target[0]);
                X(target[0]);
            }
            """);
        var main = Callable(snapshot.Program, "Main");
        var applies = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .ToArray();
        Assert.Equal(2, applies.Length);

        var cnot = applies[0];
        var control = Assert.IsType<MirQubitCallOperand>(cnot.Operands[0]).Qubit;
        var targetBefore = Assert.IsType<MirQubitCallOperand>(cnot.Operands[1]).Qubit;
        Assert.Equal(0, control.Qubit.Version.Value);
        Assert.Equal(0, targetBefore.Qubit.Version.Value);

        var targetAfterCnot = Assert.Single(cnot.QubitResults);
        Assert.Equal(targetBefore.Qubit.Id, targetAfterCnot.Id);
        Assert.Equal(1, targetAfterCnot.Version.Value);
        Assert.DoesNotContain(cnot.QubitResults, result => result.Id == control.Qubit.Id);

        var x = applies[1];
        var xInput = Assert.IsType<MirQubitCallOperand>(Assert.Single(x.Operands)).Qubit;
        Assert.Equal(targetAfterCnot.Key, xInput.Qubit);
        var targetAfterX = Assert.Single(x.QubitResults);
        Assert.Equal(targetAfterCnot.Id, targetAfterX.Id);
        Assert.Equal(2, targetAfterX.Version.Value);

        var effect = EffectFor(snapshot.Analyses.Effects, main.Id, cnot.Id);
        var effectInstruction = Assert.IsType<MirQuantumApply>(
            snapshot.Analyses.Effects.RequireInstruction(effect.Site));
        Assert.Equal(
            targetAfterCnot.Key,
            Assert.Single(effectInstruction.QubitResults).Key);

    }

    [Fact]
    public void MeasurementProducesTheVersionReadByTheFollowingQuantumInstruction()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                use target = Qubit[1];
                var measured: bit = M(target[0]);
                X(target[0]);
            }
            """);
        var main = Callable(snapshot.Program, "Main");
        var measure = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirMeasure>());
        Assert.Equal(0, measure.Qubit.Qubit.Version.Value);
        Assert.Equal(measure.Qubit.Qubit.Id, measure.QubitResult.Id);
        Assert.Equal(1, measure.QubitResult.Version.Value);

        var x = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        var xInput = Assert.IsType<MirQubitCallOperand>(Assert.Single(x.Operands));
        Assert.Equal(measure.QubitResult.Key, xInput.Qubit.Qubit);
        Assert.Equal(2, Assert.Single(x.QubitResults).Version.Value);

        var effect = EffectFor(snapshot.Analyses.Effects, main.Id, measure.Id);
        var effectInstruction = Assert.IsType<MirMeasure>(
            snapshot.Analyses.Effects.RequireInstruction(effect.Site));
        Assert.Equal(
            measure.QubitResult.Key,
            effectInstruction.QubitResult.Key);
    }

    [Fact]
    public void OneInstructionWritingTwoElementsProducesOneRegisterVersion()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                use register = Qubit[2];
                SWAP(register[0], register[1]);
                X(register[0]);
            }
            """);
        var main = Callable(snapshot.Program, "Main");
        var applies = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .ToArray();
        Assert.Equal(2, applies.Length);

        var swap = applies[0];
        var inputs = swap.Operands
            .OfType<MirQubitCallOperand>()
            .Select(operand => operand.Qubit)
            .ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.Equal(inputs[0].Qubit, inputs[1].Qubit);
        Assert.NotEqual(inputs[0].Index, inputs[1].Index);

        var registerAfterSwap = Assert.Single(swap.QubitResults);
        Assert.Equal(inputs[0].Qubit.Id, registerAfterSwap.Id);
        Assert.Equal(1, registerAfterSwap.Version.Value);
        Assert.Equal(
            registerAfterSwap.Key,
            Assert.IsType<MirQubitCallOperand>(
                Assert.Single(applies[1].Operands)).Qubit.Qubit);
    }

    [Fact]
    public void IfJoinCreatesOneQubitPhiAndTheFollowingAccessReadsIt()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                use target = Qubit[1];
                if (1 == 1) {
                    X(target[0]);
                } else {
                    H(target[0]);
                }
                Z(target[0]);
            }
            """);
        var main = Callable(snapshot.Program, "Main");
        var phi = Assert.Single(
            main.Blocks.SelectMany(block => block.QubitPhis));
        Assert.Equal(2, phi.Inputs.Count);
        Assert.All(phi.Inputs, input => Assert.Equal(phi.Id, input.Qubit.Id));
        Assert.Equal(2, phi.Inputs.Select(input => input.Qubit).Distinct().Count());

        var z = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target is MirBuiltinGateTarget { Name: "Z" });
        var zInput = Assert.IsType<MirQubitCallOperand>(Assert.Single(z.Operands));
        Assert.Equal(phi.Key, zInput.Qubit.Qubit);
        Assert.Empty(z.QubitResults);
    }

    [Fact]
    public void LoopHeaderQubitPhiConnectsTheEntryAndBackedgeVersions()
    {
        var snapshot = CompileSnapshot("""
            operation Main() {
                use target = Qubit[1];
                var index: int = 0;
                while (index < 1) {
                    X(target[0]);
                    index = index + 1;
                }
                Z(target[0]);
            }
            """);
        var main = Callable(snapshot.Program, "Main");
        var header = Assert.Single(main.Blocks, block => block.QubitPhis.Count != 0);
        var phi = Assert.Single(header.QubitPhis);
        Assert.Equal(2, phi.Inputs.Count);
        Assert.Contains(phi.Inputs, input => input.Qubit.Version.Value == 0);

        var x = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target is MirBuiltinGateTarget { Name: "X" });
        var xInput = Assert.IsType<MirQubitCallOperand>(Assert.Single(x.Operands));
        Assert.Equal(phi.Key, xInput.Qubit.Qubit);
        var loopResult = Assert.Single(x.QubitResults);
        Assert.Contains(phi.Inputs, input => input.Qubit == loopResult.Key);

        var z = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target is MirBuiltinGateTarget { Name: "Z" });
        Assert.Equal(
            phi.Key,
            Assert.IsType<MirQubitCallOperand>(Assert.Single(z.Operands)).Qubit.Qubit);
    }

    [Fact]
    public void VerifierRejectsAStaleQubitVersionAfterAWrite()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use register = Qubit[1];
                X(register[0]);
                X(register[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var seed = Assert.Single(callable.Qubits.OfType<MirQubitFromUse>());
        var applies = callable.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .ToArray();
        Assert.Equal(2, applies.Length);

        var second = applies[1];
        var operand = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(second.Operands));
        var staleOperand = new MirQubitCallOperand(
            new MirQubitAccess(seed, operand.Qubit.Index),
            operand.Ownership,
            operand.Access);
        var malformedApply = new MirQuantumApply(
            second.Id,
            second.Target,
            new MirCallOperand[] { staleOperand },
            second.QubitResults,
            second.MutableArrayResults,
            second.Functors,
            second.Origin);
        var malformed = ReplaceInstruction(
            program,
            callable,
            malformedApply);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR146");
        Assert.Contains("stale version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsMixedVersionsOfOneRegisterWithinAnInstruction()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use register = Qubit[2];
                X(register[0]);
                SWAP(register[0], register[1]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var seed = Assert.Single(callable.Qubits.OfType<MirQubitFromUse>());
        var swap = Assert.Single(
            callable.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target is MirBuiltinGateTarget { Name: "SWAP" });
        var operands = swap.Operands
            .Cast<MirQubitCallOperand>()
            .ToArray();
        Assert.Equal(2, operands.Length);
        Assert.Equal(operands[0].Qubit.Qubit, operands[1].Qubit.Qubit);

        var staleFirst = new MirQubitCallOperand(
            new MirQubitAccess(seed, operands[0].Qubit.Index),
            operands[0].Ownership,
            operands[0].Access);
        var malformedApply = new MirQuantumApply(
            swap.Id,
            swap.Target,
            new MirCallOperand[] { staleFirst, operands[1] },
            swap.QubitResults,
            swap.MutableArrayResults,
            swap.Functors,
            swap.Origin);
        var malformed = ReplaceInstruction(
            program,
            callable,
            malformedApply);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR146");
        Assert.Contains(
            "multiple input versions",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAStaleQubitPhiInput()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use target = Qubit[1];
                if (1 == 1) {
                    X(target[0]);
                } else {
                    H(target[0]);
                }
                X(target[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var seed = Assert.Single(callable.Qubits.OfType<MirQubitFromUse>());
        var merge = Assert.Single(
            callable.Blocks,
            block => block.QubitPhis.Count != 0);
        var phi = Assert.Single(merge.QubitPhis);
        var malformedPhi = phi with
        {
            Inputs = phi.Inputs
                .Select((input, index) =>
                    index == 0
                        ? new MirQubitPhiInput(input.Edge, seed)
                        : input)
                .ToArray(),
        };
        var malformedMerge = merge with
        {
            QubitPhis = new[] { malformedPhi },
        };
        var malformedCallable = CopyCallable(
            callable,
            blocks: callable.Blocks
                .Select(block =>
                    block.Id == merge.Id
                        ? malformedMerge
                        : block)
                .ToArray());
        var malformed = new MirProgram(
            malformedCallable,
            new[] { malformedCallable });

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR146");
        Assert.Contains("stale version", error.Message, StringComparison.Ordinal);
        Assert.Contains("Phi", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAJoinWhoseCurrentQubitVersionsLackAPhi()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use target = Qubit[1];
                if (1 == 1) {
                    X(target[0]);
                } else {
                    H(target[0]);
                }
            }
            """);
        var callable = Assert.Single(program.Callables);
        var merge = Assert.Single(
            callable.Blocks,
            block => block.QubitPhis.Count != 0);
        var malformedMerge = merge with
        {
            QubitPhis = Array.Empty<MirQubitPhi>(),
        };
        var malformedCallable = CopyCallable(
            callable,
            blocks: callable.Blocks
                .Select(block =>
                    block.Id == merge.Id
                        ? malformedMerge
                        : block)
                .ToArray());
        var malformed = new MirProgram(
            malformedCallable,
            new[] { malformedCallable });

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR146");
        Assert.Contains("reachable join", error.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one Phi", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchLocalQubitDoesNotRequireAPhiAfterItsLifetimeEnds()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use local = Qubit[1];
                if (1 == 1) {
                } else {
                }
            }
            """);
        var callable = Assert.Single(program.Callables);
        var entry = callable.EntryBlock;
        var branch = Assert.IsType<MirBranch>(entry.Terminator);
        var trueBlock = Assert.IsType<MirBlock>(
            callable.FindBlock(branch.TrueTarget));
        var allocation = Assert.Single(
            entry.Instructions.OfType<MirQubitAllocate>());
        var rewrittenEntry = entry with
        {
            Instructions = entry.Instructions
                .Where(instruction => instruction.Id != allocation.Id)
                .ToArray(),
        };
        var rewrittenTrue = trueBlock with
        {
            Instructions = trueBlock.Instructions
                .Prepend(allocation)
                .ToArray(),
        };
        var malformedCallable = CopyCallable(
            callable,
            blocks: callable.Blocks
                .Select(block => block.Id switch
                {
                    var id when id == rewrittenEntry.Id => rewrittenEntry,
                    var id when id == rewrittenTrue.Id => rewrittenTrue,
                    _ => block,
                })
                .ToArray());
        var branchLocal = new MirProgram(
            malformedCallable,
            new[] { malformedCallable });

        Assert.Empty(QoraMirVerifier.Verify(branchLocal));
    }

    [Fact]
    public void OwnershipTransferIsRecordedSeparatelyFromQuantumIrreversibility()
    {
        var (program, effects) = CompileMir("""
            operation Consume(move values: Qubit[]) {
                X(values[0]);
            }

            operation Main() {
                use values = Qubit[1];
                Consume(move values);
            }
            """);

        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        var effect = EffectFor(effects, main.Id, call.Id);

        Assert.True(effect.TransfersOwnership);
        Assert.False(effect.IsIrreversible);
        Assert.Contains(
            effect.Qubits,
            qubit => qubit.Flags.HasFlag(MirQubitEffectFlags.OwnershipTransfer));

        var summary = Assert.IsType<MirCallableEffectSummary>(
            effects.SummaryOf(main.Id));
        Assert.True(summary.TransfersOwnership);
    }

    [Fact]
    public void VerifierRejectsAMissingTransitiveUserCallQubitResult()
    {
        var (program, _) = CompileMir("""
            operation Mutate(q: Qubit) {
                X(q);
            }

            operation Forward(q: Qubit) {
                Mutate(q);
            }

            operation Main() {
                use register = Qubit[1];
                Forward(register[0]);
            }
            """);
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        Assert.IsType<MirUserCallableTarget>(call.Target);
        Assert.Single(call.QubitResults);

        var malformedCall = new MirQuantumApply(
            call.Id,
            call.Target,
            call.Operands,
            Array.Empty<MirQubitAfterInstruction>(),
            call.MutableArrayResults,
            call.Functors,
            call.Origin);
        var malformed = ReplaceInstruction(program, main, malformedCall);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR142");
        Assert.Contains(
            "semantic write set",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsASpuriousReadOnlyUserCallQubitResult()
    {
        var (program, _) = CompileMir("""
            operation Inspect(q: Qubit) {}

            operation Main() {
                use register = Qubit[1];
                Inspect(register[0]);
            }
            """);
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var operand = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(call.Operands));
        Assert.Empty(call.QubitResults);

        var spurious = new MirQubitAfterInstruction(
            operand.Qubit.Qubit.Id,
            new MirQubitVersion(1),
            call.Origin);
        var malformedCall = new MirQuantumApply(
            call.Id,
            call.Target,
            call.Operands,
            new[] { spurious },
            call.MutableArrayResults,
            call.Functors,
            call.Origin);
        var malformed = ReplaceInstruction(program, main, malformedCall);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR142");
        Assert.Contains(
            "semantic write set",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsAnEntryBlockItDoesNotOwn()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var detachedEntryBlock = callable.EntryBlock with { };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: detachedEntryBlock));

        Assert.Contains("exact object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsAnEntryBlockWithAPredecessor()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var entry = Assert.Single(callable.Blocks);
        var malformedEntry = entry with
        {
            Terminator = new MirJump(
                entry.Id,
                Array.Empty<MirValueId>(),
                entry.Terminator.Origin),
        };
        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: malformedEntry,
                blocks: new[] { malformedEntry }));

        Assert.Contains("unique CFG root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsAControlFlowTargetItDoesNotOwn()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var entry = Assert.Single(callable.Blocks);
        var missingTarget = MirTestContext.BlockId(entry.Id.Value + 1);
        var malformedEntry = entry with
        {
            Terminator = new MirJump(
                missingTarget,
                Array.Empty<MirValueId>(),
                entry.Terminator.Origin),
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: malformedEntry,
                blocks: new[] { malformedEntry }));

        Assert.Contains("does not belong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsTheWrongNumberOfEdgeArguments()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var originalEntry = Assert.Single(callable.Blocks);
        var targetArgument = new MirValue(
            new MirValueId(0),
            MirType.Scalar(QType.Int),
            originalEntry.Origin);
        var target = new MirBlock(
            MirTestContext.BlockId(originalEntry.Id.Value + 1),
            new[] { targetArgument },
            Array.Empty<MirInstruction>(),
            new MirReturn(null, originalEntry.Terminator.Origin),
            originalEntry.Origin);
        var entry = originalEntry with
        {
            Terminator = new MirJump(
                target.Id,
                Array.Empty<MirValueId>(),
                originalEntry.Terminator.Origin),
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: entry,
                blocks: new[] { entry, target }));

        Assert.Contains("supplies 0 value(s)", error.Message, StringComparison.Ordinal);
        Assert.Contains("expects 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsAnUnknownEdgeArgument()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var originalEntry = Assert.Single(callable.Blocks);
        var targetArgument = new MirValue(
            new MirValueId(0),
            MirType.Scalar(QType.Int),
            originalEntry.Origin);
        var target = new MirBlock(
            MirTestContext.BlockId(originalEntry.Id.Value + 1),
            new[] { targetArgument },
            Array.Empty<MirInstruction>(),
            new MirReturn(null, originalEntry.Terminator.Origin),
            originalEntry.Origin);
        var entry = originalEntry with
        {
            Terminator = new MirJump(
                target.Id,
                new[] { new MirValueId(1) },
                originalEntry.Terminator.Origin),
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: entry,
                blocks: new[] { entry, target }));

        Assert.Contains("does not belong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirCallableConstructionRejectsAnEdgeArgumentWithTheWrongType()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var originalEntry = Assert.Single(callable.Blocks);
        var suppliedValue = new MirValue(
            new MirValueId(0),
            MirType.Scalar(QType.Bit),
            originalEntry.Origin);
        var suppliedValueDefinition = new MirConstant(
            new MirInstructionId(0),
            suppliedValue,
            "1",
            originalEntry.Origin);
        var targetArgument = new MirValue(
            new MirValueId(1),
            MirType.Scalar(QType.Int),
            originalEntry.Origin);
        var target = new MirBlock(
            MirTestContext.BlockId(originalEntry.Id.Value + 1),
            new[] { targetArgument },
            Array.Empty<MirInstruction>(),
            new MirReturn(null, originalEntry.Terminator.Origin),
            originalEntry.Origin);
        var entry = originalEntry with
        {
            Instructions = new MirInstruction[] { suppliedValueDefinition },
            Terminator = new MirJump(
                target.Id,
                new[] { suppliedValue.Id },
                originalEntry.Terminator.Origin),
        };

        var error = Assert.Throws<ArgumentException>(
            () => CopyCallable(
                callable,
                entryBlock: entry,
                blocks: new[] { entry, target }));

        Assert.Contains("is bit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expects int", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MirProgramConstructionRejectsAnEntryCallableItDoesNotOwn()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var ownedEntryCallable = program.EntryPoint;
        var detachedEntryCallable = CopyCallable(ownedEntryCallable);

        var error = Assert.Throws<ArgumentException>(
            () => new MirProgram(
                detachedEntryCallable,
                program.Callables));

        Assert.Contains("exact object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirProgramConstructionRejectsAFunctionAsTheEntryCallable()
    {
        var (program, _) = CompileMir("""
            function Value(): int {
                return 1;
            }

            operation Main() {}
            """);
        var function = Assert.Single(
            program.Callables,
            callable => callable.Name == "Value");
        var error = Assert.Throws<ArgumentException>(
            () => new MirProgram(function, program.Callables));

        Assert.Contains("operation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirProgramConstructionRejectsAParameterizedOperationAsTheEntryCallable()
    {
        var (program, _) = CompileMir("""
            operation Worker(value: int) {}

            operation Main() {}
            """);
        var worker = Assert.Single(
            program.Callables,
            callable => callable.Name == "Worker");
        var error = Assert.Throws<ArgumentException>(
            () => new MirProgram(worker, program.Callables));

        Assert.Contains("must not declare parameters", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuantumApplyConstructionRejectsAnyFunctorOnANonUnitaryBuiltin()
    {
        var (program, _) = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                Reset(q[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var block = Assert.Single(callable.Blocks);
        var reset = Assert.Single(
            block.Instructions.OfType<MirQuantumApply>());
        var error = Assert.Throws<ArgumentException>(
            () => CopyApplyWithFunctors(
                reset,
                new[] { MirFunctor.Adjoint }));
        Assert.Contains(
            "non-unitary",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuantumApplyConstructionRejectsUnknownAndNonCanonicalFunctorLists()
    {
        var (program, _) = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                X(q[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var block = Assert.Single(callable.Blocks);
        var apply = Assert.Single(
            block.Instructions.OfType<MirQuantumApply>());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyApplyWithFunctors(
                apply,
                new[] { (MirFunctor)int.MaxValue }));
        Assert.Throws<ArgumentException>(
            () => CopyApplyWithFunctors(
                apply,
                new[] { MirFunctor.Adjoint, MirFunctor.Adjoint }));
        Assert.Throws<ArgumentException>(
            () => CopyApplyWithFunctors(
                apply,
                new[] { MirFunctor.Controlled, MirFunctor.Controlled }));
        Assert.Throws<ArgumentException>(
            () => CopyApplyWithFunctors(
                apply,
                new[] { MirFunctor.Controlled, MirFunctor.Adjoint }));
    }

    [Fact]
    public void BuiltinTargetConstructionRejectsUnknownNamesImmediately()
    {
        Assert.Throws<ArgumentException>(
            () => new MirBuiltinGateTarget("MissingGate"));
        Assert.Throws<ArgumentException>(
            () => new MirBuiltinFunctionTarget("MissingFunction"));
    }

    [Fact]
    public void PureCallConstructionRejectsInvalidLocalContractsImmediately()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                var bits: bit[] = new bit[2];
                var result: int = AsInt(bits);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var call = Assert.Single(
            callable.Blocks.SelectMany(block => block.Instructions).OfType<MirPureCall>());

        Assert.Throws<ArgumentException>(() => new MirPureCall(
            call.Id,
            call.Result,
            new MirBuiltinGateTarget("X"),
            call.Operands,
            call.Origin));
        Assert.Throws<ArgumentException>(() => new MirPureCall(
            call.Id,
            call.Result,
            call.Target,
            call.Operands.Append(call.Operands[0]).ToArray(),
            call.Origin));

        var context = MirTestContext.Create();
        var qubit = MirQubitParameter.Single(
            new MirQubitId(0),
            "target",
            QOwnershipMode.Borrowed,
            context.Origin());
        var qubitOperand = new MirQubitCallOperand(new MirQubitAccess(qubit));
        Assert.Throws<ArgumentException>(() => new MirPureCall(
            call.Id,
            call.Result,
            call.Target,
            new MirCallOperand[] { qubitOperand },
            call.Origin));

        var wrongResult = new MirValue(
            new MirValueId(callable.Values.Max(value => value.Id.Value) + 1),
            MirType.Scalar(QType.Bit),
            call.Result.Origin);
        Assert.Throws<ArgumentException>(() => new MirPureCall(
            call.Id,
            wrongResult,
            call.Target,
            call.Operands,
            call.Origin));
    }

    [Fact]
    public void BuiltinQuantumApplyConstructionRejectsInvalidOperandShapeImmediately()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                use target = Qubit[1];
                X(target[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var apply = Assert.Single(
            callable.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());

        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            Array.Empty<MirCallOperand>(),
            apply.QubitResults,
            apply.MutableArrayResults,
            apply.Functors,
            apply.Origin));
        Assert.Throws<ArgumentException>(() => new MirQuantumApply(
            apply.Id,
            apply.Target,
            new MirCallOperand[]
            {
                new MirClassicalCallOperand(new MirValueId(0)),
            },
            apply.QubitResults,
            apply.MutableArrayResults,
            apply.Functors,
            apply.Origin));
    }

    private static MirCallable CopyCallable(
        MirCallable source,
        MirBlock? entryBlock = null,
        IReadOnlyList<MirBlock>? blocks = null,
        IReadOnlyList<IMirParameter>? parameters = null,
        string? name = null)
    {
        var copiedBlocks = blocks ?? source.Blocks;
        var copiedEntryBlock = entryBlock
            ?? copiedBlocks.Single(block => block.Id == source.EntryBlock.Id);
        return new MirCallable(
            source.Id,
            name ?? source.Name,
            source.ReturnType,
            parameters ?? source.Parameters,
            copiedEntryBlock,
            copiedBlocks,
            source.Origin);
    }

    private sealed record UnknownMirParameter(string Name) : IMirParameter;

    private static MirQuantumApply CopyApplyWithFunctors(
        MirQuantumApply source,
        IReadOnlyList<MirFunctor> functors) =>
        new(
            source.Id,
            source.Target,
            source.Operands,
            source.QubitResults,
            source.MutableArrayResults,
            functors,
            source.Origin);

    private static MirProgram ReplaceInstruction(
        MirProgram program,
        MirCallable callable,
        MirInstruction replacement)
    {
        var blocks = callable.Blocks
            .Select(block => block.Instructions.Any(
                instruction => instruction.Id == replacement.Id)
                ? block with
                {
                    Instructions = block.Instructions
                        .Select(instruction =>
                            instruction.Id == replacement.Id
                                ? replacement
                                : instruction)
                        .ToArray(),
                }
                : block)
            .ToArray();
        var rewritten = CopyCallable(callable, blocks: blocks);
        var rewrittenCallables = program.Callables
            .Select(candidate =>
                candidate.Id == callable.Id
                    ? rewritten
                    : candidate)
            .ToArray();
        var rewrittenEntryPoint = ReferenceEquals(callable, program.EntryPoint)
            ? rewritten
            : program.EntryPoint;
        return new MirProgram(rewrittenEntryPoint, rewrittenCallables);
    }

    private static (MirProgram Program, MirEffectSnapshot Effects) CompileMir(string source)
    {
        var snapshot = CompileSnapshot(source);
        return (snapshot.Program, snapshot.Analyses.Effects);
    }

    private static MirSnapshot CompileSnapshot(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
        return Assert.IsType<MirSnapshot>(result.Mir);
    }

    private static MirCallable Callable(MirProgram program, string name) =>
        Assert.Single(program.Callables, callable => callable.Name == name);

    private static MirQuantumInstructionEffect EffectFor(
        MirEffectSnapshot effects,
        MirCallableId callable,
        MirInstructionId instruction) =>
        Assert.Single(
            effects.Effects,
            effect => effect.Site.Callable == callable
                && effect.Site.Instruction == instruction);

    private static IReadOnlyList<IReadOnlyList<MirValueId>> IncomingArguments(
        MirCallable callable,
        MirBlockId target)
    {
        var incoming = new List<IReadOnlyList<MirValueId>>();
        foreach (var block in callable.Blocks)
        {
            switch (block.Terminator)
            {
                case MirJump jump when jump.Target == target:
                    incoming.Add(jump.Arguments);
                    break;
                case MirBranch branch when branch.TrueTarget == target:
                    incoming.Add(branch.TrueArguments);
                    break;
                case MirBranch branch when branch.FalseTarget == target:
                    incoming.Add(branch.FalseArguments);
                    break;
            }
        }
        return incoming;
    }

    private static void AssertLoopHasBackedgeArguments(MirCallable callable)
    {
        var header = Assert.Single(
            callable.Blocks,
            block => block.Arguments.Count > 0
                && IncomingArguments(callable, block.Id).Count >= 2);
        var backedgeBlock = Assert.Single(
            callable.Blocks,
            block => block.Id.Value > header.Id.Value
                && block.Terminator is MirJump jump
                && jump.Target == header.Id);
        var backedge = Assert.IsType<MirJump>(backedgeBlock.Terminator);

        Assert.Equal(header.Arguments.Count, backedge.Arguments.Count);
        Assert.Contains(
            backedge.Arguments,
            value =>
            {
                var definition = callable.DefinitionOf(value);
                if (definition.Instruction is not MirInstructionId instruction)
                    return false;

                return callable
                    .RequireInstructionLocation(instruction)
                    .Block.Id == backedgeBlock.Id;
            });
    }
}
