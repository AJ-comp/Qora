namespace Qora.Ir.Passes;

/// <summary>
/// Derives each statement's enclosing control-flow containers from the HIR tree. The map is computed and
/// never stored as a second mutable nesting ledger.
/// </summary>
internal static class ContainerMap
{
    /// <summary>
    /// Builds a statement-identity to outermost-first container chain map for one callable.
    /// </summary>
    public static IReadOnlyDictionary<
        HirNodeId,
        IReadOnlyList<HirStatement>> Build(
        HirCallable callable)
    {
        var map =
            new Dictionary<
                HirNodeId,
                IReadOnlyList<HirStatement>>();

        Visit(
            callable,
            (statement, chain) =>
                map[statement.Id] = chain.ToArray());

        return map;
    }

    /// <summary>
    /// Visits each statement exactly once with its live outermost-first container stack. Consumers that
    /// retain the chain must snapshot it.
    /// </summary>
    public static void Visit(
        HirCallable callable,
        Action<
            HirStatement,
            IReadOnlyList<HirStatement>> visit) =>
        Walk(
            callable.Body,
            new List<HirStatement>(),
            visit);

    private static void Walk(
        IReadOnlyList<HirStatement> body,
        List<HirStatement> stack,
        Action<
            HirStatement,
            IReadOnlyList<HirStatement>> visit)
    {
        foreach (var statement in body)
        {
            visit(statement, stack);

            switch (statement)
            {
                case HirIfStatement @if:
                    stack.Add(@if);
                    Walk(@if.Then, stack, visit);
                    Walk(@if.Else, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;

                case HirForStatement @for:
                    stack.Add(@for);
                    Walk(@for.Body, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;

                case HirWhileStatement @while:
                    stack.Add(@while);
                    Walk(@while.Body, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;

                case HirRepeatStatement repeat:
                    stack.Add(repeat);
                    Walk(repeat.Body, stack, visit);
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }
        }
    }
}
