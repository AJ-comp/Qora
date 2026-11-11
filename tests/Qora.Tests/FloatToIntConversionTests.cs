using Qora.Compiler;
using Qora.Ir;

namespace Qora.Tests;

public sealed class FloatToIntConversionTests
{
    [Theory]
    [InlineData("var value: int = 0.5;")]
    [InlineData("const value: int = 0.5;")]
    [InlineData("var value: int = 1.0;")]
    [InlineData("var value: int = ((-0.5));")]
    [InlineData("var value: int = (1 + 0.5) * 2;")]
    [InlineData("var value: int = pi;")]
    [InlineData("var value: int = tau;")]
    [InlineData("var value: int = euler;")]
    [InlineData("var source: float = 0.5; var value: int = source;")]
    [InlineData("var source = 0.5; var value: int = source;")]
    [InlineData("const source = 0.5; const value: int = source;")]
    [InlineData("var source: float[] = [0.5]; var value: int = source[0];")]
    [InlineData("var value: int = 0; value = 0.5;")]
    [InlineData("var value = 0; value = 1.0;")]
    [InlineData("var source: float = 0.5; var value: int = 0; value = (source + 1) * 2;")]
    [InlineData("var source: float = 0.5; if (true) { var source: int = source; }")]
    public void ScalarStorageRejectsFloatValues(string body) =>
        AssertRejected($"operation Main() {{ {body} }}", "QSEM037");

    [Theory]
    [InlineData("var values: int[] = [0.5];")]
    [InlineData("var values: int[] = [0.5, 1.5];")]
    [InlineData("const values: int[] = [1.0];")]
    [InlineData("var values: int[] = [0, (1 + 0.5) * 2];")]
    [InlineData("var source = 0.5; var values: int[] = [source];")]
    [InlineData("var values: int[] = [0]; values[0] = 0.5;")]
    [InlineData("var source = 0.5; var values: int[] = [0]; values[(0)] = (source);")]
    [InlineData("var source: float[] = [0.5]; var values: int[] = [0]; values[0] = source[0];")]
    public void ArrayElementsUseTheSameFloatToIntRule(string body) =>
        AssertRejected($"operation Main() {{ {body} }}", "QSEM037");

    [Theory]
    [InlineData("function", "0.5")]
    [InlineData("function", "1.0")]
    [InlineData("function", "(1 + 0.5) * 2")]
    [InlineData("operation", "0.5")]
    [InlineData("operation", "1.0")]
    [InlineData("operation", "(1 + 0.5) * 2")]
    public void ScalarArgumentsRejectFloatInStatementAndValueCalls(
        string callableKind,
        string argument)
    {
        var callee = $"{callableKind} Take(value: int): int {{ return value; }}";
        AssertRejected($"{callee} operation Main() {{ Take({argument}); }}", "QSEM006");
        AssertRejected($"{callee} operation Main(): int {{ return Take({argument}); }}", "QSEM006");
    }

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void ArgumentsRejectFloatVariablesAndIndexedValues(string callableKind)
    {
        var callee = $"{callableKind} Take(value: int): int {{ return value; }}";
        AssertRejected(
            $"{callee} operation Main() {{ var source = 0.5; Take(source); }}",
            "QSEM006");
        AssertRejected(
            $"{callee} operation Main() {{ var source: float[] = [0.5]; Take(source[0]); }}",
            "QSEM006");
    }

    [Theory]
    [InlineData("function", "return 0.5;")]
    [InlineData("function", "return 1.0;")]
    [InlineData("function", "return (1 + 0.5) * 2;")]
    [InlineData("function", "var value = 0.5; return value;")]
    [InlineData("function", "var values: float[] = [0.5]; return values[0];")]
    [InlineData("operation", "return 0.5;")]
    [InlineData("operation", "return 1.0;")]
    [InlineData("operation", "return (1 + 0.5) * 2;")]
    [InlineData("operation", "var value = 0.5; return value;")]
    [InlineData("operation", "var values: float[] = [0.5]; return values[0];")]
    public void BothCallableKindsRejectFloatReturns(string callableKind, string body) =>
        AssertRejected(
            $"{callableKind} Read(): int {{ {body} }} operation Main() {{ Read(); }}",
            "QSEM037");

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void ForwardDeclaredFloatResultCannotEnterIntStorage(string callableKind)
    {
        AssertRejected($$"""
            operation Work() { var value: int = Read(); }
            {{callableKind}} Read(): float { return 0.5; }
            operation Main() { Work(); }
            """, "QSEM037");
        AssertRejected($$"""
            operation Work() { var values: int[] = [Read()]; }
            {{callableKind}} Read(): float { return 0.5; }
            operation Main() { Work(); }
            """, "QSEM037");
    }

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void ForwardDeclaredIntParameterRejectsFloat(string callableKind) =>
        AssertRejected($$"""
            operation Work() { Take((1 + 0.5)); }
            {{callableKind}} Take(value: int): int { return value; }
            operation Main() { Work(); }
            """, "QSEM006");

    [Theory]
    [InlineData("var value: int = Read(0.5);")]
    [InlineData("var values: int[] = [Read(0.5)];")]
    [InlineData("var values: int[] = [Read()];")]
    [InlineData("var values: int[] = [0.5, Read(0.5)];")]
    public void InvalidCallOwnsItsDiagnosticWithoutASecondConversionError(string body) =>
        AssertRejected($$"""
            function Read(value: int): float { return 0.5; }
            operation Main() { {{body}} }
            """, "QSEM006");

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void FloatParameterCannotInitializeIntStorage(string callableKind) =>
        AssertRejected($$"""
            {{callableKind}} Read(value: float): int { var local: int = value; return local; }
            operation Main() { Read(0.5); }
            """, "QSEM037");

    [Theory]
    [InlineData("0.5")]
    [InlineData("(source[0] + 1) * 2")]
    public void MutableArrayParameterCannotReceiveFloatElements(string value) =>
        AssertRejected($$"""
            operation Write(source: float[], var destination: int[]) {
                destination[0] = {{value}};
            }
            operation Main() {
                var source: float[] = [0.5];
                var destination: int[] = [0];
                Write(source, var destination);
            }
            """, "QSEM037");

    [Fact]
    public void FunctionLocalArrayWritesUseTheSameConversionRule() =>
        AssertRejected("""
            function Read(value: float): int {
                var local: int[] = [0];
                local[0] = value;
                return local[0];
            }
            operation Main() { Read(0.5); }
            """, "QSEM037");

    [Theory]
    [InlineData("var value: float = 0.5; value = 1;")]
    [InlineData("const value: float = 1;")]
    [InlineData("var value: int = (1 + 2) * 3; value = (value + 1) * 2;")]
    [InlineData("var values: float[] = [0.5, 1]; values[0] = 1;")]
    [InlineData("var values: int[] = [1, (2 + 3)]; values[0] = (1 + 2) * 3;")]
    [InlineData("var value: bit = 2; var angleValue: angle = 1; var bits: bit[] = [2]; var angles: angle[] = [1];")]
    [InlineData("var value: int = 1; if (value != 0) { value = 2; }")]
    public void ExistingNonNarrowingAndUnrelatedAssignmentsRemainAccepted(string body) =>
        Compiler.Accepts($"operation Main() {{ {body} }}");

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void IntToFloatArgumentsAndReturnsRemainAccepted(string callableKind) =>
        Compiler.Accepts($$"""
            {{callableKind}} Widen(value: float): float { return value; }
            {{callableKind}} Read(): float { return 1; }
            operation Main(): float {
                var value: float = Widen(1);
                value = Read();
                return value;
            }
            """);

    [Theory]
    [InlineData("var")]
    [InlineData("const")]
    public void UnannotatedDecimalStillInfersFloat(string declarationKind)
    {
        var compilation = Compiler.Compile($$"""
            operation Main(): float {
                {{declarationKind}} value = 0.5;
                return value;
            }
            """);
        Assert.True(compilation.Succeeded, Describe(compilation));
        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var declaration = Assert.Single(
            analyzed.Program.EntryCallable!.Body.OfType<HirVariableDeclarationStatement>());
        var symbol = analyzed.Model.FindSymbol(declaration.Id);

        Assert.NotNull(symbol);
        Assert.Equal(QType.Float, symbol.Type);
        Assert.NotNull(compilation.Mir);
        Assert.NotEmpty(compilation.Targets.Artifacts);
    }

    private static void AssertRejected(string source, string expectedCode)
    {
        var compilation = Compiler.Compile(source);
        Assert.False(compilation.Succeeded, $"Expected {expectedCode}, but compiled:\n{source}");
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Error.Code);
        Assert.Equal(CompilationStage.HirValidation, diagnostic.Stage);
        var span = Assert.IsType<SourceSpan>(diagnostic.Error.Span);
        Assert.InRange(span.Start, 0, source.Length - 1);
        Assert.InRange(span.End, span.Start + 1, source.Length);
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }

    private static string Describe(Compilation compilation) =>
        string.Join(" | ", compilation.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
}
