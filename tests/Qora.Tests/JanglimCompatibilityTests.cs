using Qora.Compiler;

namespace Qora.Tests;

/// <summary>Source-language contracts at the Janglim lexer/parser boundary.</summary>
public sealed class JanglimCompatibilityTests
{
    [Fact]
    public void KeywordPrefixesRemainWholeIdentifiers()
    {
        const string source = """
            function operationValue(integerValue: int): int {
                var returnValue: int = integerValue + 1;
                return returnValue;
            }
            operation Main() {
                var ifCount: int = operationValue(1);
                var ControlledCount: int = ifCount;
            }
            """;

        var syntax = QoraParser.Parse(source);

        Assert.True(syntax.Succeeded);
        foreach (var identifier in new[]
                 {
                     "operationValue", "integerValue", "returnValue", "ifCount", "ControlledCount",
                 })
        {
            Assert.Contains(syntax.Tokens, token => token.Text == identifier);
        }
        Compiler.Accepts(source);
    }

    [Fact]
    public void DecimalLiteralAndRangeOperatorRemainDistinctTokens()
    {
        const string source = """
            operation Main() {
                use q = Qubit[2];
                for i in 0..1 {
                    Rx(0.5, q[i]);
                }
            }
            """;

        var syntax = QoraParser.Parse(source);

        Assert.True(syntax.Succeeded);
        Assert.Single(syntax.Tokens, token => token.Text == "0.5");
        Assert.Single(syntax.Tokens, token => token.Text == "..");
        Assert.DoesNotContain(syntax.Tokens, token => token.Text == ".");
        Compiler.Accepts(source);
    }

    [Fact]
    public void ComparisonAndBooleanOperatorsUseTheirCompleteSpelling()
    {
        const string source = """
            operation Main() {
                var value: int = 1;
                var flag: bool = false;
                if (value <= 2 && value >= 0 || value != 1) {
                    value = value + 1;
                }
                if (value == 0) {
                    value = value + 1;
                }
                if (!flag) {
                    value = value - 1;
                }
            }
            """;

        var syntax = QoraParser.Parse(source);

        Assert.True(syntax.Succeeded);
        foreach (var spelling in new[] { "<=", "&&", ">=", "||", "!=", "==", "!" })
        {
            Assert.Single(syntax.Tokens, token => token.Text == spelling);
        }
        Compiler.Accepts(source);
    }

    [Fact]
    public void LineCommentsDoNotConsumeDivisionOrTheNextLine()
    {
        const string source = """
            function Half(value: int): int {
                return value/2; // ignored: / operation broken( <=
            }
            operation Main() {
                var result: int = Half(4);
            } // trailing comment without a final newline
            """;

        var syntax = QoraParser.Parse(source);

        Assert.True(syntax.Succeeded);
        Assert.Single(syntax.Tokens, token => token.Text == "/");
        Assert.Contains(syntax.Tokens, token => token.Text == "Main");
        Assert.DoesNotContain(syntax.Tokens, token => token.Text.Contains("ignored"));
        Assert.DoesNotContain(syntax.Tokens, token => token.Text.Contains("trailing"));
        Compiler.Accepts(source);
    }

    [Theory]
    [InlineData("1value")]
    [InlineData("0.5value")]
    [InlineData("1_0")]
    public void NumericLiteralsCannotRunIntoIdentifiers(string expression)
    {
        var source = $"operation Main() {{ var value: float = {expression}; }}";

        var compilation = Compiler.Compile(source);

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Stage == CompilationStage.Syntax);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Error.Code == "QORA0000");
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }

    [Fact]
    public void CommentMarkersInsideAnImportPathRemainStringContent()
    {
        const string source = """
            import "lib//gates.qor";
            operation Main() {}
            """;

        // Syntax-only parsing must preserve the quoted path without attempting file I/O.
        var syntax = QoraParser.Parse(source);

        Assert.True(syntax.Succeeded);
        Assert.Single(syntax.Tokens, token => token.Text == "\"lib//gates.qor\"");
        Assert.Contains(syntax.Tokens, token => token.Text == "Main");
    }
}
