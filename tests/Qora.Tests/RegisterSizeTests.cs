namespace Qora.Tests;

/// <summary>
/// A register size and a literal qubit index must be positive integers within 32-bit range. A number too big
/// to parse (<c>Qubit[99999999999]</c>, <c>q[2147483648]</c>) once overflowed <c>int.Parse</c> and CRASHED
/// the compiler (QORA0000 — finding A); it must now be a clean QSEM016. A compiler must never crash on input.
/// </summary>
public class RegisterSizeTests
{
    [Theory]
    [InlineData("operation Main(){ use q = Qubit[99999999999]; H(q[0]); }")]           // size overflows int
    [InlineData("operation Main(){ use q = Qubit[2147483648]; H(q[0]); }")]            // int.MaxValue + 1
    [InlineData("operation Main(){ use q = Qubit[0]; }")]                              // zero-size register
    [InlineData("operation Main(){ use q = Qubit[2]; H(q[2147483648]); }")]           // index overflows int
    [InlineData("operation Main(){ use q = Qubit[2]; H(q[99999999999]); }")]          // index overflows int
    public void RejectsOutOfRangeSizeOrIndex(string source) => Compiler.Rejects(source, "QSEM016");

    [Theory]
    [InlineData("operation Main(){ use q = Qubit[99999999999]; H(q[0]); }")]
    [InlineData("operation Main(){ use q = Qubit[2]; H(q[2147483648]); }")]
    public void NeverCrashesOnHugeNumbers(string source)
    {
        // the crux of the fix: a clean diagnostic, never an internal-error crash
        var r = Compiler.Compile(source);
        Assert.False(r.Success);
        Assert.DoesNotContain("QORA0000", r.Errors.Select(e => e.Code));
        Assert.DoesNotContain("QINTERNAL", r.Errors.Select(e => e.Code));
    }

    [Theory]
    [InlineData("operation Main(){ use q = Qubit[3]; H(q[0]); H(q[2]); }")]            // valid size + in-range indices
    [InlineData("operation Main(){ use q = Qubit[2]; H(q[1]); }")]
    [InlineData("operation Foo(Qubit[] q){ H(q[3]); }\noperation Main(){ use q=Qubit[4]; Foo(q); }")]  // array parameter specialized to size 4
    public void AcceptsValidSizesAndIndices(string source) => Compiler.Accepts(source);
}
