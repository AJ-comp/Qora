using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Semantic, MIR-analysis, and target-policy coverage for full index expressions. Parsing/lowering shape
/// is covered separately; these tests pin the boundary between a failed MIR proof and OpenQASM's QSEM030
/// policy.
/// </summary>
public class GeneralIndexExpressionSemanticTests
{
    [Fact]
    public void MirAnalysisRecordsTheFactAndOpenQasmDerivesQsem030()
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
        Assert.Same(compiled.Mir, targetOrigin.Input);
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code is "CE0001" or "QSEM005" or "QSEM007");

        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic =>
                diagnostic.Stage == CompilationStage.HirValidation
                && diagnostic.Error.Code == "QSEM030");
        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic => diagnostic.Stage == CompilationStage.MirAnalysis);
        var mir = Assert.IsType<MirSnapshot>(compiled.Mir);
        var main = Assert.Single(
            mir.Program.Callables,
            callable => callable.Name == "Main");
        var bounds = mir.Analyses.Bounds(main);
        var fact = Assert.Single(bounds.Results);
        Assert.Equal(MirBoundsClassification.Unproven, fact.Classification);
        var mirOrigin = bounds.OriginFor(fact);
        var factOrigin = mirOrigin.SourceHirOrigin;
        var factSpan = Assert.IsType<SourceSpan>(factOrigin.Span);
        var sourceDocument = Assert.Single(
            compiled.Sources.Documents,
            document => document.Ref == factSpan.Document);
        Assert.Equal("xs[idx()]", sourceDocument.Text[factSpan.Start..factSpan.End]);
        Assert.Same(mirOrigin, targetOrigin.Location);

        var targetError = targetDiagnostic.Error;
        Assert.Equal("QSEM030", targetError.Code);
        Assert.Contains("cannot be proven in bounds", targetError.Message);
        Assert.Equal((factSpan.Start, factSpan.End), (targetError.Start, targetError.End));
    }

    [Fact]
    public void OpenQasmUsesMirSsaToProveAStaticVariableIndex()
    {
        var compiled = Compiler.Compile("""
            operation Main() {
                var xs: int[] = [10, 20];
                var i: int = 1;
                var value: int = xs[i];
            }
            """);

        Assert.True(
            compiled.Succeeded,
            string.Join(
                " | ",
                compiled.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        var mir = Assert.IsType<MirSnapshot>(compiled.Mir);
        var main = Assert.Single(
            mir.Program.Callables,
            callable => callable.Name == "Main");
        var bounds = mir.Analyses.Bounds(main);
        var result = Assert.Single(bounds.Results);

        Assert.Equal(MirBoundsClassification.Proven, result.Classification);
        Assert.Equal(2, result.KnownLength);
        Assert.Equal("xs[i]", TextAt(compiled, bounds.OriginFor(result)));
        Assert.NotNull(compiled.Targets.OpenQasm);
    }

    [Fact]
    public void StaticVariableOutsideTheArrayIsRejectedByMirAnalysisBeforeOpenQasm()
    {
        var compiled = Compiler.Compile("""
            operation Main() {
                var xs: int[] = [10, 20];
                var i: int = 5;
                var value: int = xs[i];
            }
            """);

        Assert.False(compiled.Succeeded);
        Assert.Null(compiled.Targets.OpenQasm);
        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic => diagnostic.Stage == CompilationStage.OpenQasm);
        var diagnostic = Assert.Single(compiled.Diagnostics);
        Assert.Equal(CompilationStage.MirAnalysis, diagnostic.Stage);
        Assert.Equal("QSEM016", diagnostic.Error.Code);
        var diagnosticOrigin = Assert.IsType<DiagnosticOrigin.Mir>(diagnostic.Origin);
        var mir = Assert.IsType<MirSnapshot>(compiled.Mir);
        Assert.Same(mir, diagnosticOrigin.Snapshot);
        var main = Assert.Single(
            mir.Program.Callables,
            callable => callable.Name == "Main");
        var bounds = mir.Analyses.Bounds(main);
        var result = Assert.Single(bounds.Results);
        var origin = bounds.OriginFor(result);

        Assert.Equal(MirBoundsClassification.Invalid, result.Classification);
        Assert.Equal(2, result.KnownLength);
        Assert.Same(origin, diagnosticOrigin.Location);
        Assert.Equal("xs[i]", TextAt(compiled, origin));
        var sourceOrigin = origin.SourceHirOrigin;
        var span = Assert.IsType<SourceSpan>(sourceOrigin.Span);
        Assert.Equal((span.Start, span.End), (diagnostic.Error.Start, diagnostic.Error.End));
    }

    [Fact]
    public void MirOnlyKeepsARuntimeIndexAsUnprovenWithoutATargetDiagnostic()
    {
        var compiled = QoraCompiler.Compile(
            """
            function idx(): int {
                return 0;
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var value: int = xs[idx()];
            }
            """,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));

        Assert.True(
            compiled.Succeeded,
            string.Join(
                " | ",
                compiled.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(compiled.Targets.Artifacts);
        var mir = Assert.IsType<MirSnapshot>(compiled.Mir);
        var main = Assert.Single(
            mir.Program.Callables,
            callable => callable.Name == "Main");
        var bounds = mir.Analyses.Bounds(main);
        var result = Assert.Single(bounds.Results);

        Assert.Equal(MirBoundsClassification.Unproven, result.Classification);
        Assert.Equal(2, result.KnownLength);
        Assert.Equal("xs[idx()]", TextAt(compiled, bounds.OriginFor(result)));
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
    public void InvalidHirStopsBeforeAnyTargetPolicyRuns()
    {
        var compiled = Compiler.Compile("""
            operation Main(n: int) {
                var xs: int[] = [10, 20];
                var value: int = xs[n];
            }
            """);

        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM010");
        Assert.DoesNotContain(
            compiled.Diagnostics,
            diagnostic => diagnostic.Stage == CompilationStage.OpenQasm);
        Assert.DoesNotContain(
            compiled.Diagnostics.Select(diagnostic => diagnostic.Error),
            error => error.Code == "QSEM030");
        Assert.Null(compiled.Mir);
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
        Assert.DoesNotContain(
            compiled.Hir.Specialized!.Program.Callables,
            operation => operation.Name == "Dead");
        var mir = Assert.IsType<MirSnapshot>(compiled.Mir);
        Assert.DoesNotContain(
            mir.Program.Callables,
            callable => callable.Name == "Dead");
        Assert.DoesNotContain(
            mir.Program.Callables.SelectMany(
                callable => mir.Analyses.Bounds(callable).Results),
            result => result.Classification == MirBoundsClassification.Unproven);
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

        var mono = HirTestFactory.Monomorphize(
            Assert.IsType<HirSnapshot>(
                compiled.Hir.Resolved));
        var specialization = Assert.Single(mono.Program.Callables,
            operation => operation.DisplayName == "CountBits" && operation.Parameters[0].RegisterSize == 2);
        var main = Assert.Single(mono.Program.Callables, operation => operation.Name == "Main");
        var value = Assert.Single(
            main.Body.OfType<HirVariableDeclarationStatement>(),
            declaration => declaration.Name == "value");
        var indexedRead = Assert.IsType<HirIndexExpression>(
            value.Value);
        var call = Assert.IsType<HirCallExpression>(
            indexedRead.Index);

        Assert.Equal(specialization.Id, call.CalleeId);
        Assert.Contains("__sz2", HirExpressions.QualifiedNameOf(call.Callee));
    }

    [Fact]
    public void QasmBackendRequiresAnExactMirSnapshot()
    {
        Assert.Throws<ArgumentNullException>(() =>
            QasmBackend.Run(null!));
    }

    private static string TextAt(
        Compilation compilation,
        MirOrigin origin)
    {
        var span = Assert.IsType<SourceSpan>(origin.SourceHirOrigin.Span);
        var document = Assert.Single(
            compilation.Sources.Documents,
            candidate => candidate.Ref == span.Document);
        return document.Text[span.Start..span.End];
    }

}
