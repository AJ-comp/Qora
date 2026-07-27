using System.Collections.Frozen;
using System.Collections.Immutable;
using Qora.Ir;

namespace Qora.Compiler;

/// <summary>
/// The syntax-front-end result for a complete import graph. Per-document HIR is an internal hand-off to
/// module merging; the public source snapshot remains syntax-owned and does not absorb HIR facts.
/// </summary>
internal sealed class LoadedSourceGraph
{
    private readonly FrozenDictionary<SourceDocumentRef, QProgram> _loweredDocuments;

    public LoadedSourceGraph(
        SourceSetSnapshot sources,
        IReadOnlyDictionary<SourceDocumentRef, QProgram> loweredDocuments,
        IEnumerable<QoraError> importDiagnostics)
    {
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentNullException.ThrowIfNull(loweredDocuments);
        ArgumentNullException.ThrowIfNull(importDiagnostics);

        _loweredDocuments = loweredDocuments.ToFrozenDictionary();
        foreach (var document in _loweredDocuments.Keys)
            if (!sources.Contains(document))
                throw new ArgumentException(
                    $"Lowered document {document} is absent from the source set.",
                    nameof(loweredDocuments));

        ImportDiagnostics = importDiagnostics.ToImmutableArray();
    }

    public SourceSetSnapshot Sources { get; }
    public IReadOnlyDictionary<SourceDocumentRef, QProgram> LoweredDocuments =>
        _loweredDocuments;
    public IReadOnlyList<QoraError> ImportDiagnostics { get; }
    public QProgram? EntryProgram =>
        _loweredDocuments.GetValueOrDefault(Sources.Entry);
}

/// <summary>
/// Reads and parses every document in one import graph. It is the only front-end component allowed to
/// perform import I/O; downstream module expansion consumes the resulting immutable graph and document
/// programs without reading files or reparsing text.
/// </summary>
internal static class SourceGraphLoader
{
    public static LoadedSourceGraph Load(
        string entryText,
        CompilationOptions options,
        CompilationId compilationId,
        CompilationRevision compilationRevision)
    {
        ArgumentNullException.ThrowIfNull(options);
        var documents = new List<SourceDocumentSnapshot>();
        var syntax = new List<SyntaxSnapshot>();
        var lowered = new Dictionary<SourceDocumentRef, QProgram>();
        var edges = new List<ImportEdge>();
        var importDiagnostics = new List<QoraError>();
        var canonicalDocuments = new Dictionary<string, SourceDocumentRef>(
            SourcePathComparer);
        var nextDocument = 0;

        SourceDocumentSnapshot AddDocument(
            string text,
            string? path)
        {
            var reference = new SourceDocumentRef(
                compilationId,
                compilationRevision,
                new SourceDocumentId(nextDocument++));
            var document = new SourceDocumentSnapshot(reference, text, path);
            documents.Add(document);

            var parseProduct = QoraParser.ParseOnCurrentThread(document);
            syntax.Add(parseProduct.Snapshot);
            if (parseProduct.Snapshot.Succeeded
                && parseProduct.LoweringAst is { } ast)
            {
                var program = QoraLowering.Lower(ast, reference)
                    ?? throw new InvalidOperationException(
                        $"QINTERNAL: lowering syntax for {reference} produced no HIR program.");
                lowered.Add(reference, program);
            }

            return document;
        }

        var entryPath = CanonicalOrOriginal(options.SourcePath);
        var entry = AddDocument(entryText ?? string.Empty, entryPath);
        if (entryPath is not null)
            canonicalDocuments.TryAdd(entryPath, entry.Ref);

        var rootDirectory = options.BaseDirectory;
        if (rootDirectory is null && entryPath is not null)
            rootDirectory = Path.GetDirectoryName(entryPath);
        rootDirectory = CanonicalOrOriginal(rootDirectory);

        if (lowered.TryGetValue(entry.Ref, out var entryProgram))
            LoadImports(entry, entryProgram, rootDirectory);

        var imports = new ImportGraph(
            documents.Select(document => document.Ref),
            edges);
        var sources = new SourceSetSnapshot(
            compilationId,
            compilationRevision,
            entry.Ref,
            documents,
            syntax,
            imports);
        return new LoadedSourceGraph(
            sources,
            lowered,
            importDiagnostics);

        void LoadImports(
            SourceDocumentSnapshot importer,
            QProgram program,
            string? directory)
        {
            if (program.Imports is not { Count: > 0 })
                return;

            foreach (var import in program.Imports)
            {
                var directive = import.Span
                    ?? throw new InvalidOperationException(
                        $"QINTERNAL: source import `{import.Target}` has no document span.");

                if (directory is null)
                {
                    edges.Add(new ImportEdge(
                        importer.Ref,
                        imported: null,
                        import.Target,
                        resolvedPath: null,
                        directive));
                    importDiagnostics.Add(new QoraError(
                        $"`import {import.Display};` cannot be resolved here: imports require a source path or base directory",
                        "QSEM020",
                        directive));
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(
                        Path.Combine(directory, import.Target));
                }
                catch (Exception)
                {
                    edges.Add(new ImportEdge(
                        importer.Ref,
                        imported: null,
                        import.Target,
                        resolvedPath: null,
                        directive));
                    importDiagnostics.Add(new QoraError(
                        $"`import {import.Display};` is not a usable path (`{import.Target}`)",
                        "QSEM020",
                        directive));
                    continue;
                }

                if (canonicalDocuments.TryGetValue(fullPath, out var existing))
                {
                    edges.Add(new ImportEdge(
                        importer.Ref,
                        existing,
                        import.Target,
                        fullPath,
                        directive));
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    edges.Add(new ImportEdge(
                        importer.Ref,
                        imported: null,
                        import.Target,
                        fullPath,
                        directive));
                    importDiagnostics.Add(new QoraError(
                        $"`import {import.Display};` file not found: {fullPath}",
                        "QSEM020",
                        directive));
                    continue;
                }

                string importedText;
                try
                {
                    importedText = File.ReadAllText(fullPath);
                }
                catch (Exception exception)
                {
                    edges.Add(new ImportEdge(
                        importer.Ref,
                        imported: null,
                        import.Target,
                        fullPath,
                        directive));
                    importDiagnostics.Add(new QoraError(
                        $"`import {import.Display};` cannot read {fullPath}: {exception.Message}",
                        "QSEM020",
                        directive));
                    continue;
                }

                var imported = AddDocument(importedText, fullPath);

                // Register before descending. A recursive back-edge and a diamond edge therefore both
                // resolve to this exact document snapshot without reading or parsing it again.
                canonicalDocuments.Add(fullPath, imported.Ref);
                edges.Add(new ImportEdge(
                    importer.Ref,
                    imported.Ref,
                    import.Target,
                    fullPath,
                    directive));

                if (lowered.TryGetValue(imported.Ref, out var importedProgram))
                {
                    LoadImports(
                        imported,
                        importedProgram,
                        Path.GetDirectoryName(fullPath));
                }
            }
        }
    }

    private static string? CanonicalOrOriginal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static StringComparer SourcePathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
