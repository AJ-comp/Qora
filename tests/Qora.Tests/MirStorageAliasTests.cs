using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirStorageAliasTests
{
    [Fact]
    public void SameReadonlyActualPassedToTwoFormalsRemainsMayAlias()
    {
        var program = CompileMir("""
            operation Observe(left: int[], right: int[], target: Qubit) {
                if (left[0] == right[0]) {
                    X(target);
                }
            }

            operation Main() {
                use register = Qubit[1];
                var values: int[] = [1];
                Observe(values, values, register[0]);
            }
            """);

        var observe = Callable(program, "Observe");
        var formalStorages = observe.Storages
            .Where(storage => storage.Kind == MirArrayStorageKind.Parameter)
            .OrderBy(storage => storage.ParameterIndex)
            .ToArray();
        Assert.Equal(2, formalStorages.Length);
        Assert.NotEqual(formalStorages[0].Id, formalStorages[1].Id);
        Assert.All(
            formalStorages,
            storage => Assert.Equal(MirStorageAliasMode.SharedParameter, storage.AliasMode));

        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget target
                && target.Callable == observe.Id);
        var leftActual = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        var rightActual = Assert.IsType<MirClassicalCallOperand>(call.Operands[1]).Value;
        Assert.Equal(leftActual, rightActual);

        var left = Complete(formalStorages[0].Id);
        var right = Complete(formalStorages[1].Id);
        Assert.True(MirStorageAliasAnalysis.MayAlias(observe, left, right));
    }

    [Fact]
    public void LocalAndExclusiveRegionsAreDisjointFromDifferentStorageRegions()
    {
        var program = CompileMir("""
            operation Contracts(
                var writable: int[],
                move consumed: int[],
                shared: int[]) {
                var local: int[] = [0];
            }

            operation Main() {
                var writable: int[] = [1];
                var consumed: int[] = [2];
                var shared: int[] = [3];
                Contracts(var writable, move consumed, shared);
            }
            """);

        var contracts = Callable(program, "Contracts");
        var writable = StorageAt(contracts, 0);
        var consumed = StorageAt(contracts, 1);
        var shared = StorageAt(contracts, 2);
        var local = Assert.Single(
            contracts.Storages,
            storage => storage.Kind == MirArrayStorageKind.Local);

        Assert.Equal(MirStorageAliasMode.ExclusiveParameter, writable.AliasMode);
        Assert.Equal(MirStorageAliasMode.ExclusiveParameter, consumed.AliasMode);
        Assert.Equal(MirStorageAliasMode.SharedParameter, shared.AliasMode);
        Assert.Equal(MirStorageAliasMode.UniqueLocal, local.AliasMode);

        Assert.False(MirStorageAliasAnalysis.MayAlias(
            contracts,
            Complete(writable.Id),
            Complete(consumed.Id)));
        Assert.False(MirStorageAliasAnalysis.MayAlias(
            contracts,
            Complete(writable.Id),
            Complete(shared.Id)));
        Assert.False(MirStorageAliasAnalysis.MayAlias(
            contracts,
            Complete(local.Id),
            Complete(shared.Id)));
    }

    [Fact]
    public void IncompleteOrUnknownProvenanceNeverProvesDisjointness()
    {
        var program = CompileMir("""
            operation Main() {
                var left: int[] = [1];
                var right: int[] = [2];
            }
            """);
        var main = Callable(program, "Main");
        var storages = main.Storages.OrderBy(storage => storage.Id.Value).ToArray();
        Assert.Equal(2, storages.Length);

        Assert.True(MirStorageAliasAnalysis.MayAlias(
            main,
            new MirStorageProvenance(Array.Empty<MirStorageId>(), IsComplete: false),
            Complete(storages[1].Id)));
        Assert.True(MirStorageAliasAnalysis.MayAlias(
            main,
            Complete(new MirStorageId(int.MaxValue)),
            Complete(storages[1].Id)));
    }

    [Fact]
    public void VerifierRejectsAnAliasModeThatContradictsTheStorageContract()
    {
        var program = CompileMir("""
            operation Inspect(values: int[]) {}

            operation Main() {
                var values: int[] = [1];
                Inspect(values);
            }
            """);
        var inspect = Callable(program, "Inspect");
        var storage = Assert.Single(inspect.Storages);
        var malformedStorage = storage with { AliasMode = MirStorageAliasMode.ExclusiveParameter };
        var malformedCallable = RebuildCallable(
            inspect,
            storages: new[] { malformedStorage });
        var malformedProgram = new MirProgram(
            program.SnapshotId,
            program.Origins,
            program.EntryPoint,
            program.Callables
                .Select(callable => callable.Id == inspect.Id ? malformedCallable : callable)
                .ToArray());

        Assert.Contains(
            QoraMirVerifier.Verify(malformedProgram),
            error => error.Code == "MIR138");
    }

    [Fact]
    public void VerifierRejectsTwoMutableSlotsThatAliasInTransformedMir()
    {
        var program = CompileMir("""
            operation Touch(var left: int[], var right: int[], target: Qubit) {
                left[0] = left[0] + 1;
                right[0] = right[0] + 1;
                X(target);
            }

            operation Main() {
                use q = Qubit[1];
                var left: int[] = [1];
                var right: int[] = [2];
                Touch(var left, var right, q[0]);
            }
            """);
        var main = Callable(program, "Main");
        var originalCall = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var first = Assert.IsType<MirClassicalCallOperand>(
            originalCall.Operands[0]);
        var second = Assert.IsType<MirClassicalCallOperand>(
            originalCall.Operands[1]);
        var aliasedCall = new MirQuantumApply(
            originalCall.Id,
            originalCall.Target,
            originalCall.Operands
                .Select((operand, index) => index == 1
                    ? second with { Value = first.Value }
                    : operand)
                .ToArray(),
            originalCall.QubitResults,
            originalCall.MutableArrayResults,
            originalCall.Functors,
            originalCall.Origin);
        var malformedMain = ReplaceInstruction(main, aliasedCall);
        var malformedProgram = new MirProgram(
            program.SnapshotId,
            program.Origins,
            program.EntryPoint,
            program.Callables
                .Select(callable => callable.Id == main.Id
                    ? malformedMain
                    : callable)
                .ToArray());

        var error = Assert.Single(
            QoraMirVerifier.Verify(malformedProgram),
            candidate => candidate.Code == "MIR142");
        Assert.Contains("operands 0 and 1", error.Message);
    }

    private static MirStorageProvenance Complete(
        MirStorageId storage) =>
        new(
            new[] { storage },
            IsComplete: true);

    private static MirArrayStorage StorageAt(MirCallable callable, int parameterIndex) =>
        Assert.Single(
            callable.Storages,
            storage => storage.ParameterIndex == parameterIndex);

    private static MirProgram CompileMir(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
        return Assert.IsType<MirProgram>(result.Mir?.Program);
    }

    private static MirCallable Callable(MirProgram program, string name) =>
        Assert.Single(program.Callables, callable => callable.Name == name);

    private static MirCallable ReplaceInstruction(
        MirCallable callable,
        MirInstruction replacement) =>
        RebuildCallable(
            callable,
            blocks: callable.Blocks
                .Select(block => block.Instructions.Any(
                        instruction => instruction.Id == replacement.Id)
                    ? block with
                    {
                        Instructions = block.Instructions
                            .Select(instruction => instruction.Id == replacement.Id
                                ? replacement
                                : instruction)
                            .ToArray(),
                    }
                    : block)
                .ToArray());

    private static MirCallable RebuildCallable(
        MirCallable source,
        IReadOnlyList<MirBlock>? blocks = null,
        IReadOnlyList<MirArrayStorage>? storages = null) =>
        new(
            source.Id,
            source.Name,
            source.Kind,
            source.ReturnType,
            source.Parameters,
            source.EntryBlock,
            blocks ?? source.Blocks,
            source.Values,
            storages ?? source.Storages,
            source.Origin);
}
