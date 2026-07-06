namespace Qora.Tests;

/// <summary>QSEM026 — a qubit cannot appear where a CLASSICAL value is required (a condition, a range bound,
/// arithmetic, a rotation angle, or as an assignment target). A qubit has no numeric value, so any of these
/// would emit invalid OpenQASM. Also QSEM017 (a measurement result is a bit) and QSEM024 (const) at
/// assignment sites.</summary>
public class ClassicalValueTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; Rx(pi/q, q[0]); }")]                 // qubit buried in an angle expression
    [InlineData("operation Main(){ use q=Qubit[2]; if(q==1){ X(q[1]); } }")]            // qubit in a condition
    [InlineData("operation Main(){ use q=Qubit[1]; while(q==1){ X(q[0]); } }")]         // qubit in a while condition
    [InlineData("operation Main(){ use q=Qubit[2]; for i in 0..q { X(q[1]); } }")]      // qubit as a range bound
    [InlineData("operation Main(){ use q=Qubit[1]; var x = q+1; Rx(x, q[0]); }")]       // qubit in an initializer
    [InlineData("operation Main(){ use q=Qubit[1]; var x = 0; x = q + 1; Rx(x, q[0]); }")] // qubit in an assigned value
    [InlineData("operation Rot(angle t, Qubit a){ Rx(t, a); }\noperation Main(){ use q=Qubit[1]; Rot(pi/q, q[0]); }")] // qubit in a classical arg expr
    [InlineData("operation Main(){ use q=Qubit[1]; q = 5; X(q[0]); }")]                 // assign TO a qubit register
    [InlineData("operation Main(){ use q=Qubit[1]; var x=0; q = x; X(q[0]); }")]        // assign TO a qubit (any value)
    [InlineData("operation Foo(Qubit a){ a = 3; }\noperation Main(){ use q=Qubit[1]; Foo(q[0]); }")] // assign TO a qubit parameter
    public void RejectsQubitAsClassical(string source) => Compiler.Rejects(source, "QSEM026");

    [Theory]
    // one qubit named twice in ONE expression must report QSEM026 exactly once (not per token)
    [InlineData("operation Main(){ use q=Qubit[1]; if(q==q){ X(q[0]); } }")]
    [InlineData("operation Main(){ use q=Qubit[1]; var x=q*q; X(q[0]); }")]
    [InlineData("operation Main(){ use q=Qubit[2]; for i in q..q { X(q[0]); } }")]
    public void QubitTwiceInOneExpressionReportsOnce(string source) => Compiler.RejectsExactly(source, "QSEM026");

    [Fact]
    public void MeasurementIntoNonBitTargetIsQsem017() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; int x=0; x = M(q[0]); }", "QSEM017");

    [Fact]
    public void ReassigningConstIsQsem024() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; const int c=0; c = 1; }", "QSEM024");

    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; var a = pi; Rx(a/2, q[0]); }")]      // classical var in an angle
    [InlineData("operation Main(){ use q=Qubit[3]; for i in 0..2 { X(q[i]); } }")]      // loop var as an index
    [InlineData("operation Main(){ use q=Qubit[2]; bit r=M(q[0]); if(r==1){ X(q[1]); } }")] // measure-bit in a condition
    [InlineData("operation Main(){ use q=Qubit[3]; const int n=2; for i in 0..n { X(q[i]); } }")] // const int as a bound
    [InlineData("operation Flip(Qubit[n] q){ for i in 0..n-1 { X(q[i]); } }\noperation Main(){ use q=Qubit[3]; Flip(q); }")] // symbolic size in a bound
    [InlineData("operation Main(){ use q=Qubit[2]; bit r=M(q[0]); r = M(q[1]); }")]     // reassign a bit with a measurement
    [InlineData("operation Main(){ use q=Qubit[1]; int c=0; c = c + 1; Rx(c, q[0]); }")] // reassign a classical
    public void AcceptsLegitimateClassicalUse(string source) => Compiler.Accepts(source);
}
