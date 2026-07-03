namespace Qora.Ir.Passes;

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
///   <item><b>QSEM013</b> — a declared name that shadows a Qora BUILT-IN where no qualification could
///         ever disambiguate: an operation named like the measurement family (<c>operation M</c>), any
///         declaration named <c>pi</c>/<c>tau</c>/<c>euler</c> (expression-position tokens), a GLOBAL
///         operation named like a built-in gate (the global namespace shares one scope with the
///         built-ins), or declaring the reserved <c>Qora.Intrinsic</c> namespace (raised by the
///         <see cref="Resolver"/>). Inside a namespace, gate names ARE allowed (Q#-style) — an
///         ambiguous use is QSEM018, qualified by <c>MyLib.H(…)</c> / <c>Qora.Intrinsic.H(…)</c>.
///         Every other name is free — the <see cref="NameMangler"/> pass appends <c>_</c> to all user
///         names at emission, so target-world collisions (stdgates, keywords) are structurally
///         impossible.</item>
///   <item><b>QSEM014</b> — the same qubit (or overlapping register/element) passed twice to one gate.</item>
///   <item><b>QSEM015</b> — duplicate declared names inside one operation: parameters, <c>use</c>
///         registers, and hoisted measure bits share one scope and must not collide.</item>
///   <item><b>QSEM016</b> — a literal qubit index out of the register's range, or indexing a
///         single-qubit parameter.</item>
///   <item><b>QSEM017</b> — a measurement assigned to a non-<c>bit</c> declaration.</item>
///   <item><b>QSEM022</b> — the same operation name defined more than once WITHIN one namespace
///         (namespaced twin of QSEM008; the same simple name in two different namespaces is fine).</item>
///   <item><b>QSEM023</b> — names that would COLLIDE IN THE EMITTED PROGRAM: two distinct operation
///         names meeting at one mangled def name (`A.F` vs `A__F` → `A__F_`), an entry-op declaration
///         landing on a def's name (entry locals are QASM top-level globals), or a def-local shadowing
///         an operation that def calls. Mangling makes user-vs-QASM collisions impossible; this rule
///         closes the remaining user-vs-user seams.</item>
/// </list>
/// Earlier pipeline steps own the remaining codes, and each step's errors preempt this validator:
/// QSEM020 (import file not found/unreadable) and QSEM021 (cyclic imports) come from
/// <see cref="ModuleLoader"/>; QSEM018 (ambiguous unqualified reference) and QSEM019 (unknown
/// namespace/member) come from <see cref="Resolver"/>.
/// Every error carries the source span of the offending construct (statement spans for statement-level
/// rules, name-token spans for declaration-level ones). Nodes from IMPORTED files are spanless by
/// design — their offsets would lie about the entry document — so their errors use the (-1, -1)
/// "no span" convention and consumers fall back to a whole-document marker.
/// </summary>
public static class QoraValidator
{
    public static List<QoraError> Validate(QProgram? program)
    {
        var errors = new List<QoraError>();
        if (program is null) return errors;

        if (program.Operations.Count == 0) return errors;

        var ops = program.Operations.Select(o => o.Name).ToHashSet();
        var opByName = new Dictionary<string, QOperation>();
        foreach (var o in program.Operations) opByName.TryAdd(o.Name, o);
        var inverter = new Inverter(program.Operations);
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];

        // QSEM008 / QSEM022 — duplicate definitions (everything downstream keys ops by name). Names are
        // FQNs after resolution, so the check is naturally per-namespace: the same simple name in two
        // different namespaces is NOT a duplicate. Inside one namespace the code is QSEM022 (design doc).
        foreach (var dup in program.Operations.GroupBy(o => o.Name).Where(g => g.Count() > 1))
            Add(errors,
                dup.Key.Contains('.') ? "QSEM022" : "QSEM008",
                dup.Key.Contains('.')
                    ? $"operation `{Simple(dup.Key)}` is defined {dup.Count()} times in namespace `{dup.Key[..dup.Key.LastIndexOf('.')]}`; each operation needs a unique name within its namespace"
                    : $"operation `{dup.Key}` is defined {dup.Count()} times; each operation needs a unique name",
                dup.Skip(1).First().Span ?? dup.First().Span);   // point at the SECOND definition

        // QSEM010 — the entry op has no caller, so parameters can never be supplied.
        if (entry.Params.Count > 0)
            Add(errors, "QSEM010", $"the entry operation `{entry.Name}` cannot take parameters; allocate qubits with `use` inside it instead",
                entry.Params[0].Span ?? entry.Span);

        // QSEM011 — recursive call cycles (any functor counts as a reference).
        foreach (var cycle in FindCycles(program, ops))
            Add(errors, "QSEM011", cycle.Count == 1
                ? $"operation `{cycle[0]}` calls itself; OpenQASM defs cannot recurse"
                : $"operations {string.Join(" -> ", cycle)} -> {cycle[0]} call each other recursively; OpenQASM defs cannot recurse",
                opByName.TryGetValue(cycle[0], out var cyc) ? cyc.Span : null);

        // QSEM023 (op vs op) — the mangling transform is not injective across dots-vs-underscores:
        // `A.F` and `A__F` both emit as `A__F_`. Distinct names meeting at one def name would silently
        // merge two operations, so it is rejected here (same-name duplicates are QSEM008/022 above).
        var defsByMangled = new Dictionary<string, string>(); // mangled def name -> original op name
        foreach (var clash in program.Operations.Where(o => o != entry)
                     .GroupBy(o => NameMangler.Mangled(o.Name)))
        {
            var names = clash.Select(o => o.Name).Distinct().ToList();
            if (names.Count > 1)
                Add(errors, "QSEM023",
                    $"operations {string.Join(" and ", names.Select(n => $"`{n}`"))} would both be emitted as `{clash.Key}` (namespace dots become `__` in the output); rename one",
                    clash.Last().Span ?? clash.First().Span);
            defsByMangled.TryAdd(clash.Key, names[0]);
        }

        foreach (var op in program.Operations)
        {
            var isEntry = op == entry;

            // QSEM013 — names whose MEANING cannot be disambiguated stay reserved: the measurement
            // family and pi/tau/euler (expression-position tokens the resolver never sees), and
            // built-in GATE names in the GLOBAL namespace only — a global op has no qualifier, so a
            // gate-named one could never be referenced. Inside a namespace a gate name is legal
            // (Q#-style): the resolver makes any ambiguous USE an explicit QSEM018, never a silent pick.
            var simpleName = Simple(op.Name);
            if (QoraGates.MeasureLike.Contains(simpleName) || IsBuiltinConstant(simpleName))
                Add(errors, "QSEM013", $"operation name `{simpleName}` shadows the built-in `{simpleName}`; choose another name", op.Span);
            else if (!op.Name.Contains('.') && QoraGates.Names.ContainsKey(simpleName))
                Add(errors, "QSEM013", $"global operation `{simpleName}` shadows the built-in gate `{simpleName}` (the global namespace shares one scope with the built-ins, so it has no qualifier to disambiguate with); move it into a namespace or rename it", op.Span);

            // QSEM015 — parameters, `use` registers, and hoisted measure bits share one emitted scope.
            foreach (var dup in op.Params.GroupBy(p => p.Name).Where(g => g.Count() > 1))
                Add(errors, "QSEM015", $"in `{op.Name}`: the parameter name `{dup.Key}` is used twice; each parameter needs a unique name",
                    dup.Skip(1).First().Span ?? dup.First().Span);
            var useNames = new Dictionary<string, QSpan?>();
            var measureBits = new Dictionary<string, QSpan?>();
            CollectScopedNames(op.Body, useNames, measureBits);
            foreach (var p in op.Params)
            {
                CheckShadowsConstant(p.Name, op.Name, "parameter", errors, p.Span);
                if (useNames.ContainsKey(p.Name) || measureBits.ContainsKey(p.Name))
                    Add(errors, "QSEM015", $"in `{op.Name}`: `{p.Name}` is declared more than once (parameter vs register/measure bit)", p.Span);
            }
            foreach (var clash in useNames.Keys.Intersect(measureBits.Keys))
                Add(errors, "QSEM015", $"in `{op.Name}`: `{clash}` names both a qubit register and a measure bit; hoisting would merge them — rename one",
                    measureBits[clash] ?? useNames[clash]);

            var scope = BuildScope(op, entry.Name, isEntry);

            // QSEM023 (local vs def) — a declared name that lands on an emitted def's name breaks that
            // name's meaning in the output. The entry op's declarations become QASM TOP-LEVEL globals —
            // the same scope every def lives in, so any hit is illegal. Inside another def a local only
            // breaks the defs that op actually CALLS (the call would resolve to the local instead).
            var declared = scope.Registers.Keys.Concat(scope.SingleQubits).Concat(scope.Classicals);
            if (isEntry)
            {
                foreach (var d in declared)
                    if (defsByMangled.TryGetValue(NameMangler.Mangled(d), out var hit))
                        Add(errors, "QSEM023",
                            $"in `{op.Name}`: `{d}` collides with operation `{hit}` — the entry operation's declarations share the QASM top level with the emitted defs; rename one",
                            scope.DeclSpans.GetValueOrDefault(d));
            }
            else
            {
                var called = new HashSet<string>();
                CollectOpRefs(op.Body, ops, called);
                var calledByMangled = called.ToDictionary(NameMangler.Mangled, c => c);
                foreach (var d in declared)
                    if (calledByMangled.TryGetValue(NameMangler.Mangled(d), out var hit))
                        Add(errors, "QSEM023",
                            $"in `{op.Name}`: `{d}` shadows operation `{hit}`, which this operation calls — the call would hit the local instead of the def; rename one",
                            scope.DeclSpans.GetValueOrDefault(d));
            }

            Walk(op.Body, scope, ops, opByName, inverter, errors, inControlFlow: false);
        }

        return errors;
    }

    /// <summary>What the walk needs to know about the operation it is inside.</summary>
    private sealed record Scope(
        QOperation Op, string EntryName, bool IsEntry,
        Dictionary<string, int> Registers,       // register name -> size (sized qubit params + use)
        HashSet<string> SingleQubits,            // unsized qubit parameter names
        HashSet<string> Classicals,              // int/bit params + declared variables + loop vars
        Dictionary<string, QSpan?> DeclSpans)    // declared name -> its declaration's span (first wins)
    {
        public HashSet<string> UseNames { get; } = new();
    }

    private static Scope BuildScope(QOperation op, string entryName, bool isEntry)
    {
        var registers = new Dictionary<string, int>();
        var singles = new HashSet<string>();
        var classicals = new HashSet<string>();
        var declSpans = new Dictionary<string, QSpan?>();

        foreach (var p in op.Params)
        {
            if (p.Type == QType.Qubit && p.RegisterSize is int n) registers[p.Name] = n;
            else if (p.Type == QType.Qubit) singles.Add(p.Name);
            else classicals.Add(p.Name);
            declSpans.TryAdd(p.Name, p.Span);
        }

        void Scan(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: registers.TryAdd(u.Name, u.Size); declSpans.TryAdd(u.Name, u.Span); break;
                    case QDecl d: classicals.Add(d.Name); declSpans.TryAdd(d.Name, d.Span); break;
                    case QFor f: classicals.Add(f.Var); declSpans.TryAdd(f.Var, f.Span); Scan(f.Body); break;
                    case QIf i: Scan(i.Then); Scan(i.Else); break;
                    case QWhile w: Scan(w.Body); break;
                    case QRepeat r: Scan(r.Body); break;
                    case QConjugate c: Scan(c.Within); Scan(c.Apply); break;
                }
        }
        Scan(op.Body);
        return new Scope(op, entryName, isEntry, registers, singles, classicals, declSpans);
    }

    /// <summary>Collect `use` register names and measure-bit declaration names, with spans (for QSEM015).</summary>
    private static void CollectScopedNames(IReadOnlyList<QStmt> stmts, Dictionary<string, QSpan?> useNames, Dictionary<string, QSpan?> measureBits)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QUse u: useNames.TryAdd(u.Name, u.Span); break;
                case QDecl { Value: QMeasure } d: measureBits.TryAdd(d.Name, d.Span); break;
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
                        Add(errors, "QSEM012", $"in `{opName}`: `use {u.Name} = Qubit[{u.Size}];` is not supported inside an operation; allocate in `{scope.EntryName}` and pass the qubits as a parameter", u.Span);
                    else if (inControlFlow)
                        Add(errors, "QSEM012", $"in `{opName}`: `use {u.Name} = ...` inside a loop or branch is not supported; allocate once at the top level", u.Span);
                    else if (!scope.UseNames.Add(u.Name))
                        Add(errors, "QSEM015", $"in `{opName}`: the register name `{u.Name}` is used twice; each `use` needs a unique name", u.Span);
                    CheckShadowsConstant(u.Name, opName, "register", errors, u.Span);
                    break;

                case QDecl d:
                    if (d.Value is QText { HasCall: true })
                        Add(errors, "QSEM005", $"in `{opName}`: the initializer of `{d.Name}` contains a call; only the lone form `bit r = M(q[i]);` is supported", d.Span);
                    if (d.Value is QMeasure dm)
                    {
                        // QSEM017 — measure results are bits; QSEM016 — validate the measured index.
                        if (d.Type is not null && d.Type != QType.Bit)
                            Add(errors, "QSEM017", $"in `{opName}`: `{d.Name}` is declared `{d.Type.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`", d.Span);
                        if (dm.Target is not null) CheckQubitIndex(dm.Target, scope, errors, d.Span);
                    }
                    CheckShadowsConstant(d.Name, opName, "variable", errors, d.Span);
                    break;

                case QAssign a:
                    if (a.Value is QText { HasCall: true })
                        Add(errors, "QSEM005", $"in `{opName}`: the value assigned to `{a.Name}` contains a call; only the lone form `{a.Name} = M(q[i]);` is supported", a.Span);
                    if (a.Value is QMeasure am && am.Target is not null)
                        CheckQubitIndex(am.Target, scope, errors, a.Span);
                    break;

                case QIf i:
                    CheckCondition(i.Cond, opName, errors, i.Span);
                    Walk(i.Then, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    Walk(i.Else, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QFor f:
                    CheckShadowsConstant(f.Var, opName, "loop variable", errors, f.Span);
                    Walk(f.Body, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QWhile w:
                    CheckCondition(w.Cond, opName, errors, w.Span);
                    Walk(w.Body, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    break;
                case QRepeat r:
                    Walk(r.Body, scope, ops, opByName, inverter, errors, inControlFlow: true);
                    CheckCondition(r.Until, opName, errors, r.Span);
                    break;
                case QConjugate c:
                    Walk(c.Within, scope, ops, opByName, inverter, errors, inControlFlow);
                    Walk(c.Apply, scope, ops, opByName, inverter, errors, inControlFlow);
                    break;
            }
        }
    }

    // QSEM013 — the only names a declaration may not take are the built-in constants: an expression
    // token `pi` must always mean THE pi (the mangler leaves those tokens alone, so a user `pi` would
    // be irrecoverably ambiguous in expressions). All other collisions are emission-side and vanish
    // under the NameMangler pass.
    private static bool IsBuiltinConstant(string name) => name is "pi" or "tau" or "euler";

    /// <summary>The simple (last) segment of a possibly fully-qualified name.</summary>
    private static string Simple(string name) => name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

    private static void CheckShadowsConstant(string name, string opName, string kind, List<QoraError> errors, QSpan? span)
    {
        if (IsBuiltinConstant(name))
            Add(errors, "QSEM013", $"in `{opName}`: {kind} name `{name}` shadows the built-in constant `{name}`; choose another name", span);
    }

    // QSEM005 — OpenQASM has no measurement expressions: a condition cannot measure.
    private static void CheckCondition(QCond cond, string opName, List<QoraError> errors, QSpan? span)
    {
        if (cond.HasCall)
            Add(errors, "QSEM005", $"in `{opName}`: a condition cannot contain a measurement; measure into a bit first (`bit r = M(q[i]);`) and test the bit", span);
    }

    private static void CheckGate(QGate g, Scope scope, HashSet<string> ops,
        Dictionary<string, QOperation> opByName, Inverter inverter, List<QoraError> errors)
    {
        var opName = scope.Op.Name;

        // QSEM005 — calls inside gate arguments have no OpenQASM form.
        foreach (var arg in g.Args)
            if (arg is QTextArg { HasCall: true })
                Add(errors, "QSEM005", $"in `{opName}`: an argument of `{g.Name}` contains a call; measure into a bit first and pass the bit", g.Span);

        // QSEM016 — literal indices must fit their register; a single qubit cannot be indexed.
        foreach (var arg in g.Args)
            if (arg is QQubitArg qa)
                CheckQubitIndex(qa, scope, errors, g.Span);

        // QSEM014 — the same qubit twice in one gate. Whole registers count: `CNOT(q, q)` broadcasts to
        // duplicate operands, and `CNOT(q, q[0])` overlaps the register with its own element.
        var refs = g.Args.Select(a => QubitRefOf(a, scope)).Where(r => r is not null).Select(r => r!.Value).ToList();
        foreach (var dup in refs.GroupBy(r => (r.Reg, r.Index)).Where(grp => grp.Count() > 1))
            Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the qubit `{Show(dup.Key.Reg, dup.Key.Index)}` more than once; gate operands must be distinct", g.Span);
        foreach (var whole in refs.Where(r => r.Index is null).Select(r => r.Reg).Distinct())
            if (refs.Any(r => r.Reg == whole && r.Index is not null))
                Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the register `{whole}` and one of its own qubits; operands must not overlap", g.Span);

        // QSEM009 — the entry op is emitted as the QASM top level, not as a def: nothing can call it.
        if (g.Name == scope.EntryName)
        {
            Add(errors, "QSEM009", $"in `{opName}`: the entry operation `{scope.EntryName}` cannot be called (its body is the program's top level, not a def)", g.Span);
            return;
        }

        // QSEM004 — measurement only exists in the assignment forms.
        if (!ops.Contains(g.Name) && QoraGates.MeasureLike.Contains(g.Name))
        {
            Add(errors, "QSEM004", $"in `{opName}`: a bare measurement statement is not supported: assign the result instead: `bit r = {QoraGates.Measurement}(q[i]);`", g.Span);
            return;
        }

        if (ops.Contains(g.Name))
        {
            // QSEM002 — OpenQASM gate modifiers apply to gates only, never to def subroutine calls.
            if (g.Functors.Contains("Controlled"))
            {
                Add(errors, "QSEM002", $"in `{opName}`: `Controlled {g.Name}` is not supported: OpenQASM cannot apply ctrl @ to a def", g.Span);
                return;
            }

            // QSEM001 — Adjoint on a user operation compiles to a synthesized inverse def, which must exist.
            if (g.Functors.FirstOrDefault() == "Adjoint" && !inverter.TryInvertOperation(g.Name, out _, out var reason))
            {
                Add(errors, "QSEM001", $"in `{opName}`: `Adjoint {g.Name}` cannot be compiled: {reason}", g.Span);
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
            Add(errors, "QSEM007", $"in `{opName}`: `{g.Name}` is not a known gate or operation" + (hint is null ? string.Empty : $" (did you mean `{hint}`?)"), g.Span);
            return;
        }

        // QSEM003 — reset is a statement, not a gate: no inv @ / ctrl @ on it.
        if (QoraGates.NonUnitary.Contains(g.Name) && g.Functors.Count > 0)
        {
            Add(errors, "QSEM003", $"in `{opName}`: `{string.Join(" ", g.Functors)} {g.Name}` is not supported: reset is not a gate and takes no modifiers", g.Span);
            return;
        }

        // QSEM006 — built-ins: argument count, then argument KIND per slot (only when the count fits,
        // so the slots line up). Rotations take the angle first; every other slot is a qubit.
        if (QoraGates.Arity.TryGetValue(g.Name, out var baseArity))
        {
            var expected = baseArity + (g.Functors.Contains("Controlled") ? 1 : 0);
            if (g.Args.Count != expected)
            {
                Add(errors, "QSEM006", $"in `{opName}`: `{(g.Functors.Count > 0 ? string.Join(" ", g.Functors) + " " : "")}{g.Name}` expects {expected} argument(s) but got {g.Args.Count}", g.Span);
                return;
            }

            var qubitStart = 0;
            if (QoraGates.Rotations.Contains(g.Name))
            {
                qubitStart = 1;
                if (IsQubitLike(g.Args[0], scope))
                    Add(errors, "QSEM006", $"in `{opName}`: the first argument of `{g.Name}` is the rotation angle, but a qubit was passed (write `{g.Name}(angle, qubit)`)", g.Span);
            }
            for (var i = qubitStart; i < g.Args.Count; i++)
                if (IsDefinitelyNotQubit(g.Args[i], scope))
                    Add(errors, "QSEM006", $"in `{opName}`: argument {i + 1} of `{g.Name}` must be a qubit (like `q[0]`), not a number or classical value", g.Span);
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
            Add(errors, "QSEM006", $"in `{opName}`: `{callee.Name}` expects {callee.Params.Count} argument(s) but got {g.Args.Count}", g.Span);
            return;
        }

        for (int i = 0; i < callee.Params.Count; i++)
        {
            var p = callee.Params[i];
            var arg = g.Args[i];

            if (p.Type == QType.Qubit && p.RegisterSize is int need)
            {
                if (arg is QQubitArg qa)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a register of {need} qubit(s), but `{qa.Reg}[{qa.Index}]` is a single qubit", g.Span);
                else if (arg is QTextArg ta && scope.Registers.TryGetValue(ta.Text, out var have) && have != need)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a register of {need} qubit(s), but `{ta.Text}` has {have}", g.Span);
                else if (arg is QTextArg tb && !scope.Registers.ContainsKey(tb.Text) && (scope.SingleQubits.Contains(tb.Text) || IsClassicalText(tb.Text, scope)))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a qubit register, but `{tb.Text}` is not one", g.Span);
            }
            else if (p.Type == QType.Qubit)
            {
                if (arg is QTextArg ta && (scope.Registers.ContainsKey(ta.Text) || IsClassicalText(ta.Text, scope)))
                    Add(errors, "QSEM006", scope.Registers.ContainsKey(ta.Text)
                        ? $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a single qubit, but `{ta.Text}` is a whole register (pass `{ta.Text}[i]`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is a qubit, but `{ta.Text}` is not one", g.Span);
            }
            else if (IsQubitLike(arg, scope))
            {
                Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{callee.Name}` is `{p.Type.ToString()!.ToLowerInvariant()}`, but a qubit was passed", g.Span);
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
    private static void CheckQubitIndex(QQubitArg q, Scope scope, List<QoraError> errors, QSpan? span)
    {
        var opName = scope.Op.Name;
        if (scope.SingleQubits.Contains(q.Reg))
        {
            Add(errors, "QSEM016", $"in `{opName}`: `{q.Reg}` is a single qubit and cannot be indexed (`{q.Reg}[{q.Index}]`)", span);
            return;
        }
        if (scope.Registers.TryGetValue(q.Reg, out var size)
            && q.Index.Length > 0 && q.Index.All(char.IsDigit)
            && int.Parse(q.Index) >= size)
        {
            Add(errors, "QSEM016", $"in `{opName}`: index `{q.Reg}[{q.Index}]` is out of range; `{q.Reg}` has {size} qubit(s) (valid: 0..{size - 1})", span);
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

    private static void Add(List<QoraError> errors, string code, string message, QSpan? span = null) =>
        errors.Add(new QoraError(message, code, span?.Start ?? -1, span?.End ?? -1));
}
