namespace Qora.Ir.Passes;

/// <summary>
/// The statement-Id → statement lookup for one operation: hand it a <see cref="QubitEvent.StmtId"/> and it
/// returns the exact <see cref="QStmt"/> node that event came from. The event stream carries only Ids (a flat
/// timeline of touches), so the uncompute injector — which must fetch the very gate statements that wrote an
/// ancilla in order to synthesize their inverse — resolves each write Id back to its node HERE. Sibling to
/// <see cref="ContainerMap"/> (that one answers "which containers enclose this Id?", this one answers "what IS
/// this Id?"), and built from the SAME single tree walk (<see cref="ContainerMap.Visit"/>) so nesting is never
/// a place a statement can hide: every statement at any depth is reachable by its Id.
/// </summary>
public static class StmtMap
{
    /// <summary>Build the map for one operation: every statement Id (containers included, at any depth) → its
    /// statement node. An absent key means the Id is not a statement of this operation. One walk, O(1) lookups
    /// afterwards. Ids are unique per node (<see cref="QNodeIds"/>), so no statement overwrites another.</summary>
    public static IReadOnlyDictionary<int, QStmt> Build(QOperation op)
    {
        var map = new Dictionary<int, QStmt>();
        ContainerMap.Visit(op, (stmt, _) => map[stmt.Id] = stmt);
        return map;
    }
}
