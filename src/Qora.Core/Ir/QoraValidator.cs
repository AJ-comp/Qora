namespace Qora.Ir;

/// <summary>
/// The semantic-validation pass ("lap 0"): one full walk over the whole IR that answers a single
/// question — does this program mean something OpenQASM can express? — and returns EVERY violation it
/// finds. It never stops at the first error (collect-all, like any modern compiler) and produces no
/// output; the caller (<see cref="QoraParser"/>) gates on the result: any error ⇒ report all of them and
/// skip emission entirely, so the emitter only ever sees validated IR.
///
/// Rules (codes are stable identifiers for editor tooling):
/// <list type="bullet">
///   <item><b>QSEM001</b> — <c>Adjoint Foo</c> where <c>Foo</c> has no inverse (reason chain from
///         <see cref="Inverter"/>).</item>
///   <item><b>QSEM002</b> — <c>Controlled Foo</c> on a user operation (no <c>ctrl @</c> on defs).</item>
///   <item><b>QSEM003</b> — a functor on <c>Reset</c>/<c>ResetAll</c> (reset is a statement, not a gate).</item>
///   <item><b>QSEM004</b> — a bare measurement statement (only the <c>bit r = M(q[i]);</c> /
///         <c>r = M(q[i]);</c> forms exist).</item>
///   <item><b>QSEM005</b> — a call inside an expression (condition, arithmetic, gate argument, or a
///         non-measure call used as a value).</item>
///   <item><b>QSEM006</b> — wrong arguments for a callee: built-in gate arity (<c>Controlled</c> adds
///         one), user-operation arity against its parameter list, and best-effort qubit shape/size
///         (single qubit vs register, register size mismatch) where both sides are known.</item>
///   <item><b>QSEM007</b> — an unknown gate/operation name (typo or unsupported case variant).</item>
///   <item><b>QSEM008</b> — the same operation name defined more than once.</item>
///   <item><b>QSEM009</b> — calling (or functoring) the entry operation.</item>
///   <item><b>QSEM010</b> — the entry operation takes parameters.</item>
///   <item><b>QSEM011</b> — recursive operation calls (self or mutual).</item>
///   <item><b>QSEM012</b> — <c>use</c> outside the entry operation, or inside a loop/branch (hoisting
///         would silently turn a fresh-per-iteration qubit into one reused dirty register).</item>
///   <item><b>QSEM013</b> — a declared name (operation, parameter, variable, register, loop variable)
///         that is reserved: an OpenQASM keyword/constant, a <c>stdgates.inc</c> gate name, or — for
///         operations — a Qora built-in gate/measurement name it would shadow.</item>
///   <item><b>QSEM014</b> — the same qubit passed twice to one gate (<c>CNOT(q[0], q[0])</c>).</item>
///   <item><b>QSEM015</b> — two <c>use</c> registers with the same name in one operation.</item>
/// </list>
/// Errors carry no source span yet (the IR does not record positions), so <see cref="QoraError"/> uses
/// the (-1, -1) "no span" convention and consumers fall back to a whole-document marker.
/// </summary>
public static class QoraValidator
{

    public static List<QoraError> Validate(QProgram? program)
    {
        var errors = new List<QoraError>();
        if (program is null || program.Operations.Count == 0) return errors;

        var ops = program.Operations.Select(o => o.Name).ToHashSet();
        var opByName = new Dictionary<string, QOperation>();
        foreach (var o in program.Operations) opByName.TryAdd(o.Name, o);
        var inverter = new Inverter(program.Operations);
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];

        // QSEM008 — duplicate definitions (everything downstream keys ops by name).
        foreach (var dup in program.Operations.GroupBy(o => o.Name).Where(g => g.Count() > 1))
            Add(errors, "QSEM008", $"operation `{dup.Key}` is defined {dup.Count()} times; each operation needs a unique name");

        // QSEM010 — the entry op has no caller, so parameters can never be supplied.
        if (entry.Params.Count > 0)
            Add(errors, "QSEM010", $"the entry operation `{entry.Name}` cannot take parameters; allocate qubits with `use` inside it instead");

        // QSEM011 — recursive call cycles (any functor counts as a reference).
        foreach (var cycle in FindCycles(program, ops))
            Add(errors, "QSEM011", cycle.Count == 1
                ? $"operation `{cycle[0]}` calls itself; OpenQASM defs cannot recurse"
                : $"operations {string.Join(" -> ", cycle)} -> {cycle[0]} call each other recursively; OpenQASM defs cannot recurse");

        foreach (var op in program.Operations)
        {
            // QSEM013 — an operation name that shadows a Qora built-in (gate / measurement) or lands on
            // a reserved OpenQASM identifier would silently change meaning somewhere in the pipeline.
            if (QoraGates.Names.ContainsKey(op.Name) || QoraGates.MeasureLike.Contains(op.Name))
                Add(errors, "QSEM013", $"operation name `{op.Name}` shadows the built-in gate/measurement `{op.Name}`; choose another name");
            else if (QoraGates.QasmReserved.Contains(op.Name))
                Add(errors, "QSEM013", $"operation name `{op.Name}` is reserved in OpenQASM; choose another name");

            foreach (var p in op.Params)
                CheckDeclaredName(p.Name, op.Name, "parameter", errors);

            var scope = new Scope(op, entry.Name, RegisterSizes(op));
            Walk(op.Body, scope, ops, opByName, inverter, errors, inControlFlow: false);
        }

        return errors;
    }

    /// <summary>What the walk needs to know about the operation it is inside.</summary>
    private sealed record Scope(QOperation Op, string EntryName, Dictionary<string, int> Registers)
    {
        public HashSet<string> UseNames { get; } = new();
    }

    /// <summary>Known register sizes in an op's scope: sized qubit parameters plus its `use` statements.</summary>
    private static Dictionary<string, int> RegisterSizes(QOperation op)
    {
        var sizes = new Dictionary<string, int>();
        foreach (var p in op.Params)
            if (p.Type == QType.Qubit && p.RegisterSize is int n) sizes[p.Name] = n;
        void Scan(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: sizes.TryAdd(u.Name, u.Size); break;
                    case QIf i: Scan(i.Then); Scan(i.Else); break;
                    case QFor f: Scan(f.Body); break;
                    case QWhile w: Scan(w.Body); break;
                    case QRepeat r: Scan(r.Body); break;
                    case QConjugate c: Scan(c.Within); Scan(c.Apply); break;
                }
        }
        Scan(op.Body);
        return sizes;
    }

    private static void Walk(IReadOnlyList<QStmt> stmts, Scope scope, HashSet<string> ops,
        Dictionary<string, QOperation> opByName, Inverter inverter, List<QoraError> errors, bool inControlFlow)
    {
        var opName = scope.Op.Name;

        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QGate g:
                    CheckGate(g, scope, ops, opByName, inverter, errors);
                    break;

                // QSEM012 / QSEM015 — `use` only at the top level of the entry op, each name once.
                // Hoisting a loop/branch-local `use` would silently reuse one dirty register where the
                // surface language promises a fresh |0> allocation.
                case QUse u:
                    if (opName != scope.EntryName)
                        Add(errors, "QSEM012", $"in `{opName}`: `use {u.Name} = Qubit[{u.Size}];` is not supported inside an operation; allocate in `{scope.EntryName}` and pass the qubits as a parameter");
                    else if (inControlFlow)
                        Add(errors, "QSEM012", $"in `{opName}`: `use {u.Name} = ...` inside a loop or branch is not supported; allocate once at the top level");
                    else if (!scope.UseNames.Add(u.Name))
                        Add(errors, "QSEM015", $"in `{opName}`: the register name `{u.Name}` is used twice; each `use` needs a unique name");
                    CheckDeclaredName(u.Name, opName, "register", errors);
                    break;

                // QSEM005 — a call mixed into an initializer/assignment expression.
                case QDecl d:
                    if (d.Value is QText { HasCall: true })
                        Add(errors, "QSEM005", $"in `{opName}`: the initializer of `{d.Name}` contains a call; only the lone form `bit r = M(q[i]);` is supported");
                    CheckDeclaredName(d.Name, opName, "variable", errors);
                    break;
                case QAssign { Value: QText { HasCall: true } } a:
                    Add(errors, "QSEM005", $"in `{opName}`: the value assigned to `{a.Name}` contains a call; only the lone form `{a.Name} = M(q[i]);` is supported");
                    break;

                case QIf i:
                    CheckCondition(i.Cond, opName, errors);
                    Walk(i.Then, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    Walk(i.Else, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QFor f:
                    CheckDeclaredName(f.Var, opName, "loop variable", errors);
                    Walk(f.Body, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QWhile w:
                    CheckCondition(w.Cond, opName, errors);
                    Walk(w.Body, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QRepeat r:
                    Walk(r.Body, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    CheckCondition(r.Until, opName, errors);
                    break;
                case QConjugate c:
                    Walk(c.Within, scope, ops, opByName, inverter, errors, inControlFlow);
                    Walk(c.Apply, scope, ops, opByName, inverter, errors, inControlFlow);
                    break;
            }
        }
    }

    // QSEM013 — declared identifiers land verbatim in the emitted QASM, which shares one namespace
    // with stdgates' gates and the language keywords.
    private static void CheckDeclaredName(string name, string opName, string kind, List<QoraError> errors)
    {
        if (QoraGates.QasmReserved.Contains(name))
            Add(errors, "QSEM013", $"in `{opName}`: {kind} name `{name}` collides with an OpenQASM keyword or stdgates gate name; choose another name");
    }

    // QSEM005 — OpenQASM has no measurement expressions: a condition cannot measure.
    private static void CheckCondition(QCond cond, string opName, List<QoraError> errors)
    {
        if (cond.HasCall)
            Add(errors, "QSEM005", $"in `{opName}`: a condition cannot contain a measurement; measure into a bit first (`bit r = M(q[i]);`) and test the bit");
    }

    private static void CheckGate(QGate g, Scope scope, HashSet<string> ops,
        Dictionary<string, QOperation> opByName, Inverter inverter, List<QoraError> errors)
    {
        var opName = scope.Op.Name;

        // QSEM005 — calls inside gate arguments (e.g. `Rx(M(q[0]), q[1])`) have no OpenQASM form.
        foreach (var arg in g.Args)
            if (arg is QTextArg { HasCall: true })
                Add(errors, "QSEM005", $"in `{opName}`: an argument of `{g.Name}` contains a call; measure into a bit first and pass the bit");

        // QSEM014 — the same qubit twice in one gate application is rejected by every QASM consumer.
        var qubitRefs = g.Args.Select(RenderQubitRef).Where(t => t is not null).ToList();
        foreach (var dup in qubitRefs.GroupBy(t => t).Where(grp => grp.Count() > 1))
            Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the qubit `{dup.Key}` more than once; gate operands must be distinct");

        // QSEM009 — the entry op is emitted as the QASM top level, not as a def: nothing can call it.
        if (g.Name == scope.EntryName)
        {
            Add(errors, "QSEM009", $"in `{opName}`: the entry operation `{scope.EntryName}` cannot be called (its body is the program's top level, not a def)");
            return;
        }

        // QSEM004 — measurement only exists in the assignment forms. MeasureLike names that are not the
        // registered `M` are equally illegal, but deserve this message rather than "unknown gate".
        if (!ops.Contains(g.Name) && QoraGates.MeasureLike.Contains(g.Name))
        {
            Add(errors, "QSEM004", $"in `{opName}`: a bare measurement statement is not supported: assign the result instead: `bit r = {QoraGates.Measurement}(q[i]);`");
            return;
        }

        if (ops.Contains(g.Name))
        {
            // QSEM002 — OpenQASM gate modifiers apply to gates only, never to def subroutine calls.
            if (g.Functors.Contains("Controlled"))
            {
                Add(errors, "QSEM002", $"in `{opName}`: `Controlled {g.Name}` is not supported: OpenQASM cannot apply ctrl @ to a def");
                return;
            }

            // QSEM001 — Adjoint on a user operation compiles to a synthesized inverse def, which must exist.
            if (g.Functors.FirstOrDefault() == "Adjoint" && !inverter.TryInvertOperation(g.Name, out _, out var reason))
            {
                Add(errors, "QSEM001", $"in `{opName}`: `Adjoint {g.Name}` cannot be compiled: {reason}");
                return;
            }

            // QSEM006 — a def call must match the def's signature (an Adjoint call shares it).
            if (opByName.TryGetValue(g.Name, out var callee))
                CheckCallSignature(g, callee, scope, errors);
            return;
        }

        // QSEM007 — not a user op and not a known built-in: a typo would otherwise emit an undefined gate.
        if (!QoraGates.Names.ContainsKey(g.Name))
        {
            var hint = QoraGates.Names.Keys.Concat(ops).FirstOrDefault(k => string.Equals(k, g.Name, StringComparison.OrdinalIgnoreCase));
            Add(errors, "QSEM007", $"in `{opName}`: `{g.Name}` is not a known gate or operation" + (hint is null ? string.Empty : $" (did you mean `{hint}`?)"));
            return;
        }

        // QSEM003 — reset is a statement, not a gate: no inv @ / ctrl @ on it.
        if (QoraGates.NonUnitary.Contains(g.Name) && g.Functors.Count > 0)
        {
            Add(errors, "QSEM003", $"in `{opName}`: `{string.Join(" ", g.Functors)} {g.Name}` is not supported: reset is not a gate and takes no modifiers");
            return;
        }

        // QSEM006 — wrong argument count for a built-in (Controlled adds one control argument).
        if (QoraGates.Arity.TryGetValue(g.Name, out var baseArity))
        {
            var expected = baseArity + (g.Functors.Contains("Controlled") ? 1 : 0);
            if (g.Args.Count != expected)
                Add(errors, "QSEM006", $"in `{opName}`: `{(g.Functors.Count > 0 ? string.Join(" ", g.Functors) + " " : "")}{g.Name}` expects {expected} argument(s) but got {g.Args.Count}");
        }
    }

    /// <summary>
    /// QSEM006 for user-operation calls: arity against the parameter list, then best-effort qubit
    /// shape/size — a sized qubit param needs a register of that size, an unsized one a single qubit.
    /// Size checks only fire when the caller-side register is known (its `use` or sized param).
    /// </summary>
    private static void CheckCallSignature(QGate g, QOperation callee, Scope scope, List<QoraError> errors)
    {
        var opName = scope.Op.Name;

        if (g.Args.Count != callee.Params.Count)
        {
            Add(errors, "QSEM006", $"in `{opName}`: `{callee.Name}` expects {callee.Params.Count} argument(s) but got {g.Args.Count}");
            return;
        }

        for (int i = 0; i < callee.Params.Count; i++)
        {
            var p = callee.Params[i];
            var arg = g.Args[i];

            if (p.Type == QType.Qubit && p.RegisterSize is int need)
            {
                if (arg is QQubitArg qa)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a register of {need} qubit(s), but `{qa.Reg}[{qa.Index}]` is a single qubit");
                else if (arg is QTextArg ta && scope.Registers.TryGetValue(ta.Text, out var have) && have != need)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a register of {need} qubit(s), but `{ta.Text}` has {have}");
            }
            else if (p.Type == QType.Qubit)
            {
                if (arg is QTextArg ta && scope.Registers.ContainsKey(ta.Text))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a single qubit, but `{ta.Text}` is a whole register (pass `{ta.Text}[i]`)");
            }
            else if (arg is QQubitArg qb)
            {
                Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is `{p.Type.ToString().ToLowerInvariant()}`, but `{qb.Reg}[{qb.Index}]` is a qubit");
            }
        }
    }

    /// <summary>A gate argument as a comparable qubit reference, or null when it is not one.</summary>
    private static string? RenderQubitRef(QArg arg) => arg switch
    {
        QQubitArg q => $"{q.Reg}[{q.Index}]",
        _ => null,
    };

    // --- call-cycle detection (Tarjan's strongly connected components) ---

    /// <summary>Cycles in the op-call graph: every SCC larger than one op, plus direct self-calls.</summary>
    private static List<List<string>> FindCycles(QProgram program, HashSet<string> ops)
    {
        var adj = new Dictionary<string, List<string>>();
        foreach (var op in program.Operations)
        {
            var refs = new HashSet<string>();
            CollectOpRefs(op.Body, ops, refs);
            adj[op.Name] = refs.ToList();
        }

        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<string>();
        var cycles = new List<List<string>>();
        var counter = 0;

        void Strongconnect(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adj[v])
            {
                if (!adj.ContainsKey(w)) continue;
                if (!index.ContainsKey(w))
                {
                    Strongconnect(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }

            if (low[v] == index[v])
            {
                var scc = new List<string>();
                string w;
                do { w = stack.Pop(); onStack.Remove(w); scc.Add(w); } while (w != v);
                if (scc.Count > 1 || adj[v].Contains(v)) { scc.Reverse(); cycles.Add(scc); }
            }
        }

        foreach (var name in adj.Keys)
            if (!index.ContainsKey(name)) Strongconnect(name);

        return cycles;
    }

    /// <summary>All user-operation names referenced by a body's call sites (any functor).</summary>
    private static void CollectOpRefs(IReadOnlyList<QStmt> stmts, HashSet<string> ops, HashSet<string> into)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QGate g when ops.Contains(g.Name):
                    into.Add(g.Name);
                    break;
                case QIf i:
                    CollectOpRefs(i.Then, ops, into);
                    CollectOpRefs(i.Else, ops, into);
                    break;
                case QFor f:
                    CollectOpRefs(f.Body, ops, into);
                    break;
                case QWhile w:
                    CollectOpRefs(w.Body, ops, into);
                    break;
                case QRepeat r:
                    CollectOpRefs(r.Body, ops, into);
                    break;
                case QConjugate c:
                    CollectOpRefs(c.Within, ops, into);
                    CollectOpRefs(c.Apply, ops, into);
                    break;
            }
        }
    }

    private static void Add(List<QoraError> errors, string code, string message) =>
        errors.Add(new QoraError(message, code, -1, -1));
}
