namespace Qora.Tests;

/// <summary>Scoping and declarations: same-scope duplicates (QSEM015), use-before-declaration and unknown
/// names (QSEM025), block scoping of measure bits, and the rule that a measure bit — hoisted to one flat
/// emitted scope — may not shadow an enclosing register/parameter/measure bit.</summary>
public class ScopeTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; var r: bit=M(q[0]); var r: bit=M(q[1]); }")]   // duplicate measure bit
    [InlineData("operation Main(){ use q=Qubit[1]; use q=Qubit[1]; }")]                 // duplicate register
    [InlineData("operation Main(){ use r=Qubit[1]; var r: bit=M(r[0]); }")]                  // register vs measure bit
    [InlineData("operation Foo(a: Qubit, a: Qubit){ H(a); }\noperation Main(){ use q=Qubit[2]; Foo(q[0], q[1]); }")] // duplicate parameter
    [InlineData("operation Main(){ var x: int=0; var x: int=1; }")]                               // duplicate var in a block
    // a measure bit may not shadow an ENCLOSING hoisted name (they flatten to one emitted scope):
    [InlineData("operation Main(){ use q=Qubit[2]; var c: bit=M(q[0]); if(c==1){ var q: bit=M(q[1]); } }")]  // shadows a register
    [InlineData("operation Main(){ use q=Qubit[2]; var r: bit=M(q[0]); if(r==1){ var r: bit=M(q[1]); } }")]  // shadows an outer measure bit
    public void RejectsDuplicateDeclaration(string source) => Compiler.Rejects(source, "QSEM015");

    [Theory]
    [InlineData("operation Main(){ var y: int = x; var x: int = 0; }")]                           // classical used before its declaration
    [InlineData("operation Main(){ use q=Qubit[1]; Rx(theta, q[0]); }")]                // unknown name
    [InlineData("operation Main(){ use q=Qubit[2]; var c: bit=M(q[0]); if(c==1){ var r: bit=M(q[1]); } if(r==1){ X(q[0]); } }")] // measure bit used outside its block
    [InlineData("operation Main(){ use q=Qubit[1]; if(r==1){ X(q[0]); } var r: bit=M(q[0]); }")] // measure bit used before its line
    public void RejectsUnresolvedName(string source) => Compiler.Rejects(source, "QSEM025");

    [Theory]
    // an operation is a symbol at the program layer, but NOT a value: it resolves up the scope chain yet
    // can only be CALLED, never referenced in an expression / argument / index.
    [InlineData("operation Foo(q: Qubit[]){ H(q[0]); }\noperation Main(){ use a=Qubit[1]; var x: int = Foo; }")]        // op as an initializer value
    [InlineData("operation Foo(q: Qubit[]){ H(q[0]); }\noperation Main(){ use a=Qubit[1]; Foo(a); var y: int = Foo + 1; }")] // op inside an arithmetic expression
    public void RejectsOperationUsedAsValue(string source) => Compiler.Rejects(source, "QSEM028");

    [Theory]
    // nested shadowing of a BLOCK-SCOPED classical is allowed (C++/Q#/Silq-style):
    [InlineData("operation Main(){ use q=Qubit[1]; var x: int=0; if(x==0){ var x: int=1; Rx(0.5, q[0]); } }")]
    // a `use` register may be forward-referenced (registers are hoisted):
    [InlineData("operation Main(){ H(q[0]); use q=Qubit[1]; }")]
    // a measure bit declared inside a block is fine when used within that block:
    [InlineData("operation Main(){ use q=Qubit[2]; var c: bit=M(q[0]); if(c==1){ var r: bit=M(q[1]); if(r==1){ X(q[0]); } } }")]
    // the same measure-bit name in disjoint if/else branches is fine (they never coexist):
    [InlineData("operation Main(){ use q=Qubit[2]; var c: bit=M(q[0]); if(c==1){ var r: bit=M(q[1]); if(r==1){X(q[0]);} } else { var r: bit=M(q[0]); if(r==1){X(q[1]);} } }")]
    [InlineData("operation Main(){ use q=Qubit[2]; var r0: bit=M(q[0]); var r1: bit=M(q[1]); }")] // distinct measure bits
    // `.Count` is a member, so an unrelated operation named Count is not resolved as its value.
    [InlineData("operation Count(q: Qubit[]){ H(q[0]); }\noperation Foo(q: Qubit[]){ for i in 0..q.Count-1 { H(q[i]); } }\noperation Main(){ use a=Qubit[2]; Foo(a); }")]
    public void AcceptsValidScoping(string source) => Compiler.Accepts(source);
}
