using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// Parameter access is explicit at the call boundary. A classical array parameter is read-only unless
/// declared <c>inout</c>; an <c>inout</c> argument must be marked at the call site as well. This keeps
/// ordinary calls freely shareable while making every caller-visible mutation visible in source.
/// QSEM038 owns mode-contract violations, QSEM024 still owns const mutation, and QSEM014 still owns
/// overlapping access to one mutable storage location.
/// </summary>
public class InOutParameterTests
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

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains(
            "def Compare(readonly array[int, #dim = 1] left, readonly array[int, #dim = 1] right) {",
            result.Qasm);
        Assert.Contains("Compare(values, values);", result.Qasm);
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
    public void InOutClassicalArrayParameterMayMutateAndEmitsMutable()
    {
        var result = Compiler.Compile("""
            operation Clear(inout values: int[]) {
                values[0] = 0;
            }

            operation Main() {
                var values: int[] = [1, 2];
                Clear(inout values);
            }
            """);

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def Clear(mutable array[int, #dim = 1] values) {", result.Qasm);
        Assert.Contains("Clear(values);", result.Qasm);
        Assert.DoesNotContain("inout values", result.Qasm);
    }

    [Fact]
    public void ReadonlyAndInOutParametersKeepTheirDistinctQasmModes()
    {
        var result = Compiler.Compile("""
            operation CopyFirst(source: float[], inout destination: float[]) {
                destination[0] = source[0];
            }

            operation Main() {
                var source: float[] = [0.5];
                var destination: float[] = [0.0];
                CopyFirst(source, inout destination);
            }
            """);

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains(
            "def CopyFirst(readonly array[float, #dim = 1] source, mutable array[float, #dim = 1] destination) {",
            result.Qasm);
        Assert.Contains("CopyFirst(source, destination);", result.Qasm);
    }

    [Fact]
    public void QualifiedInOutCallSurvivesNamespaceResolution()
    {
        var result = Compiler.Compile("""
            namespace Buffers {
                operation Clear(inout values: int[]) {
                    values[0] = 0;
                }
            }

            operation Main() {
                var values: int[] = [1];
                Buffers.Clear(inout values);
            }
            """);

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def Buffers_Clear(mutable array[int, #dim = 1] values) {", result.Qasm);
        Assert.Contains("Buffers_Clear(values);", result.Qasm);
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

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def First(readonly array[int, #dim = 1] values) -> int {", result.Qasm);
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
    public void RejectsForwardingReadonlyAccessAsInOut()
    {
        Compiler.RejectsExactly(
            """
            operation Clear(inout values: int[]) {}

            operation Forward(values: int[]) {
                Clear(inout values);
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
            operation Clear(inout values: int[]) {
                values[0] = 0;
            }

            operation Work(values: int[]) {
                if (1 == 1) {
                    var values: int[] = [1];
                    values[0] = 2;
                    Clear(inout values);
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
        operation Clear(inout values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Clear(values);
        }
        """)]
    [InlineData("""
        operation Inspect(values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Inspect(inout values);
        }
        """)]
    public void RejectsAnInOutModeMismatchAtTheCallSite(string source) =>
        Compiler.RejectsExactly(source, "QSEM038");

    [Fact]
    public void ConstArrayCannotBePassedAsInOut()
    {
        Compiler.RejectsExactly(
            """
            operation Consume(inout values: int[]) {}

            operation Main() {
                const values: int[] = [1];
                Consume(inout values);
            }
            """,
            "QSEM024");
    }

    [Theory]
    [InlineData("""
        operation Pair(inout left: int[], inout right: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Pair(inout values, inout values);
        }
        """)]
    [InlineData("""
        operation Pair(readonlyValues: int[], inout writableValues: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Pair(values, inout values);
        }
        """)]
    public void RejectsOverlappingAccessWhenAnyParameterIsInOut(string source) =>
        Compiler.RejectsExactly(source, "QSEM014");

    [Fact]
    public void DistinctInOutArgumentsDoNotOverlap()
    {
        Compiler.Accepts("""
            operation Pair(inout left: angle[], inout right: angle[]) {}

            operation Main() {
                var left: angle[] = [0.5];
                var right: angle[] = [1.0];
                Pair(inout left, inout right);
            }
            """);
    }

    [Fact]
    public void ATypeMismatchDoesNotCascadeIntoAnAliasDiagnostic()
    {
        Compiler.RejectsExactly(
            """
            operation Pair(inout integers: int[], floats: float[]) {}

            operation Main() {
                var values: int[] = [1];
                Pair(inout values, values);
            }
            """,
            "QSEM006");
    }

    [Fact]
    public void FunctionCannotDeclareInOut()
    {
        Compiler.RejectsExactly(
            """
            function Bad(inout values: int[]): int {
                return 0;
            }
            operation Main() {}
            """,
            "QSEM038");
    }

    [Fact]
    public void InOutBelongsBeforeTheParameterNameRatherThanInsideItsType()
    {
        Compiler.RejectsExactly(
            "operation Bad(values: inout int[]) {}\noperation Main() {}",
            "CE0001");
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bit")]
    [InlineData("float")]
    [InlineData("angle")]
    public void ScalarInOutIsNotSupported(string type)
    {
        Compiler.RejectsExactly(
            $"operation Bad(inout value: {type}) {{}}\noperation Main() {{}}",
            "QSEM038");
    }

    [Theory]
    [InlineData("bit[]")]
    [InlineData("Qubit")]
    [InlineData("Qubit[]")]
    public void BackendUnsupportedInOutShapesAreRejected(string type)
    {
        Compiler.RejectsExactly(
            $"operation Bad(inout value: {type}) {{}}\noperation Main() {{}}",
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
    public void QubitArraySpecializationPreservesAnIndependentInOutParameter()
    {
        var result = Compiler.Compile("""
            operation Touch(qubits: Qubit[], inout values: int[]) {
                for i in 0..qubits.Count-1 {
                    X(qubits[i]);
                }
                values[0] = values[0] + 1;
            }

            operation Main() {
                use qubits = Qubit[2];
                var values: int[] = [0];
                Touch(qubits, inout values);
            }
            """);

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        var specialization = Assert.Single(
            result.AnalyzedIr!.Operations,
            operation => operation.DisplayName == "Touch");
        Assert.Equal(2, specialization.Params[0].RegisterSize);
        Assert.Equal(QOwnershipMode.Borrowed, specialization.Params[1].Ownership);
        Assert.Equal(QAccessMode.Mutable, specialization.Params[1].Access);
        Assert.Contains(
            "def Touch__sz2(qubit[2] qubits, mutable array[int, #dim = 1] values) {",
            result.Qasm);
        Assert.Contains("Touch__sz2(qubits, values);", result.Qasm);
    }

    [Fact]
    public void BitArraySpecializationPreservesAnIndependentInOutParameter()
    {
        var result = Compiler.Compile("""
            operation CountInto(flags: bit[], inout counts: int[]) {
                if (flags[0] == 1) {
                    counts[0] = counts[0] + 1;
                }
            }

            operation Main() {
                var flags: bit[] = new bit[3];
                var counts: int[] = [0];
                CountInto(flags, inout counts);
            }
            """);

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        var specialization = Assert.Single(
            result.AnalyzedIr!.Operations,
            operation => operation.DisplayName == "CountInto");
        Assert.Equal(3, specialization.Params[0].RegisterSize);
        Assert.Equal(QOwnershipMode.Borrowed, specialization.Params[1].Ownership);
        Assert.Equal(QAccessMode.Mutable, specialization.Params[1].Access);
        Assert.Contains(
            "def CountInto__sz3(bit[3] flags, mutable array[int, #dim = 1] counts) {",
            result.Qasm);
        Assert.Contains("CountInto__sz3(flags, counts);", result.Qasm);
    }

    [Fact]
    public void CollisionDrivenOperationRenamePreservesTheInOutCallContract()
    {
        var result = Compiler.Compile("""
            operation A_B() {}

            namespace A {
                operation B(inout values: int[]) {
                    values[0] = 0;
                }
            }

            operation Main() {
                var values: int[] = [1];
                A.B(inout values);
                A_B();
            }
            """);

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def A_B() {", result.Qasm);
        Assert.Contains("def A_B_(mutable array[int, #dim = 1] values) {", result.Qasm);
        Assert.Contains("A_B_(values);", result.Qasm);
        Assert.Contains("A_B();", result.Qasm);
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

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains(
            "def LocalValue(mutable array[int, #dim = 1] values) -> int {",
            result.Qasm);
        Assert.Contains("int result = LocalValue(LocalValue_values);", result.Qasm);
    }

    [Theory]
    [InlineData("""
        operation Main() {
            use qubits = Qubit[1];
            H(inout qubits);
        }
        """)]
    [InlineData("""
        operation Take(value: int) {}
        operation Main() {
            var value: int = 1;
            Take(inout value);
        }
        """)]
    [InlineData("""
        operation Rewrite(inout values: int[]) {}
        operation Main() {
            var value: int = 1;
            Rewrite(inout value);
        }
        """)]
    [InlineData("""
        operation Rewrite(inout values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Rewrite(inout values[0]);
        }
        """)]
    [InlineData("""
        operation Take(value: int) {}
        operation Main() {
            var value: int = 1;
            Take(inout value + 1);
        }
        """)]
    public void InOutMarkerOnANonBorrowableArgumentIsQsem038(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, error => Assert.Equal("QSEM038", error.Code));
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

        Assert.True(result.Success, string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("def First(readonly array[int, #dim = 1] values) -> int {", result.Qasm);
        Assert.Contains(
            "def IncrementFirst(readonly array[int, #dim = 1] values) -> int {",
            result.Qasm);
        Assert.Contains("First(values) + 1", result.Qasm);
        Assert.Contains("IncrementFirst(values) + First(values)", result.Qasm);
    }
}
