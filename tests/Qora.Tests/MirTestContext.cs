using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

/// <summary>
/// Internal test seam for hand-authored MIR. Production construction remains compiler-owned, while
/// fixtures still receive one exact snapshot identity for their program and origins.
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
        params (HirNodeId CallableId, HirNodeId NodeId)[] hirOrigins)
    {
        var sources = hirOrigins.Length == 0
            ? new[]
            {
                (
                    CallableId: new HirNodeId(1),
                    NodeId: new HirNodeId(1)),
            }
            : hirOrigins;
        return new MirOriginTable(
            SnapshotId,
            sources.Select(
                (source, index) => MirOrigin.FromHir(
                    Origin(index),
                    source.CallableId,
                    source.NodeId)));
    }

    public MirProgram Program(
        MirCallableId entryPoint,
        IEnumerable<MirCallable> callables,
        params (HirNodeId CallableId, HirNodeId NodeId)[] hirOrigins) =>
        new(
            SnapshotId,
            Origins(hirOrigins),
            entryPoint,
            callables);

    public MirInstructionSite InstructionSite(
        MirCallableId callable,
        MirInstructionId instruction) =>
        new(callable, instruction);
}
