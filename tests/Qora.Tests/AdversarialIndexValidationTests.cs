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

        Assert.True(compiled.Success);
        Assert.Empty(compiled.Errors);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM016", error.Code);
        Assert.DoesNotContain(compiled.Errors, candidate => candidate.Code == "QSEM030");
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM016", error.Code);
        Assert.Contains("ys[99]", error.Message);
        Assert.DoesNotContain(compiled.Errors, candidate => candidate.Code == "QSEM030");
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        var error = Assert.Single(compiled.Errors);
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

        Assert.False(compiled.Success);
        Assert.Equal(2, compiled.Errors.Count(error => error.Code == "QSEM030"));
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM016");
        Assert.Collection(
            compiled.Semantics!.UnprovenIndexes.OrderBy(fact => fact.Array),
            fact => Assert.Equal(("xs", "ys [ n ]"), (fact.Array, fact.Index)),
            fact => Assert.Equal(("ys", "n"), (fact.Array, fact.Index)));
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM016", error.Code);
        Assert.Contains("ys[99]", error.Message);
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM016", error.Code);
        Assert.Contains("ys[99]", error.Message);
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        Assert.False(compiled.Success);
        Assert.Equal(2, compiled.Errors.Count(error => error.Code == "QSEM030"));
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM016");
        Assert.Equal(
            ["xs", "ys"],
            compiled.Semantics!.UnprovenIndexes.Select(fact => fact.Array).Order().ToArray());
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

        Assert.True(compiled.Success);
        Assert.Empty(compiled.Errors);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM030", error.Code);
        Assert.Single(compiled.Semantics!.UnprovenIndexes);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM016", error.Code);
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM030", error.Code);
        Assert.Single(compiled.Semantics!.UnprovenIndexes);
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

        Assert.True(compiled.Success);
        Assert.Empty(compiled.Errors);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM016", error.Code);
        Assert.DoesNotContain(compiled.Errors, candidate => candidate.Code == "QSEM030");
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

        Assert.True(compiled.Success);
        Assert.Empty(compiled.Errors);
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

        Assert.Equal(2, compiled.Errors.Count);
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM007" && error.Message.Contains("missing"));
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM030");
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        Assert.Equal(2, compiled.Errors.Count(error => error.Code == "QSEM016"));
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("value") && error.Message.Contains("scalar"));
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM030");
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        Assert.Equal(2, compiled.Errors.Count(error => error.Code == "QSEM016"));
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("ys[99]"));
        Assert.Contains(compiled.Errors,
            error => error.Code == "QSEM016" && error.Message.Contains("classical integer index"));
        Assert.DoesNotContain(compiled.Errors, error => error.Code == "QSEM030");
        Assert.Empty(compiled.Semantics!.UnprovenIndexes);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM030", error.Code);
        Assert.Single(compiled.Semantics!.UnprovenIndexes);
        Assert.Empty(compiled.Qasm);
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

        var error = Assert.Single(compiled.Errors);
        Assert.Equal("QSEM030", error.Code);
        Assert.Single(compiled.Semantics!.UnprovenIndexes);
        Assert.Empty(compiled.Qasm);
    }
}
