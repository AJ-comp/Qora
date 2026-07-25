namespace Qora.Ir.Passes;

/// <summary>
/// The semantic-validation pass ("lap 0"): one full walk over the whole IR that returns EVERY violation it
/// finds (collect-all, no early stop). Some historical rules still encode constraints shared with the
/// current OpenQASM target; bounds proof now has an explicit fact/policy boundary: this pass records an
/// unproven indexed access in <see cref="SemanticModel.UnprovenIndexes"/> without deciding its target policy.
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
///   <item><b>QSEM014</b> — overlapping gate operands, or one classical array passed into multiple
///         parameter slots when at least one slot is <c>inout</c>.</item>
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
///         a const array to an <c>inout</c> parameter. The initializer may still be any valid value.</item>
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
///   <item><b>QSEM030</b> is deliberately NOT emitted here. A failed in-bounds proof is recorded as
///         <see cref="UnprovenIndex"/> data; the OpenQASM backend turns final unresolved facts into QSEM030.
///         A runtime-capable backend still needs stable site identity and a lowering policy before it can
///         handle this category differently. An index proven OUT of range remains QSEM016 here.</item>
///   <item><b>QSEM031</b> — an expression or nested-block structure deeper than the compiler's recursion
///         limit (only ever machine-generated). Rejected up front so pathological depth is a clean
///         diagnostic, not an uncatchable stack overflow.</item>
///   <item><b>QSEM032</b> — a write to a <c>bit[]</c> PARAMETER. Its OpenQASM form is a by-value
///         <c>bit[N]</c> register (references exist only for array-typed parameters, which bit cannot
///         be), so the write would silently never reach the caller — banned so the value/reference
///         asymmetry between <c>bit[]</c> and the other <c>T[]</c> parameters stays unobservable.</item>
///   <item><b>QSEM036</b> — a WHOLE <c>bit[]</c> register read as a NUMBER (assigned to an <c>int</c>, used
///         in arithmetic, used as a bare truth condition, compared against a number), or compared to a
///         register of a different width, or ordered (<c>&lt;</c>/<c>&gt;</c>) against another register. A bit
///         pattern carries no sign, so it has no numeric value on its own — the same bits read 2 unsigned and
///         −2 in two's complement — and the language refuses to choose one silently. <c>AsInt(f)</c> is the
///         one explicit reading. Raised in <see cref="SymbolTableBuilder"/>'s expression walk, the single
///         place that pairs each position with its own scope, so no position can be forgotten; the four spots
///         where a whole register IS legitimate (index base, <c>.Count</c> base, an argument, and
///         <c>==</c>/<c>!=</c> against an equal-width register) are carved out by the PARENT node.</item>
///   <item><b>QSEM037</b> — a function return violates its declared scalar return type, a function result is
///         stored in an incompatible explicitly typed scalar, or a whole classical array is used where one
///         scalar value is required.</item>
///   <item><b>QSEM038</b> — an invalid parameter access contract: unsupported <c>inout</c> declaration,
///         declaration/call-site mode mismatch, write through an ordinary read-only parameter, or forwarding
///         ordinary read-only access into an <c>inout</c> slot.</item>
///   <item><b>QSEM035</b> — a <c>return</c> in an <c>operation</c> (void), or a <c>function</c> with a path
///         that produces no value. Nothing is said about WHERE a return stands: it may sit anywhere a
///         statement may, and <see cref="ReturnFlattening"/> reshapes the function into the single-bottom-
///         return form the execution target needs. The coverage predicate is literally the pass's own
///         (<c>ReturnFlattening.AlwaysReturns</c>), so what validation accepts is exactly what it can
///         reshape — the two can never drift apart.</item>
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
        var deferred = new List<DeferredSizeCheck>();   // rung B′ data: verdicts postponed to the post-mono re-check
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

        // Namespace and callable declarations belong to the program graph, built before any callable body
        // so forward references share stable SymbolIds. Each operation body then receives its own parentless
        // lexical Scope for parameters and locals.
        var programSymbols = SymbolTableBuilder.BuildProgramSymbols(program);
        model.SetProgramSymbols(programSymbols);

        // A deferred ("re-check after monomorphization") fact is only sound when a post-mono re-check
        // actually happens. The Monomorphizer specializes exactly the generics REACHABLE from concrete
        // ops through the call graph (a concrete body's calls are rewritten; each specialization's body
        // is rewritten in turn, propagating onward) — a generic whose call sites all live inside OTHER
        // dropped generics is itself dropped, however many "callers" it has. So the predicate must be
        // the same transitive closure the Monomorphizer computes, not a flat has-any-caller set.
        // The Monomorphizer's own trigger, by construction — both read QParam.NeedsMonoSizing, the one
        // definition of "specializes per call-site length", so this test can never drift from what the
        // monomorphizer actually does.
        static bool IsGenericOp(QOperation o) => o.Params.Any(p => p.NeedsMonoSizing);

        void CollectCallTargets(IReadOnlyList<QStmt> body, HashSet<int> ids, HashSet<string> names)
        {
            foreach (var s in body)
            {
                // Function calls in expressions (value/condition/argument/return) are call edges too.
                foreach (var tree in QNodes.ExpressionSites(s))
                    foreach (var call in QNodes.CallsIn(tree))
                    {
                        if (call.CalleeOpId is int expressionCalleeId) ids.Add(expressionCalleeId);
                        names.Add(call.Name);
                    }
                switch (s)
                {
                    case QGate g:
                        if (g.CalleeOpId is int cid) ids.Add(cid);
                        names.Add(g.Name);
                        break;
                    case QIf i: CollectCallTargets(i.Then, ids, names); CollectCallTargets(i.Else, ids, names); break;
                    case QFor f: CollectCallTargets(f.Body, ids, names); break;
                    case QWhile w: CollectCallTargets(w.Body, ids, names); break;
                    case QRepeat r: CollectCallTargets(r.Body, ids, names); break;
                    case QConjugate c: CollectCallTargets(c.Within, ids, names); CollectCallTargets(c.Apply, ids, names); break;
                }
            }
        }

        // worklist closure: seed with every CONCRETE op's call targets, then follow reached generics.
        var reachedIds = new HashSet<int>();
        var reachedNames = new HashSet<string>();
        var frontier = new Queue<QOperation>(program.Operations.Where(o => !IsGenericOp(o)));
        var enqueued = new HashSet<int>(frontier.Select(o => o.Id));
        while (frontier.Count > 0)
        {
            var caller = frontier.Dequeue();
            var ids = new HashSet<int>();
            var names = new HashSet<string>();
            CollectCallTargets(caller.Body, ids, names);
            reachedIds.UnionWith(ids);
            reachedNames.UnionWith(names);
            foreach (var callee in program.Operations)
                if (IsGenericOp(callee) && (ids.Contains(callee.Id) || names.Contains(callee.Name)) && enqueued.Add(callee.Id))
                    frontier.Enqueue(callee);
        }

        foreach (var op in program.Operations)
        {
            // QSEM013 — names whose MEANING cannot be disambiguated stay reserved: the measurement
            // family and pi/tau/euler (expression-position tokens the resolver never sees), and
            // built-in GATE names in the GLOBAL namespace only — a global op has no qualifier, so a
            // gate-named one could never be referenced. Inside a namespace a gate name is legal
            // (Q#-style): the resolver makes any ambiguous USE an explicit QSEM018, never a silent pick.
            var simpleName = Simple(op.Name);
            if (QoraGates.MeasureLike.Contains(simpleName) || QoraGates.Functions.ContainsKey(simpleName)
                || SymbolTableBuilder.IsReservedName(simpleName))
                Add(errors, "QSEM013", $"operation name `{simpleName}` shadows the built-in `{simpleName}`; choose another name", op.Span);
            else if (!op.Name.Contains('.') && QoraGates.Names.ContainsKey(simpleName))
                Add(errors, "QSEM013", $"global operation `{simpleName}` shadows the built-in gate `{simpleName}` (the global namespace shares one scope with the built-ins, so it has no qualifier to disambiguate with); move it into a namespace or rename it", op.Span);

            // QSEM016 — an internally specialized Qubit[] parameter must have a positive concrete size.
            // QSEM038 — `inout` is deliberately a narrow classical-array borrowing contract. Functions
            // stay side-effect-free, qubits keep their existing gate-driven state semantics, bit[] stays a
            // by-value read-only register, and scalar reassignment never needs caller-visible borrowing.
            foreach (var p in op.Params)
            {
                if (p.Type == QType.Qubit && p.RegisterSize is int rs && rs < 1)
                    Add(errors, "QSEM016", $"in `{op.DisplayName ?? op.Name}`: parameter `{p.Name}` has an invalid register size; it must be a whole number from 1 to {int.MaxValue}", p.Span);
                if (p.Mode != QParameterMode.InOut) continue;
                if (op.IsFunction)
                    Add(errors, "QSEM038", $"in function `{op.DisplayName ?? op.Name}`: parameter `{p.Name}` cannot be `inout`; functions are read-only and cannot change caller-owned storage", p.Span);
                else if (!p.IsArray || p.Type is QType.Bit or QType.Qubit)
                    Add(errors, "QSEM038", $"in `{op.DisplayName ?? op.Name}`: parameter `{p.Name}` cannot be `inout`; only `int[]`, `float[]`, and `angle[]` operation parameters support caller-visible mutation", p.Span);
            }

            // The unified symbol table IS the scope model: a nested scope tree in which every name carries
            // its kind / type / register size. Building it reports EVERY same-scope collision the emitter
            // cannot tolerate (QSEM015) through the single `Declare` insertion door — parameters and `use`
            // registers at the root, block-scoped measure bits / vars / consts / loop vars in their block
            // alike — so there are no parallel duplicate-name checks out here. `scopeOf` maps each body list
            // to its scope so the walk below resolves every operand with correct nesting. Nothing re-derives it.
            var scopeOf = new Dictionary<IReadOnlyList<QStmt>, Scope>();
            var typeMismatchCandidates = new Dictionary<int, QoraError>();
            var root = SymbolTableBuilder.Build(op, errors, scopeOf, programSymbols, opById,
                (statementId, error) => typeMismatchCandidates.TryAdd(statementId, error));
            model.AddOperation(op, root);

            var opNeeds = new Dictionary<string, long>();
            needsByOp[op.Id] = opNeeds;
            // A generic (unsized Qubit[]) op gets its precise re-check post-monomorphization ONLY if the
            // Monomorphizer will actually specialize it — i.e. it is REACHABLE from a concrete op through
            // the call graph; otherwise deferral would be a silent skip (a dead generic calling another
            // generic does not make the callee live).
            var willBeRechecked = !IsGenericOp(op) || reachedIds.Contains(op.Id) || reachedNames.Contains(op.Name);
            model.SetWillBeRechecked(op.Id, willBeRechecked);   // the verdict is model DATA, not a discarded local — see the model doc
            var ctx = new Ctx(op, entry.Name, ops, opByName, opById, programSymbols, inverter, errors, unproven,
                deferred, opNeeds, new ArrayFloorSink(op.Id, calls), scopeOf, typeMismatchCandidates,
                new Dictionary<QCallNode, bool>(ReferenceEqualityComparer.Instance), willBeRechecked);
            Walk(op.Body, root, ctx, inControlFlow: false);
            if (op.IsFunction) ValidateFunctionShape(op, ctx);
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

        // Rung B′ fact publication. The walk above RECORDS unproven accesses as target-independent data;
        // it does not turn them into diagnostics. OpenQASM rejects the final list because it has no runtime
        // failure channel. A future checked-access backend must additionally define stable site identity,
        // runtime failure, and dynamic-alias policies.
        // Preserve every visited access site in walk order. A lowering may produce two value-equal entries
        // from one source span (for example the pre-loop and back-edge copies of a measured while condition);
        // collapsing them here would discard backend work. OpenQASM de-duplicates only the user-facing
        // diagnostics it derives. Stable expression-site identity remains a prerequisite for a future
        // runtime-check rewriting backend.
        foreach (var u in unproven)
            model.AddUnprovenIndex(u);

        // The deferral ledger — recorded during the walk, model DATA afterwards. No diagnostic is derived
        // here: a deferral is a promise, not a failure. On today's pipeline the post-monomorphization
        // re-validation runs on a fully sized program and defers nothing, so the FINAL model always
        // carries an empty ledger; a pre-mono model of a generic program carries the outstanding promises
        // a no-specialization backend would have to dispose of. NO Distinct, deliberately — the opposite
        // of the diagnostic flushes above: a diagnostic names a source MISTAKE (value-equal copies are one
        // mistake, collapse), but a ledger entry names an access SITE a backend must dispose of, and two
        // sites can be value-equal — same statement (one span), spanless imported code (Span null), or a
        // lowering-duplicated statement (each copy is a real emitted access). Collapsing would silently
        // under-count the work list. One walk visits each site once, so no spurious duplicates exist.
        foreach (var d in deferred)
            model.AddDeferredSizeCheck(d);

        // Two byte-identical diagnostics (same message, code, span) are noise, never information — one
        // source mistake shows once. Value-equal QoraError records collapse.
        return errors.Distinct().ToList();
    }

    /// <summary>The compiler's recursion-depth ceiling for statement nesting and expression trees. Every
    /// pass walks these recursively; a program past the limit (only ever machine-generated — no one nests
    /// 400 blocks or writes a 400-operator expression by hand) would overflow the stack into an UNCATCHABLE
    /// <c>StackOverflowException</c>, bypassing the front end's try/catch and its "always one JSON line"
    /// contract. Well under the ~1300+ frames where the overflow actually appears.</summary>
    private const int DepthLimit = 400;

    /// <summary>True when any operation nests statements or an expression tree deeper than
    /// <see cref="DepthLimit"/>. Measured ITERATIVELY (an explicit stack, never our own recursion), so the
    /// guard itself cannot overflow — run BEFORE the recursive passes, it turns pathological depth into a
    /// clean diagnostic instead of a process crash.</summary>
    internal static bool ExceedsDepthLimit(QProgram program, out string opName)
    {
        foreach (var op in program.Operations)
        {
            opName = op.DisplayName ?? op.Name;
            var stack = new Stack<(IReadOnlyList<QStmt> Body, int Depth)>();
            stack.Push((op.Body, 1));
            while (stack.Count > 0)
            {
                var (body, depth) = stack.Pop();
                if (depth > DepthLimit) return true;
                foreach (var s in body)
                {
                    if (StmtExprTooDeep(s)) return true;
                    switch (s)
                    {
                        case QIf i: stack.Push((i.Then, depth + 1)); stack.Push((i.Else, depth + 1)); break;
                        case QFor f: stack.Push((f.Body, depth + 1)); break;
                        case QWhile w: stack.Push((w.Body, depth + 1)); break;
                        case QRepeat r: stack.Push((r.Body, depth + 1)); break;
                        case QConjugate c: stack.Push((c.Within, depth + 1)); stack.Push((c.Apply, depth + 1)); break;
                    }
                }
            }
        }
        opName = string.Empty;
        return false;
    }

    // Every expression position, from the ONE canonical enumeration — a hand-rolled list here once
    // missed array-literal elements, and a missed position is an uncatchable stack overflow in the
    // recursive walkers instead of a clean QSEM031.
    private static bool StmtExprTooDeep(QStmt s) => QNodes.ExpressionSites(s).Any(NodeTooDeep);

    private static bool NodeTooDeep(QNode? node)
    {
        if (node is null) return false;
        var stack = new Stack<(QNode Node, int Depth)>();
        stack.Push((node, 1));
        while (stack.Count > 0)
        {
            var (n, d) = stack.Pop();
            if (d > DepthLimit) return true;
            switch (n)
            {
                case QBinOp b: stack.Push((b.Left, d + 1)); stack.Push((b.Right, d + 1)); break;
                case QUnary u: stack.Push((u.Operand, d + 1)); break;
                case QMember m: stack.Push((m.Base, d + 1)); break;
                case QIndexNode ix: stack.Push((ix.Base, d + 1)); stack.Push((ix.Index, d + 1)); break;
                case QCallNode c: foreach (var a in c.Args) stack.Push((a, d + 1)); break;
            }
        }
        return false;
    }

    /// <summary>
    /// Everything the per-operation walk needs beyond the current lexical <see cref="Scope"/>: the program
    /// declaration graph, callable indexes, and <c>ScopeOf</c>, which maps each body list to its exact
    /// lexical scope so branch and loop recursion preserves visibility.
    /// </summary>
    private sealed record Ctx(
        QOperation Op, string EntryName, HashSet<string> Ops, Dictionary<string, QOperation> OpByName,
        Dictionary<int, QOperation> OpById, ProgramSymbolGraph ProgramSymbols, Inverter Inverter,
        List<QoraError> Errors,
        List<UnprovenIndex> Unproven,
        List<DeferredSizeCheck> Deferred,
        Dictionary<string, long> ParamNeeds,
        ArrayFloorSink Floors,
        IReadOnlyDictionary<IReadOnlyList<QStmt>, Scope> ScopeOf,
        Dictionary<int, QoraError> TypeMismatchCandidates,
        Dictionary<QCallNode, bool> CallValidity,
        // "Will this op's postponed judgements get their post-mono re-validation?" — a PREDICTION made
        // before monomorphization runs, from the same reachability the Monomorphizer acts on. False for
        // a generic op nothing calls: monomorphization will DROP it, so no re-check ever runs — every
        // "defer to mono" judgement must then fall back to its conservative pre-mono form instead of
        // silently skipping.
        bool WillBeRechecked);

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
                    var declarationCallsAreValid = true;
                    if (d.IsArray)
                    {
                        // Array placement is a TARGET limitation, not a language rule: OpenQASM wants
                        // arrays at global scope, so the QASM backend's ArrayLocalHoisting pass threads a
                        // def-local array as a hidden reference parameter (and hoists a nested one to its
                        // scope top). The language itself allows `T[]` locals anywhere a scalar goes —
                        // the QSEM012 arm that once rejected them here was that target rule leaking out.
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

                    // A FUNCTION call is a legal scalar value (`var k: int = two();`), checked by CheckExprCalls.
                    // A measurement or operation call is not; nor is a call inside an array-literal ELEMENT
                    // (`var b: int[] = [f(x), 2]` would ship a call inside a `{…}` initializer — invalid
                    // OpenQASM), which stays QSEM005 via the TreesOf enumeration.
                    if (d.Value is QText { Tree: { } declTree })
                        declarationCallsAreValid = CheckExprCalls(declTree, scope, ctx, opName, d.Span);
                    else if (ContainsCallOutsideMeasurementIndex(d.Value))
                    {
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: the initializer of `{d.Name}` contains a call; only the lone form `var r: bit = M(q[i]);` or a `function` call is supported here", d.Span);
                        declarationCallsAreValid = false;
                    }
                    // CheckExprIndexes bounds-checks every index in the value, INCLUDING a measurement target
                    // (direct or nested in an array literal) — the single measure-index check, so no dedicated
                    // CheckQubitIndex is repeated here (that duplicated the diagnostic).
                    CheckExprIndexes(d.Value, scope, opName, ctx, d.Span, flow);
                    // QSEM017 — a measurement result is a `bit`, so measuring into a non-bit target is a type
                    // error, whether the measurement is the whole value (`var x: int = M(q)`) or an element of an
                    // array literal (`var a: int[] = [M(q)]`, which would otherwise emit `measure` inside a `{…}`
                    // initializer — invalid OpenQASM).
                    if (d.Type is not null && d.Type != QType.Bit
                        && (d.Value is QMeasure || d.Value is QArrayLiteral { Elements: { } els } && els.Any(e => e is QMeasure)))
                        Add(ctx.Errors, "QSEM017", $"in `{opName}`: `{d.Name}` is declared `{d.Type.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`", d.Span);
                    CommitTypeMismatch(d.Id, declarationCallsAreValid, ctx);
                    break;

                case QAssign a:
                    var assignmentCallsAreValid = true;
                    // The assignment TARGET's kind, resolved once. `a.Name` is also recorded as a use by the
                    // symbol table, so an unknown target is QSEM025 there.
                    var target = scope.Lookup(a.Name);
                    // QSEM032 — a bit[] PARAMETER is read-only: its QASM form is a by-value `bit[N]`
                    // register (references exist only for array-typed parameters, which bit cannot be), so
                    // a write inside the operation would silently never reach the caller.
                    if (target is { Kind: SymbolKind.Parameter, Type: QType.Bit, IsArray: true })
                        Add(ctx.Errors, "QSEM032", $"in `{opName}`: parameter `{a.Name}` is a `bit[]`, which is read-only inside an operation (OpenQASM passes bit registers by value, so the write would silently stay local); measure into a local bit[] instead, or pass `int[]` if the caller must see updates", a.Span);
                    // QSEM038 — every ordinary parameter binding is read-only. A supported classical array
                    // becomes writable only when its declaration explicitly says `inout`; the symbol carries
                    // that access mode, so a shadowing local with the same source name is unaffected.
                    else if (target is
                             {
                                 Kind: SymbolKind.Parameter,
                                 Type: { } parameterType,
                                 ParameterMode: not QParameterMode.InOut
                             }
                             && parameterType != QType.Qubit)
                        Add(ctx.Errors, "QSEM038", target.IsArray
                            ? $"in `{opName}`: parameter `{a.Name}` is read-only; declare it as `inout {a.Name}: {TypeName(parameterType)}[]` and mark matching call arguments with `inout` if caller-visible array mutation is intended"
                            : $"in `{opName}`: parameter `{a.Name}` is read-only and cannot be reassigned; copy it into a local `var` before changing the value",
                            a.Span);
                    if (target is { IsArray: true } && a.Index is null)
                        Add(ctx.Errors, "QSEM029", $"in `{opName}`: whole-array assignment to `{a.Name}` is not supported; assign one element with `{a.Name}[i] = value`", a.Span);
                    if (a.Index is not null)
                        CheckIndexedAccess(a.Name, a.Index, scope, opName, ctx, a.Span, flow);
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
                    // same type error as declaring `var r: int = M(...)`.
                    else if (a.Value is QMeasure && target is { Type: { } tt } && tt != QType.Bit)
                        Add(ctx.Errors, "QSEM017", $"in `{opName}`: `{a.Name}` is `{tt.ToString()!.ToLowerInvariant()}` but a measurement result is a `bit`", a.Span);
                    if (a.Value is QText { Tree: { } assignTree })
                        assignmentCallsAreValid = CheckExprCalls(assignTree, scope, ctx, opName, a.Span);
                    else if (ContainsCallOutsideMeasurementIndex(a.Value))
                    {
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: the value assigned to `{a.Name}` contains a call; only the lone form `{a.Name} = M(q[i]);` or a `function` call is supported here", a.Span);
                        assignmentCallsAreValid = false;
                    }
                    // CheckExprIndexes bounds-checks the measure target too (via its QMeasure case) — the
                    // single measure-index check, so no dedicated CheckQubitIndex is repeated (it duplicated).
                    CheckExprIndexes(a.Value, scope, opName, ctx, a.Span, flow);
                    CommitTypeMismatch(a.Id, assignmentCallsAreValid, ctx);
                    break;

                case QIf i:
                    CheckCondition(i.Cond, scope, ctx, opName, i.Span);
                    CheckTextIndexes(i.Cond.Tree, scope, opName, ctx, i.Span, flow);
                    // P5 — the then-branch runs only when the guard held, so a guarded index is proven there.
                    // The guard's names resolve HERE, at the if's own scope: the facts are about these
                    // variables, and a shadowing binder inside gets a different Symbol — no leak either way.
                    Walk(i.Then, Child(i.Then), ctx, inControlFlow: true, flow.WithGuards(ParseGuards(i.Cond.Tree, scope)));
                    Walk(i.Else, Child(i.Else), ctx, inControlFlow: true, flow);
                    break;
                case QFor f:
                    // A `for` bound is a plain expression; a call/measurement there has no OpenQASM lowering.
                    // Judged structurally (a call is a NODE, QSEM005). The bounds ARE trees now — the old
                    // dropped-tree desync is unrepresentable, so no tree-presence guard exists to need.
                    // A `for` bound must be statically foldable (the bounds prover ranges the loop variable
                    // over it), so it stays call-free — a measurement OR a function call is rejected; compute
                    // the bound into a variable first and use that.
                    if (QNodes.ContainsCall(f.From) || QNodes.ContainsCall(f.To))
                        Add(ctx.Errors, "QSEM005", $"in `{opName}`: a `for` bound cannot contain a call or measurement; compute it into a variable first and use a numeric or variable bound", f.Span);
                    CheckTextIndexes(f.From, scope, opName, ctx, f.Span, flow);
                    CheckTextIndexes(f.To, scope, opName, ctx, f.Span, flow);
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
                    if (forBody.ParentScope?.LookupLocal(f.Var) is { Kind: SymbolKind.LoopVar } loopSym)
                    {
                        // Fold the bound trees (built once at lowering) in the header's scope. A bound over an
                        // unsized Qubit[] .Count — directly or through a const — folds to an ArrayLengthBound and
                        // defers to the post-mono pass, where the size is concrete. The diagnostic spellings
                        // are rendered HERE, once, from the same trees the verdicts fold.
                        var fromB = BoundFolder.Fold(f.From, scope);
                        var toB = BoundFolder.Fold(f.To, scope);
                        forFlow = forFlow.WithLoop(loopSym, new LoopFact(QNodes.Render(f.From), QNodes.Render(f.To), fromB, toB,
                            BoundFolder.DefersToUnsizedQubit(fromB, scope) || BoundFolder.DefersToUnsizedQubit(toB, scope)));
                    }
                    Walk(f.Body, forBody, ctx, inControlFlow: true, forFlow);
                    break;
                case QWhile w:
                    // The condition re-evaluates after EVERY pass through the body, so it too must hold with
                    // the body's reassignments invalidated — not just on the pre-loop first evaluation.
                    CheckCondition(w.Cond, scope, ctx, opName, w.Span);
                    // Back-edge: pre-loop facts about a name the body reassigns cannot survive into the body.
                    var whileWiped = WithoutBodyAssigned(flow, w.Body, Child(w.Body), ctx.ScopeOf);
                    // The condition's own indexes are checked under those wiped facts (not its own guard).
                    CheckTextIndexes(w.Cond.Tree, scope, opName, ctx, w.Span, whileWiped);
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
                    // …and it therefore RESOLVES in the body's scope too: an `until` may read a name the body
                    // declared, exactly as the symbol table records it (and as CheckTextIndexes below already
                    // did). Checking it against the enclosing scope made a legal body-local argument look like
                    // an undeclared one.
                    CheckCondition(r.Until, Child(r.Body), ctx, opName, r.Span);
                    CheckTextIndexes(r.Until.Tree, Child(r.Body), opName, ctx, r.Span, repeatFlow);
                    break;
                case QConjugate c:
                    Walk(c.Within, Child(c.Within), ctx, inControlFlow, flow);
                    Walk(c.Apply, Child(c.Apply), ctx, inControlFlow, flow);
                    break;
                case QReturn ret:
                    var returnCallsAreValid = true;
                    // QSEM035 — `return` is only meaningful inside a function (an operation is void).
                    if (!ctx.Op.IsFunction)
                        Add(ctx.Errors, "QSEM035", $"in `{opName}`: `return` is only allowed inside a function; an operation is void", ret.Span);
                    // The returned value is an ordinary classical expression: a function call is a legal value,
                    // a measurement/operation call is not. (A `return M(q)` is caught as impure by
                    // ValidateFunctionShape too — this covers a return nested in a returned sub-expression.)
                    if (ret.Value is QText { Tree: { } retTree })
                        returnCallsAreValid = CheckExprCalls(retTree, scope, ctx, opName, ret.Span);
                    CheckExprIndexes(ret.Value, scope, opName, ctx, ret.Span, flow);
                    CommitTypeMismatch(ret.Id, returnCallsAreValid, ctx);
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
                if (rhs is ArrayLengthBound { IsOverflowFree: true, Coeff: 1, Offset: <= 0 } bc)
                    upperArr.Add((us, scope.GetSymbol(bc.ArraySymbolId)));
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

    /// <summary>
    /// True when a non-<see cref="QText"/> value contains a call in a value-producing position. A call in
    /// a measurement target's INDEX is deliberately excluded: <c>M(q[idx()])</c> is still one whole
    /// measurement assignment, and <c>idx()</c> is an ordinary function-valued index checked by
    /// <see cref="CheckIndexedAccess"/>. Calls in array-literal value elements retain the existing QSEM005
    /// rule because that initializer shape has no supported call lowering.
    /// </summary>
    private static bool ContainsCallOutsideMeasurementIndex(QExpr value) => value switch
    {
        QText text => QNodes.ContainsCall(text.Tree),
        QMeasure => false,
        QArrayLiteral literal => literal.Elements.Any(ContainsCallOutsideMeasurementIndex),
        _ => false,
    };

    /// <summary>
    /// Validate every call inside an expression that sits in a VALUE position (a decl/assign RHS, a
    /// condition, a gate argument, a returned value). A <c>function</c> call is a legal value — its argument
    /// count, shape, and scalar types are checked (QSEM006). Anything else has no OpenQASM expression form:
    /// a measurement outside a whole <c>var r: bit = M(q[i]);</c>, an <c>operation</c> (void) call, or an
    /// unknown name → QSEM005.
    /// A resolved user function follows its program symbol's declaring operation Id, the same lookup
    /// <see cref="ExpressionTypes"/> uses for its return type.
    /// </summary>
    private static bool CheckExprCalls(QNode? tree, Scope scope, Ctx ctx, string opName, QSpan? span)
    {
        var allValid = true;
        foreach (var c in QNodes.CallsIn(tree))
        {
            // The same call node can be reached once by the owning value-expression check and again while
            // validating an index nested inside that expression. Its contract has one owner and therefore
            // one diagnostic. Cache by NODE identity (not structural equality), while retaining the verdict
            // so every later consumer still learns that an already-diagnosed call was invalid.
            if (ctx.CallValidity.TryGetValue(c, out var cached))
            {
                allValid &= cached;
                continue;
            }

            var errorCount = ctx.Errors.Count;
            if (QoraGates.Functions.TryGetValue(c.Name, out var builtin))
                CheckBuiltinCall(c, builtin, scope, ctx, opName, span);
            else if (ExpressionTypes.TryGetFunction(c, ctx.ProgramSymbols, ctx.OpById, out var callee))
                // The SAME signature check the statement form runs (a function is an ICallableSig like any
                // callable), so both call forms of one callee agree. Checking only the argument COUNT here let
                // a whole `bit[]` register — or any array — reach a scalar parameter with no diagnostic, while
                // the identical call written as a statement was rejected. Each argument is wrapped as the
                // value-shaped QTextArg it is: an expression-position call has no qubit-operand form.
                CheckCall(callee, c.Args.Select(a => (QArg)new QTextArg(a)).ToList(), "",
                    scope, opName, ctx.Errors, span, ctx, ctx.Floors);
            else if (QoraGates.MeasureLike.Contains(c.Name))
                Add(ctx.Errors, "QSEM005", $"in `{opName}`: a measurement `M(q[i])` can only be a whole assignment (`var r: bit = M(q[i]);`), never part of a larger expression", span);
            else if ((c.CalleeOpId
                      ?? ctx.ProgramSymbols.LookupCallable(c.Name)?.DeclarationNodeId) is int operationId
                     && ctx.OpById.TryGetValue(operationId, out var op) && !op.IsFunction)
                Add(ctx.Errors, "QSEM005", $"in `{opName}`: `{Simple(c.Name)}` is an operation (void) — only a `function` returns a value that an expression can use", span);
            else if (QoraGates.Names.ContainsKey(c.Name))
                Add(ctx.Errors, "QSEM005", $"in `{opName}`: `{c.Name}` is a gate (void) — only a `function` returns a value that an expression can use", span);
            else
                Add(ctx.Errors, "QSEM007", $"in `{opName}`: `{Simple(c.Name)}` is not a known function", span);

            var valid = ctx.Errors.Count == errorCount;
            ctx.CallValidity.Add(c, valid);
            allValid &= valid;
        }
        return allValid;
    }

    /// <summary>
    /// Commit the type contract computed by <see cref="SymbolTableBuilder"/> at the initializer's true
    /// point-of-declaration scope. A malformed call owns the statement first: its QSEM005/006/007 (or more
    /// specific argument diagnostic) explains why the expression is invalid, so a return-type consequence
    /// is withheld until that call is fixed.
    /// </summary>
    private static void CommitTypeMismatch(int statementId, bool callsAreValid, Ctx ctx)
    {
        if (!ctx.TypeMismatchCandidates.Remove(statementId, out var candidate) || !callsAreValid) return;
        ctx.Errors.Add(candidate);
    }

    /// <summary>
    /// A BUILT-IN function call. Its argument shape is deliberately NOT run through the shared
    /// <see cref="IParamSpec"/> path: <c>AsInt</c> takes a WHOLE <c>bit[]</c> register, and a whole register is
    /// precisely the thing that is not a value in any ordinary slot (QSEM036) — there is no value slot that
    /// could describe it. Passing anything else leaves the target lowering with no width to emit, so it is
    /// rejected here, at the source, rather than becoming a QINTERNAL later.
    /// </summary>
    private static void CheckBuiltinCall(QCallNode c, BuiltinFunction fn, Scope scope, Ctx ctx, string opName, QSpan? span)
    {
        if (c.Args.Count != 1)
        {
            Add(ctx.Errors, "QSEM006", $"in `{opName}`: `{c.Name}` expects 1 argument but got {c.Args.Count}", span);
            return;
        }
        if (!fn.TakesBitRegister) return;
        if (c.Args[0] is QNameRef r && scope.Lookup(r.Name) is { Type: QType.Bit, IsArray: true }) return;
        Add(ctx.Errors, "QSEM006", $"in `{opName}`: `{c.Name}` reads a whole `bit[]` register, but `{QNodes.Render(c.Args[0])}` is not one — pass the register itself (`{c.Name}(results)`); a single bit `results[i]` is already a value and needs no conversion", span);
    }

    /// <summary>
    /// Validate a <c>function</c>'s shape: its parameters and return are classical (QSEM034), its body is
    /// pure — no gates, no <c>use</c>, no measurement, no operation calls (QSEM033, a function may only call
    /// other functions) — and it returns a value (QSEM035). Purity is what makes a function call a safe
    /// expression value: with no quantum side effect, its position in an expression carries no ordering.
    /// </summary>
    private static void ValidateFunctionShape(QOperation fn, Ctx ctx)
    {
        foreach (var p in fn.Params)
            if (p.Type == QType.Qubit)
                Add(ctx.Errors, "QSEM034", $"in function `{Simple(fn.Name)}`: parameter `{p.Name}` cannot be a qubit; a function is classical (its parameters and return are `int`/`bit`/`float`/`angle`)", p.Span ?? fn.Span);

        CheckFunctionPurity(fn.Body, fn, ctx);
        if (!ReturnTerminal(fn.Body, fn, ctx) && fn.ReturnType is { } rt)
            Add(ctx.Errors, "QSEM035", $"function `{Simple(fn.Name)}` must return a value of type `{TypeName(rt)}` on every path (an `if` that returns must also cover the other path — with an `else`, or with a `return` after it)", fn.Span);
    }

    /// <summary>Recursively flag any QUANTUM statement in a function body (QSEM033). A statement-form call
    /// to ANOTHER function is allowed (it discards the return); a gate, a functor, an operation call, a
    /// <c>use</c>, or a measurement is not.</summary>
    private static void CheckFunctionPurity(IReadOnlyList<QStmt> body, QOperation fn, Ctx ctx)
    {
        var name = Simple(fn.Name);
        foreach (var s in body)
            switch (s)
            {
                case QUse u:
                    Add(ctx.Errors, "QSEM033", $"in function `{name}`: `use {u.Name} = ...` is not allowed; a function is classical and cannot allocate qubits", u.Span ?? fn.Span);
                    break;
                case QGate g when g.Functors.Count == 0 && ctx.OpByName.TryGetValue(g.Name, out var callee) && callee.IsFunction:
                    break;   // fn -> fn statement call (discards the return) is fine
                case QGate g:
                    Add(ctx.Errors, "QSEM033", $"in function `{name}`: `{Simple(g.Name)}` applies a gate or calls an operation; a function is classical — no gates, no `use`, no measurement, and it may only call other functions", g.Span ?? fn.Span);
                    break;
                case QDecl { Value: QMeasure } d:
                    Add(ctx.Errors, "QSEM033", $"in function `{name}`: a measurement `M(...)` is not allowed; a function is classical", d.Span ?? fn.Span);
                    break;
                case QAssign { Value: QMeasure } a:
                    Add(ctx.Errors, "QSEM033", $"in function `{name}`: a measurement `M(...)` is not allowed; a function is classical", a.Span ?? fn.Span);
                    break;
                case QReturn { Value: QMeasure } r:
                    Add(ctx.Errors, "QSEM033", $"in function `{name}`: a measurement `M(...)` is not allowed; a function is classical", r.Span ?? fn.Span);
                    break;
                case QIf i: CheckFunctionPurity(i.Then, fn, ctx); CheckFunctionPurity(i.Else, fn, ctx); break;
                case QFor f: CheckFunctionPurity(f.Body, fn, ctx); break;
                case QWhile w: CheckFunctionPurity(w.Body, fn, ctx); break;
                case QRepeat rp: CheckFunctionPurity(rp.Body, fn, ctx); break;
            }
    }

    /// <summary>
    /// Whether a block produces a value on EVERY path — the LANGUAGE rule behind QSEM035, true or false
    /// regardless of any target: a function that can reach its end without a <c>return</c> is ill-formed no
    /// matter what runs it. A block always returns iff SOME statement in it is a <c>return</c>, or an
    /// <c>if</c>/<c>else</c> whose branches both always return. A loop never counts — it may run zero times.
    ///
    /// This is deliberately the SAME predicate <see cref="ReturnFlattening"/> uses to decide where a skipped
    /// tail belongs, so a function the validator accepts is exactly one the pass can reshape — but the rule
    /// lives HERE, in the language layer, because it is the language's. The QASM pass reads it; if that pass
    /// is ever removed (a target that runs a <c>return</c> from a nested block needs no reshaping), this check
    /// stays exactly as is.
    /// </summary>
    internal static bool AlwaysReturns(IReadOnlyList<QStmt> body) =>
        body.Any(s => s switch
        {
            QReturn => true,
            QIf i when i.Else.Count > 0 => AlwaysReturns(i.Then) && AlwaysReturns(i.Else),
            _ => false,
        });

    private static bool ReturnTerminal(IReadOnlyList<QStmt> body, QOperation fn, Ctx ctx) => AlwaysReturns(body);

    // QSEM005 — a measurement in a condition IS allowed (MeasureConditionLowering hoists it to a bit before
    // validation), so a call still here is either a FUNCTION call (a legal value) or a non-measurement
    // operation call (rejected). CheckExprCalls sorts them out.
    private static void CheckCondition(QCond cond, Scope scope, Ctx ctx, string opName, QSpan? span)
    {
        if (cond.HasCall) CheckExprCalls(cond.Tree, scope, ctx, opName, span);
    }

    private static void CheckGate(QGate g, Scope scope, Ctx ctx, BoundsCtx bounds = default)
    {
        var opName = ctx.Op.DisplayName ?? ctx.Op.Name;
        var errors = ctx.Errors;

        // A gate argument may contain a FUNCTION call (`Rx(angleOf(k), q[0])` — a classical value); a
        // measurement or operation call there has no OpenQASM form. CheckExprCalls sorts them out.
        foreach (var arg in g.Args)
            if (arg is QTextArg { Tree: { } argTree, HasCall: true })
                CheckExprCalls(argTree, scope, ctx, opName, g.Span);

        // QSEM016/030 — an index must be provably in bounds.
        foreach (var arg in g.Args)
            if (arg is QQubitArg qa)
                CheckIndexedAccess(qa.Reg, qa.Index, scope, opName, ctx, g.Span, bounds);
            else if (arg is QTextArg text)
                CheckTextIndexes(text.Tree, scope, opName, ctx, g.Span, bounds);

        // QSEM014 — the same qubit twice in one gate. Whole registers count: `CNOT(q, q)` broadcasts to
        // duplicate operands, and `CNOT(q, q[0])` overlaps the register with its own element. Indexes are
        // compared by FOLDED VALUE, never by spelling: `CNOT(q[k], q[2])` with `const k: int = 2` is the
        // same qubit twice — the same calculator the bounds prover uses, so no two spellings of one index
        // can slip past as "different" qubits.
        // Each qubit operand's index resolves to a DOMAIN of possible values: a const/literal folds to one
        // point; a loop variable folds to its header range [From..To]; a whole register (Index null) or an
        // unresolved index covers everything / itself. Two operands on one register alias when their domains
        // overlap — so `CNOT(q[i], q[2])` both under `for i in 2..2` (singleton) and under `for i in 0..2`
        // (i reaches 2) is caught, not just literal duplicates — and at most one QSEM014 is reported per gate.
        var refs = g.Args.Select(a => QubitRefOf(a, scope)).Where(r => r is not null)
            .Select(r => (r!.Value.Reg, r.Value.Index,
                Domain: r.Value.Index is { } idx ? IndexDomain(idx, scope, bounds, ctx.WillBeRechecked) : null))
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
            Add(errors, "QSEM004", $"in `{opName}`: a bare measurement statement is not supported: assign the result instead: `var r: bit = {QoraGates.Measurement}(q[i]);`", g.Span);
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
                CheckCall(callee, g.Args, "", scope, opName, errors, g.Span, ctx, ctx.Floors);
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
            CheckCall(gateSig, g.Args, g.Functors.Count > 0 ? string.Join(" ", g.Functors) + " " : "",
                scope, opName, errors, g.Span, ctx);
    }

    /// <summary>
    /// QSEM006 for ANY call — a built-in gate or a user callable — against its <see cref="ICallableSig"/>:
    /// argument count, then per-slot shape and type. A VALUE slot rejects a qubit; a user callable's scalar
    /// slot also applies the shared <see cref="ExpressionTypes.CanAssign"/> conversion contract. A QUBIT slot
    /// rejects a classical, and — for a user op — checks whether the parameter expects one qubit or a whole
    /// <c>Qubit[]</c>. An internally specialized array also checks its concrete size. A built-in gate keeps
    /// its established angle-value rules, while its qubit slots broadcast (a whole register applies
    /// element-wise), so they only require "is a qubit".
    /// Identifiers the caller's scope does not resolve are left alone (treated as not-provably-wrong).
    /// </summary>
    private static void CheckCall(ICallableSig sig, IReadOnlyList<QArg> args, string functorPrefix,
        Scope scope, string opName, List<QoraError> errors, QSpan? span,
        Ctx ctx, ArrayFloorSink? floors = null)
    {
        var calleeName = sig.CalleeName;

        void CheckScalarType(IParamSpec parameter, QText value)
        {
            // Built-in gates deliberately keep their established angle-value rules. A USER callable's
            // declared scalar parameter is a language contract, so it shares the same conversion table as
            // function returns and values receiving function results.
            if (sig.IsBuiltin
                || ExpressionTypes.TypeOf(value, scope, ctx.ProgramSymbols, ctx.OpById) is not
                    { Type: not QType.Qubit } actual)
                return;   // an existing qubit/unknown-expression diagnostic owns this argument

            // A compound expression containing a whole bit[] is already QSEM036, with the explicit AsInt
            // guidance. Other array-shaped expressions have no more specific owner and must fail this scalar
            // call boundary instead of reaching emission.
            if (actual is { IsArray: true, Type: QType.Bit }) return;

            var expected = new QValueShape(parameter.Type, IsArray: false);
            if (ExpressionTypes.CanAssign(expected, actual, value)) return;

            Add(errors, "QSEM006",
                $"in `{opName}`: parameter `{parameter.Name}` of `{calleeName}` expects `{expected}`, " +
                $"but argument `{QNodes.Render(value.Tree)}` is `{actual}`; `{actual}` cannot be implicitly converted to `{expected}`",
                span);
        }

        if (args.Count != sig.Params.Count)
        {
            Add(errors, "QSEM006", $"in `{opName}`: `{functorPrefix}{calleeName}` expects {sig.Params.Count} argument(s) but got {args.Count}", span);
            return;
        }

        // Only slots whose type, visible mode, and borrowing permission all succeed become alias
        // candidates. A failed call therefore does not cascade into QSEM014 on a borrow that was never valid.
        var validArrayAccesses = new List<(IParamSpec Parameter, Symbol Symbol)>();

        for (var i = 0; i < sig.Params.Count; i++)
        {
            var p = sig.Params[i];
            var arg = args[i];
            var argSym = WholeArgumentSymbol(arg, scope);
            var modeMatches = p.Mode == arg.Mode;

            // An inout argument denotes one caller-owned binding, never a temporary, element, or computed
            // expression. Diagnose that malformed borrow once, before comparing it with the formal mode.
            if (arg.Mode == QParameterMode.InOut
                && argSym is not
                   {
                       IsArray: true,
                       Type: QType.Int or QType.Float or QType.Angle
                   })
            {
                Add(errors, "QSEM038", $"in `{opName}`: an `inout` argument of `{calleeName}` must name a whole `int[]`, `float[]`, or `angle[]` binding, not a scalar, bit/qubit value, indexed element, or computed expression", span);
                continue;
            }

            // The declaration and call site must visibly agree. This is checked for every callable,
            // including built-ins: `inout` is a permission transfer, not decorative syntax that may be
            // silently ignored. Expression-position function calls naturally carry ordinary `In` args.
            if (!modeMatches)
                Add(errors, "QSEM038", p.Mode == QParameterMode.InOut
                    ? $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is `inout`; pass its argument as `inout name`"
                    : $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is read-only; remove `inout` from this argument",
                    span);

            if (p.Type != QType.Qubit)
            {
                if (p.IsArray)
                {
                    if (arg is not QTextArg || argSym is not { IsArray: true, Type: { } actualType }
                                             || actualType == QType.Qubit)
                        Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects `{TypeName(p.Type)}[]`, but the argument is not a classical array", span);
                    else if (actualType != p.Type)
                        Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects `{TypeName(p.Type)}[]`, but `{argSym.SourceName}` is `{TypeName(actualType)}[]`", span);
                    else if (!modeMatches)
                    {
                        // The QSEM038 above owns this slot. It cannot establish a valid floor or alias fact.
                    }
                    // Permission checks apply after the visible declaration/call-site modes agree. This keeps
                    // a missing marker a single QSEM038, while a correctly marked const borrow retains the
                    // established, more specific QSEM024 ownership diagnostic.
                    else if (p.Mode == QParameterMode.InOut && argSym.IsConst)
                        Add(errors, "QSEM024", $"in `{opName}`: `{argSym.SourceName}` is a const array and cannot be passed to mutable parameter `{p.Name}` of `{calleeName}`", span);
                    else if (p.Mode == QParameterMode.InOut
                             && argSym is { Kind: SymbolKind.Parameter, ParameterMode: not QParameterMode.InOut })
                        Add(errors, "QSEM038", $"in `{opName}`: parameter `{argSym.SourceName}` is read-only and cannot be forwarded to `inout` parameter `{p.Name}` of `{calleeName}`", span);
                    // Rung B'/P4 — record this array argument as DATA. A `T[]` parameter carries no length of
                    // its own (it arrives with the argument), so the callee's minimum-length requirement can
                    // only be checked at a call. The check itself happens AFTER the walk, against the
                    // requirement table the prover recorded: a known-length argument is a CHECK fact, and
                    // handing our own parameter through is a PROPAGATION fact (the callee's need becomes ours).
                    else
                    {
                        validArrayAccesses.Add((p, argSym));
                        if (floors is not null && sig is QOperation calleeOp)
                            floors.Calls.Add(argSym.ArrayLength is int have
                                ? new ArrayCallFact(floors.CallerOpId, opName, calleeOp.Id, calleeName, p.Name, argSym.SourceName, have, null, span)
                                : new ArrayCallFact(floors.CallerOpId, opName, calleeOp.Id, calleeName, p.Name, argSym.SourceName, null, argSym.SourceName, span));
                    }
                    continue;
                }

                if (argSym is { IsArray: true })
                {
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` expects one `{TypeName(p.Type)}` value, but a whole array was passed", span);
                    continue;
                }

                if (arg is QQubitArg indexed
                    && scope.Lookup(indexed.Reg) is { IsArray: true, Type: not QType.Qubit })
                {
                    CheckScalarType(
                        p,
                        new QText(new QIndexNode(new QNameRef(indexed.Reg), indexed.Index)));
                    continue;
                }
                // VALUE slot (rotation angle, or classical param): a qubit here is wrong. A classical array
                // element was accepted above; a whole qubit or indexed qubit is QSEM006, while a qubit buried
                // inside a classical expression such as `pi / q` is QSEM026.
                if (IsQubitLike(arg, scope) || arg is QQubitArg)
                    Add(errors, "QSEM006", sig.IsBuiltin
                        ? $"in `{opName}`: the first argument of `{calleeName}` is the rotation angle, but a qubit was passed (write `{calleeName}(angle, qubit)`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is `{p.Type.ToString()!.ToLowerInvariant()}`, but a qubit was passed", span);
                else if (arg is QTextArg vt && FirstQubitIn(vt.Tree, scope) is { } qn)
                    Add(errors, "QSEM026", $"in `{opName}`: `{qn}` is a qubit, but `{QNodes.Render(vt.Tree)}` is used as a classical value ({(sig.IsBuiltin ? "the rotation angle" : $"the `{p.Name}` argument")} of `{calleeName}`) — a qubit has no numeric value", span);
                else if (arg is QTextArg scalar)
                    CheckScalarType(p, new QText(scalar.Tree));
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
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit array, but `{qa.Reg}[{QNodes.Render(qa.Index)}]` is a single qubit", span);
                else if (arg is QTextArg tb && !IsQubitArray(argSym))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit register, but `{QNodes.Render(tb.Tree)}` is not one", span);
                else if (p.RegisterSize is int need && IsSizedRegister(argSym, out var have) && have != need)
                    Add(errors, "QSEM006", $"in `{opName}`: internal specialization `{calleeName}` expects {need} qubit(s) for `{p.Name}`, but the argument has {have}", span);
            }
            else
            {
                // single-qubit slot (user op)
                if (arg is QQubitArg indexed && !IsQubit(scope.Lookup(indexed.Reg)))
                    Add(errors, "QSEM006", $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit, but `{indexed.Reg}[{QNodes.Render(indexed.Index)}]` is a classical array element", span);
                else if (arg is QTextArg ta && (IsQubitArray(argSym) || (ta.Tree is { } tt && IsClassicalNode(tt, scope))))
                    Add(errors, "QSEM006", IsQubitArray(argSym)
                        ? $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a single qubit, but `{QNodes.Render(ta.Tree)}` is a whole register (pass `{QNodes.Render(ta.Tree)}[i]`)"
                        : $"in `{opName}`: parameter `{p.Name}` of `{calleeName}` is a qubit, but `{QNodes.Render(ta.Tree)}` is not one", span);
            }
        }

        // Ordinary read-only slots may share storage. Once ANY slot in a same-storage group is `inout`,
        // however, its exclusive mutable borrow cannot coexist with another mutable OR read-only view for
        // the duration of the call. Group by resolved SymbolId, never source spelling: shadowed bindings with
        // the same name are different storage, while every spelling of one binding has the same identity.
        var overlappingArray = validArrayAccesses
            .GroupBy(x => x.Symbol.Id)
            .FirstOrDefault(group => group.Count() > 1
                                     && group.Any(x => x.Parameter.Mode == QParameterMode.InOut));
        if (overlappingArray is not null)
            Add(errors, "QSEM014", $"in `{opName}`: `{calleeName}` receives `{overlappingArray.First().Symbol.SourceName}` through multiple array parameters while at least one is `inout`; an `inout` array cannot overlap another read or write view in the same call", span);
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
        QTextArg t => IsQubit(scope.Lookup(BareName(t) ?? string.Empty)),
        _ => false,
    };

    /// <summary>The bare name an expression argument denotes — a <see cref="QNameRef"/> tree — or null for
    /// a compound expression (which names no single symbol).</summary>
    private static string? BareName(QTextArg arg) => arg.Tree is QNameRef r ? r.Name : null;

    /// <summary>The first name inside a value expression that resolves to a qubit, or null — finds a qubit
    /// smuggled into a classical position (<c>pi / q</c>) that the whole-argument <see cref="IsQubitLike"/>
    /// check misses. A member's base is excluded (<c>q.Count</c> reads classical shape metadata), exactly
    /// as the symbol table's QSEM026 walk excludes it.</summary>
    private static string? FirstQubitIn(QNode? node, Scope scope) => node switch
    {
        QNameRef r when IsQubit(scope.Lookup(r.Name)) => r.Name,
        QUnary u => FirstQubitIn(u.Operand, scope),
        QBinOp b => FirstQubitIn(b.Left, scope) ?? FirstQubitIn(b.Right, scope),
        QIndexNode i => FirstQubitIn(i.Base, scope) ?? FirstQubitIn(i.Index, scope),
        QCallNode c => c.Args.Select(a => FirstQubitIn(a, scope)).FirstOrDefault(x => x is not null),
        _ => null,   // literals; QMember (classical shape metadata)
    };

    /// <summary>True only when the argument provably cannot denote a qubit: a number/expression, or a name
    /// that resolves to a known non-qubit (a classical value, or a register hidden by a local of the same name).</summary>
    private static bool IsDefinitelyNotQubit(QArg arg, Scope scope) => arg switch
    {
        QQubitArg q => scope.Lookup(q.Reg) is { Type: { } t } && t != QType.Qubit,   // `a[0]` where a resolves to an int
        QTextArg { Tree: { } tree } => IsClassicalNode(tree, scope),
        _ => false,
    };

    private static bool IsClassicalNode(QNode node, Scope scope) => node switch
    {
        QNumLit or QLit => true,                                        // numeric/verbatim literal
        QNameRef r => r.Name is "pi" or "tau" or "euler"
            || (scope.Lookup(r.Name) is { Type: { } t } && t != QType.Qubit),
        QCallNode => false,                                             // a call's value is not classifiable here
        _ => true,                                                      // any operator/member/index computation
    };

    private static Symbol? WholeArgumentSymbol(QArg arg, Scope scope) =>
        arg is QTextArg text && BareName(text) is { } name ? scope.Lookup(name) : null;

    private static void CheckExprIndexes(
        QExpr expr,
        Scope scope,
        string opName,
        Ctx ctx,
        QSpan? span,
        BoundsCtx bounds = default)
    {
        switch (expr)
        {
            case QText text:
                CheckTextIndexes(text.Tree, scope, opName, ctx, span, bounds);
                break;
            case QArrayLiteral literal:
                foreach (var element in literal.Elements)
                    CheckExprIndexes(element, scope, opName, ctx, span, bounds);
                break;
            // A measurement's target index must be bounds-checked here too — a measurement NESTED in an
            // array-literal initializer (`var r: bit[] = [M(q[3])]`) reaches this recursion, whereas the QDecl
            // handler's measurement branch only fires for a DIRECT `M(...)` value.
            // An ELEMENT measurement (`M(q[i])`) must have a provably in-range index.
            case QMeasure m when QNodes.IndexOf(m.Target) is { } measuredIndex:
                CheckQubitIndex(
                    new QQubitArg(QNodes.RegOf(m.Target), measuredIndex),
                    scope,
                    opName,
                    ctx,
                    span,
                    bounds);
                break;
            // A WHOLE-reference measurement (`M(a)`) is legal for exactly one shape: a SINGLE qubit. A whole
            // REGISTER would collapse many qubits into one bit, and a classical is not measurable at all —
            // both are QSEM006 (wrong argument for the `M` call), not a silently emitted `measure q;`.
            case QMeasure m:
                var measuredName = QNodes.RegOf(m.Target);
                if (scope.Lookup(measuredName) is { } measuredSym)
                {
                    if (measuredSym.Type != QType.Qubit)
                        Add(ctx.Errors, "QSEM006", $"in `{opName}`: `M({measuredName})` needs a qubit, but `{measuredName}` is `{TypeName(measuredSym.Type ?? QType.Int)}`", span);
                    else if (measuredSym.IsArray)
                        Add(ctx.Errors, "QSEM006", $"in `{opName}`: `M({measuredName})` measures a whole qubit register into one `bit`; measure a single qubit instead (`M({measuredName}[i])`)", span);
                }
                break;
        }
    }

    /// <summary>The disposition of an indexed expression after common semantic validation. A parent index
    /// stops only on <see cref="Invalid"/>: an inner <see cref="Unproven"/> access remains a real target-
    /// policy fact, but does not make the value expression itself malformed.</summary>
    private enum IndexCheckResult { Proven, Unproven, Invalid }

    private static IndexCheckResult MergeIndexResults(IndexCheckResult left, IndexCheckResult right) =>
        left == IndexCheckResult.Invalid || right == IndexCheckResult.Invalid
            ? IndexCheckResult.Invalid
            : left == IndexCheckResult.Unproven || right == IndexCheckResult.Unproven
                ? IndexCheckResult.Unproven
                : IndexCheckResult.Proven;

    private enum DirectLengthIndexResult { AlwaysInBounds, LengthDependent, AlwaysOutOfBounds }

    /// <summary>
    /// Classify a direct same-array index <c>Coeff*Count + Offset</c> over every legal Qora array length
    /// (1..<see cref="int.MaxValue"/>). This is an integer interval test, not a collection of spellings:
    /// it proves both familiar forms (<c>Count-1</c>, <c>Count</c>) and algebraic equivalents such as
    /// <c>2*Count-1</c> or <c>0-Count</c>. <see cref="System.Numerics.BigInteger"/> keeps the proof exact even
    /// when a folded 64-bit coefficient is multiplied by the largest legal array length.
    /// </summary>
    private static DirectLengthIndexResult ClassifyDirectLengthIndex(long coeff, long offset)
    {
        var c = new System.Numerics.BigInteger(coeff);
        var o = new System.Numerics.BigInteger(offset);
        var firstLength = System.Numerics.BigInteger.One;
        var lastLength = new System.Numerics.BigInteger(int.MaxValue);

        // For every L, a valid index satisfies 0 <= cL+o <= L-1. Both sides are linear, so checking their
        // extrema at the two interval endpoints answers whether EVERY legal length is safe.
        var atFirst = c + o;
        var atLast = c * lastLength + o;
        var relativeAtFirst = atFirst;   // index - (L-1), with L=1
        var relativeAtLast = atLast - (lastLength - 1);
        if (System.Numerics.BigInteger.Min(atFirst, atLast) >= 0
            && System.Numerics.BigInteger.Max(relativeAtFirst, relativeAtLast) <= 0)
            return DirectLengthIndexResult.AlwaysInBounds;

        // Otherwise find whether even ONE legal integer length can satisfy both inequalities. Each
        // inequality narrows [1..int.MaxValue]; an empty intersection means the access is wrong for every
        // possible array length, even when it changes from "negative" to "past Count" between lengths.
        var lower = firstLength;
        var upper = lastLength;

        // cL + o >= 0
        if (c > 0)
            lower = System.Numerics.BigInteger.Max(lower, CeilDiv(-o, c));
        else if (c < 0)
            upper = System.Numerics.BigInteger.Min(upper, FloorDiv(o, -c));
        else if (o < 0)
            return DirectLengthIndexResult.AlwaysOutOfBounds;

        // (c-1)L + o <= -1
        var slope = c - 1;
        var rhs = -1 - o;
        if (slope > 0)
            upper = System.Numerics.BigInteger.Min(upper, FloorDiv(rhs, slope));
        else if (slope < 0)
            lower = System.Numerics.BigInteger.Max(lower, CeilDiv(-rhs, -slope));
        else if (rhs < 0)
            return DirectLengthIndexResult.AlwaysOutOfBounds;

        return lower <= upper
            ? DirectLengthIndexResult.LengthDependent
            : DirectLengthIndexResult.AlwaysOutOfBounds;

        static System.Numerics.BigInteger FloorDiv(
            System.Numerics.BigInteger numerator,
            System.Numerics.BigInteger positiveDenominator)
        {
            var quotient = System.Numerics.BigInteger.DivRem(
                numerator,
                positiveDenominator,
                out var remainder);
            return numerator < 0 && remainder != 0 ? quotient - 1 : quotient;
        }

        static System.Numerics.BigInteger CeilDiv(
            System.Numerics.BigInteger numerator,
            System.Numerics.BigInteger positiveDenominator) =>
            -FloorDiv(-numerator, positiveDenominator);
    }

    /// <summary>Bounds-check every indexed access in an expression TREE (see <see cref="QNode"/>). Walking
    /// the tree — not a text regex — finds each <c>base[index]</c> structurally, wherever it is nested, and
    /// returns the combined disposition so an enclosing index can stop after a proven-invalid child.</summary>
    private static IndexCheckResult CheckTextIndexes(
        QNode? tree,
        Scope scope,
        string opName,
        Ctx ctx,
        QSpan? span,
        BoundsCtx bounds = default)
    {
        switch (tree)
        {
            case QIndexNode { Base: QNameRef b } acc:
                var indexed = CheckIndexedAccess(b.Name, acc.Index, scope, opName, ctx, span, bounds);
                if (scope.Lookup(b.Name) is { Type: QType.Qubit })
                {
                    Add(ctx.Errors, "QSEM026", $"in `{opName}`: `{b.Name}[{QNodes.Render(acc.Index)}]` is a qubit and cannot be used as a classical value", span);
                    return IndexCheckResult.Invalid;
                }
                return indexed;
            case QBinOp bin:
                var left = CheckTextIndexes(bin.Left, scope, opName, ctx, span, bounds);
                var right = CheckTextIndexes(bin.Right, scope, opName, ctx, span, bounds);
                return MergeIndexResults(left, right);
            case QUnary u:
                return CheckTextIndexes(u.Operand, scope, opName, ctx, span, bounds);
            case QMember m:
                return CheckTextIndexes(m.Base, scope, opName, ctx, span, bounds);
            case QCallNode c:
                var calls = IndexCheckResult.Proven;
                foreach (var callArg in c.Args)
                    calls = MergeIndexResults(
                        calls,
                        CheckTextIndexes(callArg, scope, opName, ctx, span, bounds));
                return calls;
            default:
                return IndexCheckResult.Proven;
        }
    }

    /// <summary>
    /// An array/register index must be PROVABLY in bounds (rung B'). Proof paths: P1 a literal within a
    /// known length; P2 a loop variable ranging <c>0..a.Count-1</c>; P3 a loop variable with a constant
    /// upper bound K (equivalent to the literal access <c>a[K]</c>); P4 a call-site minimum-length floor for
    /// a classical-array parameter — recorded HERE as data from the same folded value the verdict used
    /// (one calculator; the floor is resolved against every call after the walk); P5 a programmer guard
    /// <c>if (0 &lt;= n &amp;&amp; n &lt; a.Count)</c>. Safety proofs are ALTERNATIVES — any one suffices, none
    /// outranks another — and they are consulted before any wrongness verdict: QSEM016 ("PROVEN out of
    /// bounds") carries the premise that the access actually executes at the offending index, which an
    /// enclosing guard falsifies. Only when no safety proof exists is wrongness judged (QSEM016); failing
    /// both records an <see cref="UnprovenIndex"/>, which OpenQASM later disposes as QSEM030.
    /// </summary>
    private static IndexCheckResult CheckIndexedAccess(
        string name,
        QNode indexNode,
        Scope scope,
        string opName,
        Ctx ctx,
        QSpan? span,
        BoundsCtx bounds = default)
    {
        var errors = ctx.Errors;
        var unproven = ctx.Unproven;
        var deferred = ctx.Deferred;
        var paramNeeds = ctx.ParamNeeds;
        var index = QNodes.Render(indexNode);   // the diagnostic spelling, rendered once from the node
        var errorStart = errors.Count;
        var unprovenStart = unproven.Count;

        // The index is a full expression in EVERY owning context (read, write, gate, measurement). Its
        // independent contracts are all checked before any early exit: nested accesses, function calls,
        // the outer base's array shape, and the whole index's scalar-int type. A malformed child makes only
        // the OUTER bounds question meaningless; it must not hide a sibling call error or a scalar-base/type
        // error that remains true after the child is fixed.
        var nestedResult = CheckTextIndexes(indexNode, scope, opName, ctx, span, bounds);
        var callsValid = !QNodes.ContainsCall(indexNode)
                         || CheckExprCalls(indexNode, scope, ctx, opName, span);

        var symbol = scope.Lookup(name);
        if (symbol is { IsArray: false })
            Add(errors, "QSEM016", $"in `{opName}`: `{name}` is a scalar and cannot be indexed (`{name}[{index}]`)", span);

        // An index must produce one scalar int. This checks the WHOLE expression, so a function returning
        // float/bit/angle or an indexed array value receives the same common type error as a bare variable
        // of that type. An unresolved expression already owns a QSEM005/007/025; do not cascade QSEM030.
        var indexType = ExpressionTypes.TypeOf(indexNode, scope, ctx.ProgramSymbols, ctx.OpById);
        var indexTypeValid = indexType is { Type: QType.Int, IsArray: false };
        if (indexType is not null && !indexTypeValid)
        {
            Add(errors, "QSEM016",
                $"in `{opName}`: `{name}[{index}]` needs one classical integer index, but `{index}` is `{indexType}`",
                span);
        }

        // Only the DERIVATIVE outer bounds proof stops after an invalid child/contract. The independent
        // checks above have already reported everything that can be decided without evaluating this index.
        if (symbol is null
            || !symbol.IsArray
            || !callsValid
            || !indexTypeValid
            || nestedResult == IndexCheckResult.Invalid)
            return IndexCheckResult.Invalid;

        IndexCheckResult Result(IndexCheckResult own = IndexCheckResult.Proven)
        {
            if (own == IndexCheckResult.Invalid || errors.Count > errorStart)
                return IndexCheckResult.Invalid;
            if (own == IndexCheckResult.Unproven
                || nestedResult == IndexCheckResult.Unproven
                || unproven.Count > unprovenStart)
                return IndexCheckResult.Unproven;
            return IndexCheckResult.Proven;
        }

        // The index name resolved ONCE, through the scope chain, to the variable it actually denotes HERE.
        // Every fact lookup below keys on this symbol — a same-named variable elsewhere is a different key.
        // (A numeric-literal index names nothing to resolve.)
        var idxSym = indexNode is QNameRef idxName ? scope.Lookup(idxName.Name) : null;

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

        // The OTHER outcome a size question can have (see <see cref="DeferredSizeCheck"/>): the verdict is
        // postponed to the post-monomorphization re-validation, and the postponement itself is recorded as
        // DATA — every `return`-on-a-promise below goes through here, so no deferral is ever silent.
        void Defer(string reason) =>
            deferred.Add(new DeferredSizeCheck(opName, name, $"{name}[{index}]", reason, span));

        // P1 — a literal index. Known length: bounds-check now. Unknown length: a Qubit[] parameter defers to
        // the post-mono re-validation (its size becomes concrete); a classical parameter records its P4 floor.
        // (A digit run too large for long lowers to a verbatim QLit — a fortiori past any length.)
        if (indexNode is QNumLit numLit)
        {
            if (length is int lit && numLit.Value >= lit)
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is out of range; `{name}` has {lit} element(s) (valid: 0..{lit - 1})", span);
            else if (length is null && symbol.Type != QType.Qubit)
                RequireArgLength(numLit.Value);
            else if (length is null)
                Defer($"literal index {numLit.Value} awaits `{name}`'s specialized size");
            return Result(length is null && symbol.Type == QType.Qubit
                ? IndexCheckResult.Unproven
                : IndexCheckResult.Proven);
        }
        if (indexNode is QLit)
        {
            if (length is int lit)
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is out of range; `{name}` has {lit} element(s) (valid: 0..{lit - 1})", span);
            else if (symbol.Type != QType.Qubit)
                RequireArgLength(long.MaxValue);
            else
                Defer($"literal index `{index}` awaits `{name}`'s specialized size");
            return Result(length is null && symbol.Type == QType.Qubit
                ? IndexCheckResult.Unproven
                : IndexCheckResult.Proven);
        }

        // P5 FIRST — safety proofs are alternatives, and a guard is sufficient ON ITS OWN: the access only
        // EXECUTES when `0 <= index && index < name.Count` held, so no other fact can make it unsafe. The
        // loop verdict below is a WRONGNESS proof, and wrongness proofs carry a premise — "the access
        // executes at the loop's maximum" — that an enclosing guard falsifies. So wrongness may only be
        // judged when no safety proof exists; asking in the other order rejected the clamp idiom
        // `for i in 0..5 { if (0 <= i && i < a.Count) { a[i] } }` as "PROVEN out of bounds" (it never is)
        // and recorded a P4 floor for an access whose guard makes ANY argument length safe.
        if (bounds.Guarded(idxSym, symbol, length)) return Result();

        // A ByConst guard `index < K` on a MONO-SIZED parameter (the Symbol.MonoSized stamp — the
        // monomorphizer's own trigger, copied once from QParam.NeedsMonoSizing) can't be confirmed
        // against the (unknown) length yet, but post-monomorphization the size is concrete, so defer,
        // exactly as a literal index does (a guard proving a strict subset of a deferred access must not
        // be rejected where the literal is accepted). The `index < q.Count` form is length-independent
        // and already proved just above. int[]/float[]/angle[] never specialize — no deferral for them.
        if (length is null && symbol.MonoSized && bounds.HasConstGuard(idxSym))
        {
            Defer($"const guard on `{index}` awaits `{name}`'s specialized size");
            return Result(IndexCheckResult.Unproven);
        }

        // P1 extended — a non-literal index that FOLDS to a definite value (a const name) IS the literal
        // access at that value: same calculator, same verdicts. Sits after P5 because an enclosing guard
        // would keep an out-of-range access from ever executing.
        var foldedIndex = BoundFolder.Fold(indexNode, scope);
        if (foldedIndex is BoundNum idxVal)
        {
            if (idxVal.Value < 0)
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is {idxVal.Value} — negative, out of range for any array", span);
            else if (length is int len && idxVal.Value >= len)
                Add(errors, "QSEM016", $"in `{opName}`: index `{name}[{index}]` is {idxVal.Value}, out of range; `{name}` has {len} element(s) (valid: 0..{len - 1})", span);
            else if (length is null && symbol.Type != QType.Qubit)
                RequireArgLength(idxVal.Value);
            else if (length is null)
                Defer($"index `{index}` (= {idxVal.Value}) awaits `{name}`'s specialized size");
            return Result(length is null && symbol.Type == QType.Qubit
                ? IndexCheckResult.Unproven
                : IndexCheckResult.Proven);   // in range, or a Qubit[] parameter (post-mono re-check)
        }

        // A direct access relative to the SAME array's unknown length is classified over the whole legal
        // length domain. This proves algebraic forms, not just `Count-1` / `Count` spellings. A result that
        // depends on a Qubit[] size is deferred to specialization; a classical parameter never acquires a
        // concrete size in a later common pass, so its size-dependent result remains unproven below.
        if (foldedIndex is ArrayLengthBound direct
            && direct.ArraySymbolId == symbol.Id
            && direct.IsOverflowFree)
        {
            switch (ClassifyDirectLengthIndex(direct.Coeff, direct.Offset))
            {
                case DirectLengthIndexResult.AlwaysInBounds:
                    return Result();
                case DirectLengthIndexResult.AlwaysOutOfBounds:
                    Add(errors, "QSEM016",
                        $"in `{opName}`: index `{name}[{index}]` cannot be in range for any valid length of `{name}` (valid: 0..{name}.Count-1)",
                        span);
                    return Result();
            }
        }

        // A symbolic length belonging to an unsized Qubit[] becomes a number after monomorphization. This
        // covers both same-register `q[q.Count-2]` and cross-register `q[r.Count-1]`; the post-mono common
        // validation then gives the ordinary exact in-range/QSEM016 verdict for the specialized sizes.
        if (BoundFolder.DefersToUnsizedQubit(foldedIndex, scope))
        {
            Defer($"index `{index}` has a `.Count`-relative value, awaits specialized qubit-array sizes");
            return Result(IndexCheckResult.Unproven);
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
                return Result();
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
                            else if (length is null)
                                Defer($"loop `{index} in {from}..{to}` reaches index {t.Value}, awaits `{name}`'s specialized size");
                        }
                        return Result(length is null && symbol.Type == QType.Qubit && t.Value >= f.Value
                            ? IndexCheckResult.Unproven
                            : IndexCheckResult.Proven);   // in range, empty, or a Qubit[] parameter (post-mono re-check)

                    // P2 generalized — a SAME-array bound `Count + C` is judged for ANY length:
                    //   C <= -1 → max index <= Count-1: safe however long the array turns out to be
                    //   C >=  0 → reaches index Count or beyond: out of range for EVERY length
                    case ArrayLengthBound c when c.IsOverflowFree
                                                       && c.ArraySymbolId == symbol.Id
                                                       && c.Coeff == 1:
                        if (c.Offset <= -1) return Result();
                        Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` reaches index `{to}` — at or past `{name}.Count`, out of range for any length (valid: 0..{name}.Count-1)", span);
                        return Result();

                    // `k*Count + C` with k >= 2, C >= -1 exceeds Count-1 for every length >= 1.
                    case ArrayLengthBound c when c.IsOverflowFree
                                                       && c.ArraySymbolId == symbol.Id
                                                       && c.Coeff >= 2
                                                       && c.Offset >= -1:
                        Add(errors, "QSEM016", $"in `{opName}`: `{name}[{index}]` reaches index `{to}`, out of range for any length of `{name}`", span);
                        return Result();
                }
            }

            // A Qubit[] parameter's `.Count` becomes a concrete size after specialization — defer this access
            // to the post-monomorphization validation pass, exactly like an unknown-length literal index.
            if (fact.DefersToMono)
            {
                Defer($"loop `{index} in {from}..{to}` has a `.Count`-relative bound, awaits the specialized size");
                return Result(IndexCheckResult.Unproven);
            }
            // otherwise the bound does not settle → unproven → rejection below
        }

        // No proof exists. Recorded as DATA, not as a diagnostic — the failed proof is target-independent;
        // the OpenQASM policy pass later derives QSEM030 from <see cref="SemanticModel.UnprovenIndexes"/>.
        // A future runtime-capable backend will need both a checked-access lowering and stable site identity
        // before it can consume this category as a rewrite work list.
        // Blame the bound that actually failed to settle: when From never folded, naming To would accuse
        // the wrong bound (and the fix hint would send the user to the wrong place).
        unproven.Add(new UnprovenIndex(opName, name, index,
            !inLoop ? null : fact.FromB is null ? from : to, span));
        return Result(IndexCheckResult.Unproven);
    }

    /// <summary>Validate and bounds-check the full index expression of one measurement target.</summary>
    private static void CheckQubitIndex(
        QQubitArg q,
        Scope scope,
        string opName,
        Ctx ctx,
        QSpan? span,
        BoundsCtx bounds = default)
    {
        if (scope.Lookup(q.Reg) is not { } resolved)
            return; // the symbol-table walk already owns the unknown-name diagnostic

        CheckIndexedAccess(q.Reg, q.Index, scope, opName, ctx, span, bounds);
        if (resolved.Type != QType.Qubit)
            Add(ctx.Errors, "QSEM006", $"in `{opName}`: measurement target `{q.Reg}[{QNodes.Render(q.Index)}]` is not a qubit", span);
    }

    private static string TypeName(QType type) => type.ToString().ToLowerInvariant();

    /// <summary>The set of values a qubit index can take, as an inclusive <c>[Lo..Hi]</c> range: a const or
    /// literal folds to a single point; a loop variable to its header range, and when only the lower bound
    /// settles for a genuinely runtime upper bound to <c>[From..long.MaxValue]</c> — the loop still starts at
    /// From on its guaranteed first iteration, so an operand equal to a reachable value provably aliases.
    /// Null means unresolvable (compared by spelling). A loop bound that DEFERS to monomorphization (a
    /// <c>Qubit[].Count</c> upper) is also null here: it becomes concrete post-mono, so the aliasing check
    /// re-runs then with the real range — over-approximating it to <c>[From..MaxValue]</c> pre-mono would
    /// falsely alias a fixed operand (the fan-in idiom <c>for i in 0..q.Count-2 { CNOT(q[i], q[last]) }</c>).
    /// Used for QSEM014 operand-aliasing: two operands collide when their ranges intersect.</summary>
    private static (long Lo, long Hi)? IndexDomain(QNode idx, Scope scope, BoundsCtx bounds, bool willBeRechecked)
    {
        if (BoundFolder.Fold(idx, scope) is BoundNum v) return (v.Value, v.Value);
        if (idx is QNameRef nr && scope.Lookup(nr.Name) is { } sym && bounds.LoopRange(sym, out var f) && f.FromB is BoundNum a)
        {
            if (f.ToB is BoundNum b) return a.Value <= b.Value ? (a.Value, b.Value) : null;  // settled range (empty -> GateNeverRuns)
            // A symbolic Qubit[].Count upper bound: defer aliasing to the precise post-mono re-check —
            // but ONLY when that re-check will actually run (the op has a call site). An uncalled generic
            // op is dropped by monomorphization, so its deferral would be a silent skip: judge it now with
            // the conservative over-approximation instead.
            if (f.DefersToMono) return willBeRechecked ? null : (a.Value, long.MaxValue);
            return (a.Value, long.MaxValue);                        // genuinely runtime upper: reachable set is at least [From, ...]
        }
        return null;
    }

    /// <summary>True when a gate operand's index is a loop variable whose header range is PROVABLY EMPTY
    /// (From &gt; To, both settled): the loop body never runs, so the gate is never emitted and no operand
    /// aliasing is possible — QSEM014 must skip it entirely (not fall to a spelling comparison that would
    /// reject a never-executed <c>CNOT(q[i], q[i])</c> while accepting the equally-dead <c>CNOT(q[i], q[2])</c>).</summary>
    private static bool GateNeverRuns(IEnumerable<(string Reg, QNode? Index, (long, long)? Domain)> refs, Scope scope, BoundsCtx bounds) =>
        refs.Any(r => r.Index is QNameRef idx && scope.Lookup(idx.Name) is { } sym && bounds.LoopRange(sym, out var f)
            && f.FromB is BoundNum a && f.ToB is BoundNum b && a.Value > b.Value);

    private static (string Reg, QNode? Index)? QubitRefOf(QArg arg, Scope scope) => arg switch
    {
        // Only a QUBIT-based reference is a gate operand for aliasing purposes. `x[0]` where `x` is a
        // classical array parses to the same (reg, index) shape but is a classical value — passing it twice
        // is fine, so it must not count as a duplicate qubit operand (QSEM014).
        QQubitArg q when IsQubit(scope.Lookup(q.Reg)) => (q.Reg, q.Index),
        QTextArg t when BareName(t) is { } name && IsQubit(scope.Lookup(name)) => (name, (QNode?)null),
        _ => null,
    };

    private static string Show(string reg, QNode? index) => index is null ? reg : $"{reg}[{QNodes.Render(index)}]";

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
            // A FUNCTION call lives in an expression (a value/condition/argument/return), not a QGate — so a
            // recursive function (`function f(x) { return f(x); }`) has its edge only here. Every call node
            // in every direct expression site is a call edge for the cycle check.
            foreach (var tree in QNodes.ExpressionSites(stmt))
                foreach (var call in QNodes.CallsIn(tree))
                    if (ops.Contains(call.Name)) into.Add(call.Name);

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
