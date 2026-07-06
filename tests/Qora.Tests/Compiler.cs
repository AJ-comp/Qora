using System.Linq;

namespace Qora.Tests;

/// <summary>
/// Test harness over the whole compiler front end: <see cref="QoraParser.Parse"/> in-process (no CLI), then
/// assertions on the outcome. A program either compiles (<see cref="QoraParseResult.Success"/> with emitted
/// <see cref="QoraParseResult.Qasm"/>) or is rejected with a set of <see cref="QoraError"/> codes. These are
/// LOGIC-CONSISTENCY tests: for each construct, exactly the right diagnostic (or clean emission) — no missed
/// rejection (which would ship invalid OpenQASM) and no spurious/duplicate error.
/// </summary>
internal static class Compiler
{
    public static QoraParseResult Compile(string source) => QoraParser.Parse(source);

    private static IReadOnlyList<string> Codes(QoraParseResult r) => r.Errors.Select(e => e.Code).ToList();

    private static string Explain(QoraParseResult r) =>
        r.Success ? "compiled cleanly" : string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}"));

    /// <summary>The program is REJECTED and <paramref name="code"/> is among the reported errors.</summary>
    public static void Rejects(string source, string code)
    {
        var r = Compile(source);
        Assert.False(r.Success, $"expected rejection with {code}, but it compiled:\n  {source}");
        Assert.True(Codes(r).Contains(code), $"expected {code}, got [{Explain(r)}]\n  {source}");
    }

    /// <summary>The program is rejected with EXACTLY these codes (order-insensitive) — guards against a
    /// missed rejection AND against duplicate/extra diagnostics for one mistake.</summary>
    public static void RejectsExactly(string source, params string[] codes)
    {
        var r = Compile(source);
        Assert.False(r.Success, $"expected rejection, but it compiled:\n  {source}");
        Assert.Equal(codes.OrderBy(c => c, StringComparer.Ordinal),
                     Codes(r).OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>The program compiles cleanly (and therefore emits OpenQASM).</summary>
    public static void Accepts(string source)
    {
        var r = Compile(source);
        Assert.True(r.Success, $"expected success, got [{Explain(r)}]\n  {source}");
        Assert.False(string.IsNullOrWhiteSpace(r.Qasm), $"compiled but emitted no QASM:\n  {source}");
    }

    /// <summary>The program compiles and its emitted QASM contains <paramref name="fragment"/>.</summary>
    public static void Emits(string source, string fragment)
    {
        var r = Compile(source);
        Assert.True(r.Success, $"expected success, got [{Explain(r)}]\n  {source}");
        Assert.True(r.Qasm.Contains(fragment), $"emitted QASM missing `{fragment}`:\n{r.Qasm}");
    }
}
