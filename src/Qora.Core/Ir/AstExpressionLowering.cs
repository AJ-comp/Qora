using System.Globalization;
using Janglim.FrontEnd.Ast;

namespace Qora.Ir;

/// <summary>
/// Lowers Janglim's flat expression-shaped AST nodes into the unified HIR expression tree.
/// All construction is delegated to the revision-bound lowering session so every source
/// occurrence receives a distinct identity and an exact source span.
/// </summary>
internal static class AstExpressionLowering
{
    private static InvalidOperationException QInternal(string detail) =>
        new(
            $"QINTERNAL: HIR expression lowering {detail}; "
            + "the grammar should make this unreachable, so please report this");

    /// <summary>
    /// Lowers one expression-shaped nonterminal. A flat grammar <c>Expr</c> additionally
    /// recovers arithmetic precedence before it is materialized as HIR.
    /// </summary>
    internal static HirExpression ToHirExpression(
        this AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(session);
        return syntax.Name switch
        {
            "Expr" => LowerFlatExpressionToHir(syntax, session),
            "Call" => LowerCallToHir(syntax, session),
            "IndexAccess" => LowerIndexAccessToHir(syntax, session),
            _ => throw QInternal(
                $"cannot lower `{syntax.Name}` as an expression"),
        };
    }

    /// <summary>
    /// Lowers any expression-shaped AST symbol through one revision-bound session.
    /// This is the AST-to-HIR boundary consumed by declaration and statement lowering.
    /// </summary>
    internal static HirExpression ToHirExpression(
        this AstSymbol syntax,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(session);
        return syntax switch
        {
            AstNonTerminal nonTerminal =>
                nonTerminal.ToHirExpression(session),
            AstTerminal terminal =>
                LowerTerminalToHir(terminal, session),
            _ => throw QInternal(
                "found an unknown AST symbol in expression position"),
        };
    }

    private static HirExpression LowerFlatExpressionToHir(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        var position = 0;
        var expression = ParseSum(syntax.Items, ref position, session)
            ?? throw QInternal("received an empty `Expr`");
        if (position != syntax.Items.Count)
        {
            throw QInternal(
                $"consumed only {position} of {syntax.Items.Count} items in an `Expr`");
        }

        return expression;
    }

    /// <summary>
    /// Lowers one grammar <c>Condition</c>. Relational operators bind more tightly
    /// than equality, followed by logical-and and logical-or.
    /// </summary>
    internal static HirExpression ToHirCondition(
        this AstNonTerminal? syntax,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (syntax is null)
            return session.Missing();
        if (syntax.Name != "Condition")
            throw QInternal($"received `{syntax.Name}` where a `Condition` was required");

        var operands = new List<HirExpression>();
        var operators = new List<ConditionOperator>();
        SourceSpan? pendingNot = null;

        foreach (var item in syntax.Items)
        {
            if (item is AstNonTerminal { Name: "Expr" } operandSyntax)
            {
                var operand = operandSyntax.ToHirExpression(session);
                if (pendingNot is { } notSpan)
                {
                    operand = session.Unary(
                        HirUnaryOperator.LogicalNot,
                        operand,
                        Cover(notSpan, operand.Span));
                    pendingNot = null;
                }

                operands.Add(operand);
                continue;
            }

            if (item is not AstTerminal terminal)
                throw QInternal($"found `{item}` inside a `Condition`");

            var token = terminal.ToString() ?? string.Empty;
            if (token == "!")
            {
                if (pendingNot is not null)
                    throw QInternal("found consecutive condition negations");
                pendingNot = SpanOf(terminal, session);
            }
            else
            {
                operators.Add(new ConditionOperator(
                    token,
                    SpanOf(terminal, session)));
            }
        }

        if (pendingNot is not null)
            throw QInternal("found a condition negation without an operand");
        if (operands.Count == 0)
            throw QInternal("received an empty `Condition`");
        if (operators.Count != operands.Count - 1)
        {
            throw QInternal(
                $"found {operands.Count} operands but {operators.Count} binary operators");
        }

        ReduceCondition(
            operands,
            operators,
            session,
            "<",
            "<=",
            ">",
            ">=");
        ReduceCondition(operands, operators, session, "==", "!=");
        ReduceCondition(operands, operators, session, "&&");
        ReduceCondition(operands, operators, session, "||");

        if (operands.Count != 1 || operators.Count != 0)
        {
            throw QInternal(
                $"left {operators.Count} unclassified condition operator(s)");
        }

        return operands[0];
    }

    /// <summary>
    /// Lowers a type-neutral <c>name[index]</c> occurrence. Reads, writes, call
    /// arguments, and measurement targets all use this exact path.
    /// </summary>
    private static HirExpression LowerIndexAccessToHir(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(session);
        if (syntax.Name != "IndexAccess")
            throw QInternal($"received `{syntax.Name}` where an `IndexAccess` was required");

        var receiverSyntax = syntax.Items.OfType<AstTerminal>().FirstOrDefault()
            ?? throw QInternal("received an indexed access without a receiver");
        var indexSyntax = syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "Expr")
            ?? throw QInternal("received an indexed access without an index expression");

        var receiver = LowerIdentifierToHir(receiverSyntax, session);
        var index = indexSyntax.ToHirExpression(session);
        return session.Index(
            receiver,
            index,
            SpanOf(syntax, session));
    }

    /// <summary>
    /// Lowers a dotted name as nested member-access occurrences. Each segment receives
    /// its own leaf span and each prefix receives the exact covering span.
    /// </summary>
    internal static HirExpression LowerQualifiedNameToHir(
        IReadOnlyList<AstTerminal> syntax,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(session);
        if (syntax.Count == 0)
            return session.Missing();

        var first = syntax[0];
        var expression = LowerIdentifierToHir(first, session);
        var position = 1;
        while (position < syntax.Count)
        {
            var dot = syntax[position];
            if ((dot.ToString() ?? string.Empty) != "."
                || position + 1 >= syntax.Count)
            {
                throw QInternal("found a malformed qualified name");
            }

            var segment = syntax[position + 1];
            var segmentName = IdentifierText(segment);
            expression = session.Member(
                expression,
                segmentName,
                Cover(
                    expression.Span,
                    SpanOf(dot, session),
                    SpanOf(segment, session)));
            position += 2;
        }

        return expression;
    }

    /// <summary>
    /// Span of a complete AST occurrence. Connected parse-tree tokens are preferred
    /// because punctuation such as brackets and parentheses is intentionally absent
    /// from the semantic AST.
    /// </summary>
    internal static SourceSpan? SpanOf(
        AstSymbol? syntax,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (syntax is null)
            return null;

        if (syntax is AstTerminal terminal)
        {
            return terminal.Token.StartIndex < 0
                ? null
                : new SourceSpan(
                    session.Document,
                    terminal.Token.StartIndex,
                    terminal.Token.EndIndex + 1);
        }

        if (syntax is not AstNonTerminal nonTerminal)
            return null;

        var tokens = (nonTerminal.ConnectedParseTree?.AllTokens
                ?? nonTerminal.AllTokens)
            .Where(token => token.StartIndex >= 0 && token.EndIndex >= token.StartIndex)
            .ToArray();
        if (tokens.Length == 0)
            return null;

        return new SourceSpan(
            session.Document,
            tokens.Min(token => token.StartIndex),
            tokens.Max(token => token.EndIndex) + 1);
    }

    /// <summary>Smallest source range containing all supplied ranges.</summary>
    internal static SourceSpan? Cover(params SourceSpan?[] spans) =>
        Cover((IEnumerable<SourceSpan?>)spans);

    /// <summary>Smallest source range containing all supplied ranges.</summary>
    internal static SourceSpan? Cover(IEnumerable<SourceSpan?> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var present = spans
            .Where(span => span is not null)
            .Select(span => span!.Value)
            .ToArray();
        if (present.Length == 0)
            return null;

        var document = present[0].Document;
        if (present.Any(span => span.Document != document))
            throw QInternal("attempted to combine spans from different documents");

        return new SourceSpan(
            document,
            present.Min(span => span.Start),
            present.Max(span => span.End));
    }

    /// <summary>
    /// Exact call-expression span inside a call statement. The statement AST owns the
    /// parentheses and semicolon only in its connected parse tree, so the range starts
    /// at the first callee segment and ends at the outer closing parenthesis.
    /// </summary>
    internal static SourceSpan CallSpanWithinStatement(
        AstNonTerminal statement,
        AstTerminal firstCallee,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(firstCallee);
        ArgumentNullException.ThrowIfNull(session);

        var closingParentheses = (statement.ConnectedParseTree?.AllTokens
                ?? statement.AllTokens)
            .Where(token =>
                token.StartIndex >= firstCallee.Token.StartIndex
                && token.ToString() == ")")
            .ToArray();
        if (firstCallee.Token.StartIndex < 0 || closingParentheses.Length == 0)
            throw QInternal("could not recover a call statement's parentheses");

        return new SourceSpan(
            session.Document,
            firstCallee.Token.StartIndex,
            closingParentheses[^1].EndIndex + 1);
    }

    private static HirExpression? ParseSum(
        IReadOnlyList<AstSymbol> items,
        ref int position,
        AstToHirLoweringSession session)
    {
        var left = ParseProduct(items, ref position, session);
        while (left is not null
               && position < items.Count
               && IsArithmeticOperator(items[position], out var token)
               && token is "+" or "-")
        {
            var operatorSyntax = items[position++];
            var right = ParseProduct(items, ref position, session)
                ?? throw QInternal($"found binary `{token}` without a right operand");
            left = session.Binary(
                HirExpressions.ParseBinaryOperator(token),
                left,
                right,
                Cover(
                    left.Span,
                    SpanOf(operatorSyntax, session),
                    right.Span));
        }

        return left;
    }

    private static HirExpression? ParseProduct(
        IReadOnlyList<AstSymbol> items,
        ref int position,
        AstToHirLoweringSession session)
    {
        var left = ParseUnary(items, ref position, session);
        while (left is not null
               && position < items.Count
               && IsArithmeticOperator(items[position], out var token)
               && token is "*" or "/")
        {
            var operatorSyntax = items[position++];
            var right = ParseUnary(items, ref position, session)
                ?? throw QInternal($"found binary `{token}` without a right operand");
            left = session.Binary(
                HirExpressions.ParseBinaryOperator(token),
                left,
                right,
                Cover(
                    left.Span,
                    SpanOf(operatorSyntax, session),
                    right.Span));
        }

        return left;
    }

    private static HirExpression? ParseUnary(
        IReadOnlyList<AstSymbol> items,
        ref int position,
        AstToHirLoweringSession session)
    {
        if (position < items.Count
            && IsArithmeticOperator(items[position], out var token)
            && token == "-")
        {
            var operatorSyntax = items[position++];
            var operand = ParseUnary(items, ref position, session)
                ?? throw QInternal("found unary `-` without an operand");
            return session.Unary(
                HirUnaryOperator.Negate,
                operand,
                Cover(
                    SpanOf(operatorSyntax, session),
                    operand.Span));
        }

        return ParsePrimary(items, ref position, session);
    }

    private static HirExpression? ParsePrimary(
        IReadOnlyList<AstSymbol> items,
        ref int position,
        AstToHirLoweringSession session)
    {
        if (position >= items.Count)
            return null;

        var item = items[position];
        if (item is AstNonTerminal { Name: "Call" } call)
        {
            position++;
            return LowerCallToHir(call, session);
        }

        if (item is AstNonTerminal { Name: "IndexAccess" } index)
        {
            position++;
            return LowerIndexAccessToHir(index, session);
        }

        if (item is AstNonTerminal { Name: "Expr" } nested)
        {
            position++;
            return nested.ToHirExpression(session);
        }

        if (item is AstNonTerminal nonTerminal)
        {
            throw QInternal(
                $"found `{nonTerminal.Name}` in arithmetic primary position");
        }

        if (item is not AstTerminal terminal)
            throw QInternal("found an unknown AST symbol in arithmetic primary position");

        position++;
        var expression = LowerTerminalToHir(terminal, session);
        while (position + 1 < items.Count
               && items[position] is AstTerminal dot
               && (dot.ToString() ?? string.Empty) == "."
               && items[position + 1] is AstTerminal member)
        {
            expression = session.Member(
                expression,
                IdentifierText(member),
                Cover(
                    expression.Span,
                    SpanOf(dot, session),
                    SpanOf(member, session)));
            position += 2;
        }

        return expression;
    }

    private static HirExpression LowerCallToHir(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        if (syntax.Name != "Call")
            throw QInternal($"received `{syntax.Name}` where a `Call` was required");

        var calleeSyntax = syntax.Items.OfType<AstTerminal>().ToArray();
        var argumentSyntax = syntax.Items
            .OfType<AstNonTerminal>()
            .Where(item => item.Name == "Expr")
            .ToArray();

        // Measurement is a value-producing HIR expression, not an ordinary callable.
        // Canonicalize it at the shared AST-expression boundary so declarations,
        // assignments, returns, array elements, and nested expressions all see the
        // same node kind. Whether the target denotes a measurable qubit remains a
        // semantic-validation question.
        if (calleeSyntax.Length == 1
            && IdentifierText(calleeSyntax[0]) == QoraGates.Measurement
            && argumentSyntax.Length == 1)
        {
            return session.Measurement(
                argumentSyntax[0].ToHirExpression(session),
                SpanOf(syntax, session));
        }

        var callee = LowerQualifiedNameToHir(calleeSyntax, session);
        var arguments = argumentSyntax
            .Select(item =>
            {
                var expression = item.ToHirExpression(session);
                return session.Argument(
                    expression,
                    span: SpanOf(item, session));
            })
            .ToArray();
        return session.Call(
            callee,
            arguments,
            SpanOf(syntax, session));
    }

    private static HirExpression LowerTerminalToHir(
        AstTerminal syntax,
        AstToHirLoweringSession session)
    {
        var text = syntax.ToString() ?? string.Empty;
        var span = SpanOf(syntax, session);
        if (text.Length > 0 && char.IsDigit(text[0]))
        {
            return text.All(char.IsDigit)
                   && long.TryParse(
                       text,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var value)
                ? session.Integer(value, span)
                : session.Literal(text, span);
        }

        return LowerIdentifierToHir(syntax, session);
    }

    private static HirExpression LowerIdentifierToHir(
        AstTerminal syntax,
        AstToHirLoweringSession session) =>
        session.Name(
            IdentifierText(syntax),
            SpanOf(syntax, session));

    private static string IdentifierText(AstTerminal syntax)
    {
        var text = syntax.ToString() ?? string.Empty;
        if (text.Length == 0
            || !(char.IsLetter(text[0]) || text[0] == '_'))
        {
            throw QInternal(
                $"found non-identifier token `{text}` in identifier position");
        }

        return text;
    }

    private static void ReduceCondition(
        List<HirExpression> operands,
        List<ConditionOperator> operators,
        AstToHirLoweringSession session,
        params string[] level)
    {
        var position = 0;
        while (position < operators.Count)
        {
            var current = operators[position];
            if (!level.Contains(current.Token))
            {
                position++;
                continue;
            }

            var left = operands[position];
            var right = operands[position + 1];
            operands[position] = session.Binary(
                HirExpressions.ParseBinaryOperator(current.Token),
                left,
                right,
                Cover(left.Span, current.Span, right.Span));
            operands.RemoveAt(position + 1);
            operators.RemoveAt(position);
        }
    }

    private static bool IsArithmeticOperator(
        AstSymbol syntax,
        out string token)
    {
        token = syntax is AstTerminal
            ? syntax.ToString() ?? string.Empty
            : string.Empty;
        return token is "+" or "-" or "*" or "/";
    }

    private readonly record struct ConditionOperator(
        string Token,
        SourceSpan? Span);
}
