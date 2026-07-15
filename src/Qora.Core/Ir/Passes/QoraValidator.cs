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
///   <item><b>QSEM012</b> — <c>use</c> or a classical-array declaration outside the entry operation's
///         top-level body.</item>
///   <item><b>QSEM013</b> — a declared name that shadows a Qora BUILT-IN where no qualification could
///         ever disambiguate: an operation named like the measurement family (<c>operation M</c>), any
///         declaration named <c>pi</c>/<c>tau</c>/<c>euler</c> (expression-position tokens), a GLOBAL
///         operation named like a built-in gate (the global namespace shares one scope with the
///         built-ins), or declaring the reserved <c>Qora.Intrinsic</c> namespace (raised by the
///         <see cref="Resolver"/>). Inside a namespace, gate names ARE allowed (Q#-style) — an
///         ambiguous use is QSEM018, qualified by <c>MyLib.H(…)</c> / <c>Qora.Intrinsic.H(…)</c>.
///         Every other name is free — the <see cref="NameMangler"/> pass renames emitted identifiers
///         only when they collide with target-world names (stdgates, keywords) or another emitted name.</item>
///   <item><b>QSEM014</b> — overlapping gate operands, or the same mutable classical array passed into
///         more than one array parameter of a call.</item>
///   <item><b>QSEM015</b> — duplicate declared names within one scope: parameters and <c>use</c>
///         registers seed the top-level scope; measure bits, vars, consts and loop variables are
///         block-scoped. Every declaration flows through the symbol table's one insertion door, which
///         rejects any same-scope collision.</item>
///   <item><b>QSEM016</b> — an invalid array/qubit index, indexing a scalar or single qubit, or an
///         allocation/specialization size that is not a positive 32-bit integer.</item>
///   <item><b>QSEM017</b> — a measurement assigned to a non-<c>bit</c> declaration.</item>
///   <item><b>QSEM022</b> — the same operation name defined more than once WITHIN one namespace
///         (namespaced twin of QSEM008; the same simple name in two different namespaces is fine).</item>
///   <item><b>QSEM024</b> — mutation of a <c>const</c> value: reassignment, indexed array update, or passing
///         a const array to a mutable array parameter. The initializer may still be any valid value.</item>
///   <item><b>QSEM025</b> — a name referenced but not resolvable in scope at that point: an unknown
///         identifier, or a block-scoped name (measure bit, var, const, loop variable) used before its
///         declaration. Only <c>use</c> registers are HOISTED, so they alone may be referenced before their
///         textual line. Raised by <see cref="SymbolTableBuilder"/> as every expression identifier is
///         resolved against the unified symbol table (pi/tau/euler/true/false are exempt).</item>
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
///   <item><b>QSEM028</b> — an operation name used as a VALUE (<c>var x = Foo</c>, or an op name in any
///         expression / argument / index slot). An operation can only be CALLED (<c>Foo(…)</c>); it has no
///         value. Raised by <see cref="SymbolTableBuilder"/> when an expression identifier resolves — up the
///         scope chain to the program-level table — to an operation symbol.</item>
///   <item><b>QSEM029</b> — an invalid array shape, initializer, whole-array assignment, or member access.</item>
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
    public static List<QoraError> Validate(QProgram? program) => Validate(program, out _);

    /// <summary>Validate AND keep what validation proved: the scope trees built per operation are collected
    /// into a <see cref="SemanticModel"/> (Id-keyed side table) instead of being discarded, so later stages
    /// consume the validation-time facts rather than re-deriving them.</summary>
    public static List<QoraError> Validate(QProgram? program, out SemanticModel? model)
    {
        model = null;
        var errors = new List<QoraError>();
        if (program is null) return errors;

        if (program.Operations.Count == 0) return errors;
        model = new SemanticModel();

        var ops = program.Operations.Select(o => o.Name).ToHashSet();
        var opByName = new Dictionary<string, QOperation>();
        var opById = new Dictionary<int, QOperation>();          // call-site → callee resolution by reference (CalleeOpId)

        foreach (var o in program.Operations) { opByName.TryAdd(o.Name, o); opById.TryAdd(o.Id, o); }

        // Per-operation minimum lengths for classical-array parameters (see RequiredArrayLengths): computed
        // ONCE over the call graph, then a plain lookup at each call site.
        var arrayNeeds = RequiredArrayLengths(program.Operations, opById, opByName);
        
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

        // Operations are symbols too, at the PROGRAM layer: the top-level symbol table whose entries are the
        // ops. They enter through Scope.TryAdd — the SAME insertion door every parameter / register /
        // variable uses — so nothing registers a symbol by a side path. Built BEFORE the per-op walk so
        // forward calls resolve, and handed to each Build so a call records a UseSite on its callee.
        var programScope = SymbolTableBuilder.BuildProgramScope(program);
        model.SetProgramScope(programScope);

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

            // QSEM016 — an internally specialized Qubit[] parameter must have a positive concrete size.
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
            var root = SymbolTableBuilder.Build(op, errors, scopeOf, programScope);
            model.AddOperation(op, root);

            var ctx = new Ctx(op, entry.Name, ops, opByName, opById, inverter, errors, scopeOf, arrayNeeds);
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
        Dictionary<int, QOperation> OpById, Inverter Inverter, List<QoraError> Errors,
        IReadOnlyDictionary<IReadOnlyList<QStmt>, Scope> ScopeOf,
        Dictionary<int, IReadOnlyDictionary<string, int>> ArrayNeeds);

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
                    if (d.IsArray)
                    {
                        if (ctx.Op.Name != ctx.EntryName)
                            Add(ctx.Errors, "QSEM012", $"in `{opName}`: classical array `{d.Name}` must be declared at the top level of `{ctx.EntryName}` and passed into helper operations", d.Span);
                        else if (inControlFlow)
                            Add(ctx.Errors, "QSEM012", $"in `{opName}`: classical array `{d.Name}` cannot be declared inside a loop or branch; declare it once at the top level", d.Span);

                        if (d.Type is null)
                            Add(ctx.Errors, "QSEM029", $"in `{opName}`: array `{d.Name}` needs an explicit element type such as `int[]`", d.Span);
                        if (d.Value is not (QArrayLiteral or QArrayNew))
                            Add(ctx.Errors, "QSEM029", $"in `{opName}`: array `{d.Name}` must be initialized with an array literal or `new T[N]`", d.Span);
                        if (d.Value is QArrayLiteral { Elements.Count: 0 })
                            Add(ctx.Errors, "QSEM029", $"in `{opName}`: array `{d.Name}` cannot use an empty initializer", d.Span);
                        if (d.Value is QArrayNew allocation)
                        {
                            if (allocation.Length < 1)
                                Add(ctx.Errors, "QSEM016", $"in `{opName}`: `new {TypeName(allocation.ElementType)}[{allocation.Length}]` needs a positive literal length", d.Span);
                            if (d.Type is { } declared && declared != allocation.ElementType)
                                Add(ctx.Errors, "QSEM029", $"in `{opName}`: `{d.Name}` is `{TypeName(declared)}[]` but its initializer creates `{TypeName(allocation.ElementType)}[]`", d.Span);
                        }
                    }
                    else if (d.Value is QArrayLiteral or QArrayNew)
                        Add(ctx.Errors, "QSEM029", $"in `{opName}`: scalar `{d.Name}` cannot be initialized with an array value; declare it as `T[]`", d.Span);

                    if (d.Value is QText { HasCall: true })
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: the initializer of `{d.Name}` contains a call; only the lone form `bit r = M(q[i]);` is supported", d.Span);
                    CheckExprIndexes(d.Value, scope, opName, ctx.Errors, d.Span);
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
                    if (target is { IsArray: true } && a.Index is null)
                        Add(ctx.Errors, "QSEM029", $"in `{opName}`: whole-array assignment to `{a.Name}` is not supported; assign one element with `{a.Name}[i] = value`", a.Span);
                    else if (target is { IsArray: false } && a.Index is not null)
                        Add(ctx.Errors, "QSEM016", $"in `{opName}`: `{a.Name}` is a scalar and cannot be indexed", a.Span);
                    if (a.Index is not null)
                        CheckIndexedAccess(a.Name, a.Index, scope, opName, ctx.Errors, a.Span);
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
                    CheckExprIndexes(a.Value, scope, opName, ctx.Errors, a.Span);
                    break;

                case QIf i:
                    CheckCondition(i.Cond, opName, ctx.Errors, i.Span);
                    CheckTextIndexes(i.Cond.Text, scope, opName, ctx.Errors, i.Span);
                    Walk(i.Then, Child(i.Then), ctx, inControlFlow: true);
                    Walk(i.Else, Child(i.Else), ctx, inControlFlow: true);
                    break;
                case QFor f:
                    // A `for` bound is a plain expression; a call/measurement there has no OpenQASM lowering
                    // (and only a call renders a `(` into the bound text). Reject it like other call
                    // positions (QSEM005) instead of shipping invalid QASM.
                    if (f.From.Contains('(') || f.To.Contains('('))
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: a `for` bound cannot contain a call or measurement; measure into a bit first and use a numeric or variable bound", f.Span);
                    CheckTextIndexes(f.From, scope, opName, ctx.Errors, f.Span);
                    CheckTextIndexes(f.To, scope, opName, ctx.Errors, f.Span);
                    if (f.Step is not null) CheckTextIndexes(f.Step, scope, opName, ctx.Errors, f.Span);
                    Walk(f.Body, Child(f.Body), ctx, inControlFlow: true);
                    break;
                case QWhile w:
                    CheckCondition(w.Cond, opName, ctx.Errors, w.Span);
                    CheckTextIndexes(w.Cond.Text, scope, opName, ctx.Errors, w.Span);
                    Walk(w.Body, Child(w.Body), ctx, inControlFlow: true);
                    break;
                case QRepeat r:
                    Walk(r.Body, Child(r.Body), ctx, inControlFlow: true);
                    CheckCondition(r.Until, opName, ctx.Errors, r.Span);
                    CheckTextIndexes(r.Until.Text, Child(r.Body), opName, ctx.Errors, r.Span);
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
                CheckIndexedAccess(qa.Reg, qa.Index, scope, opName, errors, g.Span);
            else if (arg is QTextArg text)
                CheckTextIndexes(text.Text, scope, opName, errors, g.Span);

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
            // an ICallableSig, so the one CheckCall a built-in gate uses validates it too. Resolve the callee by
            // REFERENCE (CalleeOpId, bound at name resolution) with a name fallback for hand-built IR.
            var callee = g.CalleeOpId is int cid && ctx.OpById.TryGetValue(cid, out var byId) ? byId
                       : ctx.OpByName.GetValueOrDefault(g.Name);
            if (callee is not null)
                CheckCall(callee, g.Args, "", scope, opName, errors, g.Span,
                    ctx.ArrayNeeds.GetValueOrDefault(callee.Id));
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
    /// a qubit. A QUBIT slot rejects a classical, and — for a user op — checks whether the parameter expects
    /// one qubit or a whole <c>Qubit[]</c>. An internally specialized array also checks its concrete size.
    /// A built-in gate's qubit slots broadcast (a whole register applies
    /// element-wise), so they only require "is a qubit". The check reads the slot spec, never whether the
    /// callee is a gate or an op; only message wording consults <see cref="ICallableSig.IsBuiltin"/>.
    /// Identifiers the caller's scope does not resolve are left alone (treated as not-provably-wrong).
    /// </summary>
    private static void CheckCall(ICallableSig sig, IReadOnlyList<QArg> args, string functorPrefix,
        Scope scope, string opName, List<QoraError> errors, QSpan? span,
        IReadOnlyDictionary<string, int>? arrayNeeds = null)
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
            var argSym = WholeArgumentSymbol(arg, scope);

            if (p.Type != QType.Qubit)
            {
                if (p.IsArray)
                {
                    if (arg is not QTextArg || argSym is not { IsArray: true, Type: { } actualType }
                                             || actualType == QType.Qubit)
                        Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects `{TypeName(p.Type)}[]`, but the argument is not a classical array", span);
                    else if (actualType != p.Type)
                        Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects `{TypeName(p.Type)}[]`, but `{argSym.SourceName}` is `{TypeName(actualType)}[]`", span);
                    else if (argSym.IsConst)
                        Add(errors, "QSEM024", $"in `{opName}`: `{argSym.SourceName}` is a const array and cannot be passed to mutable parameter `{p.Name}` of `{calleeName}`", span);
                    // QSEM016 — the callee indexes this parameter with a LITERAL, so it has a minimum length.
                    // A `T[]` parameter carries no length of its own (it arrives with the argument), so this is
                    // the only place the precondition can be checked — without it a helper emits an
                    // out-of-bounds access that the identical inline access would have been rejected for.
                    else if (arrayNeeds?.TryGetValue(p.Name, out var need) == true
                             && argSym.ArrayLength is int have && have < need)
                        Add(errors, "QSEM016", $"in `{opName}`: `{argSym.SourceName}` has {have} element(s), but `{calleeName}` indexes `{p.Name}[{need - 1}]` — it needs at least {need}", span);
                    continue;
                }

                if (argSym is { IsArray: true })
                {
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects one `{TypeName(p.Type)}` value, but a whole array was passed", span);
                    continue;
                }

                if (arg is QQubitArg indexed && scope.Lookup(indexed.Reg) is { IsArray: true, Type: not QType.Qubit } indexedArray)
                {
                    if (!sig.IsBuiltin && indexedArray.Type is { } elementType && elementType != p.Type)
                        Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects `{TypeName(p.Type)}`, but `{indexed.Reg}` contains `{TypeName(elementType)}`", span);
                    continue;
                }
                // VALUE slot (rotation angle, or classical param): a qubit here is wrong. A classical array
                // element was accepted above; a whole qubit or indexed qubit is QSEM006, while a qubit buried
                // inside a classical expression such as `pi / q` is QSEM026.
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
            else if (p.IsQubitArray)
            {
                if (arg is QQubitArg qa)
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit array, but `{qa.Reg}[{qa.Index}]` is a single qubit", span);
                else if (arg is QTextArg tb && !IsQubitArray(argSym))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit register, but `{tb.Text}` is not one", span);
                else if (p.RegisterSize is int need && IsSizedRegister(argSym, out var have) && have != need)
                    Add(errors, "QSEM006", $"in `{opName}`: internal specialization `{calleeName}` expects {need} qubit(s) for `{p.Name}`, but the argument has {have}", span);
            }
            else
            {
                // single-qubit slot (user op)
                if (arg is QQubitArg indexed && !IsQubit(scope.Lookup(indexed.Reg)))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit, but `{indexed.Reg}[{indexed.Index}]` is a classical array element", span);
                else if (arg is QTextArg ta && (IsQubitArray(argSym) || IsClassicalText(ta.Text, scope)))
                    Add(errors, "QSEM006", IsQubitArray(argSym)
                        ? $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a single qubit, but `{ta.Text}` is a whole register (pass `{ta.Text}[i]`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit, but `{ta.Text}` is not one", span);
            }
        }

        var overlappingArray = sig.Params.Select((parameter, index) => (parameter, index))
            .Where(x => x.parameter.Type != QType.Qubit && x.parameter.IsArray)
            .Select(x => args[x.index] is QTextArg text ? text.Text.Trim() : string.Empty)
            .Where(name => name.Length > 0)
            .GroupBy(name => name)
            .FirstOrDefault(group => group.Count() > 1);
        if (overlappingArray is not null)
            Add(errors, "QSEM014", $"in `{opName}`: `{calleeName}` receives the mutable array `{overlappingArray.Key}` more than once; mutable array arguments may not overlap", span);
    }

    // --- qubit-shape queries over the unified symbol table ---
    // Every classification the walk needs is a query on the resolved Symbol: Type and IsQubitArray distinguish
    // classical values, single qubits, and qubit arrays; RegisterSize records an internal concrete specialization.
    // There is no second scope model — a name means whatever `scope.Lookup` resolves it to at this lexical point.

    private static bool IsQubit(Symbol? s) => s?.Type == QType.Qubit;

    private static bool IsSizedRegister(Symbol? s, out int size)
    {
        if (s is { Type: QType.Qubit, IsQubitArray: true, RegisterSize: int n } && n >= 1) { size = n; return true; }
        size = 0;
        return false;
    }

    private static bool IsQubitArray(Symbol? s) => s is { Type: QType.Qubit, IsQubitArray: true };
    private static bool IsSingleQubit(Symbol? s) => s is { Type: QType.Qubit, IsQubitArray: false };

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
        SymbolTableBuilder.ExpressionIdentifiers(text).FirstOrDefault(n => IsQubit(scope.Lookup(n)));

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

    private static Symbol? WholeArgumentSymbol(QArg arg, Scope scope)
    {
        if (arg is not QTextArg text) return null;
        var name = text.Text.Trim();
        return IsIdentifier(name) ? scope.Lookup(name) : null;
    }

    /// <summary>
    /// QSEM016 support — the minimum length every classical-array PARAMETER requires, derived from the LITERAL
    /// indices used on it. A parameter has no length of its own (a source <c>T[]</c> is size-independent: the
    /// length arrives with the ARGUMENT, and differs per call), so a literal <c>x[5]</c> in the body is a
    /// PRECONDITION — "whatever array is passed here needs at least 6 elements". That precondition can only be
    /// checked at the CALL, which is what <see cref="CheckCall"/> does with this table; without it a helper
    /// emits an out-of-bounds access verbatim while the identical access written inline is rejected.
    /// Requirements fold TRANSITIVELY: handing a parameter straight to another operation's array parameter
    /// inherits that callee's requirement, so the check fires at the outermost call where a concrete array
    /// (with a known length) actually enters. Recursion is rejected (QSEM011), so the memoized walk terminates.
    /// </summary>
    private static Dictionary<int, IReadOnlyDictionary<string, int>> RequiredArrayLengths(
        IReadOnlyList<QOperation> ops, Dictionary<int, QOperation> opById, Dictionary<string, QOperation> opByName)
    {
        var memo = new Dictionary<int, IReadOnlyDictionary<string, int>>();
        var inProgress = new HashSet<int>();

        IReadOnlyDictionary<string, int> For(QOperation op)
        {
            if (memo.TryGetValue(op.Id, out var done)) return done;
            if (!inProgress.Add(op.Id)) return new Dictionary<string, int>();   // call cycle — QSEM011 reports it

            var need = new Dictionary<string, int>();
            var arrayParams = op.Params
                .Where(p => p.Type != QType.Qubit && p.IsArray)
                .Select(p => p.Name).ToHashSet();
            if (arrayParams.Count > 0) Walk(op.Body);

            inProgress.Remove(op.Id);
            memo[op.Id] = need;
            return need;

            // a literal index on one of OUR array params raises that param's floor
            void Require(string name, string index)
            {
                if (!arrayParams.Contains(name) || index.Length == 0 || !index.All(char.IsDigit)
                    || !int.TryParse(index, out var i)) return;
                need[name] = System.Math.Max(need.GetValueOrDefault(name), i + 1);
            }

            void Text(string text)
            {
                foreach (var access in SymbolTableBuilder.IndexedAccesses(text)) Require(access.Base, access.Index);
            }

            void Expr(QExpr? e)
            {
                switch (e)
                {
                    case QText t: Text(t.Text); break;
                    case QArrayLiteral lit: foreach (var el in lit.Elements) Expr(el); break;
                }
            }

            void Walk(IReadOnlyList<QStmt> body)
            {
                foreach (var stmt in body)
                    switch (stmt)
                    {
                        case QGate g:
                            foreach (var arg in g.Args)
                            {
                                if (arg is QQubitArg qa) Require(qa.Reg, qa.Index);
                                else if (arg is QTextArg ta) Text(ta.Text);
                            }
                            // handing one of OUR array params to a callee's array param inherits its floor
                            var callee = g.CalleeOpId is int cid && opById.TryGetValue(cid, out var byId) ? byId
                                       : opByName.GetValueOrDefault(g.Name);
                            if (callee is not null)
                            {
                                var calleeNeed = For(callee);
                                for (var i = 0; i < callee.Params.Count && i < g.Args.Count; i++)
                                    if (callee.Params[i] is { Type: not QType.Qubit, IsArray: true } cp
                                        && g.Args[i] is QTextArg whole
                                        && arrayParams.Contains(whole.Text.Trim())
                                        && calleeNeed.TryGetValue(cp.Name, out var floor))
                                        need[whole.Text.Trim()] =
                                            System.Math.Max(need.GetValueOrDefault(whole.Text.Trim()), floor);
                            }
                            break;
                        case QAssign a:
                            if (a.Index is not null) Require(a.Name, a.Index);
                            Expr(a.Value);
                            break;
                        case QDecl d: Expr(d.Value); break;
                        case QIf i: Text(i.Cond.Text); Walk(i.Then); Walk(i.Else); break;
                        case QFor f: Text(f.From); Text(f.To); Walk(f.Body); break;
                        case QWhile w: Text(w.Cond.Text); Walk(w.Body); break;
                        case QRepeat r: Text(r.Until.Text); Walk(r.Body); break;
                        case QConjugate c: Walk(c.Within); Walk(c.Apply); break;
                    }
            }
        }

        foreach (var op in ops) For(op);
        return memo;
    }

    private static void CheckExprIndexes(QExpr expr, Scope scope, string opName, List<QoraError> errors, QSpan? span)
    {
        switch (expr)
        {
            case QText text:
                CheckTextIndexes(text.Text, scope, opName, errors, span);
                break;
            case QArrayLiteral literal:
                foreach (var element in literal.Elements)
                    CheckExprIndexes(element, scope, opName, errors, span);
                break;
        }
    }

    private static void CheckTextIndexes(string text, Scope scope, string opName, List<QoraError> errors, QSpan? span)
    {
        foreach (var access in SymbolTableBuilder.IndexedAccesses(text))
        {
            CheckIndexedAccess(access.Base, access.Index, scope, opName, errors, span);
            if (scope.Lookup(access.Base) is { Type: QType.Qubit })
                Add(errors, "QSEM026", $"in `{opName}`: `{access.Base}[{access.Index}]` is a qubit and cannot be used as a classical value", span);
        }
    }

    private static void CheckIndexedAccess(string name, string index, Scope scope, string opName,
        List<QoraError> errors, QSpan? span)
    {
        var symbol = scope.Lookup(name);
        if (symbol is null) return;
        if (!symbol.IsArray)
        {
            Add(errors, "QSEM016", $"in `{opName}`: `{name}` is a scalar and cannot be indexed (`{name}[{index}]`)", span);
            return;
        }

        if (scope.Lookup(index) is { Type: QType.Qubit } or { IsArray: true })
            Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` needs one classical integer index", span);

        var length = symbol.Type == QType.Qubit ? symbol.RegisterSize : symbol.ArrayLength;
        if (length is int size && index.Length > 0 && index.All(char.IsDigit)
            && (!int.TryParse(index, out var value) || value >= size))
            Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is out of range; `{name}` has {size} element(s) (valid: 0..{size - 1})", span);
    }

    /// <summary>QSEM016 — literal index bounds against known register sizes; no indexing single qubits.</summary>
    private static void CheckQubitIndex(QQubitArg q, Scope scope, string opName, List<QoraError> errors, QSpan? span)
    {
        if (scope.Lookup(q.Reg) is { } resolved)
        {
            CheckIndexedAccess(q.Reg, q.Index, scope, opName, errors, span);
            if (resolved.Type != QType.Qubit)
                Add(errors, "QSEM006", $"in `{opName}`: measurement target `{q.Reg}[{q.Index}]` is not a qubit", span);
            return;
        }

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
    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string TypeName(QType type) => type.ToString().ToLowerInvariant();

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
