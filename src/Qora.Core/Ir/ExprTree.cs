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
/// </summary>
internal static class ExprTree
{
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
                var node = Expression(e);
                operands.Add(negateNext ? new QUnary("!", node ?? new QLit(string.Empty)) : node ?? new QLit(string.Empty));
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

    /// <summary>Parse an <c>Expr</c> nonterminal (a flat primary/operator run) into a tree.</summary>
    public static QNode? Expression(AstNonTerminal? expr)
    {
        if (expr is null) return null;
        var pos = 0;
        var node = ParseSum(expr.Items, ref pos);
        return pos == expr.Items.Count ? node : node;   // trailing items shouldn't occur for a well-formed Expr
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

        // || (lowest) over && over comparisons (highest of the three).
        return Fold(new[] { "||" }, (o1, p1) =>
               Fold2(o1, p1, new[] { "&&" }, (o2, p2) =>
               Fold2(o2, p2, new[] { "==", "!=", "<", "<=", ">", ">=" }, (o3, _) => o3[0])));
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
            var operand = ParseUnary(items, ref pos);
            return operand is null ? null : new QUnary("-", operand);
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
        var target = call.Items.OfType<AstNonTerminal>().FirstOrDefault(q => q.Name == "IndexAccess");
        return new QCallNode(name, target is null ? null : IndexNode(target));
    }

    /// <summary>A single index/atom token (the grammar limits an index to a number or bare identifier).</summary>
    private static QNode Atom(string text) =>
        text.Length > 0 && text.All(char.IsDigit) && long.TryParse(text, out var v)
            ? new QNumLit(v) : new QNameRef(text);

    private static bool IsOp(AstSymbol item, out string op)
    {
        op = item is AstTerminal ? item.ToString() ?? string.Empty : string.Empty;
        return item is AstTerminal && op.Length > 0 && "+-*/".Contains(op) && op.Length == 1;
    }
}
