using Qora.Compiler;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// Fixed-size collection counts are a user-visible OpenQASM guarantee. These tests exercise the complete
/// HIR-to-MIR-to-target route; no test is coupled to a retired HIR target pass.
/// </summary>
public sealed class MirOpenQasmKnownCountTests
{
    [Fact]
    public void FixedBitAndQubitCountsReachOpenQasmAsLiterals()
    {
        var compilation = CompileSuccessfully("""
            function Increment(value: int): int {
                return value + 1;
            }

            function BitLength(flags: bit[]): int {
                return Increment(flags.Count);
            }

            operation QubitLength(register: Qubit[]) {
                var length: int = Increment(register.Count);
            }

            operation Main() {
                use register = Qubit[3];
                var flags: bit[] = new bit[2];
                QubitLength(register);
                var length: int = BitLength(flags);
            }
            """);

        var program = compilation.Targets.OpenQasm!.Program;
        var increment = program.RequireDefinition(
            definition =>
                definition.Kind == MirQasmCallableKind.Function
                && definition.Parameters.Length == 1
                && definition.Parameters[0].Type is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                }
                && definition.ReturnType is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                });
        var calls = CallsTo(program, increment.Id).ToArray();

        Assert.Contains(
            calls,
            item =>
                item.Body.DependsOn(
                    Assert.Single(item.Call.Arguments),
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "2" }));
        Assert.Contains(
            calls,
            item =>
                item.Body.DependsOn(
                    Assert.Single(item.Call.Arguments),
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "3" }));
        Assert.DoesNotContain(
            program.Expressions(),
            expression => expression is MirQasmSizeOfExpression);
    }

    [Fact]
    public void ShadowingDoesNotStealTheOuterCollectionsKnownCount()
    {
        var compilation = CompileSuccessfully("""
            function Identity(value: int): int {
                return value;
            }

            operation Main() {
                use values = Qubit[3];
                if (1 == 1) {
                    var values: int[] = [values.Count];
                    var inner: int = Identity(values.Count);
                }
            }
            """);

        var program = compilation.Targets.OpenQasm!.Program;
        Assert.Contains(
            program.Expressions(),
            expression =>
                expression is MirQasmLiteralExpression { Text: "3" });
        var identity = program.RequireDefinition(
            definition =>
                definition.Kind == MirQasmCallableKind.Function
                && definition.Parameters.Length == 1
                && definition.Parameters[0].Type is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                }
                && definition.ReturnType is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                });
        Assert.Contains(
            CallsTo(program, identity.Id),
            item =>
                item.Body.DependsOn(
                    Assert.Single(item.Call.Arguments),
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "1" }));
    }

    [Fact]
    public void SameNamedSiblingBitArraysKeepIndependentWidths()
    {
        var compilation = CompileSuccessfully("""
            function Identity(value: int): int {
                return value;
            }

            operation Main() {
                if (1 == 1) {
                    var flags: bit[] = new bit[2];
                    var left: int = Identity(flags.Count);
                } else {
                    var flags: bit[] = new bit[5];
                    var right: int = Identity(Identity(flags.Count));
                }
            }
            """);

        var program = compilation.Targets.OpenQasm!.Program;
        var identity = program.RequireDefinition(
            definition =>
                definition.Kind == MirQasmCallableKind.Function
                && definition.Parameters.Length == 1
                && definition.Parameters[0].Type is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                }
                && definition.ReturnType is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                });
        var calls = CallsTo(program, identity.Id).ToArray();

        Assert.Contains(
            calls,
            item =>
                item.Body.DependsOn(
                    Assert.Single(item.Call.Arguments),
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "2" }));
        Assert.Contains(
            calls,
            item =>
                item.Body.DependsOn(
                    Assert.Single(item.Call.Arguments),
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "5" }));
    }

    private static Compilation CompileSuccessfully(string source)
    {
        var compilation = Compiler.Compile(source);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        Assert.NotNull(compilation.Mir);
        Assert.NotNull(compilation.Targets.OpenQasm);
        return compilation;
    }

    private static IEnumerable<(
        IReadOnlyList<MirQasmStatement> Body,
        MirQasmFunctionCallExpression Call)> CallsTo(
        MirOpenQasmTargetProgram program,
        MirQasmCallableId target)
    {
        foreach (var body in Bodies(program))
        {
            foreach (var call in MirQasmTestModel
                         .Statements(body)
                         .SelectMany(MirQasmTestModel.Expressions)
                         .OfType<MirQasmFunctionCallExpression>())
            {
                if (call.Target is MirQasmUserFunctionTarget user
                    && user.Callable == target)
                {
                    yield return (body, call);
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<MirQasmStatement>> Bodies(
        MirOpenQasmTargetProgram program)
    {
        yield return program.EntryBody;
        foreach (var definition in program.Definitions)
            yield return definition.Body;
    }
}
