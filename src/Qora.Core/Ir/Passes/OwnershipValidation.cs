namespace Qora.Ir.Passes;

/// <summary>
/// Validates the time axis of ownership after <see cref="QoraValidator"/> has resolved every lexical name
/// to a stable <see cref="SymbolId"/> and recorded the calls whose ownership contracts are fully valid.
/// The symbol table answers which storage a spelling denotes; this pass answers whether that storage is
/// still available along each control-flow path.
/// </summary>
internal static class OwnershipValidation
{
    private sealed record Flow(
        HashSet<SymbolId>? Next,
        IReadOnlyList<HashSet<SymbolId>> Breaks);

    public static void Validate(
        HirCallable op,
        Scope root,
        HirScopeGraph scopeGraph,
        IReadOnlyDictionary<HirNodeId, IReadOnlyList<Symbol>> validMoves,
        List<QoraError> errors,
        bool deferUnknownControlFlow = false)
    {
        if (validMoves.Count == 0) return;

        var callableSymbol = scopeGraph.FindDeclaration(op.Id)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: callable `{op.Name}` has no semantic symbol");
        var callableName =
            op.DisplayName ?? scopeGraph.QualifiedName(callableSymbol);
        var symbols = root.AllSymbols().ToDictionary(symbol => symbol.Id);
        var usesByStatement = new Dictionary<HirNodeId, HashSet<SymbolId>>();
        foreach (var symbol in symbols.Values)
            foreach (var use in symbol.Uses)
            {
                if (!usesByStatement.TryGetValue(use.NodeId, out var uses))
                    usesByStatement[use.NodeId] = uses = new HashSet<SymbolId>();
                uses.Add(symbol.Id);
            }

        // Loops revisit the same statement while finding their ownership fixed point. One source use should
        // still produce one diagnostic, not one per abstract iteration.
        var reported = new HashSet<(HirNodeId StatementId, SymbolId SymbolId)>();
        AnalyzeBlock(op.Body, new HashSet<SymbolId>(), inLoop: false);
        return;

        Flow AnalyzeBlock(IReadOnlyList<HirStatement> body, HashSet<SymbolId> incoming, bool inLoop)
        {
            HashSet<SymbolId>? state = Copy(incoming);
            var breaks = new List<HashSet<SymbolId>>();

            foreach (var statement in body)
            {
                if (state is null) break;

                // A repeat condition runs after its body. Every other statement-owned expression (an if or
                // while condition, for bounds, an initializer, an assignment, and call arguments) runs first.
                if (statement is not HirRepeatStatement)
                    CheckUses(statement, state);

                switch (statement)
                {
                    case HirCallStatement:
                        ApplyMoves(statement.Id, state);
                        break;

                    case HirIfStatement conditional:
                    {
                        var conditionScope = scopeGraph.RequireScope(new HirScopeSite(
                            conditional.Id,
                            HirScopeSiteRole.IfCondition));
                        var condition = ConditionValue(conditional.Condition, conditionScope);
                        if (deferUnknownControlFlow && condition is null)
                            break;
                        var thenFlow = condition == false
                            ? new Flow(null, Array.Empty<HashSet<SymbolId>>())
                            : AnalyzeBlock(conditional.Then, state, inLoop);
                        var elseFlow = condition == true
                            ? new Flow(null, Array.Empty<HashSet<SymbolId>>())
                            : AnalyzeBlock(conditional.Else, state, inLoop);
                        state = Join(thenFlow.Next, elseFlow.Next);
                        breaks.AddRange(thenFlow.Breaks.Select(Copy));
                        breaks.AddRange(elseFlow.Breaks.Select(Copy));
                        break;
                    }

                    case HirForStatement loop:
                    {
                        var loopFlow = AnalyzeLoop(loop.Body, state, loop, conditionAfterBody: false);
                        state = loopFlow.Next;
                        breaks.AddRange(loopFlow.Breaks);
                        break;
                    }

                    case HirWhileStatement loop:
                    {
                        var loopFlow = AnalyzeLoop(loop.Body, state, loop, conditionAfterBody: false);
                        state = loopFlow.Next;
                        breaks.AddRange(loopFlow.Breaks);
                        break;
                    }

                    case HirRepeatStatement loop:
                    {
                        var loopFlow = AnalyzeLoop(loop.Body, state, loop, conditionAfterBody: true);
                        state = loopFlow.Next;
                        breaks.AddRange(loopFlow.Breaks);
                        break;
                    }

                    case HirReturnStatement:
                        state = null;
                        break;

                }
            }

            return new Flow(state, breaks);
        }

        Flow AnalyzeLoop(
            IReadOnlyList<HirStatement> body,
            HashSet<SymbolId> incoming,
            HirStatement owner,
            bool conditionAfterBody)
        {
            var bodyRole = owner switch
            {
                HirForStatement => HirScopeSiteRole.ForBody,
                HirWhileStatement => HirScopeSiteRole.WhileBody,
                HirRepeatStatement => HirScopeSiteRole.RepeatBody,
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: `{owner.GetType().Name}` is not a loop scope owner"),
            };
            var bodyScope = scopeGraph.RequireScope(new HirScopeSite(owner.Id, bodyRole));
            Scope ParentOf(Scope child) =>
                child.ParentScope
                ?? throw new InvalidOperationException(
                    $"QINTERNAL: HIR scope {child.Id} has no containment parent");
            var enclosingScope = owner switch
            {
                HirForStatement => ParentOf(scopeGraph.RequireScope(new HirScopeSite(
                    owner.Id,
                    HirScopeSiteRole.ForBinder))),
                HirWhileStatement => ParentOf(scopeGraph.RequireScope(new HirScopeSite(
                    owner.Id,
                    HirScopeSiteRole.WhileCondition))),
                HirRepeatStatement => ParentOf(bodyScope),
                _ => root,
            };
            var conditionScope = owner switch
            {
                HirWhileStatement => scopeGraph.RequireScope(new HirScopeSite(
                    owner.Id,
                    HirScopeSiteRole.WhileCondition)),
                HirRepeatStatement => scopeGraph.RequireScope(new HirScopeSite(
                    owner.Id,
                    HirScopeSiteRole.RepeatCondition)),
                _ => enclosingScope,
            };
            var condition = owner switch
            {
                HirWhileStatement loop => ConditionValue(loop.Condition, conditionScope),
                HirRepeatStatement loop => ConditionValue(loop.Until, conditionScope),
                _ => null,
            };
            int? knownIterationCap = owner switch
            {
                HirForStatement counted => IterationCap(counted, enclosingScope),
                HirWhileStatement when condition == false => 0,
                HirWhileStatement when condition == true => 2,
                HirRepeatStatement when condition == true => 1,
                HirRepeatStatement when condition == false => 2,
                _ => null,
            };
            if (deferUnknownControlFlow && knownIterationCap is null)
                return new Flow(Copy(incoming), Array.Empty<HashSet<SymbolId>>());
            var iterationCap = knownIterationCap ?? 2;
            var normalExitPossible = owner switch
            {
                HirWhileStatement when condition == true => false,
                HirRepeatStatement when condition == false => false,
                _ => true,
            };
            var mayExecuteZero = owner switch
            {
                HirForStatement => knownIterationCap is null or 0,
                HirWhileStatement => condition != true,
                HirRepeatStatement => false,
                _ => true,
            };
            if (iterationCap == 0)
                return new Flow(Copy(incoming), Array.Empty<HashSet<SymbolId>>());

            var locals = LoopLocalIds(body, owner);
            var first = AnalyzeBlock(body, incoming, inLoop: true);

            // The repeat condition sees body-local bindings and runs before the next iteration recreates
            // them, so inspect the unprojected state first.
            if (first.Next is not null && conditionAfterBody)
                CheckUses(owner, first.Next);

            var firstNext = ProjectOuter(first.Next, locals);
            var exits = first.Breaks.Select(state => ProjectOuter(state, locals)!).ToList();

            // while evaluates its condition both before entry (already checked by the caller) and on every
            // back edge. A repeat-until condition was checked above while body locals were still visible.
            if (firstNext is not null && owner is HirWhileStatement)
                CheckUses(owner, firstNext);

            // One extra abstract iteration reaches the fixed point because ownership facts only grow from
            // available to unavailable. Body-local symbols are projected out: each iteration declares a fresh
            // binding, while outer symbols retain moves made by an earlier iteration.
            if (firstNext is not null && iterationCap > 1)
            {
                var second = AnalyzeBlock(body, firstNext, inLoop: true);
                if (second.Next is not null && conditionAfterBody)
                    CheckUses(owner, second.Next);
                var secondNext = ProjectOuter(second.Next, locals);
                exits.AddRange(second.Breaks.Select(state => ProjectOuter(state, locals)!));
                if (secondNext is not null && owner is HirWhileStatement)
                    CheckUses(owner, secondNext);
            }

            HashSet<SymbolId>? after;
            if (conditionAfterBody)
            {
                // repeat executes at least once; a completed iteration is its only source-level exit.
                after = JoinMany(normalExitPossible && firstNext is not null
                    ? exits.Prepend(firstNext)
                    : exits);
            }
            else
            {
                // Only an unknown/empty for or a non-true while may execute zero times. A statically
                // non-empty for must enter its body, so an incoming path cannot bypass a non-returning
                // first iteration. A statically-true while has no source-level exit.
                IEnumerable<HashSet<SymbolId>> paths = normalExitPossible && mayExecuteZero
                    ? exits.Prepend(Copy(incoming))
                    : exits;
                if (normalExitPossible && firstNext is not null) paths = paths.Append(firstNext);
                after = JoinMany(paths);
            }

            // Breaks have been consumed by this loop and become ordinary exit paths.
            return new Flow(after, Array.Empty<HashSet<SymbolId>>());
        }

        static int? IterationCap(HirForStatement loop, Scope scope)
        {
            if (BoundFolder.Fold(loop.From, scope) is not BoundNum from
                || BoundFolder.Fold(loop.To, scope) is not BoundNum to)
                return null;
            if (from.Value > to.Value) return 0;
            return from.Value == to.Value ? 1 : 2;
        }

        HashSet<SymbolId> LoopLocalIds(IReadOnlyList<HirStatement> body, HirStatement owner)
        {
            var bodyRole = owner switch
            {
                HirForStatement => HirScopeSiteRole.ForBody,
                HirWhileStatement => HirScopeSiteRole.WhileBody,
                HirRepeatStatement => HirScopeSiteRole.RepeatBody,
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: `{owner.GetType().Name}` is not a loop scope owner"),
            };
            var bodyScope = scopeGraph.RequireScope(new HirScopeSite(owner.Id, bodyRole));
            var ids = bodyScope.AllSymbols().Select(symbol => symbol.Id).ToHashSet();
            if (owner is HirForStatement
                && scopeGraph.RequireScope(new HirScopeSite(
                    owner.Id,
                    HirScopeSiteRole.ForBinder)) is { } loopScope)
                foreach (var symbol in loopScope.LocalSymbols)
                    if (symbol.DeclarationNodeId == owner.Id)
                        ids.Add(symbol.Id);
            return ids;
        }

        void CheckUses(HirStatement statement, HashSet<SymbolId> state)
        {
            if (!usesByStatement.TryGetValue(statement.Id, out var uses)) return;
            foreach (var symbolId in uses)
            {
                if (!state.Contains(symbolId) || !reported.Add((statement.Id, symbolId))) continue;
                if (!symbols.TryGetValue(symbolId, out var symbol)) continue;
                Add(errors, "QSEM039",
                    $"in `{callableName}`: `{symbol.SourceName}` cannot be used here because ownership was moved on an earlier execution path; create or receive a new binding before using it again",
                    statement.Span);
            }
        }

        void ApplyMoves(HirNodeId statementId, HashSet<SymbolId> state)
        {
            if (!validMoves.TryGetValue(statementId, out var moves)) return;
            foreach (var symbol in moves) state.Add(symbol.Id);
        }
    }

    private static HashSet<SymbolId> Copy(HashSet<SymbolId> state) => new(state);

    private static HashSet<SymbolId>? Join(
        HashSet<SymbolId>? left,
        HashSet<SymbolId>? right)
    {
        if (left is null) return right is null ? null : Copy(right);
        if (right is null) return Copy(left);
        var joined = Copy(left);
        joined.UnionWith(right);
        return joined;
    }

    private static HashSet<SymbolId>? JoinMany(IEnumerable<HashSet<SymbolId>> states)
    {
        HashSet<SymbolId>? joined = null;
        foreach (var state in states)
            joined = Join(joined, state);
        return joined;
    }

    private static HashSet<SymbolId>? ProjectOuter(
        HashSet<SymbolId>? state,
        IReadOnlySet<SymbolId> locals)
    {
        if (state is null) return null;
        var projected = Copy(state);
        projected.ExceptWith(locals);
        return projected;
    }

    /// <summary>Return a definite compile-time condition, or null when both paths remain possible.</summary>
    private static bool? ConditionValue(HirExpression node, Scope scope) =>
        BooleanFolder.Fold(node, scope);

    private static void Add(List<QoraError> errors, string code, string message, SourceSpan? span) =>
        errors.Add(new QoraError(message, code, span));
}
