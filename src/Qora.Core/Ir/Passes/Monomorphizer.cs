namespace Qora.Ir.Passes;

/// <summary>
/// Gives a source-level <c>Qubit[]</c> operation the concrete register widths required by OpenQASM.
/// The length stays out of the source type: each distinct tuple of call-site lengths produces one hidden
/// specialization, and <c>q.Count</c> becomes the matching integer literal inside that copy — a
/// structural substitution on the expression trees (<see cref="QMember"/> → <see cref="QNumLit"/>).
///
/// A COMMON pass, though OpenQASM motivated it: the bounds prover's architecture is
/// <c>validate(symbolic) → monomorphize → validate(concrete)</c>, and the second validation is where
/// every "defer to mono" bounds/aliasing fact gets its precise re-check — so the prover owns this pass
/// as much as any emitter does, and static-width hardware backends (Base-profile QIR fixes its qubit
/// count too) would want the same specialization.
/// </summary>
public static class Monomorphizer
{
    public static QProgram Run(QProgram program)
    {
        // The specialization trigger IS QParam.NeedsMonoSizing — the one definition every consumer of
        // "monomorphization supplies this length" shares (validator generic test, prover deferral gates).
        static bool IsUnsizedArray(QParam p) => p.NeedsMonoSizing;
        static bool NeedsSpecialization(QOperation o) => o.Params.Any(IsUnsizedArray);

        var genericById = program.Operations.Where(NeedsSpecialization).ToDictionary(o => o.Id);
        var genericByName = program.Operations.Where(NeedsSpecialization).ToDictionary(o => o.Name);
        if (genericById.Count == 0 && !HasCount(program)) return program;

        var concrete = program.Operations.Where(o => !NeedsSpecialization(o)).ToList();
        var allNames = new HashSet<string>(program.Operations.Select(o => o.Name));
        var specs = new Dictionary<string, QOperation>();
        var specNameByKey = new Dictionary<string, string>();

        QOperation? GenericCallee(QGate gate)
        {
            if (gate.CalleeOpId is int id && genericById.TryGetValue(id, out var byId)) return byId;
            return genericByName.GetValueOrDefault(gate.Name); // hand-built IR fallback
        }

        Dictionary<string, int> ConcreteRegisters(QOperation op)
        {
            var regs = new Dictionary<string, int>();
            foreach (var p in op.Params)
                if ((p.IsQubitArray || p is { Type: QType.Bit, IsArray: true }) && p.RegisterSize is int n)
                    regs[p.Name] = n;   // sized Qubit[]/bit[] params: their .Count folds, and they can size a callee's slot

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
                        case QConjugate c: CollectUses(c.Within); CollectUses(c.Apply); break;
                    }
            }
            CollectUses(op.Body);
            return regs;
        }

        // `q.Count` with a known size becomes the literal, structurally: QMember(reg, Count) → QNumLit.
        // (A qubit index / assign index / step is a single Num-or-Ident token by grammar, so it can never
        // contain a `.Count` — those fields need no rewrite.)
        QNode? SubstCounts(QNode? node, IReadOnlyDictionary<string, int> regs) => node switch
        {
            null => null,
            QMember { Base: QNameRef r, Member: "Count" } when regs.TryGetValue(r.Name, out var size) =>
                new QNumLit(size),
            QMember m => m with { Base = SubstCounts(m.Base, regs)! },
            QBinOp b => b with { Left = SubstCounts(b.Left, regs)!, Right = SubstCounts(b.Right, regs)! },
            QUnary u => u with { Operand = SubstCounts(u.Operand, regs)! },
            QIndexNode i => i with { Base = SubstCounts(i.Base, regs)!, Index = SubstCounts(i.Index, regs)! },
            QCallNode c => c with { Args = c.Args.Select(a => SubstCounts(a, regs)!).ToList() },
            _ => node,
        };

        QArg ResolveArg(QArg arg, IReadOnlyDictionary<string, int> regs) => arg switch
        {
            QTextArg t => t with { Tree = SubstCounts(t.Tree, regs) },
            _ => arg,
        };

        QExpr ResolveExpr(QExpr expr, IReadOnlyDictionary<string, int> regs) => expr switch
        {
            QText t => t with { Tree = SubstCounts(t.Tree, regs) },
            QArrayLiteral literal => literal with
            {
                Elements = literal.Elements.Select(element => ResolveExpr(element, regs)).ToList(),
            },
            _ => expr,
        };

        /// <summary>A `bit[]` declaration binds its literal length HERE, at its own site: the initializer is
        /// folded first (it is evaluated before the name is bound), then the length enters the CURRENT block
        /// map, in effect for the rest of this block and any block nested inside it.</summary>
        QStmt ResolveDecl(QDecl d, Dictionary<string, int> regs)
        {
            var rewritten = d with { Value = ResolveExpr(d.Value, regs) };
            // A declaration SHADOWS any enclosing binding of the same name — that is what a scope chain
            // means. A `bit[]` binds its own literal length: it lowers to OpenQASM's dedicated `bit[N]`
            // register (bit is not a legal array base type) and `sizeof` is not defined on a bit register,
            // so its `.Count` must fold. EVERY OTHER declaration removes the inherited entry instead, so a
            // `.Count` on it can never fold to an enclosing `use` register's size (it keeps the general
            // `array[T, N]` form, where `sizeof` IS defined and the emitter renders it).
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

        List<QStmt> Rewrite(IReadOnlyList<QStmt> body, Dictionary<string, int> outer)
        {
            // A BLOCK gets its OWN length map, seeded from the enclosing one — the ordinary scope chain,
            // the same shape the mangler's rename map and ArrayLocalHoisting's active map use. Params and
            // `use` registers arrive already seeded at op level (they are hoisted / forward-referenceable).
            var regs = new Dictionary<string, int>(outer);
            var output = new List<QStmt>(body.Count);
            foreach (var stmt in body)
            {
                QStmt rewritten = stmt switch
                {
                    QGate g => g with { Args = g.Args.Select(a => ResolveArg(a, regs)).ToList() },
                    QDecl d => ResolveDecl(d, regs),
                    QAssign a => a with { Value = ResolveExpr(a.Value, regs) },
                    QIf i => i with
                    {
                        Cond = i.Cond with { Tree = SubstCounts(i.Cond.Tree, regs) },
                        Then = Rewrite(i.Then, regs),
                        Else = Rewrite(i.Else, regs),
                    },
                    QFor f => f with
                    {
                        From = SubstCounts(f.From, regs)!,   // bounds evaluate in the ENCLOSING block
                        To = SubstCounts(f.To, regs)!,
                        Body = Rewrite(f.Body, Shadow(regs, f.Var)),   // the loop variable shadows in the body
                    },
                    QWhile w => w with
                    {
                        Cond = w.Cond with { Tree = SubstCounts(w.Cond.Tree, regs) },
                        Body = Rewrite(w.Body, regs),
                    },
                    QRepeat r => r with
                    {
                        Body = Rewrite(r.Body, regs),
                        Until = r.Until with { Tree = SubstCounts(r.Until.Tree, regs) },
                    },
                    QConjugate c => c with
                    {
                        Within = Rewrite(c.Within, regs),
                        Apply = Rewrite(c.Apply, regs),
                    },
                    _ => stmt,
                };

                if (rewritten is QGate gate && GenericCallee(gate) is { } callee)
                    rewritten = SpecializeCall(gate, callee, regs);
                output.Add(rewritten);
            }
            return output;
        }

        QGate SpecializeCall(QGate gate, QOperation callee, IReadOnlyDictionary<string, int> regs)
        {
            var bindings = new Dictionary<int, int>(); // parameter Id -> concrete length
            for (var i = 0; i < callee.Params.Count; i++)
            {
                var parameter = callee.Params[i];
                if (!IsUnsizedArray(parameter)) continue;
                // the actual for a Qubit[] slot is a bare register name — a QNameRef since lowering.
                var actualName = i < gate.Args.Count && gate.Args[i] is QTextArg { Tree: QNameRef nr }
                    ? nr.Name
                    : null;
                if (actualName is null || !regs.TryGetValue(actualName, out var size))
                    throw new InvalidOperationException(
                        $"QINTERNAL: call to `{callee.Name}` cannot bind the size of Qubit[] parameter `{parameter.Name}` after validation");
                bindings[parameter.Id] = size;
            }

            var arrays = callee.Params.Where(IsUnsizedArray).ToList();
            if (bindings.Count != arrays.Count)
                throw new InvalidOperationException(
                    $"QINTERNAL: call to `{callee.Name}` bound {bindings.Count} of {arrays.Count} Qubit[] parameter sizes");

            var sizes = arrays.Select(p => bindings[p.Id]).ToList();
            var key = $"{callee.Id}|{string.Join(",", sizes)}";
            if (!specNameByKey.TryGetValue(key, out var specName))
            {
                specName = MakeName(callee.Name, sizes);
                specNameByKey[key] = specName;
                var spec = Specialize(callee, bindings, specName);
                specs[specName] = spec;
            }

            return gate with { Name = specName, CalleeOpId = specs[specName].Id };
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
                return IsUnsizedArray(p)
                    ? fresh with { RegisterSize = bindings[p.Id], IsArray = true }
                    : fresh;
            }).ToList();

            var shell = new QOperation(specName, parameters, Array.Empty<QStmt>(), source.Namespace)
            {
                Span = source.Span,
                DisplayName = source.DisplayName ?? source.Name,
            };
            var regs = ConcreteRegisters(shell);
            var rewrittenBody = Rewrite(source.Body, regs);
            return shell with { Body = ReId.Run(rewrittenBody) };
        }

        var outputOps = new List<QOperation>();
        foreach (var op in concrete)
            outputOps.Add(op with { Body = Rewrite(op.Body, ConcreteRegisters(op)) });
        outputOps.AddRange(specs.Values);

        var result = program with { Operations = outputOps };
        if (result.Operations.SelectMany(o => o.Params).Any(IsUnsizedArray) || HasUnresolvedQubitCount(result))
            throw new InvalidOperationException(
                "QINTERNAL: monomorphization left an unresolved Qubit[] size or qubit-array `.Count` in the emitted program");
        return result;
    }

    /// <summary>The declared length of a <c>bit[]</c>, which QSEM016/QSEM029 guarantee is a literal.</summary>
    private static int? BitArrayLength(QDecl d) => d.Value switch
    {
        QArrayLiteral literal => literal.Elements.Count,
        QArrayNew allocation => allocation.Length,
        _ => null,
    };

    private static bool HasCount(QProgram program) => program.Operations.Any(op => HasCount(op.Body));

    /// <summary>Does any expression tree mention a <c>.Count</c>? <paramref name="owners"/> null = any
    /// base name counts; otherwise only members whose base is one of the given (qubit-array) names.
    /// (Index/step tokens are grammar-atomic and can never carry a member access.)</summary>
    private static bool MentionsCount(QNode? node, IReadOnlySet<string>? owners) => node switch
    {
        null => false,
        QMember { Base: QNameRef r, Member: "Count" } => owners is null || owners.Contains(r.Name),
        QMember m => MentionsCount(m.Base, owners),
        QBinOp b => MentionsCount(b.Left, owners) || MentionsCount(b.Right, owners),
        QUnary u => MentionsCount(u.Operand, owners),
        QIndexNode i => MentionsCount(i.Base, owners) || MentionsCount(i.Index, owners),
        QCallNode c => c.Args.Any(a => MentionsCount(a, owners)),
        _ => false,
    };

    /// <summary>The owner set with one name SHADOWED (null keeps the "any base name" meaning).</summary>
    private static IReadOnlySet<string>? Shadowed(IReadOnlySet<string>? owners, string name)
    {
        if (owners is null) return null;
        var inner = new HashSet<string>(owners);
        inner.Remove(name);
        return inner;
    }

    /// <summary>Does a body still mention a <c>.Count</c> owned by <paramref name="owners"/>? The set
    /// NARROWS as the block declares names: a declaration shadows the outer meaning, so a <c>.Count</c>
    /// written after it belongs to the inner variable, not to the enclosing qubit array. This mirrors the
    /// scope chain <c>Rewrite</c> folds with — a name-only test would flag a legitimately unfolded
    /// <c>sizeof</c> on a shadowing classical array as an unresolved qubit count.</summary>
    private static bool HasCount(IReadOnlyList<QStmt> body, IReadOnlySet<string>? owners = null)
    {
        var live = owners;
        foreach (var stmt in body)
        {
            if (QNodes.ExpressionSites(stmt).Any(n => MentionsCount(n, live))) return true;   // canonical positions
            var nested = stmt switch                                                          // plus nested bodies
            {
                QIf i => HasCount(i.Then, live) || HasCount(i.Else, live),
                QFor f => HasCount(f.Body, Shadowed(live, f.Var)),
                QWhile w => HasCount(w.Body, live),
                QRepeat r => HasCount(r.Body, live),
                QConjugate c => HasCount(c.Within, live) || HasCount(c.Apply, live),
                _ => false,
            };
            if (nested) return true;
            if (stmt is QDecl d) live = Shadowed(live, d.Name);   // in effect for the REST of this block
        }
        return false;
    }

    private static bool HasUnresolvedQubitCount(QProgram program)
    {
        foreach (var op in program.Operations)
        {
            var qubitArrays = op.Params.Where(p => p.IsQubitArray).Select(p => p.Name).ToHashSet();
            void CollectUses(IReadOnlyList<QStmt> body)
            {
                foreach (var statement in body)
                    switch (statement)
                    {
                        case QUse use: qubitArrays.Add(use.Name); break;
                        case QIf branch: CollectUses(branch.Then); CollectUses(branch.Else); break;
                        case QFor loop: CollectUses(loop.Body); break;
                        case QWhile loop: CollectUses(loop.Body); break;
                        case QRepeat loop: CollectUses(loop.Body); break;
                        case QConjugate conjugate: CollectUses(conjugate.Within); CollectUses(conjugate.Apply); break;
                    }
            }
            CollectUses(op.Body);
            if (HasCount(op.Body, qubitArrays)) return true;
        }
        return false;
    }
}
