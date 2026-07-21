using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Rung B' — an array or qubit-register index must be PROVABLY in bounds at compile time. A runtime index
/// with no proof is rejected (QSEM030), never deferred to run time: OpenQASM 3 has no runtime bounds check,
/// so a check would silently no-op on hardware. Proof paths: a literal within a known length (P1); a loop
/// over <c>0..a.Count-1</c> (P2); a loop whose maximum index folds to a constant, with each <c>.Count</c>
/// resolved to its known length (P3); a call-site minimum-length floor for array parameters (P4); and a
/// programmer guard <c>if (0 &lt;= n &amp;&amp; n &lt; a.Count)</c> that narrows the index in its then-branch (P5).
/// The mechanism mirrors TypeScript/Kotlin flow narrowing (occurrence typing) applied to integer ranges,
/// as Google Wuffs does for array bounds.
/// </summary>
public class BoundsProofTests
{
    // --- the escape hatch that makes a runtime index usable: a guard narrows it in the then-branch (P5) ---

    [Theory]
    [InlineData("if (0 <= n && n < a.Count)")]
    [InlineData("if (n >= 0 && n < a.Count)")]
    public void AcceptsARuntimeIndexInsideAGuard(string guard) =>
        Compiler.Accepts($$"""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                {{guard}} { a[n] = 1; }
                H(q[1]);
            }
            """);

    [Fact]
    public void RejectsABareRuntimeIndexIntoAClassicalArray() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                a[n] = 1;
                H(q[1]);
            }
            """, "QSEM030");

    [Fact]
    public void RejectsABareRuntimeIndexIntoAQubitRegister() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[3];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                H(q[n]);
            }
            """, "QSEM030");

    /// <summary>The guard proves only the array it names — a different array is still unproven.</summary>
    [Fact]
    public void RejectsAGuardedIndexAppliedToADifferentArray() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit c = M(q[0]);
                int n = c;
                int[] a = [1, 2, 3];
                int[] b = [1, 2];
                if (0 <= n && n < a.Count) { b[n] = 1; }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>Reassigning the guarded name inside the branch drops the proof — the new value is unbounded.</summary>
    [Fact]
    public void RejectsAGuardedIndexAfterTheIndexIsReassigned() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    n = n + 9;
                    a[n] = 1;
                }
                H(q[1]);
            }
            """, "QSEM030");

    // --- P3: a constant-bounded loop is the literal access at its maximum index ---

    [Fact]
    public void RejectsAConstantBoundedLoopThatReachesOutOfRange() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..5 { a[i] = 1; }
                H(q[0]);
            }
            """, "QSEM016");

    [Fact]
    public void AcceptsAConstantBoundedLoopWithinRange() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[3]; for i in 0..2 { H(q[i]); } }");

    /// <summary>A <c>const</c> upper bound folds like a literal.</summary>
    [Fact]
    public void RejectsAConstBoundedLoopThatReachesOutOfRange() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                const int k = 5;
                int[] a = [1, 2, 3];
                for i in 0..k { a[i] = 1; }
                H(q[0]);
            }
            """, "QSEM016");

    // --- P2: a `.Count-1` loop is in range for any length, including a size-independent parameter ---

    [Fact]
    public void AcceptsACountBoundedLoopOverAParameter() =>
        Compiler.Accepts("""
            operation Visit(Qubit[] q) {
                for i in 0..q.Count-1 { H(q[i]); }
            }
            operation Main() {
                use r = Qubit[3];
                Visit(r);
            }
            """);

    // --- cross-array loops: sound, because each `.Count` resolves to the array's known length ---

    /// <summary>The measurement idiom: measure each qubit into a bit array of the same size. The loop is
    /// bounded by <c>results.Count</c> while indexing <c>q</c> — proven because both sizes are known.</summary>
    [Fact]
    public void AcceptsMeasuringEachQubitIntoAParallelBitArray() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                bit[] results = new bit[2];
                for i in 0..results.Count-1 {
                    results[i] = M(q[i]);
                }
            }
            """);

    /// <summary>A loop bounded by a LONGER array's count, indexing a shorter one, is caught — the maximum
    /// index provably exceeds the shorter array's length.</summary>
    [Fact]
    public void RejectsACrossArrayLoopWhoseBoundExceedsTheIndexedArray() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                int[] b = [1, 2];
                for i in 0..a.Count-1 { b[i] = 1; }
                H(q[0]);
            }
            """, "QSEM016");

    // --- reassignment of a guarded index drops the proof, DIRECTLY or through a nested block ---

    [Fact]
    public void RejectsAGuardedIndexReassignedInsideANestedLoop() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    for i in 0..0 { n = n + 9; }
                    a[n] = 1;
                }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>The guard still proves an access that comes BEFORE the reassignment — precise, not blanket.</summary>
    [Fact]
    public void AcceptsAGuardedIndexUsedBeforeItIsReassigned() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    a[n] = 1;
                    n = n + 9;
                }
                H(q[1]);
            }
            """);

    // --- a loop whose lower bound is a non-zero non-negative literal is still bounded by its maximum (To) ---

    [Fact]
    public void AcceptsALoopStartingAboveZero() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 1..a.Count-1 { a[i] = 1; }
                H(q[0]);
            }
            """);

    [Fact]
    public void RejectsALoopStartingAboveZeroWhoseMaximumIsOutOfRange() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; for i in 2..5 { a[i]=1; } H(q[0]); }", "QSEM016");

    // --- P5 through a monomorphized array: the guard's `.Count` becomes a concrete literal after
    //     specialization (`n < q.Count` -> `n < 2`) and must still prove the index ---

    [Fact]
    public void AcceptsAGuardedIndexIntoAQubitRegister() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                if (0 <= n && n < q.Count) { H(q[n]); }
            }
            """);

    [Fact]
    public void AcceptsAGuardedIndexIntoABitArray() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                bit[] r = new bit[2];
                if (0 <= n && n < r.Count) { r[n] = 1; }
            }
            """);

    // --- a constant-bounded loop over a classical-array PARAMETER states a call-site precondition, exactly
    //     like a literal index does: the maximum index the loop reaches becomes the argument's minimum length ---

    [Fact]
    public void RejectsAConstLoopOverAParameterWhenTheArgumentIsTooShort() =>
        Compiler.Rejects("""
            operation Helper(int[] x) {
                for i in 0..5 { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """, "QSEM016");

    [Fact]
    public void AcceptsAConstLoopOverAParameterWhenTheArgumentIsLongEnough() =>
        Compiler.Accepts("""
            operation Helper(int[] x) {
                for i in 0..5 { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2, 3, 4, 5, 6];
                Helper(a);
            }
            """);

    // --- bounds are EVALUATED, not pattern-matched: any `+ - * /` arithmetic over literals, `const`
    //     names and `.Count` folds to its value when every leaf is known, and the verdict is exact ---

    /// <summary>`a.Count*2 - 4` = 2 for a 3-element array — in range, however the bound is spelled.</summary>
    [Fact]
    public void AcceptsAnArithmeticBoundThatEvaluatesInRange() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..a.Count*2-4 { a[i] = 1; }
                H(q[0]);
            }
            """);

    /// <summary>`a.Count*2 - 3` = 3 for a 3-element array — provably one past the end.</summary>
    [Fact]
    public void RejectsAnArithmeticBoundThatEvaluatesOutOfRange() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..a.Count*2-3 { a[i] = 1; }
                H(q[0]);
            }
            """, "QSEM016");

    /// <summary>Multiple `const` terms fold too: `a.Count - k - l` = 1 for k = l = 1.</summary>
    [Fact]
    public void AcceptsABoundMixingCountAndSeveralConsts() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                const int k = 1;
                const int l = 1;
                int[] a = [1, 2, 3];
                for i in 0..a.Count-k-l { a[i] = 1; }
                H(q[0]);
            }
            """);

    /// <summary>One unresolvable leaf (`n` is a runtime value) means the computation never settles — no
    /// value, no proof, rejected. OpenQASM 3 has no runtime bounds check to fall back on.</summary>
    [Fact]
    public void RejectsABoundContainingARuntimeValue() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                for i in 0..a.Count-n { a[i] = 1; }
            }
            """, "QSEM030");

    // --- a classical-array PARAMETER's length never becomes concrete, so its `.Count` bounds are judged
    //     symbolically: `Count-K` (K >= 1) is in range for ANY length; `Count` itself or past it is out of
    //     range for EVERY length ---

    /// <summary>`0..a.Count` reaches index `a.Count` — the classic off-by-one, wrong for every length.</summary>
    [Fact]
    public void RejectsACountBoundedLoopOverAParameterReachingCountItself() =>
        Compiler.Rejects("""
            operation Helper(int[] a) {
                for i in 0..a.Count { a[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2, 3];
                Helper(a);
            }
            """, "QSEM016");

    /// <summary>`a.Count - 2` on an unknown length is at most `Count-1` — safe for any argument.</summary>
    [Fact]
    public void AcceptsACountMinusTwoLoopOverAParameter() =>
        Compiler.Accepts("""
            operation Helper(int[] a) {
                for i in 0..a.Count-2 { a[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2, 3];
                Helper(a);
            }
            """);

    /// <summary>`a.Count*2 - 1` reaches past the end of `a` for every non-empty length.</summary>
    [Fact]
    public void RejectsADoubledCountLoopOverAParameter() =>
        Compiler.Rejects("""
            operation Helper(int[] a) {
                for i in 0..a.Count*2-1 { a[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2, 3];
                Helper(a);
            }
            """, "QSEM016");

    /// <summary>A loop bounded by ONE parameter's count indexing ANOTHER parameter proves nothing — the two
    /// lengths are unrelated and neither ever becomes concrete. Rejected, not deferred.</summary>
    [Fact]
    public void RejectsACrossArrayLoopOverTwoParameters() =>
        Compiler.Rejects("""
            operation Helper(int[] x, int[] y) {
                for i in 0..y.Count-1 { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2, 3];
                int[] b = [1, 2, 3];
                Helper(a, b);
            }
            """, "QSEM030");

    // --- Qubit[] parameters DO become concrete (monomorphization), so their cross-register loops defer
    //     to the post-specialization pass and are then judged with real sizes ---

    [Fact]
    public void AcceptsACrossRegisterLoopOverQubitParametersOfEqualSize() =>
        Compiler.Accepts("""
            operation Visit(Qubit[] q, Qubit[] r) {
                for i in 0..r.Count-1 { H(q[i]); }
            }
            operation Main() {
                use a = Qubit[3];
                use b = Qubit[3];
                Visit(a, b);
            }
            """);

    [Fact]
    public void RejectsACrossRegisterLoopWhoseBoundRegisterIsLonger() =>
        Compiler.Rejects("""
            operation Visit(Qubit[] q, Qubit[] r) {
                for i in 0..r.Count-1 { H(q[i]); }
            }
            operation Main() {
                use a = Qubit[2];
                use b = Qubit[3];
                Visit(a, b);
            }
            """, "QSEM016");

    /// <summary>The off-by-one on a Qubit[] parameter is caught symbolically, before specialization.</summary>
    [Fact]
    public void RejectsACountBoundedLoopOverAQubitParameterReachingCountItself() =>
        Compiler.Rejects("""
            operation Visit(Qubit[] q) {
                for i in 0..q.Count { H(q[i]); }
            }
            operation Main() {
                use a = Qubit[3];
                Visit(a);
            }
            """, "QSEM016");

    // --- a `for` header BINDS its variable anew: an outer guard on the same name proved a DIFFERENT
    //     variable, so it must not leak onto the shadowing loop variable ---

    /// <summary>The outer guard proved the OUTER n; the loop's n is a new binding ranging over a runtime
    /// bound. Without the wipe this compiled and wrote a[3..9] on a 3-element array.</summary>
    [Fact]
    public void RejectsALoopVariableShadowingAGuardedNameWhenItsBoundIsRuntime() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int m = b * 9;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    for n in 0..m { a[n] = 1; }
                }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>The shadowing loop variable can still be proven by its OWN bound — the wipe removes the
    /// leaked guard, not the loop's legitimate P2 proof.</summary>
    [Fact]
    public void AcceptsALoopVariableShadowingAGuardedNameWithASafeBound() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    for n in 0..a.Count-1 { a[n] = 1; }
                }
                H(q[1]);
            }
            """);

    /// <summary>Facts outlive the verdict, so the tree they describe must too: when the POST-MONO validation
    /// rejects, the specialized ops its facts are keyed by exist only in the monomorphized tree — exposed as
    /// MonoIr so a consumer can resolve the contract of the op that was actually judged.</summary>
    [Fact]
    public void KeepsASpecializedOpsContractReachableWhenThePostMonoValidationRejects()
    {
        var r = Compiler.Compile("""
            operation Helper(Qubit[] q, int[] x) {
                for i in 0..q.Count*2-2 { x[i] = 1; }
            }
            operation Main() {
                use r = Qubit[5];
                int[] a = [1, 2];
                Helper(r, a);
            }
            """);
        Assert.False(r.Success);
        Assert.NotNull(r.MonoIr);
        var specialized = r.MonoIr!.Operations.Single(o => o.Name.Contains("Helper"));
        var contract = r.Semantics!.RequiredArgLengths(specialized.Id);
        Assert.NotNull(contract);
        Assert.Equal(9L, contract!["x"]);
    }

    // --- gate operands are compared by FOLDED index value, never by spelling: two spellings of one
    //     qubit cannot pass as distinct operands ---

    [Fact]
    public void RejectsAGateReceivingTheSameQubitSpelledAsAConstAndALiteral() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[3]; const int k = 2; CNOT(q[k], q[2]); }", "QSEM014");

    [Fact]
    public void AcceptsAGateWhoseConstIndexFoldsToADifferentQubit() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[3]; const int k = 1; CNOT(q[k], q[2]); }");

    // --- bound arithmetic is 64-bit and CHECKED: a computation that wraps is not a value ---

    /// <summary>`2000000000*2` used to wrap negative and prove the loop "empty"; it now folds to four
    /// billion and is out of range for any array.</summary>
    [Theory]
    [InlineData("0..2000000000*2")]
    [InlineData("0..65536*65536")]
    [InlineData("0..3000000000")]
    public void RejectsALoopWhoseBoundExceedsAnyPossibleArray(string range) =>
        Compiler.Rejects($$"""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in {{range}} { a[i] = 1; }
                H(q[0]);
            }
            """, "QSEM016");

    // --- guard facts ACCUMULATE, tightest wins: a weaker inner conjunct or nested guard never erases a
    //     stronger outer proof ---

    /// <summary>The outer `n &lt; 2` proves the access on a 3-element array; the inner weaker `n &lt; 9`
    /// used to OVERWRITE it and reject, order-dependently.</summary>
    [Fact]
    public void KeepsTheTighterOuterGuardWhenANestedWeakerGuardFollows() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < 2) {
                    if (0 <= n && n < 9) {
                        a[n] = 1;
                    }
                }
                H(q[1]);
            }
            """);

    // --- diagnostics point at the bound that actually failed ---

    /// <summary>A settled NEGATIVE From that provably executes starts at a negative index — proven wrong
    /// (QSEM016), not "cannot be determined".</summary>
    [Fact]
    public void RejectsALoopStartingAtAProvablyNegativeIndexAsProvenWrong() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; for i in 0-1..2 { a[i]=1; } H(q[0]); }", "QSEM016");

    /// <summary>When the FROM bound is the one that never settles, the recorded data (and so the QSEM030
    /// message) must blame From — not the innocent To.</summary>
    [Fact]
    public void BlamesTheFromBoundWhenItIsTheOneThatFailedToSettle()
    {
        var r = Compiler.Compile("""
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
                bit b = M(q[0]);
                int m = b;
                int[] a = [1, 2, 3];
                for i in m..2 { a[i] = 1; }
            }
            """);
        Assert.False(r.Success);
        var u = Assert.Single(r.Semantics!.UnprovenIndexes);
        Assert.Equal("m", u.LoopBound);
    }

    // --- a measurement into a NON-bit array-literal element is a type error (QSEM017), like every other
    //     measure-into-non-bit position — it would otherwise emit `measure` inside a classical initializer ---

    [Fact]
    public void RejectsAMeasurementIntoANonBitArrayLiteral() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                int[] a = [M(q[0]), M(q[1])];
                H(q[0]);
            }
            """, "QSEM017");

    [Fact]
    public void AcceptsAMeasurementIntoABitArrayLiteral() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[2]; bit[] r = [M(q[0]), M(q[1])]; }");

    /// <summary>A direct measure with an out-of-range target reports ONE diagnostic, not two — the measure
    /// index is bounds-checked once (via CheckExprIndexes), not also by a redundant dedicated check.</summary>
    [Fact]
    public void ReportsASingleDiagnosticForAnOutOfRangeMeasureTarget()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[2]; bit x = M(q[5]); H(q[0]); }");
        Assert.False(r.Success);
        Assert.Single(r.Errors.Where(e => e.Code == "QSEM016"));
    }

    /// <summary>An unproven measure index records ONE UnprovenIndexes entry, not two.</summary>
    [Fact]
    public void RecordsASingleUnprovenEntryForAnUnprovenMeasureTarget()
    {
        var r = Compiler.Compile("""
            operation Foo(Qubit[] q) {
                bit a = M(q[0]);
                int n = a;
                bit c = M(q[n]);
            }
            operation Main() { use r = Qubit[3]; Foo(r); }
            """);
        Assert.False(r.Success);
        Assert.Single(r.Semantics!.UnprovenIndexes);
    }

    /// <summary>A measurement in a WHILE condition lowers to two statements (a pre-loop measure and an
    /// end-of-body re-measure sharing one span). An out-of-range measured index must still report ONCE —
    /// value-equal diagnostics collapse — not once per lowered copy.</summary>
    [Fact]
    public void ReportsASingleDiagnosticForAnOutOfRangeMeasureInAWhileCondition()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[3]; while (M(q[9]) == 1) { } }");
        Assert.False(r.Success);
        Assert.Single(r.Errors.Where(e => e.Code == "QSEM016"));
    }

    /// <summary>...and an UNPROVEN measured index in a while condition records ONE UnprovenIndexes entry, not
    /// one per lowered copy.</summary>
    [Fact]
    public void RecordsASingleUnprovenEntryForAMeasureInAWhileCondition()
    {
        var r = Compiler.Compile("""
            operation Foo(Qubit[] q) {
                bit a = M(q[0]);
                int n = a;
                while (M(q[n]) == 1) { }
            }
            operation Main() { use r = Qubit[3]; Foo(r); }
            """);
        Assert.False(r.Success);
        Assert.Single(r.Semantics!.UnprovenIndexes);
    }

    // --- a `while` condition guard narrows the loop body (re-established each iteration), applied after the
    //     back-edge wipe: an access is proven as long as it precedes any reassignment within the iteration ---

    [Fact]
    public void AcceptsAWhileConditionGuardNarrowingTheBody() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                while (0 <= n && n < a.Count) { a[n] = 1; }
                H(q[0]);
            }
            """);

    /// <summary>The condition re-establishes the guard each iteration, so an access BEFORE a reassignment is
    /// still proven even though the body reassigns the index.</summary>
    [Fact]
    public void AcceptsAWhileGuardedAccessBeforeAReassignmentInTheBody() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                while (0 <= n && n < a.Count) { a[n] = 1; n = 0; }
                H(q[0]);
            }
            """);

    /// <summary>But an access AFTER a reassignment runs with an unguarded value — the per-statement
    /// invalidation drops the guard, so it is correctly rejected.</summary>
    [Fact]
    public void RejectsAWhileGuardedAccessAfterAReassignmentInTheBody() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                while (0 <= n && n < a.Count) { n = n + 100; a[n] = 1; }
                H(q[0]);
            }
            """, "QSEM030");

    /// <summary>A const aliasing an unsized Qubit[]'s `.Count` works as a guard upper bound, exactly as the
    /// direct `n &lt; q.Count` does — the folded (symbolic) value is read the same way.</summary>
    [Fact]
    public void AcceptsAConstOfQubitCountAsAGuardUpperBound() =>
        Compiler.Accepts("""
            operation Foo(Qubit[] q) {
                bit b = M(q[0]);
                int n = b;
                const int k = q.Count;
                if (0 <= n && n < k) { H(q[n]); }
            }
            operation Main() {
                use r = Qubit[3];
                Foo(r);
            }
            """);

    // --- a measurement inside a condition is hoisted to a bit, but the condition's parsed tree is kept in
    //     sync (the measurement node becomes the bit reference), so indexes and guard facts in the rest of
    //     the condition are NOT lost ---

    /// <summary>An out-of-range array index in a condition that also contains a measurement is still caught —
    /// the tree survives the measure-hoisting rewrite.</summary>
    [Fact]
    public void RejectsAnOutOfRangeIndexInAConditionContainingAMeasurement() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                int[] a = [1, 2, 3];
                if (M(q[0]) == a[5]) { H(q[1]); }
            }
            """, "QSEM016");

    /// <summary>A guard conjoined with a measurement still narrows the index — the guard facts survive the
    /// rewrite, so the guarded access is accepted (not falsely rejected).</summary>
    [Fact]
    public void KeepsGuardFactsInAConditionContainingAMeasurement() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit c = M(q[0]);
                int n = c;
                int[] a = [1, 2, 3];
                if (M(q[1]) == 1 && 0 <= n && n < a.Count) { a[n] = 1; }
            }
            """);

    /// <summary>A measurement nested in an array-literal initializer has its target index bounds-checked —
    /// the recursion into array elements now handles the measurement case.</summary>
    [Fact]
    public void RejectsAnOutOfRangeMeasurementIndexInsideAnArrayLiteral() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                bit[] r = [M(q[3])];
                H(q[0]);
            }
            """, "QSEM016");

    /// <summary>A ByConst guard (`n &lt; 3`) on an UNSIZED Qubit[] parameter defers to the post-mono pass like
    /// a literal index, rather than being rejected pre-mono — the guarded access proves a strict subset of a
    /// deferred literal access.</summary>
    [Fact]
    public void AcceptsAConstGuardedIndexIntoAnUnsizedQubitParameter() =>
        Compiler.Accepts("""
            operation Foo(Qubit[] q) {
                bit b = M(q[0]);
                int n = b;
                if (0 <= n && n < 3) { H(q[n]); }
            }
            operation Main() {
                use r = Qubit[3];
                Foo(r);
            }
            """);

    /// <summary>...but if the register turns out SMALLER than the guard's constant, the post-mono re-check
    /// rejects it — the deferral is sound, not a blanket accept.</summary>
    [Fact]
    public void RejectsAConstGuardedIndexWhenTheQubitParameterIsSmallerThanTheConstant() =>
        Compiler.Rejects("""
            operation Foo(Qubit[] q) {
                bit b = M(q[0]);
                int n = b;
                if (0 <= n && n < 3) { H(q[n]); }
            }
            operation Main() {
                use r = Qubit[2];
                Foo(r);
            }
            """, "QSEM030");

    // --- point-of-declaration scoping: a name may not be used before its own-scope declaration. A later
    //     same-block const/var/bit shadows an outer value, and an EARLIER use binds to the outer — but the
    //     validator's completed-dictionary lookup would read the later local, so the two would disagree
    //     (index folded to the wrong value, duplicate qubit missed). Rejected at build time (QSEM025). ---

    [Fact]
    public void RejectsAnIndexUsedBeforeAShadowingConstDeclaredLaterInTheBlock() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[3];
                const int n = 9;
                if (true) {
                    H(q[n]);
                    const int n = 0;
                }
            }
            """, "QSEM025");

    [Fact]
    public void RejectsAGateOperandUsedBeforeAShadowingConstDeclaredLaterInTheBlock() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[3];
                const int n = 1;
                if (true) {
                    CNOT(q[n], q[1]);
                    const int n = 0;
                }
            }
            """, "QSEM025");

    [Fact]
    public void RejectsALoopBoundUsedBeforeAShadowingConstDeclaredLaterInTheBlock() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                const int k = 9;
                int[] a = [1, 2, 3];
                if (true) {
                    for i in 0..k { a[i] = 1; }
                    const int k = 2;
                }
                H(q[0]);
            }
            """, "QSEM025");

    /// <summary>The loop variable is used at `a[i]`, then a body-local `const int i` shadows it later — the
    /// same hole through the loop variable rather than an outer const.</summary>
    [Fact]
    public void RejectsALoopVariableUsedBeforeAShadowingConstInTheBody() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..5 {
                    a[i] = 1;
                    const int i = 0;
                }
                H(q[0]);
            }
            """, "QSEM025");

    /// <summary>A register name reused as a later const in a block where the register is indexed earlier —
    /// the same hole (previously miscoded as "scalar cannot be indexed").</summary>
    [Fact]
    public void RejectsARegisterIndexedBeforeAShadowingConstDeclaredLaterInTheBlock() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[3];
                if (true) {
                    H(q[0]);
                    const int q = 5;
                }
            }
            """, "QSEM025");

    /// <summary>Normal shadowing — the inner declaration comes BEFORE its use — is untouched.</summary>
    [Fact]
    public void AcceptsAShadowingConstDeclaredBeforeItsUse() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[3];
                const int n = 1;
                H(q[n]);
                if (true) {
                    const int n = 2;
                    H(q[n]);
                }
            }
            """);

    /// <summary>A const chain reading the OUTER value in its own initializer is exempt (the initializer use
    /// is at the declaration's own program point, not before it).</summary>
    [Fact]
    public void AcceptsAConstInitializerReadingTheOuterValueItShadows() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[5];
                const int n = 1;
                if (true) {
                    const int n = n + 1;
                    H(q[n]);
                }
            }
            """);

    // --- QSEM014 aliasing sees a loop variable's value range: a folded literal operand that the loop
    //     variable can equal is a duplicate qubit, whatever the spelling ---

    [Fact]
    public void RejectsAGateAliasingViaASingletonLoopVariable() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[3]; for i in 2..2 { CNOT(q[i], q[2]); } }", "QSEM014");

    /// <summary>A loop bound that DEFERS to monomorphization (a `Qubit[].Count` upper) must not be
    /// over-approximated to `[From..MaxValue]` for QSEM014 — the fan-in idiom `for i in 0..q.Count-2 {
    /// CNOT(q[i], q[last]) }` never aliases the fixed last operand, and post-mono re-checks with the real
    /// range. The aliasing check now defers exactly as the bounds check does.</summary>
    [Fact]
    public void AcceptsTheFanInIdiomOverAQubitParameter() =>
        Compiler.Accepts("""
            operation Foo(Qubit[] q) {
                for i in 0..q.Count-2 { CNOT(q[i], q[4]); }
            }
            operation Main() {
                use r = Qubit[5];
                Foo(r);
            }
            """);

    /// <summary>...but a loop that DOES reach the fixed operand still aliases and is rejected post-mono —
    /// `0..q.Count-1` reaches `q[4]` on a 5-qubit register.</summary>
    [Fact]
    public void RejectsAFanInWhoseLoopReachesTheFixedOperand() =>
        Compiler.Rejects("""
            operation Foo(Qubit[] q) {
                for i in 0..q.Count-1 { CNOT(q[i], q[4]); }
            }
            operation Main() {
                use r = Qubit[5];
                Foo(r);
            }
            """, "QSEM014");

    [Fact]
    public void RejectsAGateAliasingWhenTheLoopRangeContainsTheLiteralOperand() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[2]; for i in 0..1 { CNOT(q[i], q[1]); } }", "QSEM014");

    [Fact]
    public void AcceptsAGateWhoseLoopRangeCannotReachTheLiteralOperand() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[5]; for i in 0..1 { CNOT(q[i], q[4]); } }");

    /// <summary>Classical-array elements are not qubit operands — passing the same element twice as a value
    /// argument is fine, not a duplicate-qubit error.</summary>
    [Fact]
    public void AcceptsTheSameClassicalArrayElementPassedTwiceAsValueArguments() =>
        Compiler.Accepts("""
            operation Use(int a, int b) { }
            operation Main() {
                int[] x = [1, 2, 3];
                Use(x[0], x[0]);
            }
            """);

    /// <summary>A loop variable whose lower bound settles but whose upper bound is runtime still starts at
    /// From on its guaranteed first iteration — a literal operand equal to a reachable value provably
    /// aliases. Here i=0 always runs, so CNOT(q[i], q[0]) is cx q[0],q[0].</summary>
    [Fact]
    public void RejectsAliasingViaALoopVariableWithASettledLowerBoundAndRuntimeUpper() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[3];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                for i in 0..n {
                    if (0 <= i && i < q.Count) { CNOT(q[i], q[0]); }
                }
            }
            """, "QSEM014");

    /// <summary>The same shape but the loop starts ABOVE the literal operand — i never reaches 0, so no
    /// aliasing however large the runtime upper bound is.</summary>
    [Fact]
    public void AcceptsALoopStartingAboveTheLiteralOperandWithARuntimeUpperBound() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[5];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                for i in 1..n {
                    if (0 <= i && i < q.Count) { CNOT(q[i], q[0]); }
                }
            }
            """);

    /// <summary>A provably-empty loop never runs its gate, so no operand aliasing is possible — the
    /// same-spelling CNOT(q[i], q[i]) must be accepted, consistently with the distinct-spelling case.</summary>
    [Fact]
    public void AcceptsASameOperandGateInsideAProvablyEmptyLoop() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[3]; for i in 5..3 { CNOT(q[i], q[i]); } H(q[0]); }");

    // --- a measure bit HOISTS to a flat top-level `bit r;`, so it shares the emitted top-level namespace
    //     with a root-scope const/var of the same name — a duplicate top-level declaration, rejected ---

    [Fact]
    public void RejectsAMeasureBitReusingARootLevelConstName() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[3];
                const int m = 1;
                if (true) { bit m = M(q[0]); }
            }
            """, "QSEM015");

    [Fact]
    public void RejectsAMeasureBitReusingARootLevelVarName() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                int m = 7;
                if (true) { bit m = M(q[0]); }
            }
            """, "QSEM015");

    /// <summary>A BLOCK-local classical stays in its own emitted scope and does not collide with a hoisted
    /// top-level measure bit — accepted.</summary>
    [Fact]
    public void AcceptsAMeasureBitAlongsideADisjointBlockLocalConstOfTheSameName() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[3];
                if (true) { const int m = 0; }
                if (true) { bit m = M(q[0]); }
            }
            """);

    // --- a const's value is folded ONCE at its declaration (Symbol.FoldedConst) and an index that folds
    //     to a definite value is judged exactly like a literal — the same calculator at every position,
    //     so `a[k]` and `for i in 0..k` can never read the same k differently ---

    /// <summary>A const-named index is the literal access at its folded value.</summary>
    [Fact]
    public void AcceptsAConstIndexWithinRange() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; const int k = 1; a[k]=1; H(q[0]); }");

    [Fact]
    public void RejectsAConstIndexOutOfRange() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; const int k = 5; a[k]=1; H(q[0]); }", "QSEM016");

    /// <summary>A const whose initializer is itself an expression folds too — `1 + 1` settles at the
    /// declaration, and chains (`m = k + 1`) settle through earlier folded consts.</summary>
    [Fact]
    public void FoldsAConstExpressionInitializerAndChains() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                const int k = 1 + 1;
                const int m = k - 1;
                a[k] = 1;
                a[m] = 1;
                for i in 0..k { a[i] = 1; }
                H(q[0]);
            }
            """);

    /// <summary>A negative folded index is out of range for ANY array — proven wrong, not unprovable.</summary>
    [Fact]
    public void RejectsANegativeConstIndex() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; const int k = 0 - 3; a[k]=1; H(q[0]); }", "QSEM016");

    /// <summary>A negative const as a loop's To bound folds to an EMPTY loop (To &lt; From) — accepted.</summary>
    [Fact]
    public void AcceptsAnEmptyLoopBoundedByANegativeConst() =>
        Compiler.Accepts("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; const int k = 0 - 3; for i in 0..k { a[i]=1; } H(q[0]); }");

    /// <summary>The boundary of value knowledge: a `var` is NOT tracked — its statically-obvious initial
    /// value is still no proof (constant propagation is deliberately not implemented; declare it const).</summary>
    [Fact]
    public void RejectsAVarIndexEvenWithAStaticInitializer() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; int[] a=[1,2,3]; int n = 1; a[n]=1; H(q[0]); }", "QSEM030");

    /// <summary>A const index on a classical-array PARAMETER raises the call-site floor like a literal.</summary>
    [Fact]
    public void RejectsAConstIndexOnAParameterWhenTheArgumentIsTooShort() =>
        Compiler.Rejects("""
            operation Helper(int[] x) {
                const int k = 5;
                x[k] = 1;
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """, "QSEM016");

    /// <summary>A const holding an unsized Qubit[] parameter's <c>.Count</c>, used as a loop bound, defers to
    /// the post-monomorphization pass exactly as the direct <c>q.Count</c> does — the folder reads the const's
    /// symbolic folded value, so the const indirection is transparent. Accepted when the classical argument is
    /// long enough...</summary>
    [Fact]
    public void AcceptsAConstBoundOfAQubitCountWhenTheArgumentIsLongEnough() =>
        Compiler.Accepts("""
            operation Helper(Qubit[] q, int[] x) {
                const int hi = q.Count;
                for i in 0..hi { x[i] = 1; }
            }
            operation Main() {
                use r = Qubit[3];
                int[] a = [1, 2, 3, 4, 5];
                Helper(r, a);
            }
            """);

    /// <summary>...and rejected (with the P4 floor) when it is too short — post-mono <c>hi</c> = 3, so the
    /// loop reaches <c>x[3]</c> and the 2-element argument fails.</summary>
    [Fact]
    public void RejectsAConstBoundOfAQubitCountWhenTheArgumentIsTooShort() =>
        Compiler.Rejects("""
            operation Helper(Qubit[] q, int[] x) {
                const int hi = q.Count;
                for i in 0..hi { x[i] = 1; }
            }
            operation Main() {
                use r = Qubit[3];
                int[] a = [1, 2];
                Helper(r, a);
            }
            """, "QSEM016");

    // --- facts are keyed by SYMBOL IDENTITY, resolved through the scope chain: a shadowing binder of any
    //     kind (declaration or for-header) is a different variable, so it neither inherits an outer
    //     variable's proof nor destroys it ---

    /// <summary>A declaration shadowing the guarded name is a NEW variable — the outer guard proved someone
    /// else. Identity keying rejects this without any special-case invalidation.</summary>
    [Fact]
    public void RejectsADeclarationShadowingAGuardedNameFromInheritingItsProof() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    int n = 9;
                    a[n] = 1;
                }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>A declaration shadowing a LOOP variable severs the loop's proof the same way — the inner
    /// symbol has no loop fact, whatever its name.</summary>
    [Fact]
    public void RejectsADeclarationShadowingALoopVariableFromInheritingItsRange() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..2 {
                    int i = 9;
                    a[i] = 1;
                }
                H(q[0]);
            }
            """, "QSEM030");

    /// <summary>Precision, not just soundness: mutating a shadowed INNER variable invalidates the inner
    /// symbol only — the outer guarded variable keeps its proof (the name-keyed wipe used to kill it).</summary>
    [Fact]
    public void KeepsAnOuterGuardWhenOnlyAShadowingInnerVariableIsReassigned() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    for j in 0..2 {
                        int n = 0;
                        n = n + 9;
                    }
                    a[n] = 1;
                }
                H(q[1]);
            }
            """);

    // --- loop bounds fold in the HEADER's scope — the scope the emitted loop evaluates them in. A shadowing
    //     `const` inside the body must not change what the bound means. ---

    /// <summary>The emitted loop runs 0..9 (outer k); folding the bound at the ACCESS site would read the
    /// inner k=2 and wrongly prove it — the verdict must follow the header's k.</summary>
    [Fact]
    public void RejectsALoopWhoseBoundConstIsShadowedSmallerInsideTheBody() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                const int k = 9;
                int[] a = [1, 2, 3];
                for i in 0..k {
                    const int k = 2;
                    a[i] = 1;
                }
                H(q[0]);
            }
            """, "QSEM016");

    /// <summary>Mirror: the emitted loop runs 0..2 (outer k) and is in range — the inner k=9 must not make
    /// the verdict reject a loop that never overshoots.</summary>
    [Fact]
    public void AcceptsALoopWhoseBoundConstIsShadowedLargerInsideTheBody() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                const int k = 2;
                int[] a = [1, 2, 3];
                for i in 0..k {
                    const int k = 9;
                    a[i] = 1;
                }
                H(q[0]);
            }
            """);

    // --- safety proofs are alternatives and outrank wrongness verdicts: a guard is sufficient ON ITS OWN,
    //     because the access only EXECUTES when the guard held — so the clamp idiom passes even when the
    //     loop's maximum is out of range, and no P4 floor is recorded for a guard-proven access ---

    /// <summary>The clamp idiom: the loop overshoots, the guard blocks the overshooting iterations. The
    /// access never executes out of bounds, so the loop's folded maximum proves nothing wrong.</summary>
    [Fact]
    public void AcceptsAGuardedAccessInsideALoopWhoseBoundOvershoots() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..5 {
                    if (0 <= i && i < a.Count) { a[i] = 1; }
                }
                H(q[0]);
            }
            """);

    /// <summary>Same idiom over the always-wrong bound `0..a.Count` — the guard still clamps every executed
    /// access into range, whatever the length.</summary>
    [Fact]
    public void AcceptsAGuardedAccessInsideACountBoundedLoop() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                for i in 0..a.Count {
                    if (0 <= i && i < a.Count) { a[i] = 1; }
                }
                H(q[0]);
            }
            """);

    /// <summary>The clamp idiom on a PARAMETER adapts to any argument length — accepted, and NO minimum-length
    /// contract is recorded (a guarded access demands nothing of the caller).</summary>
    [Fact]
    public void AcceptsAGuardedClampOverAParameterAndRecordsNoFloor()
    {
        var r = Compiler.Compile("""
            operation Helper(int[] x) {
                for i in 0..5 {
                    if (0 <= i && i < x.Count) { x[i] = 1; }
                }
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """);
        Assert.True(r.Success);
        var helper = r.Ir!.Operations.Single(o => o.Name == "Helper");
        Assert.Null(r.Semantics!.RequiredArgLengths(helper.Id));
    }

    /// <summary>A guard proves only the array it names: clamping by ANOTHER array's Count leaves the access
    /// on `a` unproven, and the loop verdict (reaches 5 on a 3-element array) stands.</summary>
    [Fact]
    public void RejectsAGuardOnADifferentArrayInsideAnOvershootingLoop() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[1];
                int[] a = [1, 2, 3];
                int[] b = [1, 2, 3, 4, 5, 6];
                for i in 0..5 {
                    if (0 <= i && i < b.Count) { a[i] = 1; }
                }
                H(q[0]);
            }
            """, "QSEM016");

    // --- the back-edge rule: a loop body re-executes, so a guard fact from OUTSIDE the loop cannot survive
    //     into a body that reassigns the guarded name — iteration 2's access runs AFTER iteration 1's
    //     reassignment, whatever the text order. A guard INSIDE the body re-proves on every iteration. ---

    [Fact]
    public void RejectsAGuardedIndexReassignedLaterInTheSameLoopBody() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    for i in 0..5 {
                        a[n] = 1;
                        n = n + 9;
                    }
                }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>A while-condition re-evaluates after every pass through the body — the second evaluation
    /// reads the mutated index, so the pre-loop guard proves nothing.</summary>
    [Fact]
    public void RejectsAGuardedIndexInAWhileConditionWhoseBodyReassignsIt() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    while (a[n] == 0) {
                        n = n + 9;
                    }
                }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>An until-condition ALWAYS runs after the body: even the FIRST evaluation reads the mutated
    /// index, so this is out of bounds on iteration one.</summary>
    [Fact]
    public void RejectsAGuardedIndexInAnUntilConditionWhoseBodyReassignsIt() =>
        Compiler.Rejects("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    repeat {
                        n = n + 9;
                    } until (a[n] == 1);
                }
                H(q[1]);
            }
            """, "QSEM030");

    /// <summary>The sound version of the same loop: the guard INSIDE the body re-checks on every iteration,
    /// exactly like the runtime check it models — accepted.</summary>
    [Fact]
    public void AcceptsAGuardInsideTheLoopBodyEvenWhenTheBodyReassignsTheIndex() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                for i in 0..5 {
                    if (0 <= n && n < a.Count) {
                        a[n] = 1;
                        n = n + 9;
                    }
                }
                H(q[1]);
            }
            """);

    /// <summary>A body that never reassigns the guarded name keeps the outer proof — the wipe is per-name,
    /// not blanket.</summary>
    [Fact]
    public void AcceptsAGuardedIndexInsideALoopThatNeverReassignsIt() =>
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                if (0 <= n && n < a.Count) {
                    for i in 0..5 {
                        a[n] = 1;
                    }
                }
                H(q[1]);
            }
            """);

    // --- P4 floors come from the SAME folded bound the prover judged (one calculator): any spelling the
    //     prover can fold — arithmetic, const names, symbolic cancellation — raises the call-site floor.
    //     Before the data-driven rewrite these compiled and emitted out-of-bounds QASM, because a separate
    //     floor walk re-read the bound with a weaker folder and silently recorded nothing. ---

    [Theory]
    [InlineData("0..2+3")]
    [InlineData("0..2*3")]
    [InlineData("0..x.Count-x.Count+5")]
    public void RejectsAFoldableLoopBoundOverAParameterWhenTheArgumentIsTooShort(string range) =>
        Compiler.Rejects($$"""
            operation Helper(int[] x) {
                for i in {{range}} { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """, "QSEM016");

    [Fact]
    public void RejectsAConstNameBoundOverAParameterWhenTheArgumentIsTooShort() =>
        Compiler.Rejects("""
            operation Helper(int[] x) {
                const int k = 5;
                for i in 0..k { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """, "QSEM016");

    /// <summary>The floor survives monomorphization: `q.Count*2-2` is deferred pre-mono (unsized Qubit[]),
    /// substituted to `5 * 2 - 2` = 8 in the specialization, and must then require 9 elements of `x`.</summary>
    [Fact]
    public void RejectsAPostMonoArithmeticBoundOverAClassicalParameter() =>
        Compiler.Rejects("""
            operation Helper(Qubit[] q, int[] x) {
                for i in 0..q.Count*2-2 { x[i] = 1; }
            }
            operation Main() {
                use r = Qubit[5];
                int[] a = [1, 2];
                Helper(r, a);
            }
            """, "QSEM016");

    /// <summary>The floor propagates through a pass-through call: `Outer` hands its own parameter to
    /// `Inner`, so `Inner`'s requirement becomes `Outer`'s and fires at `Main`'s concrete call.</summary>
    [Fact]
    public void RejectsATransitiveArithmeticFloorThroughAPassThroughCall() =>
        Compiler.Rejects("""
            operation Inner(int[] x) {
                for i in 0..2+3 { x[i] = 1; }
            }
            operation Outer(int[] y) {
                Inner(y);
            }
            operation Main() {
                int[] a = [1, 2];
                Outer(a);
            }
            """, "QSEM016");

    /// <summary>A literal index no legal array can satisfy is provably wrong for EVERY argument — rejected
    /// in the body, call or no call.</summary>
    [Theory]
    [InlineData("99999999999999999999")]
    [InlineData("3000000000")]
    public void RejectsALiteralIndexBeyondAnyPossibleArrayLength(string index) =>
        Compiler.Rejects($$"""
            operation Helper(int[] x) {
                x[{{index}}] = 1;
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """, "QSEM016");

    /// <summary>An empty loop (To &lt; From) never runs, so it raises NO floor — the argument's length is
    /// irrelevant. The old floor walk recorded the To bound regardless and falsely rejected this.</summary>
    [Fact]
    public void AcceptsAnEmptyConstantLoopOverAParameterRegardlessOfArgumentLength() =>
        Compiler.Accepts("""
            operation Helper(int[] x) {
                for i in 5..2 { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1];
                Helper(a);
            }
            """);

    // --- nested loops reusing a variable name: the access binds to the INNERMOST loop's bound, in both
    //     directions (the old floor walk bound it to the outer loop's, unsound one way, over-rejecting the other) ---

    [Fact]
    public void RejectsAShadowedInnerLoopWhoseBoundExceedsTheArgument() =>
        Compiler.Rejects("""
            operation Helper(int[] x) {
                for i in 0..1 {
                    for i in 0..5 { x[i] = 1; }
                }
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """, "QSEM016");

    [Fact]
    public void AcceptsAShadowedInnerLoopWhoseBoundFitsTheArgument() =>
        Compiler.Accepts("""
            operation Helper(int[] x) {
                for i in 0..5 {
                    for i in 0..1 { x[i] = 1; }
                }
            }
            operation Main() {
                int[] a = [1, 2];
                Helper(a);
            }
            """);

    /// <summary>The settled requirement table is a FACT on the model — the op's array-argument contract,
    /// the same data the call-site QSEM016s are derived from.</summary>
    [Fact]
    public void RecordsTheArrayArgumentContractOnTheSemanticModel()
    {
        var r = Compiler.Compile("""
            operation Helper(int[] x) {
                for i in 0..2+3 { x[i] = 1; }
            }
            operation Main() {
                int[] a = [1, 2, 3, 4, 5, 6];
                Helper(a);
            }
            """);
        Assert.True(r.Success);
        var helper = r.Ir!.Operations.Single(o => o.Name == "Helper");
        var contract = r.Semantics!.RequiredArgLengths(helper.Id);
        Assert.NotNull(contract);
        Assert.Equal(6L, contract!["x"]);
    }

    // --- the failed proof is recorded as DATA (SemanticModel.UnprovenIndexes), and each QSEM030 is DERIVED
    //     from an entry: the fact is target-independent, only its disposition is per-backend (the OpenQASM
    //     path rejects; a future QIR backend reads the same list as its runtime-check insertion plan) ---

    [Fact]
    public void RecordsEachUnprovenAccessOnTheSemanticModel()
    {
        var r = Compiler.Compile("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                bit b = M(q[0]);
                int n = b;
                int[] a = [1, 2, 3];
                a[n] = 1;
                for i in 0..a.Count-n { a[i] = 1; }
            }
            """);
        Assert.False(r.Success);
        Assert.NotNull(r.Semantics);
        Assert.Collection(r.Semantics!.UnprovenIndexes,
            u => Assert.Equal(("Main", "a", "n", null), (u.Op, u.Array, u.Index, u.LoopBound)),
            u => Assert.Equal(("Main", "a", "i", "a . Count - n"), (u.Op, u.Array, u.Index, u.LoopBound)));   // bound text as the IR stores it (tokenizer-spaced)
        Assert.Equal(r.Semantics.UnprovenIndexes.Count, r.Errors.Count(e => e.Code == "QSEM030"));
    }

    // --- the deferral ledger: a "re-check after monomorphization" promise is itself recorded as DATA
    //     (SemanticModel.DeferredSizeChecks), never a silent return. The pipeline's surviving model is
    //     empty on success (the post-mono re-check answered every promise); validating the pre-mono
    //     program directly exposes the outstanding entries a no-specialization backend must dispose of. ---

    [Fact]
    public void DeferralLedgerIsEmptyOnTheFinalModelAndListsPreMonoPromises()
    {
        var r = Compiler.Compile("""
            operation Flip(Qubit[] q, Qubit[] r) {
                X(q[5]);
                for i in 0..q.Count-1 { H(q[i]); }
                for i in 0..r.Count-1 { H(q[i]); }
            }
            operation Main() {
                use a = Qubit[6];
                use b = Qubit[6];
                Flip(a, b);
            }
            """);
        Assert.True(r.Success);
        Assert.Empty(r.Semantics!.DeferredSizeChecks);   // the specialize→re-validate pair ran: every promise was answered

        // The same (resolved, pre-mono) program validated directly: the literal index and the CROSS-array
        // loop bound are postponed — and on the ledger. The same-array `0..q.Count-1` loop is NOT: P2
        // proves it for ANY length, so there is no promise to record.
        var preErrors = QoraValidator.Validate(r.Ir, out var pre);
        Assert.Empty(preErrors);
        Assert.Collection(pre!.DeferredSizeChecks,
            d => Assert.Equal(("Flip", "q", "q[5]"), (d.Op, d.Array, d.Access)),
            d => Assert.Equal(("Flip", "q", "q[i]"), (d.Op, d.Array, d.Access)));
    }

    /// <summary>The ledger is a per-SITE work list, never deduplicated: two deferring accesses stay two
    /// entries even when the records are value-equal (here: one statement, one span, identical spelling —
    /// the R12 finding was that Distinct silently under-counted exactly this, and any spanless imported
    /// pair the same way). Diagnostics collapse value-equal records; promises must not.</summary>
    [Fact]
    public void DeferralLedgerCountsEverySiteEvenWhenEntriesAreValueEqual()
    {
        var r = Compiler.Compile("""
            operation Pick(bit[] f, int n, Qubit q) {
                if (0 <= n && n < 2) { if (f[n] == 1 && f[n] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                bit[] f = new bit[2];
                int n = 1;
                Pick(f, n, q[0]);
            }
            """);
        Assert.True(r.Success);
        Assert.Empty(r.Semantics!.DeferredSizeChecks);

        var preErrors = QoraValidator.Validate(r.Ir, out var pre);
        Assert.Empty(preErrors);
        Assert.Collection(pre!.DeferredSizeChecks,
            d => Assert.Equal(("Pick", "f", "f[n]"), (d.Op, d.Array, d.Access)),
            d => Assert.Equal(("Pick", "f", "f[n]"), (d.Op, d.Array, d.Access)));
    }

    /// <summary>The walk's liveness prediction is recorded per op instead of discarded: WillBeRechecked
    /// answers "does the post-mono re-check come for this op?" — the ledger's companion question (an entry
    /// whose op answers false is a promise nothing will ever answer). Dead1→Dead2 pins the TRANSITIVE
    /// closure: Dead2 has a caller, but that caller is itself dead, so no size ever reaches it.</summary>
    [Fact]
    public void ModelRecordsWillBeRecheckedPerOp()
    {
        var r = Compiler.Compile("""
            operation Flip(Qubit[] q) { X(q[5]); }
            operation Dead1(Qubit[] q) { Dead2(q); }
            operation Dead2(Qubit[] q) { X(q[5]); }
            operation Main() {
                use a = Qubit[6];
                Flip(a);
            }
            """);
        Assert.True(r.Success);

        QoraValidator.Validate(r.Ir, out var pre);
        var ops = r.Ir!.Operations.ToDictionary(o => o.Name, o => o.Id);
        Assert.True(pre!.WillBeRechecked(ops["Main"]));    // concrete: checks complete without deferral
        Assert.True(pre.WillBeRechecked(ops["Flip"]));     // generic reached from Main
        Assert.False(pre.WillBeRechecked(ops["Dead1"]));   // generic nothing calls
        Assert.False(pre.WillBeRechecked(ops["Dead2"]));   // one caller — but a dead one
        Assert.Null(pre.WillBeRechecked(-1));              // an op this validation never saw

        // The FINAL (post-mono) model: every surviving op is concrete, so every verdict is true.
        Assert.All(r.MonoIr!.Operations, o => Assert.True(r.Semantics!.WillBeRechecked(o.Id)));
    }

    /// <summary>A digit-run index too large for long lowers to a verbatim literal node — the diagnostic
    /// must still SHOW it (the message once dropped it to an empty `q[]`).</summary>
    [Fact]
    public void HugeDigitIndexDiagnosticShowsTheLiteral()
    {
        var r = Compiler.Compile("""
            operation Main() {
                use q = Qubit[2];
                X(q[99999999999999999999]);
            }
            """);
        Assert.False(r.Success);
        var e = Assert.Single(r.Errors, e => e.Code == "QSEM016");
        Assert.Contains("99999999999999999999", e.Message);
    }

    // --- deferral soundness: "re-check after monomorphization" is only a proof when the re-check RUNS.
    //     A generic op nothing calls is dropped by the Monomorphizer, so its deferred aliasing check
    //     falls back to the conservative pre-mono judgement instead of silently skipping. ---

    [Fact]
    public void RejectsAliasingInAnUncalledGenericOperation()
    {
        // i reaches 0 on iteration 0 in every possible specialization — qs[i] aliases qs[0]. With no call
        // site there is no post-mono re-check, so the pre-mono walk must reject it (QSEM014), exactly as
        // it rejects the literal form `CNOT(qs[0], qs[0])`.
        var r = Compiler.Compile("""
            operation Dead(Qubit[] qs) {
                for i in 0..qs.Count-1 { CNOT(qs[i], qs[0]); }
            }
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
            }
            """);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Code == "QSEM014");
    }

    [Fact]
    public void RejectsAliasingInAGenericReachableOnlyFromDeadGenerics()
    {
        // Dead2 HAS a caller — but that caller is itself a dead generic, so the Monomorphizer will
        // specialize NEITHER and the deferred post-mono re-check never runs. "Will be re-checked" must
        // mean transitive reachability from a CONCRETE op (mirroring what the Monomorphizer actually
        // specializes), not merely "has any call site".
        var r = Compiler.Compile("""
            operation Dead2(Qubit[] b) {
                for i in 0..b.Count-1 { CNOT(b[i], b[0]); }
            }
            operation Dead1(Qubit[] a) {
                Dead2(a);
            }
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
            }
            """);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Code == "QSEM014");
    }

    [Fact]
    public void FanInOverACalledGenericOperationStillCompiles()
    {
        // the CALLED counterpart keeps the precise post-mono verdict: `i in 0..q.Count-2` on a 5-qubit
        // argument never reaches q[4], so the fan-in does not alias — deferral is sound because the
        // re-check runs (the uncalled sibling above must NOT defer, having no re-check to defer to).
        var r = Compiler.Compile("""
            operation FanIn(Qubit[] q) {
                for i in 0..q.Count-2 { CNOT(q[i], q[4]); }
            }
            operation Main() {
                use a = Qubit[5];
                FanIn(a);
            }
            """);
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }
}
