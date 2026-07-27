using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// <c>function</c> — a classical, pure, value-returning subroutine (Q#'s <c>function</c> vs the quantum,
/// void <c>operation</c>). Its call is a VALUE, usable anywhere in an expression; a measurement stays the
/// one side-effecting value form (whole <c>var r: bit = M(q[i]);</c> only), and an operation stays void.
/// A function emits an OpenQASM <c>def Name(...) -&gt; T { … return e; }</c>. Verifies acceptance of the
/// value positions, the emitted shape, and every purity / return / arity rule.
/// </summary>
public class FunctionTests
{
    [Theory]
    // a function call is a value in a declaration initializer, an assignment RHS, a gate argument,
    // a condition, and another function's body (fn -> fn):
    [InlineData("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); Rx(pi/k, q[0]); }")]
    [InlineData("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = 0; k = two(); }")]
    [InlineData("function half(x: float): float { return x / 2; }\noperation Main(){ use q=Qubit[1]; Rx(half(pi), q[0]); }")]
    [InlineData("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var c: int = 0; if(c == two()){ X(q[0]); } }")]
    [InlineData("function inner(x: int): int { return x + 1; }\nfunction outer(y: int): int { return inner(y) + 1; }\noperation Main(){ use q=Qubit[1]; var k: int = outer(3); }")]
    // parameters of every classical type; a zero-parameter function; a function used inside another expression:
    [InlineData("function pick(a: int, b: int): int { return a + b; }\noperation Main(){ use q=Qubit[1]; var k: int = pick(1, 2) + 3; }")]
    [InlineData("function angleOf(k: int): angle { return pi / k; }\noperation Main(){ use q=Qubit[1]; Rz(angleOf(4), q[0]); }")]
    // a function whose every path returns (if/else both return):
    [InlineData("function sign(x: int): int { if(x == 0){ return 0; } else { return 1; } }\noperation Main(){ use q=Qubit[1]; var k: int = sign(2); }")]
    public void AcceptsFunctionUses(string source) => Compiler.Accepts(source);

    [Fact]
    public void FunctionTargetCarriesReturnTypeAndTypedCallIdentity()
    {
        var program = CompileTarget(
            "function two(): int { return 2; }\n" +
            "operation Main(){ use q=Qubit[1]; var k: int = two(); }");
        var function = RequireOnlyFunction(program);
        var returnType = Assert.IsType<MirQasmScalarType>(function.ReturnType);
        var returned = Assert.IsType<MirQasmReturnStatement>(function.Body[^1]);
        var returnPlace = Assert.IsType<MirQasmDeclarationReferenceExpression>(
            returned.Value);
        var call = Assert.Single(
            program.Expressions().OfType<MirQasmFunctionCallExpression>());
        var target = Assert.IsType<MirQasmUserFunctionTarget>(call.Target);

        Assert.Equal(MirQasmCallableKind.Function, function.Kind);
        Assert.Equal(MirQasmScalarKind.Int, returnType.Kind);
        Assert.Equal(function.Id, target.Callable);
        Assert.Contains(
            MirQasmTestModel.Statements(function.Body)
                .OfType<MirQasmAssignmentStatement>(),
            assignment =>
                assignment.Target
                    is MirQasmDeclarationReferenceExpression reference
                && reference.Declaration == returnPlace.Declaration);
    }

    [Fact]
    public void FunctionTargetCarriesClassicalParameterAndReturnTypes()
    {
        var program = CompileTarget(
            "function pick(a: int, b: float): float { return a + b; }\n" +
            "operation Main(){ use q=Qubit[1]; Rx(pick(1, 0.5), q[0]); }");
        var function = RequireOnlyFunction(program);
        var parameterTypes = function.Parameters
            .Select(parameter => Assert.IsType<MirQasmScalarType>(parameter.Type).Kind)
            .ToArray();

        Assert.Equal(MirQasmCallableKind.Function, function.Kind);
        Assert.Equal(
            MirQasmScalarKind.Float,
            Assert.IsType<MirQasmScalarType>(function.ReturnType).Kind);
        Assert.Equal(
            new[] { MirQasmScalarKind.Int, MirQasmScalarKind.Float },
            parameterTypes);
        Assert.All(
            function.Parameters,
            parameter => Assert.Equal(MirQasmParameterAccess.Value, parameter.Access));
    }

    [Theory]
    // These scalar conversions are intentional: the literal 1 is a valid bit value, while the built-in
    // real constant pi is a valid angle value. Return checking must preserve both established cases.
    [InlineData("function flag(): bit { return 1; }\noperation Main(){ use q=Qubit[1]; var result: bit = flag(); }")]
    [InlineData("function phase(): angle { return pi; }\noperation Main(){ use q=Qubit[1]; var result: angle = phase(); }")]
    public void AcceptsCompatibleFunctionReturnValues(string source) => Compiler.Accepts(source);

    [Theory]
    [InlineData("function bad(): int { var values: int[] = [1, 2]; return values; }\noperation Main(){ use q=Qubit[1]; }")]
    [InlineData("function bad(): int { return 0.5; }\noperation Main(){ use q=Qubit[1]; }")]
    [InlineData("function bad(): bit { return 2; }\noperation Main(){ use q=Qubit[1]; }")]
    [InlineData("function bad(): angle { return 1; }\noperation Main(){ use q=Qubit[1]; }")]
    [InlineData("function bad(x: int): int { if (x == 0) { return 1; } else { return 0.5; } }\noperation Main(){ use q=Qubit[1]; }")]
    public void RejectsAnIncompatibleFunctionReturnValue(string source) =>
        Compiler.Rejects(source, "QSEM037");

    [Fact]
    public void RejectsANestedFunctionCallWhoseReturnTypeDoesNotMatch() =>
        Compiler.Rejects(
            "function inner(): float { return 0.5; }\n" +
            "function outer(): int { return inner(); }\n" +
            "operation Main(){ use q=Qubit[1]; var result: int = outer(); }",
            "QSEM037");

    [Theory]
    [InlineData("function half(): float { return 0.5; }\noperation Main(){ use q=Qubit[1]; var result: int = half(); }")]
    [InlineData("function half(): float { return 0.5; }\noperation Main(){ use q=Qubit[1]; var result: int = 0; result = half(); }")]
    public void RejectsAFloatFunctionResultStoredInAnInt(string source) =>
        Compiler.Rejects(source, "QSEM037");

    [Theory]
    [InlineData("var result: int = values;")]
    [InlineData("var result = values;")]
    [InlineData("var result = values + 1;")]
    public void RejectsAWholeArrayStoredInAScalar(string declaration) =>
        Compiler.Rejects(
            $"operation Main(){{ use q=Qubit[1]; var values: int[] = [1, 2]; {declaration} }}",
            "QSEM037");

    [Theory]
    // PURITY (QSEM033) — a function is classical: no gate, no measurement, no operation call:
    [InlineData("function f(): int { X(q); return 1; }\noperation Main(){ use q=Qubit[1]; }")]                                   // applies a gate
    [InlineData("function f(a: Qubit): int { H(a); return 1; }\noperation Main(){ use q=Qubit[1]; }")]                           // applies a gate (also QSEM034 on the param)
    [InlineData("operation g(a: Qubit){ X(a); }\nfunction f(): int { g(q[0]); return 1; }\noperation Main(){ use q=Qubit[2]; }")] // calls an operation
    public void RejectsQuantumInFunction(string source) => Compiler.Rejects(source, "QSEM033");

    [Fact]
    public void RejectsMeasurementInFunction() =>
        Compiler.Rejects("function f(): bit { return M(q[0]); }\noperation Main(){ use q=Qubit[1]; }", "QSEM033");

    [Theory]
    // a function parameter (and its return) is classical, never a qubit (QSEM034):
    [InlineData("function f(a: Qubit): int { return 1; }\noperation Main(){ use q=Qubit[1]; }")]
    [InlineData("function f(a: Qubit[]): int { return 1; }\noperation Main(){ use q=Qubit[1]; }")]
    public void RejectsQubitParameterInFunction(string source) => Compiler.Rejects(source, "QSEM034");

    [Fact]
    public void RejectsReturnOutsideFunction() =>
        Compiler.Rejects("operation Main(){ use q=Qubit[1]; return 5; }", "QSEM035");

    [Theory]
    // a function must return on every path (QSEM035): no return at all, or a return in only one branch:
    [InlineData("function f(): int { var x: int = 1; }\noperation Main(){ use q=Qubit[1]; var k: int = f(); }")]
    [InlineData("function f(x: int): int { if(x == 0){ return 0; } }\noperation Main(){ use q=Qubit[1]; var k: int = f(1); }")]
    public void RejectsFunctionThatMayNotReturn(string source) => Compiler.Rejects(source, "QSEM035");

    [Theory]
    // an OPERATION call has no value — only a function's does (QSEM005):
    [InlineData("operation g(a: Qubit){ X(a); }\noperation Main(){ use q=Qubit[1]; var k: int = g(q[0]) + 1; }")]
    // a measurement is a value only as a whole RHS, never inside a larger expression (QSEM005):
    [InlineData("operation Main(){ use q=Qubit[1]; var k: int = M(q[0]) + 1; }")]
    public void RejectsNonFunctionCallInExpression(string source) => Compiler.Rejects(source, "QSEM005");

    [Theory]
    // wrong argument count for a function call (QSEM006):
    [InlineData("function f(a: int): int { return a; }\noperation Main(){ use q=Qubit[1]; var k: int = f(1, 2); }")]
    [InlineData("function f(a: int, b: int): int { return a; }\noperation Main(){ use q=Qubit[1]; var k: int = f(1); }")]
    public void RejectsWrongFunctionArgumentCount(string source) => Compiler.Rejects(source, "QSEM006");

    [Theory]
    [InlineData("function take(x: int): int { return x; }\noperation Main(){ var result: int = take(0.5); }")]
    [InlineData("function take(x: bit): bit { return x; }\noperation Main(){ var result: bit = take(2); }")]
    [InlineData("function giveFloat(): float { return 0.5; }\nfunction take(x: int): int { return x; }\noperation Main(){ var result: int = take(giveFloat()); }")]
    [InlineData("function take(x: int): int { return x; }\noperation Main(){ var values: float[] = [0.5]; var result: int = take(values[0]); }")]
    [InlineData("function take(x: int): int { return x; }\noperation Main(){ var values: int[] = [1]; var result: int = take(values + 1); }")]
    public void RejectsAnIncompatibleScalarArgumentToAFunction(string source) =>
        Compiler.RejectsExactly(source, "QSEM006");

    [Fact]
    public void WholeBitRegisterDiagnosticOwnsACompoundFunctionArgument() =>
        Compiler.RejectsExactly(
            "function take(x: int): int { return x; }\n" +
            "operation Main(){ var flags: bit[] = new bit[2]; var result: int = take(flags + 1); }",
            "QSEM036");

    [Fact]
    public void AcceptsCompatibleScalarArgumentsToAFunctionInBothCallForms() =>
        Compiler.Accepts("""
            function accept(i: int, f: float, a: angle, b0: bit, b1: bit): int {
                return i;
            }
            operation Main() {
                var flag: bit = 1;
                var result: int = accept(flag, 2, 0.5, 0, 1);
                accept(flag, 2, 0.5, 0, 1);
            }
            """);

    [Theory]
    [InlineData("function half(): float { return 0.5; }\noperation Main(){ use q=Qubit[1]; var result: int = half(1); }")]
    [InlineData("function inner(x: int): float { return 0.5; }\nfunction outer(): int { return inner(); }\noperation Main(){ use q=Qubit[1]; }")]
    public void InvalidFunctionCallOwnsTheDiagnosticBeforeItsResultType(string source) =>
        Compiler.RejectsExactly(source, "QSEM006");

    [Fact]
    public void FunctionResultContractUsesTheInitializerPointOfDeclarationScope() =>
        Compiler.RejectsExactly(
            "function one(): int { return 1; }\n" +
            "operation Main(){ use q=Qubit[1]; var x: float = 0.5; " +
            "if (1 == 1) { var x: int = x + one(); } }",
            "QSEM037");

    [Fact]
    public void OperationCannotDeclareAReturnType() =>
        // only a `function` carries a `): T`; `operation Foo(): int` is a parse error (operations are void).
        Compiler.Rejects("operation Foo(): int { return 1; }\noperation Main(){ use q=Qubit[1]; }", "CE0001");

    [Fact]
    public void FunctionsCannotRecurse() =>
        // recursion is banned for every callable (QSEM011) — a function is no exception.
        Compiler.Rejects("function f(x: int): int { return f(x); }\noperation Main(){ use q=Qubit[1]; var k: int = f(1); }", "QSEM011");

    // --- a function call in EXPRESSION position is a call like any other ---
    //
    // A `function` introduced a SECOND call form: a QCallNode inside an expression tree, where every other
    // callable is a QGate statement. Passes written before it existed switched only on the statement shape
    // and skipped the new one silently. These pin the three places that mattered.

    [Theory]
    // The SAME signature check the statement form runs. Checking only the argument COUNT let a whole array —
    // and anything else of the wrong shape — reach a scalar parameter with no diagnostic, while the identical
    // call written as a statement was rejected: one callee, two answers.
    [InlineData("function twice(p: int): int { return p + p; }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var n: int = twice(f); }")]
    [InlineData("function pick(p: bit): int { return 1; }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[3]; var n: int = pick(f); }")]
    [InlineData("function twice(p: int): int { return p + p; }\noperation Main(){ use q=Qubit[1]; var xs: int[] = [1,2]; var n: int = twice(xs); }")]
    public void RejectsAWrongShapedArgumentToAFunctionCalledInAnExpression(string source) =>
        Compiler.Rejects(source, "QSEM006");

    [Fact]
    public void RejectsAQubitArgumentToAFunctionCalledInAnExpression() =>
        // a qubit in a classical slot has its own, more specific code — a qubit has no numeric value at all
        Compiler.Rejects("function twice(p: int): int { return p + p; }\noperation Main(){ use q=Qubit[2]; var n: int = twice(q[0]); }", "QSEM026");

    [Theory]
    [InlineData("var f: bit[] = new bit[2];", "f")]
    [InlineData("", "0.5")]
    [InlineData("var values: float[] = [0.5];", "values[0]")]
    public void AFunctionCallInAnExpressionGetsTheSameDiagnosticAsTheStatementForm(string setup, string argument)
    {
        var prefix = $"function twice(p: int): int {{ return p + p; }}\noperation Main(){{ use q=Qubit[1]; {setup} ";
        var asValue = Compiler.Compile(prefix + $"var n: int = twice({argument}); }}");
        var asStatement = Compiler.Compile(prefix + $"twice({argument}); }}");
        Assert.False(asValue.Succeeded);
        Assert.False(asStatement.Succeeded);
        Assert.Equal(asStatement.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Single(e => e.Code == "QSEM006").Message,
                     asValue.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Single(e => e.Code == "QSEM006").Message);
    }

    [Fact]
    public void AFunctionWithAnArrayLocalIsCalledWithItsHiddenArgument()
    {
        // An array local becomes a hidden reference PARAMETER (OpenQASM: arrays enter a def only by
        // reference). Every call site must supply it — including the expression-position one, which is the
        // only way a function is ever called. Missing it emitted a def/call arity mismatch under success:true.
        var program = CompileTarget(
            "function f(): int { var xs: int[] = [4, 5, 6]; return xs.Count; }\n" +
            "operation Main(){ use q=Qubit[1]; var n: int = f(); " +
            "if (n == 3) { X(q[0]); } }");
        var function = RequireOnlyFunction(program);
        var hidden = Assert.Single(function.Parameters);
        var hiddenType = Assert.IsType<MirQasmArrayType>(hidden.Type);
        var call = Assert.Single(
            program.EntryPoint.Body
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>());
        var target = Assert.IsType<MirQasmUserFunctionTarget>(call.Target);
        var hiddenArgument = Assert.IsType<MirQasmDeclarationReferenceExpression>(
            Assert.Single(call.Arguments));
        var backing = Assert.Single(
            MirQasmTestModel.Statements(program.EntryPoint.Body)
                .OfType<MirQasmArrayDeclarationStatement>(),
            declaration => declaration.Declaration == hiddenArgument.Declaration);

        Assert.Equal(function.Id, target.Callable);
        Assert.Equal(MirQasmParameterAccess.Mutable, hidden.Access);
        Assert.Equal(3, hiddenType.Length);
        Assert.Equal(3, backing.Type.Length);
    }

    [Fact]
    public void AReturnedArrayReferenceFollowsTheNearestDeclaration()
    {
        // The hoisting pass renames array references when a nested declaration shadows an outer one. It never
        // rewrote a `return` VALUE, so the returned reference kept a name a shadowing declaration had taken
        // over — the function returned a DIFFERENT array's contents, with no diagnostic anywhere.
        var program = CompileTarget("""
            function f(): int {
                var b: bit[] = new bit[3];
                b[0] = 1;
                if (1 > 0) { var b: bit[] = new bit[2]; b[1] = 1; }
                return AsInt(b);
            }
            operation Main() { use q = Qubit[1]; var n: int = f(); if (n == 4) { X(q[0]); } }
            """);
        var function = RequireOnlyFunction(program);
        var returned = Assert.IsType<MirQasmReturnStatement>(function.Body[^1]);
        var bitDeclarations = MirQasmTestModel.Statements(function.Body)
            .OfType<MirQasmValueDeclarationStatement>()
            .Where(
                declaration =>
                    declaration.Type is MirQasmBitType { IsRegister: true })
            .ToArray();

        Assert.Contains(
            bitDeclarations,
            declaration =>
                declaration.Type is MirQasmBitType { Width: 3 });
        Assert.Contains(
            bitDeclarations,
            declaration =>
                declaration.Type is MirQasmBitType { Width: 2 });
        Assert.True(
            function.Body.DependsOn(
                Assert.IsAssignableFrom<MirQasmExpression>(returned.Value),
                expression =>
                    expression is MirQasmUnsignedCastExpression { Width: 3 }));
        Assert.False(
            function.Body.DependsOn(
                Assert.IsAssignableFrom<MirQasmExpression>(returned.Value),
                expression =>
                    expression is MirQasmUnsignedCastExpression { Width: 2 }));
    }

    // --- `return` may stand anywhere; the target's one-return-at-the-end shape is produced by a pass ---
    //
    // A `return` means "produce this value and do nothing further". OpenQASM's grammar allows one inside a
    // block, but the execution target cannot leave a `def` from there — so the SHAPE is adapted at emission
    // instead of the language being narrowed to what the target happens to run.

    [Theory]
    // an early return, then a tail return:
    [InlineData("function f(x: int): int { if (x == 0) { return 7; } return 4; }")]
    // two early returns in a row:
    [InlineData("function f(x: int): int { if (x == 0) { return 1; } if (x == 1) { return 2; } return 3; }")]
    // a return inside a `for`, and inside a `while`:
    [InlineData("function f(n: int): int { for i in 0..4 { if (i == n) { return i; } } return 9; }")]
    [InlineData("function f(n: int): int { var i: int = 0; while (i < 3) { if (i == n) { return i; } i = i + 1; } return 9; }")]
    // a return nested in an else, with statements still following the whole `if`:
    [InlineData("function f(x: int): int { if (x == 0) { return 1; } else { if (x == 1) { return 2; } } return 3; }")]
    public void AcceptsAReturnAnywhere(string fn) =>
        Compiler.Accepts($"{fn}\noperation Main(){{ use q=Qubit[1]; var k: int = f(1); }}");

    [Theory]
    [InlineData("function sign(x: int): int { if (x == 0) { return 7; } return 4; }", "sign")]
    [InlineData("function find(n: int): int { for i in 0..4 { if (i == n) { return i; } } return 9; }", "find")]
    [InlineData("function deep(x: int): int { if (x == 0) { return 1; } else { if (x == 1) { return 2; } } return 3; }", "deep")]
    public void EveryEmittedDefTakesExactlyOneReturnAtItsEnd(string fn, string name)
    {
        var program = CompileTarget(
            $"{fn}\noperation Main(){{ use q=Qubit[1]; var k: int = {name}(1); }}");
        var function = RequireOnlyFunction(program);
        var returns = MirQasmTestModel.Statements(function.Body)
            .OfType<MirQasmReturnStatement>()
            .ToArray();

        Assert.Equal(MirQasmCallableKind.Function, function.Kind);
        Assert.Single(returns);
        Assert.Same(function.Body[^1], returns[0]);
        Assert.NotNull(returns[0].Value);
    }

    [Fact]
    public void AnEarlyReturnPutsTheSkippedTailInTheElseWithNoBookkeeping()
    {
        // The path that did NOT return is exactly the one that should still run the rest, so the structure
        // already carries the answer — no "have we returned?" flag is minted for straight-line code.
        var program = CompileTarget(
            "function sign(x: int): int { if (x == 0) { return 7; } return 4; }\n" +
            "operation Main(){ use q=Qubit[1]; var k: int = sign(0); }");
        var function = RequireOnlyFunction(program);
        var branch = Assert.Single(function.Body.OfType<MirQasmIfStatement>());

        Assert.NotEmpty(branch.Then);
        Assert.NotEmpty(branch.Else);
        Assert.Empty(
            MirQasmTestModel.Statements(function.Body)
                .OfType<MirQasmWhileStatement>());
        Assert.Empty(
            MirQasmTestModel.Statements(function.Body)
                .OfType<MirQasmBreakStatement>());
        Assert.Single(
            MirQasmTestModel.Statements(function.Body)
                .OfType<MirQasmReturnStatement>());
    }

    [Fact]
    public void AReturnInsideALoopIsGuardedSoTheFirstOneWins()
    {
        // A loop's tail cannot be re-nested into a branch, so this one shape needs the flag: later iterations
        // stop doing work, and the statements after the loop only run if no return happened.
        var program = CompileTarget(
            "function find(n: int): int { for i in 0..4 { " +
            "if (i == n) { return i; } } return 9; }\n" +
            "operation Main(){ use q=Qubit[1]; var k: int = find(2); }");
        var function = RequireOnlyFunction(program);
        var loop = Assert.Single(
            MirQasmTestModel.Statements(function.Body)
                .OfType<MirQasmWhileStatement>());
        var propagationGuard = RequireReturnPropagationGuard(
            function.Body,
            loop);
        var returnFlag = ReturnFlag(propagationGuard);

        Assert.Contains(
            MirQasmTestModel.Statements(loop.Body),
            statement => statement is MirQasmBreakStatement);
        Assert.True(
            propagationGuard.Then.IsEmpty || propagationGuard.Else.IsEmpty);
        Assert.DoesNotContain(
            loop.Body.OfType<MirQasmIfStatement>(),
            branch => IsReturnFlagCondition(branch.Condition, returnFlag));
    }

    [Fact]
    public void AReturnThroughALoopNestedInAnIfStillGuardsWhatFollows()
    {
        // The loop sits INSIDE an `if`, so it is not a direct element of the function body — yet a value it
        // produces must still stop the statements after the `if` from overwriting it. The guard follows any
        // statement that can return THROUGH a loop, not only a loop that is itself the next statement.
        var program = CompileTarget("""
            function f(n: int): int {
                var acc: int = 0;
                if (n > 0) { for i in 0..3 { if (i == n) { return i + 100; } } }
                acc = acc + 5;
                return acc;
            }
            operation Main() { use q = Qubit[1]; var k: int = f(2); }
            """);
        var function = RequireOnlyFunction(program);
        var loop = Assert.Single(
            MirQasmTestModel.Statements(function.Body)
                .OfType<MirQasmWhileStatement>());
        var returnFlag = FindReturnFlag(function.Body, loop);
        var guards = MirQasmTestModel.Statements(function.Body)
            .OfType<MirQasmIfStatement>()
            .Where(branch => IsReturnFlagCondition(branch.Condition, returnFlag))
            .ToArray();

        Assert.NotEmpty(guards);
        Assert.Contains(
            guards,
            guard =>
                MirQasmTestModel.Statements(guard.Then)
                    .Concat(MirQasmTestModel.Statements(guard.Else))
                    .OfType<MirQasmAssignmentStatement>()
                    .Any(
                        assignment =>
                            assignment.Target
                                is MirQasmDeclarationReferenceExpression target
                            && target.Declaration != returnFlag));
    }

    [Fact]
    public void AReturnInsideNestedLoopsLeavesEveryLevel()
    {
        // `break` leaves only the INNERMOST loop, so each enclosing one is left as well once the result exists.
        var program = CompileTarget("""
            function grid(n: int): int {
                for i in 0..2 {
                    for j in 0..2 { if (i + j == n) { return i * 10 + j; } }
                }
                return 99;
            }
            operation Main() { use q = Qubit[1]; var k: int = grid(1); }
            """);
        var function = RequireOnlyFunction(program);
        var outer = Assert.Single(function.Body.OfType<MirQasmWhileStatement>());
        var inner = Assert.Single(
            MirQasmTestModel.Statements(outer.Body)
                .OfType<MirQasmWhileStatement>());
        var returnFlag = FindReturnFlag(function.Body, inner);
        var outerBreakGuard = Assert.Single(
            MirQasmTestModel.Statements(outer.Body)
                .OfType<MirQasmIfStatement>(),
            branch =>
                IsReturnFlagCondition(branch.Condition, returnFlag)
                && MirQasmTestModel.Statements(branch.Then)
                    .Concat(MirQasmTestModel.Statements(branch.Else))
                    .Any(statement => statement is MirQasmBreakStatement));
        var innerReturnBranch = Assert.Single(
            MirQasmTestModel.Statements(inner.Body)
                .OfType<MirQasmIfStatement>(),
            branch =>
                branch.Then
                    .Concat(branch.Else)
                    .OfType<MirQasmAssignmentStatement>()
                    .Any(
                        assignment =>
                            assignment.Target
                                is MirQasmDeclarationReferenceExpression target
                            && target.Declaration == returnFlag
                            && assignment.Value
                                is MirQasmLiteralExpression { Text: "1" })
                && branch.Then
                    .Concat(branch.Else)
                    .Any(statement => statement is MirQasmBreakStatement));
        var innerReturnBreak = Assert.Single(
            MirQasmTestModel.Statements(innerReturnBranch.Then)
                .Concat(MirQasmTestModel.Statements(innerReturnBranch.Else))
                .OfType<MirQasmBreakStatement>());
        var outerPropagationBreak = Assert.Single(
            MirQasmTestModel.Statements(outerBreakGuard.Then)
                .Concat(MirQasmTestModel.Statements(outerBreakGuard.Else))
                .OfType<MirQasmBreakStatement>());

        Assert.NotEqual(innerReturnBreak.Id, outerPropagationBreak.Id);
    }

    [Theory]
    // still QSEM035 — not a SHAPE the target dislikes, but a function that genuinely has a path with no value
    [InlineData("function f(x: int): int { if (x == 0) { return 1; } }")]
    [InlineData("function f(x: int): int { for i in 0..2 { return i; } }")]   // a loop may run zero times
    [InlineData("function f(x: int): int { var y: int = x; }")]
    public void RejectsAFunctionWithAPathThatReturnsNothing(string fn) =>
        Compiler.Rejects($"{fn}\noperation Main(){{ use q=Qubit[1]; var k: int = f(1); }}", "QSEM035");


    [Fact]
    public void TheIrViewShowsReturns() =>
        // the `--stages` IR view dropped every `return`, rendering function bodies as incomplete
        Assert.Contains("QReturn", Qora.Ir.IrPrinter.Print(
            Compiler.Compile("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); }").Hir.Resolved!.Program));

    private static MirOpenQasmTargetProgram CompileTarget(string source) =>
        MirQasmTestModel.Compile(source).Program;

    private static MirQasmCallableDefinition RequireOnlyFunction(
        MirOpenQasmTargetProgram program) =>
        Assert.Single(
            program.Definitions,
            definition => definition.Kind == MirQasmCallableKind.Function);

    private static MirQasmIfStatement RequireReturnPropagationGuard(
        IReadOnlyList<MirQasmStatement> body,
        MirQasmWhileStatement loop)
    {
        var returnFlag = FindReturnFlag(body, loop);
        return Assert.Single(
            MirQasmTestModel.Statements(body)
                .OfType<MirQasmIfStatement>(),
            branch =>
                IsReturnFlagCondition(branch.Condition, returnFlag)
                && (branch.Then.IsEmpty || branch.Else.IsEmpty));
    }

    private static MirQasmDeclarationId ReturnFlag(
        MirQasmIfStatement propagationGuard)
    {
        var condition = Assert.IsType<MirQasmBinaryExpression>(
            propagationGuard.Condition);
        return condition.Left switch
        {
            MirQasmDeclarationReferenceExpression reference =>
                reference.Declaration,
            _ => Assert.IsType<MirQasmDeclarationReferenceExpression>(
                    condition.Right)
                .Declaration,
        };
    }

    private static MirQasmDeclarationId FindReturnFlag(
        IReadOnlyList<MirQasmStatement> body,
        MirQasmWhileStatement loop)
    {
        var initializedToZero = MirQasmTestModel.Statements(body)
            .OfType<MirQasmValueDeclarationStatement>()
            .Where(
                declaration =>
                    declaration.Type
                        is MirQasmScalarType { Kind: MirQasmScalarKind.Int }
                    && declaration.Initializer
                        is MirQasmLiteralExpression { Text: "0" })
            .Select(declaration => declaration.Declaration)
            .ToHashSet();
        var setToOneInLoop = MirQasmTestModel.Statements(loop.Body)
            .OfType<MirQasmAssignmentStatement>()
            .Where(
                assignment =>
                    assignment.Target
                        is MirQasmDeclarationReferenceExpression
                    && assignment.Value
                        is MirQasmLiteralExpression { Text: "1" })
            .Select(
                assignment =>
                    ((MirQasmDeclarationReferenceExpression)assignment.Target)
                    .Declaration)
            .Where(initializedToZero.Contains)
            .Distinct()
            .ToArray();

        return Assert.Single(setToOneInLoop);
    }

    private static bool IsReturnFlagCondition(
        MirQasmExpression condition,
        MirQasmDeclarationId returnFlag)
    {
        if (condition
            is not MirQasmBinaryExpression
            {
                Operator: MirQasmBinaryOperator.Equal
            } equality)
        {
            return false;
        }

        return IsFlag(equality.Left, equality.Right)
               || IsFlag(equality.Right, equality.Left);

        bool IsFlag(
            MirQasmExpression flag,
            MirQasmExpression expected) =>
            flag
                is MirQasmDeclarationReferenceExpression reference
                && reference.Declaration == returnFlag
                && expected
                    is MirQasmLiteralExpression { Text: "1" };
    }
}
