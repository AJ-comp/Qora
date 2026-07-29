using Qora.Compiler;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirCallGraphTests
{
    [Fact]
    public async Task AnalysisStoreCachesExactPureAndQuantumCallEdges()
    {
        var compilation = QoraCompiler.Compile(
            """
            function Increment(value: int): int {
                return value + 1;
            }

            operation ApplyX(q: Qubit) {
                X(q);
            }

            operation Main() {
                var value: int = Increment(1);
                use q = Qubit[1];
                ApplyX(q[0]);
            }
            """);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));

        var snapshot = Assert.IsType<MirSnapshot>(compilation.Mir);
        var increment = snapshot.Program.Callables.Single(
            callable => callable.Name == "Increment");
        var applyX = snapshot.Program.Callables.Single(
            callable => callable.Name == "ApplyX");
        var main = snapshot.Program.Callables.Single(
            callable => callable.Name == "Main");
        var graph = snapshot.Analyses.CallGraph;

        Assert.Same(graph, snapshot.Analyses.CallGraph);
        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => snapshot.Analyses.CallGraph)));
        Assert.All(concurrent, candidate => Assert.Same(graph, candidate));

        var pure = Assert.Single(
            graph.Calls,
            call => call.Kind == MirCallKind.PureCall);
        Assert.Equal(main.Id, pure.Caller);
        Assert.Equal(increment.Id, pure.Callee);
        var pureInstruction = Assert.IsType<MirPureCall>(
            main.RequireInstruction(pure.Instruction.Instruction));
        Assert.Equal(
            increment.Id,
            Assert.IsType<MirUserCallableTarget>(pureInstruction.Target).Callable);
        AssertCallLocation(main, pure, pureInstruction);

        var quantum = Assert.Single(
            graph.Calls,
            call => call.Kind == MirCallKind.QuantumApply);
        Assert.Equal(main.Id, quantum.Caller);
        Assert.Equal(applyX.Id, quantum.Callee);
        var quantumInstruction = Assert.IsType<MirQuantumApply>(
            main.RequireInstruction(quantum.Instruction.Instruction));
        Assert.Equal(
            applyX.Id,
            Assert.IsType<MirUserCallableTarget>(quantumInstruction.Target).Callable);
        AssertCallLocation(main, quantum, quantumInstruction);

        Assert.Equal(
            new[] { increment.Id, applyX.Id }
                .OrderBy(callable => callable.Value),
            graph.CalleesOf(main));
        Assert.Equal(
            new[] { increment.Id },
            graph.CalleesOf(main.Id, MirCallKind.PureCall));
        Assert.Equal(
            new[] { applyX.Id },
            graph.CalleesOf(main.Id, MirCallKind.QuantumApply));
        Assert.Empty(graph.CallsFrom(increment));
        Assert.Empty(graph.CallsFrom(applyX));
        Assert.Single(graph.CallsTo(increment));
        Assert.Single(graph.CallsTo(applyX));
    }

    [Fact]
    public void CallGraphRejectsACallableObjectFromAnotherSnapshot()
    {
        const string source = """
            operation Helper(q: Qubit) {
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                Helper(q[0]);
            }
            """;
        var firstCompilation = QoraCompiler.Compile(source);
        var secondCompilation = QoraCompiler.Recompile(firstCompilation, source);
        var first = Assert.IsType<MirSnapshot>(firstCompilation.Mir);
        var second = Assert.IsType<MirSnapshot>(secondCompilation.Mir);
        var firstMain = first.Program.Callables.Single(
            callable => callable.Name == "Main");
        var secondMain = second.Program.Callables.Single(
            callable => callable.Name == "Main");
        var graph = second.Analyses.CallGraph;

        Assert.Equal(firstMain.Id, secondMain.Id);
        Assert.False(second.Program.ContainsCallable(firstMain));
        Assert.Throws<ArgumentException>(() => graph.CallsFrom(firstMain));
        Assert.Throws<ArgumentException>(() => graph.CallsTo(firstMain));
        Assert.Throws<ArgumentException>(() => graph.CalleesOf(firstMain));
        Assert.Throws<InvalidOperationException>(
            () => graph.EnsureFor(first.Program));

        Assert.Single(graph.CallsFrom(secondMain));
        Assert.Single(graph.CalleesOf(secondMain));
    }

    private static void AssertCallLocation(
        MirCallable caller,
        MirCallSite call,
        MirInstruction instruction)
    {
        Assert.Equal(caller.Id, call.Instruction.Callable);
        var location =
            caller.RequireInstructionLocation(call.Instruction.Instruction);
        Assert.Equal(call.Block, location.Block.Id);
        Assert.Same(instruction, location.Block.Instructions[location.Index]);
    }
}
