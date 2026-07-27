using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class OpenQasmTargetInvariantTests
{
    [Fact]
    public void BackendConsumesOnlyOneExactHirSemanticContext()
    {
        var run = Assert.Single(
            typeof(QasmBackend)
                .GetMethods(
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic),
            method => method.Name == nameof(QasmBackend.Run));
        var parameters = run.GetParameters();

        Assert.Equal(typeof(HirSemanticContext), parameters[0].ParameterType);
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(QProgram));
    }

    [Fact]
    public void ArtifactTextIsDerivedFromItsAuthoritativeTargetProgram()
    {
        var compilation = QoraCompiler.Compile("operation Main() { }");
        Assert.True(compilation.Succeeded);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);

        Assert.Equal(QasmEmitter.Emit(artifact.Program), artifact.Text);
        var constructor = Assert.Single(
            typeof(OpenQasmArtifact).GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic));
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(string));
    }

    [Fact]
    public void BackendResultCannotBeForgedOutsideItsOwningBackend()
    {
        var factories = typeof(QasmBackend.Result)
            .GetMethods(
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(QasmBackend.Result))
            .ToArray();

        Assert.NotEmpty(factories);
        Assert.All(factories, factory => Assert.True(factory.IsPrivate));
    }

    [Fact]
    public void TargetRejectsUserCallsWithoutExactDeclarationIds()
    {
        var worker = new QOperation(
            "Worker",
            Array.Empty<QParam>(),
            Array.Empty<QStmt>());
        var unboundCall = new QGate(
            Array.Empty<string>(),
            worker.Name,
            Array.Empty<QArg>());
        var main = new QOperation(
            "Main",
            Array.Empty<QParam>(),
            new QStmt[] { unboundCall });

        var error = Assert.Throws<ArgumentException>(
            () => OpenQasmTargetProgram.CreateExplicitForTesting(
                new QProgram(new[] { worker, main })));
        Assert.Contains("has no CalleeOpId", error.Message);
    }

    [Fact]
    public void TargetRejectsCallNamesThatDisagreeWithTheirDeclarationIds()
    {
        var worker = new QOperation(
            "Worker",
            Array.Empty<QParam>(),
            Array.Empty<QStmt>());
        var mismatchedCall = new QGate(
            Array.Empty<string>(),
            "Other",
            Array.Empty<QArg>())
        {
            CalleeOpId = worker.Id,
        };
        var main = new QOperation(
            "Main",
            Array.Empty<QParam>(),
            new QStmt[] { mismatchedCall });

        var error = Assert.Throws<ArgumentException>(
            () => OpenQasmTargetProgram.CreateExplicitForTesting(
                new QProgram(new[] { worker, main })));
        Assert.Contains("disagrees with CalleeOpId", error.Message);
    }

    [Fact]
    public void TargetRejectsMissingAndExtraSymbolEntries()
    {
        var (program, operation, declaration) = ProgramWithDeclaration();
        var types = OpenQasmTypeEnvironment.BuildExplicitForTesting(program);

        var missing = new OpenQasmSymbolMap(
            new[]
            {
                KeyValuePair.Create(operation.Id, operation.Name),
            });
        Assert.Throws<InvalidOperationException>(
            () => Create(program, missing, types));

        var extra = new OpenQasmSymbolMap(
            new[]
            {
                KeyValuePair.Create(operation.Id, operation.Name),
                KeyValuePair.Create(declaration.Id, declaration.Name),
                KeyValuePair.Create(QNodeIds.Next(), "ghost"),
            });
        var error = Assert.Throws<InvalidOperationException>(
            () => Create(program, extra, types));
        Assert.Contains("absent from the final target tree", error.Message);
    }

    [Fact]
    public void TargetRejectsMissingAndExtraClassicalTypeEntries()
    {
        var (program, operation, declaration) = ProgramWithDeclaration();
        var symbols = new OpenQasmSymbolMap(
            new[]
            {
                KeyValuePair.Create(operation.Id, operation.Name),
                KeyValuePair.Create(declaration.Id, declaration.Name),
            });

        var noDeclaration = program with
        {
            Operations = new[]
            {
                operation with { Body = Array.Empty<QStmt>() },
            },
        };
        var missing = OpenQasmTypeEnvironment.BuildExplicitForTesting(noDeclaration);
        Assert.Throws<InvalidOperationException>(
            () => Create(program, symbols, missing));

        var ghost = new QDecl(
            false,
            QType.Int,
            "ghost",
            new QText(new QNumLit(0)));
        var withExtra = program with
        {
            Operations = new[]
            {
                operation with
                {
                    Body = new QStmt[] { declaration, ghost },
                },
            },
        };
        var extra = OpenQasmTypeEnvironment.BuildExplicitForTesting(withExtra);
        var error = Assert.Throws<InvalidOperationException>(
            () => Create(program, symbols, extra));
        Assert.Contains("absent from the final target tree", error.Message);
    }

    private static (
        QProgram Program,
        QOperation Operation,
        QDecl Declaration) ProgramWithDeclaration()
    {
        var declaration = new QDecl(
            false,
            QType.Int,
            "value",
            new QText(new QNumLit(1)));
        var operation = new QOperation(
            "Main",
            Array.Empty<QParam>(),
            new QStmt[] { declaration });
        return (
            new QProgram(new[] { operation }),
            operation,
            declaration);
    }

    private static OpenQasmTargetProgram Create(
        QProgram program,
        OpenQasmSymbolMap symbols,
        OpenQasmTypeEnvironment types) =>
        new(
            program,
            symbols,
            types,
            OpenQasmTargetFacts.Empty,
            Array.Empty<string>());
}
