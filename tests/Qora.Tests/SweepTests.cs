using System.Collections.Generic;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// NEGATIVE tests for the always-on graph↔event coherence sweep — adversarially demanded: without a seam,
/// neutering every Fail path kept the whole suite green (the safety net itself was unfalsifiable). These
/// feed hand-corrupted (events, graph) pairs to the SAME verifier the pipeline runs
/// (<see cref="EffectAnalysis.VerifySweep"/>, InternalsVisibleTo) and pin that each clause actually fires.
/// The model's add-only guards are pinned here too.
/// </summary>
public class SweepTests
{
    private static QubitRef At(string reg, int i) => new(reg, i);
    private static QubitRef Whole(string reg) => new(reg, null);

    private static QubitEvent Ev(QubitRef q, QubitEventKind kind, int order, int stmtId, int nodeId) =>
        new(q, kind, order, stmtId, false, false, nodeId);

    /// <summary>The minimal VALID pair: `use q` birth (hoisted) then X(q[0]).</summary>
    private static (List<QubitEvent> Events, QubitGraph Graph) ValidPair()
    {
        var g = new QubitGraph();
        g.AddNode(Whole("q"), System.Array.Empty<QubitEdge>());                       // 0: birth
        g.AddNode(At("q", 0), new[] { new QubitEdge(0, At("q", 0)) });                // 1: X(q[0])
        var events = new List<QubitEvent>
        {
            Ev(Whole("q"), QubitEventKind.Write, 0, 1, 0),
            Ev(At("q", 0), QubitEventKind.Write, 1, 2, 1),
        };
        return (events, g);
    }

    private static void Sweep(List<QubitEvent> events, QubitGraph graph, params string[] paramRegs) =>
        EffectAnalysis.VerifySweep("T", paramRegs, events, graph);

    [Fact]
    public void ValidPairPasses()
    {
        var (events, g) = ValidPair();
        Sweep(events, g);   // no throw
    }

    [Fact]
    public void WrongReadLinkFailsLoud()
    {
        var (events, g) = ValidPair();
        events.Add(Ev(At("q", 0), QubitEventKind.Read, 2, 3, 0));   // then-current is node 1, not 0
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("re-derivation says 1", ex.Message);
    }

    [Fact]
    public void DroppedParentEdgeFailsLoud()
    {
        var g = new QubitGraph();
        g.AddNode(Whole("q"), System.Array.Empty<QubitEdge>());
        g.AddNode(At("q", 0), System.Array.Empty<QubitEdge>());     // ← own-prev edge dropped
        var events = new List<QubitEvent>
        {
            Ev(Whole("q"), QubitEventKind.Write, 0, 1, 0),
            Ev(At("q", 0), QubitEventKind.Write, 1, 2, 1),
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("parents", ex.Message);
    }

    [Fact]
    public void WriteLinkedToAParamSeedFailsLoud()
    {
        var g = new QubitGraph();
        g.AddSeed("p");                                             // 0: the parameter's from-outside value
        var events = new List<QubitEvent> { Ev(At("p", 0), QubitEventKind.Write, 0, 1, 0) };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g, "p"));
        Assert.Contains("seed", ex.Message);
    }

    [Fact]
    public void OrphanNodeFailsLoud()
    {
        var (events, g) = ValidPair();
        g.AddNode(At("q", 1), System.Array.Empty<QubitEdge>());     // node no event ever created
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("no creating event", ex.Message);
    }

    [Fact]
    public void OutOfOrderStreamFailsLoud()
    {
        // two independent births whose Orders run backwards — every other clause is coherent, so only the
        // program-order invariant can (and must) fire
        var g = new QubitGraph();
        g.AddNode(Whole("q"), System.Array.Empty<QubitEdge>());
        g.AddNode(Whole("r"), System.Array.Empty<QubitEdge>());
        var events = new List<QubitEvent>
        {
            Ev(Whole("q"), QubitEventKind.Write, 2, 1, 0),
            Ev(Whole("r"), QubitEventKind.Write, 1, 2, 1),          // ← earlier Order after a later one
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("not program-ordered", ex.Message);
    }

    [Fact]
    public void SeedParamMismatchFailsLoud()
    {
        var (events, g) = ValidPair();                              // graph has no seed for `p`
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g, "p"));
        Assert.Contains("seed registers", ex.Message);
    }

    // --- round-5: one hand-corrupted pair per remaining FALSIFIABLE Fail clause, so every clause reachable
    //     through the public graph API is pinned (adversarially demanded: 10 clauses were still unfalsifiable) ---

    [Fact]
    public void EventPointsAtMissingNodeFailsLoud()
    {
        var (events, g) = ValidPair();
        events.Add(Ev(At("q", 0), QubitEventKind.Read, 2, 3, 99));    // NodeId out of range
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("missing node", ex.Message);
    }

    [Fact]
    public void ReadLinkedToAnUnrelatedRegisterFailsLoud()
    {
        var g = new QubitGraph();
        g.AddNode(Whole("q"), System.Array.Empty<QubitEdge>());       // 0
        g.AddNode(Whole("r"), System.Array.Empty<QubitEdge>());       // 1 — a different register
        var events = new List<QubitEvent>
        {
            Ev(Whole("q"), QubitEventKind.Write, 0, 1, 0),
            Ev(Whole("r"), QubitEventKind.Write, 1, 2, 1),
            Ev(Whole("q"), QubitEventKind.Read, 2, 3, 1),            // read of q linked to r's node
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("unrelated node", ex.Message);
    }

    [Fact]
    public void WriteLinkedToAnotherQubitsNodeFailsLoud()
    {
        var g = new QubitGraph();
        g.AddNode(Whole("q"), System.Array.Empty<QubitEdge>());       // 0
        g.AddNode(At("q", 0), new[] { new QubitEdge(0, At("q", 0)) }); // 1 — a node OF q[0]
        var events = new List<QubitEvent>
        {
            Ev(Whole("q"), QubitEventKind.Write, 0, 1, 0),
            Ev(At("q", 1), QubitEventKind.Write, 1, 2, 1),           // write of q[1] linked to q[0]'s node
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("linked to a node of", ex.Message);
    }

    [Fact]
    public void TwoEventsCreatingOneNodeFailsLoud()
    {
        var (events, g) = ValidPair();
        events.Add(Ev(At("q", 0), QubitEventKind.Write, 2, 3, 1));    // node 1 already created at order 1
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("two creating events", ex.Message);
    }

    [Fact]
    public void DuplicateParameterSeedsFailLoud()
    {
        var g = new QubitGraph();
        g.AddSeed("p");                                              // 0
        g.AddSeed("p");                                             // 1 — a second seed for the same register
        var events = new List<QubitEvent> { Ev(At("p", 0), QubitEventKind.Read, 0, 1, 0) };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g, "p"));
        Assert.Contains("duplicate parameter seed", ex.Message);
    }

    [Fact]
    public void ParentEdgeOutOfRangeFailsLoud()
    {
        var g = new QubitGraph();
        g.AddNode(Whole("q"), System.Array.Empty<QubitEdge>());                       // 0
        g.AddNode(At("q", 0), new[] { new QubitEdge(99, At("q", 0)) });               // 1 — parent 99 missing
        var events = new List<QubitEvent>
        {
            Ev(Whole("q"), QubitEventKind.Write, 0, 1, 0),
            Ev(At("q", 0), QubitEventKind.Write, 1, 2, 1),
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("missing parent", ex.Message);
    }

    [Fact]
    public void ParentNotOlderThanChildFailsLoud()
    {
        // node 1's creating event is at a LATER order than node 0's, yet node 0 lists node 1 as a parent —
        // the child would then be older than its parent
        var g = new QubitGraph();
        g.AddNode(At("q", 0), new[] { new QubitEdge(1, At("q", 0)) }); // 0 — parent is node 1
        g.AddNode(At("q", 0), System.Array.Empty<QubitEdge>());       // 1
        var events = new List<QubitEvent>
        {
            Ev(At("q", 0), QubitEventKind.Write, 0, 1, 0),          // node 0 created at order 0
            Ev(At("q", 0), QubitEventKind.Write, 1, 2, 1),          // node 1 created at order 1 (later)
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => Sweep(events, g));
        Assert.Contains("not older than itself", ex.Message);
    }

    // NOTE — three Fail clauses (a seed that does not resolve via ParamSeed; a malformed seed with parents or a
    // non-null index; a per-register Version out of sequence) are CONSTRUCTOR BACKSTOPS: QubitGraph.AddSeed /
    // AddNode assign the seed shape, ParamSeed mapping, and Version counter themselves, so no input reachable
    // through the public graph API can violate them. They guard a future edit to AddNodeCore, not a caller —
    // the positive construction tests (QubitGraphTests) exercise the assignment they protect.

    // --- the model's add-only guards: a second analysis result for the same op must fail loud, per store ---

    [Fact]
    public void ModelStoresAreAddOnlyPerStore()
    {
        var m = new SemanticModel();
        var events = new List<QubitEvent> { Ev(Whole("q"), QubitEventKind.Write, 0, 1, 0) };
        var g = new QubitGraph();
        var summary = new OpEffectSummary(
            new HashSet<QubitRef>(), new HashSet<QubitRef>(), new HashSet<QubitRef>(), new HashSet<QubitRef>(), false);

        m.AddQubitEvents(7, events);
        Assert.Contains("already has an event stream",
            Assert.Throws<System.InvalidOperationException>(() => m.AddQubitEvents(7, events)).Message);

        m.AddQubitGraph(7, g);
        Assert.Contains("already has a qubit graph",
            Assert.Throws<System.InvalidOperationException>(() => m.AddQubitGraph(7, g)).Message);

        m.AddOpEffects(7, summary);
        Assert.Contains("already has an effect summary",
            Assert.Throws<System.InvalidOperationException>(() => m.AddOpEffects(7, summary)).Message);
    }
}
