using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// A dotted namespace is a path through the program symbol graph, not one flat scope whose name happens
/// to contain dots. These tests pin the resulting lexical lookup order and keep <c>open</c> as a separate,
/// direct, non-transitive source of candidates.
/// </summary>
public class NestedNamespaceSymbolGraphTests
{
    private static QoraParseResult Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
        return result;
    }

    private static QCallNode SoleExpressionCall(QoraParseResult result, string operationName)
    {
        var operation = result.Ir!.Operations.Single(item => item.Name == operationName);
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

        var symbols = Assert.IsType<ProgramSymbolGraph>(result.Semantics!.ProgramSymbols);
        var rootSymbol = symbols.RootSymbol;
        var namespaceA = Assert.IsType<Symbol>(symbols.FindNamespace("A"));
        var namespaceAB = Assert.IsType<Symbol>(symbols.FindNamespace("A.B"));
        var work = result.MonoIr!.Operations.Single(operation => operation.Name == "A.B.Work");
        var workSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(work.Id));
        var workScope = Assert.IsType<Scope>(result.Semantics.FindRootScope(work.Id));

        Assert.Equal(SymbolKind.Namespace, namespaceA.Kind);
        Assert.Equal(SymbolKind.Namespace, namespaceAB.Kind);
        Assert.Equal(rootSymbol.Id, namespaceA.OwnerSymbolId);
        Assert.Equal(namespaceA.Id, namespaceAB.OwnerSymbolId);
        Assert.Equal(namespaceAB.Id, workSymbol.OwnerSymbolId);
        Assert.Null(workScope.ParentScopeId);
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "A.B.f");
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "A.f");
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "f");
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "A.B.f");
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM019");
        Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM007");
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

        var symbols = Assert.IsType<ProgramSymbolGraph>(result.Semantics!.ProgramSymbols);
        var namespaceA = Assert.IsType<Symbol>(symbols.FindNamespace("A"));
        var namespaceAB = Assert.IsType<Symbol>(symbols.FindNamespace("A.B"));
        var function = result.MonoIr!.Operations.Single(operation => operation.Name == "A.B.f");
        var work = result.MonoIr.Operations.Single(operation => operation.Name == "A.B.Work");
        var functionSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(function.Id));
        var workSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(work.Id));

        Assert.Same(namespaceAB, symbols.FindNamespace("A.B"));
        Assert.Equal(namespaceA.Id, namespaceAB.OwnerSymbolId);
        Assert.Equal(namespaceAB.Id, functionSymbol.OwnerSymbolId);
        Assert.Equal(namespaceAB.Id, workSymbol.OwnerSymbolId);
        Assert.Same(functionSymbol,
            symbols.LookupMember(namespaceAB.Id, "f", SymbolKind.Operation));
        Assert.Same(workSymbol,
            symbols.LookupMember(namespaceAB.Id, "Work", SymbolKind.Operation));
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

        var callableA = result.Ir!.Operations.Single(operation => operation.Name == "A");
        var nestedFunction = result.Ir.Operations.Single(operation => operation.Name == "A.B.f");
        var calls = result.Ir.Operations.Single(operation => operation.Name == "Main").Body
            .SelectMany(QNodes.ExpressionSites)
            .SelectMany(QNodes.CallsIn)
            .ToList();
        var symbols = Assert.IsType<ProgramSymbolGraph>(result.Semantics!.ProgramSymbols);
        var namespaceA = Assert.IsType<Symbol>(symbols.FindNamespace("A"));
        var callableSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(callableA.Id));

        Assert.Equal(callableA.Id, calls.Single(call => call.Name == "A").CalleeOpId);
        Assert.Equal(nestedFunction.Id, calls.Single(call => call.Name == "A.B.f").CalleeOpId);
        Assert.NotEqual(namespaceA.Id, callableSymbol.Id);
        Assert.Equal(symbols.RootSymbol.Id, namespaceA.OwnerSymbolId);
        Assert.Equal(symbols.RootSymbol.Id, callableSymbol.OwnerSymbolId);
        Assert.Same(namespaceA,
            symbols.LookupMember(symbols.RootSymbol.Id, "A", SymbolKind.Namespace));
        Assert.Same(callableSymbol,
            symbols.LookupMember(symbols.RootSymbol.Id, "A", SymbolKind.Operation));
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

        var callableB = result.Ir!.Operations.Single(operation => operation.Name == "A.B");
        var nestedFunction = result.Ir.Operations.Single(operation => operation.Name == "A.B.f");
        var calls = result.Ir.Operations.Single(operation => operation.Name == "Main").Body
            .SelectMany(QNodes.ExpressionSites)
            .SelectMany(QNodes.CallsIn)
            .ToList();
        var symbols = Assert.IsType<ProgramSymbolGraph>(result.Semantics!.ProgramSymbols);
        var namespaceA = Assert.IsType<Symbol>(symbols.FindNamespace("A"));
        var namespaceAB = Assert.IsType<Symbol>(symbols.FindNamespace("A.B"));
        var callableSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(callableB.Id));

        Assert.Equal(callableB.Id, calls.Single(call => call.Name == "A.B").CalleeOpId);
        Assert.Equal(nestedFunction.Id, calls.Single(call => call.Name == "A.B.f").CalleeOpId);
        Assert.NotEqual(namespaceAB.Id, callableSymbol.Id);
        Assert.Equal(namespaceA.Id, namespaceAB.OwnerSymbolId);
        Assert.Equal(namespaceA.Id, callableSymbol.OwnerSymbolId);
        Assert.Same(namespaceAB,
            symbols.LookupMember(namespaceA.Id, "B", SymbolKind.Namespace));
        Assert.Same(callableSymbol,
            symbols.LookupMember(namespaceA.Id, "B", SymbolKind.Operation));
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "A.B.f");
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM019");
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Errors, error => error.Code is "QSEM018" or "QSEM019");
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Errors, error => error.Code is "QSEM018" or "QSEM019");
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM019");
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
    public void LexicalScopesUseIdsAndRemainSeparateFromNamespaceOwnership()
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

        var model = result.Semantics!;
        var work = result.MonoIr!.Operations.Single(operation => operation.Name == "A.B.Work");
        var workSymbol = Assert.IsType<Symbol>(model.ProgramSymbols!.FindDeclaration(work.Id));
        var root = Assert.IsType<Scope>(model.FindRootScope(work.Id));
        var outer = Assert.IsType<QDecl>(work.Body[0]);
        var branch = Assert.IsType<QIf>(work.Body[1]);
        var inner = Assert.IsType<QDecl>(branch.Then[0]);
        var pending = new Stack<ScopeId>(root.ChildScopeIds);
        var descendants = new List<Scope>();
        while (pending.Count > 0)
        {
            var scope = Assert.IsType<Scope>(model.FindScope(pending.Pop()));
            descendants.Add(scope);
            foreach (var childId in scope.ChildScopeIds)
                pending.Push(childId);
        }
        var innerScope = descendants.Single(scope => scope.LookupLocal("inner") is not null);

        Assert.Null(root.ParentScopeId);
        Assert.NotNull(innerScope.ParentScopeId);
        Assert.Same(innerScope, model.FindScope(innerScope.Id));
        Assert.Same(innerScope, model.Scopes[innerScope.Id]);
        Assert.Same(workSymbol, model.ProgramSymbols.Symbols[workSymbol.Id]);
        var ancestor = innerScope;
        while (ancestor.ParentScopeId is { } parentId)
            ancestor = Assert.IsType<Scope>(model.FindScope(parentId));
        Assert.Same(root, ancestor);
        Assert.Equal(workSymbol.Id, model.FindSymbol(outer.Id)!.OwnerSymbolId);
        Assert.Equal(workSymbol.Id, model.FindSymbol(inner.Id)!.OwnerSymbolId);
    }
}
