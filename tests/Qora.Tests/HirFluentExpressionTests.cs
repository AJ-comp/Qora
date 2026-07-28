using Qora.Ir;

namespace Qora.Tests;

public sealed class HirFluentExpressionTests
{
    [Fact]
    public void AddBuildsThroughTheOperandsActiveConstructionSession()
    {
        var hir = new HirTestFactory();
        var left = hir.Integer(1);
        var right = hir.Integer(2);

        var sum = Assert.IsType<HirBinaryExpression>(
            left.Add(right));

        Assert.Equal(HirBinaryOperator.Add, sum.Operator);
        Assert.Same(left, sum.Left);
        Assert.Same(right, sum.Right);
    }

    [Fact]
    public void AddFailsAfterTheOwningSessionPublishes()
    {
        var hir = new HirTestFactory();
        var left = hir.Integer(1);
        var right = hir.Integer(2);
        var sum = Assert.IsType<HirBinaryExpression>(
            left.Add(right));
        hir.PublishProgram(
            new[]
            {
                hir.Callable(
                    "Main",
                    body: new HirStatement[]
                    {
                        hir.Variable(
                            "sum",
                            sum,
                            QType.Int),
                    }),
            });

        var error = Assert.Throws<InvalidOperationException>(
            () => left.Add(right));

        Assert.Contains("already published", error.Message);
    }

    [Fact]
    public void AddRejectsAnOperandFromAnotherActiveSession()
    {
        var hir = new HirTestFactory();
        var left = hir.Integer(1);
        var foreignRight = hir.IntegerFromSiblingSession(2);

        var error = Assert.Throws<ArgumentException>(
            () => left.Add(foreignRight));

        Assert.Contains("another document session", error.Message);
    }
}
