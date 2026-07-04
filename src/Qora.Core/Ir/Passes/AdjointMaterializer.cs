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
public static class AdjointMaterializer
{
    /// <summary>The transformed program plus one note per synthesized inverse-def name that had to dodge a
    /// user name (surfaced as a <c>// Qora:</c> comment, alongside the mangler's own rename notes).</summary>
    public sealed record Result(QProgram Program, IReadOnlyList<string> Notes);

    public static Result Run(QProgram program)
    {
        if (program.Operations.Count == 0) return new Result(program, Array.Empty<string>());

        var opNames = program.Operations.Select(o => o.Name).ToHashSet();
        var inverter = new Inverter(program.Operations);

        // Close the set of ops that need an inverse def: seed with the Adjoint-refs in every body, then
        // follow each inverse body's own Adjoint-refs (`Foo`'s inverse calls `Adjoint Bar`) to a fixpoint.
        var adjBody = new Dictionary<string, IReadOnlyList<QStmt>>();  // op -> its inverse body (raw)
        var adjName = new Dictionary<string, string>();                // op -> synthesized inverse-def name
        var order = new List<string>();                                // discovery order (stable emission)
        var minted = new HashSet<string>(opNames);                     // taken names: ops + adj names so far

        var seen = new HashSet<string>();
        var work = new Queue<string>();
        void Enqueue(IReadOnlyList<QStmt> body)
        {
            var refs = new HashSet<string>();
            CollectAdjointRefs(body, opNames, refs);
            foreach (var r in refs) if (seen.Add(r)) work.Enqueue(r);
        }

        foreach (var o in program.Operations) Enqueue(o.Body);
        while (work.Count > 0)
        {
            var name = work.Dequeue();
            if (!opNames.Contains(name) || adjBody.ContainsKey(name)) continue;
            // Invertibility is guaranteed for any Adjoint that reaches emission (QSEM001, transitively);
            // skip defensively rather than crash if a body somehow is not invertible.
            if (!inverter.TryInvertOperation(name, out var inverse, out _)) continue;
            adjBody[name] = inverse;
            order.Add(name);
            // Unique among ALL op names (+ adj names minted so far) so each op keeps a distinct key; the
            // mangler still resolves any remaining collision with a user declaration afterwards.
            var candidate = name + "__adj";
            while (minted.Contains(candidate)) candidate += "_";
            minted.Add(candidate);
            adjName[name] = candidate;
            Enqueue(inverse);
        }

        if (order.Count == 0) return new Result(program, Array.Empty<string>());

        var opByName = program.Operations.ToDictionary(o => o.Name);

        // Rewrite `Adjoint Foo(...)` -> `Foo__adj(...)` in every existing body, then append the synthesized
        // inverse defs (their bodies get the same rewrite: one inverse can call another op's inverse).
        var result = program.Operations
            .Select(o => o with { Body = RewriteAdjointCalls(o.Body, adjName, opNames) })
            .ToList();
        var notes = new List<string>();
        foreach (var name in order)
        {
            var orig = opByName[name];
            // When the canonical `Foo__adj` was already a user name, the inverse took `Foo__adj_` (etc.).
            // Note it so a reader who sees the adjusted def name knows why (dots are flattened to match the
            // emitted identifier, mirroring the mangler). No note when the canonical name was free.
            if (adjName[name] != name + "__adj")
                notes.Add($"inverse of `{orig.DisplayName ?? orig.Name}` emitted as `{Flat(adjName[name])}` (the name `{Flat(name)}__adj` was already taken)");
            result.Add(orig with
            {
                Name = adjName[name],
                DisplayName = "Adjoint " + (orig.DisplayName ?? orig.Name),
                Body = RewriteAdjointCalls(adjBody[name], adjName, opNames),
            });
        }
        return new Result(program with { Operations = result }, notes);
    }

    /// <summary>Flatten a namespaced name's dots to match the emitted identifier (as NameMangler does).</summary>
    private static string Flat(string name) => name.Replace(".", "_");

    // --- rewrite: `Adjoint <user-op>` call -> plain call to the synthesized inverse def ---

    private static IReadOnlyList<QStmt> RewriteAdjointCalls(
        IReadOnlyList<QStmt> stmts, Dictionary<string, string> adjName, HashSet<string> opNames) =>
        stmts.Select(s => RewriteStmt(s, adjName, opNames)).ToList();

    private static QStmt RewriteStmt(QStmt s, Dictionary<string, string> adjName, HashSet<string> opNames) => s switch
    {
        QGate g when opNames.Contains(g.Name) && g.Functors.FirstOrDefault() == "Adjoint" && adjName.ContainsKey(g.Name)
            => g with { Name = adjName[g.Name], Functors = g.Functors.Skip(1).ToList() },
        QIf i => i with { Then = RewriteAdjointCalls(i.Then, adjName, opNames), Else = RewriteAdjointCalls(i.Else, adjName, opNames) },
        QFor f => f with { Body = RewriteAdjointCalls(f.Body, adjName, opNames) },
        QWhile w => w with { Body = RewriteAdjointCalls(w.Body, adjName, opNames) },
        QRepeat r => r with { Body = RewriteAdjointCalls(r.Body, adjName, opNames) },
        QConjugate c => c with { Within = RewriteAdjointCalls(c.Within, adjName, opNames), Apply = RewriteAdjointCalls(c.Apply, adjName, opNames) },
        _ => s,
    };

    /// <summary>Collect user-op names invoked under an <c>Adjoint</c> functor (recursing into control flow).</summary>
    private static void CollectAdjointRefs(IReadOnlyList<QStmt> stmts, HashSet<string> ops, HashSet<string> into)
    {
        foreach (var stmt in stmts)
            switch (stmt)
            {
                case QGate g when g.Functors.FirstOrDefault() == "Adjoint" && ops.Contains(g.Name):
                    into.Add(g.Name);
                    break;
                case QIf i: CollectAdjointRefs(i.Then, ops, into); CollectAdjointRefs(i.Else, ops, into); break;
                case QFor f: CollectAdjointRefs(f.Body, ops, into); break;
                case QWhile w: CollectAdjointRefs(w.Body, ops, into); break;
                case QRepeat r: CollectAdjointRefs(r.Body, ops, into); break;
                case QConjugate c: CollectAdjointRefs(c.Within, ops, into); CollectAdjointRefs(c.Apply, ops, into); break;
            }
    }
}
