using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// The parameter contract has two independent axes: ownership (borrow or move) and access
/// (read-only or mutable).  These tests deliberately exercise the full matrix at declarations,
/// call sites, forwarding boundaries, and control-flow joins.  The diagnostics are part of the
/// language contract: QSEM038 is a malformed ownership/access contract, QSEM014 is an aliasing
/// conflict, and QSEM039 is a use after a possible move.
/// </summary>
public class OwnershipParameterTests
{
    [Fact]
    public void FourParameterModesLowerToTwoIndependentAxes()
    {
        var result = Compiler.Compile("""
            operation Transfer(
                borrowed: int[],
                var writable: float[],
                move consumed: bit[],
                move var replacement: angle[]) {
                replacement[0] = 0.0;
            }

            operation Main() {
                var borrowed: int[] = [1];
                var writable: float[] = [0.0];
                var consumed: bit[] = [0];
                var replacement: angle[] = [0.0];
                Transfer(borrowed, var writable, move consumed, move var replacement);
            }
            """);

        Assert.True(result.Succeeded, Describe(result));
        var transfer = Assert.Single(result.Hir.Resolved!.Program!.Operations, operation => operation.Name == "Transfer");
        Assert.Collection(
            transfer.Params,
            parameter => AssertMode(parameter, QOwnershipMode.Borrowed, QAccessMode.ReadOnly),
            parameter => AssertMode(parameter, QOwnershipMode.Borrowed, QAccessMode.Mutable),
            parameter => AssertMode(parameter, QOwnershipMode.Moved, QAccessMode.ReadOnly),
            parameter => AssertMode(parameter, QOwnershipMode.Moved, QAccessMode.Mutable));
    }

    [Fact]
    public void RemovedInOutSpellingIsRejected() =>
        Compiler.RejectsExactly(
            "operation Bad(inout values: int[]) {}\noperation Main() {}",
            "CE0001");

    [Theory]
    [InlineData("""
        operation Edit(var values: int[]) {}
        operation Main() { var values: int[] = [0]; Edit(values); }
        """)]
    [InlineData("""
        operation Inspect(values: int[]) {}
        operation Main() { var values: int[] = [0]; Inspect(var values); }
        """)]
    [InlineData("""
        operation Consume(move values: int[]) {}
        operation Main() { var values: int[] = [0]; Consume(values); }
        """)]
    [InlineData("""
        operation Replace(move var values: int[]) {}
        operation Main() { var values: int[] = [0]; Replace(move values); }
        """)]
    [InlineData("""
        operation Consume(move values: int[]) {}
        operation Main() { var values: int[] = [0]; Consume(var values); }
        """)]
    public void CallSiteMustMatchBothOwnershipAndAccessAxes(string source) =>
        Compiler.RejectsExactly(source, "QSEM038");

    [Theory]
    [InlineData("var values: int[]")]
    [InlineData("move values: int[]")]
    [InlineData("move var values: int[]")]
    public void FunctionParametersMustRemainBorrowedAndReadonly(string parameter)
    {
        Compiler.RejectsExactly(
            $$"""
            function Bad({{parameter}}): int {
                return 0;
            }
            operation Main() {}
            """,
            "QSEM038");
    }

    [Theory]
    [InlineData("int")]
    [InlineData("float")]
    [InlineData("angle")]
    public void MutableBorrowIsSupportedForWritableClassicalArrays(string elementType)
    {
        Compiler.Accepts($$"""
            operation Edit(var values: {{elementType}}[]) {
                values[0] = values[0];
            }
            operation Main() {
                var values: {{elementType}}[] = new {{elementType}}[1];
                Edit(var values);
            }
            """);
    }

    [Theory]
    [InlineData("bit")]
    [InlineData("Qubit")]
    public void MutableBorrowRejectsUnsupportedShapes(string type)
    {
        Compiler.RejectsExactly(
            $$"""
            operation Bad(var value: {{type}}[]) {}
            operation Main() {}
            """,
            "QSEM038");
    }

    [Fact]
    public void BorrowedParameterCannotForwardItsCallerResourceAsAMove()
    {
        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Forward(values: int[]) {
                Consume(move values);
            }
            operation Main() {
                var values: int[] = [1];
                Forward(values);
            }
            """, "QSEM038");
    }

    [Fact]
    public void MovedParameterMayForwardItsOwnedResource()
    {
        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Forward(move values: int[]) {
                Consume(move values);
            }
            operation Main() {
                var values: int[] = [1];
                Forward(move values);
            }
            """);
    }

    [Fact]
    public void ConstBindingCannotBeMutablyBorrowedButMayBeMoved()
    {
        Compiler.RejectsExactly("""
            operation Edit(var values: int[]) {}
            operation Main() {
                const values: int[] = [1];
                Edit(var values);
            }
            """, "QSEM024");

        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Replace(move var values: int[]) {
                values[0] = 2;
            }
            operation Main() {
                const first: int[] = [1];
                const second: int[] = [1];
                Consume(move first);
                Replace(move var second);
            }
            """);
    }

    [Theory]
    [InlineData("""
        operation Pair(left: int[], move right: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Pair(values, move values);
        }
        """)]
    [InlineData("""
        operation Pair(move first: int[], move var second: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Pair(move values, move var values);
        }
        """)]
    public void MovedOrMutableSlotCannotAliasAnotherSlotOfTheSameCall(string source) =>
        Compiler.RejectsExactly(source, "QSEM014");

    [Theory]
    [InlineData("""
        operation Pair(move values: int[], item: int) {}
        operation Main() {
            var values: int[] = [1];
            Pair(move values, values[0]);
        }
        """)]
    [InlineData("""
        operation Pair(move values: int[], count: int) {}
        operation Main() {
            var values: int[] = [1];
            Pair(move values, values.Count);
        }
        """)]
    [InlineData("""
        operation Pair(var values: int[], item: int) {}
        operation Main() {
            var values: int[] = [1];
            Pair(var values, values[0]);
        }
        """)]
    public void ExclusiveWholeStorageCannotOverlapAnElementOrCountView(string source) =>
        Compiler.RejectsExactly(source, "QSEM014");

    [Theory]
    [InlineData("""
        operation Consume(move values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Consume(move values[0]);
        }
        """)]
    [InlineData("""
        operation Consume(move values: int[]) {}
        operation Main() {
            var value: int = 1;
            Consume(move value);
        }
        """)]
    [InlineData("""
        operation Consume(move values: int[]) {}
        operation Main() {
            var values: int[] = [1];
            Consume(move values.Count);
        }
        """)]
    public void MoveRequiresAWholeNonCopyBinding(string source) =>
        Compiler.RejectsExactly(source, "QSEM038");

    [Fact]
    public void WholeQubitRegisterMayBeMovedButAnElementMayNot()
    {
        Compiler.Accepts("""
            operation Consume(move register: Qubit[]) {}
            operation Main() {
                use register = Qubit[1];
                Consume(move register);
            }
            """);

        Compiler.RejectsExactly("""
            operation Consume(move register: Qubit[]) {}
            operation Main() {
                use register = Qubit[1];
                Consume(move register[0]);
            }
            """, "QSEM038");
    }

    [Fact]
    public void DirectUseAfterMoveIsRejected()
    {
        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Consume(move values);
                Inspect(values);
            }
            """, "QSEM039");
    }

    [Fact]
    public void UseAfterAMoveInOnlyOneBranchIsStillRejected()
    {
        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                use q = Qubit[1];
                var flag: bit = M(q[0]);
                var values: int[] = [1];
                if (flag == 1) {
                    Consume(move values);
                }
                Inspect(values);
            }
            """, "QSEM039");
    }

    [Fact]
    public void RepeatedLoopMoveOfAnOuterBindingIsRejectedButFreshLocalIsAllowed()
    {
        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                for i in 0..1 {
                    Consume(move values);
                }
            }
            """, "QSEM039");

        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Main() {
                for i in 0..1 {
                    var values: int[] = [1];
                    Consume(move values);
                }
            }
            """);
    }

    [Fact]
    public void ShadowedBindingIsTrackedByIdentityRatherThanName()
    {
        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                if (1 == 1) {
                    var values: int[] = [2];
                    Consume(move values);
                }
                Inspect(values);
            }
            """);
    }

    [Fact]
    public void InvalidMoveCallDoesNotPoisonTheBindingForLaterStatements()
    {
        // The call contract is invalid because a borrowed formal cannot receive a moved actual.  In
        // particular, the failed call must not also make the later borrow look like a use-after-move.
        Compiler.RejectsExactly("""
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Inspect(move values);
                Inspect(values);
            }
            """, "QSEM038");

        Compiler.RejectsExactly("""
            operation Consume(move values: int[], q: Qubit) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                use q = Qubit[1];
                var values: int[] = [1];
                Consume(move values, q[2]);
                Inspect(values);
            }
            """, "QSEM016");
    }

    [Fact]
    public void ErrorsFoundBeforeAndAfterCallCheckingDoNotCommitAMove()
    {
        // Name resolution happens while building the symbol table, before CheckCall.  A missing argument
        // must therefore invalidate the whole call instead of consuming the otherwise-valid first slot.
        Compiler.RejectsExactly("""
            operation Consume(move values: int[], count: int) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Consume(move values, missing);
                Inspect(values);
            }
            """, "QSEM025");

        // Minimum array lengths are propagated only after every body has been walked.  This late QSEM016
        // likewise owns the bad call; the following borrow must not cascade into QSEM039.
        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {
                var second: int = values[1];
            }
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Consume(move values);
                Inspect(values);
            }
            """, "QSEM016");

        // The same late error may belong to a function nested in another call's argument.  It still
        // invalidates the enclosing move statement as one semantic unit.
        Compiler.RejectsExactly("""
            function Second(values: int[]): int {
                return values[1];
            }
            operation Consume(move target: int[], value: int) {}
            operation Inspect(target: int[]) {}
            operation Main() {
                var target: int[] = [0];
                var source: int[] = [1];
                Consume(move target, Second(source));
                Inspect(target);
            }
            """, "QSEM016");
    }

    [Fact]
    public void UnsupportedCalleeContractCannotCommitAMoveAtItsCallSite()
    {
        Compiler.RejectsExactly("""
            function Bad(move values: int[]): int {
                return 0;
            }
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Bad(move values);
                Inspect(values);
            }
            """, "QSEM038");
    }

    [Fact]
    public void InvalidCallDoesNotCascadeIntoAliasOrUseAfterMoveErrors()
    {
        Compiler.RejectsExactly("""
            operation Pair(move first: int[], second: int[], count: int) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Pair(move values, values, 0.5);
                Inspect(values);
            }
            """, "QSEM006");

        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                if (true) {
                    Consume(move values);
                    var values: int[] = [2];
                    Inspect(values);
                }
            }
            """, "QSEM025");
    }

    [Fact]
    public void QubitIndexExpressionCannotBorrowStorageMovedByTheSameCall()
    {
        Compiler.RejectsExactly("""
            operation Pair(move values: int[], q: Qubit) {}
            operation Main() {
                use register = Qubit[2];
                var values: int[] = [0, 1];
                Pair(move values, register[values.Count - 1]);
            }
            """, "QSEM014");
    }

    [Fact]
    public void GenericOwnershipLoopsAreJudgedAfterConcreteQubitSizing()
    {
        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Once(register: Qubit[], move values: int[]) {
                for i in 0..register.Count - 1 {
                    Consume(move values);
                }
            }
            operation Main() {
                use register = Qubit[1];
                var values: int[] = [1];
                Once(register, move values);
            }
            """);

        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Twice(register: Qubit[], move values: int[]) {
                for i in 0..register.Count - 1 {
                    Consume(move values);
                }
            }
            operation Main() {
                use register = Qubit[2];
                var values: int[] = [1];
                Twice(register, move values);
            }
            """, "QSEM039");
    }

    [Fact]
    public void GenericStraightLineUseIsCheckedBeforeCountIsSpecializedAway()
    {
        Compiler.RejectsExactly("""
            operation Consume(move register: Qubit[]) {}
            operation Bad(move register: Qubit[]) {
                Consume(move register);
                var count: int = register.Count;
            }
            operation Main() {
                use register = Qubit[1];
                Bad(move register);
            }
            """, "QSEM039");

        Compiler.RejectsExactly("""
            operation Consume(move register: Qubit[]) {}
            operation Bad(move register: Qubit[]) {
                if (register.Count > 0) {
                    Consume(move register);
                    var count: int = register.Count;
                }
            }
            operation Main() {
                use register = Qubit[1];
                Bad(move register);
            }
            """, "QSEM039");
    }

    [Fact]
    public void SpecializedHirPreservesCountAndOpenQasmTargetFoldsItAfterOwnershipValidation()
    {
        var result = Compiler.Compile("""
            operation Observe(register: Qubit[]) {
                var count: int = register.Count;
            }
            operation Main() {
                use register = Qubit[2];
                Observe(register);
            }
            """);

        Assert.True(result.Succeeded, Describe(result));
        var specialized = Assert.Single(
            result.Hir.Specialized!.Program!.Operations,
            operation => operation.DisplayName == "Observe");
        var declaration = Assert.IsType<QDecl>(Assert.Single(specialized.Body));
        var value = Assert.IsType<QText>(declaration.Value);
        Assert.IsType<QMember>(value.Tree);

        var target = Assert.IsType<OpenQasmArtifact>(result.Targets.OpenQasm);
        var targetObserve = Assert.Single(
            target.Program.Definitions,
            operation =>
                operation.EmittedName.StartsWith(
                    "Observe",
                    StringComparison.Ordinal));
        Assert.Contains(
            MirQasmTestModel.Statements(targetObserve.Body)
                .OfType<MirQasmAssignmentStatement>(),
            assignment =>
                assignment.Value is MirQasmLiteralExpression { Text: "2" });
    }

    [Fact]
    public void EveryOrdinaryArrayUseIsRejectedAfterAMove()
    {
        var result = Compiler.Compile("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Consume(move values);
                var length: int = values.Count;
                var first: int = values[0];
                values[0] = 2;
                Inspect(values);
                Consume(move values);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            new[] { "QSEM039", "QSEM039", "QSEM039", "QSEM039", "QSEM039" },
            result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => error.Code).OrderBy(code => code));
    }

    [Fact]
    public void MovedParameterCannotBeUsedAgainInsideItsCallee()
    {
        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Forward(move values: int[]) {
                Consume(move values);
                Inspect(values);
            }
            operation Main() {
                var values: int[] = [1];
                Forward(move values);
            }
            """, "QSEM039");
    }

    [Fact]
    public void CountedLoopsRespectKnownZeroAndOneIterationBounds()
    {
        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                for i in 1..0 {
                    Consume(move values);
                }
                Inspect(values);
            }
            """);

        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                for i in 0..0 {
                    Consume(move values);
                }
                Inspect(values);
            }
            """, "QSEM039");

        Compiler.RejectsExactly("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                const once: int = 0;
                var values: int[] = [1];
                for i in once..once {
                    Consume(move values);
                }
                Inspect(values);
            }
            """, "QSEM039");
    }

    [Fact]
    public void StaticallyUnreachableBranchesAndLoopsDoNotMoveBindings()
    {
        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                if (1 == 0) {
                    Consume(move values);
                }
                while (1 == 0) {
                    Consume(move values);
                }
                Inspect(values);
            }
            """);

        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                if (false) {
                    Consume(move values);
                }
                while (false) {
                    Consume(move values);
                }
                Inspect(values);
            }
            """);

        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                repeat {
                    Consume(move values);
                } until (1 == 1);
            }
            """);

        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                const never: bit = false;
                const done: bit = true;
                var values: int[] = [1];
                if (never) {
                    Consume(move values);
                }
                while (never || false) {
                    Consume(move values);
                }
                Inspect(values);
                repeat {
                    Consume(move values);
                } until (done);
            }
            """);
    }

    [Fact]
    public void DefinitelyEnteredForDoesNotInventAZeroIterationExit()
    {
        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Inspect(values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                Consume(move values);
                for i in 0..0 {
                    while (true) {}
                }
                Inspect(values);
            }
            """);
    }

    [Fact]
    public void EmptyLoopStillValidatesTheCallContract()
    {
        Compiler.RejectsExactly("""
            operation Consume(move values: int[], q: Qubit) {}
            operation Main() {
                use register = Qubit[1];
                var values: int[] = [1];
                for i in 1..0 {
                    Consume(values, register[i]);
                }
            }
            """, "QSEM038");
    }

    [Fact]
    public void WhileConditionIsCheckedAgainAfterItsBodyCanMoveAValue()
    {
        var result = Compiler.Compile("""
            operation Consume(move values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                while (values.Count > 0) {
                    Consume(move values);
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM039");
        Assert.All(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => Assert.Equal("QSEM039", error.Code));
    }

    [Fact]
    public void RepeatUntilChecksThePostBodyConditionButRecreatesBodyLocals()
    {
        var outer = Compiler.Compile("""
            operation Consume(move values: int[]) {}
            operation Main() {
                var values: int[] = [1];
                repeat {
                    Consume(move values);
                } until (values.Count == 0);
            }
            """);
        Assert.False(outer.Succeeded);
        Assert.Contains(outer.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM039");
        Assert.All(outer.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => Assert.Equal("QSEM039", error.Code));

        Compiler.Accepts("""
            operation Consume(move values: int[]) {}
            operation Main() {
                repeat {
                    var values: int[] = [1];
                    Consume(move values);
                } until (1 == 1);
            }
            """);
    }

    [Fact]
    public void MovedQubitRegisterCannotBeUsedByAGateAfterward()
    {
        Compiler.RejectsExactly("""
            operation Consume(move register: Qubit[]) {}
            operation Main() {
                use register = Qubit[1];
                Consume(move register);
                H(register[0]);
            }
            """, "QSEM039");
    }

    [Fact]
    public void BitArrayCanMoveReadOnlyButNeverMoveMutable()
    {
        Compiler.Accepts("""
            operation Consume(move flags: bit[]) {}
            operation Main() {
                var flags: bit[] = [0, 1];
                Consume(move flags);
            }
            """);

        Compiler.RejectsExactly("""
            operation Bad(move var flags: bit[]) {}
            operation Main() {}
            """, "QSEM038");
    }

    [Fact]
    public void QubitCannotUseMoveVarBecauseThatIsClassicalMutableAccess()
    {
        Compiler.RejectsExactly("""
            operation Bad(move var register: Qubit[]) {}
            operation Main() {}
            """, "QSEM038");
    }

    [Fact]
    public void NamespaceResolutionAndSpecializationPreserveBothParameterAxes()
    {
        var result = Compiler.Compile("""
            namespace Buffers {
                operation Transfer(move register: Qubit[], move var values: int[]) {
                    X(register[0]);
                    values[0] = values[0] + 1;
                }
            }

            operation Main() {
                use register = Qubit[2];
                var values: int[] = [0];
                Buffers.Transfer(move register, move var values);
            }
            """);

        Assert.True(result.Succeeded, Describe(result));
        var specialized = Assert.Single(
            result.Hir.EffectAnalysis!.Program!.Operations,
            operation => operation.DisplayName == "Buffers.Transfer");
        Assert.Collection(
            specialized.Params,
            parameter => AssertMode(parameter, QOwnershipMode.Moved, QAccessMode.ReadOnly),
            parameter => AssertMode(parameter, QOwnershipMode.Moved, QAccessMode.Mutable));
        Assert.Contains("mutable array[int, #dim = 1] values", result.Targets.OpenQasm!.Text);
        Assert.DoesNotContain("move ", result.Targets.OpenQasm!.Text);
    }

    private static void AssertMode(QParam parameter, QOwnershipMode ownership, QAccessMode access)
    {
        Assert.Equal(ownership, parameter.Ownership);
        Assert.Equal(access, parameter.Access);
    }

    private static string Describe(Compilation result) =>
        string.Join(
            " | ",
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
}
