using Qora.Ir;

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
    [InlineData("int", "1, 2", "var x: int = values[0]")]
    [InlineData("float", "1.0, 2.5", "var x: float = values[0]")]
    [InlineData("bit", "0, 1", "var x: bit = values[0]")]
    [InlineData("angle", "0.0, pi/2", "var x: angle = values[0]")]
    public void AcceptsExplicitArrayParametersDeclarationsAndLiterals(
        string type, string elements, string read)
    {
        var source = $$"""
            operation Read(values: {{type}}[]) {
                {{read}};
            }
            operation Main() {
                var values: {{type}}[] = [{{elements}}];
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
        Compiler.Accepts($"operation Main(){{ var values: {type}[] = new {type}[3]; values[2] = {assignedValue}; }}");

    [Fact]
    public void AcceptsElementReadsWritesAndCount()
    {
        Compiler.Accepts("""
            operation Main() {
                var values: int[] = [1, 2, 3];
                var saved: int = values[1];
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
        var result = CompileSuccessfully("operation Take(value: int){} operation Main(){ var values: int[] = [1,2]; Take(values[0]); }");

        Assert.Contains("Take(values[0]);", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void AcceptsMeasurementIntoBitArrayElement()
    {
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                var results: bit[] = new bit[2];
                results[0] = M(q[0]);
                results[1] = M(q[1]);
            }
            """);
    }

    /// <summary>Array locals go wherever a scalar goes — a helper op, a branch, a loop. The old QSEM012
    /// arm rejecting these was OpenQASM's placement rule leaking into the language; the QASM backend's
    /// ArrayLocalHoisting pass now absorbs it (hidden-parameter threading / scope-top hoisting), so the
    /// language accepts all three shapes it once rejected.</summary>
    [Theory]
    [InlineData("operation Work(){ var values: int[] = [1,2]; } operation Main(){ Work(); }")]
    [InlineData("operation Main(){ var flag: int = 1; if(flag==1){ var values: int[] = [1,2]; } }")]
    [InlineData("operation Main(){ for i in 0..1 { var values: int[] = [1,2]; } }")]
    public void AcceptsArrayDeclarationsOutsideMainTopLevel(string source) => Compiler.Accepts(source);

    [Theory]
    [InlineData("operation Main(){ var values: int[] = new int[0]; }")]
    [InlineData("operation Main(){ var values: int[] = new int[-1]; }")]
    [InlineData("operation Main(){ var n: int = 3; var values: int[] = new int[n]; }")]
    public void RejectsNewWithoutAPositiveLiteralLength(string source) => RejectsCleanly(source);

    [Theory]
    [InlineData("operation Take(values: int[]){} operation Main(){ var value: int = 1; Take(value); }")]
    [InlineData("operation Take(value: int){} operation Main(){ var values: int[] = [1,2]; Take(values); }")]
    [InlineData("operation Main(){ var value: int = 1; var copy: int = value[0]; }")]
    [InlineData("operation Main(){ var value: int = 1; value[0]=2; }")]
    [InlineData("operation Main(){ var values: int[] = 1; }")]
    [InlineData("operation Main(){ var value: int = [1,2]; }")]
    public void RejectsScalarArrayShapeMismatch(string source) => RejectsCleanly(source);

    [Fact]
    public void RejectsMismatchedArrayElementType() =>
        RejectsCleanly("operation Take(values: int[]){} operation Main(){ var values: float[] = [1.0,2.0]; Take(values); }");

    [Theory]
    [InlineData("operation Main(){ var values: int[] = [1,2]; var x: int = values[2]; }")]
    [InlineData("operation Main(){ var values: int[] = [1,2]; values[2]=3; }")]
    [InlineData("operation Main(){ var values: int[] = new int[1]; var x: int = values[1]; }")]
    public void RejectsLiteralIndexOutsideKnownBounds(string source) => RejectsCleanly(source);

    [Fact]
    public void RejectsCountOnScalar() =>
        RejectsCleanly("operation Main(){ var value: int = 1; for i in 0..value.Count-1 { value=i; } }");

    [Fact]
    public void RejectsSameArrayPassedToReadonlyAndMutableParameters()
    {
        RejectsCleanly("""
            operation Copy(var left: int[], right: int[]) {
                left[0] = right[0];
            }
            operation Main() {
                var values: int[] = [1, 2];
                Copy(var values, values);
            }
            """);
    }

    [Theory]
    [InlineData("operation Main(){ const values: int[] = [1,2]; values[0]=3; }")]
    [InlineData("operation Change(var values: int[]){ values[0]=3; } operation Main(){ const values: int[] = [1,2]; Change(var values); }")]
    public void RejectsMutationOfConstArray(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM024");
    }

    /// <summary>Every element type EXCEPT bit takes the general array form. See <see cref="EmitsBitArrayAsNativeBitRegister"/>.</summary>
    [Theory]
    [InlineData("int", "1, 2", "1, 2")]
    [InlineData("float", "1.0, 2.5", "1.0, 2.5")]
    [InlineData("angle", "0.0, pi/2", "0.0, pi / 2")]
    public void EmitsGeneralOpenQasmArrayAndBraceLiteral(
        string type, string sourceElements, string qasmElements)
    {
        var result = CompileSuccessfully($"operation Main(){{ var values: {type}[] = [{sourceElements}]; }}");

        Assert.Contains($"array[{type}, 2] values = {{{qasmElements}}};", result.Targets.OpenQasm!.Text);
    }

    /// <summary>
    /// A literal is written element by element, NEVER as a bitstring: the spec's prose puts element 0 at the
    /// right (least-significant) end while Braket reads it at the left, so a non-uniform bitstring would
    /// silently reverse the array. Indexed writes mean the same thing on both.
    /// </summary>
    [Fact]
    public void EmitsBitArrayAsNativeBitRegister()
    {
        var result = CompileSuccessfully("operation Main(){ var flags: bit[] = [0,1]; }");

        Assert.Contains("bit[2] flags;", result.Targets.OpenQasm!.Text);
        Assert.Contains("flags[0] = 0;", result.Targets.OpenQasm!.Text);
        Assert.Contains("flags[1] = 1;", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("array[bit", result.Targets.OpenQasm!.Text);
    }

    /// <summary>
    /// <c>new bit[N]</c> keeps Qora's zero-initialization promise explicitly: a bare <c>bit[N] f;</c> is
    /// UNDEFINED rather than zeroed. All-zeros is uniform, so the element-order divergence cannot bite it.
    /// </summary>
    [Fact]
    public void EmitsNewBitArrayAsZeroInitializedBitRegister()
    {
        var result = CompileSuccessfully("operation Main(){ var flags: bit[] = new bit[3]; }");

        Assert.Contains("bit[3] flags = \"000\";", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("array[bit", result.Targets.OpenQasm!.Text);
    }

    /// <summary><c>sizeof</c> is not defined on a bit register, so a bit array's Count must fold to a literal.
    /// Contrast <see cref="EmitsCountAsSizeof"/>, which pins the general-array behaviour for <c>int[]</c>.</summary>
    [Fact]
    public void FoldsBitArrayCountToALiteralRatherThanSizeof()
    {
        var result = CompileSuccessfully("""
            operation Main() {
                use q = Qubit[2];
                var flags: bit[] = new bit[2];
                for i in 0..flags.Count-1 {
                    flags[i] = M(q[i]);
                }
            }
            """);

        Assert.Contains("[0:2 - 1]", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("sizeof(flags)", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void EmitsMutableOneDimensionalArrayParameter()
    {
        var result = CompileSuccessfully("""
            operation SetFirst(var values: int[]) {
                values[0] = 7;
            }
            operation Main() {
                var values: int[] = [1, 2];
                SetFirst(var values);
            }
            """);

        Assert.Contains("mutable array[int, #dim = 1] values", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void EmitsCountAsSizeof()
    {
        var result = CompileSuccessfully("""
            operation Visit(var values: int[]) {
                for i in 0..values.Count-1 {
                    values[i] = values[i] + 1;
                }
            }
            operation Main() {
                var values: int[] = [1, 2, 3];
                Visit(var values);
            }
            """);

        Assert.Contains("sizeof(values)", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain(".Count", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void EmitsNewAsZeroInitializedArray()
    {
        var result = CompileSuccessfully("operation Main(){ var values: int[] = new int[3]; }");

        Assert.Contains("array[int, 3] values = {0, 0, 0};", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void EmitsIndexedReadsAndWrites()
    {
        var result = CompileSuccessfully("operation Main(){ var values: int[] = [1,2]; var saved: int = values[1]; values[0]=saved; }");

        Assert.Contains("int saved = values[1];", result.Targets.OpenQasm!.Text);
        Assert.Contains("values[0] = saved;", result.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void EmitsBitArrayElementConditionsAsBooleanComparisons()
    {
        var result = CompileSuccessfully("operation Main(){ var flags: bit[] = [0,1]; if(flags[1]==1){ flags[0]=1; } }");

        Assert.Contains("if (flags[1] == true)", result.Targets.OpenQasm!.Text);
    }

    private static Compilation CompileSuccessfully(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Succeeded, Explain(result));
        Assert.False(
            string.IsNullOrWhiteSpace(result.Targets.OpenQasm?.Text),
            "a successful array program must emit OpenQASM");
        return result;
    }

    private static void RejectsCleanly(string source)
    {
        var result = Compiler.Compile(source);
        Assert.False(
            result.Succeeded,
            $"expected the array program to be rejected, but it compiled:\n{source}\n{result.Targets.OpenQasm?.Text}");
        Assert.NotEmpty(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code is "QORA0000" or "QINTERNAL");
    }

    // --- literal out-of-bounds THROUGH a parameter. A `T[]` parameter carries no length of its own (the
    //     length arrives with the argument and differs per call), so a literal `x[5]` in the body is a
    //     PRECONDITION — "the array passed here needs at least 6 elements" — and the only place to check it is
    //     the CALL. Without that check the identical access written inline is rejected (QSEM016) while the
    //     helper form silently emits an out-of-bounds write. ---

    [Fact]
    public void RejectsLiteralOutOfBoundsThroughAnArrayParameter() =>
        Compiler.Rejects("""
            operation Helper(var x: int[]) { x[5] = 99; }
            operation Main() { use q=Qubit[1]; var a: int[] = [1, 2, 3]; Helper(var a); H(q[0]); }
            """, "QSEM016");

    /// <summary>The requirement folds through a CHAIN: Middle never indexes `y` itself, but it hands it to
    /// Deep, so the length Deep needs is demanded of Middle's caller — where the concrete array enters.</summary>
    [Fact]
    public void RejectsLiteralOutOfBoundsThroughAChainOfArrayParameters() =>
        Compiler.Rejects("""
            operation Deep(var x: int[]) { x[5] = 99; }
            operation Middle(var y: int[]) { Deep(var y); }
            operation Main() { use q=Qubit[1]; var a: int[] = [1, 2, 3]; Middle(var a); H(q[0]); }
            """, "QSEM016");

    /// <summary>NOT over-broad: an array long enough for the callee's literal index is fine.</summary>
    [Fact]
    public void AcceptsAnArrayLongEnoughForTheCalleeLiteralIndex() =>
        Compiler.Accepts("""
            operation Helper(var x: int[]) { x[5] = 99; }
            operation Main() { use q=Qubit[1]; var a: int[] = new int[8]; Helper(var a); H(q[0]); }
            """);

    /// <summary>A DYNAMIC index imposes no static floor — it stays a runtime concern, exactly as it does for a
    /// local array (and as it does in every other language). Only literal indices are checked.</summary>
    [Fact]
    public void AcceptsDynamicIndexingOfAnArrayParameter() =>
        Compiler.Accepts("""
            operation Helper(var x: int[]) { for i in 0..x.Count-1 { x[i] = 1; } }
            operation Main() { use q=Qubit[1]; var a: int[] = [1, 2, 3]; Helper(var a); H(q[0]); }
            """);

    // --- A WHOLE bit[] register is a CONTAINER OF BITS, not a number (QSEM036) ---
    //
    // A bit pattern carries no sign, so it has no single numeric meaning: the same "10" reads 2 unsigned and
    // −2 in two's complement (both verified on the Braket oracle). Rather than pick one silently, every
    // numeric use of a whole register is rejected and `AsInt(f)` is the one way to ask for a reading. What a
    // register may still do without any numeric interpretation: be indexed (`f[0]`), report `.Count`, be
    // passed as an argument, and be compared to another register OF THE SAME WIDTH.

    /// <summary>Every position that would read a whole register AS A NUMBER is a compile error — the rule is
    /// on the VALUE, not on a list of syntactic positions, so assignment, arithmetic, a bare truth condition
    /// and comparison-against-a-number are all one rule. Before this, `int n = f;` compiled and silently
    /// evaluated to −2 on the execution oracle.</summary>
    [Theory]
    [InlineData("var n: int = f;", "int assignment")]
    [InlineData("var n: int = f + 1;", "arithmetic")]
    [InlineData("if (f) { X(q[0]); }", "bare truth condition")]
    [InlineData("if (f == 1) { X(q[0]); }", "comparison against a number")]
    [InlineData("if (f > 1) { X(q[0]); }", "ordering against a number")]
    [InlineData("for i in 0..f { X(q[0]); }", "range bound")]
    [InlineData("Rx(f * pi, q[0]);", "angle expression")]
    public void RejectsAWholeBitRegisterUsedAsANumber(string statement, string why)
    {
        var r = Compiler.Compile($"operation Main(){{ use q=Qubit[1]; var f: bit[] = new bit[2]; {statement} }}");
        Assert.False(r.Succeeded, $"{why}: expected QSEM036 but the program compiled");
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM036");
    }

    /// <summary>`AsInt(f)` is the ONE reading, and it emits the WIDTH-QUALIFIED unsigned cast: the spec allows
    /// `bit[n]` → `uint[m]` only when `n == m`, and Braket accepts a wrong width silently, so the compiler
    /// must supply it. UNSIGNED because `int[2]("10")` is −2 while `uint[2]("10")` is 2.</summary>
    [Fact]
    public void AsIntEmitsTheWidthQualifiedUnsignedCast()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; if (AsInt(f) == 1) { X(q[0]); } }");
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("if (uint[2](f) == 1)", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("(int[2](f)", r.Targets.OpenQasm!.Text);   // never the SIGNED cast (`uint[2](f)` contains `int[2](f)`)
        Assert.DoesNotContain("uint(f)", r.Targets.OpenQasm!.Text);      // never width-less: the spec requires n == m
    }

    /// <summary>The width follows the REGISTER, not a single op-wide guess — two same-named registers in
    /// disjoint blocks are different arrays, and each cast carries its own width.</summary>
    [Fact]
    public void AsIntTakesTheWidthOfTheNearestDeclaration()
    {
        var r = Compiler.Compile("""
            operation Main() {
                use q = Qubit[1];
                if (1 == 1) { var f: bit[] = new bit[2]; if (AsInt(f) == 1) { X(q[0]); } }
                else        { var f: bit[] = new bit[5]; if (AsInt(f) == 1) { X(q[0]); } }
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("uint[2](", r.Targets.OpenQasm!.Text);
        Assert.Contains("uint[5](", r.Targets.OpenQasm!.Text);
    }

    /// <summary>Register-to-register comparison needs no numeric reading — it matches bit patterns — so it
    /// stays legal and emits BARE, with no cast at all.</summary>
    [Fact]
    public void EqualWidthRegistersCompareDirectly()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var g: bit[] = new bit[2]; if (f == g) { X(q[0]); } }");
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("if (f == g)", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("uint", r.Targets.OpenQasm!.Text);
    }

    /// <summary>Different widths are never equal in OpenQASM whatever bits they hold — `bit[2] "10"` and
    /// `bit[3] "010"` both read as 2 yet compare unequal — so the comparison is rejected instead of silently
    /// answering "different".</summary>
    [Fact]
    public void RejectsComparingRegistersOfDifferentWidths()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var g: bit[] = new bit[3]; if (f == g) { X(q[0]); } }");
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM036");
    }

    /// <summary>ORDERING between registers is rejected even at equal width: the target compares `&lt;`/`&gt;`
    /// NUMERICALLY while `==` compares bit patterns, so the two would disagree about the same pair. Ordering
    /// is a numeric question and must be asked with an explicit reading on both sides.</summary>
    [Fact]
    public void RejectsOrderingBetweenRegisters()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var g: bit[] = new bit[2]; if (f < g) { X(q[0]); } }");
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM036");
    }

    /// <summary>`AsInt` reads a whole register; anything else has no width to cast with, so it is refused at
    /// the source rather than becoming a QINTERNAL in the target lowering. A single bit is already a value.</summary>
    [Theory]
    [InlineData("var n: int = AsInt(k);", "an int variable")]
    [InlineData("var n: int = AsInt(f[0]);", "a single bit")]
    [InlineData("var n: int = AsInt(4);", "a literal")]
    [InlineData("var n: int = AsInt(f, f);", "two arguments")]
    public void RejectsAsIntOnAnythingButAWholeRegister(string statement, string why)
    {
        var r = Compiler.Compile($"operation Main(){{ use q=Qubit[1]; var f: bit[] = new bit[2]; var k: int = 3; {statement} }}");
        Assert.False(r.Succeeded, $"{why}: expected QSEM006 but the program compiled");
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM006");
    }

    /// <summary>The built-in name is reserved: an expression-position call is spelled by BARE name, so no
    /// qualifier could ever disambiguate a user callable that reused it.</summary>
    [Fact]
    public void RejectsAUserCallableNamedAsInt() =>
        Compiler.Rejects("operation AsInt() { } operation Main(){ use q=Qubit[1]; X(q[0]); }", "QSEM013");

    /// <summary>A `repeat`'s `until` runs AFTER the body, so it reads the body's names — including a register
    /// the body declared. Resolving it against the ENCLOSING scope instead made a legal body-local argument
    /// look like something that is not a register at all.</summary>
    [Fact]
    public void AnUntilConditionSeesARegisterTheRepeatBodyDeclared()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; repeat { var f: bit[] = new bit[2]; f[0] = M(q[0]); } until (AsInt(f) == 1); }");
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("uint[2](f) == 1", r.Targets.OpenQasm!.Text);
    }

    /// <summary>A whole register passed as an ARGUMENT is judged by the callee's signature, not by the rule —
    /// a `bit[]` parameter still accepts it.</summary>
    [Fact]
    public void AWholeRegisterIsStillALegalArgument() =>
        Compiler.Accepts("""
            operation Helper(f: bit[], q: Qubit) { if (f[0] == 1) { X(q); } }
            operation Main() { use q=Qubit[1]; var f: bit[] = new bit[2]; Helper(f, q[0]); }
            """);

    // --- bit[] PARAMETERS: length-specialized like Qubit[] (bit is not a valid array base type, so the
    //     only legal QASM parameter form is the sized register `bit[N]`) ---

    [Fact]
    public void BitArrayParameterSpecializesToASizedRegister()
    {
        var r = Compiler.Compile("""
            operation Read(values: bit[], q: Qubit) {
                if (values[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                var f: bit[] = new bit[2];
                Read(f, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("def Read__sz2(bit[2] values, qubit q)", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("array[bit", r.Targets.OpenQasm!.Text);   // the invalid base type never appears
    }

    [Fact]
    public void BitArrayParameterCountFoldsToALiteralNotSizeof()
    {
        // sizeof is undefined on a bit register, so `f.Count` must fold to the specialized length.
        var r = Compiler.Compile("""
            operation Scan(f: bit[], q: Qubit) {
                for i in 0..f.Count-1 { if (f[i] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                var f: bit[] = new bit[3];
                Scan(f, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("[0:3 - 1]", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("sizeof", r.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void TwoBitArrayLengthsMakeTwoSpecializations()
    {
        var r = Compiler.Compile("""
            operation Read(values: bit[], q: Qubit) {
                if (values[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[2];
                var a: bit[] = new bit[2];
                var b: bit[] = new bit[3];
                Read(a, q[0]);
                Read(b, q[1]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("def Read__sz2(bit[2] values, qubit q)", r.Targets.OpenQasm!.Text);
        Assert.Contains("def Read__sz3(bit[3] values, qubit q)", r.Targets.OpenQasm!.Text);
    }

    /// <summary>Functions are called as <see cref="QCallNode"/> values inside expression trees rather than
    /// as <see cref="QGate"/> statements. The monomorphizer must therefore find calls nested under both a
    /// declaration initializer and another function call. Equal widths reuse one specialization, while a
    /// distinct width creates a second one.</summary>
    [Fact]
    public void BitArrayFunctionCallsInNestedInitializersSpecializePerLengthAndReuse()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            function Increment(value: int): int { return value + 1; }
            operation Main() {
                use q = Qubit[1];
                var first: bit[] = new bit[2];
                var second: bit[] = new bit[3];
                var sameWidth: bit[] = new bit[2];
                var a: int = CountBits(first);
                var b: int = CountBits(second);
                var c: int = Increment(CountBits(sameWidth)) + CountBits(first);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var specs = r.Hir.EffectAnalysis!.Program!.Operations.Where(o => o.DisplayName == "CountBits").ToList();
        Assert.Equal(2, specs.Count);
        Assert.Equal(new[] { 2, 3 },
            specs.Select(o => o.Params.Single().RegisterSize!.Value).Order().ToArray());
        Assert.All(specs, specialization =>
        {
            Assert.True(specialization.IsFunction);
            Assert.Equal(QType.Int, specialization.ReturnType);
        });
        Assert.Equal(1, r.Targets.OpenQasm!.Text.Split("def CountBits__sz2(").Length - 1);
        Assert.Equal(1, r.Targets.OpenQasm!.Text.Split("def CountBits__sz3(").Length - 1);
        Assert.Contains("int a = CountBits__sz2(first);", r.Targets.OpenQasm!.Text);
        Assert.Contains("int b = CountBits__sz3(second);", r.Targets.OpenQasm!.Text);
        Assert.Contains("Increment(CountBits__sz2(sameWidth)) + CountBits__sz2(first)", r.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void BitArrayFunctionCallOnAnAssignmentRhsIsSpecialized()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[2];
                var count: int = 0;
                count = CountBits(flags);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("def CountBits__sz2(bit[2] flags) -> int", r.Targets.OpenQasm!.Text);
        Assert.Contains("count = CountBits__sz2(flags);", r.Targets.OpenQasm!.Text);
    }

    /// <summary>A function specialization can itself expose another expression-position call. Rewriting
    /// continues through the specialized body, so the inner function receives the outer parameter's now
    /// concrete width before the unsized originals are removed.</summary>
    [Fact]
    public void BitArrayFunctionCallInAReturnExpressionIsSpecializedTransitively()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            function ForwardCount(flags: bit[]): int { return CountBits(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[3];
                var count: int = ForwardCount(flags);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("def ForwardCount__sz3(bit[3] flags) -> int", r.Targets.OpenQasm!.Text);
        Assert.Contains("def CountBits__sz3(bit[3] flags) -> int", r.Targets.OpenQasm!.Text);
        Assert.Contains("CountBits__sz3(flags)", r.Targets.OpenQasm!.Text);
        Assert.Contains("int count = ForwardCount__sz3(flags);", r.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void BitArrayFunctionCallsInAConditionAndGateArgumentAreSpecialized()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[2];
                if (CountBits(flags) == 0) { Rx(CountBits(flags), q[0]); }
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("if (CountBits__sz2(flags) == 0)", r.Targets.OpenQasm!.Text);
        Assert.Contains("rx(CountBits__sz2(flags)) q[0];", r.Targets.OpenQasm!.Text);
        Assert.Equal(1, r.Targets.OpenQasm!.Text.Split("def CountBits__sz2(").Length - 1);
    }

    [Fact]
    public void RepeatUntilSpecializesWithABitArrayDeclaredInTheBodyScope()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[3];
                repeat {
                    var flags: bit[] = new bit[2];
                    flags[0] = 1;
                } until (CountBits(flags) == 2);
                var outerCount: int = CountBits(flags);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("def CountBits__sz2(bit[2] flags) -> int", r.Targets.OpenQasm!.Text);
        Assert.Contains("def CountBits__sz3(bit[3] flags) -> int", r.Targets.OpenQasm!.Text);
        Assert.Contains("bit[2] flags = \"00\";", r.Targets.OpenQasm!.Text);
        Assert.Contains("bit[3] flags_ = \"000\";", r.Targets.OpenQasm!.Text);
        Assert.Contains("CountBits__sz2(flags) == 2", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("CountBits__sz2(flags_)", r.Targets.OpenQasm!.Text);
        Assert.Contains("int outerCount = CountBits__sz3(flags_);", r.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void RepeatUntilFunctionCallUsesTheBodyLocalAfterScalarShadowing()
    {
        var r = Compiler.Compile("""
            function Echo(value: int): int { return value; }
            operation Main() {
                use q = Qubit[1];
                var value: int = 0;
                repeat {
                    var value: int = 1;
                } until (Echo(value) == 1);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("int value = 0;", r.Targets.OpenQasm!.Text);
        Assert.Contains("int value_ = 1;", r.Targets.OpenQasm!.Text);
        Assert.Contains("Echo(value_) == 1", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("Echo(value) == 1", r.Targets.OpenQasm!.Text);
    }

    [Fact]
    public void HoistedArrayRenameDoesNotCaptureRepeatOrForScalarShadows()
    {
        var r = Compiler.Compile("""
            function Echo(value: int): int { return value; }
            operation Worker() {
                var value: int[] = [7];
                repeat {
                    var value: int = 1;
                } until (Echo(value) == 1);
                for value in 0..0 {
                    var seen: int = Echo(value);
                }
            }
            operation Main() {
                use q = Qubit[1];
                Worker();
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        Assert.Contains("int value_ = 1;", r.Targets.OpenQasm!.Text);
        Assert.Contains("Echo(value_) == 1", r.Targets.OpenQasm!.Text);
        Assert.Contains("for int value__ in", r.Targets.OpenQasm!.Text);
        Assert.Contains("Echo(value__)", r.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("Echo(value) == 1", r.Targets.OpenQasm!.Text);
    }

    /// <summary>A bit[] parameter is READ-ONLY: its QASM form is a by-value register, so a write would
    /// silently never reach the caller — rejected loudly instead (int[] passes by mutable reference; the
    /// asymmetry is OpenQASM's, and the ban keeps it unobservable at the Qora surface).</summary>
    [Theory]
    [InlineData("operation Zero(f: bit[], q: Qubit){ f[0] = 0; H(q); }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[1]; Zero(f, q[0]); }")]
    [InlineData("operation Store(f: bit[], qs: Qubit[]){ f[0] = M(qs[0]); }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[1]; Store(f, q); }")]
    public void RejectsWritingToABitArrayParameter(string source) =>
        Compiler.Rejects(source, "QSEM032");

    /// <summary>bit[] parameters are read-only by-value registers (QSEM032), so the MUTABLE-array rules
    /// must not apply to them: the same register may feed two bit[] slots (reads cannot conflict), and a
    /// const array is a perfectly fine argument.</summary>
    [Fact]
    public void DuplicateBitArrayArgumentsAreAcceptedReadsCannotConflict()
    {
        var r = Compiler.Compile("""
            operation Both(a: bit[], b: bit[], q: Qubit) { if (a[0] == 1) { X(q); } if (b[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; var f: bit[] = new bit[2]; Both(f, f, q[0]); }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    [Fact]
    public void ConstBitArrayIsAValidArgumentToAReadOnlyBitParameter()
    {
        var r = Compiler.Compile("""
            operation Read(f: bit[], q: Qubit) { if (f[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; const f: bit[] = [0, 1]; Read(f, q[0]); }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    /// <summary>bit[] parameters specialize like Qubit[], so their bounds facts DEFER to the post-mono
    /// re-check the same way: a loop bounded by ANOTHER bit[] param's Count, and a constant guard
    /// `n &lt; K`, must both prove post-specialization instead of rejecting pre-mono (QSEM030).</summary>
    [Fact]
    public void BitArrayCrossCountLoopDefersAndProvesPostMono()
    {
        var r = Compiler.Compile("""
            operation Zip(a: bit[], b: bit[], q: Qubit) {
                for i in 0..a.Count-1 { if (b[i] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                var a: bit[] = new bit[2];
                var b: bit[] = new bit[2];
                Zip(a, b, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    [Fact]
    public void BitArrayConstGuardDefersAndProvesPostMono()
    {
        var r = Compiler.Compile("""
            operation Pick(f: bit[], n: int, q: Qubit) {
                if (0 <= n && n < 2) { if (f[n] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                var f: bit[] = new bit[2];
                var n: int = 1;
                Pick(f, n, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    // --- ArrayLocalHoisting: a classical-array LOCAL in a def-emitted op is inexpressible in OpenQASM
    //     (arrays are global-or-parameter only, and defs cannot see mutable globals — scope.rst), so the
    //     QASM backend threads it as a hidden array-reference parameter backed by a global, with the
    //     declaration site becoming element-wise re-initialization (fresh value on every entry). ---

    [Fact]
    public void ThreadsHelperArrayLocalAsHiddenParameter()
    {
        var result = CompileSuccessfully("""
            operation SetTable(q: Qubit) {
                var tbl: int[] = [1, 2, 3];
                if (tbl[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                SetTable(q[0]);
            }
            """);

        Assert.Contains("array[int, 3] SetTable_tbl = {0, 0, 0};", result.Targets.OpenQasm!.Text);   // global backing, default-init
        Assert.Contains("mutable array[int, #dim = 1] tbl", result.Targets.OpenQasm!.Text);          // hidden parameter on the def
        Assert.Contains("tbl[0] = 1;", result.Targets.OpenQasm!.Text);                               // declaration site = re-init
        Assert.Contains("tbl[2] = 3;", result.Targets.OpenQasm!.Text);
        Assert.Contains("SetTable(q[0], SetTable_tbl);", result.Targets.OpenQasm!.Text);             // caller supplies the backing
        Assert.DoesNotContain("array[int, 3] tbl", result.Targets.OpenQasm!.Text);                   // no array DECLARATION inside the def
        // the backing declaration precedes the call that hands it over
        Assert.True(result.Targets.OpenQasm!.Text.IndexOf("array[int, 3] SetTable_tbl")
                    < result.Targets.OpenQasm!.Text.IndexOf("SetTable(q[0], SetTable_tbl);"));
    }

    [Fact]
    public void ThreadsHiddenParameterTransitivelyThroughIntermediateDefs()
    {
        var result = CompileSuccessfully("""
            operation Inner(q: Qubit) {
                var t: int[] = [4, 5];
                if (t[1] == 5) { X(q); }
            }
            operation Outer(q: Qubit) {
                Inner(q);
            }
            operation Main() {
                use q = Qubit[1];
                Outer(q[0]);
            }
            """);

        Assert.Contains("array[int, 2] Inner_t = {0, 0};", result.Targets.OpenQasm!.Text);   // one backing global
        Assert.Contains("Inner(q, Inner_t);", result.Targets.OpenQasm!.Text);                // Outer hands its pass-through on
        Assert.Contains("Outer(q[0], Inner_t);", result.Targets.OpenQasm!.Text);             // Main names the global directly
    }

    [Fact]
    public void HoistsEntryNestedArrayDeclarationToTheGlobalTop()
    {
        var result = CompileSuccessfully("""
            operation Main() {
                use q = Qubit[1];
                var b: bit = M(q[0]);
                var n: int = b;
                if (n == 1) {
                    var a: int[] = [7, 8];
                    var x: int = a[0];
                }
            }
            """);

        Assert.Contains("array[int, 2] a = {0, 0};", result.Targets.OpenQasm!.Text);   // declaration at global top…
        Assert.Contains("a[0] = 7;", result.Targets.OpenQasm!.Text);                   // …site keeps element-wise re-init
        Assert.Contains("a[1] = 8;", result.Targets.OpenQasm!.Text);
        Assert.True(result.Targets.OpenQasm!.Text.IndexOf("array[int, 2] a") < result.Targets.OpenQasm!.Text.IndexOf("if ("));
    }

    /// <summary>bit[] locals are sized REGISTERS in OpenQASM — legal inside a def, not "arrays" — so the
    /// hoisting pass must leave them exactly where they are.</summary>
    [Fact]
    public void LeavesBitArrayLocalsInsideDefsUntouched()
    {
        var result = CompileSuccessfully("""
            operation Flag(q: Qubit) {
                var f: bit[] = new bit[2];
                f[0] = 1;
                if (f[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                Flag(q[0]);
            }
            """);

        Assert.Contains("bit[2] f = \"00\";", result.Targets.OpenQasm!.Text);   // register declaration stays in the def
        Assert.Contains("Flag(q[0]);", result.Targets.OpenQasm!.Text);          // no hidden parameter added
        Assert.DoesNotContain("Flag_f", result.Targets.OpenQasm!.Text);         // no backing global minted
    }

    /// <summary>A bit[] NESTED in a control-flow block hoists only to the top of its own op (importers
    /// reject classical declarations inside blocks) — a register declaration is legal at def scope, so
    /// no threading and no backing global.</summary>
    [Fact]
    public void HoistsNestedBitArrayToItsOwnOpTop()
    {
        var result = CompileSuccessfully("""
            operation Tally(q: Qubit, n: int) {
                if (n == 1) {
                    var f: bit[] = new bit[2];
                    f[0] = 1;
                    if (f[0] == 1) { X(q); }
                }
            }
            operation Main() {
                use q = Qubit[1];
                var b: bit = M(q[0]);
                var n: int = b;
                Tally(q[0], n);
            }
            """);

        Assert.Contains("bit[2] f = \"00\";", result.Targets.OpenQasm!.Text);          // storage at the def's top
        Assert.Contains("f[0] = 0;", result.Targets.OpenQasm!.Text);                   // site re-initializes per entry
        Assert.Contains("Tally(q[0], n);", result.Targets.OpenQasm!.Text);             // signature unchanged — no threading
        Assert.DoesNotContain("Tally_f", result.Targets.OpenQasm!.Text);
        Assert.True(result.Targets.OpenQasm!.Text.IndexOf("bit[2] f") < result.Targets.OpenQasm!.Text.IndexOf("if ("));
    }

    // --- ArrayLocalHoisting name-uniqueness (R13/R14): the pass mints every global / parameter / storage
    //     as a UNIQUE placeholder (#hoist#base#uid), so two distinct entities can never share a spelling
    //     and a placeholder can never equal a user name — without enumerating the scope. NameMangler then
    //     turns each placeholder into a pretty, collision-free name (distinct placeholders never trigger
    //     its same-name MERGE; its per-key freshening splits a shared base into `x`/`x_`). These pin the
    //     collision vectors that once emitted invalid QASM with success=true; semantics are Braket-verified
    //     separately. When a base clashes, whichever name the mangler reaches SECOND takes the `_` — which
    //     for a hoisted-vs-user clash is often the USER's name (the mangler notes the rename), and that is
    //     fine: the two just need to differ and bind correctly. ---

    /// <summary>Vector 2 — the `{op}_{var}` base is ambiguous (`A`+`b_c` and `A_b`+`c` both yield base
    /// `A_b_c`); distinct placeholders make the mangler split them into two globals rather than merge them
    /// into one storage.</summary>
    [Fact]
    public void MintedGlobalsThatWouldConcatenateAlikeAreDisambiguated()
    {
        var result = CompileSuccessfully("""
            operation A(q: Qubit) { var b_c: int[] = [1, 1]; if (b_c[1] == 1) { X(q); } }
            operation A_b(q: Qubit) { var c: int[] = [9]; if (c[0] == 9) { X(q); } }
            operation Main() { use q = Qubit[2]; A(q[0]); A_b(q[1]); }
            """);

        Assert.Contains("array[int, 2] A_b_c = {0, 0};", result.Targets.OpenQasm!.Text);   // A's b_c
        Assert.Contains("array[int, 1] A_b_c_ = {0};", result.Targets.OpenQasm!.Text);     // A_b's c — split apart
        Assert.Contains("A(q[0], A_b_c);", result.Targets.OpenQasm!.Text);
        Assert.Contains("A_b(q[1], A_b_c_);", result.Targets.OpenQasm!.Text);
    }

    /// <summary>Vector 1 — a minted backing global and a user top-level variable of the same spelling get
    /// DISTINCT emitted names (here the user scalar takes the `_`), never one merged declaration.</summary>
    [Fact]
    public void MintedGlobalAndUserTopLevelNameGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation Foo(q: Qubit) { var bar: int[] = [1]; if (bar[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; var Foo_bar: int = 5; Foo(q[0]); if (Foo_bar == 5) { X(q[0]); } }
            """);

        Assert.Contains("array[int, 1] Foo_bar = {0};", result.Targets.OpenQasm!.Text);   // the backing global
        Assert.Contains("int Foo_bar_ = 5;", result.Targets.OpenQasm!.Text);             // the user scalar, split apart
        Assert.Contains("Foo(q[0], Foo_bar);", result.Targets.OpenQasm!.Text);           // the call supplies the backing global
        Assert.Contains("Foo_bar_ == 5", result.Targets.OpenQasm!.Text);                 // the user scalar's own use follows its rename
    }

    /// <summary>Vector 3 — a pass-through parameter (named after the global it forwards) must be freshened
    /// away from an owned parameter of the same spelling, so the def has no duplicate parameter.</summary>
    [Fact]
    public void PassThroughParameterIsFreshenedAwayFromAnOwnedParameter()
    {
        var result = CompileSuccessfully("""
            operation D(q: Qubit) { var g: int[] = [1]; if (g[0] == 1) { X(q); } }
            operation Mid(q: Qubit) { var D_g: int[] = [7]; if (D_g[0] == 7) { X(q); } D(q); }
            operation Main() { use q = Qubit[1]; Mid(q[0]); }
            """);

        // Mid owns array `D_g` (its own param) AND forwards D's global `D_g` — the two params must differ.
        Assert.Contains("def Mid(qubit q, mutable array[int, #dim = 1] D_g, mutable array[int, #dim = 1] D_g_) {", result.Targets.OpenQasm!.Text);
        Assert.Contains("D(q, D_g_);", result.Targets.OpenQasm!.Text);              // D receives the forwarded (freshened) slot
        Assert.Contains("Mid(q[0], Mid_D_g, D_g);", result.Targets.OpenQasm!.Text); // Main supplies Mid's own + D's backing
    }

    /// <summary>Vector 4 — an array local that shadows a same-named parameter gets a freshened parameter,
    /// and ONLY the array's in-scope references are rewritten to it; the shadowed parameter's own
    /// references (here the enclosing `if (a == 0)`) are left intact.</summary>
    [Fact]
    public void ArrayLocalShadowingAParameterRewritesOnlyItsOwnReferences()
    {
        var result = CompileSuccessfully("""
            operation Helper(q: Qubit[], a: int) {
                if (a == 0) {
                    var a: int[] = [1, 2];
                    if (a[0] == 1) { X(q[0]); }
                }
            }
            operation Main() { use q = Qubit[1]; Helper(q, 0); }
            """);

        Assert.Contains("if (a == 0)", result.Targets.OpenQasm!.Text);        // the PARAMETER comparison — untouched
        Assert.Contains("a_[0] = 1;", result.Targets.OpenQasm!.Text);         // the ARRAY — freshened and rewritten
        Assert.Contains("if (a_[0] == 1)", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("if (a_ == 0)", result.Targets.OpenQasm!.Text); // the rename must NOT leak to the param comparison
    }

    // --- ArrayLocalHoisting seed completeness (R14): the Namer that mints unique names must be seeded
    //     with EVERY inhabitant of the emission scope — the full set NameMangler collects — not just op
    //     names and parameters. A body-declared local (loop variable, scalar, measure bit) or a NESTED
    //     entry declaration is in that scope too; omitting it let a minted name collide with it and the
    //     mangler then merged the two. These pin the collisions against body-declared names. ---

    /// <summary>A hidden parameter and a LOOP VARIABLE the body declares get DISTINCT names — the mangler
    /// splits the placeholder-derived base from the loop variable (here the parameter base `g` avoids the
    /// operation name `g` → `g_`, and the loop variable `g_` then avoids that → `g__`).</summary>
    [Fact]
    public void HiddenParameterAndABodyLoopVariableGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation g(q: Qubit) { X(q); }
            operation Helper(q: Qubit) {
                var g: int[] = [1];
                for g_ in 0..0 { if (g[0] == 1) { X(q); } }
            }
            operation Main() { use q = Qubit[1]; Helper(q[0]); }
            """);

        Assert.Contains("mutable array[int, #dim = 1] g_)", result.Targets.OpenQasm!.Text);   // the array parameter
        Assert.Contains("for int g__ in", result.Targets.OpenQasm!.Text);                     // the loop variable, split apart
        Assert.Contains("g_[0] == 1", result.Targets.OpenQasm!.Text);                         // the array reference points at the parameter
        Assert.Contains("Helper(q[0], Helper_g);", result.Targets.OpenQasm!.Text);            // the backing global is distinct too
    }

    /// <summary>A minted backing global and a user variable declared inside a NESTED block of the entry op
    /// get DISTINCT names — the mangler flattens the whole entry body into one global scope, so the two
    /// same-spelled entities are split (here the nested user scalar takes the `_`).</summary>
    [Fact]
    public void MintedGlobalAndNestedEntryDeclarationGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation SetTable(q: Qubit) { var tbl: int[] = [1, 2, 3]; if (tbl[0] == 1) { X(q); } }
            operation Main() {
                use q = Qubit[2];
                var flag: int = 1;
                if (flag == 1) { var SetTable_tbl: int = 1; if (SetTable_tbl == 1) { X(q[1]); } }
                SetTable(q[0]);
            }
            """);

        Assert.Contains("array[int, 3] SetTable_tbl = {0, 0, 0};", result.Targets.OpenQasm!.Text);   // the backing global
        Assert.Contains("int SetTable_tbl_ = 1;", result.Targets.OpenQasm!.Text);                    // the nested user scalar, split apart
        Assert.Contains("if (SetTable_tbl_ == 1)", result.Targets.OpenQasm!.Text);                   // its own use follows the rename
        Assert.Contains("SetTable(q[0], SetTable_tbl);", result.Targets.OpenQasm!.Text);
    }

    /// <summary>A pass-through parameter and a caller body-local of the same spelling get DISTINCT names
    /// (here the body-local takes the `_`), so the def has no duplicate name.</summary>
    [Fact]
    public void PassThroughParameterAndCallerBodyLocalGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation Inner(q: Qubit) { var t: int[] = [1, 2]; if (t[0] == 1) { X(q); } }
            operation Outer(q: Qubit) { var Inner_t: int = 0; Inner(q); if (Inner_t == 0) { X(q); } }
            operation Main() { use q = Qubit[1]; Outer(q[0]); }
            """);

        Assert.Contains("mutable array[int, #dim = 1] Inner_t)", result.Targets.OpenQasm!.Text);   // Outer's pass-through parameter
        Assert.Contains("int Inner_t_ = 0;", result.Targets.OpenQasm!.Text);                       // Outer's own scalar, split apart
        Assert.Contains("Inner(q, Inner_t);", result.Targets.OpenQasm!.Text);                      // the pass-through is forwarded
        Assert.Contains("Inner_t_ == 0", result.Targets.OpenQasm!.Text);                           // the scalar's use follows its rename
    }

    private static string Explain(Compilation result) =>
        string.Join(
            " | ",
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
}
