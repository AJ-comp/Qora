namespace Qora.Ir.Passes;

/// <summary>What an integer expression folds to. <see cref="BoundNum"/>: every leaf resolved (integer
/// literals, <c>const</c> names, <c>.Count</c> of a known-length array) — the value is definite.
/// <see cref="ArrayLengthBound"/>: a linear form <c>Coeff·Array.Count + Offset</c> over exactly ONE array whose
/// length is not yet known — still judgeable symbolically (e.g. <c>a.Count-1</c> is in range for ANY length
/// of <c>a</c>). Anything else — a runtime variable, two unknown lengths, division by a symbol — does not
/// settle, and <see cref="BoundFolder.Fold"/> returns null: no value, no proof.</summary>
internal abstract record Bound;
internal sealed record BoundNum(long Value) : Bound;
internal sealed record ArrayLengthBound(
    SymbolId ArraySymbolId,
    long Coeff,
    long Offset,
    bool IsOverflowFree = true) : Bound;

/// <summary>
/// THE one calculator for compile-time integer expressions — <c>+ - * /</c>, integer literals, <c>const</c>
/// names, <c>&lt;array&gt;.Count</c> — over the parsed <see cref="QNode"/> tree (built once at lowering, see
/// <see cref="ExprTree"/>): <see cref="SymbolTableBuilder"/> folds each <c>const</c>'s initializer at its
/// DECLARATION (the value is then <see cref="Symbol.FoldedBound"/> data), and <see cref="QoraValidator"/>
/// folds loop bounds and index expressions. Reading one tree — never re-parsing text — means no two
/// readings of one expression can disagree. The criterion is "does the computation settle?", never a
/// syntactic pattern: <c>a.Count*2 - k - 3</c> folds to a number when <c>a</c>'s length and <c>k</c> are
/// known, to an <see cref="ArrayLengthBound"/> when only the length is missing, and to null past that.
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
                    ? new BoundNum(len) : new ArrayLengthBound(a.Id, 1, 0)
                : null,
        // A const reads the value its DECLARATION already folded (owner's site) — no re-derivation, and it
        // may be symbolic, so `const hi = q.Count; 0..hi` carries the same ArrayLengthBound the direct form does.
        QNameRef r => scope.Lookup(r.Name) is { IsConst: true, FoldedBound: { } fb } ? fb : null,
        QUnary { Op: "-", Operand: { } op } => Apply(new BoundNum(0), "-", Fold(op, scope)),
        QBinOp b when b.Op is "+" or "-" or "*" or "/" => Apply(Fold(b.Left, scope), b.Op, Fold(b.Right, scope)),
        _ => null,   // a float literal, an index/call, a comparison/boolean op, a runtime variable: no value
    };

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
                    (ArrayLengthBound c, BoundNum b, "+") =>
                        Norm(c.ArraySymbolId, c.Coeff, c.Offset + b.Value, c.IsOverflowFree),
                    (BoundNum a, ArrayLengthBound c, "+") =>
                        Norm(c.ArraySymbolId, c.Coeff, a.Value + c.Offset, c.IsOverflowFree),
                    (ArrayLengthBound c, BoundNum b, "-") =>
                        Norm(c.ArraySymbolId, c.Coeff, c.Offset - b.Value, c.IsOverflowFree),
                    (BoundNum a, ArrayLengthBound c, "-") =>
                        Norm(c.ArraySymbolId, -c.Coeff, a.Value - c.Offset, c.IsOverflowFree),
                    (ArrayLengthBound c, BoundNum b, "*") =>
                        Norm(c.ArraySymbolId, c.Coeff * b.Value, c.Offset * b.Value, c.IsOverflowFree),
                    (BoundNum a, ArrayLengthBound c, "*") =>
                        Norm(c.ArraySymbolId, a.Value * c.Coeff, a.Value * c.Offset, c.IsOverflowFree),
                    (ArrayLengthBound a, ArrayLengthBound b, "+") when a.ArraySymbolId == b.ArraySymbolId =>
                        Norm(a.ArraySymbolId, a.Coeff + b.Coeff, a.Offset + b.Offset,
                            a.IsOverflowFree && b.IsOverflowFree),
                    (ArrayLengthBound a, ArrayLengthBound b, "-") when a.ArraySymbolId == b.ArraySymbolId =>
                        Norm(a.ArraySymbolId, a.Coeff - b.Coeff, a.Offset - b.Offset,
                            a.IsOverflowFree && b.IsOverflowFree),
                    _ => null,   // Count·Count, mixed arrays, division by/of a symbol: does not settle
                };
            }
        }
        catch (System.OverflowException)
        {
            return null;
        }

        static Bound Norm(SymbolId arraySymbolId, long coeff, long offset, bool operandsOverflowFree)
        {
            // Algebraic cancellation must not erase an overflow that occurs earlier in source evaluation.
            // Example: `Count + long.MaxValue - long.MaxValue - 1` looks like `Count-1` only AFTER the
            // first addition has already overflowed for every positive Count. Track whether every
            // intermediate is representable for the full legal length domain; once false it stays false.
            var first = new System.Numerics.BigInteger(coeff) + offset;
            var last = new System.Numerics.BigInteger(coeff) * int.MaxValue + offset;
            var overflowFree = operandsOverflowFree
                               && System.Numerics.BigInteger.Min(first, last) >= long.MinValue
                               && System.Numerics.BigInteger.Max(first, last) <= long.MaxValue;

            // A cancelled symbolic dependency may become a number only if its complete evaluation was
            // overflow-free. Otherwise retain the originating SymbolId so monomorphization can substitute
            // the real length and re-run the ordinary checked fold.
            return coeff == 0 && overflowFree
                ? new BoundNum(offset)
                : new ArrayLengthBound(arraySymbolId, coeff, offset, overflowFree);
        }
    }

    /// <summary>True when a folded bound is <c>k·p.Count + c</c> over a parameter whose length ONLY
    /// monomorphization can supply — the cases a loop-bound access defers to the post-monomorphization
    /// pass, where the size is concrete. The judgement is the <see cref="Symbol.MonoSized"/> STAMP, which
    /// the symbol table copies once from <see cref="QParam.NeedsMonoSizing"/> — the same single answer
    /// the monomorphizer's trigger reads, so this gate can never drift from what actually specializes.
    /// Reading the folded bound instead of a text regex sees through a const: <c>const hi = q.Count</c>
    /// used as a bound defers exactly as the direct <c>q.Count</c> does.</summary>
    internal static bool DefersToUnsizedQubit(Bound? b, Scope scope) =>
        b is ArrayLengthBound c && scope.GetSymbol(c.ArraySymbolId).MonoSized;
}

/// <summary>
/// The one compile-time boolean folder used by control-flow analyses. Like <see cref="BoundFolder"/>,
/// it reads an expression tree and already-folded const data instead of reparsing source text. A null
/// result means that the condition depends on runtime state and both control-flow paths remain possible.
/// </summary>
internal static class BooleanFolder
{
    internal static bool? Fold(QNode? node, Scope scope)
    {
        switch (node)
        {
            case QLit { Text: "true" }:
                return true;
            case QLit { Text: "false" }:
                return false;
            // Surface boolean literals currently lower through the identifier-shaped node used by the
            // expression parser. They are reserved values, not lexical names.
            case QNameRef { Name: "true" }:
                return true;
            case QNameRef { Name: "false" }:
                return false;
            case QNameRef name when scope.Lookup(name.Name) is
                     { IsConst: true, FoldedBoolean: { } constValue }:
                return constValue;
            case QUnary { Op: "!", Operand: { } operand }:
                return Fold(operand, scope) is { } operandValue ? !operandValue : null;
            case QBinOp { Op: "&&" } and:
            {
                var left = Fold(and.Left, scope);
                var right = Fold(and.Right, scope);
                if (left == false || right == false) return false;
                return left == true && right == true ? true : null;
            }
            case QBinOp { Op: "||" } or:
            {
                var left = Fold(or.Left, scope);
                var right = Fold(or.Right, scope);
                if (left == true || right == true) return true;
                return left == false && right == false ? false : null;
            }
            case QBinOp comparison when comparison.Op is "==" or "!=" or "<" or "<=" or ">" or ">="
                                              && BoundFolder.Fold(comparison.Left, scope) is BoundNum left
                                              && BoundFolder.Fold(comparison.Right, scope) is BoundNum right:
                return comparison.Op switch
                {
                    "==" => left.Value == right.Value,
                    "!=" => left.Value != right.Value,
                    "<" => left.Value < right.Value,
                    "<=" => left.Value <= right.Value,
                    ">" => left.Value > right.Value,
                    ">=" => left.Value >= right.Value,
                    _ => null,
                };
            case QBinOp comparison when comparison.Op is "==" or "!=":
            {
                var left = Fold(comparison.Left, scope);
                var right = Fold(comparison.Right, scope);
                if (left is null || right is null) return null;
                return comparison.Op == "==" ? left == right : left != right;
            }
            default:
                return null;
        }
    }
}
