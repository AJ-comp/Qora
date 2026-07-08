using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Effect analysis (auto-uncompute rung ①): per-statement qubit Touched/Modified sets and per-operation
/// summaries, recorded on the persistent <see cref="SemanticModel"/> keyed by stable node Ids. Modified is
/// COMPUTATIONAL-BASIS: control slots and diagonal-gate targets are touched but not modified. Parse-based
/// tests use NON-generic programs on purpose — <c>r.Ir</c> is the pre-monomorphize program, and only for a
/// generics-free program is that the exact program the analysis (and its Ids) ran on.
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

    private static StmtEffects FxOf(SemanticModel m, QStmt stmt)
    {
        var fx = m.FindEffects(stmt.Id);
        Assert.NotNull(fx);
        return fx!;
    }

    private static QubitRef At(string reg, int i) => new(reg, i);
    private static QubitRef Whole(string reg) => new(reg, null);

    private static void AssertRefs(IReadOnlySet<QubitRef> actual, params QubitRef[] expected)
        => Assert.True(actual.SetEquals(expected),
            $"expected {{{string.Join(", ", expected)}}}, got {{{string.Join(", ", actual)}}}");

    // --- 1. plain single-qubit gate: the target is touched AND modified ---

    [Fact]
    public void SingleQubitGateTouchesAndModifiesTarget()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; H(q[0]); }");
        var fx = FxOf(m, Op(r, "Main").Body[1]);
        AssertRefs(fx.Touched, At("q", 0));
        AssertRefs(fx.Modified, At("q", 0));
    }

    // --- 2. controlled gate: the control is touched only — its basis value never changes ---

    [Fact]
    public void CnotControlIsTouchedNotModified()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; CNOT(q[0], q[1]); }");
        var fx = FxOf(m, Op(r, "Main").Body[1]);
        AssertRefs(fx.Touched, At("q", 0), At("q", 1));
        AssertRefs(fx.Modified, At("q", 1));
    }

    // --- 3. the Controlled FUNCTOR prepends one control slot on top of the gate's own ---

    [Fact]
    public void ControlledFunctorAddsALeadingControlSlot()
    {
        var (r, m) = Compile("operation Main(){ use c=Qubit[1]; use q=Qubit[1]; Controlled X(c[0], q[0]); }");
        var fx = FxOf(m, Op(r, "Main").Body[2]);
        AssertRefs(fx.Touched, At("c", 0), At("q", 0));
        AssertRefs(fx.Modified, At("q", 0));
    }

    // --- 4. SWAP has no controls: both operands are targets, both modified ---

    [Fact]
    public void SwapModifiesBothOperands()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; SWAP(q[0], q[1]); }");
        var fx = FxOf(m, Op(r, "Main").Body[1]);
        AssertRefs(fx.Touched, At("q", 0), At("q", 1));
        AssertRefs(fx.Modified, At("q", 0), At("q", 1));
    }

    // --- 5. diagonal gates preserve every basis value: touched yes, modified NOTHING;
    //        a rotation's angle argument produces no qubit ref at all ---

    [Fact]
    public void DiagonalGatesModifyNothing()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; CZ(q[0], q[1]); Rz(pi/4, q[0]); }");
        var body = Op(r, "Main").Body;

        var cz = FxOf(m, body[1]);
        AssertRefs(cz.Touched, At("q", 0), At("q", 1));
        Assert.Empty(cz.Modified);

        var rz = FxOf(m, body[2]);
        AssertRefs(rz.Touched, At("q", 0));   // exactly one ref — the angle is a value, not a qubit
        Assert.Empty(rz.Modified);
    }

    // --- 6. measurement modifies (collapses) its target and makes the op irreversible;
    //        a purely classical declaration has no qubit contact ---

    [Fact]
    public void MeasurementModifiesTargetAndMarksOpIrreversible()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[1]; const int x = 1; H(q[0]); bit r = M(q[0]); }");
        var main = Op(r, "Main");

        var classical = FxOf(m, main.Body[1]);
        Assert.Empty(classical.Touched);
        Assert.Empty(classical.Modified);

        var measure = FxOf(m, main.Body[3]);
        AssertRefs(measure.Touched, At("q", 0));
        AssertRefs(measure.Modified, At("q", 0));

        var summary = m.FindOpEffects(main.Id);
        Assert.NotNull(summary);
        Assert.True(summary!.Irreversible);
        Assert.Empty(summary.ParamTouched);   // entry op has no formal params
    }

    // --- 7. reset-like built-ins: targets modified, op irreversible; ResetAll blankets the register ---

    [Fact]
    public void ResetModifiesAndIsIrreversible()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; Reset(q[0]); ResetAll(q); }");
        var main = Op(r, "Main");

        AssertRefs(FxOf(m, main.Body[1]).Modified, At("q", 0));
        AssertRefs(FxOf(m, main.Body[2]).Modified, Whole("q"));
        Assert.True(m.FindOpEffects(main.Id)!.Irreversible);
    }

    // --- 8. a whole-register broadcast gate blankets the register ---

    [Fact]
    public void BroadcastGateBlanketsWholeRegister()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[3]; H(q); }");
        var fx = FxOf(m, Op(r, "Main").Body[1]);
        AssertRefs(fx.Touched, Whole("q"));
        AssertRefs(fx.Modified, Whole("q"));
    }

    // --- 9. a loop-variable index blankets the register (both on the gate and the aggregated QFor);
    //        a literal index elsewhere stays precise ---

    [Fact]
    public void LoopVariableIndexBlanketsButLiteralStaysPrecise()
    {
        var (r, m) = Compile("operation Main(){ use q=Qubit[2]; for i in 0..1 { X(q[i]); } H(q[0]); }");
        var body = Op(r, "Main").Body;

        var loop = Assert.IsType<QFor>(body[1]);
        AssertRefs(FxOf(m, loop).Modified, Whole("q"));
        AssertRefs(FxOf(m, loop.Body[0]).Modified, Whole("q"));
        AssertRefs(FxOf(m, body[2]).Modified, At("q", 0));
    }

    // --- 10. QIf aggregates the union over BOTH branches (conservative: either path may run) ---

    [Fact]
    public void IfAggregatesUnionOfBothBranches()
    {
        var (r, m) = Compile(
            "operation Main(){ use q=Qubit[2]; bit r = M(q[0]); if (r == 1) { X(q[0]); } else { H(q[1]); } }");
        var fx = FxOf(m, Op(r, "Main").Body[2]);
        AssertRefs(fx.Touched, At("q", 0), At("q", 1));
        AssertRefs(fx.Modified, At("q", 0), At("q", 1));
    }

    // --- 11. two-level call DAG: a callee's summary is stated in FORMAL param names, and each call
    //         site projects it into the caller's actual registers, level by level ---

    [Fact]
    public void CallEffectsProjectThroughTwoLevels()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit a, Qubit b){ CNOT(a, b); }\n" +
            "operation Bar(Qubit x, Qubit y){ Foo(x, y); }\n" +
            "operation Main(){ use q=Qubit[2]; Bar(q[0], q[1]); }");

        var foo = m.FindOpEffects(Op(r, "Foo").Id)!;
        AssertRefs(foo.ParamTouched, Whole("a"), Whole("b"));   // summary speaks formal names
        AssertRefs(foo.ParamModified, Whole("b"));
        Assert.False(foo.Irreversible);

        var call = FxOf(m, Op(r, "Main").Body[1]);              // Bar(q[0], q[1]) — fully projected
        AssertRefs(call.Touched, At("q", 0), At("q", 1));
        AssertRefs(call.Modified, At("q", 1));

        Assert.Empty(m.FindOpEffects(Op(r, "Main").Id)!.ParamTouched);
    }

    // --- 12. single-qubit param binding: a literal actual stays precise, a loop-variable actual blankets ---

    [Fact]
    public void SingleQubitParamProjectsLiteralPreciselyAndLoopVarAsBlanket()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit a){ X(a); }\n" +
            "operation Main(){ use q=Qubit[3]; Foo(q[1]); for i in 0..2 { Foo(q[i]); } }");
        var body = Op(r, "Main").Body;

        AssertRefs(FxOf(m, body[1]).Modified, At("q", 1));
        var loop = Assert.IsType<QFor>(body[2]);
        AssertRefs(FxOf(m, loop.Body[0]).Modified, Whole("q"));
    }

    // --- 13. Adjoint Foo touches/modifies exactly what Foo does (U† acts on the same qubits) ---

    [Fact]
    public void AdjointCallHasSameEffectsAsPlainCall()
    {
        var (r, m) = Compile(
            "operation Foo(Qubit[2] a){ H(a[0]); CNOT(a[0], a[1]); }\n" +
            "operation Main(){ use q=Qubit[2]; Foo(q); Adjoint Foo(q); }");
        var body = Op(r, "Main").Body;

        var plain = FxOf(m, body[1]);
        var adjoint = FxOf(m, body[2]);
        Assert.True(plain.Touched.SetEquals(adjoint.Touched));
        Assert.True(plain.Modified.SetEquals(adjoint.Modified));
    }

    // --- 14. lineage: ConjugationLowering's inverse copies carry fresh Ids, and FindEffects resolves a
    //         copy's Id — through the DerivedFrom chain — to the effects recorded for the SOURCE gate ---

    [Fact]
    public void ConjugationCopyInheritsEffectsThroughDerivationChain()
    {
        var withinGate = new QGate(new List<string>(), "X", new List<QArg> { new QQubitArg("a", "0") });
        var program = new QProgram(new List<QOperation>
        {
            new("Main", new List<QParam>(), new List<QStmt>
            {
                new QUse("a", 1),
                new QConjugate(
                    Within: new List<QStmt> { withinGate },
                    Apply: new List<QStmt> { new QGate(new List<string>(), "H", new List<QArg> { new QQubitArg("a", "0") }) }),
            }),
        });

        var valErrors = QoraValidator.Validate(program, out var model);
        Assert.Empty(valErrors);
        EffectAnalysis.Run(program, model!);

        var (lowered, errors) = ConjugationLowering.Run(program, model!);
        Assert.Empty(errors);

        // use a; X; H; Adjoint X(copy)
        var body = lowered.Operations.Single().Body;
        Assert.Equal(4, body.Count);
        var gateCopy = Assert.IsType<QGate>(body[3]);
        Assert.NotEqual(withinGate.Id, gateCopy.Id);

        var viaCopy = model!.FindEffects(gateCopy.Id);
        Assert.NotNull(viaCopy);
        Assert.Same(model.FindEffects(withinGate.Id), viaCopy);
        AssertRefs(viaCopy!.Touched, At("a", 0));
    }

    // --- 15. coverage regression: a representative program records effects for EVERY statement
    //         (containers included) and still compiles to the same QASM shapes as before ---

    [Fact]
    public void EveryStatementOfARepresentativeProgramHasEffects()
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

        foreach (var stmt in Op(r, "Main").Body.SelectMany(Flatten))
            Assert.NotNull(m.FindEffects(stmt.Id));

        // pure analysis: emission is untouched (the full byte-identity check is the CLI diff)
        Assert.Contains("h q[0];", r.Qasm);
        Assert.Contains("cx q[0], q[1];", r.Qasm);
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
