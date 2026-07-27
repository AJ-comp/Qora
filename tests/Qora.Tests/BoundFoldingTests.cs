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
        var graph = new HirScopeGraph();
        var outerScope = graph.CreateScope(
            HirScopeKind.Callable,
            graph.RootScope.Id);
        var outerArray = new Symbol(
            outerScope.Id,
            "xs",
            SymbolKind.Parameter,
            QType.Int,
            isArray: true);
        Assert.True(outerScope.TryAdd(outerArray));

        var outerLength = BoundFolder.Fold(
            new QMember(new QNameRef("xs"), "Count"),
            outerScope);
        Assert.IsType<ArrayLengthBound>(outerLength);

        var innerScope = graph.CreateScope(
            HirScopeKind.Block,
            outerScope.Id);
        var outerLengthAlias = new Symbol(
            innerScope.Id,
            "outerLength",
            SymbolKind.Const,
            QType.Int,
            isConst: true)
        {
            FoldedBound = outerLength,
        };
        var innerArray = new Symbol(
            innerScope.Id,
            "xs",
            SymbolKind.Var,
            QType.Int,
            isArray: true);
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
