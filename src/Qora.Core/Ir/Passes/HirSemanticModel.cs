using Qora.Compiler;

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
/// <para><see cref="Irreversible"/> marks a touch that a future MIR cleanup transformation CANNOT undo — a
/// <c>reset</c> (a non-unitary gate) or a call whose body transitively measures/resets. It is the one bit NOT
/// derivable from <see cref="Kind"/> alone: a reset lands as a
/// <see cref="QubitEventKind.Write"/> indistinguishable from a unitary write, so its irreversibility must be
/// recorded here. A measurement stays the separate <see cref="QubitEventKind.Measure"/> and does NOT set this
/// flag; rung ③ qfree treats a qubit as un-uncomputable when any of its events is a Measure OR sets Irreversible.</para>
/// <para><see cref="NonQfree"/> marks a <see cref="QubitEventKind.Write"/> that cannot be cleanly reversed as
/// a whole statement when its target is entangled — for one of TWO reasons: the gate created a
/// genuine SUPERPOSITION (<c>H</c>, <c>Rx</c>, <c>Ry</c>), or it is a PHASE PERMUTATION carrying a
/// basis-value-dependent phase (<c>Y</c>, <c>CY</c>) whose relative phase across survivor branches a cleanup
/// reversal would strip (or a call that transitively does either). This is the SECOND thing not
/// derivable from <see cref="Kind"/> alone — a non-qfree write and a phase-free classical permutation (X, CNOT,
/// SWAP) are both just a Write — and it is the decisive uncompute clause: an ancilla with any such write cannot
/// be auto-uncomputed (whereas a phase-free permutation is a reversible function of live sources). Always
/// false on Read/Measure events and on a <c>use</c> register's |0…0⟩ birth.</para>
/// <para><see cref="NodeId"/> is the second KEY the event carries (the first is <see cref="StmtId"/> into the
/// IR): it points into the operation's <see cref="QubitGraph"/> — for a Write/Measure, the value-version NODE
/// this event created (1:1); for a Read, the version that was read (the source's then-current node). Time and
/// roles live here; relations (parents, versions) live on the node; structure lives in the IR.</para>
/// </summary>
public sealed record QubitEvent(
    QubitRef Qubit,
    QubitEventKind Kind,
    int Order,
    HirNodeId StmtId,
    bool Irreversible,
    bool NonQfree,
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
    bool Irreversible)
{
    private IReadOnlySet<QubitRef> _paramTouched = HirCollections.FreezeSet(ParamTouched);
    private IReadOnlySet<QubitRef> _paramModified = HirCollections.FreezeSet(ParamModified);
    private IReadOnlySet<QubitRef> _paramModifiedNonQfree =
        HirCollections.FreezeSet(ParamModifiedNonQfree);
    private IReadOnlySet<QubitRef> _paramMeasured = HirCollections.FreezeSet(ParamMeasured);

    public IReadOnlySet<QubitRef> ParamTouched
    {
        get => _paramTouched;
        init => _paramTouched = HirCollections.FreezeSet(value);
    }

    public IReadOnlySet<QubitRef> ParamModified
    {
        get => _paramModified;
        init => _paramModified = HirCollections.FreezeSet(value);
    }

    public IReadOnlySet<QubitRef> ParamModifiedNonQfree
    {
        get => _paramModifiedNonQfree;
        init => _paramModifiedNonQfree = HirCollections.FreezeSet(value);
    }

    public IReadOnlySet<QubitRef> ParamMeasured
    {
        get => _paramMeasured;
        init => _paramMeasured = HirCollections.FreezeSet(value);
    }
}

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
    bool IsParamSeed)
{
    private IReadOnlyList<QubitEdge> _parents = HirCollections.Freeze(Parents);

    public IReadOnlyList<QubitEdge> Parents
    {
        get => _parents;
        init => _parents = HirCollections.Freeze(value);
    }
}

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

    public IReadOnlyList<QubitNode> Nodes => HirCollections.Freeze(_nodes);
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
/// RUNG-1 rulings relayed by <see cref="HirSemanticModel.UncomputeSafety"/> rather than safety clauses of its own:
/// <see cref="NotACleanupCandidate"/> (not an ancilla at all — a caller-owned parameter or an unknown name) and
/// <see cref="Measured"/> (an ancilla promoted to OUTPUT — its value was delivered, and a collapse has no
/// unitary inverse either; the culprit is the measuring event). The rest are one value per safety clause:
/// <see cref="Irreversible"/> breaks clause 1 (reversible history), <see cref="NonQfreeWrite"/> breaks clause 2
/// (qfree compute), and <see cref="ContainedWrite"/> breaks clause 3 (unconditional compute: a write sitting
/// inside a branch or loop cannot be treated as an unconditional straight-line event),
/// <see cref="CoWrittenPartner"/> and <see cref="SourceDied"/> break clause 4 (well-sourced compute: a
/// statement that writes q must READ its other qubits, and those sources must stay unchanged until q dies).
/// <see cref="NotAnalyzed"/> means the operation has no recorded event stream (effect analysis never ran on
/// it — a semantic-error abort, or a synthesized op) — reported instead of a vacuous "safe".</summary>
public enum UncomputeBlocker { None, NotACleanupCandidate, Measured, Irreversible, NonQfreeWrite, ContainedWrite, CoWrittenPartner, SourceDied, NotAnalyzed }

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
/// as DATA, not as a diagnostic — the verdict is target-independent, only its disposition differs per backend.
/// The OpenQASM backend derives QSEM030 because an unproven access cannot ship. <see cref="UnprovenIndex.Site"/>
/// is the exact <see cref="HirIndexExpression.Id"/> that HIR-to-MIR lowering translates into an
/// instruction reference;
/// diagnostic strings are never used as rewrite keys. <see cref="UnprovenIndex.LoopBound"/> is the
/// undetermined loop upper bound when <see cref="UnprovenIndex.Index"/> is a
/// <c>for</c> variable, and null for any other runtime index expression.</summary>
public sealed record UnprovenIndex(
    HirNodeId Site,
    string Op,
    string Array,
    string Index,
    string? LoopBound,
    SourceSpan? Span);

/// <summary>One outstanding "re-check after monomorphization" PROMISE (the deferral ledger): a size-dependent
/// judgement the validator postponed because <see cref="Array"/> is a parameter whose length only
/// specialization can supply (<see cref="HirParameter.NeedsMonoSizing"/>). On the pipeline that runs the
/// specialize/re-validate pair, every entry is resolved there or MOOTED there — an uncalled generic op is
/// DROPPED by the Monomorphizer, so its promises die with the op (no size ever exists to judge against;
/// the code is never emitted) — so the FINAL model's ledger is empty and
/// the list means "promises still outstanding". A future backend that SKIPS the pair (full QIR: dynamic
/// arrays) reads this ledger as its work list, wrapping each site in a runtime bounds check — without it,
/// the postponed judgements would silently evaporate on any no-specialization path. (Deferred ALIASING
/// precision — QSEM014's post-mono domain re-check — is NOT ledgered yet: a no-pair backend must decide
/// its own distinctness policy before it can dispose of those.)</summary>
public sealed record DeferredSizeCheck(string Op, string Array, string Access, string Reason, SourceSpan? Span);

/// <summary>
/// The persistent semantic side table produced for one exact HIR generation. Cross-generation node
/// lineage belongs to Compilation's HIR graph, and emitted names belong to each target artifact. This
/// model therefore contains only HIR facts: scopes, symbols, validation results, and the temporary
/// HIR-native quantum analysis that remains until the MIR cleanup planner replaces it.
/// </summary>
public sealed class HirSemanticModel
{
    private static readonly IReadOnlyDictionary<ScopeId, Scope> EmptyScopes =
        HirCollections.Freeze(new Dictionary<ScopeId, Scope>());
    private static readonly IReadOnlyDictionary<HirNodeId, Scope> EmptyRootScopes =
        HirCollections.Freeze(new Dictionary<HirNodeId, Scope>());

    /// <summary>
    /// Validation-owned facts shared by the validation snapshot and its effect-analysis fork. Effect
    /// analysis never writes this state; its dictionaries live directly on each model instance below.
    /// Grouping the facts makes the sharing boundary explicit and prevents a fork from copying a second,
    /// drift-prone symbol/scope truth.
    /// </summary>
    private sealed class ValidationFacts
    {
        internal readonly List<UnprovenIndex> UnprovenIndexes = new();
        internal readonly HashSet<HirNodeId> UnprovenIndexSites = new();
        internal readonly List<DeferredSizeCheck> DeferredSizeChecks = new();
        internal readonly Dictionary<HirNodeId, bool> WillBeRecheckedByCallable = new();
        internal readonly Dictionary<HirNodeId, IReadOnlyDictionary<string, long>>
            RequiredArgLengthsByCallable = new();
        internal HirScopeGraph? ScopeGraph;
        internal HirValidationOutcome? Outcome;
        internal bool ProducerCompleted;
        internal bool IsSealed;
    }

    private readonly Dictionary<HirNodeId, IReadOnlyList<QubitEvent>>
        _qubitEventsByCallable = new();
    private readonly Dictionary<HirNodeId, QubitGraph>
        _qubitGraphByCallable = new();
    private readonly Dictionary<HirNodeId, OpEffectSummary>
        _effectSummaryByCallableId = new();
    private readonly ValidationFacts _validation;
    private readonly HirSnapshot? _sourceSnapshot;
    private readonly HirSemanticPhase _artifactPhase;
    private bool _effectAnalysisCompleted;
    private bool _effectsSealed;

    /// <summary>
    /// Creates detached mutable facts for focused pass tests. A detached model can never be published as a
    /// <see cref="HirSemanticArtifact"/>; production validation must use the snapshot-bound constructor.
    /// </summary>
    internal HirSemanticModel()
        : this(
            sourceSnapshot: null,
            HirSemanticPhase.Validation,
            new ValidationFacts())
    {
    }

    internal HirSemanticModel(HirSnapshot sourceSnapshot)
        : this(
            sourceSnapshot ?? throw new ArgumentNullException(nameof(sourceSnapshot)),
            HirSemanticPhase.Validation,
            new ValidationFacts())
    {
    }

    private HirSemanticModel(
        HirSnapshot? sourceSnapshot,
        HirSemanticPhase artifactPhase,
        ValidationFacts validation)
    {
        if (!Enum.IsDefined(artifactPhase))
            throw new ArgumentOutOfRangeException(
                nameof(artifactPhase),
                artifactPhase,
                "unknown HIR semantic phase");

        _sourceSnapshot = sourceSnapshot;
        _artifactPhase = artifactPhase;
        _validation = validation;
    }

    /// <summary>
    /// Proves that this model was allocated for one exact HIR snapshot and semantic phase. Snapshot IDs are
    /// deliberately insufficient because a detached tree may reuse an otherwise valid ID.
    /// </summary>
    internal bool IsBoundTo(
        HirSnapshot sourceSnapshot,
        HirSemanticPhase artifactPhase) =>
        ReferenceEquals(_sourceSnapshot, sourceSnapshot)
        && _artifactPhase == artifactPhase;

    internal HirValidationOutcome? ValidationOutcome => _validation.Outcome;

    /// <summary>
    /// A model becomes publishable only after the phase-specific producer has sealed all of its sinks.
    /// This prevents an exact source token from being attached before validation/effect analysis completed.
    /// </summary>
    internal bool IsSealedForArtifact(HirSemanticPhase artifactPhase)
    {
        if (_artifactPhase != artifactPhase
            || !_validation.ProducerCompleted
            || _validation.Outcome is null
            || !_validation.IsSealed
            || _validation.ScopeGraph is not { IsSealed: true }
            || !_effectsSealed)
        {
            return false;
        }

        return artifactPhase switch
        {
            HirSemanticPhase.Validation => !_effectAnalysisCompleted,
            HirSemanticPhase.EffectAnalysis => _effectAnalysisCompleted,
            _ => false,
        };
    }

    /// <summary>
    /// Create the semantic model for the effect-analyzed HIR snapshot. Validation facts and the unified
    /// scope graph remain the one shared authority, while every effect-owned table starts empty. Therefore
    /// running <see cref="EffectAnalysis"/> on the returned model cannot mutate the validation-only model.
    /// </summary>
    internal HirSemanticModel ForkForEffectAnalysis()
    {
        if (!_validation.IsSealed
            || _validation.ScopeGraph is not { IsSealed: true }
            || _validation.Outcome is not { IsAccepted: true })
            throw new InvalidOperationException(
                "QINTERNAL: accepted validation facts and their scope graph must be sealed "
                + "before effect analysis is forked");
        if (_artifactPhase != HirSemanticPhase.Validation
            || _sourceSnapshot is null)
        {
            throw new InvalidOperationException(
                "QINTERNAL: only an exact snapshot-bound validation artifact can start effect analysis");
        }
        if (_qubitEventsByCallable.Count != 0
            || _qubitGraphByCallable.Count != 0
            || _effectSummaryByCallableId.Count != 0
            || _effectAnalysisCompleted)
            throw new InvalidOperationException(
                "QINTERNAL: only a validation-only HIR semantic model can be forked for effect analysis");
        return new HirSemanticModel(
            _sourceSnapshot,
            HirSemanticPhase.EffectAnalysis,
            _validation);
    }

    /// <summary>
    /// Freeze a validation result before a <c>HirSemanticArtifact</c> publishes it. The validation-only
    /// model cannot later acquire effect facts; effect analysis must use a dedicated fork.
    /// </summary>
    internal void SealValidationArtifact(IEnumerable<QoraError> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        RequireValidationOpen();
        if (_artifactPhase != HirSemanticPhase.Validation
            || _sourceSnapshot is null
            || !_validation.ProducerCompleted)
        {
            throw new InvalidOperationException(
                "QINTERNAL: a validation artifact requires one completed snapshot-bound validation pass");
        }
        var scopeGraph = _validation.ScopeGraph
            ?? throw new InvalidOperationException(
                "QINTERNAL: a validation artifact requires a completed HIR scope graph");
        if (_qubitEventsByCallable.Count != 0
            || _qubitGraphByCallable.Count != 0
            || _effectSummaryByCallableId.Count != 0)
        {
            throw new InvalidOperationException(
                "QINTERNAL: a validation artifact cannot contain effect-analysis facts");
        }

        _validation.Outcome = new HirValidationOutcome(diagnostics);
        scopeGraph.Seal();
        _validation.IsSealed = true;
        _effectsSealed = true;
    }

    /// <summary>Freeze an effect-analysis fork before publishing its semantic artifact.</summary>
    internal void SealEffectAnalysisArtifact()
    {
        if (_artifactPhase != HirSemanticPhase.EffectAnalysis
            || _sourceSnapshot is null
            || !_validation.IsSealed
            || _validation.ScopeGraph is not { IsSealed: true })
        {
            throw new InvalidOperationException(
                "QINTERNAL: an effect artifact requires an exact source and sealed validation facts");
        }
        if (_effectsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: effect-analysis facts were already sealed");

        var expectedCallables = _sourceSnapshot.Program.Callables
            .Select(callable => callable.Id)
            .ToHashSet();
        if (!expectedCallables.SetEquals(_effectSummaryByCallableId.Keys)
            || !expectedCallables.SetEquals(_qubitEventsByCallable.Keys)
            || !expectedCallables.SetEquals(_qubitGraphByCallable.Keys))
        {
            throw new InvalidOperationException(
                "QINTERNAL: effect analysis did not publish one coherent summary, event stream, "
                + "and qubit graph for every source callable");
        }
        _effectAnalysisCompleted = true;
        _effectsSealed = true;
    }

    /// <summary>
    /// Mark the end of the validation producer after its collect-all walk has populated every callable's
    /// scope and recheck verdict. Sealing is deliberately separate: this completion proof is recorded by
    /// the producer, while publication later freezes the graph and all fact sinks.
    /// </summary>
    internal void CompleteValidation(HirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        RequireValidationOpen();
        if (_artifactPhase != HirSemanticPhase.Validation)
            throw new InvalidOperationException(
                "QINTERNAL: only a validation-phase model can complete validation");
        if (_validation.ProducerCompleted)
            throw new InvalidOperationException(
                "QINTERNAL: validation was already marked complete");
        if (_sourceSnapshot is { } source
            && !ReferenceEquals(source.Program, program))
        {
            throw new InvalidOperationException(
                "QINTERNAL: validation completed against a different HIR program than its exact source");
        }

        var scopeGraph = _validation.ScopeGraph
            ?? throw new InvalidOperationException(
                "QINTERNAL: validation completed without a HIR scope graph");
        var expectedCallables = program.Callables
            .Select(callable => callable.Id)
            .ToHashSet();
        if (!expectedCallables.SetEquals(_validation.WillBeRecheckedByCallable.Keys))
        {
            throw new InvalidOperationException(
                "QINTERNAL: validation did not publish one recheck verdict for every source callable");
        }
        foreach (var callable in program.Callables)
        {
            if (scopeGraph.FindCallableScope(callable.Id) is null
                || scopeGraph.FindDeclaration(callable.Id) is not
                    { Kind: SymbolKind.Callable })
            {
                throw new InvalidOperationException(
                    $"QINTERNAL: validation did not publish the callable symbol and scope for "
                    + $"callable {callable.Id}");
            }
        }

        _validation.ProducerCompleted = true;
    }

    /// <summary>
    /// Register the one HIR scope graph. It already owns the scope, symbol, declaration, callable-root, and
    /// source-site indexes, so this model must not mirror those tables.
    /// </summary>
    internal void SetScopeGraph(HirScopeGraph scopeGraph)
    {
        ArgumentNullException.ThrowIfNull(scopeGraph);
        RequireValidationOpen();
        if (scopeGraph.IsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: a mutable validation model cannot attach an already-published HIR scope graph");
        if (_validation.ScopeGraph is not null
            && !ReferenceEquals(_validation.ScopeGraph, scopeGraph))
            throw new InvalidOperationException(
                "QINTERNAL: HirSemanticModel already owns a different HIR scope graph");
        _validation.ScopeGraph = scopeGraph;
    }

    /// <summary>Store an operation's qubit-event stream — its leaf statements' reads/writes/measures in
    /// program order, keyed by <c>op.Id</c>. The single producer is <see cref="EffectAnalysis"/>, exactly
    /// ONCE: facts are add-only, so a second analysis of the same op would silently REPLACE what earlier
    /// consumers already read — fail loud instead (a future post-injection re-analysis needs generation-keyed
    /// storage, registered as a known design gap).</summary>
    internal void AddQubitEvents(
        HirNodeId opId,
        IReadOnlyList<QubitEvent> events)
    {
        RequireEffectsOpen();
        if (!_qubitEventsByCallable.TryAdd(
                opId,
                HirCollections.Freeze(events)))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an event stream — re-analysis would silently replace add-only facts");
    }

    /// <summary>Store an operation's qubit graph — recorded by the SAME producer in the same pass as the
    /// event stream, coherence-swept before it lands here. Add-only, like the stream.</summary>
    internal void AddQubitGraph(
        HirNodeId opId,
        QubitGraph graph)
    {
        RequireEffectsOpen();
        if (!_qubitGraphByCallable.TryAdd(opId, graph))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has a qubit graph — re-analysis would silently replace add-only facts");
    }

    /// <summary>Record one unproven indexed access (rung B′) — produced by <see cref="QoraValidator"/> during
    /// the bounds-proof walk, add-only like every other fact. The backend decides the disposition; the
    /// OpenQASM path derives source-distinct QSEM030 diagnostics from this list.</summary>
    internal void AddUnprovenIndex(UnprovenIndex access)
    {
        ArgumentNullException.ThrowIfNull(access);
        RequireValidationOpen();

        if (_sourceSnapshot is { } snapshot
            && snapshot.Structure.FindNode(access.Site)
                is not HirIndexExpression)
        {
            throw new ArgumentException(
                $"unproven index site {access.Site} is not a HirIndexExpression "
                + "of this model's exact HIR snapshot",
                nameof(access));
        }

        if (!_validation.UnprovenIndexSites.Add(access.Site))
        {
            throw new InvalidOperationException(
                $"QINTERNAL: HIR index expression {access.Site} was recorded more than once");
        }

        _validation.UnprovenIndexes.Add(access);
    }

    /// <summary>
    /// Returns the exact HIR index-expression identity when that expression has an unproven bounds fact.
    /// Node identity is the only lookup authority; no statement/object-reference side map exists.
    /// </summary>
    internal HirNodeId? UnprovenIndexSite(
        HirNodeId indexExpressionId) =>
        _validation.UnprovenIndexSites.Contains(indexExpressionId)
            ? indexExpressionId
            : null;

    /// <summary>Record an operation's array-argument CONTRACT (rung B′/P4): the minimum length each of its
    /// classical-array parameters requires, settled after call-graph propagation. Single producer
    /// (<see cref="QoraValidator"/>, once per validation), add-only like every other fact.</summary>
    internal void SetRequiredArgLengths(
        HirNodeId opId,
        IReadOnlyDictionary<string, long> needs)
    {
        RequireValidationOpen();
        if (!_validation.RequiredArgLengthsByCallable.TryAdd(
                opId,
                HirCollections.Freeze(needs)))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an array-argument contract — re-validation would silently replace add-only facts");
    }

    /// <summary>The operation's array-argument contract — parameter name → minimum required length — or null
    /// when the op demands nothing. The call-site QSEM016s are DERIVED from this table; consumers (signature
    /// help, docs, backends) can read the same contract.</summary>
    public IReadOnlyDictionary<string, long>? RequiredArgLengths(
        HirNodeId opId) =>
        _validation.RequiredArgLengthsByCallable.TryGetValue(opId, out var needs) ? needs : null;

    /// <summary>Every indexed access this validation could not prove in bounds, in walk order — empty when
    /// the whole program is proven. Non-empty NEVER coexists with a successful OpenQASM compile because its
    /// target-policy pass derives QSEM030. Each entry's semantic site identity is translated to an exact
    /// MIR instruction reference during lowering.</summary>
    public IReadOnlyList<UnprovenIndex> UnprovenIndexes =>
        HirCollections.Freeze(_validation.UnprovenIndexes);

    /// <summary>The deferral ledger (see <see cref="DeferredSizeCheck"/>): the size-dependent judgements
    /// THIS validation postponed to the post-monomorphization re-check, in walk order. On a SUCCESSFUL
    /// compile the pipeline's surviving model always holds an EMPTY ledger — either the post-mono
    /// re-validation ran on concrete sizes (nothing left to postpone) or the program had no generics to
    /// postpone for. A program rejected AT the pre-mono validation keeps that model, so its ledger may
    /// carry the promises still outstanding at rejection; a program rejected at the POST-mono
    /// re-validation keeps the post-mono model, whose ledger is empty like any sized program's. A backend
    /// that skips specialization must dispose of every entry (runtime checks) instead of letting them
    /// evaporate.</summary>
    public IReadOnlyList<DeferredSizeCheck> DeferredSizeChecks =>
        HirCollections.Freeze(_validation.DeferredSizeChecks);

    /// <summary>Deferral-ledger sink (rung B′): single producer (<see cref="QoraValidator"/>), recorded
    /// during the bounds walk, add-only like every other fact.</summary>
    internal void AddDeferredSizeCheck(DeferredSizeCheck deferred)
    {
        RequireValidationOpen();
        _validation.DeferredSizeChecks.Add(deferred);
    }

    /// <summary>Will the post-monomorphization re-validation come for this operation? The validator's own
    /// PREDICTION, made BEFORE the Monomorphizer runs, from the same transitive reachability (concrete ops
    /// outward through the call graph) the Monomorphizer acts on — so it cannot disagree with the actual
    /// drop. True: a concrete op (its checks complete without deferral) or a generic reached from concrete
    /// code (its specializations get the re-check). FALSE: a dead generic — dropped, so its postponed
    /// judgements are MOOTED, never judged at any size (harmless: the code is never emitted). Null: an op
    /// this validation never saw. This is the ledger's companion question — a <see cref="DeferredSizeCheck"/>
    /// whose op answers false is a promise nothing will ever answer.</summary>
    public bool? WillBeRechecked(HirNodeId opId) =>
        _validation.WillBeRecheckedByCallable.TryGetValue(opId, out var will) ? will : null;

    /// <summary>Liveness-prediction sink (rung B′): single producer (<see cref="QoraValidator"/>, once per
    /// op), add-only — recording the same op twice is a pipeline bug, not a merge.</summary>
    internal void SetWillBeRechecked(
        HirNodeId opId,
        bool willBeRechecked)
    {
        RequireValidationOpen();
        if (!_validation.WillBeRecheckedByCallable.TryAdd(opId, willBeRechecked))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has a WillBeRechecked verdict — re-recording would silently replace an add-only fact");
    }

    /// <summary>The operation's value-genealogy graph (see <see cref="QubitNode"/>), or null when this exact
    /// operation was never analyzed. Facts are keyed by operation identity and never borrowed through a
    /// derivation chain.</summary>
    public QubitGraph? Graph(HirNodeId opId) =>
        _qubitGraphByCallable.TryGetValue(opId, out var graph)
            ? graph
            : null;

    internal void AddOpEffects(
        HirNodeId opId,
        OpEffectSummary summary)
    {
        RequireEffectsOpen();
        if (!_effectSummaryByCallableId.TryAdd(opId, summary))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an effect summary — re-analysis would silently replace add-only facts");
    }

    /// <summary>The symbol declared by this node in this exact HIR generation, if any.</summary>
    public Symbol? FindSymbol(HirNodeId nodeId) =>
        _validation.ScopeGraph?.FindDeclaration(nodeId);

    /// <summary>
    /// The symbol selected for this exact HIR use-site expression, if any. This is the authoritative
    /// result of HIR name resolution; later stages must not repeat lookup from source spelling.
    /// </summary>
    public Symbol? FindReferencedSymbol(HirNodeId nodeId) =>
        _validation.ScopeGraph?.FindReferencedSymbol(nodeId);

    /// <summary>The callable scope of this operation (or of the operation it was derived from), if any.</summary>
    public Scope? FindRootScope(HirNodeId opId) =>
        _validation.ScopeGraph?.FindCallableScope(opId);

    /// <summary>
    /// The scope at a stable HIR node-and-role site. A copied owner node follows the same derivation chain
    /// as declaration lookup before consulting the graph.
    /// </summary>
    public Scope? FindScope(HirScopeSite site) =>
        _validation.ScopeGraph?.FindScope(site);

    /// <summary>One HIR scope by semantic scope identity.</summary>
    public Scope? FindScope(ScopeId scopeId) => _validation.ScopeGraph?.FindScope(scopeId);

    /// <summary>Every HIR scope in the model, keyed by semantic scope identity.</summary>
    public IReadOnlyDictionary<ScopeId, Scope> Scopes =>
        _validation.ScopeGraph?.Scopes ?? EmptyScopes;

    /// <summary>This operation's qubit-event stream (leaf reads/writes/measures in program order), or an
    /// empty list if the model never analyzed this exact op. Keyed by <c>op.Id</c> directly and deliberately
    /// NOT resolved through a derivation chain: a structurally derived operation requires its own event
    /// analysis. Use <see cref="WasEffectAnalyzed"/> to tell "analyzed, zero events" from "never analyzed".
    /// </summary>
    public IReadOnlyList<QubitEvent> QubitEvents(HirNodeId opId) =>
        _qubitEventsByCallable.TryGetValue(opId, out var events)
            ? events
            : System.Array.Empty<QubitEvent>();

    /// <summary>Did <see cref="EffectAnalysis"/> actually run on this op Id? False for an op the analysis
    /// never saw — for example, a semantic-error abort or a still-generic definition. The safety queries
    /// refuse to answer "safe" from an absent stream (that would be vacuous truth, not analysis).</summary>
    public bool WasEffectAnalyzed(HirNodeId opId) =>
        _qubitEventsByCallable.ContainsKey(opId);

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
    public (QubitEvent Birth, QubitEvent Death)? LiveRange(
        HirNodeId opId,
        QubitRef q)
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
    public bool IsAncilla(
        HirNodeId opId,
        QubitRef q)
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
    public bool IsCleanupCandidate(
        HirNodeId opId,
        QubitRef q)
    {
        if (!WasEffectAnalyzed(opId)) return false;
        if (!IsAncilla(opId, q)) return false;
        foreach (var e in QubitEvents(opId))
            if (e.Kind == QubitEventKind.Measure && e.Qubit.Overlaps(q)) return false;
        return true;
    }

    /// <summary>Rung ③ — the full safety verdict for auto-uncomputing qubit <paramref name="q"/> in operation
    /// <paramref name="opId"/>. SAFE means: a future MIR cleanup that reverses the statements that wrote q
    /// (whole statements, reverse order, the <c>use</c> birth never replayed) at q's death point yields, in every
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
    /// destroys the value the cleanup would need).</item>
    /// <item>QFREE COMPUTE — no <see cref="QubitEventKind.Write"/> of q carries <c>NonQfree</c>: neither an
    /// H/Rx/Ry write (which injects a fresh superposition cleanup cannot fold back once a surviving qubit
    /// has recorded it) NOR a Y/CY phase-permutation write (whose basis-value-dependent phase becomes a
    /// survivor-relative phase under entanglement that cleanup would strip — state-vector
    /// verified, round 5; matches Silq's qfree excluding Y).</item>
    /// <item>UNCONDITIONAL COMPUTE — no <see cref="QubitEventKind.Write"/> of q sits inside a CONTAINER
    /// (<c>if</c> / <c>for</c> / <c>while</c> / <c>repeat</c>). The event stream is a flat
    /// timeline that walks each container body once, so a contained write runs conditionally (an <c>if</c>)
    /// or repeatedly (a loop) at runtime — a straight-line cleanup sequence at the death point would not
    /// mirror what actually executed. Structure is not in the events; it is read from the IR via
    /// <see cref="ContainerMap"/> — which is why this query takes the OPERATION, not just its Id. (Reads
    /// inside containers are harmless: cleanup reverses the write chain only.) Lifted later by conditional
    /// cleanup support, which additionally requires proving the CONDITION BIT unchanged between the compute
    /// and cleanup; classical-bit flow is NOT in the event stream (known gap #11 of
    /// the requirements table).</item>
    /// <item>WELL-SOURCED COMPUTE — a statement that writes q must only READ its other qubits (a co-WRITTEN
    /// partner — a SWAP operand, a call modifying two params — blocks outright: cleanup would
    /// rewrite that partner at q's death, and no window scan can see the partner's uses AFTER q dies).
    /// Additionally, a Write of q WIDER than q — a whole-register broadcast or a blanket projected call under
    /// an ELEMENT query — writes sibling elements inside ONE event and blocks as
    /// <see cref="UncomputeBlocker.CoWrittenPartner"/> too: whole-statement cleanup cannot be sliced to
    /// q. The register's <c>use</c> birth is exempt (the only parentless write node — enforced QINTERNAL-loud
    /// at construction), sound because cleanup never replays an allocation. Every parent EDGE of
    /// q's write (the graph's recorded sources) must then not be value-changed (Written/Measured) between
    /// that write and q's death, so q stays a function of still-present, unchanged sources cleanup can use.
    /// Only edges FULLY COVERED by q (same register, and q is the whole register or names the
    /// same element) are exempt as q's own chain — a same-register BLANKET source under an element query
    /// covers sibling elements too, so it is a real source and its liveness is scanned (adversarially
    /// confirmed).</item>
    /// </list>
    /// The verdict names the failed clause and carries the offending event, whose <see cref="QubitEvent.StmtId"/>
    /// lets a future MIR planner point at the exact statement in diagnostics. An op with no recorded stream
    /// answers <see cref="UncomputeBlocker.NotAnalyzed"/> —
    /// never a vacuous "safe". Conservative (may reject a safe case, never admits an unsafe one). The
    /// source-liveness clause is sound under the dependency-respecting (LIFO) uncompute-injection order rung ④
    /// must use, injecting at the death point — or, when the death sits inside a container, immediately AFTER
    /// the outermost enclosing container (the window extension above makes the verdict honest for that
    /// placement). Matches Silq's <c>qfree</c> on the deciding case: a basis permutation carrying a
    /// basis-value-dependent phase (Y, CY) is REJECTED — under entanglement cleanup would strip a
    /// survivor-relative phase the documented result keeps (state-vector verified, round 5; an earlier
    /// "broader than Silq — Y allowed" claim was a confirmed bug). Diagonal phase gates (Z/S/T/CZ) stay safe:
    /// they never write a value (all-Read), so they are not qfree writes at all.</summary>
    public UncomputeVerdict UncomputeSafety(
        HirCallable op,
        QubitRef q)
    {
        if (!WasEffectAnalyzed(op.Id)) return new(UncomputeBlocker.NotAnalyzed, null);

        // CLASSIFICATION BEFORE SAFETY, in code: safety is a question about CLEANUP CANDIDATES only —
        // the candidacy ruling (measurement included, and it outranks EVERY scan clause below, Irreversible
        // included) is delivered first. Whether q is a candidate is
        // rung 1's ruling (IsCleanupCandidate — measurement means the value was DELIVERED, an output), so
        // it is delegated there, never re-judged as a scan clause here: one concept, one home. It is
        // delivered as a VERDICT rather than a silent precondition, so a direct caller can never receive
        // "safe" for a measured ancilla (whose collapse also has no unitary cleanup) or a caller-owned input.
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
                    $"UncomputeSafety: statement {e.StmtId} of `{op.Name}`'s event stream is not in the supplied HIR snapshot — use Compilation.Hir.EffectAnalysis, the exact generation this analysis describes");
            if (e.Irreversible) return new(UncomputeBlocker.Irreversible, e);                          // (a) lossy touch
            if (e.Kind == QubitEventKind.Write && e.NonQfree)
                return new(UncomputeBlocker.NonQfreeWrite, e);                                    // (b) superposition (H/Rx/Ry) or phase-permutation (Y/CY)
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
        //     a SWAP-style value move / a call modifying both): block outright — cleanup rewrites
        //     that partner at q's death, and its later uses can sit AFTER q's death where no window reaches.
        //   · each parent edge not fully covered by q is a SOURCE: it must still be the CURRENT version of its
        //     access ref (Edge.Via keeps a blanketed read's conservative breadth) through (write.Order,
        //     windowEnd], or q is no longer a function of it and cleanup reverses against the wrong value.
        //     Edges fully covered by q are q's own chain, which cleanup itself reverses.
        var graph = Graph(op.Id)!;   // guaranteed by the WasEffectAnalyzed guard above
        foreach (var write in events)
        {
            if (write.Kind != QubitEventKind.Write || !write.Qubit.Overlaps(q)) continue;

            // A write of q WIDER than q (a blanket write under an element query — a broadcast, an opaque
            // call) writes SIBLING ELEMENTS inside this one event: whole-statement cleanup cannot be
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
    public bool IsSafelyUncomputable(
        HirCallable op,
        QubitRef q) =>
        UncomputeSafety(op, q).IsSafe;

    /// <summary>This operation's effect summary (or its derivation source's), if any.</summary>
    public OpEffectSummary? FindOpEffects(HirNodeId opId) =>
        _effectSummaryByCallableId.TryGetValue(opId, out var summary)
            ? summary
            : null;

    /// <summary>Every operation root scope in the model, keyed by operation Id.</summary>
    public IReadOnlyDictionary<HirNodeId, Scope> RootScopes =>
        _validation.ScopeGraph?.CallableScopes ?? EmptyRootScopes;

    /// <summary>
    /// The unified HIR scope graph containing program, namespace, callable, and lexical scopes.
    /// </summary>
    public HirScopeGraph? ScopeGraph => _validation.ScopeGraph;

    private void RequireValidationOpen()
    {
        if (_validation.IsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: validation facts are sealed by an immutable HIR semantic artifact");
    }

    private void RequireEffectsOpen()
    {
        if (_effectsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: effect facts are sealed by an immutable HIR semantic artifact");
    }

}
