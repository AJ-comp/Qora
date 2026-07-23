using Janglim.FrontEnd.Ast;
using Qora.Ir;

namespace Qora.Tests;

public class QoraLoweringTests
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
        var result = QoraParser.Parse(Source);
        var ast = Assert.IsAssignableFrom<AstSymbol>(result.Ast);
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
        var result = QoraParser.Parse(Source);
        var program = Assert.IsType<QProgram>(QoraLowering.Lower(result.Ast));
        var probe = Assert.Single(program.Operations, o => o.Name == "Probe");
        var main = Assert.Single(program.Operations, o => o.Name == "Main");

        Assert.Collection(
            probe.Params,
            p => AssertParam(p, "q", QType.Qubit, isArray: false),
            p => AssertParam(p, "work", QType.Qubit, isArray: true),
            p => AssertParam(p, "counts", QType.Int, isArray: true),
            p => AssertParam(p, "weight", QType.Float, isArray: false));

        var use = Assert.IsType<QUse>(Assert.Single(main.Body));
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

        var result = QoraParser.Parse(source);
        var ast = Assert.IsAssignableFrom<AstSymbol>(result.Ast);
        var nodes = Descendants(ast).OfType<AstNonTerminal>().ToList();
        var accesses = nodes.Where(n => n.Name == "IndexAccess").ToList();

        Assert.Collection(
            accesses,
            access => Assert.Equal(new[] { "results", "0" }, Leaves(access)),
            access => Assert.Equal(new[] { "q", "0" }, Leaves(access)));
        Assert.DoesNotContain(nodes, n => n.Name == "Qubit");
        Assert.Contains(Descendants(ast).OfType<AstTerminal>(), t => t.ToString() == "Qubit");

        var program = Assert.IsType<QProgram>(QoraLowering.Lower(ast));
        var main = Assert.Single(program.Operations);
        var assignment = Assert.IsType<QAssign>(main.Body[2]);
        var measurement = Assert.IsType<QMeasure>(assignment.Value);

        Assert.Equal("results", assignment.Name);
        Assert.Equal(new QNumLit(0), assignment.Index);   // the index atom is settled at lowering
        // the measure target is the IR's canonical reference form (QIndexNode for a register element)
        Assert.Equal("q", QNodes.RegOf(measurement.Target));
        Assert.Equal(new QNumLit(0), QNodes.IndexOf(measurement.Target));
    }

    private static void AssertParam(QParam param, string name, QType type, bool isArray)
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
