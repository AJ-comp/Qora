using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// <see cref="ContainerMap"/> — the statement→enclosing-container lookup the if-uncompute tools need. The
/// event stream is a flat TIMELINE (containers emit no events; list position says nothing about nesting —
/// the zigzag test pins that), so nesting is answered from the IR tree via this one-walk map.
/// </summary>
public class ContainerMapTests
{
    private static HirCallable CompileMain(string src)
    {
        var r = QoraCompiler.Compile(src);
        Assert.True(r.Succeeded, string.Join(" | ", r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList()));
        return r.Hir.Resolved!.Program.Callables.Single(o => o.Name == "Main");
    }

    // --- 1. nesting chains: top-level = empty; in-if = [if]; nested = [outer, inner] outermost first;
    //        a container's own chain excludes itself; else-branch and for-body are enclosed too ---

    [Fact]
    public void ChainsAreOutermostFirstAndContainersExcludeThemselves()
    {
        var main = CompileMain(
            "operation Main(){ use a=Qubit[2]; var c: bit = M(a[0]); X(a[1]); " +
            "if (c == 1) { X(a[1]); if (c == 0) { H(a[1]); } else { Z(a[1]); } } " +
            "for i in 0..1 { Y(a[1]); } Z(a[1]); }");
        var map = ContainerMap.Build(main);

        var body = main.Body;                                   // [use, bit=M, X, if, for, Z]
        var outerIf = Assert.IsType<HirIfStatement>(body[3]);
        var innerIf = Assert.IsType<HirIfStatement>(outerIf.Then[1]);
        var loop = Assert.IsType<HirForStatement>(body[4]);

        Assert.Empty(map[body[0].Id]);                          // use a — top level
        Assert.Empty(map[body[2].Id]);                          // X — top level
        Assert.Empty(map[outerIf.Id]);                          // the if ITSELF is top level (chain excludes itself)

        Assert.Equal(new[] { outerIf.Id }, map[outerIf.Then[0].Id].Select(s => s.Id));           // X in if
        Assert.Equal(new[] { outerIf.Id }, map[innerIf.Id].Select(s => s.Id));                   // inner if sits in outer
        Assert.Equal(new[] { outerIf.Id, innerIf.Id }, map[innerIf.Then[0].Id].Select(s => s.Id)); // H — outermost first
        Assert.Equal(new[] { outerIf.Id, innerIf.Id }, map[innerIf.Else[0].Id].Select(s => s.Id)); // Z in else — same chain

        Assert.Equal(new[] { loop.Id }, map[loop.Body[0].Id].Select(s => s.Id));                 // Y in for
        Assert.Empty(map[body[5].Id]);                          // trailing Z — back to top level
    }

    // --- 2. the zigzag counterexample: event/list ORDER says nothing about nesting — the map does.
    //        Depth runs 0 → 1 → 0 across three consecutive statements ---

    [Fact]
    public void ZigzagDepthIsRecordedCorrectly()
    {
        var main = CompileMain(
            "operation Main(){ use a=Qubit[2]; var c: bit = M(a[0]); X(a[1]); if (c == 1) { Y(a[1]); } Z(a[1]); }");
        var map = ContainerMap.Build(main);

        var iff = Assert.IsType<HirIfStatement>(main.Body[3]);
        Assert.Empty(map[main.Body[2].Id]);                     // X  — depth 0
        Assert.Single(map[iff.Then[0].Id]);                     // Y  — depth 1
        Assert.Empty(map[main.Body[4].Id]);                     // Z  — depth 0 again
    }

    // --- 3. an Id that is not a statement of this op is ABSENT (not "top level") ---

    [Fact]
    public void UnknownStatementIdIsAbsentNotTopLevel()
    {
        var main = CompileMain("operation Main(){ use a=Qubit[1]; X(a[0]); }");
        var map = ContainerMap.Build(main);
        Assert.False(map.ContainsKey(new HirNodeId(int.MaxValue)));
    }

    // --- 4. while/repeat bodies and else-if chains are mapped (pin: deleting a Walk arm must fail here) ---

    [Fact]
    public void WhileRepeatAndElseIfChainsAreMapped()
    {
        var main = CompileMain(
            "operation Main(){ use a=Qubit[2]; var c: bit = M(a[0]); " +
            "while (c == 0) { X(a[1]); } repeat { Y(a[1]); } until (c == 1); " +
            "if (c == 1) { X(a[1]); } else if (c == 0) { Z(a[1]); } }");
        var map = ContainerMap.Build(main);

        var wh = Assert.IsType<HirWhileStatement>(main.Body[2]);
        Assert.Equal(new[] { wh.Id }, map[wh.Body[0].Id].Select(s => s.Id));

        var rp = Assert.IsType<HirRepeatStatement>(main.Body[3]);
        Assert.Equal(new[] { rp.Id }, map[rp.Body[0].Id].Select(s => s.Id));

        // `else if` nests a second HIR if inside the outer Else; its body carries both ifs in its chain.
        var outer = Assert.IsType<HirIfStatement>(main.Body[4]);
        var inner = Assert.IsType<HirIfStatement>(outer.Else[0]);
        Assert.Equal(new[] { outer.Id, inner.Id }, map[inner.Then[0].Id].Select(s => s.Id));
    }

    // --- 5. exhaustiveness guard: any FUTURE statement type that carries a nested statement list must be a
    //        known container — otherwise ContainerMap (and EffectAnalysis's own switch) would silently label
    //        its children top-level, the unsafe direction ---

    [Fact]
    public void EveryBodyBearingStatementTypeIsAKnownContainer()
    {
        var known = new HashSet<System.Type>
        {
            typeof(HirIfStatement),
            typeof(HirForStatement),
            typeof(HirWhileStatement),
            typeof(HirRepeatStatement),
        };
        var stmtTypes = typeof(HirStatement).Assembly.GetTypes()
            .Where(
                type =>
                    !type.IsAbstract
                    && typeof(HirStatement).IsAssignableFrom(type));
        foreach (var t in stmtTypes)
        {
            var carriesBody = t.GetProperties()
                .Any(
                    property =>
                        typeof(IEnumerable<HirStatement>)
                            .IsAssignableFrom(property.PropertyType));
            Assert.True(!carriesBody || known.Contains(t),
                $"statement type `{t.Name}` carries a nested statement list but is not handled by " +
                "ContainerMap.Walk (nor, likely, EffectAnalysis's statement switch) — add it to both");
        }
    }
}
