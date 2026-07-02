namespace Qora.Ir;

/// <summary>
/// The semantic-validation pass ("lap 0"): one full walk over the whole IR that answers a single
/// question — does this program mean something OpenQASM can express? — and returns EVERY violation it
/// finds (collect-all, no early stop). The caller (<see cref="QoraParser"/>) gates on the result: any
/// error ⇒ report all of them and skip emission entirely, so the emitter only ever sees validated IR.
///
/// Rules (codes are stable identifiers for editor tooling):
/// <list type="bullet">
///   <item><b>QSEM001</b> — <c>Adjoint Foo</c> where <c>Foo</c> has no inverse (reason chain from
///         <see cref="Inverter"/>).</item>
///   <item><b>QSEM002</b> — <c>Controlled Foo</c> on a user operation (no <c>ctrl @</c> on defs).</item>
///   <item><b>QSEM003</b> — a functor on <c>Reset</c>/<c>ResetAll</c> (reset is a statement, not a gate).</item>
///   <item><b>QSEM004</b> — a bare measurement statement (only assignment forms exist).</item>
///   <item><b>QSEM005</b> — a call inside an expression (condition, arithmetic, argument, non-measure value).</item>
///   <item><b>QSEM006</b> — wrong arguments for a callee: count for built-ins (+1 under <c>Controlled</c>)
///         and user operations, argument KIND per slot (a qubit where an angle belongs and vice versa,
///         a number/classical where a qubit belongs), and qubit shape/size against user-op signatures.</item>
///   <item><b>QSEM007</b> — an unknown gate/operation name (typo or unsupported case variant).</item>
///   <item><b>QSEM008</b> — the same operation name defined more than once.</item>
///   <item><b>QSEM009</b> — calling (or functoring) the entry operation.</item>
///   <item><b>QSEM010</b> — the entry operation takes parameters.</item>
///   <item><b>QSEM011</b> — recursive operation calls (self or mutual).</item>
///   <item><b>QSEM012</b> — <c>use</c> outside the entry operation or inside a loop/branch.</item>
///   <item><b>QSEM013</b> — a reserved declared name. Two tiers, mirroring OpenQASM scoping: QASM
///         keywords/constants are illegal everywhere; stdgates gate names are illegal only for
///         declarations that land in the QASM GLOBAL scope (operation names, the entry op's registers,
///         hoisted measure bits, and its top-level variables) — def-local parameters/variables/loop
///         variables may legally shadow them. Operation names additionally may not shadow Qora
///         built-ins (<c>operation Rx</c>, <c>operation M</c>).</item>
///   <item><b>QSEM014</b> — the same qubit (or overlapping register/element) passed twice to one gate.</item>
///   <item><b>QSEM015</b> — duplicate declared names inside one operation: parameters, <c>use</c>
///         registers, and hoisted measure bits share one scope and must not collide.</item>
///   <item><b>QSEM016</b> — a literal qubit index out of the register's range, or indexing a
///         single-qubit parameter.</item>
///   <item><b>QSEM017</b> — a measurement assigned to a non-<c>bit</c> declaration.</item>
/// </list>
/// Errors carry no source span yet (the IR does not record positions), so <see cref="QoraError"/> uses
/// the (-1, -1) "no span" convention and consumers fall back to a whole-document marker.
/// </summary>
public static class QoraValidator
{
    public static List<QoraError> Validate(QProgram? program)
    {
        var errors = new List<QoraError>();
        if (program is null) return errors;

        // QSEM099 (provisional) — the module-system grammar landed ahead of its resolver pass: gate the
        // constructs so a namespaced program can never compile with silently-global semantics.
        foreach (var decl in program.ModuleDecls ?? (IReadOnlyList<string>)Array.Empty<string>())
            Add(errors, "QSEM099", $"`{decl}` is not supported yet: the module system is in progress (see docs/namespaces-design.md)");

        if (program.Operations.Count == 0) return errors;

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
            var isEntry = op == entry;

            // QSEM013 tier for operation names — always global (they become def names), and shadowing a
            // Qora built-in would silently change what a call site means somewhere in the pipeline.
            if (QoraGates.Names.ContainsKey(op.Name) || QoraGates.MeasureLike.Contains(op.Name))
                Add(errors, "QSEM013", $"operation name `{op.Name}` shadows the built-in gate/measurement `{op.Name}`; choose another name");
            else if (QoraGates.QasmReserved.Contains(op.Name))
                Add(errors, "QSEM013", $"operation name `{op.Name}` is reserved in OpenQASM; choose another name");

            // QSEM015 — parameters, `use` registers, and hoisted measure bits share one emitted scope.
            foreach (var dup in op.Params.GroupBy(p => p.Name).Where(g => g.Count() > 1))
                Add(errors, "QSEM015", $"in `{op.Name}`: the parameter name `{dup.Key}` is used twice; each parameter needs a unique name");
            var useNames = new HashSet<string>();
            var measureBits = new HashSet<string>();
            CollectScopedNames(op.Body, useNames, measureBits);
            foreach (var p in op.Params)
            {
                CheckDeclaredName(p.Name, op.Name, "parameter", isGlobal: false, errors);
                if (useNames.Contains(p.Name) || measureBits.Contains(p.Name))
                    Add(errors, "QSEM015", $"in `{op.Name}`: `{p.Name}` is declared more than once (parameter vs register/measure bit)");
            }
            foreach (var clash in useNames.Intersect(measureBits))
                Add(errors, "QSEM015", $"in `{op.Name}`: `{clash}` names both a qubit register and a measure bit; hoisting would merge them — rename one");

            var scope = BuildScope(op, entry.Name, isEntry);
            Walk(op.Body, scope, ops, opByName, inverter, errors, inControlFlow: false);
        }

        return errors;
    }

    /// <summary>What the walk needs to know about the operation it is inside.</summary>
    private sealed record Scope(
        QOperation Op, string EntryName, bool IsEntry,
        Dictionary<string, int> Registers,       // register name -> size (sized qubit params + use)
        HashSet<string> SingleQubits,            // unsized qubit parameter names
        HashSet<string> Classicals)              // int/bit params + declared variables + loop vars
    {
        public HashSet<string> UseNames { get; } = new();
    }

    private static Scope BuildScope(QOperation op, string entryName, bool isEntry)
    {
        var registers = new Dictionary<string, int>();
        var singles = new HashSet<string>();
        var classicals = new HashSet<string>();

        foreach (var p in op.Params)
        {
            if (p.Type == QType.Qubit && p.RegisterSize is int n) registers[p.Name] = n;
            else if (p.Type == QType.Qubit) singles.Add(p.Name);
            else classicals.Add(p.Name);
        }

        void Scan(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: registers.TryAdd(u.Name, u.Size); break;
                    case QDecl d: classicals.Add(d.Name); break;
                    case QFor f: classicals.Add(f.Var); Scan(f.Body); break;
                    case QIf i: Scan(i.Then); Scan(i.Else); break;
                    case QWhile w: Scan(w.Body); break;
                    case QRepeat r: Scan(r.Body); break;
                    case QConjugate c: Scan(c.Within); Scan(c.Apply); break;
                }
        }
        Scan(op.Body);
        return new Scope(op, entryName, isEntry, registers, singles, classicals);
    }

    /// <summary>Collect `use` register names and measure-bit declaration names (for QSEM015).</summary>
    private static void CollectScopedNames(IReadOnlyList<QStmt> stmts, HashSet<string> useNames, HashSet<string> measureBits)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QUse u: useNames.Add(u.Name); break;
                case QDecl { Value: QMeasure } d: measureBits.Add(d.Name); break;
                case QIf i: CollectScopedNames(i.Then, useNames, measureBits); CollectScopedNames(i.Else, useNames, measureBits); break;
                case QFor f: CollectScopedNames(f.Body, useNames, measureBits); break;
                case QWhile w: CollectScopedNames(w.Body, useNames, measureBits); break;
                case QRepeat r: CollectScopedNames(r.Body, useNames, measureBits); break;
                case QConjugate c: CollectScopedNames(c.Within, useNames, measureBits); CollectScopedNames(c.Apply, useNames, measureBits); break;
            }
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
                case QUse u:
                    if (opName != scope.EntryName)
                        Add(errors, "QSEM012", $"in `{opName}`: `use {u.Name} = Qubit[{u.Size}];` is not supported inside an operation; allocate in `{scope.EntryName}` and pass the qubits as a parameter");
                    else if (inControlFlow)
                        Add(errors, "QSEM012", $"in `{opName}`: `use {u.Name} = ...` inside a loop or branch is not supported; allocate once at the top level");
                    else if (!scope.UseNames.Add(u.Name))
                        Add(errors, "QSEM015", $"in `{opName}`: the register name `{u.Name}` is used twice; each `use` needs a unique name");
                    // entry registers are hoisted to the QASM global scope.
                    CheckDeclaredName(u.Name, opName, "register", isGlobal: scope.IsEntry, errors);
                    break;

                case QDecl d:
                    if (d.Value is QText { HasCall: true })
                        Add(errors, "QSEM005", $"in `{opName}`: the initializer of `{d.Name}` contains a call; only the lone form `bit r = M(q[i]);` is supported");
                    if (d.Value is QMeasure dm)
                    {
                        // QSEM017 — measure results are bits; QSEM016 — validate the measured index.
                        if (d.Type is not null && d.Type != QType.Bit)
                            Add(errors, "QSEM017", $"in `{opName}`: `{d.Name}` is declared `{d.Type.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`");
                        if (dm.Target is not null) CheckQubitIndex(dm.Target, scope, errors);
                    }
                    // measure bits hoist to the top of their op; in the ENTRY op that is the global scope.
                    // Ordinary variables land where written: global only at the entry's top level.
                    var declGlobal = scope.IsEntry && (d.Value is QMeasure || !inControlFlow);
                    CheckDeclaredName(d.Name, opName, "variable", declGlobal, errors);
                    break;

                case QAssign a:
                    if (a.Value is QText { HasCall: true })
                        Add(errors, "QSEM005", $"in `{opName}`: the value assigned to `{a.Name}` contains a call; only the lone form `{a.Name} = M(q[i]);` is supported");
                    if (a.Value is QMeasure am && am.Target is not null)
                        CheckQubitIndex(am.Target, scope, errors);
                    break;

                case QIf i:
                    CheckCondition(i.Cond, opName, errors);
                    Walk(i.Then, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    Walk(i.Else, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QFor f:
                    // the loop variable is scoped to the loop body — never global.
                    CheckDeclaredName(f.Var, opName, "loop variable", isGlobal: false, errors);
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

    // QSEM013 — keywords can never be identifiers; stdgates names only collide at the global scope
    // (locals may shadow them, per OpenQASM scoping).
    private static void CheckDeclaredName(string name, string opName, string kind, bool isGlobal, List<QoraError> errors)
    {
        if (QoraGates.QasmKeywords.Contains(name))
            Add(errors, "QSEM013", $"in `{opName}`: {kind} name `{name}` is an OpenQASM keyword; choose another name");
        else if (isGlobal && QoraGates.StdgatesNames.Contains(name))
            Add(errors, "QSEM013", $"in `{opName}`: {kind} name `{name}` collides with the stdgates gate `{name}` at the program's top level; choose another name");
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

        // QSEM005 — calls inside gate arguments have no OpenQASM form.
        foreach (var arg in g.Args)
            if (arg is QTextArg { HasCall: true })
                Add(errors, "QSEM005", $"in `{opName}`: an argument of `{g.Name}` contains a call; measure into a bit first and pass the bit");

        // QSEM016 — literal indices must fit their register; a single qubit cannot be indexed.
        foreach (var arg in g.Args)
            if (arg is QQubitArg qa)
                CheckQubitIndex(qa, scope, errors);

        // QSEM014 — the same qubit twice in one gate. Whole registers count: `CNOT(q, q)` broadcasts to
        // duplicate operands, and `CNOT(q, q[0])` overlaps the register with its own element.
        var refs = g.Args.Select(a => QubitRefOf(a, scope)).Where(r => r is not null).Select(r => r!.Value).ToList();
        foreach (var dup in refs.GroupBy(r => (r.Reg, r.Index)).Where(grp => grp.Count() > 1))
            Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the qubit `{Show(dup.Key.Reg, dup.Key.Index)}` more than once; gate operands must be distinct");
        foreach (var whole in refs.Where(r => r.Index is null).Select(r => r.Reg).Distinct())
            if (refs.Any(r => r.Reg == whole && r.Index is not null))
                Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the register `{whole}` and one of its own qubits; operands must not overlap");

        // QSEM009 — the entry op is emitted as the QASM top level, not as a def: nothing can call it.
        if (g.Name == scope.EntryName)
        {
            Add(errors, "QSEM009", $"in `{opName}`: the entry operation `{scope.EntryName}` cannot be called (its body is the program's top level, not a def)");
            return;
        }

        // QSEM004 — measurement only exists in the assignment forms.
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

        // QSEM006 — built-ins: argument count, then argument KIND per slot (only when the count fits,
        // so the slots line up). Rotations take the angle first; every other slot is a qubit.
        if (QoraGates.Arity.TryGetValue(g.Name, out var baseArity))
        {
            var expected = baseArity + (g.Functors.Contains("Controlled") ? 1 : 0);
            if (g.Args.Count != expected)
            {
                Add(errors, "QSEM006", $"in `{opName}`: `{(g.Functors.Count > 0 ? string.Join(" ", g.Functors) + " " : "")}{g.Name}` expects {expected} argument(s) but got {g.Args.Count}");
                return;
            }

            var qubitStart = 0;
            if (QoraGates.Rotations.Contains(g.Name))
            {
                qubitStart = 1;
                if (IsQubitLike(g.Args[0], scope))
                    Add(errors, "QSEM006", $"in `{opName}`: the first argument of `{g.Name}` is the rotation angle, but a qubit was passed (write `{g.Name}(angle, qubit)`)");
            }
            for (var i = qubitStart; i < g.Args.Count; i++)
                if (IsDefinitelyNotQubit(g.Args[i], scope))
                    Add(errors, "QSEM006", $"in `{opName}`: argument {i + 1} of `{g.Name}` must be a qubit (like `q[0]`), not a number or classical value");
        }
    }

    /// <summary>
    /// QSEM006 for user-operation calls: arity, then per-slot qubit shape/size — a sized qubit param
    /// needs a register of that size, an unsized one a single qubit, a classical one no qubit at all.
    /// Identifiers the caller's scope does not know are left alone (no symbol table for classicals yet).
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
                else if (arg is QTextArg tb && !scope.Registers.ContainsKey(tb.Text) && (scope.SingleQubits.Contains(tb.Text) || IsClassicalText(tb.Text, scope)))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a qubit register, but `{tb.Text}` is not one");
            }
            else if (p.Type == QType.Qubit)
            {
                if (arg is QTextArg ta && (scope.Registers.ContainsKey(ta.Text) || IsClassicalText(ta.Text, scope)))
                    Add(errors, "QSEM006", scope.Registers.ContainsKey(ta.Text)
                        ? $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a single qubit, but `{ta.Text}` is a whole register (pass `{ta.Text}[i]`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a qubit, but `{ta.Text}` is not one");
            }
            else if (IsQubitLike(arg, scope))
            {
                Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is `{p.Type.ToString()!.ToLowerInvariant()}`, but a qubit was passed");
            }
        }
    }

    // --- argument classification helpers ---

    private static bool IsQubitLike(QArg arg, Scope scope) => arg switch
    {
        QQubitArg => true,
        QTextArg t => scope.Registers.ContainsKey(t.Text) || scope.SingleQubits.Contains(t.Text),
        _ => false,
    };

    /// <summary>True only when the argument provably cannot denote a qubit (numbers, expressions, known classicals).</summary>
    private static bool IsDefinitelyNotQubit(QArg arg, Scope scope) =>
        arg is QTextArg t && !scope.Registers.ContainsKey(t.Text) && !scope.SingleQubits.Contains(t.Text)
                          && IsClassicalText(t.Text, scope);

    private static bool IsClassicalText(string text, Scope scope) =>
        text.Contains(' ')                                   // any operator expression: `pi / 2`, `a + 1`
        || (text.Length > 0 && char.IsDigit(text[0]))        // numeric literal
        || text is "pi" or "tau" or "euler"
        || scope.Classicals.Contains(text);                  // declared variable / int/bit param / loop var

    /// <summary>QSEM016 — literal index bounds against known register sizes; no indexing single qubits.</summary>
    private static void CheckQubitIndex(QQubitArg q, Scope scope, List<QoraError> errors)
    {
        var opName = scope.Op.Name;
        if (scope.SingleQubits.Contains(q.Reg))
        {
            Add(errors, "QSEM016", $"in `{opName}`: `{q.Reg}` is a single qubit and cannot be indexed (`{q.Reg}[{q.Index}]`)");
            return;
        }
        if (scope.Registers.TryGetValue(q.Reg, out var size)
            && q.Index.Length > 0 && q.Index.All(char.IsDigit)
            && int.Parse(q.Index) >= size)
        {
            Add(errors, "QSEM016", $"in `{opName}`: index `{q.Reg}[{q.Index}]` is out of range; `{q.Reg}` has {size} qubit(s) (valid: 0..{size - 1})");
        }
    }

    /// <summary>An argument as a qubit reference: (register, index) — index null for a whole register / single qubit.</summary>
    private static (string Reg, string? Index)? QubitRefOf(QArg arg, Scope scope) => arg switch
    {
        QQubitArg q => (q.Reg, q.Index),
        QTextArg t when scope.Registers.ContainsKey(t.Text) => (t.Text, (string?)null),
        QTextArg t when scope.SingleQubits.Contains(t.Text) => (t.Text, (string?)null),
        _ => null,
    };

    private static string Show(string reg, string? index) => index is null ? reg : $"{reg}[{index}]";

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
