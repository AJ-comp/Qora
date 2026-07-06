namespace Qora.Tests;

/// <summary>
/// The logical-NOT `!` on a condition. OpenQASM has no `!` on a classical scalar (it reads a bit/int as a
/// numeric literal, not a bool), so the emitter rewrites `!x` at emit time: a bit becomes `x == false`, any
/// other classical becomes `x == 0`. The name's type comes from the symbol table (one source), so EVERY
/// classical kind — parameter, var, measure bit, AND loop variable — is rewritten; a qubit has no numeric
/// value and is rejected outright. (Regression guard for finding E: a loop variable used to emit an invalid
/// `! ( i )` because it was missing from the emitter's hand-rolled type map.)
/// </summary>
public class NegationTests
{
    [Fact]
    public void NotOnBitBecomesEqualsFalse() =>
        Compiler.Emits("operation Main(){ use q=Qubit[1]; bit r=M(q[0]); if(!r){ X(q[0]); } }", "r == false");

    [Fact]
    public void NotOnIntVarBecomesEqualsZero() =>
        Compiler.Emits("operation Main(){ use q=Qubit[1]; int n=0; if(!n){ X(q[0]); } }", "n == 0");

    [Fact]
    public void NotOnLoopVarBecomesEqualsZero() =>
        // finding E — the loop variable is now known (from the symbol table) to be an int, so `!i` rewrites
        Compiler.Emits("operation Main(){ use q=Qubit[1]; for i in 0..2 { if(!i){ X(q[0]); } } }", "i == 0");

    [Fact]
    public void LoopVarEqualityIsNotBitRewritten()
    {
        // an int loop var compared to 1 must stay `i == 1` — NOT wrongly rewritten to `i == true`
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; for i in 0..2 { if(i==1){ X(q[0]); } } }");
        Assert.True(r.Success);
        Assert.Contains("i == 1", r.Qasm);
        Assert.DoesNotContain("i == true", r.Qasm);
    }

    [Fact]
    public void NotOnQubitIsRejected() =>
        // a qubit has no boolean/numeric value; negating it is QSEM026, not a broken emission
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; if(!q){ X(q[0]); } }", "QSEM026");
}
