using Qora.Compiler;
using Qora.Ir.Mir;

namespace Qora.Ir;

/// <summary>
/// The OpenQASM backend boundary. A successful run consumes one exact, verified MIR snapshot and
/// produces a target-owned program; it never reaches back into HIR or its semantic model.
/// </summary>
internal static class QasmBackend
{
    internal sealed record Diagnostic(
        QoraError Error,
        MirOrigin Location);

    internal sealed class Result
    {
        private Result(
            MirSnapshot source,
            MirOpenQasmTargetProgram? target,
            IReadOnlyList<Diagnostic> diagnostics)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Target = target;
            ArgumentNullException.ThrowIfNull(diagnostics);
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        }

        public MirSnapshot Source { get; }
        public MirOpenQasmTargetProgram? Target { get; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; }
        public bool Success => Target is not null && Diagnostics.Count == 0;

        internal static Result Succeeded(
            MirSnapshot source,
            MirOpenQasmTargetProgram target) =>
            new(source, target, Array.Empty<Diagnostic>());

        internal static Result Failed(
            MirSnapshot source,
            IReadOnlyList<Diagnostic> diagnostics) =>
            new(source, null, diagnostics);
    }

    /// <summary>
    /// Lowers MIR to OpenQASM. Validate return widths before bounds: an unknown-width result
    /// cannot be represented even if a caller guards all of its indexed accesses.
    /// </summary>
    public static Result Run(MirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var widthErrors = ValidateReturnWidths(source);
        if (widthErrors.Count > 0)
            return Result.Failed(source, widthErrors);

        var boundsErrors = OpenQasmBoundsValidation.Run(source);
        if (boundsErrors.Count > 0)
            return Result.Failed(source, boundsErrors);

        var lowering = MirOpenQasmLowering.Lower(source);
        if (!lowering.Success)
        {
            var errors = lowering.Errors
                .Select(error => new Diagnostic(
                    new QoraError(
                        error.Message,
                        error.Code,
                        error.Origin.SourceHirOrigin.Span),
                    error.Origin))
                .ToArray();
            return Result.Failed(source, errors);
        }

        return Result.Succeeded(
            source,
            lowering.Target
            ?? throw new InvalidOperationException(
                "QINTERNAL: successful MIR OpenQASM lowering has no target program"));
    }

    private static IReadOnlyList<Diagnostic> ValidateReturnWidths(MirSnapshot source)
    {
        var errors = new List<Diagnostic>();
        foreach (var callable in source.Program.Callables)
        {
            if (callable.ReturnType is not { IsArray: true, ElementType: QType.Bit, KnownLength: null })
                continue;

            var widths = source.Analyses.ControlFlow(callable).ReachableBlocks
                .Select(id => callable.RequireBlock(id).Terminator)
                .OfType<MirReturn>()
                .Select(returned => returned.Value)
                .OfType<MirValueId>()
                .Select(id => callable.RequireValue(id).Type.KnownLength)
                .OfType<int>()
                .Distinct()
                .Order()
                .ToArray();
            var detail = widths.Length > 1
                ? $" Reachable return paths have different widths: {string.Join(", ", widths)}."
                : "";
            var error = new QoraError(
                $"operation `{callable.Name}` returns bit[], but OpenQASM requires a single " +
                $"compile-time width (bit[n]) for the return value.{detail}",
                "QASM003",
                callable.Origin.SourceHirOrigin.Span);
            errors.Add(new Diagnostic(error, callable.Origin));
        }
        return errors;
    }
}
