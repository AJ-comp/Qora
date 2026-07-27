using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// A Qora <c>const</c> is an immutable source binding. MIR expresses the same contract with immutable SSA
/// values, so tests inspect semantic symbols and typed target values instead of source variable spellings.
/// </summary>
public class ConstTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[1]; const c: int=5; c=10; }")]
    [InlineData("operation Main(){ use q=Qubit[2]; const c=M(q[0]); c=M(q[1]); }")]
    [InlineData("operation Main(){ use q=Qubit[2]; var r: bit=M(q[0]); const c: int=5; if(r==1){ c=10; } }")]
    [InlineData("operation Main(){ use q=Qubit[2]; const c: int=5; for i in 0..1 { c=10; } }")]
    [InlineData("operation Main(){ use q=Qubit[1]; var x: int=5; const c: int=x; c=10; }")]
    public void RejectsReassigningConst(string source) =>
        Compiler.Rejects(source, "QSEM024");

    [Fact]
    public void CompileTimeConstRemainsAConstSymbolAndALiteralTargetValue()
    {
        var integer = AssertConstSymbol(
            "operation Main(){ use q=Qubit[1]; const c: int = 5; Rx(c, q[0]); }",
            "c",
            QType.Int);
        var real = AssertConstSymbol(
            "operation Main(){ use q=Qubit[1]; const c = pi/4; Rx(c, q[0]); }",
            "c",
            QType.Float);

        Assert.Contains(
            integer.Program.Expressions(),
            expression =>
                expression is MirQasmLiteralExpression { Text: "5" });
        Assert.Contains(
            real.Program.Expressions(),
            expression =>
                expression is MirQasmLiteralExpression { Text: "pi" });
    }

    [Fact]
    public void RuntimeBoundConstStaysImmutableInTheSourceModelAndLowersToSsa()
    {
        var artifact = AssertConstSymbol(
            "operation Main(){ use q=Qubit[1]; var x: int=5; const c: int = x; Rx(c, q[0]); }",
            "c",
            QType.Int);

        Assert.Contains(
            artifact.Program.Statements()
                .OfType<MirQasmValueDeclarationStatement>(),
            declaration =>
                declaration.Type
                    is MirQasmScalarType { Kind: MirQasmScalarKind.Int });
    }

    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; const a = M(q[0]); if(a==1){ X(q[1]); } }")]
    [InlineData("operation Main(){ use q=Qubit[1]; var x: int=5; const c: int=x; Rx(c, q[0]); }")]
    [InlineData("operation Main(){ use q=Qubit[1]; var x=5; x=10; Rx(x, q[0]); }")]
    public void AcceptsValidBindings(string source) =>
        Compiler.Accepts(source);

    private static OpenQasmArtifact AssertConstSymbol(
        string source,
        string name,
        QType type)
    {
        var compilation = Compiler.Compile(source);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));
        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var declaration = analyzed.Program.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QDecl>()
            .Single(item => item.Name == name);
        var symbol = Assert.IsType<Symbol>(
            analyzed.Model.FindSymbol(declaration.Id));

        Assert.True(symbol.IsConst);
        Assert.Equal(type, symbol.Type);
        Assert.NotNull(compilation.Mir);
        return Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
    }
}
