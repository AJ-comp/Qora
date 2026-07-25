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

        var parsed = QoraParser.Parse(source);
        Assert.DoesNotContain(parsed.Errors, error => error.Code == "CE0001");

        var ast = Assert.IsAssignableFrom<AstSymbol>(parsed.Ast);
        var program = Assert.IsType<QProgram>(QoraLowering.Lower(ast));
        var main = Assert.Single(program.Operations, operation => operation.Name == "Main");

        var write = Assert.IsType<QAssign>(main.Body[2]);
        Assert.Equal("xs", write.Name);
        AssertIndexCall(write.Index);

        var gate = Assert.IsType<QGate>(main.Body[3]);
        var gateTarget = Assert.IsType<QQubitArg>(Assert.Single(gate.Args));
        Assert.Equal("q", gateTarget.Reg);
        AssertIndexCall(gateTarget.Index);

        var measurementDecl = Assert.IsType<QDecl>(main.Body[4]);
        var measurement = Assert.IsType<QMeasure>(measurementDecl.Value);
        var measuredTarget = Assert.IsType<QIndexNode>(measurement.Target);
        Assert.Equal("q", Assert.IsType<QNameRef>(measuredTarget.Base).Name);
        AssertIndexCall(measuredTarget.Index);

        var readDecl = Assert.IsType<QDecl>(main.Body[5]);
        var read = Assert.IsType<QIndexNode>(Assert.IsType<QText>(readDecl.Value).Tree);
        Assert.Equal("xs", Assert.IsType<QNameRef>(read.Base).Name);
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

        var parsed = QoraParser.Parse(source);
        Assert.DoesNotContain(parsed.Errors, error => error.Code == "CE0001");

        var ast = Assert.IsAssignableFrom<AstSymbol>(parsed.Ast);
        var program = Assert.IsType<QProgram>(QoraLowering.Lower(ast));
        var main = Assert.Single(program.Operations, operation => operation.Name == "Main");
        var declaration = Assert.IsType<QDecl>(main.Body[1]);
        var read = Assert.IsType<QIndexNode>(Assert.IsType<QText>(declaration.Value).Tree);

        var plus = Assert.IsType<QBinOp>(read.Index);
        Assert.Equal("+", plus.Op);
        AssertIndexCall(plus.Left);
        Assert.Equal(1, Assert.IsType<QNumLit>(plus.Right).Value);
    }

    private static void AssertIndexCall(QNode? node)
    {
        var call = Assert.IsType<QCallNode>(node);
        Assert.Equal("idx", call.Name);
        Assert.Empty(call.Args);
    }
}
