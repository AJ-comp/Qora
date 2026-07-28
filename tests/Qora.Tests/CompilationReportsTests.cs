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
        Assert.Contains("Main: callable", CompilationReports.FormatSymbols(validation));
    }
}
