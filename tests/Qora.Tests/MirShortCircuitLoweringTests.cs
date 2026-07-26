using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MirShortCircuitLoweringTests
{
    [Fact]
    public void LogicalAndEvaluatesRightCallOnlyOnTrueEdge()
    {
        var main = CompileMain("""
            function value(): int {
                return 1;
            }

            operation Main() {
                if (false && value() == 1) {
                }
            }
            """);

        var entry = Assert.IsType<MirBlock>(main.FindBlock(main.EntryBlock));
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
        var main = CompileMain("""
            function value(): int {
                return 1;
            }

            operation Main() {
                if (true || value() == 1) {
                }
            }
            """);

        var entry = Assert.IsType<MirBlock>(main.FindBlock(main.EntryBlock));
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
        var main = CompileMain("""
            operation Main() {
                var values: int[] = [1];
                var index: int = 0;
                if (0 <= index && index < values.Count) {
                    var value: int = values[index];
                }
            }
            """);

        var entry = Assert.IsType<MirBlock>(main.FindBlock(main.EntryBlock));
        Assert.Empty(entry.Instructions.OfType<MirArrayLoad>());
        var loadBlock = Assert.Single(
            main.Blocks,
            block => block.Instructions.OfType<MirArrayLoad>().Any());

        var cfg = Qora.Ir.Mir.Analysis.MirControlFlowAnalysis.Analyze(
            new MirProgram(0, new[] { main }),
            main.Id);
        Assert.False(cfg.Dominates(loadBlock.Id, main.EntryBlock));
        Assert.Contains(
            main.Blocks
                .Where(block => block.Terminator is MirBranch),
            block => cfg.Dominates(block.Id, loadBlock.Id));
    }

    private static MirCallable CompileMain(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
        var program = Assert.IsType<MirProgram>(result.Mir);
        return Assert.Single(program.Callables, callable => callable.Name == "Main");
    }
}
