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
        Walk(op.Body, new List<QStmt>(), map);
        return map;
    }

    private static void Walk(IReadOnlyList<QStmt> body, List<QStmt> stack, Dictionary<int, IReadOnlyList<QStmt>> map)
    {
        foreach (var stmt in body)
        {
            map[stmt.Id] = stack.ToArray();   // snapshot of the chain enclosing THIS statement
            switch (stmt)
            {
                case QIf i:
                    stack.Add(i);
                    Walk(i.Then, stack, map);
                    Walk(i.Else, stack, map);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QFor f:
                    stack.Add(f);
                    Walk(f.Body, stack, map);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QWhile w:
                    stack.Add(w);
                    Walk(w.Body, stack, map);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QRepeat r:
                    stack.Add(r);
                    Walk(r.Body, stack, map);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case QConjugate c:
                    stack.Add(c);
                    Walk(c.Within, stack, map);
                    Walk(c.Apply, stack, map);
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }
        }
    }
}
