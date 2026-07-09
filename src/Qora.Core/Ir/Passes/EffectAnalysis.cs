namespace Qora.Ir.Passes;

/// <summary>
/// Effect analysis — rung ① of the auto-uncompute ladder (① use/def → ② liveness → ③ qfree → ④ inject).
/// For every LEAF statement (gate, measurement, <c>use</c>) it emits one <see cref="QubitEvent"/> per qubit
/// operand — <see cref="QubitEventKind.Read"/> (a control or diagonal-gate target: value preserved),
/// <see cref="QubitEventKind.Write"/> (a target / reset / register birth: value may change), or
/// <see cref="QubitEventKind.Measure"/> — building each operation's program-ordered use/def stream, plus a
/// per-operation summary over its formal qubit parameters. PURE analysis: the IR is never changed and no
/// diagnostics are raised; the event stream and summary are stored on the <see cref="SemanticModel"/> keyed
/// by <c>op.Id</c>. The per-qubit stream (liveness's timeline) and per-statement grouping (entanglement
/// edges) rung ②/③ need are both READ OFF this one stream.
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
            var log = new OpEventLog();   // THIS op's own program-ordered stream (a callee's is separate)
            var (touched, modified, irreversible) = AnalyzeBlock(op.Body, log);
            // Only effects on FORMAL qubit parameters escape the op; locals from `use` are op-private
            // (they are the ancilla candidates the liveness rung will hunt for).
            var paramNames = op.Params.Where(p => p.Type == QType.Qubit).Select(p => p.Name).ToHashSet();
            var summary = new OpEffectSummary(
                touched.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                modified.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                irreversible);
            _summaries[opName] = summary;
            _model.AddOpEffects(op.Id, summary);
            _model.AddQubitEvents(op.Id, log.Events);
            return summary;
        }

        // AnalyzeBlock/AnalyzeStmt do double duty: they RETURN the aggregated (touched, modified, irr) their
        // caller unions for the operation summary, AND emit each LEAF statement's per-qubit events into `log`
        // (containers emit none of their own — their children carry the precise per-gate detail).
        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, bool Irreversible)
            AnalyzeBlock(IReadOnlyList<QStmt> body, OpEventLog log)
        {
            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            var irreversible = false;
            foreach (var stmt in body)
            {
                var (t, m, irr) = AnalyzeStmt(stmt, log);
                touched.UnionWith(t);
                modified.UnionWith(m);
                irreversible |= irr;
            }
            return (touched, modified, irreversible);
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, bool Irreversible)
            AnalyzeStmt(QStmt stmt, OpEventLog log)
        {
            HashSet<QubitRef> touched = new(), modified = new();
            var irreversible = false;
            var leaf = false;      // directly contacts qubits ⇒ emits its own events; containers do not
            var measure = false;   // a leaf that collapses its target (Measure events, not Write)

            switch (stmt)
            {
                case QUse u:
                    // birth point: the fresh register is defined here (its qubits enter |0…0⟩)
                    var born = new QubitRef(u.Name, null);
                    touched.Add(born);
                    modified.Add(born);
                    leaf = true;
                    break;

                case QGate g:
                    (touched, modified, irreversible) = AnalyzeGate(g);
                    leaf = true;
                    break;

                case QDecl { Value: QMeasure m }:
                    ApplyMeasure(m, touched, modified);
                    irreversible = true;
                    leaf = true;
                    measure = true;
                    break;

                case QAssign { Value: QMeasure m }:
                    ApplyMeasure(m, touched, modified);
                    irreversible = true;
                    leaf = true;
                    measure = true;
                    break;

                // containers aggregate their children conservatively (union over all paths)
                case QIf i:
                    var (tt, tm, ti) = AnalyzeBlock(i.Then, log);
                    var (et, em, ei) = AnalyzeBlock(i.Else, log);
                    touched.UnionWith(tt); touched.UnionWith(et);
                    modified.UnionWith(tm); modified.UnionWith(em);
                    irreversible = ti || ei;
                    break;

                case QFor f:
                    (touched, modified, irreversible) = AnalyzeBlock(f.Body, log);
                    break;

                case QWhile w:
                    (touched, modified, irreversible) = AnalyzeBlock(w.Body, log);
                    break;

                case QRepeat r:
                    (touched, modified, irreversible) = AnalyzeBlock(r.Body, log);
                    break;

                case QConjugate c:
                    var (wt, wm, wi) = AnalyzeBlock(c.Within, log);
                    var (at, am, ai) = AnalyzeBlock(c.Apply, log);
                    touched.UnionWith(wt); touched.UnionWith(at);
                    modified.UnionWith(wm); modified.UnionWith(am);
                    irreversible = wi || ai;
                    break;

                // purely classical QDecl/QAssign: no qubit contact. The measurement-bit → later-if
                // dataflow edge is already recorded by Symbol.Uses — not duplicated here.
                default:
                    break;
            }

            if (leaf) log.Emit(stmt.Id, touched, modified, measure);
            return (touched, modified, irreversible);
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, bool Irreversible)
            AnalyzeGate(QGate g)
        {
            // user-operation call (Adjoint Foo projects identically — U† touches the same qubits;
            // Controlled on a user op is impossible here, QSEM002 rejected it)
            if (_opByName.TryGetValue(g.Name, out var callee))
            {
                // INVARIANT: the only functor that reaches a user-op call is Adjoint — Controlled on a user
                // op is rejected by QSEM002. Adjoint preserves both arg count and qubit support, so the
                // positional projection below is exact. A Controlled here would prepend a control arg,
                // misaligning params↔args and dropping the shifted target (a silent wrong-mapping +
                // under-approximation). If that invariant ever regressed, fail loud rather than mis-analyze.
                if (g.Functors.Any(f => f != "Adjoint"))
                    throw new System.InvalidOperationException(
                        $"effect analysis: user-op call `{g.Name}` carries a non-Adjoint functor [{string.Join(", ", g.Functors)}]; QSEM002 should have rejected it before analysis");
                var summary = Summarize(g.Name);
                return (Project(summary.ParamTouched, callee, g.Args),
                        Project(summary.ParamModified, callee, g.Args),
                        summary.Irreversible);
            }

            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            // INVARIANT: a name that is neither a user op nor a built-in gate is exactly QSEM007's rejection
            // predicate, so it can never reach here on clean IR. Returning empty effects would be a silent
            // under-approximation (the statement would look like it touches nothing); fail loud instead.
            if (!QoraGates.Gates.TryGetValue(g.Name, out var info))
                throw new System.InvalidOperationException(
                    $"effect analysis: `{g.Name}` is neither a user operation nor a built-in gate; QSEM007 should have rejected it before analysis");

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
            // INVARIANT: QSEM006 guarantees arg count == param count for every user-op call, so the loop
            // visits every qubit param. If they ever diverged, trailing params would silently vanish from
            // the caller's effect set (a silent under-approximation); fail loud rather than under-report.
            if (args.Count != callee.Params.Count)
                throw new System.InvalidOperationException(
                    $"effect analysis: call to `{callee.Name}` has {args.Count} argument(s) for {callee.Params.Count} parameter(s); QSEM006 should have caught the mismatch before analysis");
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
                        // INVARIANT: a QQubitArg actual binds only to a SINGLE-qubit param (QSEM006), whose
                        // refs are all whole-register (Index=null) — a single qubit cannot be indexed
                        // (QSEM016). So collapsing every ref onto `target` loses no index. A ref carrying a
                        // concrete index here would mean an indexed effect leaked into a single-qubit param;
                        // collapsing it would silently retarget the wrong qubit — fail loud instead.
                        var target = RefOf(single);
                        foreach (var r in refs)
                            if (r.Reg == param.Name)
                            {
                                if (r.Index is not null)
                                    throw new System.InvalidOperationException(
                                        $"effect analysis: single-qubit binding of `{param.Name}` carries an indexed effect `{r}`; QSEM016 should have rejected indexing a single qubit");
                                result.Add(target);
                            }
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

        /// <summary>One operation's growing event stream plus its program-order counter. A fresh log is made
        /// per <see cref="Summarize"/> so a callee's stream never interleaves with its caller's.</summary>
        private sealed class OpEventLog
        {
            public readonly List<QubitEvent> Events = new();
            private int _order;

            /// <summary>Emit one leaf statement's events — one per qubit it touched, its Kind decided by the
            /// touched/modified sets: a measured target is <see cref="QubitEventKind.Measure"/>, a modified
            /// (value-changing) qubit is <see cref="QubitEventKind.Write"/>, anything else touched is
            /// <see cref="QubitEventKind.Read"/> (a control or diagonal-gate target). All events of one
            /// statement share its program-order index; the counter then advances.</summary>
            public void Emit(int stmtId, HashSet<QubitRef> touched, HashSet<QubitRef> modified, bool measure)
            {
                foreach (var r in touched)
                {
                    var kind = measure ? QubitEventKind.Measure
                             : modified.Contains(r) ? QubitEventKind.Write
                             : QubitEventKind.Read;
                    Events.Add(new QubitEvent(r, kind, _order, stmtId));
                }
                _order++;
            }
        }
    }
}
