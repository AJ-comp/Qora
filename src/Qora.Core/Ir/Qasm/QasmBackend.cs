using Qora.Ir.Passes;

namespace Qora.Ir;

/// <summary>
/// The OpenQASM 3 BACKEND — the pipeline's TARGET half, composed in one place. The conceptual pipeline is
///
///   COMMON front end (parse → lower → import-expand → resolve → validate → monomorphize → re-validate
///                     → effect analysis → materialize within/apply + Adjoint into real defs;
///                     owned by <see cref="QoraParser"/> — its output contract is a validated,
///                     monomorphized, MATERIALIZED program)
///   → TARGET lowering (THIS sequence: adapt that program to what OpenQASM 3 can express)
///   → EMISSION       (<see cref="QasmEmitter"/> — a pure printer, the last step below).
///
/// The folder test is DELETABILITY: a pass lives in <c>Ir/Qasm</c> if and only if a change to the
/// OpenQASM spec (or retiring the backend) would delete it — each pass's header cites the constraint it
/// neutralizes, and everything here is called from this facade alone. Passes that OpenQASM merely
/// MOTIVATED but the language now relies on regardless of target live in the common <c>Passes/</c>
/// instead: <see cref="Passes.MeasureConditionLowering"/> (the call-free-condition IR invariant every
/// validator checker leans on) and <see cref="Passes.Monomorphizer"/> (the B′ prover's post-mono
/// re-validation axis; any static-width hardware backend wants it too). A future QIR backend would be a
/// SIBLING of this class composing its own (much shorter) sequence; the common front end is shared as-is.
/// </summary>
public static class QasmBackend
{
    public sealed record Result(string Qasm, IReadOnlyList<QoraError> Errors);

    /// <summary>Adapt the front end's output — a validated, monomorphized, materialized program — to
    /// OpenQASM and emit it. <paramref name="materializationNotes"/> are the front's rename notes
    /// (synthesized inverse defs), surfaced in the QASM header alongside the mangler's own. Errors
    /// returned here are QINTERNAL consistency failures; an empty <see cref="Result.Qasm"/> accompanies
    /// them.</summary>
    public static Result Run(QProgram program, IReadOnlyList<string> materializationNotes, SemanticModel? semantics)
    {
        // 1. Def-local classical arrays are inexpressible in OpenQASM (arrays are global-or-parameter
        //    only, and defs cannot see mutable globals): thread each as a hidden array-reference
        //    parameter backed by a global, before any renaming so the minted names mangle like user names.
        var hoisted = ArrayLocalHoisting.Run(program);

        // 2. Map every Qora name to a valid, collision-free OpenQASM identifier (reserved words,
        //    stdgates names, QASM's flat global scope).
        var mangled = NameMangler.Mangle(hoisted.Program, semantics);

        // 3. Referential-integrity gate: after renaming, every used identifier must resolve to a
        //    declaration/op/built-in. A dangling reference is a COMPILER bug (a name not renamed
        //    consistently) — fail loudly (QINTERNAL) instead of emitting silently-broken QASM.
        var refErrors = ReferentialCheck.Verify(mangled.Program);
        if (refErrors.Count > 0) return new(string.Empty, refErrors);

        // 4. Const demotion (OpenQASM's `const` requires a compile-time initializer), then print.
        //    Hoisting, materialization and mangling all rewrite visibly; every note surfaces in the header.
        var qasm = QasmEmitter.Emit(OpenQasmLowering.Run(mangled.Program),
            materializationNotes.Concat(hoisted.Notes).Concat(mangled.Notes).ToList(), semantics);
        return new(qasm, Array.Empty<QoraError>());
    }
}
