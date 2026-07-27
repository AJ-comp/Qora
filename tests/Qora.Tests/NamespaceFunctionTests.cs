using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Namespace resolution applies to expression-position calls just as it does to statement calls:
/// the parser accepts qualified function names, the resolver writes both the canonical name and stable
/// callee Id, and program, namespace, callable, and lexical scopes form one <see cref="HirScopeGraph"/>.
/// These tests pin that contract through resolution, validation, monomorphization, and emission.
/// </summary>
public class NamespaceFunctionTests
{
    private static Compilation Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(
                " | ",
                result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return result;
    }

    private static QCallNode SoleExpressionCall(QOperation operation) =>
        operation.Body
            .SelectMany(QNodes.ExpressionSites)
            .SelectMany(QNodes.CallsIn)
            .Single();

    [Fact]
    public void SameNamespaceFunctionCallBindsItsCanonicalNameAndCalleeId()
    {
        var result = Compile("""
            namespace L {
                function two(): int { return 2; }
                operation Work() { var n: int = two(); }
            }
            operation Main() { L.Work(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "L.two");
        var work = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "L.Work");
        var call = SoleExpressionCall(work);

        Assert.Equal("L.two", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void QualifiedFunctionCallFromGlobalScopeParsesResolvesAndEmitsOneMatchingName()
    {
        var result = Compile("""
            namespace L {
                function two(): int { return 2; }
            }
            operation Main() { var n: int = L.two(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "L.two");
        var main = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "Main");
        var call = SoleExpressionCall(main);

        Assert.Equal("L.two", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
        var target = result.Targets.OpenQasm!.Program;
        var targetCall = Assert.Single(
            target.Expressions()
                .OfType<MirQasmFunctionCallExpression>(),
            expression =>
                expression.Target is MirQasmUserFunctionTarget);
        var targetFunction = target.Resolve(
            Assert.IsType<MirQasmUserFunctionTarget>(targetCall.Target));

        Assert.Contains("L_two", targetFunction.EmittedName);
        Assert.Equal(
            MirQasmScalarKind.Int,
            Assert.IsType<MirQasmScalarType>(
                targetFunction.ReturnType).Kind);
    }

    [Fact]
    public void OpenedNamespaceMakesItsFunctionAvailableToAnExpressionCall()
    {
        var result = Compile("""
            namespace L {
                function two(): int { return 2; }
            }
            namespace App {
                open L;
                operation Work() { var n: int = two(); }
            }
            operation Main() { App.Work(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "L.two");
        var work = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "App.Work");
        var call = SoleExpressionCall(work);

        Assert.Equal("L.two", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void TwoOpenedFunctionsWithTheSameNameReportOnlyAmbiguity()
    {
        var result = Compiler.Compile("""
            namespace A {
                function two(): int { return 1; }
            }
            namespace B {
                function two(): int { return 2; }
            }
            namespace App {
                open A;
                open B;
                operation Work() { var n: int = two(); }
            }
            operation Main() { App.Work(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "QSEM018" }, result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => error.Code));
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM007");
    }

    [Fact]
    public void QualifiedOperationUsedAsAValueIsKnownButRejectedAsVoid()
    {
        var result = Compiler.Compile("""
            namespace L {
                operation Work() { }
            }
            operation Main() { var n: int = L.Work(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM005");
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM019");
    }

    [Fact]
    public void QualifiedIntrinsicGateInAnExpressionIsKnownButRejectedAsVoid()
    {
        var result = Compiler.Compile("""
            operation Main() { var n: int = Qora.Intrinsic.H(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "QSEM005" }, result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => error.Code));
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM019");
    }

    [Fact]
    public void NamespacedCallablesAreDeclaredInTheirNamespaceScope()
    {
        var result = Compile("""
            namespace L {
                function two(): int { return 2; }
                operation Work() { var n: int = two(); }
            }
            operation Main() { L.Work(); }
            """);

        var work = result.Hir.Specialized!.Program!.Operations.Single(operation => operation.Name == "L.Work");
        var function = result.Hir.Specialized!.Program.Operations.Single(operation => operation.Name == "L.two");
        var graph = Assert.IsType<HirScopeGraph>(result.Hir.SpecializedValidation!.Model.ScopeGraph);
        var namespaceScope = Assert.IsType<Scope>(graph.FindNamespaceScope("L"));
        var namespaceSymbol = Assert.IsType<Symbol>(graph.FindNamespaceSymbol("L"));
        var workSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(work.Id));
        var functionSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(function.Id));
        var operationScope = Assert.IsType<Scope>(result.Hir.SpecializedValidation!.Model.FindRootScope(work.Id));

        Assert.Equal(SymbolKind.Namespace, namespaceSymbol.Kind);
        Assert.Equal(graph.RootScope.Id, namespaceSymbol.DeclaringScopeId);
        Assert.Equal(graph.RootScope.Id, namespaceScope.ParentScopeId);
        Assert.Equal(namespaceSymbol.Id, namespaceScope.DeclaringSymbolId);
        Assert.Equal(namespaceScope.Id, workSymbol.DeclaringScopeId);
        Assert.Equal(namespaceScope.Id, functionSymbol.DeclaringScopeId);
        Assert.Same(functionSymbol,
            graph.LookupMember(namespaceScope.Id, "two", SymbolKind.Operation));
        Assert.Same(workSymbol,
            graph.LookupMember(namespaceScope.Id, "Work", SymbolKind.Operation));
        Assert.Equal(namespaceScope.Id, operationScope.ParentScopeId);
        Assert.Equal(HirScopeKind.Callable, operationScope.Kind);
        Assert.Equal(workSymbol.Id, operationScope.DeclaringSymbolId);
    }

    [Fact]
    public void NamespacedBitArrayFunctionCallRepointsToItsGeneratedSpecialization()
    {
        var result = Compile("""
            namespace L {
                function count(flags: bit[]): int { return AsInt(flags); }
            }
            operation Main() {
                var flags: bit[] = new bit[2];
                var n: int = L.count(flags);
            }
            """);

        var generic = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "L.count");
        var sourceCall = SoleExpressionCall(
            result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "Main"));
        Assert.Equal(generic.Id, sourceCall.CalleeOpId);

        var analyzed = result.Hir.EffectAnalysis!.Program!;
        var specialization = analyzed.Operations.Single(operation =>
            operation.IsFunction
            && operation.Name.StartsWith("L.count__sz", StringComparison.Ordinal));
        var specializedCall = SoleExpressionCall(
            analyzed.Operations.Single(operation => operation.Name == "Main"));

        Assert.Equal(specialization.Name, specializedCall.Name);
        Assert.Equal(specialization.Id, specializedCall.CalleeOpId);
        Assert.DoesNotContain(analyzed.Operations, operation => operation.Id == generic.Id);

        var graph = Assert.IsType<HirScopeGraph>(result.Hir.SpecializedValidation!.Model.ScopeGraph);
        var namespaceScope = Assert.IsType<Scope>(graph.FindNamespaceScope("L"));
        var specializationSymbol = Assert.IsType<Symbol>(
            graph.FindDeclaration(specialization.Id));
        Assert.Equal(namespaceScope.Id, specializationSymbol.DeclaringScopeId);
    }

    [Fact]
    public void ExpressionCallDependencyEmitsCalleeBeforeCaller()
    {
        var result = Compile("""
            function wrapper(): int { return L.two(); }
            namespace L {
                function two(): int { return 2; }
            }
            operation Main() { var n: int = wrapper(); }
            """);

        var target = result.Targets.OpenQasm!.Program;
        var definitions = MirQasmEmitter
            .OrderDefinitions(target.Definitions)
            .ToArray();
        var caller = Assert.Single(
            definitions,
            definition =>
            MirQasmTestModel
                .Statements(definition.Body)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>()
                .Any(
                    expression =>
                        expression.Target is MirQasmUserFunctionTarget));
        var call = Assert.Single(
            MirQasmTestModel
                .Statements(caller.Body)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>());
        var callee = target.Resolve(
            Assert.IsType<MirQasmUserFunctionTarget>(call.Target));

        Assert.True(
            Array.IndexOf(definitions, callee)
            < Array.IndexOf(definitions, caller));
    }
}
