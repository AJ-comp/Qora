using Qora.Ir;

namespace Qora.Tests;

public sealed class OpenQasmTargetFactsTests
{
    [Fact]
    public void ReturnFlatteningOwnsSynthesizedTypesAndExactHirOrigins()
    {
        var compilation = Compiler.Compile(
            """
            function Identity(value: int): int {
                return value;
            }

            operation Main() {
                var result: int = Identity(7);
            }
            """);

        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));

        var source = compilation.Hir.Require(HirStage.AdjointMaterialized);
        var sourceFunction = Assert.Single(
            source.Program.Operations,
            operation => operation.Name == "Identity");
        var sourceReturn = Assert.IsType<QReturn>(
            Assert.Single(sourceFunction.Body));

        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        Assert.Equal(source.Id, artifact.Source);

        var resultStorage = Assert.Single(
            artifact.Program.Facts.SynthesizedNodes.Values,
            fact => fact.Kind == OpenQasmSynthesisKind.ReturnResultStorage);
        Assert.Equal(sourceFunction.Id, resultStorage.SourceHirNodeId);
        Assert.Equal(
            new OpenQasmClassicalType(QType.Int, isArray: false),
            resultStorage.DeclaredType);
        Assert.Equal(
            resultStorage.DeclaredType,
            artifact.Program.Types.GetType(resultStorage.NodeId));

        var valueAssignment = Assert.Single(
            artifact.Program.Facts.SynthesizedNodes.Values,
            fact => fact.Kind == OpenQasmSynthesisKind.ReturnValueAssignment);
        Assert.Equal(sourceReturn.Id, valueAssignment.SourceHirNodeId);
        Assert.Null(valueAssignment.DeclaredType);

        var finalReturn = Assert.Single(
            artifact.Program.Facts.SynthesizedNodes.Values,
            fact => fact.Kind == OpenQasmSynthesisKind.FinalReturn);
        Assert.Equal(sourceFunction.Id, finalReturn.SourceHirNodeId);
        Assert.Null(finalReturn.DeclaredType);

        Assert.Contains("return ret;", artifact.Text);
    }
}
