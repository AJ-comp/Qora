using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// HIR node IDs remain stable within one HIR lineage, semantic symbols belong to an exact HIR snapshot,
/// and final emitted identities belong exclusively to the MIR-derived target model.
/// </summary>
public class NodeIdentityTests
{
    [Fact]
    public void EqualShapeOccurrencesReceiveDistinctIdsAndPublishingSealsConstruction()
    {
        var hir = new HirTestFactory();
        var first = hir.Apply("X", hir.Index("q", 0));
        var second = hir.Apply("X", hir.Index("q", 0));
        var main = hir.Callable(
            "Main",
            body: new HirStatement[] { first, second });
        var program = hir.PublishProgram(new[] { main });
        var builder = hir.CreatePipelineBuilder();
        var snapshot = builder.PublishLowered(program);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(
            snapshot.Structure.NodeIds.Count,
            snapshot.Structure.NodeIds.Distinct().Count());
        Assert.Throws<InvalidOperationException>(
            () => hir.Integer(1));
    }

    [Fact]
    public void SnapshotRejectsOneOccurrenceAttachedToTwoParents()
    {
        var hir = new HirTestFactory();
        var shared = hir.Variable(
            "value",
            hir.Integer(1),
            QType.Int);
        var branch = hir.If(
            hir.Name("true"),
            new HirStatement[] { shared },
            new HirStatement[] { shared });
        var main = hir.Callable(
            "Main",
            body: new HirStatement[] { branch });
        var program = hir.PublishProgram(new[] { main });
        var builder = hir.CreatePipelineBuilder();

        var error = Assert.Throws<ArgumentException>(
            () => builder.PublishLowered(program));
        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void PipelineWithSpecializationsHasUniqueIds()
    {
        var compilation = QoraCompiler.Compile(
            "operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; use b=Qubit[3]; Flip(a); Flip(b); }");

        AssertSucceeded(compilation);
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code == "QINTERNAL");
    }

    [Fact]
    public void SemanticsFindSymbolReturnsValidationTimeTypeById()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main(){ use q=Qubit[1]; const x: int = 1; H(q[0]); }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var declaration = analyzed.Program.Callables
            .SelectMany(operation => operation.Body)
            .OfType<HirVariableDeclarationStatement>()
            .Single(item => item.Name == "x");
        var symbol = analyzed.Model.FindSymbol(declaration.Id);

        Assert.NotNull(symbol);
        Assert.Equal(QType.Int, symbol!.Type);
        Assert.True(symbol.IsConst);
    }

    [Fact]
    public void SymbolFormatReadsTheExactAnalyzedSnapshot()
    {
        var compilation = QoraCompiler.Compile(
            "operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; const x: int = 1; Flip(a); }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var formatted = SymbolTableBuilder.Format(
            analyzed.Program,
            analyzed.Model);

        Assert.Contains("Main: callable", formatted);
        Assert.Contains("x: const int = 1", formatted);
    }

    [Fact]
    public void TargetAstOwnsEmittedNamesAndSourceNameStaysUserSpelling()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main(){ use q=Qubit[1]; const x: int = 1; Rx(x, q[0]); }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var declaration = analyzed.Program.Callables
            .SelectMany(operation => operation.Body)
            .OfType<HirVariableDeclarationStatement>()
            .Single(item => item.Name == "x");
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var targetDeclarations = TargetStatements(
                artifact.Program.EntryPoint.Body)
            .OfType<MirQasmValueDeclarationStatement>()
            .ToArray();
        var targetQubit = Assert.Single(
            TargetStatements(artifact.Program.EntryPoint.Body)
                .OfType<MirQasmQubitDeclarationStatement>());
        var targetNames = targetDeclarations
            .Select(item => item.EmittedName)
            .Append(targetQubit.EmittedName)
            .ToArray();

        Assert.Equal("x", analyzed.Model.FindSymbol(declaration.Id)!.SourceName);
        Assert.Equal(
            targetDeclarations.Length,
            targetDeclarations
                .Select(item => item.Declaration)
                .Distinct()
                .Count());
        Assert.Equal(
            targetNames.Length,
            targetNames.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(
            artifact.Program.Definitions.Select(item => item.EmittedName),
            targetNames.Contains);
        Assert.All(
            targetDeclarations,
            item => Assert.False(string.IsNullOrWhiteSpace(item.EmittedName)));
    }

    [Fact]
    public void SiblingSameNameDeclarationsGetDistinctTargetNames()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main(){ use q=Qubit[2]; " +
            "for i in 0..1 { H(q[i]); } for i in 0..1 { X(q[i]); } }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var loops = analyzed.Program.Callables.Single().Body
            .OfType<HirForStatement>()
            .ToArray();
        var mir = Assert.IsType<MirSnapshot>(compilation.Mir);
        var loweredValues = loops
            .Select(
                loop =>
                {
                    var symbol = Assert.IsType<Symbol>(
                        analyzed.Model.FindSymbol(loop.Id));
                    var reference = new HirSymbolRef(analyzed.Id, symbol.Id);
                    return mir.Links.ValuesBySymbol[reference];
                })
            .ToArray();

        Assert.Equal(2, loweredValues.Length);
        Assert.NotEmpty(loweredValues[0]);
        Assert.NotEmpty(loweredValues[1]);
        Assert.Empty(loweredValues[0].Intersect(loweredValues[1]));

        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var targetDeclarations = TargetStatements(
                artifact.Program.EntryPoint.Body)
            .Select(
                statement => statement switch
                {
                    MirQasmValueDeclarationStatement value =>
                        (
                            Declaration: value.Declaration,
                            EmittedName: value.EmittedName),
                    MirQasmArrayDeclarationStatement array =>
                        (
                            Declaration: array.Declaration,
                            EmittedName: array.EmittedName),
                    MirQasmQubitDeclarationStatement qubit =>
                        (
                            Declaration: qubit.Declaration,
                            EmittedName: qubit.EmittedName),
                    _ => ((MirQasmDeclarationId Declaration, string EmittedName)?)null,
                })
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToArray();

        Assert.Equal(
            targetDeclarations.Length,
            targetDeclarations.Select(item => item.Declaration).Distinct().Count());
        Assert.Equal(
            targetDeclarations.Length,
            targetDeclarations
                .Select(item => item.Item2)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void ParametersAndCallablesCarryTargetOwnedNames()
    {
        var compilation = QoraCompiler.Compile(
            "operation Foo(x: Qubit[]){ H(x[0]); }\n" +
            "operation Main(){ use q=Qubit[1]; Foo(q); }");
        AssertSucceeded(compilation);

        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var foo = Assert.Single(
            artifact.Program.Definitions.Where(
                definition =>
                    definition.EmittedName.StartsWith(
                        "Foo",
                        StringComparison.Ordinal)));

        var parameter = Assert.Single(foo.Parameters);
        var call = Assert.Single(
            TargetStatements(artifact.Program.EntryPoint.Body)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var target = Assert.IsType<MirQasmUserQuantumTarget>(call.Target);

        Assert.Equal(MirQasmCallableKind.Operation, foo.Kind);
        Assert.Null(foo.ReturnType);
        Assert.Equal(foo.Id, target.Callable);
        Assert.IsType<MirQasmQubitType>(parameter.Type);
        Assert.False(string.IsNullOrWhiteSpace(parameter.EmittedName));
        Assert.NotEqual(foo.EmittedName, artifact.Program.EntryPoint.EmittedName);
    }

    [Fact]
    public void TargetFlattensNamespacedOperationName()
    {
        var compilation = QoraCompiler.Compile("""
            namespace MyLib {
                operation Bell(q: Qubit[]) {
                    H(q[0]);
                }
            }
            operation Main() {
                use a = Qubit[1];
                MyLib.Bell(a);
            }
            """);
        AssertSucceeded(compilation);

        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var bell = Assert.Single(artifact.Program.Definitions);
        var call = Assert.Single(
            TargetStatements(artifact.Program.EntryPoint.Body)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var target = Assert.IsType<MirQasmUserQuantumTarget>(call.Target);

        Assert.Contains('_', bell.EmittedName);
        Assert.DoesNotContain(".", bell.EmittedName);
        Assert.NotEqual(bell.EmittedName, artifact.Program.EntryPoint.EmittedName);
        Assert.Equal(bell.Id, target.Callable);
    }

    [Fact]
    public void OperationIsASymbolWithOneUsePerCallSite()
    {
        var compilation = QoraCompiler.Compile(
            "operation Foo(q: Qubit[]){ H(q[0]); }\n" +
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Foo(a); Foo(b); }");
        AssertSucceeded(compilation);

        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var foo = analyzed.Program.Callables.Single(
            operation => operation.DisplayName == "Foo");
        var main = analyzed.Program.Callables.Single(
            operation => operation.Name == "Main");
        var fooSymbol = analyzed.Model.FindSymbol(foo.Id);

        Assert.NotNull(fooSymbol);
        Assert.Equal(SymbolKind.Callable, fooSymbol!.Kind);
        Assert.Equal(foo.Name, fooSymbol.SourceName);
        Assert.Equal(2, fooSymbol.Uses.Count);
        Assert.Empty(analyzed.Model.FindSymbol(main.Id)!.Uses);
    }

    private static IEnumerable<MirQasmStatement> TargetStatements(
        IEnumerable<MirQasmStatement> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            switch (statement)
            {
                case MirQasmIfStatement branch:
                    foreach (var nested in TargetStatements(branch.Then))
                        yield return nested;
                    foreach (var nested in TargetStatements(branch.Else))
                        yield return nested;
                    break;
                case MirQasmWhileStatement loop:
                    foreach (var nested in TargetStatements(loop.Body))
                        yield return nested;
                    break;
            }
        }
    }

    private static void AssertSucceeded(Compilation compilation) =>
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));
}
