using Qora.Compiler;

namespace Qora.Tests;

public sealed class MirOpenQasmLoweringTests
{
    [Fact]
    public void PreservesLengthOneRegisterShape()
    {
        var qasm = Compile("""
            operation Main() {
                use q = Qubit[1];
                var bits: bit[] = [1];
                X(q[0]);
            }
            """);

        Assert.Contains("qubit[1] q;", qasm);
        Assert.Contains("bit[1] bits;", qasm);
        Assert.Contains("x q[", qasm);
    }

    [Fact]
    public void RecoversRepeatAsStructuredLoopWithoutProgramCounter()
    {
        var qasm = Compile("""
            operation Main() {
                var x: int = 0;
                repeat {
                    x = x + 1;
                } until (x == 2);
            }
            """);

        Assert.Contains("while (true) {", qasm);
        Assert.Contains("break;", qasm);
        Assert.DoesNotContain("__pc", qasm);
    }

    [Fact]
    public void NestedRepeatsKeepIndependentNormalExits()
    {
        var compilation = CompileCompilation("""
            operation Main() {
                var outer: int = 0;
                var inner: int = 0;
                repeat {
                    inner = 0;
                    repeat {
                        inner = inner + 1;
                    } until (inner == 2);
                    outer = outer + 1;
                } until (outer == 2);
            }
            """);
        var qasm = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm).Text;
        Assert.Equal(2, Count(qasm, "while (true) {"));
        Assert.DoesNotContain("__pc", qasm);
    }

    [Fact]
    public void IfReturnInsideRepeatIsATypedCallableReturnSideExit()
    {
        var compilation = CompileCompilation("""
            function find(n: int): int {
                var x: int = 0;
                repeat {
                    x = x + 1;
                    if (x == n) {
                        return x;
                    }
                } until (x > 3);
                return 9;
            }

            operation Main() {
                var result: int = find(2);
            }
            """);
        var qasm = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm).Text;
        Assert.Contains("int return_done = 0;", qasm);
        Assert.Contains("return_done = 1;", qasm);
        Assert.Equal(1, Count(qasm, "return ret;"));
    }

    [Fact]
    public void ReturnFromRepeatNestedInsideForLeavesBothLoops()
    {
        var compilation = CompileCompilation("""
            function find(n: int): int {
                var x: int = 0;
                for outer in 0..2 {
                    x = 0;
                    repeat {
                        x = x + 1;
                        if (x == n) {
                            return outer + x;
                        }
                    } until (x > 3);
                }
                return 9;
            }

            operation Main() {
                var result: int = find(2);
            }
            """);
        var qasm = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm).Text;
        Assert.Equal(2, Count(qasm, "while (true) {"));
        Assert.Contains("return_done = 1;", qasm);
        Assert.Equal(1, Count(qasm, "return ret;"));
    }

    [Fact]
    public void PropagatesFunctionReturnAcrossNestedLoops()
    {
        var qasm = Compile("""
            function find(n: int): int {
                for i in 0..2 {
                    for j in 0..2 {
                        if (i + j == n) {
                            return i;
                        }
                    }
                }
                return 9;
            }

            operation Main() {
                var result: int = find(2);
            }
            """);

        Assert.Contains("int return_done = 0;", qasm);
        Assert.Contains("return_done = 1;", qasm);
        Assert.Contains("return ret;", qasm);
        Assert.Equal(1, Count(qasm, "return ret;"));
    }

    [Fact]
    public void ThreadsDefLocalGeneralArrayByTypedHiddenParameters()
    {
        var qasm = Compile("""
            operation Leaf() {
                var values: int[] = [1, 2];
            }

            operation Middle() {
                Leaf();
            }

            operation Main() {
                Middle();
            }
            """);

        Assert.Contains("def Leaf(mutable array[int, #dim = 1]", qasm);
        Assert.Contains("def Middle(mutable array[int, #dim = 1]", qasm);
        Assert.Contains("array[int, 2]", qasm);
        Assert.Contains("Leaf(", qasm);
        Assert.Contains("Middle(", qasm);
    }

    private static string Compile(string source)
    {
        var result = CompileCompilation(source);
        return Assert.IsType<OpenQasmArtifact>(
            result.Targets.OpenQasm).Text;
    }

    private static Compilation CompileCompilation(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(
                " | ",
                result.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return result;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var offset = 0;
             (offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
             offset += value.Length)
        {
            count++;
        }
        return count;
    }
}
