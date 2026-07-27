using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// Parameter access is explicit at the call boundary. A classical array parameter is read-only unless
/// declared <c>var</c>; an <c>var</c> argument must be marked at the call site as well. This keeps
/// ordinary calls freely shareable while making every caller-visible mutation visible in source.
/// QSEM038 owns mode-contract violations, QSEM024 still owns const mutation, and QSEM014 still owns
/// overlapping access to one mutable storage location.
/// </summary>
public class MutableParameterTests
{
    [Fact]
    public void OrdinaryClassicalArrayParametersEmitReadonlyAndMayShareOneArgument()
    {
        var result = Compiler.Compile("""
            operation Compare(left: int[], right: int[]) {}

            operation Main() {
                var values: int[] = [1, 2];
                Compare(values, values);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains(
            "def Compare(readonly array[int, #dim = 1] left, readonly array[int, #dim = 1] right) {",
            result.Targets.OpenQasm!.Text);
        Assert.Contains("Compare(values, values);", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void ConstArrayMayBePassedToAnOrdinaryReadonlyParameter()
    {
        Compiler.Accepts("""
            operation Inspect(values: int[]) {}

            operation Main() {
                const values: int[] = [1, 2];
                Inspect(values);
            }
            """);
    }

    [Fact]
    public void MutableClassicalArrayParameterMayMutateAndEmitsMutable()
    {
        var result = Compiler.Compile("""
            operation Clear(var values: int[]) {
                values[0] = 0;
            }

            operation Main() {
                var values: int[] = [1, 2];
                Clear(var values);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def Clear(mutable array[int, #dim = 1] values) {", result.Targets.OpenQasm!.Text);
        Assert.Contains("Clear(values);", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("var values", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void ReadonlyAndMutableParametersKeepTheirDistinctQasmModes()
    {
        var result = Compiler.Compile("""
            operation CopyFirst(source: float[], var destination: float[]) {
                destination[0] = source[0];
            }

            operation Main() {
                var source: float[] = [0.5];
                var destination: float[] = [0.0];
                CopyFirst(source, var destination);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains(
            "def CopyFirst(readonly array[float, #dim = 1] source, mutable array[float, #dim = 1] destination) {",
            result.Targets.OpenQasm!.Text);
        Assert.Contains("CopyFirst(source, destination);", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void QualifiedMutableCallSurvivesNamespaceResolution()
    {
        var result = Compiler.Compile("""
            namespace Buffers {
                operation Clear(var values: int[]) {
                    values[0] = 0;
                }
            }

            operation Main() {
                var values: int[] = [1];
                Buffers.Clear(var values);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def Buffers_Clear(mutable array[int, #dim = 1] values) {", result.Targets.OpenQasm!.Text);
        Assert.Contains("Buffers_Clear(values);", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void FunctionArrayParameterIsReadonlyAndEmitsReadonly()
    {
        var result = Compiler.Compile("""
            function First(values: int[]): int {
                return values[0];
            }

            operation Main() {
                var values: int[] = [4, 5];
                var first: int = First(values);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def First(readonly array[int, #dim = 1] values) -> int {", result.Targets.OpenQasm!.Text);
    }

    [Theory]
    [InlineData("""
        operation Rewrite(values: int[]) {
            values[0] = 1;
        }
        operation Main() {
            var values: int[] = [0];
            Rewrite(values);
        }
        """)]
    [InlineData("""
        function Rewrite(values: int[]): int {
            values[0] = 1;
            return values[0];
        }
        operation Main() {
            var values: int[] = [0];
            var result: int = Rewrite(values);
        }
        """)]
    public void RejectsWritingThroughAnOrdinaryArrayParameter(string source) =>
        Compiler.RejectsExactly(source, "QSEM038");

    [Fact]
    public void RejectsForwardingReadonlyAccessAsMutable()
    {
        Compiler.RejectsExactly(
            """
            operation Clear(var values: int[]) {}

            operation Forward(values: int[]) {
                Clear(var values);
            }

            operation Main() {
                var values: int[] = [1];
                Forward(values);
            }
            """,
            "QSEM038");
    }

    [Fact]
    public void AShadowingLocalKeepsItsOwnMutablePermission()
    {
        Compiler.Accepts("""
            operation Clear(var values: int[]) {
                values[0] = 0;
            }

            operation Work(values: int[]) {
                if (1 == 1) {
                    var values: int[] = [1];
                    values[0] = 2;
                    Clear(var values);
                }
            }

            operation Main() {
                const values: int[] = [3];
                Work(values);
            }
            """);
    }

    [Theory]
    [InlineData("""
        operation Clear(var values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Clear(values);
        }
        """)]
    [InlineData("""
        operation Inspect(values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Inspect(var values);
        }
        """)]
    public void RejectsAnMutableModeMismatchAtTheCallSite(string source) =>
        Compiler.RejectsExactly(source, "QSEM038");

    [Fact]
    public void ConstArrayCannotBePassedAsMutable()
    {
        Compiler.RejectsExactly(
            """
            operation Consume(var values: int[]) {}

            operation Main() {
                const values: int[] = [1];
                Consume(var values);
            }
            """,
            "QSEM024");
    }

    [Theory]
    [InlineData("""
        operation Pair(var left: int[], var right: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Pair(var values, var values);
        }
        """)]
    [InlineData("""
        operation Pair(readonlyValues: int[], var writableValues: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Pair(values, var values);
        }
        """)]
    public void RejectsOverlappingAccessWhenAnyParameterIsMutable(string source) =>
        Compiler.RejectsExactly(source, "QSEM014");

    [Fact]
    public void DistinctMutableArgumentsDoNotOverlap()
    {
        Compiler.Accepts("""
            operation Pair(var left: angle[], var right: angle[]) {}

            operation Main() {
                var left: angle[] = [0.5];
                var right: angle[] = [1.0];
                Pair(var left, var right);
            }
            """);
    }

    [Fact]
    public void ATypeMismatchDoesNotCascadeIntoAnAliasDiagnostic()
    {
        Compiler.RejectsExactly(
            """
            operation Pair(var integers: int[], floats: float[]) {}

            operation Main() {
                var values: int[] = [1];
                Pair(var values, values);
            }
            """,
            "QSEM006");
    }

    [Fact]
    public void FunctionCannotDeclareMutable()
    {
        Compiler.RejectsExactly(
            """
            function Bad(var values: int[]): int {
                return 0;
            }
            operation Main() {}
            """,
            "QSEM038");
    }

    [Fact]
    public void MutableBelongsBeforeTheParameterNameRatherThanInsideItsType()
    {
        Compiler.RejectsExactly(
            "operation Bad(values: var int[]) {}\noperation Main() {}",
            "CE0001");
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bit")]
    [InlineData("float")]
    [InlineData("angle")]
    public void ScalarMutableIsNotSupported(string type)
    {
        Compiler.RejectsExactly(
            $"operation Bad(var value: {type}) {{}}\noperation Main() {{}}",
            "QSEM038");
    }

    [Theory]
    [InlineData("bit[]")]
    [InlineData("Qubit")]
    [InlineData("Qubit[]")]
    public void BackendUnsupportedMutableShapesAreRejected(string type)
    {
        Compiler.RejectsExactly(
            $"operation Bad(var value: {type}) {{}}\noperation Main() {{}}",
            "QSEM038");
    }

    [Fact]
    public void QubitParametersKeepTheirExistingImplicitMutationSemantics()
    {
        Compiler.Accepts("""
            operation Flip(register: Qubit[]) {
                X(register[0]);
            }

            operation Main() {
                use register = Qubit[1];
                Flip(register);
            }
            """);
    }

    [Fact]
    public void QubitArraySpecializationPreservesAnIndependentMutableParameter()
    {
        var result = Compiler.Compile("""
            operation Touch(qubits: Qubit[], var values: int[]) {
                for i in 0..qubits.Count-1 {
                    X(qubits[i]);
                }
                values[0] = values[0] + 1;
            }

            operation Main() {
                use qubits = Qubit[2];
                var values: int[] = [0];
                Touch(qubits, var values);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        var specialization = Assert.Single(
            result.Hir.EffectAnalysis!.Program!.Operations,
            operation => operation.DisplayName == "Touch");
        Assert.Equal(2, specialization.Params[0].RegisterSize);
        Assert.Equal(QOwnershipMode.Borrowed, specialization.Params[1].Ownership);
        Assert.Equal(QAccessMode.Mutable, specialization.Params[1].Access);
        Assert.Contains(
            "def Touch__sz2(qubit[2] qubits, mutable array[int, #dim = 1] values) {",
            result.Targets.OpenQasm!.Text);
        Assert.Contains("Touch__sz2(qubits, values);", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void BitArraySpecializationPreservesAnIndependentMutableParameter()
    {
        var result = Compiler.Compile("""
            operation CountInto(flags: bit[], var counts: int[]) {
                if (flags[0] == 1) {
                    counts[0] = counts[0] + 1;
                }
            }

            operation Main() {
                var flags: bit[] = new bit[3];
                var counts: int[] = [0];
                CountInto(flags, var counts);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        var specialization = Assert.Single(
            result.Hir.EffectAnalysis!.Program!.Operations,
            operation => operation.DisplayName == "CountInto");
        Assert.Equal(3, specialization.Params[0].RegisterSize);
        Assert.Equal(QOwnershipMode.Borrowed, specialization.Params[1].Ownership);
        Assert.Equal(QAccessMode.Mutable, specialization.Params[1].Access);
        Assert.Contains(
            "def CountInto__sz3(bit[3] flags, mutable array[int, #dim = 1] counts) {",
            result.Targets.OpenQasm!.Text);
        Assert.Contains("CountInto__sz3(flags, counts);", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void CollisionDrivenOperationRenamePreservesTheMutableCallContract()
    {
        var result = Compiler.Compile("""
            operation A_B() {}

            namespace A {
                operation B(var values: int[]) {
                    values[0] = 0;
                }
            }

            operation Main() {
                var values: int[] = [1];
                A.B(var values);
                A_B();
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def A_B() {", result.Targets.OpenQasm!.Text);
        Assert.Contains("def A_B_(mutable array[int, #dim = 1] values) {", result.Targets.OpenQasm!.Text);
        Assert.Contains("A_B_(values);", result.Targets.OpenQasm!.Text);
        Assert.Contains("A_B();", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void FunctionArrayLocalHoistsAsASyntheticMutableParameter()
    {
        var result = Compiler.Compile("""
            function LocalValue(): int {
                var values: int[] = [1, 2];
                values[0] = values[0] + 1;
                return values[0];
            }

            operation Main() {
                var result: int = LocalValue();
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains(
            "def LocalValue(mutable array[int, #dim = 1] values) -> int {",
            result.Targets.OpenQasm!.Text);
        Assert.Contains("int result = LocalValue(LocalValue_values);", result.Targets.OpenQasm!.Text);
    }

    [Theory]
    [InlineData("""
        operation Main() {
            use qubits = Qubit[1];
            H(var qubits);
        }
        """)]
    [InlineData("""
        operation Take(value: int) {}
        operation Main() {
            var value: int = 1;
            Take(var value);
        }
        """)]
    [InlineData("""
        operation Rewrite(var values: int[]) {}
        operation Main() {
            var value: int = 1;
            Rewrite(var value);
        }
        """)]
    [InlineData("""
        operation Rewrite(var values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Rewrite(var values[0]);
        }
        """)]
    [InlineData("""
        operation Take(value: int) {}
        operation Main() {
            var value: int = 1;
            Take(var value + 1);
        }
        """)]
    public void MutableMarkerOnANonBorrowableArgumentIsQsem038(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.All(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => Assert.Equal("QSEM038", error.Code));
    }

    [Fact]
    public void ReadonlyFunctionArrayParameterRemainsReadonlyThroughExpressionCalls()
    {
        var result = Compiler.Compile("""
            function First(values: int[]): int {
                return values[0];
            }

            function IncrementFirst(values: int[]): int {
                return First(values) + 1;
            }

            operation Main() {
                var values: int[] = [4, 5];
                var answer: int = IncrementFirst(values) + First(values);
            }
            """);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def First(readonly array[int, #dim = 1] values) -> int {", result.Targets.OpenQasm!.Text);
        Assert.Contains(
            "def IncrementFirst(readonly array[int, #dim = 1] values) -> int {",
            result.Targets.OpenQasm!.Text);
        Assert.Contains("First(values) + 1", result.Targets.OpenQasm!.Text);
        Assert.Contains("IncrementFirst(values) + First(values)", result.Targets.OpenQasm!.Text);
    }
}
