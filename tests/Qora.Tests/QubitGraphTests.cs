using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// The qubit graph — the value-genealogy DAG <see cref="EffectAnalysis"/> records WITH the event stream
/// (same hand, same pass). Nodes = value versions; parent edges = what each value was made from, carrying
/// the ACCESS ref (<see cref="QubitEdge.Via"/>) so blanketed reads keep their conservative breadth. Events
/// link in via <see cref="QubitEvent.NodeId"/>. A pipeline sweep re-derives every link on every compile and
/// fails QINTERNAL-loud on mismatch — these tests pin the recorded shape itself.
/// </summary>
public class QubitGraphTests
{
    private static (QoraParseResult R, SemanticModel M) Compile(string src)
    {
        var r = QoraParser.Parse(src);
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        return (r, r.Semantics!);
    }

    private static QOperation Op(QoraParseResult r, string name) => r.Ir!.Operations.Single(o => o.Name == name);
    private static QubitRef At(string reg, int i) => new(reg, i);
    private static QubitRef Whole(string reg) => new(reg, null);

    // --- 1. the worked example: births, versions, parents (own previous + read source), and the Read
    //        event's link to the then-current source version ---

    [Fact]
    public void NodesVersionsAndParentsAreRecordedAsTheAnalyzerSawThem()
    {
        var (r, m) = Compile("operation Main(){ use a=Qubit[1]; use s=Qubit[1]; X(s[0]); CNOT(s[0], a[0]); X(s[0]); }");
        var main = Op(r, "Main");
        var g = m.Graph(main.Id)!;
        var events = m.QubitEvents(main.Id);

        Assert.Equal(5, g.Nodes.Count);   // a birth, s birth, s[0] v1, a[0] v1, s[0] v2 — no seeds (no params)

        // the CNOT: its Read links to s's THEN-current version (v1, made by the first X)
        var cnotRead = events.Single(e => e.Kind == QubitEventKind.Read);
        var sV1 = g.Node(cnotRead.NodeId);
        Assert.Equal(At("s", 0), sV1.Qubit);
        Assert.Equal(1, sV1.Version);

        // the CNOT's write node: a[0] v1, parents = a's own previous (the birth) + the read source s[0] v1
        var cnotWrite = events.Single(e => e.Kind == QubitEventKind.Write && e.StmtId == cnotRead.StmtId);
        var aV1 = g.Node(cnotWrite.NodeId);
        Assert.Equal(At("a", 0), aV1.Qubit);
        Assert.Equal(2, aV1.Parents.Count);
        var births = events.Where(e => e.Order is 0 or 1).ToList();
        var aBirth = births.Single(e => e.Qubit.Reg == "a").NodeId;
        Assert.Contains(aV1.Parents, p => p.NodeId == aBirth && p.Via == At("a", 0));          // own previous
        Assert.Contains(aV1.Parents, p => p.NodeId == sV1.Id && p.Via == At("s", 0));          // read source

        // the final X(s[0]) chains s's versions: v2's parent is v1
        var sV2 = g.Node(events.Last().NodeId);
        Assert.Equal(2, sV2.Version);
        Assert.Contains(sV2.Parents, p => p.NodeId == sV1.Id);
    }

    // --- 2. a qubit parameter's first value comes from outside: a param SEED node, parent of its first write ---

    [Fact]
    public void ParameterValuesStartAtASeedNode()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit p){ X(p); }\n" +
            "operation Main(){ use q=Qubit[1]; Foo(q[0]); }");
        var foo = Op(r, "Foo");
        var g = m.Graph(foo.Id)!;

        var seedId = g.ParamSeed("p");
        Assert.NotNull(seedId);
        Assert.True(g.Node(seedId!.Value).IsParamSeed);
        Assert.Empty(g.Node(seedId.Value).Parents);                       // from outside — no recorded origin

        var write = m.QubitEvents(foo.Id).Single(e => e.Kind == QubitEventKind.Write);
        Assert.Contains(g.Node(write.NodeId).Parents, p => p.NodeId == seedId.Value);   // seeded own-previous
    }

    // --- 3. a conjugation's W† replay makes FRESH nodes with freshly-resolved parents — never stale copies ---

    [Fact]
    public void ConjugateReplayMakesFreshNodesChainedToTheForwardOnes()
    {
        static QGate G(string n, params QArg[] a) => new(new List<string>(), n, a.ToList());
        var w = G("X", new QQubitArg("a", "0"));
        var op = new QOperation("Main", new List<QParam>(), new List<QStmt>
        {
            new QUse("a", 1), new QUse("d", 1),
            new QConjugate(
                Within: new List<QStmt> { w },
                Apply: new List<QStmt> { G("CNOT", new QQubitArg("a", "0"), new QQubitArg("d", "0")) }),
        });
        var m = new SemanticModel();
        EffectAnalysis.Run(new QProgram(new List<QOperation> { op }), m);
        var g = m.Graph(op.Id)!;
        var events = m.QubitEvents(op.Id);

        var xWrites = events.Where(e => e.StmtId == w.Id && e.Kind == QubitEventKind.Write)
            .OrderBy(e => e.Order).ToList();
        Assert.Equal(2, xWrites.Count);                                    // forward + replayed W†
        Assert.NotEqual(xWrites[0].NodeId, xWrites[1].NodeId);             // fresh node, not a stale copy
        Assert.Contains(g.Node(xWrites[1].NodeId).Parents, p => p.NodeId == xWrites[0].NodeId);   // chained
    }

    // --- 4a. same-Order double writes of ONE register (SWAP, a two-param-writing call) followed by a
    //         blanket ref: construction and sweep must agree on the tie (adversarially found: the sweep's
    //         Order-keyed tie-break crashed these VALID programs with QINTERNAL) ---

    [Fact]
    public void SameStatementDoubleWriteThenBlanketRefCompiles()
    {
        var (r1, _) = Compile("operation Main(){ use q=Qubit[2]; SWAP(q[0], q[1]); X(q); }");
        Assert.True(r1.Success);

        var (r2, _) = Compile(
            "operation Two(Qubit a, Qubit b){ X(a); X(b); }\n" +
            "operation Main(){ use q=Qubit[2]; Two(q[0], q[1]); X(q); }");
        Assert.True(r2.Success);

        var (r3, _) = Compile("operation Main(){ use q=Qubit[2]; SWAP(q[0], q[1]); for i in 0..1 { X(q[i]); } }");
        Assert.True(r3.Success);
    }

    // --- 4b. register declarations are HOISTED (like the emitter's declaration hoisting): a gate may
    //         textually precede its register's `use` and still write onto the hoisted birth ---

    [Fact]
    public void GateBeforeItsUseWritesOntoTheHoistedBirth()
    {
        var (r, m) = Compile("operation Main(){ X(q[0]); use q=Qubit[1]; }");
        Assert.True(r.Success);
        var main = Op(r, "Main");
        var g = m.Graph(main.Id)!;
        var events = m.QubitEvents(main.Id);

        var birth = events.Single(e => e.Qubit == Whole("q"));       // hoisted to order 0
        Assert.Equal(0, birth.Order);
        Assert.Equal(main.Body[1].Id, birth.StmtId);                 // still names its `use` statement

        var x = events.Single(e => e.Qubit.Equals(At("q", 0)));
        Assert.Contains(g.Node(x.NodeId).Parents, p => p.NodeId == birth.NodeId);   // X writes on the birth
    }

    // --- 4. a blanketed read keeps its breadth on the EDGE: the parent may resolve to a precise element's
    //        version, but Via records the whole-register access the analysis actually saw ---

    [Fact]
    public void BlanketReadBreadthLivesOnTheEdgeVia()
    {
        var (r, m) = Compile(
            "operation Fold(Qubit[] p){ for i in 0..0 { CNOT(p[i], p[1]); } }\n" +
            "operation Main(){ use a=Qubit[2]; X(a[0]); Fold(a); }");
        var main = Op(r, "Main");
        var g = m.Graph(main.Id)!;

        var foldWrite = m.QubitEvents(main.Id).Single(e => e.Qubit.Equals(At("a", 1)) && e.Kind == QubitEventKind.Write);
        // the read was blanketed to whole-a; it RESOLVES to a[0]'s version (the X), but Via stays whole-a
        Assert.Contains(g.Node(foldWrite.NodeId).Parents, p => p.Via == Whole("a"));
    }
}
