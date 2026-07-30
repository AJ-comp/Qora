namespace Qora.Ir.Passes;

/// <summary>
/// Resolves statement and value calls through the unified HIR scope graph. Both call forms contain the
/// same <see cref="HirCallExpression"/>, so qualification and declaration binding have one implementation.
/// </summary>
internal static class Resolver
{
    public sealed record Result(
        HirRewriteResult Rewrite,
        IReadOnlyList<QoraError> Errors)
    {
        public HirProgram Program => Rewrite.Root;
    }

    public static Result Resolve(
        HirProgram program,
        HirRewriteSession rewrite)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(rewrite);
        if (!ReferenceEquals(rewrite.Source.Program, program))
            throw new ArgumentException(
                "Name resolution must consume the rewrite session's exact source program.",
                nameof(program));

        var errors = new List<QoraError>();
        var scopeGraph = SymbolTableBuilder.BuildHirScopeGraph(program);

        var declaredNamespacePaths =
            program.NamespacePaths.ToHashSet(StringComparer.Ordinal);

        foreach (var namespacePath in declaredNamespacePaths.Where(namespacePath =>
                     namespacePath == QoraGates.IntrinsicNamespace
                     || namespacePath.StartsWith(
                         QoraGates.IntrinsicNamespace + ".",
                         StringComparison.Ordinal)))
        {
            Add(
                errors,
                "QSEM013",
                $"namespace `{namespacePath}` is reserved for the built-in gates; choose another name",
                program.Callables
                    .FirstOrDefault(callable =>
                        program.NamespaceOf(callable) == namespacePath)
                    ?.Span
                ?? program.OpenDirectivesByNamespace
                    .GetValueOrDefault(namespacePath)
                    ?.FirstOrDefault()
                    ?.Span);
        }

        foreach (var (owner, opens) in
                 program.OpenDirectivesByNamespace)
        {
            foreach (var open in opens
                         .DistinctBy(item => item.Target)
                         .Where(item =>
                         {
                             var target =
                                 scopeGraph.FindNamespaceScope(item.Target);
                             var targetSymbol =
                                 target?.DeclaringSymbolId is { } symbolId
                                     ? scopeGraph.FindSymbol(symbolId)
                                     : null;
                             return target is null
                                    || (targetSymbol?.Origin
                                            == SymbolOrigin.Builtin
                                        && item.Target
                                            != QoraGates
                                                .IntrinsicNamespace);
                         }))
            {
                Add(
                    errors,
                    "QSEM019",
                    $"in namespace `{owner}`: `open {open.Target};` names an unknown namespace — `open` only makes loaded names shorter; if `{open.Target}` lives in another file, `import` that file first",
                    open.Span);
            }
        }

        IReadOnlyList<HirCallable> ResolveCallable(HirCallable callable)
        {
            var callableSymbol =
                scopeGraph.FindDeclaration(callable.Id)
                ?? throw new InvalidOperationException(
                    $"QINTERNAL: callable `{callable.Name}` has no program symbol");
            var namespacePath = program.NamespaceOf(callable);
            var callerNamespace = namespacePath.Length == 0
                ? scopeGraph.RootScope
                : scopeGraph.FindNamespaceScope(namespacePath)
                  ?? scopeGraph.RootScope;
            var callableResolver = new CallableResolver(
                scopeGraph,
                callerNamespace,
                scopeGraph.QualifiedName(callableSymbol),
                errors,
                rewrite);
            var body = callableResolver.ResolveBlock(callable.Body);
            var resolvedCallable = rewrite.RewriteCallable(
                callable,
                callable.Name,
                callable.Parameters,
                body,
                callable.IsFunction,
                callable.ReturnType,
                callable.DisplayName);

            return new[] { resolvedCallable };
        }

        var resolvedProgram = rewrite.ReplaceCallables(
            program,
            ResolveCallable);
        return new Result(
            rewrite.Publish(resolvedProgram),
            errors);
    }

    private enum CallForm
    {
        Statement,
        Expression,
    }

    private readonly record struct ResolvedCallee(
        string Name,
        HirNodeId? CallableId);

    private sealed class CallableResolver
    {
        private readonly HirScopeGraph _scopeGraph;
        private readonly Scope _callerNamespace;
        private readonly string _callableName;
        private readonly List<QoraError> _errors;
        private readonly HirRewriteSession _rewrite;

        internal CallableResolver(
            HirScopeGraph scopeGraph,
            Scope callerNamespace,
            string callableName,
            List<QoraError> errors,
            HirRewriteSession rewrite)
        {
            _scopeGraph = scopeGraph;
            _callerNamespace = callerNamespace;
            _callableName = callableName;
            _errors = errors;
            _rewrite = rewrite;
        }

        internal HirBlock ResolveBlock(HirBlock block)
        {
            var statements = new HirStatement[block.Count];
            for (var index = 0; index < block.Count; index++)
                statements[index] = ResolveStatement(block[index]);

            return _rewrite.RewriteBlock(block, statements);
        }

        private HirStatement ResolveStatement(HirStatement statement) =>
            statement switch
            {
                HirCallStatement call =>
                    _rewrite.RewriteCallStatement(
                        call,
                        call.Modifiers,
                        ResolveCall(call.Call, CallForm.Statement)),
                HirVariableDeclarationStatement declaration =>
                    _rewrite.RewriteVariableDeclaration(
                        declaration,
                        declaration.IsConst,
                        declaration.Type,
                        declaration.Name,
                        ResolveExpression(declaration.Value),
                        declaration.IsArray),
                HirAssignmentStatement assignment =>
                    _rewrite.RewriteAssignment(
                        assignment,
                        ResolveExpression(assignment.Target),
                        ResolveExpression(assignment.Value)),
                HirReturnStatement returned =>
                    _rewrite.RewriteReturn(
                        returned,
                        ResolveExpression(returned.Value)),
                HirIfStatement branch =>
                    _rewrite.RewriteIf(
                        branch,
                        ResolveExpression(branch.Condition),
                        ResolveBlock(branch.Then),
                        ResolveBlock(branch.Else)),
                HirForStatement loop =>
                    _rewrite.RewriteFor(
                        loop,
                        loop.Variable,
                        ResolveExpression(loop.From),
                        ResolveExpression(loop.To),
                        ResolveBlock(loop.Body)),
                HirWhileStatement loop =>
                    _rewrite.RewriteWhile(
                        loop,
                        ResolveExpression(loop.Condition),
                        ResolveBlock(loop.Body)),
                HirRepeatStatement loop =>
                    _rewrite.RewriteRepeat(
                        loop,
                        ResolveBlock(loop.Body),
                        ResolveExpression(loop.Until)),
                _ => statement,
            };

        private HirExpression ResolveExpression(HirExpression expression) =>
            expression switch
            {
                HirUnaryExpression unary =>
                    _rewrite.RewriteUnary(
                        unary,
                        unary.Operator,
                        ResolveExpression(unary.Operand)),
                HirBinaryExpression binary =>
                    _rewrite.RewriteBinary(
                        binary,
                        binary.Operator,
                        ResolveExpression(binary.Left),
                        ResolveExpression(binary.Right)),
                HirMemberAccessExpression member =>
                    _rewrite.RewriteMember(
                        member,
                        ResolveExpression(member.Receiver),
                        member.MemberName),
                HirIndexExpression index =>
                    _rewrite.RewriteIndex(
                        index,
                        ResolveExpression(index.Receiver),
                        ResolveExpression(index.Index)),
                HirCallExpression call =>
                    ResolveCall(call, CallForm.Expression),
                HirMeasurementExpression measurement =>
                    _rewrite.RewriteMeasurement(
                        measurement,
                        ResolveExpression(measurement.Target)),
                HirArrayLiteralExpression literal =>
                    _rewrite.RewriteArrayLiteral(
                        literal,
                        literal.Elements
                            .Select(ResolveExpression)
                            .ToArray()),
                _ => expression,
            };

        private HirCallExpression ResolveCall(
            HirCallExpression call,
            CallForm form)
        {
            var arguments = new HirArgument[call.Arguments.Count];
            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var argument = call.Arguments[index];
                var expression = ResolveExpression(argument.Expression);

                arguments[index] = _rewrite.RewriteArgument(
                    argument,
                    expression,
                    argument.Ownership,
                    argument.Access);
            }

            var callee = ResolveCallee(
                call.Name,
                form,
                call.Span);
            var calleeExpression = _rewrite.RewriteQualifiedCallee(
                call.Callee,
                callee.Name);
            return _rewrite.RewriteCall(
                call,
                calleeExpression,
                arguments,
                callee.CallableId);
        }

        private ResolvedCallee ResolveCallee(
            string name,
            CallForm form,
            SourceSpan? span)
        {
            if (name.Contains('.'))
                return ResolveQualified(name, span);

            if (QoraGates.MeasureLike.Contains(name))
                return new ResolvedCallee(name, null);
            if (form == CallForm.Expression
                && QoraGates.Functions.ContainsKey(name))
            {
                return new ResolvedCallee(name, null);
            }

            var isBuiltinGate = QoraGates.Names.ContainsKey(name);
            var enclosing = _scopeGraph.LookupCallableOutward(
                _callerNamespace.Id,
                name);
            if (enclosing is not null)
            {
                var isGlobal =
                    enclosing.DeclaringScopeId == _scopeGraph.RootScope.Id;
                if (!isBuiltinGate)
                    return Bound(enclosing);
                if (!isGlobal)
                {
                    Add(
                        _errors,
                        "QSEM018",
                        BuiltinAmbiguity(
                            name,
                            $"`{_scopeGraph.QualifiedName(enclosing)}`"),
                        span);
                    return new ResolvedCallee(name, null);
                }
            }

            var openedScopes =
                _callerNamespace.Kind == HirScopeKind.Namespace
                    ? _scopeGraph.ImportedScopes(_callerNamespace.Id)
                    : Array.Empty<Scope>();
            var candidates = new List<ResolvedCallee>();
            foreach (var namespaceScope in openedScopes)
            {
                var symbol = _scopeGraph.LookupMember(
                    namespaceScope.Id,
                    name,
                    SymbolKind.Callable);
                if (symbol is not null)
                    candidates.Add(Bound(symbol));
            }

            candidates = candidates
                .DistinctBy(candidate => candidate.CallableId)
                .ToList();

            if (isBuiltinGate)
            {
                if (candidates.Count == 0)
                    return new ResolvedCallee(name, null);
                Add(
                    _errors,
                    "QSEM018",
                    BuiltinAmbiguity(
                        name,
                        string.Join(
                            " or ",
                            candidates.Select(candidate =>
                                $"`{candidate.Name}`"))),
                    span);
                return new ResolvedCallee(name, null);
            }

            if (candidates.Count == 1)
                return candidates[0];
            if (candidates.Count > 1)
            {
                Add(
                    _errors,
                    "QSEM018",
                    $"in `{_callableName}`: `{name}` is ambiguous here: it could be {string.Join(" or ", candidates.Select(candidate => $"`{candidate.Name}`"))} — qualify the call (e.g. `{candidates[0].Name}(...)`)",
                    span);
            }

            return new ResolvedCallee(name, null);
        }

        private ResolvedCallee ResolveQualified(
            string name,
            SourceSpan? span)
        {
            var segments = name.Split('.');
            var owner = _scopeGraph.RootScope;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (_scopeGraph.LookupMember(
                        owner.Id,
                        segments[index],
                        SymbolKind.Namespace) is { } namespaceSymbol
                    && _scopeGraph.FindOwnedScope(
                        namespaceSymbol.Id,
                        HirScopeKind.Namespace) is { } next)
                {
                    owner = next;
                    continue;
                }

                var failedPath = string.Join(
                    ".",
                    segments.Take(index + 1));
                Add(
                    _errors,
                    "QSEM019",
                    $"in `{_callableName}`: unknown namespace `{failedPath}` in `{name}` — if it lives in another file, `import` that file first",
                    span);
                return new ResolvedCallee(name, null);
            }

            var memberName = segments[^1];
            if (_scopeGraph.LookupMember(
                    owner.Id,
                    memberName,
                    SymbolKind.Callable) is { } sourceCallable)
            {
                return Bound(sourceCallable);
            }

            if (_scopeGraph.LookupMember(
                    owner.Id,
                    memberName,
                    SymbolKind.BuiltinGate) is not null
                || _scopeGraph.LookupMember(
                    owner.Id,
                    memberName,
                    SymbolKind.BuiltinFunction) is not null)
            {
                return new ResolvedCallee(memberName, null);
            }

            Add(
                _errors,
                "QSEM019",
                $"in `{_callableName}`: namespace `{_scopeGraph.QualifiedName(owner)}` has no callable `{memberName}`",
                span);
            return new ResolvedCallee(name, null);
        }

        private ResolvedCallee Bound(Symbol symbol) =>
            symbol.DeclarationNodeId is { } declarationId
                ? new ResolvedCallee(
                    _scopeGraph.QualifiedName(symbol),
                    declarationId)
                : throw new InvalidOperationException(
                    $"QINTERNAL: source callable `{_scopeGraph.QualifiedName(symbol)}` has no declaration node");

        private string BuiltinAmbiguity(
            string name,
            string userCandidates) =>
            $"in `{_callableName}`: `{name}` is ambiguous here: it could be {userCandidates} or the built-in `{name}` — qualify the call (`{QoraGates.IntrinsicNamespace}.{name}(...)` names the built-in)";
    }

    private static void Add(
        List<QoraError> errors,
        string code,
        string message,
        SourceSpan? span = null) =>
        errors.Add(new QoraError(message, code, span));
}
