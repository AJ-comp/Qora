namespace Qora.Tests;

/// <summary>Import-graph expansion across real files.</summary>
public class ModuleLoaderTests
{
    [Fact]
    public void CyclicBackEdgeIsSkippedAndEachFileIsMergedOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qora-module-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var aPath = Path.Combine(dir, "a.qor");
            var bPath = Path.Combine(dir, "b.qor");
            var aSource = """
                import "b.qor";
                operation Main() { FromB(); }
                operation FromA() { }
                """;
            var bSource = """
                import "a.qor";
                operation FromB() { FromA(); }
                """;

            File.WriteAllText(aPath, aSource);
            File.WriteAllText(bPath, bSource);

            var result = QoraParser.Parse(aSource, dir, aPath);

            Assert.True(result.Success,
                string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
            Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM021");

            var operations = Assert.IsAssignableFrom<IReadOnlyList<Ir.QOperation>>(result.Ir?.Operations);
            Assert.Equal(3, operations.Count);
            Assert.Single(operations, operation => operation.Name == "Main");
            Assert.Single(operations, operation => operation.Name == "FromA");
            Assert.Single(operations, operation => operation.Name == "FromB");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
