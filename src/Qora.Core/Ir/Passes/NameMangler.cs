namespace Qora.Ir.Passes;

/// <summary>
/// The name-mangling pass (IR→IR): appends <c>_</c> to EVERY user-defined name — operations,
/// parameters, registers, variables, loop variables, and identifier tokens inside rendered
/// expressions — right before emission.
///
/// Why: the emitted OpenQASM world has one flat global namespace already occupied by stdgates' gate
/// names (<c>s</c>, <c>t</c>, <c>x</c>, …) and the language's keywords. No built-in ends with
/// <c>_</c> and the transform is injective (<c>s</c>→<c>s_</c>, <c>s_</c>→<c>s__</c>), so a mangled
/// user name can never collide with anything — no reserved-name list, no conditional renaming, no
/// uniquify fallback. Synthesized inverse defs end in <c>adj</c> (never <c>_</c>), keeping the three
/// name spaces (built-ins / mangled user names / generated names) structurally disjoint.
///
/// What is NOT mangled: built-in gate/measurement names at call sites (they are the target world's own
/// names) and the built-in constants <c>pi</c>/<c>tau</c>/<c>euler</c> inside expressions — which is
/// also why the validator keeps declarations from shadowing those few names (the source-level meaning
/// of a token must stay unambiguous; renaming output cannot fix a resolution ambiguity).
///
/// Runs AFTER validation (errors always show the user's original names) and before the emitter, which
/// therefore stays a dumb printer. The stages view (<c>ast</c>/<c>ir</c>/<c>irInverse</c>) shows
/// original names; only the QASM stage shows the mangled ones — making the "names get encoded at the
/// boundary to a poorer world" idea (C++-style mangling) directly visible to learners.
/// </summary>
public static class NameMangler
{
    private static readonly HashSet<string> BuiltinConstants = new() { "pi", "tau", "euler" };

    public static QProgram Mangle(QProgram program)
    {
        // The entry op keeps its name: it is never emitted as a def (its body becomes the QASM top
        // level) and QSEM009 guarantees no call site references it — mangling it would only break the
        // emitter's entry lookup. Resolve the entry exactly like the emitter does.
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];
        var ops = program.Operations.Where(o => o != entry).Select(o => o.Name).ToHashSet();

        return program with
        {
            Operations = program.Operations.Select(op => MangleOp(op, ops, keepName: op == entry)).ToList(),
        };
    }

    /// <summary>
    /// The mangling transform: dots in a fully-qualified name encode as <c>__</c> plus the trailing
    /// <c>_</c> marker (<c>MyLib.Bell</c> → <c>MyLib__Bell_</c>), C++-style flattening. NOT injective
    /// across dots-vs-underscores (<c>A.F</c> and <c>A__F</c> meet at <c>A__F_</c>) — the validator's
    /// QSEM023 rejects any program where two names would actually meet, so the pipeline never emits one.
    /// Public because that check needs the exact transform.
    /// </summary>
    public static string Mangled(string name) => name.Replace(".", "__") + "_";

    private static string M(string name) => Mangled(name);

    private static QOperation MangleOp(QOperation op, HashSet<string> ops, bool keepName) => new(
        keepName ? op.Name : M(op.Name),
        op.Params.Select(p => p with { Name = M(p.Name) }).ToList(),
        MangleBody(op.Body, ops));

    private static IReadOnlyList<QStmt> MangleBody(IReadOnlyList<QStmt> stmts, HashSet<string> ops) =>
        stmts.Select(s => MangleStmt(s, ops)).ToList();

    private static QStmt MangleStmt(QStmt s, HashSet<string> ops) => s switch
    {
        QUse u => u with { Name = M(u.Name) },
        QGate g => g with
        {
            // a call to a user operation follows the op's mangled name; built-in gates keep theirs.
            Name = ops.Contains(g.Name) ? M(g.Name) : g.Name,
            Args = g.Args.Select(MangleArg).ToList(),
        },
        QDecl d => d with { Name = M(d.Name), Value = MangleExpr(d.Value) },
        QAssign a => a with { Name = M(a.Name), Value = MangleExpr(a.Value) },
        QIf i => i with { Cond = MangleCond(i.Cond), Then = MangleBody(i.Then, ops), Else = MangleBody(i.Else, ops) },
        QFor f => f with { Var = M(f.Var), Body = MangleBody(f.Body, ops) },   // bounds are numeric literals
        QWhile w => w with { Cond = MangleCond(w.Cond), Body = MangleBody(w.Body, ops) },
        QRepeat r => r with { Body = MangleBody(r.Body, ops), Until = MangleCond(r.Until) },
        QConjugate c => c with { Within = MangleBody(c.Within, ops), Apply = MangleBody(c.Apply, ops) },
        _ => s,
    };

    private static QArg MangleArg(QArg arg) => arg switch
    {
        QQubitArg q => new QQubitArg(M(q.Reg), MangleIndex(q.Index)),
        QTextArg t => t with { Text = MangleTokens(t.Text) },
        _ => arg,
    };

    private static QExpr MangleExpr(QExpr expr) => expr switch
    {
        QMeasure { Target: { } t } m => m with { Target = new QQubitArg(M(t.Reg), MangleIndex(t.Index)) },
        QText t => t with { Text = MangleTokens(t.Text) },
        _ => expr,
    };

    private static QCond MangleCond(QCond cond) => cond with { Text = MangleTokens(cond.Text) };

    /// <summary>A qubit index is a numeric literal (kept) or a loop variable (mangled).</summary>
    private static string MangleIndex(string index) =>
        index.Length > 0 && index.All(char.IsDigit) ? index : M(index);

    /// <summary>
    /// Rendered expression/condition text is space-joined tokens: mangle each identifier token except
    /// the built-in constants; numbers and operators pass through.
    /// </summary>
    private static string MangleTokens(string text) =>
        string.Join(" ", text.Split(' ').Select(tok =>
            IsIdentifier(tok) && !BuiltinConstants.Contains(tok) ? M(tok) : tok));

    private static bool IsIdentifier(string tok) =>
        tok.Length > 0 && (char.IsLetter(tok[0]) || tok[0] == '_')
                       && tok.All(c => char.IsLetterOrDigit(c) || c == '_');
}
