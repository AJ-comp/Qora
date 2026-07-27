using Qora.Compiler;

namespace Qora;

/// <summary>
/// A half-open character range in one exact source-document revision. A span from an earlier
/// recompilation cannot be mistaken for a location in the current text because the document reference
/// carries both the logical compilation identity and its immutable revision.
/// </summary>
public readonly record struct SourceSpan
{
    public SourceSpan(
        SourceDocumentRef document,
        int start,
        int end)
    {
        if (document.CompilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "A source span requires a revision-qualified document reference.",
                nameof(document));
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (end < start)
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "A source span end must not precede its start.");

        Document = document;
        Start = start;
        End = end;
    }

    public SourceDocumentRef Document { get; }
    public int Start { get; }
    public int End { get; }

    public override string ToString() => $"{Document}:{Start}..{End}";
}
