using System.Linq;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// <see cref="QGate.CalleeOpId"/> — reference binding. A call is bound to its callee by stable node Id at name
/// resolution (<see cref="Qora.Ir.Passes.Resolver"/>), and RE-POINTED to the size specialization at
/// monomorphization. <c>CalleeOpId is int</c> ⟺ a user-op call; a built-in gate stays null. This removes
/// name-matching (which shifts across mono/mangle domains) from the analysis middle — consumers FOLLOW the
/// reference rather than re-match the name. These pin the binding VALUE directly (not just downstream behavior).
/// </summary>
public class CalleeOpIdTests
{
    private static QoraParseResult Parse(string src)
    {
        var r = QoraParser.Parse(src);
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        return r;
    }

    private static QGate SoleUserCall(QProgram p) =>
        p.Operations.Single(o => o.Name == "Main").Body.OfType<QGate>().Single(g => g.CalleeOpId is not null);

    // --- 1. a plain user-op call is bound to its callee's Id ---
    [Fact]
    public void UserCallBindsToItsCalleeId()
    {
        var r = Parse("operation Foo(Qubit p){ X(p); }\noperation Main(){ use a=Qubit[1]; Foo(a[0]); }");
        var foo = r.Ir!.Operations.Single(o => o.Name == "Foo");
        Assert.Equal(foo.Id, SoleUserCall(r.Ir!).CalleeOpId);
    }

    // --- 2. a built-in gate binds to nothing (null ⇒ "not a user-op call") ---
    [Fact]
    public void BuiltinGateHasNullCalleeOpId()
    {
        var r = Parse("operation Main(){ use a=Qubit[1]; X(a[0]); }");
        var x = r.Ir!.Operations.Single(o => o.Name == "Main").Body.OfType<QGate>().Single(g => g.Name == "X");
        Assert.Null(x.CalleeOpId);
    }

    // --- 3. Adjoint Foo is still bound to Foo — the reference survives the functor ---
    [Fact]
    public void AdjointCallBindsToTheForwardOp()
    {
        var r = Parse("operation Foo(Qubit p){ X(p); }\noperation Main(){ use a=Qubit[1]; Adjoint Foo(a[0]); }");
        var foo = r.Ir!.Operations.Single(o => o.Name == "Foo");
        var call = SoleUserCall(r.Ir!);
        Assert.Equal("Adjoint", call.Functors.Single());
        Assert.Equal(foo.Id, call.CalleeOpId);
    }

    // --- 4. THE point: a generic call is bound to the generic pre-mono, then RE-POINTED to the size
    //        specialization in the analyzed (mono) tree — the exact domain shift the reference survives ---
    [Fact]
    public void GenericCallRepointsFromGenericToSpecialization()
    {
        var r = Parse("operation Loop(Qubit[] p){ X(p[0]); }\noperation Main(){ use a=Qubit[2]; Loop(a); }");

        // pre-mono (r.Ir): bound to the GENERIC Loop
        var genLoop = r.Ir!.Operations.Single(o => o.Name == "Loop");
        Assert.Equal(genLoop.Id, SoleUserCall(r.Ir!).CalleeOpId);

        // analyzed (mono): re-pointed to the size-2 specialization — a DIFFERENT op, and NOT the generic
        var spec = r.AnalyzedIr!.Operations.Single(o => o.Name.StartsWith("Loop__sz"));
        var monoCall = SoleUserCall(r.AnalyzedIr!);
        Assert.Equal(spec.Id, monoCall.CalleeOpId);
        Assert.NotEqual(genLoop.Id, monoCall.CalleeOpId);
    }
}
