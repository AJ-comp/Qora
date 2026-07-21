namespace Qora.Tests;

/// <summary>Whole programs that must compile, plus a few checks on the SHAPE of the emitted OpenQASM
/// (hoisted declarations, functor lowering, bit-condition rewrite) so a regression there is caught.</summary>
public class ValidProgramTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; H(q[0]); CNOT(q[0], q[1]); var r: bit = M(q[0]); }")]  // Bell + measure
    [InlineData("operation Prep(q: Qubit[]){ H(q[0]); CNOT(q[0], q[1]); }\noperation Main(){ use q=Qubit[2]; Prep(q); var r: bit = M(q[0]); if(r==1){ X(q[1]); } }")] // call + feedback
    [InlineData("operation Main(){ use q=Qubit[3]; H(q[1]); CNOT(q[1],q[2]); CNOT(q[0],q[1]); H(q[0]); var m0: bit = M(q[0]); var m1: bit = M(q[1]); if(m1==1){ X(q[2]); } if(m0==1){ Z(q[2]); } }")] // teleportation
    [InlineData("operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\noperation Main(){ use q=Qubit[3]; Flip(q); }")] // array, monomorphized
    [InlineData("operation Prep(q: Qubit[]){ H(q[0]); }\noperation Main(){ use q=Qubit[1]; Prep(q); Adjoint Prep(q); }")]  // whole-op Adjoint
    [InlineData("operation Main(){ use q=Qubit[2]; for i in 0..1 { Rx(pi/2, q[i]); } }")] // for + rotation
    public void CompilesCleanly(string source) => Compiler.Accepts(source);

    [Fact]
    public void MeasureBitDeclarationIsHoisted()
    {
        // a measure bit is emitted as one flat top-level `bit r;`, with the measurement assigned in place —
        // OpenQASM importers accept only global classical declarations.
        var r = Compiler.Compile("operation Main(){ use q=Qubit[2]; var r: bit = M(q[0]); if(r==1){ X(q[1]); } }");
        Assert.True(r.Success);
        Assert.Contains("bit r;", r.Qasm);
        Assert.Contains("r = measure q[0];", r.Qasm);
    }

    [Fact]
    public void BitConditionRewritesToBool() =>
        // `r == 1` on a bit becomes `r == true` (OpenQASM compares a bit to a bool, not an int)
        Compiler.Emits("operation Main(){ use q=Qubit[2]; var r: bit = M(q[0]); if(r==1){ X(q[1]); } }", "r == true");

    [Fact]
    public void ControlledLowersToCtrlModifier() =>
        Compiler.Emits("operation Main(){ use q=Qubit[2]; Controlled X(q[0], q[1]); }", "ctrl @");

    [Fact]
    public void EmptyProgramCompiles()
    {
        var r = Compiler.Compile("operation Main(){ }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }
}
