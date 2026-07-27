namespace Qora.Ir.Passes;

/// <summary>
/// Specializes source-level <c>Qubit[]</c>/<c>bit[]</c> callables by concrete call-site widths.
/// The length stays out of the source type: each distinct tuple of call-site lengths produces one hidden
/// specialization, while unreachable unsized callables disappear with the rest of the generic source
/// definitions.
///
/// Statement calls (<see cref="QGate"/>) and function calls inside expressions
/// (<see cref="QCallNode"/>) share one specialization cache and worklist.
///
/// This common pass owns only callable cloning, call retargeting, and dead-generic elimination.
/// It deliberately preserves every <c>.Count</c> expression so later HIR validation and non-OpenQASM
/// consumers continue to observe the source-level read. A target that requires literal widths performs
/// that rewrite in its own lowering boundary.
/// </summary>
internal static class Monomorphizer
{
    public sealed record Result(
        QProgram Program,
        IReadOnlyList<NodeDerivation> Derivations);

    public static Result Run(QProgram program)
    {
        var derivations = new List<NodeDerivation>();
        void Record(int sourceId, int derivedId) =>
            derivations.Add(new NodeDerivation(sourceId, derivedId));

        // The specialization trigger IS QParam.NeedsMonoSizing — the one definition every consumer of
        // "monomorphization supplies this length" shares (validator generic test, prover deferral gates).
        static bool IsUnsizedArray(QParam p) => p.NeedsMonoSizing;
        static bool NeedsSpecialization(QOperation o) => o.Params.Any(IsUnsizedArray);

        var genericById = program.Operations.Where(NeedsSpecialization).ToDictionary(o => o.Id);
        if (genericById.Count == 0)
            return new Result(program, Array.Empty<NodeDerivation>());

        var concrete = program.Operations.Where(o => !NeedsSpecialization(o)).ToList();
        var allNames = new HashSet<string>(program.Operations.Select(o => o.Name));
        var specs = new Dictionary<string, QOperation>();
        var specNameByKey = new Dictionary<string, string>();

        QOperation? GenericCallee(QGate gate)
        {
            if (gate.CalleeOpId is int id && genericById.TryGetValue(id, out var byId)) return byId;
            return null;
        }

        QOperation? GenericExpressionCallee(QCallNode call)
        {
            if (call.CalleeOpId is int boundId
                && genericById.TryGetValue(boundId, out var bound))
                return bound;
            return null;
        }

        Dictionary<string, int> ConcreteRegisters(QOperation op)
        {
            var regs = new Dictionary<string, int>();
            foreach (var p in op.Params)
                if ((p.IsQubitArray || p is { Type: QType.Bit, IsArray: true }) && p.RegisterSize is int n)
                    regs[p.Name] = n;   // sized Qubit[]/bit[] params can size a nested generic callee's slot

            // `use` declarations are semantically hoisted. Seed every allocation before rewriting so a
            // legal call/Count that textually precedes its `use` still resolves.
            void CollectUses(IReadOnlyList<QStmt> body)
            {
                foreach (var stmt in body)
                    switch (stmt)
                    {
                        case QUse u: regs[u.Name] = u.Size; break;
                        // NOTE: `bit[]` DECLARATIONS are deliberately NOT seeded here. They are block-scoped,
                        // so two same-named ones in disjoint blocks are different arrays with different
                        // lengths; a flat op-wide entry made every `.Count` fold to whichever came last.
                        // Each is bound at its own declaration site instead — see Rewrite.
                        case QIf i: CollectUses(i.Then); CollectUses(i.Else); break;
                        case QFor f: CollectUses(f.Body); break;
                        case QWhile w: CollectUses(w.Body); break;
                        case QRepeat r: CollectUses(r.Body); break;
                    }
            }
            CollectUses(op.Body);
            return regs;
        }

        // Walk every expression position because a generic function call can be nested inside another
        // call, an index, a condition, or an arithmetic expression. Count nodes are ordinary preserved
        // members here; only the OpenQASM target is allowed to replace a known Count with a literal.
        QNode? RewriteNode(QNode? node, IReadOnlyDictionary<string, int> regs) => node switch
        {
            null => null,
            QMember m => m with { Base = RewriteNode(m.Base, regs)! },
            QBinOp b => b with { Left = RewriteNode(b.Left, regs)!, Right = RewriteNode(b.Right, regs)! },
            QUnary u => u with { Operand = RewriteNode(u.Operand, regs)! },
            QIndexNode i => i with { Base = RewriteNode(i.Base, regs)!, Index = RewriteNode(i.Index, regs)! },
            QCallNode c => RewriteCall(c, regs),
            _ => node,
        };

        QCallNode RewriteCall(QCallNode call, IReadOnlyDictionary<string, int> regs)
        {
            var rewritten = call with { Args = call.Args.Select(a => RewriteNode(a, regs)!).ToList() };
            if (GenericExpressionCallee(rewritten) is not { } callee) return rewritten;

            var actualNames = rewritten.Args
                .Select(a => a is QNameRef name ? name.Name : null)
                .ToList();
            var specialization = SpecializationFor(callee, actualNames, regs);
            return rewritten with
            {
                Name = specialization.Name,
                CalleeOpId = specialization.Id,
            };
        }

        QArg ResolveArg(QArg arg, IReadOnlyDictionary<string, int> regs) => arg switch
        {
            QTextArg t => t with { Tree = RewriteNode(t.Tree, regs) },
            QQubitArg q => q with { Index = RewriteNode(q.Index, regs)! },
            _ => arg,
        };

        QExpr ResolveExpr(QExpr expr, IReadOnlyDictionary<string, int> regs) => expr switch
        {
            QText t => t with { Tree = RewriteNode(t.Tree, regs) },
            QMeasure measure => measure with { Target = RewriteNode(measure.Target, regs)! },
            QArrayLiteral literal => literal with
            {
                Elements = literal.Elements.Select(element => ResolveExpr(element, regs)).ToList(),
            },
            _ => expr,
        };

        /// <summary>A `bit[]` declaration binds its literal length HERE, at its own site: the initializer is
        /// specialized first (it is evaluated before the name is bound), then the length enters the CURRENT
        /// block map so later generic calls can bind an unsized <c>bit[]</c> parameter.</summary>
        QStmt ResolveDecl(QDecl d, Dictionary<string, int> regs)
        {
            var rewritten = d with { Value = ResolveExpr(d.Value, regs) };
            // A declaration shadows any enclosing size binding. A bit[] binds its own literal length for
            // subsequent specialization; every other declaration removes the inherited entry so a call
            // cannot accidentally borrow the width of a different same-named qubit/bit register.
            if (d is { IsArray: true, Type: QType.Bit } && BitArrayLength(d) is int len) regs[d.Name] = len;
            else regs.Remove(d.Name);
            return rewritten;
        }

        /// <summary>The enclosing length map with one name SHADOWED — a declaration of that name in the
        /// inner block means a different thing, so the outer binding must not leak into it.</summary>
        static Dictionary<string, int> Shadow(Dictionary<string, int> outer, string name)
        {
            var inner = new Dictionary<string, int>(outer);
            inner.Remove(name);
            return inner;
        }

        QRepeat RewriteRepeat(QRepeat repeat, Dictionary<string, int> outer)
        {
            // `until` executes after the body and resolves in the body's scope, so declarations made at the
            // body's top level (including a sized bit[]) must remain visible while its expression is rewritten.
            var bodyFinal = new Dictionary<string, int>();
            var rewrittenBody = Rewrite(repeat.Body, outer, bodyFinal);
            return repeat with
            {
                Body = rewrittenBody,
                Until = repeat.Until with { Tree = RewriteNode(repeat.Until.Tree, bodyFinal) },
            };
        }

        List<QStmt> Rewrite(IReadOnlyList<QStmt> body, Dictionary<string, int> outer,
            Dictionary<string, int>? finalRegs = null)
        {
            // A block gets its own specialization-size map, seeded from the enclosing one. Parameters and
            // `use` registers arrive already seeded at operation level (they are forward-referenceable).
            var regs = new Dictionary<string, int>(outer);
            var output = new List<QStmt>(body.Count);
            foreach (var stmt in body)
            {
                QStmt rewritten = stmt switch
                {
                    QGate g => g with { Args = g.Args.Select(a => ResolveArg(a, regs)).ToList() },
                    QDecl d => ResolveDecl(d, regs),
                    QAssign a => a with
                    {
                        Index = RewriteNode(a.Index, regs),
                        Value = ResolveExpr(a.Value, regs),
                    },
                    QReturn r => r with { Value = ResolveExpr(r.Value, regs) },
                    QIf i => i with
                    {
                        Cond = i.Cond with { Tree = RewriteNode(i.Cond.Tree, regs) },
                        Then = Rewrite(i.Then, regs),
                        Else = Rewrite(i.Else, regs),
                    },
                    QFor f => f with
                    {
                        From = RewriteNode(f.From, regs)!,   // bounds evaluate in the ENCLOSING block
                        To = RewriteNode(f.To, regs)!,
                        Body = Rewrite(f.Body, Shadow(regs, f.Var)),   // the loop variable shadows in the body
                    },
                    QWhile w => w with
                    {
                        Cond = w.Cond with { Tree = RewriteNode(w.Cond.Tree, regs) },
                        Body = Rewrite(w.Body, regs),
                    },
                    QRepeat r => RewriteRepeat(r, regs),
                    _ => stmt,
                };

                if (rewritten is QGate gate && GenericCallee(gate) is { } callee)
                    rewritten = SpecializeCall(gate, callee, regs);
                output.Add(rewritten);
            }
            if (finalRegs is not null)
            {
                finalRegs.Clear();
                foreach (var (name, size) in regs) finalRegs[name] = size;
            }
            return output;
        }

        QGate SpecializeCall(QGate gate, QOperation callee, IReadOnlyDictionary<string, int> regs)
        {
            var actualNames = gate.Args.Select(arg => arg is QTextArg { Tree: QNameRef name }
                ? name.Name
                : null).ToList();
            var specialization = SpecializationFor(callee, actualNames, regs);
            return gate with { Name = specialization.Name, CalleeOpId = specialization.Id };
        }

        QOperation SpecializationFor(QOperation callee, IReadOnlyList<string?> actualNames,
            IReadOnlyDictionary<string, int> regs)
        {
            var bindings = new Dictionary<int, int>(); // parameter Id -> concrete length
            for (var i = 0; i < callee.Params.Count; i++)
            {
                var parameter = callee.Params[i];
                if (!IsUnsizedArray(parameter)) continue;
                // Either call form supplies an unsized Qubit[]/bit[] slot as one bare register name.
                var actualName = i < actualNames.Count ? actualNames[i] : null;
                if (actualName is null || !regs.TryGetValue(actualName, out var size))
                    throw new InvalidOperationException(
                        $"QINTERNAL: call to `{callee.Name}` cannot bind the size of array parameter `{parameter.Name}` after validation");
                bindings[parameter.Id] = size;
            }

            var arrays = callee.Params.Where(IsUnsizedArray).ToList();
            if (bindings.Count != arrays.Count)
                throw new InvalidOperationException(
                    $"QINTERNAL: call to `{callee.Name}` bound {bindings.Count} of {arrays.Count} array parameter sizes");

            var sizes = arrays.Select(p => bindings[p.Id]).ToList();
            var key = $"{callee.Id}|{string.Join(",", sizes)}";
            if (!specNameByKey.TryGetValue(key, out var specName))
            {
                specName = MakeName(callee.Name, sizes);
                specNameByKey[key] = specName;
                var spec = Specialize(callee, bindings, specName);
                specs[specName] = spec;
            }

            return specs[specName];
        }

        string MakeName(string baseName, IReadOnlyList<int> sizes)
        {
            var name = baseName + "__sz" + string.Join("_", sizes);
            while (allNames.Contains(name)) name += "_";
            allNames.Add(name);
            return name;
        }

        QOperation Specialize(QOperation source, IReadOnlyDictionary<int, int> bindings, string specName)
        {
            var parameters = source.Params.Select(p =>
            {
                var fresh = p with { Id = QNodeIds.Next() };
                Record(p.Id, fresh.Id);
                return IsUnsizedArray(p)
                    ? fresh with { RegisterSize = bindings[p.Id], IsArray = true }
                    : fresh;
            }).ToList();

            var shell = new QOperation(specName, parameters, Array.Empty<QStmt>(), source.Namespace)
            {
                Span = source.Span,
                DisplayName = source.DisplayName ?? source.Name,
                IsFunction = source.IsFunction,
                ReturnType = source.ReturnType,
            };
            Record(source.Id, shell.Id);
            var regs = ConcreteRegisters(shell);
            var rewrittenBody = Rewrite(source.Body, regs);
            return shell with { Body = ReId.Run(rewrittenBody, Record) };
        }

        var outputOps = new List<QOperation>();
        foreach (var op in concrete)
            outputOps.Add(op with { Body = Rewrite(op.Body, ConcreteRegisters(op)) });
        outputOps.AddRange(specs.Values);

        var result = program with { Operations = outputOps };
        if (HasGenericCall(result, genericById.Keys.ToHashSet()))
            throw new InvalidOperationException(
                "QINTERNAL: monomorphization removed a generic callable while a call still points to it");
        if (result.Operations.SelectMany(o => o.Params).Any(IsUnsizedArray))
            throw new InvalidOperationException(
                "QINTERNAL: monomorphization left an unresolved Qubit[]/bit[] parameter in the specialized HIR");
        return new Result(result, derivations.AsReadOnly());
    }

    /// <summary>The declared length of a <c>bit[]</c>, which QSEM016/QSEM029 guarantee is a literal.</summary>
    private static int? BitArrayLength(QDecl d) => d.Value switch
    {
        QArrayLiteral literal => literal.Elements.Count,
        QArrayNew allocation => allocation.Length,
        _ => null,
    };

    private static bool HasGenericCall(QProgram program, IReadOnlySet<int> genericIds) =>
        program.Operations.Any(op => HasGenericCall(op.Body, genericIds));

    private static bool HasGenericCall(IReadOnlyList<QStmt> body, IReadOnlySet<int> genericIds)
    {
        foreach (var statement in body)
        {
            if (statement is QGate gate
                && gate.CalleeOpId is int id
                && genericIds.Contains(id))
                return true;
            if (QNodes.ExpressionSites(statement)
                .SelectMany(QNodes.CallsIn)
                .Any(call => call.CalleeOpId is int id && genericIds.Contains(id)))
                return true;

            var nested = statement switch
            {
                QIf branch => HasGenericCall(branch.Then, genericIds)
                              || HasGenericCall(branch.Else, genericIds),
                QFor loop => HasGenericCall(loop.Body, genericIds),
                QWhile loop => HasGenericCall(loop.Body, genericIds),
                QRepeat loop => HasGenericCall(loop.Body, genericIds),
                _ => false,
            };
            if (nested) return true;
        }
        return false;
    }
}
