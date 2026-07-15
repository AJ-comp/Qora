using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// <see cref="UncomputePlanner"/> (rung ④, step 3) — turns one safe ancilla into its cleanup statement list
/// without touching the IR. These pin the four things the plan must get right: the write list is the value
/// WRITES only (birth |0…0⟩ and read-only controls excluded), inverted in REVERSE order, anchored at the
/// rung-② death point; and an unsafe ancilla yields NO plan. Safe/unsafe programs are the same ones
/// EffectAnalysisTests already certifies, so the planner is tested against ground truth, not a fresh claim.
/// </summary>
public class UncomputePlannerTests
{
    private static (QOperation Op, SemanticModel M, Inverter Inv) Compile(string src)
    {
        var r = QoraParser.Parse(src);
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        Assert.NotNull(r.Semantics);
        var op = r.Ir!.Operations.Single(o => o.Name == "Main");
        return (op, r.Semantics!, new Inverter(r.Ir!.Operations));
    }

    private static QubitRef Whole(string reg) => new(reg, null);

    private static (string Name, bool Adjoint) Gate(QStmt s)
    {
        var g = Assert.IsType<QGate>(s);
        return (g.Name, g.Functors.Count == 1 && g.Functors[0] == "Adjoint");
    }

    // --- 1. the canonical safe slice (EffectAnalysisTests §25's safe case): X computes a, CNOT uses it as a
    //        control. Cleanup = the single Adjoint X; the birth is NOT in it; death = a's last use (the CNOT). ---

    [Fact]
    public void SingleWriteAncillaPlansOneAdjointAtItsDeath()
    {
        var (op, m, inv) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; X(a[0]); CNOT(a[0], b[0]); }");
        var plan = UncomputePlanner.Plan(m, inv, op, Whole("a"));

        Assert.NotNull(plan);
        var g = Assert.Single(plan!.Cleanup);          // ONE statement — the birth |0⟩ Write is excluded
        Assert.Equal(("X", true), Gate(g));            // Adjoint X (inv @ x)

        var cnot = Assert.IsType<QGate>(op.Body[3]);   // use a; use b; X; CNOT
        Assert.Equal(cnot.Id, plan.Death.StmtId);      // death anchored at a's last use
        Assert.Equal(QubitEventKind.Read, plan.Death.Kind);  // a is the CONTROL there — a read
    }

    // --- 2. reverse order (socks-and-shoes): two writes to a, undone last-first. Safety asserted first so the
    //        example is ground truth, then the cleanup order. ---

    [Fact]
    public void TwoWritesAreInvertedInReverseOrder()
    {
        var (op, m, inv) = Compile(
            "operation Main(){ use a=Qubit[1]; use c=Qubit[1]; use b=Qubit[1]; " +
            "X(a[0]); CNOT(c[0], a[0]); CNOT(a[0], b[0]); }");
        Assert.True(m.IsSafelyUncomputable(op, Whole("a")));   // ground truth before we trust the plan

        var plan = UncomputePlanner.Plan(m, inv, op, Whole("a"));
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Cleanup.Count);
        Assert.Equal(("CNOT", true), Gate(plan.Cleanup[0]));   // last write undone first
        Assert.Equal(("X", true), Gate(plan.Cleanup[1]));      // first write undone last
    }

    // --- 3. an ancilla only ever read (a control, never value-written) is already |0⟩ — safe, empty cleanup ---

    [Fact]
    public void NeverWrittenAncillaPlansEmptyCleanup()
    {
        var (op, m, inv) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; X(b[0]); CNOT(a[0], b[0]); }");
        var plan = UncomputePlanner.Plan(m, inv, op, Whole("a"));

        Assert.NotNull(plan);
        Assert.Empty(plan!.Cleanup);                    // nothing to undo — a stayed |0⟩
        var cnot = Assert.IsType<QGate>(op.Body[3]);
        Assert.Equal(cnot.Id, plan.Death.StmtId);       // still anchored at a's last use
    }

    // --- 4. unsafe ancillas yield NO plan (same rejects EffectAnalysisTests §25 certifies) ---

    [Theory]
    [InlineData("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Y(a[0]); CNOT(a[0], b[0]); }")] // phase-permutation write
    [InlineData("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; H(a[0]); CNOT(a[0], b[0]); }")] // superposition write
    [InlineData("operation Main(){ use a=Qubit[1]; X(a[0]); bit c = M(a[0]); }")]                   // measured → output
    public void UnsafeAncillaHasNoPlan(string src)
    {
        var (op, m, inv) = Compile(src);
        Assert.Null(UncomputePlanner.Plan(m, inv, op, Whole("a")));
    }

    // --- 5. the birth is excluded by IDENTITY, not by count: a same-gate double-write (X;X) still yields two
    //        adjoints and never the |0⟩ birth (which is a Write event too) ---

    [Fact]
    public void BirthWriteIsNeverInTheCleanup()
    {
        var (op, m, inv) = Compile("operation Main(){ use a=Qubit[1]; use b=Qubit[1]; X(a[0]); X(a[0]); CNOT(a[0], b[0]); }");
        var plan = UncomputePlanner.Plan(m, inv, op, Whole("a"));

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Cleanup.Count);           // two X writes → two adjoints; birth Write not counted
        Assert.All(plan.Cleanup, s => Assert.Equal("X", Assert.IsType<QGate>(s).Name));
    }

    // --- 6. ONE statement, ONE adjoint — a gate writing TWO elements of q (SWAP) emits two write-events at the
    //        same StmtId; the whole-statement adjoint cleans both, so it must appear ONCE, not per-element
    //        (regression: double-count left the ancilla dirty; adversarially found). ---

    [Fact]
    public void MultiElementWriteIsInvertedOncePerStatement()
    {
        var (op, m, inv) = Compile("operation Main(){ use a=Qubit[2]; X(a[0]); SWAP(a[0], a[1]); }");
        Assert.True(m.IsSafelyUncomputable(op, Whole("a")));

        var plan = UncomputePlanner.Plan(m, inv, op, Whole("a"));
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Cleanup.Count);                  // [Adjoint SWAP, Adjoint X] — NOT [SWAP, SWAP, X]
        Assert.Equal(("SWAP", true), Gate(plan.Cleanup[0]));   // last write (SWAP) undone first, once
        Assert.Equal(("X", true), Gate(plan.Cleanup[1]));
    }

    // --- 7. a write that is a call the Inverter CANNOT invert (while/repeat of unknown count, or classical
    //        mutation in the callee) is NOT certified safe → rung ③ names NotInvertibleCall and Plan returns
    //        null. FLEXIBLE+DIAGNOSTIC: the program still COMPILES (Compile asserts success) — the ancilla is
    //        just not auto-cleaned, never a crash (the QINTERNAL guard is now genuinely unreachable). ---

    [Theory]
    [InlineData("operation Loop(Qubit p){ repeat { X(p); } until (1 == 1); }\n" +
                "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Loop(a[0]); CNOT(a[0], b[0]); }")]  // unknown-count loop
    [InlineData("operation Mut(Qubit p){ int c = 0; c = c + 1; X(p); }\n" +
                "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Mut(a[0]); CNOT(a[0], b[0]); }")]    // classical mutation
    public void NonInvertibleCallWriteIsNotSafeAndPlanIsNull(string src)
    {
        var (op, m, inv) = Compile(src);                                        // still compiles — forward code is fine
        Assert.False(m.IsSafelyUncomputable(op, Whole("a")));
        Assert.Equal(UncomputeBlocker.NotInvertibleCall, m.UncomputeSafety(op, Whole("a")).Blocker);
        Assert.Null(UncomputePlanner.Plan(m, inv, op, Whole("a")));             // no crash, no plan
    }

    // --- 8. NOT over-broad: a qfree helper the Inverter CAN invert stays safe and plans its adjoint ---

    [Fact]
    public void InvertibleHelperCallStillPlansItsAdjoint()
    {
        var (op, m, inv) = Compile(
            "operation Flip(Qubit p){ X(p); }\n" +
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Flip(a[0]); CNOT(a[0], b[0]); }");
        var plan = UncomputePlanner.Plan(m, inv, op, Whole("a"));
        Assert.NotNull(plan);
        Assert.Equal(("Flip", true), Gate(Assert.Single(plan!.Cleanup)));       // Adjoint Flip — clean helper still cleans
    }

    // --- 9. REGRESSION (adversarially found): a GENERIC non-invertible callee is monomorphized to a DIFFERENT name
    //        (Loop → Loop__sz2), so keying the block by op NAME missed it whenever rung ③ is handed the PRE-MONO
    //        tree — which Compile does (op from r.Ir) — wrongly returning "safe" and crashing Plan. Keying the block
    //        by the call statement's StmtId (preserved across mono, unlike the name) blocks it on either tree. ---

    [Fact]
    public void GenericNonInvertibleCalleeIsBlockedOnThePreMonoTree()
    {
        var (op, m, inv) = Compile(
            "operation Loop(Qubit[] p){ repeat { X(p[0]); } until (1 == 1); }\n" +
            "operation Main(){ use a=Qubit[2]; use b=Qubit[1]; Loop(a); CNOT(a[0], b[0]); }");
        // op is the PRE-MONO Main (r.Ir): its call gate is still named "Loop", not the mono "Loop__sz2"
        Assert.False(m.IsSafelyUncomputable(op, Whole("a")));
        Assert.Equal(UncomputeBlocker.NotInvertibleCall, m.UncomputeSafety(op, Whole("a")).Blocker);
        Assert.Null(UncomputePlanner.Plan(m, inv, op, Whole("a")));             // no crash on the pre-mono tree
    }
}
