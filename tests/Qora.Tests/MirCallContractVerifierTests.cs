using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MirCallContractVerifierTests
{
    [Fact]
    public void VerifierRejectsKnownArrayShorterThanCalleeMinimumLength()
    {
        var context = MirTestContext.Create();
        var callee = RequiredLengthOperation(
            context,
            new MirCallableId(0),
            "RequiresSix",
            minimumLength: 6);
        var entry = EntryCallingWithTwoElementArray(
            context,
            new MirCallableId(1),
            callee.Id);
        var program = context.Program(entry.Id, new[] { callee, entry });

        var error = Assert.Single(
            QoraMirVerifier.Verify(program),
            candidate => candidate.Code == "MIR152");

        Assert.Contains("at least 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("requires at least 6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsCallerParameterWithInsufficientMinimumLength()
    {
        var context = MirTestContext.Create();
        var callee = RequiredLengthOperation(
            context,
            new MirCallableId(0),
            "RequiresSix",
            minimumLength: 6);
        var forwarder = ForwardingOperation(
            context,
            new MirCallableId(1),
            callee.Id,
            minimumLength: 2);
        var entry = EmptyEntryOperation(context, new MirCallableId(2));
        var program = context.Program(
            entry.Id,
            new[] { callee, forwarder, entry });

        var error = Assert.Single(
            QoraMirVerifier.Verify(program),
            candidate => candidate.Code == "MIR152");

        Assert.Contains("at least 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("requires at least 6", error.Message, StringComparison.Ordinal);
    }

    private static MirCallable RequiredLengthOperation(
        MirTestContext context,
        MirCallableId callableId,
        string name,
        int minimumLength)
    {
        var origin = context.Origin();
        var entryBlockId = new MirBlockId(0);
        var parameterValueId = new MirValueId(0);
        var parameterStorageId = new MirStorageId(0);

        return new MirCallable(
            callableId,
            name,
            returnType: null,
            parameters: new IMirParameter[]
            {
                new MirClassicalParameter(
                    "values",
                    parameterValueId,
                    parameterStorageId,
                    MinimumLength: minimumLength),
            },
            entryBlock: entryBlockId,
            blocks: new[]
            {
                new MirBlock(
                    entryBlockId,
                    Array.Empty<MirValueId>(),
                    Array.Empty<MirInstruction>(),
                    new MirReturn(null, origin),
                    origin),
            },
            values: new[]
            {
                new MirValue(
                    parameterValueId,
                    MirType.Array(QType.Int),
                    MirValueDefinition.ParameterAt(0),
                    origin),
            },
            storages: new[]
            {
                new MirArrayStorage(parameterStorageId, "values", origin),
            },
            origin);
    }

    private static MirCallable EntryCallingWithTwoElementArray(
        MirTestContext context,
        MirCallableId callableId,
        MirCallableId calleeId)
    {
        var origin = context.Origin();
        var entryBlockId = new MirBlockId(0);
        var firstElementId = new MirValueId(0);
        var secondElementId = new MirValueId(1);
        var arrayValueId = new MirValueId(2);
        var storageId = new MirStorageId(0);
        var firstConstantId = new MirInstructionId(0);
        var secondConstantId = new MirInstructionId(1);
        var arrayCreateId = new MirInstructionId(2);
        var callId = new MirInstructionId(3);

        return new MirCallable(
            callableId,
            "Main",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlockId,
            blocks: new[]
            {
                new MirBlock(
                    entryBlockId,
                    Array.Empty<MirValueId>(),
                    new MirInstruction[]
                    {
                        new MirConstant(firstConstantId, firstElementId, "10", origin),
                        new MirConstant(secondConstantId, secondElementId, "20", origin),
                        new MirArrayCreate(
                            arrayCreateId,
                            arrayValueId,
                            storageId,
                            MirArrayInitialization.ExplicitElements,
                            new[] { firstElementId, secondElementId },
                            origin),
                        new MirQuantumApply(
                            callId,
                            new MirUserCallableTarget(calleeId),
                            new MirCallOperand[]
                            {
                                new MirClassicalCallOperand(arrayValueId),
                            },
                            Array.Empty<MirQubitAfterInstruction>(),
                            Array.Empty<MirMutableArrayResult>(),
                            Array.Empty<MirFunctor>(),
                            origin),
                    },
                    new MirReturn(null, origin),
                    origin),
            },
            values: new[]
            {
                new MirValue(
                    firstElementId,
                    MirType.Scalar(QType.Int),
                    MirValueDefinition.InstructionResultAt(firstConstantId),
                    origin),
                new MirValue(
                    secondElementId,
                    MirType.Scalar(QType.Int),
                    MirValueDefinition.InstructionResultAt(secondConstantId),
                    origin),
                new MirValue(
                    arrayValueId,
                    MirType.Array(QType.Int, knownLength: 2),
                    MirValueDefinition.InstructionResultAt(arrayCreateId),
                    origin),
            },
            storages: new[]
            {
                new MirArrayStorage(storageId, "values", origin),
            },
            origin);
    }

    private static MirCallable ForwardingOperation(
        MirTestContext context,
        MirCallableId callableId,
        MirCallableId calleeId,
        int minimumLength)
    {
        var origin = context.Origin();
        var entryBlockId = new MirBlockId(0);
        var parameterValueId = new MirValueId(0);
        var parameterStorageId = new MirStorageId(0);
        var callId = new MirInstructionId(0);

        return new MirCallable(
            callableId,
            "Forward",
            returnType: null,
            parameters: new IMirParameter[]
            {
                new MirClassicalParameter(
                    "values",
                    parameterValueId,
                    parameterStorageId,
                    MinimumLength: minimumLength),
            },
            entryBlock: entryBlockId,
            blocks: new[]
            {
                new MirBlock(
                    entryBlockId,
                    Array.Empty<MirValueId>(),
                    new MirInstruction[]
                    {
                        new MirQuantumApply(
                            callId,
                            new MirUserCallableTarget(calleeId),
                            new MirCallOperand[]
                            {
                                new MirClassicalCallOperand(parameterValueId),
                            },
                            Array.Empty<MirQubitAfterInstruction>(),
                            Array.Empty<MirMutableArrayResult>(),
                            Array.Empty<MirFunctor>(),
                            origin),
                    },
                    new MirReturn(null, origin),
                    origin),
            },
            values: new[]
            {
                new MirValue(
                    parameterValueId,
                    MirType.Array(QType.Int),
                    MirValueDefinition.ParameterAt(0),
                    origin),
            },
            storages: new[]
            {
                new MirArrayStorage(parameterStorageId, "values", origin),
            },
            origin);
    }

    private static MirCallable EmptyEntryOperation(
        MirTestContext context,
        MirCallableId callableId)
    {
        var origin = context.Origin();
        var entryBlockId = new MirBlockId(0);

        return new MirCallable(
            callableId,
            "Main",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlockId,
            blocks: new[]
            {
                new MirBlock(
                    entryBlockId,
                    Array.Empty<MirValueId>(),
                    Array.Empty<MirInstruction>(),
                    new MirReturn(null, origin),
                    origin),
            },
            values: Array.Empty<MirValue>(),
            storages: Array.Empty<MirArrayStorage>(),
            origin);
    }
}
