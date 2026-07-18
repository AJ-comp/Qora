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
///   <item><b>QSEM030</b> — an array/register index that cannot be PROVEN in bounds at compile time (rung
///         B'): a runtime index with no literal value, no <c>0..a.Count-1</c> or constant-bounded loop, and
///         no enclosing guard <c>if (0 &lt;= n &amp;&amp; n &lt; a.Count)</c>. OpenQASM 3 has no runtime bounds
///         check, so an unprovable index cannot be deferred to run time — it must be proven or guarded.
///         (An index proven OUT of range is QSEM016; this is the "no proof exists" case.)</item>
/// </list>
/// Earlier pipeline steps own the remaining codes, and each step's errors preempt this validator:
/// QSEM020 (import file not found/unreadable) comes from <see cref="ModuleLoader"/>;
/// QSEM018 (ambiguous unqualified reference) and QSEM019 (unknown
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
        var unproven = new List<UnprovenIndex>();   // rung B′ data: accesses whose bounds proof never settled
        if (program is null) return errors;

        if (program.Operations.Count == 0) return errors;
        model = new SemanticModel();

        var ops = program.Operations.Select(o => o.Name).ToHashSet();
        var opByName = new Dictionary<string, QOperation>();
        var opById = new Dictionary<int, QOperation>();          // call-site → callee resolution by reference (CalleeOpId)

        foreach (var o in program.Operations) { opByName.TryAdd(o.Name, o); opById.TryAdd(o.Id, o); }

        // Rung B'/P4 working data. The walk RECORDS two kinds of facts instead of computing floors in a
        // separate pass: each classical-array parameter's minimum required length (produced by
        // CheckIndexedAccess from the SAME folded bound its verdict used — one calculator, so the floor can
        // never disagree with the prover) and every call's array arguments (produced by CheckCall). The
        // floor check is DERIVED from both after the walk — same discipline as UnprovenIndexes.
        var needsByOp = new Dictionary<int, Dictionary<string, long>>();
        var calls = new List<ArrayCallFact>();

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

            var opNeeds = new Dictionary<string, long>();
            needsByOp[op.Id] = opNeeds;
            var ctx = new Ctx(op, entry.Name, ops, opByName, opById, inverter, errors, unproven,
                opNeeds, new ArrayFloorSink(op.Id, calls), scopeOf);
            Walk(op.Body, root, ctx, inControlFlow: false);
        }

        // Rung B'/P4 disposition, derived from the recorded facts. First PROPAGATE: a caller that hands its
        // own parameter to a callee inherits the callee's requirement, iterated to a fixpoint so chains of
        // any depth converge (needs only ever grow, bounded by the finitely many direct floors; a call cycle
        // is already QSEM011). Then CHECK: every argument with a known length must meet its parameter's
        // final requirement. The check fires at the outermost call where a concrete array actually enters.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var c in calls)
            {
                if (c.CallerParam is null
                    || !needsByOp.TryGetValue(c.CalleeOpId, out var calleeNeeds)
                    || !calleeNeeds.TryGetValue(c.CalleeParam, out var need)) continue;
                var mine = needsByOp[c.CallerOpId];
                if (need > mine.GetValueOrDefault(c.CallerParam))
                {
                    mine[c.CallerParam] = need;
                    changed = true;
                }
            }
        }
        foreach (var c in calls)
            if (c.ArgLength is int have
                && needsByOp.TryGetValue(c.CalleeOpId, out var calleeNeeds)
                && calleeNeeds.TryGetValue(c.CalleeParam, out var need) && have < need)
                Add(errors, "QSEM016", $"in `{c.CallerOpName}`: `{c.ArgName}` has {have} element(s), but `{c.CalleeName}` indexes `{c.CalleeParam}[{need - 1}]` — it needs at least {need}", c.Span);

        // The settled per-op requirement table is a validation FACT — each operation's array-argument
        // contract — recorded on the model for any later consumer (IDE signature help, docs, backends).
        foreach (var (opId, needs) in needsByOp)
            if (needs.Count > 0)
                model.SetRequiredArgLengths(opId, needs);

        // Rung B′ disposition. The walk above RECORDS unproven accesses as data (the verdict is
        // target-independent); what happens to them belongs to the backend. The only backend today is
        // OpenQASM 3, which has no runtime failure channel — so every entry becomes a QSEM030 here. A QIR
        // backend will instead read <see cref="SemanticModel.UnprovenIndexes"/> as its list of sites to
        // wrap in a runtime bounds check + abort, and this loop is the single place that then goes per-target.
        // Distinct: a while-condition measurement lowers to TWO validated statements (a pre-loop measure and
        // an end-of-body re-measure sharing one span), so one source access can record the SAME unproven
        // entry / emit the SAME QSEM016 twice. Both records are value-equal (Op/Array/Index/Span match), so
        // one survives — one source mistake, one diagnostic, one model entry, whatever lowering duplicated.
        foreach (var u in unproven.Distinct())
        {
            model.AddUnprovenIndex(u);
            Add(errors, "QSEM030", u.LoopBound is { } bound
                ? $"in `{u.Op}`: `{u.Array}[{u.Index}]` — the loop bound `{bound}` cannot be determined at compile time, so the index cannot be proven in bounds. Guard the access — `if (0 <= {u.Index} && {u.Index} < {u.Array}.Count) {{ … }}` — or bound the loop by `{u.Array}.Count-1` or a constant"
                : $"in `{u.Op}`: `{u.Array}[{u.Index}]` uses the runtime index `{u.Index}`, which cannot be proven in bounds at compile time. Guard it — `if (0 <= {u.Index} && {u.Index} < {u.Array}.Count) {{ … }}` — or index with a loop `for {u.Index} in 0..{u.Array}.Count-1`", u.Span);
        }

        // Two byte-identical diagnostics (same message, code, span) are noise, never information — one
        // source mistake shows once. Value-equal QoraError records collapse.
        return errors.Distinct().ToList();
    }

    /// <summary>Everything the per-operation walk needs beyond the current lexical <see cref="Scope"/> (the
    /// unified symbol table): the operation, the entry name (for `use` placement), the callable sets, and
    /// <c>ScopeOf</c> — the body-list → scope map the symbol table produced, so recursing into a branch/loop
    /// resolves names in the right nested scope.</summary>
    private sealed record Ctx(
        QOperation Op, string EntryName, HashSet<string> Ops, Dictionary<string, QOperation> OpByName,
        Dictionary<int, QOperation> OpById, Inverter Inverter, List<QoraError> Errors,
        List<UnprovenIndex> Unproven,
        Dictionary<string, long> ParamNeeds,
        ArrayFloorSink Floors,
        IReadOnlyDictionary<IReadOnlyList<QStmt>, Scope> ScopeOf);

    /// <summary>One call's classical-array argument, recorded as DATA during the walk (rung B'/P4).
    /// <see cref="ArgLength"/> is the argument's known length (a local/literal array) — a CHECK fact,
    /// compared against the callee's required minimum after the walk. <see cref="CallerParam"/> is set
    /// instead when the argument is the CALLER's own parameter (the only unknown-length classical array) —
    /// a PROPAGATION fact: the callee's requirement becomes the caller's, so the check fires at the
    /// outermost call where a concrete array actually enters.</summary>
    private sealed record ArrayCallFact(
        int CallerOpId, string CallerOpName, int CalleeOpId, string CalleeName,
        string CalleeParam, string ArgName, int? ArgLength, string? CallerParam, QSpan? Span);

    /// <summary>Where <see cref="CheckCall"/> deposits <see cref="ArrayCallFact"/>s: the calling op's Id plus
    /// the per-run shared list. Null when the callee is not a user operation (built-in gates take no
    /// classical arrays).</summary>
    private sealed record ArrayFloorSink(int CallerOpId, List<ArrayCallFact> Calls);

    /// <summary>
    /// The bounds facts at one point of the walk, used to PROVE a non-literal index in range (rung B').
    /// Facts are keyed by <see cref="Symbol"/> IDENTITY, never by name: the scope chain resolves a name to
    /// the nearest declaration's Symbol at each use site, so a shadowing binder (a `for` header, an inner
    /// declaration) is a DIFFERENT key and can neither inherit an outer variable's proof nor destroy it.
    /// What identity cannot express is TIME — the same symbol reassigned mid-block or across a loop
    /// back-edge — and that is exactly what <see cref="Invalidate"/> handles. Space is the symbol table's
    /// job; this struct carries only the walk-order dimension on top of it. Defaults to no facts.
    /// </summary>
    /// <summary>What a parsed guard proves: index &lt; a specific array's Count (<c>ByArray</c>), and/or
    /// index &lt; a constant K (<c>ByConst</c>). The constant form is what a guard <c>n &lt; q.Count</c> becomes
    /// on the post-monomorphization pass, where <c>q.Count</c> has been substituted to its concrete size.
    /// Names are resolved to Symbols AT THE GUARD's site — the facts are about those variables, whatever
    /// anyone inside calls them.</summary>
    private readonly record struct GuardFacts(
        IReadOnlySet<(Symbol Index, Symbol Array)> ByArray,
        IReadOnlyDictionary<Symbol, int> ByConst);

    /// <summary>One loop variable's range, FOLDED AT THE HEADER: the bound expressions are evaluated in the
    /// scope where the loop statement lives — the scope the emitted QASM evaluates them in — never at the
    /// access site, where a shadowing inner <c>const</c> could resolve the same name to a different value
    /// and split the verdict from the emitted code. Texts are kept for diagnostics only.</summary>
    private readonly record struct LoopFact(string From, string To, Bound? FromB, Bound? ToB, bool DefersToMono);

    private readonly record struct BoundsCtx(
        IReadOnlyDictionary<Symbol, LoopFact>? LoopVars,
        IReadOnlySet<(Symbol Index, Symbol Array)>? Guards,
        IReadOnlyDictionary<Symbol, int>? GuardConsts)
    {
        public bool LoopRange(Symbol? index, out LoopFact fact)
        {
            if (index is not null && LoopVars is { } m && m.TryGetValue(index, out fact)) return true;
            fact = default;
            return false;
        }

        /// <summary>Is <paramref name="index"/> proven in bounds for <paramref name="array"/> (of length
        /// <paramref name="length"/>, if known) by an enclosing guard? Either the guard named this exact
        /// array, or it bounded the index by a constant K that fits a known length (K ≤ length).</summary>
        public bool Guarded(Symbol? index, Symbol array, int? length)
        {
            if (index is null) return false;
            if (Guards?.Contains((index, array)) == true) return true;
            return GuardConsts?.TryGetValue(index, out var k) == true && length is int len && k <= len;
        }

        /// <summary>Is <paramref name="index"/> bounded by a constant K (<c>index &lt; K</c>) regardless of any
        /// array length? Used to DEFER a guarded access on an unsized Qubit[] parameter to the post-mono pass,
        /// where the concrete size lets <see cref="Guarded"/> confirm K ≤ length.</summary>
        public bool HasConstGuard(Symbol? index) => index is not null && GuardConsts?.ContainsKey(index) == true;

        /// <summary>Record the loop variable's range. No wipe is needed for shadowing: the loop variable is
        /// its OWN Symbol, so an outer same-named variable's facts sit under a different key and simply never
        /// match — identity does what name-keyed storage needed explicit invalidation for.</summary>
        public BoundsCtx WithLoop(Symbol loopVar, LoopFact fact)
        {
            var m = LoopVars is null ? new Dictionary<Symbol, LoopFact>()
                                     : new Dictionary<Symbol, LoopFact>(LoopVars);
            m[loopVar] = fact;
            return this with { LoopVars = m };
        }

        public BoundsCtx WithGuards(GuardFacts g)
        {
            var s = Guards is null ? new HashSet<(Symbol, Symbol)>()
                                   : new HashSet<(Symbol, Symbol)>(Guards);
            foreach (var p in g.ByArray) s.Add(p);
            var c = GuardConsts is null ? new Dictionary<Symbol, int>()
                                        : new Dictionary<Symbol, int>(GuardConsts);
            // Proven facts ACCUMULATE — they never weaken each other. Inside `if (n < 2)`, an inner
            // `if (n < 9)` proves nothing new; the MIN (tightest bound) always stands. Overwriting made
            // the verdict depend on nesting order.
            foreach (var kv in g.ByConst)
                c[kv.Key] = c.TryGetValue(kv.Key, out var held) ? System.Math.Min(held, kv.Value) : kv.Value;
            return this with { Guards = s, GuardConsts = c };
        }

        /// <summary>The TIME axis: drop every fact about this symbol once it is REASSIGNED — the identity is
        /// unchanged but the checked value is gone, so <c>if (n &lt; a.Count) { n = n + 9; a[n] }</c> is not
        /// falsely proven. Identity-keying already handles the space axis (shadowing) without this.</summary>
        public BoundsCtx Invalidate(Symbol name)
        {
            if (!Mentions(name)) return this;
            var lv = LoopVars?.Where(kv => kv.Key != name).ToDictionary(kv => kv.Key, kv => kv.Value);
            var g = Guards?.Where(p => p.Index != name).ToHashSet();
            var c = GuardConsts?.Where(kv => kv.Key != name).ToDictionary(kv => kv.Key, kv => kv.Value);
            return new BoundsCtx(lv, g, c);
        }

        private bool Mentions(Symbol name) =>
            LoopVars?.ContainsKey(name) == true || Guards?.Any(p => p.Index == name) == true
            || GuardConsts?.ContainsKey(name) == true;
    }

    private static void Walk(IReadOnlyList<QStmt> stmts, Scope scope, Ctx ctx, bool inControlFlow,
        BoundsCtx bounds = default)
    {
        var opName = ctx.Op.DisplayName ?? ctx.Op.Name;
        Scope Child(IReadOnlyList<QStmt> body) => ctx.ScopeOf.TryGetValue(body, out var s) ? s : scope;

        // `flow` is the running bounds context: it starts from the enclosing level and SHRINKS as
        // statements reassign a guarded/loop name, so `if (n < a.Count) { n = n + 9; a[n] }` is not proven.
        var flow = bounds;
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QGate g:
                    CheckGate(g, scope, ctx, flow);
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
                    // CheckExprIndexes bounds-checks every index in the value, INCLUDING a measurement target
                    // (direct or nested in an array literal) — the single measure-index check, so no dedicated
                    // CheckQubitIndex is repeated here (that duplicated the diagnostic).
                    CheckExprIndexes(d.Value, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, d.Span, flow);
                    // QSEM017 — a measurement result is a `bit`, so measuring into a non-bit target is a type
                    // error, whether the measurement is the whole value (`int x = M(q)`) or an element of an
                    // array literal (`int[] a = [M(q)]`, which would otherwise emit `measure` inside a `{…}`
                    // initializer — invalid OpenQASM).
                    if (d.Type is not null && d.Type != QType.Bit
                        && (d.Value is QMeasure || d.Value is QArrayLiteral { Elements: { } els } && els.Any(e => e is QMeasure)))
                        Add(ctx.Errors, "QSEM017", $"in `{opName}`: `{d.Name}` is declared `{d.Type.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`", d.Span);
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
                        CheckIndexedAccess(a.Name, a.Index, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, a.Span, flow);
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
                    // CheckExprIndexes bounds-checks the measure target too (via its QMeasure case) — the
                    // single measure-index check, so no dedicated CheckQubitIndex is repeated (it duplicated).
                    CheckExprIndexes(a.Value, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, a.Span, flow);
                    break;

                case QIf i:
                    CheckCondition(i.Cond, opName, ctx.Errors, i.Span);
                    CheckTextIndexes(i.Cond.Tree, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, i.Span, flow);
                    // P5 — the then-branch runs only when the guard held, so a guarded index is proven there.
                    // The guard's names resolve HERE, at the if's own scope: the facts are about these
                    // variables, and a shadowing binder inside gets a different Symbol — no leak either way.
                    Walk(i.Then, Child(i.Then), ctx, inControlFlow: true, flow.WithGuards(ParseGuards(i.Cond.Tree, scope)));
                    Walk(i.Else, Child(i.Else), ctx, inControlFlow: true, flow);
                    break;
                case QFor f:
                    // A `for` bound is a plain expression; a call/measurement there has no OpenQASM lowering
                    // (and only a call renders a `(` into the bound text). Reject it like other call
                    // positions (QSEM005) instead of shipping invalid QASM.
                    if (f.From.Contains('(') || f.To.Contains('('))
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: a `for` bound cannot contain a call or measurement; measure into a bit first and use a numeric or variable bound", f.Span);
                    CheckTextIndexes(f.FromTree, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, f.Span, flow);
                    CheckTextIndexes(f.ToTree, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, f.Span, flow);
                    // Step has no surface syntax (synthesized "-1" by the inverter, post-validation) and carries no index.
                    // P2/P3 — the loop variable ranges over From..To inside the body. The bounds are FOLDED
                    // HERE, in the header's scope — the scope the emitted QASM evaluates them in — so a
                    // shadowing `const` inside the body cannot resolve a bound name to a different value than
                    // the loop actually runs with. Back-edge rule: a loop body re-executes, so "the access is
                    // written before the reassignment" is no guarantee — iteration 2's access runs AFTER
                    // iteration 1's reassignment. Any symbol the body reassigns ANYWHERE loses its facts for
                    // the WHOLE body. The loop's own variable is exempt by construction: the header
                    // re-assigns it fresh at every entry (WithLoop after the wipe). The variable's fact is
                    // keyed by ITS Symbol (declared in the body scope), so a same-named outer variable's
                    // guard can never prove it, nor the reverse.
                    var forBody = Child(f.Body);
                    var forFlow = WithoutBodyAssigned(flow, f.Body, forBody, ctx.ScopeOf);
                    // The loop variable lives in ITS OWN scope — the body's PARENT (the body may shadow it).
                    // Resolve it there, never through the body: a body-local shadowing declaration must not
                    // become the key holding the loop's range.
                    if (forBody.Parent?.LookupLocal(f.Var) is { Kind: SymbolKind.LoopVar } loopSym)
                    {
                        // Fold the bound TREES (built once at lowering) in the header's scope. A bound over an
                        // unsized Qubit[] .Count — directly or through a const — folds to a BoundCount and
                        // defers to the post-mono pass, where the size is concrete.
                        var fromB = BoundFolder.Fold(f.FromTree, scope);
                        var toB = BoundFolder.Fold(f.ToTree, scope);
                        forFlow = forFlow.WithLoop(loopSym, new LoopFact(f.From, f.To, fromB, toB,
                            BoundFolder.DefersToUnsizedQubit(fromB, scope) || BoundFolder.DefersToUnsizedQubit(toB, scope)));
                    }
                    Walk(f.Body, forBody, ctx, inControlFlow: true, forFlow);
                    break;
                case QWhile w:
                    // The condition re-evaluates after EVERY pass through the body, so it too must hold with
                    // the body's reassignments invalidated — not just on the pre-loop first evaluation.
                    CheckCondition(w.Cond, opName, ctx.Errors, w.Span);
                    // Back-edge: pre-loop facts about a name the body reassigns cannot survive into the body.
                    var whileWiped = WithoutBodyAssigned(flow, w.Body, Child(w.Body), ctx.ScopeOf);
                    // The condition's own indexes are checked under those wiped facts (not its own guard).
                    CheckTextIndexes(w.Cond.Tree, scope, opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, w.Span, whileWiped);
                    // A `while` condition holds at the TOP of every iteration and is RE-checked each time, so
                    // its guard narrows the body exactly as an `if` narrows its then-branch — applied AFTER the
                    // back-edge wipe (the condition re-establishes it every iteration, so a reassignment does
                    // not erase it across iterations; within one iteration, per-statement invalidation still
                    // drops it once the name is reassigned, so an access after the reassignment is unguarded).
                    Walk(w.Body, Child(w.Body), ctx, inControlFlow: true, whileWiped.WithGuards(ParseGuards(w.Cond.Tree, scope)));
                    break;
                case QRepeat r:
                    // Same back-edge rule; the until-condition ALWAYS runs after the body, so even its first
                    // evaluation sees the body's reassignments — `repeat { n = n + 9; } until (a[n] == 1)`
                    // reads the mutated n on the very first pass.
                    var repeatFlow = WithoutBodyAssigned(flow, r.Body, Child(r.Body), ctx.ScopeOf);
                    Walk(r.Body, Child(r.Body), ctx, inControlFlow: true, repeatFlow);
                    CheckCondition(r.Until, opName, ctx.Errors, r.Span);
                    CheckTextIndexes(r.Until.Tree, Child(r.Body), opName, ctx.Errors, ctx.Unproven, ctx.ParamNeeds, r.Span, repeatFlow);
                    break;
                case QConjugate c:
                    Walk(c.Within, Child(c.Within), ctx, inControlFlow, flow);
                    Walk(c.Apply, Child(c.Apply), ctx, inControlFlow, flow);
                    break;
            }

            // A statement that reassigns a variable — DIRECTLY or in any nested block — drops every bounds
            // fact about that SYMBOL, so a guard/loop that proved the OLD value cannot prove a LATER access.
            // Handling this uniformly (not just for a top-level `n = …`) closes the nested-reassignment
            // hole, e.g. `if (n < a.Count) { for … { n = n + 9; } a[n] }`.
            foreach (var reassigned in AssignedSymbols(stmt, scope, ctx.ScopeOf)) flow = flow.Invalidate(reassigned);
        }
    }

    /// <summary>The back-edge rule: entering a loop body, drop every bounds fact about a SYMBOL the body
    /// reassigns ANYWHERE. Within one iteration, text order is execution order (the per-statement
    /// invalidation in <see cref="Walk"/> handles it) — but across iterations it is not: iteration 2's
    /// access runs after iteration 1's reassignment, so a fact from OUTSIDE the loop cannot survive into a
    /// body that mutates its variable. A guard written INSIDE the body is unaffected: it re-proves on every
    /// iteration, exactly like the re-executed runtime check it models.</summary>
    private static BoundsCtx WithoutBodyAssigned(BoundsCtx flow, IReadOnlyList<QStmt> body, Scope bodyScope,
        IReadOnlyDictionary<IReadOnlyList<QStmt>, Scope> scopeOf)
    {
        foreach (var stmt in body)
            foreach (var sym in AssignedSymbols(stmt, bodyScope, scopeOf))
                flow = flow.Invalidate(sym);
        return flow;
    }

    /// <summary>The SYMBOLS a statement reassigns, transitively through nested loops/branches — each
    /// assignment's name resolved in ITS OWN scope, so mutating a shadowed inner variable invalidates that
    /// inner symbol and leaves the outer one's facts standing (and vice versa). Only <c>QAssign</c> mutates
    /// an existing binding; a nested <c>QDecl</c> introduces a NEW symbol, which identity-keyed facts
    /// already keep separate — nothing to collect.</summary>
    private static HashSet<Symbol> AssignedSymbols(QStmt stmt, Scope scope,
        IReadOnlyDictionary<IReadOnlyList<QStmt>, Scope> scopeOf)
    {
        var symbols = new HashSet<Symbol>();
        Collect(stmt, scope);
        return symbols;

        void Collect(QStmt s, Scope sc)
        {
            Scope Of(IReadOnlyList<QStmt> body) => scopeOf.TryGetValue(body, out var inner) ? inner : sc;
            switch (s)
            {
                case QAssign a: if (sc.Lookup(a.Name) is { } sym) symbols.Add(sym); break;
                case QIf i: foreach (var t in i.Then) Collect(t, Of(i.Then)); foreach (var e in i.Else) Collect(e, Of(i.Else)); break;
                case QFor f: foreach (var b in f.Body) Collect(b, Of(f.Body)); break;
                case QWhile w: foreach (var b in w.Body) Collect(b, Of(w.Body)); break;
                case QRepeat r: foreach (var b in r.Body) Collect(b, Of(r.Body)); break;
                case QConjugate c: foreach (var b in c.Within) Collect(b, Of(c.Within)); foreach (var b in c.Apply) Collect(b, Of(c.Apply)); break;
            }
        }
    }

    /// <summary>
    /// P5 — read a guard condition TREE (see <see cref="QNode"/>) for what it proves. Recognizes
    /// <c>0 &lt;= n</c> / <c>n &gt;= 0</c> (a lower bound) conjoined with <c>n &lt; a.Count</c> (bounded by an
    /// array's Count) or <c>n &lt; K</c> (bounded by a constant/const-folded value). Both a lower AND an upper
    /// conjunct are needed for the same index. Names resolve to Symbols HERE, at the guard's site, so the
    /// facts are about these exact variables — shadowing settled by identity. Order-insensitive; bails on any
    /// <c>||</c>/<c>!</c>. A fixed closed set of shapes — no general evaluator — matching the Wuffs/eBPF
    /// philosophy: prove the common guard, not an arbitrary predicate.
    /// </summary>
    private static GuardFacts ParseGuards(QNode? tree, Scope scope)
    {
        var byArray = new HashSet<(Symbol, Symbol)>();
        var byConst = new Dictionary<Symbol, int>();
        if (tree is null || ContainsOrOrNot(tree)) return new GuardFacts(byArray, byConst);

        var lowered = new HashSet<Symbol>();                    // indices with a proven `>= 0`
        var upperArr = new List<(Symbol Idx, Symbol Arr)>();    // (index, array) with a proven `< a.Count`
        var upperConst = new List<(Symbol Idx, int K)>();       // (index, K)    with a proven `< K`
        foreach (var atom in Conjuncts(tree))
        {
            if (atom is not QBinOp cmp) continue;
            // lower bound: `0 <= idx` or `idx >= 0`
            if (cmp is { Op: "<=", Left: QNumLit { Value: 0 }, Right: QNameRef lo } && scope.Lookup(lo.Name) is { } ls)
                lowered.Add(ls);
            else if (cmp is { Op: ">=", Left: QNameRef lo2, Right: QNumLit { Value: 0 } } && scope.Lookup(lo2.Name) is { } ls2)
                lowered.Add(ls2);
            // upper bound: `idx < a.Count` or `idx < K`. Fold the RHS through the ONE calculator, so a direct
            // `a.Count`, a const `k = a.Count`, and `a.Count - 1` are read identically — the const indirection
            // is transparent, exactly as it is for a loop bound.
            else if (cmp is { Op: "<", Left: QNameRef up } && scope.Lookup(up.Name) is { } us)
            {
                var rhs = BoundFolder.Fold(cmp.Right, scope);
                // `idx < k*arr.Count + c` with k=1, c<=0 implies idx < arr.Count for ANY length — a ByArray
                // proof (length-independent), covering the direct `.Count` and a const aliasing it.
                if (rhs is BoundCount { Coeff: 1, Offset: <= 0 } bc && scope.Lookup(bc.Array) is { } arrSym)
                    upperArr.Add((us, arrSym));
                // a definite constant upper bound (a value past int32 is simply no usable fact, no crash).
                else if (rhs is BoundNum { Value: >= 0 and <= int.MaxValue } k)
                    upperConst.Add((us, (int)k.Value));
            }
        }
        foreach (var (idx, arr) in upperArr)
            if (lowered.Contains(idx)) byArray.Add((idx, arr));       // both bounds present for this index
        foreach (var (idx, k) in upperConst)
            if (lowered.Contains(idx)) byConst[idx] = byConst.TryGetValue(idx, out var held) ? System.Math.Min(held, k) : k;   // tightest conjunct wins
        return new GuardFacts(byArray, byConst);
    }

    /// <summary>A guard with any <c>||</c> or <c>!</c> proves nothing usable (the then-branch may run without
    /// the narrowing conjunct holding) — detected on the tree, so no textual false positive (a name
    /// containing "or" never trips it).</summary>
    private static bool ContainsOrOrNot(QNode n) => n switch
    {
        QUnary { Op: "!" } => true,
        QBinOp { Op: "||" } => true,
        QBinOp b => ContainsOrOrNot(b.Left) || ContainsOrOrNot(b.Right),
        QUnary u => ContainsOrOrNot(u.Operand),
        _ => false,
    };

    /// <summary>Flatten a top-level <c>&amp;&amp;</c> conjunction into its atoms (a non-<c>&amp;&amp;</c> node is
    /// one atom).</summary>
    private static IEnumerable<QNode> Conjuncts(QNode n)
    {
        if (n is QBinOp { Op: "&&" } a)
        {
            foreach (var c in Conjuncts(a.Left)) yield return c;
            foreach (var c in Conjuncts(a.Right)) yield return c;
        }
        else yield return n;
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
        // Invariant: a call-free, non-empty condition MUST carry its parsed tree — the bounds check and guard
        // reader consume the tree, so a null tree would SILENTLY skip the whole condition (an out-of-bounds
        // index escaping, a guard's facts lost). Any rewrite pass that reconstructs a condition must preserve
        // or rebuild the tree; a violation is a compiler bug, caught loud here rather than as a silent hole.
        else if (cond.Tree is null && !string.IsNullOrWhiteSpace(cond.Text))
            Add(errors, "QINTERNAL", $"in `{opName}`: the condition `{cond.Text}` reached validation without a parsed tree (a rewrite pass dropped it) — please report this", span);
    }

    private static void CheckGate(QGate g, Scope scope, Ctx ctx, BoundsCtx bounds = default)
    {
        var opName = ctx.Op.DisplayName ?? ctx.Op.Name;
        var errors = ctx.Errors;

        // QSEM005 — calls inside gate arguments have no OpenQASM form.
        foreach (var arg in g.Args)
            if (arg is QTextArg { HasCall: true })
                Add(errors, "QSEM005", $"in `{opName}`: an argument of `{g.Name}` contains a call; measure into a bit first and pass the bit", g.Span);

        // QSEM016/030 — an index must be provably in bounds.
        foreach (var arg in g.Args)
            if (arg is QQubitArg qa)
                CheckIndexedAccess(qa.Reg, qa.Index, scope, opName, errors, ctx.Unproven, ctx.ParamNeeds, g.Span, bounds);
            else if (arg is QTextArg text)
                CheckTextIndexes(text.Tree, scope, opName, errors, ctx.Unproven, ctx.ParamNeeds, g.Span, bounds);

        // QSEM014 — the same qubit twice in one gate. Whole registers count: `CNOT(q, q)` broadcasts to
        // duplicate operands, and `CNOT(q, q[0])` overlaps the register with its own element. Indexes are
        // compared by FOLDED VALUE, never by spelling: `CNOT(q[k], q[2])` with `const int k = 2` is the
        // same qubit twice — the same calculator the bounds prover uses, so no two spellings of one index
        // can slip past as "different" qubits.
        // Each qubit operand's index resolves to a DOMAIN of possible values: a const/literal folds to one
        // point; a loop variable folds to its header range [From..To]; a whole register (Index null) or an
        // unresolved index covers everything / itself. Two operands on one register alias when their domains
        // overlap — so `CNOT(q[i], q[2])` both under `for i in 2..2` (singleton) and under `for i in 0..2`
        // (i reaches 2) is caught, not just literal duplicates — and at most one QSEM014 is reported per gate.
        var refs = g.Args.Select(a => QubitRefOf(a, scope)).Where(r => r is not null)
            .Select(r => (r!.Value.Reg, r.Value.Index, Domain: r.Value.Index is { } idx ? IndexDomain(idx, scope, bounds) : null))
            .ToList();
        if (GateNeverRuns(refs, scope, bounds)) return;   // gate inside a provably-empty loop — never emitted, no aliasing possible
        for (var ai = 0; ai < refs.Count; ai++)
            for (var bi = ai + 1; bi < refs.Count; bi++)
            {
                var (aReg, aIdx, aDom) = refs[ai];
                var (bReg, bIdx, bDom) = refs[bi];
                if (aReg != bReg) continue;
                if (aIdx is null || bIdx is null)   // a whole register overlaps anything on it (or another whole)
                    Add(errors, "QSEM014", aIdx is null && bIdx is null
                        ? $"in `{opName}`: `{g.Name}` receives the qubit `{aReg}` more than once; gate operands must be distinct"
                        : $"in `{opName}`: `{g.Name}` receives the register `{aReg}` and one of its own qubits; operands must not overlap", g.Span);
                else if (aDom is { } da && bDom is { } db ? da.Lo <= db.Hi && db.Lo <= da.Hi : aIdx == bIdx)
                    Add(errors, "QSEM014", $"in `{opName}`: `{g.Name}` receives the qubit `{Show(aReg, aIdx)}` more than once; gate operands must be distinct", g.Span);
                else continue;
                return;   // one aliasing pair is enough — one diagnostic per gate
            }

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
                CheckCall(callee, g.Args, "", scope, opName, errors, g.Span, ctx.Floors);
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
        ArrayFloorSink? floors = null)
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
                    // Rung B'/P4 — record this array argument as DATA. A `T[]` parameter carries no length of
                    // its own (it arrives with the argument), so the callee's minimum-length requirement can
                    // only be checked at a call. The check itself happens AFTER the walk, against the
                    // requirement table the prover recorded: a known-length argument is a CHECK fact, and
                    // handing our own parameter through is a PROPAGATION fact (the callee's need becomes ours).
                    else if (floors is not null && sig is QOperation calleeOp)
                        floors.Calls.Add(argSym.ArrayLength is int have
                            ? new ArrayCallFact(floors.CallerOpId, opName, calleeOp.Id, calleeName, p.Name, argSym.SourceName, have, null, span)
                            : new ArrayCallFact(floors.CallerOpId, opName, calleeOp.Id, calleeName, p.Name, argSym.SourceName, null, argSym.SourceName, span));
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

    private static void CheckExprIndexes(QExpr expr, Scope scope, string opName, List<QoraError> errors,
        List<UnprovenIndex> unproven, Dictionary<string, long> paramNeeds, QSpan? span, BoundsCtx bounds = default)
    {
        switch (expr)
        {
            case QText text:
                CheckTextIndexes(text.Tree, scope, opName, errors, unproven, paramNeeds, span, bounds);
                break;
            case QArrayLiteral literal:
                foreach (var element in literal.Elements)
                    CheckExprIndexes(element, scope, opName, errors, unproven, paramNeeds, span, bounds);
                break;
            // A measurement's target index must be bounds-checked here too — a measurement NESTED in an
            // array-literal initializer (`bit[] r = [M(q[3])]`) reaches this recursion, whereas the QDecl
            // handler's measurement branch only fires for a DIRECT `M(...)` value.
            case QMeasure { Target: { } target }:
                CheckQubitIndex(target, scope, opName, errors, unproven, paramNeeds, span, bounds);
                break;
        }
    }

    /// <summary>Bounds-check every indexed access in an expression TREE (see <see cref="QNode"/>). Walking
    /// the tree — not a text regex — finds each <c>base[index]</c> structurally, wherever it is nested.</summary>
    private static void CheckTextIndexes(QNode? tree, Scope scope, string opName, List<QoraError> errors,
        List<UnprovenIndex> unproven, Dictionary<string, long> paramNeeds, QSpan? span, BoundsCtx bounds = default)
    {
        switch (tree)
        {
            case QIndexNode { Base: QNameRef b } acc:
                var idx = IndexText(acc.Index);
                CheckIndexedAccess(b.Name, idx, scope, opName, errors, unproven, paramNeeds, span, bounds);
                if (scope.Lookup(b.Name) is { Type: QType.Qubit })
                    Add(errors, "QSEM026", $"in `{opName}`: `{b.Name}[{idx}]` is a qubit and cannot be used as a classical value", span);
                break;
            case QBinOp bin:
                CheckTextIndexes(bin.Left, scope, opName, errors, unproven, paramNeeds, span, bounds);
                CheckTextIndexes(bin.Right, scope, opName, errors, unproven, paramNeeds, span, bounds);
                break;
            case QUnary u: CheckTextIndexes(u.Operand, scope, opName, errors, unproven, paramNeeds, span, bounds); break;
            case QMember m: CheckTextIndexes(m.Base, scope, opName, errors, unproven, paramNeeds, span, bounds); break;
            case QCallNode { Arg: { } callArg }: CheckTextIndexes(callArg, scope, opName, errors, unproven, paramNeeds, span, bounds); break;
        }
    }

    /// <summary>The text of an atomic index node (the grammar restricts an index to a number or a bare name).</summary>
    private static string IndexText(QNode index) => index switch
    {
        QNumLit n => n.Value.ToString(),
        QNameRef r => r.Name,
        _ => string.Empty,
    };

    /// <summary>
    /// An array/register index must be PROVABLY in bounds (rung B'). Proof paths: P1 a literal within a
    /// known length; P2 a loop variable ranging <c>0..a.Count-1</c>; P3 a loop variable with a constant
    /// upper bound K (equivalent to the literal access <c>a[K]</c>); P4 a call-site minimum-length floor for
    /// a classical-array parameter — recorded HERE as data from the same folded value the verdict used
    /// (one calculator; the floor is resolved against every call after the walk); P5 a programmer guard
    /// <c>if (0 &lt;= n &amp;&amp; n &lt; a.Count)</c>. Safety proofs are ALTERNATIVES — any one suffices, none
    /// outranks another — and they are consulted before any wrongness verdict: QSEM016 ("PROVEN out of
    /// bounds") carries the premise that the access actually executes at the offending index, which an
    /// enclosing guard falsifies. Only when no safety proof exists is wrongness judged (QSEM016), and
    /// failing both, the access is unprovable — QSEM030, because OpenQASM 3 has no runtime bounds check
    /// to defer to.
    /// </summary>
    private static void CheckIndexedAccess(string name, string index, Scope scope, string opName,
        List<QoraError> errors, List<UnprovenIndex> unproven, Dictionary<string, long> paramNeeds,
        QSpan? span, BoundsCtx bounds = default)
    {
        var symbol = scope.Lookup(name);
        if (symbol is null) return;
        if (!symbol.IsArray)
        {
            Add(errors, "QSEM016", $"in `{opName}`: `{name}` is a scalar and cannot be indexed (`{name}[{index}]`)", span);
            return;
        }

        // The index name resolved ONCE, through the scope chain, to the variable it actually denotes HERE.
        // Every fact lookup below keys on this symbol — a same-named variable elsewhere is a different key.
        var idxSym = scope.Lookup(index);
        if (idxSym is { Type: QType.Qubit } or { IsArray: true })
        {
            Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` needs one classical integer index", span);
            return;
        }

        var length = symbol.Type == QType.Qubit ? symbol.RegisterSize : symbol.ArrayLength;

        // P4 — the access is on a classical-array PARAMETER (the only unknown-length classical array: locals
        // always have a known length, and a parameter's length arrives with each argument). The maximum index
        // this access provably reaches becomes the parameter's minimum required length — recorded as DATA
        // from the SAME folded value the verdict logic used, and resolved against every call site after the
        // walk. A need no legal array can meet (past int.MaxValue) is provably wrong for EVERY argument.
        void RequireArgLength(long maxIndex)
        {
            if (maxIndex >= int.MaxValue)
                Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` can never be in bounds — no array has more than {int.MaxValue} element(s)", span);
            else
                paramNeeds[name] = System.Math.Max(paramNeeds.GetValueOrDefault(name), maxIndex + 1);
        }

        // P1 — a literal index. Known length: bounds-check now. Unknown length: a Qubit[] parameter defers to
        // the post-mono re-validation (its size becomes concrete); a classical parameter records its P4 floor.
        if (index.Length > 0 && index.All(char.IsDigit))
        {
            if (length is int lit && (!int.TryParse(index, out var value) || value >= lit))
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is out of range; `{name}` has {lit} element(s) (valid: 0..{lit - 1})", span);
            else if (length is null && symbol.Type != QType.Qubit)
                RequireArgLength(long.TryParse(index, out var idx) ? idx : long.MaxValue);   // an unparseable literal is, a fortiori, past any length
            return;
        }

        // P5 FIRST — safety proofs are alternatives, and a guard is sufficient ON ITS OWN: the access only
        // EXECUTES when `0 <= index && index < name.Count` held, so no other fact can make it unsafe. The
        // loop verdict below is a WRONGNESS proof, and wrongness proofs carry a premise — "the access
        // executes at the loop's maximum" — that an enclosing guard falsifies. So wrongness may only be
        // judged when no safety proof exists; asking in the other order rejected the clamp idiom
        // `for i in 0..5 { if (0 <= i && i < a.Count) { a[i] } }` as "PROVEN out of bounds" (it never is)
        // and recorded a P4 floor for an access whose guard makes ANY argument length safe.
        if (bounds.Guarded(idxSym, symbol, length)) return;

        // A ByConst guard `index < K` on an UNSIZED Qubit[] parameter can't be confirmed against the (unknown)
        // length yet — but post-monomorphization the size is concrete, so defer, exactly as a literal index
        // does (a guard proving a strict subset of a deferred access must not be rejected where the literal
        // is accepted). The `index < q.Count` form is length-independent and already proved just above.
        if (length is null && symbol.Type == QType.Qubit && bounds.HasConstGuard(idxSym)) return;

        // P1 extended — a non-literal index that FOLDS to a definite value (a const name) IS the literal
        // access at that value: same calculator, same verdicts. The grammar keeps an index atomic (a number
        // or a bare name), so FoldAtom suffices. Sits after P5 because an enclosing guard would keep an
        // out-of-range access from ever executing.
        if (BoundFolder.FoldAtom(index, scope) is BoundNum idxVal)
        {
            if (idxVal.Value < 0)
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is {idxVal.Value} — negative, out of range for any array", span);
            else if (length is int len && idxVal.Value >= len)
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is {idxVal.Value}, out of range; `{name}` has {len} element(s) (valid: 0..{len - 1})", span);
            else if (length is null && symbol.Type != QType.Qubit)
                RequireArgLength(idxVal.Value);
            return;   // in range, or a Qubit[] parameter (post-mono re-check)
        }

        // The index is a LOOP VARIABLE: judge it by the loop's bounds — folded AT THE HEADER (see the QFor
        // case in Walk), so the verdict reads the same values the emitted loop runs with. The rule is "does
        // the computation settle?", not "does a pattern match": the verdict follows what folding yielded.
        var inLoop = bounds.LoopRange(idxSym, out var fact);
        var (from, to) = (fact.From, fact.To);
        if (inLoop)
        {
            // A NEGATIVE settled From that provably executes (To settles at or above it) starts at a
            // negative index — out of range for ANY array: proven wrong, not unprovable. To < From is an
            // empty loop and safe. An unsettled To alongside a negative From stays unproven below.
            if (fact.FromB is BoundNum { Value: < 0 } neg && fact.ToB is BoundNum settled)
            {
                if (settled.Value >= neg.Value)
                    Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` starts at index {neg.Value} (loop `{index} in {from}..{to}`) — negative, out of range for any array", span);
                return;
            }

            if (fact.FromB is BoundNum { Value: >= 0 } f)
            {
                switch (fact.ToB)
                {
                    // P3 — the bound COMPUTES (every leaf known: literals, consts, known lengths). The loop's
                    // maximum index is exact, so the verdict is exact — evaluation, not pattern-matching.
                    // An empty loop (To < From) never runs its body, so it is trivially safe and no floor is
                    // recorded; a non-empty one over a classical parameter records its P4 floor from the SAME
                    // folded maximum the verdict used — the floor can never disagree with the prover.
                    case BoundNum t:
                        if (t.Value >= f.Value)
                        {
                            if (length is int sz && t.Value >= sz)
                                Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` reaches index {t.Value} (loop `{index} in {from}..{to}`), out of range; `{name}` has {sz} element(s) (valid: 0..{sz - 1})", span);
                            else if (length is null && symbol.Type != QType.Qubit)
                                RequireArgLength(t.Value);
                        }
                        return;   // in range, empty, or a Qubit[] parameter (post-mono re-check)

                    // P2 generalized — a SAME-array bound `Count + C` is judged for ANY length:
                    //   C <= -1 → max index <= Count-1: safe however long the array turns out to be
                    //   C >=  0 → reaches index Count or beyond: out of range for EVERY length
                    case BoundCount c when c.Array == name && c.Coeff == 1:
                        if (c.Offset <= -1) return;
                        Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` reaches index `{to}` — at or past `{name}.Count`, out of range for any length (valid: 0..{name}.Count-1)", span);
                        return;

                    // `k*Count + C` with k >= 2, C >= -1 exceeds Count-1 for every length >= 1.
                    case BoundCount c when c.Array == name && c.Coeff >= 2 && c.Offset >= -1:
                        Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` reaches index `{to}`, out of range for any length of `{name}`", span);
                        return;
                }
            }

            // A Qubit[] parameter's `.Count` becomes a concrete size after specialization — defer this access
            // to the post-monomorphization validation pass, exactly like an unknown-length literal index.
            if (fact.DefersToMono) return;
            // otherwise the bound does not settle → unproven → rejection below
        }

        // No proof exists. Recorded as DATA, not as a diagnostic — the failed proof is target-independent;
        // its disposition (QSEM030 on the OpenQASM path, a runtime check on a QIR path) is derived from
        // <see cref="SemanticModel.UnprovenIndexes"/> after the walk, in ONE place at the end of Validate.
        // Blame the bound that actually failed to settle: when From never folded, naming To would accuse
        // the wrong bound (and the fix hint would send the user to the wrong place).
        unproven.Add(new UnprovenIndex(opName, name, index,
            !inLoop ? null : fact.FromB is null ? from : to, span));
    }

    /// <summary>QSEM016 — literal index bounds against known register sizes; no indexing single qubits.</summary>
    private static void CheckQubitIndex(QQubitArg q, Scope scope, string opName, List<QoraError> errors,
        List<UnprovenIndex> unproven, Dictionary<string, long> paramNeeds, QSpan? span, BoundsCtx bounds = default)
    {
        if (scope.Lookup(q.Reg) is { } resolved)
        {
            CheckIndexedAccess(q.Reg, q.Index, scope, opName, errors, unproven, paramNeeds, span, bounds);
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

    /// <summary>The set of values a qubit index can take, as an inclusive <c>[Lo..Hi]</c> range: a const or
    /// literal folds to a single point; a loop variable to its header range, and when only the lower bound
    /// settles (a runtime/symbolic upper bound) to <c>[From..long.MaxValue]</c> — the loop still starts at
    /// From on its guaranteed first iteration, so an operand equal to a reachable value provably aliases.
    /// Null means unresolvable (compared by spelling). Used for QSEM014 operand-aliasing: two operands
    /// collide when their ranges intersect.</summary>
    private static (long Lo, long Hi)? IndexDomain(string idx, Scope scope, BoundsCtx bounds)
    {
        if (BoundFolder.FoldAtom(idx, scope) is BoundNum v) return (v.Value, v.Value);
        if (scope.Lookup(idx) is { } sym && bounds.LoopRange(sym, out var f) && f.FromB is BoundNum a)
            return f.ToB is BoundNum b
                ? a.Value <= b.Value ? (a.Value, b.Value) : null   // settled range (empty -> handled by GateNeverRuns)
                : (a.Value, long.MaxValue);                        // From settled, To symbolic: reachable set is at least [From, ...]
        return null;
    }

    /// <summary>True when a gate operand's index is a loop variable whose header range is PROVABLY EMPTY
    /// (From &gt; To, both settled): the loop body never runs, so the gate is never emitted and no operand
    /// aliasing is possible — QSEM014 must skip it entirely (not fall to a spelling comparison that would
    /// reject a never-executed <c>CNOT(q[i], q[i])</c> while accepting the equally-dead <c>CNOT(q[i], q[2])</c>).</summary>
    private static bool GateNeverRuns(IEnumerable<(string Reg, string? Index, (long, long)? Domain)> refs, Scope scope, BoundsCtx bounds) =>
        refs.Any(r => r.Index is { } idx && scope.Lookup(idx) is { } sym && bounds.LoopRange(sym, out var f)
            && f.FromB is BoundNum a && f.ToB is BoundNum b && a.Value > b.Value);

    private static (string Reg, string? Index)? QubitRefOf(QArg arg, Scope scope) => arg switch
    {
        // Only a QUBIT-based reference is a gate operand for aliasing purposes. `x[0]` where `x` is a
        // classical array parses to the same (reg, index) shape but is a classical value — passing it twice
        // is fine, so it must not count as a duplicate qubit operand (QSEM014).
        QQubitArg q when IsQubit(scope.Lookup(q.Reg)) => (q.Reg, q.Index),
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
