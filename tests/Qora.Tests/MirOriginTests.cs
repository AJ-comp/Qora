using Qora.Compiler;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MirOriginTests
{
    [Fact]
    public void ReplayPreservesOriginValuesAndAppendsAfterTheReplayedTable()
    {
        var source = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
            }
            """);
        Assert.NotEmpty(source.Origins.Origins);

        var firstBuilder = new MirOriginTableBuilder(
            source.LoweringSource.Source);
        firstBuilder.Replay(source.Origins);

        var firstParent = new MirOriginId(0);
        var synthesized = firstBuilder.Synthesized(
            firstParent,
            "origin replay test");
        Assert.Equal(source.Origins.Origins.Count, synthesized.Value);
        var expanded = firstBuilder.Build();

        var secondBuilder = new MirOriginTableBuilder(
            source.LoweringSource.Source);
        secondBuilder.Replay(expanded);
        var replayed = secondBuilder.Build();

        Assert.Equal(expanded.Origins.Count, replayed.Origins.Count);
        for (var index = 0; index < expanded.Origins.Count; index++)
        {
            var expected = expanded.Origins[index];
            var actual = replayed.Origins[index];

            Assert.Equal(expected.HirNodeId, actual.HirNodeId);
            Assert.Equal(expected.Span, actual.Span);
            Assert.Equal(expected.SynthesisReason, actual.SynthesisReason);
            Assert.Equal(expected.Parent?.Value, actual.Parent?.Value);
        }

        var replayedSynthesized = secondBuilder.Synthesized(
            firstParent,
            "origin replay test");
        Assert.Equal(synthesized.Value, replayedSynthesized.Value);

        var appended = secondBuilder.Synthesized(
            replayedSynthesized,
            "appended after replay");
        Assert.Equal(expanded.Origins.Count, appended.Value);
    }

    [Fact]
    public void TransformationPreservesExistingOriginIdsAndRejectsMissingParents()
    {
        var source = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                X(q[0]);
            }
            """);
        var transformation = new MirSnapshotTransformation(source);
        var replayed = transformation.BuildOrigins();

        for (var value = 0; value < source.Origins.Origins.Count; value++)
        {
            var originId = new MirOriginId(value);

            Assert.Equal(
                source.Origins.Require(originId),
                replayed.Require(originId));
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => transformation.Synthesize(
                new MirOriginId(source.Origins.Origins.Count),
                "missing parent"));
    }

    private static MirSnapshot CompileMir(string source)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return Assert.IsType<MirSnapshot>(compilation.Mir);
    }
}
