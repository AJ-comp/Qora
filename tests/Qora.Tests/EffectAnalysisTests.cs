using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Effect analysis (auto-uncompute rung ①): each operation's program-ordered qubit-EVENT stream — every
/// leaf statement's reads/writes/measures — plus a per-operation summary, recorded on the persistent
/// <see cref="SemanticModel"/>. A Read preserves the computational-basis value (a control or diagonal-gate
/// target); a Write may change it (a target / reset / register birth); a Measure collapses it. Per-statement
/// touched/modified is READ OFF the stream by filtering on StmtId; entanglement edges by grouping on it.
/// Parse-based tests use NON-generic programs on purpose — <c>r.Ir</c> is the pre-monomorphize program, and
/// only for a generics-free program is that the exact program the analysis (and its Ids) ran on.
/// </summary>
public class EffectAnalysisTests
{
    private static (QoraParseResult R, SemanticModel M) Compile(string src)
    {
        var r = QoraParser.Parse(src);
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        Assert.NotNull(r.Semantics);
        return (r, r.Semantics!);
    }

    private static QOperation Op(QoraParseResult r, string name) => r.Ir!.Operations.Single(o => o.Name == name);

    private static QubitRef At(string reg, int i) => new(reg, i);
    private static QubitRef Whole(string reg) => new(reg, null);

    /// <summary>The events one leaf statement emitted (empty for a container or a classical statement).</summary>
    private static List<QubitEvent> EventsOf(SemanticModel m, QOperation op, QStmt stmt) =>
        m.QubitEvents(op.Id).Where(e => e.StmtId == stmt.Id).ToList();

    /// <summary>Reconstruct the old per-statement views from a statement's events: TOUCHED is every qubit it
    /// referenced; MODIFIED is those whose value may change (a Write or a Measure — a Read is value-preserving).</summary>
    private static HashSet<QubitRef> Touched(IEnumerable<QubitEvent> evs) => evs.Select(e => e.Qubit).ToHashSet();
    private static HashSet<QubitRef> Modified(IEnumerable<QubitEvent> evs) =>
        evs.Where(e => e.Kind != QubitEventKind.Read).Select(e => e.Qubit).ToHashSet();

    private static void AssertRefs(IReadOnlySet<QubitRef> actual, params QubitRef[] expected)
        => Assert.True(actual.SetEquals(expected),
            $"expected {{{string.Join(", ", expected)}}}, got {{{string.Join(", ", actual)}}}");

    // --- 0. the subsumption contract (rung-② query surface): a whole-register effect {q} and an element
    //        {q[0]} overlap BOTH ways, so a consumer must query via Overlaps, not exact-set Contains. ---

    [Fact]
    public void QubitRefOverlapIsSubsumptionAwareBothDirections()
    {
        Assert.True(Whole("q").Overlaps(At("q", 0)));    // whole register covers an element
        Assert.True(At("q", 0).Overlaps(Whole("q")));    // an element answers a whole-register query
        Assert.True(At("q", 0).Overlaps(At("q", 0)));    // same element
        Assert.True(Whole("q").Overlaps(Whole("q")));    // same whole register
        Assert.False(At("q", 0).Overlaps(At("q", 1)));   // distinct elements — no overlap
        Assert.False(Whole("q").Overlaps(Whole("r")));   // different registers
        Assert.False(At("q", 0).Overlaps(At("r", 0)));   // different registers
    }

    // --- 1. plain single-qubit gate: the target is a Write ---

    [Fact]
    public void SingleQubitGateWritesTarget()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; H(q[0]); }");
        var evs = EventsOf(m, Op(r, "Main"), Op(r, "Main").Body[1]);
        AssertRefs(Touched(evs), At("q", 0));
        AssertRefs(Modified(evs), At("q", 0));
    }

    // --- 2. controlled gate: the control is a Read (value preserved), the target a Write ---

    [Fact]
    public void CnotControlIsReadTargetIsWrite()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; CNOT(q[0], q[1]); }");
        var evs = EventsOf(m, Op(r, "Main"), Op(r, "Main").Body[1]);
        AssertRefs(Touched(evs), At("q", 0), At("q", 1));
        AssertRefs(Modified(evs), At("q", 1));   // only the target — the control stays a Read
    }

    // --- 3. the Controlled FUNCTOR prepends one control slot on top of the gate's own ---

    [Fact]
    public void ControlledFunctorAddsALeadingControlSlot()
    {
        var (r, m) = Compile("operation Main(){ use c=Qubit[1]; use q=Qubit[1]; Controlled X(c[0], q[0]); }");
        var evs = EventsOf(m, Op(r, "Main"), Op(r, "Main").Body[2]);
        AssertRefs(Touched(evs), At("c", 0), At("q", 0));
        AssertRefs(Modified(evs), At("q", 0));
    }

    // --- 4. SWAP has no controls: both operands are targets, both Writes ---

    [Fact]
    public void SwapWritesBothOperands()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; SWAP(q[0], q[1]); }");
        var evs = EventsOf(m, Op(r, "Main"), Op(r, "Main").Body[1]);
        AssertRefs(Touched(evs), At("q", 0), At("q", 1));
        AssertRefs(Modified(evs), At("q", 0), At("q", 1));
    }

    // --- 5. diagonal gates preserve every basis value: all operands are Reads, none modified;
    //        a rotation's angle argument produces no qubit event at all ---

    [Fact]
    public void DiagonalGatesAreReadsOnly()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; CZ(q[0], q[1]); Rz(pi/4, q[0]); }");
        var body = Op(r, "Main").Body;

        var cz = EventsOf(m, Op(r, "Main"), body[1]);
        AssertRefs(Touched(cz), At("q", 0), At("q", 1));
        Assert.Empty(Modified(cz));                          // diagonal: no Writes, both Reads
        Assert.All(cz, e => Assert.Equal(QubitEventKind.Read, e.Kind));

        var rz = EventsOf(m, Op(r, "Main"), body[2]);
        AssertRefs(Touched(rz), At("q", 0));                 // exactly one event — the angle is a value, not a qubit
        Assert.Empty(Modified(rz));
    }

    // --- 6. measurement is a Measure event (counts as modified) and makes the op irreversible;
    //        a purely classical declaration emits no qubit events ---

    [Fact]
    public void MeasurementIsAMeasureEventAndMarksOpIrreversible()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; const int x = 1; H(q[0]); bit r = M(q[0]); }");
        var main = Op(r, "Main");

        Assert.Empty(EventsOf(m, main, main.Body[1]));       // const int x — no qubit contact

        var measure = EventsOf(m, main, main.Body[3]);
        AssertRefs(Touched(measure), At("q", 0));
        AssertRefs(Modified(measure), At("q", 0));
        Assert.All(measure, e => Assert.Equal(QubitEventKind.Measure, e.Kind));

        var summary = m.FindOpEffects(main.Id);
        Assert.NotNull(summary);
        Assert.True(summary!.Irreversible);
        Assert.Empty(summary.ParamTouched);                  // entry op has no formal params
    }

    // --- 7. reset-like built-ins: targets are Writes, op irreversible; ResetAll blankets the register ---

    [Fact]
    public void ResetWritesAndIsIrreversible()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; Reset(q[0]); ResetAll(q); }");
        var main = Op(r, "Main");

        AssertRefs(Modified(EventsOf(m, main, main.Body[1])), At("q", 0));
        AssertRefs(Modified(EventsOf(m, main, main.Body[2])), Whole("q"));
        Assert.True(m.FindOpEffects(main.Id)!.Irreversible);
    }

    // --- 8. a whole-register broadcast gate blankets the register ---

    [Fact]
    public void BroadcastGateBlanketsWholeRegister()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[3]; H(q); }");
        var evs = EventsOf(m, Op(r, "Main"), Op(r, "Main").Body[1]);
        AssertRefs(Touched(evs), Whole("q"));
        AssertRefs(Modified(evs), Whole("q"));
    }

    // --- 9. containers emit NO events of their own — their leaves carry the precise per-gate detail.
    //        A loop-variable index blankets the register at the inner gate; a literal index stays precise ---

    [Fact]
    public void ContainersEmitNothingLeavesCarryTheDetail()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; for i in 0..1 { X(q[i]); } H(q[0]); }");
        var main = Op(r, "Main");
        var body = main.Body;

        var loop = Assert.IsType<QFor>(body[1]);
        Assert.Empty(EventsOf(m, main, loop));                               // the container holds no events itself
        AssertRefs(Modified(EventsOf(m, main, loop.Body[0])), Whole("q"));   // inner X(q[i]) — loop var blankets
        AssertRefs(Modified(EventsOf(m, main, body[2])), At("q", 0));        // H(q[0]) — literal stays precise
    }

    // --- 10. a QIf emits no events; both branch leaves appear in the stream (conservative: either may run) ---

    [Fact]
    public void IfBranchLeavesBothAppearContainerHasNone()
    {
        var (r, m) = Compile(
            "operation Main(){ use q=Qubit[2]; bit r = M(q[0]); if (r == 1) { X(q[0]); } else { H(q[1]); } }");
        var main = Op(r, "Main");
        var iff = Assert.IsType<QIf>(main.Body[2]);

        Assert.Empty(EventsOf(m, main, iff));                                // the if itself holds no events
        AssertRefs(Modified(EventsOf(m, main, iff.Then[0])), At("q", 0));    // then-branch X(q[0])
        AssertRefs(Modified(EventsOf(m, main, iff.Else[0])), At("q", 1));    // else-branch H(q[1])
    }

    // --- 11. two-level call DAG: a callee's summary is stated in FORMAL param names, and each call site
    //         projects it into the caller's actual registers (the call statement's events) ---

    [Fact]
    public void CallEventsProjectThroughTwoLevels()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit a, Qubit b){ CNOT(a, b); }\n" +
            "operation Bar(Qubit x, Qubit y){ Foo(x, y); }\n" +
            "operation Main(){ use q=Qubit[2]; Bar(q[0], q[1]); }");

        var foo = m.FindOpEffects(Op(r, "Foo").Id)!;
        AssertRefs(foo.ParamTouched, Whole("a"), Whole("b"));   // summary speaks formal names
        AssertRefs(foo.ParamModified, Whole("b"));
        Assert.False(foo.Irreversible);

        var call = EventsOf(m, Op(r, "Main"), Op(r, "Main").Body[1]);   // Bar(q[0], q[1]) — fully projected
        AssertRefs(Touched(call), At("q", 0), At("q", 1));
        AssertRefs(Modified(call), At("q", 1));

        Assert.Empty(m.FindOpEffects(Op(r, "Main").Id)!.ParamTouched);
    }

    // --- 12. single-qubit param binding: a literal actual stays precise, a loop-variable actual blankets ---

    [Fact]
    public void SingleQubitParamProjectsLiteralPreciselyAndLoopVarAsBlanket()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit a){ X(a); }\n" +
            "operation Main(){ use q=Qubit[3]; Foo(q[1]); for i in 0..2 { Foo(q[i]); } }");
        var main = Op(r, "Main");
        var body = main.Body;

        AssertRefs(Modified(EventsOf(m, main, body[1])), At("q", 1));
        var loop = Assert.IsType<QFor>(body[2]);
        AssertRefs(Modified(EventsOf(m, main, loop.Body[0])), Whole("q"));
    }

    // --- 13. Adjoint Foo touches/modifies exactly what Foo does (U† acts on the same qubits) ---

    [Fact]
    public void AdjointCallHasSameEventsAsPlainCall()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit[2] a){ H(a[0]); CNOT(a[0], a[1]); }\n" +
            "operation Main(){ use q=Qubit[2]; Foo(q); Adjoint Foo(q); }");
        var main = Op(r, "Main");

        var plain = EventsOf(m, main, main.Body[1]);
        var adjoint = EventsOf(m, main, main.Body[2]);
        Assert.True(Touched(plain).SetEquals(Touched(adjoint)));
        Assert.True(Modified(plain).SetEquals(Modified(adjoint)));
    }

    // --- 14. per-qubit program order: a register's events run birth-first, and the LAST by Order is its
    //         last use (the liveness death point) ---

    [Fact]
    public void EventsAreInProgramOrderPerQubitLastIsTheDeathPoint()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; H(q[0]); X(q[0]); }");
        var main = Op(r, "Main");
        var qEvents = m.QubitEvents(main.Id).Where(e => e.Qubit.Overlaps(Whole("q"))).ToList();

        // birth (use), then H, then X — three events, strictly increasing Order
        Assert.Equal(3, qEvents.Count);
        Assert.True(qEvents.Select(e => e.Order).SequenceEqual(qEvents.Select(e => e.Order).OrderBy(o => o)));
        Assert.True(qEvents.Zip(qEvents.Skip(1), (a, b) => a.Order < b.Order).All(x => x));

        var last = qEvents.OrderBy(e => e.Order).Last();
        Assert.Equal(main.Body[2].Id, last.StmtId);   // last event = X(q[0]) = last use / death point
    }

    // --- 15. entanglement edges are read off by grouping events on StmtId: the qubits sharing a statement
    //         are the ones that interacted there ---

    [Fact]
    public void EventsShareAStmtIdForTheQubitsThatInteract()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; CNOT(q[0], q[1]); }");
        var main = Op(r, "Main");
        var cnot = main.Body[1];

        var group = m.QubitEvents(main.Id).Where(e => e.StmtId == cnot.Id).Select(e => e.Qubit).ToHashSet();
        AssertRefs(group, At("q", 0), At("q", 1));   // both qubits of the CNOT share its StmtId
    }

    // --- 16. coverage regression: a representative program emits events for every LEAF (containers none),
    //         and emission is untouched (pure analysis) ---

    [Fact]
    public void EveryLeafOfARepresentativeProgramHasEventsContainersNone()
    {
        var (r, m) = Compile(
            "operation Main(){\n" +
            "  use q=Qubit[2];\n" +
            "  H(q[0]);\n" +
            "  Rz(pi/2, q[1]);\n" +
            "  for i in 0..1 { CNOT(q[0], q[1]); }\n" +
            "  bit r = M(q[0]);\n" +
            "  if (r == 1) { X(q[1]); }\n" +
            "}");
        var main = Op(r, "Main");

        foreach (var stmt in main.Body.SelectMany(Flatten))
        {
            var evs = EventsOf(m, main, stmt);
            var isContainer = stmt is QFor or QIf or QWhile or QRepeat or QConjugate;
            if (isContainer) Assert.Empty(evs);      // containers emit none of their own
            else Assert.NotEmpty(evs);               // every leaf (use / gate / measure) emits
        }

        // pure analysis: emission is untouched
        Assert.Contains("h q[0];", r.Qasm);
        Assert.Contains("cx q[0], q[1];", r.Qasm);
    }

    // --- 17. rung ② liveness (DERIVED, no storage): LiveRange is min/max-Order over a qubit's events —
    //         birth = the `use` register's birth Write, death = its last use ---

    [Fact]
    public void LiveRangeIsBirthToLastUse()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; H(q[0]); X(q[0]); }");
        var main = Op(r, "Main");

        var lr = m.LiveRange(main.Id, Whole("q"));
        Assert.NotNull(lr);
        Assert.Equal(main.Body[0].Id, lr!.Value.Birth.StmtId);   // birth = the `use q`
        Assert.Equal(main.Body[2].Id, lr.Value.Death.StmtId);    // death = X(q[0]), the last use
        Assert.True(lr.Value.Birth.Order < lr.Value.Death.Order);
    }

    // --- 18. the death point is the LAST use of any kind — a final control Read counts (the qubit still
    //         holds a value to clean after being read as a control) ---

    [Fact]
    public void LiveRangeDeathCountsAFinalReadUse()
    {
        var (r, m) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; X(a[0]); CNOT(a[0], b[0]); }");
        var main = Op(r, "Main");

        // a is written by X, then READ as CNOT's control — its last use is the CNOT, a Read.
        var lr = m.LiveRange(main.Id, Whole("a"));
        Assert.NotNull(lr);
        Assert.Equal(main.Body[3].Id, lr!.Value.Death.StmtId);       // CNOT(a[0], b[0])
        Assert.Equal(QubitEventKind.Read, lr.Value.Death.Kind);      // last use is a control Read, not a Write
    }

    // --- 19. a qubit with no events (never used) has no live range ---

    [Fact]
    public void LiveRangeIsNullForAnUnusedQubit()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; H(q[0]); }");
        Assert.Null(m.LiveRange(Op(r, "Main").Id, Whole("nope")));   // no register 'nope' → no events
    }

    // --- 20. rung ③ groundwork — the Irreversible flag: a reset (a non-unitary Write) and an irreversible
    //         call carry it, while a plain unitary write does not, and a measurement carries its
    //         irreversibility in its Kind (flag stays off) ---

    [Fact]
    public void IrreversibleFlagMarksResetAndIrreversibleCallButNotUnitaryWriteOrMeasure()
    {
        var (r, m) = Compile(
            "operation Sink(Qubit a){ Reset(a); }\n" +
            "operation Main(){ use q=Qubit[4]; H(q[0]); Reset(q[1]); bit b = M(q[2]); Sink(q[3]); }");
        var main = Op(r, "Main");

        // H(q[0]) — a reversible unitary write: not irreversible
        var h = EventsOf(m, main, main.Body[1]).Single();
        Assert.Equal(QubitEventKind.Write, h.Kind);
        Assert.False(h.Irreversible);

        // Reset(q[1]) — a non-unitary write: lands as a Write (hidden without the flag), so it MUST be flagged
        var reset = EventsOf(m, main, main.Body[2]).Single();
        Assert.Equal(QubitEventKind.Write, reset.Kind);
        Assert.True(reset.Irreversible);

        // M(q[2]) — a measurement: the Measure kind carries the irreversibility; the flag stays off
        var meas = EventsOf(m, main, main.Body[3]).Single();
        Assert.Equal(QubitEventKind.Measure, meas.Kind);
        Assert.False(meas.Irreversible);

        // Sink(q[3]) — a call whose body resets: the projected event is flagged irreversible
        Assert.True(EventsOf(m, main, main.Body[4]).Single().Irreversible);
    }

    // --- 21. rung ③ qfree — the NonQfree flag: a non-qfree WRITE carries it for either reason — superposition
    //         (H, Rx, Ry) OR phase permutation (Y, CY). A phase-free permutation write (X, CNOT target), a
    //         diagonal Read (CZ), a reset, and a `use` birth do NOT ---

    [Fact]
    public void NonQfreeFlagMarksOnlyNonQfreeWrites()
    {
        var (r, m) = Compile(
            "operation Main(){ use q=Qubit[2]; H(q[0]); X(q[0]); Rx(pi/2, q[1]); CNOT(q[0], q[1]); Y(q[0]); CZ(q[0], q[1]); Reset(q[0]); }");
        var main = Op(r, "Main");
        var body = main.Body;
        bool NonQ(QStmt s, QubitRef q) => EventsOf(m, main, s).Single(e => e.Qubit.Equals(q)).NonQfree;

        Assert.All(EventsOf(m, main, body[0]), e => Assert.False(e.NonQfree));  // use birth = |0…0⟩
        Assert.True(NonQ(body[1], At("q", 0)));                                             // H — superposition
        Assert.False(NonQ(body[2], At("q", 0)));                                            // X — phase-free permutation
        Assert.True(NonQ(body[3], At("q", 1)));                                             // Rx — superposition
        Assert.All(EventsOf(m, main, body[4]), e => Assert.False(e.NonQfree));  // CNOT — control Read + classical Write
        Assert.True(NonQ(body[5], At("q", 0)));                                             // Y — phase-carrying permutation (round-5 fix)
        Assert.All(EventsOf(m, main, body[6]), e => Assert.False(e.NonQfree));  // CZ — diagonal, both Reads
        Assert.False(NonQ(body[7], At("q", 0)));                                            // Reset — forces |0⟩, not qfree-relevant (a Write, but classical)
    }

    // --- 22. NonQfree propagates through user-op calls PER PARAM: a helper that H's a param
    //         superposes it (the call site's projected write is flagged); one that only X's a param does not ---

    [Fact]
    public void SuperpositionPropagatesThroughCallsPerParam()
    {
        var (r, m) = Compile(
            "operation Super(Qubit a){ H(a); }\n" +
            "operation Classical(Qubit a){ X(a); }\n" +
            "operation Main(){ use q=Qubit[2]; Super(q[0]); Classical(q[1]); }");
        var main = Op(r, "Main");

        AssertRefs(m.FindOpEffects(Op(r, "Super").Id)!.ParamModifiedNonQfree, Whole("a"));   // Super superposes its param
        Assert.Empty(m.FindOpEffects(Op(r, "Classical").Id)!.ParamModifiedNonQfree);          // Classical does not

        Assert.True(EventsOf(m, main, main.Body[1]).Single().NonQfree);    // Super(q[0]) — flagged
        Assert.False(EventsOf(m, main, main.Body[2]).Single().NonQfree);   // Classical(q[1]) — not
    }

    // --- 23. the two-term split, ONE TERM PER CONCEPT: IsAncilla = BIRTH (a `use` workspace register —
    //         measurement does not change what it was born as); IsCleanupCandidate = ancilla whose value was
    //         never delivered (measured ⇒ promoted to output ⇒ leaves the cleanup pool) ---

    [Fact]
    public void AncillaIsBirthCleanupCandidateIsLiveness()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit p){ H(p); }\n" +
            "operation Main(){ use a=Qubit[1]; use o=Qubit[1]; Foo(a[0]); H(o[0]); bit b = M(o[0]); }");
        var main = Op(r, "Main");
        var foo = Op(r, "Foo");

        // ancilla = born as a use workspace — the MEASURED register is still an ancilla (promoted, not unmade)
        Assert.True(m.IsAncilla(main.Id, Whole("a")));
        Assert.True(m.IsAncilla(main.Id, Whole("o")));               // measured, but born an ancilla
        Assert.False(m.IsAncilla(foo.Id, Whole("p")));               // a parameter is caller-owned, never an ancilla
        Assert.False(m.IsAncilla(main.Id, Whole("nope")));           // unknown name

        // cleanup candidate = ancilla + value never delivered
        Assert.True(m.IsCleanupCandidate(main.Id, Whole("a")));      // ancilla, never measured → candidate
        Assert.False(m.IsCleanupCandidate(main.Id, Whole("o")));     // ancilla, but promoted to output → not
        Assert.False(m.IsCleanupCandidate(foo.Id, Whole("p")));      // not an ancilla at all
        Assert.False(m.IsCleanupCandidate(main.Id, Whole("nope")));  // unknown name → not
    }

    // --- 24. gate-table invariant (fail-safe guard for clause b): NonQfree is EXACTLY {H, Rx, Ry (superposition),
    //         Y, CY (phase permutation)}, and every built-in must be classified here — a NEW gate added without
    //         a deliberate flag value breaks this test rather than silently defaulting to qfree (which would let
    //         rung ③ green-light an un-uncomputable ancilla) ---

    [Fact]
    public void EveryGateIsConsciouslyClassifiedForNonQfree()
    {
        var expected = new Dictionary<string, bool>
        {
            ["H"] = true, ["Rx"] = true, ["Ry"] = true,                        // superposition creators
            ["Y"] = true, ["CY"] = true,                                       // phase permutations (round-5 fix)
            ["X"] = false, ["Z"] = false, ["S"] = false, ["T"] = false,
            ["CNOT"] = false, ["CX"] = false, ["CZ"] = false,
            ["SWAP"] = false, ["CCX"] = false, ["Rz"] = false,
            ["Reset"] = false, ["ResetAll"] = false,
        };

        foreach (var (name, info) in QoraGates.Gates)
        {
            Assert.True(expected.ContainsKey(name),
                $"gate `{name}` is not classified for NonQfree — classify it (superposition H/Rx/Ry or phase-permutation Y/CY = true, a phase-free permutation false) so rung ③ qfree stays sound");
            Assert.Equal(expected[name], info.NonQfree);
        }
    }

    // --- 25. IsSafelyUncomputable (DERIVED, the rung ③ predicate): a classically-computed ancilla used as a
    //         control is safe; a superposition (H) write, a phase-permutation (Y) write, a measurement, and a
    //         source modified before the drop each make it unsafe. The Y case is the round-5 fix — an earlier
    //         "Y is safe, broader than Silq" claim was state-vector-refuted (see YEntangledPhase... below) ---

    [Fact]
    public void IsSafelyUncomputableAcceptsClassicalRejectsNonQfreeMeasureAndDeadSource()
    {
        // (safe) a is a classical function of self, used as a control
        var (r1, m1) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; X(a[0]); CNOT(a[0], b[0]); }");
        Assert.True(m1.IsSafelyUncomputable(Op(r1, "Main"), Whole("a")));

        // (unsafe: phase permutation) Y carries a basis-value-dependent phase — under entanglement the injected
        // Y† strips a survivor-relative phase; blocked as NonQfreeWrite, verdict names the Y statement
        var (r2, m2) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Y(a[0]); CNOT(a[0], b[0]); }");
        var main2 = Op(r2, "Main");
        Assert.False(m2.IsSafelyUncomputable(main2, Whole("a")));
        var v2 = m2.UncomputeSafety(main2, Whole("a"));
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, v2.Blocker);
        Assert.Equal(main2.Body[2].Id, v2.Culprit!.StmtId);          // culprit = the Y(a[0])

        // (unsafe: superposition) H writes a fresh superposition into a — verdict names the H statement
        var (r3, m3) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; H(a[0]); CNOT(a[0], b[0]); }");
        var main3 = Op(r3, "Main");
        Assert.False(m3.IsSafelyUncomputable(main3, Whole("a")));
        var v3 = m3.UncomputeSafety(main3, Whole("a"));
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, v3.Blocker);
        Assert.Equal(main3.Body[2].Id, v3.Culprit!.StmtId);          // culprit = the H(a[0])

        // (unsafe: measured) a collapse cannot be replayed
        var (r4, m4) = Compile("operation Main(){ use a=Qubit[1]; X(a[0]); bit c = M(a[0]); }");
        Assert.False(m4.IsSafelyUncomputable(Op(r4, "Main"), Whole("a")));
        Assert.Equal(UncomputeBlocker.Measured, m4.UncomputeSafety(Op(r4, "Main"), Whole("a")).Blocker);

        // (unsafe: reset) an irreversible touch — the flag from rung ①'s hardening
        var (r6, m6) = Compile("operation Main(){ use a=Qubit[1]; X(a[0]); Reset(a[0]); }");
        Assert.Equal(UncomputeBlocker.Irreversible, m6.UncomputeSafety(Op(r6, "Main"), Whole("a")).Blocker);

        // (unsafe: source dies) b = a, then a is flipped BEFORE b's last use → b can't be rebuilt from a;
        // the verdict points at the flip that killed the source
        var (r5, m5) = Compile(
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; use c=Qubit[1]; CNOT(a[0], b[0]); X(a[0]); CNOT(b[0], c[0]); }");
        var main5 = Op(r5, "Main");
        Assert.False(m5.IsSafelyUncomputable(main5, Whole("b")));
        var v5 = m5.UncomputeSafety(main5, Whole("b"));
        Assert.Equal(UncomputeBlocker.SourceDied, v5.Blocker);
        Assert.Equal(main5.Body[4].Id, v5.Culprit!.StmtId);          // culprit = the X(a[0])

        // safe ⇒ no blocker, no culprit
        var safe = m1.UncomputeSafety(Op(r1, "Main"), Whole("a"));
        Assert.True(safe.IsSafe);
        Assert.Null(safe.Culprit);
    }

    // --- 26. the ④-0 dry-run view (--stages `uncompute`): every qubit classified (input / output / ancilla
    //         candidate), each candidate carrying its rung-③ verdict with the reason ---

    [Fact]
    public void UncomputeReportClassifiesQubitsAndNamesTheBlocker()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit p){ X(p); }\n" +
            "operation Main(){ use a=Qubit[1]; use h=Qubit[1]; use o=Qubit[1]; X(a[0]); H(h[0]); bit b = M(o[0]); Foo(a[0]); }");
        var report = UncomputeReport.Format(r.AnalyzedIr, m);

        Assert.Contains("p: input (parameter) — caller-owned, not an ancilla", report);                          // param = input
        Assert.Contains("a: ancilla, cleanup candidate — safe to uncompute", report);                        // clean scratch
        Assert.Contains("h: ancilla, cleanup candidate — NOT uncomputable: non-qfree write", report);        // H-born
        Assert.Contains("o: ancilla, measured — promoted to output, not a cleanup candidate", report);                          // read out

        // nothing to say ⇒ empty (no model / no program)
        Assert.Equal(string.Empty, UncomputeReport.Format(r.AnalyzedIr, null));
        Assert.Equal(string.Empty, UncomputeReport.Format(null, m));
    }

    // --- 27. co-written partners (adversarially found): a statement that WRITES q and another qubit at once
    //         (SWAP value-move, a call modifying two params) blocks OUTRIGHT — the injected adjoint would
    //         rewrite the partner at q's death, and the partner's later uses can sit beyond any window ---

    [Fact]
    public void CoWrittenPartnerBlocksSwapAndCoModifyingCalls()
    {
        // partner rewritten inside the window — the original attack; culprit = the SWAP's partner write
        var (r1, m1) = Compile(
            "operation Main(){ use anc=Qubit[1]; use x=Qubit[1]; use o=Qubit[1]; X(x[0]); SWAP(anc[0], x[0]); X(x[0]); CNOT(anc[0], o[0]); bit b = M(o[0]); }");
        var main1 = Op(r1, "Main");
        var v1 = m1.UncomputeSafety(main1, Whole("anc"));
        Assert.Equal(UncomputeBlocker.CoWrittenPartner, v1.Blocker);
        Assert.Equal(main1.Body[4].Id, v1.Culprit!.StmtId);          // the SWAP statement
        Assert.Equal(At("x", 0), v1.Culprit.Qubit);                  // the co-written partner

        // clean window is NOT enough: the adjoint still reverts the partner at anc's death,
        // and the partner's next use (CNOT(x,p)) sits AFTER that death — must still block
        var (r2, m2) = Compile(
            "operation Main(){ use anc=Qubit[1]; use x=Qubit[1]; use o=Qubit[1]; use p=Qubit[1]; X(x[0]); SWAP(anc[0], x[0]); CNOT(anc[0], o[0]); CNOT(x[0], p[0]); bit b1 = M(o[0]); bit b2 = M(p[0]); }");
        Assert.Equal(UncomputeBlocker.CoWrittenPartner,
            m2.UncomputeSafety(Op(r2, "Main"), Whole("anc")).Blocker);

        // the same value-move hidden inside a call: Twist writes BOTH its params
        var (r3, m3) = Compile(
            "operation Twist(Qubit a, Qubit s){ CNOT(s, a); X(s); }\n" +
            "operation Main(){ use anc=Qubit[1]; use x=Qubit[1]; use o=Qubit[1]; X(x[0]); Twist(anc[0], x[0]); X(x[0]); CNOT(anc[0], o[0]); bit b = M(o[0]); }");
        Assert.Equal(UncomputeBlocker.CoWrittenPartner,
            m3.UncomputeSafety(Op(r3, "Main"), Whole("anc")).Blocker);
    }

    // --- 28. no event stream ⇒ NotAnalyzed, never a vacuous "safe" ---

    [Fact]
    public void UnanalyzedOpAnswersNotAnalyzedNotSafe()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; H(q[0]); }");
        // an op the analysis never saw (fresh Id — no stream was ever recorded for it)
        var ghost = new QOperation("Ghost", new List<QParam>(), new List<QStmt>());

        var v = m.UncomputeSafety(ghost, Whole("q"));
        Assert.Equal(UncomputeBlocker.NotAnalyzed, v.Blocker);
        Assert.False(v.IsSafe);
        Assert.Null(v.Culprit);
        Assert.False(m.IsCleanupCandidate(ghost.Id, Whole("q")));
    }

    // --- 29. a register measured TRANSITIVELY through a call is an output, exactly like a direct M:
    //         the call site's projected event is a Measure, so candidacy and the report classify it right ---

    [Fact]
    public void TransitiveMeasureThroughACallMakesAnOutput()
    {
        var (r, m) = Compile(
            "operation ReadOut(Qubit[1] s){ bit r = M(s[0]); }\n" +
            "operation Main(){ use o=Qubit[1]; X(o[0]); ReadOut(o); }");
        var main = Op(r, "Main");

        AssertRefs(m.FindOpEffects(Op(r, "ReadOut").Id)!.ParamMeasured, At("s", 0));   // summary knows

        var call = EventsOf(m, main, main.Body[2]).Single();
        Assert.Equal(QubitEventKind.Measure, call.Kind);      // projected as a Measure, not a flagged Write
        Assert.False(call.Irreversible);                      // the Measure kind carries the irreversibility

        Assert.False(m.IsCleanupCandidate(main.Id, Whole("o")));
        Assert.Contains("o: ancilla, measured — promoted to output, not a cleanup candidate", UncomputeReport.Format(r.AnalyzedIr, m));
    }

    // --- 30. clause-(c) window boundaries: a source killed AT the death statement blocks; one killed strictly
    //         AFTER the death is safe (the uncompute is injected at the death point, before it); a source
    //         killed BETWEEN two writes of q blocks ---

    [Fact]
    public void SourceWindowIsWriteExclusiveToDeathInclusive()
    {
        // killer shares the death statement: CNOT(a,s) writes s while a dies there → (write, death] catches it
        var (r1, m1) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; CNOT(s[0], a[0]); CNOT(a[0], s[0]); }");
        Assert.Equal(UncomputeBlocker.SourceDied, m1.UncomputeSafety(Op(r1, "Main"), Whole("a")).Blocker);

        // killer strictly after a's death: the injection point precedes it — safe
        var (r2, m2) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; CNOT(s[0], a[0]); CNOT(a[0], o[0]); X(s[0]); }");
        Assert.True(m2.IsSafelyUncomputable(Op(r2, "Main"), Whole("a")));

        // source killed between q's two writes: still inside the first write's window
        var (r3, m3) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; CNOT(s[0], a[0]); X(s[0]); CNOT(s[0], a[0]); CNOT(a[0], o[0]); }");
        Assert.Equal(UncomputeBlocker.SourceDied, m3.UncomputeSafety(Op(r3, "Main"), Whole("a")).Blocker);
    }

    // --- 31. culprit identity: a MEASURED source reports the Measure event; an irreversible call reports the
    //         call statement ---

    [Fact]
    public void CulpritsNameTheMeasuringAndIrreversibleStatements()
    {
        var (r1, m1) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; CNOT(s[0], a[0]); bit r = M(s[0]); CNOT(a[0], o[0]); }");
        var v1 = m1.UncomputeSafety(Op(r1, "Main"), Whole("a"));
        Assert.Equal(UncomputeBlocker.SourceDied, v1.Blocker);
        Assert.Equal(QubitEventKind.Measure, v1.Culprit!.Kind);      // the killer was a measurement of the source

        var (r2, m2) = Compile(
            "operation Zap(Qubit t){ Reset(t); }\n" +
            "operation Main(){ use a=Qubit[1]; X(a[0]); Zap(a[0]); }");
        var main2 = Op(r2, "Main");
        var v2 = m2.UncomputeSafety(main2, Whole("a"));
        Assert.Equal(UncomputeBlocker.Irreversible, v2.Blocker);
        Assert.Equal(main2.Body[2].Id, v2.Culprit!.StmtId);          // the Zap(a[0]) call statement
    }

    // --- 32. Adjoint calls propagate the superposition flag (U† superposes exactly what U does) ---

    [Fact]
    public void AdjointCallPropagatesSuperpositionFlag()
    {
        var (r, m) = Compile(
            "operation Sup(Qubit a){ H(a); }\n" +
            "operation Main(){ use q=Qubit[1]; Adjoint Sup(q[0]); }");
        var main = Op(r, "Main");

        Assert.True(EventsOf(m, main, main.Body[1]).Single().NonQfree);
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, m.UncomputeSafety(main, Whole("q")).Blocker);
    }

    // --- 33. Controlled H: the added control slot stays an unflagged Read (and uncomputable); only the
    //         target's Write carries NonQfree ---

    [Fact]
    public void ControlledHFlagsTargetNotControl()
    {
        var (r, m) = Compile("operation Main(){ use c=Qubit[1]; use q=Qubit[1]; X(c[0]); Controlled H(c[0], q[0]); }");
        var main = Op(r, "Main");
        var evs = EventsOf(m, main, main.Body[3]);

        var control = evs.Single(e => e.Qubit.Equals(At("c", 0)));
        Assert.Equal(QubitEventKind.Read, control.Kind);
        Assert.False(control.NonQfree);
        Assert.True(m.IsSafelyUncomputable(main, Whole("c")));

        var target = evs.Single(e => e.Qubit.Equals(At("q", 0)));
        Assert.Equal(QubitEventKind.Write, target.Kind);
        Assert.True(target.NonQfree);
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, m.UncomputeSafety(main, Whole("q")).Blocker);
    }

    // --- 34. QConjugate mirrors the synthesized W† in the stream: the flattened form runs inv(Within) after
    //         Apply, so the within-qubit's death point must sit AFTER the apply statements ---

    [Fact]
    public void ConjugateStreamContainsTheReplayedInverse()
    {
        var xStmt = new QGate(new List<string>(), "X", new List<QArg> { new QQubitArg("a", "0") });
        var program = new QProgram(new List<QOperation> { new("Main", new List<QParam>(), new List<QStmt>
        {
            new QUse("a", 1),
            new QUse("d", 1),
            new QConjugate(
                Within: new List<QStmt> { xStmt },
                Apply: new List<QStmt> { new QGate(new List<string>(), "CNOT", new List<QArg> { new QQubitArg("a", "0"), new QQubitArg("d", "0") }) }),
        }) });
        var m = new SemanticModel();
        EffectAnalysis.Run(program, m);
        var op = program.Operations.Single();

        // a: birth, X-write, CNOT-control-read, REPLAYED X-write — the replay is last, same stmt, fresh order
        var aEvents = m.QubitEvents(op.Id).Where(e => e.Qubit.Overlaps(Whole("a"))).OrderBy(e => e.Order).ToList();
        Assert.Equal(4, aEvents.Count);
        var replay = aEvents[^1];
        Assert.Equal(QubitEventKind.Write, replay.Kind);
        Assert.Equal(xStmt.Id, replay.StmtId);                        // culprits still point at the source within-stmt

        var death = m.LiveRange(op.Id, Whole("a"))!.Value.Death;
        Assert.Equal(replay.Order, death.Order);                      // death = the W† replay …
        Assert.True(death.Order > aEvents[2].Order);                  // … strictly after the apply's control read
    }

    // --- 35. the report reads the ANALYZED (post-monomorphize) program, so generic ops appear as their
    //         specializations instead of silently vanishing ---

    [Fact]
    public void ReportShowsMonomorphizedSpecializationsOfGenerics()
    {
        var (r, m) = Compile(
            "operation Prep(Qubit[n] p){ X(p[0]); }\n" +
            "operation Main(){ use w=Qubit[2]; Prep(w); }");
        var report = UncomputeReport.Format(r.AnalyzedIr, m);

        Assert.Contains("Prep", report);                              // the specialization row exists
        Assert.Contains("p: input (parameter) — caller-owned, not an ancilla", report);
        Assert.Contains("w: ancilla, cleanup candidate — safe to uncompute", report);

        // the generic DEF itself was never analyzed (only its specializations were) — never a vacuous safe
        Assert.Equal(UncomputeBlocker.NotAnalyzed,
            m.UncomputeSafety(r.Ir!.Operations.Single(o => o.Name == "Prep"), Whole("p")).Blocker);
    }

    // --- 36. per-element refinement: one blocked element must not hide that the others are clean candidates ---

    [Fact]
    public void ReportNamesCleanElementsWhenTheRegisterIsBlocked()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; X(q[0]); H(q[1]); }");
        var report = UncomputeReport.Format(r.AnalyzedIr, m);

        Assert.Contains("NOT uncomputable: non-qfree write", report);       // whole-register headline
        Assert.Contains("per element: q[0] safe cleanup candidate", report);        // the clean element is named
    }

    // --- 37. ContainedWrite: a WRITE of q inside a container (if / for) blocks — the flat event stream
    //         walks a container body once, so a straight-line adjoint would not mirror the conditional /
    //         repeated execution. A READ inside a container is harmless (only the write chain is replayed) ---

    [Fact]
    public void WriteInsideContainerBlocksReadInsideDoesNot()
    {
        // write inside an if → blocked, culprit = the contained CNOT
        var (r1, m1) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; use x=Qubit[1]; " +
            "bit r = M(x[0]); if (r == 1) { CNOT(s[0], a[0]); } CNOT(a[0], o[0]); }");
        var main1 = Op(r1, "Main");
        var iff1 = Assert.IsType<QIf>(main1.Body[5]);
        var v1 = m1.UncomputeSafety(main1, Whole("a"));
        Assert.Equal(UncomputeBlocker.ContainedWrite, v1.Blocker);
        Assert.Equal(iff1.Then[0].Id, v1.Culprit!.StmtId);          // the CNOT inside the if

        // write inside a for → blocked for the same reason (repeated, not conditional)
        var (r2, m2) = Compile("operation Main(){ use a=Qubit[2]; for i in 0..1 { X(a[i]); } }");
        Assert.Equal(UncomputeBlocker.ContainedWrite,
            m2.UncomputeSafety(Op(r2, "Main"), Whole("a")).Blocker);

        // READ inside an if is harmless: a's writes are all unconditional, so the adjoint mirrors them
        var (r3, m3) = Compile(
            "operation Main(){ use a=Qubit[1]; use o=Qubit[1]; use x=Qubit[1]; " +
            "bit r = M(x[0]); X(a[0]); if (r == 1) { CNOT(a[0], o[0]); } }");
        Assert.True(m3.IsSafelyUncomputable(Op(r3, "Main"), Whole("a")));
    }

    // --- 38. contained DEATH (adversarially found blocker): q dies inside a container and its source is
    //         rewritten later in the SAME container. The realizable injection point is after the container,
    //         so the source-liveness window must reach the container's end — not stop at the death ---

    [Fact]
    public void SourceKilledAfterAContainedDeathBlocks()
    {
        // if: a's death is the control Read inside the if; the source kill X(s) follows it inside the same if
        var (r1, m1) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; use x=Qubit[1]; " +
            "H(x[0]); bit r = M(x[0]); X(s[0]); CNOT(s[0], a[0]); if (r == 1) { CNOT(a[0], o[0]); X(s[0]); } }");
        var v1 = m1.UncomputeSafety(Op(r1, "Main"), Whole("a"));
        Assert.Equal(UncomputeBlocker.SourceDied, v1.Blocker);
        Assert.Equal(At("s", 0), v1.Culprit!.Qubit);                 // the in-container kill is the culprit

        // for: deterministic variant — the kill sits in the loop body after a's last read
        var (r2, m2) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use t=Qubit[3]; " +
            "X(s[0]); CNOT(s[0], a[0]); for i in 0..2 { CNOT(a[0], t[i]); X(s[0]); } }");
        Assert.Equal(UncomputeBlocker.SourceDied, m2.UncomputeSafety(Op(r2, "Main"), Whole("a")).Blocker);
    }

    // --- 39. same hole through a conjugation (hand-built — no surface syntax): a death inside Apply with a
    //         later source kill in Apply blocks too (containers are treated uniformly-conservatively) ---

    [Fact]
    public void SourceKilledAfterADeathInsideAConjugateBlocks()
    {
        static QGate G(string n, params QArg[] a) => new(new List<string>(), n, a.ToList());
        var op = new QOperation("Main", new List<QParam>(), new List<QStmt>
        {
            new QUse("a", 1), new QUse("s", 1), new QUse("w", 1), new QUse("d", 1),
            G("X", new QQubitArg("s", "0")),
            G("CNOT", new QQubitArg("s", "0"), new QQubitArg("a", "0")),
            new QConjugate(
                Within: new List<QStmt> { G("CNOT", new QQubitArg("d", "0"), new QQubitArg("w", "0")) },
                Apply: new List<QStmt>
                {
                    G("CNOT", new QQubitArg("a", "0"), new QQubitArg("d", "0")),
                    G("X", new QQubitArg("s", "0")),
                }),
        });
        var m = new SemanticModel();
        EffectAnalysis.Run(new QProgram(new List<QOperation> { op }), m);
        Assert.Equal(UncomputeBlocker.SourceDied, m.UncomputeSafety(op, Whole("a")).Blocker);
    }

    // --- 40. foreign-tree guard: a with-copy keeps the op Id but carries a DIFFERENT statement tree — the
    //         safety checker must fail loud, never read absent map keys as "not contained" ---

    [Fact]
    public void ForeignTreeWithSameOpIdFailsLoud()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; X(q[0]); }");
        var analyzed = Op(r, "Main");
        var foreign = analyzed with
        {
            Body = new List<QStmt> { new QGate(new List<string>(), "X", new List<QArg> { new QQubitArg("q", "0") }) },
        };
        Assert.Throws<System.InvalidOperationException>(() => m.UncomputeSafety(foreign, Whole("q")));
    }

    // --- 41. blanket partner under an ELEMENT query (adversarially found blocker): a same-register blanket
    //         source covers OTHER elements too, so it is a real source — its kill must be scanned, not
    //         exempted as "q's own chain" ---

    [Fact]
    public void BlanketPartnerUnderElementQueryIsARealSource()
    {
        var (r, m) = Compile(
            "operation Fold(Qubit[2] p){ for i in 0..0 { CNOT(p[i], p[1]); } }\n" +
            "operation Main(){ use a=Qubit[2]; use w=Qubit[1]; X(a[0]); Fold(a); X(a[0]); CNOT(a[1], w[0]); bit b = M(a[0]); }");
        // a[1] = f(a[0]) via the Fold call (whose control blankets to the whole register); X(a[0]) kills the source
        var v = m.UncomputeSafety(Op(r, "Main"), At("a", 1));
        Assert.Equal(UncomputeBlocker.SourceDied, v.Blocker);
        Assert.Equal(At("a", 0), v.Culprit!.Qubit);
    }

    // --- 42. Irreversible marks WRITES only: a qubit that is merely a CONTROL of a measuring helper stays
    //         uncomputable (its own chain replays fine), while a WRITE through an irreversible callee stays
    //         blocked (that write's replay would need the callee's nonexistent adjoint) ---

    [Fact]
    public void ReadThroughAMeasuringCalleeStaysCleanWriteStaysBlocked()
    {
        var (r1, m1) = Compile(
            "operation Peek(Qubit ctl, Qubit[1] res){ CNOT(ctl, res[0]); bit b = M(res[0]); }\n" +
            "operation Main(){ use a=Qubit[1]; use o=Qubit[1]; X(a[0]); Peek(a[0], o); }");
        var main1 = Op(r1, "Main");
        Assert.True(m1.IsSafelyUncomputable(main1, Whole("a")));          // a: control only — clean
        Assert.False(m1.IsCleanupCandidate(main1.Id, Whole("o")));        // o: measured through the call

        var (r2, m2) = Compile(
            "operation Mix(Qubit p, Qubit[1] s){ CNOT(s[0], p); bit r = M(s[0]); }\n" +
            "operation Main(){ use a=Qubit[1]; use k=Qubit[1]; Mix(a[0], k); }");
        Assert.Equal(UncomputeBlocker.Irreversible,
            m2.UncomputeSafety(Op(r2, "Main"), Whole("a")).Blocker);      // a: WRITTEN by the measuring callee
    }

    // --- 43. while/repeat are containers too (pin: deleting either ContainerMap arm must fail a test) ---

    [Fact]
    public void WhileAndRepeatBodiesBlockContainedWrites()
    {
        var (r, m) = Compile(
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; use x=Qubit[1]; bit r = M(x[0]); " +
            "while (r == 0) { X(a[0]); } repeat { X(b[0]); } until (r == 1); }");
        var main = Op(r, "Main");
        var wh = Assert.IsType<QWhile>(main.Body[4]);
        var rp = Assert.IsType<QRepeat>(main.Body[5]);

        var va = m.UncomputeSafety(main, Whole("a"));
        Assert.Equal(UncomputeBlocker.ContainedWrite, va.Blocker);
        Assert.Equal(wh.Body[0].Id, va.Culprit!.StmtId);

        var vb = m.UncomputeSafety(main, Whole("b"));
        Assert.Equal(UncomputeBlocker.ContainedWrite, vb.Blocker);
        Assert.Equal(rp.Body[0].Id, vb.Culprit!.StmtId);
    }

    // --- 44. ReplayReversed pins: a TWO-statement within replays in REVERSED statement order under fresh,
    //         increasing Orders, kinds/flags preserved; the verdict's culprit is the FIRST forward write ---

    [Fact]
    public void ConjugateReplayReversesStatementOrderWithFreshOrders()
    {
        static QGate G(string n, params QArg[] a) => new(new List<string>(), n, a.ToList());
        var w1 = G("X", new QQubitArg("a", "0"));
        var w2 = G("X", new QQubitArg("d", "0"));
        var op = new QOperation("Main", new List<QParam>(), new List<QStmt>
        {
            new QUse("a", 1), new QUse("d", 1), new QUse("o", 1),
            new QConjugate(
                Within: new List<QStmt> { w1, w2 },
                Apply: new List<QStmt> { G("CNOT", new QQubitArg("a", "0"), new QQubitArg("o", "0")) }),
        });
        var m = new SemanticModel();
        EffectAnalysis.Run(new QProgram(new List<QOperation> { op }), m);

        var all = m.QubitEvents(op.Id);
        var applyOrder = all.Single(e => e.Qubit.Equals(At("o", 0))).Order;
        var replay = all.Where(e => e.Order > applyOrder).OrderBy(e => e.Order).ToList();
        Assert.Equal(new[] { w2.Id, w1.Id }, replay.Select(e => e.StmtId));           // reversed statement order
        Assert.True(replay[0].Order < replay[1].Order);                               // fresh, strictly increasing
        Assert.All(replay, e => Assert.Equal(QubitEventKind.Write, e.Kind));          // kinds preserved
        Assert.All(replay, e => Assert.False(e.NonQfree));                // flags preserved

        var v = m.UncomputeSafety(op, Whole("a"));
        Assert.Equal(UncomputeBlocker.ContainedWrite, v.Blocker);
        Assert.Equal(w1.Id, v.Culprit!.StmtId);                                       // first forward offender
    }

    // --- 45. per-element suffix on the MEASURED branch (a different code path from the blocked headline) ---

    [Fact]
    public void MeasuredRegisterStillNamesItsCleanElements()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; X(q[1]); bit b = M(q[0]); }");
        Assert.Contains("q: ancilla, measured — promoted to output, not a cleanup candidate — per element: q[1] safe cleanup candidate",
            UncomputeReport.Format(r.AnalyzedIr, m));
    }

    // --- 46. one call that measures one argument and resets another: the per-qubit Measure/Write +
    //         Irreversible split must hold within a single statement ---

    [Fact]
    public void MeasureAndResetInOneCallSplitPerQubit()
    {
        var (r, m) = Compile(
            "operation Both(Qubit[1] a, Qubit b){ bit r = M(a[0]); Reset(b); }\n" +
            "operation Main(){ use x=Qubit[1]; use y=Qubit[1]; Both(x, y[0]); }");
        var main = Op(r, "Main");
        var evs = EventsOf(m, main, main.Body[2]);
        Assert.Equal(2, evs.Count);

        var xe = evs.Single(e => e.Qubit.Equals(At("x", 0)));
        Assert.Equal(QubitEventKind.Measure, xe.Kind);        // measured — the Kind carries irreversibility
        Assert.False(xe.Irreversible);

        var ye = evs.Single(e => e.Qubit.Equals(At("y", 0)));
        Assert.Equal(QubitEventKind.Write, ye.Kind);          // reset — a Write that must stay flagged
        Assert.True(ye.Irreversible);

        AssertRefs(m.FindOpEffects(Op(r, "Both").Id)!.ParamMeasured, At("a", 0));
    }

    // --- 47. blanket write of q under an ELEMENT query (adversarially found blocker): a broadcast / opaque
    //         call writes SIBLING elements inside ONE event — the statement-level adjoint cannot be sliced to
    //         q, so replaying it would rewrite the others (deterministically flips a measured sibling). The
    //         `use` birth (the only parentless write) stays exempt, so precise per-element chains survive ---

    [Fact]
    public void BlanketWriteOfQUnderElementQueryBlocksBirthStaysExempt()
    {
        // opaque call whose adjoint has the full statement breadth
        var (r1, m1) = Compile(
            "operation Bcast(Qubit[2] p){ for i in 0..1 { X(p[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; Bcast(a); bit c = M(a[0]); }");
        var main1 = Op(r1, "Main");
        var v1 = m1.UncomputeSafety(main1, At("a", 1));
        Assert.Equal(UncomputeBlocker.CoWrittenPartner, v1.Blocker);
        Assert.Equal(main1.Body[1].Id, v1.Culprit!.StmtId);              // the Bcast call itself
        Assert.DoesNotContain("a[1] safe cleanup candidate", UncomputeReport.Format(r1.AnalyzedIr, m1));

        // plain broadcast — same hole, same block
        var (r2, m2) = Compile("operation Main(){ use a=Qubit[2]; X(a); bit c = M(a[0]); }");
        Assert.Equal(UncomputeBlocker.CoWrittenPartner,
            m2.UncomputeSafety(Op(r2, "Main"), At("a", 1)).Blocker);

        // birth exemption: element-precise writes after the (blanket) birth are still per-element safe
        var (r3, m3) = Compile("operation Main(){ use a=Qubit[2]; X(a[1]); bit c = M(a[0]); }");
        Assert.True(m3.IsSafelyUncomputable(Op(r3, "Main"), At("a", 1)));
    }

    // --- 48. contained death in NESTED containers: the window must extend to the OUTERMOST container's end
    //         (an innermost regression would stop at the inner if and miss the kill in the outer body) ---

    [Fact]
    public void ContainedDeathWindowReachesTheOutermostContainerEnd()
    {
        var (r, m) = Compile(
            "operation Main(){ use a=Qubit[1]; use s=Qubit[1]; use o=Qubit[1]; use x=Qubit[1]; " +
            "H(x[0]); bit r = M(x[0]); X(s[0]); CNOT(s[0], a[0]); " +
            "if (r == 1) { if (r == 1) { CNOT(a[0], o[0]); } X(s[0]); } }");
        var v = m.UncomputeSafety(Op(r, "Main"), Whole("a"));
        Assert.Equal(UncomputeBlocker.SourceDied, v.Blocker);            // death in the INNER if,
        Assert.Equal(At("s", 0), v.Culprit!.Qubit);                      // kill in the OUTER body — still seen
    }

    // --- 49. the model's analysis facts are add-only: a second analysis of the same op must fail loud, never
    //         silently replace streams/graphs consumers already read ---

    [Fact]
    public void ReanalysisOfTheSameOpFailsLoud()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; X(q[0]); }");
        Assert.Throws<System.InvalidOperationException>(
            () => EffectAnalysis.Run(r.AnalyzedIr!, m));                 // same ops, same model → QINTERNAL
    }

    // --- 49-0. round-5: the R4-F2 write-before-birth GUARD, pinned directly. Births are hoisted from the body's
    //           TOP LEVEL only; a `use` nested in a container (only constructible via hand-built IR — QSEM012
    //           forbids it from source) records no hoisted birth, so the register's first write mints a
    //           parentless impostor — the guard the birth-exemption's soundness rests on must fail loud ---

    [Fact]
    public void WriteBeforeBirthFailsLoud()
    {
        // QUse("n") nested inside an if; a top-level X(n[0]) then writes n with no hoisted birth and no seed
        var op = new QOperation("Main", new List<QParam>(), new List<QStmt>
        {
            new QIf(new QCond("true"),
                Then: new List<QStmt> { new QUse("n", 1) },
                Else: new List<QStmt>()),
            new QGate(new List<string>(), "X", new List<QArg> { new QQubitArg("n", "0") }),
        });
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => EffectAnalysis.Run(new QProgram(new List<QOperation> { op }), new SemanticModel()));
        Assert.Contains("before any birth", ex.Message);
        Assert.Contains("only a `use` may create", ex.Message);
    }

    // --- 49a. round-5: the gate-before-use (hoisting) VERDICT family, end-to-end. A gate textually preceding
    //          its register's `use` writes onto the hoisted birth, and the verdict reads correctly off it ---

    [Fact]
    public void GateBeforeUseProducesSaneVerdicts()
    {
        // H before the use: q is born |0⟩ (hoisted), the H is a non-qfree write → blocked, live 0..1
        var (r1, m1) = Compile("operation Main(){ H(q[0]); use q=Qubit[1]; }");
        var main1 = Op(r1, "Main");
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, m1.UncomputeSafety(main1, Whole("q")).Blocker);
        Assert.Equal((0, 1), (m1.LiveRange(main1.Id, Whole("q"))!.Value.Birth.Order,
                              m1.LiveRange(main1.Id, Whole("q"))!.Value.Death.Order));

        // X before the use: a phase-free classical write on the birth → safe to uncompute
        var (r2, m2) = Compile("operation Main(){ X(q[0]); use q=Qubit[1]; }");
        Assert.True(m2.IsSafelyUncomputable(Op(r2, "Main"), Whole("q")));

        // a register only declared, never touched: death == birth, no blocker
        var (r3, m3) = Compile("operation Main(){ use q=Qubit[1]; }");
        Assert.True(m3.IsSafelyUncomputable(Op(r3, "Main"), Whole("q")));
    }

    // --- 49b. per-element diagnostic (round-5): when the register headline hides an element blocked for a
    //          DIFFERENT reason, the report names that element WITH its own reason — the only path that renders
    //          an element-level culprit (here a broadcast's statement-wide write under the a[1] query) ---

    [Fact]
    public void ReportRendersPerElementBlockReasonUnderABlanketHeadline()
    {
        var (r, m) = Compile(
            "operation Bcast(Qubit[2] p){ for i in 0..1 { X(p[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; Bcast(a); bit c = M(a[0]); }");
        var report = UncomputeReport.Format(r.AnalyzedIr, m);
        Assert.Contains("a: ancilla, measured — promoted to output, not a cleanup candidate", report);                    // headline: a[0] measured
        Assert.Contains("a[1] blocked: statement-wide write of `a`", report);        // element reason rendered
    }

    // --- 49c. rung ORDER in the verdict: measurement is rung 1's ruling (cleanup candidacy), relayed by
    //          UncomputeSafety BEFORE any safety clause runs — never re-judged in the scan. A non-ancilla gets
    //          NotACleanupCandidate; a measured ancilla gets Measured (culprit = the measuring event) even when
    //          an earlier event would trip a safety clause ---

    [Fact]
    public void VerdictRelaysRungOneRulingsBeforeAnySafetyClause()
    {
        // a parameter is not an ancilla — the safety question does not apply to it
        var (r1, m1) = Compile("operation Foo(Qubit p){ X(p); }\noperation Main(){ use q=Qubit[1]; Foo(q[0]); }");
        Assert.Equal(UncomputeBlocker.NotACleanupCandidate,
            m1.UncomputeSafety(Op(r1, "Foo"), Whole("p")).Blocker);

        // H (a non-qfree write, order 1) happens BEFORE the measurement (order 2) — the verdict still says
        // Measured: candidacy is ruled first, the scan never runs for a promoted output
        var (r2, m2) = Compile("operation Main(){ use a=Qubit[1]; H(a[0]); bit c = M(a[0]); }");
        var main2 = Op(r2, "Main");
        var v = m2.UncomputeSafety(main2, Whole("a"));
        Assert.Equal(UncomputeBlocker.Measured, v.Blocker);
        Assert.Equal(QubitEventKind.Measure, v.Culprit!.Kind);        // culprit = the measuring event itself
    }

    // --- 50. the exact round-5 fuzzer repro programs: a Y (and CY) write onto an ENTANGLED ancilla, which the
    //         old verdict certified SAFE while the contract injection flips a downstream measurement. Both must
    //         be blocked as NonQfreeWrite. These are the state-vector-verified counterexamples, pinned. ---

    [Fact]
    public void EntangledPhasePermutationAncillaIsBlocked()
    {
        // H(b);CNOT(b,a);Y(a);H(b);M(b): a is entangled with b at the Y; injecting Y†;CNOT strips a
        // survivor-relative phase, turning a 50/50 m into a deterministic flip — a is NOT safe
        var (r1, m1) = Compile(
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; H(b[0]); CNOT(b[0], a[0]); Y(a[0]); H(b[0]); bit m = M(b[0]); }");
        var main1 = Op(r1, "Main");
        Assert.False(m1.IsSafelyUncomputable(main1, Whole("a")));
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, m1.UncomputeSafety(main1, Whole("a")).Blocker);

        // CY variant — same hole through the controlled form
        var (r2, m2) = Compile(
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; H(b[0]); CY(b[0], a[0]); H(b[0]); bit m = M(b[0]); }");
        var main2 = Op(r2, "Main");
        Assert.False(m2.IsSafelyUncomputable(main2, Whole("a")));
        Assert.Equal(UncomputeBlocker.NonQfreeWrite, m2.UncomputeSafety(main2, Whole("a")).Blocker);
    }

    private static IEnumerable<QStmt> Flatten(QStmt stmt)
    {
        yield return stmt;
        var children = stmt switch
        {
            QIf i => i.Then.Concat(i.Else),
            QFor f => f.Body,
            QWhile w => w.Body,
            QRepeat rp => rp.Body,
            QConjugate c => c.Within.Concat(c.Apply),
            _ => Enumerable.Empty<QStmt>(),
        };
        foreach (var child in children.SelectMany(Flatten)) yield return child;
    }
}
