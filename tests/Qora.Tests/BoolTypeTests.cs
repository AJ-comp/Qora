using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class BoolTypeTests
{
    [Theory]
    [InlineData("var value: bool = true;")]
    [InlineData("const value: bool = false;")]
    [InlineData("var value: bool = 1 < 2; value = 2 == 2;")]
    [InlineData("var value = true; value = false;")]
    [InlineData("var values: bool[] = [true, false, 1 == 1]; values[0] = 3 < 4;")]
    [InlineData("const values: bool[] = [true, false];")]
    [InlineData("var values: bool[] = new bool[2]; values[0] = true;")]
    [InlineData("var left: bool[] = [true]; var right: bool[] = [false]; var same: bool = left == right;")]
    [InlineData("var value: bool = true == false;")]
    [InlineData("var value: bool = (1 < 2) != (3 > 4);")]
    public void BoolStorageUsesLogicalValues(string body) =>
        Compiler.Accepts($"operation Main() {{ {body} }}");

    [Theory]
    [InlineData("var value: bool = 0;")]
    [InlineData("var value: bool = 1;")]
    [InlineData("const value: bool = 1;")]
    [InlineData("var value: bit = true;")]
    [InlineData("var value: bit = 1 == 1;")]
    [InlineData("const value: bit = false;")]
    [InlineData("var value: int = true;")]
    [InlineData("var value: int = 1 == 1;")]
    [InlineData("var value: float = true;")]
    [InlineData("var value: angle = false;")]
    [InlineData("var value: bool = 0.5;")]
    [InlineData("var number: angle = 1; var value: bool = number;")]
    [InlineData("var source: bit = 1; var value: bool = source;")]
    [InlineData("var source: int = 1; var value: bool = source;")]
    [InlineData("var source = true; var value: bit = source;")]
    [InlineData("var source = false; var value: int = source;")]
    [InlineData("var value: bool = true; value = 1;")]
    [InlineData("var source: bit = 1; var value: bool = true; value = source;")]
    [InlineData("var value: bit = 0; value = true;")]
    [InlineData("var value: int = 0; value = false;")]
    [InlineData("var source: bit[] = [1]; var value: bool = source[0];")]
    [InlineData("var source: bool[] = [true]; var value: bit = source[0];")]
    [InlineData("var source: bool[] = [true]; var value: int = source[0];")]
    public void ScalarStorageRejectsImplicitBoolConversions(string body) =>
        AssertRejected($"operation Main() {{ {body} }}", "QSEM037");

    [Theory]
    [InlineData("var values: bool[] = [0];")]
    [InlineData("var values: bool[] = [1];")]
    [InlineData("var values: bool[] = [true, 1];")]
    [InlineData("var values: int[] = [0, false];")]
    [InlineData("var values: bit[] = [true];")]
    [InlineData("var values: int[] = [1 < 2];")]
    [InlineData("var values: float[] = [false];")]
    [InlineData("var values: angle[] = [true];")]
    [InlineData("var source: bit = 1; var values: bool[] = [source];")]
    [InlineData("var values: bool[] = [true]; values[0] = 1;")]
    [InlineData("var values: bit[] = [0]; values[0] = false;")]
    [InlineData("var values: int[] = [0]; values[0] = true;")]
    public void ArrayElementsRejectImplicitBoolConversions(string body) =>
        AssertRejected($"operation Main() {{ {body} }}", "QSEM037");

    [Theory]
    [InlineData("bool", "true", "bit", "1")]
    [InlineData("bool", "false", "int", "0")]
    [InlineData("bit", "1", "bool", "true")]
    [InlineData("int", "0", "bool", "false")]
    public void WholeArrayAssignmentRemainsUnsupportedForBoolAndNumericArrays(
        string targetType, string initial, string sourceType, string sourceValue) =>
        AssertRejected($$"""
            operation Main() {
                var target: {{targetType}}[] = [{{initial}}];
                var source: {{sourceType}}[] = [{{sourceValue}}];
                target = source;
            }
            """, "QSEM029");

    [Theory]
    [InlineData("bool", "bit", "1")]
    [InlineData("bit", "bool", "true")]
    [InlineData("int", "bool", "true")]
    public void ReadOnlyArrayArgumentsCannotCrossBoolBoundary(
        string parameterType, string sourceType, string sourceValue) =>
        AssertRejected($$"""
            operation Take(values: {{parameterType}}[]) {}
            operation Main() {
                var values: {{sourceType}}[] = [{{sourceValue}}];
                Take(values);
            }
            """, "QSEM006");

    [Theory]
    [InlineData("use q = Qubit[1]; Rx(true, q[0]);", "QSEM006")]
    [InlineData("use q = Qubit[1]; Rx(1 == 1, q[0]);", "QSEM006")]
    [InlineData("var values: int[] = [3]; var result = values[true];", "QSEM016")]
    [InlineData("for i in 0..true {}", "QSEM041")]
    [InlineData("for i in false..true {}", "QSEM041")]
    [InlineData("var flags: bool[] = [true]; var encoded: int = AsInt(flags);", "QSEM006")]
    public void NumericOnlyContextsRejectBool(string body, string diagnostic) =>
        AssertRejected($"operation Main() {{ {body} }}", diagnostic);

    [Theory]
    [InlineData("function", "bool", "1")]
    [InlineData("operation", "bool", "1")]
    [InlineData("function", "bit", "true")]
    [InlineData("operation", "bit", "true")]
    [InlineData("function", "int", "1 == 1")]
    [InlineData("operation", "int", "1 == 1")]
    [InlineData("function", "float", "false")]
    [InlineData("operation", "angle", "false")]
    public void CallableArgumentsRejectImplicitBoolConversions(string kind, string type, string argument) =>
        AssertRejected($$"""
            {{kind}} Take(value: {{type}}){{(kind == "function" ? ": int { return 0; }" : " {}")}}
            operation Main() { Take({{argument}}); }
            """, "QSEM006");

    [Theory]
    [InlineData("function", "bool", "1")]
    [InlineData("operation", "bool", "1")]
    [InlineData("function", "bit", "true")]
    [InlineData("operation", "bit", "true")]
    [InlineData("function", "int", "1 == 1")]
    [InlineData("operation", "int", "1 == 1")]
    [InlineData("function", "float", "false")]
    [InlineData("operation", "angle", "false")]
    public void CallableReturnsRejectImplicitBoolConversions(string kind, string type, string expression) =>
        AssertRejected($$"""
            {{kind}} Read(): {{type}} { return {{expression}}; }
            operation Main() { Read(); }
            """, "QSEM037");

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void BoolArgumentsAndReturnsRemainBool(string kind) =>
        Compiler.Accepts($$"""
            {{kind}} Same(left: bool, right: bool): bool { return left == right; }
            operation Main(): bool {
                var value: bool = Same(1 < 2, true);
                return Same(value, false);
            }
            """);

    [Fact]
    public void MutableBoolArrayParametersKeepTheirElementType() =>
        Compiler.Accepts("""
            operation Set(var flags: bool[], value: bool) { flags[0] = value; }
            operation Main(): bool {
                var flags: bool[] = new bool[2];
                Set(var flags, 1 == 1);
                return flags[0];
            }
            """);

    [Theory]
    [InlineData("bool", "true", "bit", "0")]
    [InlineData("bit", "1", "bool", "false")]
    [InlineData("int", "1", "bool", "false")]
    public void MutableArrayWritesCannotCrossBoolBoundary(
        string sourceType, string sourceValue, string targetType, string initialValue) =>
        AssertRejected($$"""
            operation Copy(value: {{sourceType}}, var target: {{targetType}}[]) { target[0] = value; }
            operation Main() {
                var target: {{targetType}}[] = [{{initialValue}}];
                Copy({{sourceValue}}, var target);
            }
            """, "QSEM037");

    [Theory]
    [InlineData("if (1) {}")]
    [InlineData("if (0) {}")]
    [InlineData("var value: int = 1; if (value) {}")]
    [InlineData("var value: bit = 1; if (value) {}")]
    [InlineData("var value: bit = 0; while (value) {}")]
    [InlineData("repeat {} until (1);")]
    [InlineData("var values: bool[] = [true]; if (values) {}")]
    [InlineData("if (!1) {}")]
    [InlineData("var value: bit = 1; if (!value) {}")]
    [InlineData("if (true && 1) {}")]
    [InlineData("var value: bit = 0; if (value || false) {}")]
    public void ConditionsAndLogicalOperatorsRequireBool(string body) =>
        AssertRejected($"operation Main() {{ {body} }}", "QSEM041");

    [Fact]
    public void BoolConditionsAndMeasuredBitComparisonsAreAccepted() =>
        Compiler.Accepts("""
            operation Main(): bool {
                use q = Qubit[1];
                var measured: bit = M(q[0]);
                var flag: bool = measured == 1;
                if (flag && !false) { X(q[0]); }
                while (false) { X(q[0]); }
                repeat {} until (true);
                if (M(q[0]) == 1) { return true; }
                return false;
            }
            """);

    [Theory]
    [InlineData("true + 1")]
    [InlineData("1 - false")]
    [InlineData("true * true")]
    [InlineData("true / false")]
    [InlineData("-true")]
    [InlineData("true < false")]
    [InlineData("true <= false")]
    [InlineData("true > false")]
    [InlineData("true >= false")]
    [InlineData("true == 1")]
    [InlineData("false != 0")]
    [InlineData("1 == 2 < 3")]
    public void BoolCannotParticipateInNumericOperations(string expression) =>
        AssertRejected($"operation Main() {{ var value = {expression}; }}", "QSEM041");

    [Theory]
    [InlineData("var value: bool = M(q[0]);")]
    [InlineData("var values: bool[] = [M(q[0])];")]
    public void MeasurementStillProducesBit(string body) =>
        AssertRejected($"operation Main() {{ use q = Qubit[1]; {body} }}", "QSEM017");

    [Fact]
    public void PredicateAndMeasurementTypesRemainDistinctInHirAndMir()
    {
        var compilation = Compiler.Compile("""
            operation Main(): bool {
                use q = Qubit[1];
                var measured = M(q[0]);
                var predicate = measured == 1;
                return predicate;
            }
            """);
        AssertSucceeded(compilation);
        var artifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var declarations = artifact.Program.EntryCallable!.Body
            .OfType<HirVariableDeclarationStatement>().ToArray();
        Assert.Equal(QType.Bit, artifact.Model.FindSymbol(declarations[0].Id)!.Type);
        Assert.Equal(QType.Bool, artifact.Model.FindSymbol(declarations[1].Id)!.Type);
        var main = Assert.IsType<MirSnapshot>(compilation.Mir).Program.EntryPoint;
        Assert.Equal(MirType.Scalar(QType.Bool), main.ReturnType);
        var comparison = Assert.Single(main.Blocks.SelectMany(block => block.Instructions).OfType<MirBinary>());
        Assert.Equal(MirType.Scalar(QType.Bool), comparison.Result.Type);
    }

    private static void AssertRejected(string source, string code)
    {
        var compilation = Compiler.Compile(source);
        Assert.False(compilation.Succeeded, $"Expected {code}, but compiled:\n{source}");
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Stage == CompilationStage.HirValidation && diagnostic.Error.Code == code);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Error.Code is "QINTERNAL" or "QORA0000" or "CE0001");
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }

    private static void AssertSucceeded(Compilation compilation) =>
        Assert.True(compilation.Succeeded, string.Join(" | ", compilation.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
}
