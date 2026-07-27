namespace Qora.Ir.Passes;

/// <summary>
/// Adjoint materialization (IR→IR): rewrites every whole-operation <c>Adjoint Foo(...)</c> on a USER
/// operation into an ordinary call to a synthesized inverse operation <c>Foo__adj</c>, and adds that
/// inverse as a real <see cref="QOperation"/> to the program (its body produced by <see cref="Inverter"/>).
/// After this pass no whole-op <c>Adjoint</c> functor remains on a user-op call, so the emitter is a pure
/// printer that never inverts or mints a name.
///
/// Why a pass, not emit-time synthesis: an inverse def is a NAME the emitted QASM must keep clear of every
/// other global name (a user register <c>Foo__adj</c>, another def). Minting that name HERE — before
/// <see cref="NameMangler"/> — lets the synthesized op flow through the exact collision resolution the
/// mangler already applies to every operation, so no generated name is ever born outside the mangler's
/// authority. That makes the <c>Foo__adj</c>-vs-user-name collision class structurally impossible instead
/// of patched at the emit-time seam. It mirrors how <see cref="Monomorphizer"/> materializes its
/// size-specialized ops before mangling.
///
/// Runs AFTER monomorphization and validation (so every <c>Adjoint Foo</c> is on a concrete op already
/// proven invertible by QSEM001, transitively) and BEFORE mangling. <c>Adjoint</c> on a BUILT-IN gate
/// (<c>inv @ h</c>) is deliberately left untouched — that lowering is the emitter's job; only user-op
/// adjoints are materialized here.
/// </summary>
internal static class AdjointMaterializer
{
    /// <summary>The transformed program plus one note per synthesized inverse-def name that had to dodge a
    /// user name (surfaced as a <c>// Qora:</c> comment, alongside the mangler's own rename notes).</summary>
    public sealed record Result(
        QProgram Program,
        IReadOnlyList<string> Notes,
        IReadOnlyList<NodeDerivation> Derivations);

    public static Result Run(QProgram program)
    {
        if (program.Operations.Count == 0)
            return new Result(
                program,
                Array.Empty<string>(),
                Array.Empty<NodeDerivation>());

        var opById = program.Operations.ToDictionary(o => o.Id);
        var opNames = program.Operations.Select(o => o.Name).ToHashSet();
        var inverter = new Inverter(program.Operations);
        var derivations = new List<NodeDerivation>();
        void Record(int sourceId, int derivedId) =>
            derivations.Add(new NodeDerivation(sourceId, derivedId));

        // Close the set of ops that need an inverse def: seed with the Adjoint-refs in every body, then
        // follow each inverse body's own Adjoint-refs (`Foo`'s inverse calls `Adjoint Bar`) to a fixpoint.
        var adjBody = new Dictionary<int, IReadOnlyList<QStmt>>();
        var adjName = new Dictionary<int, string>();
        var order = new List<int>();
        var minted = new HashSet<string>(opNames);

        var seen = new HashSet<int>();
        var work = new Queue<int>();
        void Enqueue(IReadOnlyList<QStmt> body)
        {
            var refs = new HashSet<int>();
            CollectAdjointRefs(body, opById, refs);
            foreach (var r in refs) if (seen.Add(r)) work.Enqueue(r);
        }

        foreach (var o in program.Operations) Enqueue(o.Body);
        while (work.Count > 0)
        {
            var operationId = work.Dequeue();
            if (adjBody.ContainsKey(operationId)) continue;
            var operation = opById[operationId];
            if (!inverter.TryInvertOperation(operationId, out var inverse, out var reason))
                throw new InvalidOperationException(
                    $"QINTERNAL: validated Adjoint target `{operation.Name}` cannot be materialized ({reason})");
            adjBody[operationId] = inverse;
            order.Add(operationId);
            // Unique among ALL op names (+ adj names minted so far) so each op keeps a distinct key; the
            // mangler still resolves any remaining collision with a user declaration afterwards.
            var candidate = operation.Name + "__adj";
            while (minted.Contains(candidate)) candidate += "_";
            minted.Add(candidate);
            adjName[operationId] = candidate;
            Enqueue(inverse);
        }

        if (order.Count == 0)
            return new Result(
                program,
                Array.Empty<string>(),
                Array.Empty<NodeDerivation>());

        var notes = new List<string>();
        var inverseBySourceId = new Dictionary<int, QOperation>();
        foreach (var operationId in order)
        {
            var orig = opById[operationId];
            // When the canonical `Foo__adj` was already a user name, the inverse took `Foo__adj_` (etc.).
            // Note it so a reader who sees the adjusted def name knows why (dots are flattened to match the
            // emitted identifier, mirroring the mangler). No note when the canonical name was free.
            if (adjName[operationId] != orig.Name + "__adj")
                notes.Add($"inverse of `{orig.DisplayName ?? orig.Name}` emitted as `{Flat(adjName[operationId])}` (the name `{Flat(orig.Name)}__adj` was already taken)");
            // ReId: the synthesized op is a `with`-copy of the forward op — its op Id, every param Id and
            // every body statement Id would otherwise duplicate the forward definition's. This runs AFTER
            // the semantic model, so each fresh Id's lineage back to the forward node is recorded.
            inverseBySourceId.Add(operationId, ReId.Run(orig with
            {
                Name = adjName[operationId],
                DisplayName = "Adjoint " + (orig.DisplayName ?? orig.Name),
                Body = adjBody[operationId],
            }, Record));
        }

        // Every synthesized declaration receives its identity before any call is rewritten. Consequently,
        // no intermediate tree relies on a spelling being rebound by a later pass.
        var targets = inverseBySourceId.ToDictionary(
            pair => pair.Key,
            pair => new InverseTarget(pair.Value.Id, pair.Value.Name));

        var result = program.Operations
            .Select(o => o with { Body = RewriteAdjointCalls(o.Body, targets, opById) })
            .ToList();
        foreach (var operationId in order)
        {
            var inverse = inverseBySourceId[operationId];
            result.Add(inverse with
            {
                Body = RewriteAdjointCalls(inverse.Body, targets, opById),
            });
        }

        return new Result(
            program with { Operations = result },
            notes.AsReadOnly(),
            derivations.AsReadOnly());
    }

    /// <summary>Flatten a namespaced name's dots to match the emitted identifier (as NameMangler does).</summary>
    private static string Flat(string name) => name.Replace(".", "_");

    // --- rewrite: `Adjoint <user-op>` call -> plain call to the synthesized inverse def ---

    private static IReadOnlyList<QStmt> RewriteAdjointCalls(
        IReadOnlyList<QStmt> stmts,
        IReadOnlyDictionary<int, InverseTarget> targets,
        IReadOnlyDictionary<int, QOperation> opById) =>
        stmts.Select(stmt => RewriteStmt(stmt, targets, opById)).ToList();

    private static QStmt RewriteStmt(
        QStmt stmt,
        IReadOnlyDictionary<int, InverseTarget> targets,
        IReadOnlyDictionary<int, QOperation> opById) => stmt switch
    {
        QGate gate => RewriteGate(gate, targets, opById),
        QIf conditional => conditional with
        {
            Then = RewriteAdjointCalls(conditional.Then, targets, opById),
            Else = RewriteAdjointCalls(conditional.Else, targets, opById),
        },
        QFor loop => loop with { Body = RewriteAdjointCalls(loop.Body, targets, opById) },
        QWhile loop => loop with { Body = RewriteAdjointCalls(loop.Body, targets, opById) },
        QRepeat loop => loop with { Body = RewriteAdjointCalls(loop.Body, targets, opById) },
        QConjugate conjugate => conjugate with
        {
            Within = RewriteAdjointCalls(conjugate.Within, targets, opById),
            Apply = RewriteAdjointCalls(conjugate.Apply, targets, opById),
        },
        _ => stmt,
    };

    private static QGate RewriteGate(
        QGate gate,
        IReadOnlyDictionary<int, InverseTarget> targets,
        IReadOnlyDictionary<int, QOperation> opById)
    {
        if (gate.CalleeOpId is int operationId)
        {
            if (!opById.ContainsKey(operationId))
                throw new InvalidOperationException(
                    $"QINTERNAL: call `{gate.Name}` carries dangling CalleeOpId {operationId}");
            if (gate.Functors.FirstOrDefault() != "Adjoint")
                return gate;
            if (!targets.TryGetValue(operationId, out var target))
                throw new InvalidOperationException(
                    $"QINTERNAL: no synthesized inverse exists for Adjoint target `{gate.Name}` ({operationId})");
            return gate with
            {
                Name = target.Name,
                Functors = gate.Functors.Skip(1).ToList(),
                CalleeOpId = target.OperationId,
            };
        }

        if (!IsBuiltinStatement(gate.Name))
            throw new InvalidOperationException(
                $"QINTERNAL: user-callable-looking gate `{gate.Name}` reached adjoint materialization without CalleeOpId");
        return gate;
    }

    /// <summary>Collect user-operation identities invoked under an <c>Adjoint</c> functor.</summary>
    private static void CollectAdjointRefs(
        IReadOnlyList<QStmt> stmts,
        IReadOnlyDictionary<int, QOperation> opById,
        HashSet<int> into)
    {
        foreach (var stmt in stmts)
            switch (stmt)
            {
                case QGate gate when gate.CalleeOpId is int operationId:
                    if (!opById.ContainsKey(operationId))
                        throw new InvalidOperationException(
                            $"QINTERNAL: call `{gate.Name}` carries dangling CalleeOpId {operationId}");
                    if (gate.Functors.FirstOrDefault() == "Adjoint")
                        into.Add(operationId);
                    break;
                case QGate gate when !IsBuiltinStatement(gate.Name):
                    throw new InvalidOperationException(
                        $"QINTERNAL: user-callable-looking gate `{gate.Name}` reached adjoint discovery without CalleeOpId");
                case QIf conditional:
                    CollectAdjointRefs(conditional.Then, opById, into);
                    CollectAdjointRefs(conditional.Else, opById, into);
                    break;
                case QFor loop:
                    CollectAdjointRefs(loop.Body, opById, into);
                    break;
                case QWhile loop:
                    CollectAdjointRefs(loop.Body, opById, into);
                    break;
                case QRepeat loop:
                    CollectAdjointRefs(loop.Body, opById, into);
                    break;
                case QConjugate conjugate:
                    CollectAdjointRefs(conjugate.Within, opById, into);
                    CollectAdjointRefs(conjugate.Apply, opById, into);
                    break;
            }
    }

    private static bool IsBuiltinStatement(string name) =>
        QoraGates.Names.ContainsKey(name)
        || QoraGates.MeasureLike.Contains(name)
        || QoraGates.NonUnitary.Contains(name)
        || name == "reset";

    private sealed record InverseTarget(int OperationId, string Name);
}
