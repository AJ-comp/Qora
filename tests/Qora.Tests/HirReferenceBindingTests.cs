using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class HirReferenceBindingTests
{
    [Fact]
    public void UserAndBuiltinCallsKeepExactCallableBindings()
    {
        var compilation = CompileHir("""
            namespace L {
                function two(): int {
                    return 2;
                }

                operation Work(q: Qubit) {
                    var bits: bit[] = [0, 1];
                    Qora.Intrinsic.H(q);
                    var result: int = two() + Qora.Intrinsic.AsInt(bits);
                }
            }

            operation Main() {
                use q = Qubit[1];
                L.Work(q[0]);
            }
            """);
        var analyzed = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis);
        var two = Assert.Single(
            analyzed.Program.Callables,
            callable => callable.Name == "two");
        var work = Assert.Single(
            analyzed.Program.Callables,
            callable => callable.Name == "Work");
        var gateCall = Assert.Single(
            work.Body.OfType<HirCallStatement>()).Call;
        var result = Assert.Single(
            work.Body.OfType<HirVariableDeclarationStatement>(),
            statement => statement.Name == "result");
        var calls = HirExpressions.CallsIn(result.Value).ToArray();
        Assert.Equal(2, calls.Length);
        var userCall = Assert.Single(calls, call => call.Name == "L.two");
        var builtinCall = Assert.Single(
            calls,
            call => call.Name == QoraGates.BitsAsInt);

        var userSymbol = Assert.IsType<Symbol>(
            analyzed.Model.FindReferencedSymbol(userCall.Callee.Id));
        var builtinFunction = Assert.IsType<Symbol>(
            analyzed.Model.FindReferencedSymbol(builtinCall.Callee.Id));
        var builtinGate = Assert.IsType<Symbol>(
            analyzed.Model.FindReferencedSymbol(gateCall.Callee.Id));

        Assert.Equal(SymbolKind.Callable, userSymbol.Kind);
        Assert.Equal(two.Id, userSymbol.DeclarationNodeId);
        Assert.Equal(SymbolKind.BuiltinFunction, builtinFunction.Kind);
        Assert.Equal(QoraGates.BitsAsInt, builtinFunction.SourceName);
        Assert.Equal(SymbolKind.BuiltinGate, builtinGate.Kind);
        Assert.Equal("H", builtinGate.SourceName);
    }

    [Fact]
    public void ArrayCountIndexAndMeasurementReferencesAllKeepExactBindings()
    {
        var compilation = CompileHir("""
            operation Main() {
                use q = Qubit[1];
                var values: int[] = [10];
                const index: int = 0;
                values[index] = values[index] + values.Count;
                var result: bit = M(q[0]);
            }
            """);
        var program = Assert.IsType<HirSnapshot>(
            compilation.Hir.Specialized).Program;
        var semantics = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.EffectAnalysis).Model;
        var main = Assert.Single(program.Callables);
        var qubitDeclaration =
            Assert.IsType<HirQubitDeclarationStatement>(
                main.Body.Statements[0]);
        var arrayDeclaration =
            Assert.IsType<HirVariableDeclarationStatement>(
                main.Body.Statements[1]);
        var indexDeclaration =
            Assert.IsType<HirVariableDeclarationStatement>(
                main.Body.Statements[2]);
        var assignment = Assert.IsType<HirAssignmentStatement>(
            main.Body.Statements[3]);
        var measurementDeclaration =
            Assert.IsType<HirVariableDeclarationStatement>(
                main.Body.Statements[4]);

        var qubitSymbol = Assert.IsType<Symbol>(
            semantics.FindSymbol(qubitDeclaration.Id));
        var arraySymbol = Assert.IsType<Symbol>(
            semantics.FindSymbol(arrayDeclaration.Id));
        var indexSymbol = Assert.IsType<Symbol>(
            semantics.FindSymbol(indexDeclaration.Id));

        var target = Assert.IsType<HirIndexExpression>(
            assignment.Target);
        Assert.Same(
            arraySymbol,
            semantics.FindReferencedSymbol(
                Assert.IsType<HirNameExpression>(
                    target.Receiver).Id));
        Assert.Same(
            indexSymbol,
            semantics.FindReferencedSymbol(
                Assert.IsType<HirNameExpression>(
                    target.Index).Id));

        var sum = Assert.IsType<HirBinaryExpression>(
            assignment.Value);
        var load = Assert.IsType<HirIndexExpression>(sum.Left);
        Assert.Same(
            arraySymbol,
            semantics.FindReferencedSymbol(
                Assert.IsType<HirNameExpression>(
                    load.Receiver).Id));
        Assert.Same(
            indexSymbol,
            semantics.FindReferencedSymbol(
                Assert.IsType<HirNameExpression>(
                    load.Index).Id));
        var count = Assert.IsType<HirMemberAccessExpression>(
            sum.Right);
        Assert.Same(
            arraySymbol,
            semantics.FindReferencedSymbol(
                Assert.IsType<HirNameExpression>(
                    count.Receiver).Id));

        var measurement =
            Assert.IsType<HirMeasurementExpression>(
                measurementDeclaration.Value);
        var measuredQubit =
            Assert.IsType<HirIndexExpression>(
                measurement.Target);
        Assert.Same(
            qubitSymbol,
            semantics.FindReferencedSymbol(
                Assert.IsType<HirNameExpression>(
                    measuredQubit.Receiver).Id));
    }

    private static Compilation CompileHir(string source)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly));
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return compilation;
    }
}
