using Qora.Compiler;

namespace Qora.Tests;

public sealed class ScalarConversionConsistencyTests
{
    private static readonly string[] ScalarTypes = ["bool", "bit", "int", "float", "angle"];

    public static IEnumerable<object[]> ScalarConversionCases()
    {
        foreach (var targetType in ScalarTypes)
        {
            foreach (var sourceType in ScalarTypes)
            {
                foreach (var sourceForm in new[] { "variable", "element", "function" })
                {
                    var allowed = targetType == sourceType
                        || (sourceType, targetType) is ("bit", "int")
                            or ("int", "float")
                            or ("float", "angle");
                    yield return [targetType, sourceType, sourceForm, allowed];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ScalarConversionCases))]
    public void ScalarConversionsUseOneRuleInEveryValueContext(
        string targetType,
        string sourceType,
        string sourceForm,
        bool allowed)
    {
        var setup = sourceForm switch
        {
            "variable" => $"var source: {sourceType} = {InitialValue(sourceType)};",
            "element" => $"var source: {sourceType}[] = [{InitialValue(sourceType)}];",
            "function" => "",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceForm)),
        };
        var expression = sourceForm switch
        {
            "variable" => "source",
            "element" => "source[0]",
            "function" => "ReadSource()",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceForm)),
        };
        var declarations = sourceForm == "function"
            ? $"function ReadSource(): {sourceType} {{ return {InitialValue(sourceType)}; }}"
            : "";

        foreach (var (program, diagnostic) in ValueContexts(targetType, expression, setup))
            AssertOutcome($"{declarations} {program}", allowed, diagnostic);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("((1))", true)]
    [InlineData("2", false)]
    [InlineData("-1", false)]
    [InlineData("1.0", false)]
    [InlineData("0 + 1", false)]
    [InlineData("source", false)]
    [InlineData("ReadOne()", false)]
    public void OnlyIntegerZeroAndOneLiteralsConvertToBit(string expression, bool allowed)
    {
        const string declarations = "function ReadOne(): int { return 1; }";
        foreach (var (program, diagnostic) in ValueContexts("bit", expression, "const source: int = 1;"))
            AssertOutcome($"{declarations} {program}", allowed, diagnostic);
    }

    public static IEnumerable<object[]> ArrayArgumentCases()
    {
        foreach (var callableKind in new[] { "function", "operation" })
        {
            foreach (var parameterType in ScalarTypes)
            {
                foreach (var sourceType in ScalarTypes)
                    yield return [callableKind, parameterType, sourceType];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ArrayArgumentCases))]
    public void BorrowedArraysRequireTheExactElementType(
        string callableKind,
        string parameterType,
        string sourceType)
    {
        var source = $$"""
            {{callableKind}} Take(values: {{parameterType}}[]): int { return values.Count; }
            operation Main(): int {
                var values: {{sourceType}}[] = [{{InitialValue(sourceType)}}];
                return Take(values);
            }
            """;
        AssertOutcome(source, parameterType == sourceType, "QSEM006");
    }

    [Theory]
    [InlineData("function", "bit", "1")]
    [InlineData("function", "float", "1")]
    [InlineData("function", "angle", "0.5")]
    [InlineData("operation", "bit", "1")]
    [InlineData("operation", "float", "1")]
    [InlineData("operation", "angle", "0.5")]
    public void ScalarConversionsDoNotEraseTheArrayArgumentBoundary(
        string callableKind,
        string parameterType,
        string argument) =>
        AssertOutcome($$"""
            {{callableKind}} Take(values: {{parameterType}}[]): int { return values.Count; }
            operation Main(): int { return Take({{argument}}); }
            """, false, "QSEM006");

    [Theory]
    [InlineData("bool", false)]
    [InlineData("bit", false)]
    [InlineData("int", false)]
    [InlineData("float", true)]
    [InlineData("angle", true)]
    public void IntrinsicAngleArgumentsUseTheSameConversionRule(string sourceType, bool allowed)
    {
        AssertOutcome($$"""
            operation Main() {
                use q = Qubit[1];
                var source: {{sourceType}} = {{InitialValue(sourceType)}};
                Rx(source, q[0]);
            }
            """, allowed, "QSEM006");
        AssertOutcome($$"""
            function ReadSource(): {{sourceType}} { return {{InitialValue(sourceType)}}; }
            operation Main() {
                use q = Qubit[1];
                Rx(ReadSource(), q[0]);
            }
            """, allowed, "QSEM006");
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("1.0", true)]
    [InlineData("pi / 2", true)]
    public void IntrinsicLiteralArgumentsDoNotUseASeparatePermissiveRule(string expression, bool allowed) =>
        AssertOutcome($"operation Main() {{ use q = Qubit[1]; Rx({expression}, q[0]); }}", allowed, "QSEM006");

    [Theory]
    [InlineData("bool", false)]
    [InlineData("bit", true)]
    [InlineData("int", true)]
    [InlineData("float", false)]
    [InlineData("angle", false)]
    public void DirectMeasurementsFollowTheBitValueConversionRule(string targetType, bool allowed)
    {
        const string setup = "use q = Qubit[1];";
        foreach (var declarationKind in new[] { "var", "const" })
        {
            AssertOutcome(
                $"operation Main() {{ {setup} {declarationKind} result: {targetType} = M(q[0]); }}",
                allowed, "QSEM037");
            AssertOutcome(
                $"operation Main() {{ {setup} {declarationKind} result: {targetType}[] = [M(q[0])]; }}",
                allowed, "QSEM037");
        }
        AssertOutcome(
            $"operation Main() {{ {setup} var result: {targetType} = {InitialValue(targetType)}; result = M(q[0]); }}",
            allowed, "QSEM037");
        AssertOutcome(
            $"operation Main() {{ {setup} var result: {targetType}[] = [{InitialValue(targetType)}]; result[0] = M(q[0]); }}",
            allowed, "QSEM037");
        AssertOutcome(
            $"operation Main(): {targetType} {{ {setup} return M(q[0]); }}",
            allowed, "QSEM037");
    }

    [Theory]
    [InlineData("var", "bit")]
    [InlineData("const", "bit")]
    [InlineData("var", "int")]
    [InlineData("const", "int")]
    public void MeasurementInitializerReadsTheQubitBeforeItsNewBindingShadowsIt(
        string declarationKind,
        string targetType) =>
        AssertOutcome($$"""
            operation Main(): {{targetType}} {
                use q = Qubit[1];
                if (true) {
                    {{declarationKind}} q: {{targetType}} = M(q[0]);
                    return q;
                }
                return 0;
            }
            """, true, "QSEM037");

    [Theory]
    [InlineData("bit")]
    [InlineData("int")]
    public void MeasurementConversionPreservesConstWriteProtection(string targetType) =>
        AssertOutcome($$"""
            operation Main() {
                use q = Qubit[1];
                const result: {{targetType}} = M(q[0]);
                result = 0;
            }
            """, false, "QSEM024");

    [Theory]
    [InlineData("bool", false)]
    [InlineData("bit", false)]
    [InlineData("int", true)]
    [InlineData("float", true)]
    [InlineData("angle", false)]
    public void OperationResultConversionMatchesAnOrdinaryIntValue(string targetType, bool allowed)
    {
        const string declarations = "operation ReadSource(): int { return 1; }";
        var source = $$"""
            {{declarations}}
            operation Main(): {{targetType}} {
                var result: {{targetType}} = ReadSource();
                return result;
            }
            """;
        AssertOutcome(source, allowed, "QSEM037");
    }

    private static IEnumerable<(string Program, string Diagnostic)> ValueContexts(
        string targetType,
        string expression,
        string setup)
    {
        foreach (var declarationKind in new[] { "var", "const" })
        {
            yield return (
                $"operation Main() {{ {setup} {declarationKind} result: {targetType} = {expression}; }}",
                "QSEM037");
            yield return (
                $"operation Main() {{ {setup} {declarationKind} result: {targetType}[] = [{expression}]; }}",
                "QSEM037");
        }
        yield return (
            $"operation Main() {{ {setup} var result: {targetType} = {InitialValue(targetType)}; result = {expression}; }}",
            "QSEM037");
        yield return (
            $"operation Main() {{ {setup} var result: {targetType}[] = [{InitialValue(targetType)}]; result[0] = {expression}; }}",
            "QSEM037");

        foreach (var callableKind in new[] { "function", "operation" })
        {
            var callee = $"{callableKind} Take(value: {targetType}): {targetType} {{ return value; }}";
            yield return (
                $"{callee} operation Main() {{ {setup} Take({expression}); }}",
                "QSEM006");
            yield return (
                $"{callee} operation Main(): {targetType} {{ {setup} return Take({expression}); }}",
                "QSEM006");
            yield return (
                $"{callableKind} Read(): {targetType} {{ {setup} return {expression}; }} operation Main() {{ Read(); }}",
                "QSEM037");
        }
    }

    private static string InitialValue(string type) => type switch
    {
        "bool" => "true",
        "bit" => "1",
        "int" => "2",
        "float" => "0.5",
        "angle" => "0.5",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static void AssertOutcome(string source, bool allowed, string expectedDiagnostic)
    {
        var compilation = Compiler.Compile(source);
        var diagnostics = string.Join(" | ", compilation.Diagnostics.Select(
            diagnostic => $"{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
        Assert.True(compilation.Succeeded == allowed,
            $"Expected {(allowed ? "success" : expectedDiagnostic)}, got [{diagnostics}]:\n{source}");

        if (allowed)
        {
            Assert.Empty(compilation.Diagnostics);
            Assert.NotNull(compilation.Mir);
            Assert.False(string.IsNullOrWhiteSpace(compilation.Targets.OpenQasm?.Text));
            return;
        }

        var error = Assert.Single(compilation.Diagnostics);
        Assert.Equal(expectedDiagnostic, error.Error.Code);
        Assert.Equal(CompilationStage.HirValidation, error.Stage);
        var span = Assert.IsType<SourceSpan>(error.Error.Span);
        Assert.InRange(span.Start, 0, source.Length - 1);
        Assert.InRange(span.End, span.Start + 1, source.Length);
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }
}
