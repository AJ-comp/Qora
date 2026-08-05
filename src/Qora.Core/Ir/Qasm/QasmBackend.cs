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
    /// Lowers materialized MIR to OpenQASM. Unproven MIR bounds results are rejected before target
    /// structure recovery, so every diagnostic still resolves through its exact MIR origin.
    /// </summary>
    public static Result Run(MirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MirAdjointMaterializer.VerifyMaterialized(source);

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
}
