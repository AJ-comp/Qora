using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class LogicalValueExpressionTests
{
    [Theory]
    [InlineData("!false")]
    [InlineData("!!true")]
    [InlineData("!!!false")]
    [InlineData("true && false")]
    [InlineData("false || true")]
    [InlineData("(true || false) && !false")]
    [InlineData("!(1 < 2) || 3 == 3 && 4 >= 4")]
    [InlineData("!(false || (true && !true))")]
    public void LogicalValuesWorkInEveryStoragePosition(string expression)
    {
        Compiler.Accepts($$"""
            operation Main(): bool {
                var inferred = {{expression}};
                const constant = {{expression}};
                var typed: bool = {{expression}};
                const typedConstant: bool = {{expression}};
                typed = {{expression}};
                var values: bool[] = [{{expression}}, !inferred];
                const constants: bool[] = [{{expression}}];
                values[0] = {{expression}};
                return typed && constant || typedConstant && constants[0] || values[0];
            }
            """);
    }

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void LogicalValuesWorkInCallableArgumentsAndReturns(string callableKind) =>
        Compiler.Accepts($$"""
            {{callableKind}} Echo(value: bool): bool { return !!value; }
            {{callableKind}} Combine(left: bool, right: bool): bool {
                return !(left && right) || Echo(left || right);
            }
            operation Main(): bool {
                Echo(!false && true);
                var result = Combine(true || false, !(true && false));
                result = Echo(!result || false);
                return Echo(result && !false);
            }
            """);

    [Theory]
    [InlineData("var", "false || true")]
    [InlineData("const", "false || true")]
    [InlineData("var", "!false")]
    [InlineData("const", "!false")]
    public void UnannotatedLogicalExpressionInfersBool(
        string declarationKind,
        string expression)
    {
        var compilation = Compiler.Compile(
            $"operation Main(): bool {{ {declarationKind} value = {expression}; return value; }}");
        AssertSucceeded(compilation);
        var artifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var declaration = Assert.Single(artifact.Program.EntryCallable!.Body
            .OfType<HirVariableDeclarationStatement>());
        Assert.Equal(QType.Bool, artifact.Model.FindSymbol(declaration.Id)!.Type);
    }

    [Theory]
    [InlineData("a || b && !c", "LogicalOr(a,LogicalAnd(b,LogicalNot(c)))")]
    [InlineData("!a == b && c", "LogicalAnd(Equal(LogicalNot(a),b),c)")]
    [InlineData("!(a == b) && c", "LogicalAnd(LogicalNot(Equal(a,b)),c)")]
    [InlineData("a && b || c && !a", "LogicalOr(LogicalAnd(a,b),LogicalAnd(c,LogicalNot(a)))")]
    [InlineData("a || b || c", "LogicalOr(LogicalOr(a,b),c)")]
    [InlineData("a && b && c", "LogicalAnd(LogicalAnd(a,b),c)")]
    [InlineData("!!a", "LogicalNot(LogicalNot(a))")]
    [InlineData("(a || b) && !(c || a)", "LogicalAnd(LogicalOr(a,b),LogicalNot(LogicalOr(c,a)))")]
    [InlineData("1 + 2 * 3 > 4 == a && b || c", "LogicalOr(LogicalAnd(Equal(GreaterThan(Add(1,Multiply(2,3)),4),a),b),c)")]
    [InlineData("!a == 1 + 2 < 4 || b", "LogicalOr(Equal(LogicalNot(a),LessThan(Add(1,2),4)),b)")]
    public void ConditionsAndValuesUseTheSamePrecedenceTree(string expression, string expected)
    {
        var compilation = Compiler.Compile($$"""
            operation Main(): bool {
                var a: bool = false;
                var b: bool = true;
                var c: bool = false;
                var value: bool = {{expression}};
                if ({{expression}}) {}
                while ({{expression}}) { return value; }
                repeat { } until ({{expression}});
                return {{expression}};
            }
            """);
        AssertSucceeded(compilation);
        var body = compilation.Hir.Resolved!.Program.EntryCallable!.Body;
        var declaration = Assert.Single(body.OfType<HirVariableDeclarationStatement>(), item => item.Name == "value");
        var branch = Assert.Single(body.OfType<HirIfStatement>());
        var whileStatement = Assert.Single(body.OfType<HirWhileStatement>());
        var repeat = Assert.Single(body.OfType<HirRepeatStatement>());
        var returned = Assert.Single(body.OfType<HirReturnStatement>());

        foreach (var value in new[] { declaration.Value, branch.Condition, whileStatement.Condition, repeat.Until, returned.Value! })
            Assert.Equal(expected, Shape(value));
    }

    [Theory]
    [InlineData("!(Check(true)) && (Check(false) || true)")]
    [InlineData("(Check(true)) || !(Check(false))")]
    [InlineData("!!(Check(true) && Check(false))")]
    public void LogicalExpressionSpansAreTheSameInConditionAndValuePositions(string expression)
    {
        var source = $$"""
            function Check(value: bool): bool { return value; }
            operation Main(): bool {
                var value: bool = {{expression}};
                if ({{expression}}) {}
                return {{expression}};
            }
            """;
        var compilation = Compiler.Compile(source);
        AssertSucceeded(compilation);
        var body = compilation.Hir.Resolved!.Program.EntryCallable!.Body;
        var declaration = Assert.Single(body.OfType<HirVariableDeclarationStatement>());
        var branch = Assert.Single(body.OfType<HirIfStatement>());
        var returned = Assert.Single(body.OfType<HirReturnStatement>());

        foreach (var value in new[] { declaration.Value, branch.Condition, returned.Value! })
        {
            var span = Assert.IsType<SourceSpan>(value.Span);
            Assert.Equal(expression, source[span.Start..span.End]);
            var calls = HirExpressions.DescendantsAndSelf(value).OfType<HirCallExpression>().ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal(new[] { "Check(true)", "Check(false)" }, calls.Select(call =>
            {
                var callSpan = Assert.IsType<SourceSpan>(call.Span);
                return source[callSpan.Start..callSpan.End];
            }));
        }
    }

    [Fact]
    public void LogicalInitializerReadsTheOuterBindingBeforeShadowingIt() =>
        Compiler.Accepts("""
            operation Main(): bool {
                var value: bool = true;
                if (true) {
                    var value: bool = !value || false;
                    return value;
                }
                return value;
            }
            """);

    [Theory]
    [InlineData("!1")]
    [InlineData("!0.5")]
    [InlineData("true && 1")]
    [InlineData("1 || false")]
    [InlineData("false && 1")]
    [InlineData("true || 1")]
    [InlineData("!1 + 2 == 3")]
    [InlineData("true && 1 == true")]
    public void LogicalOperatorsRequireBoolEvenOnASkippedRightOperand(string expression) =>
        AssertRejected($"operation Main(): bool {{ return {expression}; }}", "QSEM041");

    [Theory]
    [InlineData("!measured")]
    [InlineData("true && measured")]
    [InlineData("measured || false")]
    public void MeasuredBitsAreNotImplicitLogicalValues(string expression) =>
        AssertRejected($$"""
            operation Main(): bool {
                use q = Qubit[1];
                var measured: bit = M(q[0]);
                return {{expression}};
            }
            """, "QSEM041");

    [Theory]
    [InlineData("!values")]
    [InlineData("false && values")]
    [InlineData("values || true")]
    public void WholeBoolArraysAreNotScalarLogicalOperands(string expression) =>
        AssertRejected($$"""
            operation Main(): bool {
                var values: bool[] = [true];
                return {{expression}};
            }
            """, "QSEM037");

    [Theory]
    [InlineData("bit")]
    [InlineData("int")]
    [InlineData("float")]
    [InlineData("angle")]
    public void LogicalResultsCannotBypassTheUnifiedScalarConversionRule(string targetType)
    {
        AssertRejected($"operation Main() {{ var value: {targetType} = !false && true; }}", "QSEM037");
        AssertRejected($"operation Main() {{ var values: {targetType}[] = [true || false]; }}", "QSEM037");
        AssertRejected($"operation Main(): {targetType} {{ return !false; }}", "QSEM037");
        AssertRejected($$"""
            function Take(value: {{targetType}}): {{targetType}} { return value; }
            operation Main() { Take(true && false); }
            """, "QSEM006");
    }

    [Theory]
    [InlineData("!")]
    [InlineData("true &&")]
    [InlineData("|| false")]
    [InlineData("true || || false")]
    [InlineData("true && && false")]
    [InlineData("true ! false")]
    [InlineData("!(true ||)")]
    [InlineData("(true && false")]
    [InlineData("true && false)")]
    public void MalformedLogicalExpressionsReportSyntaxErrors(string expression) =>
        AssertRejected($"operation Main(): bool {{ return {expression}; }}", "CE0001");

    [Fact]
    public void AShortCircuitedNameMustStillBeDeclared() =>
        AssertRejected("operation Main(): bool { return false && missing; }", "QSEM025");

    [Theory]
    [InlineData("var value: bool = true && M(q[0]) == 1;")]
    [InlineData("var values: bool[] = [false || M(q[0]) == 1];")]
    [InlineData("return !(M(q[0]) == 1);")]
    public void LogicalValuesDoNotExpandMeasurementPlacement(string statement) =>
        AssertRejected($$"""
            operation Main(): bool {
                use q = Qubit[1];
                {{statement}}
                return false;
            }
            """, "QSEM005");

    [Theory]
    [InlineData("false && Consume(move values)", true)]
    [InlineData("true || Consume(move values)", true)]
    [InlineData("true && Consume(move values)", false)]
    [InlineData("false || Consume(move values)", false)]
    [InlineData("Guard() && Consume(move values)", false)]
    [InlineData("Guard() || Consume(move values)", false)]
    public void ShortCircuitOwnershipOnlyPreservesStorageWhenTheMoveIsDefinitelySkipped(
        string expression,
        bool accepted)
    {
        var source = $$"""
            operation Guard(): bool { return true; }
            operation Consume(move values: int[]): bool { return values[0] == 1; }
            operation Main(): int {
                var values: int[] = [1];
                var result: bool = {{expression}};
                return values[0];
            }
            """;
        if (accepted)
            Compiler.Accepts(source);
        else
            AssertRejected(source, "QSEM039");
    }

    public static IEnumerable<object[]> EffectfulValueContexts()
    {
        foreach (var op in new[] { "&&", "||" })
        {
            foreach (var context in new[] { "initializer", "assignment", "array-literal", "array-write", "argument", "return" })
                yield return [op, context];
        }
    }

    [Theory]
    [MemberData(nameof(EffectfulValueContexts))]
    public void EffectfulRightOperandIsOnlyInTheRequiredBranch(string op, string context)
    {
        var expression = $"guard {op} Touch(q[0])";
        var body = context switch
        {
            "initializer" => $"var value: bool = {expression}; return value;",
            "assignment" => $"var value: bool = false; value = {expression}; return value;",
            "array-literal" => $"var values: bool[] = [{expression}]; return values[0];",
            "array-write" => $"var values: bool[] = [false]; values[0] = {expression}; return values[0];",
            "argument" => $"return Echo({expression});",
            "return" => $"return {expression};",
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };
        var compilation = Compiler.Compile($$"""
            function Echo(value: bool): bool { return value; }
            operation Touch(q: Qubit): bool { X(q); return true; }
            operation Main(): bool {
                use q = Qubit[1];
                var guard: bool = false;
                {{body}}
            }
            """);
        AssertSucceeded(compilation);
        var program = Assert.IsType<MirSnapshot>(compilation.Mir).Program;
        Assert.Empty(QoraMirVerifier.Verify(program));
        var main = program.EntryPoint;
        var branch = Assert.IsType<MirBranch>(main.EntryBlock.Terminator);
        Assert.Empty(main.EntryBlock.Instructions.OfType<MirQuantumApply>());
        var rhsId = op == "&&" ? branch.TrueTarget : branch.FalseTarget;
        var mergeId = op == "&&" ? branch.FalseTarget : branch.TrueTarget;
        var rhs = Assert.IsType<MirBlock>(main.FindBlock(rhsId));
        var merge = Assert.IsType<MirBlock>(main.FindBlock(mergeId));
        var call = Assert.Single(rhs.Instructions.OfType<MirQuantumApply>());
        var target = Assert.IsType<MirUserCallableTarget>(call.Target);
        Assert.Equal("Touch", program.Callables.Single(callable => callable.Id == target.Callable).Name);
        Assert.Single(main.Blocks.SelectMany(block => block.Instructions).OfType<MirQuantumApply>());
        Assert.Empty(merge.Instructions.OfType<MirQuantumApply>());
        var jump = Assert.IsType<MirJump>(rhs.Terminator);
        Assert.Equal(merge.Id, jump.Target);
        Assert.Equal(MirType.Scalar(QType.Bool), merge.Arguments[0].Type);
        Assert.Equal(Assert.IsType<MirValue>(call.Result).Id, jump.Arguments[0]);
        Assert.Equal(branch.Condition, (op == "&&" ? branch.FalseArguments : branch.TrueArguments)[0]);
    }

    private static string Shape(HirExpression expression) => expression switch
    {
        HirIntegerLiteralExpression literal => literal.Value.ToString(),
        HirNameExpression name => name.Name,
        HirUnaryExpression unary => $"{unary.Operator}({Shape(unary.Operand)})",
        HirBinaryExpression binary => $"{binary.Operator}({Shape(binary.Left)},{Shape(binary.Right)})",
        _ => throw new InvalidOperationException($"Unexpected expression node {expression.GetType().Name}"),
    };

    private static void AssertSucceeded(Compilation compilation) =>
        Assert.True(compilation.Succeeded, string.Join(" | ", compilation.Diagnostics.Select(
            diagnostic => $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));

    private static void AssertRejected(string source, string expectedCode)
    {
        var compilation = Compiler.Compile(source);
        Assert.False(compilation.Succeeded, $"Expected {expectedCode}, but compiled:\n{source}");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Error.Code == expectedCode);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code is "QINTERNAL" or "QORA0000");
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }
}
