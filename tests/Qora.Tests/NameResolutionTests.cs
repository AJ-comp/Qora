namespace Qora.Tests;

/// <summary>Unknown gate/operation names (QSEM007), declarations that shadow a built-in whose meaning can't
/// be disambiguated (QSEM013), and literal qubit-index bounds (QSEM016).</summary>
public class NameResolutionTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; Hadamard(q[0]); }")]                 // not a known gate
    [InlineData("operation Main(){ use q=Qubit[1]; foo(q[0]); }")]                      // typo / undefined op
    public void RejectsUnknownGate(string source) => Compiler.Rejects(source, "QSEM007");

    [Theory]
    [InlineData("operation Main(){ const pi = 3; }")]                                   // pi is a reserved expression literal
    [InlineData("operation Main(){ use q=Qubit[1]; var tau = 0; Rx(tau, q[0]); }")]     // tau is reserved
    public void RejectsReservedName(string source) => Compiler.Rejects(source, "QSEM013");

    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; H(q[5]); }")]                        // index past the register
    [InlineData("operation Main(){ use q=Qubit[2]; H(q[2]); }")]                        // index == size (0-based)
    public void RejectsIndexOutOfRange(string source) => Compiler.Rejects(source, "QSEM016");

    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; H(q[0]); H(q[1]); }")]
    [InlineData("operation Main(){ use q=Qubit[3]; for i in 0..2 { H(q[i]); } }")]      // a loop-var index is not range-checked at compile time
    public void AcceptsValidIndices(string source) => Compiler.Accepts(source);
}
