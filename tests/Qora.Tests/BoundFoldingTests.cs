using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>The symbolic integer folder keeps an unknown array length tied to the array's semantic identity.
/// Source spelling is diagnostic text only: two shadowing arrays named <c>xs</c> are unrelated values.</summary>
public class BoundFoldingTests
{
    [Fact]
    public void DoesNotCombineCountsFromDifferentSameNamedSymbols()
    {
        var graph = new ProgramSymbolGraph();
        var outerScope = new Scope(graph, graph.RootSymbol.Id);
        var outerArray = new Symbol(
            "xs",
            SymbolKind.Parameter,
            QType.Int,
            isArray: true,
            ownerSymbolId: graph.RootSymbol.Id);
        Assert.True(outerScope.TryAdd(outerArray));

        var outerLength = BoundFolder.Fold(
            new QMember(new QNameRef("xs"), "Count"),
            outerScope);
        Assert.IsType<ArrayLengthBound>(outerLength);

        var innerScope = new Scope(outerScope);
        var outerLengthAlias = new Symbol(
            "outerLength",
            SymbolKind.Const,
            QType.Int,
            isConst: true,
            ownerSymbolId: graph.RootSymbol.Id)
        {
            FoldedBound = outerLength,
        };
        var innerArray = new Symbol(
            "xs",
            SymbolKind.Var,
            QType.Int,
            isArray: true,
            ownerSymbolId: graph.RootSymbol.Id);
        Assert.True(innerScope.TryAdd(outerLengthAlias));
        Assert.True(innerScope.TryAdd(innerArray));
        Assert.NotEqual(outerArray.Id, innerArray.Id);

        var difference = BoundFolder.Fold(
            new QBinOp(
                "-",
                new QNameRef("outerLength"),
                new QMember(new QNameRef("xs"), "Count")),
            innerScope);

        Assert.Null(difference);
    }
}
