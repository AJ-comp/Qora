using Qora.Compiler;
using Qora.Ir.Mir;

namespace Qora.Tests;

/// <summary>
/// Internal test seam for hand-authored MIR. Production construction remains compiler-owned, while
/// fixtures receive only the direct origins and owner-local entities needed by their program.
/// </summary>
internal sealed class MirTestContext
{
    private static readonly Lazy<MirOrigin> SharedOrigin = new(CreateOrigin);

    private MirTestContext()
    {
    }

    public static MirTestContext Create() => new();

    public static MirBlockId BlockId(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        var allocator = new MirBlockId.Allocator();
        var blockId = allocator.Allocate();
        for (var index = 0; index < ordinal; index++)
            blockId = allocator.Allocate();
        return blockId;
    }

    public MirOrigin Origin() => SharedOrigin.Value;

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

    private static MirOrigin CreateOrigin()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { }",
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        var hirArtifact = compilation.Hir.EffectAnalysis
            ?? throw new InvalidOperationException("The MIR test origin requires final HIR.");
        var sourceHirNode = hirArtifact.Source.Program.EntryCallable
            ?? throw new InvalidOperationException("The MIR test origin requires an entry operation.");

        return MirOrigin.FromHirNode(hirArtifact, sourceHirNode);
    }
}
