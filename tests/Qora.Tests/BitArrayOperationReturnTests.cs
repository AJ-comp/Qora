using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class BitArrayOperationReturnTests
{
    private const string Varying = """
        operation Choose(wide: bool): bit[] {
            if (wide) { return [1, 0, 1]; }
            return [0, 1];
        }
        """;

    [Theory]
    [InlineData("operation Main(): bit[] { use q = Qubit[1]; var b: bit = M(q[0]); return Choose(b == 1); }")]
    [InlineData("""
        operation Consume(bits: bit[]): int { return bits.Count; }
        operation Main(): int {
            use q = Qubit[1];
            var b: bit = M(q[0]);
            var bits: bit[] = Choose(b == 1);
            return Consume(bits);
        }
        """)]
    [InlineData("""
        operation Relay(flag: bool): bit[] { return Choose(flag); }
        operation Main(): bit {
            use q = Qubit[1];
            var b: bit = M(q[0]);
            var bits: bit[] = Relay(b == 1);
            return bits[0];
        }
        """)]
    [InlineData("""
        operation Copy(bits: bit[]): bit[] { return bits; }
        operation Second(bits: bit[]): bit { return bits[1]; }
        operation Main(): bit {
            use q = Qubit[1]; var b: bit = M(q[0]);
            var copy: bit[] = Copy(Choose(b == 1));
            return Second(copy);
        }
        """)]
    public void RuntimeLengthSurvivesMirButIsRejectedAtTheOpenQasmBoundary(string main)
    {
        var source = Varying + "\n" + main;
        var mirOnly = QoraCompiler.Compile(source, new CompilationOptions(
            outputPlan: new CompilationOutputPlan(produceMir: true, Array.Empty<TargetBackend>())));

        Assert.True(mirOnly.Succeeded, Describe(mirOnly));
        var mir = Assert.IsType<MirSnapshot>(mirOnly.Mir);
        Assert.Contains(mir.Program.Callables, c => c.ReturnType is { ElementType: QType.Bit, IsArray: true });
        Assert.Empty(mirOnly.Targets.Artifacts);

        var compiled = QoraCompiler.Compile(source);
        Assert.False(compiled.Succeeded);
        Assert.NotNull(compiled.Mir);
        Assert.Null(compiled.Targets.OpenQasm);
        Assert.NotEmpty(compiled.Diagnostics);
        Assert.All(compiled.Diagnostics, d =>
        {
            Assert.Equal(CompilationStage.OpenQasm, d.Stage);
            Assert.Equal("QASM003", d.Error.Code);
            Assert.IsType<DiagnosticOrigin.Target>(d.Origin);
        });
        Assert.Contains(compiled.Diagnostics, d =>
            source[d.Error.Start..d.Error.End] == "Choose" &&
            d.Error.Message.Contains("Choose") && d.Error.Message.Contains('2') && d.Error.Message.Contains('3'));
    }

    [Theory]
    [InlineData(1, "[1]", "[0]")]
    [InlineData(2, "[1, 0]", "[0, 1]")]
    [InlineData(3, "[0, 1, 0]", "[1, 0, 1]")]
    public void OneCommonWidthIsPreservedAcrossBranchesCallsAndEntry(int width, string yes, string no)
    {
        var compiled = QoraCompiler.Compile($$"""
            operation Choose(flag: bool): bit[] {
                if (flag) { return {{yes}}; }
                var other: bit[] = {{no}};
                return other;
            }
            operation Main(): bit[] {
                use q = Qubit[1];
                var b: bit = M(q[0]);
                return Choose(b == 1);
            }
            """);
        Assert.True(compiled.Succeeded, Describe(compiled));
        var target = Assert.IsType<OpenQasmArtifact>(compiled.Targets.OpenQasm);
        var register = new MirQasmBitType(width, isRegister: true);
        Assert.Equal(register, target.Program.EntryOutput!.Type);
        Assert.Equal(register, Assert.Single(target.Program.Definitions).ReturnType);
        Assert.Contains($"-> bit[{width}]", target.Text);
        Assert.Contains($"output bit[{width}]", target.Text);
    }

    [Theory]
    [InlineData("bit[]", "var source: bit[] = [0, 0];", 2)]
    [InlineData("bit[]", "var source: bit[] = [0, 0, 0];", 3)]
    [InlineData("Qubit[]", "use source = Qubit[2];", 2)]
    [InlineData("Qubit[]", "use source = Qubit[3];", 3)]
    public void ParameterWidthSelectsTheReturnPassedToANestedConsumer(string type, string source, int width)
    {
        var compiled = QoraCompiler.Compile($$"""
            operation Make(x: {{type}}): bit[] {
                const last = x.Count - 1;
                const pair = last == 1;
                if (pair) { return [1, 0]; }
                return [1, 0, 1];
            }
            operation Count(bits: bit[]): int { return bits.Count; }
            operation Main(): int { {{source}} return Count(Make(source)); }
            """);
        Assert.True(compiled.Succeeded, Describe(compiled));
        var definitions = compiled.Targets.OpenQasm!.Program.Definitions;
        var result = Assert.Single(definitions, d => d.ReturnType is MirQasmBitType);
        var consumer = Assert.Single(definitions, d => d != result);
        var register = new MirQasmBitType(width, isRegister: true);
        Assert.Equal(register, result.ReturnType);
        Assert.Equal(register, Assert.Single(consumer.Parameters).Type);
    }

    [Theory]
    [InlineData("operation Main(): bit[] { return 1; }")]
    [InlineData("operation Main(): bit[] { return [0, 2]; }")]
    [InlineData("operation Main(): bit[] { var ints: int[] = [0, 1]; return ints; }")]
    [InlineData("operation Bits(): bit[] { return [1]; } operation Main(): bit { return Bits(); }")]
    public void BitArrayIsNotAScalarOrAnArrayOfAnotherElementType(string source) =>
        Compiler.Rejects(source, "QSEM037");

    private static string Describe(Compilation result) =>
        string.Join(" | ", result.Diagnostics.Select(d => $"{d.Stage}/{d.Error.Code}: {d.Error.Message}"));
}
