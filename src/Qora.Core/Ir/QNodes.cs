using System.Globalization;

namespace Qora.Ir;

/// <summary>
/// Tree-side utilities shared by every pass: the CANONICAL renderer (tree → tokenizer-spaced text) and
/// structural predicates. The renderer is the single definition of "what an expression looks like as
/// text" — diagnostics, the IR printer, and any pass that needs a spelling call this ONE function, so a
/// tree and its rendering can never disagree the way two independently-maintained ledgers could.
///
/// The output reproduces, byte for byte, the spelling lowering historically stored: space-joined tokens
/// (<c>a . Count - 1</c>), the spaced index form (<c>q [ i ]</c>), the glued call form (<c>M(q[0])</c>),
/// and the space-padded negation wrapper (<c>! ( x )</c>). No precedence parentheses are inserted: trees
/// come from a paren-free grammar and every rewrite pass substitutes LEAVES only (a name, a
/// <c>.Count</c>), so an in-order token render re-parses to exactly the same structure. A pass that ever
/// synthesized a structure-bearing subtree (say a sum under a product) would need this renderer taught
/// parenthesization first — asserted nowhere because no such pass exists.
/// </summary>
public static class QNodes
{
    /// <summary>Render a tree to its canonical tokenizer-spaced text. Null renders as the empty string
    /// (the shape of an absent initializer / empty condition).</summary>
    public static string Render(QNode? node) => node switch
    {
        null => string.Empty,
        QNumLit n => n.Value.ToString(CultureInfo.InvariantCulture),
        QLit l => l.Text,
        QNameRef r => r.Name,
        QMember m => $"{Render(m.Base)} . {m.Member}",
        // `!` wraps its operand in space-padded parens — the flat re-parsed QASM would otherwise bind `!`
        // to the first token only; the padding keeps every identifier a standalone token (see LowerCondition).
        QUnary { Op: "!" } u => $"! ( {Render(u.Operand)} )",
        QUnary u => $"{u.Op} {Render(u.Operand)}",
        QBinOp b => $"{Render(b.Left)} {b.Op} {Render(b.Right)}",
        QIndexNode i => $"{Render(i.Base)} [ {Render(i.Index)} ]",
        // calls glue their argument (the historical RenderCall shape), unlike the spaced bare index form.
        QCallNode { Arg: QIndexNode idx } c => $"{c.Name}({Render(idx.Base)}[{Render(idx.Index)}])",
        QCallNode { Arg: null } c => $"{c.Name}()",
        QCallNode c => $"{c.Name}({Render(c.Arg)})",
        _ => string.Empty,
    };

    /// <summary>True when the tree contains a call node anywhere — the structural form of the historical
    /// <c>HasCall</c> flag (a flag can contradict its content; the walk cannot).</summary>
    public static bool ContainsCall(QNode? node) => node switch
    {
        null => false,
        QCallNode => true,
        QUnary u => ContainsCall(u.Operand),
        QBinOp b => ContainsCall(b.Left) || ContainsCall(b.Right),
        QMember m => ContainsCall(m.Base),
        QIndexNode i => ContainsCall(i.Base) || ContainsCall(i.Index),
        _ => false,
    };

    // ---- the canonical "where do expressions live" enumeration ----
    //
    // Historically every flat checker (the depth guard, the referential check, the `.Count` scan, the
    // call-position check) enumerated the expression-bearing positions of a statement BY HAND — and each
    // hand-rolled list could miss one (two of them missed array-literal elements, adversarial round 9).
    // These two iterators are the ONE list. A checker that consumes them cannot forget a position; a new
    // expression-bearing field added to the IR gets wired here ONCE and every consumer sees it.
    //
    // Deliberately NOT used by the scope-sensitive walks (the symbol table, the validator's flow walk):
    // there, each position pairs with its own scope and flags (an if-condition resolves in the condition
    // scope, a for-bound in the ENCLOSING scope, an until in the body scope) — that per-position pairing
    // is semantics, not duplication, so those walks keep their explicit cases.

    /// <summary>Every expression tree a statement holds DIRECTLY — bounds, condition, values, argument
    /// trees, index atoms. Nested statement BODIES are not entered (walk those yourself).</summary>
    public static IEnumerable<QNode> ExpressionSites(QStmt s)
    {
        switch (s)
        {
            case QGate g:
                foreach (var a in g.Args)
                    switch (a)
                    {
                        case QTextArg { Tree: { } t }: yield return t; break;
                        case QQubitArg q: yield return q.Index; break;
                    }
                break;
            case QDecl d:
                foreach (var t in TreesOf(d.Value)) yield return t;
                break;
            case QAssign a:
                if (a.Index is { } idx) yield return idx;
                foreach (var t in TreesOf(a.Value)) yield return t;
                break;
            case QIf i:
                if (i.Cond.Tree is { } c) yield return c;
                break;
            case QWhile w:
                if (w.Cond.Tree is { } wc) yield return wc;
                break;
            case QRepeat r:
                if (r.Until.Tree is { } u) yield return u;
                break;
            case QFor f:
                yield return f.From;
                yield return f.To;
                if (f.Step is { } st) yield return st;
                break;
            // QUse and QConjugate carry no expressions of their own (a conjugate is bodies only).
        }
    }

    /// <summary>Every expression tree inside a value (a decl initializer / assign RHS): the expression
    /// itself, a measure target's index atom, or each element of an array literal — recursively, so no
    /// checker can see the literal and miss what its elements hold.</summary>
    public static IEnumerable<QNode> TreesOf(QExpr value)
    {
        switch (value)
        {
            case QText { Tree: { } t }:
                yield return t;
                break;
            case QMeasure { Target: { } target }:
                yield return target.Index;
                break;
            case QArrayLiteral literal:
                foreach (var element in literal.Elements)
                    foreach (var t in TreesOf(element))
                        yield return t;
                break;
            // QArrayNew: a type and a literal length — no expressions.
        }
    }
}
