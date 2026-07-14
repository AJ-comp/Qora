using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// <see cref="StmtMap"/> — the statement-Id → statement lookup the uncompute injector uses to turn a write
/// event's <see cref="QubitEvent.StmtId"/> back into the exact gate node it must invert. Sibling to
/// <see cref="ContainerMap"/> and built from the same one walk, so the key property is REACH: every statement
/// at every depth (nested in if/for/while/repeat/within-apply, and the containers themselves) resolves back to
/// its OWN node, and an Id that is not of this operation is absent.
/// </summary>
public class StmtMapTests
{
    private static QOperation CompileMain(string src)
    {
        var r = QoraParser.Parse(src);
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        return r.Ir!.Operations.Single(o => o.Name == "Main");
    }

    // --- 1. every statement — top level, nested, and the containers themselves — resolves to its own node ---

    [Fact]
    public void EveryStatementResolvesToItsOwnNode()
    {
        var main = CompileMain(
            "operation Main(){ use a=Qubit[2]; bit c = M(a[0]); X(a[1]); " +
            "if (c == 1) { Y(a[1]); } for i in 0..1 { Z(a[1]); } }");
        var map = StmtMap.Build(main);

        var body = main.Body;                          // [use, bit=M, X, if, for]
        var iff = Assert.IsType<QIf>(body[3]);
        var loop = Assert.IsType<QFor>(body[4]);

        Assert.Same(body[0], map[body[0].Id]);         // use — top level
        Assert.Same(body[2], map[body[2].Id]);         // X   — top level
        Assert.Same(iff, map[iff.Id]);                 // the if ITSELF is in the map, as itself
        Assert.Same(iff.Then[0], map[iff.Then[0].Id]); // Y — nested inside the if, still reachable
        Assert.Same(loop, map[loop.Id]);               // the for itself
        Assert.Same(loop.Body[0], map[loop.Body[0].Id]); // Z — nested inside the for
    }

    // --- 2. the injector's real use: a write event carries a StmtId; look it up and get the very gate back ---

    [Fact]
    public void LookupByStmtIdReturnsTheGateToInvert()
    {
        var main = CompileMain("operation Main(){ use a=Qubit[2]; CNOT(a[0], a[1]); }");
        var map = StmtMap.Build(main);

        var cnot = Assert.IsType<QGate>(main.Body[1]);
        var found = Assert.IsType<QGate>(map[cnot.Id]);
        Assert.Same(cnot, found);
        Assert.Equal("CNOT", found.Name);
        Assert.Equal(2, found.Args.Count);
    }

    // --- 3. within/apply children are reachable too (hand-built IR — no surface syntax) ---

    [Fact]
    public void ConjugateChildrenAreReachable()
    {
        var w = new QGate(new List<string>(), "X", new List<QArg> { new QQubitArg("a", "0") });
        var ap = new QGate(new List<string>(), "CNOT", new List<QArg> { new QQubitArg("a", "0"), new QQubitArg("d", "0") });
        var conj = new QConjugate(Within: new List<QStmt> { w }, Apply: new List<QStmt> { ap });
        var op = new QOperation("Main", new List<QParam>(), new List<QStmt> { new QUse("a", 1), new QUse("d", 1), conj });

        var map = StmtMap.Build(op);
        Assert.Same(conj, map[conj.Id]);
        Assert.Same(w, map[w.Id]);
        Assert.Same(ap, map[ap.Id]);
    }

    // --- 4. an Id that is not a statement of this op is ABSENT (no false hit) ---

    [Fact]
    public void UnknownStatementIdIsAbsent()
    {
        var main = CompileMain("operation Main(){ use a=Qubit[1]; X(a[0]); }");
        var map = StmtMap.Build(main);
        Assert.False(map.ContainsKey(-1));
    }
}
