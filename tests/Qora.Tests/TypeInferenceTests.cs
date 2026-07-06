namespace Qora.Tests;

/// <summary>
/// An untyped <c>var</c>/<c>const</c> takes the type of its initializer at emission — inferred from the
/// symbol-table-seeded type map: a lone bit reference stays a <c>bit</c>, a real-valued expression a
/// <c>float</c>, everything else an <c>int</c>. (Finding G: <c>var x = r</c> with a bit <c>r</c> once emitted
/// a mis-typed <c>int x = r;</c> and compared it as an int (<c>== 1</c>) instead of a bit (<c>== true</c>).)
/// </summary>
public class TypeInferenceTests
{
    [Fact]
    public void UntypedVarFromBitIsEmittedAsBit()
    {
        // `var res = mb` where mb is a bit must emit `bit res = mb;` — not `int res = mb;` — and the condition
        // that reads it must compare as a bit (`res == true`), matching an explicitly-typed `bit res`.
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; bit mb=M(q[0]); var res = mb; if(res==1){ X(q[0]); } }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("bit res = mb;", r.Qasm);
        Assert.DoesNotContain("int res", r.Qasm);
        Assert.Contains("res == true", r.Qasm);
    }

    [Fact]
    public void UntypedVarFromIntIsEmittedAsInt() =>
        Compiler.Emits("operation Main(){ use q=Qubit[1]; int cnt=5; var got = cnt; Rx(got, q[0]); }", "int got = cnt;");

    [Fact]
    public void UntypedVarFromRealIsEmittedAsFloat() =>
        // floatness propagates from a built-in constant
        Compiler.Emits("operation Main(){ use q=Qubit[1]; var ang = pi/2; Rx(ang, q[0]); }", "float ang = pi / 2;");

    [Fact]
    public void FloatPropagatesThroughAnotherUntypedVar() =>
        // `var a = pi; var b = a / 2;` — b is a float too (the map records a as float)
        Compiler.Emits("operation Main(){ use q=Qubit[1]; var a = pi; var b = a / 2; Rx(b, q[0]); }", "float b = a / 2;");
}
