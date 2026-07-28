using System.Globalization;
using Janglim.FrontEnd.Ast;

namespace Qora.Ir;

/// <summary>
/// Converts Janglim's source AST into one immutable, source-shaped HIR tree.
/// This is the only AST-to-HIR boundary: every node is minted by the supplied
/// revision-bound session, and only the completed program is published.
/// </summary>
internal static class AstToHirLowering
{
    private static readonly HashSet<string> TypeKeywords =
        new(StringComparer.Ordinal)
        {
            "Qubit",
            "int",
            "bit",
            "float",
            "angle",
        };

    /// <summary>
    /// Lowers and publishes one source document. Nested helpers never publish, so
    /// callers cannot observe a partially assembled HIR tree.
    /// </summary>
    public static HirProgram Lower(
        AstSymbol ast,
        AstToHirLoweringSession session)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(session);
        if (ast is not AstNonTerminal program)
        {
            throw new ArgumentException(
                "AST-to-HIR lowering requires a program nonterminal.",
                nameof(ast));
        }

        var declarations = new List<HirDeclaration>();
        var imports = new List<HirImportDirective>();

        foreach (var item in program.Items.OfType<AstNonTerminal>())
        {
            switch (item.Name)
            {
                case "Operation":
                    declarations.Add(
                        LowerCallable(
                            item,
                            session,
                            isFunction: false));
                    break;

                case "Function":
                    declarations.Add(
                        LowerCallable(
                            item,
                            session,
                            isFunction: true));
                    break;

                case "Import":
                    imports.Add(
                        session.Import(
                            QualifiedText(item).Trim('"'),
                            AstExpressionLowering.SpanOf(item, session)));
                    break;

                case "Namespace":
                    declarations.Add(
                        LowerNamespace(
                            item,
                            session));
                    break;

                default:
                    throw QInternal(
                        $"found unsupported top-level AST node `{item.Name}`");
            }
        }

        var root = session.Program(
            declarations,
            imports.Count == 0 ? null : imports,
            AstExpressionLowering.SpanOf(program, session));
        return session.Publish(root);
    }

    private static HirNamespaceDeclaration LowerNamespace(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        var segments = QualifiedText(syntax)
            .Split(
                '.',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            throw QInternal("received a namespace without a name");
        var segmentSyntax = syntax.Items
            .TakeWhile(item => item is AstTerminal)
            .OfType<AstTerminal>()
            .Where(terminal => TerminalText(terminal) != ".")
            .ToArray();
        if (segmentSyntax.Length != segments.Length)
        {
            throw QInternal(
                "could not match namespace name segments to their source tokens");
        }

        var declarations = new List<HirDeclaration>();
        var openDirectives = new List<HirOpenDirective>();

        foreach (var item in syntax.Items.OfType<AstNonTerminal>())
        {
            switch (item.Name)
            {
                case "Operation":
                    declarations.Add(
                        LowerCallable(
                            item,
                            session,
                            isFunction: false));
                    break;

                case "Function":
                    declarations.Add(
                        LowerCallable(
                            item,
                            session,
                            isFunction: true));
                    break;

                case "Open":
                    openDirectives.Add(
                        session.Open(
                            QualifiedText(item),
                            AstExpressionLowering.SpanOf(item, session)));
                    break;

                default:
                    throw QInternal(
                        $"found unsupported namespace AST node `{item.Name}`");
            }
        }

        HirNamespaceDeclaration? nested = null;
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            var isLeaf = index == segments.Length - 1;
            nested = session.Namespace(
                segments[index],
                isLeaf
                    ? openDirectives
                    : Array.Empty<HirOpenDirective>(),
                isLeaf
                    ? declarations
                    : new HirDeclaration[] { nested! },
                AstExpressionLowering.SpanOf(
                    segmentSyntax[index],
                    session));
        }
        return nested!;
    }

    private static HirCallable LowerCallable(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        bool isFunction)
    {
        var parameters = Parameters(syntax)
            .Select(parameter => LowerParameter(parameter, session))
            .ToArray();
        var statements = Body(syntax)
            .Select(statement => LowerStatement(statement, session))
            .ToArray();
        var body = session.Block(
            statements,
            BlockSpan(statements)
            ?? AstExpressionLowering.SpanOf(syntax, session));

        QType? returnType = null;
        if (isFunction)
        {
            var keyword = syntax.Items
                .OfType<AstTerminal>()
                .Select(TerminalText)
                .FirstOrDefault(TypeKeywords.Contains);
            returnType = ParseType(keyword)
                ?? throw QInternal(
                    "received a function without an explicit return type");
        }

        return session.Callable(
            CallableName(syntax),
            parameters,
            body,
            isFunction,
            returnType,
            span: AstExpressionLowering.SpanOf(
                syntax.Items
                    .OfType<AstTerminal>()
                    .FirstOrDefault(),
                session));
    }

    private static HirParameter LowerParameter(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        var directTerms = syntax.Items
            .OfType<AstTerminal>()
            .ToArray();
        var directText = directTerms
            .Select(TerminalText)
            .ToArray();
        var arrayType = syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "ArrayType");
        var typeKeyword = directText.FirstOrDefault(TypeKeywords.Contains)
            ?? arrayType?.Items
                .OfType<AstTerminal>()
                .Select(TerminalText)
                .FirstOrDefault(TypeKeywords.Contains);
        var type = ParseType(typeKeyword)
            ?? throw QInternal(
                "received a parameter without an explicit type");
        var name = directText
            .Where(text =>
                !TypeKeywords.Contains(text)
                && !IsNumber(text))
            .LastOrDefault()
            ?? string.Empty;
        var ownership = syntax.Name is "MovedParam" or "MovedMutableParam"
            ? QOwnershipMode.Moved
            : QOwnershipMode.Borrowed;
        var access = syntax.Name is "MutableParam" or "MovedMutableParam"
            ? QAccessMode.Mutable
            : QAccessMode.ReadOnly;
        var nameSyntax = directTerms
            .LastOrDefault(terminal => TerminalText(terminal) == name);

        return session.Parameter(
            name,
            type,
            registerSize: null,
            isArray: arrayType is not null,
            ownership,
            access,
            AstExpressionLowering.SpanOf(nameSyntax, session));
    }

    private static HirStatement LowerStatement(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        var span = AstExpressionLowering.SpanOf(syntax, session);
        return syntax.Name switch
        {
            "Use" => LowerUse(syntax, session, span),
            "Gate" => LowerGate(syntax, session, span),
            "ConstDecl" => LowerDeclaration(
                syntax,
                session,
                isConst: true,
                span),
            "VarDecl" => LowerDeclaration(
                syntax,
                session,
                isConst: false,
                span),
            "Assign" => LowerAssignment(syntax, session, span),
            "If" => LowerIf(syntax, session, span),
            "For" => LowerFor(syntax, session, span),
            "While" => LowerWhile(syntax, session, span),
            "Repeat" => LowerRepeat(syntax, session, span),
            "Return" => session.Return(
                LowerValueExpression(
                    ExpressionOf(syntax),
                    session),
                span),
            _ => throw QInternal(
                $"found unsupported statement AST node `{syntax.Name}`"),
        };
    }

    private static HirStatement LowerUse(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? span)
    {
        var terms = syntax.Items
            .OfType<AstTerminal>()
            .Select(TerminalText)
            .ToArray();
        var name = terms.FirstOrDefault(
                text =>
                    !TypeKeywords.Contains(text)
                    && !IsNumber(text))
            ?? string.Empty;
        var size = terms.FirstOrDefault(IsNumber) ?? string.Empty;
        return session.QubitDeclaration(
            name,
            Count(size),
            span);
    }

    private static HirStatement LowerGate(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? statementSpan)
    {
        var items = syntax.Items;
        var modifiers = new List<QGateModifier>();
        var start = 0;
        if (items.Count > 0
            && TerminalText(items[0]) == "Controlled")
        {
            modifiers.Add(QGateModifier.Controlled);
            start = 1;
        }

        var calleeSyntax = items
            .Skip(start)
            .TakeWhile(item => item is AstTerminal)
            .Cast<AstTerminal>()
            .ToArray();
        if (calleeSyntax.Length == 0)
            throw QInternal("received a call statement without a callee");

        var callee = AstExpressionLowering.LowerQualifiedNameToHir(
            calleeSyntax,
            session);
        var arguments = items
            .Skip(start + calleeSyntax.Length)
            .Select(argument => LowerArgument(argument, session))
            .ToArray();
        var callSpan = AstExpressionLowering.CallSpanWithinStatement(
            syntax,
            calleeSyntax[0],
            session);
        var call = session.Call(
            callee,
            arguments,
            callSpan);
        return session.CallStatement(
            modifiers,
            call,
            statementSpan);
    }

    private static HirArgument LowerArgument(
        AstSymbol syntax,
        AstToHirLoweringSession session)
    {
        var ownership = QOwnershipMode.Borrowed;
        var access = QAccessMode.ReadOnly;
        AstSymbol valueSyntax = syntax;

        if (syntax is AstNonTerminal
            {
                Name: "MutableArg" or "MovedArg" or "MovedMutableArg",
            } modeSyntax)
        {
            valueSyntax = modeSyntax.Items
                .OfType<AstNonTerminal>()
                .FirstOrDefault(item => item.Name == "Expr")
                ?? throw QInternal(
                    "received a parameter-mode argument without an expression");
            ownership = modeSyntax.Name is "MovedArg" or "MovedMutableArg"
                ? QOwnershipMode.Moved
                : QOwnershipMode.Borrowed;
            access = modeSyntax.Name is "MutableArg" or "MovedMutableArg"
                ? QAccessMode.Mutable
                : QAccessMode.ReadOnly;
        }

        var expression = valueSyntax.ToHirExpression(session);
        return session.Argument(
            expression,
            ownership,
            access,
            AstExpressionLowering.SpanOf(syntax, session));
    }

    private static HirStatement LowerDeclaration(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        bool isConst,
        SourceSpan? span)
    {
        var arrayLiteral = syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "ArrayLiteral");
        var arrayCreation = syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "ArrayNew");
        var value = arrayLiteral is not null
            ? LowerArrayLiteral(arrayLiteral, session)
            : arrayCreation is not null
                ? LowerArrayCreation(arrayCreation, session)
                : LowerValueExpression(
                    ExpressionOf(syntax),
                    session);
        var type = ParseType(DeclarationType(syntax));

        return session.VariableDeclaration(
            isConst,
            type,
            DeclarationName(syntax),
            value,
            syntax.Items
                .OfType<AstNonTerminal>()
                .Any(item => item.Name == "ArrayType"),
            span);
    }

    private static HirStatement LowerAssignment(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? span)
    {
        var indexed = syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "IndexAccess");
        var target = indexed is not null
            ? indexed.ToHirExpression(session)
            : syntax.Items
                .OfType<AstTerminal>()
                .First(item => IsIdentifier(TerminalText(item)))
                .ToHirExpression(session);
        var value = LowerValueExpression(
            ExpressionOf(syntax),
            session);
        return session.Assignment(
            target,
            value,
            span);
    }

    private static HirExpression LowerArrayLiteral(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        var elements = syntax.Items
            .OfType<AstNonTerminal>()
            .Where(item => item.Name == "Expr")
            .Select(item => item.ToHirExpression(session))
            .ToArray();
        return session.ArrayLiteral(
            elements,
            AstExpressionLowering.SpanOf(syntax, session));
    }

    private static HirExpression LowerArrayCreation(
        AstNonTerminal syntax,
        AstToHirLoweringSession session)
    {
        var terms = syntax.Items
            .OfType<AstTerminal>()
            .Select(TerminalText)
            .ToArray();
        var type = ParseType(
                terms.FirstOrDefault(TypeKeywords.Contains))
            ?? QType.Int;
        var length = terms.FirstOrDefault(IsNumber)
            ?? string.Empty;
        return session.ArrayCreation(
            type,
            Count(length),
            AstExpressionLowering.SpanOf(syntax, session));
    }

    private static HirStatement LowerIf(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? span)
    {
        var condition = ConditionOf(syntax)
            .ToHirCondition(session);
        var elseIndex = -1;
        for (var index = 0; index < syntax.Items.Count; index++)
        {
            if (syntax.Items[index] is AstTerminal terminal
                && TerminalText(terminal) == "else")
            {
                elseIndex = index;
                break;
            }
        }

        var thenStatements = new List<HirStatement>();
        var elseStatements = new List<HirStatement>();
        for (var index = 0; index < syntax.Items.Count; index++)
        {
            if (syntax.Items[index] is not AstNonTerminal statement
                || statement.Name == "Condition")
            {
                continue;
            }

            var lowered = LowerStatement(statement, session);
            (elseIndex < 0 || index < elseIndex
                    ? thenStatements
                    : elseStatements)
                .Add(lowered);
        }

        var thenBlock = session.Block(
            thenStatements,
            BlockSpan(thenStatements) ?? span);
        var elseBlock = session.Block(
            elseStatements,
            BlockSpan(elseStatements) ?? span);
        return session.If(
            condition,
            thenBlock,
            elseBlock,
            span);
    }

    private static HirStatement LowerFor(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? span)
    {
        var variable = syntax.Items
            .OfType<AstTerminal>()
            .FirstOrDefault();
        var bounds = syntax.Items
            .OfType<AstNonTerminal>()
            .Where(item => item.Name == "Expr")
            .ToArray();

        HirExpression from;
        HirExpression to;
        switch (bounds.Length)
        {
            case 0:
                from = session.Integer(0);
                to = session.Integer(0);
                break;

            case 1:
                // The same syntax denotes two distinct HIR occurrences. Lower it
                // twice so the IDs cannot alias across the two semantic roles.
                from = bounds[0].ToHirExpression(session);
                to = bounds[0].ToHirExpression(session);
                break;

            default:
                from = bounds[0].ToHirExpression(session);
                to = bounds[1].ToHirExpression(session);
                break;
        }

        var statements = BodyStatements(syntax)
            .Select(statement => LowerStatement(statement, session))
            .ToArray();
        var body = session.Block(
            statements,
            BlockSpan(statements) ?? span);
        return session.For(
            variable is null ? string.Empty : TerminalText(variable),
            from,
            to,
            body,
            span);
    }

    private static HirStatement LowerWhile(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? span)
    {
        var condition = ConditionOf(syntax)
            .ToHirCondition(session);
        var statements = BodyStatements(syntax)
            .Select(statement => LowerStatement(statement, session))
            .ToArray();
        return session.While(
            condition,
            session.Block(
                statements,
                BlockSpan(statements) ?? span),
            span);
    }

    private static HirStatement LowerRepeat(
        AstNonTerminal syntax,
        AstToHirLoweringSession session,
        SourceSpan? span)
    {
        var statements = BodyStatements(syntax)
            .Select(statement => LowerStatement(statement, session))
            .ToArray();
        return session.Repeat(
            session.Block(
                statements,
                BlockSpan(statements) ?? span),
            ConditionOf(syntax).ToHirCondition(session),
            span);
    }

    private static HirExpression LowerValueExpression(
        AstNonTerminal? syntax,
        AstToHirLoweringSession session)
        => syntax is null
            ? session.Missing()
            : syntax.ToHirExpression(session);

    private static SourceSpan? BlockSpan(
        IEnumerable<HirStatement> statements) =>
        AstExpressionLowering.Cover(
            statements.Select(statement => statement.Span));

    private static int Count(string text) =>
        int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : -1;

    private static string QualifiedText(AstNonTerminal syntax) =>
        string.Concat(
            syntax.Items
                .TakeWhile(item => item is AstTerminal)
                .Select(TerminalText));

    private static string CallableName(AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstTerminal>()
            .Select(TerminalText)
            .FirstOrDefault()
        ?? string.Empty;

    private static bool IsParameter(AstNonTerminal syntax) =>
        syntax.Name is
            "Param"
            or "MutableParam"
            or "MovedParam"
            or "MovedMutableParam";

    private static IEnumerable<AstNonTerminal> Parameters(
        AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstNonTerminal>()
            .Where(IsParameter);

    private static IEnumerable<AstNonTerminal> Body(
        AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstNonTerminal>()
            .Where(item => !IsParameter(item));

    private static IEnumerable<AstNonTerminal> BodyStatements(
        AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstNonTerminal>()
            .Where(item =>
                item.Name != "Condition"
                && item.Name != "Expr");

    private static AstNonTerminal? ConditionOf(
        AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "Condition");

    private static AstNonTerminal? ExpressionOf(
        AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstNonTerminal>()
            .FirstOrDefault(item => item.Name == "Expr");

    private static string DeclarationName(AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstTerminal>()
            .Select(TerminalText)
            .FirstOrDefault(text => !TypeKeywords.Contains(text))
        ?? string.Empty;

    private static string? DeclarationType(AstNonTerminal syntax) =>
        syntax.Items
            .OfType<AstTerminal>()
            .Select(TerminalText)
            .FirstOrDefault(TypeKeywords.Contains)
        ?? syntax.Items
            .OfType<AstNonTerminal>()
            .Where(item => item.Name == "ArrayType")
            .SelectMany(item => item.Items.OfType<AstTerminal>())
            .Select(TerminalText)
            .FirstOrDefault(TypeKeywords.Contains);

    private static QType? ParseType(string? keyword) =>
        keyword switch
        {
            "Qubit" => QType.Qubit,
            "int" => QType.Int,
            "bit" => QType.Bit,
            "float" => QType.Float,
            "angle" => QType.Angle,
            _ => null,
        };

    private static string TerminalText(AstSymbol syntax) =>
        syntax.ToString() ?? string.Empty;

    private static bool IsNumber(string text) =>
        text.Length > 0
        && text.All(char.IsDigit);

    private static bool IsIdentifier(string text) =>
        text.Length > 0
        && (char.IsLetter(text[0]) || text[0] == '_');

    private static InvalidOperationException QInternal(string detail) =>
        new(
            $"QINTERNAL: AST-to-HIR lowering {detail}; "
            + "the grammar should make this unreachable, so please report this");
}
