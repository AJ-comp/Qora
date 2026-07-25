using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Semantic and target-policy coverage for full index expressions. Parsing/lowering shape is covered
/// separately; these tests pin the boundary between a failed common proof and OpenQASM's QSEM030 policy.
/// </summary>
public class GeneralIndexExpressionSemanticTests
{
    [Fact]
    public void CommonValidationRecordsTheFactAndOpenQasmDerivesQsem030()
    {
        var compiled = Compiler.Compile("""
            function idx(): int {
                return 0;
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var value: int = xs[idx()];
            }
            """);

        Assert.False(compiled.Success);
        Assert.Empty(compiled.Qasm);
        Assert.Single(compiled.Errors, error => error.Code == "QSEM030");
        Assert.DoesNotContain(compiled.Errors, error => error.Code is "CE0001" or "QSEM005" or "QSEM007");

        var commonErrors = QoraValidator.Validate(Assert.IsType<QProgram>(compiled.Ir), out var commonModel);
        Assert.DoesNotContain(commonErrors, error => error.Code == "QSEM030");
        var fact = Assert.Single(Assert.IsType<SemanticModel>(commonModel).UnprovenIndexes);
        Assert.Equal(("Main", "xs", "idx()"), (fact.Op, fact.Array, fact.Index));

        var targetError = Assert.Single(OpenQasmBoundsValidation.Run(commonModel!));
        Assert.Equal("QSEM030", targetError.Code);
        Assert.Contains("var i: int = idx();", targetError.Message);
        Assert.True(fact.Span.HasValue);
        Assert.Equal((fact.Span.Value.Start, fact.Span.Value.End), (targetError.Start, targetError.End));
    }

    [Theory]
    [InlineData("half()", "function half(): float { return 0.5; }")]
    [InlineData("0.5", "")]
    [InlineData("true", "")]
    public void RejectsANonIntegerIndexAsACommonTypeError(string index, string declaration)
    {
        var compiled = Compiler.Compile($$"""
            {{declaration}}
            operation Main() {
                var xs: int[] = [10, 20];
                var value: int = xs[{{index}}];
            }
            """);

        Assert.False(compiled.Success);
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("classical integer index"));
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM030");
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
    }

    [Fact]
    public void ValidatesCallsInsideAQubitIndex()
    {
        var badArity = Compiler.Compile("""
            function idx(x: int): int {
                return x;
            }

            operation Main() {
                use q = Qubit[2];
                H(q[idx()]);
            }
            """);
        Assert.Contains(badArity.Errors, error => error.Code == "QSEM006");
        Assert.DoesNotContain(badArity.Errors, error => error.Code == "QSEM030");

        var voidCall = Compiler.Compile("""
            operation pick() {
            }

            operation Main() {
                use q = Qubit[2];
                H(q[pick()]);
            }
            """);
        Assert.Contains(voidCall.Errors, error => error.Code == "QSEM005");
        Assert.DoesNotContain(voidCall.Errors, error => error.Code == "QSEM030");
    }

    [Fact]
    public void AllowsAFunctionCallInAMeasurementIndexUntilTheQasmBoundsPolicy()
    {
        var compiled = Compiler.Compile("""
            function idx(): int {
                return 0;
            }

            operation Main() {
                use q = Qubit[2];
                var measured: bit = M(q[idx()]);
            }
            """);

        Assert.False(compiled.Success);
        Assert.Single(compiled.Errors, error => error.Code == "QSEM030");
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM005");
    }

    [Fact]
    public void ChecksAnIndexedReadNestedInsideAnotherIndex()
    {
        var compiled = Compiler.Compile("""
            function pass(value: int): int {
                return value;
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                var value: int = xs[pass(ys[99])];
            }
            """);

        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
    }

    [Theory]
    [InlineData("xs[ys[99]] = 1;")]
    [InlineData("H(q[ys[99]]);")]
    [InlineData("var measured: bit = M(q[ys[99]]);")]
    public void ChecksNestedIndexesInEveryNonReadContext(string statement)
    {
        var compiled = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[2];
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                {{statement}}
            }
            """);

        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
    }

    [Fact]
    public void CollectsCommonAndOpenQasmBoundsErrorsTogether()
    {
        var compiled = Compiler.Compile("""
            operation Main(n: int) {
                var xs: int[] = [10, 20];
                var value: int = xs[n];
            }
            """);

        Assert.Contains(compiled.Errors, error => error.Code == "QSEM010");
        Assert.Contains(compiled.Errors, error => error.Code == "QSEM030");
    }

    [Fact]
    public void AppliesTheQasmPolicyBeforeAnUncalledGenericCanBeDropped()
    {
        var compiled = Compiler.Compile("""
            operation Dead(q: Qubit[], n: int) {
                H(q[n]);
            }

            operation Main() {
                use q = Qubit[1];
                H(q[0]);
            }
            """);

        Assert.False(compiled.Success);
        Assert.Contains(compiled.Errors, error => error.Code == "QSEM030");
        Assert.Contains(compiled.Semantics!.UnprovenIndexes, fact => fact.Op == "Dead");
    }

    [Fact]
    public void MonomorphizerSpecializesABitArrayFunctionCallInsideAnIndex()
    {
        var compiled = Compiler.Compile("""
            function CountBits(flags: bit[]): int {
                return AsInt(flags);
            }

            operation Main() {
                var flags: bit[] = new bit[2];
                var xs: int[] = [10, 20, 30, 40];
                var value: int = xs[CountBits(flags)];
            }
            """);
        Assert.Contains(compiled.Errors, error => error.Code == "QSEM030");

        var mono = Monomorphizer.Run(Assert.IsType<QProgram>(compiled.Ir));
        var specialization = Assert.Single(mono.Operations,
            operation => operation.DisplayName == "CountBits" && operation.Params[0].RegisterSize == 2);
        var main = Assert.Single(mono.Operations, operation => operation.Name == "Main");
        var value = Assert.Single(main.Body.OfType<QDecl>(), declaration => declaration.Name == "value");
        var indexedRead = Assert.IsType<QIndexNode>(Assert.IsType<QText>(value.Value).Tree);
        var call = Assert.IsType<QCallNode>(indexedRead.Index);

        Assert.Equal(specialization.Id, call.CalleeOpId);
        Assert.Contains("__sz2", call.Name);
    }

    [Fact]
    public void RefusesToRunTheQasmBackendWithoutASemanticModel()
    {
        var backend = QasmBackend.Run(
            new QProgram(Array.Empty<QOperation>()),
            Array.Empty<string>(),
            semantics: null);

        var error = Assert.Single(backend.Errors);
        Assert.Equal("QINTERNAL", error.Code);
        Assert.Empty(backend.Qasm);
    }

    [Fact]
    public void ConvertsASpanlessFactToASpanlessQasmDiagnostic()
    {
        var model = new SemanticModel();
        model.AddUnprovenIndex(new UnprovenIndex("Imported", "xs", "idx()", null, null));

        var error = Assert.Single(OpenQasmBoundsValidation.Run(model));
        Assert.Equal((-1, -1), (error.Start, error.End));
    }
}
