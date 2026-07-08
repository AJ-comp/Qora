namespace Qora.Ir.Passes;

/// <summary>
/// The PERSISTENT semantic side table: everything <see cref="SymbolTableBuilder"/> proved during the final
/// validation, keyed by stable node <see cref="QStmt.Id"/>s and carried to the END of the pipeline instead
/// of being rebuilt on demand (Roslyn's SemanticModel / rust-analyzer's AstId-keyed queries, in miniature).
/// Passes that run AFTER the model is built never invalidate it: rewrites via <c>with</c> keep the node's
/// Id, and passes that COPY subtrees (<see cref="ConjugationLowering"/>, <see cref="AdjointMaterializer"/>)
/// register each copy's lineage through <see cref="RecordDerivation"/>, so a lookup on a copied node walks
/// the derivation chain back to the node the model actually saw. This is also the designated result store
/// for the upcoming effect/liveness analysis.
/// </summary>
public sealed class SemanticModel
{
    private readonly Dictionary<int, Scope> _rootScopeByOp = new();     // QOperation.Id → root scope
    private readonly Dictionary<int, Symbol> _symbolByDeclId = new();   // declaring node Id → Symbol
    private readonly Dictionary<int, int> _derivedFrom = new();         // copied node Id → source node Id

    internal void AddOperation(QOperation op, Scope root)
    {
        _rootScopeByOp[op.Id] = root;
        foreach (var sym in root.AllSymbols())
            if (sym.DeclNodeId != 0) _symbolByDeclId[sym.DeclNodeId] = sym;
    }

    /// <summary>Register that node <paramref name="freshId"/> is a copy of <paramref name="sourceId"/>.
    /// The (source, fresh) order matches <see cref="ReId"/>'s record callback, so a model can be passed
    /// to it directly as <c>model.RecordDerivation</c>.</summary>
    public void RecordDerivation(int sourceId, int freshId) => _derivedFrom[freshId] = sourceId;

    /// <summary>The symbol DECLARED by this node (or by the node it was copied from), if any.</summary>
    public Symbol? FindSymbol(int nodeId) => Resolve(nodeId, _symbolByDeclId);

    /// <summary>The root scope of this operation (or of the operation it was derived from), if any.</summary>
    public Scope? FindRootScope(int opId) => Resolve(opId, _rootScopeByOp);

    /// <summary>Every operation root scope in the model, keyed by operation Id.</summary>
    public IReadOnlyDictionary<int, Scope> RootScopes => _rootScopeByOp;

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
