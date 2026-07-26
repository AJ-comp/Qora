using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirControlFlowAnalysisTests
{
    [Fact]
    public void DiamondComputesEdgesReachabilityDominanceAndPostDominance()
    {
        var program = DiamondProgram();
        var callable = Assert.Single(program.Callables);
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);

        Assert.Equal(new[] { B(1), B(2) }, cfg.SuccessorsOf(B(0)));
        Assert.Equal(new[] { B(1), B(2) }, cfg.PredecessorsOf(B(3)));
        Assert.Equal(new[] { B(0), B(1), B(2), B(3) }, cfg.ReachableBlocks);
        Assert.False(cfg.IsReachable(B(4)));

        Assert.True(cfg.Dominates(B(0), B(3)));
        Assert.False(cfg.Dominates(B(1), B(3)));
        Assert.True(cfg.PostDominates(B(3), B(0)));
        Assert.True(cfg.PostDominates(B(3), B(1)));
        Assert.False(cfg.PostDominates(B(1), B(0)));

        Assert.Equal(new[] { B(3) }, cfg.ExitBlocks);
        Assert.True(cfg.CanReachExit(B(0)));
        Assert.False(cfg.CanReachExit(B(4)));
    }

    [Fact]
    public void AvailabilityUsesInstructionOrderDominanceAndBlockArguments()
    {
        var program = DiamondProgram();
        var callable = Assert.Single(program.Callables);
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);

        var beforeLeftDefinition = cfg.PointBeforeInstruction(I(1));
        Assert.True(beforeLeftDefinition.IsBeforeInstruction);
        Assert.Equal(0, beforeLeftDefinition.InstructionIndex);
        Assert.False(cfg.IsValueAvailableAt(V(1), beforeLeftDefinition));
        Assert.True(cfg.IsValueAvailableAtTerminator(V(1), B(1)));

        Assert.True(cfg.IsValueAvailableBeforeInstruction(V(0), I(1)));
        Assert.False(cfg.IsValueAvailableAtTerminator(V(1), B(3)));
        Assert.True(cfg.IsValueAvailableAtTerminator(V(3), B(3)));

        Assert.True(cfg.IsValueAvailableAtTerminator(V(4), B(4)));
        Assert.False(cfg.IsValueAvailableAtTerminator(V(0), B(4)));
    }

    [Fact]
    public void ParameterIsCallableWideEvenInAnUnreachableBlock()
    {
        var source = S();
        var parameter = new MirClassicalParameter(
            "input",
            SourceSymbolId: null,
            V(0),
            MirType.Scalar(QType.Int));
        var value = new MirValue(
            V(0),
            MirType.Scalar(QType.Int),
            MirValueDefinition.ParameterAt(0));
        var callable = new MirCallable(
            C(0),
            SourceOperationId: 1,
            "ParameterScope",
            MirCallableKind.Operation,
            ReturnType: null,
            Parameters: new MirParameter[] { parameter },
            EntryBlock: B(0),
            Blocks: new[]
            {
                new MirBlock(B(0), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
                new MirBlock(B(1), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
            },
            Values: new[] { value },
            Storages: Array.Empty<MirArrayStorage>(),
            Qubits: Array.Empty<MirQubitResource>(),
            source);
        var program = new MirProgram(3, new[] { callable });
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);

        Assert.False(cfg.IsReachable(B(1)));
        Assert.True(cfg.IsValueAvailableAtTerminator(V(0), B(1)));
    }

    [Fact]
    public void MultipleExitsAndANonTerminatingLoopDoNotCreateFalsePostDominance()
    {
        var source = S();
        var condition = new MirConstant(
            I(0),
            V(0),
            new MirConstantValue(QType.Bit, "1"),
            source);
        var callable = new MirCallable(
            C(0),
            SourceOperationId: 1,
            "MixedExits",
            MirCallableKind.Operation,
            ReturnType: null,
            Parameters: Array.Empty<MirParameter>(),
            EntryBlock: B(0),
            Blocks: new[]
            {
                new MirBlock(B(0), Array.Empty<MirBlockArgument>(), new MirInstruction[] { condition },
                    new MirBranch(V(0), B(1), Array.Empty<MirValueId>(),
                        B(2), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(1), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
                new MirBlock(B(2), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirBranch(V(0), B(3), Array.Empty<MirValueId>(),
                        B(4), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(3), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirUnreachable(source), source),
                new MirBlock(B(4), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirJump(B(4), Array.Empty<MirValueId>(), source), source),
            },
            Values: new[]
            {
                new MirValue(V(0), MirType.Scalar(QType.Bit),
                    MirValueDefinition.InstructionResultAt(B(0), I(0))),
            },
            Storages: Array.Empty<MirArrayStorage>(),
            Qubits: Array.Empty<MirQubitResource>(),
            source);
        var program = new MirProgram(5, new[] { callable });
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);

        Assert.Equal(new[] { B(1), B(3) }, cfg.ExitBlocks);
        Assert.False(cfg.CanReachExit(B(4)));
        Assert.True(cfg.PostDominates(B(4), B(4)));
        Assert.False(cfg.PostDominates(B(1), B(0)));
        Assert.False(cfg.PostDominates(B(3), B(2)));
        Assert.False(cfg.PostDominates(B(4), B(2)));
    }

    [Fact]
    public void LoopFixedPointFindsBackedgeDominanceAndExitPostDominance()
    {
        var source = S();
        var condition = new MirConstant(
            I(0),
            V(0),
            new MirConstantValue(QType.Bit, "1"),
            source);
        var callable = new MirCallable(
            C(0),
            SourceOperationId: 1,
            "Loop",
            MirCallableKind.Operation,
            ReturnType: null,
            Parameters: Array.Empty<MirParameter>(),
            EntryBlock: B(0),
            Blocks: new[]
            {
                new MirBlock(B(0), Array.Empty<MirBlockArgument>(), new MirInstruction[] { condition },
                    new MirJump(B(1), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(1), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirBranch(V(0), B(2), Array.Empty<MirValueId>(),
                        B(3), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(2), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirJump(B(1), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(3), Array.Empty<MirBlockArgument>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
            },
            Values: new[]
            {
                new MirValue(V(0), MirType.Scalar(QType.Bit),
                    MirValueDefinition.InstructionResultAt(B(0), I(0))),
            },
            Storages: Array.Empty<MirArrayStorage>(),
            Qubits: Array.Empty<MirQubitResource>(),
            source);
        var program = new MirProgram(9, new[] { callable });
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);

        Assert.True(cfg.Dominates(B(1), B(2)));
        Assert.True(cfg.PostDominates(B(3), B(1)));
        Assert.True(cfg.PostDominates(B(3), B(2)));
        Assert.True(cfg.CanReachExit(B(2)));
    }

    [Fact]
    public void SnapshotAndProgramPointsRejectOtherProgramInstancesAndAnalyses()
    {
        var program = DiamondProgram();
        var callable = Assert.Single(program.Callables);
        var first = MirControlFlowAnalysis.Analyze(program, callable.Id);
        var second = MirControlFlowAnalysis.Analyze(program, callable.Id);
        var firstPoint = first.PointBeforeInstruction(I(1));

        Assert.True(first.IsFor(program, callable.Id));
        first.EnsureFor(program, callable.Id);

        var copy = program with { Callables = program.Callables.ToArray() };
        Assert.False(first.IsFor(copy, callable.Id));
        Assert.Throws<InvalidOperationException>(() => first.EnsureFor(copy, callable.Id));
        Assert.Throws<InvalidOperationException>(
            () => second.IsValueAvailableAt(V(0), firstPoint));
    }

    private static MirProgram DiamondProgram()
    {
        var source = S();
        var condition = new MirConstant(
            I(0),
            V(0),
            new MirConstantValue(QType.Bit, "1"),
            source);
        var left = new MirConstant(
            I(1),
            V(1),
            new MirConstantValue(QType.Int, "10"),
            source);
        var right = new MirConstant(
            I(2),
            V(2),
            new MirConstantValue(QType.Int, "20"),
            source);
        var disconnected = new MirConstant(
            I(3),
            V(4),
            new MirConstantValue(QType.Int, "30"),
            source);

        var callable = new MirCallable(
            C(0),
            SourceOperationId: 1,
            "Diamond",
            MirCallableKind.Operation,
            ReturnType: null,
            Parameters: Array.Empty<MirParameter>(),
            EntryBlock: B(0),
            Blocks: new[]
            {
                new MirBlock(B(0), Array.Empty<MirBlockArgument>(), new MirInstruction[] { condition },
                    new MirBranch(V(0), B(1), Array.Empty<MirValueId>(),
                        B(2), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(1), Array.Empty<MirBlockArgument>(), new MirInstruction[] { left },
                    new MirJump(B(3), new[] { V(1) }, source), source),
                new MirBlock(B(2), Array.Empty<MirBlockArgument>(), new MirInstruction[] { right },
                    new MirJump(B(3), new[] { V(2) }, source), source),
                new MirBlock(B(3),
                    new[] { new MirBlockArgument(V(3), MirType.Scalar(QType.Int)) },
                    Array.Empty<MirInstruction>(),
                    new MirReturn(null, source),
                    source),
                new MirBlock(B(4), Array.Empty<MirBlockArgument>(), new MirInstruction[] { disconnected },
                    new MirReturn(null, source), source),
            },
            Values: new[]
            {
                new MirValue(V(0), MirType.Scalar(QType.Bit),
                    MirValueDefinition.InstructionResultAt(B(0), I(0))),
                new MirValue(V(1), MirType.Scalar(QType.Int),
                    MirValueDefinition.InstructionResultAt(B(1), I(1))),
                new MirValue(V(2), MirType.Scalar(QType.Int),
                    MirValueDefinition.InstructionResultAt(B(2), I(2))),
                new MirValue(V(3), MirType.Scalar(QType.Int),
                    MirValueDefinition.BlockArgumentAt(B(3), 0)),
                new MirValue(V(4), MirType.Scalar(QType.Int),
                    MirValueDefinition.InstructionResultAt(B(4), I(3))),
            },
            Storages: Array.Empty<MirArrayStorage>(),
            Qubits: Array.Empty<MirQubitResource>(),
            source);
        return new MirProgram(7, new[] { callable });
    }

    private static MirSource S() => MirSource.Synthetic(1);
    private static MirCallableId C(int value) => new(value);
    private static MirBlockId B(int value) => new(value);
    private static MirInstructionId I(int value) => new(value);
    private static MirValueId V(int value) => new(value);
}
