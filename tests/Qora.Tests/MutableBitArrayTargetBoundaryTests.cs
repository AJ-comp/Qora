using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MutableBitArrayTargetBoundaryTests
{
    [Theory]
    [InlineData("var", QOwnershipMode.Borrowed)]
    [InlineData("move var", QOwnershipMode.Moved)]
    public void MutableBitArrayContractsAreAcceptedThroughMir(
        string contract,
        QOwnershipMode expectedOwnership)
    {
        var source = SourceWith(contract);

        var hirOnly = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(hirOnly.Succeeded, Describe(hirOnly));
        Assert.Null(hirOnly.Mir);
        Assert.Empty(hirOnly.Targets.Artifacts);

        var mirOnly = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));
        Assert.True(mirOnly.Succeeded, Describe(mirOnly));
        Assert.Empty(mirOnly.Targets.Artifacts);

        var mir = Assert.IsType<MirSnapshot>(mirOnly.Mir);
        var rewrite = Assert.Single(
            mir.Program.Callables,
            callable => callable.Id != mir.Program.EntryPoint.Id);
        var parameter = Assert.IsType<MirClassicalParameter>(
            Assert.Single(
                rewrite.Parameters,
                candidate => candidate is MirClassicalParameter));
        var parameterType = rewrite.RequireValue(parameter.Value).Type;

        Assert.Equal(expectedOwnership, parameter.Ownership);
        Assert.Equal(QAccessMode.Mutable, parameter.Access);
        Assert.Equal(QType.Bit, parameterType.ElementType);
        Assert.True(parameterType.IsArray);
    }

    [Fact]
    public void OpenQasmRejectsBorrowedMutableBitArrayAtTheTargetBoundary()
    {
        var source = SourceWith("var");
        var compiled = QoraCompiler.Compile(source);

        Assert.False(compiled.Succeeded);
        Assert.NotNull(compiled.Mir);
        Assert.Null(compiled.Targets.OpenQasm);
        var diagnostic = Assert.Single(compiled.Diagnostics);
        Assert.Equal(CompilationStage.OpenQasm, diagnostic.Stage);
        Assert.Equal("QASM002", diagnostic.Error.Code);
        var origin = Assert.IsType<DiagnosticOrigin.Target>(diagnostic.Origin);
        Assert.Equal(TargetBackend.OpenQasm, origin.Backend);
        Assert.Same(compiled.Mir, origin.Input);
        var location = Assert.IsType<MirHirOrigin>(origin.Location);
        var span = Assert.IsType<SourceSpan>(location.Span);
        Assert.Equal("flags", source[span.Start..span.End]);
        Assert.Equal((span.Start, span.End), (diagnostic.Error.Start, diagnostic.Error.End));
    }

    [Fact]
    public void MovedMutableBitArrayLowersToAValueParameterAndKeepsInternalMutation()
    {
        var compiled = QoraCompiler.Compile(SourceWith("move var"));

        Assert.True(compiled.Succeeded, Describe(compiled));
        var target = Assert.IsType<OpenQasmArtifact>(
            compiled.Targets.OpenQasm).Program;
        var rewrite = Assert.Single(target.Definitions);
        var flags = RequireBitRegisterParameter(rewrite);

        Assert.Equal(MirQasmParameterAccess.Value, flags.Access);
        Assert.Contains(
            MirQasmTestModel.Statements(rewrite.Body)
                .OfType<MirQasmAssignmentStatement>(),
            assignment => IsParameterIndexWrite(
                rewrite.Body,
                assignment,
                flags.Id,
                "0",
                "1"));

        var branch = Assert.Single(
            MirQasmTestModel.Statements(rewrite.Body)
                .OfType<MirQasmIfStatement>());
        Assert.True(
            rewrite.Body.DependsOn(
                branch.Condition,
                expression => expression is MirQasmIndexExpression
                {
                    Base: MirQasmParameterReferenceExpression reference,
                } && reference.Parameter == flags.Id));
        Assert.Contains(
            MirQasmTestModel.Statements(branch.Then)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmBuiltinGateTarget
            {
                EmittedName: "x",
            });
        Assert.Contains(
            target.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget user
                     && user.Callable == rewrite.Id);
    }

    [Fact]
    public void NestedMovedMutableBitArraysRemainValueParametersAtEachCall()
    {
        var compiled = QoraCompiler.Compile("""
            operation Inner(move var flags: bit[], q: Qubit) {
                flags[0] = 1;
                if (flags[0] == 1) {
                    X(q);
                }
            }

            operation Outer(move var flags: bit[], q: Qubit) {
                flags[1] = 1;
                Inner(move var flags, q);
            }

            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = [0, 0];
                Outer(move var flags, q[0]);
            }
            """);

        Assert.True(compiled.Succeeded, Describe(compiled));
        var target = Assert.IsType<OpenQasmArtifact>(
            compiled.Targets.OpenQasm).Program;
        var outerCall = Assert.Single(
            target.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var outer = target.Resolve(
            Assert.IsType<MirQasmUserQuantumTarget>(outerCall.Target));
        var outerFlags = RequireBitRegisterParameter(outer);
        var innerCall = Assert.Single(
            MirQasmTestModel.Statements(outer.Body)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var inner = target.Resolve(
            Assert.IsType<MirQasmUserQuantumTarget>(innerCall.Target));
        var innerFlags = RequireBitRegisterParameter(inner);

        Assert.Equal(MirQasmParameterAccess.Value, outerFlags.Access);
        Assert.Equal(MirQasmParameterAccess.Value, innerFlags.Access);
        Assert.Contains(
            MirQasmTestModel.Statements(outer.Body)
                .OfType<MirQasmAssignmentStatement>(),
            assignment => IsParameterIndexWrite(
                outer.Body,
                assignment,
                outerFlags.Id,
                "1",
                "1"));
        Assert.Contains(
            innerCall.Operands,
            operand => operand is MirQasmParameterReferenceExpression reference
                       && reference.Parameter == outerFlags.Id);
        Assert.Contains(
            MirQasmTestModel.Statements(inner.Body)
                .OfType<MirQasmAssignmentStatement>(),
            assignment => IsParameterIndexWrite(
                inner.Body,
                assignment,
                innerFlags.Id,
                "0",
                "1"));
    }

    [Fact]
    public void MovedMutableBitArrayCannotBeUsedAfterTheCall()
    {
        var compiled = QoraCompiler.Compile(
            """
            operation Consume(move var flags: bit[]) {}

            operation Main() {
                var flags: bit[] = [0, 1];
                Consume(move var flags);
                var observed: bit = flags[0];
            }
            """,
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly));

        Assert.False(compiled.Succeeded);
        Assert.Contains(
            compiled.Diagnostics,
            diagnostic => diagnostic.Stage == CompilationStage.HirValidation
                          && diagnostic.Error.Code == "QSEM039");
    }

    [Fact]
    public void ReadonlyBitArrayParameterStillLowersToOpenQasm()
    {
        var compiled = QoraCompiler.Compile("""
            operation Read(flags: bit[], q: Qubit) {
                if (flags[0] == 1) {
                    X(q);
                }
            }

            operation Main() {
                use q = Qubit[1];
                const flags: bit[] = [1];
                Read(flags, q[0]);
            }
            """);

        Assert.True(compiled.Succeeded, Describe(compiled));
        Assert.NotNull(compiled.Targets.OpenQasm);
    }

    [Fact]
    public void FunctionBitArrayParametersRemainReadonly()
    {
        var compiled = QoraCompiler.Compile(
            """
            function First(var flags: bit[]): bit {
                return flags[0];
            }

            operation Main() {}
            """,
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly));

        Assert.False(compiled.Succeeded);
        var diagnostic = Assert.Single(compiled.Diagnostics);
        Assert.Equal(CompilationStage.HirValidation, diagnostic.Stage);
        Assert.Equal("QSEM038", diagnostic.Error.Code);
    }

    private static string SourceWith(string contract) => $$"""
        operation Rewrite({{contract}} flags: bit[], q: Qubit) {
            flags[0] = 1;
            if (flags[0] == 1) {
                X(q);
            }
        }

        operation Main() {
            use q = Qubit[1];
            var flags: bit[] = [0, 1];
            Rewrite({{contract}} flags, q[0]);
        }
        """;

    private static MirQasmParameter RequireBitRegisterParameter(
        MirQasmCallableDefinition definition) =>
        Assert.Single(
            definition.Parameters,
            parameter => parameter.Type is MirQasmBitType
            {
                Width: 2,
                IsRegister: true,
            });

    private static bool IsParameterIndexWrite(
        IEnumerable<MirQasmStatement> ownerBody,
        MirQasmAssignmentStatement assignment,
        MirQasmParameterId parameter,
        string index,
        string value) =>
        assignment is
        {
            Target: MirQasmIndexExpression
            {
                Base: MirQasmParameterReferenceExpression reference,
                Index: var actualIndex,
            },
            Value: var actualValue,
        }
        && reference.Parameter == parameter
        && ownerBody.DependsOn(
            actualIndex,
            expression => expression is MirQasmLiteralExpression
            {
                Text: var actual,
            } && actual == index)
        && ownerBody.DependsOn(
            actualValue,
            expression => expression is MirQasmLiteralExpression
            {
                Text: var actual,
            } && actual == value);

    private static string Describe(Compilation compilation) =>
        string.Join(
            " | ",
            compilation.Diagnostics.Select(
                diagnostic =>
                    $"{diagnostic.Stage}/{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
}
