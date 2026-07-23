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
///
/// Second job — <b>the bit-register cast</b>: Qora's <c>AsInt(f)</c> becomes OpenQASM's width-qualified
/// unsigned cast <c>uint[N](f)</c>. The width is MANDATORY: the spec allows <c>bit[n]</c> → <c>uint[m]</c>
/// only when <c>n == m</c>. The cast must be UNSIGNED because <c>int()</c> is two's complement, so a register
/// with its top bit set reads negative (<c>uint("10") == 2</c> but <c>int("10") == -2</c> — both verified on
/// the execution oracle). This lowering runs LAST among the IR→IR steps precisely so the synthesized cast is
/// minted after <see cref="ReferentialCheck"/>, and so every register already carries a literal width.
/// </summary>
public static class OpenQasmLowering
{
    public static QProgram Run(QProgram program) =>
        program with { Operations = program.Operations.Select(LowerOperation).ToList() };

    /// <summary>Lower one operation. The register WIDTHS the cast needs come from a symbol table built over
    /// THIS tree — the post-hoisting, post-mangling one actually being emitted. It is built here rather than
    /// carried from validation because hoisting mints declarations validation never saw, and because a width
    /// is a per-DECLARATION fact: two same-named registers in disjoint blocks are different arrays with
    /// different widths, so only a scope-aware lookup can pick the right one.</summary>
    private static QOperation LowerOperation(QOperation op)
    {
        var scopeOf = new Dictionary<IReadOnlyList<QStmt>, Scope>();
        var sink = new List<QoraError>();   // the program is already validated; a rebuild's diagnostics are noise
        var root = SymbolTableBuilder.Build(op, sink, scopeOf);
        return op with { Body = Lower(op.Body, root, scopeOf) };
    }

    private static IReadOnlyList<QStmt> Lower(IReadOnlyList<QStmt> stmts, Scope scope,
        Dictionary<IReadOnlyList<QStmt>, Scope> scopeOf) =>
        stmts.Select(s => LowerStmt(s, scope, scopeOf)).ToList();

    /// <summary>A nested body's own scope, resolved exactly as the validator resolves it (<c>Ctx.ScopeOf</c>);
    /// falling back to the enclosing scope keeps a hand-built tree working.</summary>
    private static Scope Child(IReadOnlyList<QStmt> body, Scope scope,
        Dictionary<IReadOnlyList<QStmt>, Scope> scopeOf) =>
        scopeOf.TryGetValue(body, out var s) ? s : scope;

    private static QStmt LowerStmt(QStmt s, Scope scope, Dictionary<IReadOnlyList<QStmt>, Scope> scopeOf)
    {
        // Every expression the statement carries is cast-lowered first, so no position can be forgotten —
        // these are exactly the shapes QNodes.ExpressionSites enumerates.
        s = s switch
        {
            QDecl d => d with { Value = LowerValue(d.Value, scope) },
            QAssign a => a with { Index = Cast(a.Index, scope), Value = LowerValue(a.Value, scope) },
            QReturn r => r with { Value = LowerValue(r.Value, scope) },
            QIf i => i with { Cond = i.Cond with { Tree = Cast(i.Cond.Tree, scope) } },
            QWhile w => w with { Cond = w.Cond with { Tree = Cast(w.Cond.Tree, scope) } },
            QRepeat r => r with { Until = r.Until with { Tree = Cast(r.Until.Tree, scope) } },
            QFor f => f with { From = Cast(f.From, scope)!, To = Cast(f.To, scope)!, Step = Cast(f.Step, scope) },
            QGate g => g with { Args = g.Args.Select(a => LowerArg(a, scope)).ToList() },
            _ => s,
        };

        return s switch
        {
            // A `const` whose initializer is not compile-time-constant cannot carry OpenQASM's `const` keyword —
            // demote it to a plain declaration (its immutability is already guaranteed by QSEM024).
            QDecl { IsConst: true } d when !IsCompileTimeConstant(d.Value) => d with { IsConst = false },
            QIf i => i with
            {
                Then = Lower(i.Then, Child(i.Then, scope, scopeOf), scopeOf),
                Else = Lower(i.Else, Child(i.Else, scope, scopeOf), scopeOf),
            },
            QFor f => f with { Body = Lower(f.Body, Child(f.Body, scope, scopeOf), scopeOf) },
            QWhile w => w with { Body = Lower(w.Body, Child(w.Body, scope, scopeOf), scopeOf) },
            QRepeat r => r with { Body = Lower(r.Body, Child(r.Body, scope, scopeOf), scopeOf) },
            QConjugate c => c with
            {
                Within = Lower(c.Within, Child(c.Within, scope, scopeOf), scopeOf),
                Apply = Lower(c.Apply, Child(c.Apply, scope, scopeOf), scopeOf),
            },
            _ => s,
        };
    }

    private static QExpr LowerValue(QExpr value, Scope scope) => value switch
    {
        QText t => t with { Tree = Cast(t.Tree, scope) },
        QArrayLiteral l => l with { Elements = l.Elements.Select(e => LowerValue(e, scope)).ToList() },
        _ => value,   // QMeasure (a target, not a computed value) and QArrayNew hold no castable expression
    };

    private static QArg LowerArg(QArg arg, Scope scope) => arg switch
    {
        QTextArg t => t with { Tree = Cast(t.Tree, scope) },
        QQubitArg q => q with { Index = Cast(q.Index, scope)! },
        _ => arg,
    };

    /// <summary>Rewrite every built-in function call in a tree to its OpenQASM form. The width comes from the
    /// register's own declaration through the scope chain, so the NEAREST binding wins. The cast is spelled as
    /// a call node named <c>uint[N]</c> because that IS OpenQASM's cast syntax — a type name applied to a
    /// value — so it prints correctly with no rendering rule of its own.</summary>
    private static QNode? Cast(QNode? node, Scope scope) => node switch
    {
        null or QNumLit or QLit or QNameRef or QMember => node,
        QUnary u => u with { Operand = Cast(u.Operand, scope)! },
        QBinOp b => b with { Left = Cast(b.Left, scope)!, Right = Cast(b.Right, scope)! },
        QIndexNode i => i with { Base = Cast(i.Base, scope)!, Index = Cast(i.Index, scope)! },
        QCallNode c when QoraGates.Functions.ContainsKey(c.Name) => new QCallNode(CastName(c, scope), c.Args),
        QCallNode c => c with { Args = c.Args.Select(a => Cast(a, scope)!).ToList() },
        _ => node,
    };

    private static string CastName(QCallNode c, Scope scope)
    {
        // QSEM006 guarantees the argument is a whole `bit[]` register, and the front end guarantees every
        // register's width is a source literal (QSEM010/016/029 plus monomorphization). A missing width would
        // mean the tree no longer matches what validation approved — so it is loud, never a silent `uint()`.
        var width = c.Args is [QNameRef r] ? scope.Lookup(r.Name)?.ArrayLength : null;
        if (width is not int n)
            throw new InvalidOperationException(
                $"QINTERNAL: `{QoraGates.BitsAsInt}({QNodes.Render(c.Args.FirstOrDefault())})` reached OpenQASM lowering without a known register width");
        return $"uint[{n}]";
    }

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
        QCallNode c => SymbolTableBuilder.IsReservedName(c.Name) && c.Args.All(AllNamesReserved),
        _ => true,
    };
}
