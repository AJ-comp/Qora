namespace Qora.Tests;

/// <summary>
/// `const` is an IMMUTABLE BINDING (QSEM024, Q#-<c>let</c> style): it accepts ANY value — a literal, a
/// runtime variable, a measurement — but can never be reassigned. Immutability is enforced at the SOURCE;
/// the OpenQASM target-lowering (<see cref="Ir.Passes.OpenQasmLowering"/>) then emits <c>const</c> only where
/// OpenQASM allows it (a compile-time-constant initializer) and a plain — still never reassigned — variable
/// otherwise. (OpenQASM <c>const</c> is compile-time only, like C#/Rust; Qora's is runtime-OK, like Q#/JS.)
/// </summary>
public class ConstTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; const int c=5; c=10; }")]                            // top level
    [InlineData("operation Main(){ use q=Qubit[2]; const c=M(q[0]); c=M(q[1]); }")]                     // re-measure
    [InlineData("operation Main(){ use q=Qubit[2]; bit r=M(q[0]); const int c=5; if(r==1){ c=10; } }")] // inside a branch
    [InlineData("operation Main(){ use q=Qubit[2]; const int c=5; for i in 0..1 { c=10; } }")]          // inside a loop
    [InlineData("operation Main(){ use q=Qubit[1]; int x=5; const int c=x; c=10; }")]                   // a runtime-bound const still can't be reassigned
    public void RejectsReassigningConst(string source) => Compiler.Rejects(source, "QSEM024");

    [Fact]
    public void CompileTimeConstKeepsConstKeyword()
    {
        // an initializer known at compile time (a literal, or an expression of only pi/tau/euler) is a real
        // OpenQASM const.
        Compiler.Emits("operation Main(){ use q=Qubit[1]; const int c = 5; Rx(c, q[0]); }", "const int c = 5;");
        Compiler.Emits("operation Main(){ use q=Qubit[1]; const c = pi/4; Rx(c, q[0]); }", "const float c = pi / 4;");
    }

    [Fact]
    public void RuntimeBoundConstIsDemotedToPlainVariable()
    {
        // OpenQASM `const` requires a compile-time constant, but Qora `const` accepts a runtime value — so it
        // must be emitted as a plain declaration (never reassigned, so still effectively immutable), NOT `const`.
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; int x=5; const int c = x; Rx(c, q[0]); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("int c =", r.Qasm);
        Assert.DoesNotContain("const int c", r.Qasm);   // the const keyword was correctly dropped
    }

    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; const a = M(q[0]); if(a==1){ X(q[1]); } }")]    // const bound to a measurement (Q#-let idiom)
    [InlineData("operation Main(){ use q=Qubit[1]; int x=5; const int c=x; Rx(c, q[0]); }")]       // const bound to a runtime value, never reassigned
    [InlineData("operation Main(){ use q=Qubit[1]; var x=5; x=10; Rx(x, q[0]); }")]                // a var, by contrast, CAN be reassigned
    public void AcceptsValidBindings(string source) => Compiler.Accepts(source);
}
