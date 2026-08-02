using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class OpenQasmTargetInvariantTests
{
    [Fact]
    public void BackendConsumesOneExactMirSnapshot()
    {
        var run = Assert.Single(
            typeof(QasmBackend)
                .GetMethods(
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic),
            method => method.Name == nameof(QasmBackend.Run));
        var parameters = run.GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(MirSnapshot), parameters[0].ParameterType);
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(HirProgram));
    }

    [Fact]
    public void ArtifactOwnsTheExactMirSnapshotAndDerivesTextFromItsTargetProgram()
    {
        var compilation = QoraCompiler.Compile("operation Main() { }");
        Assert.True(compilation.Succeeded);
        var source = Assert.IsType<MirSnapshot>(compilation.Mir);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);

        Assert.Same(source, artifact.Source);
        Assert.Equal(MirQasmEmitter.Emit(artifact.Program), artifact.Text);

        var constructor = Assert.Single(
            typeof(OpenQasmArtifact).GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic));
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(string));
    }

    [Fact]
    public void TargetProgramDoesNotCarryASecondHirOrMirSourceOfTruth()
    {
        var sourceTypes = typeof(MirOpenQasmTargetProgram)
            .GetProperties(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.PropertyType)
            .Concat(
                typeof(MirOpenQasmTargetProgram)
                    .GetFields(
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)
                    .Select(field => field.FieldType))
            .ToArray();

        Assert.DoesNotContain(typeof(HirProgram), sourceTypes);
        Assert.DoesNotContain(typeof(MirProgram), sourceTypes);
        Assert.DoesNotContain(typeof(MirSnapshot), sourceTypes);
    }

    [Fact]
    public void TargetRejectsAUserCallToAnUndefinedTargetCallableId()
    {
        var dangling = new MirQasmQuantumApplyStatement(
            new MirQasmUserQuantumTarget(new MirQasmCallableId(7)),
            Array.Empty<MirQasmExpression>(),
            Array.Empty<MirQasmExpression>());

        var error = Assert.Throws<ArgumentException>(
            () => new MirOpenQasmTargetProgram(
                new[] { dangling },
                Array.Empty<MirQasmCallableDefinition>()));

        Assert.Contains(new MirQasmCallableId(7).ToString(), error.Message);
    }

    [Fact]
    public void TargetRejectsAUserOperationCallWithTheWrongArity()
    {
        var workerId = new MirQasmCallableId(1);
        var call = new MirQasmQuantumApplyStatement(
            new MirQasmUserQuantumTarget(workerId),
            Array.Empty<MirQasmExpression>(),
            Array.Empty<MirQasmExpression>());
        var worker = new MirQasmCallableDefinition(
            workerId,
            "Worker",
            new[]
            {
                new MirQasmParameter(
                    new MirQasmParameterId(0),
                    "value",
                    new MirQasmScalarType(MirQasmScalarKind.Int)),
            },
            returnType: null,
            Array.Empty<MirQasmStatement>());

        var error = Assert.Throws<ArgumentException>(
            () => new MirOpenQasmTargetProgram(new[] { call }, new[] { worker }));

        Assert.Contains("0 operand(s), expected 1", error.Message);
    }

    [Fact]
    public void TargetRejectsAUserFunctionCallWithTheWrongArity()
    {
        var functionId = new MirQasmCallableId(1);
        var intType = new MirQasmScalarType(MirQasmScalarKind.Int);
        var call = new MirQasmFunctionCallExpression(
            new MirQasmUserFunctionTarget(functionId),
            Array.Empty<MirQasmExpression>());
        var entryBody = new[]
        {
            new MirQasmValueDeclarationStatement(
                new MirQasmDeclarationId(0),
                "result",
                intType,
                call),
        };
        var function = new MirQasmCallableDefinition(
            functionId,
            "Identity",
            new[]
            {
                new MirQasmParameter(
                    new MirQasmParameterId(0),
                    "value",
                    intType),
            },
            intType,
            new[]
            {
                new MirQasmReturnStatement(
                    new MirQasmParameterReferenceExpression(
                        new MirQasmParameterId(0))),
            });

        var error = Assert.Throws<ArgumentException>(
            () => new MirOpenQasmTargetProgram(entryBody, new[] { function }));

        Assert.Contains("0 argument(s), expected 1", error.Message);
    }
}
