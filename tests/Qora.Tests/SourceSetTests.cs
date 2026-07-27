using Qora.Compiler;

namespace Qora.Tests;

public sealed class SourceSetTests
{
    [Fact]
    public void SourceSetRejectsAnImportGraphWithAStaleExtraDocument()
    {
        var compilation = QoraCompiler.Compile("operation Main() { }");
        Assert.True(compilation.Succeeded);

        var sources = compilation.Sources;
        var stale = new SourceDocumentRef(
            compilation.Id,
            compilation.Revision,
            new SourceDocumentId(99));
        var graph = new ImportGraph(
            sources.Documents.Select(document => document.Ref).Append(stale),
            Array.Empty<ImportEdge>());

        Assert.Throws<ArgumentException>(
            () => new SourceSetSnapshot(
                compilation.Id,
                compilation.Revision,
                sources.Entry,
                sources.Documents,
                sources.SyntaxByDocument.Values,
                graph));
    }

    [Fact]
    public void MissingImportRemainsAnUnresolvedSourceGraphEdge()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"qora-missing-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var entryPath = Path.Combine(directory, "main.qor");
            const string source =
                "import \"missing.qor\"; operation Main() { }";
            File.WriteAllText(entryPath, source);

            var compilation = QoraCompiler.Compile(
                source,
                new CompilationOptions(directory, entryPath));

            Assert.False(compilation.Succeeded);
            Assert.Single(compilation.Sources.Documents);

            var edge = Assert.Single(compilation.Sources.Imports.Edges);
            Assert.False(edge.IsResolved);
            Assert.Null(edge.Imported);
            Assert.Equal(compilation.Sources.Entry, edge.Importer);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(directory, "missing.qor")),
                edge.ResolvedPath);
            Assert.Equal(compilation.Sources.Entry, edge.Directive.Document);

            var diagnostic = Assert.Single(
                compilation.Diagnostics,
                item => item.Error.Code == "QSEM020");
            Assert.Equal(CompilationStage.ImportExpansion, diagnostic.Stage);
            Assert.Equal(
                compilation.Sources.Entry,
                Assert.IsType<DiagnosticOrigin.Source>(diagnostic.Origin).Document);
            Assert.Equal(
                compilation.Sources.Entry,
                diagnostic.Error.Span?.Document);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
