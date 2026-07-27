using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class OpenQasmKnownCountLoweringTests
{
    [Fact]
    public void CommonSpecializationPreservesCountsAndTheTargetPassFoldsThem()
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

        var specialized = compilation.Hir.Specialized!;
        var bitLength = Assert.Single(
            specialized.Program.Operations,
            operation => operation.DisplayName == "BitLength");
        var qubitLength = Assert.Single(
            specialized.Program.Operations,
            operation => operation.DisplayName == "QubitLength");

        AssertCountArgument(bitLength, expectedCount: null);
        AssertCountArgument(qubitLength, expectedCount: null);

        var target = OpenQasmKnownCountLowering.Run(
            specialized.Program,
            new ExactHirSemanticContext(
                compilation.Hir.SpecializedValidation!.Model));
        var targetBitLength = Assert.Single(
            target.Operations,
            operation => operation.DisplayName == "BitLength");
        var targetQubitLength = Assert.Single(
            target.Operations,
            operation => operation.DisplayName == "QubitLength");

        AssertCountArgument(targetBitLength, expectedCount: 2);
        AssertCountArgument(targetQubitLength, expectedCount: 3);
    }

    [Fact]
    public void UsesPointOfDeclarationAndDoesNotGiveAnInnerClassicalArrayTheOuterQubitCount()
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

        var specialized = compilation.Hir.Specialized!;
        var lowered = OpenQasmKnownCountLowering.Run(
            specialized.Program,
            new ExactHirSemanticContext(
                compilation.Hir.SpecializedValidation!.Model));
        var main = Assert.Single(lowered.Operations, operation => operation.Name == "Main");
        var branch = Assert.Single(main.Body.OfType<QIf>());
        var declarations = branch.Then.OfType<QDecl>().ToList();

        var shadowingDeclaration = Assert.Single(
            declarations,
            declaration => declaration.Name == "values");
        var initializer = Assert.IsType<QArrayLiteral>(shadowingDeclaration.Value);
        Assert.Equal(
            3L,
            Assert.IsType<QNumLit>(
                Assert.IsType<QText>(
                    Assert.Single(initializer.Elements)).Tree).Value);

        var innerDeclaration = Assert.Single(
            declarations,
            declaration => declaration.Name == "inner");
        var innerCall = Assert.IsType<QCallNode>(
            Assert.IsType<QText>(innerDeclaration.Value).Tree);
        var innerCount = Assert.IsType<QMember>(Assert.Single(innerCall.Args));
        Assert.Equal(
            "values",
            Assert.IsType<QNameRef>(innerCount.Base).Name);
    }

    [Fact]
    public void KeepsSameNamedSiblingBitArraysIndependentInsideNestedCalls()
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

        var specialized = compilation.Hir.Specialized!;
        var lowered = OpenQasmKnownCountLowering.Run(
            specialized.Program,
            new ExactHirSemanticContext(
                compilation.Hir.SpecializedValidation!.Model));
        var main = Assert.Single(lowered.Operations, operation => operation.Name == "Main");
        var branch = Assert.IsType<QIf>(Assert.Single(main.Body));

        var left = Assert.Single(
            branch.Then.OfType<QDecl>(),
            declaration => declaration.Name == "left");
        var leftCall = Assert.IsType<QCallNode>(
            Assert.IsType<QText>(left.Value).Tree);
        Assert.Equal(2L, Assert.IsType<QNumLit>(Assert.Single(leftCall.Args)).Value);

        var right = Assert.Single(
            branch.Else.OfType<QDecl>(),
            declaration => declaration.Name == "right");
        var outerCall = Assert.IsType<QCallNode>(
            Assert.IsType<QText>(right.Value).Tree);
        var innerCall = Assert.IsType<QCallNode>(Assert.Single(outerCall.Args));
        Assert.Equal(5L, Assert.IsType<QNumLit>(Assert.Single(innerCall.Args)).Value);
    }

    private static void AssertCountArgument(
        QOperation operation,
        int? expectedCount)
    {
        var statement = Assert.Single(operation.Body);
        var value = statement switch
        {
            QReturn @return => @return.Value,
            QDecl declaration => declaration.Value,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected a return or declaration in `{operation.Name}`."),
        };
        var call = Assert.IsType<QCallNode>(Assert.IsType<QText>(value).Tree);
        var argument = Assert.Single(call.Args);
        if (expectedCount is int count)
            Assert.Equal((long)count, Assert.IsType<QNumLit>(argument).Value);
        else
            Assert.IsType<QMember>(argument);
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
        return compilation;
    }
}
