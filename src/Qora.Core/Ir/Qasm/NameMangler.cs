namespace Qora.Ir.Passes;

/// <summary>
/// Name mangling (IR→IR): maps every Qora name to a valid, collision-free OpenQASM identifier right
/// before emission. A namespaced name flattens its dots (<c>MyLib.Bell</c> → <c>MyLib_Bell</c>); then,
/// within each emission scope, any name that would collide — with a built-in gate/keyword, or with
/// another emitted name — gets <c>_</c> appended until it is unique.
///
/// A name collision is therefore NEVER a compile error: the pass resolves it automatically and records
/// a note (surfaced as a <c>// Qora:</c> comment in the emitted QASM), so ANY validated program emits.
///
/// Scopes mirror OpenQASM: the GLOBAL scope holds operation def names plus the entry op's top-level
/// declarations (they share one flat namespace with stdgates); each def has its own LOCAL scope for
/// parameters / variables / loop variables (names may freely repeat across different defs). A def-local
/// is kept clear of every operation name too, so it can never shadow an operation the def calls. The
/// entry op keeps its own name (its body is the QASM top level and nothing calls it).
///
/// Built-in gate names at call sites and the constants <c>pi</c>/<c>tau</c>/<c>euler</c> in expressions
/// are never mangled — they are the target world's own names. Runs AFTER validation (diagnostics show
/// the user's original names) and before the emitter (which stays a dumb printer).
/// </summary>
public static class NameMangler
{
    private static readonly HashSet<string> BuiltinConstants = new() { "pi", "tau", "euler" };

    /// <summary>The mangled program plus one note per collision-driven rename (for the QASM header).</summary>
    public sealed record Result(QProgram Program, IReadOnlyList<string> Notes);

    /// <summary>Mangle every name, recording each declaration's emitted name into <paramref name="model"/>
    /// (<see cref="SemanticModel.RecordEmittedName"/>) — this pass is the single producer of the
    /// source-name → emitted-name fact. The parameter has NO default on purpose: a caller that has no
    /// model (a test on a bare program) must say so explicitly with null, so the model can never be
    /// forgotten silently on the compile path.</summary>
    public static Result Mangle(QProgram program, SemanticModel? model)
    {
        var m = new Mangler(model);
        var prog = m.Run(program);
        return new Result(prog, m.Notes);
    }

    private sealed class Mangler
    {
        public readonly List<string> Notes = new();
        private readonly SemanticModel? _model;
        private HashSet<string> _opNames = new();
        private readonly Dictionary<string, string> _opMap = new();   // op FQN -> emitted def name

        public Mangler(SemanticModel? model) => _model = model;

        public QProgram Run(QProgram program)
        {
            if (program.Operations.Count == 0) return program;
            var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];
            _opNames = program.Operations.Select(o => o.Name).ToHashSet();

            // GLOBAL scope: op def names + the entry op's top-level declarations. Assign op def names first
            // (deterministic source order); the entry keeps its own name — it is never emitted as a def.
            var global = new HashSet<string>(QoraGates.QasmReserved);
            foreach (var o in program.Operations)
            {
                _opMap[o.Name] = o == entry ? o.Name : Fresh(o.Name, global, "operation");
                _model?.RecordEmittedName(o.Id, _opMap[o.Name]);
            }

            var outOps = new List<QOperation>(program.Operations.Count);
            foreach (var o in program.Operations)
                outOps.Add(MangleOp(o, o == entry, global));
            return program with { Operations = outOps };
        }

        /// <summary>Assign a unique emitted name in <paramref name="scope"/>: append <c>_</c> to the
        /// dot-flattened candidate until free, then reserve it, noting any user-visible rename. A HOIST
        /// PLACEHOLDER (<see cref="HoistName"/>) is prettified to its embedded base name here — that is how
        /// two distinct hoisted arrays with the same desired base become <c>x</c> and <c>x_</c> (distinct
        /// placeholders ⇒ distinct map keys ⇒ independent freshening), with no note, since a placeholder is
        /// internal machinery rather than a name the user wrote.</summary>
        private string Fresh(string qoraName, HashSet<string> scope, string kind)
        {
            var isHoist = HoistName.Base(qoraName) is not null;
            var flat = (HoistName.Base(qoraName) ?? qoraName).Replace(".", "_");
            var name = flat;
            while (scope.Contains(name)) name += "_";
            scope.Add(name);
            if (name != flat && !isHoist)
                Notes.Add($"{kind} `{qoraName}` emitted as `{name}` (renamed to avoid a name collision)");
            return name;
        }

        private QOperation MangleOp(QOperation op, bool isEntry, HashSet<string> global)
        {
            // Entry declarations live in the GLOBAL scope (top level). A non-entry def gets its own local
            // scope, seeded with built-ins AND every operation name — so a local can never shadow a gate
            // or an operation the def calls.
            var scope = isEntry ? global : new HashSet<string>(QoraGates.QasmReserved.Concat(_opMap.Values));
            var map = new Dictionary<string, string>();

            foreach (var p in op.Params)
            {
                if (!map.ContainsKey(p.Name)) map[p.Name] = Fresh(p.Name, scope, "parameter");
                _model?.RecordEmittedName(p.Id, map[p.Name]);
            }
            // Qubit REGISTERS are hoisted to the def top and may be forward-referenced, so they are named
            // up front, at OP level. Everything else (var/const, measure bits, loop variables) is named AT
            // ITS DECLARATION during the walk below — see MangleBody.
            CollectRegisters(op.Body, scope, map);

            return op with
            {
                Name = isEntry ? op.Name : _opMap[op.Name],
                Params = op.Params.Select(p => p with { Name = map[p.Name] }).ToList(),
                Body = MangleBody(op.Body, map, scope),
            };
        }

        /// <summary>Name the qubit REGISTERS a body declares, at OP level. Registers alone are pre-named:
        /// the emitter hoists their declaration to the def top and the language lets a gate textually precede
        /// its <c>use</c>, so a reference can appear before the declaration. Every OTHER declaration is named
        /// at its own site (MangleBody), so two same-named locals in disjoint blocks get DISTINCT emitted
        /// identifiers — QASM's def scope is flat, so sharing one identifier merged two variables.</summary>
        private void CollectRegisters(IReadOnlyList<QStmt> stmts, HashSet<string> scope, Dictionary<string, string> map)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u:
                        if (!map.ContainsKey(u.Name)) map[u.Name] = Fresh(u.Name, scope, "register");
                        _model?.RecordEmittedName(u.Id, map[u.Name]);
                        break;
                    case QFor f: CollectRegisters(f.Body, scope, map); break;
                    case QIf i: CollectRegisters(i.Then, scope, map); CollectRegisters(i.Else, scope, map); break;
                    case QWhile w: CollectRegisters(w.Body, scope, map); break;
                    case QRepeat r: CollectRegisters(r.Body, scope, map); break;
                    case QConjugate c: CollectRegisters(c.Within, scope, map); CollectRegisters(c.Apply, scope, map); break;
                }
        }

        // --- body rewriting: `map` renames this def's locals; _opMap renames calls; built-ins stay ---

        /// <summary>Rewrite a BLOCK. It gets its OWN rename map, seeded from the enclosing block: a
        /// declaration inserts its entry when reached, in effect from that point through the rest of this
        /// block and any block nested inside it. That is the ordinary scope chain — the same shape the
        /// symbol table models and ArrayLocalHoisting already uses — so a reference always resolves to the
        /// NEAREST declaration and two disjoint same-named declarations never collapse onto one name.</summary>
        private IReadOnlyList<QStmt> MangleBody(IReadOnlyList<QStmt> stmts, Dictionary<string, string> map, HashSet<string> scope)
        {
            var local = new Dictionary<string, string>(map);
            var result = new List<QStmt>(stmts.Count);
            foreach (var s in stmts) result.Add(MangleStmt(s, local, scope));
            return result;
        }

        /// <summary>A declaration is named HERE, at its own site: its initializer is rewritten FIRST (it is
        /// evaluated before the name is bound, so it still sees the enclosing meaning), then the new name
        /// enters this block map and is recorded as this declaring node's emitted name.</summary>
        private QStmt MangleDecl(QDecl d, Dictionary<string, string> map, HashSet<string> scope)
        {
            var value = MangleExpr(d.Value, map);
            var name = Fresh(d.Name, scope, "variable");
            map[d.Name] = name;
            _model?.RecordEmittedName(d.Id, name);
            return d with { Name = name, Value = value };
        }

        /// <summary>A loop header declares its variable for the BODY only; the bounds are evaluated in the
        /// ENCLOSING block, so they are rewritten before the loop variable enters scope.</summary>
        private QStmt MangleFor(QFor f, Dictionary<string, string> map, HashSet<string> scope)
        {
            var from = MangleTree(f.From, map)!;
            var to = MangleTree(f.To, map)!;
            var step = f.Step is null ? null : MangleTree(f.Step, map);
            var loopName = Fresh(f.Var, scope, "loop variable");
            _model?.RecordEmittedName(f.Id, loopName);
            var inner = new Dictionary<string, string>(map) { [f.Var] = loopName };
            return f with { Var = loopName, From = from, To = to, Step = step, Body = MangleBody(f.Body, inner, scope) };
        }

        private static string L(string name, Dictionary<string, string> map) => map.TryGetValue(name, out var m) ? m : name;

        private QStmt MangleStmt(QStmt s, Dictionary<string, string> map, HashSet<string> scope) => s switch
        {
            QUse u => u with { Name = L(u.Name, map) },
            QGate g => g with
            {
                Name = _opNames.Contains(g.Name) ? _opMap[g.Name] : g.Name,
                Args = g.Args.Select(a => MangleArg(a, map)).ToList(),
            },
            QDecl d => MangleDecl(d, map, scope),
            QAssign a => a with
            {
                Name = L(a.Name, map),
                Index = a.Index is null ? null : MangleIndex(a.Index, map),
                Value = MangleExpr(a.Value, map),
            },
            QIf i => i with { Cond = MangleCond(i.Cond, map), Then = MangleBody(i.Then, map, scope), Else = MangleBody(i.Else, map, scope) },
            QFor f => MangleFor(f, map, scope),
            QWhile w => w with { Cond = MangleCond(w.Cond, map), Body = MangleBody(w.Body, map, scope) },
            QRepeat r => r with { Body = MangleBody(r.Body, map, scope), Until = MangleCond(r.Until, map) },
            QConjugate c => c with { Within = MangleBody(c.Within, map, scope), Apply = MangleBody(c.Apply, map, scope) },
            QReturn r => r with { Value = MangleExpr(r.Value, map) },
            _ => s,
        };

        private QArg MangleArg(QArg arg, Dictionary<string, string> map) => arg switch
        {
            QQubitArg q => new QQubitArg(L(q.Reg, map), MangleIndex(q.Index, map)),
            QTextArg t => t with { Tree = MangleTree(t.Tree, map) },
            _ => arg,
        };

        private QExpr MangleExpr(QExpr expr, Dictionary<string, string> map) => expr switch
        {
            QMeasure mm => mm with { Target = MangleTree(mm.Target, map)! },
            QText t => t with { Tree = MangleTree(t.Tree, map) },
            QArrayLiteral literal => literal with
            {
                Elements = literal.Elements.Select(element => MangleExpr(element, map)).ToList(),
            },
            _ => expr,
        };

        private QCond MangleCond(QCond cond, Dictionary<string, string> map) =>
            cond with { Tree = MangleTree(cond.Tree, map) };

        /// <summary>
        /// Rename every free name an expression tree references, structurally: a bare name this def
        /// declares is renamed via the local map; built-in constants pass through; a member NAME is
        /// structural (never a local); numbers and literals are untouched. A FUNCTION call node renames its
        /// ARGUMENTS (which reference locals); its NAME is a global function name that can never collide
        /// (QSEM013/QSEM008 forbid a function taking a reserved/gate/duplicate name), so it stays as-is. A
        /// measurement call never reaches mangling (QMeasure / measure-condition lowering handle it first).
        /// </summary>
        private QNode? MangleTree(QNode? node, Dictionary<string, string> map) => node switch
        {
            null => null,
            QNameRef r when !BuiltinConstants.Contains(r.Name) => new QNameRef(L(r.Name, map)),
            QMember m => m with { Base = MangleTree(m.Base, map)! },
            QIndexNode i => i with { Base = MangleTree(i.Base, map)!, Index = MangleTree(i.Index, map)! },
            QUnary u => u with { Operand = MangleTree(u.Operand, map)! },
            QBinOp b => b with { Left = MangleTree(b.Left, map)!, Right = MangleTree(b.Right, map)! },
            // A call's NAME is a reference to an operation/function — it goes through the SAME `_opMap`
            // a QGate call target does, so a renamed callee can never keep an un-renamed call site. (An
            // earlier version left it alone on the assumption that a function name can never collide; a
            // namespaced operation flattening onto that name disproved it.)
            QCallNode c => c with
            {
                Name = _opNames.Contains(c.Name) ? _opMap[c.Name] : c.Name,
                Args = c.Args.Select(a => MangleTree(a, map)!).ToList(),
            },
            _ => node,   // QNameRef(pi/tau/euler), QNumLit, QLit
        };

        /// <summary>A qubit index is a numeric literal (kept) or a loop-variable name (renamed via the
        /// local map) — the node kind says which; no digit-scan re-derivation.</summary>
        private QNode MangleIndex(QNode index, Dictionary<string, string> map) =>
            index is QNameRef r ? new QNameRef(L(r.Name, map)) : index;
    }
}
