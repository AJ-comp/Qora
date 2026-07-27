using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Qora.Compiler;

/// <summary>Identity of a source document within one compilation.</summary>
public readonly record struct SourceDocumentId
{
    public SourceDocumentId(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"d{Value}";
}

/// <summary>
/// A globally unambiguous reference to one source document inside one immutable compilation revision.
/// </summary>
public readonly record struct SourceDocumentRef(
    CompilationId CompilationId,
    CompilationRevision CompilationRevision,
    SourceDocumentId DocumentId)
{
    public override string ToString() =>
        $"{CompilationId}@{CompilationRevision}/{DocumentId}";
}

/// <summary>An immutable source document snapshot.</summary>
public sealed class SourceDocumentSnapshot
{
    internal SourceDocumentSnapshot(
        SourceDocumentRef reference,
        string text,
        string? path)
    {
        if (reference.CompilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "A source document requires a non-empty Compilation identity.",
                nameof(reference));

        Ref = reference;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Path = path;
    }

    public SourceDocumentRef Ref { get; }
    public SourceDocumentId Id => Ref.DocumentId;
    public string Text { get; }
    public string? Path { get; }
}

/// <summary>
/// The stable node kinds exposed by Qora's syntax snapshots. These values describe structure without
/// leaking the parser engine's mutable node hierarchy into the public compiler model.
/// </summary>
public enum SyntaxTreeNodeKind
{
    NonTerminal,
    Terminal,
}

/// <summary>
/// One immutable, parser-engine-independent syntax-tree node. A node captures its display label and
/// children when the syntax snapshot is created, so later mutation of a parser-owned tree cannot alter a
/// published compilation revision.
/// </summary>
public sealed class SyntaxTreeNode
{
    internal SyntaxTreeNode(
        SyntaxTreeNodeKind kind,
        string label,
        IEnumerable<SyntaxTreeNode> children)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(children);

        Kind = kind;
        Label = label;
        Children = children.ToImmutableArray();

        if (kind == SyntaxTreeNodeKind.Terminal && Children.Length != 0)
        {
            throw new ArgumentException(
                "A terminal syntax-tree node cannot contain children.",
                nameof(children));
        }
    }

    public SyntaxTreeNodeKind Kind { get; }
    public string Label { get; }
    public ImmutableArray<SyntaxTreeNode> Children { get; }
}

/// <summary>
/// The syntax result for one exact source document. It contains only lexer/parser products; HIR, semantic facts,
/// MIR, and target output belong to later compilation stages.
/// </summary>
public sealed class SyntaxSnapshot
{
    private readonly IReadOnlyList<QoraToken> _tokens;
    private readonly IReadOnlyList<QoraError> _diagnostics;

    internal SyntaxSnapshot(
        SourceDocumentSnapshot document,
        IEnumerable<QoraToken> tokens,
        SyntaxTreeNode? parseTree,
        SyntaxTreeNode? ast,
        string parseTreeText,
        string astText,
        IEnumerable<QoraError> diagnostics)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _tokens = Array.AsReadOnly(tokens.ToArray());
        ParseTree = parseTree;
        Ast = ast;
        ParseTreeText = parseTreeText
            ?? throw new ArgumentNullException(nameof(parseTreeText));
        AstText = astText
            ?? throw new ArgumentNullException(nameof(astText));
        _diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        foreach (var diagnostic in _diagnostics)
        {
            if (diagnostic.Span is not { } span)
                continue;
            if (span.Document != document.Ref)
                throw new ArgumentException(
                    $"Syntax diagnostic span {span.Document} does not belong to document {document.Ref}.",
                    nameof(diagnostics));
            if (span.End > document.Text.Length)
                throw new ArgumentException(
                    $"Syntax diagnostic span {span} exceeds its document length.",
                    nameof(diagnostics));
        }
    }

    public SourceDocumentSnapshot Document { get; }
    public IReadOnlyList<QoraToken> Tokens => _tokens;
    public SyntaxTreeNode? ParseTree { get; }
    public SyntaxTreeNode? Ast { get; }
    public IReadOnlyList<QoraError> Diagnostics => _diagnostics;
    public bool Succeeded => Diagnostics.Count == 0;
    public string ParseTreeText { get; }
    public string AstText { get; }
}

/// <summary>
/// One source-level import directive. Resolved edges name the exact imported document; unresolved edges
/// are retained with a null <see cref="Imported"/> so tooling can still inspect the attempted graph.
/// </summary>
public sealed record ImportEdge
{
    internal ImportEdge(
        SourceDocumentRef importer,
        SourceDocumentRef? imported,
        string target,
        string? resolvedPath,
        SourceSpan directive)
    {
        if (directive.Document != importer)
            throw new ArgumentException(
                "An import directive span must belong to its importing document.",
                nameof(directive));

        Importer = importer;
        Imported = imported;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ResolvedPath = resolvedPath;
        Directive = directive;
    }

    public SourceDocumentRef Importer { get; }
    public SourceDocumentRef? Imported { get; }
    public string Target { get; }
    public string? ResolvedPath { get; }
    public SourceSpan Directive { get; }
    public bool IsResolved => Imported is not null;
}

/// <summary>
/// Immutable ordered import graph. Edge order is source order within the loader's deterministic
/// depth-first discovery and is therefore also the authority used by module merging.
/// </summary>
public sealed class ImportGraph
{
    private readonly FrozenSet<SourceDocumentRef> _documents;
    private readonly ImmutableArray<ImportEdge> _edges;
    private readonly FrozenDictionary<SourceDocumentRef, IReadOnlyList<ImportEdge>> _outgoing;

    internal ImportGraph(
        IEnumerable<SourceDocumentRef> documents,
        IEnumerable<ImportEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(edges);

        var documentRefs = documents.ToImmutableArray();
        var known = documentRefs.ToHashSet();
        if (known.Count != documentRefs.Length)
            throw new ArgumentException(
                "An import graph cannot contain duplicate source-document references.",
                nameof(documents));
        _documents = known.ToFrozenSet();
        _edges = edges.ToImmutableArray();
        var outgoing = new Dictionary<SourceDocumentRef, List<ImportEdge>>();

        foreach (var edge in _edges)
        {
            if (!known.Contains(edge.Importer))
                throw new ArgumentException(
                    $"Import edge names unknown importer {edge.Importer}.",
                    nameof(edges));
            if (edge.Imported is { } imported && !known.Contains(imported))
                throw new ArgumentException(
                    $"Import edge names unknown imported document {imported}.",
                    nameof(edges));

            if (!outgoing.TryGetValue(edge.Importer, out var list))
                outgoing.Add(edge.Importer, list = new List<ImportEdge>());
            list.Add(edge);
        }

        _outgoing = outgoing.ToFrozenDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ImportEdge>)pair.Value.ToImmutableArray());
    }

    public IReadOnlySet<SourceDocumentRef> Documents => _documents;
    public IReadOnlyList<ImportEdge> Edges => _edges;

    public IReadOnlyList<ImportEdge> Outgoing(SourceDocumentRef importer)
    {
        if (!_documents.Contains(importer))
        {
            throw new ArgumentOutOfRangeException(
                nameof(importer),
                importer,
                "the importer does not belong to this import graph");
        }

        return _outgoing.GetValueOrDefault(importer)
            ?? Array.Empty<ImportEdge>();
    }
}

/// <summary>
/// The complete immutable source and syntax input owned by one compilation revision. Every imported
/// document remains independently queryable instead of disappearing into a merged HIR tree.
/// </summary>
public sealed class SourceSetSnapshot
{
    private readonly ImmutableArray<SourceDocumentSnapshot> _documents;
    private readonly FrozenDictionary<SourceDocumentRef, SourceDocumentSnapshot> _documentsByRef;
    private readonly FrozenDictionary<SourceDocumentRef, SyntaxSnapshot> _syntaxByDocument;

    internal SourceSetSnapshot(
        CompilationId compilationId,
        CompilationRevision compilationRevision,
        SourceDocumentRef entry,
        IEnumerable<SourceDocumentSnapshot> documents,
        IEnumerable<SyntaxSnapshot> syntax,
        ImportGraph imports)
    {
        if (compilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "A source set requires a non-empty Compilation identity.",
                nameof(compilationId));

        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(syntax);
        CompilationId = compilationId;
        CompilationRevision = compilationRevision;
        Entry = entry;
        Imports = imports ?? throw new ArgumentNullException(nameof(imports));

        _documents = documents.ToImmutableArray();
        if (_documents.Length == 0)
            throw new ArgumentException(
                "A source set requires an entry document.",
                nameof(documents));
        _documentsByRef = _documents.ToFrozenDictionary(document => document.Ref);
        _syntaxByDocument = syntax.ToFrozenDictionary(tree => tree.Document.Ref);

        foreach (var document in _documents)
        {
            if (document.Ref.CompilationId != compilationId
                || document.Ref.CompilationRevision != compilationRevision)
            {
                throw new ArgumentException(
                    $"Source document {document.Ref} belongs to a different Compilation snapshot.",
                    nameof(documents));
            }
            if (!_syntaxByDocument.TryGetValue(document.Ref, out var tree)
                || !ReferenceEquals(tree.Document, document))
            {
                throw new ArgumentException(
                    $"Source document {document.Ref} has no exact syntax snapshot.",
                    nameof(syntax));
            }
        }

        if (_syntaxByDocument.Count != _documents.Length)
            throw new ArgumentException(
                "Syntax snapshots must cover exactly the source documents.",
                nameof(syntax));
        if (!_documentsByRef.ContainsKey(entry))
            throw new ArgumentException(
                "The entry reference does not belong to this source set.",
                nameof(entry));
        if (!Imports.Documents.SetEquals(_documentsByRef.Keys))
            throw new ArgumentException(
                "The import graph must cover exactly the source documents in this source set.",
                nameof(imports));

        foreach (var edge in Imports.Edges)
        {
            if (!_documentsByRef.ContainsKey(edge.Importer)
                || edge.Imported is { } imported && !_documentsByRef.ContainsKey(imported))
            {
                throw new ArgumentException(
                    "The import graph contains a document outside this source set.",
                    nameof(imports));
            }
            if (edge.Directive.End > _documentsByRef[edge.Importer].Text.Length)
                throw new ArgumentException(
                    $"Import directive span {edge.Directive} exceeds its document length.",
                    nameof(imports));
        }
    }

    public CompilationId CompilationId { get; }
    public CompilationRevision CompilationRevision { get; }
    public SourceDocumentRef Entry { get; }
    public IReadOnlyList<SourceDocumentSnapshot> Documents => _documents;
    public IReadOnlyDictionary<SourceDocumentRef, SyntaxSnapshot> SyntaxByDocument =>
        _syntaxByDocument;
    public ImportGraph Imports { get; }
    public SyntaxSnapshot EntrySyntax => RequireSyntax(Entry);

    public SourceDocumentSnapshot? FindDocument(SourceDocumentRef reference) =>
        _documentsByRef.GetValueOrDefault(reference);

    public SourceDocumentSnapshot RequireDocument(SourceDocumentRef reference) =>
        FindDocument(reference)
        ?? throw new ArgumentOutOfRangeException(
            nameof(reference),
            reference,
            "the source document does not belong to this source set");

    public SyntaxSnapshot? FindSyntax(SourceDocumentRef reference) =>
        _syntaxByDocument.GetValueOrDefault(reference);

    public SyntaxSnapshot RequireSyntax(SourceDocumentRef reference) =>
        FindSyntax(reference)
        ?? throw new ArgumentOutOfRangeException(
            nameof(reference),
            reference,
            "the syntax snapshot does not belong to this source set");

    public bool Contains(SourceDocumentRef reference) =>
        _documentsByRef.ContainsKey(reference);
}
