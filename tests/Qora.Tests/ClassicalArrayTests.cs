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

    /// <summary>Array locals go wherever a scalar goes — a helper op, a branch, a loop. The old QSEM012
    /// arm rejecting these was OpenQASM's placement rule leaking into the language; the QASM backend's
    /// ArrayLocalHoisting pass now absorbs it (hidden-parameter threading / scope-top hoisting), so the
    /// language accepts all three shapes it once rejected.</summary>
    [Theory]
    [InlineData("operation Work(){ int[] values=[1,2]; } operation Main(){ Work(); }")]
    [InlineData("operation Main(){ int flag=1; if(flag==1){ int[] values=[1,2]; } }")]
    [InlineData("operation Main(){ for i in 0..1 { int[] values=[1,2]; } }")]
    public void AcceptsArrayDeclarationsOutsideMainTopLevel(string source) => Compiler.Accepts(source);

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

    /// <summary>A WHOLE bit register compared to an int emits through the explicit spec cast
    /// (`int(f) == 1`) — the one spelling the Braket execution oracle actually runs (it crashes on the
    /// bare register-vs-int form, and both targets reject the old bool form). An ELEMENT stays a scalar
    /// bit with the bool rewrite.</summary>
    [Fact]
    public void WholeBitRegisterComparesViaAnIntCast()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; bit[] f = new bit[2]; if (f == 1) { X(q[0]); } }");
        Assert.True(r.Success, Explain(r));
        Assert.Contains("if (int(f) == 1)", r.Qasm);
        Assert.DoesNotContain("f == true", r.Qasm);
    }

    // --- bit[] PARAMETERS: length-specialized like Qubit[] (bit is not a valid array base type, so the
    //     only legal QASM parameter form is the sized register `bit[N]`) ---

    [Fact]
    public void BitArrayParameterSpecializesToASizedRegister()
    {
        var r = Compiler.Compile("""
            operation Read(bit[] values, Qubit q) {
                if (values[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                bit[] f = new bit[2];
                Read(f, q[0]);
            }
            """);
        Assert.True(r.Success, Explain(r));
        Assert.Contains("def Read__sz2(bit[2] values, qubit q)", r.Qasm);
        Assert.DoesNotContain("array[bit", r.Qasm);   // the invalid base type never appears
    }

    [Fact]
    public void BitArrayParameterCountFoldsToALiteralNotSizeof()
    {
        // sizeof is undefined on a bit register, so `f.Count` must fold to the specialized length.
        var r = Compiler.Compile("""
            operation Scan(bit[] f, Qubit q) {
                for i in 0..f.Count-1 { if (f[i] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                bit[] f = new bit[3];
                Scan(f, q[0]);
            }
            """);
        Assert.True(r.Success, Explain(r));
        Assert.Contains("[0:3 - 1]", r.Qasm);
        Assert.DoesNotContain("sizeof", r.Qasm);
    }

    [Fact]
    public void TwoBitArrayLengthsMakeTwoSpecializations()
    {
        var r = Compiler.Compile("""
            operation Read(bit[] values, Qubit q) {
                if (values[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[2];
                bit[] a = new bit[2];
                bit[] b = new bit[3];
                Read(a, q[0]);
                Read(b, q[1]);
            }
            """);
        Assert.True(r.Success, Explain(r));
        Assert.Contains("def Read__sz2(bit[2] values, qubit q)", r.Qasm);
        Assert.Contains("def Read__sz3(bit[3] values, qubit q)", r.Qasm);
    }

    /// <summary>A bit[] parameter is READ-ONLY: its QASM form is a by-value register, so a write would
    /// silently never reach the caller — rejected loudly instead (int[] passes by mutable reference; the
    /// asymmetry is OpenQASM's, and the ban keeps it unobservable at the Qora surface).</summary>
    [Theory]
    [InlineData("operation Zero(bit[] f, Qubit q){ f[0] = 0; H(q); }\noperation Main(){ use q=Qubit[1]; bit[] f = new bit[1]; Zero(f, q[0]); }")]
    [InlineData("operation Store(bit[] f, Qubit[] qs){ f[0] = M(qs[0]); }\noperation Main(){ use q=Qubit[1]; bit[] f = new bit[1]; Store(f, q); }")]
    public void RejectsWritingToABitArrayParameter(string source) =>
        Compiler.Rejects(source, "QSEM032");

    /// <summary>bit[] parameters are read-only by-value registers (QSEM032), so the MUTABLE-array rules
    /// must not apply to them: the same register may feed two bit[] slots (reads cannot conflict), and a
    /// const array is a perfectly fine argument.</summary>
    [Fact]
    public void DuplicateBitArrayArgumentsAreAcceptedReadsCannotConflict()
    {
        var r = Compiler.Compile("""
            operation Both(bit[] a, bit[] b, Qubit q) { if (a[0] == 1) { X(q); } if (b[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; bit[] f = new bit[2]; Both(f, f, q[0]); }
            """);
        Assert.True(r.Success, Explain(r));
    }

    [Fact]
    public void ConstBitArrayIsAValidArgumentToAReadOnlyBitParameter()
    {
        var r = Compiler.Compile("""
            operation Read(bit[] f, Qubit q) { if (f[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; const bit[] f = [0, 1]; Read(f, q[0]); }
            """);
        Assert.True(r.Success, Explain(r));
    }

    /// <summary>bit[] parameters specialize like Qubit[], so their bounds facts DEFER to the post-mono
    /// re-check the same way: a loop bounded by ANOTHER bit[] param's Count, and a constant guard
    /// `n &lt; K`, must both prove post-specialization instead of rejecting pre-mono (QSEM030).</summary>
    [Fact]
    public void BitArrayCrossCountLoopDefersAndProvesPostMono()
    {
        var r = Compiler.Compile("""
            operation Zip(bit[] a, bit[] b, Qubit q) {
                for i in 0..a.Count-1 { if (b[i] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                bit[] a = new bit[2];
                bit[] b = new bit[2];
                Zip(a, b, q[0]);
            }
            """);
        Assert.True(r.Success, Explain(r));
    }

    [Fact]
    public void BitArrayConstGuardDefersAndProvesPostMono()
    {
        var r = Compiler.Compile("""
            operation Pick(bit[] f, int n, Qubit q) {
                if (0 <= n && n < 2) { if (f[n] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                bit[] f = new bit[2];
                int n = 1;
                Pick(f, n, q[0]);
            }
            """);
        Assert.True(r.Success, Explain(r));
    }

    // --- ArrayLocalHoisting: a classical-array LOCAL in a def-emitted op is inexpressible in OpenQASM
    //     (arrays are global-or-parameter only, and defs cannot see mutable globals — scope.rst), so the
    //     QASM backend threads it as a hidden array-reference parameter backed by a global, with the
    //     declaration site becoming element-wise re-initialization (fresh value on every entry). ---

    [Fact]
    public void ThreadsHelperArrayLocalAsHiddenParameter()
    {
        var result = CompileSuccessfully("""
            operation SetTable(Qubit q) {
                int[] tbl = [1, 2, 3];
                if (tbl[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                SetTable(q[0]);
            }
            """);

        Assert.Contains("array[int, 3] SetTable_tbl = {0, 0, 0};", result.Qasm);   // global backing, default-init
        Assert.Contains("mutable array[int, #dim = 1] tbl", result.Qasm);          // hidden parameter on the def
        Assert.Contains("tbl[0] = 1;", result.Qasm);                               // declaration site = re-init
        Assert.Contains("tbl[2] = 3;", result.Qasm);
        Assert.Contains("SetTable(q[0], SetTable_tbl);", result.Qasm);             // caller supplies the backing
        Assert.DoesNotContain("array[int, 3] tbl", result.Qasm);                   // no array DECLARATION inside the def
        // the backing declaration precedes the call that hands it over
        Assert.True(result.Qasm.IndexOf("array[int, 3] SetTable_tbl")
                    < result.Qasm.IndexOf("SetTable(q[0], SetTable_tbl);"));
    }

    [Fact]
    public void ThreadsHiddenParameterTransitivelyThroughIntermediateDefs()
    {
        var result = CompileSuccessfully("""
            operation Inner(Qubit q) {
                int[] t = [4, 5];
                if (t[1] == 5) { X(q); }
            }
            operation Outer(Qubit q) {
                Inner(q);
            }
            operation Main() {
                use q = Qubit[1];
                Outer(q[0]);
            }
            """);

        Assert.Contains("array[int, 2] Inner_t = {0, 0};", result.Qasm);   // one backing global
        Assert.Contains("Inner(q, Inner_t);", result.Qasm);                // Outer hands its pass-through on
        Assert.Contains("Outer(q[0], Inner_t);", result.Qasm);             // Main names the global directly
    }

    [Fact]
    public void HoistsEntryNestedArrayDeclarationToTheGlobalTop()
    {
        var result = CompileSuccessfully("""
            operation Main() {
                use q = Qubit[1];
                bit b = M(q[0]);
                int n = b;
                if (n == 1) {
                    int[] a = [7, 8];
                    int x = a[0];
                }
            }
            """);

        Assert.Contains("array[int, 2] a = {0, 0};", result.Qasm);   // declaration at global top…
        Assert.Contains("a[0] = 7;", result.Qasm);                   // …site keeps element-wise re-init
        Assert.Contains("a[1] = 8;", result.Qasm);
        Assert.True(result.Qasm.IndexOf("array[int, 2] a") < result.Qasm.IndexOf("if ("));
    }

    /// <summary>bit[] locals are sized REGISTERS in OpenQASM — legal inside a def, not "arrays" — so the
    /// hoisting pass must leave them exactly where they are.</summary>
    [Fact]
    public void LeavesBitArrayLocalsInsideDefsUntouched()
    {
        var result = CompileSuccessfully("""
            operation Flag(Qubit q) {
                bit[] f = new bit[2];
                f[0] = 1;
                if (f[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                Flag(q[0]);
            }
            """);

        Assert.Contains("bit[2] f = \"00\";", result.Qasm);   // register declaration stays in the def
        Assert.Contains("Flag(q[0]);", result.Qasm);          // no hidden parameter added
        Assert.DoesNotContain("Flag_f", result.Qasm);         // no backing global minted
    }

    /// <summary>A bit[] NESTED in a control-flow block hoists only to the top of its own op (importers
    /// reject classical declarations inside blocks) — a register declaration is legal at def scope, so
    /// no threading and no backing global.</summary>
    [Fact]
    public void HoistsNestedBitArrayToItsOwnOpTop()
    {
        var result = CompileSuccessfully("""
            operation Tally(Qubit q, int n) {
                if (n == 1) {
                    bit[] f = new bit[2];
                    f[0] = 1;
                    if (f[0] == 1) { X(q); }
                }
            }
            operation Main() {
                use q = Qubit[1];
                bit b = M(q[0]);
                int n = b;
                Tally(q[0], n);
            }
            """);

        Assert.Contains("bit[2] f = \"00\";", result.Qasm);          // storage at the def's top
        Assert.Contains("f[0] = 0;", result.Qasm);                   // site re-initializes per entry
        Assert.Contains("Tally(q[0], n);", result.Qasm);             // signature unchanged — no threading
        Assert.DoesNotContain("Tally_f", result.Qasm);
        Assert.True(result.Qasm.IndexOf("bit[2] f") < result.Qasm.IndexOf("if ("));
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
            operation A(Qubit q) { int[] b_c = [1, 1]; if (b_c[1] == 1) { X(q); } }
            operation A_b(Qubit q) { int[] c = [9]; if (c[0] == 9) { X(q); } }
            operation Main() { use q = Qubit[2]; A(q[0]); A_b(q[1]); }
            """);

        Assert.Contains("array[int, 2] A_b_c = {0, 0};", result.Qasm);   // A's b_c
        Assert.Contains("array[int, 1] A_b_c_ = {0};", result.Qasm);     // A_b's c — split apart
        Assert.Contains("A(q[0], A_b_c);", result.Qasm);
        Assert.Contains("A_b(q[1], A_b_c_);", result.Qasm);
    }

    /// <summary>Vector 1 — a minted backing global and a user top-level variable of the same spelling get
    /// DISTINCT emitted names (here the user scalar takes the `_`), never one merged declaration.</summary>
    [Fact]
    public void MintedGlobalAndUserTopLevelNameGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation Foo(Qubit q) { int[] bar = [1]; if (bar[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; int Foo_bar = 5; Foo(q[0]); if (Foo_bar == 5) { X(q[0]); } }
            """);

        Assert.Contains("array[int, 1] Foo_bar = {0};", result.Qasm);   // the backing global
        Assert.Contains("int Foo_bar_ = 5;", result.Qasm);             // the user scalar, split apart
        Assert.Contains("Foo(q[0], Foo_bar);", result.Qasm);           // the call supplies the backing global
        Assert.Contains("Foo_bar_ == 5", result.Qasm);                 // the user scalar's own use follows its rename
    }

    /// <summary>Vector 3 — a pass-through parameter (named after the global it forwards) must be freshened
    /// away from an owned parameter of the same spelling, so the def has no duplicate parameter.</summary>
    [Fact]
    public void PassThroughParameterIsFreshenedAwayFromAnOwnedParameter()
    {
        var result = CompileSuccessfully("""
            operation D(Qubit q) { int[] g = [1]; if (g[0] == 1) { X(q); } }
            operation Mid(Qubit q) { int[] D_g = [7]; if (D_g[0] == 7) { X(q); } D(q); }
            operation Main() { use q = Qubit[1]; Mid(q[0]); }
            """);

        // Mid owns array `D_g` (its own param) AND forwards D's global `D_g` — the two params must differ.
        Assert.Contains("def Mid(qubit q, mutable array[int, #dim = 1] D_g, mutable array[int, #dim = 1] D_g_) {", result.Qasm);
        Assert.Contains("D(q, D_g_);", result.Qasm);              // D receives the forwarded (freshened) slot
        Assert.Contains("Mid(q[0], Mid_D_g, D_g);", result.Qasm); // Main supplies Mid's own + D's backing
    }

    /// <summary>Vector 4 — an array local that shadows a same-named parameter gets a freshened parameter,
    /// and ONLY the array's in-scope references are rewritten to it; the shadowed parameter's own
    /// references (here the enclosing `if (a == 0)`) are left intact.</summary>
    [Fact]
    public void ArrayLocalShadowingAParameterRewritesOnlyItsOwnReferences()
    {
        var result = CompileSuccessfully("""
            operation Helper(Qubit[] q, int a) {
                if (a == 0) {
                    int[] a = [1, 2];
                    if (a[0] == 1) { X(q[0]); }
                }
            }
            operation Main() { use q = Qubit[1]; Helper(q, 0); }
            """);

        Assert.Contains("if (a == 0)", result.Qasm);        // the PARAMETER comparison — untouched
        Assert.Contains("a_[0] = 1;", result.Qasm);         // the ARRAY — freshened and rewritten
        Assert.Contains("if (a_[0] == 1)", result.Qasm);
        Assert.DoesNotContain("if (a_ == 0)", result.Qasm); // the rename must NOT leak to the param comparison
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
            operation g(Qubit q) { X(q); }
            operation Helper(Qubit q) {
                int[] g = [1];
                for g_ in 0..0 { if (g[0] == 1) { X(q); } }
            }
            operation Main() { use q = Qubit[1]; Helper(q[0]); }
            """);

        Assert.Contains("mutable array[int, #dim = 1] g_)", result.Qasm);   // the array parameter
        Assert.Contains("for int g__ in", result.Qasm);                     // the loop variable, split apart
        Assert.Contains("g_[0] == 1", result.Qasm);                         // the array reference points at the parameter
        Assert.Contains("Helper(q[0], Helper_g);", result.Qasm);            // the backing global is distinct too
    }

    /// <summary>A minted backing global and a user variable declared inside a NESTED block of the entry op
    /// get DISTINCT names — the mangler flattens the whole entry body into one global scope, so the two
    /// same-spelled entities are split (here the nested user scalar takes the `_`).</summary>
    [Fact]
    public void MintedGlobalAndNestedEntryDeclarationGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation SetTable(Qubit q) { int[] tbl = [1, 2, 3]; if (tbl[0] == 1) { X(q); } }
            operation Main() {
                use q = Qubit[2];
                int flag = 1;
                if (flag == 1) { int SetTable_tbl = 1; if (SetTable_tbl == 1) { X(q[1]); } }
                SetTable(q[0]);
            }
            """);

        Assert.Contains("array[int, 3] SetTable_tbl = {0, 0, 0};", result.Qasm);   // the backing global
        Assert.Contains("int SetTable_tbl_ = 1;", result.Qasm);                    // the nested user scalar, split apart
        Assert.Contains("if (SetTable_tbl_ == 1)", result.Qasm);                   // its own use follows the rename
        Assert.Contains("SetTable(q[0], SetTable_tbl);", result.Qasm);
    }

    /// <summary>A pass-through parameter and a caller body-local of the same spelling get DISTINCT names
    /// (here the body-local takes the `_`), so the def has no duplicate name.</summary>
    [Fact]
    public void PassThroughParameterAndCallerBodyLocalGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation Inner(Qubit q) { int[] t = [1, 2]; if (t[0] == 1) { X(q); } }
            operation Outer(Qubit q) { int Inner_t = 0; Inner(q); if (Inner_t == 0) { X(q); } }
            operation Main() { use q = Qubit[1]; Outer(q[0]); }
            """);

        Assert.Contains("mutable array[int, #dim = 1] Inner_t)", result.Qasm);   // Outer's pass-through parameter
        Assert.Contains("int Inner_t_ = 0;", result.Qasm);                       // Outer's own scalar, split apart
        Assert.Contains("Inner(q, Inner_t);", result.Qasm);                      // the pass-through is forwarded
        Assert.Contains("Inner_t_ == 0", result.Qasm);                           // the scalar's use follows its rename
    }

    private static string Explain(QoraParseResult result) =>
        string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}"));
}
