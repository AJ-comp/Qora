using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

/// <summary>
/// Internal test seam for hand-authored MIR. Production construction remains compiler-owned, while
/// fixtures receive only the direct origins and owner-local entities needed by their program.
/// </summary>
internal sealed class MirTestContext
{
    private MirTestContext()
    {
    }

    public static MirTestContext Create() => new();

    public MirOrigin Origin(int index = 0) =>
        new MirHirOrigin(
            new HirNodeId(index + 1),
            span: null);

    public MirProgram Program(
        MirCallable entryPoint,
        IEnumerable<MirCallable> callables) =>
        new(
            entryPoint,
            callables);

    public MirInstructionSite InstructionSite(
        MirCallableId callable,
        MirInstructionId instruction) =>
        new(callable, instruction);
}
