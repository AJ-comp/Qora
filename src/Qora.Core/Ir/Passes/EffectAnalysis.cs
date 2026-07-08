namespace Qora.Ir.Passes;

/// <summary>
/// Effect analysis — rung ① of the auto-uncompute ladder (① use/def → ② liveness → ③ qfree → ④ inject).
/// For every statement it computes which qubits it TOUCHED (any reference, controls included) and which
/// it MODIFIED (computational-basis value may change), plus a per-operation summary over its formal
/// parameters. PURE analysis: the IR is never changed and no diagnostics are raised — results are stored
/// in the <see cref="SemanticModel"/> keyed by stable node Ids, so later passes that copy subtrees still
/// find them through the derivation chain.
///
/// Runs on VALIDATED, monomorphized IR, which is what makes the classification signature-driven:
/// argument counts/kinds are already checked (QSEM006), qubit registers exist only as parameters or
/// entry-top-level <c>use</c> registers (QSEM012/QSEM015) so a text argument in a qubit slot IS a
/// register name, and the call graph is a DAG (QSEM011) so memoized recursion terminates.
/// </summary>
public static class EffectAnalysis
{
    public static void Run(QProgram program, SemanticModel model) => new Analyzer(program, model).RunAll();

    private sealed class Analyzer
    {
        private readonly SemanticModel _model;
        private readonly IReadOnlyList<QOperation> _operations;
        private readonly Dictionary<string, QOperation> _opByName;
        private readonly Dictionary<string, OpEffectSummary> _summaries = new();

        public Analyzer(QProgram program, SemanticModel model)
        {
            _model = model;
            _operations = program.Operations;
            _opByName = program.Operations.ToDictionary(o => o.Name);
        }

        public void RunAll()
        {
            foreach (var op in _operations) Summarize(op.Name);
        }

        private OpEffectSummary Summarize(string opName)
        {
            if (_summaries.TryGetValue(opName, out var cached)) return cached;
            var op = _opByName[opName];
            var (touched, modified, irreversible) = AnalyzeBlock(op.Body);
            // Only effects on FORMAL qubit parameters escape the op; locals from `use` are op-private
            // (they are the ancilla candidates the liveness rung will hunt for).
            var paramNames = op.Params.Where(p => p.Type == QType.Qubit).Select(p => p.Name).ToHashSet();
            var summary = new OpEffectSummary(
                touched.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                modified.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                irreversible);
            _summaries[opName] = summary;
            _model.AddOpEffects(op.Id, summary);
            return summary;
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, bool Irreversible)
            AnalyzeBlock(IReadOnlyList<QStmt> body)
        {
            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            var irreversible = false;
            foreach (var stmt in body)
            {
                var (t, m, irr) = AnalyzeStmt(stmt);
                touched.UnionWith(t);
                modified.UnionWith(m);
                irreversible |= irr;
            }
            return (touched, modified, irreversible);
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, bool Irreversible)
            AnalyzeStmt(QStmt stmt)
        {
            HashSet<QubitRef> touched = new(), modified = new();
            var irreversible = false;

            switch (stmt)
            {
                case QUse u:
                    // birth point: the fresh register is defined here (its qubits enter |0…0⟩)
                    var born = new QubitRef(u.Name, null);
                    touched.Add(born);
                    modified.Add(born);
                    break;

                case QGate g:
                    (touched, modified, irreversible) = AnalyzeGate(g);
                    break;

                case QDecl { Value: QMeasure m }:
                    ApplyMeasure(m, touched, modified);
                    irreversible = true;
                    break;

                case QAssign { Value: QMeasure m }:
                    ApplyMeasure(m, touched, modified);
                    irreversible = true;
                    break;

                // containers aggregate their children conservatively (union over all paths)
                case QIf i:
                    var (tt, tm, ti) = AnalyzeBlock(i.Then);
                    var (et, em, ei) = AnalyzeBlock(i.Else);
                    touched.UnionWith(tt); touched.UnionWith(et);
                    modified.UnionWith(tm); modified.UnionWith(em);
                    irreversible = ti || ei;
                    break;

                case QFor f:
                    (touched, modified, irreversible) = AnalyzeBlock(f.Body);
                    break;

                case QWhile w:
                    (touched, modified, irreversible) = AnalyzeBlock(w.Body);
                    break;

                case QRepeat r:
                    (touched, modified, irreversible) = AnalyzeBlock(r.Body);
                    break;

                case QConjugate c:
                    var (wt, wm, wi) = AnalyzeBlock(c.Within);
                    var (at, am, ai) = AnalyzeBlock(c.Apply);
                    touched.UnionWith(wt); touched.UnionWith(at);
                    modified.UnionWith(wm); modified.UnionWith(am);
                    irreversible = wi || ai;
                    break;

                // purely classical QDecl/QAssign: no qubit contact. The measurement-bit → later-if
                // dataflow edge is already recorded by Symbol.Uses — not duplicated here.
                default:
                    break;
            }

            _model.AddEffects(stmt.Id, new StmtEffects(touched, modified));
            return (touched, modified, irreversible);
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, bool Irreversible)
            AnalyzeGate(QGate g)
        {
            // user-operation call (Adjoint Foo projects identically — U† touches the same qubits;
            // Controlled on a user op is impossible here, QSEM002 rejected it)
            if (_opByName.TryGetValue(g.Name, out var callee))
            {
                var summary = Summarize(g.Name);
                return (Project(summary.ParamTouched, callee, g.Args),
                        Project(summary.ParamModified, callee, g.Args),
                        summary.Irreversible);
            }

            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            if (!QoraGates.Gates.TryGetValue(g.Name, out var info))
                return (touched, modified, false); // unknown callee — validated IR never gets here

            var qubitArgs = info.AngleFirst ? g.Args.Skip(1).ToList() : g.Args.ToList();

            if (!info.Unitary)
            {
                // reset-like: every operand is re-prepared to |0⟩ — modified, and the transition is lossy
                foreach (var arg in qubitArgs)
                {
                    var r = RefOf(arg);
                    touched.Add(r);
                    modified.Add(r);
                }
                return (touched, modified, true);
            }

            // leading slots are controls (gate's own + one per Controlled functor): steering only,
            // their basis value is preserved. Diagonal gates preserve every target's basis value too
            // (phase kickback is the gate table's Diagonal flag's problem, not Modified's).
            var controls = info.Controls + g.Functors.Count(f => f == "Controlled");
            for (var i = 0; i < qubitArgs.Count; i++)
            {
                var r = RefOf(qubitArgs[i]);
                touched.Add(r);
                if (i >= controls && !info.Diagonal) modified.Add(r);
            }
            return (touched, modified, false);
        }

        /// <summary>Rewrite a callee summary's formal-parameter refs into the caller's actual registers.</summary>
        private static HashSet<QubitRef> Project(
            IReadOnlySet<QubitRef> refs, QOperation callee, IReadOnlyList<QArg> args)
        {
            var result = new HashSet<QubitRef>();
            for (var i = 0; i < callee.Params.Count && i < args.Count; i++)
            {
                var param = callee.Params[i];
                if (param.Type != QType.Qubit) continue;
                switch (args[i])
                {
                    case QTextArg whole: // whole register actual: indices carry over 1:1
                        foreach (var r in refs)
                            if (r.Reg == param.Name) result.Add(r with { Reg = whole.Text });
                        break;
                    case QQubitArg single: // single-qubit binding: everything done to the param lands here
                        var target = RefOf(single);
                        foreach (var r in refs)
                            if (r.Reg == param.Name) result.Add(target);
                        break;
                }
            }
            return result;
        }

        private static void ApplyMeasure(QMeasure m, HashSet<QubitRef> touched, HashSet<QubitRef> modified)
        {
            if (m.Target is null) return;
            var r = RefOf(m.Target);
            touched.Add(r);
            modified.Add(r);
        }

        /// <summary>A literal index stays precise; a loop-variable (or otherwise unknown) index is
        /// conservatively blanketed to the whole register — same split the validator's index check makes.</summary>
        private static QubitRef RefOf(QArg arg) => arg switch
        {
            QQubitArg q => int.TryParse(q.Index, out var i) ? new QubitRef(q.Reg, i) : new QubitRef(q.Reg, null),
            QTextArg t => new QubitRef(t.Text, null),
            _ => throw new System.InvalidOperationException($"unexpected argument kind: {arg}"),
        };
    }
}
