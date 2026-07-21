namespace Qora.Tests;

/// <summary>
/// A measurement written inside a condition is DESUGARED to the two-step form OpenQASM needs
/// (<see cref="Ir.Passes.MeasureConditionLowering"/>): a hoisted <c>bit</c> measures the qubit, the condition
/// tests the bit. Verifies acceptance, the emitted shape per construct (if / while / repeat), scope
/// containment (only conditions, not other positions), and that non-measurement calls are still rejected.
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
    public void IfDesugarsToBitThenTest()
    {
        // the measurement becomes a hoisted bit assigned before the branch; the branch tests that bit
        Compiler.Emits("operation Main(){ use q=Qubit[2]; if(M(q[0])==1){ X(q[1]); } }", "= measure q[0];");
        Compiler.Emits("operation Main(){ use q=Qubit[2]; if(M(q[0])==1){ X(q[1]); } }", "if (");
    }

    [Fact]
    public void WhileReMeasuresAtEndOfBody()
    {
        // the condition is re-evaluated each iteration, so the qubit is re-measured at the end of the body
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; while(M(q[0])==1){ X(q[0]); } }");
        Assert.True(r.Success);
        // two `measure q[0]` occurrences: once before the loop, once at the end of the body
        var count = r.Qasm.Split("measure q[0];").Length - 1;
        Assert.True(count == 2, $"expected 2 measurements (before loop + per iteration), got {count}:\n{r.Qasm}");
    }

    [Fact]
    public void UserThatShadowsTempNameStillCompiles()
    {
        // a user variable literally named `__m0` must not collide with the synthetic temp (would be QSEM015)
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var __m0 = 5; if(M(q[0])==1){ Rx(__m0, q[0]); } }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Fact]
    public void SyntheticTempStaysDistinctFromUserNames()
    {
        // The synthetic temp is a HoistName placeholder (base `__m0`); the mangler keeps the user's own
        // `__m0`/`__m1` and splits the temp to a distinct spelling — no name-scanning, no collision.
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var __m0=1; var __m1=2; if(M(q[0])==1){ X(q[0]); } }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("int __m0 = 1;", r.Qasm);        // the user's names are preserved
        Assert.Contains("int __m1 = 2;", r.Qasm);
        Assert.Contains("__m0_ = measure q[0];", r.Qasm); // the temp is a distinct spelling
        Assert.DoesNotContain("#hoist", r.Qasm);          // the placeholder never leaks into the final QASM
    }

    /// <summary>The masking hole (Codex R-report): a synthetic temp named exactly `__m0` used to become a
    /// real declaration BEFORE validation, so a user's UNDECLARED `__m0 = …` silently bound to it and its
    /// `QSEM025` was lost. With the placeholder temp, the user's `__m0` stays undeclared and is reported.</summary>
    [Fact]
    public void UndeclaredAssignmentToTempSpellingIsStillReported()
    {
        var masked = Compiler.Compile("operation Main(){ use q=Qubit[1]; if(M(q[0])==1){ X(q[0]); } __m0 = 1; }");
        Assert.False(masked.Success);
        Assert.Contains(masked.Errors, e => e.Code == "QSEM025");

        // control: the identical shape with any other undeclared name already reported QSEM025 — the fix
        // makes `__m0` behave the same, not specially.
        var control = Compiler.Compile("operation Main(){ use q=Qubit[1]; if(M(q[0])==1){ X(q[0]); } foo = 1; }");
        Assert.False(control.Success);
        Assert.Contains(control.Errors, e => e.Code == "QSEM025");
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
