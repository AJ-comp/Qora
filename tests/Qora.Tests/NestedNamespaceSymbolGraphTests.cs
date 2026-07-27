using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// A dotted namespace is a path through the unified HIR scope graph, not one flat scope whose name happens
/// to contain dots. These tests pin the containment chain and lexical lookup order while keeping
/// <c>open</c> as a separate, direct, non-transitive source of candidates.
/// </summary>
public class NestedNamespaceSymbolGraphTests
{
    [Fact]
    public void ScopeGraphRejectsMismatchedDeclaringSymbolsAndCallableIds()
    {
        var graph = new HirScopeGraph();
        var namespaceSymbol = new Symbol(
            graph.RootScope.Id,
            "N",
            SymbolKind.Namespace);
        graph.RegisterDeclaredMember(namespaceSymbol);

        Assert.Throws<InvalidOperationException>(() =>
            graph.CreateScope(
                HirScopeKind.Callable,
                graph.RootScope.Id,
                namespaceSymbol.Id));

        var callableSymbol = new Symbol(
            graph.RootScope.Id,
            "Work",
            SymbolKind.Operation,
            declarationNodeId: 42);
        graph.RegisterDeclaredMember(callableSymbol);
        var callableScope = graph.CreateScope(
            HirScopeKind.Callable,
            graph.RootScope.Id,
            callableSymbol.Id);

        Assert.Throws<InvalidOperationException>(() =>
            graph.RegisterCallableScope(43, callableScope));
    }

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

    private static QCallNode SoleExpressionCall(Compilation result, string operationName)
    {
        var operation = result.Hir.Resolved!.Program!.Operations.Single(item => item.Name == operationName);
        return operation.Body
            .SelectMany(QNodes.ExpressionSites)
            .SelectMany(QNodes.CallsIn)
            .Single();
    }

    [Fact]
    public void DottedNamespaceCreatesOneScopePerSegment()
    {
        var result = Compile("""
            namespace A.B {
                function f(): int { return 1; }
                operation Work() { var n: int = f(); }
            }
            operation Main() { A.B.Work(); }
            """);

        var graph = Assert.IsType<HirScopeGraph>(result.Hir.SpecializedValidation!.Model.ScopeGraph);
        var namespaceAScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A"));
        var namespaceABScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A.B"));
        var namespaceA = Assert.IsType<Symbol>(graph.FindNamespaceSymbol("A"));
        var namespaceAB = Assert.IsType<Symbol>(graph.FindNamespaceSymbol("A.B"));
        var work = result.Hir.Specialized!.Program!.Operations.Single(operation => operation.Name == "A.B.Work");
        var workSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(work.Id));
        var workScope = Assert.IsType<Scope>(result.Hir.SpecializedValidation!.Model.FindRootScope(work.Id));

        Assert.Equal(HirScopeKind.Program, graph.RootScope.Kind);
        Assert.Equal(SymbolKind.Namespace, namespaceA.Kind);
        Assert.Equal(SymbolKind.Namespace, namespaceAB.Kind);
        Assert.Equal(graph.RootScope.Id, namespaceA.DeclaringScopeId);
        Assert.Equal(graph.RootScope.Id, namespaceAScope.ParentScopeId);
        Assert.Equal(namespaceAScope.Id, namespaceAB.DeclaringScopeId);
        Assert.Equal(namespaceAScope.Id, namespaceABScope.ParentScopeId);
        Assert.Equal(namespaceABScope.Id, workSymbol.DeclaringScopeId);
        Assert.Equal(namespaceABScope.Id, workScope.ParentScopeId);
        Assert.Equal(HirScopeKind.Callable, workScope.Kind);
    }

    [Fact]
    public void BareCallPrefersTheCurrentNestedNamespace()
    {
        var result = Compile("""
            function f(): int { return 0; }
            namespace A {
                function f(): int { return 1; }
            }
            namespace A.B {
                function f(): int { return 2; }
                operation Work() { var n: int = f(); }
            }
            operation Main() { A.B.Work(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "A.B.f");
        var call = SoleExpressionCall(result, "A.B.Work");

        Assert.Equal("A.B.f", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void BareCallFallsBackFromNestedNamespaceToItsParentNamespace()
    {
        var result = Compile("""
            function f(): int { return 0; }
            namespace A {
                function f(): int { return 1; }
            }
            namespace A.B {
                operation Work() { var n: int = f(); }
            }
            operation Main() { A.B.Work(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "A.f");
        var call = SoleExpressionCall(result, "A.B.Work");

        Assert.Equal("A.f", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void BareCallFallsBackFromNestedNamespaceThroughParentToGlobal()
    {
        var result = Compile("""
            function f(): int { return 0; }
            namespace A.B {
                operation Work() { var n: int = f(); }
            }
            operation Main() { A.B.Work(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "f");
        var call = SoleExpressionCall(result, "A.B.Work");

        Assert.Equal("f", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void QualifiedCallWalksEveryExactNamespaceSegment()
    {
        var result = Compile("""
            namespace A {
                function f(): int { return 1; }
            }
            namespace A.B {
                function f(): int { return 2; }
            }
            namespace B {
                function f(): int { return 3; }
            }
            operation Main() { var n: int = A.B.f(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "A.B.f");
        var call = SoleExpressionCall(result, "Main");

        Assert.Equal("A.B.f", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void QualifiedCallDoesNotSkipAMissingIntermediateSegment()
    {
        var result = Compiler.Compile("""
            namespace A {
                function f(): int { return 1; }
            }
            namespace X {
                function f(): int { return 2; }
            }
            operation Main() { var n: int = A.X.f(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM019");
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM007");
    }

    [Fact]
    public void RepeatedDottedNamespaceBlocksMergeIntoTheSameSegmentScopes()
    {
        var result = Compile("""
            namespace A.B {
                function f(): int { return 1; }
            }
            namespace A.B {
                operation Work() { var n: int = f(); }
            }
            operation Main() { A.B.Work(); }
            """);

        var graph = Assert.IsType<HirScopeGraph>(result.Hir.SpecializedValidation!.Model.ScopeGraph);
        var namespaceAScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A"));
        var namespaceABScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A.B"));
        var namespaceAB = Assert.IsType<Symbol>(graph.FindNamespaceSymbol("A.B"));
        var function = result.Hir.Specialized!.Program!.Operations.Single(operation => operation.Name == "A.B.f");
        var work = result.Hir.Specialized!.Program.Operations.Single(operation => operation.Name == "A.B.Work");
        var functionSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(function.Id));
        var workSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(work.Id));

        Assert.Same(namespaceABScope, graph.FindNamespaceScope("A.B"));
        Assert.Equal(namespaceAScope.Id, namespaceAB.DeclaringScopeId);
        Assert.Equal(namespaceAScope.Id, namespaceABScope.ParentScopeId);
        Assert.Equal(namespaceABScope.Id, functionSymbol.DeclaringScopeId);
        Assert.Equal(namespaceABScope.Id, workSymbol.DeclaringScopeId);
        Assert.Same(functionSymbol,
            graph.LookupMember(namespaceABScope.Id, "f", SymbolKind.Operation));
        Assert.Same(workSymbol,
            graph.LookupMember(namespaceABScope.Id, "Work", SymbolKind.Operation));
    }

    [Fact]
    public void GlobalCallableAndNamespaceSegmentCoexistByRole()
    {
        var result = Compile("""
            function A(): int { return 0; }
            namespace A.B {
                function f(): int { return 1; }
            }
            operation Main() {
                var left: int = A();
                var right: int = A.B.f();
            }
            """);

        var callableA = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "A");
        var nestedFunction = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "A.B.f");
        var calls = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "Main").Body
            .SelectMany(QNodes.ExpressionSites)
            .SelectMany(QNodes.CallsIn)
            .ToList();
        var graph = Assert.IsType<HirScopeGraph>(result.Hir.SpecializedValidation!.Model.ScopeGraph);
        var namespaceA = Assert.IsType<Symbol>(graph.FindNamespaceSymbol("A"));
        var callableSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(callableA.Id));

        Assert.Equal(callableA.Id, calls.Single(call => call.Name == "A").CalleeOpId);
        Assert.Equal(nestedFunction.Id, calls.Single(call => call.Name == "A.B.f").CalleeOpId);
        Assert.NotEqual(namespaceA.Id, callableSymbol.Id);
        Assert.Equal(graph.RootScope.Id, namespaceA.DeclaringScopeId);
        Assert.Equal(graph.RootScope.Id, callableSymbol.DeclaringScopeId);
        Assert.Same(namespaceA,
            graph.LookupMember(graph.RootScope.Id, "A", SymbolKind.Namespace));
        Assert.Same(callableSymbol,
            graph.LookupMember(graph.RootScope.Id, "A", SymbolKind.Operation));
        Assert.Null(graph.RootScope.Lookup("A"));
    }

    [Fact]
    public void CallableAndChildNamespaceSegmentCoexistByRole()
    {
        var result = Compile("""
            namespace A {
                function B(): int { return 0; }
            }
            namespace A.B {
                function f(): int { return 1; }
            }
            operation Main() {
                var left: int = A.B();
                var right: int = A.B.f();
            }
            """);

        var callableB = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "A.B");
        var nestedFunction = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "A.B.f");
        var calls = result.Hir.Resolved!.Program.Operations.Single(operation => operation.Name == "Main").Body
            .SelectMany(QNodes.ExpressionSites)
            .SelectMany(QNodes.CallsIn)
            .ToList();
        var graph = Assert.IsType<HirScopeGraph>(result.Hir.SpecializedValidation!.Model.ScopeGraph);
        var namespaceAScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A"));
        var namespaceAB = Assert.IsType<Symbol>(graph.FindNamespaceSymbol("A.B"));
        var callableSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(callableB.Id));

        Assert.Equal(callableB.Id, calls.Single(call => call.Name == "A.B").CalleeOpId);
        Assert.Equal(nestedFunction.Id, calls.Single(call => call.Name == "A.B.f").CalleeOpId);
        Assert.NotEqual(namespaceAB.Id, callableSymbol.Id);
        Assert.Equal(namespaceAScope.Id, namespaceAB.DeclaringScopeId);
        Assert.Equal(namespaceAScope.Id, callableSymbol.DeclaringScopeId);
        Assert.Same(namespaceAB,
            graph.LookupMember(namespaceAScope.Id, "B", SymbolKind.Namespace));
        Assert.Same(callableSymbol,
            graph.LookupMember(namespaceAScope.Id, "B", SymbolKind.Operation));
        Assert.Null(namespaceAScope.Lookup("B"));
    }

    [Fact]
    public void OpenTargetsOneExactNestedNamespace()
    {
        var result = Compile("""
            namespace A.B {
                function f(): int { return 1; }
            }
            namespace App {
                open A.B;
                operation Work() { var n: int = f(); }
            }
            operation Main() { App.Work(); }
            """);

        var function = result.Hir.Resolved!.Program!.Operations.Single(operation => operation.Name == "A.B.f");
        var call = SoleExpressionCall(result, "App.Work");

        Assert.Equal("A.B.f", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
    }

    [Fact]
    public void OpeningAParentNamespaceDoesNotExposeDescendantMembers()
    {
        var result = Compiler.Compile("""
            namespace A.B {
                function f(): int { return 1; }
            }
            namespace App {
                open A;
                operation Work() { var n: int = f(); }
            }
            operation Main() { App.Work(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM019");
    }

    [Fact]
    public void OpenDoesNotReExportNamespacesOpenedByItsTarget()
    {
        var result = Compiler.Compile("""
            namespace Base {
                function f(): int { return 1; }
            }
            namespace Middle {
                open Base;
                function g(): int { return f(); }
            }
            namespace App {
                open Middle;
                operation Work() { var n: int = f(); }
            }
            operation Main() { App.Work(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code is "QSEM018" or "QSEM019");
    }

    [Fact]
    public void ParentNamespaceOpenIsNotInheritedByANestedNamespace()
    {
        var result = Compiler.Compile("""
            namespace Base {
                function f(): int { return 1; }
            }
            namespace A {
                open Base;
                function g(): int { return f(); }
            }
            namespace A.B {
                operation Work() { var n: int = f(); }
            }
            operation Main() { A.B.Work(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code is "QSEM018" or "QSEM019");
    }

    [Fact]
    public void BuiltinIntermediateNamespaceDoesNotMakeItsParentOpenable()
    {
        var result = Compiler.Compile("""
            namespace App {
                open Qora;
                operation Work() { }
            }
            operation Main() { App.Work(); }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM019");
    }

    [Fact]
    public void IntrinsicNamespaceItselfCanBeOpened()
    {
        Compile("""
            namespace App {
                open Qora.Intrinsic;
                operation Work() { }
            }
            operation Main() { App.Work(); }
            """);
    }

    [Fact]
    public void ProgramNamespaceCallableAndBlockScopesShareOneParentChain()
    {
        var result = Compile("""
            namespace A.B {
                operation Work() {
                    var outer: int = 1;
                    if (true) {
                        var inner: int = 2;
                    }
                }
            }
            operation Main() { A.B.Work(); }
            """);

        var model = result.Hir.SpecializedValidation!.Model;
        var graph = Assert.IsType<HirScopeGraph>(model.ScopeGraph);
        var work = result.Hir.Specialized!.Program!.Operations.Single(operation => operation.Name == "A.B.Work");
        var workSymbol = Assert.IsType<Symbol>(graph.FindDeclaration(work.Id));
        var namespaceAScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A"));
        var namespaceABScope = Assert.IsType<Scope>(graph.FindNamespaceScope("A.B"));
        var callableScope = Assert.IsType<Scope>(model.FindRootScope(work.Id));
        var outer = Assert.IsType<QDecl>(work.Body[0]);
        var branch = Assert.IsType<QIf>(work.Body[1]);
        var inner = Assert.IsType<QDecl>(branch.Then[0]);
        var callableSite = new HirScopeSite(work.Id, HirScopeSiteRole.CallableBody);
        var conditionSite = new HirScopeSite(branch.Id, HirScopeSiteRole.IfCondition);
        var thenSite = new HirScopeSite(branch.Id, HirScopeSiteRole.IfThen);
        var elseSite = new HirScopeSite(branch.Id, HirScopeSiteRole.IfElse);
        var conditionScope = Assert.IsType<Scope>(model.FindScope(conditionSite));
        var thenScope = Assert.IsType<Scope>(model.FindScope(thenSite));
        var elseScope = Assert.IsType<Scope>(model.FindScope(elseSite));

        Assert.Equal(HirScopeKind.Program, graph.RootScope.Kind);
        Assert.Null(graph.RootScope.ParentScopeId);
        Assert.Equal(graph.RootScope.Id, namespaceAScope.ParentScopeId);
        Assert.Equal(namespaceAScope.Id, namespaceABScope.ParentScopeId);
        Assert.Equal(namespaceABScope.Id, callableScope.ParentScopeId);
        Assert.Equal(callableScope.Id, conditionScope.ParentScopeId);
        Assert.Equal(conditionScope.Id, thenScope.ParentScopeId);
        Assert.Equal(conditionScope.Id, elseScope.ParentScopeId);
        Assert.Equal(HirScopeKind.Callable, callableScope.Kind);
        Assert.Equal(HirScopeKind.Condition, conditionScope.Kind);
        Assert.Equal(HirScopeKind.Block, thenScope.Kind);
        Assert.Equal(HirScopeKind.Block, elseScope.Kind);

        Assert.Same(callableScope, model.FindScope(callableSite));
        Assert.Same(callableScope, graph.FindScope(callableSite));
        Assert.Same(thenScope, graph.FindScope(thenSite));
        Assert.NotEqual(thenScope.Id, elseScope.Id);
        Assert.Same(thenScope, model.FindScope(thenScope.Id));
        Assert.Same(thenScope, model.Scopes[thenScope.Id]);
        Assert.Same(workSymbol, graph.Symbols[workSymbol.Id]);
        Assert.Same(model.FindSymbol(inner.Id), thenScope.LookupLocal("inner"));
        Assert.Equal(callableScope.Id, model.FindSymbol(outer.Id)!.DeclaringScopeId);
        Assert.Equal(thenScope.Id, model.FindSymbol(inner.Id)!.DeclaringScopeId);
    }
}
