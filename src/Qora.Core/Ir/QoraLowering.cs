using Janglim.FrontEnd.Ast;

namespace Qora.Ir;

/// <summary>
/// The engine boundary: converts a Janglim parse-AST (<see cref="AstSymbol"/>, tagged by MeaningUnit
/// name) into Qora's own <see cref="QProgram"/> IR, once. After this, nothing downstream touches Janglim
/// types — passes and the emitter work purely on the IR.
///
/// Structure (statements, gates, control flow) becomes typed IR nodes; expressions and conditions are
/// flattened to their rendered text here (Qora never restructures them), mirroring the old emitter's
/// space-joined output exactly.
/// </summary>
public static class QoraLowering
{
    private static readonly HashSet<string> TypeKeywords = new() { "int", "bit" };
    private static readonly HashSet<string> FunctorNames = new() { "Adjoint", "Controlled" };

    // Spans are meaningful only for the ENTRY document (the editor contract's offsets). Imported files
    // lower spanless (withSpans: false) so their nodes can never masquerade as entry-document
    // locations. Thread-static instead of a parameter: it spares every helper below a threading
    // argument, and Lower() is a synchronous single call per thread.
    [ThreadStatic] private static bool _noSpans;

    public static QProgram? Lower(AstSymbol? ast, bool withSpans = true)
    {
        _noSpans = !withSpans;
        if (ast is not AstNonTerminal program) return null;

        var operations = new List<QOperation>();
        var imports = new List<QImport>();
        var opens = new Dictionary<string, IReadOnlyList<QOpen>>();
        foreach (var item in program.Items.OfType<AstNonTerminal>())
        {
            switch (item.Name)
            {
                case "Operation":
                    operations.Add(LowerOperation(item));
                    break;
                // import lib.gates;  (bare dotted name)  /  import "path.qor";  (string path — the
                // StringLit token keeps its quotes, which is how the two forms are told apart here).
                case "Import":
                    var target = QnameText(item);
                    imports.Add((target.StartsWith('"')
                        ? new QImport(target.Trim('"'), IsPath: true)
                        : new QImport(target, IsPath: false)) with { Span = SpanOf(item) });
                    break;
                // namespace blocks lower for real: ops carry their namespace, opens are recorded
                // per namespace (merged across blocks of the same name) for the resolver pass.
                case "Namespace":
                    var nsName = QnameText(item);
                    var nsOpens = opens.TryGetValue(nsName, out var prev) ? new List<QOpen>(prev) : new List<QOpen>();
                    foreach (var nested in item.Items.OfType<AstNonTerminal>())
                    {
                        if (nested.Name == "Operation")
                            operations.Add(LowerOperation(nested) with { Namespace = nsName });
                        else if (nested.Name == "Open")
                            nsOpens.Add(new QOpen(QnameText(nested), SpanOf(nested)));
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
    private static QSpan? SpanOf(AstNonTerminal node)
    {
        if (_noSpans) return null;
        var tokens = (node.ConnectedParseTree?.AllTokens ?? node.AllTokens)
            .Where(t => t.StartIndex >= 0).ToList();
        if (tokens.Count == 0) return null;
        return new QSpan(tokens[0].StartIndex, tokens[^1].EndIndex + 1);
    }

    private static QSpan? SpanOf(AstTerminal? terminal) =>
        _noSpans || terminal is null || terminal.Token.StartIndex < 0
            ? null
            : new QSpan(terminal.Token.StartIndex, terminal.Token.EndIndex + 1);

    /// <summary>The leading dotted name of an Import/Namespace node (its terminals up to the first block content).</summary>
    private static string QnameText(AstNonTerminal node) =>
        string.Concat(node.Items.TakeWhile(i => i is AstTerminal).Select(i => i.ToString()));

    private static QOperation LowerOperation(AstNonTerminal op)
    {
        var name = OpName(op);
        var ps = Params(op).Select(LowerParam).ToList();
        var body = Body(op).Select(LowerSpanned).ToList();
        return new QOperation(name, ps, body) { Span = SpanOf(op.Items.OfType<AstTerminal>().FirstOrDefault()) };
    }

    /// <summary>Every statement gets its source span here, in ONE place, whatever its kind.</summary>
    private static QStmt LowerSpanned(AstNonTerminal node) => LowerStmt(node) with { Span = SpanOf(node) };

    // Qubit q / Qubit[2] q / int n / bit b  (mirrors the old EmitParam token inspection).
    private static QParam LowerParam(AstNonTerminal param)
    {
        var terms = param.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        var name = terms.FirstOrDefault(t => !TypeKeywords.Contains(t) && !IsNumber(t)) ?? string.Empty;
        var typeKw = terms.FirstOrDefault(t => TypeKeywords.Contains(t));
        var size = terms.FirstOrDefault(IsNumber);
        var span = SpanOf(param.Items.OfType<AstTerminal>().FirstOrDefault(t => (t.ToString() ?? "") == name));

        if (typeKw == "int") return new QParam(name, QType.Int, null) { Span = span };
        if (typeKw == "bit") return new QParam(name, QType.Bit, null) { Span = span };
        if (size is not null) return new QParam(name, QType.Qubit, int.Parse(size)) { Span = span };
        return new QParam(name, QType.Qubit, null) { Span = span };
    }

    private static QStmt LowerStmt(AstNonTerminal node) => node.Name switch
    {
        "Use" => new QUse(Child(node, 0), int.Parse(Child(node, 1))),
        "Gate" => LowerGate(node),
        "ConstDecl" => LowerDecl(node, isConst: true),
        "VarDecl" => LowerDecl(node, isConst: false),
        "Assign" => new QAssign(FirstIdent(node), LowerExpr(ExprOf(node))),
        "If" => LowerIf(node),
        "For" => LowerFor(node),
        "While" => new QWhile(LowerCondition(CondOf(node)), BodyStmts(node).Select(LowerSpanned).ToList()),
        "Repeat" => new QRepeat(BodyStmts(node).Select(LowerSpanned).ToList(), LowerCondition(CondOf(node))),
        _ => new QGate(new List<string>(), node.Name, new List<QArg>()), // defensive: unknown node -> inert
    };

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
            if (nt.Name == "Qubit") return new QQubitArg(Child(nt, 0), Child(nt, 1));
            if (nt.Name == "Expr")
            {
                var (text, hasCall) = RenderExpr(nt);
                return new QTextArg(text, hasCall);
            }
        }
        return new QTextArg(sym.ToString() ?? string.Empty);
    }

    private static QDecl LowerDecl(AstNonTerminal node, bool isConst)
    {
        var name = DeclName(node);
        var type = DeclType(node) switch { "int" => (QType?)QType.Int, "bit" => QType.Bit, _ => null };
        return new QDecl(isConst, type, name, LowerExpr(ExprOf(node)));
    }

    private static QIf LowerIf(AstNonTerminal node)
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
            (elseIdx < 0 || k < elseIdx ? thenStmts : elseStmts).Add(LowerSpanned(nt));
        }

        return new QIf(LowerCondition(CondOf(node)), thenStmts, elseStmts);
    }

    private static QFor LowerFor(AstNonTerminal node)
    {
        var f = node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        return new QFor(At(f, 0), At(f, 1), At(f, 2), BodyStmts(node).Select(LowerSpanned).ToList());
    }

    // A decl/assign RHS. The only legal call form is a LONE call to the registered measurement
    // function (`bit r = M(q[i]);`), which becomes QMeasure — exact name, no aliases. Any other call —
    // a different name, or a call mixed with arithmetic — has no OpenQASM lowering: keep the full
    // rendered text and set HasCall so the validator rejects it. (Nothing is silently dropped: the old
    // code truncated `M(q) + 1` to just the measure, and read ANY call name as a measurement.)
    private static QExpr LowerExpr(AstNonTerminal? expr)
    {
        if (expr is null) return new QText(string.Empty);

        var call = expr.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Call");
        if (call is null)
            return new QText(string.Join(" ", expr.Items.OfType<AstTerminal>().Select(t => t.ToString())));

        var isLoneCall = expr.Items.Count == 1;
        if (isLoneCall && CallName(call) == QoraGates.Measurement)
        {
            var qref = call.Items.OfType<AstNonTerminal>().FirstOrDefault(q => q.Name == "Qubit");
            return new QMeasure(qref is null ? null : new QQubitArg(Child(qref, 0), Child(qref, 1)));
        }
        return new QText(RenderItems(expr), HasCall: true);
    }

    /// <summary>Render an expression to text, reporting whether a call appeared anywhere inside it.</summary>
    private static (string Text, bool HasCall) RenderExpr(AstNonTerminal expr)
    {
        var hasCall = expr.Items.OfType<AstNonTerminal>().Any(n => n.Name == "Call");
        return (RenderItems(expr), hasCall);
    }

    private static QCond LowerCondition(AstNonTerminal? cond)
    {
        if (cond is null) return new QCond(string.Empty);

        var parts = new List<string>();
        var hasCall = false;
        var negateNext = false;   // a `!` binds to the WHOLE following expr in Qora's grammar
        foreach (var item in cond.Items)
        {
            if (item is AstNonTerminal nt && nt.Name == "Expr")
            {
                var (text, call) = RenderExpr(nt);
                // Qora's `!expr` negates the whole expression, but the flat re-parsed QASM would bind
                // `!` to the first token only (`! a + 1` -> (!a)+1) — parenthesize to keep the meaning.
                parts.Add(negateNext ? $"({text})" : text);
                negateNext = false;
                hasCall |= call;
            }
            else
            {
                var tok = item.ToString() ?? string.Empty;
                parts.Add(tok);
                negateNext = tok == "!";
            }
        }
        return new QCond(string.Join(" ", parts), hasCall);
    }

    private static string RenderItems(AstNonTerminal expr) =>
        string.Join(" ", expr.Items.Select(item => item switch
        {
            AstNonTerminal { Name: "Call" } call => RenderCall(call),
            AstNonTerminal nt => nt.ToString() ?? string.Empty,
            _ => item.ToString() ?? string.Empty,
        }));

    private static string RenderCall(AstNonTerminal call)
    {
        var qref = call.Items.OfType<AstNonTerminal>().FirstOrDefault(q => q.Name == "Qubit");
        return qref is null ? $"{CallName(call)}()" : $"{CallName(call)}({Child(qref, 0)}[{Child(qref, 1)}])";
    }

    private static string CallName(AstNonTerminal call) =>
        call.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    // --- helpers (ported from the old emitter's AST accessors) ---

    private static string OpName(AstNonTerminal op) =>
        op.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static IEnumerable<AstNonTerminal> Params(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Param");

    private static IEnumerable<AstNonTerminal> Body(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Param");

    private static IEnumerable<AstNonTerminal> BodyStmts(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Condition");

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
            .FirstOrDefault(t => TypeKeywords.Contains(t));

    private static bool IsNumber(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static string Child(AstNonTerminal node, int i) =>
        i < node.Items.Count ? node.Items[i].ToString() ?? string.Empty : string.Empty;

    private static string At(List<string> list, int i) =>
        i < list.Count ? (list[i] ?? string.Empty) : string.Empty;
}
