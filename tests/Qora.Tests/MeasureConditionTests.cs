using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// A measurement inside a condition remains a canonical HIR expression. HIR-to-MIR lowering emits the
/// measurement at that expression's exact control-flow position, so short-circuit and loop evaluation
/// order are preserved. Verifies acceptance, target shape, HIR preservation, and invalid placements.
/// </summary>
public class MeasureConditionTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; if(M(q[0])==1){ X(q[1]); } }")]
    [InlineData("operation Main(){ use q=Qubit[1]; while(M(q[0])==1){ X(q[0]); } }")]
    [InlineData("operation Main(){ use q=Qubit[1]; repeat { X(q[0]); } until(M(q[0])==1); }")]
    [InlineData("operation Main(){ use q=Qubit[2]; if(M(q[0])==1 && M(q[1])==0){ X(q[0]); } }")] // two measurements
    [InlineData("operation Main(){ use q=Qubit[3]; for i in 0..2 { if(M(q[i])==1){ X(q[i]); } } }")] // loop-var index
    [InlineData("operation Main(){ use q=Qubit[2]; if(M(q[0])==1){ if(M(q[1])==0){ X(q[0]); } } }")] // nested
    [InlineData("operation Foo(a: Qubit[]){ if(M(a[0])==1){ X(a[0]); } }\noperation Main(){ use q=Qubit[1]; Foo(q); }")] // inside a def
    [InlineData("operation Main(){ use q=Qubit[1]; if(!M(q[0])){ X(q[0]); } }")]        // negated measurement
    [InlineData("operation Main(){ use q=Qubit[1]; var __m0 = 5; if(M(q[0])==1){ Rx(__m0, q[0]); } }")] // temp name avoids a user's __m0
    public void AcceptsMeasurementInCondition(string source) => Compiler.Accepts(source);

    [Fact]
    public void IfLowersMeasurementAtItsConditionPoint()
    {
        var artifact = MirQasmTestModel.Compile(
            "operation Main(){ use q=Qubit[2]; if(M(q[0])==1){ X(q[1]); } }");
        var statements = MirQasmTestModel
            .Statements(artifact.Program.EntryBody)
            .ToArray();

        Assert.Single(
            statements.OfType<MirQasmMeasurementAssignmentStatement>());
        Assert.Single(statements.OfType<MirQasmIfStatement>());
    }

    [Fact]
    public void WhileKeepsMeasurementInsideTheRepeatedConditionFlow()
    {
        var artifact = MirQasmTestModel.Compile(
            "operation Main(){ use q=Qubit[1]; while(M(q[0])==1){ X(q[0]); } }");
        var loop = Assert.Single(
            artifact.Program.EntryBody
                .OfType<MirQasmWhileStatement>());

        Assert.Single(
            MirQasmTestModel
                .Statements(loop.Body)
                .OfType<MirQasmMeasurementAssignmentStatement>());
        Assert.DoesNotContain(
            artifact.Program.EntryBody,
            statement => statement is MirQasmMeasurementAssignmentStatement);
    }

    [Fact]
    public void HirKeepsTheCanonicalMeasurementInsideTheCondition()
    {
        var compiled = Compiler.Compile(
            "operation Main(){ use q=Qubit[1]; if(M(q[0])==1){ X(q[0]); } }");
        Assert.True(compiled.Succeeded);
        var resolved = compiled.Hir.Resolved!.Program;
        var branch = Assert.IsType<HirIfStatement>(
            resolved.Callables.Single().Body[1]);

        Assert.Single(
            HirExpressions
                .DescendantsAndSelf(branch.Condition)
                .OfType<HirMeasurementExpression>());
        Assert.DoesNotContain(
            resolved.Callables.Single().Body,
            statement => statement is HirVariableDeclarationStatement);
    }

    [Theory]
    // desugaring is scoped to CONDITIONS only — a measurement elsewhere is left in place and still rejected:
    [InlineData("operation Main(){ use q=Qubit[2]; for i in 0..M(q[0]) { X(q[1]); } }")] // a for bound
    [InlineData("operation Main(){ use q=Qubit[2]; Rx(M(q[0]), q[1]); }")]              // a rotation angle
    [InlineData("operation Main(){ use q=Qubit[1]; var x = M(q[0]) + 1; Rx(x, q[0]); }")] // a mixed initializer
    [InlineData("operation Main(){ use q=Qubit[1]; H(q[0]); var a: int[] = [M(q[0]) + 1, 2]; X(q[0]); }")] // an array-literal ELEMENT
    // a non-measurement call in a condition has no lowering and is rejected:
    [InlineData("operation Foo(a: Qubit){ H(a); }\noperation Main(){ use q=Qubit[1]; if(Foo(q[0])==1){ X(q[0]); } }")]
    public void RejectsCallInWrongPlace(string source) => Compiler.Rejects(source, "QSEM005");

    [Theory]
    // conditions WITHOUT a measurement are untouched:
    [InlineData("operation Main(){ use q=Qubit[2]; var r: bit = M(q[0]); if(r==1){ X(q[1]); } }")]
    [InlineData("operation Main(){ use q=Qubit[2]; var c: int = 0; if(c==0){ X(q[0]); } }")]
    public void LeavesPlainConditionsAlone(string source) => Compiler.Accepts(source);
}
