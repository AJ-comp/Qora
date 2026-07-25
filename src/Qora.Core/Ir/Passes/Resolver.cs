namespace Qora.Ir.Passes;

/// <summary>
/// Resolves every user-callable reference to one declaration. Program ownership lives in a
/// <see cref="ProgramSymbolGraph"/>:
/// <code>
/// global namespace symbol
/// ├─ global callables
/// └─ namespace A
///    ├─ A's callables
///    └─ namespace B
///       └─ A.B's callables
/// </code>
/// Each ownership edge is the child's <see cref="Symbol.OwnerSymbolId"/>. Thus an unqualified call in
/// <c>A.B</c> walks <c>A.B -&gt; A -&gt; global</c>, while a qualified call starts at the global symbol
/// and follows every namespace segment exactly. <c>open</c> is deliberately not another ownership edge:
/// only direct callable members of the namespaces opened by the caller's exact namespace are inspected,
/// so two matches remain an explicit QSEM018 ambiguity and opens never become transitive.
///
/// Lexical <see cref="Scope"/> objects are separate: they contain parameters, registers, variables and
/// block-local declarations only. Callable resolution never walks a lexical scope, and operation root
/// scopes therefore need no namespace/global parent.
///
/// Both statement calls (<see cref="QGate"/>) and calls nested anywhere in an expression
/// (<see cref="QCallNode"/>) receive the canonical name and the declaring operation's stable node Id.
/// Later passes follow that Id instead of re-matching a spelling that may change during specialization
/// or mangling.
/// </summary>
public static class Resolver
{
    public static (QProgram Program, List<QoraError> Errors) Resolve(QProgram program)
    {
        var errors = new List<QoraError>();

        // Build the declaration graph before resolving any body, so forward calls and declarations across
        // repeated namespace blocks share the same namespace/callable symbols.
        var programSymbols = SymbolTableBuilder.BuildProgramSymbols(program);

        // Empty/open-only namespace blocks are represented by Opens keys; operation-bearing namespaces
        // come from declarations. The intrinsic namespace exists implicitly but users may not declare it.
        var declaredNamespacePaths = program.Operations.Select(operation => operation.Namespace)
            .Where(namespacePath => namespacePath.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (program.Opens is not null)
            foreach (var declared in program.Opens.Keys)
                declaredNamespacePaths.Add(declared);

        foreach (var namespacePath in declaredNamespacePaths.Where(namespacePath =>
                     namespacePath == QoraGates.IntrinsicNamespace
                     || namespacePath.StartsWith(QoraGates.IntrinsicNamespace + ".", StringComparison.Ordinal)))
            Add(errors, "QSEM013",
                $"namespace `{namespacePath}` is reserved for the built-in gates; choose another name",
                program.Operations.FirstOrDefault(operation => operation.Namespace == namespacePath)?.Span);

        // `open` only shortens names from a namespace that is already loaded into this program.
        if (program.Opens is not null)
            foreach (var (owner, opens) in program.Opens)
                foreach (var open in opens.DistinctBy(item => item.Target)
                             .Where(item =>
                             {
                                 var target = programSymbols.FindNamespace(item.Target);
                                 return target is null
                                        || (target.Origin == SymbolOrigin.Builtin
                                            && item.Target != QoraGates.IntrinsicNamespace);
                             }))
                    Add(errors, "QSEM019",
                        $"in namespace `{owner}`: `open {open.Target};` names an unknown namespace — `open` only makes loaded names shorter; if `{open.Target}` lives in another file, `import` that file first",
                        open.Span);

        var resolvedOperations = new List<QOperation>(program.Operations.Count);
        foreach (var operation in program.Operations)
        {
            var operationSymbol = programSymbols.FindDeclaration(operation.Id)
                ?? throw new InvalidOperationException(
                    $"QINTERNAL: operation `{operation.Name}` has no program symbol");
            var callerNamespace = operation.Namespace.Length == 0
                ? programSymbols.RootSymbol
                : programSymbols.FindNamespace(operation.Namespace) ?? programSymbols.RootSymbol;
            var operationResolver = new OperationResolver(
                program, programSymbols, callerNamespace, operation.Namespace, operation.Name,
                errors);
            resolvedOperations.Add(operation with
            {
                Name = programSymbols.QualifiedName(operationSymbol),
                Body = operationResolver.ResolveBody(operation.Body),
            });
        }

        return (program with { Operations = resolvedOperations }, errors);
    }

    private enum CallForm
    {
        Statement,
        Expression,
    }

    private readonly record struct ResolvedCallee(string Name, int? OperationId);

    private sealed class OperationResolver
    {
        private readonly QProgram _program;
        private readonly ProgramSymbolGraph _programSymbols;
        private readonly Symbol _callerNamespace;
        private readonly string _namespace;
        private readonly string _operationName;
        private readonly List<QoraError> _errors;

        internal OperationResolver(
            QProgram program,
            ProgramSymbolGraph programSymbols,
            Symbol callerNamespace,
            string namespacePath,
            string operationName,
            List<QoraError> errors)
        {
            _program = program;
            _programSymbols = programSymbols;
            _callerNamespace = callerNamespace;
            _namespace = namespacePath;
            _operationName = operationName;
            _errors = errors;
        }

        internal IReadOnlyList<QStmt> ResolveBody(IReadOnlyList<QStmt> statements) =>
            statements.Select(ResolveStatement).ToList();

        private QStmt ResolveStatement(QStmt statement) => statement switch
        {
            QGate gate => ResolveGate(gate),
            QDecl declaration => declaration with { Value = ResolveExpression(declaration.Value, declaration.Span) },
            QAssign assignment => assignment with
            {
                Index = ResolveNode(assignment.Index, assignment.Span),
                Value = ResolveExpression(assignment.Value, assignment.Span),
            },
            QReturn returned => returned with { Value = ResolveExpression(returned.Value, returned.Span) },
            QIf branch => branch with
            {
                Cond = ResolveCondition(branch.Cond, branch.Span),
                Then = ResolveBody(branch.Then),
                Else = ResolveBody(branch.Else),
            },
            QFor loop => loop with
            {
                From = ResolveNode(loop.From, loop.Span)!,
                To = ResolveNode(loop.To, loop.Span)!,
                Step = ResolveNode(loop.Step, loop.Span),
                Body = ResolveBody(loop.Body),
            },
            QWhile loop => loop with
            {
                Cond = ResolveCondition(loop.Cond, loop.Span),
                Body = ResolveBody(loop.Body),
            },
            QRepeat loop => loop with
            {
                Body = ResolveBody(loop.Body),
                Until = ResolveCondition(loop.Until, loop.Span),
            },
            QConjugate conjugate => conjugate with
            {
                Within = ResolveBody(conjugate.Within),
                Apply = ResolveBody(conjugate.Apply),
            },
            _ => statement,
        };

        private QGate ResolveGate(QGate gate)
        {
            var arguments = gate.Args.Select(argument => ResolveArgument(argument, gate.Span)).ToList();
            var callee = ResolveCallee(gate.Name, CallForm.Statement, gate.Span);
            return gate with
            {
                Args = arguments,
                Name = callee.Name,
                CalleeOpId = callee.OperationId,
            };
        }

        private QArg ResolveArgument(QArg argument, QSpan? span) => argument switch
        {
            QTextArg text => text with { Tree = ResolveNode(text.Tree, span) },
            QQubitArg qubit => qubit with { Index = ResolveNode(qubit.Index, span)! },
            _ => argument,
        };

        private QExpr ResolveExpression(QExpr expression, QSpan? span) => expression switch
        {
            QText text => text with { Tree = ResolveNode(text.Tree, span) },
            QMeasure measurement => measurement with { Target = ResolveNode(measurement.Target, span)! },
            QArrayLiteral literal => literal with
            {
                Elements = literal.Elements.Select(element => ResolveExpression(element, span)).ToList(),
            },
            _ => expression,
        };

        private QCond ResolveCondition(QCond condition, QSpan? span) =>
            condition with { Tree = ResolveNode(condition.Tree, span) };

        private QNode? ResolveNode(QNode? node, QSpan? span) => node switch
        {
            null => null,
            QUnary unary => unary with { Operand = ResolveNode(unary.Operand, span)! },
            QBinOp binary => binary with
            {
                Left = ResolveNode(binary.Left, span)!,
                Right = ResolveNode(binary.Right, span)!,
            },
            QMember member => member with { Base = ResolveNode(member.Base, span)! },
            QIndexNode index => index with
            {
                Base = ResolveNode(index.Base, span)!,
                Index = ResolveNode(index.Index, span)!,
            },
            QCallNode call => ResolveExpressionCall(call, span),
            _ => node,
        };

        private QCallNode ResolveExpressionCall(QCallNode call, QSpan? span)
        {
            var arguments = call.Args.Select(argument => ResolveNode(argument, span)!).ToList();
            var callee = ResolveCallee(call.Name, CallForm.Expression, span);
            return call with
            {
                Args = arguments,
                Name = callee.Name,
                CalleeOpId = callee.OperationId,
            };
        }

        private ResolvedCallee ResolveCallee(string name, CallForm form, QSpan? span)
        {
            if (name.Contains('.'))
                return ResolveQualified(name, span);

            if (QoraGates.MeasureLike.Contains(name))
                return new ResolvedCallee(name, null);
            if (form == CallForm.Expression && QoraGates.Functions.ContainsKey(name))
                return new ResolvedCallee(name, null);

            var isBuiltinGate = QoraGates.Names.ContainsKey(name);
            var enclosing = _programSymbols.LookupCallableOutward(_callerNamespace.Id, name);
            if (enclosing is not null)
            {
                var isGlobal = enclosing.OwnerSymbolId == _programSymbols.RootSymbol.Id;
                if (!isBuiltinGate) return Bound(enclosing);
                if (!isGlobal)
                {
                    Add(_errors, "QSEM018",
                        BuiltinAmbiguity(name, $"`{_programSymbols.QualifiedName(enclosing)}`"), span);
                    return new ResolvedCallee(name, null);
                }
                // A same-named global declaration is invalid on its own (QSEM013), so the built-in
                // remains the only usable meaning while validation reports that declaration.
            }

            var opens = _program.Opens is not null
                        && _namespace.Length > 0
                        && _program.Opens.TryGetValue(_namespace, out var declaredOpens)
                ? declaredOpens
                : (IReadOnlyList<QOpen>)Array.Empty<QOpen>();
            var candidates = opens
                .Select(open => _programSymbols.FindNamespace(open.Target))
                .Where(namespaceSymbol => namespaceSymbol is not null)
                .Select(namespaceSymbol => _programSymbols.LookupMember(
                    namespaceSymbol!.Id,
                    name,
                    SymbolKind.Operation))
                .OfType<Symbol>()
                .Select(Bound)
                .DistinctBy(candidate => candidate.OperationId)
                .ToList();

            if (isBuiltinGate)
            {
                if (candidates.Count == 0) return new ResolvedCallee(name, null);
                Add(_errors, "QSEM018",
                    BuiltinAmbiguity(name,
                        string.Join(" or ", candidates.Select(candidate => $"`{candidate.Name}`"))),
                    span);
                return new ResolvedCallee(name, null);
            }

            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1)
                Add(_errors, "QSEM018",
                    $"in `{_operationName}`: `{name}` is ambiguous here: it could be {string.Join(" or ", candidates.Select(candidate => $"`{candidate.Name}`"))} — qualify the call (e.g. `{candidates[0].Name}(...)`)",
                    span);

            return new ResolvedCallee(name, null);
        }

        private ResolvedCallee ResolveQualified(string name, QSpan? span)
        {
            var segments = name.Split('.');
            var owner = _programSymbols.RootSymbol;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (_programSymbols.LookupMember(
                        owner.Id,
                        segments[i],
                        SymbolKind.Namespace) is { } next)
                {
                    owner = next;
                    continue;
                }

                var failedPath = string.Join(".", segments.Take(i + 1));
                Add(_errors, "QSEM019",
                    $"in `{_operationName}`: unknown namespace `{failedPath}` in `{name}` — if it lives in another file, `import` that file first",
                    span);
                return new ResolvedCallee(name, null);
            }

            var memberName = segments[^1];
            if (_programSymbols.LookupMember(
                    owner.Id,
                    memberName,
                    SymbolKind.Operation) is { } sourceCallable)
                return Bound(sourceCallable);

            if (_programSymbols.LookupMember(owner.Id, memberName, SymbolKind.BuiltinGate) is not null
                || _programSymbols.LookupMember(owner.Id, memberName, SymbolKind.BuiltinFunction) is not null)
                return new ResolvedCallee(memberName, null);

            Add(_errors, "QSEM019",
                $"in `{_operationName}`: namespace `{_programSymbols.QualifiedName(owner)}` has no callable `{memberName}`",
                span);
            return new ResolvedCallee(name, null);
        }

        private ResolvedCallee Bound(Symbol symbol) =>
            symbol.DeclarationNodeId is { } declarationId
                ? new ResolvedCallee(_programSymbols.QualifiedName(symbol), declarationId)
                : throw new InvalidOperationException(
                    $"QINTERNAL: source callable `{_programSymbols.QualifiedName(symbol)}` has no declaration node");

        private string BuiltinAmbiguity(string name, string userCandidates) =>
            $"in `{_operationName}`: `{name}` is ambiguous here: it could be {userCandidates} or the built-in `{name}` — qualify the call (`{QoraGates.IntrinsicNamespace}.{name}(...)` names the built-in)";
    }

    private static void Add(List<QoraError> errors, string code, string message, QSpan? span = null) =>
        errors.Add(new QoraError(message, code, span?.Start ?? -1, span?.End ?? -1));
}
