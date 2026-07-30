using Qora.Ir.Mir;

namespace Qora.Ir;

/// <summary>
/// OpenQASM's disposition for unresolved bounds-proof obligations owned by MIR. OpenQASM 3 has no
/// portable runtime trap for an invalid indexed access, so this backend rejects obligations which a
/// runtime-capable backend could instead lower to checked accesses.
/// </summary>
internal static class OpenQasmBoundsValidation
{
    public static IReadOnlyList<QoraError> Run(MirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.UnprovenBounds
            .Select(ToDiagnostic)
            .Distinct()
            .ToArray();
    }

    private static QoraError ToDiagnostic(MirBoundsObligation unresolved)
    {
        var message = unresolved.LoopBound is { } bound
            ? $"in `{unresolved.Operation}`: `{unresolved.Aggregate}[{unresolved.Index}]` — the loop bound `{bound}` cannot be determined at compile time, so the index cannot be proven in bounds. Guard the access — `if (0 <= {unresolved.Index} && {unresolved.Index} < {unresolved.Aggregate}.Count) {{ … }}` — or bound the loop by `{unresolved.Aggregate}.Count-1` or a constant"
            : $"in `{unresolved.Operation}`: `{unresolved.Aggregate}[{unresolved.Index}]` uses a runtime index that cannot be proven in bounds at compile time. Evaluate it once — `var i: int = {unresolved.Index};` — then guard the access with `if (0 <= i && i < {unresolved.Aggregate}.Count) {{ {unresolved.Aggregate}[i] … }}`";

        return new QoraError(
            message,
            "QSEM030",
            unresolved.Span);
    }
}
