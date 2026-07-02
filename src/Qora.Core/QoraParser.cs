using System.Collections.Generic;
using System.Linq;
using Janglim.FrontEnd;
using Janglim.FrontEnd.Ast;
using Janglim.FrontEnd.Parsers.LR;
using Janglim.FrontEnd.ParseTree;
using Janglim.FrontEnd.Tokenize;
using Qora.Ir;

namespace Qora;

/// <summary>One lexed token: the matched text and the terminal it was recognized as.</summary>
public sealed record QoraToken(string Text, string Type);

/// <summary>
/// One parse error with a source span, ready for an editor squiggle. <see cref="Start"/> and
/// <see cref="End"/> are half-open character offsets into the source (End exclusive), so an editor can
/// map them straight to a range. Both are -1 when the error has no located token (e.g. an internal or
/// virtual token) — a consumer should then fall back to a whole-document / first-line marker.
/// </summary>
public sealed record QoraError(string Message, string Code, int Start, int End)
{
    public override string ToString()
        => Start >= 0 ? $"{Message} ({Code} @ {Start}..{End})" : $"{Message} ({Code})";
}

/// <summary>The outcome of parsing a Qora source string.</summary>
public sealed class QoraParseResult
{
    public bool Success { get; init; }
    public IReadOnlyList<QoraToken> Tokens { get; init; } = new List<QoraToken>();
    public string TreeText { get; init; } = string.Empty;
    /// <summary>The parse-tree root, for visual rendering (null when parsing failed).</summary>
    public ParseTreeSymbol? Tree { get; init; }
    /// <summary>The semantic AST root (MeaningUnit-tagged; null when parsing failed).</summary>
    public AstSymbol? Ast { get; init; }
    public string AstText { get; init; } = string.Empty;
    /// <summary>The lowered IR (null when parsing failed) — present even when semantic errors block emission.</summary>
    public Ir.QProgram? Ir { get; init; }
    /// <summary>The AST emitted as OpenQASM 3.0 (empty when parsing failed).</summary>
    public string Qasm { get; init; } = string.Empty;
    /// <summary>Parse errors, each carrying a source span (see <see cref="QoraError"/>); empty on success.</summary>
    public IReadOnlyList<QoraError> Errors { get; init; } = new List<QoraError>();
}

/// <summary>
/// The front door for Qora: source string in, parse result out. A fresh grammar/lexer/parser
/// is built per call (cheap for a playground, and sidesteps any shared parser state).
/// </summary>
public static class QoraParser
{
    public static QoraParseResult Parse(string source)
    {
        source ??= string.Empty;

        var grammar = new QoraGrammar();
        var lexer = new Lexer();
        foreach (var terminal in grammar.TerminalSet) lexer.AddTokenRule(terminal);

        var tokens = lexer.Lexing(source).TokensForParsing;
        var result = new LALRParser(grammar, bLogging: false).Parsing(tokens);

        // On success the tree/AST are usually present, but an empty (or whitespace-only) source parses
        // to zero blocks — and the engine's AstRoot getter calls Last() with no Count guard, so it throws
        // on that empty-but-successful case. Gate every derived value on there actually being content.
        var hasParse = result.Success && result.Count > 0;
        var tree = hasParse ? result.ToParseTree : null;
        var ast = hasParse ? result.AstRoot : null;

        // Pipeline: AST → Lower → IR → validation pass → (only when clean) emit. Semantic errors gate
        // emission: an invalid program reports every violation and produces no QASM, so the emitter only
        // ever runs on validated IR.
        var ir = ast != null ? QoraLowering.Lower(ast) : null;
        var semanticErrors = QoraValidator.Validate(ir);

        return new QoraParseResult
        {
            Success = result.Success && semanticErrors.Count == 0,
            Tokens = tokens
                .Select(c => new QoraToken(c.Data, c.PatternInfo?.Terminal?.ToString() ?? "?"))
                .ToList(),
            TreeText = tree?.ToTreeString() ?? string.Empty,
            Tree = tree,
            Ast = ast,
            AstText = ast?.ToTreeString() ?? string.Empty,
            Ir = ir,
            Qasm = ir != null && semanticErrors.Count == 0 ? QasmEmitter.Emit(ir) : string.Empty,
            Errors = !result.Success
                ? result.AllErrors.Select(ToQoraError).ToList()
                : semanticErrors,
        };
    }

    /// <summary>Map an engine <see cref="ParsingErrorInfo"/> to a positioned <see cref="QoraError"/>.</summary>
    private static QoraError ToQoraError(ParsingErrorInfo error)
    {
        var token = error.ErrTokens.FirstOrDefault();

        // TokenData.EndIndex is the inclusive last-char offset; +1 makes it half-open for an editor range.
        // A virtual/unlocated token reports StartIndex == -1, which we pass through as "no span".
        var (start, end) = (token != null && token.StartIndex >= 0)
            ? (token.StartIndex, token.EndIndex + 1)
            : (-1, -1);

        return new QoraError(error.Message, error.Code, start, end);
    }
}
