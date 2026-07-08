namespace Qora.Ir;

/// <summary>
/// Re-mints node identity over a COPIED subtree. A pass that duplicates statements into a second tree
/// position — the <see cref="Passes.Monomorphizer"/> stamping one generic body into several
/// specializations, or the <see cref="Inverter"/>'s output being installed next to the forward body it
/// was derived from — would otherwise carry the source Ids along (the copies are made with <c>with</c>,
/// which inherits <see cref="QStmt.Id"/> by design). Duplicate Ids would silently corrupt every side
/// table keyed by Id, so the copier calls this at the INSTALL site. The optional <paramref name="record"/>
/// callback receives each (sourceId, freshId) pair so post-model passes can log lineage into
/// <see cref="Passes.SemanticModel"/> and keep Id-based lookups working for the copies.
/// </summary>
public static class ReId
{
    public static IReadOnlyList<QStmt> Run(IReadOnlyList<QStmt> stmts, Action<int, int>? record = null) =>
        stmts.Select(s => Fresh(s, record)).ToList();

    public static QOperation Run(QOperation op, Action<int, int>? record = null)
    {
        var freshOp = op with
        {
            Id = QNodeIds.Next(),
            Params = op.Params.Select(p =>
            {
                var fp = p with { Id = QNodeIds.Next() };
                record?.Invoke(p.Id, fp.Id);
                return fp;
            }).ToList(),
            Body = Run(op.Body, record),
        };
        record?.Invoke(op.Id, freshOp.Id);
        return freshOp;
    }

    private static QStmt Fresh(QStmt stmt, Action<int, int>? record)
    {
        QStmt fresh = stmt switch
        {
            QIf i => i with
            {
                Id = QNodeIds.Next(),
                Then = Run(i.Then, record),
                Else = Run(i.Else, record),
            },
            QFor f => f with { Id = QNodeIds.Next(), Body = Run(f.Body, record) },
            QWhile w => w with { Id = QNodeIds.Next(), Body = Run(w.Body, record) },
            QRepeat r => r with { Id = QNodeIds.Next(), Body = Run(r.Body, record) },
            QConjugate c => c with
            {
                Id = QNodeIds.Next(),
                Within = Run(c.Within, record),
                Apply = Run(c.Apply, record),
            },
            _ => stmt with { Id = QNodeIds.Next() },   // leaf statements: QUse / QGate / QDecl / QAssign
        };
        record?.Invoke(stmt.Id, fresh.Id);
        return fresh;
    }
}
