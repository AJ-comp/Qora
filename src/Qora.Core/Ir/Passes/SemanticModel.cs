namespace Qora.Ir.Passes;

/// <summary>
/// One qubit operand as effect analysis sees it. <see cref="Index"/> null means the WHOLE register —
/// a broadcast (<c>H(q)</c>), a loop-variable index (<c>q[i]</c>), or any index not known at analysis
/// time is conservatively blanketed to the full register.
/// </summary>
public readonly record struct QubitRef(string Reg, int? Index)
{
    public override string ToString() => Index is int i ? $"{Reg}[{i}]" : Reg;
}

/// <summary>
/// What one statement does to qubits (use/def in dataflow terms).
/// <see cref="Touched"/> is every qubit operand the statement references, controls included;
/// <see cref="Modified"/> only those whose COMPUTATIONAL-BASIS value may change — control slots and
/// diagonal-gate targets are touched but not modified (their 0/1 value is preserved; a possible phase
/// change is tracked by <see cref="GateInfo.Diagonal"/> on the gate table, not here).
/// </summary>
public sealed record StmtEffects(
    IReadOnlySet<QubitRef> Touched,
    IReadOnlySet<QubitRef> Modified);

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
/// per-statement and per-operation qubit effects here too, and <see cref="NameMangler"/> records each
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
    private readonly Dictionary<int, StmtEffects> _effectsByStmtId = new();        // QStmt.Id → effects
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

    internal void AddEffects(int stmtId, StmtEffects fx) => _effectsByStmtId[stmtId] = fx;
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

    /// <summary>The qubit effects of this statement (or of the statement it was copied from), if any.</summary>
    public StmtEffects? FindEffects(int stmtId) => Resolve(stmtId, _effectsByStmtId);

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
