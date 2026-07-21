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
                        // A `bit[]` lowers to OpenQASM's dedicated `bit[N]` register (bit is not a legal array
                        // base type), and `sizeof` is not defined on a bit register — so a bit array's `.Count`
                        // must fold to its literal length here. Every other element type keeps the general
                        // `array[T, N]` form, where `sizeof` IS defined, and is deliberately left alone.
                        case QDecl { IsArray: true, Type: QType.Bit } d when BitArrayLength(d) is int len:
                            regs[d.Name] = len; break;
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
            QCallNode { Arg: { } a } c => c with { Arg = SubstCounts(a, regs) },
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

        List<QStmt> Rewrite(IReadOnlyList<QStmt> body, Dictionary<string, int> regs)
        {
            var output = new List<QStmt>(body.Count);
            foreach (var stmt in body)
            {
                QStmt rewritten = stmt switch
                {
                    QGate g => g with { Args = g.Args.Select(a => ResolveArg(a, regs)).ToList() },
                    QDecl d => d with { Value = ResolveExpr(d.Value, regs) },
                    QAssign a => a with { Value = ResolveExpr(a.Value, regs) },
                    QIf i => i with
                    {
                        Cond = i.Cond with { Tree = SubstCounts(i.Cond.Tree, regs) },
                        Then = Rewrite(i.Then, regs),
                        Else = Rewrite(i.Else, regs),
                    },
                    QFor f => f with
                    {
                        From = SubstCounts(f.From, regs)!,
                        To = SubstCounts(f.To, regs)!,
                        Body = Rewrite(f.Body, regs),
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
        QCallNode c => MentionsCount(c.Arg, owners),
        _ => false,
    };

    private static bool HasCount(IReadOnlyList<QStmt> body, IReadOnlySet<string>? owners = null) =>
        body.Any(stmt =>
            QNodes.ExpressionSites(stmt).Any(n => MentionsCount(n, owners))   // every expression position, canonically
            || stmt switch                                                     // plus the nested bodies
            {
                QIf i => HasCount(i.Then, owners) || HasCount(i.Else, owners),
                QFor f => HasCount(f.Body, owners),
                QWhile w => HasCount(w.Body, owners),
                QRepeat r => HasCount(r.Body, owners),
                QConjugate c => HasCount(c.Within, owners) || HasCount(c.Apply, owners),
                _ => false,
            });

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
