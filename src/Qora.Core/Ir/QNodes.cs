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
        // A measurement glues its single index argument (the historical RenderCall shape, `M(q[0])`),
        // unlike the spaced bare index form; a function call renders its arguments comma-separated.
        QCallNode { Args: [QIndexNode idx] } c => $"{c.Name}({Render(idx.Base)}[{Render(idx.Index)}])",
        QCallNode c => $"{c.Name}({string.Join(", ", c.Args.Select(Render))})",
        _ => string.Empty,
    };

    // ---- measurement target accessors ----
    //
    // A QMeasure target is the IR's canonical reference form: QNameRef (a whole single qubit, `M(a)`) or
    // QIndexNode (a register element, `M(q[i])`). These two accessors are the ONE place that splits it, so
    // no consumer re-derives "which register" / "which element" from a spelling or a shape of its own.

    /// <summary>The qubit/register NAME a measurement target refers to: <c>q[i]</c> → <c>q</c>, <c>a</c> → <c>a</c>.</summary>
    public static string RegOf(QNode target) => target switch
    {
        QIndexNode { Base: QNameRef r } => r.Name,
        QNameRef r => r.Name,
        _ => string.Empty,
    };

    /// <summary>The element index of a measurement target, or NULL when it names a whole single qubit.</summary>
    public static QNode? IndexOf(QNode target) => target is QIndexNode i ? i.Index : null;

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

    /// <summary>Every call node in a tree, in pre-order — so the validator can resolve each against the
    /// function table (a function call is a legal value; an operation/unknown call, or a stray measurement,
    /// is not). Recurses through call arguments too, so a call nested in another call's arguments is seen.</summary>
    public static IEnumerable<QCallNode> CallsIn(QNode? node)
    {
        switch (node)
        {
            case null: yield break;
            case QCallNode c:
                yield return c;
                foreach (var a in c.Args)
                    foreach (var nested in CallsIn(a)) yield return nested;
                break;
            case QUnary u:
                foreach (var n in CallsIn(u.Operand)) yield return n;
                break;
            case QBinOp b:
                foreach (var n in CallsIn(b.Left)) yield return n;
                foreach (var n in CallsIn(b.Right)) yield return n;
                break;
            case QMember m:
                foreach (var n in CallsIn(m.Base)) yield return n;
                break;
            case QIndexNode i:
                foreach (var n in CallsIn(i.Base)) yield return n;
                foreach (var n in CallsIn(i.Index)) yield return n;
                break;
        }
    }

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
            case QReturn r:
                foreach (var t in TreesOf(r.Value)) yield return t;
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
            case QMeasure m:
                yield return m.Target;
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
