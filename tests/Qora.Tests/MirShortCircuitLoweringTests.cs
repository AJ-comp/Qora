using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirShortCircuitLoweringTests
{
    [Fact]
    public void LogicalAndEvaluatesRightCallOnlyOnTrueEdge()
    {
        var (_, main) = CompileMain("""
            function value(): int {
                return 1;
            }

            operation Main() {
                if (false && value() == 1) {
                }
            }
            """);

        var entry = main.EntryBlock;
        var branch = Assert.IsType<MirBranch>(entry.Terminator);
        Assert.Empty(entry.Instructions.OfType<MirPureCall>());

        var rightBlock = Assert.IsType<MirBlock>(main.FindBlock(branch.TrueTarget));
        Assert.Single(rightBlock.Instructions.OfType<MirPureCall>());
        Assert.Empty(
            Assert.IsType<MirBlock>(main.FindBlock(branch.FalseTarget))
                .Instructions
                .OfType<MirPureCall>());
    }

    [Fact]
    public void LogicalOrEvaluatesRightCallOnlyOnFalseEdge()
    {
        var (_, main) = CompileMain("""
            function value(): int {
                return 1;
            }

            operation Main() {
                if (true || value() == 1) {
                }
            }
            """);

        var entry = main.EntryBlock;
        var branch = Assert.IsType<MirBranch>(entry.Terminator);
        Assert.Empty(entry.Instructions.OfType<MirPureCall>());

        var rightBlock = Assert.IsType<MirBlock>(main.FindBlock(branch.FalseTarget));
        Assert.Single(rightBlock.Instructions.OfType<MirPureCall>());
        Assert.Empty(
            Assert.IsType<MirBlock>(main.FindBlock(branch.TrueTarget))
                .Instructions
                .OfType<MirPureCall>());
    }

    [Fact]
    public void BoundsGuardKeepsArrayLoadBehindTheSuccessfulLeftCondition()
    {
        var (program, main) = CompileMain("""
            operation Main() {
                var values: int[] = [1];
                var index: int = 0;
                if (0 <= index && index < values.Count) {
                    var value: int = values[index];
                }
            }
            """);

        var entry = main.EntryBlock;
        Assert.Empty(entry.Instructions.OfType<MirArrayLoad>());
        var loadBlock = Assert.Single(
            main.Blocks,
            block => block.Instructions.OfType<MirArrayLoad>().Any());

        var analyses = new MirAnalysisStore(program);
        var cfg = analyses.ControlFlow(main);
        Assert.False(cfg.Dominates(loadBlock.Id, main.EntryBlock.Id));
        Assert.Contains(
            main.Blocks
                .Where(block => block.Terminator is MirBranch),
            block => cfg.Dominates(block.Id, loadBlock.Id));
    }

    [Theory]
    [InlineData("&&", true)]
    [InlineData("||", false)]
    public void ConditionalMeasurementMergesTheMeasuredAndSkippedQubitVersions(
        string logicalOperator,
        bool rightOperandUsesTrueEdge)
    {
        var (program, main) = CompileMain($$"""
            operation Main() {
                use target = Qubit[1];
                var guard: bit = true;
                if (guard {{logicalOperator}} M(target[0])) {
                }
                X(target[0]);
            }
            """);

        Assert.Empty(QoraMirVerifier.Verify(program));
        var measurementSite = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions
                    .OfType<MirMeasure>()
                    .Select(measure => (Block: block, Measure: measure))));
        var shortCircuitSite = Assert.Single(
            main.Blocks
                .Where(block => block.Terminator is MirBranch)
                .Select(block => (
                    Block: block,
                    Branch: Assert.IsType<MirBranch>(block.Terminator))),
            site =>
                rightOperandUsesTrueEdge
                    ? site.Branch.TrueTarget == measurementSite.Block.Id
                    : site.Branch.FalseTarget == measurementSite.Block.Id);
        var mergeId = rightOperandUsesTrueEdge
            ? shortCircuitSite.Branch.FalseTarget
            : shortCircuitSite.Branch.TrueTarget;
        var merge = Assert.IsType<MirBlock>(main.FindBlock(mergeId));
        var phi = Assert.Single(merge.QubitPhis);

        Assert.Contains(
            phi.Inputs,
            input =>
                input.Edge.Source == shortCircuitSite.Block.Id
                && input.Edge.SuccessorOrdinal
                    == (rightOperandUsesTrueEdge ? 1 : 0)
                && input.Qubit == measurementSite.Measure.Qubit.Qubit);
        Assert.Contains(
            phi.Inputs,
            input =>
                input.Edge.Source == measurementSite.Block.Id
                && input.Edge.SuccessorOrdinal == 0
                && input.Qubit == measurementSite.Measure.QubitResult.Key);

        var followingX = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target is MirBuiltinGateTarget { Name: "X" });
        var xInput = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(followingX.Operands));
        Assert.Equal(phi.Key, xInput.Qubit.Qubit);
    }

    [Fact]
    public void WhileConditionMeasurementFeedsTheBodyBackedgeAndExit()
    {
        var (program, main) = CompileMain("""
            operation Main() {
                use target = Qubit[1];
                while (M(target[0])) {
                    X(target[0]);
                }
                Z(target[0]);
            }
            """);

        Assert.Empty(QoraMirVerifier.Verify(program));
        var header = Assert.Single(
            main.Blocks,
            block => block.QubitPhis.Count != 0);
        var headerPhi = Assert.Single(header.QubitPhis);
        var measurement = Assert.Single(
            header.Instructions.OfType<MirMeasure>());
        Assert.Equal(headerPhi.Key, measurement.Qubit.Qubit);

        var xSite = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions
                    .OfType<MirQuantumApply>()
                    .Where(apply =>
                        apply.Target is MirBuiltinGateTarget { Name: "X" })
                    .Select(apply => (Block: block, Apply: apply))));
        var xInput = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(xSite.Apply.Operands));
        Assert.Equal(measurement.QubitResult.Key, xInput.Qubit.Qubit);
        var bodyResult = Assert.Single(xSite.Apply.QubitResults);
        Assert.Contains(
            headerPhi.Inputs,
            input => input.Qubit == bodyResult.Key);

        var z = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target is MirBuiltinGateTarget { Name: "Z" });
        var zInput = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(z.Operands));
        Assert.Equal(measurement.QubitResult.Key, zInput.Qubit.Qubit);
    }

    private static (MirProgram Program, MirCallable Main) CompileMain(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
        var program = Assert.IsType<MirProgram>(result.Mir?.Program);
        return (
            program,
            Assert.Single(program.Callables, callable => callable.Name == "Main"));
    }
}
