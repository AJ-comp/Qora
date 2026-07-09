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
/// </summary>
public sealed record QubitEvent(QubitRef Qubit, QubitEventKind Kind, int Order, int StmtId);

/// <summary>
/// One operation's effect on its FORMAL qubit parameters (locals allocated by <c>use</c> are op-private
/// and excluded — they are the ancilla candidates a later liveness pass hunts for).
/// <see cref="Irreversible"/> is true when the body (transitively) measures or resets.
/// </summary>
public sealed record OpEffectSummary(
    IReadOnlySet<QubitRef> ParamTouched,
    IReadOnlySet<QubitRef> ParamModified,
    bool Irreversible);

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
    private readonly Dictionary<int, OpEffectSummary> _effectSummaryByOpId = new(); // QOperation.Id → summary
    private readonly Dictionary<int, string> _emittedNameByDeclId = new(); // declaring node Id → emitted (post-mangling) name
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
    /// program order, keyed by <c>op.Id</c>. The single producer is <see cref="EffectAnalysis"/>.</summary>
    internal void AddQubitEvents(int opId, IReadOnlyList<QubitEvent> events) => _qubitEventsByOp[opId] = events;
    internal void AddOpEffects(int opId, OpEffectSummary s) => _effectSummaryByOpId[opId] = s;

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
    /// pre-copy, so no derivation walk is needed (the ladder analyzes the program before any injection).</summary>
    public IReadOnlyList<QubitEvent> QubitEvents(int opId) =>
        _qubitEventsByOp.TryGetValue(opId, out var e) ? e : System.Array.Empty<QubitEvent>();

    /// <summary>Rung ② liveness, DERIVED (nothing stored): the <c>[Birth, Death]</c> events bracketing
    /// <paramref name="q"/>'s life inside operation <paramref name="opId"/>. Birth is its earliest event
    /// (Order-min — a <c>use</c> register's birth <see cref="QubitEventKind.Write"/>, or a parameter's first
    /// use); Death is its latest event (Order-max — its LAST use, of ANY kind: a final control Read counts,
    /// since the qubit still holds a value that must be cleaned after it). Death is the point after which
    /// rung ④ may inject an uncompute. Null when the qubit has no events in the op (never used).
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
