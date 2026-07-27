using Janglim.FrontEnd;
using Janglim.FrontEnd.Ast;
using Janglim.FrontEnd.Parsers.LR;
using Janglim.FrontEnd.ParseTree;
using Janglim.FrontEnd.Tokenize;
using Qora.Compiler;
using Qora.Ir;

namespace Qora;

/// <summary>One lexed token: the matched text and the terminal it was recognized as.</summary>
public sealed record QoraToken(string Text, string Type);

/// <summary>
/// One diagnostic with an optional, revision-qualified source range. <see cref="Start"/> and
/// <see cref="End"/> remain convenience views for CLI/editor serialization and are -1 when no source
/// location is available.
/// </summary>
public sealed record QoraError(
    string Message,
    string Code,
    SourceSpan? Span = null)
{
    public int Start => Span?.Start ?? -1;
    public int End => Span?.End ?? -1;

    public override string ToString() =>
        Span is { } span
            ? $"{Message} ({Code} @ {span})"
            : $"{Message} ({Code})";
}

/// <summary>
/// The transient parser-to-lowering hand-off. The immutable snapshot owns only Qora projections, while
/// the parser-engine AST remains confined to this internal product and is discarded after immediate HIR
/// lowering.
/// </summary>
internal sealed class SyntaxParseProduct
{
    public SyntaxParseProduct(
        SyntaxSnapshot snapshot,
        AstSymbol? loweringAst)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        LoweringAst = loweringAst;
    }

    public SyntaxSnapshot Snapshot { get; }
    public AstSymbol? LoweringAst { get; }
}

/// <summary>
/// Qora's syntax front end. Public results are immutable Qora-owned projections rather than parser-engine
/// objects. This type stops at the syntax boundary; whole-program compilation starts at
/// <see cref="QoraCompiler"/>. Keeping those entry points separate prevents parser results from becoming a
/// bag of unrelated HIR, semantic, MIR, and target generations.
/// </summary>
public static class QoraParser
{
    /// <summary>
    /// Parse one source document into an immutable syntax snapshot without running imports, semantic
    /// analysis, MIR, or a backend.
    /// </summary>
    public static SyntaxSnapshot Parse(string source, string? sourcePath = null) =>
        ParseProduct(source, sourcePath).Snapshot;

    /// <summary>
    /// Parse one standalone document and retain the parser AST only in the internal transient product used
    /// by immediate HIR lowering and lowering-focused tests.
    /// </summary>
    internal static SyntaxParseProduct ParseProduct(
        string source,
        string? sourcePath = null)
    {
        var compilationId = CompilationId.New();
        SyntaxParseProduct? parsed = null;
        Exception? failure = null;
        var worker = new Thread(
            () =>
            {
                try
                {
                    parsed = ParseOnCurrentThread(
                        new SourceDocumentSnapshot(
                            new SourceDocumentRef(
                                compilationId,
                                new CompilationRevision(0),
                                new SourceDocumentId(0)),
                            source ?? string.Empty,
                            sourcePath));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            maxStackSize: 64 * 1024 * 1024);

        worker.Start();
        worker.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return parsed!;
    }

    /// <summary>
    /// Parse on the caller's stack. The compiler invokes this from its own wide-stack worker so parsing and
    /// all recursive HIR passes share one guarded execution boundary.
    /// </summary>
    internal static SyntaxParseProduct ParseOnCurrentThread(SourceDocumentSnapshot document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var grammar = new QoraGrammar();
        var lexer = new Lexer();
        foreach (var terminal in grammar.TerminalSet)
            lexer.AddTokenRule(terminal);

        var tokens = lexer.Lexing(document.Text).TokensForParsing;
        var result = new LALRParser(grammar, bLogging: false).Parsing(tokens);
        var hasTree = result.Success && result.Count > 0;
        var parseTree = hasTree ? result.ToParseTree : null;
        var loweringAst = hasTree ? result.AstRoot : null;
        var snapshot = new SyntaxSnapshot(
            document,
            tokens.Select(token => new QoraToken(
                token.Data,
                token.PatternInfo?.Terminal?.ToString() ?? "?")),
            parseTree is null ? null : ProjectParseTree(parseTree),
            loweringAst is null ? null : ProjectAst(loweringAst),
            parseTree?.ToTreeString() ?? string.Empty,
            loweringAst?.ToTreeString() ?? string.Empty,
            result.Success
                ? Array.Empty<QoraError>()
                : result.AllErrors.Select(error => ToQoraError(error, document.Ref)));

        return new SyntaxParseProduct(snapshot, loweringAst);
    }

    private static SyntaxTreeNode ProjectAst(AstSymbol node) =>
        node is AstNonTerminal nonTerminal
            ? new SyntaxTreeNode(
                SyntaxTreeNodeKind.NonTerminal,
                nonTerminal.Name ?? nonTerminal.ToString() ?? string.Empty,
                nonTerminal.Items.Select(ProjectAst))
            : new SyntaxTreeNode(
                SyntaxTreeNodeKind.Terminal,
                node.ToString() ?? string.Empty,
                Array.Empty<SyntaxTreeNode>());

    private static SyntaxTreeNode ProjectParseTree(ParseTreeSymbol node) =>
        node is ParseTreeNonTerminal nonTerminal
            ? new SyntaxTreeNode(
                SyntaxTreeNodeKind.NonTerminal,
                nonTerminal.ToString() ?? string.Empty,
                nonTerminal.Items.Select(ProjectParseTree))
            : new SyntaxTreeNode(
                SyntaxTreeNodeKind.Terminal,
                node.ToString() ?? string.Empty,
                Array.Empty<SyntaxTreeNode>());

    private static QoraError ToQoraError(
        ParsingErrorInfo error,
        SourceDocumentRef document)
    {
        var token = error.ErrTokens.FirstOrDefault();
        var span = token is not null && token.StartIndex >= 0
            ? new SourceSpan(
                document,
                token.StartIndex,
                token.EndIndex + 1)
            : (SourceSpan?)null;
        return new QoraError(error.Message, error.Code, span);
    }
}
