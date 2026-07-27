using Qora.Compiler;
using Qora.Ir.Passes;

namespace Qora.Ir;

/// <summary>
/// The OpenQASM 3 BACKEND — the pipeline's TARGET half, composed in one place. This backend currently
/// consumes validated, specialized HIR; it does not consume MIR and must not be presented as a MIR
/// backend until a dedicated MIR-to-target lowering replaces this boundary. The conceptual pipeline is
///
///   COMMON front end (parse → lower → import-expand → resolve → validate → specialize → re-validate
///                     → effect analysis → materialize within/apply + Adjoint into real defs;
///                     owned by <see cref="Qora.Compiler.QoraCompiler"/> — its output contract is a validated,
///                     monomorphized, MATERIALIZED program)
///   → TARGET lowering (THIS sequence: adapt that program to what OpenQASM 3 can express)
///   → EMISSION       (<see cref="QasmEmitter"/> — a pure printer, the last step below).
///
/// The folder test is DELETABILITY: a pass lives in <c>Ir/Qasm</c> if and only if a change to the
/// OpenQASM spec (or retiring the backend) would delete it — each pass's header cites the constraint it
/// neutralizes, and everything here is called from this facade alone. Passes that OpenQASM merely
/// MOTIVATED but the language now relies on regardless of target live in the common <c>Passes/</c>
/// instead: <see cref="Passes.MeasureConditionLowering"/> (the call-free-condition IR invariant every
/// validator checker leans on) and <see cref="Passes.Monomorphizer"/> (callable specialization and
/// dead-generic elimination). In contrast, replacing a known register <c>.Count</c> with a literal is
/// owned solely by <see cref="OpenQasmKnownCountLowering"/>. A future QIR backend would be a sibling of
/// this class composing its own sequence; the common front end is shared as-is.
/// </summary>
internal static class QasmBackend
{
    internal sealed class Result
    {
        private Result(
            HirSnapshot source,
            HirSemanticArtifact semanticBasis,
            OpenQasmTargetProgram? target,
            IReadOnlyList<QoraError> errors)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            SemanticBasis = semanticBasis
                ?? throw new ArgumentNullException(nameof(semanticBasis));
            Target = target;
            ArgumentNullException.ThrowIfNull(errors);
            Errors = Array.AsReadOnly(errors.ToArray());
        }

        public HirSnapshot Source { get; }
        public HirSemanticArtifact SemanticBasis { get; }
        public OpenQasmTargetProgram? Target { get; }
        public IReadOnlyList<QoraError> Errors { get; }
        public bool Success => Target is not null && Errors.Count == 0;

        private static Result Succeeded(
            HirSemanticContext semantics,
            OpenQasmTargetProgram target) =>
            new(
                semantics.Current,
                semantics.SemanticBasis,
                target,
                Array.Empty<QoraError>());

        private static Result Failed(
            HirSemanticContext semantics,
            IReadOnlyList<QoraError> errors) =>
            new(
                semantics.Current,
                semantics.SemanticBasis,
                null,
                errors);

        /// <summary>
        /// Execute the whole backend inside the only type which can construct a result. The outer facade
        /// delegates here instead of widening either the constructor or the success/failure factories.
        /// Returning through an out parameter keeps this method from becoming another result factory that
        /// callers could use to assemble a detached source/semantic/target tuple.
        /// </summary>
        internal static void Execute(
            HirSemanticContext semantics,
            IReadOnlyList<string> materializationNotes,
            out Result result)
        {
            ArgumentNullException.ThrowIfNull(semantics);
            ArgumentNullException.ThrowIfNull(materializationNotes);
            var program = semantics.Current.Program;

            // 1. OpenQASM has no runtime bounds-failure channel, so reject every unresolved site before
            //    target rewrites can move it away from its original source span.
            var boundsErrors = ValidatePolicy(semantics.SourceModel);
            if (boundsErrors.Count > 0)
            {
                result = Failed(semantics, boundsErrors);
                return;
            }

            // 2. Fixed-width OpenQASM registers need their known Count represented as a target literal.
            var targetProgram = OpenQasmKnownCountLowering.Run(program, semantics);

            // 3. Lower target casts and const semantics while node IDs still match the exact validation
            //    model.
            targetProgram = OpenQasmLowering.Run(targetProgram, semantics);

            // 4. Give each function one final return.
            var flattened = ReturnFlattening.Run(targetProgram);

            // 5. Thread def-local arrays through hidden array-reference parameters backed by globals.
            var hoisted = ArrayLocalHoisting.Run(flattened.Program);
            var targetFacts = OpenQasmTargetFacts.Merge(
                flattened.Facts,
                hoisted.Facts);

            // 6. Freeze one collision-free target name for every symbol.
            var mangled = NameMangler.Mangle(hoisted.Program);

            // 7. Reject dangling target references as compiler-internal inconsistencies.
            var refErrors = ReferentialCheck.Verify(mangled.Program);
            if (refErrors.Count > 0)
            {
                result = Failed(semantics, refErrors);
                return;
            }

            // 8. Freeze every classical type needed by emission into the target artifact.
            var typeBuild = OpenQasmTypeEnvironment.Build(
                mangled.Program,
                semantics,
                targetFacts);
            if (typeBuild.Environment is null)
            {
                result = Failed(semantics, typeBuild.Errors);
                return;
            }

            var notes = materializationNotes
                .Concat(hoisted.Notes)
                .Concat(mangled.Notes);
            var target = new OpenQasmTargetProgram(
                mangled.Program,
                mangled.Symbols,
                typeBuild.Environment,
                targetFacts,
                notes);
            result = Succeeded(semantics, target);
        }
    }

    /// <summary>Adapt the front end's output — a validated, monomorphized, materialized program — to
    /// OpenQASM and emit it. <paramref name="materializationNotes"/> are the front's rename notes
    /// (synthesized inverse defs), surfaced in the QASM header alongside the mangler's own. Errors
    /// returned here are target-policy diagnostics (such as QSEM030) or QINTERNAL consistency failures.
    /// The successful result retains the exact HIR and semantic objects consumed by this run.</summary>
    public static Result Run(
        HirSemanticContext semantics,
        IReadOnlyList<string> materializationNotes)
    {
        Result.Execute(semantics, materializationNotes, out var result);
        return result;
    }

    /// <summary>
    /// Evaluate target-policy diagnostics that are meaningful even when common HIR validation found other
    /// errors. The compiler uses this on a rejected exact semantic snapshot to preserve collect-all
    /// diagnostics without attempting target lowering on an invalid program. A successful pipeline calls
    /// the same policy exactly once through <see cref="Run"/>.
    /// </summary>
    public static IReadOnlyList<QoraError> ValidatePolicy(HirSemanticModel semantics)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        return OpenQasmBoundsValidation.Run(semantics);
    }
}
