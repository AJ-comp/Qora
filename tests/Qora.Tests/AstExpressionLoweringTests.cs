using System.Linq;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// AST expression lowering recovers a precedence-correct HIR tree from the engine's flat
/// token run — the ONE parse every downstream reader will consume instead of re-parsing text. These pin the
/// tree shape: operator precedence (<c>* /</c> above <c>+ -</c>; comparisons above <c>&amp;&amp;</c> above
/// <c>||</c>), member access, and index access.
/// </summary>
public class AstExpressionLoweringTests
{
    /// <summary>Compile a program with a single top-level <c>if</c> and return its condition tree.</summary>
    private static HirExpression ConditionTree(string condition)
    {
        var r = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[3];
                var a: int[] = [1, 2, 3];
                var b: bit = M(q[0]);
                var n: int = b;
                if ({{condition}}) { H(q[0]); }
            }
            """);
        var branch = r.Hir.Resolved!.Program.Callables[0].Body
            .OfType<HirIfStatement>()
            .Single();
        return branch.Condition;
    }

    [Fact]
    public void ParsesAGuardConjunctionWithComparisonsAboveAnd()
    {
        // 0 <= n && n < a.Count  ==  (0 <= n) && (n < a.Count)
        var tree = ConditionTree("0 <= n && n < a.Count");
        var and = Assert.IsType<HirBinaryExpression>(tree);
        Assert.Equal(HirBinaryOperator.LogicalAnd, and.Operator);

        var le = Assert.IsType<HirBinaryExpression>(and.Left);
        Assert.Equal(HirBinaryOperator.LessThanOrEqual, le.Operator);
        Assert.Equal(
            0L,
            Assert.IsType<HirIntegerLiteralExpression>(le.Left).Value);
        Assert.Equal(
            "n",
            Assert.IsType<HirNameExpression>(le.Right).Name);

        var lt = Assert.IsType<HirBinaryExpression>(and.Right);
        Assert.Equal(HirBinaryOperator.LessThan, lt.Operator);
        Assert.Equal(
            "n",
            Assert.IsType<HirNameExpression>(lt.Left).Name);
        var member = Assert.IsType<HirMemberAccessExpression>(lt.Right);
        Assert.Equal("Count", member.MemberName);
        Assert.Equal(
            "a",
            Assert.IsType<HirNameExpression>(
                member.Receiver).Name);
    }

    [Fact]
    public void ParsesArithmeticWithMultiplyAbovePlus()
    {
        // n * 2 + 1  ==  (n * 2) + 1
        var eq = Assert.IsType<HirBinaryExpression>(
            ConditionTree("n * 2 + 1 == 5"));
        Assert.Equal(HirBinaryOperator.Equal, eq.Operator);
        var plus = Assert.IsType<HirBinaryExpression>(eq.Left);
        Assert.Equal(HirBinaryOperator.Add, plus.Operator);
        var times = Assert.IsType<HirBinaryExpression>(plus.Left);
        Assert.Equal(HirBinaryOperator.Multiply, times.Operator);
        Assert.Equal(
            "n",
            Assert.IsType<HirNameExpression>(times.Left).Name);
        Assert.Equal(
            2L,
            Assert.IsType<HirIntegerLiteralExpression>(
                times.Right).Value);
        Assert.Equal(
            1L,
            Assert.IsType<HirIntegerLiteralExpression>(
                plus.Right).Value);
    }

    /// <summary>A pathologically deep expression (only ever machine-generated) is rejected with a clean
    /// diagnostic (QSEM031), never an uncatchable stack overflow — the front end always returns one result,
    /// honouring the "always one reply" contract. The rejected IR is NOT exposed: every stage renderer
    /// recurses the tree it prints, so a too-deep tree handed to the --stages view would crash exactly the
    /// way the guard exists to prevent.</summary>
    [Fact]
    public void RejectsAPathologicallyDeepExpressionCleanly()
    {
        var bound = "0" + string.Concat(Enumerable.Repeat("+1-1", 600));   // ~1200 operators deep
        var r = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[1];
                var a: int[] = [1, 2, 3];
                for i in 0..{{bound}} { a[i] = 1; }
                H(q[0]);
            }
            """);
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM031");
        Assert.Null(r.Hir.Resolved);   // unrenderable HIR stays unexposed (the stages view must not recurse it)
    }

    /// <summary>The depth guard covers an ARRAY LITERAL's element trees too — a deep expression hiding
    /// inside `[...]` is the same machine-generated shape as a deep scalar initializer and must get the
    /// same clean QSEM031, not a process-killing stack overflow in a later tree walker.</summary>
    [Fact]
    public void RejectsAPathologicallyDeepArrayElementCleanly()
    {
        var element = "1" + string.Concat(Enumerable.Repeat("+1", 1200));   // one deep element
        var r = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[1];
                var a: int[] = [{{element}}, 2];
                H(q[0]);
            }
            """);
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM031");
        Assert.Null(r.Hir.Resolved);
    }

    [Fact]
    public void ParsesOrBelowAnd()
    {
        // a == 1 && b == 0 || n == 2  ==  ((a==1) && (b==0)) || (n==2)
        var or = Assert.IsType<HirBinaryExpression>(
            ConditionTree("b == 1 && n == 0 || n == 2"));
        Assert.Equal(HirBinaryOperator.LogicalOr, or.Operator);
        Assert.Equal(
            HirBinaryOperator.LogicalAnd,
            Assert.IsType<HirBinaryExpression>(
                or.Left).Operator);
        Assert.Equal(
            HirBinaryOperator.Equal,
            Assert.IsType<HirBinaryExpression>(
                or.Right).Operator);
    }

    [Fact]
    public void ParsesRelationalAboveEquality()
    {
        // n == 1 < 2  ==  n == (1 < 2). Folding both comparison families at one level once made HIR claim
        // (n == 1) < 2, so later consumers could disagree about one source expression.
        var eq = Assert.IsType<HirBinaryExpression>(
            ConditionTree("n == 1 < 2"));
        Assert.Equal(HirBinaryOperator.Equal, eq.Operator);
        Assert.Equal(
            "n",
            Assert.IsType<HirNameExpression>(eq.Left).Name);
        var lt = Assert.IsType<HirBinaryExpression>(eq.Right);
        Assert.Equal(HirBinaryOperator.LessThan, lt.Operator);
        Assert.Equal(
            1L,
            Assert.IsType<HirIntegerLiteralExpression>(
                lt.Left).Value);
        Assert.Equal(
            2L,
            Assert.IsType<HirIntegerLiteralExpression>(
                lt.Right).Value);
    }

    /// <summary>A deep unary chain recurses in the engine's parse-stack teardown and expression lowering —
    /// depths the token cap admits must reach the QSEM031 guard as a clean reply, never kill the process
    /// (the compilation runs on a wide-stack worker thread precisely so the guard is always reachable).</summary>
    [Fact]
    public void RejectsAPathologicallyDeepUnaryChainCleanly()
    {
        var expr = string.Concat(Enumerable.Repeat("- ", 6000)) + "1";
        var r = Compiler.Compile($"operation Main() {{ use q = Qubit[1]; var x: int = {expr}; H(q[0]); }}");
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM031");
        Assert.Null(r.Hir.Resolved);
    }

    /// <summary>Deep-but-legal statement NESTING (inside the QSEM031 limit) must simply compile — the
    /// walkers' recursion budget lives on the wide-stack worker thread, so the guard's limit is the only
    /// bound, with no crash window beneath it.</summary>
    [Fact]
    public void DeepButLegalNestingCompiles()
    {
        var sb = new System.Text.StringBuilder("operation Main() {\n use q = Qubit[1];\n");
        for (var i = 0; i < 300; i++) sb.Append($"for i{i} in 0..1 {{\n");
        sb.Append("H(q[0]);\n");
        sb.Append(new string('}', 300));
        sb.Append("\n}");
        var r = Compiler.Compile(sb.ToString());
        Assert.True(r.Succeeded, string.Join(" | ", r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(e => e.Code)));
    }
}
