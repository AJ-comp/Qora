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
/// argument counts/kinds are already checked (QSEM006), every name in a qubit slot resolves to a declared
/// register or parameter (QSEM007/QSEM015) — so a text argument in a qubit slot IS a register name
/// regardless of WHERE the register was allocated — and the call graph is a DAG (QSEM011) so memoized
/// recursion terminates.
///
/// Register births are HOISTED, mirroring the emitter's declaration hoisting: <c>Summarize</c> pre-walks the
/// op body's TOP LEVEL for every <c>use</c> and records its |0…0⟩ birth first, so a gate may textually precede
/// its register's <c>use</c> (legal, pinned by ScopeTests) and still write onto the hoisted birth. This
/// pre-walk therefore DOES lean on <c>use</c> being entry-top-level-only (QSEM012): if that gate is ever lifted
/// by an allocation-lowering pass, the pre-walk must recurse into container bodies (or the lowering pass must
/// hoist births before analysis) — until then a nested <c>use</c> fails QINTERNAL-loud at its register's first
/// touch (the Stamp write-before-birth guard), never silently. Hoisting semantics are depth-independent, so
/// making the pre-walk recursive is the additive fix when the gate lifts.
/// </summary>
public static class EffectAnalysis
{
    public static void Run(QProgram program, SemanticModel model) => new Analyzer(program, model).RunAll();

    /// <summary>TEST SEAM (InternalsVisibleTo) for the coherence sweep: lets tests feed a hand-corrupted
    /// (events, graph) pair to the SAME always-on verifier the pipeline runs — without it the sweep is
    /// mutation-blind (adversarially confirmed: neutering every Fail path kept the whole suite green).</summary>
    internal static void VerifySweep(string opName, IReadOnlyCollection<string> qubitParamRegs,
        List<QubitEvent> events, QubitGraph graph)
        => Analyzer.VerifyGraphCoherence(opName, qubitParamRegs, events, graph);

    private sealed class Analyzer
    {
        private readonly SemanticModel _model;
        private readonly IReadOnlyList<QOperation> _operations;
        private readonly Dictionary<string, QOperation> _opByName;
        private readonly Dictionary<int, QOperation> _opById;   // for CALL → callee resolution by reference (CalleeOpId)
        private readonly Dictionary<string, OpEffectSummary> _summaries = new();

        public Analyzer(QProgram program, SemanticModel model)
        {
            _model = model;
            _operations = program.Operations;
            _opByName = program.Operations.ToDictionary(o => o.Name);
            _opById = program.Operations.ToDictionary(o => o.Id);
        }

        public void RunAll()
        {
            foreach (var op in _operations) Summarize(op.Name);

            // Which user operations cannot be statement-adjoint-inverted (a while/repeat of unknown count, classical
            // mutation, a local `use`, or measure/reset in the body — transitively). The Inverter is the SINGLE
            // authority on invertibility. We run it over the SAME (monomorphized) operations analysis saw, so the
            // names here are the mono names the mono call sites actually use.
            var inverter = new Inverter(_operations);
            var nonInvertibleOps = _operations
                .Where(op => !inverter.TryInvertOperation(op.Name, out _, out _))
                .Select(op => op.Name).ToHashSet();

            // Record the CALL statements (by stable Id) that target such an op, so rung ③ blocks them without a
            // name lookup — StmtIds are shared with the pre-mono tree, mono names are not (a generic call's name is
            // rewritten while its Id is preserved). One walk of every statement at every depth.
            var nonInvertibleCallStmts = new HashSet<int>();
            foreach (var op in _operations)
                ContainerMap.Visit(op, (stmt, _) =>
                {
                    if (stmt is QGate g && nonInvertibleOps.Contains(g.Name)) nonInvertibleCallStmts.Add(g.Id);
                });
            _model.RecordNonInvertibleCallStmts(nonInvertibleCallStmts);
        }

        private OpEffectSummary Summarize(string opName)
        {
            if (_summaries.TryGetValue(opName, out var cached)) return cached;
            var op = _opByName[opName];
            // THIS op's own stream + graph (a callee's are separate); param seeds give first uses an origin
            var log = new OpEventLog(op.Params.Where(p => p.Type == QType.Qubit).Select(p => p.Name));
            // Registers are HOISTED, exactly like the emitter's declaration hoisting: every `use` allocates
            // at op start, so its |0…0⟩ birth is recorded BEFORE any gate — a gate may textually precede its
            // register's `use` (legal, pinned by ScopeTests) and still write onto the hoisted birth. Each
            // birth keeps its `use` statement's Id; only its Order moves to the front.
            foreach (var stmt in op.Body)
                if (stmt is QUse u)
                {
                    var born = new QubitRef(u.Name, null);
                    log.Record(u.Id, new HashSet<QubitRef> { born }, new HashSet<QubitRef> { born },
                        new HashSet<QubitRef>(), new HashSet<QubitRef>(), irreversible: false, birth: true);
                }
            var (touched, modified, nonQfreeWrites, measured, irreversible) = AnalyzeBlock(op.Body, log);
            // Only effects on FORMAL qubit parameters escape the op; locals from `use` are op-private
            // (they are the ancilla candidates the liveness rung will hunt for).
            var paramNames = op.Params.Where(p => p.Type == QType.Qubit).Select(p => p.Name).ToHashSet();
            var summary = new OpEffectSummary(
                touched.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                modified.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                nonQfreeWrites.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                measured.Where(r => paramNames.Contains(r.Reg)).ToHashSet(),
                irreversible);
            _summaries[opName] = summary;
            VerifyGraphCoherence(op.Name, paramNames, log.Events, log.Graph);   // fail-loud BEFORE anything lands on the model
            _model.AddOpEffects(op.Id, summary);
            _model.AddQubitEvents(op.Id, log.Events);
            _model.AddQubitGraph(op.Id, log.Graph);
            return summary;
        }

        // AnalyzeBlock/AnalyzeStmt do double duty: they RETURN the aggregated (touched, modified, nonQfreeWrites,
        // measured, irr) their caller unions for the operation summary, AND emit each LEAF statement's per-qubit
        // events into `log` (containers emit none of their own — their children carry the precise per-gate detail).
        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, HashSet<QubitRef> NonQfreeWrites, HashSet<QubitRef> Measured, bool Irreversible)
            AnalyzeBlock(IReadOnlyList<QStmt> body, OpEventLog log)
        {
            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            var nonQfreeWrites = new HashSet<QubitRef>();
            var measured = new HashSet<QubitRef>();
            var irreversible = false;
            foreach (var stmt in body)
            {
                var (t, m, s, me, irr) = AnalyzeStmt(stmt, log);
                touched.UnionWith(t);
                modified.UnionWith(m);
                nonQfreeWrites.UnionWith(s);
                measured.UnionWith(me);
                irreversible |= irr;
            }
            return (touched, modified, nonQfreeWrites, measured, irreversible);
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, HashSet<QubitRef> NonQfreeWrites, HashSet<QubitRef> Measured, bool Irreversible)
            AnalyzeStmt(QStmt stmt, OpEventLog log)
        {
            HashSet<QubitRef> touched = new(), modified = new(), nonQfreeWrites = new(), measured = new();
            var irreversible = false;
            var leaf = false;      // directly contacts qubits ⇒ emits its own events; containers do not

            switch (stmt)
            {
                case QUse:
                    // the birth was already recorded HOISTED at op start (see Summarize) — the textual
                    // statement itself contributes nothing further to the stream or the summaries
                    break;

                case QGate g:
                    (touched, modified, nonQfreeWrites, measured, irreversible) = AnalyzeGate(g);
                    leaf = true;
                    break;

                case QDecl { Value: QMeasure m }:
                    ApplyMeasure(m, touched, modified, measured);
                    irreversible = true;
                    leaf = true;
                    break;

                case QAssign { Value: QMeasure m }:
                    ApplyMeasure(m, touched, modified, measured);
                    irreversible = true;
                    leaf = true;
                    break;

                // A measurement may also sit inside an ARRAY LITERAL element (`var r: bit[] = [M(q[1]), 0]`)
                // — the same collapse as the direct form, and it must land in the same summaries, or the
                // measured (possibly entangled) qubit would be judged a safe cleanup candidate and the
                // uncompute ladder would plan around wrong facts.
                case QDecl { Value: QArrayLiteral dl } when dl.Elements.Any(e => e is QMeasure):
                    foreach (var element in dl.Elements.OfType<QMeasure>())
                        ApplyMeasure(element, touched, modified, measured);
                    irreversible = true;
                    leaf = true;
                    break;

                case QAssign { Value: QArrayLiteral al } when al.Elements.Any(e => e is QMeasure):
                    foreach (var element in al.Elements.OfType<QMeasure>())
                        ApplyMeasure(element, touched, modified, measured);
                    irreversible = true;
                    leaf = true;
                    break;

                // containers aggregate their children conservatively (union over all paths)
                case QIf i:
                    var (tt, tm, ts, tme, ti) = AnalyzeBlock(i.Then, log);
                    var (et, em, es, eme, ei) = AnalyzeBlock(i.Else, log);
                    touched.UnionWith(tt); touched.UnionWith(et);
                    modified.UnionWith(tm); modified.UnionWith(em);
                    nonQfreeWrites.UnionWith(ts); nonQfreeWrites.UnionWith(es);
                    measured.UnionWith(tme); measured.UnionWith(eme);
                    irreversible = ti || ei;
                    break;

                case QFor f:
                    (touched, modified, nonQfreeWrites, measured, irreversible) = AnalyzeBlock(f.Body, log);
                    break;

                case QWhile w:
                    (touched, modified, nonQfreeWrites, measured, irreversible) = AnalyzeBlock(w.Body, log);
                    break;

                case QRepeat r:
                    (touched, modified, nonQfreeWrites, measured, irreversible) = AnalyzeBlock(r.Body, log);
                    break;

                case QConjugate c:
                    // the flattened form is Within; Apply; inv(Within) — mirror the synthesized replay in the
                    // event stream (same statements, reversed statement order, fresh Orders), so liveness death
                    // points and clause-(c) windows see the W† the emitted program will actually run.
                    var mark = log.Events.Count;
                    var (wt, wm, ws, wme, wi) = AnalyzeBlock(c.Within, log);
                    var withinCount = log.Events.Count - mark;
                    var (at, am, ap, ame, ai) = AnalyzeBlock(c.Apply, log);
                    log.ReplayReversed(mark, withinCount);
                    touched.UnionWith(wt); touched.UnionWith(at);
                    modified.UnionWith(wm); modified.UnionWith(am);
                    nonQfreeWrites.UnionWith(ws); nonQfreeWrites.UnionWith(ap);
                    measured.UnionWith(wme); measured.UnionWith(ame);
                    irreversible = wi || ai;
                    break;

                // purely classical QDecl/QAssign: no qubit contact. The measurement-bit → later-if
                // dataflow edge is already recorded by Symbol.Uses — not duplicated here.
                default:
                    break;
            }

            if (leaf) log.Record(stmt.Id, touched, modified, nonQfreeWrites, measured, irreversible);
            return (touched, modified, nonQfreeWrites, measured, irreversible);
        }

        private (HashSet<QubitRef> Touched, HashSet<QubitRef> Modified, HashSet<QubitRef> NonQfreeWrites, HashSet<QubitRef> Measured, bool Irreversible)
            AnalyzeGate(QGate g)
        {
            // user-operation call (Adjoint Foo projects identically — U† touches the same qubits;
            // Controlled on a user op is impossible here, QSEM002 rejected it). A call is bound to its callee by
            // REFERENCE (CalleeOpId, set at name resolution): `CalleeOpId is int` ⟺ user-op call, and we follow
            // the reference — never re-matching the name, which shifts across mono/mangle domains.
            if (g.CalleeOpId is int calleeId)
            {
                // The reference must resolve within THIS analyzed program; a dangling Id is an internal
                // inconsistency (a rewrite dropped the callee or forgot to re-point), never a valid input.
                if (!_opById.TryGetValue(calleeId, out var callee))
                    throw new System.InvalidOperationException(
                        $"effect analysis: call `{g.Name}` binds CalleeOpId {calleeId}, but no such operation exists — a stale/dangling callee reference");
                // INVARIANT: the only functor that reaches a user-op call is Adjoint — Controlled on a user
                // op is rejected by QSEM002. Adjoint preserves both arg count and qubit support, so the
                // positional projection below is exact. A Controlled here would prepend a control arg,
                // misaligning params↔args and dropping the shifted target (a silent wrong-mapping +
                // under-approximation). If that invariant ever regressed, fail loud rather than mis-analyze.
                if (g.Functors.Any(f => f != "Adjoint"))
                    throw new System.InvalidOperationException(
                        $"effect analysis: user-op call `{g.Name}` carries a non-Adjoint functor [{string.Join(", ", g.Functors)}]; QSEM002 should have rejected it before analysis");
                var summary = Summarize(callee.Name);
                // Adjoint is superposition-agnostic: U† writes exactly what U writes, so the callee's
                // superposition-write set projects identically to its touched/modified sets. A param the
                // callee measures projects as measured here too — the caller-side event must be a Measure,
                // so a register measured through a helper is recognized as an output.
                return (Project(summary.ParamTouched, callee, g.Args),
                        Project(summary.ParamModified, callee, g.Args),
                        Project(summary.ParamModifiedNonQfree, callee, g.Args),
                        Project(summary.ParamMeasured, callee, g.Args),
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
                // (but NOT a superposition write: reset forces a basis state, it does not superpose)
                foreach (var arg in qubitArgs)
                {
                    var r = RefOf(arg);
                    touched.Add(r);
                    modified.Add(r);
                }
                return (touched, modified, new HashSet<QubitRef>(), new HashSet<QubitRef>(), true);
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
            // A non-qfree gate (H/Rx/Ry superposition, OR Y/CY phase-permutation) makes every value it WRITES
            // un-uncomputable; controls and diagonal targets are Reads (not modified), so they never carry it.
            var nonQfreeWrites = info.NonQfree ? new HashSet<QubitRef>(modified) : new HashSet<QubitRef>();
            return (touched, modified, nonQfreeWrites, new HashSet<QubitRef>(), false);
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
                        // a bare register argument is a QNameRef since lowering (QSEM006 rejects anything
                        // else in a qubit slot); render covers malformed hand-built IR without crashing.
                        var wholeName = whole.Tree is QNameRef nr ? nr.Name : QNodes.Render(whole.Tree);
                        foreach (var r in refs)
                            if (r.Reg == param.Name) result.Add(r with { Reg = wholeName });
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

        private static void ApplyMeasure(QMeasure m, HashSet<QubitRef> touched, HashSet<QubitRef> modified,
            HashSet<QubitRef> measured)
        {
            var r = RefOfTarget(m.Target);
            touched.Add(r);
            modified.Add(r);
            measured.Add(r);
        }

        /// <summary>A literal index stays precise; a loop-variable (or otherwise unknown) index is
        /// conservatively blanketed to the whole register — same split the validator's index check makes.
        /// Number-vs-name was settled ONCE at lowering (the index IS a QNumLit or QNameRef) — no
        /// re-parsing here, so this can never classify a token differently than any other pass.</summary>
        /// <summary>A measurement target (QNameRef for a whole single qubit, QIndexNode for an element)
        /// reduced to a QubitRef with the SAME literal-vs-blanket split <see cref="RefOf"/> makes.</summary>
        private static QubitRef RefOfTarget(QNode target) => QNodes.IndexOf(target) switch
        {
            QNumLit { Value: >= int.MinValue and <= int.MaxValue } n => new QubitRef(QNodes.RegOf(target), (int)n.Value),
            _ => new QubitRef(QNodes.RegOf(target), null),
        };

        private static QubitRef RefOf(QArg arg) => arg switch
        {
            QQubitArg { Index: QNumLit { Value: >= int.MinValue and <= int.MaxValue } n } q =>
                new QubitRef(q.Reg, (int)n.Value),
            QQubitArg q => new QubitRef(q.Reg, null),
            QTextArg { Tree: QNameRef nr } => new QubitRef(nr.Name, null),
            QTextArg t => new QubitRef(QNodes.Render(t.Tree), null),   // malformed hand-built IR, rendered
            _ => throw new System.InvalidOperationException($"unexpected argument kind: {arg}"),
        };

        /// <summary>One operation's growing event stream AND its qubit graph, written by the SAME hand in the
        /// same pass: each leaf statement's events are stamped with the value-version node they touch
        /// (Write/Measure → the node born here, 1:1; Read → the source's then-current node), and each new
        /// node's parent edges are recorded at the same moment — relations are written down when the analyzer
        /// KNOWS them, never re-derived by consumers from the flat timeline (the re-derivation is where three
        /// adversarially-confirmed soundness holes lived). A fresh log is made per <see cref="Summarize"/> so
        /// a callee's stream never interleaves with its caller's.</summary>
        private sealed class OpEventLog
        {
            public readonly List<QubitEvent> Events = new();
            public readonly QubitGraph Graph = new();
            private int _order;

            /// <summary>A qubit parameter's initial value comes from OUTSIDE the op — seed a v0 node per
            /// param register so its first use has a recorded origin.</summary>
            public OpEventLog(IEnumerable<string> qubitParamRegs)
            {
                foreach (var reg in qubitParamRegs) Graph.AddSeed(reg);
            }

            /// <summary>Record one leaf statement — one event per qubit it touched, its Kind decided by the
            /// per-qubit sets: a qubit in <paramref name="measured"/> is a <see cref="QubitEventKind.Measure"/>
            /// (whether measured directly by <c>M</c> or transitively through a call), a modified
            /// (value-changing) qubit is <see cref="QubitEventKind.Write"/>, anything else touched is
            /// <see cref="QubitEventKind.Read"/> (a control or diagonal-gate target). <paramref name="irreversible"/>
            /// is the statement's irreversibility (a reset, or a call that transitively measures/resets); it
            /// stamps NON-measured WRITES only — replaying a write needs the statement's adjoint, which an
            /// irreversible statement does not have, while a read through such a statement is harmless and a
            /// measured qubit carries its irreversibility in its Kind. <paramref name="nonQfreeWrites"/> stamp
            /// <c>NonQfree</c> on the matching Writes. Graph nodes/edges for the statement are
            /// created in the same call (see <see cref="Stamp"/>).</summary>
            public void Record(int stmtId, HashSet<QubitRef> touched, HashSet<QubitRef> modified,
                HashSet<QubitRef> nonQfreeWrites, HashSet<QubitRef> measured, bool irreversible, bool birth = false)
            {
                var refs = new List<(QubitRef R, QubitEventKind Kind, bool Irr, bool NonQfree)>();
                foreach (var r in touched)
                {
                    var isMeasured = measured.Contains(r);
                    var isModified = modified.Contains(r);
                    var kind = isMeasured ? QubitEventKind.Measure
                             : isModified ? QubitEventKind.Write
                             : QubitEventKind.Read;
                    refs.Add((r, kind, irreversible && isModified && !isMeasured, nonQfreeWrites.Contains(r)));
                }
                Stamp(stmtId, refs, birth);
            }

            /// <summary>Mirror a slice of already-recorded events as the synthesized REPLAY of its inverse —
            /// used by <see cref="QConjugate"/>, whose flattened form runs <c>inv(Within)</c> after
            /// <c>Apply</c>. The slice's statements are re-recorded in REVERSE statement order (kinds/flags
            /// preserved — an inverse touches the same qubits the same way) under fresh Orders AND fresh graph
            /// nodes with freshly-resolved parents (the replay reads/writes the CURRENT versions at replay
            /// time, not stale copies). StmtIds are preserved: a culprit still points at the source
            /// <c>within</c> statement.</summary>
            public void ReplayReversed(int start, int count)
            {
                if (count <= 0) return;
                var slice = Events.GetRange(start, count);
                foreach (var group in slice.GroupBy(e => e.Order).OrderByDescending(g => g.Key))
                    Stamp(group.First().StmtId,
                        group.Select(e => (e.Qubit, e.Kind, e.Irreversible, e.NonQfree)).ToList());
            }

            /// <summary>The core recorder: one statement's refs → graph nodes + linked events, all resolved
            /// against the PRE-statement state. Parent edges of each new version: the qubit's own previous
            /// version, every read source, and every co-written partner's previous version (gate-level
            /// analysis cannot tell which co-written operand a value flowed from — all are parents,
            /// conservatively). Each edge carries the ACCESS ref (<see cref="QubitEdge.Via"/>) so a blanketed
            /// read keeps its conservative breadth even when it resolves to a narrower version node.</summary>
            private void Stamp(int stmtId, List<(QubitRef R, QubitEventKind Kind, bool Irr, bool NonQfree)> refs,
                bool birth = false)
            {
                // 1) the PRE-statement current version of every touched ref
                var current = new Dictionary<QubitRef, int?>();
                foreach (var (r, _, _, _) in refs) current[r] = CurrentNode(r);

                // 2) new version nodes for the written refs
                var born = new Dictionary<QubitRef, int>();
                foreach (var (r, kind, _, _) in refs)
                {
                    if (kind == QubitEventKind.Read) continue;
                    // INVARIANT the verdict's birth exemption stands on: "a parentless write node ⟺ the
                    // register's `use` birth". A non-birth write with no prior version and no seed would
                    // mint a parentless impostor the exemption would wave through — fail loud instead
                    // (mirrors the read-before-birth guard below).
                    if (!birth && current[r] is null && Graph.ParamSeed(r.Reg) is null)
                        throw new System.InvalidOperationException(
                            $"QINTERNAL: effect analysis wrote `{r}` before any birth — only a `use` may create a register's first version");
                    var parents = new List<QubitEdge>();
                    void Add(int? id, QubitRef via)
                    {
                        if (id is int i && !parents.Any(p => p.NodeId == i && p.Via == via))
                            parents.Add(new QubitEdge(i, via));
                    }
                    Add(current[r], r);                                          // own previous version
                    foreach (var (o, _, _, _) in refs)
                        if (!o.Equals(r)) Add(current[o], o);                    // read sources + co-write prevs
                    born[r] = Graph.AddNode(r, parents);
                }

                // 3) the events, each linked to its node
                foreach (var (r, kind, irr, nonQfree) in refs)
                {
                    var nodeId = kind == QubitEventKind.Read
                        ? current[r] ?? throw new System.InvalidOperationException(
                            $"QINTERNAL: effect analysis read `{r}` before any birth — validated IR declares before use")
                        : born[r];
                    Events.Add(new QubitEvent(r, kind, _order, stmtId, irr, nonQfree, nodeId));
                }
                _order++;
            }

            /// <summary>The then-current version of <paramref name="r"/>: the newest Write/Measure event
            /// overlapping it (register/element subsumption lives HERE, in one place), else the register's
            /// parameter seed, else null (not yet born).</summary>
            private int? CurrentNode(QubitRef r)
            {
                for (var i = Events.Count - 1; i >= 0; i--)
                {
                    var e = Events[i];
                    if (e.Kind != QubitEventKind.Read && e.Qubit.Overlaps(r)) return e.NodeId;
                }
                return Graph.ParamSeed(r.Reg);
            }
        }

        /// <summary>PIPELINE INVARIANT (a <see cref="ReferentialCheck"/>-style safety net): the event stream
        /// and the qubit graph are written by the same hand, but a bug in that hand must fail LOUD at compile
        /// time — silent divergence between the two must never reach a consumer. Re-derives everything the
        /// graph claims with its OWN independent state (shares the spec, not the construction's code paths):
        /// node bounds, Write/Measure↔node 1:1 with matching ref, seed↔param 1:1, per-register Version
        /// sequence, parents strictly older than their children, every Read link re-computed as the
        /// then-current version, and every write node's parent SET re-computed edge-for-edge. Pass 3 also first
        /// asserts its OWN precondition — the stream is program-ordered (non-decreasing Order at each statement
        /// group boundary) — so a reordered stream fails loud before any re-derivation trusts it. ONE forward
        /// walk (O(events × distinct refs)) — this runs on every compile, including the extension's
        /// per-keystroke path, so it must stay linear-ish (adversarially flagged at Θ(E²) before).</summary>
        public static void VerifyGraphCoherence(   // public-in-private-class: reachable only via the outer seam
            string opName, IReadOnlyCollection<string> qubitParamRegs, List<QubitEvent> events, QubitGraph graph)
        {
            void Fail(string what) => throw new System.InvalidOperationException(
                $"QINTERNAL: qubit graph incoherent with the event stream in `{opName}` — {what}");

            // pass 1 — links, roles, 1:1
            var creatorOrder = new Dictionary<int, int>();   // nodeId → creating event's Order
            foreach (var e in events)
            {
                if (e.NodeId < 0 || e.NodeId >= graph.Nodes.Count)
                    Fail($"event at order {e.Order} points at missing node {e.NodeId}");
                var n = graph.Node(e.NodeId);
                if (e.Kind == QubitEventKind.Read)
                {
                    if (!n.Qubit.Overlaps(e.Qubit))
                        Fail($"read of {e.Qubit} at order {e.Order} is linked to unrelated node {n.Qubit}");
                }
                else
                {
                    if (n.IsParamSeed) Fail($"write at order {e.Order} is linked to a parameter seed node");
                    if (n.Qubit != e.Qubit) Fail($"write of {e.Qubit} at order {e.Order} is linked to a node of {n.Qubit}");
                    if (!creatorOrder.TryAdd(e.NodeId, e.Order)) Fail($"node {e.NodeId} has two creating events");
                }
            }

            // seeds ↔ qubit params, exactly (a dropped or spurious seed mis-roots every later lineage)
            var seedRegs = graph.Nodes.Where(n => n.IsParamSeed).Select(n => n.Qubit.Reg).ToHashSet();
            if (seedRegs.Count != graph.Nodes.Count(n => n.IsParamSeed)) Fail("duplicate parameter seed nodes");
            if (!seedRegs.SetEquals(qubitParamRegs))
                Fail($"seed registers [{string.Join(", ", seedRegs)}] do not match the op's qubit params [{string.Join(", ", qubitParamRegs)}]");
            foreach (var reg in seedRegs)
                if (graph.ParamSeed(reg) is not int sid || !graph.Node(sid).IsParamSeed)
                    Fail($"ParamSeed(`{reg}`) does not resolve to a seed node");

            // pass 2 — per-node facts: creator existence, parent bounds/age, seed shape, Version sequence
            var versionCounter = new Dictionary<string, int>();
            foreach (var n in graph.Nodes)
            {
                if (n.IsParamSeed && (n.Parents.Count != 0 || n.Qubit.Index is not null))
                    Fail($"seed node {n.Id} is malformed ({n.Qubit}, {n.Parents.Count} parents)");
                if (!n.IsParamSeed && !creatorOrder.ContainsKey(n.Id))
                    Fail($"node {n.Id} ({n.Qubit} v{n.Version}) has no creating event");
                foreach (var p in n.Parents)
                {
                    if (p.NodeId < 0 || p.NodeId >= graph.Nodes.Count) Fail($"node {n.Id} has missing parent {p.NodeId}");
                    var pOrder = graph.Node(p.NodeId).IsParamSeed ? -1 : creatorOrder[p.NodeId];
                    if (pOrder >= creatorOrder[n.Id])
                        Fail($"node {n.Id} has a parent ({p.NodeId}) not older than itself");
                }
                versionCounter.TryGetValue(n.Qubit.Reg, out var v);
                if (n.Version != v) Fail($"node {n.Id} ({n.Qubit}) has version {n.Version}; the sequence says {v}");
                versionCounter[n.Qubit.Reg] = v + 1;
            }

            // pass 3 — ONE forward walk with independent running state: every Read link and every write
            // node's parent set re-derived against the pre-statement versions. The running state keys by
            // EVENT INDEX, not Order: a statement can write one register TWICE at one Order (SWAP, a call
            // modifying two params) and the construction's backward list scan resolves that tie to the
            // LAST-appended write — an Order-keyed max is tie-ambiguous and provably diverged (adversarially
            // found: valid SWAP-then-blanket programs died QINTERNAL). Indices are unique; max-index equals
            // the backward scan by construction.
            var lastByRef = new Dictionary<QubitRef, (int Idx, int NodeId)>();
            int? Current(QubitRef r)
            {
                var bestIdx = -1; int? best = null;
                foreach (var kv in lastByRef)
                    if (kv.Key.Overlaps(r) && kv.Value.Idx > bestIdx) { bestIdx = kv.Value.Idx; best = kv.Value.NodeId; }
                return best ?? graph.ParamSeed(r.Reg);
            }
            for (var i = 0; i < events.Count;)
            {
                if (i > 0 && events[i].Order < events[i - 1].Order)
                    Fail($"event stream not program-ordered at index {i}");           // pass-3's own precondition
                var j = i;
                while (j < events.Count && events[j].Order == events[i].Order) j++;   // one statement's group
                for (var x = i; x < j; x++)
                {
                    var e = events[x];
                    if (e.Kind == QubitEventKind.Read)
                    {
                        var expect = Current(e.Qubit);
                        if (expect != e.NodeId)
                            Fail($"read of {e.Qubit} at order {e.Order} links to node {e.NodeId}; re-derivation says {(expect?.ToString() ?? "none")}");
                    }
                    else
                    {
                        var expected = new List<QubitEdge>();
                        void Expect(QubitRef via)
                        {
                            if (Current(via) is int id && !expected.Any(p => p.NodeId == id && p.Via == via))
                                expected.Add(new QubitEdge(id, via));
                        }
                        Expect(e.Qubit);                                                        // own previous version
                        for (var y = i; y < j; y++) if (events[y].Qubit != e.Qubit) Expect(events[y].Qubit);
                        var actual = graph.Node(e.NodeId).Parents;
                        if (actual.Count != expected.Count || expected.Any(p => !actual.Contains(p)))
                            Fail($"node {e.NodeId} ({e.Qubit} at order {e.Order}) has parents [{string.Join(", ", actual.Select(p => $"{p.NodeId} via {p.Via}"))}]; re-derivation says [{string.Join(", ", expected.Select(p => $"{p.NodeId} via {p.Via}"))}]");
                    }
                }
                for (var x = i; x < j; x++)                                                     // then advance the state
                    if (events[x].Kind != QubitEventKind.Read)
                        lastByRef[events[x].Qubit] = (x, events[x].NodeId);
                i = j;
            }
        }
    }
}
