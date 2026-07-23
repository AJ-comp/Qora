using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// S1 of automatic uncomputation: <see cref="ConjugationLowering"/> flattens a hand-built
/// <see cref="QConjugate"/> (within/apply) into <c>Within ++ Apply ++ inverse(Within)</c> and it EMITS.
/// There is no surface syntax for within/apply yet (that is S2), so these build the IR directly and drive
/// the flatten + emit tail of the pipeline. They also pin the two safety guards: a non-invertible
/// <c>within</c> is a clean QSEM027 (never a silently-dropped cleanup), and a stray QConjugate that slips
/// past the flatten pass is caught loudly (ReferentialCheck QINTERNAL / a visible emitter marker) rather
/// than having its gates silently vanish.
/// </summary>
public class ConjugationTests
{
    private static QQubitArg Q(string reg, int i) => new(reg, i.ToString());

    /// <summary>A measurement target in the IR's canonical reference form (a register element).</summary>
    private static QNode MTarget(string reg, int i) => new QIndexNode(new QNameRef(reg), new QNumLit(i));
    private static QGate Gate(string name, params QArg[] args) => new(new List<string>(), name, args.ToList());
    private static QProgram Prog(params QStmt[] body) =>
        new(new List<QOperation> { new("Main", new List<QParam>(), body.ToList()) });

    /// <summary>The canonical S1 slice: within{ X(a[0]) } apply{ CNOT(a[0], d[0]) }.</summary>
    private static QProgram Canonical() => Prog(
        new QUse("a", 1),
        new QUse("d", 1),
        new QConjugate(
            Within: new List<QStmt> { Gate("X", Q("a", 0)) },
            Apply: new List<QStmt> { Gate("CNOT", Q("a", 0), Q("d", 0)) }));

    [Fact]
    public void FlattenProducesWithinThenApplyThenInverse()
    {
        var (lowered, errors) = ConjugationLowering.Run(Canonical());
        Assert.Empty(errors);

        var body = lowered.Operations.Single(o => o.Name == "Main").Body;
        // use a; use d; X(a[0]); CNOT(a[0],d[0]); Adjoint X(a[0])
        Assert.Equal(5, body.Count);
        Assert.IsType<QUse>(body[0]);
        Assert.IsType<QUse>(body[1]);

        var compute = Assert.IsType<QGate>(body[2]);
        Assert.Equal("X", compute.Name);
        Assert.Empty(compute.Functors);

        var action = Assert.IsType<QGate>(body[3]);
        Assert.Equal("CNOT", action.Name);

        var cleanup = Assert.IsType<QGate>(body[4]);
        Assert.Equal("X", cleanup.Name);
        Assert.Equal("Adjoint", Assert.Single(cleanup.Functors));
    }

    [Fact]
    public void FlattenEmitsCompute_Apply_Uncompute()
    {
        var (lowered, errors) = ConjugationLowering.Run(Canonical());
        Assert.Empty(errors);

        var qasm = QasmEmitter.Emit(OpenQasmLowering.Run(lowered));
        Assert.Contains("x a[0];", qasm);            // compute
        Assert.Contains("cx a[0], d[0];", qasm);     // apply
        Assert.Contains("inv @ x a[0];", qasm);      // uncompute (synthesized inverse of within)
    }

    [Fact]
    public void NonInvertibleWithinIsRejectedNotDropped()
    {
        // within measures — measurement is irreversible, so there is no cleanup. This must be a clean
        // QSEM027, never a flattened `compute; apply;` with the uncompute silently missing.
        var program = Prog(
            new QUse("a", 1),
            new QConjugate(
                Within: new List<QStmt> { new QDecl(false, QType.Bit, "r", new QMeasure(MTarget("a", 0))) },
                Apply: new List<QStmt> { Gate("X", Q("a", 0)) }));

        var (_, errors) = ConjugationLowering.Run(program);
        Assert.Equal("QSEM027", Assert.Single(errors).Code);
    }

    [Fact]
    public void StrayConjugatePastFlattenIsQInternal()
    {
        // ReferentialCheck is the primary structural guard: a QConjugate reaching it (i.e. ConjugationLowering
        // was skipped) is a compiler bug, reported loudly instead of dropping its gates at emission.
        var errors = ReferentialCheck.Verify(Canonical());
        Assert.Contains(errors, e => e.Code == "QINTERNAL");
    }

    [Fact]
    public void StrayConjugateAtEmitterIsVisibleNotSilent()
    {
        // Defense in depth: even bypassing the flatten pass AND ReferentialCheck, the emitter must never
        // silently drop the node — it emits a visible marker (the old latent bug was a silent drop).
        var qasm = QasmEmitter.Emit(Canonical());
        Assert.Contains("internal error", qasm);
    }
}
