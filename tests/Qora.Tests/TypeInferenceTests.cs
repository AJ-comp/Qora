using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Untyped declarations take the initializer's semantic type. Assertions use HIR symbols, MIR-owned
/// target types, and target callable IDs rather than assuming that SSA temporaries preserve source names.
/// </summary>
public class TypeInferenceTests
{
    [Fact]
    public void UntypedVarFromBitRemainsBitThroughTheTarget()
    {
        var compilation = AssertVariableType(
            "operation Main(){ use q=Qubit[1]; var mb: bit = M(q[0]); var res = mb; if(res==1){ X(q[0]); } }",
            "res",
            QType.Bit);

        Assert.Contains(
            TargetValueTypes(compilation),
            type => type is MirQasmBitType { Width: 1 });
    }

    [Fact]
    public void UntypedVarFromIntRemainsIntThroughTheTarget()
    {
        var compilation = AssertVariableType(
            "operation Main(){ use q=Qubit[1]; var cnt: int = 5; var got = cnt; Rx(got, q[0]); }",
            "got",
            QType.Int);

        AssertTargetScalar(compilation, MirQasmScalarKind.Int);
    }

    [Fact]
    public void UntypedVarFromRealRemainsFloatThroughTheTarget()
    {
        var compilation = AssertVariableType(
            "operation Main(){ use q=Qubit[1]; var ang = pi/2; Rx(ang, q[0]); }",
            "ang",
            QType.Float);

        AssertTargetScalar(compilation, MirQasmScalarKind.Float);
    }

    [Fact]
    public void FloatPropagatesThroughAnotherUntypedVar()
    {
        var compilation = AssertVariableType(
            "operation Main(){ use q=Qubit[1]; var a = pi; var b = a / 2; Rx(b, q[0]); }",
            "b",
            QType.Float);

        AssertTargetScalar(compilation, MirQasmScalarKind.Float);
    }

    [Fact]
    public void UntypedVarPreservesTheAngleTypeOfAReferencedVariable()
    {
        var compilation = AssertVariableType(
            "operation Main(){ use q=Qubit[1]; var sourceValue: angle = pi; var copy = sourceValue; Rx(copy, q[0]); }",
            "copy",
            QType.Angle);

        AssertTargetScalar(compilation, MirQasmScalarKind.Angle);
    }

    [Fact]
    public void UntypedBooleanLiteralIsBit()
    {
        var compilation = AssertVariableType(
            "operation Main(){ use q=Qubit[1]; var truth = true; if (truth) { X(q[0]); } }",
            "truth",
            QType.Bit);

        Assert.Contains(
            TargetValueTypes(compilation),
            type => type is MirQasmBitType { Width: 1 });
    }

    [Theory]
    [InlineData(
        "function giveInt(): int { return 2; }\noperation Main(){ var result = giveInt(); }",
        QType.Int,
        MirQasmScalarKind.Int)]
    [InlineData(
        "function giveFloat(): float { return 0.5; }\noperation Main(){ var result = giveFloat(); }",
        QType.Float,
        MirQasmScalarKind.Float)]
    [InlineData(
        "function giveAngle(): angle { return pi; }\noperation Main(){ var result = giveAngle(); }",
        QType.Angle,
        MirQasmScalarKind.Angle)]
    public void UntypedVarTakesAFunctionReturnType(
        string source,
        QType expectedSourceType,
        MirQasmScalarKind expectedTargetType)
    {
        var compilation = AssertVariableType(
            source,
            "result",
            expectedSourceType);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var call = Assert.Single(
            artifact.Program.Expressions()
                .OfType<MirQasmFunctionCallExpression>(),
            expression =>
                expression.Target is MirQasmUserFunctionTarget);
        var target = artifact.Program.Resolve(
            Assert.IsType<MirQasmUserFunctionTarget>(call.Target));

        Assert.Equal(
            expectedTargetType,
            Assert.IsType<MirQasmScalarType>(target.ReturnType).Kind);
    }

    [Fact]
    public void UntypedVarTakesABitFunctionReturnType()
    {
        var compilation = AssertVariableType(
            "function giveBit(): bit { return 1; }\noperation Main(){ var result = giveBit(); }",
            "result",
            QType.Bit);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var function = Assert.Single(
            artifact.Program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function));

        Assert.IsType<MirQasmBitType>(function.ReturnType);
    }

    [Fact]
    public void FloatFunctionResultPropagatesThroughArithmetic()
    {
        var compilation = AssertVariableType(
            "function half(): float { return 0.5; }\n" +
            "operation Main(){ use q=Qubit[1]; var result = half() + 1; Rx(result, q[0]); }",
            "result",
            QType.Float);

        AssertTargetScalar(compilation, MirQasmScalarKind.Float);
    }

    [Fact]
    public void BuiltinFunctionReturnTypeUsesTheRegistryAndTargetCastWidth()
    {
        var compilation = AssertVariableType(
            "operation Main(){ var bits: bit[] = new bit[2]; var result = AsInt(bits); }",
            "result",
            QType.Int);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);

        Assert.Contains(
            artifact.Program.Expressions().OfType<MirQasmUnsignedCastExpression>(),
            cast => cast.Width == 2);
        AssertTargetScalar(compilation, MirQasmScalarKind.Int);
    }

    [Fact]
    public void InferredTypeSurvivesNestedFunctionLowering()
    {
        var compilation = AssertVariableType(
            "function half(): float { return 0.5; }\n" +
            "function wrapper(): float { var result = half(); return result; }\n" +
            "operation Main(){ var answer: float = wrapper(); }",
            "result",
            QType.Float);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var functions = artifact.Program.Definitions
            .Where(definition => definition.Kind == MirQasmCallableKind.Function)
            .ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(
            functions,
            function =>
                Assert.Equal(
                    MirQasmScalarKind.Float,
                    Assert.IsType<MirQasmScalarType>(function.ReturnType).Kind));
    }

    [Fact]
    public void InferredTypeSurvivesSyntheticArrayStorage()
    {
        var compilation = AssertVariableType(
            "function half(): float { var values: int[] = [1, 2]; return 0.5; }\n" +
            "operation Main(){ var result = half(); }",
            "result",
            QType.Float);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var function = Assert.Single(
            artifact.Program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function));

        Assert.Equal(
            MirQasmScalarKind.Float,
            Assert.IsType<MirQasmScalarType>(function.ReturnType).Kind);
        Assert.Contains(
            function.Parameters,
            parameter =>
                parameter.Type is MirQasmArrayType
                && parameter.Access == MirQasmParameterAccess.Mutable);
    }

    [Fact]
    public void CallableLookupIsNotHiddenByALocalValueWithTheSameName()
    {
        var compilation = Compiler.Compile(
            "function value(): float { return 0.5; }\n" +
            "operation Main(){ var value: int = 1; var result = value(); }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var result = analyzed.Program.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QDecl>()
            .Single(declaration => declaration.Name == "result");
        var graph = Assert.IsType<HirScopeGraph>(analyzed.Model.ScopeGraph);
        var main = analyzed.Program.Operations.Single(
            operation => operation.Name == "Main");
        var mainScope = Assert.IsType<Scope>(
            graph.FindCallableScope(main.Id));
        var localValue = Assert.IsType<Symbol>(
            mainScope.Lookup("value"));
        var callable = Assert.IsType<Symbol>(
            graph.LookupCallable("value"));

        Assert.Equal(QType.Float, analyzed.Model.FindSymbol(result.Id)!.Type);
        Assert.Equal(SymbolKind.Var, localValue.Kind);
        Assert.Equal(SymbolKind.Operation, callable.Kind);
        Assert.NotEqual(localValue.Id, callable.Id);
    }

    [Fact]
    public void TargetFunctionDefinitionCarriesValidatedReturnType()
    {
        var compilation = QoraCompiler.Compile(
            "function half(): float { return 0.5; }\n" +
            "operation Main(){ var result = half(); }");
        AssertSucceeded(compilation);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var function = Assert.Single(
            artifact.Program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function));

        Assert.Equal(
            MirQasmScalarKind.Float,
            Assert.IsType<MirQasmScalarType>(function.ReturnType).Kind);
    }

    [Fact]
    public void InferredFunctionReturnTypeIsRecordedOnTheDeclarationSymbol()
    {
        var compilation = QoraCompiler.Compile(
            "function half(): float { return 0.5; }\n" +
            "operation Main(){ var result = half(); }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var declaration = analyzed.Program.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QDecl>()
            .Single(item => item.Name == "result");
        var symbol = analyzed.Model.FindSymbol(declaration.Id);
        var function = analyzed.Program.Operations.Single(
            operation => operation.Name == "half");
        var graph = Assert.IsType<HirScopeGraph>(analyzed.Model.ScopeGraph);
        var callable = graph.LookupCallable("half");

        Assert.NotNull(symbol);
        Assert.Equal(QType.Float, symbol!.Type);
        Assert.Equal(function.Id, callable!.DeclarationNodeId);
        Assert.Null(callable.Type);
    }

    private static Compilation AssertVariableType(
        string source,
        string variable,
        QType expected)
    {
        var compilation = Compiler.Compile(source);
        AssertSucceeded(compilation);
        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var declaration = analyzed.Program.Operations
            .SelectMany(operation => DescendantStatements(operation.Body))
            .OfType<QDecl>()
            .Single(item => item.Name == variable);

        Assert.Equal(expected, analyzed.Model.FindSymbol(declaration.Id)!.Type);
        Assert.NotNull(compilation.Mir);
        Assert.NotNull(compilation.Targets.OpenQasm);
        return compilation;
    }

    private static IEnumerable<QStmt> DescendantStatements(
        IEnumerable<QStmt> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            switch (statement)
            {
                case QIf branch:
                    foreach (var nested in DescendantStatements(branch.Then))
                        yield return nested;
                    foreach (var nested in DescendantStatements(branch.Else))
                        yield return nested;
                    break;
                case QFor loop:
                    foreach (var nested in DescendantStatements(loop.Body))
                        yield return nested;
                    break;
                case QWhile loop:
                    foreach (var nested in DescendantStatements(loop.Body))
                        yield return nested;
                    break;
                case QRepeat loop:
                    foreach (var nested in DescendantStatements(loop.Body))
                        yield return nested;
                    break;
            }
        }
    }

    private static IEnumerable<MirQasmType> TargetValueTypes(
        Compilation compilation) =>
        Assert.IsType<OpenQasmArtifact>(compilation.Targets.OpenQasm)
            .Program
            .Statements()
            .OfType<MirQasmValueDeclarationStatement>()
            .Select(declaration => declaration.Type);

    private static void AssertTargetScalar(
        Compilation compilation,
        MirQasmScalarKind expected) =>
        Assert.Contains(
            TargetValueTypes(compilation),
            type =>
                type is MirQasmScalarType scalar
                && scalar.Kind == expected);

    private static void AssertSucceeded(Compilation compilation) =>
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
}
