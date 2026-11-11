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
        // Loops revisit the same statement while finding their ownership fixed point. One source use should
        // still produce one diagnostic, not one per abstract iteration.
        var reported = new HashSet<(HirNodeId StatementId, SymbolId SymbolId)>();
        AnalyzeBlock(op.Body, new HashSet<SymbolId>(), root);
        return;

        Flow AnalyzeBlock(IReadOnlyList<HirStatement> body, HashSet<SymbolId> incoming, Scope scope)
        {
            HashSet<SymbolId>? state = Copy(incoming);
            var breaks = new List<HashSet<SymbolId>>();

            foreach (var statement in body)
            {
                if (state is null) break;

                switch (statement)
                {
                    case HirIfStatement conditional:
                    {
                        var conditionScope = scopeGraph.RequireScope(new HirScopeSite(
                            conditional.Id,
                            HirScopeSiteRole.IfCondition));
                        Evaluate(conditional.Condition, statement, state, conditionScope);
                        var condition = ConditionValue(conditional.Condition, conditionScope);
                        if (deferUnknownControlFlow && condition is null)
                            break;
                        var thenFlow = condition == false
                            ? new Flow(null, Array.Empty<HashSet<SymbolId>>())
                            : AnalyzeBlock(conditional.Then, state, scopeGraph.RequireScope(
                                new HirScopeSite(conditional.Id, HirScopeSiteRole.IfThen)));
                        var elseFlow = condition == true
                            ? new Flow(null, Array.Empty<HashSet<SymbolId>>())
                            : AnalyzeBlock(conditional.Else, state, scopeGraph.RequireScope(
                                new HirScopeSite(conditional.Id, HirScopeSiteRole.IfElse)));
                        state = Join(thenFlow.Next, elseFlow.Next);
                        breaks.AddRange(thenFlow.Breaks.Select(Copy));
                        breaks.AddRange(elseFlow.Breaks.Select(Copy));
                        break;
                    }

                    case HirForStatement loop:
                    {
                        Evaluate(loop.From, statement, state, scope);
                        Evaluate(loop.To, statement, state, scope);
                        var loopFlow = AnalyzeLoop(loop.Body, state, loop, conditionAfterBody: false);
                        state = loopFlow.Next;
                        breaks.AddRange(loopFlow.Breaks);
                        break;
                    }

                    case HirWhileStatement loop:
                    {
                        Evaluate(loop.Condition, statement, state, scopeGraph.RequireScope(
                            new HirScopeSite(loop.Id, HirScopeSiteRole.WhileCondition)));
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

                    case HirReturnStatement returned:
                        if (returned.Value is { } value)
                            Evaluate(value, statement, state, scope);
                        state = null;
                        break;
                    case HirAssignmentStatement assignment:
                        Evaluate(assignment.Target, statement, state, scope);
                        Evaluate(assignment.Value, statement, state, scope);
                        if (assignment.Target is HirNameExpression assignedName)
                            CheckName(assignedName, statement, state);
                        else if (assignment.Target is HirIndexExpression { Receiver: HirNameExpression assignedArray })
                            CheckName(assignedArray, statement, state);
                        break;
                    default:
                        foreach (var expression in HirExpressions.DirectExpressionSites(statement))
                            Evaluate(expression, statement, state, scope);
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
            var first = AnalyzeBlock(body, incoming, bodyScope);

            // The repeat condition sees body-local bindings and runs before the next iteration recreates
            // them, so inspect the unprojected state first.
            if (first.Next is not null && conditionAfterBody)
                Evaluate(((HirRepeatStatement)owner).Until, owner, first.Next, conditionScope);

            var firstNext = ProjectOuter(first.Next, locals);
            var exits = first.Breaks.Select(state => ProjectOuter(state, locals)!).ToList();

            // while evaluates its condition both before entry (already checked by the caller) and on every
            // back edge. A repeat-until condition was checked above while body locals were still visible.
            if (firstNext is not null && owner is HirWhileStatement firstWhile)
                Evaluate(firstWhile.Condition, owner, firstNext, conditionScope);

            // One extra abstract iteration reaches the fixed point because ownership facts only grow from
            // available to unavailable. Body-local symbols are projected out: each iteration declares a fresh
            // binding, while outer symbols retain moves made by an earlier iteration.
            if (firstNext is not null && iterationCap > 1)
            {
                var second = AnalyzeBlock(body, firstNext, bodyScope);
                if (second.Next is not null && conditionAfterBody)
                    Evaluate(((HirRepeatStatement)owner).Until, owner, second.Next, conditionScope);
                var secondNext = ProjectOuter(second.Next, locals);
                exits.AddRange(second.Breaks.Select(state => ProjectOuter(state, locals)!));
                if (secondNext is not null && owner is HirWhileStatement secondWhile)
                    Evaluate(secondWhile.Condition, owner, secondNext, conditionScope);
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

        void Evaluate(
            HirExpression expression,
            HirStatement owner,
            HashSet<SymbolId> state,
            Scope scope)
        {
            switch (expression)
            {
                case HirNameExpression name:
                    CheckName(name, owner, state);
                    break;
                case HirCallExpression call:
                    // Argument evaluation precedes the call itself. A nested call can consume storage
                    // before a later argument reads it, even when both occur in the same source statement.
                    foreach (var argument in call.Arguments)
                        Evaluate(argument.Expression, owner, state, scope);
                    // A borrowed/moved reference captured by an earlier argument must still be available
                    // when the callee begins, after every later argument has finished evaluating.
                    foreach (var argument in call.Arguments)
                    {
                        if (argument.Expression is HirNameExpression whole)
                            CheckName(whole, owner, state);
                        else if (argument.Expression is HirIndexExpression { Receiver: HirNameExpression receiver }
                                 && scopeGraph.FindReferencedSymbol(receiver.Id)?.Type == QType.Qubit)
                            CheckName(receiver, owner, state);
                    }
                    ApplyMoves(call.Id, state);
                    break;
                case HirIndexExpression index:
                    Evaluate(index.Receiver, owner, state, scope);
                    Evaluate(index.Index, owner, state, scope);
                    if (index.Receiver is HirNameExpression indexedArray)
                        CheckName(indexedArray, owner, state);
                    break;
                case HirBinaryExpression binary when binary.Operator is
                    HirBinaryOperator.LogicalAnd or HirBinaryOperator.LogicalOr:
                {
                    Evaluate(binary.Left, owner, state, scope);
                    var left = ConditionValue(binary.Left, scope);
                    var skipRight = binary.Operator == HirBinaryOperator.LogicalAnd ? left == false : left == true;
                    if (skipRight) break;
                    if (left is not null)
                    {
                        Evaluate(binary.Right, owner, state, scope);
                        break;
                    }
                    if (deferUnknownControlFlow) break;
                    var rightPath = Copy(state);
                    Evaluate(binary.Right, owner, rightPath, scope);
                    state.UnionWith(rightPath);
                    break;
                }
                default:
                    foreach (var child in expression.Children().OfType<HirExpression>())
                        Evaluate(child, owner, state, scope);
                    break;
            }
        }

        void CheckName(HirNameExpression name, HirStatement owner, HashSet<SymbolId> state)
        {
            if (scopeGraph.FindReferencedSymbol(name.Id) is not { } symbol
                || !state.Contains(symbol.Id)
                || !reported.Add((owner.Id, symbol.Id)))
                return;
            Add(errors, "QSEM039",
                $"in `{callableName}`: `{symbol.SourceName}` cannot be used here because ownership was moved on an earlier execution path; create or receive a new binding before using it again",
                name.Span ?? owner.Span);
        }

        void ApplyMoves(HirNodeId callId, HashSet<SymbolId> state)
        {
            if (!validMoves.TryGetValue(callId, out var moves)) return;
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
