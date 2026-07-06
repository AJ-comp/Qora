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
///   <item><b>QSEM005</b> — a call inside an expression (arithmetic, argument, non-measure value, or a
///         non-measurement call in a condition). A MEASUREMENT in a condition is NOT rejected — it is
///         lowered to a bit by <see cref="MeasureConditionLowering"/> before this pass runs.</item>
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
///         Every other name is free — the <see cref="NameMangler"/> pass renames emitted identifiers
///         only when they collide with target-world names (stdgates, keywords) or another emitted name.</item>
///   <item><b>QSEM014</b> — the same qubit (or overlapping register/element) passed twice to one gate.</item>
///   <item><b>QSEM015</b> — duplicate declared names within one scope: parameters and <c>use</c>
///         registers seed the top-level scope; measure bits, vars, consts and loop variables are
///         block-scoped. Every declaration flows through the symbol table's one insertion door, which
///         rejects any same-scope collision.</item>
///   <item><b>QSEM016</b> — a literal qubit index out of the register's range, indexing a single-qubit
///         parameter, or a register size that is not a positive 32-bit int (a huge <c>Qubit[99999999999]</c>
///         that overflows, or <c>Qubit[0]</c>) — caught cleanly rather than crashing the parser.</item>
///   <item><b>QSEM017</b> — a measurement assigned to a non-<c>bit</c> declaration.</item>
///   <item><b>QSEM022</b> — the same operation name defined more than once WITHIN one namespace
///         (namespaced twin of QSEM008; the same simple name in two different namespaces is fine).</item>
///   <item><b>QSEM024</b> — assignment to a <c>const</c> name. <c>const</c> is an immutable BINDING
///         (JS/Q#-let style): the initializer may be any value — a measurement result included — but
///         the name can never be assigned again; use <c>var</c>/<c>bit</c> for mutable ones.</item>
///   <item><b>QSEM025</b> — a name referenced but not resolvable in scope at that point: an unknown
///         identifier, or a block-scoped name (measure bit, var, const, loop variable) used before its
///         declaration. Only <c>use</c> registers are HOISTED, so they alone may be referenced before their
///         textual line. Raised by <see cref="SymbolTableBuilder"/> as every expression identifier is
///         resolved against the unified symbol table (pi/tau/euler/true/false and the symbolic register size
///         are exempt).</item>
///   <item><b>QSEM026</b> — a qubit used where a CLASSICAL is required: inside a condition
///         (<c>if (q == 1)</c>), a range bound (<c>0..q</c>), an arithmetic initializer/assignment
///         (<c>var x = q + 1</c>) — raised by <see cref="SymbolTableBuilder"/> — buried in a rotation
///         angle / classical argument expression (<c>Rx(pi / q, …)</c>) — raised by <c>CheckCall</c> — or as
///         an assignment TARGET (<c>q = 5;</c>, a qubit is not an assignable classical variable). A qubit has
///         no numeric value, so any of these would emit invalid QASM. (A whole qubit passed straight into a
///         value slot, e.g. <c>Rx(q, …)</c>, is the argument-KIND error QSEM006.)</item>
///   <item><b>QSEM023</b> — reserved. Emitted-name collisions are no longer rejected here; the
///         <see cref="NameMangler"/> pass auto-renames colliding emitted identifiers and records a
///         <c>// Qora:</c> note.</item>
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

        // Name collisions in the emitted QASM (op vs op, or a declaration vs a def name) are NOT rejected:
        // NameMangler auto-resolves them by appending `_` and records a `// Qora:` note in the output, so
        // any validated program emits. (Same-source-name duplicates are still QSEM008/015/022 above.)

        foreach (var op in program.Operations)
        {
            // QSEM013 — names whose MEANING cannot be disambiguated stay reserved: the measurement
            // family and pi/tau/euler (expression-position tokens the resolver never sees), and
            // built-in GATE names in the GLOBAL namespace only — a global op has no qualifier, so a
            // gate-named one could never be referenced. Inside a namespace a gate name is legal
            // (Q#-style): the resolver makes any ambiguous USE an explicit QSEM018, never a silent pick.
            var simpleName = Simple(op.Name);
            if (QoraGates.MeasureLike.Contains(simpleName) || SymbolTableBuilder.IsReservedName(simpleName))
                Add(errors, "QSEM013", $"operation name `{simpleName}` shadows the built-in `{simpleName}`; choose another name", op.Span);
            else if (!op.Name.Contains('.') && QoraGates.Names.ContainsKey(simpleName))
                Add(errors, "QSEM013", $"global operation `{simpleName}` shadows the built-in gate `{simpleName}` (the global namespace shares one scope with the built-ins, so it has no qualifier to disambiguate with); move it into a namespace or rename it", op.Span);

            // QSEM016 — a sized qubit parameter `Qubit[N] q` whose N is not a positive int (0, or -1 from an
            // int overflow like `Qubit[99999999999] q`). A symbolic size `Qubit[n] q` has RegisterSize null.
            foreach (var p in op.Params)
                if (p.Type == QType.Qubit && p.RegisterSize is int rs && rs < 1)
                    Add(errors, "QSEM016", $"in `{op.DisplayName ?? op.Name}`: parameter `{p.Name}` has an invalid register size; it must be a whole number from 1 to {int.MaxValue}", p.Span);

            // The unified symbol table IS the scope model: a nested scope tree in which every name carries
            // its kind / type / register size. Building it reports EVERY same-scope collision the emitter
            // cannot tolerate (QSEM015) through the single `Declare` insertion door — parameters and `use`
            // registers at the root, block-scoped measure bits / vars / consts / loop vars in their block
            // alike — so there are no parallel duplicate-name checks out here. `scopeOf` maps each body list
            // to its scope so the walk below resolves every operand with correct nesting. Nothing re-derives it.
            var scopeOf = new Dictionary<IReadOnlyList<QStmt>, Scope>();
            var root = SymbolTableBuilder.Build(op, errors, scopeOf);

            var ctx = new Ctx(op, entry.Name, ops, opByName, inverter, errors, scopeOf);
            Walk(op.Body, root, ctx, inControlFlow: false);
        }

        return errors;
    }

    /// <summary>Everything the per-operation walk needs beyond the current lexical <see cref="Scope"/> (the
    /// unified symbol table): the operation, the entry name (for `use` placement), the callable sets, and
    /// <c>ScopeOf</c> — the body-list → scope map the symbol table produced, so recursing into a branch/loop
    /// resolves names in the right nested scope.</summary>
    private sealed record Ctx(
        QOperation Op, string EntryName, HashSet<string> Ops, Dictionary<string, QOperation> OpByName,
        Inverter Inverter, List<QoraError> Errors,
        IReadOnlyDictionary<IReadOnlyList<QStmt>, Scope> ScopeOf);

    private static void Walk(IReadOnlyList<QStmt> stmts, Scope scope, Ctx ctx, bool inControlFlow)
    {
        var opName = ctx.Op.DisplayName ?? ctx.Op.Name;
        Scope Child(IReadOnlyList<QStmt> body) => ctx.ScopeOf.TryGetValue(body, out var s) ? s : scope;

        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QGate g:
                    CheckGate(g, scope, ctx);
                    break;

                // QSEM012 — `use` only at the top level of the entry op. A duplicate register name is
                // QSEM015, but that is caught by the symbol table's `Declare` door (registers seed the root),
                // so it is not re-checked here.
                case QUse u:
                    if (u.Size < 1)   // 0, or -1 from a size that overflowed a 32-bit int (`Qubit[99999999999]`)
                        Add(ctx.Errors, "QSEM016", $"in `{opName}`: `use {u.Name} = Qubit[…];` has an invalid register size; it must be a whole number from 1 to {int.MaxValue}", u.Span);
                    if (opName != ctx.EntryName)
                        Add(ctx.Errors, "QSEM012", $"in `{opName}`: `use {u.Name} = Qubit[{u.Size}];` is not supported inside an operation; allocate in `{ctx.EntryName}` and pass the qubits as a parameter", u.Span);
                    else if (inControlFlow)
                        Add(ctx.Errors, "QSEM012", $"in `{opName}`: `use {u.Name} = ...` inside a loop or branch is not supported; allocate once at the top level", u.Span);
                    break;

                case QDecl d:
                    if (d.Value is QText { HasCall: true })
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: the initializer of `{d.Name}` contains a call; only the lone form `bit r = M(q[i]);` is supported", d.Span);
                    if (d.Value is QMeasure dm)
                    {
                        // QSEM017 — measure results are bits; QSEM016 — validate the measured index.
                        if (d.Type is not null && d.Type != QType.Bit)
                            Add(ctx.Errors, "QSEM017", $"in `{opName}`: `{d.Name}` is declared `{d.Type.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`", d.Span);
                        if (dm.Target is not null) CheckQubitIndex(dm.Target, scope, opName, ctx.Errors, d.Span);
                    }
                    break;

                case QAssign a:
                    // The assignment TARGET's kind, resolved once. `a.Name` is also recorded as a use by the
                    // symbol table, so an unknown target is QSEM025 there.
                    var target = scope.Lookup(a.Name);
                    // QSEM024 — `const` is an IMMUTABLE BINDING (JS/Q#-let style): it may hold any value,
                    // including a measurement result, but can never be assigned again. The symbol table
                    // resolves `a.Name` to its nearest binding, so a local `var` shadowing an outer `const`
                    // is correctly mutable here.
                    if (target?.IsConst == true)
                        Add(ctx.Errors, "QSEM024", a.Value is QMeasure
                            ? $"in `{opName}`: `{a.Name}` is `const` and cannot be measured into again; declare it as `bit {a.Name} = ...` if it should be re-measured"
                            : $"in `{opName}`: `{a.Name}` is `const` and cannot be reassigned; declare it with `var` if it needs to change", a.Span);
                    // QSEM026 — a qubit register is not an assignable classical variable; assigning to it
                    // (`q = 5;`) would emit invalid QASM. A qubit's state changes only through gates/measurement.
                    else if (target is { Type: QType.Qubit })
                        Add(ctx.Errors, "QSEM026", $"in `{opName}`: `{a.Name}` is a qubit and cannot be assigned to — a qubit is not a classical variable; change its state with a gate or measurement", a.Span);
                    // QSEM017 — a measurement result is a `bit`; assigning it to a non-bit classical is the
                    // same type error as declaring `int r = M(...)`.
                    else if (a.Value is QMeasure && target is { Type: { } tt } && tt != QType.Bit)
                        Add(ctx.Errors, "QSEM017", $"in `{opName}`: `{a.Name}` is `{tt.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`", a.Span);
                    if (a.Value is QText { HasCall: true })
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: the value assigned to `{a.Name}` contains a call; only the lone form `{a.Name} = M(q[i]);` is supported", a.Span);
                    if (a.Value is QMeasure am && am.Target is not null)
                        CheckQubitIndex(am.Target, scope, opName, ctx.Errors, a.Span);
                    break;

                case QIf i:
                    CheckCondition(i.Cond, opName, ctx.Errors, i.Span);
                    Walk(i.Then, Child(i.Then), ctx, inControlFlow: true);
                    Walk(i.Else, Child(i.Else), ctx, inControlFlow: true);
                    break;
                case QFor f:
                    // A `for` bound is a plain expression; a call/measurement there has no OpenQASM lowering
                    // (and only a call renders a `(` into the bound text). Reject it like other call
                    // positions (QSEM005) instead of shipping invalid QASM.
                    if (f.From.Contains('(') || f.To.Contains('('))
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: a `for` bound cannot contain a call or measurement; measure into a bit first and use a numeric or variable bound", f.Span);
                    Walk(f.Body, Child(f.Body), ctx, inControlFlow: true);
                    break;
                case QWhile w:
                    CheckCondition(w.Cond, opName, ctx.Errors, w.Span);
                    Walk(w.Body, Child(w.Body), ctx, inControlFlow: true);
                    break;
                case QRepeat r:
                    Walk(r.Body, Child(r.Body), ctx, inControlFlow: true);
                    CheckCondition(r.Until, opName, ctx.Errors, r.Span);
                    break;
                case QConjugate c:
                    Walk(c.Within, Child(c.Within), ctx, inControlFlow);
                    Walk(c.Apply, Child(c.Apply), ctx, inControlFlow);
                    break;
            }
        }
    }

    /// <summary>The simple (last) segment of a possibly fully-qualified name.</summary>
    private static string Simple(string name) => name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

    // QSEM005 — a measurement in a condition IS allowed (MeasureConditionLowering hoists it to a bit before
    // validation), so a call still here is a non-measurement one (a user op) — which has no place in a
    // condition. OpenQASM has no call/measurement EXPRESSIONS; only `M(q[i])` is lowered for you.
    private static void CheckCondition(QCond cond, string opName, List<QoraError> errors, QSpan? span)
    {
        if (cond.HasCall)
            Add(errors, "QSEM005", $"in `{opName}`: a condition cannot call an operation; only a measurement `M(q[i])` is allowed here (it is lowered to a bit automatically)", span);
    }

    private static void CheckGate(QGate g, Scope scope, Ctx ctx)
    {
        var opName = ctx.Op.DisplayName ?? ctx.Op.Name;
        var errors = ctx.Errors;

        // QSEM005 — calls inside gate arguments have no OpenQASM form.
        foreach (var arg in g.Args)
            if (arg is QTextArg { HasCall: true })
                Add(errors, "QSEM005", $"in `{opName}`: an argument of `{g.Name}` contains a call; measure into a bit first and pass the bit", g.Span);

        // QSEM016 — literal indices must fit their register; a single qubit cannot be indexed.
        foreach (var arg in g.Args)
            if (arg is QQubitArg qa)
                CheckQubitIndex(qa, scope, opName, errors, g.Span);

        // QSEM014 — the same qubit twice in one gate. Whole registers count: `CNOT(q, q)` broadcasts to
        // duplicate operands, and `CNOT(q, q[0])` overlaps the register with its own element.
        var refs = g.Args.Select(a => QubitRefOf(a, scope)).Where(r => r is not null).Select(r => r!.Value).ToList();
        foreach (var dup in refs.GroupBy(r => (r.Reg, r.Index)).Where(grp => grp.Count() > 1))
            Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the qubit `{Show(dup.Key.Reg, dup.Key.Index)}` more than once; gate operands must be distinct", g.Span);
        foreach (var whole in refs.Where(r => r.Index is null).Select(r => r.Reg).Distinct())
            if (refs.Any(r => r.Reg == whole && r.Index is not null))
                Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the register `{whole}` and one of its own qubits; operands must not overlap", g.Span);

        // QSEM009 — the entry op is emitted as the QASM top level, not as a def: nothing can call it.
        if (g.Name == ctx.EntryName)
        {
            Add(errors, "QSEM009", $"in `{opName}`: the entry operation `{ctx.EntryName}` cannot be called (its body is the program's top level, not a def)", g.Span);
            return;
        }

        // QSEM004 — measurement only exists in the assignment forms.
        if (!ctx.Ops.Contains(g.Name) && QoraGates.MeasureLike.Contains(g.Name))
        {
            Add(errors, "QSEM004", $"in `{opName}`: a bare measurement statement is not supported: assign the result instead: `bit r = {QoraGates.Measurement}(q[i]);`", g.Span);
            return;
        }

        if (ctx.Ops.Contains(g.Name))
        {
            // QSEM002 — OpenQASM gate modifiers apply to gates only, never to def subroutine calls.
            if (g.Functors.Contains("Controlled"))
            {
                Add(errors, "QSEM002", $"in `{opName}`: `Controlled {g.Name}` is not supported: OpenQASM cannot apply ctrl @ to a def", g.Span);
                return;
            }

            // QSEM001 — Adjoint on a user operation compiles to a synthesized inverse def, which must exist.
            if (g.Functors.FirstOrDefault() == "Adjoint" && !ctx.Inverter.TryInvertOperation(g.Name, out _, out var reason))
            {
                Add(errors, "QSEM001", $"in `{opName}`: `Adjoint {g.Name}` cannot be compiled: {reason}", g.Span);
                return;
            }

            // QSEM006 — a def call must match the def's signature (an Adjoint call shares it). A user op IS
            // an ICallableSig, so the one CheckCall a built-in gate uses validates it too.
            if (ctx.OpByName.TryGetValue(g.Name, out var callee))
                CheckCall(callee, g.Args, "", scope, opName, errors, g.Span);
            return;
        }

        // QSEM007 — not a user op and not a known built-in: a typo would otherwise emit an undefined gate.
        if (!QoraGates.Names.ContainsKey(g.Name))
        {
            var hint = QoraGates.Names.Keys.Concat(ctx.Ops).FirstOrDefault(k => string.Equals(k, g.Name, StringComparison.OrdinalIgnoreCase));
            Add(errors, "QSEM007", $"in `{opName}`: `{g.Name}` is not a known gate or operation" + (hint is null ? string.Empty : $" (did you mean `{hint}`?)"), g.Span);
            return;
        }

        // QSEM003 — reset is a statement, not a gate: no inv @ / ctrl @ on it.
        if (QoraGates.NonUnitary.Contains(g.Name) && g.Functors.Count > 0)
        {
            Add(errors, "QSEM003", $"in `{opName}`: `{string.Join(" ", g.Functors)} {g.Name}` is not supported: reset is not a gate and takes no modifiers", g.Span);
            return;
        }

        // QSEM006 — a built-in gate is an ICallableSig too: QoraGates.SigOf derives its slots from GateInfo
        // (a rotation exposes a leading angle value slot; every qubit slot broadcasts), and a Controlled
        // functor adds one leading qubit slot. So the SAME CheckCall validates count + per-slot kind.
        var gateSig = QoraGates.SigOf(g.Name, g.Functors.Contains("Controlled") ? 1 : 0);
        if (gateSig is not null)
            CheckCall(gateSig, g.Args, g.Functors.Count > 0 ? string.Join(" ", g.Functors) + " " : "", scope, opName, errors, g.Span);
    }

    /// <summary>
    /// QSEM006 for ANY call — a built-in gate or a user operation — against its <see cref="ICallableSig"/>:
    /// argument count, then per-slot KIND. A VALUE slot (a rotation angle, or a classical parameter) rejects
    /// a qubit. A QUBIT slot rejects a classical, and — for a user op — checks exact shape/size: a sized
    /// register needs that many qubits, a single-qubit param one qubit, a generic <c>Qubit[n]</c> a known
    /// register to bind <c>n</c>. A built-in gate's qubit slots broadcast (a whole register applies
    /// element-wise), so they only require "is a qubit". The check reads the slot spec, never whether the
    /// callee is a gate or an op; only message wording consults <see cref="ICallableSig.IsBuiltin"/>.
    /// Identifiers the caller's scope does not resolve are left alone (treated as not-provably-wrong).
    /// </summary>
    private static void CheckCall(ICallableSig sig, IReadOnlyList<QArg> args, string functorPrefix,
        Scope scope, string opName, List<QoraError> errors, QSpan? span)
    {
        var calleeName = sig.CalleeName;

        if (args.Count != sig.Params.Count)
        {
            Add(errors, "QSEM006", $"in `{opName}`: `{functorPrefix}{calleeName}` expects {sig.Params.Count} argument(s) but got {args.Count}", span);
            return;
        }

        for (var i = 0; i < sig.Params.Count; i++)
        {
            var p = sig.Params[i];
            var arg = args[i];
            var argSym = arg is QTextArg at ? scope.Lookup(at.Text) : null;   // a text arg's resolved symbol (null for `reg[i]`)

            if (p.Type != QType.Qubit)
            {
                // VALUE slot (rotation angle, or classical param): a qubit here is wrong. Three forms — the
                // whole argument IS a qubit, OR an INDEXED reference `name[i]` (Qora has no classical arrays,
                // so `name[i]` is only ever a qubit-reference form — never a scalar value), both QSEM006; OR
                // a qubit is buried inside a classical expression like `pi / q` (QSEM026, the arg-position D).
                if (IsQubitLike(arg, scope) || arg is QQubitArg)
                    Add(errors, "QSEM006", sig.IsBuiltin
                        ? $"in `{opName}`: the first argument of `{calleeName}` is the rotation angle, but a qubit was passed (write `{calleeName}(angle, qubit)`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is `{p.Type.ToString()!.ToLowerInvariant()}`, but a qubit was passed", span);
                else if (arg is QTextArg vt && FirstQubitIn(vt.Text, scope) is { } qn)
                    Add(errors, "QSEM026", $"in `{opName}`: `{qn}` is a qubit, but `{vt.Text}` is used as a classical value ({(sig.IsBuiltin ? "the rotation angle" : $"the `{p.Name}` argument")} of `{calleeName}`) — a qubit has no numeric value", span);
            }
            else if (p.QubitBroadcast)
            {
                // built-in gate qubit slot: any qubit shape (a whole register broadcasts element-wise).
                if (IsDefinitelyNotQubit(arg, scope))
                    Add(errors, "QSEM006", $"in `{opName}`: argument {i + 1} of `{calleeName}` must be a qubit (like `q[0]`), not a number or classical value", span);
            }
            else if (p.RegisterSize is int need)
            {
                if (arg is QQubitArg qa)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a register of {need} qubit(s), but `{qa.Reg}[{qa.Index}]` is a single qubit", span);
                else if (arg is QTextArg ta && IsSizedRegister(argSym, out var have) && have != need)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a register of {need} qubit(s), but `{ta.Text}` has {have}", span);
                else if (arg is QTextArg tb && !IsSizedRegister(argSym, out _) && (IsSingleQubit(argSym) || IsClassicalText(tb.Text, scope)))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit register, but `{tb.Text}` is not one", span);
            }
            else if (p.SizeParam is not null)
            {
                // generic register `Qubit[n]`: n binds from the argument's size, so the argument MUST be a
                // known register — a concrete one, or the caller's own symbolic register (which becomes
                // concrete when the caller is itself specialized). A single qubit, a classical value, or an
                // unknown identifier cannot bind a size.
                if (arg is QQubitArg qa)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit register, but `{qa.Reg}[{qa.Index}]` is a single qubit", span);
                else if (arg is QTextArg ta && !IsSizedRegister(argSym, out _) && !IsSymbolicRegister(argSym))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` needs a qubit register to bind its size `{p.SizeParam}`, but `{ta.Text}` is not a known register", span);
            }
            else
            {
                // single-qubit slot (user op)
                if (arg is QTextArg ta && (IsSizedRegister(argSym, out _) || IsClassicalText(ta.Text, scope)))
                    Add(errors, "QSEM006", IsSizedRegister(argSym, out _)
                        ? $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a single qubit, but `{ta.Text}` is a whole register (pass `{ta.Text}[i]`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit, but `{ta.Text}` is not one", span);
            }
        }
    }

    // --- qubit-shape queries over the unified symbol table ---
    // Every classification the walk needs is a query on the resolved Symbol: Type says qubit-or-not,
    // RegisterSize / SizeParam say sized-register / symbolic-register / single-qubit. There is NO second
    // scope model — a name means whatever `scope.Lookup` resolves it to at this lexical point.

    private static bool IsQubit(Symbol? s) => s?.Type == QType.Qubit;

    private static bool IsSizedRegister(Symbol? s, out int size)
    {
        if (s is { Type: QType.Qubit, RegisterSize: int n } && n >= 1) { size = n; return true; }   // an invalid size (0, or -1 from an int overflow) is not a usable sized register
        size = 0;
        return false;
    }

    private static bool IsSymbolicRegister(Symbol? s) => s is { Type: QType.Qubit, SizeParam: not null };
    private static bool IsSingleQubit(Symbol? s) => s is { Type: QType.Qubit, RegisterSize: null, SizeParam: null };

    private static bool IsQubitLike(QArg arg, Scope scope) => arg switch
    {
        QQubitArg q => IsQubit(scope.Lookup(q.Reg)),
        QTextArg t => IsQubit(scope.Lookup(t.Text)),
        _ => false,
    };

    /// <summary>The first identifier inside a value expression that resolves to a qubit, or null — finds a
    /// qubit smuggled into a classical position (<c>pi / q</c>) that the whole-argument <see cref="IsQubitLike"/>
    /// check misses. Shares the symbol table's tokenizer so both see identifiers identically (QSEM026).</summary>
    private static string? FirstQubitIn(string text, Scope scope) =>
        SymbolTableBuilder.Identifiers(text).FirstOrDefault(n => IsQubit(scope.Lookup(n)));

    /// <summary>True only when the argument provably cannot denote a qubit: a number/expression, or a name
    /// that resolves to a known non-qubit (a classical value, or a register hidden by a local of the same name).</summary>
    private static bool IsDefinitelyNotQubit(QArg arg, Scope scope) => arg switch
    {
        QQubitArg q => scope.Lookup(q.Reg) is { Type: { } t } && t != QType.Qubit,   // `a[0]` where a resolves to an int
        QTextArg t => IsClassicalText(t.Text, scope),
        _ => false,
    };

    private static bool IsClassicalText(string text, Scope scope) =>
        text.Contains(' ')                                              // any operator expression: `pi / 2`, `a + 1`
        || (text.Length > 0 && char.IsDigit(text[0]))                  // numeric literal
        || text is "pi" or "tau" or "euler"
        || (scope.Lookup(text) is { Type: { } t } && t != QType.Qubit); // a name that resolves to a non-qubit

    /// <summary>QSEM016 — literal index bounds against known register sizes; no indexing single qubits.</summary>
    private static void CheckQubitIndex(QQubitArg q, Scope scope, string opName, List<QoraError> errors, QSpan? span)
    {
        var sym = scope.Lookup(q.Reg);
        // QSEM016 — an index must be a classical value; a qubit used as an index (`q[n]` where n is a
        // register) would emit invalid QASM. The symbol table resolves the index name to its actual kind.
        if (scope.Lookup(q.Index) is { Type: QType.Qubit })
            Add(errors, "QSEM016", $"in `{opName}`: `{q.Reg}[{q.Index}]` uses the qubit `{q.Index}` as an index; an index must be a classical value", span);
        if (IsSingleQubit(sym))
        {
            Add(errors, "QSEM016", $"in `{opName}`: `{q.Reg}` is a single qubit and cannot be indexed (`{q.Reg}[{q.Index}]`)", span);
            return;
        }
        if (IsSizedRegister(sym, out var size)
            && q.Index.Length > 0 && q.Index.All(char.IsDigit)
            && (!int.TryParse(q.Index, out var idx) || idx >= size))   // an index too big for int is, a fortiori, out of range — parse safely, don't crash
        {
            Add(errors, "QSEM016", $"in `{opName}`: index `{q.Reg}[{q.Index}]` is out of range; `{q.Reg}` has {size} qubit(s) (valid: 0..{size - 1})", span);
        }
    }

    /// <summary>An argument as a qubit reference: (register, index) — index null for a whole register / single qubit.</summary>
    private static (string Reg, string? Index)? QubitRefOf(QArg arg, Scope scope) => arg switch
    {
        QQubitArg q => (q.Reg, q.Index),
        QTextArg t when IsQubit(scope.Lookup(t.Text)) => (t.Text, (string?)null),
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
