using Qora.Ir;

namespace Qora.Tests;

public class QubitArrayTests
{
    [Theory]
    [InlineData("operation Bad(q: Qubit[2]){} operation Main(){ use q=Qubit[2]; Bad(q); }")]
    [InlineData("operation Bad(q: Qubit[n]){} operation Main(){ use q=Qubit[2]; Bad(q); }")]
    public void RejectsLengthsInSourceParameterTypes(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Success);
        Assert.DoesNotContain(result.Errors, error => error.Code is "QORA0000" or "QINTERNAL");
    }

    [Fact]
    public void CreatesOneHiddenSpecializationPerCallSize()
    {
        var result = Compile("""
            operation Visit(qubits: Qubit[]) {
                for i in 0..qubits.Count-1 { X(qubits[i]); }
            }
            operation Main() {
                use two = Qubit[2];
                use three = Qubit[3];
                Visit(two);
                Visit(three);
            }
            """);

        var specs = result.AnalyzedIr!.Operations.Where(o => o.DisplayName == "Visit").ToList();
        Assert.Equal(new[] { 2, 3 }, specs.Select(o => o.Params.Single().RegisterSize!.Value).Order().ToArray());
        Assert.DoesNotContain(".Count", result.Qasm);
        Assert.Contains("def Visit__sz2(qubit[2] qubits)", result.Qasm);
        Assert.Contains("def Visit__sz3(qubit[3] qubits)", result.Qasm);
    }

    [Fact]
    public void BindsMultipleQubitArraysIndependently()
    {
        var result = Compile("""
            operation Pair(left: Qubit[], right: Qubit[]) {
                for i in 0..left.Count-1 { X(left[i]); }
                for j in 0..right.Count-1 { X(right[j]); }
            }
            operation Main() {
                use left = Qubit[2];
                use right = Qubit[3];
                Pair(left, right);
            }
            """);

        var pair = result.AnalyzedIr!.Operations.Single(o => o.DisplayName == "Pair");
        Assert.Equal(new[] { 2, 3 }, pair.Params.Select(p => p.RegisterSize!.Value).ToArray());
        Assert.Contains("Pair__sz2_3", pair.Name);
    }

    [Fact]
    public void SpecializesNestedQubitArrayCalls()
    {
        var result = Compile("""
            operation Inner(qubits: Qubit[]) {
                for i in 0..qubits.Count-1 { X(qubits[i]); }
            }
            operation Outer(qubits: Qubit[]) { Inner(qubits); }
            operation Main() {
                use work = Qubit[4];
                Outer(work);
            }
            """);

        var inner = result.AnalyzedIr!.Operations.Single(o => o.DisplayName == "Inner");
        var outer = result.AnalyzedIr.Operations.Single(o => o.DisplayName == "Outer");
        Assert.Equal(4, inner.Params.Single().RegisterSize);
        Assert.Equal(4, outer.Params.Single().RegisterSize);
        var nestedCall = outer.Body.OfType<QGate>().Single();
        Assert.Equal(inner.Id, nestedCall.CalleeOpId);
    }

    [Fact]
    public void ResolvesCountOnAnEntryAllocationWithoutAGenericOperation()
    {
        var result = Compile("operation Main(){ use work=Qubit[3]; for i in 0..work.Count-1 { X(work[i]); } }");

        Assert.Contains("for int i in [0:3 - 1]", result.Qasm);
        Assert.DoesNotContain(".Count", result.Qasm);
    }

    [Fact]
    public void SpecializationLeavesClassicalArrayCountForSizeofLowering()
    {
        var result = Compile("""
            operation Mix(q: Qubit[], values: int[]) {
                for i in 0..q.Count-1 { X(q[i]); }
                for j in 0..values.Count-1 { values[j] = values[j] + 1; }
            }
            operation Main() {
                use q = Qubit[2];
                var values: int[] = [1, 2, 3];
                Mix(q, values);
            }
            """);

        Assert.Contains("for int i in [0:2 - 1]", result.Qasm);
        Assert.Contains("for int j in [0:sizeof(values) - 1]", result.Qasm);
    }

    [Fact]
    public void RechecksLiteralBoundsAfterSpecialization() =>
        Compiler.Rejects(
            "operation Bad(q: Qubit[]){ X(q[2]); } operation Main(){ use q=Qubit[2]; Bad(q); }",
            "QSEM016");

    private static QoraParseResult Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        return result;
    }
}
