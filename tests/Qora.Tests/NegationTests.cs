using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// Logical negation is verified through the target expression dependency graph. SSA may split one source
/// condition across temporaries, but the typed logical-not operator and its grouping must remain unchanged.
/// </summary>
public class NegationTests
{
    [Fact]
    public void NotOnMeasuredBitComparisonRemainsTypedLogicalNot() =>
        AssertConditionContainsLogicalNot(
            "operation Main(){ use q=Qubit[1]; var r: bit = M(q[0]); if(!(r == 1)){ X(q[0]); } }");

    [Fact]
    public void NotOnIntegerComparisonRemainsTypedLogicalNot() =>
        AssertConditionContainsLogicalNot(
            "operation Main(){ use q=Qubit[1]; var n: int = 0; if(!(n != 0)){ X(q[0]); } }");

    [Fact]
    public void NotOnLoopVariableComparisonRemainsTypedLogicalNot() =>
        AssertConditionContainsLogicalNot(
            "operation Main(){ use q=Qubit[1]; for i in 0..2 { if(!(i != 0)){ X(q[0]); } } }");

    [Fact]
    public void LoopVarEqualityRemainsIntegerEquality()
    {
        var (body, conditions) = Conditions(
            "operation Main(){ use q=Qubit[1]; for i in 0..2 { if(i==1){ X(q[0]); } } }");

        Assert.Contains(
            conditions,
            condition => ContainsEqualityTo(body, condition, "1"));
        Assert.DoesNotContain(
            conditions,
            condition => ContainsEqualityTo(body, condition, "true"));
    }

    [Fact]
    public void NotOnQubitIsRejected() =>
        Compiler.Rejects(
            "operation Main(){ use q=Qubit[1]; if(!q){ X(q[0]); } }",
            "QSEM026");

    [Fact]
    public void NegatedBoolCannotBecomeARelationalNumericOperand() =>
        Compiler.Rejects(
            "operation Main(){ use q=Qubit[1]; var a: bool = false; if (!a < 2) { X(q[0]); } }",
            "QSEM041");

    [Fact]
    public void NegationAsTheRightOperandOfEqualityKeepsItsDependencyGrouping()
    {
        var (body, conditions) = Conditions(
            "operation Main(){ use q=Qubit[1]; var n: bool = true; var m: bool = false; if (n == !m) { X(q[0]); } }");
        var outer = conditions
            .SelectMany(condition => body.DependencyClosure(condition))
            .OfType<MirQasmBinaryExpression>()
            .First(
                expression =>
                    expression.Operator == MirQasmBinaryOperator.Equal
                    && ContainsLogicalNot(body, expression.Right));

        Assert.True(ContainsLogicalNot(body, outer.Right));
    }

    private static void AssertConditionContainsLogicalNot(string source)
    {
        var (body, conditions) = Conditions(source);
        Assert.Contains(
            conditions,
            condition => ContainsLogicalNot(body, condition));
    }

    private static bool ContainsLogicalNot(
        IReadOnlyList<MirQasmStatement> body,
        MirQasmExpression expression) =>
        body.DependencyClosure(expression)
            .OfType<MirQasmUnaryExpression>()
            .Any(unary => unary.Operator == MirQasmUnaryOperator.LogicalNot);

    private static bool ContainsEqualityTo(
        IReadOnlyList<MirQasmStatement> body,
        MirQasmExpression expression,
        string literal) =>
        body.DependencyClosure(expression)
            .OfType<MirQasmBinaryExpression>()
            .Any(
                binary =>
                    binary.Operator == MirQasmBinaryOperator.Equal
                    && (body.DependsOn(
                            binary.Left,
                            candidate =>
                                candidate
                                    is MirQasmLiteralExpression
                                    {
                                        Text: var text,
                                    }
                                && text == literal)
                        || body.DependsOn(
                            binary.Right,
                            candidate =>
                                candidate
                                    is MirQasmLiteralExpression
                                    {
                                        Text: var text,
                                    }
                                && text == literal)));

    private static (
        IReadOnlyList<MirQasmStatement> Body,
        IReadOnlyList<MirQasmExpression> Conditions) Conditions(
        string source)
    {
        var artifact = MirQasmTestModel.Compile(source);
        var body = artifact.Program.EntryBody;
        var conditions = MirQasmTestModel
            .Statements(body)
            .Select(
                statement => statement switch
                {
                    MirQasmIfStatement branch => branch.Condition,
                    MirQasmWhileStatement loop => loop.Condition,
                    _ => null,
                })
            .Where(condition => condition is not null)
            .Cast<MirQasmExpression>()
            .ToArray();
        return (body, conditions);
    }
}
