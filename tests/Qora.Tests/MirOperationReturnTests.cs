using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MirOperationReturnTests
{
    [Fact]
    public void ReturningOperationRemainsAnOperationAndItsCallIsEffectful()
    {
        var program = Compile("""
            operation Read(q: Qubit): bit { return M(q); }
            operation Main(): bit {
                use q = Qubit[1];
                return Read(q[0]);
            }
            """);
        Assert.All(program.Callables, callable => Assert.Equal(MirCallableKind.Operation, callable.Kind));
        var call = Assert.Single(Instructions(program.EntryPoint).OfType<MirQuantumApply>());
        Assert.NotNull(call.Result);
        Assert.Contains(call.Result, call.Results);
        Assert.Single(call.QubitResults);
        Assert.Empty(Instructions(program.EntryPoint).OfType<MirPureCall>());
        Assert.Equal(call.Result.Id, Assert.Single(program.EntryPoint.Blocks.Select(b => b.Terminator).OfType<MirReturn>()).Value);
    }

    [Fact]
    public void IgnoringReturnDoesNotDiscardOperationEffects()
    {
        var program = Compile("""
            operation Flip(q: Qubit): int { X(q); return 7; }
            operation Main() { use q = Qubit[1]; Flip(q[0]); }
            """);
        var call = Assert.Single(Instructions(program.EntryPoint).OfType<MirQuantumApply>());
        Assert.NotNull(call.Result);
        Assert.Single(call.QubitResults);
        Assert.Null(program.EntryPoint.ReturnType);
    }

    [Fact]
    public void ReturningAnEffectfulValueCannotBeReclassifiedAsAPureFunction()
    {
        var program = Compile("""
            operation Value(): int { return 1; }
            operation Main(): int { return Value(); }
            """);
        var callable = program.EntryPoint;
        Assert.Throws<ArgumentException>(() => new MirCallable(
            callable.Id, callable.Name, MirCallableKind.Function, callable.ReturnType,
            callable.Parameters, callable.Blocks, callable.Origin));
    }

    [Fact]
    public void ReturningSavedMeasurementDoesNotMeasureAgain()
    {
        var program = Compile("""
            operation Main(): bit {
                use q = Qubit[1];
                var r = M(q[0]);
                X(q[0]);
                return r;
            }
            """);
        var measured = Assert.Single(Instructions(program.EntryPoint).OfType<MirMeasure>());
        var returned = Assert.Single(program.EntryPoint.Blocks.Select(b => b.Terminator).OfType<MirReturn>());
        Assert.Equal(measured.Result.Id, returned.Value);
    }

    [Theory]
    [InlineData("flag == 1 && Change(var values)")]
    [InlineData("flag == 1 || Change(var values)")]
    public void ShortCircuitMergesEffectfulArrayState(string condition)
    {
        Compile($$"""
            operation Change(var values: int[]): bool { values[0] = 5; return true; }
            operation Main(): int {
                use q = Qubit[1];
                var flag = M(q[0]);
                var values: int[] = [0];
                if ({{condition}}) { }
                return values[0];
            }
            """);
    }

    [Fact]
    public void LaterArgumentEffectsUpdateEarlierQubitArgumentState()
    {
        Compile("""
            operation Probe(q: Qubit): bit { X(q); return M(q); }
            operation Touch(q: Qubit, flag: bit) { if (flag == 1) { X(q); } }
            operation Main(): bit {
                use q = Qubit[1];
                Touch(q[0], Probe(q[0]));
                return M(q[0]);
            }
            """);
    }

    [Fact]
    public void EffectfulQubitIndexUsesStateAfterIndexEvaluation()
    {
        Compile("""
            operation Index(q: Qubit): int { X(q); return 0; }
            operation Main(): bit {
                use q = Qubit[1];
                var index = Index(q[0]);
                if (0 <= index && index < q.Count) { X(q[index]); }
                return M(q[0]);
            }
            """);
    }

    [Fact]
    public void FunctionStillUsesPureCallInstruction()
    {
        var program = Compile("""
            function Next(value: int): int { return value + 1; }
            operation Main(): int { return Next(1); }
            """);
        Assert.Single(Instructions(program.EntryPoint).OfType<MirPureCall>());
        Assert.Empty(Instructions(program.EntryPoint).OfType<MirQuantumApply>());
        Assert.Equal(MirCallableKind.Function, program.Callables.Single(c => c.Name == "Next").Kind);
    }

    [Fact]
    public void LaterArgumentMutationIsVisibleThroughEarlierArrayArgument()
    {
        Compile("""
            operation Change(var values: int[]): int { values[0] = 5; return 0; }
            function Read(values: int[], ignored: int): int { return values[0]; }
            operation Main(): int {
                var values: int[] = [0];
                return Read(values, Change(var values));
            }
            """);
    }

    [Fact]
    public void AssignmentUsesArrayStateAfterEffectfulRightHandSide()
    {
        Compile("""
            operation Change(var values: int[]): int { values[1] = 5; return 3; }
            operation Main(): int {
                var values: int[] = [0, 0];
                values[0] = Change(var values);
                return values[0] + values[1];
            }
            """);
    }

    private static IEnumerable<MirInstruction> Instructions(MirCallable callable) =>
        callable.Blocks.SelectMany(block => block.Instructions);

    private static MirProgram Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.Error.Message)));
        var program = Assert.IsType<MirSnapshot>(result.Mir).Program;
        Assert.Empty(QoraMirVerifier.Verify(program));
        return program;
    }
}
