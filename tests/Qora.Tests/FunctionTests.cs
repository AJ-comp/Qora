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
    public void EmitsDefWithReturnTypeAndReturn()
    {
        Compiler.Emits("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); }", "def two() -> int {");
        Compiler.Emits("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); }", "int k = two();");
        // A return produces its value into the result variable; the def takes its ONE return at the end.
        Compiler.Emits("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); }", "ret = 2;");
        Compiler.Emits("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); }", "return ret;");
    }

    [Fact]
    public void EmitsClassicalParametersAndBitReturn()
    {
        Compiler.Emits("function pick(a: int, b: float): float { return a + b; }\noperation Main(){ use q=Qubit[1]; Rx(pick(1, 0.5), q[0]); }", "def pick(int a, float b) -> float {");
    }

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

    [Fact]
    public void AFunctionCallInAnExpressionGetsTheSameDiagnosticAsTheStatementForm()
    {
        const string prefix = "function twice(p: int): int { return p + p; }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; ";
        var asValue = Compiler.Compile(prefix + "var n: int = twice(f); }");
        var asStatement = Compiler.Compile(prefix + "twice(f); }");
        Assert.False(asValue.Success);
        Assert.False(asStatement.Success);
        Assert.Equal(asStatement.Errors.Single(e => e.Code == "QSEM006").Message,
                     asValue.Errors.Single(e => e.Code == "QSEM006").Message);
    }

    [Fact]
    public void AFunctionWithAnArrayLocalIsCalledWithItsHiddenArgument()
    {
        // An array local becomes a hidden reference PARAMETER (OpenQASM: arrays enter a def only by
        // reference). Every call site must supply it — including the expression-position one, which is the
        // only way a function is ever called. Missing it emitted a def/call arity mismatch under success:true.
        var r = Compiler.Compile("function f(): int { var xs: int[] = [4, 5, 6]; return xs.Count; }\noperation Main(){ use q=Qubit[1]; var n: int = f(); if (n == 3) { X(q[0]); } }");
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        Assert.Contains("int n = f(f_xs);", r.Qasm);
    }

    [Fact]
    public void AReturnedArrayReferenceFollowsTheNearestDeclaration()
    {
        // The hoisting pass renames array references when a nested declaration shadows an outer one. It never
        // rewrote a `return` VALUE, so the returned reference kept a name a shadowing declaration had taken
        // over — the function returned a DIFFERENT array's contents, with no diagnostic anywhere.
        var r = Compiler.Compile("""
            function f(): int {
                var b: bit[] = new bit[3];
                b[0] = 1;
                if (1 > 0) { var b: bit[] = new bit[2]; b[1] = 1; }
                return AsInt(b);
            }
            operation Main() { use q = Qubit[1]; var n: int = f(); if (n == 4) { X(q[0]); } }
            """);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        Assert.Contains("ret = uint[3](b_);", r.Qasm);   // the OUTER bit[3], which the returned expression names
        Assert.DoesNotContain("uint[2](", r.Qasm);       // never the inner bit[2] that shadowed it
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
        var r = Compiler.Compile($"{fn}\noperation Main(){{ use q=Qubit[1]; var k: int = {name}(1); }}");
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        var def = r.Qasm.Split($"def {name}(")[1].Split("\n}")[0];
        Assert.Equal(1, def.Split("return ").Length - 1);            // exactly one return…
        Assert.EndsWith("return ret;", def.TrimEnd());               // …and it is the def's last statement
    }

    [Fact]
    public void AnEarlyReturnPutsTheSkippedTailInTheElseWithNoBookkeeping()
    {
        // The path that did NOT return is exactly the one that should still run the rest, so the structure
        // already carries the answer — no "have we returned?" flag is minted for straight-line code.
        var r = Compiler.Compile("function sign(x: int): int { if (x == 0) { return 7; } return 4; }\noperation Main(){ use q=Qubit[1]; var k: int = sign(0); }");
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        Assert.Contains("else {", r.Qasm);
        Assert.DoesNotContain("done", r.Qasm);
    }

    [Fact]
    public void AReturnInsideALoopIsGuardedSoTheFirstOneWins()
    {
        // A loop's tail cannot be re-nested into a branch, so this one shape needs the flag: later iterations
        // stop doing work, and the statements after the loop only run if no return happened.
        var r = Compiler.Compile("function find(n: int): int { for i in 0..4 { if (i == n) { return i; } } return 9; }\noperation Main(){ use q=Qubit[1]; var k: int = find(2); }");
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        Assert.Contains("break;", r.Qasm);            // leaving the loop is what makes the FIRST return win
        Assert.Contains("if (done == 0)", r.Qasm);    // …and the flag is what skips the statements after it
        Assert.DoesNotContain("if (done == 0) {\n      if (", r.Qasm);   // the body itself is not wrapped
    }

    [Fact]
    public void AReturnThroughALoopNestedInAnIfStillGuardsWhatFollows()
    {
        // The loop sits INSIDE an `if`, so it is not a direct element of the function body — yet a value it
        // produces must still stop the statements after the `if` from overwriting it. The guard follows any
        // statement that can return THROUGH a loop, not only a loop that is itself the next statement.
        var r = Compiler.Compile("""
            function f(n: int): int {
                var acc: int = 0;
                if (n > 0) { for i in 0..3 { if (i == n) { return i + 100; } } }
                acc = acc + 5;
                return acc;
            }
            operation Main() { use q = Qubit[1]; var k: int = f(2); }
            """);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        var def = r.Qasm.Split("def f(")[1].Split("\n}")[0];
        Assert.Contains("if (done == 0)", def);   // acc = acc + 5 / return acc are guarded, not unconditional
    }

    [Fact]
    public void AReturnInsideNestedLoopsLeavesEveryLevel()
    {
        // `break` leaves only the INNERMOST loop, so each enclosing one is left as well once the result exists.
        var r = Compiler.Compile("""
            function grid(n: int): int {
                for i in 0..2 {
                    for j in 0..2 { if (i + j == n) { return i * 10 + j; } }
                }
                return 99;
            }
            operation Main() { use q = Qubit[1]; var k: int = grid(1); }
            """);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Code + " " + e.Message)));
        Assert.Contains("if (done == 1) {", r.Qasm);                     // the outer loop is left too
        Assert.Equal(2, r.Qasm.Split("break;").Length - 1);              // one break per loop level
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
            Compiler.Compile("function two(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var k: int = two(); }").Ir));
}
