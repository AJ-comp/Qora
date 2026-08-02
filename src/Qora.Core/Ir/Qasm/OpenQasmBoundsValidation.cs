using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir;

/// <summary>
/// OpenQASM's disposition for indexed accesses that MIR could not prove safe. OpenQASM 3 has no portable
/// runtime trap for an invalid indexed access, so this backend rejects unproven results which a
/// runtime-capable backend could instead lower to checked accesses.
/// </summary>
internal static class OpenQasmBoundsValidation
{
    public static IReadOnlyList<QasmBackend.Diagnostic> Run(MirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var diagnostics = new List<QasmBackend.Diagnostic>();
        var diagnosedSourceSpans = new HashSet<SourceSpan>();
        var diagnosedUnmappedHirNodes = new HashSet<HirNodeId>();

        foreach (var callable in source.Program.Callables)
        {
            var bounds = source.Analyses.Bounds(callable);
            foreach (var result in bounds.Results)
            {
                if (result.Classification != MirBoundsClassification.Unproven)
                    continue;

                var origin = bounds.OriginFor(result);
                var sourceOrigin = origin.SourceHirOrigin;
                var isFirstSourceOccurrence = sourceOrigin.Span is { } span
                    ? diagnosedSourceSpans.Add(span)
                    : diagnosedUnmappedHirNodes.Add(sourceOrigin.HirNodeId);
                if (!isFirstSourceOccurrence)
                    continue;

                diagnostics.Add(new QasmBackend.Diagnostic(
                    ToDiagnostic(callable, origin),
                    origin));
            }
        }

        return diagnostics;
    }

    private static QoraError ToDiagnostic(
        MirCallable callable,
        MirOrigin origin) =>
        new(
            $"in `{callable.Name}`: this indexed access uses a runtime value that cannot be proven in bounds at compile time. Evaluate the index once, then guard the access with `if (0 <= i && i < array.Count) {{ ... }}`",
            "QSEM030",
            origin.SourceHirOrigin.Span);
}
