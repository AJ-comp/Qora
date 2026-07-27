using Qora.Ir.Passes;

namespace Qora.Ir;

/// <summary>
/// OpenQASM-only lowering of a statically known <c>Qubit[]</c>/<c>bit[]</c> <c>.Count</c> to an integer
/// literal. OpenQASM represents those values as fixed-width registers, and <c>sizeof</c> is not a
/// portable replacement for every register form. General classical arrays deliberately keep
/// <c>.Count</c>; the emitter lowers those members to <c>sizeof(array)</c>.
///
/// This pass consumes the exact validated HIR semantic context. Lengths come from semantic
/// <see cref="Symbol"/> facts, while a small transient lexical environment joins each source spelling to
/// the nearest <see cref="SymbolId"/>. The environment is updated at the declaration point, rather than
/// built as one operation-wide name map, so an inner declaration cannot steal an outer register's length
/// and an initializer still sees the enclosing binding it is allowed to reference.
///
/// The current target tree reuses HIR expression records as a transitional representation. Expressions
/// have no node identity, and every enclosing statement/parameter/operation is rebuilt with <c>with</c>,
/// preserving its existing Id. Consequently this lowering creates no identity-bearing node and needs no
/// target derivation table. A future ID-based MIR-to-target lowering should carry bound operands directly
/// and will replace this lexical bridge.
/// </summary>
internal static class OpenQasmKnownCountLowering
{
    public static QProgram Run(
        QProgram program,
        IHirSemanticContext semantics)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(semantics);

        return program with
        {
            Operations = program.Operations
                .Select(operation => LowerOperation(operation, semantics))
                .ToList(),
        };
    }

    private readonly record struct Binding(
        SymbolId SymbolId,
        int? KnownCount);

    private static QOperation LowerOperation(
        QOperation operation,
        IHirSemanticContext semantics)
    {
        _ = semantics.FindRootScope(operation.Id)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: OpenQASM Count lowering received operation `{operation.Name}` " +
                $"(node {operation.Id}) without an exact HIR semantic scope");

        var root = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var parameter in operation.Params)
            Bind(root, parameter.Id, parameter.Name, semantics);

        // `use` declarations are semantically hoisted and may be referenced before their textual site.
        // Seed every one before rewriting the body, matching the validated HIR visibility rule.
        CollectUses(operation.Body, root, semantics);

        return operation with
        {
            Body = LowerBlock(operation.Body, root, semantics),
        };
    }

    private static void CollectUses(
        IReadOnlyList<QStmt> statements,
        Dictionary<string, Binding> root,
        IHirSemanticContext semantics)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case QUse use:
                    Bind(root, use.Id, use.Name, semantics);
                    break;
                case QIf branch:
                    CollectUses(branch.Then, root, semantics);
                    CollectUses(branch.Else, root, semantics);
                    break;
                case QFor loop:
                    CollectUses(loop.Body, root, semantics);
                    break;
                case QWhile loop:
                    CollectUses(loop.Body, root, semantics);
                    break;
                case QRepeat loop:
                    CollectUses(loop.Body, root, semantics);
                    break;
                case QConjugate conjugation:
                    CollectUses(conjugation.Within, root, semantics);
                    CollectUses(conjugation.Apply, root, semantics);
                    break;
            }
        }
    }

    private static IReadOnlyList<QStmt> LowerBlock(
        IReadOnlyList<QStmt> statements,
        IReadOnlyDictionary<string, Binding> outer,
        IHirSemanticContext semantics,
        Dictionary<string, Binding>? finalBindings = null)
    {
        var bindings = Copy(outer);
        var lowered = new List<QStmt>(statements.Count);

        foreach (var statement in statements)
        {
            QStmt result;
            switch (statement)
            {
                case QGate gate:
                    result = gate with
                    {
                        Args = gate.Args
                            .Select(argument => LowerArgument(argument, bindings))
                            .ToList(),
                    };
                    break;

                case QDecl declaration:
                    // Point of declaration: the initializer resolves first, then the new binding shadows
                    // the enclosing one for the remainder of this block.
                    result = declaration with
                    {
                        Value = LowerValue(declaration.Value, bindings),
                    };
                    Bind(bindings, declaration.Id, declaration.Name, semantics);
                    break;

                case QAssign assignment:
                    result = assignment with
                    {
                        Index = LowerNode(assignment.Index, bindings),
                        Value = LowerValue(assignment.Value, bindings),
                    };
                    break;

                case QReturn @return:
                    result = @return with
                    {
                        Value = LowerValue(@return.Value, bindings),
                    };
                    break;

                case QIf branch:
                    result = branch with
                    {
                        Cond = branch.Cond with
                        {
                            Tree = LowerNode(branch.Cond.Tree, bindings),
                        },
                        Then = LowerBlock(branch.Then, bindings, semantics),
                        Else = LowerBlock(branch.Else, bindings, semantics),
                    };
                    break;

                case QFor loop:
                {
                    var bodyBindings = Copy(bindings);
                    Bind(bodyBindings, loop.Id, loop.Var, semantics);
                    result = loop with
                    {
                        From = LowerNode(loop.From, bindings)!,
                        To = LowerNode(loop.To, bindings)!,
                        Step = LowerNode(loop.Step, bindings),
                        Body = LowerBlock(loop.Body, bodyBindings, semantics),
                    };
                    break;
                }

                case QWhile loop:
                    result = loop with
                    {
                        Cond = loop.Cond with
                        {
                            Tree = LowerNode(loop.Cond.Tree, bindings),
                        },
                        Body = LowerBlock(loop.Body, bindings, semantics),
                    };
                    break;

                case QRepeat loop:
                {
                    // `until` executes in the repeat body's scope and therefore sees declarations made at
                    // the body's top level.
                    var bodyFinal = new Dictionary<string, Binding>(StringComparer.Ordinal);
                    var body = LowerBlock(
                        loop.Body,
                        bindings,
                        semantics,
                        bodyFinal);
                    result = loop with
                    {
                        Body = body,
                        Until = loop.Until with
                        {
                            Tree = LowerNode(loop.Until.Tree, bodyFinal),
                        },
                    };
                    break;
                }

                case QConjugate conjugation:
                    result = conjugation with
                    {
                        Within = LowerBlock(conjugation.Within, bindings, semantics),
                        Apply = LowerBlock(conjugation.Apply, bindings, semantics),
                    };
                    break;

                default:
                    result = statement;
                    break;
            }

            lowered.Add(result);
        }

        if (finalBindings is not null)
        {
            finalBindings.Clear();
            foreach (var (name, binding) in bindings)
                finalBindings.Add(name, binding);
        }

        return lowered;
    }

    private static QArg LowerArgument(
        QArg argument,
        IReadOnlyDictionary<string, Binding> bindings) =>
        argument switch
        {
            QTextArg text => text with
            {
                Tree = LowerNode(text.Tree, bindings),
            },
            QQubitArg qubit => qubit with
            {
                Index = LowerNode(qubit.Index, bindings)!,
            },
            _ => argument,
        };

    private static QExpr LowerValue(
        QExpr value,
        IReadOnlyDictionary<string, Binding> bindings) =>
        value switch
        {
            QText text => text with
            {
                Tree = LowerNode(text.Tree, bindings),
            },
            QMeasure measurement => measurement with
            {
                Target = LowerNode(measurement.Target, bindings)!,
            },
            QArrayLiteral literal => literal with
            {
                Elements = literal.Elements
                    .Select(element => LowerValue(element, bindings))
                    .ToList(),
            },
            _ => value,
        };

    private static QNode? LowerNode(
        QNode? node,
        IReadOnlyDictionary<string, Binding> bindings) =>
        node switch
        {
            null => null,
            QMember { Base: QNameRef name, Member: "Count" }
                when bindings.TryGetValue(name.Name, out var binding)
                     && binding.KnownCount is int count =>
                new QNumLit(count),
            QMember member => member with
            {
                Base = LowerNode(member.Base, bindings)!,
            },
            QUnary unary => unary with
            {
                Operand = LowerNode(unary.Operand, bindings)!,
            },
            QBinOp binary => binary with
            {
                Left = LowerNode(binary.Left, bindings)!,
                Right = LowerNode(binary.Right, bindings)!,
            },
            QIndexNode index => index with
            {
                Base = LowerNode(index.Base, bindings)!,
                Index = LowerNode(index.Index, bindings)!,
            },
            QCallNode call => call with
            {
                Args = call.Args
                    .Select(argument => LowerNode(argument, bindings)!)
                    .ToList(),
            },
            _ => node,
        };

    private static void Bind(
        Dictionary<string, Binding> bindings,
        int declarationNodeId,
        string sourceName,
        IHirSemanticContext semantics)
    {
        var symbol = semantics.FindSymbol(declarationNodeId)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: OpenQASM Count lowering cannot resolve declaration " +
                $"`{sourceName}` (node {declarationNodeId}) in the exact HIR semantic context");
        if (!string.Equals(symbol.SourceName, sourceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"QINTERNAL: OpenQASM Count lowering resolved declaration node {declarationNodeId} " +
                $"as `{symbol.SourceName}`, not `{sourceName}`");

        var knownCount = KnownRegisterCount(symbol);
        if (symbol is { IsArray: true, Type: QType.Qubit or QType.Bit }
            && knownCount is null)
        {
            throw new InvalidOperationException(
                $"QINTERNAL: OpenQASM Count lowering received unsized " +
                $"{symbol.Type}[] declaration `{sourceName}` (node {declarationNodeId})");
        }

        bindings[sourceName] = new Binding(symbol.Id, knownCount);
    }

    private static Dictionary<string, Binding> Copy(
        IReadOnlyDictionary<string, Binding> source)
    {
        var copy = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var (name, binding) in source)
            copy.Add(name, binding);
        return copy;
    }

    private static int? KnownRegisterCount(Symbol symbol)
    {
        if (!symbol.IsArray)
            return null;
        return symbol.Type switch
        {
            QType.Qubit => symbol.RegisterSize,
            QType.Bit => symbol.ArrayLength,
            _ => null,
        };
    }
}
