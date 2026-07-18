namespace Qora.Ir.Passes;

/// <summary>
/// One qubit operand as effect analysis sees it. <see cref="Index"/> null means the WHOLE register —
/// a broadcast (<c>H(q)</c>), a loop-variable index (<c>q[i]</c>), or any index not known at analysis
/// time is conservatively blanketed to the full register.
/// </summary>
public readonly record struct QubitRef(string Reg, int? Index)
{
    public override string ToString() => Index is int i ? $"{Reg}[{i}]" : Reg;

    /// <summary>Do these two references name overlapping physical qubit(s)? True when they share a register
    /// AND either side is the WHOLE register (null index) or they name the same element. This is the
    /// subsumption rule that lets a whole-register effect <c>{q}</c> cover an element query <c>{q[0]}</c> —
    /// and, symmetrically, lets an element effect <c>{q[0]}</c> answer a whole-register query <c>{q}</c>
    /// (any part of q being touched means q, as a whole, is touched).</summary>
    public bool Overlaps(QubitRef other) =>
        Reg == other.Reg && (Index is null || other.Index is null || Index == other.Index);
}

/// <summary>The role a qubit plays at one statement: <see cref="Read"/> — referenced but its
/// computational-basis value is preserved (a control, or a diagonal-gate target); <see cref="Write"/> —
/// its value may change (a gate target, a reset, or a <c>use</c> register's birth into |0…0⟩);
/// <see cref="Measure"/> — collapsed by a measurement (irreversible).</summary>
public enum QubitEventKind { Read, Write, Measure }

/// <summary>
/// One qubit event: a single LEAF statement's action on ONE qubit, in the operation's program order — the
/// use/def stream rung ② (liveness) and rung ③ (qfree) consume. <see cref="Order"/> is a per-operation
/// program-order index (the largest Order on a qubit = its last use = its liveness death point).
/// <see cref="StmtId"/> is the leaf statement's stable Id, so events sharing a StmtId are the qubits that
/// interacted AT that statement — entanglement edges are read off by grouping on it. Only leaf statements
/// (gates, measurements, <c>use</c>) emit events; containers hold none of their own — their children carry
/// the precise per-gate detail.
/// <para><see cref="Irreversible"/> marks a touch the inverse CANNOT undo — a <c>reset</c> (a non-unitary
/// gate) or a call whose body transitively measures/resets — since such a touch cannot be recovered by
/// replaying U†. It is the one bit NOT derivable from <see cref="Kind"/> alone: a reset lands as a
/// <see cref="QubitEventKind.Write"/> indistinguishable from a unitary write, so its irreversibility must be
/// recorded here. A measurement stays the separate <see cref="QubitEventKind.Measure"/> and does NOT set this
/// flag; rung ③ qfree treats a qubit as un-uncomputable when any of its events is a Measure OR sets Irreversible.</para>
/// <para><see cref="NonQfree"/> marks a <see cref="QubitEventKind.Write"/> that cannot be cleanly undone by
/// whole-statement adjoint injection when its target is entangled — for one of TWO reasons: the gate created a
/// genuine SUPERPOSITION (<c>H</c>, <c>Rx</c>, <c>Ry</c>), or it is a PHASE PERMUTATION carrying a
/// basis-value-dependent phase (<c>Y</c>, <c>CY</c>) whose relative phase across survivor branches the
/// injected adjoint would strip (or a call that transitively does either). This is the SECOND thing not
/// derivable from <see cref="Kind"/> alone — a non-qfree write and a phase-free classical permutation (X, CNOT,
/// SWAP) are both just a Write — and it is the decisive uncompute clause: an ancilla with any such write cannot
/// be auto-uncomputed (whereas a phase-free permutation is a reversible function of live sources the adjoint
/// undoes). Always false on Read/Measure events and on a <c>use</c> register's |0…0⟩ birth.</para>
/// <para><see cref="NodeId"/> is the second KEY the event carries (the first is <see cref="StmtId"/> into the
/// IR): it points into the operation's <see cref="QubitGraph"/> — for a Write/Measure, the value-version NODE
/// this event created (1:1); for a Read, the version that was read (the source's then-current node). Time and
/// roles live here; relations (parents, versions) live on the node; structure lives in the IR.</para>
/// </summary>
public sealed record QubitEvent(
    QubitRef Qubit, QubitEventKind Kind, int Order, int StmtId, bool Irreversible, bool NonQfree,
    int NodeId);

/// <summary>
/// One operation's effect on its FORMAL qubit parameters (locals allocated by <c>use</c> are op-private
/// and excluded — they are the ancilla candidates a later liveness pass hunts for).
/// <see cref="Irreversible"/> is true when the body (transitively) measures or resets.
/// <see cref="ParamModifiedNonQfree"/> ⊆ <see cref="ParamModified"/> is the params whose value the body
/// writes NON-QFREE — an <c>H</c>/<c>Rx</c>/<c>Ry</c> superposition write or a <c>Y</c>/<c>CY</c>
/// phase-permutation write, transitively — so a caller can tell a qfree helper (whose param-writes are
/// phase-free permutations) from one that is not, and stamp only the latter's projected writes
/// <c>NonQfree</c>.
/// <see cref="ParamMeasured"/> ⊆ <see cref="ParamModified"/> is the params the body (transitively) MEASURES —
/// so a call site can stamp the projected event <see cref="QubitEventKind.Measure"/> and a register measured
/// through a helper is recognized as measured (not an ancilla candidate), same as a direct <c>M</c>.
/// </summary>
public sealed record OpEffectSummary(
    IReadOnlySet<QubitRef> ParamTouched,
    IReadOnlySet<QubitRef> ParamModified,
    IReadOnlySet<QubitRef> ParamModifiedNonQfree,
    IReadOnlySet<QubitRef> ParamMeasured,
    bool Irreversible);

/// <summary>One parent edge of a <see cref="QubitNode"/>: the value was made from node <see cref="NodeId"/>,
/// accessed THROUGH the reference <see cref="Via"/>. Via keeps the access BREADTH: a loop-blanketed read
/// (<c>a[i]</c> → whole-<c>a</c>) may resolve to a precise element's version node, but the dependency is on
/// the WHOLE register (any element might have been the one read), so liveness must be checked against Via,
/// not the node's own (possibly narrower) ref.</summary>
public readonly record struct QubitEdge(int NodeId, QubitRef Via);

/// <summary>One node of the QUBIT GRAPH: one VALUE VERSION of one qubit — the value a write (or a
/// measurement's collapse, or a <c>use</c> birth) left in it. <see cref="Parents"/> are the versions this
/// value was MADE FROM: the qubit's own previous version, the versions read as sources at the same
/// statement, and — conservatively — the previous versions of any co-written partners (gate-level analysis
/// cannot tell which co-written operand a value flowed from, so all are parents). The graph is a DAG (a
/// value's ancestry — parents precede children in time; several lineages may merge into one node, so it is
/// not a tree). RELATIONS ONLY live here: time/role/flags stay on the linked <see cref="QubitEvent"/>
/// (1:1 via <see cref="QubitEvent.NodeId"/> for Write/Measure events), structure stays in the IR — one fact,
/// one home. <see cref="IsParamSeed"/> marks the initial "value from outside" version of a qubit parameter
/// (no creating statement, no parents).</summary>
public sealed record QubitNode(
    int Id,
    QubitRef Qubit,
    int Version,
    IReadOnlyList<QubitEdge> Parents,
    bool IsParamSeed);

/// <summary>
/// One operation's qubit graph — the value-genealogy DAG built by <see cref="EffectAnalysis"/> WITH the event
/// stream, by the same hand in the same pass (the analyzer records relations at the moment it knows them,
/// instead of consumers re-deriving them from the flat timeline — the re-derivation is where three
/// adversarially-confirmed soundness holes lived). Frozen after analysis; coherence with the event stream is
/// enforced by a pipeline sweep (<see cref="EffectAnalysis"/> throws QINTERNAL-style on any mismatch), so a
/// divergent graph can never reach a consumer silently.
/// </summary>
public sealed class QubitGraph
{
    private readonly List<QubitNode> _nodes = new();
    private readonly Dictionary<string, int> _paramSeedByReg = new();
    private readonly Dictionary<string, int> _versionByReg = new();

    public IReadOnlyList<QubitNode> Nodes => _nodes;
    public QubitNode Node(int id) => _nodes[id];

    /// <summary>The initial "value from outside" node of a qubit parameter register, if any.</summary>
    public int? ParamSeed(string reg) => _paramSeedByReg.TryGetValue(reg, out var id) ? id : null;

    internal int AddSeed(string reg)
    {
        var id = AddNodeCore(new QubitRef(reg, null), System.Array.Empty<QubitEdge>(), isParamSeed: true);
        _paramSeedByReg[reg] = id;
        return id;
    }

    internal int AddNode(QubitRef qubit, IReadOnlyList<QubitEdge> parents) => AddNodeCore(qubit, parents, isParamSeed: false);

    private int AddNodeCore(QubitRef qubit, IReadOnlyList<QubitEdge> parents, bool isParamSeed)
    {
        _versionByReg.TryGetValue(qubit.Reg, out var v);        // version = per-register sequence (informational)
        _versionByReg[qubit.Reg] = v + 1;
        var node = new QubitNode(_nodes.Count, qubit, v, parents, isParamSeed);
        _nodes.Add(node);
        return node.Id;
    }
}

/// <summary>Why a qubit is NOT safely auto-uncomputable — <see cref="None"/> means it is safe. Two values are
/// RUNG-1 rulings relayed by <see cref="SemanticModel.UncomputeSafety"/> rather than safety clauses of its own:
/// <see cref="NotACleanupCandidate"/> (not an ancilla at all — a caller-owned parameter or an unknown name) and
/// <see cref="Measured"/> (an ancilla promoted to OUTPUT — its value was delivered, and a collapse has no
/// unitary inverse either; the culprit is the measuring event). The rest are one value per safety clause:
/// <see cref="Irreversible"/> breaks clause 1 (reversible history), <see cref="NonQfreeWrite"/> breaks clause 2 (qfree compute),
/// <see cref="NotInvertibleCall"/> breaks the invertibility clause (the write is a call to a user operation whose
/// body the <see cref="Inverter"/> cannot statement-adjoint — a <c>while</c>/<c>repeat</c> of unknown count,
/// classical mutation, or a local <c>use</c>, transitively; the value is reversible in principle but no
/// straight-line cleanup can be synthesized, so it is conservatively unsafe — the culprit is the call event),
/// <see cref="ContainedWrite"/> breaks clause 3 (unconditional compute: a write sitting inside a container —
/// an <c>if</c> runs it conditionally, a loop runs it repeatedly, and a within/apply conjugation already
/// replays its own W† (another adjoint would RE-compute the restored value) — so replaying a straight-line
/// adjoint at the death point would not mirror what actually executed; the if case is lifted later by
/// conditional-inverse support, which additionally needs classical condition-bit flow — NOT in the event
/// stream, known gap #11),
/// <see cref="CoWrittenPartner"/> and <see cref="SourceDied"/> break clause 4 (well-sourced compute: a
/// statement that writes q must READ its other qubits, and those sources must stay unchanged until q dies).
/// <see cref="NotAnalyzed"/> means the operation has no recorded event stream (effect analysis never ran on
/// it — a semantic-error abort, or a synthesized op) — reported instead of a vacuous "safe".</summary>
public enum UncomputeBlocker { None, NotACleanupCandidate, Measured, Irreversible, NonQfreeWrite, NotInvertibleCall, ContainedWrite, CoWrittenPartner, SourceDied, NotAnalyzed }

/// <summary>The rung-③ safety verdict for one qubit. <see cref="Blocker"/> names the failed clause
/// (<see cref="UncomputeBlocker.None"/> = safe to auto-uncompute); <see cref="Culprit"/> is the offending
/// event — its <see cref="QubitEvent.StmtId"/> ties the reason to the exact statement, so a consumer can
/// render "blocked by the H at …" (the <c>--stages</c> uncompute view now, rung ④'s diagnostics later).
/// Culprit is null when safe, for <see cref="UncomputeBlocker.NotAnalyzed"/> (no events to point at), and for
/// <see cref="UncomputeBlocker.NotACleanupCandidate"/> (there is no offending event — the qubit was never an
/// ancilla); <see cref="UncomputeBlocker.Measured"/> carries the first measuring event.</summary>
public sealed record UncomputeVerdict(UncomputeBlocker Blocker, QubitEvent? Culprit)
{
    public bool IsSafe => Blocker == UncomputeBlocker.None;
}

/// <summary>An indexed access whose in-bounds proof FAILED (rung B′): the bound never settles to a value at
/// compile time, so the access is neither proven safe nor proven wrong. Recorded by <see cref="QoraValidator"/>
/// as DATA, not as a diagnostic — the verdict is target-independent, only its disposition differs per backend:
/// the OpenQASM backend derives one QSEM030 per entry (OpenQASM 3 has no runtime failure channel, so an
/// unproven access cannot ship), while a QIR backend would instead wrap each site in a runtime bounds check
/// that aborts. <see cref="LoopBound"/> is the undetermined loop upper bound when <see cref="Index"/> is a
/// <c>for</c> variable, and null when the index is a bare runtime value.</summary>
public sealed record UnprovenIndex(string Op, string Array, string Index, string? LoopBound, QSpan? Span);

/// <summary>
/// The PERSISTENT semantic side table: everything <see cref="SymbolTableBuilder"/> proved during the final
/// validation, keyed by stable node <see cref="QStmt.Id"/>s and carried to the END of the pipeline instead
/// of being rebuilt on demand (Roslyn's SemanticModel / rust-analyzer's AstId-keyed queries, in miniature).
/// Passes that run AFTER the model is built never invalidate it: rewrites via <c>with</c> keep the node's
/// Id, and passes that COPY subtrees (<see cref="ConjugationLowering"/>, <see cref="AdjointMaterializer"/>)
/// register each copy's lineage through <see cref="RecordDerivation"/>, so a lookup on a copied node walks
/// the derivation chain back to the node the model actually saw. <see cref="EffectAnalysis"/> stores its
/// per-operation qubit-event stream (the use/def timeline) and per-operation summary here too, and <see cref="NameMangler"/> records each
/// declaration's EMITTED name — so the model holds both name domains explicitly: the SOURCE name (what the
/// user wrote, <see cref="Symbol.SourceName"/>, frozen at validation) and the emitted name (what the QASM
/// says, <see cref="FindEmittedName"/>, written at mangling). Each fact has exactly ONE producer pass;
/// facts are only ever added, never rewritten.
/// </summary>
public sealed class SemanticModel
{
    private readonly Dictionary<int, Scope> _rootScopeByOp = new();     // QOperation.Id → root scope
    private readonly Dictionary<int, Symbol> _symbolByDeclId = new();   // declaring node Id → Symbol
    private readonly Dictionary<int, int> _derivedFrom = new();         // copied node Id → source node Id
    private readonly Dictionary<int, IReadOnlyList<QubitEvent>> _qubitEventsByOp = new(); // QOperation.Id → program-ordered qubit-event stream
    private readonly Dictionary<int, QubitGraph> _qubitGraphByOp = new();   // QOperation.Id → value-genealogy DAG
    private readonly Dictionary<int, OpEffectSummary> _effectSummaryByOpId = new(); // QOperation.Id → summary
    private readonly Dictionary<int, string> _emittedNameByDeclId = new(); // declaring node Id → emitted (post-mangling) name
    private readonly HashSet<int> _nonInvertibleCallStmts = new();      // StmtIds of CALL statements whose callee the Inverter cannot invert
    private readonly List<UnprovenIndex> _unprovenIndexes = new();      // rung B′: accesses whose bounds proof never settled
    private readonly Dictionary<int, IReadOnlyDictionary<string, long>> _requiredArgLengthsByOp = new(); // rung B′/P4: op → classical-array param → min length
    private Scope? _programScope;   // the top-level symbol table: one Operation symbol per op

    internal void AddOperation(QOperation op, Scope root)
    {
        _rootScopeByOp[op.Id] = root;
        foreach (var sym in root.AllSymbols())
            if (sym.DeclNodeId != 0) _symbolByDeclId[sym.DeclNodeId] = sym;
    }

    /// <summary>Register the PROGRAM scope — the top-level symbol table whose entries are the operation
    /// symbols. One call stores it (for the symbol view / by-name lookup) AND flattens its symbols into the
    /// Id→Symbol index (so <see cref="FindSymbol"/> resolves <c>op.Id</c>), exactly as
    /// <see cref="AddOperation"/> does for a per-operation scope. The operation symbols enter that scope
    /// through the same <see cref="Scope.TryAdd"/> door every other declaration uses — nothing registers a
    /// symbol by a side path.</summary>
    internal void SetProgramScope(Scope programScope)
    {
        _programScope = programScope;
        foreach (var sym in programScope.AllSymbols())
            if (sym.DeclNodeId != 0) _symbolByDeclId[sym.DeclNodeId] = sym;
    }

    /// <summary>Store an operation's qubit-event stream — its leaf statements' reads/writes/measures in
    /// program order, keyed by <c>op.Id</c>. The single producer is <see cref="EffectAnalysis"/>, exactly
    /// ONCE: facts are add-only, so a second analysis of the same op would silently REPLACE what earlier
    /// consumers already read — fail loud instead (a future post-injection re-analysis needs generation-keyed
    /// storage, registered as a known design gap).</summary>
    internal void AddQubitEvents(int opId, IReadOnlyList<QubitEvent> events)
    {
        if (!_qubitEventsByOp.TryAdd(opId, events))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an event stream — re-analysis would silently replace add-only facts");
    }

    /// <summary>Store an operation's qubit graph — recorded by the SAME producer in the same pass as the
    /// event stream, coherence-swept before it lands here. Add-only, like the stream.</summary>
    internal void AddQubitGraph(int opId, QubitGraph graph)
    {
        if (!_qubitGraphByOp.TryAdd(opId, graph))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has a qubit graph — re-analysis would silently replace add-only facts");
    }

    /// <summary>Record the CALL statements (by stable node <see cref="QStmt.Id"/>) whose callee the
    /// <see cref="Inverter"/> cannot invert — recorded by <see cref="EffectAnalysis"/> alongside the event stream
    /// (co-populated, same pass, so rung ③ never sees events without this companion fact). Keyed by StmtId, NOT by
    /// operation NAME: monomorphization rewrites a generic call's name (<c>Loop</c> → <c>Loop__sz2</c>) while
    /// PRESERVING the node Id, so a name test would miss the block whenever rung ③ is handed the pre-mono tree.
    /// StmtIds are shared across the pre-mono and analyzed trees, so <see cref="UncomputeSafety"/> answers
    /// correctly whichever it is given. Rung ③ reads this to refuse certifying an ancilla whose write is a call it
    /// cannot actually uncompute — keeping the safety verdict and the Inverter (the single authority) in agreement.</summary>
    internal void RecordNonInvertibleCallStmts(IEnumerable<int> stmtIds)
    {
        foreach (var id in stmtIds) _nonInvertibleCallStmts.Add(id);
    }

    /// <summary>Record one unproven indexed access (rung B′) — produced by <see cref="QoraValidator"/> during
    /// the bounds-proof walk, add-only like every other fact. The backend decides the disposition: the
    /// OpenQASM path derives QSEM030 from each entry; a QIR path would lower each to a checked access.</summary>
    internal void AddUnprovenIndex(UnprovenIndex access) => _unprovenIndexes.Add(access);

    /// <summary>Record an operation's array-argument CONTRACT (rung B′/P4): the minimum length each of its
    /// classical-array parameters requires, settled after call-graph propagation. Single producer
    /// (<see cref="QoraValidator"/>, once per validation), add-only like every other fact.</summary>
    internal void SetRequiredArgLengths(int opId, IReadOnlyDictionary<string, long> needs)
    {
        if (!_requiredArgLengthsByOp.TryAdd(opId, needs))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an array-argument contract — re-validation would silently replace add-only facts");
    }

    /// <summary>The operation's array-argument contract — parameter name → minimum required length — or null
    /// when the op demands nothing. The call-site QSEM016s are DERIVED from this table; consumers (signature
    /// help, docs, backends) can read the same contract.</summary>
    public IReadOnlyDictionary<string, long>? RequiredArgLengths(int opId) =>
        _requiredArgLengthsByOp.TryGetValue(opId, out var needs) ? needs : null;

    /// <summary>Every indexed access this validation could not prove in bounds, in walk order — empty when
    /// the whole program is proven. Non-empty NEVER coexists with a successful OpenQASM compile (each entry
    /// became a QSEM030); a future QIR backend reads this list as its runtime-check insertion plan.</summary>
    public IReadOnlyList<UnprovenIndex> UnprovenIndexes => _unprovenIndexes;

    /// <summary>The operation's value-genealogy graph (see <see cref="QubitNode"/>), or null when the op was
    /// never analyzed — same key discipline as <see cref="QubitEvents"/> (no derivation walk: a synthesized
    /// inverse's genealogy is not its source's).</summary>
    public QubitGraph? Graph(int opId) => _qubitGraphByOp.TryGetValue(opId, out var g) ? g : null;
    internal void AddOpEffects(int opId, OpEffectSummary s)
    {
        if (!_effectSummaryByOpId.TryAdd(opId, s))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an effect summary — re-analysis would silently replace add-only facts");
    }

    /// <summary>Register that node <paramref name="freshId"/> is a copy of <paramref name="sourceId"/>.
    /// The (source, fresh) order matches <see cref="ReId"/>'s record callback, so a model can be passed
    /// to it directly as <c>model.RecordDerivation</c>.</summary>
    public void RecordDerivation(int sourceId, int freshId) => _derivedFrom[freshId] = sourceId;

    /// <summary>Record the name a declaration EMITS as — written by <see cref="NameMangler"/>, the one pass
    /// that owns the source-name → emitted-name mapping. Recorded for every declared name (operations,
    /// parameters, registers, variables, loop variables), renamed or not, so a null lookup MEANS "the
    /// mangler has not seen this node", never "unchanged".</summary>
    internal void RecordEmittedName(int declNodeId, string name) => _emittedNameByDeclId[declNodeId] = name;

    /// <summary>The symbol DECLARED by this node (or by the node it was copied from), if any.</summary>
    public Symbol? FindSymbol(int nodeId) => Resolve(nodeId, _symbolByDeclId);

    /// <summary>The root scope of this operation (or of the operation it was derived from), if any.</summary>
    public Scope? FindRootScope(int opId) => Resolve(opId, _rootScopeByOp);

    /// <summary>This operation's qubit-event stream (leaf reads/writes/measures in program order), or an
    /// empty list if the model never analyzed this op. Keyed by <c>op.Id</c> directly — events are emitted
    /// pre-copy, and deliberately NOT resolved through the derivation chain: a synthesized inverse
    /// (<c>Foo__adj</c>) replays its source's gates in reverse, so the source's stream (its Orders, its
    /// windows) would be a LIE for it. Use <see cref="WasEffectAnalyzed"/> to tell "analyzed, zero events"
    /// from "never analyzed".</summary>
    public IReadOnlyList<QubitEvent> QubitEvents(int opId) =>
        _qubitEventsByOp.TryGetValue(opId, out var e) ? e : System.Array.Empty<QubitEvent>();

    /// <summary>Did <see cref="EffectAnalysis"/> actually run on this op Id? False for an op the analysis
    /// never saw — a semantic-error abort, a synthesized inverse, a still-generic def. The safety queries
    /// refuse to answer "safe" from an absent stream (that would be vacuous truth, not analysis).</summary>
    public bool WasEffectAnalyzed(int opId) => _qubitEventsByOp.ContainsKey(opId);

    /// <summary>Rung ② liveness, DERIVED (nothing stored): the <c>[Birth, Death]</c> events bracketing
    /// <paramref name="q"/>'s life inside operation <paramref name="opId"/>. Birth is its earliest event
    /// (Order-min — a <c>use</c> register's birth <see cref="QubitEventKind.Write"/>, or a parameter's first
    /// use); Death is its latest event (Order-max — its LAST use, of ANY kind: a final control Read counts,
    /// since the qubit still holds a value that must be cleaned after it). Death is the point after which
    /// rung ④ may inject an uncompute — for a death INSIDE a container the realizable injection point is
    /// after the outermost enclosing container, which is why <see cref="UncomputeSafety"/> extends its
    /// source-liveness window there. Null when the qubit has no events in the op (never used).
    /// Subsumption-aware via <see cref="QubitRef.Overlaps"/> (a whole-register birth covers an element
    /// query). This is just min/max over rung ①'s Order — liveness is a query, not a stored pass.</summary>
    public (QubitEvent Birth, QubitEvent Death)? LiveRange(int opId, QubitRef q)
    {
        QubitEvent? birth = null, death = null;
        foreach (var e in QubitEvents(opId))
        {
            if (!e.Qubit.Overlaps(q)) continue;
            if (birth is null || e.Order < birth.Order) birth = e;
            if (death is null || e.Order > death.Order) death = e;
        }
        return birth is null ? null : (birth, death!);
    }

    /// <summary>Is qubit <paramref name="q"/> an ANCILLA in operation <paramref name="opId"/> — a local
    /// <c>use</c> workspace register (NOT a formal parameter/input, which is caller-owned data)? This is the
    /// literature-standard definition (2026-07 cross-check: Q#'s <c>use</c>, Quipper's <c>with_ancilla</c>,
    /// Qiskit's <c>AncillaRegister</c>, Bennett's scratch): a locally-owned temporary workspace born in the
    /// KNOWN state |0…0⟩. Being an ancilla is a matter of BIRTH; what happened to it afterwards (measured or
    /// not) does not change it — that is <see cref="IsCleanupCandidate"/>'s question. Answered from the
    /// analysis facts, where the birth distinction is already first-class: a parameter register carries a
    /// param SEED node (a value from outside), a <c>use</c> register carries its hoisted |0…0⟩ birth in the
    /// event stream — so this answers identically for source-compiled and hand-built IR. False for a
    /// parameter, an unknown name, or an op effect analysis never ran on (no facts ⇒ never a vacuous yes).</summary>
    public bool IsAncilla(int opId, QubitRef q)
    {
        if (Graph(opId) is not { } g) return false;      // never analyzed ⇒ cannot certify the birth
        if (g.ParamSeed(q.Reg) is not null) return false; // caller-owned: the value came from outside
        foreach (var e in QubitEvents(opId))
            if (e.Qubit.Reg == q.Reg) return true;        // recorded history ⇒ use-born (births are hoisted)
        return false;                                     // unknown name
    }

    /// <summary>Is qubit <paramref name="q"/> a CLEANUP CANDIDATE in operation <paramref name="opId"/> — an
    /// <see cref="IsAncilla"/> whose value was never delivered to anyone (never measured, directly or
    /// transitively through calls)? Ancilla-ness is the BIRTH question; this is the LIVENESS question layered
    /// on top: a measured ancilla was promoted to an OUTPUT (by the deferred-measurement principle it is an
    /// output wire), so it leaves the cleanup pool — the uncompute rungs draw from what remains. In today's
    /// Qora (void operations, no aliasing, no closures) measurement is the ONLY channel a local register's
    /// value can escape through, so ancilla + never-measured exactly captures "workspace nobody needs"
    /// (literature-verified; future features add conditions — docs/TODO #16). Whether a candidate is actually
    /// SAFE to auto-uncompute is the further rung-③ question (<see cref="UncomputeSafety"/>). DERIVED, nothing
    /// stored: delegates the birth question to <see cref="IsAncilla"/> (graph facts — param seed vs use-born)
    /// and scans the op's event stream for a measurement, subsumption-aware via
    /// <see cref="QubitRef.Overlaps"/> (a measured element disqualifies its whole register). False for a
    /// non-ancilla or when effect analysis never ran (no stream ⇒ cannot certify "never measured" — never a
    /// vacuous yes).</summary>
    public bool IsCleanupCandidate(int opId, QubitRef q)
    {
        if (!WasEffectAnalyzed(opId)) return false;
        if (!IsAncilla(opId, q)) return false;
        foreach (var e in QubitEvents(opId))
            if (e.Kind == QubitEventKind.Measure && e.Qubit.Overlaps(q)) return false;
        return true;
    }

    /// <summary>Rung ③ — the full safety verdict for auto-uncomputing qubit <paramref name="q"/> in operation
    /// <paramref name="opId"/>. SAFE means: injecting the adjoint of the statements that wrote q (whole
    /// statements, reverse order, the <c>use</c> birth never replayed) at q's death point yields, in every
    /// measurement branch, exactly the no-injection state with q coherently replaced by |0⟩ (up to a
    /// branch-global phase). That is the declared return semantics — a program whose LATER interference
    /// depended on q's leftover entanglement behaves differently BY DESIGN (removing that dependence is what
    /// the rule is for); what safety guarantees is that no surviving qubit's own value/history is rewritten.
    /// This is the SAFETY half of the uncompute decision — combine with <see cref="IsCleanupCandidate"/>,
    /// which says whether q is scratch worth uncomputing at all. DERIVED, nothing stored: four clauses a
    /// Silq-semantics + state-vector verification established — clauses 1-2 read off the event stream,
    /// clause 3 off the IR via <see cref="ContainerMap"/>, clause 4 off the qubit graph's recorded parent
    /// edges —
    /// PRECONDITION, enforced as a verdict: safety is asked about CLEANUP CANDIDATES — measurement is rung 1's
    /// ruling (<see cref="IsCleanupCandidate"/>: value delivered ⇒ output), relayed here as
    /// <see cref="UncomputeBlocker.Measured"/> (or <see cref="UncomputeBlocker.NotACleanupCandidate"/> for a
    /// non-ancilla) before any clause runs — never re-judged inside the scan. The clauses themselves:
    /// <list type="number">
    /// <item>REVERSIBLE — no event of q carries <c>Irreversible</c> (a reset, or a call that resets/measures,
    /// destroys the value the adjoint would replay).</item>
    /// <item>QFREE COMPUTE — no <see cref="QubitEventKind.Write"/> of q carries <c>NonQfree</c>: neither an
    /// H/Rx/Ry write (which injects a fresh superposition the adjoint cannot fold back once a surviving qubit
    /// has recorded it) NOR a Y/CY phase-permutation write (whose basis-value-dependent phase becomes a
    /// survivor-relative phase under entanglement that the injected adjoint would strip — state-vector
    /// verified, round 5; matches Silq's qfree excluding Y).</item>
    /// <item>UNCONDITIONAL COMPUTE — no <see cref="QubitEventKind.Write"/> of q sits inside a CONTAINER
    /// (<c>if</c> / <c>for</c> / <c>while</c> / <c>repeat</c> / conjugation). The event stream is a flat
    /// timeline that walks each container body once, so a contained write runs conditionally (an <c>if</c>)
    /// or repeatedly (a loop) at runtime — a straight-line adjoint replayed at the death point would not
    /// mirror what actually executed. Structure is not in the events; it is read from the IR via
    /// <see cref="ContainerMap"/> — which is why this query takes the OPERATION, not just its Id. (Reads
    /// inside containers are harmless: the adjoint replays the write chain only.) Lifted later by
    /// conditional-inverse support — which additionally requires proving the CONDITION BIT unchanged between
    /// the compute and the injected inverse; classical-bit flow is NOT in the event stream (known gap #11 of
    /// the requirements table).</item>
    /// <item>WELL-SOURCED COMPUTE — a statement that writes q must only READ its other qubits (a co-WRITTEN
    /// partner — a SWAP operand, a call modifying two params — blocks outright: the injected adjoint would
    /// rewrite that partner at q's death, and no window scan can see the partner's uses AFTER q dies).
    /// Additionally, a Write of q WIDER than q — a whole-register broadcast or a blanket projected call under
    /// an ELEMENT query — writes sibling elements inside ONE event and blocks as
    /// <see cref="UncomputeBlocker.CoWrittenPartner"/> too: the statement-level adjoint cannot be sliced to
    /// q. The register's <c>use</c> birth is exempt (the only parentless write node — enforced QINTERNAL-loud
    /// at construction), sound because the injected adjoint never replays an allocation. Every parent EDGE of
    /// q's write (the graph's recorded sources) must then not be value-changed (Written/Measured) between
    /// that write and q's death, so q stays a function of still-present, unchanged sources the adjoint can
    /// invert against. Only edges FULLY COVERED by q (same register, and q is the whole register or names the
    /// same element) are exempt as q's own chain — a same-register BLANKET source under an element query
    /// covers sibling elements too, so it is a real source and its liveness is scanned (adversarially
    /// confirmed).</item>
    /// </list>
    /// The verdict names the failed clause and carries the offending event, whose <see cref="QubitEvent.StmtId"/>
    /// lets a consumer point at the exact statement (the <c>--stages</c> uncompute view now, rung ④'s
    /// diagnostics later). An op with no recorded stream answers <see cref="UncomputeBlocker.NotAnalyzed"/> —
    /// never a vacuous "safe". Conservative (may reject a safe case, never admits an unsafe one). The
    /// source-liveness clause is sound under the dependency-respecting (LIFO) uncompute-injection order rung ④
    /// must use, injecting at the death point — or, when the death sits inside a container, immediately AFTER
    /// the outermost enclosing container (the window extension above makes the verdict honest for that
    /// placement). Matches Silq's <c>qfree</c> on the deciding case: a basis permutation carrying a
    /// basis-value-dependent phase (Y, CY) is REJECTED — under entanglement the injected adjoint strips a
    /// survivor-relative phase the documented result keeps (state-vector verified, round 5; an earlier
    /// "broader than Silq — Y allowed" claim was a confirmed bug). Diagonal phase gates (Z/S/T/CZ) stay safe:
    /// they never write a value (all-Read), so they are not qfree writes at all.</summary>
    public UncomputeVerdict UncomputeSafety(QOperation op, QubitRef q)
    {
        if (!WasEffectAnalyzed(op.Id)) return new(UncomputeBlocker.NotAnalyzed, null);

        // CLASSIFICATION BEFORE SAFETY, in code: safety is a question about CLEANUP CANDIDATES only —
        // the candidacy ruling (measurement included, and it outranks EVERY scan clause below, Irreversible
        // included) is delivered first. Whether q is a candidate is
        // rung 1's ruling (IsCleanupCandidate — measurement means the value was DELIVERED, an output), so
        // it is delegated there, never re-judged as a scan clause here: one concept, one home. It is
        // delivered as a VERDICT rather than a silent precondition, so a direct caller can never receive
        // "safe" for a measured ancilla (whose collapse also has no unitary inverse) or a caller-owned input.
        if (!IsCleanupCandidate(op.Id, q))
        {
            if (!IsAncilla(op.Id, q)) return new(UncomputeBlocker.NotACleanupCandidate, null);
            foreach (var m in QubitEvents(op.Id))
                if (m.Kind == QubitEventKind.Measure && m.Qubit.Overlaps(q))
                    return new(UncomputeBlocker.Measured, m);          // measured ancilla — promoted to output
            throw new System.InvalidOperationException(                 // an analyzed ancilla loses candidacy
                $"QINTERNAL: `{q}` lost cleanup candidacy for no recorded reason");   // only by measurement
        }

        var events = QubitEvents(op.Id);
        var containers = ContainerMap.Build(op);   // structure lives in the IR, not the flat event timeline

        // clauses (a) reversible + (b) qfree compute + (b′) unconditional compute — one scan of q's own
        // events, also finding its death. The stream is program-ordered, so the culprit is the FIRST offender.
        QubitEvent? death = null;
        foreach (var e in events)
        {
            if (!e.Qubit.Overlaps(q)) continue;
            // FAIL LOUD on a foreign tree: every event was emitted from a statement of the ANALYZED op, so an
            // Id the map lacks means the caller passed a different tree (a later with-rewritten copy) — reading
            // that silently as "not contained" would be an unsafe default inside the safety checker.
            if (!containers.TryGetValue(e.StmtId, out var chain))
                throw new System.InvalidOperationException(
                    $"UncomputeSafety: statement {e.StmtId} of `{op.Name}`'s event stream is not in the tree passed — pass the operation effect analysis ran on (QoraParseResult.AnalyzedIr), not a later rewritten copy");
            if (e.Irreversible) return new(UncomputeBlocker.Irreversible, e);                          // (a) lossy touch
            if (e.Kind == QubitEventKind.Write && e.NonQfree)
                return new(UncomputeBlocker.NonQfreeWrite, e);                                    // (b) superposition (H/Rx/Ry) or phase-permutation (Y/CY)
            if (e.Kind == QubitEventKind.Write && _nonInvertibleCallStmts.Contains(e.StmtId))
                return new(UncomputeBlocker.NotInvertibleCall, e);                                // (b″) write is a call the Inverter cannot invert (while/repeat/mutation/use in the callee) — keyed by StmtId, tree-independent
            if (e.Kind == QubitEventKind.Write && chain.Count > 0)
                return new(UncomputeBlocker.ContainedWrite, e);                                        // (b′) contained write
            if (death is null || e.Order > death.Order) death = e;
        }
        if (death is null) return new(UncomputeBlocker.None, null);   // never used ⇒ nothing to uncompute

        // CONTAINED DEATH (adversarially found): when q's death sits INSIDE a container, the realizable
        // injection point is AFTER the outermost container enclosing it (injecting inside would make the
        // uncompute conditional/repeated), so sources must survive to the END of that container — the
        // window's upper bound extends from the death to the container's last event.
        var windowEnd = death.Order;
        if (containers[death.StmtId] is { Count: > 0 } deathChain)
        {
            var outermost = deathChain[0];
            foreach (var e in events)
                if (e.Order > windowEnd
                    && containers.TryGetValue(e.StmtId, out var c) && c.Contains(outermost))
                    windowEnd = e.Order;
        }

        // clause (c) well-sourced compute — answered by the QUBIT GRAPH: a write's sources are its node's
        // recorded PARENT EDGES (written down by the analyzer at the moment it knew them — never re-derived
        // from the flat timeline, which is where three adversarially-confirmed holes lived).
        //   · CO-WRITTEN partner (a Write/Measure sibling at the same statement not fully covered by q —
        //     a SWAP-style value move / a call modifying both): block outright — the injected adjoint rewrites
        //     that partner at q's death, and its later uses can sit AFTER q's death where no window reaches.
        //   · each parent edge not fully covered by q is a SOURCE: it must still be the CURRENT version of its
        //     access ref (Edge.Via keeps a blanketed read's conservative breadth) through (write.Order,
        //     windowEnd], or q is no longer a function of it and the adjoint inverts against the wrong value.
        //     Edges fully covered by q are q's own chain, which the adjoint itself replays.
        var graph = Graph(op.Id)!;   // guaranteed by the WasEffectAnalyzed guard above
        foreach (var write in events)
        {
            if (write.Kind != QubitEventKind.Write || !write.Qubit.Overlaps(q)) continue;

            // A write of q WIDER than q (a blanket write under an element query — a broadcast, an opaque
            // call) writes SIBLING ELEMENTS inside this one event: the statement-level adjoint cannot be
            // sliced down to q, so replaying it would rewrite the other elements too (adversarially
            // verified: it deterministically flips a measured sibling). Blocked as a co-written partner —
            // of q's own register-mates. The `use` birth is exempt: it is the only write whose node has NO
            // parents — an invariant ENFORCED at construction (Stamp throws QINTERNAL on any other
            // parentless write) — and rung ④ never replays an allocation.
            if (q.Index is not null && write.Qubit.Index is null
                && graph.Node(write.NodeId).Parents.Count > 0)
                return new(UncomputeBlocker.CoWrittenPartner, write);

            foreach (var sib in events)
            {
                if (sib.StmtId != write.StmtId || ReferenceEquals(sib, write)) continue;
                if (sib.Kind == QubitEventKind.Read) continue;
                if (sib.Qubit.Reg == q.Reg && (q.Index is null || sib.Qubit.Index == q.Index)) continue;
                return new(UncomputeBlocker.CoWrittenPartner, sib);
            }

            foreach (var edge in graph.Node(write.NodeId).Parents)
            {
                if (edge.Via.Reg == q.Reg && (q.Index is null || edge.Via.Index == q.Index)) continue;   // q's own chain
                foreach (var later in events)
                {
                    if (later.Kind == QubitEventKind.Read || !later.Qubit.Overlaps(edge.Via)) continue;
                    if (later.Order <= write.Order || later.Order > windowEnd) continue;   // window (write, windowEnd]
                    return new(UncomputeBlocker.SourceDied, later);
                }
            }
        }
        return new(UncomputeBlocker.None, null);
    }

    /// <summary>Rung ③ as a plain bool — <see cref="UncomputeSafety"/> without the reason. The injector
    /// (rung ④) needs only this; the views/diagnostics read the full verdict.</summary>
    public bool IsSafelyUncomputable(QOperation op, QubitRef q) => UncomputeSafety(op, q).IsSafe;

    /// <summary>This operation's effect summary (or its derivation source's), if any.</summary>
    public OpEffectSummary? FindOpEffects(int opId) => Resolve(opId, _effectSummaryByOpId);

    /// <summary>The name this declaration EMITS as in the final QASM (post-mangling). Null means the
    /// mangler has not run over this node — NOT "same as source". The source name stays untouched on
    /// <see cref="Symbol.SourceName"/>; the two are different name domains, each with one home.</summary>
    public string? FindEmittedName(int nodeId) => Resolve(nodeId, _emittedNameByDeclId);

    /// <summary>Every operation root scope in the model, keyed by operation Id.</summary>
    public IReadOnlyDictionary<int, Scope> RootScopes => _rootScopeByOp;

    /// <summary>The program-level symbol table — the scope whose symbols are the operations themselves
    /// (kind <see cref="SymbolKind.Operation"/>). Null until an operation-bearing program is validated.</summary>
    public Scope? ProgramScope => _programScope;

    private T? Resolve<T>(int id, Dictionary<int, T> table) where T : class
    {
        // Fresh Ids are minted after their sources, so a derivation chain strictly decreases — no cycles.
        while (true)
        {
            if (table.TryGetValue(id, out var v)) return v;
            if (!_derivedFrom.TryGetValue(id, out id)) return null;
        }
    }
}
