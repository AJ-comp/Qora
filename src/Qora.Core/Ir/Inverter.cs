namespace Qora.Ir;

/// <summary>
/// The inversion kernel as a pure IR→IR pass: given an operation body, produce the statement list of
/// its inverse (<c>(U₁ U₂ … Uₙ)† = Uₙ† … U₂† U₁†</c>), or a human-readable reason why it has none.
/// This is the engine the whole-operation <c>Adjoint</c> feature runs on today, and the one a future
/// auto-uncompute pass will reuse to synthesize the cleanup half of a <see cref="QConjugate"/>.
///
/// What inverts, and how:
/// <list type="bullet">
///   <item><b>Gates</b> — toggle the <c>Adjoint</c> functor: <c>X</c> ↔ <c>Adjoint X</c> (emits
///         <c>inv @ x</c>); <c>Controlled X</c> → <c>Adjoint Controlled X</c> (<c>inv @ ctrl @ x</c>).</item>
///   <item><b>User-op calls</b> — <c>Foo(...)</c> → <c>Adjoint Foo(...)</c>, which requires <c>Foo</c>
///         itself to be invertible (checked transitively, memoized, cycle-safe); <c>Adjoint Foo(...)</c>
///         inverts back to the plain call <c>Foo(...)</c>.</item>
///   <item><b>Classical declarations</b> — kept, unreversed, ahead of the inverted quantum tail: a decl
///         is single-assignment (mutation is rejected), so its value is position-independent and the
///         gates that read it (e.g. rotation angles) see the same value in the inverse.</item>
///   <item><b><c>if</c></b> — same condition, each branch inverted (sound because nothing the condition
///         reads can change inside the body: no mutation, no measurement).</item>
///   <item><b><c>for</c></b> — iterate backwards (<c>Step</c> = <c>-1</c>, bounds swapped) with the body
///         inverted: the loop unrolls to U(from)…U(to), so its inverse is U(to)†…U(from)†.</item>
/// </list>
///
/// What does not invert (with the reason reported): measurement and <c>Reset</c> (physically
/// irreversible); classical mutation; <c>while</c>/<c>repeat</c> (iteration count unknowable at compile
/// time); local qubit allocation (that cleanup is precisely the future uncompute pass's job);
/// <c>Controlled</c> on a user operation (no QASM lowering for ctrl-of-def yet); recursive calls.
/// </summary>
public sealed class Inverter
{
    private readonly IReadOnlyDictionary<string, QOperation> _ops;
    private readonly IReadOnlyDictionary<int, QOperation> _opsById;   // call-site → callee resolution by reference (CalleeOpId)
    private readonly Dictionary<string, (IReadOnlyList<QStmt>? Inverse, string Reason)> _cache = new();
    private readonly HashSet<string> _inProgress = new();

    public Inverter(IEnumerable<QOperation> operations)
    {
        var map = new Dictionary<string, QOperation>();
        var byId = new Dictionary<int, QOperation>();
        foreach (var op in operations) { map[op.Name] = op; byId[op.Id] = op; }
        _ops = map;
        _opsById = byId;
    }

    /// <summary>Invert a user operation's body by name. Memoized; safe under (and rejecting of) call cycles.</summary>
    public bool TryInvertOperation(string name, out IReadOnlyList<QStmt> inverse, out string reason)
    {
        var result = InvertOperationCached(name);
        inverse = result.Inverse ?? Array.Empty<QStmt>();
        reason = result.Reason;
        return result.Inverse is not null;
    }

    private (IReadOnlyList<QStmt>? Inverse, string Reason) InvertOperationCached(string name)
    {
        if (_cache.TryGetValue(name, out var hit)) return hit;
        if (!_ops.TryGetValue(name, out var op)) return (null, $"`{name}` is not a defined operation");
        if (!_inProgress.Add(name)) return (null, $"`{name}` calls itself (directly or via a cycle)");

        var result = InvertBody(op.Body);
        _inProgress.Remove(name);
        _cache[name] = result;
        return result;
    }

    /// <summary>
    /// Invert a statement list: classical declarations first (original order), then the quantum
    /// statements reversed and each inverted.
    /// </summary>
    public (IReadOnlyList<QStmt>? Inverse, string Reason) InvertBody(IReadOnlyList<QStmt> body)
    {
        var decls = new List<QStmt>();
        var quantum = new List<QStmt>();

        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case QDecl { Value: QMeasure }:
                    return (null, "it measures a qubit, and measurement is irreversible");
                case QDecl d:
                    decls.Add(d);
                    break;
                case QAssign a:
                    return (null, $"it reassigns `{a.Name}`, and classical mutation is not invertible yet");
                case QUse:
                    return (null, "it allocates local qubits (cleanup needs the uncompute pass)");
                case QWhile:
                case QRepeat:
                    return (null, "its loop count is not known at compile time");
                default:
                    quantum.Add(stmt); // QGate / QIf / QFor / QConjugate
                    break;
            }
        }

        var inverted = new List<QStmt>(decls);
        for (int i = quantum.Count - 1; i >= 0; i--)
        {
            var (inv, reason) = InvertStmt(quantum[i]);
            if (inv is null) return (null, reason);
            inverted.Add(inv);
        }
        return (inverted, string.Empty);
    }

    private (QStmt? Inverse, string Reason) InvertStmt(QStmt stmt)
    {
        switch (stmt)
        {
            case QGate g:
                return InvertGate(g);

            case QIf i:
            {
                var (then, reasonT) = InvertBody(i.Then);
                if (then is null) return (null, reasonT);
                var (els, reasonE) = InvertBody(i.Else);
                if (els is null) return (null, reasonE);
                return (i with { Then = then, Else = els }, string.Empty);
            }

            case QFor f:
            {
                var (body, reason) = InvertBody(f.Body);
                if (body is null) return (null, reason);
                // forward runs U(From)…U(To); the inverse runs U(To)†…U(From)† — iterate backwards.
                // The bounds ARE the trees, so one swap swaps everything a reader can see (the historical
                // text/tree split once let a swap cross the two ledgers).
                return (f with { From = f.To, To = f.From, Step = new QNumLit(-1), Body = body }, string.Empty);
            }

            case QConjugate c:
            {
                // (U V U†)† = U V† U† — the shell stays, only the middle inverts.
                var (apply, reason) = InvertBody(c.Apply);
                if (apply is null) return (null, reason);
                return (c with { Apply = apply }, string.Empty);
            }

            default:
                return (null, "it contains a statement the inverter does not understand");
        }
    }

    private (QStmt? Inverse, string Reason) InvertGate(QGate g)
    {
        // Resolve the callee by REFERENCE (CalleeOpId) when the call carries one; fall back to the name for
        // hand-built IR (test gates carry no CalleeOpId). isUserOp ⟺ the call resolves to a user operation;
        // the resolved op's own name then drives the memoized, single-tree op inversion below.
        var callee = g.CalleeOpId is int cid && _opsById.TryGetValue(cid, out var byId) ? byId
                   : _ops.TryGetValue(g.Name, out var byName) ? byName : null;
        var isUserOp = callee is not null;
        var hasAdjoint = g.Functors.FirstOrDefault() == "Adjoint";

        // name-based irreversibles from the shared registry: non-unitary built-ins (reset, plus its
        // lowercase passthrough as defense) and a bare measurement statement (`M(q);` outside the
        // `var r: bit = M(q);` decl form the QDecl check catches).
        if (!isUserOp && (QoraGates.NonUnitary.Contains(g.Name) || g.Name == "reset"))
            return (null, "it resets a qubit, and reset is irreversible");
        if (!isUserOp && QoraGates.MeasureLike.Contains(g.Name))
            return (null, "it measures a qubit, and measurement is irreversible");
        if (isUserOp && g.Functors.Contains("Controlled"))
            return (null, $"it applies Controlled to the operation `{g.Name}`, which has no OpenQASM lowering yet");

        // A call in either direction pins the target's invertibility:
        //   `Foo(...)`         inverts to `Adjoint Foo(...)` — needs Foo invertible so `Foo__adj` exists;
        //   `Adjoint Foo(...)` inverts to plain `Foo(...)`   — but the *forward* statement itself is only
        //     meaningful when Foo is invertible (a non-invertible Foo makes forward `Adjoint Foo` an
        //     unsupported no-op, so "undoing" it with a real Foo call would silently corrupt the circuit).
        // Requiring it on the Adjoint side also routes cycles through the _inProgress guard.
        if (isUserOp)
        {
            var target = InvertOperationCached(callee!.Name);
            if (target.Inverse is null)
                return (null, hasAdjoint
                    ? $"it applies Adjoint to `{g.Name}`, which cannot be inverted ({target.Reason})"
                    : $"it calls `{g.Name}`, which cannot be inverted ({target.Reason})");
        }

        var functors = hasAdjoint
            ? g.Functors.Skip(1).ToList()
            : g.Functors.Prepend("Adjoint").ToList();
        return (g with { Functors = functors }, string.Empty);
    }
}
