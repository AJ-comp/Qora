using Qora.Compiler;
using Qora.Ir.Mir;

namespace Qora.Tests;

/// <summary>
/// Internal test seam for hand-authored MIR. Production construction remains compiler-owned, while
/// fixtures still receive one exact snapshot identity for their program, origins, and references.
/// </summary>
internal sealed class MirTestContext
{
    private MirTestContext(MirSnapshotId snapshotId)
    {
        SnapshotId = snapshotId;
    }

    public MirSnapshotId SnapshotId { get; }

    public static MirTestContext Create(int mirRevision = 0) =>
        new(
            new MirSnapshotId(
                CompilationId.New(),
                new CompilationRevision(0),
                mirRevision));

    public static MirTestContext For(MirSnapshotId snapshotId) =>
        new(snapshotId);

    public MirOriginRef Origin(int index = 0) =>
        new(SnapshotId, index);

    public MirOriginTable Origins(
        params (int OperationId, int NodeId)[] hirOrigins)
    {
        var sources = hirOrigins.Length == 0
            ? new[] { (OperationId: 1, NodeId: 1) }
            : hirOrigins;
        return new MirOriginTable(
            SnapshotId,
            sources.Select(
                (source, index) => MirOrigin.FromHir(
                    Origin(index),
                    source.OperationId,
                    source.NodeId)));
    }

    public MirProgram Program(
        IEnumerable<MirCallable> callables,
        params (int OperationId, int NodeId)[] hirOrigins) =>
        new(
            SnapshotId,
            Origins(hirOrigins),
            callables);

    public MirCallableRef Callable(MirCallableId callable) =>
        new(SnapshotId, callable);

    public MirBlockRef Block(MirCallableId callable, MirBlockId block) =>
        new(SnapshotId, callable, block);

    public MirInstructionRef Instruction(
        MirCallableId callable,
        MirInstructionId instruction) =>
        new(SnapshotId, callable, instruction);

    public MirValueRef Value(MirCallableId callable, MirValueId value) =>
        new(SnapshotId, callable, value);

    public MirStorageRef Storage(MirCallableId callable, MirStorageId storage) =>
        new(SnapshotId, callable, storage);

    public MirQubitResourceRef Qubit(
        MirCallableId callable,
        MirQubitResourceId resource) =>
        new(SnapshotId, callable, resource);
}
