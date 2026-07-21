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

        /// <summary>Append <c>_</c> to the dot-flattened name until it is free in <paramref name="scope"/>; note any rename.</summary>
        private string Fresh(string qoraName, HashSet<string> scope, string kind)
        {
            var flat = qoraName.Replace(".", "_");
            var name = flat;
            while (scope.Contains(name)) name += "_";
            scope.Add(name);
            if (name != flat)
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
            CollectDecls(op.Body, scope, map);

            return op with
            {
                Name = isEntry ? op.Name : _opMap[op.Name],
                Params = op.Params.Select(p => p with { Name = map[p.Name] }).ToList(),
                Body = MangleBody(op.Body, map),
            };
        }

        /// <summary>Assign a fresh name to every identifier a body DECLARES (registers, variables, loop
        /// variables), recording each declaring node's emitted name into the model. The record sits OUTSIDE
        /// the ContainsKey guard on purpose: same-name declarations in disjoint sibling blocks share one map
        /// entry, but every declaring NODE still gets its own emitted-name fact.</summary>
        private void CollectDecls(IReadOnlyList<QStmt> stmts, HashSet<string> scope, Dictionary<string, string> map)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u:
                        if (!map.ContainsKey(u.Name)) map[u.Name] = Fresh(u.Name, scope, "register");
                        _model?.RecordEmittedName(u.Id, map[u.Name]);
                        break;
                    case QDecl d:
                        if (!map.ContainsKey(d.Name)) map[d.Name] = Fresh(d.Name, scope, "variable");
                        _model?.RecordEmittedName(d.Id, map[d.Name]);
                        break;
                    case QFor f:
                        if (!map.ContainsKey(f.Var)) map[f.Var] = Fresh(f.Var, scope, "loop variable");
                        _model?.RecordEmittedName(f.Id, map[f.Var]);
                        CollectDecls(f.Body, scope, map);
                        break;
                    case QIf i: CollectDecls(i.Then, scope, map); CollectDecls(i.Else, scope, map); break;
                    case QWhile w: CollectDecls(w.Body, scope, map); break;
                    case QRepeat r: CollectDecls(r.Body, scope, map); break;
                    case QConjugate c: CollectDecls(c.Within, scope, map); CollectDecls(c.Apply, scope, map); break;
                }
        }

        // --- body rewriting: `map` renames this def's locals; _opMap renames calls; built-ins stay ---

        private IReadOnlyList<QStmt> MangleBody(IReadOnlyList<QStmt> stmts, Dictionary<string, string> map) =>
            stmts.Select(s => MangleStmt(s, map)).ToList();

        private static string L(string name, Dictionary<string, string> map) => map.TryGetValue(name, out var m) ? m : name;

        private QStmt MangleStmt(QStmt s, Dictionary<string, string> map) => s switch
        {
            QUse u => u with { Name = L(u.Name, map) },
            QGate g => g with
            {
                Name = _opNames.Contains(g.Name) ? _opMap[g.Name] : g.Name,
                Args = g.Args.Select(a => MangleArg(a, map)).ToList(),
            },
            QDecl d => d with { Name = L(d.Name, map), Value = MangleExpr(d.Value, map) },
            QAssign a => a with
            {
                Name = L(a.Name, map),
                Index = a.Index is null ? null : MangleIndex(a.Index, map),
                Value = MangleExpr(a.Value, map),
            },
            QIf i => i with { Cond = MangleCond(i.Cond, map), Then = MangleBody(i.Then, map), Else = MangleBody(i.Else, map) },
            QFor f => f with
            {
                Var = L(f.Var, map),
                From = MangleTree(f.From, map)!, To = MangleTree(f.To, map)!,
                Body = MangleBody(f.Body, map),
            },
            QWhile w => w with { Cond = MangleCond(w.Cond, map), Body = MangleBody(w.Body, map) },
            QRepeat r => r with { Body = MangleBody(r.Body, map), Until = MangleCond(r.Until, map) },
            QConjugate c => c with { Within = MangleBody(c.Within, map), Apply = MangleBody(c.Apply, map) },
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
            QMeasure { Target: { } t } mm => mm with { Target = new QQubitArg(L(t.Reg, map), MangleIndex(t.Index, map)) },
            QText t => t with { Tree = MangleTree(t.Tree, map) },
            QArrayLiteral literal => literal with
            {
                Elements = literal.Elements.Select(element => MangleExpr(element, map)).ToList(),
            },
            _ => expr,
        };

        private static QCond MangleCond(QCond cond, Dictionary<string, string> map) =>
            cond with { Tree = MangleTree(cond.Tree, map) };

        /// <summary>
        /// Rename every free name an expression tree references, structurally: a bare name this def
        /// declares is renamed via the local map; built-in constants pass through; a member NAME is
        /// structural (never a local); numbers and literals are untouched. A call node cannot survive to
        /// mangling in a validated program (QSEM005 / measure-condition lowering), so it passes through.
        /// </summary>
        private static QNode? MangleTree(QNode? node, Dictionary<string, string> map) => node switch
        {
            null => null,
            QNameRef r when !BuiltinConstants.Contains(r.Name) => new QNameRef(L(r.Name, map)),
            QMember m => m with { Base = MangleTree(m.Base, map)! },
            QIndexNode i => i with { Base = MangleTree(i.Base, map)!, Index = MangleTree(i.Index, map)! },
            QUnary u => u with { Operand = MangleTree(u.Operand, map)! },
            QBinOp b => b with { Left = MangleTree(b.Left, map)!, Right = MangleTree(b.Right, map)! },
            _ => node,   // QNameRef(pi/tau/euler), QNumLit, QLit, QCallNode
        };

        /// <summary>A qubit index is a numeric literal (kept) or a loop-variable name (renamed via the
        /// local map) — the node kind says which; no digit-scan re-derivation.</summary>
        private static QNode MangleIndex(QNode index, Dictionary<string, string> map) =>
            index is QNameRef r ? new QNameRef(L(r.Name, map)) : index;
    }
}
