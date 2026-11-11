using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class ComparisonValueTests
{
    [Theory]
    [InlineData("==", MirBinaryOperator.Equal)]
    [InlineData("!=", MirBinaryOperator.NotEqual)]
    [InlineData("<", MirBinaryOperator.Less)]
    [InlineData("<=", MirBinaryOperator.LessOrEqual)]
    [InlineData(">", MirBinaryOperator.Greater)]
    [InlineData(">=", MirBinaryOperator.GreaterOrEqual)]
    public void EachComparisonProducesABoolValue(string comparison, MirBinaryOperator expectedOperator)
    {
        var compilation = Compiler.Compile($$"""
            operation Main(): bool {
                var left: int = 1;
                var right: int = 2;
                var value: bool = left {{comparison}} right;
                return value;
            }
            """);
        AssertSucceeded(compilation);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        Assert.Empty(QoraMirVerifier.Verify(mir.Program));
        var main = mir.Program.EntryPoint;
        var comparisonInstruction = Assert.Single(main.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirBinary>());

        Assert.Equal(expectedOperator, comparisonInstruction.Operator);
        Assert.Equal(MirType.Scalar(QType.Bool), comparisonInstruction.Result.Type);
        var returned = Assert.IsType<MirReturn>(Assert.Single(main.Blocks).Terminator);
        Assert.Equal(comparisonInstruction.Result.Id, returned.Value);
    }

    [Theory]
    [InlineData("var value: bool = 1 == 1;")]
    [InlineData("const value: bool = 1 != 2;")]
    [InlineData("var value: bool = false; value = 1 < 2;")]
    [InlineData("var value = false; value = 2 <= 2;")]
    [InlineData("var values: bool[] = [1 == 1, 2 < 1, (3 >= 2)];")]
    [InlineData("const values: bool[] = [1 != 2, 2 > 3];")]
    [InlineData("var values: bool[] = [false]; values[0] = 3 > 2;")]
    [InlineData("var value = 1 == 1;")]
    [InlineData("var values: bool[] = [1 == 1, 2 == 3]; var value = values[0];")]
    [InlineData("var values: int[] = [1, 2]; var value: bool = values[0] < values[1];")]
    [InlineData("var value: bool = 0.5 < 1;")]
    [InlineData("var value: bool = pi > 3;")]
    [InlineData("var a: angle = 1; var b: angle = 2; var value: bool = a < b;")]
    [InlineData("var value: bool = true == false;")]
    public void ComparisonsWorkInStoragePositions(string body) =>
        Compiler.Accepts($"operation Main() {{ {body} }}");

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void ComparisonsWorkAsArgumentsInStatementAndValueCalls(string callableKind) =>
        Compiler.Accepts($$"""
            {{callableKind}} Take(value: bool): bool { return value; }
            operation Main(): bool {
                Take(1 == 1);
                var value: bool = Take((1 + 2) <= 3);
                return Take(value != false);
            }
            """);

    [Theory]
    [InlineData("function", "value > 0")]
    [InlineData("function", "(value + 1) == 2")]
    [InlineData("operation", "value > 0")]
    [InlineData("operation", "(value + 1) == 2")]
    public void ComparisonsWorkAsCallableReturns(string callableKind, string expression) =>
        Compiler.Accepts($$"""
            {{callableKind}} Check(value: int): bool { return {{expression}}; }
            operation Main(): bool { return Check(1); }
            """);

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void ComparisonsCannotBecomeIntArgumentsOrReturns(string callableKind)
    {
        Compiler.Rejects($$"""
            {{callableKind}} Take(value: int): int { return value; }
            operation Main(): int { return Take(1 == 1); }
            """, "QSEM006");
        Compiler.Rejects($$"""
            {{callableKind}} Check(value: int): int { return value > 0; }
            operation Main(): int { return Check(1); }
            """, "QSEM037");
    }

    [Theory]
    [InlineData("var")]
    [InlineData("const")]
    public void UnannotatedComparisonInfersBool(string declarationKind)
    {
        var compilation = Compiler.Compile($$"""
            operation Main(): bool {
                {{declarationKind}} value = 1 == 1;
                return value;
            }
            """);
        AssertSucceeded(compilation);
        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var declaration = Assert.Single(analyzed.Program.EntryCallable!.Body
            .OfType<HirVariableDeclarationStatement>());
        var symbol = analyzed.Model.FindSymbol(declaration.Id);

        Assert.NotNull(symbol);
        Assert.Equal(QType.Bool, symbol.Type);
    }

    [Theory]
    [InlineData("1 + 2 * 3 == 7", "Equal(Add(1,Multiply(2,3)),7)")]
    [InlineData("(1 + 2) * 3 >= 9", "GreaterThanOrEqual(Multiply(Add(1,2),3),9)")]
    [InlineData("(1 == 1) == 1 < 2", "Equal(Equal(1,1),LessThan(1,2))")]
    [InlineData("1 < 2 == 3 > 2", "Equal(LessThan(1,2),GreaterThan(3,2))")]
    [InlineData("(1 == 2) == (3 != 4)", "Equal(Equal(1,2),NotEqual(3,4))")]
    [InlineData("((1 + 2) == (3 * 1))", "Equal(Add(1,2),Multiply(3,1))")]
    [InlineData("-(1 + 2) < -2", "LessThan(Negate(Add(1,2)),Negate(2))")]
    [InlineData("(1 < 2) == (3 > 2) == (4 == 4)", "Equal(Equal(LessThan(1,2),GreaterThan(3,2)),Equal(4,4))")]
    public void ComparisonPrecedenceAndGroupingPreserveTheExpressionTree(string expression, string expected)
    {
        var compilation = Compiler.Compile($"operation Main(): bool {{ return {expression}; }}");
        AssertSucceeded(compilation);
        var returned = Assert.Single(compilation.Hir.Resolved!.Program.EntryCallable!.Body
            .OfType<HirReturnStatement>());

        Assert.Equal(expected, Shape(Assert.IsAssignableFrom<HirExpression>(returned.Value)));
    }

    [Theory]
    [InlineData("(Next(1) + 2) == (3 * 1)")]
    [InlineData("(Next(1)) < (2)")]
    [InlineData("(Next(1) < 2) == (3 > 2)")]
    public void ComparisonSpanIncludesGroupedOperandsWithoutWideningCallSpans(string expression)
    {
        var source = "function Next(value: int): int { return value; } operation Main(): bool { return "
            + expression + "; }";
        var compilation = Compiler.Compile(source);
        AssertSucceeded(compilation);
        var returned = Assert.Single(compilation.Hir.Resolved!.Program.EntryCallable!.Body
            .OfType<HirReturnStatement>());
        var value = Assert.IsAssignableFrom<HirExpression>(returned.Value);
        var span = Assert.IsType<SourceSpan>(value.Span);
        Assert.Equal(expression, source[span.Start..span.End]);

        var call = Assert.Single(HirExpressions.DescendantsAndSelf(value).OfType<HirCallExpression>());
        var callSpan = Assert.IsType<SourceSpan>(call.Span);
        Assert.Equal("Next(1)", source[callSpan.Start..callSpan.End]);
    }

    [Fact]
    public void EffectfulComparisonOperandsExecuteOnceInLeftToRightOrder()
    {
        var compilation = Compiler.Compile("""
            operation First(q: Qubit): int { X(q); return 1; }
            operation Second(q: Qubit): int { X(q); return 2; }
            operation Main(): bool {
                use q = Qubit[1];
                return First(q[0]) < Second(q[0]);
            }
            """);
        AssertSucceeded(compilation);
        var program = Assert.IsType<MirSnapshot>(compilation.Mir).Program;
        var instructions = program.EntryPoint.Blocks.SelectMany(block => block.Instructions).ToArray();
        var calls = instructions.OfType<MirQuantumApply>().ToArray();
        var names = calls.Select(call =>
        {
            var target = Assert.IsType<MirUserCallableTarget>(call.Target);
            return program.Callables.Single(callable => callable.Id == target.Callable).Name;
        });
        Assert.Equal(new[] { "First", "Second" }, names);
        var comparison = Assert.Single(instructions.OfType<MirBinary>());
        Assert.Equal(MirBinaryOperator.Less, comparison.Operator);
        Assert.Equal(Assert.IsType<MirValue>(calls[0].Result).Id, comparison.Left);
        Assert.Equal(Assert.IsType<MirValue>(calls[1].Result).Id, comparison.Right);
    }

    [Theory]
    [InlineData("int", "1, 2", "1, 2")]
    [InlineData("int", "1", "1, 2")]
    [InlineData("bit", "0, 1", "1, 0")]
    public void ExistingArrayEqualityCanAlsoProduceAStoredValue(string type, string left, string right) =>
        Compiler.Accepts($$"""
            operation Main(): bool {
                var left: {{type}}[] = [{{left}}];
                var right: {{type}}[] = [{{right}}];
                var equal: bool = left == right;
                var different: bool = left != right;
                return equal != different;
            }
            """);

    [Fact]
    public void ExistingLogicalConditionsConsumeStoredComparisons() =>
        Compiler.Accepts("""
            operation Main(): bool {
                var count: int = 0;
                var enabled: bool = 1 == 1;
                if (enabled && !(count > 0) || count == 2) { count = count + 1; }
                while (count < 2 && enabled) { count = count + 1; }
                repeat { count = count + 1; } until (count >= 3 || !enabled);
                return count == 3;
            }
            """);

    [Fact]
    public void ConditionLogicalNotStillBindsMoreTightlyThanEquality()
    {
        var compilation = Compiler.Compile("""
            operation Main() {
                var a: bool = false;
                var b: bool = true;
                if (!a == b) {}
            }
            """);
        AssertSucceeded(compilation);
        var branch = Assert.Single(compilation.Hir.Resolved!.Program.EntryCallable!.Body
            .OfType<HirIfStatement>());
        var comparison = Assert.IsType<HirBinaryExpression>(branch.Condition);

        Assert.Equal(HirBinaryOperator.Equal, comparison.Operator);
        var negation = Assert.IsType<HirUnaryExpression>(comparison.Left);
        Assert.Equal(HirUnaryOperator.LogicalNot, negation.Operator);
        Assert.Equal("a", Assert.IsType<HirNameExpression>(negation.Operand).Name);
        Assert.Equal("b", Assert.IsType<HirNameExpression>(comparison.Right).Name);
    }

    [Fact]
    public void ComparisonsCanUsePreviouslyMeasuredBits() =>
        Compiler.Accepts("""
            operation Main(): bool {
                use q = Qubit[1];
                var measured: bit = M(q[0]);
                var isOne: bool = measured == 1;
                return isOne;
            }
            """);

    [Theory]
    [InlineData("operation Main(){ use q = Qubit[1]; var value: bool = M(q[0]) == 1; }", "QSEM005")]
    [InlineData("operation Main(): bool { use q = Qubit[1]; return M(q[0]) == 1; }", "QSEM005")]
    [InlineData("operation Main(){ use q = Qubit[1]; var values: bool[] = [M(q[0]) == 1]; }", "QSEM005")]
    [InlineData("operation Main(){ use q = Qubit[1]; var value: bool = q[0] == 1; }", "QSEM026")]
    [InlineData("operation Main(){ var values: int[] = [1]; var value: bool = values < 1; }", "QSEM037")]
    [InlineData("operation Main(){ var values: int[] = [1]; var value: bool = values == 1; }", "QSEM037")]
    [InlineData("operation Main(){ var left: int[] = [1]; var right: float[] = [1.0]; var value: bool = left == right; }", "QSEM037")]
    [InlineData("function Take(value: bool): bool { return value; } operation Main(){ use q = Qubit[1]; Take(M(q[0]) == 1); }", "QSEM005")]
    [InlineData("function Take(value: float): float { return value; } operation Main(){ Take(1 == 1); }", "QSEM006")]
    [InlineData("function Check(): float { return 1 == 1; } operation Main(){ Check(); }", "QSEM037")]
    public void ComparisonsDoNotBypassExistingSemanticRestrictions(string source, string expectedCode)
    {
        var compilation = Compiler.Compile(source);
        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Error.Code == expectedCode);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code is "QINTERNAL" or "QORA0000");
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }

    [Theory]
    [InlineData("1 ==")]
    [InlineData("== 1")]
    [InlineData("1 < < 2")]
    [InlineData("(1 == 2")]
    [InlineData("1 === 2")]
    public void MalformedComparisonsReportSyntaxErrors(string expression) =>
        Compiler.Rejects($"operation Main() {{ var value: bool = {expression}; }}", "CE0001");

    [Theory]
    [InlineData("true && false")]
    [InlineData("true || false")]
    [InlineData("!true")]
    public void LogicalValueExpressionsAreNotAddedByComparisonSupport(string expression) =>
        Compiler.Rejects($"operation Main() {{ var value: bool = {expression}; }}", "CE0001");

    private static string Shape(HirExpression expression) => expression switch
    {
        HirIntegerLiteralExpression literal => literal.Value.ToString(),
        HirUnaryExpression unary => $"{unary.Operator}({Shape(unary.Operand)})",
        HirBinaryExpression binary => $"{binary.Operator}({Shape(binary.Left)},{Shape(binary.Right)})",
        _ => throw new InvalidOperationException($"Unexpected expression node {expression.GetType().Name}"),
    };

    private static void AssertSucceeded(Compilation compilation) =>
        Assert.True(compilation.Succeeded, string.Join(" | ", compilation.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
}
