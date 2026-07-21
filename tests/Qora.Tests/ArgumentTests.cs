namespace Qora.Tests;

/// <summary>QSEM006 — call-argument checking, for built-in gates and user operations alike (both go through
/// the one unified <c>CheckCall</c> over a callable's signature). Wrong count, wrong kind per slot, and
/// (for user ops) wrong qubit shape/size.</summary>
public class ArgumentTests
{
    [Theory]
    // --- built-in gates ---
    [InlineData("operation Main(){ use q=Qubit[2]; Rx(q[0], q[1]); }")]                 // qubit in the angle slot
    [InlineData("operation Main(){ use q=Qubit[1]; const c: int=0; H(c); }")]            // classical in a qubit slot
    [InlineData("operation Main(){ use q=Qubit[2]; H(q[0], q[1]); }")]                  // too many args
    [InlineData("operation Main(){ use q=Qubit[2]; Controlled X(q[0]); }")]             // Controlled adds an arg
    [InlineData("operation Main(){ use q=Qubit[1]; var c: int=0; Rx(c[0], q[0]); }")]        // indexed classical in a value slot
    // --- user operations ---
    [InlineData("operation Foo(a: Qubit){ H(a); }\noperation Main(){ use q=Qubit[2]; Foo(q); }")]                 // single-qubit param given a whole register
    [InlineData("operation Foo(a: Qubit, x: int){ H(a); }\noperation Main(){ use q=Qubit[2]; Foo(q[0], q[1]); }")] // classical param given a qubit
    [InlineData("operation Foo(a: Qubit){ H(a); }\noperation Main(){ use q=Qubit[2]; Foo(q[0], q[1]); }")]        // wrong arity
    [InlineData("operation Foo(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\noperation Main(){ use q=Qubit[2]; Foo(q[0]); }")] // array parameter given a single qubit
    public void RejectsWrongArguments(string source) => Compiler.Rejects(source, "QSEM006");

    [Theory]
    // Regression: indexing is valid only when the base symbol is an array. Indexing scalar int/bit values must
    // not masquerade as either a classical array element or a qubit reference in a call argument.
    [InlineData("operation Main(){ use q=Qubit[1]; var c: int=0; Rx(c[0], q[0]); }")]                 // gate angle slot, int
    [InlineData("operation Main(){ use q=Qubit[2]; var r: bit=M(q[0]); Rx(r[0], q[1]); }")]            // gate angle slot, bit
    [InlineData("operation Main(){ use q=Qubit[2]; var c: int=0; Controlled Rx(c[0], q[0], q[1]); }")] // under a Controlled functor
    [InlineData("operation Foo(x: int, a: Qubit){ H(a); }\noperation Main(){ use q=Qubit[1]; var c: int=0; Foo(c[0], q[0]); }")] // a user-op classical parameter
    public void RejectsIndexedClassicalInValueSlot(string source) => Compiler.Rejects(source, "QSEM006");

    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; Rx(pi/2, q[0]); }")]                 // rotation, angle then qubit
    [InlineData("operation Main(){ use q=Qubit[2]; Controlled X(q[0], q[1]); }")]       // control, target
    [InlineData("operation Main(){ use q=Qubit[2]; Controlled Rx(0.5, q[0], q[1]); }")] // angle, control, target
    [InlineData("operation Main(){ use q=Qubit[2]; SWAP(q[0], q[1]); }")]
    [InlineData("operation Main(){ use q=Qubit[3]; CCX(q[0], q[1], q[2]); }")]
    [InlineData("operation Main(){ use q=Qubit[2]; H(q); }")]                            // a whole register broadcasts over a gate
    [InlineData("operation Foo(a: Qubit[]){ H(a[0]); }\noperation Main(){ use q=Qubit[2]; Foo(q); }")]           // qubit array
    [InlineData("operation Foo(a: Qubit[]){ H(a[0]); }\noperation Main(){ use q=Qubit[3]; Foo(q); }")]           // source type accepts any size
    [InlineData("operation Foo(a: Qubit){ H(a); }\noperation Main(){ use q=Qubit[2]; Foo(q[0]); }")]              // single qubit
    [InlineData("operation Rot(t: angle, a: Qubit){ Rx(t, a); }\noperation Main(){ use q=Qubit[1]; Rot(pi/2, q[0]); }")] // classical param
    [InlineData("operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\noperation Main(){ use q=Qubit[3]; Flip(q); }")] // internally specialized
    public void AcceptsWellFormedCalls(string source) => Compiler.Accepts(source);
}
