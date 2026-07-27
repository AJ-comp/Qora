using Qora.Compiler;

namespace Qora.Tests;

public sealed class CompilationReportsTests
{
    [Fact]
    public void ReportsConsumeOneExactSemanticArtifact()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        Assert.True(compilation.Succeeded);

        var validation = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);
        var effects = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);

        Assert.Contains("Main: operation", CompilationReports.FormatSymbols(validation));
        Assert.Contains("q: register", CompilationReports.FormatSymbols(effects));
        Assert.Contains("cleanup candidate", CompilationReports.FormatUncompute(effects));
        Assert.Throws<ArgumentException>(
            () => CompilationReports.FormatUncompute(validation));
    }
}
