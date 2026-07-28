using Qora.Compiler;
using Qora.Ir;

namespace Qora.Tests;

public class HirConstructionSessionTests
{
    [Fact]
    public void SnapshotRejectsANodeFromAnUnpublishedForeignRewriteSession()
    {
        var hir = new HirTestFactory();
        var program = hir.PublishProgram(
            new[]
            {
                hir.Callable(
                    "Main",
                    body: new HirStatement[]
                    {
                        hir.Apply("X", hir.Index("q", 0)),
                    }),
            });
        var builder = hir.CreatePipelineBuilder();
        var source = builder.PublishLowered(program);
        var mixed = hir.RewriteWithUnpublishedForeignNode(source);

        var error = Assert.Throws<ArgumentException>(
            () => builder.Advance(
                HirStage.ImportsExpanded,
                mixed));

        Assert.Contains("has not published its result", error.Message);
    }
}
