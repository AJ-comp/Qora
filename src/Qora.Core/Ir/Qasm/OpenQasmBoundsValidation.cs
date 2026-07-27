using Qora.Ir.Passes;

namespace Qora.Ir;

/// <summary>
/// OpenQASM's disposition for the target-independent failed bounds proofs recorded in
/// <see cref="HirSemanticModel.UnprovenIndexes"/>. OpenQASM 3 has no portable runtime trap/abort path for an
/// indexed access, so an access the common validator could neither prove safe nor prove wrong cannot be
/// emitted. A future runtime-capable backend can use the category when it also supplies stable access-site
/// identity, checked-access lowering, and any required dynamic-alias policy; this diagnostic fact alone is
/// not yet a rewrite work list.
/// </summary>
internal static class OpenQasmBoundsValidation
{
    /// <summary>Turn final unresolved bounds facts into source-distinct QSEM030 diagnostics.</summary>
    public static IReadOnlyList<QoraError> Run(HirSemanticModel semantics)
    {
        if (semantics.UnprovenIndexes.Count == 0)
            return Array.Empty<QoraError>();

        return semantics.UnprovenIndexes
            .Select(ToDiagnostic)
            .Distinct()
            .ToList();
    }

    private static QoraError ToDiagnostic(UnprovenIndex unresolved)
    {
        var message = unresolved.LoopBound is { } bound
            ? $"in `{unresolved.Op}`: `{unresolved.Array}[{unresolved.Index}]` — the loop bound `{bound}` cannot be determined at compile time, so the index cannot be proven in bounds. Guard the access — `if (0 <= {unresolved.Index} && {unresolved.Index} < {unresolved.Array}.Count) {{ … }}` — or bound the loop by `{unresolved.Array}.Count-1` or a constant"
            : $"in `{unresolved.Op}`: `{unresolved.Array}[{unresolved.Index}]` uses a runtime index that cannot be proven in bounds at compile time. Evaluate it once — `var i: int = {unresolved.Index};` — then guard the access with `if (0 <= i && i < {unresolved.Array}.Count) {{ {unresolved.Array}[i] … }}`";

        return new QoraError(
            message,
            "QSEM030",
            unresolved.Span);
    }
}
