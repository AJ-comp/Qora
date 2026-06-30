using System.Collections.Generic;
using System.Linq;
using Janglim.FrontEnd.Ast;
using Janglim.FrontEnd.Parsers.LR;
using Janglim.FrontEnd.ParseTree;
using Janglim.FrontEnd.Tokenize;

namespace Ket;

/// <summary>One lexed token: the matched text and the terminal it was recognized as.</summary>
public sealed record KetToken(string Text, string Type);

/// <summary>The outcome of parsing a Ket source string.</summary>
public sealed class KetParseResult
{
    public bool Success { get; init; }
    public IReadOnlyList<KetToken> Tokens { get; init; } = new List<KetToken>();
    public string TreeText { get; init; } = string.Empty;
    /// <summary>The parse-tree root, for visual rendering (null when parsing failed).</summary>
    public ParseTreeSymbol? Tree { get; init; }
    /// <summary>The semantic AST root (MeaningUnit-tagged; null when parsing failed).</summary>
    public AstSymbol? Ast { get; init; }
    public string AstText { get; init; } = string.Empty;
    /// <summary>The AST emitted as OpenQASM 3.0 (empty when parsing failed).</summary>
    public string Qasm { get; init; } = string.Empty;
    public IReadOnlyList<string> Errors { get; init; } = new List<string>();
}

/// <summary>
/// The front door for Ket: source string in, parse result out. A fresh grammar/lexer/parser
/// is built per call (cheap for a playground, and sidesteps any shared parser state).
/// </summary>
public static class KetParser
{
    public static KetParseResult Parse(string source)
    {
        source ??= string.Empty;

        var grammar = new KetGrammar();
        var lexer = new Lexer();
        foreach (var terminal in grammar.TerminalSet) lexer.AddTokenRule(terminal);

        var tokens = lexer.Lexing(source).TokensForParsing;
        var result = new LALRParser(grammar, bLogging: false).Parsing(tokens);

        return new KetParseResult
        {
            Success = result.Success,
            Tokens = tokens
                .Select(c => new KetToken(c.Data, c.PatternInfo?.Terminal?.ToString() ?? "?"))
                .ToList(),
            TreeText = result.Success ? result.ToParseTree.ToTreeString() : string.Empty,
            Tree = result.Success ? result.ToParseTree : null,
            Ast = result.Success ? result.AstRoot : null,
            AstText = result.Success ? (result.AstRoot?.ToTreeString() ?? string.Empty) : string.Empty,
            Qasm = result.Success ? KetQasmEmitter.Emit(result.AstRoot) : string.Empty,
            Errors = result.Success
                ? new List<string>()
                : result.AllErrors.Select(e => e.ToString() ?? string.Empty).ToList(),
        };
    }
}
