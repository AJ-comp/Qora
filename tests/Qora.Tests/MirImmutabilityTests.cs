using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirImmutabilityTests
{
    [Fact]
    public void MutatingConstructorInputsCannotChangeAProgramOrItsExistingAnalyses()
    {
        var compiled = CompileMir("""
            operation Main() {
                use register = Qubit[1];
                X(register[0]);
            }
            """);
        var originalCallable = Assert.Single(compiled.Callables);
        var originalBlock = Assert.Single(
            originalCallable.Blocks,
            block => block.Instructions.OfType<MirQuantumApply>().Any());
        var originalApply = Assert.Single(originalBlock.Instructions.OfType<MirQuantumApply>());

        var operands = originalApply.Operands.ToList();
        var mutableArrayResults = originalApply.MutableArrayResults.ToList();
        var functors = originalApply.Functors.ToList();
        var clonedApply = originalApply with
        {
            Operands = operands,
            MutableArrayResults = mutableArrayResults,
            Functors = functors,
        };

        var instructions = originalBlock.Instructions
            .Select(instruction => instruction.Id == originalApply.Id ? clonedApply : instruction)
            .ToList();
        var blockArguments = originalBlock.Arguments.ToList();
        var clonedBlock = originalBlock with
        {
            Arguments = blockArguments,
            Instructions = instructions,
        };

        var parameters = originalCallable.Parameters.ToList();
        var blocks = originalCallable.Blocks
            .Select(block => block.Id == originalBlock.Id ? clonedBlock : block)
            .ToList();
        var values = originalCallable.Values.ToList();
        var storages = originalCallable.Storages.ToList();
        var qubits = originalCallable.Qubits.ToList();
        var clonedCallable = originalCallable with
        {
            Parameters = parameters,
            Blocks = blocks,
            Values = values,
            Storages = storages,
            Qubits = qubits,
        };

        var callables = new List<MirCallable> { clonedCallable };
        var program = new MirProgram(
            compiled.SnapshotId,
            compiled.Origins,
            compiled.EntryPoint,
            callables);
        QoraMirVerifier.VerifyOrThrow(program);
        var effects = MirEffectAnalysis.Analyze(program);
        var cfg = MirControlFlowAnalysis.Analyze(program, clonedCallable.Id);
        var effectSite = new MirEffectSite(
            new MirBlockRef(program.SnapshotId, clonedCallable.Id, clonedBlock.Id),
            new MirInstructionRef(program.SnapshotId, clonedCallable.Id, clonedApply.Id));

        callables.Clear();
        parameters.Clear();
        blocks.Clear();
        values.Clear();
        storages.Clear();
        qubits.Clear();
        blockArguments.Clear();
        instructions.Clear();
        operands.Clear();
        mutableArrayResults.Add(new MirMutableArrayResult(0, new MirValueId(int.MaxValue)));
        functors.Add(MirFunctor.Adjoint);

        QoraMirVerifier.VerifyOrThrow(program);
        Assert.Same(clonedCallable, Assert.Single(program.Callables));
        var retainedBlock = Assert.IsType<MirBlock>(clonedCallable.FindBlock(clonedBlock.Id));
        var retainedApply = Assert.Single(retainedBlock.Instructions.OfType<MirQuantumApply>());
        Assert.Single(retainedApply.Operands);
        Assert.Empty(retainedApply.MutableArrayResults);
        Assert.Empty(retainedApply.Functors);

        effects.EnsureFor(program);
        Assert.NotNull(effects.EffectAt(effectSite));
        Assert.Single(effects.Effects);

        cfg.EnsureFor(program, clonedCallable.Id);
        Assert.Contains(
            new MirBlockRef(program.SnapshotId, clonedCallable.Id, clonedBlock.Id),
            cfg.Blocks);
        Assert.Equal(
            clonedApply.Id,
            cfg.PointBeforeInstruction(clonedApply.Id).Instruction?.Instruction);
    }

    [Fact]
    public void EveryCollectionBearingLeafNodeDefensivelyCopiesItsInput()
    {
        var context = MirTestContext.Create();
        var source = context.Origin();

        var elements = new List<MirValueId> { new(0) };
        var create = new MirArrayCreate(
            new MirInstructionId(0),
            new MirValueId(1),
            new MirStorageId(0),
            QType.Int,
            MirArrayInitialization.ExplicitElements,
            Length: 1,
            elements,
            source);

        var operands = new List<MirCallOperand>
        {
            new MirClassicalCallOperand(new MirValueId(0)),
        };
        var pureCall = new MirPureCall(
            new MirInstructionId(1),
            new MirValueId(2),
            new MirBuiltinFunctionTarget("AsInt"),
            operands,
            source);

        var jumpArguments = new List<MirValueId> { new(0) };
        var jump = new MirJump(new MirBlockId(1), jumpArguments, source);
        var trueArguments = new List<MirValueId> { new(0) };
        var falseArguments = new List<MirValueId> { new(1) };
        var branch = new MirBranch(
            new MirValueId(2),
            new MirBlockId(1),
            trueArguments,
            new MirBlockId(2),
            falseArguments,
            source);

        elements.Clear();
        operands.Clear();
        jumpArguments.Clear();
        trueArguments.Clear();
        falseArguments.Clear();

        Assert.Single(create.Elements);
        Assert.Single(pureCall.Operands);
        Assert.Single(jump.Arguments);
        Assert.Single(branch.TrueArguments);
        Assert.Single(branch.FalseArguments);
    }

    [Fact]
    public void EffectFactRecordsDefensivelyCopyTheirInputCollections()
    {
        var context = MirTestContext.Create();
        var source = context.Origin();
        var callable = new MirCallableId(0);
        var storages = new List<MirStorageRef>
        {
            context.Storage(callable, new MirStorageId(0)),
        };
        var provenance = new MirStorageProvenance(storages, IsComplete: true);

        var functors = new List<MirFunctor> { MirFunctor.Controlled };
        var qubits = new List<MirQubitOperandEffect>
        {
            new(
                null,
                new MirQubitPlaceRef(
                    context.Qubit(callable, new MirQubitResourceId(0))),
                MirQubitEffectFlags.Read),
        };
        var witnesses = new List<MirClassicalWitness>();
        var arrays = new List<MirArrayStateOperand>();
        var results = new List<MirValueRef>
        {
            context.Value(callable, new MirValueId(0)),
        };
        var effect = new MirQuantumInstructionEffect(
            new MirEffectSite(
                context.Block(callable, new MirBlockId(0)),
                context.Instruction(callable, new MirInstructionId(0))),
            MirQuantumInstructionKind.Apply,
            new MirEffectBuiltinGateTarget("X"),
            functors,
            qubits,
            witnesses,
            arrays,
            MirPathCondition.Always,
            MirExecutionMultiplicity.Single,
            results,
            IsIrreversible: false,
            TransfersOwnership: false,
            source);

        var formalQubits = new List<MirFormalQubitEffect>
        {
            new(
                context.Qubit(callable, new MirQubitResourceId(0)),
                MirQubitEffectFlags.Read),
        };
        var summary = new MirCallableEffectSummary(
            context.Callable(callable),
            formalQubits,
            IsIrreversible: false,
            TransfersOwnership: false);

        storages.Clear();
        functors.Clear();
        qubits.Clear();
        witnesses.Add(new MirClassicalWitness(
            context.Value(callable, new MirValueId(1)),
            MirType.Scalar(QType.Int),
            MirClassicalWitnessRole.CallOperand,
            OperandIndex: 0));
        arrays.Add(new MirArrayStateOperand(
            OperandIndex: 0,
            context.Value(callable, new MirValueId(0)),
            OutputState: null,
            MirType.Array(QType.Int),
            QOwnershipMode.Borrowed,
            QAccessMode.ReadOnly,
            provenance));
        results.Clear();
        formalQubits.Clear();

        Assert.Single(provenance.PossibleStorages);
        Assert.Single(effect.Functors);
        Assert.Single(effect.Qubits);
        Assert.Empty(effect.ClassicalWitnesses);
        Assert.Empty(effect.ArrayStates);
        Assert.True(effect.PathCondition.IsAlways);
        Assert.Single(effect.Results);
        Assert.Single(summary.FormalQubits);
    }

    private static MirProgram CompileMir(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
        return Assert.IsType<MirProgram>(result.Mir?.Program);
    }
}
