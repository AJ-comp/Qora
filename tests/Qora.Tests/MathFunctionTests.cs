using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// The math built-ins <c>Sqrt</c> and <c>ArcCos</c>: one <c>float</c> in (an <c>int</c> converts), one
/// <c>float</c> out, emitted as OpenQASM's own <c>sqrt</c> and <c>arccos</c>. Their purpose is to write a
/// rotation angle from an amplitude, e.g. <c>Ry(2.0 * ArcCos(0.6), q)</c> for <c>0.6|0⟩ + 0.8|1⟩</c>.
/// </summary>
public sealed class MathFunctionTests
{
    [Fact]
    public void AnAngleWrittenFromAnAmplitudeEmitsTheOpenQasmBuiltins()
    {
        var qasm = Qasm("""
            operation Main(): bit
            {
                use q = Qubit[1];
                Ry(2.0 * ArcCos(1.0 / Sqrt(2.0)), q[0]);
                var result: bit = M(q[0]);
                return result;
            }
            """);

        Assert.Contains("arccos(", qasm);
        Assert.Contains("sqrt(", qasm);
        Assert.Contains("ry(", qasm);
        Assert.DoesNotContain("ArcCos", qasm);
        Assert.DoesNotContain("Sqrt", qasm);
    }

    [Fact]
    public void AnIntegerArgumentConvertsToFloat()
    {
        var qasm = Qasm("""
            function Turn(): float { var a: float = ArcCos(1); return Sqrt(4) + a; }
            operation Main(): bit { use q = Qubit[1]; Ry(Turn(), q[0]); var r: bit = M(q[0]); return r; }
            """);

        Assert.Matches(@"= float\(\w+\);\s*\w+ = arccos\(", qasm);
        Assert.Matches(@"= float\(\w+\);\s*\w+ = sqrt\(", qasm);
    }

    [Fact]
    public void TheResultIsAFloatUsableWhereverAFloatIs() =>
        Compiler.Accepts("""
            function Half(): float { return Sqrt(0.25); }
            operation Main(): bit
            {
                use q = Qubit[1];
                var turn: angle = 2.0 * ArcCos(Half());
                Rx(turn, q[0]);
                var r: bit = M(q[0]);
                return r;
            }
            """);

    [Fact]
    public void AFloatResultDoesNotBecomeAnInt() =>
        Compiler.RejectsExactly("operation Main() { var i: int = Sqrt(4.0); }", "QSEM037");

    [Theory]
    [InlineData("var a: float = ArcCos(true);")]
    [InlineData("var x: angle = pi; var a: float = ArcCos(x);")]
    [InlineData("var v: float[] = [1.0]; var a: float = Sqrt(v);")]
    [InlineData("var a: float = Sqrt(1.0, 2.0);")]
    [InlineData("var a: float = Sqrt();")]
    public void AWrongArgumentIsOneQsem006(string body) =>
        Compiler.RejectsExactly($"operation Main() {{ {body} }}", "QSEM006");

    [Fact]
    public void AQubitArgumentKeepsItsOwnDiagnosticOnly() =>
        Compiler.RejectsExactly("operation Main() { use q = Qubit[1]; var a: float = Sqrt(q[0]); }", "QSEM026");

    [Fact]
    public void AMeasurementArgumentKeepsItsOwnDiagnosticOnly() =>
        Compiler.RejectsExactly("operation Main() { use q = Qubit[1]; var a: float = Sqrt(M(q[0])); }", "QSEM005");

    [Theory]
    [InlineData("Sqrt")]
    [InlineData("ArcCos")]
    public void AnOperationCannotShadowAMathBuiltin(string name) =>
        Compiler.Rejects($"operation {name}() {{ }} operation Main() {{ }}", "QSEM013");

    [Fact]
    public void TheOpenQasmNamesAreReservedForGeneratedCode()
    {
        Assert.Contains("sqrt", QoraGates.QasmReserved);
        Assert.Contains("arccos", QoraGates.QasmReserved);
        Assert.Equal("sqrt", QoraGates.Functions["Sqrt"].QasmName);
        Assert.Equal("arccos", QoraGates.Functions["ArcCos"].QasmName);
    }

    private static string Qasm(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return Assert.IsType<OpenQasmArtifact>(result.Targets.OpenQasm).Text;
    }
}
