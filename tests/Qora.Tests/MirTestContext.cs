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

    public MirOriginId Origin(int index = 0) =>
        new(index);

    public MirOriginTable Origins(params HirNodeId[] hirOriginNodes)
    {
        var sources = hirOriginNodes.Length == 0
            ? new[]
            {
                new HirNodeId(1),
            }
            : hirOriginNodes;
        return new MirOriginTable(
            sources.Select(
                nodeId => MirOrigin.FromHir(nodeId)));
    }

    public MirProgram Program(
        MirCallableId entryPoint,
        IEnumerable<MirCallable> callables,
        params HirNodeId[] hirOriginNodes) =>
        new(
            SnapshotId,
            Origins(hirOriginNodes),
            entryPoint,
            callables);

    public MirInstructionSite InstructionSite(
        MirCallableId callable,
        MirInstructionId instruction) =>
        new(callable, instruction);
}
