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
        var program = context.Program(entry, new[] { callee, entry });

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
            entry,
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
        var entryBlockId = MirTestContext.BlockId(0);
        var parameterValueId = new MirValueId(0);
        var parameterStorageId = new MirStorageId(0);
        var parameterValue = new MirValue(
            parameterValueId,
            MirType.Array(QType.Int),
            origin);
        var parameterStorage = new MirArrayStorage(
            parameterStorageId,
            "values",
            origin);
        var entryBlock = new MirBlock(
            entryBlockId,
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirReturn(null, origin),
            origin);

        return new MirCallable(
            callableId,
            name,
            returnType: null,
            parameters: new IMirParameter[]
            {
                MirClassicalParameter.Array(
                    "values",
                    parameterValue,
                    parameterStorage,
                    minimumLength: minimumLength),
            },
            entryBlock: entryBlock,
            blocks: new[] { entryBlock },
            origin);
    }

    private static MirCallable EntryCallingWithTwoElementArray(
        MirTestContext context,
        MirCallableId callableId,
        MirCallableId calleeId)
    {
        var origin = context.Origin();
        var entryBlockId = MirTestContext.BlockId(0);
        var firstElementId = new MirValueId(0);
        var secondElementId = new MirValueId(1);
        var arrayValueId = new MirValueId(2);
        var storageId = new MirStorageId(0);
        var firstConstantId = new MirInstructionId(0);
        var secondConstantId = new MirInstructionId(1);
        var arrayCreateId = new MirInstructionId(2);
        var callId = new MirInstructionId(3);
        var firstElement = new MirValue(
            firstElementId,
            MirType.Scalar(QType.Int),
            origin);
        var secondElement = new MirValue(
            secondElementId,
            MirType.Scalar(QType.Int),
            origin);
        var arrayValue = new MirValue(
            arrayValueId,
            MirType.Array(QType.Int, knownLength: 2),
            origin);
        var storage = new MirArrayStorage(storageId, "values", origin);
        var entryBlock = new MirBlock(
            entryBlockId,
            Array.Empty<MirValue>(),
            new MirInstruction[]
            {
                new MirConstant(firstConstantId, firstElement, "10", origin),
                new MirConstant(secondConstantId, secondElement, "20", origin),
                new MirArrayCreate(
                    arrayCreateId,
                    arrayValue,
                    storage,
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
            origin);

        return new MirCallable(
            callableId,
            "Main",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlock,
            blocks: new[] { entryBlock },
            origin);
    }

    private static MirCallable ForwardingOperation(
        MirTestContext context,
        MirCallableId callableId,
        MirCallableId calleeId,
        int minimumLength)
    {
        var origin = context.Origin();
        var entryBlockId = MirTestContext.BlockId(0);
        var parameterValueId = new MirValueId(0);
        var parameterStorageId = new MirStorageId(0);
        var callId = new MirInstructionId(0);
        var parameterValue = new MirValue(
            parameterValueId,
            MirType.Array(QType.Int),
            origin);
        var parameterStorage = new MirArrayStorage(
            parameterStorageId,
            "values",
            origin);
        var entryBlock = new MirBlock(
            entryBlockId,
            Array.Empty<MirValue>(),
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
            origin);

        return new MirCallable(
            callableId,
            "Forward",
            returnType: null,
            parameters: new IMirParameter[]
            {
                MirClassicalParameter.Array(
                    "values",
                    parameterValue,
                    parameterStorage,
                    minimumLength: minimumLength),
            },
            entryBlock: entryBlock,
            blocks: new[] { entryBlock },
            origin);
    }

    private static MirCallable EmptyEntryOperation(
        MirTestContext context,
        MirCallableId callableId)
    {
        var origin = context.Origin();
        var entryBlockId = MirTestContext.BlockId(0);
        var entryBlock = new MirBlock(
            entryBlockId,
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirReturn(null, origin),
            origin);

        return new MirCallable(
            callableId,
            "Main",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlock,
            blocks: new[] { entryBlock },
            origin);
    }
}
