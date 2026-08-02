using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

/// <summary>
/// Regression coverage for direct symbolic bounds and nested-index diagnostic ownership.
/// These tests intentionally distinguish a malformed inner access from two independently
/// valid-but-unproven accesses.
/// </summary>
public class AdversarialIndexValidationTests
{
    [Fact]
    public void AcceptsTheLastElementOfAnUnknownLengthArrayParameter()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(xs: int[]) {
                var value: int = xs[xs.Count - 1];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        Assert.True(compiled.Succeeded);
        Assert.Empty(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
    }

    [Fact]
    public void ReportsCountAsOneProvenOutOfRangeError()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(xs: int[]) {
                var value: int = xs[xs.Count];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), candidate => candidate.Code == "QSEM030");
    }

    [Fact]
    public void StopsAfterAProvenInvalidInnerIndex()
    {
        var compiled = Compiler.Compile("""
            operation Main() {
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                var value: int = xs[ys[99]];
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
        Assert.Contains("ys[99]", error.Message);
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), candidate => candidate.Code == "QSEM030");
    }

    [Fact]
    public void ReportsOneErrorWhenAssigningThroughAScalarIndex()
    {
        var compiled = Compiler.Compile("""
            operation Main() {
                var value: int = 0;
                value[0] = 1;
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
    }

    [Fact]
    public void PreservesBothIndependentlyUnprovenNestedIndexes()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(xs: int[], ys: int[], n: int) {
                var value: int = xs[ys[n]];
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var ys: int[] = [0, 1];
                Inspect(xs, ys, 0);
            }
            """);

        Assert.False(compiled.Succeeded);
        Assert.Equal(2, compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Count(error => error.Code == "QSEM030"));
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM016");
        var bounds = BoundsResults(compiled)
            .OrderBy(fact => TextAt(compiled, fact.Origin))
            .ToArray();
        Assert.Collection(
            bounds,
            fact =>
            {
                Assert.Equal(MirBoundsClassification.Unproven, fact.Result.Classification);
                Assert.Equal("xs[ys[n]]", TextAt(compiled, fact.Origin));
            },
            fact =>
            {
                Assert.Equal(MirBoundsClassification.Unproven, fact.Result.Classification);
                Assert.Equal("ys[n]", TextAt(compiled, fact.Origin));
            });
        Assert.NotEqual(bounds[0].Origin.HirNodeId, bounds[1].Origin.HirNodeId);
    }

    [Theory]
    [InlineData("ys[99] + 0")]
    [InlineData("0 - ys[99]")]
    [InlineData("pass(ys[99])")]
    public void PropagatesAnInvalidNestedIndexThroughExpressionShapes(string index)
    {
        var compiled = Compiler.Compile($$"""
            function pass(value: int): int {
                return value;
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                var value: int = xs[{{index}}];
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
        Assert.Contains("ys[99]", error.Message);
    }

    [Theory]
    [InlineData("xs[ys[99]] = 1;")]
    [InlineData("H(q[ys[99]]);")]
    [InlineData("var measured: bit = M(q[ys[99]]);")]
    public void SuppressesOnlyTheDerivativeOuterErrorInEveryIndexContext(string statement)
    {
        var compiled = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[2];
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                {{statement}}
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
        Assert.Contains("ys[99]", error.Message);
    }

    [Fact]
    public void PreservesUnprovenNestedIndexesThroughAFunctionArgument()
    {
        var compiled = Compiler.Compile("""
            function pass(value: int): int {
                return value;
            }

            operation Inspect(xs: int[], ys: int[], n: int) {
                var value: int = xs[pass(ys[n])];
            }

            operation Main() {
                var xs: int[] = [10, 20];
                var ys: int[] = [0, 1];
                Inspect(xs, ys, 0);
            }
            """);

        Assert.False(compiled.Succeeded);
        Assert.Equal(2, compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Count(error => error.Code == "QSEM030"));
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM016");
        var bounds = BoundsResults(compiled)
            .OrderBy(fact => TextAt(compiled, fact.Origin))
            .ToArray();
        Assert.Equal(
            ["xs[pass(ys[n])]", "ys[n]"],
            bounds.Select(fact => TextAt(compiled, fact.Origin)).ToArray());
        Assert.All(
            bounds,
            fact => Assert.Equal(
                MirBoundsClassification.Unproven,
                fact.Result.Classification));
    }

    [Fact]
    public void AcceptsAConstAliasOfTheSameArrayLastIndex()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(xs: int[]) {
                const last: int = xs.Count - 1;
                var value: int = xs[last];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        Assert.True(compiled.Succeeded);
        Assert.Empty(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
    }

    [Fact]
    public void DoesNotApplyAnOuterArraysConstProofToAShadowingArray()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(xs: int[]) {
                const last: int = xs.Count - 1;
                if (true) {
                    var xs: int[] = [10];
                    var value: int = xs[last];
                }
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM030", error.Code);
        var fact = Assert.Single(BoundsResults(compiled));
        Assert.Equal(MirBoundsClassification.Unproven, fact.Result.Classification);
        Assert.Equal("xs[last]", TextAt(compiled, fact.Origin));
    }

    [Theory]
    [InlineData("xs.Count * 2 - 1")]
    [InlineData("0 - xs.Count")]
    [InlineData("xs.Count * 3 - 4")]
    [InlineData("3 - xs.Count * 2")]
    [InlineData("xs.Count - 2147483648")]
    [InlineData("4294967294 - xs.Count")]
    public void ReportsEveryUniversallyOutOfRangeCountRelativeIndexAsQsem016(string index)
    {
        var compiled = Compiler.Compile($$"""
            operation Inspect(xs: int[]) {
                var value: int = xs[{{index}}];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
    }

    [Theory]
    [InlineData("xs.Count - 2")]
    [InlineData("xs.Count * 2 - 2")]
    [InlineData("xs.Count * 3 - 5")]
    [InlineData("1 - xs.Count")]
    [InlineData("2 - xs.Count * 2")]
    [InlineData("xs.Count - 2147483647")]
    [InlineData("4294967293 - xs.Count")]
    public void LeavesLengthDependentCountRelativeIndexesUnproven(string index)
    {
        var compiled = Compiler.Compile($$"""
            operation Inspect(xs: int[]) {
                var value: int = xs[{{index}}];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM030", error.Code);
        var fact = Assert.Single(BoundsResults(compiled));
        Assert.Equal(MirBoundsClassification.Unproven, fact.Result.Classification);
        Assert.Equal($"xs[{index}]", TextAt(compiled, fact.Origin));
    }

    [Fact]
    public void DefersASizeDependentDirectIndexUntilQubitSpecialization()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(q: Qubit[]) {
                H(q[q.Count - 2]);
            }

            operation Main() {
                use q = Qubit[3];
                Inspect(q);
            }
            """);

        Assert.True(compiled.Succeeded);
        Assert.Empty(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
    }

    [Fact]
    public void ReportsASizeDependentDirectIndexAfterQubitSpecialization()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(q: Qubit[]) {
                H(q[q.Count - 2]);
            }

            operation Main() {
                use q = Qubit[1];
                Inspect(q);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM016", error.Code);
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), candidate => candidate.Code == "QSEM030");
    }

    [Fact]
    public void DefersACrossRegisterCountIndexUntilBothRegistersAreSpecialized()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(q: Qubit[], r: Qubit[]) {
                H(q[r.Count - 1]);
            }

            operation Main() {
                use q = Qubit[2];
                use r = Qubit[2];
                Inspect(q, r);
            }
            """);

        Assert.True(compiled.Succeeded);
        Assert.Empty(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
    }

    [Theory]
    [InlineData("xs[ys[99] + missing()] = 1;")]
    [InlineData("H(q[ys[99] + missing()]);")]
    [InlineData("var measured: bit = M(q[ys[99] + missing()]);")]
    public void KeepsAnIndependentCallErrorWhenTheNestedIndexIsInvalid(string statement)
    {
        var compiled = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[2];
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                {{statement}}
            }
            """);

        Assert.Equal(2, compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Count);
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM007" && error.Message.Contains("missing"));
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
    }

    [Fact]
    public void KeepsAnIndependentScalarBaseErrorWhenTheNestedIndexIsInvalid()
    {
        var compiled = Compiler.Compile("""
            operation Main() {
                var value: int = 0;
                var ys: int[] = [0];
                value[ys[99]] = 1;
            }
            """);

        Assert.Equal(2, compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Count(error => error.Code == "QSEM016"));
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM016" && error.Message.Contains("value") && error.Message.Contains("scalar"));
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
    }

    [Fact]
    public void KeepsAnIndependentOuterIndexTypeErrorWhenTheNestedIndexIsInvalid()
    {
        var compiled = Compiler.Compile("""
            operation Main() {
                var xs: int[] = [10, 20];
                var ys: int[] = [0];
                var value: int = xs[ys[99] + 0.5];
            }
            """);

        Assert.Equal(2, compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Count(error => error.Code == "QSEM016"));
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
        Assert.Contains(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(),
            error => error.Code == "QSEM016" && error.Message.Contains("classical integer index"));
        Assert.DoesNotContain(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM030");
    }

    [Theory]
    [InlineData("xs.Count + 9223372036854775807 - 9223372036854775807 - 1")]
    [InlineData("0 - xs.Count - 9223372036854775807 + 9223372036854775807 + xs.Count + xs.Count - 1")]
    [InlineData("xs.Count * 9223372036854775807 - xs.Count * 9223372036854775806 - 1")]
    public void DoesNotProveASymbolicIndexWhoseIntermediateValueOverflows(string index)
    {
        var compiled = Compiler.Compile($$"""
            operation Inspect(xs: int[]) {
                var value: int = xs[{{index}}];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM030", error.Code);
        var fact = Assert.Single(BoundsResults(compiled));
        Assert.Equal(MirBoundsClassification.Unproven, fact.Result.Classification);
        Assert.Equal($"xs[{index}]", TextAt(compiled, fact.Origin));
        Assert.Null(compiled.Targets.OpenQasm);
    }

    [Fact]
    public void PreservesSymbolicOverflowStateThroughAConstAlias()
    {
        var compiled = Compiler.Compile("""
            operation Inspect(xs: int[]) {
                const last: int =
                    xs.Count + 9223372036854775807 - 9223372036854775807 - 1;
                var value: int = xs[last];
            }

            operation Main() {
                var values: int[] = [10, 20];
                Inspect(values);
            }
            """);

        var error = Assert.Single(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.Equal("QSEM030", error.Code);
        var fact = Assert.Single(BoundsResults(compiled));
        Assert.Equal(MirBoundsClassification.Unproven, fact.Result.Classification);
        Assert.Equal("xs[last]", TextAt(compiled, fact.Origin));
        Assert.Null(compiled.Targets.OpenQasm);
    }

    [Fact]
    public void TreatsAnIndexInsideAContradictoryConjunctionAsUnreachable()
    {
        var compiled = Compiler.Compile("""
            function chooseIndex(): int {
                return 0;
            }

            operation Main() {
                var values: int[] = [10];
                var index: int = chooseIndex();
                if (index < 0 && index >= 0) {
                    var invalid: int = 9;
                    var value: int = values[invalid];
                }
            }
            """);

        Assert.True(compiled.Succeeded);
        Assert.Empty(compiled.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        var fact = Assert.Single(BoundsResults(compiled));
        Assert.Equal(MirBoundsClassification.Proven, fact.Result.Classification);
    }

    [Fact]
    public void DoesNotTreatANarrowButFeasibleConjunctionAsUnreachable()
    {
        var compiled = Compiler.Compile("""
            function chooseIndex(): int {
                return 0;
            }

            operation Main() {
                var values: int[] = [10];
                var index: int = chooseIndex();
                if (index < 1 && index >= 0) {
                    var invalid: int = 9;
                    var value: int = values[invalid];
                }
            }
            """);

        var error = Assert.Single(
            compiled.Diagnostics.Select(diagnostic => diagnostic.Error),
            candidate => candidate.Code == "QSEM016");
        Assert.Equal("QSEM016", error.Code);
        var fact = Assert.Single(BoundsResults(compiled));
        Assert.Equal(MirBoundsClassification.Invalid, fact.Result.Classification);
    }

    [Fact]
    public void ReportsOneUnprovenDiagnosticForAllSizeSpecializationsOfOneSourceAccess()
    {
        var compiled = Compiler.Compile("""
            function chooseIndex(): int {
                return 0;
            }

            operation Work(q: Qubit[]) {
                H(q[chooseIndex()]);
            }

            operation Main() {
                use one = Qubit[1];
                use two = Qubit[2];
                Work(one);
                Work(two);
            }
            """);

        var errors = compiled.Diagnostics
            .Select(diagnostic => diagnostic.Error)
            .Where(error => error.Code == "QSEM030")
            .ToArray();
        Assert.Single(errors);
        Assert.Equal(
            2,
            BoundsResults(compiled).Count(fact =>
                fact.Result.Classification == MirBoundsClassification.Unproven));
    }

    [Fact]
    public void ReportsOneInvalidDiagnosticForAllSizeSpecializationsOfOneSourceAccess()
    {
        var compiled = Compiler.Compile("""
            operation Work(q: Qubit[]) {
                var index: int = 5;
                H(q[index]);
            }

            operation Main() {
                use one = Qubit[1];
                use two = Qubit[2];
                Work(one);
                Work(two);
            }
            """);

        var errors = compiled.Diagnostics
            .Select(diagnostic => diagnostic.Error)
            .Where(error => error.Code == "QSEM016")
            .ToArray();
        Assert.Single(errors);
        Assert.Equal(
            2,
            BoundsResults(compiled).Count(fact =>
                fact.Result.Classification == MirBoundsClassification.Invalid));
    }

    private static IReadOnlyList<(MirBoundsResult Result, MirHirOrigin Origin)> BoundsResults(
        Compilation compilation)
    {
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var results = new List<(MirBoundsResult Result, MirHirOrigin Origin)>();
        foreach (var callable in mir.Program.Callables)
        {
            var bounds = mir.Analyses.Bounds(callable);
            foreach (var result in bounds.Results)
            {
                results.Add((
                    result,
                    bounds.OriginFor(result).SourceHirOrigin));
            }
        }

        return results;
    }

    private static string TextAt(
        Compilation compilation,
        MirHirOrigin origin)
    {
        var span = Assert.IsType<SourceSpan>(origin.Span);
        var document = Assert.Single(
            compilation.Sources.Documents,
            candidate => candidate.Ref == span.Document);
        return document.Text[span.Start..span.End];
    }
}
