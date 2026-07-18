namespace Qora.Ir.Passes;

/// <summary>What an integer expression folds to. <see cref="BoundNum"/>: every leaf resolved (integer
/// literals, <c>const</c> names, <c>.Count</c> of a known-length array) — the value is definite.
/// <see cref="BoundCount"/>: a linear form <c>Coeff·Array.Count + Offset</c> over exactly ONE array whose
/// length is not yet known — still judgeable symbolically (e.g. <c>a.Count-1</c> is in range for ANY length
/// of <c>a</c>). Anything else — a runtime variable, two unknown lengths, division by a symbol — does not
/// settle, and <see cref="BoundFolder.Fold"/> returns null: no value, no proof.</summary>
internal abstract record Bound;
internal sealed record BoundNum(long Value) : Bound;
internal sealed record BoundCount(string Array, long Coeff, long Offset) : Bound;

/// <summary>
/// THE one calculator for compile-time integer expressions — <c>+ - * /</c>, integer literals, <c>const</c>
/// names, <c>&lt;array&gt;.Count</c> — over the parsed <see cref="QNode"/> tree (built once at lowering, see
/// <see cref="ExprTree"/>): <see cref="SymbolTableBuilder"/> folds each <c>const</c>'s initializer at its
/// DECLARATION (the value is then <see cref="Symbol.FoldedBound"/> data), and <see cref="QoraValidator"/>
/// folds loop bounds and index expressions. Reading one tree — never re-parsing text — means no two
/// readings of one expression can disagree. The criterion is "does the computation settle?", never a
/// syntactic pattern: <c>a.Count*2 - k - 3</c> folds to a number when <c>a</c>'s length and <c>k</c> are
/// known, to a <see cref="BoundCount"/> when only the length is missing, and to null past that.
/// </summary>
internal static class BoundFolder
{
    /// <summary>Fold an expression tree to a <see cref="Bound"/> (or null if it does not settle). Names
    /// resolve through <paramref name="scope"/> at fold time, so a shadowed name is the nearest binding and
    /// a const carries its own already-folded value (possibly symbolic — a <c>.Count</c>).</summary>
    internal static Bound? Fold(QNode? node, Scope scope) => node switch
    {
        QNumLit n => new BoundNum(n.Value),
        // `<array>.Count`: a known length is a number; an unknown one (a parameter) stays symbolic.
        QMember { Base: QNameRef arr, Member: "Count" } =>
            scope.Lookup(arr.Name) is { IsArray: true } a
                ? (a.Type == QType.Qubit ? a.RegisterSize : a.ArrayLength) is int len
                    ? new BoundNum(len) : new BoundCount(arr.Name, 1, 0)
                : null,
        // A const reads the value its DECLARATION already folded (owner's site) — no re-derivation, and it
        // may be symbolic, so `const hi = q.Count; 0..hi` carries the same BoundCount the direct form does.
        QNameRef r => scope.Lookup(r.Name) is { IsConst: true, FoldedBound: { } fb } ? fb : null,
        QUnary { Op: "-", Operand: { } op } => Apply(new BoundNum(0), "-", Fold(op, scope)),
        QBinOp b when b.Op is "+" or "-" or "*" or "/" => Apply(Fold(b.Left, scope), b.Op, Fold(b.Right, scope)),
        _ => null,   // a float literal, an index/call, a comparison/boolean op, a runtime variable: no value
    };

    /// <summary>Fold a bare index token — the grammar restricts an index to a number or a single identifier,
    /// so there is no arithmetic to parse: a digit run is its value, a <c>const</c> its folded value, and a
    /// runtime name is null. A thin front for the atomic index position (no expression tree there yet).</summary>
    internal static Bound? FoldAtom(string atom, Scope scope)
    {
        var t = atom.Trim();
        if (t.Length > 0 && t.All(char.IsDigit))
            return long.TryParse(t, out var v) ? new BoundNum(v) : null;
        return scope.Lookup(t) is { IsConst: true, FoldedBound: { } fb } ? fb : null;
    }

    private static Bound? Apply(Bound? l, string op, Bound? r)
    {
        if (l is null || r is null) return null;
        // 64-bit CHECKED arithmetic: a computation that wraps around is not a value, and treating it as one
        // turned a four-billion-iteration loop into a "provably empty" one. Overflow past long simply does
        // not settle (null) — no proof, rejected, never silently wrong.
        try
        {
            checked
            {
                return (l, r, op) switch
                {
                    (BoundNum a, BoundNum b, "+") => new BoundNum(a.Value + b.Value),
                    (BoundNum a, BoundNum b, "-") => new BoundNum(a.Value - b.Value),
                    (BoundNum a, BoundNum b, "*") => new BoundNum(a.Value * b.Value),
                    (BoundNum a, BoundNum { Value: not 0 } b, "/") => new BoundNum(a.Value / b.Value),
                    (BoundCount c, BoundNum b, "+") => Norm(c.Array, c.Coeff, c.Offset + b.Value),
                    (BoundNum a, BoundCount c, "+") => Norm(c.Array, c.Coeff, a.Value + c.Offset),
                    (BoundCount c, BoundNum b, "-") => Norm(c.Array, c.Coeff, c.Offset - b.Value),
                    (BoundNum a, BoundCount c, "-") => Norm(c.Array, -c.Coeff, a.Value - c.Offset),
                    (BoundCount c, BoundNum b, "*") => Norm(c.Array, c.Coeff * b.Value, c.Offset * b.Value),
                    (BoundNum a, BoundCount c, "*") => Norm(c.Array, a.Value * c.Coeff, a.Value * c.Offset),
                    (BoundCount a, BoundCount b, "+") when a.Array == b.Array => Norm(a.Array, a.Coeff + b.Coeff, a.Offset + b.Offset),
                    (BoundCount a, BoundCount b, "-") when a.Array == b.Array => Norm(a.Array, a.Coeff - b.Coeff, a.Offset - b.Offset),
                    _ => null,   // Count·Count, mixed arrays, division by/of a symbol: does not settle
                };
            }
        }
        catch (System.OverflowException)
        {
            return null;
        }

        static Bound Norm(string array, long coeff, long offset) =>
            coeff == 0 ? new BoundNum(offset) : new BoundCount(array, coeff, offset);
    }

    /// <summary>True when a folded bound is <c>k·q.Count + c</c> over an UNSIZED Qubit[] parameter — the one
    /// case a loop-bound access defers to the post-monomorphization pass (the size becomes concrete per call
    /// site). Reading the folded bound instead of a text regex sees through a const: <c>const hi = q.Count</c>
    /// used as a bound defers exactly as the direct <c>q.Count</c> does.</summary>
    internal static bool DefersToUnsizedQubit(Bound? b, Scope scope) =>
        b is BoundCount c && scope.Lookup(c.Array) is { IsArray: true, Type: QType.Qubit, RegisterSize: null };
}
