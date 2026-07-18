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

    [Fact]
    public void ParsesOrBelowAnd()
    {
        // a == 1 && b == 0 || n == 2  ==  ((a==1) && (b==0)) || (n==2)
        var or = Assert.IsType<QBinOp>(CondTree("b == 1 && n == 0 || n == 2"));
        Assert.Equal("||", or.Op);
        Assert.Equal("&&", Assert.IsType<QBinOp>(or.Left).Op);
        Assert.Equal("==", Assert.IsType<QBinOp>(or.Right).Op);
    }
}
