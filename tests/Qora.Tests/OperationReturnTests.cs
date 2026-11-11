using Qora.Compiler;
using Qora.Ir;

namespace Qora.Tests;

public class OperationReturnTests
{
    [Theory]
    [InlineData("int", "7")]
    [InlineData("bit", "1")]
    [InlineData("float", "0.5")]
    [InlineData("angle", "pi")]
    public void OperationKindIsIndependentOfItsScalarReturnType(string type, string value)
    {
        var compilation = Compiler.Compile($$"""
            operation Read(): {{type}} { return {{value}}; }
            operation Main(): {{type}} { return Read(); }
            """);
        Assert.True(compilation.Succeeded, Describe(compilation));
        Assert.All(compilation.Hir.Resolved!.Program.Callables, callable =>
        {
            Assert.False(callable.IsFunction);
            Assert.NotNull(callable.ReturnType);
        });
    }

    [Theory]
    [InlineData("operation Main() { return; }")]
    [InlineData("operation Stop(flag: int) { if (flag == 1) { return; } } operation Main() { Stop(1); }")]
    [InlineData("operation Read(q: Qubit): bit { return M(q); } operation Main(): bit { use q = Qubit[1]; return Read(q[0]); }")]
    [InlineData("operation Read(q: Qubit[]): bit { return M(q[0]); } operation Main(): bit { use q = Qubit[1]; return Read(q); }")]
    [InlineData("operation Step(q: Qubit): int { X(q); return 1; } operation Main() { use q = Qubit[1]; var n: int = Step(q[0]) + Step(q[0]); Step(q[0]); }")]
    [InlineData("operation Select(q: Qubit): bit { if (M(q) == 1) { return 1; } else { return 0; } } operation Main(): bit { use q = Qubit[1]; return Select(q[0]); }")]
    [InlineData("function Add(x: int): int { return x + 1; } operation Step(): int { return Add(1); } operation Main(): int { return Step(); }")]
    public void AcceptsTypedOperationsAndVoidEarlyReturns(string source) => Compiler.Accepts(source);

    [Theory]
    [InlineData("operation Main(): int { return; }", "QSEM035")]
    [InlineData("operation Main() { return 1; }", "QSEM035")]
    [InlineData("operation Main(): int { var n: int = 1; }", "QSEM035")]
    [InlineData("operation Read(flag: int): int { if (flag == 1) { return 1; } } operation Main() {}", "QSEM035")]
    [InlineData("operation Main(): int { return 0.5; }", "QSEM037")]
    [InlineData("operation Main(): bit { return 2; }", "QSEM037")]
    [InlineData("operation Main(): int { var values: int[] = [1]; return values; }", "QSEM037")]
    [InlineData("operation Read(): float { return 0.5; } operation Main() { var value: int = Read(); }", "QSEM037")]
    [InlineData("operation Read() {} operation Main(): int { return Read(); }", "QSEM005")]
    [InlineData("operation Main(): int[] { return [1]; }", "CE0001")]
    [InlineData("operation Main(): Qubit { use q = Qubit[1]; return q[0]; }", "CE0001")]
    [InlineData("operation Main(): (int, bit) { return (1, 0); }", "CE0001")]
    [InlineData("operation Read(): int { return Read(); } operation Main() {}", "QSEM011")]
    [InlineData("operation Read(): int { return Main(); } operation Main(): int { return 1; }", "QSEM009")]
    [InlineData("operation Read(q: Qubit): bit { return M(q); } operation Main() { var b: bit = Read(1); }", "QSEM006")]
    [InlineData("operation Main(): bit { var n: int = 1; return M(n); }", "QSEM006")]
    public void RejectsInvalidOperationReturnContracts(string source, string code) =>
        Compiler.Rejects(source, code);

    [Theory]
    [InlineData("return Read();")]
    [InlineData("var value: int = Read(); return value;")]
    [InlineData("if (Read() == 1) { return 1; } return 0;")]
    public void FunctionCannotHideAnOperationCallInAnExpression(string body) =>
        Compiler.Rejects($$"""
            operation Read(): int { return 1; }
            function Pure(): int { {{body}} }
            operation Main() {}
            """, "QSEM033");

    [Fact]
    public void ValueOperationCallsRetainDistinctQubitValidation() =>
        Compiler.Rejects("""
            operation Read(left: Qubit, right: Qubit): int { CNOT(left, right); return 1; }
            operation Main(): int { use q = Qubit[1]; return Read(q[0], q[0]); }
            """, "QSEM014");

    [Fact]
    public void EqualCallSpellingsRemainDistinctEffectfulEvaluations()
    {
        var compilation = Compiler.Compile("""
            operation Read(q: Qubit): bit { X(q); return M(q); }
            operation Main(): int {
                use q = Qubit[1];
                if (Read(q[0]) == Read(q[0])) { return 1; }
                return 0;
            }
            """);
        Assert.True(compilation.Succeeded, Describe(compilation));
        var main = compilation.Hir.Specialized!.Program.EntryCallable!;
        var calls = HirExpressions.DescendantsAndSelf(main).OfType<HirCallExpression>()
            .Where(call => call.Name == "Read").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.NotEqual(calls[0].Id, calls[1].Id);
    }

    [Fact]
    public void ValueCallCannotMoveABorrowedParameter() =>
        Compiler.Rejects("""
            operation Read(move q: Qubit[]): bit { return M(q[0]); }
            operation Relay(q: Qubit[]): bit { return Read(move q); }
            operation Main(): bit { use q = Qubit[1]; return Relay(q); }
            """, "QSEM038");

    [Fact]
    public void MovedQubitValueCallMakesTheCallerBindingUnavailable() =>
        Compiler.Rejects("""
            operation Read(move q: Qubit[]): bit { return M(q[0]); }
            operation Main(): bit {
                use q = Qubit[1];
                var value: bit = Read(move q);
                X(q[0]);
                return value;
            }
            """, "QSEM039");

    [Fact]
    public void ReturnMeasurementAndNestedValueCallsContributeQubitEffects()
    {
        var compilation = Compiler.Compile("""
            operation Read(q: Qubit): bit { return M(q); }
            operation Relay(q: Qubit): bit { return Read(q); }
            operation Main(): bit { use q = Qubit[1]; return Relay(q[0]); }
            """);
        Assert.True(compilation.Succeeded, Describe(compilation));
        var artifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        foreach (var name in new[] { "Read", "Relay" })
        {
            var callable = Assert.Single(artifact.Source.Program.Callables, node => node.Name == name);
            Assert.Contains(artifact.Model.FindOpEffects(callable.Id)!.ParamModified,
                reference => reference.Reg == "q");
        }
    }

    private const string Consume = "operation Consume(move values: int[]): int { return values[0]; } ";

    [Theory]
    [InlineData("var result: int = Consume(move values) + values[0];")]
    [InlineData("var result: int = Consume(move values); var later: int = values[0];")]
    [InlineData("values[0] = Consume(move values);")]
    [InlineData("if (Consume(move values) == 1 && 1 == 2) {} var later: int = values[0];")]
    [InlineData("var flag: int = 1; if (flag == 1 && Consume(move values) == 1) {} var later: int = values[0];")]
    [InlineData("while (Consume(move values) == 1) {}")]
    [InlineData("repeat {} until (Consume(move values) == 1); ")]
    public void ExpressionMovesTakeEffectAtTheCallAndAcrossControlFlow(string body) =>
        Compiler.Rejects(Consume + "operation Main() { var values: int[] = [1]; " + body + " }", "QSEM039");

    [Theory]
    [InlineData("var result: int = values[0] + Consume(move values);")]
    [InlineData("if (1 == 2 && Consume(move values) == 1) {} var later: int = values[0];")]
    [InlineData("if (1 == 1 || Consume(move values) == 1) {} var later: int = values[0];")]
    [InlineData("var flag: int = 1; if (flag == 1) { var result: int = Consume(move values); return; } var later: int = values[0];")]
    public void ExpressionMovesRespectEvaluationOrderAndUnexecutedPaths(string body) =>
        Compiler.Accepts(Consume + "operation Main() { var values: int[] = [1]; " + body + " }");

    [Fact]
    public void TwoMovesInOneStatementKeepBothCallIdentities() =>
        Compiler.Rejects(Consume + """
            operation Main() {
                var left: int[] = [1]; var right: int[] = [2];
                var total: int = Consume(move left) + Consume(move right);
                var first: int = left[0];
            }
            """, "QSEM039");

    [Fact]
    public void LaterArgumentCannotReadStorageConsumedByAnEarlierArgument() =>
        Compiler.Rejects(Consume + """
            function Add(left: int, right: int): int { return left + right; }
            operation Main() {
                var values: int[] = [1];
                var result: int = Add(Consume(move values), values[0]);
            }
            """, "QSEM039");

    [Fact]
    public void EarlierReferenceArgumentMustSurviveLaterArgumentEvaluation() =>
        Compiler.Rejects(Consume + """
            operation Read(values: int[], count: int): int { return values[0] + count; }
            operation Main() {
                var values: int[] = [1];
                var result: int = Read(values, Consume(move values));
            }
            """, "QSEM039");

    [Fact]
    public void NestedMutableBorrowEndsBeforeOuterCallBegins() =>
        Compiler.Accepts("""
            operation Edit(var values: int[]): int { values[0] = 2; return values[0]; }
            operation Read(var values: int[], count: int): int { return values[0] + count; }
            operation Main(): int {
                var values: int[] = [1];
                return Read(var values, Edit(var values));
            }
            """);

    private static string Describe(Qora.Compiler.Compilation compilation) =>
        string.Join(" | ", compilation.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
}
