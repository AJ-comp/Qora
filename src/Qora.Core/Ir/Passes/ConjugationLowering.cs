namespace Qora.Ir.Passes;

/// <summary>
/// Flattens every <see cref="QConjugate"/> — the within/apply node meaning "run <c>Within</c>, then
/// <c>Apply</c>, then the inverse of <c>Within</c>" — into ordinary straight-line statements:
/// <c>Within ++ Apply ++ Inverter.InvertBody(Within)</c>. This is the first slice of automatic
/// uncomputation: the piece that finally makes the (already pass-threaded) <see cref="QConjugate"/> node
/// reach OpenQASM. Reversal itself is not new — it reuses the existing <see cref="Inverter"/>.
///
/// Ordering is load-bearing: this runs BEFORE <see cref="AdjointMaterializer"/>. Inverting a <c>within</c>
/// that calls a user op <c>Foo(...)</c> yields <c>Adjoint Foo(...)</c>; only AdjointMaterializer (running
/// afterwards) turns that into a real synthesized def <c>Foo__adj</c> that <see cref="NameMangler"/> then
/// owns — so no name is minted at emit time.
///
/// A <c>within</c> block that has no inverse (it measures, resets, mutates a classical, loops unboundedly,
/// allocates local qubits, or controls a user op) is reported as a clean, span-carrying <b>QSEM027</b> and
/// emission is skipped. The inverse is NEVER treated as empty on failure, so a circuit with the uncompute
/// silently dropped can never be emitted. As defense in depth, any <see cref="QConjugate"/> that somehow
/// survives this pass is caught again by <see cref="ReferentialCheck"/> (QINTERNAL) before emission.
/// </summary>
public static class ConjugationLowering
{
    public static (QProgram Program, List<QoraError> Errors) Run(QProgram program, SemanticModel? model = null)
    {
        var errors = new List<QoraError>();
        var inverter = new Inverter(program.Operations);
        // This pass runs AFTER the semantic model is built, so every copied statement's lineage is recorded
        // — an Id-keyed lookup on a synthesized cleanup statement resolves through to its Within original.
        Action<int, int>? record = model is null ? null : model.RecordDerivation;
        var ops = program.Operations
            .Select(op => op with { Body = Lower(op.Body, inverter, errors, record) })
            .ToList();
        return (program with { Operations = ops }, errors);
    }

    /// <summary>
    /// Flatten a statement list, replacing each <see cref="QConjugate"/> with its expansion and recursing
    /// into control-flow bodies. Bottom-up by construction: the cleanup is synthesized from the ORIGINAL
    /// <c>Within</c> (the <see cref="Inverter"/> inverts a nested conjugate correctly on its own), and only
    /// then are within/apply/cleanup themselves recursively flattened.
    /// </summary>
    private static IReadOnlyList<QStmt> Lower(IReadOnlyList<QStmt> stmts, Inverter inverter, List<QoraError> errors, Action<int, int>? record)
    {
        var outp = new List<QStmt>(stmts.Count);
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QConjugate c:
                    var (inverse, reason) = inverter.InvertBody(c.Within);
                    if (inverse is null)
                    {
                        // No inverse ⇒ this within cannot be uncomputed. Report and STOP — never append a
                        // null/empty tail, which would emit the compute with the cleanup silently dropped.
                        errors.Add(new QoraError(
                            $"the `within` block cannot be uncomputed: {reason}",
                            "QSEM027", c.Span?.Start ?? -1, c.Span?.End ?? -1));
                        break;
                    }
                    outp.AddRange(Lower(c.Within, inverter, errors, record));
                    outp.AddRange(Lower(c.Apply, inverter, errors, record));
                    // ReId: the inverse is a `with`-copy of Within's statements, installed in the SAME body
                    // right next to the originals — without fresh Ids every Id-keyed side table would see
                    // each Within statement twice.
                    outp.AddRange(Lower(ReId.Run(inverse, record), inverter, errors, record));
                    break;

                case QIf i:
                    outp.Add(i with { Then = Lower(i.Then, inverter, errors, record), Else = Lower(i.Else, inverter, errors, record) });
                    break;
                case QFor f:
                    outp.Add(f with { Body = Lower(f.Body, inverter, errors, record) });
                    break;
                case QWhile w:
                    outp.Add(w with { Body = Lower(w.Body, inverter, errors, record) });
                    break;
                case QRepeat r:
                    outp.Add(r with { Body = Lower(r.Body, inverter, errors, record) });
                    break;

                default:
                    outp.Add(stmt);
                    break;
            }
        }
        return outp;
    }
}
