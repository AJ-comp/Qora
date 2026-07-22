using System.Linq;

namespace Qora.Ir.Passes;

/// <summary>
/// TARGET lowering for OpenQASM 3 — the last IR→IR step before <see cref="QasmEmitter"/>, adapting validated
/// IR to what OpenQASM specifically supports (which differs from other targets such as QIR). It runs after
/// validation/monomorphization/mangling and changes nothing the validator relies on; it only reshapes IR to
/// dodge OpenQASM-only restrictions. A future QIR target would get its own lowering here instead.
///
/// Current job — <b>const demotion</b>: a Qora <c>const</c> is a Q#-<c>let</c>-style IMMUTABLE BINDING that
/// accepts ANY value (QSEM024), but OpenQASM's <c>const</c> requires a COMPILE-TIME-constant initializer. So
/// a <c>const</c> bound to a runtime value (<c>const c: int = x;</c>) is demoted to a plain declaration
/// (<c>int c = x;</c>) for emission. Immutability is NOT lost: the source-level check (QSEM024) already
/// forbids reassignment, so the emitted variable is never written again — effectively immutable, exactly how
/// QIR/LLVM represents such a binding (an SSA value, which is single-assignment but not a <c>constant</c>).
/// A <c>const</c> with a genuine compile-time initializer (a literal, or an expression of only pi/tau/euler)
/// keeps its <c>const</c>, which OpenQASM accepts.
/// </summary>
public static class OpenQasmLowering
{
    public static QProgram Run(QProgram program) =>
        program with { Operations = program.Operations.Select(op => op with { Body = Lower(op.Body) }).ToList() };

    private static IReadOnlyList<QStmt> Lower(IReadOnlyList<QStmt> stmts) =>
        stmts.Select(LowerStmt).ToList();

    private static QStmt LowerStmt(QStmt s) => s switch
    {
        // A `const` whose initializer is not compile-time-constant cannot carry OpenQASM's `const` keyword —
        // demote it to a plain declaration (its immutability is already guaranteed by QSEM024).
        QDecl { IsConst: true } d when !IsCompileTimeConstant(d.Value) => d with { IsConst = false },
        QIf i => i with { Then = Lower(i.Then), Else = Lower(i.Else) },
        QFor f => f with { Body = Lower(f.Body) },
        QWhile w => w with { Body = Lower(w.Body) },
        QRepeat r => r with { Body = Lower(r.Body) },
        QConjugate c => c with { Within = Lower(c.Within), Apply = Lower(c.Apply) },
        _ => s,
    };

    /// <summary>A compile-time-constant initializer: a literal or an expression whose only identifiers are
    /// built-in constants (pi/tau/euler) — no reference to a runtime variable, and not a measurement.
    /// Judged on the expression TREE: every name (a bare reference, a member and its base, a call target)
    /// must be reserved; numbers and verbatim literals are constant by nature; an absent tree is an empty
    /// initializer, vacuously constant — the same verdicts the flat identifier scan produced.</summary>
    private static bool IsCompileTimeConstant(QExpr value) =>
        value is QText t && AllNamesReserved(t.Tree);

    private static bool AllNamesReserved(QNode? node) => node switch
    {
        null or QNumLit or QLit => true,
        QNameRef r => SymbolTableBuilder.IsReservedName(r.Name),
        QMember m => AllNamesReserved(m.Base) && SymbolTableBuilder.IsReservedName(m.Member),
        QUnary u => AllNamesReserved(u.Operand),
        QBinOp b => AllNamesReserved(b.Left) && AllNamesReserved(b.Right),
        QIndexNode i => AllNamesReserved(i.Base) && AllNamesReserved(i.Index),
        QCallNode c => SymbolTableBuilder.IsReservedName(c.Name) && AllNamesReserved(c.Arg),
        _ => true,
    };
}
