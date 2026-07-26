using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class QoraMirTests
{
    [Fact]
    public void ScalarReassignmentCreatesDistinctSsaValuesAndEffectsKeepTheForwardWitness()
    {
        var (program, effects) = CompileMir("""
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

        var flipIf = Callable(program, "FlipIf");
        var main = Callable(program, "Main");
        var calls = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Where(call => call.Target is MirUserCallableTarget target
                && target.Callable == flipIf.Id)
            .ToArray();
        Assert.Equal(2, calls.Length);

        var firstInput = Assert.IsType<MirClassicalCallOperand>(calls[0].Operands[0]).Value;
        var secondInput = Assert.IsType<MirClassicalCallOperand>(calls[1].Operands[0]).Value;
        Assert.NotEqual(firstInput, secondInput);

        var firstValue = Assert.IsType<MirValue>(main.FindValue(firstInput));
        var secondValue = Assert.IsType<MirValue>(main.FindValue(secondInput));
        Assert.NotNull(firstValue.SourceSymbolId);
        Assert.Equal(firstValue.SourceSymbolId, secondValue.SourceSymbolId);

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
        var (program, _) = CompileMir("""
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
            argument => argument.Value == mergedInput);
        Assert.Equal(mergedValue.SourceSymbolId, blockArgument.SourceSymbolId);

        var incoming = IncomingArguments(main, mergeBlock.Id);
        Assert.Equal(2, incoming.Count);
        Assert.All(incoming, arguments => Assert.Equal(mergeBlock.Arguments.Count, arguments.Count));
        Assert.Equal(2, incoming.Select(arguments => arguments[blockArgumentIndex(mergeBlock, blockArgument)]).Distinct().Count());

        static int blockArgumentIndex(MirBlock block, MirBlockArgument argument) =>
            block.Arguments.ToList().FindIndex(candidate => candidate.Value == argument.Value);
    }

    [Fact]
    public void WhileAndForHeadersCarryLoopValuesThroughBackedgeArguments()
    {
        var (program, _) = CompileMir("""
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
        Assert.Equal(new[] { create.Storage }, arrayEffect.Storage.PossibleStorages);
    }

    [Fact]
    public void ShadowedArraysWithTheSameSpellingHaveDistinctStorageIdentity()
    {
        var (program, _) = CompileMir("""
            operation Main() {
                var values: int[] = [1];
                if (1 == 1) {
                    var values: int[] = [2];
                    values[0] = 3;
                }
                values[0] = 4;
            }
            """);

        var main = Callable(program, "Main");
        var storages = main.Storages
            .Where(storage => storage.Name == "values")
            .OrderBy(storage => storage.Id.Value)
            .ToArray();
        Assert.Equal(2, storages.Length);
        Assert.NotEqual(storages[0].Id, storages[1].Id);
        Assert.NotNull(storages[0].SourceSymbolId);
        Assert.NotNull(storages[1].SourceSymbolId);
        Assert.NotEqual(storages[0].SourceSymbolId, storages[1].SourceSymbolId);

        var creates = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirArrayCreate>()
            .ToArray();
        Assert.Equal(2, creates.Length);
        Assert.Equal(2, creates.Select(create => create.Storage).Distinct().Count());
    }

    [Fact]
    public void UserCallTargetsCallableIdAndKeepsTheExactDynamicQubitIndexValue()
    {
        var (program, effects) = CompileMir("""
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

        var apply = Callable(program, "Apply");
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget);
        var target = Assert.IsType<MirUserCallableTarget>(call.Target);
        Assert.Equal(apply.Id, target.Callable);

        var qubitOperand = Assert.IsType<MirQubitCallOperand>(Assert.Single(call.Operands));
        var index = Assert.IsType<MirValueId>(qubitOperand.Place.Index);
        var indexValue = Assert.IsType<MirValue>(main.FindValue(index));
        Assert.NotNull(indexValue.SourceSymbolId);

        var witness = Assert.Single(
            EffectFor(effects, main.Id, call.Id).ClassicalWitnesses,
            candidate => candidate.Role == MirClassicalWitnessRole.QubitIndex);
        Assert.Equal(index, witness.Value);
    }

    [Fact]
    public void EffectSnapshotRejectsAnotherProgramInstanceAndRevision()
    {
        var (program, effects) = CompileMir("""
            operation Main() {
                use register = Qubit[1];
                X(register[0]);
            }
            """);

        Assert.True(effects.IsFor(program));
        effects.EnsureFor(program);

        var sameRevisionCopy = program with { Callables = program.Callables.ToArray() };
        Assert.False(effects.IsFor(sameRevisionCopy));
        Assert.Throws<InvalidOperationException>(() => effects.EnsureFor(sameRevisionCopy));

        var laterRevision = program with { Revision = program.Revision + 1 };
        Assert.False(effects.IsFor(laterRevision));
        Assert.Throws<InvalidOperationException>(() => effects.EnsureFor(laterRevision));
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

        var summary = Assert.IsType<MirCallableEffectSummary>(effects.SummaryOf(main.Id));
        Assert.True(summary.TransfersOwnership);
    }

    [Fact]
    public void VerifierRejectsAMissingEntryBlock()
    {
        var (program, _) = CompileMir("operation Main() {}");
        var callable = Assert.Single(program.Callables);
        var malformedCallable = callable with { EntryBlock = new MirBlockId(int.MaxValue) };
        var malformed = program with
        {
            Revision = program.Revision + 1,
            Callables = new[] { malformedCallable },
        };

        var errors = QoraMirVerifier.Verify(malformed);
        Assert.Contains(errors, error => error.Code == "MIR014");
        var exception = Assert.Throws<InvalidOperationException>(
            () => QoraMirVerifier.VerifyOrThrow(malformed));
        Assert.Contains("MIR014", exception.Message);
    }

    private static (MirProgram Program, MirEffectSnapshot Effects) CompileMir(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
        return (
            Assert.IsType<MirProgram>(result.Mir),
            Assert.IsType<MirEffectSnapshot>(result.MirEffects));
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
            value => callable.FindValue(value)?.Definition.Block == backedgeBlock.Id);
    }
}
