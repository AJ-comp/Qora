using Janglim.FrontEnd.Ast;
using Qora.Ir;

namespace Qora.Tests;

public class AstToHirLoweringTests
{
    private const string Source = """
        operation Probe(q: Qubit, work: Qubit[], counts: int[], weight: float) { }
        operation Main() {
            use work = Qubit[3];
        }
        """;

    [Fact]
    public void QubitTypeTokenRemainsInSemanticAst()
    {
        var parseProduct = QoraParser.ParseProduct(Source);
        var ast = Assert.IsAssignableFrom<AstSymbol>(parseProduct.LoweringAst);
        var nodes = Descendants(ast).OfType<AstNonTerminal>().ToList();
        var parameters = nodes.Where(n => n.Name == "Param").ToList();
        var use = Assert.Single(nodes, n => n.Name == "Use");

        // Trailing annotation (name: T): the name leaf comes first, then the type token (the `:` is
        // excluded from the AST, so the order-independent lowering reads the same shape as before).
        Assert.Collection(
            parameters,
            p => Assert.Equal(new[] { "q", "Qubit" }, Leaves(p)),
            p => Assert.Equal(new[] { "work", "Qubit" }, Leaves(p)),
            p => Assert.Equal(new[] { "counts", "int" }, Leaves(p)),
            p => Assert.Equal(new[] { "weight", "float" }, Leaves(p)));
        Assert.DoesNotContain(parameters[0].Items.OfType<AstNonTerminal>(), n => n.Name == "ArrayType");
        Assert.Contains(parameters[1].Items.OfType<AstNonTerminal>(), n => n.Name == "ArrayType");
        Assert.Contains(parameters[2].Items.OfType<AstNonTerminal>(), n => n.Name == "ArrayType");
        Assert.DoesNotContain(parameters[3].Items.OfType<AstNonTerminal>(), n => n.Name == "ArrayType");
        Assert.Equal(new[] { "work", "Qubit", "3" }, Leaves(use));
    }

    [Fact]
    public void RetainedQubitTypeTokenLowersToTheSameIrShape()
    {
        var parseProduct = QoraParser.ParseProduct(Source);
        var ast = Assert.IsAssignableFrom<AstSymbol>(
            parseProduct.LoweringAst);
        var program = new HirTestFactory(
                parseProduct.Snapshot.Document.Ref)
            .Lower(ast);
        var probe = Assert.Single(program.Callables, o => o.Name == "Probe");
        var main = Assert.Single(program.Callables, o => o.Name == "Main");

        Assert.Collection(
            probe.Parameters,
            p => AssertParam(p, "q", QType.Qubit, isArray: false),
            p => AssertParam(p, "work", QType.Qubit, isArray: true),
            p => AssertParam(p, "counts", QType.Int, isArray: true),
            p => AssertParam(p, "weight", QType.Float, isArray: false));

        var use = Assert.IsType<HirQubitDeclarationStatement>(
            Assert.Single(main.Body));
        Assert.Equal("work", use.Name);
        Assert.Equal(3, use.Size);
    }

    [Fact]
    public void IndexedReferencesUseTypeNeutralSemanticAstNode()
    {
        const string source = """
            operation Main() {
                use q = Qubit[1];
                var results: bit[] = new bit[1];
                results[0] = M(q[0]);
            }
            """;

        var parseProduct = QoraParser.ParseProduct(source);
        var ast = Assert.IsAssignableFrom<AstSymbol>(parseProduct.LoweringAst);
        var nodes = Descendants(ast).OfType<AstNonTerminal>().ToList();
        var accesses = nodes.Where(n => n.Name == "IndexAccess").ToList();

        Assert.Collection(
            accesses,
            access => Assert.Equal(new[] { "results", "0" }, Leaves(access)),
            access => Assert.Equal(new[] { "q", "0" }, Leaves(access)));
        Assert.DoesNotContain(nodes, n => n.Name == "Qubit");
        Assert.Contains(Descendants(ast).OfType<AstTerminal>(), t => t.ToString() == "Qubit");

        var program = new HirTestFactory(
                parseProduct.Snapshot.Document.Ref)
            .Lower(ast);
        var main = Assert.Single(program.Callables);
        var assignment = Assert.IsType<HirAssignmentStatement>(
            main.Body[2]);
        var writeTarget = Assert.IsType<HirIndexExpression>(
            assignment.Target);
        var measurement = Assert.IsType<HirMeasurementExpression>(
            assignment.Value);

        Assert.Equal(
            "results",
            Assert.IsType<HirNameExpression>(
                writeTarget.Receiver).Name);
        Assert.Equal(
            0,
            Assert.IsType<HirIntegerLiteralExpression>(
                writeTarget.Index).Value);
        var measuredTarget = Assert.IsType<HirIndexExpression>(
            measurement.Target);
        Assert.Equal(
            "q",
            Assert.IsType<HirNameExpression>(
                measuredTarget.Receiver).Name);
        Assert.Equal(
            0,
            Assert.IsType<HirIntegerLiteralExpression>(
                measuredTarget.Index).Value);
    }

    [Fact]
    public void DottedNamespaceLowersToAnAuthoritativeDeclarationTree()
    {
        const string source = """
            namespace A.B {
                open C;

                function f(): int {
                    return 1;
                }

                operation Work() {
                }
            }

            operation Main() {
            }
            """;

        var parseProduct = QoraParser.ParseProduct(source);
        var ast = Assert.IsAssignableFrom<AstSymbol>(
            parseProduct.LoweringAst);
        var program = new HirTestFactory(
                parseProduct.Snapshot.Document.Ref)
            .Lower(ast);

        var a = Assert.Single(
            program.Declarations.OfType<HirNamespaceDeclaration>());
        Assert.Equal("A", a.Name);
        var b = Assert.Single(
            a.Declarations.OfType<HirNamespaceDeclaration>());
        Assert.Equal("B", b.Name);
        var open = Assert.Single(b.OpenDirectives);
        Assert.Equal("C", open.Target);

        Assert.Collection(
            b.Declarations.OfType<HirCallable>(),
            function =>
            {
                Assert.Equal("f", function.Name);
                Assert.True(function.IsFunction);
            },
            operation =>
            {
                Assert.Equal("Work", operation.Name);
                Assert.False(operation.IsFunction);
            });

        var function = Assert.Single(
            program.Callables,
            callable => callable.Name == "f");
        var operation = Assert.Single(
            program.Callables,
            callable => callable.Name == "Work");
        Assert.Same(
            b.Declarations[0],
            function);
        Assert.Same(
            b.Declarations[1],
            operation);
        Assert.Equal("A.B", program.NamespaceOf(function));
        Assert.Equal("A.B", program.NamespaceOf(operation));
        Assert.Same(
            open,
            Assert.Single(
                program.OpenDirectivesByNamespace["A.B"]));
    }

    private static void AssertParam(
        HirParameter param,
        string name,
        QType type,
        bool isArray)
    {
        Assert.Equal(name, param.Name);
        Assert.Equal(type, param.Type);
        Assert.Null(param.RegisterSize);
        Assert.Equal(isArray, param.IsArray);
        Assert.Equal(type == QType.Qubit && isArray, param.IsQubitArray);
    }

    private static IEnumerable<AstSymbol> Descendants(AstSymbol node)
    {
        yield return node;
        if (node is not AstNonTerminal nonTerminal) yield break;

        foreach (var child in nonTerminal.Items)
        foreach (var descendant in Descendants(child))
            yield return descendant;
    }

    private static string[] Leaves(AstNonTerminal node) =>
        Descendants(node)
            .OfType<AstTerminal>()
            .Select(t => t.ToString() ?? string.Empty)
            .ToArray();
}
