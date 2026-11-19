using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class ConstantSizeExpressionTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("1 + 2", 3)]
    [InlineData("(1 + 2) * 2", 6)]
    [InlineData("8 / 2 - 1", 3)]
    [InlineData("7 / 2", 3)]
    [InlineData("-(-3)", 3)]
    [InlineData("10 + -7", 3)]
    [InlineData("2147483648 - 2147483645", 3)]
    [InlineData("9223372036854775807 - 9223372036854775804", 3)]
    [InlineData("(-9223372036854775807 - 1) + 9223372036854775807 + 4", 3)]
    public void ArithmeticSizesProduceTheSameConcreteQubitAndArrayLengths(string expression, int expected)
    {
        var compilation = Compiler.Compile($$"""
            operation Main() {
                use q = Qubit[{{expression}}];
                var values: int[] = new int[{{expression}}];
                H(q[{{expected - 1}}]);
                values[{{expected - 1}}] = 7;
            }
            """);
        AssertSucceeded(compilation);
        AssertDeclaredLengths(compilation, expected, expected);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var qubit = Assert.Single(mir.Program.EntryPoint.Blocks
            .SelectMany(block => block.Instructions).OfType<MirQubitAllocate>());
        Assert.Equal(expected, qubit.Result.Length);
        Assert.Empty(QoraMirVerifier.Verify(mir.Program));
    }

    [Fact]
    public void TypedAndInferredConstChainsAreResolvedBeforeBothAllocations()
    {
        var compilation = Compiler.Compile("""
            operation Main() {
                const first: int = 2;
                const second = first + 1;
                const size: int = second * 2;
                use q = Qubit[size];
                var values: int[] = new int[size + 1];
                H(q[5]);
                values[6] = size;
            }
            """);
        AssertSucceeded(compilation);
        AssertDeclaredLengths(compilation, 6, 7);
    }

    [Theory]
    [InlineData("bool", "true")]
    [InlineData("bit", "1")]
    [InlineData("int", "2")]
    [InlineData("float", "2.0")]
    [InlineData("angle", "pi")]
    public void ConstSizesWorkForEveryClassicalElementType(string type, string value) =>
        Compiler.Accepts($$"""
            operation Main() {
                const size: int = 2;
                var values: {{type}}[] = new {{type}}[size + 1];
                const frozen: {{type}}[] = new {{type}}[size];
                values[2] = {{value}};
            }
            """);

    [Fact]
    public void KnownArrayAndQubitCountsUseTheExistingConstantFolder()
    {
        var compilation = Compiler.Compile("""
            operation Main() {
                var source: int[] = [1, 2];
                const size: int = source.Count + 1;
                use q = Qubit[size];
                var values: int[] = new int[q.Count + source.Count];
                H(q[2]);
                values[4] = 7;
            }
            """);
        AssertSucceeded(compilation);
        AssertDeclaredLengths(compilation, 3, 5);
    }

    [Fact]
    public void ConstShadowingReadsTheOuterBindingAndKeepsTheOuterSize()
    {
        var compilation = Compiler.Compile("""
            operation Main(): int {
                const size: int = 2;
                use q = Qubit[size];
                if (true) {
                    const size: int = size + 1;
                    var inner: int[] = new int[size];
                    inner[2] = 7;
                }
                var values: int[] = new int[size];
                return values.Count;
            }
            """);
        AssertSucceeded(compilation);
        AssertDeclaredLengths(compilation, 2, 2);
    }

    [Theory]
    [InlineData("function")]
    [InlineData("operation")]
    public void ClassicalArrayConstSizesRemainValidInsideHelpersAndControlFlow(string callableKind) =>
        Compiler.Accepts($$"""
            {{callableKind}} Work(): int {
                const size = 2;
                var values: int[] = new int[size];
                if (true) {
                    const innerSize = size + 1;
                    var inner: int[] = new int[innerSize];
                    inner[2] = 4;
                }
                for i in 0..1 {
                    var temporary: int[] = new int[size + 1];
                    temporary[2] = 7;
                }
                return values.Count;
            }
            operation Main(): int { return Work(); }
            """);

    public static IEnumerable<object[]> InvalidNumericSizes()
    {
        foreach (var expression in new[]
        {
            "0", "-1", "2 - 2", "1 - 2", "1 / 2", "3 / 0",
            "2147483648", "2147483647 + 1", "999999999999999999999999999999",
            "9223372036854775807 + 1", "3037000500 * 3037000500",
            "(9223372036854775807 + 1) - 9223372036854775807",
            "(-9223372036854775807 - 1) / -1",
            "-(-9223372036854775807 - 1)",
        })
        {
            yield return [false, expression];
            yield return [true, expression];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidNumericSizes))]
    public void InvalidNumericSizesAreSemanticErrorsNotCompilerCrashes(bool qubits, string expression) =>
        AssertRejected($"operation Main() {{ {Allocation(qubits, expression)} }}", "QSEM016");

    [Theory]
    [InlineData(false, "0")]
    [InlineData(true, "0")]
    [InlineData(false, "-1")]
    [InlineData(true, "-1")]
    [InlineData(false, "2147483647 + 1")]
    [InlineData(true, "2147483647 + 1")]
    [InlineData(false, "9223372036854775807 + 1")]
    [InlineData(true, "9223372036854775807 + 1")]
    public void InvalidConstValuesCannotBypassSizeValidation(bool qubits, string expression) =>
        AssertRejected($$"""
            operation Main() {
                const size: int = {{expression}};
                {{Allocation(qubits, "size")}}
            }
            """, "QSEM016");

    public static IEnumerable<object[]> NonIntegerSizes()
    {
        foreach (var expression in new[] { "true", "false", "1 < 2", "3.0", "pi", "3 / 1.0" })
        {
            yield return [false, expression];
            yield return [true, expression];
        }
    }

    [Theory]
    [MemberData(nameof(NonIntegerSizes))]
    public void SizeExpressionsMustHaveScalarIntType(bool qubits, string expression) =>
        AssertRejected($"operation Main() {{ {Allocation(qubits, expression)} }}", "QSEM016");

    [Theory]
    [InlineData(false, "float", "3")]
    [InlineData(true, "float", "3")]
    [InlineData(false, "bit", "1")]
    [InlineData(true, "bit", "1")]
    [InlineData(false, "bool", "true")]
    [InlineData(true, "bool", "true")]
    public void AConstFoldedNumberDoesNotOverrideItsDeclaredType(bool qubits, string type, string value) =>
        AssertRejected($$"""
            operation Main() {
                const size: {{type}} = {{value}};
                {{Allocation(qubits, "size")}}
            }
            """, "QSEM016");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstBitArithmeticMayProduceAnIntSizedExpression(bool qubits) =>
        Compiler.Accepts($$"""
            operation Main() {
                const bitValue: bit = 1;
                {{Allocation(qubits, "bitValue + 2")}}
            }
            """);

    public static IEnumerable<object[]> RuntimeSizes()
    {
        foreach (var setup in new[]
        {
            "var size: int = 3;",
            "var original: int = 3; const size: int = original;",
            "const size: int = GetSize();",
            "var values: int[] = [3]; const size: int = values[0];",
        })
        {
            yield return [false, setup];
            yield return [true, setup];
        }
    }

    [Theory]
    [MemberData(nameof(RuntimeSizes))]
    public void ImmutableBindingsDoNotMakeRuntimeValuesCompileTimeConstants(bool qubits, string setup) =>
        AssertRejected($$"""
            function GetSize(): int { return 3; }
            operation Main() {
                {{setup}}
                {{Allocation(qubits, "size")}}
            }
            """, "QSEM016");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionCallsAreNotImplicitlyExecutedToDetermineSizes(bool qubits) =>
        AssertRejected($$"""
            function GetSize(): int { return 3; }
            operation Main() { {{Allocation(qubits, "GetSize()")}} }
            """, "QSEM016");

    [Theory]
    [InlineData("size")]
    [InlineData("size + 1")]
    public void HelperScalarParametersDoNotBecomeCompileTimeSizesAtCallSites(string expression) =>
        AssertRejected($$"""
            operation Work(size: int) { var values: int[] = new int[{{expression}}]; }
            operation Main() { Work(3); }
            """, "QSEM016");

    [Theory]
    [InlineData("int", "var source: int[] = [1, 2]; Work(source);")]
    [InlineData("Qubit", "use source = Qubit[2]; Work(source);")]
    public void UnknownHelperArrayCountsDoNotIntroduceDeferredAllocationSpecialization(string type, string call) =>
        AssertRejected($$"""
            operation Work(source: {{type}}[]) {
                var values: int[] = new int[source.Count];
            }
            operation Main() { {{call}} }
            """, "QSEM016");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownNamesKeepTheirOrdinaryNameDiagnostic(bool qubits) =>
        AssertRejected($"operation Main() {{ {Allocation(qubits, "missing")} }}", "QSEM025");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SizeReferencesCannotUseALaterDeclaration(bool qubits) =>
        AssertRejected($$"""
            operation Main() {
                {{Allocation(qubits, "size")}}
                const size: int = 3;
            }
            """, "QSEM025");

    [Theory]
    [InlineData(false, "(2 - 2)", "2 - 2")]
    [InlineData(true, "(2 - 2)", "2 - 2")]
    [InlineData(false, "runtimeSize + 1", "runtimeSize + 1")]
    [InlineData(true, "runtimeSize + 1", "runtimeSize + 1")]
    public void InvalidSizeDiagnosticsPointToTheSizeExpression(bool qubits, string expression, string expectedFragment)
    {
        var source = $$"""
            operation Main() {
                var runtimeSize: int = 3;
                {{Allocation(qubits, expression)}}
            }
            """;
        var compilation = Compiler.Compile(source);
        Assert.False(compilation.Succeeded);
        var diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Error.Code == "QSEM016");
        var span = Assert.IsType<SourceSpan>(diagnostic.Error.Span);
        Assert.Equal(expectedFragment, source[span.Start..span.End]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComputedSizesDriveExistingOutOfBoundsValidation(bool qubits)
    {
        var access = qubits ? "H(q[3]);" : "values[3] = 7;";
        AssertRejected($$"""
            operation Main() {
                const size: int = 2;
                {{Allocation(qubits, "size + 1")}}
                {{access}}
            }
            """, "QSEM016");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaximumSupportedSizeIsValidatedWithoutMaterializingAnEnormousTarget(bool qubits)
    {
        var compilation = QoraCompiler.Compile($$"""
            operation Main() {
                const size: int = 2147483646;
                {{Allocation(qubits, "size + 1")}}
            }
            """, new CompilationOptions(outputPlan: CompilationOutputPlan.HirOnly));
        AssertSucceeded(compilation);
        var artifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var declaration = Assert.Single(artifact.Program.EntryCallable!.Body,
            item => qubits ? item is HirQubitDeclarationStatement : item is HirVariableDeclarationStatement { Name: "values" });
        var symbol = artifact.Model.FindSymbol(declaration.Id)!;
        Assert.Equal(int.MaxValue, qubits ? symbol.RegisterSize : symbol.ArrayLength);
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }

    [Theory]
    [InlineData("operation Work() { const size = 2; use q = Qubit[size]; } operation Main() { Work(); }")]
    [InlineData("operation Main() { const size = 2; if (true) { use q = Qubit[size]; } }")]
    [InlineData("operation Main() { const size = 2; for i in 0..1 { use q = Qubit[size]; } }")]
    public void ConstantSizesDoNotBroadenQubitAllocationPlacement(string source) =>
        AssertRejected(source, "QSEM012");

    [Fact]
    public void SizeExpressionChildrenKeepUniqueIdsThroughHelperSpecialization()
    {
        var compilation = Compiler.Compile("""
            operation Work(q: Qubit[]) {
                const size: int = 2;
                var scratch: int[] = new int[size + 1];
                X(q[0]);
            }
            operation Main() {
                const size: int = 2;
                use first = Qubit[size + 1];
                use second = Qubit[size + 2];
                Work(first);
                Work(second);
            }
            """);
        AssertSucceeded(compilation);
        foreach (var snapshot in compilation.Hir.Snapshots)
        {
            var expressions = HirExpressions.DescendantsAndSelf(snapshot.Program).ToArray();
            Assert.Equal(expressions.Length, expressions.Select(expression => expression.Id).Distinct().Count());
            foreach (var declaration in snapshot.Structure.Nodes.Values.OfType<HirQubitDeclarationStatement>())
            {
                Assert.IsType<HirBinaryExpression>(declaration.Size);
                Assert.Same(declaration.Size, Assert.Single(declaration.Children()));
                Assert.Equal(declaration.Id, snapshot.Structure.RequireParent(declaration.Size.Id));
            }
            foreach (var creation in expressions.OfType<HirArrayCreationExpression>())
            {
                Assert.IsType<HirBinaryExpression>(creation.Length);
                Assert.Same(creation.Length, Assert.Single(creation.Children()));
                Assert.Equal(creation.Id, snapshot.Structure.RequireParent(creation.Length.Id));
            }
        }

        var specialized = Assert.IsType<HirSnapshot>(compilation.Hir.Specialized);
        var helpers = specialized.Program.Callables.Where(callable => callable.DisplayName == "Work").ToArray();
        Assert.Equal(2, helpers.Length);
        var lengths = helpers.SelectMany(helper => HirExpressions.DescendantsAndSelf(helper.Body))
            .OfType<HirArrayCreationExpression>().Select(creation => creation.Length).ToArray();
        Assert.Equal(2, lengths.Length);
        Assert.NotEqual(lengths[0].Id, lengths[1].Id);
    }

    [Fact]
    public void ComputedQubitAndBitArrayWidthsReachGenericHelperSpecializations()
    {
        var compilation = Compiler.Compile("""
            operation Work(q: Qubit[], bits: bit[]) {
                X(q[0]);
                var saved: bit = bits[0];
            }
            operation Main() {
                const size: int = 2;
                use first = Qubit[size + 1];
                use second = Qubit[size * 2];
                var firstBits: bit[] = new bit[size + 1];
                var secondBits: bit[] = new bit[size * 2];
                Work(first, firstBits);
                Work(second, secondBits);
            }
            """);
        AssertSucceeded(compilation);
        var artifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var helpers = artifact.Program.Callables.Where(callable => callable.DisplayName == "Work").ToArray();
        Assert.Equal(2, helpers.Length);
        Assert.Equal(new[] { 3, 4 }, helpers.Select(helper => helper.Parameters[0].RegisterSize!.Value).Order());
        foreach (var helper in helpers)
            Assert.Equal(helper.Parameters[0].RegisterSize, helper.Parameters[1].RegisterSize);
    }

    [Fact]
    public void AllocationSizeArithmeticIsNotEmittedAsRuntimeMirInstructions()
    {
        var compilation = Compiler.Compile("""
            operation Main() {
                use q = Qubit[(2 + 1) * 2];
                var values: int[] = new int[7 - 1];
            }
            """);
        AssertSucceeded(compilation);
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var instructions = mir.Program.EntryPoint.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(instructions, instruction => instruction is MirBinary);
        Assert.Equal(6, Assert.Single(instructions.OfType<MirQubitAllocate>()).Result.Length);
        Assert.Equal(6, Assert.Single(instructions.OfType<MirArrayCreate>()).Result.Type.KnownLength);

        var sizeNodes = HirExpressions.DescendantsAndSelf(mir.HirArtifact.Program.EntryCallable!.Body)
            .OfType<HirArrayCreationExpression>()
            .SelectMany(creation => HirExpressions.DescendantsAndSelf(creation.Length))
            .Select(expression => expression.Id)
            .ToHashSet();
        var qubits = Assert.Single(mir.HirArtifact.Program.EntryCallable.Body.OfType<HirQubitDeclarationStatement>());
        sizeNodes.UnionWith(HirExpressions.DescendantsAndSelf(qubits.Size).Select(expression => expression.Id));
        Assert.DoesNotContain(instructions,
            instruction => sizeNodes.Contains(instruction.Origin.SourceHirOrigin.HirNodeId));
    }

    private static string Allocation(bool qubits, string expression) => qubits
        ? $"use q = Qubit[{expression}];"
        : $"var values: int[] = new int[{expression}];";

    private static void AssertDeclaredLengths(Compilation compilation, int qubitLength, int arrayLength)
    {
        var artifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var body = artifact.Program.EntryCallable!.Body;
        var qubits = Assert.Single(body.OfType<HirQubitDeclarationStatement>());
        var values = Assert.Single(body.OfType<HirVariableDeclarationStatement>(), declaration => declaration.Name == "values");
        Assert.Equal(qubitLength, artifact.Model.FindSymbol(qubits.Id)!.RegisterSize);
        Assert.Equal(arrayLength, artifact.Model.FindSymbol(values.Id)!.ArrayLength);
    }

    private static void AssertSucceeded(Compilation compilation) =>
        Assert.True(compilation.Succeeded, string.Join(" | ", compilation.Diagnostics.Select(
            diagnostic => $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));

    private static void AssertRejected(string source, string expectedCode)
    {
        var compilation = Compiler.Compile(source);
        Assert.False(compilation.Succeeded, $"Expected {expectedCode}, but compiled:\n{source}");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Error.Code == expectedCode);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code is "QINTERNAL" or "QORA0000");
        Assert.Null(compilation.Mir);
        Assert.Empty(compilation.Targets.Artifacts);
    }
}
