using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Namespace resolution applies to expression-position calls just as it does to statement calls:
/// the parser accepts qualified function names, the resolver writes both the canonical name and stable
/// callee Id, and namespace ownership lives in <see cref="ProgramSymbolGraph"/> independently from lexical
/// <see cref="Scope"/> objects. These tests pin that contract through resolution, validation,
/// monomorphization, and emission.
/// </summary>
public class NamespaceFunctionTests
{
    private static QoraParseResult Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "L.two");
        var work = result.Ir.Operations.Single(operation => operation.Name == "L.Work");
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "L.two");
        var main = result.Ir.Operations.Single(operation => operation.Name == "Main");
        var call = SoleExpressionCall(main);

        Assert.Equal("L.two", call.Name);
        Assert.Equal(function.Id, call.CalleeOpId);
        Assert.Contains("def L_two() -> int {", result.Qasm);
        Assert.Contains("int n = L_two();", result.Qasm);
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

        var function = result.Ir!.Operations.Single(operation => operation.Name == "L.two");
        var work = result.Ir.Operations.Single(operation => operation.Name == "App.Work");
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

        Assert.False(result.Success);
        Assert.Equal(new[] { "QSEM018" }, result.Errors.Select(error => error.Code));
        Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM007");
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "QSEM005");
        Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM007");
        Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM019");
    }

    [Fact]
    public void QualifiedIntrinsicGateInAnExpressionIsKnownButRejectedAsVoid()
    {
        var result = Compiler.Compile("""
            operation Main() { var n: int = Qora.Intrinsic.H(); }
            """);

        Assert.False(result.Success);
        Assert.Equal(new[] { "QSEM005" }, result.Errors.Select(error => error.Code));
        Assert.DoesNotContain(result.Errors, error => error.Code == "QSEM019");
    }

    [Fact]
    public void NamespacedCallablesAreOwnedByTheirNamespaceSymbol()
    {
        var result = Compile("""
            namespace L {
                function two(): int { return 2; }
                operation Work() { var n: int = two(); }
            }
            operation Main() { L.Work(); }
            """);

        var work = result.MonoIr!.Operations.Single(operation => operation.Name == "L.Work");
        var function = result.MonoIr.Operations.Single(operation => operation.Name == "L.two");
        var symbols = Assert.IsType<ProgramSymbolGraph>(result.Semantics!.ProgramSymbols);
        var namespaceSymbol = Assert.IsType<Symbol>(symbols.FindNamespace("L"));
        var workSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(work.Id));
        var functionSymbol = Assert.IsType<Symbol>(symbols.FindDeclaration(function.Id));
        var operationScope = Assert.IsType<Scope>(result.Semantics.FindRootScope(work.Id));

        Assert.Equal(SymbolKind.Namespace, namespaceSymbol.Kind);
        Assert.Equal(symbols.RootSymbol.Id, namespaceSymbol.OwnerSymbolId);
        Assert.Equal(namespaceSymbol.Id, workSymbol.OwnerSymbolId);
        Assert.Equal(namespaceSymbol.Id, functionSymbol.OwnerSymbolId);
        Assert.Same(functionSymbol,
            symbols.LookupMember(namespaceSymbol.Id, "two", SymbolKind.Operation));
        Assert.Same(workSymbol,
            symbols.LookupMember(namespaceSymbol.Id, "Work", SymbolKind.Operation));
        Assert.Null(operationScope.ParentScopeId);
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

        var generic = result.Ir!.Operations.Single(operation => operation.Name == "L.count");
        var sourceCall = SoleExpressionCall(
            result.Ir.Operations.Single(operation => operation.Name == "Main"));
        Assert.Equal(generic.Id, sourceCall.CalleeOpId);

        var analyzed = result.AnalyzedIr!;
        var specialization = analyzed.Operations.Single(operation =>
            operation.IsFunction
            && operation.Name.StartsWith("L.count__sz", StringComparison.Ordinal));
        var specializedCall = SoleExpressionCall(
            analyzed.Operations.Single(operation => operation.Name == "Main"));

        Assert.Equal(specialization.Name, specializedCall.Name);
        Assert.Equal(specialization.Id, specializedCall.CalleeOpId);
        Assert.DoesNotContain(analyzed.Operations, operation => operation.Id == generic.Id);

        var symbols = Assert.IsType<ProgramSymbolGraph>(result.Semantics!.ProgramSymbols);
        var namespaceSymbol = Assert.IsType<Symbol>(symbols.FindNamespace("L"));
        var specializationSymbol = Assert.IsType<Symbol>(
            symbols.FindDeclaration(specialization.Id));
        Assert.Equal(namespaceSymbol.Id, specializationSymbol.OwnerSymbolId);
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

        var callee = result.Qasm.IndexOf("def L_two()", StringComparison.Ordinal);
        var caller = result.Qasm.IndexOf("def wrapper()", StringComparison.Ordinal);

        Assert.True(callee >= 0, result.Qasm);
        Assert.True(caller >= 0, result.Qasm);
        Assert.True(callee < caller, result.Qasm);
        Assert.Contains("= L_two();", result.Qasm);
    }
}
