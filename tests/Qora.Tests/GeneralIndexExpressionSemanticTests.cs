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

        Assert.False(compiled.Succeeded);
        Assert.Null(compiled.Targets.OpenQasm);
        var targetDiagnostic = Assert.Single(
            compiled.Diagnostics,
            diagnostic =>
                diagnostic.Stage == CompilationStage.OpenQasm
                && diagnostic.Error.Code == "QSEM030");
        var targetOrigin = Assert.IsType<DiagnosticOrigin.Target>(
            targetDiagnostic.Origin);
        Assert.Equal(TargetBackend.OpenQasm, targetOrigin.Backend);
        Assert.Equal(
            compiled.Hir.AdjointMaterialized!.Id,
            Assert.IsType<TargetDiagnosticInput.Hir>(targetOrigin.Input).Snapshot);
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code is "CE0001" or "QSEM005" or "QSEM007");

        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic =>
                diagnostic.Stage == CompilationStage.HirValidation
                && diagnostic.Error.Code == "QSEM030");
        var commonModel = compiled.Hir.ResolvedValidation!.Model;
        var fact = Assert.Single(commonModel.UnprovenIndexes);
        Assert.Equal(("Main", "xs", "idx()"), (fact.Op, fact.Array, fact.Index));

        var targetError = Assert.Single(OpenQasmBoundsValidation.Run(commonModel));
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

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM016" && error.Message.Contains("classical integer index"));
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
        Assert.Empty(compiled.Hir.ResolvedValidation!.Model.UnprovenIndexes);
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
        Assert.Contains(badArity.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM006");
        Assert.DoesNotContain(badArity.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");

        var voidCall = Compiler.Compile("""
            operation pick() {
            }

            operation Main() {
                use q = Qubit[2];
                H(q[pick()]);
            }
            """);
        Assert.Contains(voidCall.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM005");
        Assert.DoesNotContain(voidCall.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
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

        Assert.False(compiled.Succeeded);
        Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM005");
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

        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
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

        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
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

        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM010");
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
    }

    [Fact]
    public void OpenQasmPolicyReadsTheFinalSpecializedProgramAfterDeadGenericsAreDropped()
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

        Assert.True(compiled.Succeeded);
        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic => diagnostic.Error.Code == "QSEM030");
        Assert.Contains(compiled.Hir.ResolvedValidation!.Model.UnprovenIndexes, fact => fact.Op == "Dead");
        Assert.DoesNotContain(
            compiled.Hir.Specialized!.Program.Operations,
            operation => operation.Name == "Dead");
        Assert.NotNull(compiled.Targets.OpenQasm);
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
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");

        var mono = Monomorphizer.Run(Assert.IsType<QProgram>(compiled.Hir.Resolved!.Program));
        var specialization = Assert.Single(mono.Program.Operations,
            operation => operation.DisplayName == "CountBits" && operation.Params[0].RegisterSize == 2);
        var main = Assert.Single(mono.Program.Operations, operation => operation.Name == "Main");
        var value = Assert.Single(main.Body.OfType<QDecl>(), declaration => declaration.Name == "value");
        var indexedRead = Assert.IsType<QIndexNode>(Assert.IsType<QText>(value.Value).Tree);
        var call = Assert.IsType<QCallNode>(indexedRead.Index);

        Assert.Equal(specialization.Id, call.CalleeOpId);
        Assert.Contains("__sz2", call.Name);
    }

    [Fact]
    public void QasmBackendRequiresAnExplicitSemanticContext()
    {
        Assert.Throws<ArgumentNullException>(() =>
            QasmBackend.Run(
                semantics: null!,
                Array.Empty<string>()));
    }

    [Fact]
    public void ConvertsASpanlessFactToASpanlessQasmDiagnostic()
    {
        var model = new HirSemanticModel();
        model.AddUnprovenIndex(new UnprovenIndex("Imported", "xs", "idx()", null, null));

        var error = Assert.Single(OpenQasmBoundsValidation.Run(model));
        Assert.Equal((-1, -1), (error.Start, error.End));
    }
}
