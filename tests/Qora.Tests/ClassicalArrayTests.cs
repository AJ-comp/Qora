namespace Qora.Tests;

/// <summary>
/// One-dimensional classical arrays: explicit <c>T[]</c> declarations and parameters, literals,
/// zero-initialized <c>new T[N]</c>, element reads/writes, and <c>Count</c>.
/// <para><c>int[]</c>, <c>float[]</c> and <c>angle[]</c> lower to OpenQASM's general <c>array[T, N]</c>
/// type. <c>bit[]</c> cannot: bit is the one element type OpenQASM forbids as an array base type ("bit,
/// bit[n] and stretch are not valid array base types"), because it has a dedicated register type. So a
/// <c>bit[]</c> lowers to <c>bit[N]</c>, and its <c>Count</c> folds to a literal rather than <c>sizeof</c>,
/// which is likewise undefined on a bit register.</para>
/// </summary>
public class ClassicalArrayTests
{
    [Theory]
    [InlineData("int", "1, 2", "int x = values[0]")]
    [InlineData("float", "1.0, 2.5", "float x = values[0]")]
    [InlineData("bit", "0, 1", "bit x = values[0]")]
    [InlineData("angle", "0.0, pi/2", "angle x = values[0]")]
    public void AcceptsExplicitArrayParametersDeclarationsAndLiterals(
        string type, string elements, string read)
    {
        var source = $$"""
            operation Read({{type}}[] values) {
                {{read}};
            }
            operation Main() {
                {{type}}[] values = [{{elements}}];
                Read(values);
            }
            """;

        Compiler.Accepts(source);
    }

    [Theory]
    [InlineData("int", "0")]
    [InlineData("float", "0.0")]
    [InlineData("bit", "0")]
    [InlineData("angle", "0.0")]
    public void AcceptsPositiveLiteralNewForEveryArrayType(string type, string assignedValue) =>
        Compiler.Accepts($"operation Main(){{ {type}[] values = new {type}[3]; values[2] = {assignedValue}; }}");

    [Fact]
    public void AcceptsElementReadsWritesAndCount()
    {
        Compiler.Accepts("""
            operation Main() {
                int[] values = [1, 2, 3];
                int saved = values[1];
                values[0] = saved;
                for i in 0..values.Count-1 {
                    values[i] = values[i] + 1;
                }
            }
            """);
    }

    [Fact]
    public void AcceptsArrayElementAsScalarCallArgument()
    {
        var result = CompileSuccessfully("operation Take(int value){} operation Main(){ int[] values=[1,2]; Take(values[0]); }");

        Assert.Contains("Take(values[0]);", result.Qasm);
    }

    [Fact]
    public void AcceptsMeasurementIntoBitArrayElement()
    {
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                bit[] results = new bit[2];
                results[0] = M(q[0]);
                results[1] = M(q[1]);
            }
            """);
    }

    [Theory]
    [InlineData("operation Work(){ int[] values=[1,2]; } operation Main(){ Work(); }")]
    [InlineData("operation Main(){ int flag=1; if(flag==1){ int[] values=[1,2]; } }")]
    [InlineData("operation Main(){ for i in 0..1 { int[] values=[1,2]; } }")]
    public void RejectsArrayDeclarationsOutsideMainTopLevel(string source) => RejectsCleanly(source);

    [Theory]
    [InlineData("operation Main(){ int[] values = new int[0]; }")]
    [InlineData("operation Main(){ int[] values = new int[-1]; }")]
    [InlineData("operation Main(){ int n=3; int[] values = new int[n]; }")]
    public void RejectsNewWithoutAPositiveLiteralLength(string source) => RejectsCleanly(source);

    [Theory]
    [InlineData("operation Take(int[] values){} operation Main(){ int value=1; Take(value); }")]
    [InlineData("operation Take(int value){} operation Main(){ int[] values=[1,2]; Take(values); }")]
    [InlineData("operation Main(){ int value=1; int copy=value[0]; }")]
    [InlineData("operation Main(){ int value=1; value[0]=2; }")]
    [InlineData("operation Main(){ int[] values=1; }")]
    [InlineData("operation Main(){ int value=[1,2]; }")]
    public void RejectsScalarArrayShapeMismatch(string source) => RejectsCleanly(source);

    [Fact]
    public void RejectsMismatchedArrayElementType() =>
        RejectsCleanly("operation Take(int[] values){} operation Main(){ float[] values=[1.0,2.0]; Take(values); }");

    [Theory]
    [InlineData("operation Main(){ int[] values=[1,2]; int x=values[2]; }")]
    [InlineData("operation Main(){ int[] values=[1,2]; values[2]=3; }")]
    [InlineData("operation Main(){ int[] values=new int[1]; int x=values[1]; }")]
    public void RejectsLiteralIndexOutsideKnownBounds(string source) => RejectsCleanly(source);

    [Fact]
    public void RejectsCountOnScalar() =>
        RejectsCleanly("operation Main(){ int value=1; for i in 0..value.Count-1 { value=i; } }");

    [Fact]
    public void RejectsSameArrayPassedToTwoMutableParameters()
    {
        RejectsCleanly("""
            operation Copy(int[] left, int[] right) {
                left[0] = right[0];
            }
            operation Main() {
                int[] values = [1, 2];
                Copy(values, values);
            }
            """);
    }

    [Theory]
    [InlineData("operation Main(){ const int[] values=[1,2]; values[0]=3; }")]
    [InlineData("operation Change(int[] values){ values[0]=3; } operation Main(){ const int[] values=[1,2]; Change(values); }")]
    public void RejectsMutationOfConstArray(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM024");
    }

    /// <summary>Every element type EXCEPT bit takes the general array form. See <see cref="EmitsBitArrayAsNativeBitRegister"/>.</summary>
    [Theory]
    [InlineData("int", "1, 2", "1, 2")]
    [InlineData("float", "1.0, 2.5", "1.0, 2.5")]
    [InlineData("angle", "0.0, pi/2", "0.0, pi / 2")]
    public void EmitsGeneralOpenQasmArrayAndBraceLiteral(
        string type, string sourceElements, string qasmElements)
    {
        var result = CompileSuccessfully($"operation Main(){{ {type}[] values=[{sourceElements}]; }}");

        Assert.Contains($"array[{type}, 2] values = {{{qasmElements}}};", result.Qasm);
    }

    /// <summary>
    /// A literal is written element by element, NEVER as a bitstring: the spec's prose puts element 0 at the
    /// right (least-significant) end while Braket reads it at the left, so a non-uniform bitstring would
    /// silently reverse the array. Indexed writes mean the same thing on both.
    /// </summary>
    [Fact]
    public void EmitsBitArrayAsNativeBitRegister()
    {
        var result = CompileSuccessfully("operation Main(){ bit[] flags=[0,1]; }");

        Assert.Contains("bit[2] flags;", result.Qasm);
        Assert.Contains("flags[0] = 0;", result.Qasm);
        Assert.Contains("flags[1] = 1;", result.Qasm);
        Assert.DoesNotContain("array[bit", result.Qasm);
    }

    /// <summary>
    /// <c>new bit[N]</c> keeps Qora's zero-initialization promise explicitly: a bare <c>bit[N] f;</c> is
    /// UNDEFINED rather than zeroed. All-zeros is uniform, so the element-order divergence cannot bite it.
    /// </summary>
    [Fact]
    public void EmitsNewBitArrayAsZeroInitializedBitRegister()
    {
        var result = CompileSuccessfully("operation Main(){ bit[] flags=new bit[3]; }");

        Assert.Contains("bit[3] flags = \"000\";", result.Qasm);
        Assert.DoesNotContain("array[bit", result.Qasm);
    }

    /// <summary><c>sizeof</c> is not defined on a bit register, so a bit array's Count must fold to a literal.
    /// Contrast <see cref="EmitsCountAsSizeof"/>, which pins the general-array behaviour for <c>int[]</c>.</summary>
    [Fact]
    public void FoldsBitArrayCountToALiteralRatherThanSizeof()
    {
        var result = CompileSuccessfully("""
            operation Main() {
                use q = Qubit[2];
                bit[] flags = new bit[2];
                for i in 0..flags.Count-1 {
                    flags[i] = M(q[i]);
                }
            }
            """);

        Assert.Contains("[0:2 - 1]", result.Qasm);
        Assert.DoesNotContain("sizeof(flags)", result.Qasm);
    }

    [Fact]
    public void EmitsMutableOneDimensionalArrayParameter()
    {
        var result = CompileSuccessfully("""
            operation SetFirst(int[] values) {
                values[0] = 7;
            }
            operation Main() {
                int[] values = [1, 2];
                SetFirst(values);
            }
            """);

        Assert.Contains("mutable array[int, #dim = 1] values", result.Qasm);
    }

    [Fact]
    public void EmitsCountAsSizeof()
    {
        var result = CompileSuccessfully("""
            operation Visit(int[] values) {
                for i in 0..values.Count-1 {
                    values[i] = values[i] + 1;
                }
            }
            operation Main() {
                int[] values = [1, 2, 3];
                Visit(values);
            }
            """);

        Assert.Contains("sizeof(values)", result.Qasm);
        Assert.DoesNotContain(".Count", result.Qasm);
    }

    [Fact]
    public void EmitsNewAsZeroInitializedArray()
    {
        var result = CompileSuccessfully("operation Main(){ int[] values=new int[3]; }");

        Assert.Contains("array[int, 3] values = {0, 0, 0};", result.Qasm);
    }

    [Fact]
    public void EmitsIndexedReadsAndWrites()
    {
        var result = CompileSuccessfully("operation Main(){ int[] values=[1,2]; int saved=values[1]; values[0]=saved; }");

        Assert.Contains("int saved = values[1];", result.Qasm);
        Assert.Contains("values[0] = saved;", result.Qasm);
    }

    [Fact]
    public void EmitsBitArrayElementConditionsAsBooleanComparisons()
    {
        var result = CompileSuccessfully("operation Main(){ bit[] flags=[0,1]; if(flags[1]==1){ flags[0]=1; } }");

        Assert.Contains("if (flags[1] == true)", result.Qasm);
    }

    private static QoraParseResult CompileSuccessfully(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Success, Explain(result));
        Assert.False(string.IsNullOrWhiteSpace(result.Qasm), "a successful array program must emit OpenQASM");
        return result;
    }

    private static void RejectsCleanly(string source)
    {
        var result = Compiler.Compile(source);
        Assert.False(result.Success, $"expected the array program to be rejected, but it compiled:\n{source}\n{result.Qasm}");
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain(result.Errors, e => e.Code is "QORA0000" or "QINTERNAL");
    }

    // --- literal out-of-bounds THROUGH a parameter. A `T[]` parameter carries no length of its own (the
    //     length arrives with the argument and differs per call), so a literal `x[5]` in the body is a
    //     PRECONDITION — "the array passed here needs at least 6 elements" — and the only place to check it is
    //     the CALL. Without that check the identical access written inline is rejected (QSEM016) while the
    //     helper form silently emits an out-of-bounds write. ---

    [Fact]
    public void RejectsLiteralOutOfBoundsThroughAnArrayParameter() =>
        Compiler.Rejects("""
            operation Helper(int[] x) { x[5] = 99; }
            operation Main() { use q=Qubit[1]; int[] a = [1, 2, 3]; Helper(a); H(q[0]); }
            """, "QSEM016");

    /// <summary>The requirement folds through a CHAIN: Middle never indexes `y` itself, but it hands it to
    /// Deep, so the length Deep needs is demanded of Middle's caller — where the concrete array enters.</summary>
    [Fact]
    public void RejectsLiteralOutOfBoundsThroughAChainOfArrayParameters() =>
        Compiler.Rejects("""
            operation Deep(int[] x) { x[5] = 99; }
            operation Middle(int[] y) { Deep(y); }
            operation Main() { use q=Qubit[1]; int[] a = [1, 2, 3]; Middle(a); H(q[0]); }
            """, "QSEM016");

    /// <summary>NOT over-broad: an array long enough for the callee's literal index is fine.</summary>
    [Fact]
    public void AcceptsAnArrayLongEnoughForTheCalleeLiteralIndex() =>
        Compiler.Accepts("""
            operation Helper(int[] x) { x[5] = 99; }
            operation Main() { use q=Qubit[1]; int[] a = new int[8]; Helper(a); H(q[0]); }
            """);

    /// <summary>A DYNAMIC index imposes no static floor — it stays a runtime concern, exactly as it does for a
    /// local array (and as it does in every other language). Only literal indices are checked.</summary>
    [Fact]
    public void AcceptsDynamicIndexingOfAnArrayParameter() =>
        Compiler.Accepts("""
            operation Helper(int[] x) { for i in 0..x.Count-1 { x[i] = 1; } }
            operation Main() { use q=Qubit[1]; int[] a = [1, 2, 3]; Helper(a); H(q[0]); }
            """);

    private static string Explain(QoraParseResult result) =>
        string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}"));
}
