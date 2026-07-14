namespace Qora.Ir.Passes;

/// <summary>
/// The statement → enclosing-container map (the "메모장" of the uncompute design discussions): for every
/// statement in one operation, WHICH container statements (<c>if</c> / <c>for</c> / <c>while</c> /
/// <c>repeat</c> / <c>within-apply</c>) enclose it, outermost first. The qubit-event stream is a flat
/// timeline (containers emit no events, and time order says nothing about nesting), so structure questions —
/// "is this ancilla's compute inside an if, and which one?" — are answered HERE, from the IR tree, which is
/// the one place nesting is a fact. DERIVED, nothing stored: built by ONE recursive walk of the body (the
/// same walk <see cref="EffectAnalysis"/> already does to emit events — this one writes the position down),
/// then every event's <see cref="QubitEvent.StmtId"/> is a plain dictionary lookup.
/// </summary>
public static class ContainerMap
{
    /// <summary>Build the map for one operation: every statement Id (containers included, at any depth) →
    /// its enclosing container chain, OUTERMOST first. An empty chain means top-level straight-line code; a
    /// container's own chain does not include itself. An absent key means the Id is not a statement of this
    /// operation. One walk, O(1) lookups afterwards.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<QStmt>> Build(QOperation op)
    {
        var map = new Dictionary<int, IReadOnlyList<QStmt>>();
        Visit(op, (stmt, chain) => map[stmt.Id] = chain.ToArray()); // snapshot the chain enclosing THIS statement
        return map;
    }

    /// <summary>The ONE recursive walk of an operation body: invoke <paramref name="visit"/> exactly once per
    /// statement (containers included, at any depth) with its enclosing container chain, outermost first. The
    /// chain handed in is the live stack — snapshot it (<c>.ToArray()</c>) if you need to keep it past the
    /// call. Both the container map above and <see cref="StmtMap"/> are derived from this single walk, so a new
    /// container statement type is taught to the walk in ONE place and both maps learn it at once (the
    /// exhaustiveness guard in ContainerMapTests fails until a new body-bearing type is added here).</summary>
    public static void Visit(QOperation op, Action<QStmt, IReadOnlyList<QStmt>> visit)
        => Walk(op.Body, new List<QStmt>(), visit);

    private static void Walk(IReadOnlyList<QStmt> body, List<QStmt> stack, Action<QStmt, IReadOnlyList<QStmt>> visit)
    {
        foreach (var stmt in body)
        {
            visit(stmt, stack);
            switch (stmt)
            {
                case QIf i:
                    stack.Add(i);
                    Walk(i.Then, stack, visit);
                    Walk(i.Else, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QFor f:
                    stack.Add(f);
                    Walk(f.Body, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QWhile w:
                    stack.Add(w);
                    Walk(w.Body, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QRepeat r:
                    stack.Add(r);
                    Walk(r.Body, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QConjugate c:
                    stack.Add(c);
                    Walk(c.Within, stack, visit);
                    Walk(c.Apply, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }
        }
    }
}
