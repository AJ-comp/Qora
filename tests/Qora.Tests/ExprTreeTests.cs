using System.Linq;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// The expression parser (<see cref="ExprTree"/>) recovers a precedence-correct tree from the engine's flat
/// token run — the ONE parse every downstream reader will consume instead of re-parsing text. These pin the
/// tree shape: operator precedence (<c>* /</c> above <c>+ -</c>; comparisons above <c>&amp;&amp;</c> above
/// <c>||</c>), member access, and index access.
/// </summary>
public class ExprTreeTests
{
    /// <summary>Compile a program with a single top-level <c>if</c> and return its condition tree.</summary>
    private static QNode CondTree(string condition)
    {
        var r = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[3];
                int[] a = [1, 2, 3];
                bit b = M(q[0]);
                int n = b;
                if ({{condition}}) { H(q[0]); }
            }
            """);
        var iff = r.Ir!.Operations[0].Body.OfType<QIf>().Single();
        Assert.NotNull(iff.Cond.Tree);
        return iff.Cond.Tree!;
    }

    [Fact]
    public void ParsesAGuardConjunctionWithComparisonsAboveAnd()
    {
        // 0 <= n && n < a.Count  ==  (0 <= n) && (n < a.Count)
        var tree = CondTree("0 <= n && n < a.Count");
        var and = Assert.IsType<QBinOp>(tree);
        Assert.Equal("&&", and.Op);

        var le = Assert.IsType<QBinOp>(and.Left);
        Assert.Equal("<=", le.Op);
        Assert.Equal(0L, Assert.IsType<QNumLit>(le.Left).Value);
        Assert.Equal("n", Assert.IsType<QNameRef>(le.Right).Name);

        var lt = Assert.IsType<QBinOp>(and.Right);
        Assert.Equal("<", lt.Op);
        Assert.Equal("n", Assert.IsType<QNameRef>(lt.Left).Name);
        var member = Assert.IsType<QMember>(lt.Right);
        Assert.Equal("Count", member.Member);
        Assert.Equal("a", Assert.IsType<QNameRef>(member.Base).Name);
    }

    [Fact]
    public void ParsesArithmeticWithMultiplyAbovePlus()
    {
        // n * 2 + 1  ==  (n * 2) + 1
        var eq = Assert.IsType<QBinOp>(CondTree("n * 2 + 1 == 5"));
        Assert.Equal("==", eq.Op);
        var plus = Assert.IsType<QBinOp>(eq.Left);
        Assert.Equal("+", plus.Op);
        var times = Assert.IsType<QBinOp>(plus.Left);
        Assert.Equal("*", times.Op);
        Assert.Equal("n", Assert.IsType<QNameRef>(times.Left).Name);
        Assert.Equal(2L, Assert.IsType<QNumLit>(times.Right).Value);
        Assert.Equal(1L, Assert.IsType<QNumLit>(plus.Right).Value);
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
                int[] a = [1, 2, 3];
                for i in 0..{{bound}} { a[i] = 1; }
                H(q[0]);
            }
            """);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Code == "QSEM031");
        Assert.Null(r.Ir);   // unrenderable IR stays unexposed (the stages view must not recurse it)
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
                int[] a = [{{element}}, 2];
                H(q[0]);
            }
            """);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Code == "QSEM031");
        Assert.Null(r.Ir);
    }

    [Fact]
    public void ParsesOrBelowAnd()
    {
        // a == 1 && b == 0 || n == 2  ==  ((a==1) && (b==0)) || (n==2)
        var or = Assert.IsType<QBinOp>(CondTree("b == 1 && n == 0 || n == 2"));
        Assert.Equal("||", or.Op);
        Assert.Equal("&&", Assert.IsType<QBinOp>(or.Left).Op);
        Assert.Equal("==", Assert.IsType<QBinOp>(or.Right).Op);
    }

    [Fact]
    public void ParsesRelationalAboveEquality()
    {
        // n == 1 < 2  ==  n == (1 < 2) — the SAME C-style ladder OpenQASM applies when it re-parses the
        // emitted tokens; folding both comparison families at one level once made the tree claim
        // (n == 1) < 2 while the executed QASM computed the other grouping.
        var eq = Assert.IsType<QBinOp>(CondTree("n == 1 < 2"));
        Assert.Equal("==", eq.Op);
        Assert.Equal("n", Assert.IsType<QNameRef>(eq.Left).Name);
        var lt = Assert.IsType<QBinOp>(eq.Right);
        Assert.Equal("<", lt.Op);
        Assert.Equal(1L, Assert.IsType<QNumLit>(lt.Left).Value);
        Assert.Equal(2L, Assert.IsType<QNumLit>(lt.Right).Value);
    }

    /// <summary>A deep UNARY chain recurses in the engine's parse-stack teardown and in ExprTree itself —
    /// depths the token cap admits must reach the QSEM031 guard as a clean reply, never kill the process
    /// (the compilation runs on a wide-stack worker thread precisely so the guard is always reachable).</summary>
    [Fact]
    public void RejectsAPathologicallyDeepUnaryChainCleanly()
    {
        var expr = string.Concat(Enumerable.Repeat("- ", 6000)) + "1";
        var r = Compiler.Compile($"operation Main() {{ use q = Qubit[1]; int x = {expr}; H(q[0]); }}");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Code == "QSEM031");
        Assert.Null(r.Ir);
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
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => e.Code)));
    }
}
