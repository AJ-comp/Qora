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
        var analyses = new MirAnalysisStore(program);
        var cfg = analyses.ControlFlow(callable);

        Assert.Equal(new[] { B(1), B(2) }, cfg.SuccessorsOf(B(0)));
        Assert.Equal(new[] { B(1), B(2) }, cfg.PredecessorsOf(B(3)));
        Assert.Equal(
            new[] { B(0), B(1), B(2), B(3) },
            cfg.ReachableBlocks);
        Assert.False(cfg.IsReachable(B(4)));

        Assert.True(cfg.Dominates(B(0), B(3)));
        Assert.False(cfg.Dominates(B(1), B(3)));
        Assert.True(cfg.PostDominates(B(3), B(0)));
        Assert.True(cfg.PostDominates(B(3), B(1)));
        Assert.False(cfg.PostDominates(B(1), B(0)));

    }

    [Fact]
    public void AvailabilityUsesInstructionOrderDominanceAndBlockArguments()
    {
        var program = DiamondProgram();
        var callable = Assert.Single(program.Callables);
        var analyses = new MirAnalysisStore(program);
        var cfg = analyses.ControlFlow(callable);

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
        var context = MirTestContext.Create();
        var source = context.Origin();
        var value = new MirValue(
            V(0),
            MirType.Scalar(QType.Int),
            source);
        var parameter = MirClassicalParameter.Scalar("input", value);
        var entryBlock = new MirBlock(
            B(0),
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirReturn(null, source),
            source);
        var callable = new MirCallable(
            C(0),
            "ParameterScope",
            returnType: null,
            parameters: new IMirParameter[] { parameter },
            entryBlock: entryBlock,
            blocks: new[]
            {
                entryBlock,
                new MirBlock(B(1), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
            },
            source);
        var entryPoint = EmptyEntry(C(1), source);
        var program = context.Program(
            entryPoint,
            new[] { callable, entryPoint });
        var analyses = new MirAnalysisStore(program);
        var cfg = analyses.ControlFlow(callable);

        Assert.False(cfg.IsReachable(B(1)));
        Assert.True(cfg.IsValueAvailableAtTerminator(V(0), B(1)));
    }

    [Fact]
    public void MultipleExitsAndANonTerminatingLoopDoNotCreateFalsePostDominance()
    {
        var context = MirTestContext.Create();
        var source = context.Origin();
        var conditionValue = new MirValue(V(0), MirType.Scalar(QType.Bit), source);
        var condition = new MirConstant(
            I(0),
            conditionValue,
            "1",
            source);
        var entryBlock = new MirBlock(
            B(0),
            Array.Empty<MirValue>(),
            new MirInstruction[] { condition },
            new MirBranch(
                V(0),
                B(1),
                Array.Empty<MirValueId>(),
                B(2),
                Array.Empty<MirValueId>(),
                source),
            source);
        var callable = new MirCallable(
            C(0),
            "MixedExits",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlock,
            blocks: new[]
            {
                entryBlock,
                new MirBlock(B(1), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
                new MirBlock(B(2), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirBranch(V(0), B(3), Array.Empty<MirValueId>(),
                        B(4), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(3), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirUnreachable(source), source),
                new MirBlock(B(4), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirJump(B(4), Array.Empty<MirValueId>(), source), source),
            },
            source);
        var program = context.Program(callable, new[] { callable });
        var analyses = new MirAnalysisStore(program);
        var cfg = analyses.ControlFlow(callable);

        Assert.True(cfg.PostDominates(B(4), B(4)));
        Assert.False(cfg.PostDominates(B(1), B(0)));
        Assert.False(cfg.PostDominates(B(3), B(2)));
        Assert.False(cfg.PostDominates(B(4), B(2)));
    }

    [Fact]
    public void LoopFixedPointFindsBackedgeDominanceAndExitPostDominance()
    {
        var context = MirTestContext.Create();
        var source = context.Origin();
        var conditionValue = new MirValue(V(0), MirType.Scalar(QType.Bit), source);
        var condition = new MirConstant(
            I(0),
            conditionValue,
            "1",
            source);
        var entryBlock = new MirBlock(
            B(0),
            Array.Empty<MirValue>(),
            new MirInstruction[] { condition },
            new MirJump(B(1), Array.Empty<MirValueId>(), source),
            source);
        var callable = new MirCallable(
            C(0),
            "Loop",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlock,
            blocks: new[]
            {
                entryBlock,
                new MirBlock(B(1), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirBranch(V(0), B(2), Array.Empty<MirValueId>(),
                        B(3), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(2), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirJump(B(1), Array.Empty<MirValueId>(), source), source),
                new MirBlock(B(3), Array.Empty<MirValue>(), Array.Empty<MirInstruction>(),
                    new MirReturn(null, source), source),
            },
            source);
        var program = context.Program(callable, new[] { callable });
        var analyses = new MirAnalysisStore(program);
        var cfg = analyses.ControlFlow(callable);

        Assert.True(cfg.Dominates(B(1), B(2)));
        Assert.True(cfg.PostDominates(B(3), B(1)));
        Assert.True(cfg.PostDominates(B(3), B(2)));
    }

    [Fact]
    public void AnalysisStoreCachesControlFlowForTheSameCallable()
    {
        var program = DiamondProgram();
        var callable = Assert.Single(program.Callables);
        var analyses = new MirAnalysisStore(program);
        var first = analyses.ControlFlow(callable);
        var second = analyses.ControlFlow(callable);

        Assert.Same(first, second);
    }

    private static MirProgram DiamondProgram()
    {
        var context = MirTestContext.Create();
        var source = context.Origin();
        var conditionValue = new MirValue(V(0), MirType.Scalar(QType.Bit), source);
        var leftValue = new MirValue(V(1), MirType.Scalar(QType.Int), source);
        var rightValue = new MirValue(V(2), MirType.Scalar(QType.Int), source);
        var joinValue = new MirValue(V(3), MirType.Scalar(QType.Int), source);
        var disconnectedValue = new MirValue(V(4), MirType.Scalar(QType.Int), source);
        var condition = new MirConstant(
            I(0),
            conditionValue,
            "1",
            source);
        var left = new MirConstant(
            I(1),
            leftValue,
            "10",
            source);
        var right = new MirConstant(
            I(2),
            rightValue,
            "20",
            source);
        var disconnected = new MirConstant(
            I(3),
            disconnectedValue,
            "30",
            source);
        var entryBlock = new MirBlock(
            B(0),
            Array.Empty<MirValue>(),
            new MirInstruction[] { condition },
            new MirBranch(
                V(0),
                B(1),
                Array.Empty<MirValueId>(),
                B(2),
                Array.Empty<MirValueId>(),
                source),
            source);

        var callable = new MirCallable(
            C(0),
            "Diamond",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlock,
            blocks: new[]
            {
                entryBlock,
                new MirBlock(B(1), Array.Empty<MirValue>(), new MirInstruction[] { left },
                    new MirJump(B(3), new[] { V(1) }, source), source),
                new MirBlock(B(2), Array.Empty<MirValue>(), new MirInstruction[] { right },
                    new MirJump(B(3), new[] { V(2) }, source), source),
                new MirBlock(B(3),
                    new[] { joinValue },
                    Array.Empty<MirInstruction>(),
                    new MirReturn(null, source),
                    source),
                new MirBlock(B(4), Array.Empty<MirValue>(), new MirInstruction[] { disconnected },
                    new MirReturn(null, source), source),
            },
            source);
        return context.Program(callable, new[] { callable });
    }

    private static MirCallable EmptyEntry(MirCallableId id, MirOrigin source)
    {
        var entryBlock = new MirBlock(
            B(0),
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirReturn(null, source),
            source);
        return new MirCallable(
            id,
            "Main",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: entryBlock,
            blocks: new[] { entryBlock },
            source);
    }

    private static MirCallableId C(int value) => new(value);
    private static MirBlockId B(int value) => MirTestContext.BlockId(value);
    private static MirInstructionId I(int value) => new(value);
    private static MirValueId V(int value) => new(value);
}
