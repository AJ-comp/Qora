using System.Collections.Generic;
using System.Linq;
using Janglim.FrontEnd;
using Janglim.FrontEnd.Ast;
using Janglim.FrontEnd.Parsers.LR;
using Janglim.FrontEnd.ParseTree;
using Janglim.FrontEnd.Tokenize;
using Qora.Ir;
using Qora.Ir.Passes;

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
    /// <summary>The persistent semantic side table built at the final validation (null when validation never
    /// ran or failed earlier) — Id-keyed symbol/scope facts carried through the rest of the pipeline.</summary>
    public Ir.Passes.SemanticModel? Semantics { get; init; }
}

/// <summary>
/// The front door for Qora: source string in, parse result out. A fresh grammar/lexer/parser
/// is built per call (cheap for a playground, and sidesteps any shared parser state).
/// </summary>
public static class QoraParser
{
    /// <summary>Single-file mode: no import resolution (an <c>import</c> reports QSEM020).</summary>
    public static QoraParseResult Parse(string source) => Parse(source, baseDir: null);

    /// <param name="baseDir">Directory the entry file's imports resolve against (null = single-file mode).</param>
    /// <param name="sourcePath">The entry file's own path when known — lets the loader catch an import
    /// cycle that leads back to the entry file itself.</param>
    public static QoraParseResult Parse(string source, string? baseDir, string? sourcePath = null)
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
        // Import expansion first (the merged program is what everything downstream sees), then
        // resolution (names become fully-qualified). Each step's errors preempt the next: a partially
        // loaded or partially resolved program would only add unknown-name noise on top.
        Ir.QProgram? expanded = null;
        Ir.QProgram? resolved = null;
        Ir.QProgram? monoProgram = null;   // program after monomorphization — what actually gets emitted
        Ir.Passes.SemanticModel? semantics = null;   // side table from the FINAL validation (post-monomorphize)
        List<QoraError> semanticErrors;
        if (ir != null)
        {
            var (merged, importErrors) = ModuleLoader.Expand(ir, baseDir, sourcePath);
            expanded = merged;
            if (importErrors.Count > 0)
            {
                semanticErrors = importErrors;
            }
            else
            {
                // Desugar a measurement written inside a condition (`if (M(q[i]) == v)`) into the two-step
                // form OpenQASM needs (`bit t = M(q[i]); if (t == v)`). Runs before resolution/validation, so
                // everything downstream sees only the lowered form.
                merged = MeasureConditionLowering.Run(merged);
                expanded = merged;
                var (res, resolveErrors) = Resolver.Resolve(merged);
                resolved = res;
                if (resolveErrors.Count > 0)
                {
                    semanticErrors = resolveErrors;
                }
                else
                {
                    var baseErrors = QoraValidator.Validate(res, out var baseModel);
                    if (baseErrors.Count > 0)
                    {
                        semanticErrors = baseErrors;
                    }
                    else
                    {
                        // Monomorphization pins each generic register to a concrete size, so re-validate the
                        // specialized program: size-dependent checks (index bounds, register-size matches)
                        // can only run once sizes are known. No generics -> Run returns the same program and
                        // there is nothing new to check. Whichever Validate ran LAST owns the semantic model
                        // — its scope trees describe the program the rest of the pipeline actually consumes.
                        monoProgram = Monomorphizer.Run(res);
                        if (ReferenceEquals(monoProgram, res))
                        {
                            semanticErrors = baseErrors;
                            semantics = baseModel;
                        }
                        else
                        {
                            semanticErrors = QoraValidator.Validate(monoProgram, out semantics);
                        }
                    }
                }
            }
        }
        else
        {
            semanticErrors = new List<QoraError>();
        }

        // emission runs on the MANGLED program; NameMangler auto-resolves any name collision by appending
        // `_` and returns one note per rename, surfaced as a `// Qora:` comment in the emitted QASM.
        string qasm = string.Empty;
        if (monoProgram != null && semanticErrors.Count == 0)
        {
            // Effect analysis (pure, model-only): per-statement qubit touched/modified sets recorded on
            // the final SemanticModel — the seed data for the auto-uncompute ladder. Runs here, before
            // any tree-copying pass, so every recorded Id is one the model itself knows.
            if (semantics is not null) EffectAnalysis.Run(monoProgram, semantics);

            // Flatten within/apply conjugations (QConjugate) into straight-line gates + a synthesized inverse
            // of the `within` block, BEFORE AdjointMaterializer — so a reversal's `Adjoint Foo` becomes a real
            // Foo__adj that the mangler then owns (nothing minted at emit time). A within block with no inverse
            // is a clean QSEM027 here; emission is skipped rather than dropping the uncompute silently.
            var (conjugated, conjErrors) = ConjugationLowering.Run(monoProgram, semantics);
            if (conjErrors.Count > 0)
            {
                semanticErrors = conjErrors;
            }
            else
            {
                // Materialize whole-op Adjoint into real inverse-def ops BEFORE mangling, so every synthesized
                // name flows through the mangler's collision resolution (nothing is minted at emit time).
                var materialized = AdjointMaterializer.Run(conjugated, semantics);
                var mangled = NameMangler.Mangle(materialized.Program, semantics);
                // referential-integrity gate: after mangling, every used identifier must resolve to a
                // declaration/op/built-in. A dangling reference here is a COMPILER bug (a name not renamed
                // consistently) — fail loudly (QINTERNAL) instead of emitting silently-broken QASM.
                var refErrors = ReferentialCheck.Verify(mangled.Program);
                if (refErrors.Count > 0) semanticErrors = refErrors;
                // both passes may rename to dodge a collision; surface every note in the QASM header. The final
                // OpenQASM target-lowering (e.g. demoting a runtime `const` to a plain var — OQ3 `const` is
                // compile-time only) runs right before emission, adapting the IR to OpenQASM's specifics.
                else qasm = QasmEmitter.Emit(OpenQasmLowering.Run(mangled.Program), materialized.Notes.Concat(mangled.Notes).ToList(), semantics);
            }
        }

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
            Ir = resolved ?? expanded ?? ir,
            Qasm = qasm,
            Semantics = semantics,
            Errors = !result.Success
                ? result.AllErrors.Select(ToQoraError).ToList()
                : semanticErrors,
        };
    }

    /// <summary>
    /// The lean front end for IMPORTED files (<see cref="ModuleLoader"/>): source → lowered IR, no
    /// tokens/trees/stages. Parse errors come back positioned in THAT file's offsets — the caller
    /// re-labels them, since spans in the JSON contract refer to the entry document only.
    /// </summary>
    internal static (Ir.QProgram? Program, List<QoraError> ParseErrors) ParseToIr(string source)
    {
        var grammar = new QoraGrammar();
        var lexer = new Lexer();
        foreach (var terminal in grammar.TerminalSet) lexer.AddTokenRule(terminal);

        var tokens = lexer.Lexing(source ?? string.Empty).TokensForParsing;
        var result = new LALRParser(grammar, bLogging: false).Parsing(tokens);
        if (!result.Success) return (null, result.AllErrors.Select(ToQoraError).ToList());

        var ast = result.Count > 0 ? result.AstRoot : null;
        // spanless: these nodes belong to ANOTHER document, so entry-document offsets would lie.
        return (ast != null ? QoraLowering.Lower(ast, withSpans: false) : null, new List<QoraError>());
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
