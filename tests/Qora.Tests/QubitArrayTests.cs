using Qora.Ir;

namespace Qora.Tests;

public class QubitArrayTests
{
    [Theory]
    [InlineData("operation Bad(q: Qubit[2]){} operation Main(){ use q=Qubit[2]; Bad(q); }")]
    [InlineData("operation Bad(q: Qubit[n]){} operation Main(){ use q=Qubit[2]; Bad(q); }")]
    public void RejectsLengthsInSourceParameterTypes(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code is "QORA0000" or "QINTERNAL");
    }

    [Fact]
    public void CreatesOneHiddenSpecializationPerCallSize()
    {
        var result = Compile("""
            operation Visit(qubits: Qubit[]) {
                for i in 0..qubits.Count-1 { X(qubits[i]); }
            }
            operation Main() {
                use two = Qubit[2];
                use three = Qubit[3];
                Visit(two);
                Visit(three);
            }
            """);

        var specs = result.Hir.EffectAnalysis!.Program!.Callables.Where(o => o.DisplayName == "Visit").ToList();
        Assert.Equal(new[] { 2, 3 }, specs.Select(o => o.Parameters.Single().RegisterSize!.Value).Order().ToArray());

        var targetSpecializations = result.Targets.OpenQasm!.Program.Definitions
            .Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Operation
                    && definition.Parameters.Length == 1
                    && definition.Parameters[0].Type is MirQasmQubitType
                    {
                        IsRegister: true,
                    })
            .ToArray();
        Assert.Equal(
            new[] { 2, 3 },
            targetSpecializations
                .Select(
                    definition =>
                        ((MirQasmQubitType)definition.Parameters[0].Type).Count)
                .Order()
                .ToArray());
        Assert.DoesNotContain(
            targetSpecializations.SelectMany(
                definition =>
                    definition.Body.SelectMany(MirQasmTestModel.Expressions)),
            expression => expression is MirQasmSizeOfExpression);
    }

    [Fact]
    public void BindsMultipleQubitArraysIndependently()
    {
        var result = Compile("""
            operation Pair(left: Qubit[], right: Qubit[]) {
                for i in 0..left.Count-1 { X(left[i]); }
                for j in 0..right.Count-1 { X(right[j]); }
            }
            operation Main() {
                use left = Qubit[2];
                use right = Qubit[3];
                Pair(left, right);
            }
            """);

        var pair = result.Hir.EffectAnalysis!.Program!.Callables.Single(o => o.DisplayName == "Pair");
        Assert.Equal(new[] { 2, 3 }, pair.Parameters.Select(p => p.RegisterSize!.Value).ToArray());
        Assert.Contains("Pair__sz2_3", pair.Name);
    }

    [Fact]
    public void SpecializesNestedQubitArrayCalls()
    {
        var result = Compile("""
            operation Inner(qubits: Qubit[]) {
                for i in 0..qubits.Count-1 { X(qubits[i]); }
            }
            operation Outer(qubits: Qubit[]) { Inner(qubits); }
            operation Main() {
                use work = Qubit[4];
                Outer(work);
            }
            """);

        var inner = result.Hir.EffectAnalysis!.Program!.Callables.Single(o => o.DisplayName == "Inner");
        var outer = result.Hir.EffectAnalysis!.Program.Callables.Single(o => o.DisplayName == "Outer");
        Assert.Equal(4, inner.Parameters.Single().RegisterSize);
        Assert.Equal(4, outer.Parameters.Single().RegisterSize);
        var nestedCall = outer.Body.OfType<HirCallStatement>().Single();
        Assert.Equal(inner.Id, nestedCall.Call.CalleeId);
    }

    [Fact]
    public void ResolvesCountOnAnEntryAllocationWithoutAGenericOperation()
    {
        var result = Compile("operation Main(){ use work=Qubit[3]; for i in 0..work.Count-1 { X(work[i]); } }");

        var entry = result.Targets.OpenQasm!.Program.EntryPoint.Body;
        var statements = MirQasmTestModel.Statements(entry).ToArray();
        Assert.Contains(
            statements.OfType<MirQasmQubitDeclarationStatement>(),
            declaration =>
                declaration.Type is
                {
                    Count: 3,
                    IsRegister: true,
                });
        var loop = Assert.Single(statements.OfType<MirQasmWhileStatement>());
        var dependencies = LoopDependencies(entry, loop).ToArray();
        Assert.Contains(
            dependencies,
            expression =>
                expression is MirQasmLiteralExpression { Text: "3" });
        Assert.DoesNotContain(
            dependencies,
            expression => expression is MirQasmSizeOfExpression);
    }

    [Fact]
    public void SpecializationLeavesClassicalArrayCountForSizeofLowering()
    {
        var result = Compile("""
            operation Mix(q: Qubit[], var values: int[]) {
                for i in 0..q.Count-1 { X(q[i]); }
                for j in 0..values.Count-1 { values[j] = values[j] + 1; }
            }
            operation Main() {
                use q = Qubit[2];
                var values: int[] = [1, 2, 3];
                Mix(q, var values);
            }
            """);

        var target = result.Targets.OpenQasm!.Program;
        var mix = Assert.Single(
            target.Definitions.Where(
                definition =>
                    definition.Parameters.Any(
                        parameter =>
                            parameter.Type is MirQasmQubitType
                            {
                                Count: 2,
                                IsRegister: true,
                            })
                    && definition.Parameters.Any(
                        parameter =>
                            parameter.Type is MirQasmArrayType
                            {
                                ElementType.Kind: MirQasmScalarKind.Int,
                            })));
        var array = Assert.Single(
            mix.Parameters.Where(
                parameter => parameter.Type is MirQasmArrayType));
        Assert.Equal(MirQasmParameterAccess.Mutable, array.Access);
        var loops = MirQasmTestModel.Statements(mix.Body)
            .OfType<MirQasmWhileStatement>()
            .ToArray();
        Assert.Equal(2, loops.Length);
        Assert.Contains(
            loops,
            loop =>
                LoopDependencies(mix.Body, loop).Any(
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "2" }));
        Assert.Contains(
            loops,
            loop =>
                LoopDependencies(mix.Body, loop).Any(
                    expression =>
                        expression is MirQasmSizeOfExpression
                        {
                            Operand: MirQasmParameterReferenceExpression reference,
                        }
                        && reference.Parameter == array.Id));
    }

    [Fact]
    public void RechecksLiteralBoundsAfterSpecialization() =>
        Compiler.Rejects(
            "operation Bad(q: Qubit[]){ X(q[2]); } operation Main(){ use q=Qubit[2]; Bad(q); }",
            "QSEM016");

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

    private static IEnumerable<MirQasmExpression> LoopDependencies(
        IEnumerable<MirQasmStatement> ownerBody,
        MirQasmWhileStatement loop)
    {
        foreach (var expression in MirQasmTestModel.Expressions(loop.Condition))
            foreach (var dependency in ownerBody.DependencyClosure(expression))
                yield return dependency;
        foreach (var statement in MirQasmTestModel.Statements(loop.Body))
            foreach (var expression in MirQasmTestModel.Expressions(statement))
                foreach (var dependency in ownerBody.DependencyClosure(expression))
                    yield return dependency;
    }
}
