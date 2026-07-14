namespace Qora.Ir.Passes;

/// <summary>
/// One safe ancilla's cleanup, COMPUTED but not yet spliced (rung ④, step 3). <see cref="Cleanup"/> is the
/// statement list that returns <see cref="Ancilla"/> to |0…0⟩ — the adjoints of the gates that WROTE it, in
/// reverse program order (socks-and-shoes). <see cref="Death"/> is the ancilla's last-use event (rung ②'s
/// death point), the anchor from which step 4 will locate the realizable insertion position and step 5 will
/// splice. An EMPTY <see cref="Cleanup"/> is legal and means "born |0…0⟩ and never value-written (used only as
/// a control) — already clean, nothing to inject."
/// </summary>
public sealed record CleanupPlan(QubitRef Ancilla, IReadOnlyList<QStmt> Cleanup, QubitEvent Death);

/// <summary>
/// Rung ④, step 3 — the cleanup PLAN builder. Pure: given the analysis facts and one ancilla, it computes the
/// cleanup statement list and its death anchor, touching no IR. (Steps 4-5 turn a plan into an actual splice.)
///
/// The recipe, each part backed by an existing tool:
/// <list type="number">
///   <item>SAFETY GATE — <see cref="SemanticModel.IsSafelyUncomputable"/> (rung ③). Not safe ⇒ no plan
///         (<c>null</c>). The step-6 driver pre-filters to safe ancillas; this is the guard, not the filter.</item>
///   <item>WRITE LIST — q's <see cref="QubitEventKind.Write"/> events in program order (<see cref="QubitEvent.Order"/>),
///         with the <c>use</c> |0…0⟩ birth excluded (its StmtId resolves to a <see cref="QUse"/>, never replayed).</item>
///   <item>FETCH — each write event's <see cref="QubitEvent.StmtId"/> → its gate node via <see cref="StmtMap"/>.</item>
///   <item>INVERT — <see cref="Inverter.InvertBody"/> reverses the list and adjoints each gate in one call.</item>
/// </list>
///
/// FAIL LOUD, never a silent hole: a write event whose statement is missing, or is neither a gate nor the
/// birth <c>use</c>, is a QINTERNAL contradiction (every Write comes from a gate or the hoisted birth). And a
/// rung-③-SAFE ancilla whose writes do not invert is a contradiction between the two analyses — thrown, not
/// swallowed into a cleanup that silently drops statements.
/// </summary>
public static class UncomputePlanner
{
    /// <summary>Build the cleanup plan for ancilla <paramref name="q"/> in operation <paramref name="op"/>, or
    /// <c>null</c> if q is not safely uncomputable. <paramref name="inverter"/> carries the operation table so a
    /// qfree user-op call in the write list resolves (build it once from the program's operations, reuse it).</summary>
    public static CleanupPlan? Plan(SemanticModel model, Inverter inverter, QOperation op, QubitRef q)
    {
        if (!model.IsSafelyUncomputable(op, q)) return null;   // rung ③ guard — caller should pre-filter

        var stmts = StmtMap.Build(op);
        var writeGates = new List<QStmt>();
        var seen = new HashSet<int>();
        foreach (var e in model.QubitEvents(op.Id)
                               .Where(e => e.Qubit.Overlaps(q) && e.Kind == QubitEventKind.Write)
                               .OrderBy(e => e.Order))                         // program order — InvertBody reverses it
        {
            // ONE statement → ONE adjoint. A gate that writes several elements of q (SWAP(a[0],a[1]), a call
            // co-modifying two register-mates) emits one Write event PER element, all sharing this StmtId; its
            // whole-statement adjoint already undoes them all, so dedup by StmtId — else the adjoint replays N×
            // and leaves q dirty (adversarially found). Events of one statement share Order, so first-seen keeps
            // program position.
            if (!seen.Add(e.StmtId)) continue;
            if (!stmts.TryGetValue(e.StmtId, out var s))
                throw new System.InvalidOperationException(
                    $"QINTERNAL: write event {e.StmtId} of `{q}` in `{op.Name}` has no statement in the tree");
            switch (s)
            {
                case QGate g: writeGates.Add(g); break;
                case QUse: break;                                             // the |0…0⟩ birth — never replayed
                default:
                    throw new System.InvalidOperationException(
                        $"QINTERNAL: `{q}` in `{op.Name}` was written by a {s.GetType().Name}, not a gate or `use` birth");
            }
        }

        var (cleanup, reason) = inverter.InvertBody(writeGates);
        if (cleanup is null)                                                  // rung ③ said safe ⇒ must invert
            throw new System.InvalidOperationException(
                $"QINTERNAL: rung ③ deemed `{q}` in `{op.Name}` safe, but its writes did not invert ({reason})");

        var death = model.LiveRange(op.Id, q)!.Value.Death;                   // safe ⇒ has events ⇒ non-null
        return new CleanupPlan(q, cleanup, death);
    }
}
