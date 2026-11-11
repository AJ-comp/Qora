using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class OpenQasmOperationReturnTests
{
    [Theory]
    [InlineData("int", "42", "int")]
    [InlineData("bit", "1", "bit")]
    [InlineData("bool", "true", "bool")]
    [InlineData("bool", "false", "bool")]
    [InlineData("float", "0.125", "float")]
    [InlineData("angle", "pi / 4", "float")]
    public void ExportsOnlyTheEntryReturnThroughOneTypedOutput(
        string sourceType,
        string value,
        string transportType)
    {
        var artifact = Compile($"operation Main(): {sourceType} {{ return {value}; }}");
        var output = Assert.IsType<MirQasmValueDeclarationStatement>(artifact.Program.EntryOutput);

        Assert.Equal(MirCallableKind.Operation, artifact.Source.Program.EntryPoint.Kind);
        Assert.NotNull(artifact.Source.Program.EntryPoint.ReturnType);
        Assert.True(output.IsOutput);
        Assert.Null(output.Initializer);
        Assert.Contains($"output {transportType} {output.EmittedName};", artifact.Text);
        Assert.Single(artifact.Program.Statements()
            .OfType<MirQasmValueDeclarationStatement>(),
            declaration => declaration.IsOutput);
        Assert.Empty(artifact.Program.Statements()
            .OfType<MirQasmMeasurementAssignmentStatement>());

        var assignment = Assert.IsType<MirQasmAssignmentStatement>(artifact.Program.EntryBody[^1]);
        Assert.Equal(
            new MirQasmDeclarationReferenceExpression(output.Declaration),
            assignment.Target);
        if (sourceType == "angle")
        {
            Assert.IsType<MirQasmFloatCastExpression>(assignment.Value);
            Assert.Contains($"{output.EmittedName} = float(", artifact.Text);
        }
        else
        {
            Assert.IsType<MirQasmDeclarationReferenceExpression>(assignment.Value);
        }
        Assert.Empty(MirQasmTestModel.Statements(artifact.Program.EntryBody)
            .OfType<MirQasmReturnStatement>());
    }

    [Fact]
    public void VoidEntryExportsCompletionWithoutExposingInternalMeasurements()
    {
        var artifact = Compile("""
            operation Read(q: Qubit) {
                var first: bit = M(q);
                X(q);
                var second: bit = M(q);
            }
            operation Main() {
                use q = Qubit[1];
                Read(q[0]);
            }
            """);
        var output = Assert.IsType<MirQasmValueDeclarationStatement>(artifact.Program.EntryOutput);

        Assert.Null(artifact.Source.Program.EntryPoint.ReturnType);
        Assert.Equal(new MirQasmScalarType(MirQasmScalarKind.Int), output.Type);
        Assert.Equal(2, artifact.Program.Statements()
            .OfType<MirQasmMeasurementAssignmentStatement>().Count());
        Assert.DoesNotContain(artifact.Program.Definitions.SelectMany(definition =>
                MirQasmTestModel.Statements(definition.Body))
            .OfType<MirQasmValueDeclarationStatement>(),
            declaration => declaration.IsOutput);
        var assignment = Assert.IsType<MirQasmAssignmentStatement>(artifact.Program.EntryBody[^1]);
        Assert.Equal(new MirQasmLiteralExpression("1"), assignment.Value);
        Assert.Equal(
            new MirQasmDeclarationReferenceExpression(output.Declaration),
            assignment.Target);
    }

    [Fact]
    public void VoidEntryWithoutQubitsStillHasACompletionTransport()
    {
        var artifact = Compile("operation Main() { }");
        var output = Assert.IsType<MirQasmValueDeclarationStatement>(artifact.Program.EntryOutput);

        Assert.Null(artifact.Source.Program.EntryPoint.ReturnType);
        Assert.Contains($"output int {output.EmittedName};", artifact.Text);
        Assert.Contains($"{output.EmittedName} = 1;", artifact.Text);
        Assert.Empty(artifact.Program.Statements()
            .OfType<MirQasmMeasurementAssignmentStatement>());
    }

    [Fact]
    public void ReturningAnEarlierMeasurementDoesNotRemeasureTheQubit()
    {
        var artifact = Compile("""
            operation Main(): bit {
                use q = Qubit[1];
                var result: bit = M(q[0]);
                X(q[0]);
                return result;
            }
            """);
        var measurement = Assert.Single(artifact.Program.Statements()
            .OfType<MirQasmMeasurementAssignmentStatement>());
        var finalAssignment = Assert.IsType<MirQasmAssignmentStatement>(artifact.Program.EntryBody[^1]);
        var returnStorage = Assert.IsType<MirQasmDeclarationReferenceExpression>(finalAssignment.Value);
        var returnAssignment = Assert.Single(artifact.Program.Statements()
            .OfType<MirQasmAssignmentStatement>(),
            assignment => assignment.Target == returnStorage);

        Assert.Equal(measurement.Target, returnAssignment.Value);
        Assert.Single(artifact.Program.Statements().OfType<MirQasmQuantumApplyStatement>());
    }

    [Fact]
    public void ReturningHelperOperationRemainsEffectfulAndUsesOneValueCall()
    {
        var artifact = Compile("""
            operation Read(q: Qubit): bit {
                var result: bit = M(q);
                X(q);
                return result;
            }
            operation Main(): bit {
                use q = Qubit[1];
                return Read(q[0]);
            }
            """);
        var helper = Assert.Single(artifact.Program.Definitions);
        var mirCall = Assert.Single(artifact.Source.Program.EntryPoint.Blocks
            .SelectMany(block => block.Instructions).OfType<MirQuantumApply>());

        Assert.NotNull(mirCall.Result);
        Assert.IsType<MirQasmBitType>(helper.ReturnType);
        Assert.Single(MirQasmTestModel.Statements(helper.Body).OfType<MirQasmReturnStatement>());
        var call = Assert.Single(artifact.Program.Expressions()
            .OfType<MirQasmFunctionCallExpression>());
        Assert.Equal(new MirQasmUserFunctionTarget(helper.Id), call.Target);
        Assert.DoesNotContain(MirQasmTestModel.Statements(helper.Body)
            .OfType<MirQasmValueDeclarationStatement>(),
            declaration => declaration.IsOutput);
        Assert.Single(artifact.Program.Statements().OfType<MirQasmMeasurementAssignmentStatement>());
    }

    [Fact]
    public void HelperBindingsCannotAliasCallerBindingsInTheInterpreter()
    {
        var artifact = Compile("""
            operation ReadAndToggle(q: Qubit): int {
                var result: bit = M(q);
                X(q);
                return result;
            }
            operation Read(q: Qubit): int {
                return ReadAndToggle(q);
            }
            operation Main(): int {
                use q = Qubit[1];
                return Read(q[0]) - Read(q[0]);
            }
            """);
        var bindingNames = artifact.Program.Statements()
            .Select(statement => statement switch
            {
                MirQasmValueDeclarationStatement value => value.EmittedName,
                MirQasmArrayDeclarationStatement array => array.EmittedName,
                MirQasmQubitDeclarationStatement qubit => qubit.EmittedName,
                _ => null,
            })
            .OfType<string>()
            .Concat(artifact.Program.Definitions.SelectMany(definition =>
                definition.Parameters.Select(parameter => parameter.EmittedName)))
            .ToArray();

        Assert.Equal(bindingNames.Length, bindingNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, artifact.Program.Expressions().OfType<MirQasmFunctionCallExpression>()
            .Count(call => call.Target is MirQasmUserFunctionTarget));
        Assert.Single(artifact.Program.Statements().OfType<MirQasmMeasurementAssignmentStatement>());
    }

    [Fact]
    public void EntryBranchReturnsReachOneOutputAssignmentWithoutTopLevelReturn()
    {
        var artifact = Compile("""
            operation Main(): bit {
                use q = Qubit[1];
                var result: bit = M(q[0]);
                if (result == 0) {
                    return result;
                }
                X(q[0]);
                return M(q[0]);
            }
            """);

        Assert.Equal(2, artifact.Program.Statements()
            .OfType<MirQasmMeasurementAssignmentStatement>().Count());
        Assert.Empty(MirQasmTestModel.Statements(artifact.Program.EntryBody)
            .OfType<MirQasmReturnStatement>());
        Assert.IsType<MirQasmAssignmentStatement>(artifact.Program.EntryBody[^1]);
    }

    [Fact]
    public void EntryReturnInsideLoopUsesTheExistingReturnPropagation()
    {
        var artifact = Compile("""
            operation Main(): int {
                for i in 0..2 {
                    if (i == 1) {
                        return 7;
                    }
                }
                return 9;
            }
            """);

        Assert.Contains("return_done = 1;", artifact.Text);
        Assert.NotEmpty(artifact.Program.Statements().OfType<MirQasmWhileStatement>());
        Assert.Empty(MirQasmTestModel.Statements(artifact.Program.EntryBody)
            .OfType<MirQasmReturnStatement>());
        Assert.IsType<MirQasmAssignmentStatement>(artifact.Program.EntryBody[^1]);
    }

    [Fact]
    public void EffectfulWhilePredicateStaysInsideTheLoopAndIsEmittedOnce()
    {
        var artifact = Compile("""
            operation ToggleAndRead(q: Qubit): bit {
                X(q);
                return M(q);
            }
            operation Main(): int {
                use q = Qubit[1];
                var count: int = 0;
                while (ToggleAndRead(q[0]) == 1) {
                    count = count + 1;
                }
                return count;
            }
            """);
        var helper = Assert.Single(artifact.Program.Definitions);
        var loop = Assert.Single(MirQasmTestModel.Statements(artifact.Program.EntryBody)
            .OfType<MirQasmWhileStatement>());
        var loopCalls = MirQasmTestModel.Statements(loop.Body)
            .SelectMany(MirQasmTestModel.Expressions)
            .OfType<MirQasmFunctionCallExpression>();
        var call = Assert.Single(loopCalls,
            candidate => candidate.Target is MirQasmUserFunctionTarget);

        Assert.Equal(new MirQasmUserFunctionTarget(helper.Id), call.Target);
        Assert.Single(artifact.Program.Expressions().OfType<MirQasmFunctionCallExpression>(),
            candidate => candidate.Target is MirQasmUserFunctionTarget);
    }

    [Fact]
    public void MeasurementDependentNestedReturnUsesIndependentLoopFlagsWithoutBreaks()
    {
        var artifact = Compile("""
            operation Main(): int {
                use q = Qubit[1];
                for i in 0..2 {
                    while (M(q[0]) == 0) {
                        H(q[0]);
                        if (M(q[0]) == 1) {
                            return 7;
                        }
                    }
                }
                return 9;
            }
            """);
        var loops = artifact.Program.Statements().OfType<MirQasmWhileStatement>().ToArray();
        var loopFlags = loops.Select(loop =>
        {
            var condition = Assert.IsType<MirQasmBinaryExpression>(loop.Condition);
            Assert.Equal(MirQasmBinaryOperator.Equal, condition.Operator);
            Assert.Equal(new MirQasmLiteralExpression("1"), condition.Right);
            return Assert.IsType<MirQasmDeclarationReferenceExpression>(condition.Left);
        }).ToArray();

        Assert.Equal(2, loops.Length);
        Assert.Equal(2, loopFlags.Distinct().Count());
        Assert.Empty(artifact.Program.Statements().OfType<MirQasmBreakStatement>());
        Assert.Equal(2, artifact.Program.Statements()
            .OfType<MirQasmMeasurementAssignmentStatement>().Count());
        AssertLoopEntriesResetTheirFlag(artifact.Program.EntryBody);
    }

    private static void AssertLoopEntriesResetTheirFlag(IReadOnlyList<MirQasmStatement> statements)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            switch (statements[index])
            {
                case MirQasmWhileStatement loop:
                    Assert.True(index > 0);
                    var reset = Assert.IsType<MirQasmAssignmentStatement>(statements[index - 1]);
                    var condition = Assert.IsType<MirQasmBinaryExpression>(loop.Condition);
                    Assert.Equal(condition.Left, reset.Target);
                    Assert.Equal(new MirQasmLiteralExpression("1"), reset.Value);
                    AssertLoopEntriesResetTheirFlag(loop.Body);
                    break;
                case MirQasmIfStatement conditional:
                    AssertLoopEntriesResetTheirFlag(conditional.Then);
                    AssertLoopEntriesResetTheirFlag(conditional.Else);
                    break;
            }
        }
    }

    [Fact]
    public void VoidEarlyReturnLeavesTheLoopBeforeTheCompletionMarker()
    {
        var artifact = Compile("""
            operation Main() {
                use q = Qubit[1];
                for i in 0..2 {
                    if (i == 1) {
                        return;
                    }
                    X(q[0]);
                }
                X(q[0]);
            }
            """);

        Assert.Null(artifact.Source.Program.EntryPoint.ReturnType);
        Assert.Contains("return_done = 1;", artifact.Text);
        Assert.Empty(MirQasmTestModel.Statements(artifact.Program.EntryBody)
            .OfType<MirQasmReturnStatement>());
        var completed = Assert.IsType<MirQasmAssignmentStatement>(artifact.Program.EntryBody[^1]);
        Assert.Equal(new MirQasmLiteralExpression("1"), completed.Value);
    }

    [Fact]
    public void OutputMetadataUsesTheAllocatedNameRatherThanAReservedSpelling()
    {
        var artifact = Compile("""
            operation __qora_result() { }
            operation Main(): int {
                __qora_result();
                return 1;
            }
            """);
        var output = Assert.IsType<MirQasmValueDeclarationStatement>(artifact.Program.EntryOutput);

        Assert.NotEqual("__qora_result", output.EmittedName);
        Assert.Contains($"output int {output.EmittedName};", artifact.Text);
        Assert.Contains($"{output.EmittedName} = ", artifact.Text);
    }

    [Fact]
    public void OutputDeclarationRejectsConstAndInitializer()
    {
        Assert.Throws<ArgumentException>(() => new MirQasmValueDeclarationStatement(
            new MirQasmDeclarationId(0), "result", new MirQasmBitType(),
            isConst: true, isOutput: true));
        Assert.Throws<ArgumentException>(() => new MirQasmValueDeclarationStatement(
            new MirQasmDeclarationId(0), "result", new MirQasmBitType(),
            new MirQasmLiteralExpression("0"), isOutput: true));
    }

    [Fact]
    public void TargetRejectsOutputDeclarationsInDefinitionsOrNestedBlocks()
    {
        var output = new MirQasmValueDeclarationStatement(
            new MirQasmDeclarationId(0), "result", new MirQasmBitType(), isOutput: true);
        var definition = new MirQasmCallableDefinition(
            new MirQasmCallableId(0), "helper", Array.Empty<MirQasmParameter>(),
            returnType: null, new[] { output });

        Assert.Throws<ArgumentException>(() => new MirOpenQasmTargetProgram(
            Array.Empty<MirQasmStatement>(), new[] { definition }));
        Assert.Throws<ArgumentException>(() => new MirOpenQasmTargetProgram(
            new[] { new MirQasmIfStatement(new MirQasmLiteralExpression("true"), new[] { output }) },
            Array.Empty<MirQasmCallableDefinition>()));
    }

    [Fact]
    public void TargetRejectsAmbiguousEntryOutputMetadata()
    {
        var first = new MirQasmValueDeclarationStatement(
            new MirQasmDeclarationId(0), "first", new MirQasmBitType(), isOutput: true);
        var second = new MirQasmValueDeclarationStatement(
            new MirQasmDeclarationId(1), "second", new MirQasmBitType(), isOutput: true);

        Assert.Throws<ArgumentException>(() => new MirOpenQasmTargetProgram(
            new[] { first, second }, Array.Empty<MirQasmCallableDefinition>()));
    }

    private static OpenQasmArtifact Compile(string source) => MirQasmTestModel.Compile(source);
}
