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
