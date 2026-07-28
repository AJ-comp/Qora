using System.Linq;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// <see cref="HirCallExpression.CalleeId"/> is the reference binding. A call is bound to its callee by stable node Id at name
/// resolution (<see cref="Qora.Ir.Passes.Resolver"/>), and RE-POINTED to the size specialization at
/// monomorphization. A non-null <c>CalleeId</c> identifies a user callable; a built-in gate stays null. This removes
/// name-matching (which shifts across mono/mangle domains) from the analysis middle — consumers FOLLOW the
/// reference rather than re-match the name. These pin the binding VALUE directly (not just downstream behavior).
/// </summary>
public class CalleeIdTests
{
    private static Compilation Parse(string src)
    {
        var r = QoraCompiler.Compile(src);
        Assert.True(
            r.Succeeded,
            string.Join(" | ", r.Diagnostics.Select(diagnostic => diagnostic.Error)));
        return r;
    }

    private static HirCallExpression SoleUserCall(HirProgram program) =>
        program.Callables
            .Single(operation => operation.Name == "Main")
            .Body
            .OfType<HirCallStatement>()
            .Select(statement => statement.Call)
            .Single(call => call.CalleeId is not null);

    // --- 1. a plain user-op call is bound to its callee's Id ---
    [Fact]
    public void UserCallBindsToItsCalleeId()
    {
        var r = Parse("operation Foo(p: Qubit){ X(p); }\noperation Main(){ use a=Qubit[1]; Foo(a[0]); }");
        var foo = r.Hir.Resolved!.Program!.Callables.Single(o => o.Name == "Foo");
        Assert.Equal(foo.Id, SoleUserCall(r.Hir.Resolved!.Program!).CalleeId);
    }

    // --- 2. a built-in gate binds to nothing (null ⇒ "not a user-op call") ---
    [Fact]
    public void BuiltinGateHasNullCalleeId()
    {
        var r = Parse("operation Main(){ use a=Qubit[1]; X(a[0]); }");
        var x = r.Hir.Resolved!.Program.Callables
            .Single(operation => operation.Name == "Main")
            .Body
            .OfType<HirCallStatement>()
            .Select(statement => statement.Call)
            .Single(call => HirExpressions.QualifiedNameOf(call.Callee) == "X");
        Assert.Null(x.CalleeId);
    }

    // --- 3. THE point: a generic call is bound to the generic pre-mono, then RE-POINTED to the size
    //        specialization in the analyzed (mono) tree — the exact domain shift the reference survives ---
    [Fact]
    public void GenericCallRepointsFromGenericToSpecialization()
    {
        var r = Parse("operation Loop(p: Qubit[]){ X(p[0]); }\noperation Main(){ use a=Qubit[2]; Loop(a); }");

        // pre-mono (r.Hir.Resolved!.Program): bound to the GENERIC Loop
        var genLoop = r.Hir.Resolved!.Program!.Callables.Single(o => o.Name == "Loop");
        Assert.Equal(genLoop.Id, SoleUserCall(r.Hir.Resolved!.Program!).CalleeId);

        // analyzed (mono): re-pointed to the size-2 specialization — a DIFFERENT op, and NOT the generic
        var spec = r.Hir.EffectAnalysis!.Program!.Callables.Single(o => o.Name.StartsWith("Loop__sz"));
        var monoCall = SoleUserCall(r.Hir.EffectAnalysis!.Program!);
        Assert.Equal(spec.Id, monoCall.CalleeId);
        Assert.NotEqual(genLoop.Id, monoCall.CalleeId);
    }
}
