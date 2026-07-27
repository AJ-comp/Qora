using Janglim.FrontEnd.Ast;
using Qora.Compiler;

namespace Qora.Ir;

/// <summary>
/// The engine boundary: converts a Janglim parse-AST (<see cref="AstSymbol"/>, tagged by MeaningUnit
/// name) into Qora's source-shaped <see cref="QProgram"/> HIR, once. After this, nothing downstream
/// touches Janglim types.
///
/// Structure (statements, gates, control flow) becomes typed HIR nodes; expressions and conditions become
/// one canonical <see cref="QNode"/> tree here. Validation and source diagnostics operate on this form,
/// while <see cref="Mir.QoraMirLowering"/> later converts it to SSA values and basic blocks.
/// </summary>
internal static class QoraLowering
{
    private static readonly HashSet<string> TypeKeywords = new() { "Qubit", "int", "bit", "float", "angle" };
    private static readonly HashSet<string> FunctorNames = new() { "Adjoint", "Controlled" };

    public static QProgram? Lower(
        AstSymbol? ast,
        SourceDocumentRef source)
    {
        if (source.CompilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "HIR lowering requires an exact source document revision.",
                nameof(source));
        if (ast is not AstNonTerminal program) return null;

        var operations = new List<QOperation>();
        var imports = new List<QImport>();
        var opens = new Dictionary<string, IReadOnlyList<QOpen>>();
        foreach (var item in program.Items.OfType<AstNonTerminal>())
        {
            switch (item.Name)
            {
                case "Operation":
                    operations.Add(LowerOperation(item, source));
                    break;
                case "Function":
                    operations.Add(LowerFunction(item, source));
                    break;
                // import "lib/gates.qor";  — the only import form: a quoted relative path. The StringLit
                // token keeps its quotes, so trim them to recover the literal path.
                case "Import":
                    var target = QnameText(item).Trim('"');
                    imports.Add(new QImport(target) with { Span = SpanOf(item, source) });
                    break;
                // namespace blocks lower for real: ops carry their namespace, opens are recorded
                // per namespace (merged across blocks of the same name) for the resolver pass.
                case "Namespace":
                    var nsName = QnameText(item);
                    var nsOpens = opens.TryGetValue(nsName, out var prev) ? new List<QOpen>(prev) : new List<QOpen>();
                    foreach (var nested in item.Items.OfType<AstNonTerminal>())
                    {
                        if (nested.Name == "Operation")
                            operations.Add(LowerOperation(nested, source) with { Namespace = nsName });
                        else if (nested.Name == "Function")
                            operations.Add(LowerFunction(nested, source) with { Namespace = nsName });
                        else if (nested.Name == "Open")
                            nsOpens.Add(new QOpen(QnameText(nested), SpanOf(nested, source)));
                    }
                    opens[nsName] = nsOpens;
                    break;
            }
        }

        return new QProgram(
            operations,
            imports.Count > 0 ? imports : null,
            opens.Count > 0 ? opens : null);
    }

    /// <summary>
    /// Span of a whole AST node: first to last token, half-open. Prefers the node's connected PARSE
    /// tree — the AST drops non-meaning tokens (<c>;</c>, parens), so AST tokens alone would end a
    /// statement's squiggle at its last identifier instead of covering the full statement.
    /// </summary>
    private static SourceSpan? SpanOf(
        AstNonTerminal node,
        SourceDocumentRef source)
    {
        var tokens = (node.ConnectedParseTree?.AllTokens ?? node.AllTokens)
            .Where(t => t.StartIndex >= 0).ToList();
        if (tokens.Count == 0) return null;
        return new SourceSpan(
            source,
            tokens[0].StartIndex,
            tokens[^1].EndIndex + 1);
    }

    private static SourceSpan? SpanOf(
        AstTerminal? terminal,
        SourceDocumentRef source) =>
        terminal is null || terminal.Token.StartIndex < 0
            ? null
            : new SourceSpan(
                source,
                terminal.Token.StartIndex,
                terminal.Token.EndIndex + 1);

    /// <summary>The leading dotted name of an Import/Namespace node (its terminals up to the first block content).</summary>
    private static string QnameText(AstNonTerminal node) =>
        string.Concat(node.Items.TakeWhile(i => i is AstTerminal).Select(i => i.ToString()));

    private static QOperation LowerOperation(
        AstNonTerminal op,
        SourceDocumentRef source)
    {
        var name = OpName(op);
        var ps = Params(op).Select(parameter => LowerParam(parameter, source)).ToList();
        var body = Body(op).Select(statement => LowerSpanned(statement, source)).ToList();
        return new QOperation(name, ps, body)
        {
            Span = SpanOf(op.Items.OfType<AstTerminal>().FirstOrDefault(), source),
        };
    }

    /// <summary>A <c>function</c> lowers to a <see cref="QOperation"/> flagged <see cref="QOperation.IsFunction"/>
    /// with a <see cref="QOperation.ReturnType"/>. The return type is the one direct type-keyword terminal (the
    /// <c>): T</c> tail); the name is the first terminal; params and body come out exactly as for an operation.</summary>
    private static QOperation LowerFunction(
        AstNonTerminal fn,
        SourceDocumentRef source)
    {
        var name = OpName(fn);
        var ps = Params(fn).Select(parameter => LowerParam(parameter, source)).ToList();
        var returnKw = fn.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => TypeKeywords.Contains(t));
        var body = Body(fn).Select(statement => LowerSpanned(statement, source)).ToList();
        return new QOperation(name, ps, body)
        {
            IsFunction = true,
            ReturnType = ParseType(returnKw),
            Span = SpanOf(fn.Items.OfType<AstTerminal>().FirstOrDefault(), source),
        };
    }

    /// <summary>Every statement gets its source span here, in ONE place, whatever its kind.</summary>
    private static QStmt LowerSpanned(
        AstNonTerminal node,
        SourceDocumentRef source) =>
        LowerStmt(node, source) with { Span = SpanOf(node, source) };

    // Qubit q / Qubit[] qs / int n / bit b. Every type keyword is explicit in the AST.
    private static QParam LowerParam(
        AstNonTerminal param,
        SourceDocumentRef source)
    {
        var terms = param.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        var arrayType = param.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "ArrayType");
        var typeKw = terms.FirstOrDefault(t => TypeKeywords.Contains(t))
                     ?? arrayType?.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
                         .FirstOrDefault(t => TypeKeywords.Contains(t));
        var isArray = arrayType is not null;
        var ownership = param.Name is "MovedParam" or "MovedMutableParam"
            ? QOwnershipMode.Moved
            : QOwnershipMode.Borrowed;
        var access = param.Name is "MutableParam" or "MovedMutableParam"
            ? QAccessMode.Mutable
            : QAccessMode.ReadOnly;
        // A source parameter contains one identifier besides its explicit type: the parameter name.
        var idents = terms.Where(t => !TypeKeywords.Contains(t) && !IsNumber(t)).ToList();
        var name = idents.Count > 0 ? idents[^1] : string.Empty;
        var span = SpanOf(
            param.Items.OfType<AstTerminal>().FirstOrDefault(t => (t.ToString() ?? "") == name),
            source);

        if (typeKw == "int") return new QParam(name, QType.Int, null) { IsArray = isArray, Ownership = ownership, Access = access, Span = span };
        if (typeKw == "bit") return new QParam(name, QType.Bit, null) { IsArray = isArray, Ownership = ownership, Access = access, Span = span };
        if (typeKw == "float") return new QParam(name, QType.Float, null) { IsArray = isArray, Ownership = ownership, Access = access, Span = span };
        if (typeKw == "angle") return new QParam(name, QType.Angle, null) { IsArray = isArray, Ownership = ownership, Access = access, Span = span };
        if (typeKw == "Qubit" && isArray)
            return new QParam(name, QType.Qubit, null) { IsArray = true, Ownership = ownership, Access = access, Span = span };   // Qubit[] qs
        if (typeKw == "Qubit")
            return new QParam(name, QType.Qubit, null) { IsArray = false, Ownership = ownership, Access = access, Span = span };  // Qubit q

        throw new InvalidOperationException("a parameter AST must contain an explicit type keyword");
    }

    private static QStmt LowerStmt(
        AstNonTerminal node,
        SourceDocumentRef source) => node.Name switch
    {
        "Use" => LowerUse(node),
        "Gate" => LowerGate(node),
        "ConstDecl" => LowerDecl(node, isConst: true),
        "VarDecl" => LowerDecl(node, isConst: false),
        "Assign" => LowerAssign(node),
        "If" => LowerIf(node, source),
        "For" => LowerFor(node, source),
        "While" => new QWhile(
            LowerCondition(CondOf(node)),
            BodyStmts(node).Select(statement => LowerSpanned(statement, source)).ToList()),
        "Repeat" => new QRepeat(
            BodyStmts(node).Select(statement => LowerSpanned(statement, source)).ToList(),
            LowerCondition(CondOf(node))),
        "Return" => new QReturn(LowerExpr(ExprOf(node))),
        _ => new QGate(new List<string>(), node.Name, new List<QArg>()), // defensive: unknown node -> inert
    };

    /// <summary>Parse a register size (a grammar-guaranteed number). Returns <c>-1</c> when the value does
    /// not fit in a 32-bit int — an absurd size like <c>Qubit[99999999999]</c> — so the validator rejects it
    /// cleanly (QSEM016) instead of <c>int.Parse</c> throwing and crashing the compiler.</summary>
    private static int Count(string s) => int.TryParse(s, out var n) ? n : -1;

    private static QUse LowerUse(AstNonTerminal node)
    {
        var terms = node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        var name = terms.FirstOrDefault(t => !TypeKeywords.Contains(t) && !IsNumber(t)) ?? string.Empty;
        var size = terms.FirstOrDefault(IsNumber) ?? string.Empty;
        return new QUse(name, Count(size));
    }

    private static QGate LowerGate(AstNonTerminal node)
    {
        var items = node.Items;
        var head = items.Count > 0 ? items[0].ToString() ?? string.Empty : string.Empty;

        var functors = new List<string>();
        var start = 0;
        if (FunctorNames.Contains(head)) { functors.Add(head); start = 1; }

        // the callee may be namespace-qualified: consume Ident (Dot Ident)* into one dotted name.
        var name = items.Count > start ? items[start].ToString() ?? string.Empty : string.Empty;
        var idx = start + 1;
        while (idx + 1 < items.Count
               && items[idx] is AstTerminal dot && (dot.ToString() ?? "") == "."
               && items[idx + 1] is AstTerminal seg)
        {
            name += "." + seg;
            idx += 2;
        }

        var args = items.Skip(idx).Select(LowerArg).ToList();
        return new QGate(functors, name, args);
    }

    private static QArg LowerArg(AstSymbol sym)
    {
        if (sym is AstNonTerminal nt)
        {
            if (nt.Name is "MutableArg" or "MovedArg" or "MovedMutableArg")
            {
                var value = nt.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Expr")
                    ?? throw new InvalidOperationException("a parameter-mode argument AST must contain one expression");
                var ownership = nt.Name is "MovedArg" or "MovedMutableArg"
                    ? QOwnershipMode.Moved
                    : QOwnershipMode.Borrowed;
                var access = nt.Name is "MutableArg" or "MovedMutableArg"
                    ? QAccessMode.Mutable
                    : QAccessMode.ReadOnly;
                return LowerArg(value) switch
                {
                    QQubitArg q => q with { Ownership = ownership, Access = access },
                    QTextArg t => t with { Ownership = ownership, Access = access },
                    var other => other,
                };
            }
            if (nt.Name == "IndexAccess") return LowerIndexedArg(nt);
            if (nt.Name == "Expr")
            {
                if (nt.Items.Count == 1 && nt.Items[0] is AstNonTerminal { Name: "IndexAccess" } indexed)
                    return LowerIndexedArg(indexed);
                return new QTextArg(ExprTree.Expression(nt));
            }
        }
        // A bare terminal argument — a whole register `H(q)`, an angle name — is an atom.
        var bare = sym.ToString() ?? string.Empty;
        return new QTextArg(bare.Length == 0 ? null : ExprTree.Atom(bare));
    }

    private static QQubitArg LowerIndexedArg(AstNonTerminal indexed)
    {
        var access = ExprTree.IndexNode(indexed);
        return new QQubitArg(((QNameRef)access.Base).Name, access.Index);
    }

    private static QDecl LowerDecl(AstNonTerminal node, bool isConst)
    {
        var name = DeclName(node);
        var type = DeclType(node) switch { "int" => (QType?)QType.Int, "bit" => QType.Bit, "float" => QType.Float, "angle" => QType.Angle, _ => null };
        var literal = node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "ArrayLiteral");
        var allocation = node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "ArrayNew");
        var value = literal is not null ? LowerArrayLiteral(literal)
                  : allocation is not null ? LowerArrayNew(allocation)
                  : LowerExpr(ExprOf(node));
        return new QDecl(isConst, type, name, value)
        {
            IsArray = node.Items.OfType<AstNonTerminal>().Any(n => n.Name == "ArrayType"),
        };
    }

    private static QAssign LowerAssign(AstNonTerminal node)
    {
        var indexed = node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "IndexAccess");
        return indexed is null
            ? new QAssign(FirstIdent(node), LowerExpr(ExprOf(node)))
            : LowerIndexedAssign(indexed, LowerExpr(ExprOf(node)));
    }

    private static QAssign LowerIndexedAssign(AstNonTerminal indexed, QExpr value)
    {
        var access = ExprTree.IndexNode(indexed);
        return new QAssign(((QNameRef)access.Base).Name, value) { Index = access.Index };
    }

    private static QArrayLiteral LowerArrayLiteral(AstNonTerminal node) =>
        new(node.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Expr").Select(LowerExpr).ToList());

    private static QArrayNew LowerArrayNew(AstNonTerminal node)
    {
        var terms = node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        var type = ParseType(terms.FirstOrDefault(t => TypeKeywords.Contains(t)));
        var length = terms.FirstOrDefault(IsNumber) ?? string.Empty;
        return new QArrayNew(type ?? QType.Int, Count(length));
    }

    private static QIf LowerIf(
        AstNonTerminal node,
        SourceDocumentRef source)
    {
        var items = node.Items;

        // the `else` terminal (if present) splits then-branch statements from else-branch (an `else if`
        // shows up as an "If" nonterminal in the else-branch, lowered recursively like any statement).
        int elseIdx = -1;
        for (int k = 0; k < items.Count; k++)
            if (items[k] is AstTerminal t && t.ToString() == "else") { elseIdx = k; break; }

        var thenStmts = new List<QStmt>();
        var elseStmts = new List<QStmt>();
        for (int k = 0; k < items.Count; k++)
        {
            if (items[k] is not AstNonTerminal nt || nt.Name == "Condition") continue;
            (elseIdx < 0 || k < elseIdx ? thenStmts : elseStmts).Add(
                LowerSpanned(nt, source));
        }

        return new QIf(LowerCondition(CondOf(node)), thenStmts, elseStmts);
    }

    private static QFor LowerFor(
        AstNonTerminal node,
        SourceDocumentRef source)
    {
        // The loop variable is the first meaning terminal (for/in/braces are dropped). The two bounds are
        // the direct "Expr" children — full expressions (e.g. `0 .. n - 1`), parsed once (see ExprTree).
        // A missing first bound defaults to 0; a missing second bound repeats the first (grammar
        // guarantees a bound's Expr is non-empty, so a null parse is a compiler bug).
        var loopVar = node.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;
        var bounds = node.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Expr").ToList();
        var from = bounds.Count > 0
            ? ExprTree.Expression(bounds[0]) ?? throw new InvalidOperationException("QINTERNAL: a for-bound Expr lowered to no tree")
            : new QNumLit(0);
        var to = bounds.Count > 1
            ? ExprTree.Expression(bounds[1]) ?? throw new InvalidOperationException("QINTERNAL: a for-bound Expr lowered to no tree")
            : from;
        return new QFor(
            loopVar,
            from,
            to,
            BodyStmts(node).Select(statement => LowerSpanned(statement, source)).ToList());
    }

    // A decl/assign RHS. The only legal call form is a LONE call to the registered measurement
    // function (`var r: bit = M(q[i]);`), which becomes QMeasure — exact name, no aliases. Any other call —
    // a different name, or a call mixed with arithmetic — has no OpenQASM lowering: the tree keeps the
    // call node and the validator rejects it (QText.HasCall derives from the tree). Nothing is silently
    // dropped: the old code truncated `M(q) + 1` to just the measure, and read ANY call name as a
    // measurement.
    private static QExpr LowerExpr(AstNonTerminal? expr)
    {
        if (expr is null) return new QText();

        var call = Descendants(expr).FirstOrDefault(n => n.Name == "Call");
        if (call is not null && expr.Items.Count == 1 && CallName(call) == QoraGates.Measurement)
        {
            // The call's argument is an `expr` (grammar: `Ident(expr, …)`). A measurement target must be a
            // plain qubit REFERENCE — a register element `q[i]` or a whole single qubit `a`. Anything else
            // (arithmetic, a nested call) is NOT a measurement: it stays a call node for the validator to
            // reject, rather than lowering to a target-less measurement that emitted a bare `measure;`.
            var argExpr = call.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Expr");
            if (MeasureTarget(argExpr) is { } target) return new QMeasure(target);
        }
        return new QText(ExprTree.Expression(expr));
    }

    /// <summary>The measured qubit reference of <c>M(…)</c>, in the IR's canonical reference form: a register
    /// element <c>q[i]</c> becomes a <see cref="QIndexNode"/>, a whole single qubit <c>a</c> becomes a
    /// <see cref="QNameRef"/>. Null when the argument is neither — the caller then does NOT build a
    /// measurement, so "a measurement with no qubit" never enters the IR.</summary>
    private static QNode? MeasureTarget(AstNonTerminal? argExpr)
    {
        if (argExpr is null || argExpr.Items.Count != 1) return null;
        if (argExpr.Items[0] is AstNonTerminal { Name: "IndexAccess" } idx)
            return ExprTree.IndexNode(idx);
        if (argExpr.Items[0] is AstTerminal term)
        {
            var name = term.ToString() ?? string.Empty;
            if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_')) return new QNameRef(name);
        }
        return null;
    }

    private static QCond LowerCondition(AstNonTerminal? cond) =>
        new(ExprTree.Condition(cond));

    // A Call's direct terminals are exactly its qname segments and dots; arguments are Expr children.
    // Keep measurement classification on the same complete spelling that ExprTree.CallNode stores.
    private static string CallName(AstNonTerminal call) =>
        string.Concat(call.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty));

    // --- helpers (ported from the old emitter's AST accessors) ---

    private static string OpName(AstNonTerminal op) =>
        op.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static bool IsParam(AstNonTerminal node) =>
        node.Name is "Param" or "MutableParam" or "MovedParam" or "MovedMutableParam";

    private static IEnumerable<AstNonTerminal> Params(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(IsParam);

    private static IEnumerable<AstNonTerminal> Body(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => !IsParam(n));

    // Body statements are the block's nonterminal children, minus the "meta" nonterminals a statement
    // header carries as direct children: a "Condition" (if/while/repeat) and the two "Expr" for-bounds.
    private static IEnumerable<AstNonTerminal> BodyStmts(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Condition" && n.Name != "Expr");

    private static AstNonTerminal? CondOf(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Condition");

    private static AstNonTerminal? ExprOf(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Expr");

    private static string FirstIdent(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static string DeclName(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => !TypeKeywords.Contains(t)) ?? string.Empty;

    private static string? DeclType(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => TypeKeywords.Contains(t))
        ?? node.Items.OfType<AstNonTerminal>().Where(n => n.Name == "ArrayType")
            .SelectMany(n => n.Items.OfType<AstTerminal>())
            .Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => TypeKeywords.Contains(t));

    private static QType? ParseType(string? keyword) => keyword switch
    {
        "Qubit" => QType.Qubit,
        "int" => QType.Int,
        "bit" => QType.Bit,
        "float" => QType.Float,
        "angle" => QType.Angle,
        _ => null,
    };

    private static IEnumerable<AstNonTerminal> Descendants(AstNonTerminal node)
    {
        foreach (var child in node.Items.OfType<AstNonTerminal>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static bool IsNumber(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static string Child(AstNonTerminal node, int i) =>
        i < node.Items.Count ? node.Items[i].ToString() ?? string.Empty : string.Empty;

    private static string At(List<string> list, int i) =>
        i < list.Count ? (list[i] ?? string.Empty) : string.Empty;
}
