using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class HirTypeBoundaryTests
{
    [Theory]
    [InlineData("operation Main(){ if (0.5) {} }", "QSEM041")]
    [InlineData("operation Main(){ for i in 0..0.5 {} }", "QSEM041")]
    [InlineData("operation Main(){ if (0.5 && true) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; if (xs) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; if (!xs) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; if (xs && true) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; for i in 0..xs {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [xs + 1]; }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [-xs]; }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: float[] = [1.0]; if (xs == ys) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [2]; if (xs < ys) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; if (xs == 1) {} }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [2]; if (xs + 1 == ys) {} }", "QSEM041")]
    [InlineData("function one(): int { return 1; } operation Main(){ use q = Qubit[1]; H(one()); }", "QSEM006")]
    [InlineData("function one(): int { return 1; } operation Take(q: Qubit) {} operation Main(){ Take(one()); }", "QSEM006")]
    [InlineData("operation Main(){ use q = Qubit[1]; var xs: int[] = [1]; Rx(xs + 1, q[0]); }", "QSEM006")]
    [InlineData("operation Main(){ use q = Qubit[1]; q[0] = 3; }", "QSEM026")]
    [InlineData("operation Work(a: Qubit[], b: Qubit[]){ if (a == b) {} } operation Main(){}", "QSEM026")]
    [InlineData("operation Main(){ use r = Qubit[1]; var r: bit = M(r[0]); }", "QSEM015")]
    public void HirRejectsInvalidTypeContextsBeforeMir(
        string source,
        string expectedCode)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));

        Assert.False(compilation.Succeeded);
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code == expectedCode);
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code is "QINTERNAL" or "QORA0000");
        Assert.Null(compilation.Mir);
    }

    [Theory]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [2]; if (xs == ys) {} }")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [2, 3]; if (xs != ys) {} }")]
    [InlineData("operation Work(xs: int[]){ var ys: int[] = [1]; if (xs == ys) {} } operation Main(){ var xs: int[] = [1]; Work(xs); }")]
    [InlineData("operation Work(xs: int[], ys: int[]){ if (xs == ys) {} } operation Main(){ var xs: int[] = [1]; var ys: int[] = [2, 3]; Work(xs, ys); }")]
    [InlineData("operation Main(){ var xs: bit[] = [0]; var ys: bit[] = [0, 1]; if (xs != ys) {} }")]
    public void HirAcceptsStructuralEqualityForSameElementTypeArrays(string source)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));

        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
    }

    [Fact]
    public void MirPreservesStructuralEqualityAcrossDifferentKnownLengths()
    {
        var mirOnly = new CompilationOutputPlan(
            produceMir: true,
            Array.Empty<TargetBackend>());
        var compilation = QoraCompiler.Compile(
            "operation Main(){ var xs: int[] = [1]; var ys: int[] = [2, 3]; if (xs == ys) {} }",
            new CompilationOptions(outputPlan: mirOnly));

        Assert.True(compilation.Succeeded);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var comparison = Assert.Single(
            mir.Program.Callables
                .SelectMany(callable => callable.Blocks)
                .SelectMany(block => block.Instructions)
                .OfType<MirBinary>());
        Assert.Equal(MirBinaryOperator.Equal, comparison.Operator);

        var callable = Assert.Single(mir.Program.Callables);
        Assert.Equal(MirType.Array(QType.Int, 1), callable.RequireValue(comparison.Left).Type);
        Assert.Equal(MirType.Array(QType.Int, 2), callable.RequireValue(comparison.Right).Type);
        Assert.Equal(MirType.Scalar(QType.Bit), callable.RequireValue(comparison.Result.Id).Type);
    }

    [Fact]
    public void ExistingLooseAssignmentsAndIntegralConditionsRemainAccepted() =>
        Compiler.Accepts("""
            operation Main() {
                const c: int = 0.5;
                var i: int = 0.5;
                i = 0.5;
                var b: bit = 2;
                var a: angle = 1;
                var ints: int[] = [0.5];
                var bits: bit[] = [2];
                var angles: angle[] = [1];
                ints[0] = 0.5;
                if (i) {
                    i = 1;
                }
            }
            """);

    [Theory]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [0]; var z: int = xs[ys + 1]; }", "QSEM016")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [0]; var z: int = xs[ys + 1 + 2]; }", "QSEM041")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var flags: bit[] = [0]; var z: int = xs[flags]; }", "QSEM036")]
    [InlineData("operation Main(){ use q = Qubit[1]; var xs: int[] = [1]; var z: int = xs[q[0]]; }", "QSEM026")]
    [InlineData("operation Main(){ use q = Qubit[1]; var xs: int[] = [1]; var z: int = xs[M(q[0])]; }", "QSEM005")]
    public void InvalidArrayIndexExpressionHasOneOwningDiagnostic(
        string source,
        string expectedCode)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));

        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Error.Code);
    }

    [Theory]
    [InlineData("operation Take(value: int) {} operation Main(){ var xs: int[] = [1]; Take(xs + 1 + 2); }")]
    [InlineData("operation Main(){ var xs: int[] = [1]; var ys: int[] = [xs + 1 + 2]; }")]
    [InlineData("function Bad(xs: int[]): int { return xs + 1 + 2; } operation Main(){ var xs: int[] = [1]; var value: int = Bad(xs); }")]
    public void NestedInvalidExpressionHasOneOwningDiagnostic(string source)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));

        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("QSEM041", diagnostic.Error.Code);
    }

    [Theory]
    [InlineData("operation Main(){ var x: int = 1; if (true) { var x: float = x + 1; } }")]
    [InlineData("operation Main(){ var flags: bit[] = new bit[2]; if (true) { var flags: int = AsInt(flags); } }")]
    [InlineData("function ReadFlags(flags: bit[]): int { return AsInt(flags); } operation Main(){ var flags: bit[] = new bit[2]; if (true) { var flags: int = ReadFlags(flags); } }")]
    [InlineData("operation Main(){ var xs: int[] = [10, 20]; if (true) { var xs: int[] = [xs[xs.Count - 1], 0, 0]; } }")]
    public void InitializerTypeUsesTheExactPreviouslyResolvedSymbol(string source)
    {
        var compilation = Compiler.Compile(source);

        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
    }

    [Fact]
    public void MirContainsOnlyTheConversionsApprovedForExactHirExpressions()
    {
        var compilation = Compiler.Compile("""
            operation Main() {
                use q = Qubit[1];
                var i: int = true;
                var f: float = i;
                var a: angle = f;
                var b: bit = 1;
                if (i) {}
                Rx(1, q[0]);
            }
            """);
        Assert.True(compilation.Succeeded);

        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var main = mir.Program.EntryPoint;
        var conversionInstructions = main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirConvert>()
            .ToList();
        var conversions = conversionInstructions
            .Select(conversion => (
                Source: main.RequireValue(conversion.Operand).Type,
                Target: main.RequireValue(conversion.Result.Id).Type))
            .ToList();

        Assert.Contains((MirType.Scalar(QType.Bit), MirType.Scalar(QType.Int)), conversions);
        Assert.Contains((MirType.Scalar(QType.Int), MirType.Scalar(QType.Float)), conversions);
        Assert.Contains((MirType.Scalar(QType.Float), MirType.Scalar(QType.Angle)), conversions);
        Assert.Contains((MirType.Scalar(QType.Int), MirType.Scalar(QType.Bit)), conversions);
        Assert.Contains((MirType.Scalar(QType.Int), MirType.Scalar(QType.Angle)), conversions);

        var hirArtifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        foreach (var conversion in conversionInstructions)
        {
            var origin = Assert.IsType<MirHirOrigin>(conversion.Origin);
            var approvedTarget = hirArtifact.Model.FindImplicitConversionTarget(origin.HirNodeId);
            Assert.Equal(main.RequireValue(conversion.Result.Id).Type.ElementType, approvedTarget);
        }
    }
}
