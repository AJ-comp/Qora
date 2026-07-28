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

            var result = QoraCompiler.Compile(
                aSource,
                new CompilationOptions(dir, aPath));

            Assert.True(result.Succeeded,
                string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
            Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM021");

            var operations = Assert.IsAssignableFrom<IReadOnlyList<Ir.HirCallable>>(
                result.Hir.Resolved!.Program.Callables);
            Assert.Equal(3, operations.Count);
            Assert.Single(operations, operation => operation.Name == "Main");
            Assert.Single(operations, operation => operation.Name == "FromA");
            Assert.Single(operations, operation => operation.Name == "FromB");
            Assert.Equal(2, result.Sources.Documents.Count);
            Assert.Equal(2, result.Sources.Imports.Edges.Count);
            Assert.All(
                result.Sources.Imports.Edges,
                edge => Assert.True(edge.IsResolved));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RootAndImportedSyntaxSnapshotsRemainIndependentlyQueryable()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qora-source-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var rootPath = Path.Combine(dir, "main.qor");
            var libraryPath = Path.Combine(dir, "library.qor");
            var rootSource = """
                import "library.qor";
                operation Main() { Library(); }
                """;
            var librarySource = "operation Library() { }";
            File.WriteAllText(rootPath, rootSource);
            File.WriteAllText(libraryPath, librarySource);

            var compilation = QoraCompiler.Compile(
                rootSource,
                new CompilationOptions(dir, rootPath));

            Assert.True(compilation.Succeeded);
            Assert.Equal(2, compilation.Sources.Documents.Count);
            Assert.Equal(rootSource, compilation.Sources.EntrySyntax.Document.Text);

            var imported = Assert.Single(
                compilation.Sources.Documents,
                document => document.Ref != compilation.Sources.Entry);
            Assert.Equal(
                Path.GetFullPath(libraryPath),
                imported.Path);
            var importedSyntax = compilation.Sources.RequireSyntax(imported.Ref);
            Assert.Equal(librarySource, importedSyntax.Document.Text);
            Assert.NotNull(importedSyntax.Ast);

            var edge = Assert.Single(compilation.Sources.Imports.Edges);
            Assert.Equal(compilation.Sources.Entry, edge.Importer);
            Assert.Equal(imported.Ref, edge.Imported);
            Assert.Equal("library.qor", edge.Target);

            var resolved = compilation.Hir.Resolved!;
            var library = Assert.Single(
                resolved.Program.Callables,
                operation => operation.Name == "Library");
            Assert.Equal(
                imported.Ref,
                resolved.SourceMap.Require(library.Id).Document);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiamondGraphKeepsBothEdgesButLoadsSharedDocumentOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qora-source-diamond-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var rootPath = Path.Combine(dir, "main.qor");
            var rootSource = """
                import "a.qor";
                import "b.qor";
                operation Main() { A(); B(); }
                """;
            File.WriteAllText(rootPath, rootSource);
            File.WriteAllText(
                Path.Combine(dir, "a.qor"),
                "import \"shared.qor\"; operation A() { Shared(); }");
            File.WriteAllText(
                Path.Combine(dir, "b.qor"),
                "import \"shared.qor\"; operation B() { Shared(); }");
            File.WriteAllText(
                Path.Combine(dir, "shared.qor"),
                "operation Shared() { }");

            var compilation = QoraCompiler.Compile(
                rootSource,
                new CompilationOptions(dir, rootPath));

            Assert.True(compilation.Succeeded);
            Assert.Equal(4, compilation.Sources.Documents.Count);
            Assert.Equal(4, compilation.Sources.Imports.Edges.Count);

            var shared = Assert.Single(
                compilation.Sources.Documents,
                document => Path.GetFileName(document.Path) == "shared.qor");
            Assert.Equal(
                2,
                compilation.Sources.Imports.Edges.Count(
                    edge => edge.Imported == shared.Ref));
            Assert.Single(
                compilation.Hir.Resolved!.Program.Callables,
                operation => operation.Name == "Shared");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImportedParseDiagnosticKeepsImportedDocumentAndSpan()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qora-import-parse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var rootPath = Path.Combine(dir, "main.qor");
            var importedPath = Path.Combine(dir, "broken.qor");
            var rootSource = "import \"broken.qor\"; operation Main() { }";
            File.WriteAllText(rootPath, rootSource);
            File.WriteAllText(
                importedPath,
                "operation Broken() { var x: int = ; }");

            var compilation = QoraCompiler.Compile(
                rootSource,
                new CompilationOptions(dir, rootPath));

            Assert.False(compilation.Succeeded);
            var imported = Assert.Single(
                compilation.Sources.Documents,
                document => Path.GetFileName(document.Path) == "broken.qor");
            var diagnostic = Assert.Single(
                compilation.Diagnostics,
                item => item.Stage == CompilationStage.Syntax);
            var origin = Assert.IsType<DiagnosticOrigin.Source>(diagnostic.Origin);
            Assert.Equal(imported.Ref, origin.Document);
            Assert.Equal(imported.Ref, diagnostic.Error.Span?.Document);
            Assert.True(diagnostic.Error.Start >= 0);

            var wrongOrigin = new CompilationDiagnostic(
                CompilationStage.Syntax,
                diagnostic.Error,
                new DiagnosticOrigin.Source(compilation.Sources.Entry));
            Assert.Throws<ArgumentException>(
                () => new Compilation(
                    compilation.Id,
                    compilation.Revision,
                    compilation.Session,
                    compilation.ParentRevision,
                    compilation.Options,
                    compilation.Sources,
                    compilation.Hir,
                    compilation.Mir,
                    compilation.Links,
                    compilation.Targets,
                    new[] { wrongOrigin }));

            var hirOrigin = new CompilationDiagnostic(
                CompilationStage.Syntax,
                diagnostic.Error,
                new DiagnosticOrigin.Hir(
                    compilation.Hir.ImportsExpanded!.Id));
            Assert.Throws<ArgumentException>(
                () => new Compilation(
                    compilation.Id,
                    compilation.Revision,
                    compilation.Session,
                    compilation.ParentRevision,
                    compilation.Options,
                    compilation.Sources,
                    compilation.Hir,
                    compilation.Mir,
                    compilation.Links,
                    compilation.Targets,
                    new[] { hirOrigin }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImportedSemanticDiagnosticKeepsImportedSourceSpan()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qora-import-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var rootPath = Path.Combine(dir, "main.qor");
            var importedPath = Path.Combine(dir, "broken.qor");
            var rootSource = "import \"broken.qor\"; operation Main() { }";
            File.WriteAllText(rootPath, rootSource);
            File.WriteAllText(
                importedPath,
                "operation Broken() { Missing(); }");

            var compilation = QoraCompiler.Compile(
                rootSource,
                new CompilationOptions(dir, rootPath));

            Assert.False(compilation.Succeeded);
            var imported = Assert.Single(
                compilation.Sources.Documents,
                document => Path.GetFileName(document.Path) == "broken.qor");
            var diagnostic = Assert.Single(
                compilation.Diagnostics,
                item => item.Stage == CompilationStage.HirValidation);
            Assert.IsType<DiagnosticOrigin.Hir>(diagnostic.Origin);
            Assert.Equal(imported.Ref, diagnostic.Error.Span?.Document);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
