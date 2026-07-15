using System.Text.RegularExpressions;

namespace Qora.Ir.Passes;

/// <summary>
/// Gives a source-level <c>Qubit[]</c> operation the concrete register widths required by OpenQASM.
/// The length stays out of the source type: each distinct tuple of call-site lengths produces one hidden
/// specialization, and <c>q.Count</c> becomes the matching integer literal inside that copy.
/// </summary>
public static class Monomorphizer
{
    private static readonly Regex CountPattern = new(
        @"\b(?<reg>[_a-zA-Z][_a-zA-Z0-9]*)\s*\.\s*Count\b",
        RegexOptions.Compiled);

    public static QProgram Run(QProgram program)
    {
        static bool IsUnsizedArray(QParam p) =>
            p.Type == QType.Qubit && p.IsQubitArray && p.RegisterSize is null;
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
                if (p.Type == QType.Qubit && p.IsQubitArray && p.RegisterSize is int n)
                    regs[p.Name] = n;

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

        string ResolveCounts(string text, IReadOnlyDictionary<string, int> regs) =>
            CountPattern.Replace(text, match =>
            {
                var reg = match.Groups["reg"].Value;
                return regs.TryGetValue(reg, out var size) ? size.ToString() : match.Value;
            });

        QArg ResolveArg(QArg arg, IReadOnlyDictionary<string, int> regs) => arg switch
        {
            QQubitArg q => new QQubitArg(q.Reg, ResolveCounts(q.Index, regs)),
            QTextArg t => new QTextArg(ResolveCounts(t.Text, regs), t.HasCall),
            _ => arg,
        };

        QExpr ResolveExpr(QExpr expr, IReadOnlyDictionary<string, int> regs) => expr switch
        {
            QText t => new QText(ResolveCounts(t.Text, regs), t.HasCall),
            QMeasure { Target: { } q } m => m with
            {
                Target = new QQubitArg(q.Reg, ResolveCounts(q.Index, regs)),
            },
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
                    QAssign a => a with
                    {
                        Index = a.Index is null ? null : ResolveCounts(a.Index, regs),
                        Value = ResolveExpr(a.Value, regs),
                    },
                    QIf i => i with
                    {
                        Cond = i.Cond with { Text = ResolveCounts(i.Cond.Text, regs) },
                        Then = Rewrite(i.Then, regs),
                        Else = Rewrite(i.Else, regs),
                    },
                    QFor f => f with
                    {
                        From = ResolveCounts(f.From, regs),
                        To = ResolveCounts(f.To, regs),
                        Step = f.Step is null ? null : ResolveCounts(f.Step, regs),
                        Body = Rewrite(f.Body, regs),
                    },
                    QWhile w => w with
                    {
                        Cond = w.Cond with { Text = ResolveCounts(w.Cond.Text, regs) },
                        Body = Rewrite(w.Body, regs),
                    },
                    QRepeat r => r with
                    {
                        Body = Rewrite(r.Body, regs),
                        Until = r.Until with { Text = ResolveCounts(r.Until.Text, regs) },
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
                if (i >= gate.Args.Count || gate.Args[i] is not QTextArg actual
                    || !regs.TryGetValue(actual.Text.Trim(), out var size))
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

    private static bool HasCount(IReadOnlyList<QStmt> body)
    {
        bool Text(string? value) => value is not null && CountPattern.IsMatch(value);
        bool Arg(QArg arg) => arg switch
        {
            QQubitArg q => Text(q.Index),
            QTextArg t => Text(t.Text),
            _ => false,
        };
        bool Expr(QExpr expr) => expr switch
        {
            QText t => Text(t.Text),
            QMeasure { Target: { } q } => Text(q.Index),
            QArrayLiteral literal => literal.Elements.Any(Expr),
            _ => false,
        };

        foreach (var stmt in body)
            if (stmt switch
            {
                QGate g => g.Args.Any(Arg),
                QDecl d => Expr(d.Value),
                QAssign a => Text(a.Index) || Expr(a.Value),
                QIf i => Text(i.Cond.Text) || HasCount(i.Then) || HasCount(i.Else),
                QFor f => Text(f.From) || Text(f.To) || Text(f.Step) || HasCount(f.Body),
                QWhile w => Text(w.Cond.Text) || HasCount(w.Body),
                QRepeat r => HasCount(r.Body) || Text(r.Until.Text),
                QConjugate c => HasCount(c.Within) || HasCount(c.Apply),
                _ => false,
            }) return true;
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

    private static bool HasCount(IReadOnlyList<QStmt> body, IReadOnlySet<string> owners)
    {
        bool Text(string? value) => value is not null && CountPattern.Matches(value)
            .Cast<Match>().Any(match => owners.Contains(match.Groups["reg"].Value));
        bool Arg(QArg arg) => arg switch
        {
            QQubitArg indexed => Text(indexed.Index),
            QTextArg text => Text(text.Text),
            _ => false,
        };
        bool Expr(QExpr expr) => expr switch
        {
            QText text => Text(text.Text),
            QMeasure { Target: { } target } => Text(target.Index),
            QArrayLiteral literal => literal.Elements.Any(Expr),
            _ => false,
        };

        return body.Any(statement => statement switch
        {
            QGate gate => gate.Args.Any(Arg),
            QDecl declaration => Expr(declaration.Value),
            QAssign assignment => Text(assignment.Index) || Expr(assignment.Value),
            QIf branch => Text(branch.Cond.Text) || HasCount(branch.Then, owners) || HasCount(branch.Else, owners),
            QFor loop => Text(loop.From) || Text(loop.To) || Text(loop.Step) || HasCount(loop.Body, owners),
            QWhile loop => Text(loop.Cond.Text) || HasCount(loop.Body, owners),
            QRepeat loop => HasCount(loop.Body, owners) || Text(loop.Until.Text),
            QConjugate conjugate => HasCount(conjugate.Within, owners) || HasCount(conjugate.Apply, owners),
            _ => false,
        });
    }
}
