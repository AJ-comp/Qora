using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class ParenthesizedExpressionTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "Add(1,Multiply(2,3))")]
    [InlineData("(1 + 2) * 3", "Multiply(Add(1,2),3)")]
    [InlineData("1 * (2 + 3)", "Multiply(1,Add(2,3))")]
    [InlineData("((1 + 2) * (3 + 4))", "Multiply(Add(1,2),Add(3,4))")]
    [InlineData("20 - 5 - 2", "Subtract(Subtract(20,5),2)")]
    [InlineData("20 - (5 - 2)", "Subtract(20,Subtract(5,2))")]
    [InlineData("(20 - 5) - 2", "Subtract(Subtract(20,5),2)")]
    [InlineData("8 / (4 / 2)", "Divide(8,Divide(4,2))")]
    [InlineData("(8 / 4) / 2", "Divide(Divide(8,4),2)")]
    [InlineData("-(1 + 2) * 3", "Multiply(Negate(Add(1,2)),3)")]
    [InlineData("(-2) * (-3)", "Multiply(Negate(2),Negate(3))")]
    [InlineData("(((7)))", "7")]
    [InlineData("(1) + (2) * (3)", "Add(1,Multiply(2,3))")]
    public void GroupingPreservesTheArithmeticTree(string expression, string expected)
    {
        var compilation = Compiler.Compile($"operation Main(): int {{ return {expression}; }}");
        AssertSucceeded(compilation);
        var main = compilation.Hir.Resolved!.Program.EntryCallable!;
        var returned = Assert.Single(main.Body.OfType<HirReturnStatement>());

        Assert.Equal(expected, Shape(Assert.IsAssignableFrom<HirExpression>(returned.Value)));
    }

    [Theory]
    [InlineData("operation Main(){ var value: int = (1 + 2) * 3; value = (value + 1) * 2; }")]
    [InlineData("operation Main(){ const value: int = (1 + 2) * 3; }")]
    [InlineData("operation Main(){ var values: int[] = [(1 + 2) * 3, (4 + 5)]; }")]
    [InlineData("operation Main(): int { var values: int[] = [10, 20, 30]; values[(1 + 1)] = (2 + 3); return values[(1 + 1)]; }")]
    [InlineData("operation Main(): bit { use q = Qubit[2]; X(q[(0 + 1)]); return M(q[(0 + 1)]); }")]
    [InlineData("operation Main(): bit { use q = Qubit[1]; return ((M(q[0]))); }")]
    [InlineData("operation Main(){ use q = Qubit[1]; Rx((pi / 2) + (pi / 4), q[0]); }")]
    [InlineData("function Next(value: int): int { return (value + 1) * 2; } operation Main(): int { return (Next((1 + 2)) + Next((3))) * 2; }")]
    [InlineData("operation Main(){ var values: int[] = [0, 0, 0]; for i in (1 - 1)..(1 + 1) { values[i] = (i + 1); } }")]
    [InlineData("operation Main(){ var value: int = 1; if ((value + 1) * 2 == (3 + 1)) { value = (value + 2); } }")]
    [InlineData("operation Main(){ var value: int = 0; while ((value + 1) < (2 + 1)) { value = (value + 1); } }")]
    [InlineData("operation Main(){ var value: int = 0; repeat { value = (value + 1); } until ((value + 1) == (1 + 1)); }")]
    public void GroupingWorksInExistingValueExpressionPositions(string source) =>
        Compiler.Accepts(source);

    [Theory]
    [InlineData("var")]
    [InlineData("move")]
    [InlineData("move var")]
    public void GroupedArrayBindingsPreserveArgumentModes(string mode) =>
        Compiler.Accepts($$"""
            operation Take({{mode}} values: int[]) {}
            operation Main() {
                var values: int[] = [1, 2];
                Take({{mode}} (values));
            }
            """);

    [Fact]
    public void GroupedMovedBindingIsStillUnavailableAfterTheCall() =>
        Compiler.Rejects("""
            operation Take(move values: int[]) {}
            operation Main(): int {
                var values: int[] = [1, 2];
                Take(move (values));
                return values[0];
            }
            """, "QSEM039");

    [Fact]
    public void SixtyFourRedundantGroupsPreserveTheValue()
    {
        var expression = new string('(', 64) + "7" + new string(')', 64);
        var compilation = Compiler.Compile($"operation Main(): int {{ return {expression}; }}");
        AssertSucceeded(compilation);
        var main = compilation.Hir.Resolved!.Program.EntryCallable!;
        var returned = Assert.Single(main.Body.OfType<HirReturnStatement>());
        Assert.Equal(7L, Assert.IsType<HirIntegerLiteralExpression>(returned.Value).Value);
    }

    [Fact]
    public void EffectfulCallsKeepLeftToRightOrderAndGroupedResultDependencies()
    {
        var compilation = Compiler.Compile("""
            operation First(q: Qubit): int { X(q); return 1; }
            operation Second(q: Qubit): int { X(q); return 2; }
            operation Third(q: Qubit): int { X(q); return 3; }
            operation Main(): int {
                use q = Qubit[1];
                return (First(q[0]) + Second(q[0])) * Third(q[0]);
            }
            """);
        AssertSucceeded(compilation);
        var program = Assert.IsType<MirSnapshot>(compilation.Mir).Program;
        Assert.Empty(QoraMirVerifier.Verify(program));
        var instructions = program.EntryPoint.Blocks.SelectMany(block => block.Instructions).ToArray();
        var calls = instructions.OfType<MirQuantumApply>().ToArray();
        var callNames = calls.Select(call =>
        {
            var target = Assert.IsType<MirUserCallableTarget>(call.Target);
            return program.Callables.Single(callable => callable.Id == target.Callable).Name;
        });
        Assert.Equal(new[] { "First", "Second", "Third" }, callNames);

        var sum = Assert.Single(instructions.OfType<MirBinary>(), instruction => instruction.Operator == MirBinaryOperator.Add);
        var product = Assert.Single(instructions.OfType<MirBinary>(), instruction => instruction.Operator == MirBinaryOperator.Multiply);
        Assert.Equal(Assert.IsType<MirValue>(calls[0].Result).Id, sum.Left);
        Assert.Equal(Assert.IsType<MirValue>(calls[1].Result).Id, sum.Right);
        Assert.Equal(sum.Result.Id, product.Left);
        Assert.Equal(Assert.IsType<MirValue>(calls[2].Result).Id, product.Right);
    }

    [Fact]
    public void NestedGroupingPreservesOriginalLiteralSpans()
    {
        const string source = "operation Main(): int { return ((12 + 34) * 56); }";
        var compilation = Compiler.Compile(source);
        AssertSucceeded(compilation);
        var main = compilation.Hir.Resolved!.Program.EntryCallable!;
        var returned = Assert.Single(main.Body.OfType<HirReturnStatement>());
        var literals = HirExpressions.DescendantsAndSelf(returned.Value!)
            .OfType<HirIntegerLiteralExpression>()
            .ToArray();

        Assert.Equal(new long[] { 12, 34, 56 }, literals.Select(literal => literal.Value));
        foreach (var literal in literals)
        {
            var span = Assert.IsType<SourceSpan>(literal.Span);
            Assert.Equal(literal.Value.ToString(), source[span.Start..span.End]);
        }
    }

    [Theory]
    [InlineData("(Next(1) + 2) * 3")]
    [InlineData("1 + (Next(1) * 3)")]
    [InlineData("-(Next(1) + 2)")]
    [InlineData("(Next(1)) + (2 * 3)")]
    public void ArithmeticExpressionSpanIncludesGroupedOperandsWithoutWideningCallSpans(string expression)
    {
        var source = "function Next(value: int): int { return value; } operation Main(): int { return " + expression + "; }";
        var compilation = Compiler.Compile(source);
        AssertSucceeded(compilation);
        var main = compilation.Hir.Resolved!.Program.EntryCallable!;
        var returned = Assert.Single(main.Body.OfType<HirReturnStatement>());
        var value = Assert.IsAssignableFrom<HirExpression>(returned.Value);
        var expressionSpan = Assert.IsType<SourceSpan>(value.Span);
        Assert.Equal(expression, source[expressionSpan.Start..expressionSpan.End]);

        var call = Assert.Single(HirExpressions.DescendantsAndSelf(value).OfType<HirCallExpression>());
        var callSpan = Assert.IsType<SourceSpan>(call.Span);
        Assert.Equal("Next(1)", source[callSpan.Start..callSpan.End]);
    }

    [Theory]
    [InlineData("(x) == (y)")]
    [InlineData("!(x)")]
    public void ConditionSpanIncludesGroupedOperands(string condition)
    {
        var source = "operation Main(){ var x: bool = true; var y: bool = false; if (" + condition + ") {} }";
        var compilation = Compiler.Compile(source);
        AssertSucceeded(compilation);
        var main = compilation.Hir.Resolved!.Program.EntryCallable!;
        var branch = Assert.Single(main.Body.OfType<HirIfStatement>());
        var conditionSpan = Assert.IsType<SourceSpan>(branch.Condition.Span);

        Assert.Equal(condition, source[conditionSpan.Start..conditionSpan.End]);
        foreach (var name in HirExpressions.DescendantsAndSelf(branch.Condition).OfType<HirNameExpression>())
        {
            var nameSpan = Assert.IsType<SourceSpan>(name.Span);
            Assert.Equal(name.Name, source[nameSpan.Start..nameSpan.End]);
        }
    }

    [Theory]
    [InlineData("()")]
    [InlineData("(1 + 2")]
    [InlineData("1 + 2)")]
    [InlineData("(1 +)")]
    [InlineData("(1 2)")]
    [InlineData("(1, 2)")]
    [InlineData("(1)(2)")]
    public void MalformedGroupingReportsSyntaxDiagnostics(string expression) =>
        Compiler.Rejects($"operation Main(): int {{ return {expression}; }}", "CE0001");

    [Theory]
    [InlineData("operation Main(){ var values: int[] = [1, 2]; var result: int = values[(1 + 2)]; }", "QSEM016")]
    [InlineData("operation Main(): int { return (0.5); }", "QSEM037")]
    [InlineData("operation Main(){ use q = Qubit[1]; var result: int = (M(q[0]) + 1); }", "QSEM005")]
    [InlineData("operation Main(){ use q = Qubit[1]; var result: int = (M(q[0])) + 1; }", "QSEM005")]
    [InlineData("operation Take(var values: int[]) {} operation Main(){ var value: int = 1; Take(var (value + 1)); }", "QSEM038")]
    public void GroupingDoesNotBypassExistingSemanticChecks(string source, string code) =>
        Compiler.Rejects(source, code);

    private static string Shape(HirExpression expression) => expression switch
    {
        HirIntegerLiteralExpression literal => literal.Value.ToString(),
        HirUnaryExpression unary => $"{unary.Operator}({Shape(unary.Operand)})",
        HirBinaryExpression binary => $"{binary.Operator}({Shape(binary.Left)},{Shape(binary.Right)})",
        _ => throw new InvalidOperationException($"Unexpected arithmetic node {expression.GetType().Name}"),
    };

    private static void AssertSucceeded(Qora.Compiler.Compilation compilation) =>
        Assert.True(compilation.Succeeded, string.Join(" | ", compilation.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
}
