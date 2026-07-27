using System.Reflection;
using Janglim.FrontEnd.Ast;
using Qora.Compiler;

namespace Qora.Tests;

public class SyntaxSnapshotImmutabilityTests
{
    [Fact]
    public void SnapshotDoesNotRetainParserAstAndProjectionIgnoresTransientAstMutation()
    {
        var parseProduct = QoraParser.ParseProduct(
            "operation Main() { use q = Qubit[1]; H(q[0]); }");
        var snapshot = parseProduct.Snapshot;

        var publicAst = Assert.IsType<SyntaxTreeNode>(snapshot.Ast);
        var publicParseTree = Assert.IsType<SyntaxTreeNode>(snapshot.ParseTree);
        var originalAstText = snapshot.AstText;
        var originalParseTreeText = snapshot.ParseTreeText;
        var originalAstLabel = publicAst.Label;
        var originalAstChildren = publicAst.Children;
        var originalParseTreeLabel = publicParseTree.Label;
        var originalParseTreeChildren = publicParseTree.Children;
        Assert.NotEmpty(originalAstChildren);
        Assert.NotEmpty(originalParseTreeChildren);

        var janglimAssembly = typeof(AstSymbol).Assembly;
        Assert.DoesNotContain(
            typeof(SyntaxSnapshot).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType.Assembly == janglimAssembly);
        Assert.DoesNotContain(
            typeof(SyntaxSnapshot).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            property => property.PropertyType.Assembly == janglimAssembly);

        var loweringAst = Assert.IsType<AstNonTerminal>(parseProduct.LoweringAst);
        loweringAst.Clear();

        Assert.Same(publicAst, snapshot.Ast);
        Assert.Same(publicParseTree, snapshot.ParseTree);
        Assert.Equal(originalAstText, snapshot.AstText);
        Assert.Equal(originalParseTreeText, snapshot.ParseTreeText);
        Assert.Equal(originalAstLabel, snapshot.Ast!.Label);
        Assert.Equal(originalAstChildren, snapshot.Ast.Children);
        Assert.Equal(originalParseTreeLabel, snapshot.ParseTree!.Label);
        Assert.Equal(originalParseTreeChildren, snapshot.ParseTree.Children);
    }
}
