using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Janglim.FrontEnd.Ast;

namespace Qora.Ir;

/// <summary>
/// Builds the <see cref="QNode"/> expression tree from the engine's AST, ONCE, at lowering. The grammar
/// gives an <c>Expr</c> as a flat token run (<c>a . Count - 1</c> → <c>[a, ., Count, -, 1]</c>) with only
/// <c>IndexAccess</c> and <c>Call</c> grouped; this recovers the structure with standard operator precedence
/// (<c>* /</c> above <c>+ -</c>; comparisons above <c>&amp;&amp;</c> above <c>||</c>) — the same precedence
/// OpenQASM applies when it re-evaluates the emitted tokens, so the tree means exactly what the emitted code
/// computes. Downstream reads the tree instead of re-parsing text, so no two readings can disagree.
///
/// The parse is TOTAL over the grammar: <c>Expr</c> admits exactly the four operators <c>+ - * /</c>
/// (QoraGrammar's <c>expr</c> production) and a condition's operators are the comparisons plus
/// <c>&amp;&amp; || !</c> — every shape this parser does not recognize is grammatically unreachable. So an
/// unconsumed token, a missing operand, or an operator-shaped primary is never "input to skip": it is a
/// compiler bug (a grammar/parser drift), thrown loudly as QINTERNAL instead of silently truncating the
/// tree — a truncated tree would under-represent the text with no signal, and every tree reader downstream
/// would silently judge a shorter expression than the one emitted.
/// </summary>
internal static class ExprTree
{
    private static InvalidOperationException QInternal(string what) =>
        new($"QINTERNAL: the expression parser {what} — the grammar should make this unreachable; please report this");

    /// <summary>Parse a <c>Condition</c> node — <c>Expr</c> operands joined by comparison / boolean
    /// operators — into a tree. Flat in the grammar; precedence (<c>== …</c> tighter than <c>&amp;&amp;</c>
    /// tighter than <c>||</c>) is applied here.</summary>
    public static QNode? Condition(AstNonTerminal? cond)
    {
        if (cond is null) return null;
        // A condition alternates operands and operators: operand (op operand)*. An operand is either an
        // `Expr` nonterminal or a `!`-prefixed one (condAtom = expr | Not expr). Collect them in order.
        var operands = new List<QNode>();
        var ops = new List<string>();
        var negateNext = false;
        foreach (var item in cond.Items)
        {
            if (item is AstNonTerminal { Name: "Expr" } e)
            {
                // A condition's operand is a full Expr; the grammar guarantees it is non-empty, so a null
                // parse here is a compiler bug — never papered over with an empty placeholder node.
                var node = Expression(e) ?? throw QInternal("got an empty condition operand");
                operands.Add(negateNext ? new QUnary("!", node) : node);
                negateNext = false;
            }
            else if (item is AstTerminal t)
            {
                var tok = t.ToString() ?? string.Empty;
                if (tok == "!") negateNext = true;
                else ops.Add(tok);
            }
        }
        return operands.Count == 0 ? null : CombineBoolean(operands, ops);
    }

    /// <summary>Parse an <c>Expr</c> nonterminal (a flat primary/operator run) into a tree. Null only for an
    /// EMPTY item run (no expression at all); a PARTIAL consume — trailing items the parser did not
    /// recognize — throws instead of returning a silently truncated tree.</summary>
    public static QNode? Expression(AstNonTerminal? expr)
    {
        if (expr is null) return null;
        var pos = 0;
        var node = ParseSum(expr.Items, ref pos);
        return pos == expr.Items.Count ? node : throw QInternal(
            $"consumed only {pos} of {expr.Items.Count} tokens of an Expr");
    }

    // --- condition precedence: comparisons bind tighter than &&, which binds tighter than || ---

    private static QNode CombineBoolean(List<QNode> operands, List<string> ops)
    {
        // operands[0] op[0] operands[1] op[1] ... — fold by precedence level, lowest (||) last.
        QNode Fold(IReadOnlyList<string> level, System.Func<List<QNode>, List<string>, QNode> inner)
        {
            var groups = new List<QNode>();
            var groupOps = new List<string>();
            var current = new List<QNode> { operands[0] };
            var currentOps = new List<string>();
            for (var i = 0; i < ops.Count; i++)
            {
                if (level.Contains(ops[i]))
                {
                    groups.Add(inner(current, currentOps));
                    groupOps.Add(ops[i]);
                    current = new List<QNode> { operands[i + 1] };
                    currentOps = new List<string>();
                }
                else
                {
                    current.Add(operands[i + 1]);
                    currentOps.Add(ops[i]);
                }
            }
            groups.Add(inner(current, currentOps));
            var acc = groups[0];
            for (var i = 0; i < groupOps.Count; i++) acc = new QBinOp(groupOps[i], acc, groups[i + 1]);
            return acc;
        }

        // || (lowest) over && over equality (== !=) over relational (< <= > >=) — the SAME ladder
        // OpenQASM 3 (C-style) applies when it re-parses the emitted token run, so a mixed chain like
        // `a == b < c` means a == (b < c) in the tree exactly as it will in the executed QASM; folding
        // both comparison families at one level once made the tree claim (a == b) < c while the emitted
        // bytes computed the other grouping. Below the relationals nothing is left to split on — the
        // grammar has no further condition operators — so the innermost group must be exactly one
        // operand; more would mean an operator this parser never classified (a drift).
        return Fold(new[] { "||" }, (o1, p1) =>
               Fold2(o1, p1, new[] { "&&" }, (o2, p2) =>
               Fold2(o2, p2, new[] { "==", "!=" }, (o3, p3) =>
               Fold2(o3, p3, new[] { "<", "<=", ">", ">=" }, (o4, rest) =>
                   rest.Count == 0 && o4.Count == 1 ? o4[0]
                       : throw QInternal($"left {rest.Count} unclassified condition operator(s) ({string.Join(" ", rest)})")))));
    }

    private static QNode Fold2(List<QNode> operands, List<string> ops, IReadOnlyList<string> level,
        System.Func<List<QNode>, List<string>, QNode> inner)
    {
        var groups = new List<QNode>();
        var groupOps = new List<string>();
        var current = new List<QNode> { operands[0] };
        var currentOps = new List<string>();
        for (var i = 0; i < ops.Count; i++)
        {
            if (level.Contains(ops[i]))
            {
                groups.Add(inner(current, currentOps));
                groupOps.Add(ops[i]);
                current = new List<QNode> { operands[i + 1] };
                currentOps = new List<string>();
            }
            else
            {
                current.Add(operands[i + 1]);
                currentOps.Add(ops[i]);
            }
        }
        groups.Add(inner(current, currentOps));
        var acc = groups[0];
        for (var i = 0; i < groupOps.Count; i++) acc = new QBinOp(groupOps[i], acc, groups[i + 1]);
        return acc;
    }

    // --- expression precedence: * / above + -, over unary -, over primaries ---

    private static QNode? ParseSum(IReadOnlyList<AstSymbol> items, ref int pos)
    {
        var left = ParseProduct(items, ref pos);
        while (left is not null && pos < items.Count && IsOp(items[pos], out var op) && (op == "+" || op == "-"))
        {
            pos++;
            var right = ParseProduct(items, ref pos);
            if (right is null) return left;
            left = new QBinOp(op, left, right);
        }
        return left;
    }

    private static QNode? ParseProduct(IReadOnlyList<AstSymbol> items, ref int pos)
    {
        var left = ParseUnary(items, ref pos);
        while (left is not null && pos < items.Count && IsOp(items[pos], out var op) && (op == "*" || op == "/"))
        {
            pos++;
            var right = ParseUnary(items, ref pos);
            if (right is null) return left;
            left = new QBinOp(op, left, right);
        }
        return left;
    }

    private static QNode? ParseUnary(IReadOnlyList<AstSymbol> items, ref int pos)
    {
        if (pos < items.Count && IsOp(items[pos], out var op) && op == "-")
        {
            pos++;
            var operand = ParseUnary(items, ref pos)
                ?? throw QInternal("found a unary `-` with no operand");   // grammar always supplies one
            return new QUnary("-", operand);
        }
        return ParsePrimary(items, ref pos);
    }

    private static QNode? ParsePrimary(IReadOnlyList<AstSymbol> items, ref int pos)
    {
        if (pos >= items.Count) return null;
        var item = items[pos];

        if (item is AstNonTerminal { Name: "Call" } call) { pos++; return CallNode(call); }
        if (item is AstNonTerminal { Name: "IndexAccess" } idx) { pos++; return IndexNode(idx); }
        if (item is AstNonTerminal nt) { pos++; return Expression(nt); }   // a nested/parenthesized group, if any

        var text = item.ToString() ?? string.Empty;
        pos++;
        if (text.Length > 0 && char.IsDigit(text[0]))
            return text.All(c => char.IsDigit(c)) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                ? new QNumLit(v) : new QLit(text);   // float / too-big-for-long stays a literal (not integer-foldable)
        // A primary must be identifier-shaped from here on; an operator-shaped token in primary position
        // would previously have minted a phantom QNameRef("==") — a misparse surfacing as a wrong name
        // reference instead of a compiler bug. Grammar-unreachable, so throw.
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
            throw QInternal($"found the non-identifier token `{text}` in primary position");
        // an identifier; a following `. Count` makes it a member access
        if (pos + 1 < items.Count && (items[pos].ToString() ?? string.Empty) == "."
            && items[pos + 1] is AstTerminal member)
        {
            var m = member.ToString() ?? string.Empty;
            pos += 2;
            return new QMember(new QNameRef(text), m);
        }
        return new QNameRef(text);
    }

    private static QNode IndexNode(AstNonTerminal idx)
    {
        var baseName = idx.Items.Count > 0 ? idx.Items[0].ToString() ?? string.Empty : string.Empty;
        var indexText = idx.Items.Count > 1 ? idx.Items[1].ToString() ?? string.Empty : string.Empty;
        return new QIndexNode(new QNameRef(baseName), Atom(indexText));
    }

    private static QNode CallNode(AstNonTerminal call)
    {
        var name = call.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;
        // Grammar: `Ident(expr, …)`. Each argument is an `Expr` nonterminal — parse each to a subtree.
        // `M(q[0])`'s single argument is an `Expr` that is a lone index access, so it becomes one QIndexNode
        // (matching the measurement pattern the desugarer/validator expect: `Args: [QIndexNode …]`).
        var args = call.Items.OfType<AstNonTerminal>()
            .Where(n => n.Name == "Expr")
            .Select(Expression)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
        return new QCallNode(name, args);
    }

    /// <summary>A single index/atom token (the grammar limits an index to a number or bare identifier).
    /// Also the tree form of a bare gate argument (a whole register, an angle name) at lowering.
    /// A digit run too large for long stays a verbatim <see cref="QLit"/> (same rule as ParsePrimary) —
    /// it is a NUMBER a fortiori past any array length, never a name.</summary>
    internal static QNode Atom(string text) =>
        text.Length > 0 && text.All(char.IsDigit)
            ? long.TryParse(text, out var v) ? new QNumLit(v) : new QLit(text)
            : new QNameRef(text);

    private static bool IsOp(AstSymbol item, out string op)
    {
        op = item is AstTerminal ? item.ToString() ?? string.Empty : string.Empty;
        return item is AstTerminal && op.Length > 0 && "+-*/".Contains(op) && op.Length == 1;
    }
}
