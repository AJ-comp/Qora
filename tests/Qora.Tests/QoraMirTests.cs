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

        Assert.Equal(MirValueDefinitionKind.BlockArgument, mergedValue.Definition.Kind);
        var mergeBlockId = Assert.IsType<MirBlockId>(mergedValue.Definition.Block);
        var mergeBlock = Assert.IsType<MirBlock>(main.FindBlock(mergeBlockId));
        var blockArgument = Assert.Single(
            mergeBlock.Arguments,
            argument => argument == mergedInput);
        var incoming = IncomingArguments(main, mergeBlock.Id);
        Assert.Equal(2, incoming.Count);
        Assert.All(incoming, arguments => Assert.Equal(mergeBlock.Arguments.Count, arguments.Count));
        var argumentIndex = blockArgumentIndex(mergeBlock, blockArgument);
        Assert.Equal(
            2,
            incoming.Select(arguments => arguments[argumentIndex]).Distinct().Count());
        static int blockArgumentIndex(MirBlock block, MirValueId argument) =>
            block.Arguments.ToList().FindIndex(candidate => candidate == argument);
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
        Assert.Equal(create.Result, store.Array);
        Assert.NotEqual(store.Array, store.Result);

        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget target
                && target.Callable == touch.Id);
        var inputState = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        Assert.Equal(store.Result, inputState);
        var transition = Assert.Single(call.MutableArrayResults);
        Assert.Equal(0, transition.OperandIndex);
        Assert.NotEqual(inputState, transition.Result);

        var arrayEffect = Assert.Single(
            EffectFor(effects, main.Id, call.Id).ArrayStates);
        Assert.Equal(inputState, arrayEffect.InputState);
        Assert.Equal(transition.Result, arrayEffect.OutputState);
        Assert.True(arrayEffect.Storage.IsComplete);
        Assert.Equal(
            new[] { create.Storage },
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

        var finalHirArtifact = compilation.Hir.EffectAnalysis!;
        var mirSnapshot = Assert.IsType<MirSnapshot>(compilation.Mir);

        foreach (var hirCallable in finalHirArtifact.Program.Callables)
        {
            var mirCallable = Assert.Single(
                mirSnapshot.Program.Callables,
                candidate => candidate.Name == hirCallable.Name);
            Assert.Equal(
                hirCallable.Id.Value,
                mirCallable.Id.Value);
        }

        Assert.Equal(
            finalHirArtifact.Program.EntryCallable!.Id.Value,
            mirSnapshot.Program.EntryPoint.Value);
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

        Assert.Equal(load.Result, operand.Value);
        Assert.Equal(loadExpression.Id, OriginNode(load.Origin));
        Assert.Equal(callExpression.Id, OriginNode(call.Origin));
        Assert.Equal(indexExpression.Id, OriginNode(index.Origin));
        Assert.Equal(
            loadExpression.Id,
            OriginNode(
                Assert.IsType<MirValue>(
                    main.FindValue(load.Result)).Origin));
        Assert.Equal(
            callExpression.Id,
            OriginNode(
                Assert.IsType<MirValue>(
                    main.FindValue(call.Result)).Origin));
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
                        ? new MirQubitPhiInput(input.Edge, seed.Key)
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
            program.EntryPoint,
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
            program.EntryPoint,
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
        var entry = Assert.IsType<MirBlock>(
            callable.FindBlock(callable.EntryBlock));
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
            program.EntryPoint,
            new[] { malformedCallable });

        Assert.Empty(QoraMirVerifier.Verify(branchLocal));
    }

    [Fact]
    public void EffectSnapshotRejectsEveryOtherProgramInstance()
    {
        var (program, effects) = CompileMir("""
            operation Main() {
                use register = Qubit[1];
                X(register[0]);
            }
            """);

        Assert.True(effects.IsFor(program));
        effects.EnsureFor(program);

        var detachedProgramCopy = new MirProgram(
            program.EntryPoint,
            program.Callables.ToArray());
        Assert.False(effects.IsFor(detachedProgramCopy));
        Assert.Throws<InvalidOperationException>(() => effects.EnsureFor(detachedProgramCopy));

        var (otherSnapshot, _) = CompileMir("""
            operation Main() {
                use register = Qubit[1];
                X(register[0]);
            }
            """);
        Assert.False(effects.IsFor(otherSnapshot));
        Assert.Throws<InvalidOperationException>(() => effects.EnsureFor(otherSnapshot));
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
    public void VerifierRejectsAMissingEntryBlock()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var malformedCallable = CopyCallable(
            callable,
            entryBlock: new MirBlockId(int.MaxValue));
        var malformed = new MirProgram(
            program.EntryPoint,
            new[] { malformedCallable });

        var errors = QoraMirVerifier.Verify(malformed);
        Assert.Contains(errors, error => error.Code == "MIR014");
        var exception = Assert.Throws<InvalidOperationException>(
            () => QoraMirVerifier.VerifyOrThrow(malformed));
        Assert.Contains("MIR014", exception.Message);
    }

    [Fact]
    public void VerifierRejectsAnEntryBlockWithAPredecessor()
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
        var malformedCallable = CopyCallable(
            callable,
            blocks: new[] { malformedEntry });
        var malformed = new MirProgram(
            program.EntryPoint,
            new[] { malformedCallable });

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR147");
        Assert.Contains("unique CFG root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAMissingProgramEntryCallable()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var malformed = new MirProgram(
            new MirCallableId(int.MaxValue),
            program.Callables);

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR009");
        Assert.Contains(new MirCallableId(int.MaxValue).ToString(), error.Message);
    }

    [Fact]
    public void VerifierRejectsAFunctionAsTheProgramEntryCallable()
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
        var malformed = new MirProgram(
            function.Id,
            program.Callables);

        Assert.Contains(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR036");
    }

    [Fact]
    public void VerifierRejectsAParameterizedOperationAsTheProgramEntryCallable()
    {
        var (program, _) = CompileMir("""
            operation Worker(value: int) {}

            operation Main() {}
            """);
        var worker = Assert.Single(
            program.Callables,
            callable => callable.Name == "Worker");
        var malformed = new MirProgram(
            worker.Id,
            program.Callables);

        Assert.Contains(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR037");
    }

    [Fact]
    public void VerifierRejectsAnyFunctorOnANonUnitaryBuiltin()
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
        var malformedReset = CopyApplyWithFunctors(
            reset,
            new[] { MirFunctor.Adjoint });
        var malformedBlock = block with
        {
            Instructions = block.Instructions
                .Select(instruction =>
                    instruction.Id == reset.Id
                        ? malformedReset
                        : instruction)
                .ToArray(),
        };
        var malformedCallable = CopyCallable(
            callable,
            blocks: new[] { malformedBlock });
        var malformed = new MirProgram(
            program.EntryPoint,
            new[] { malformedCallable });

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformed),
            candidate => candidate.Code == "MIR139");
        Assert.Contains(
            "non-unitary",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsUnknownAndNonCanonicalFunctorLists()
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

        MirProgram WithFunctors(params MirFunctor[] functors)
        {
            var rewrittenApply = CopyApplyWithFunctors(apply, functors);
            var rewrittenBlock = block with
            {
                Instructions = block.Instructions
                    .Select(instruction =>
                        instruction.Id == apply.Id
                            ? rewrittenApply
                            : instruction)
                    .ToArray(),
            };
            return new MirProgram(
                program.EntryPoint,
                new[]
                {
                    CopyCallable(callable, blocks: new[] { rewrittenBlock }),
                });
        }

        Assert.Contains(
            QoraMirVerifier.Verify(
                WithFunctors((MirFunctor)int.MaxValue)),
            error => error.Code == "MIR140");
        Assert.Contains(
            QoraMirVerifier.Verify(
                WithFunctors(
                    MirFunctor.Adjoint,
                    MirFunctor.Adjoint)),
            error => error.Code == "MIR141");
        Assert.Contains(
            QoraMirVerifier.Verify(
                WithFunctors(
                    MirFunctor.Controlled,
                    MirFunctor.Controlled)),
            error => error.Code == "MIR141");
        Assert.Contains(
            QoraMirVerifier.Verify(
                WithFunctors(
                    MirFunctor.Controlled,
                    MirFunctor.Adjoint)),
            error => error.Code == "MIR141");
    }

    private static MirCallable CopyCallable(
        MirCallable source,
        MirBlockId? entryBlock = null,
        IReadOnlyList<MirBlock>? blocks = null) =>
        new(
            source.Id,
            source.Name,
            source.ReturnType,
            source.Parameters,
            entryBlock ?? source.EntryBlock,
            blocks ?? source.Blocks,
            source.Values,
            source.Storages,
            source.Origin);

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
        return new MirProgram(
            program.EntryPoint,
            program.Callables
                .Select(candidate =>
                    candidate.Id == callable.Id
                        ? rewritten
                        : candidate)
                .ToArray());
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
                var definition = callable.RequireValue(value).Definition;
                if (definition.Instruction is not MirInstructionId instruction)
                    return false;

                return callable
                    .RequireInstructionLocation(instruction)
                    .Block.Id == backedgeBlock.Id;
            });
    }
}
