using Janglim.FrontEnd.Ast;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// An index is a normal expression in every surface context. These tests stop at lowering so the
/// backend's policy for an index whose range cannot be proven does not obscure the front-end shape.
/// </summary>
public class GeneralIndexExpressionLoweringTests
{
    [Fact]
    public void LowersAFunctionCallIndexThroughEveryIndexedContext()
    {
        const string source = """
            function idx(): int {
                return 0;
            }

            operation Main() {
                use q = Qubit[2];
                var xs: int[] = [10, 20];
                xs[idx()] = 30;
                H(q[idx()]);
                var measured: bit = M(q[idx()]);
                var value: int = xs[idx()];
            }
            """;

        var parseProduct = QoraParser.ParseProduct(source);
        var snapshot = parseProduct.Snapshot;
        Assert.DoesNotContain(snapshot.Diagnostics, error => error.Code == "CE0001");

        var ast = Assert.IsAssignableFrom<AstSymbol>(parseProduct.LoweringAst);
        var program = new HirTestFactory(snapshot.Document.Ref)
            .Lower(ast);
        var main = Assert.Single(program.Callables, operation => operation.Name == "Main");

        var write = Assert.IsType<HirAssignmentStatement>(main.Body[2]);
        var writeTarget = Assert.IsType<HirIndexExpression>(write.Target);
        Assert.Equal(
            "xs",
            Assert.IsType<HirNameExpression>(
                writeTarget.Receiver).Name);
        AssertIndexCall(writeTarget.Index);

        var gate = Assert.IsType<HirCallStatement>(main.Body[3]);
        var gateTarget = Assert.IsType<HirIndexExpression>(
            Assert.Single(gate.Call.Arguments).Expression);
        Assert.Equal(
            "q",
            Assert.IsType<HirNameExpression>(
                gateTarget.Receiver).Name);
        AssertIndexCall(gateTarget.Index);

        var measurementDecl =
            Assert.IsType<HirVariableDeclarationStatement>(
                main.Body[4]);
        var measurement =
            Assert.IsType<HirMeasurementExpression>(
                measurementDecl.Value);
        var measuredTarget = Assert.IsType<HirIndexExpression>(
            measurement.Target);
        Assert.Equal(
            "q",
            Assert.IsType<HirNameExpression>(
                measuredTarget.Receiver).Name);
        AssertIndexCall(measuredTarget.Index);

        var readDecl = Assert.IsType<HirVariableDeclarationStatement>(
            main.Body[5]);
        var read = Assert.IsType<HirIndexExpression>(
            readDecl.Value);
        Assert.Equal(
            "xs",
            Assert.IsType<HirNameExpression>(
                read.Receiver).Name);
        AssertIndexCall(read.Index);
    }

    [Fact]
    public void PreservesTheWholeArithmeticIndexExpression()
    {
        const string source = """
            function idx(): int {
                return 0;
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var value: int = xs[idx() + 1];
            }
            """;

        var parseProduct = QoraParser.ParseProduct(source);
        var snapshot = parseProduct.Snapshot;
        Assert.DoesNotContain(snapshot.Diagnostics, error => error.Code == "CE0001");

        var ast = Assert.IsAssignableFrom<AstSymbol>(parseProduct.LoweringAst);
        var program = new HirTestFactory(snapshot.Document.Ref)
            .Lower(ast);
        var main = Assert.Single(program.Callables, operation => operation.Name == "Main");
        var declaration =
            Assert.IsType<HirVariableDeclarationStatement>(
                main.Body[1]);
        var read = Assert.IsType<HirIndexExpression>(
            declaration.Value);

        var plus = Assert.IsType<HirBinaryExpression>(read.Index);
        Assert.Equal(HirBinaryOperator.Add, plus.Operator);
        AssertIndexCall(plus.Left);
        Assert.Equal(
            1,
            Assert.IsType<HirIntegerLiteralExpression>(
                plus.Right).Value);
    }

    private static void AssertIndexCall(HirExpression? node)
    {
        var call = Assert.IsType<HirCallExpression>(node);
        Assert.Equal("idx", HirExpressions.QualifiedNameOf(call.Callee));
        Assert.Empty(call.Arguments);
    }
}
