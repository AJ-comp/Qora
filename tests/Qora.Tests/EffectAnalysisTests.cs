using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// The HIR effect pass retains one lowering contract: which formal qubit parameters a callable may
/// modify. Detailed quantum effects, histories, and version graphs belong to MIR.
/// </summary>
public sealed class EffectAnalysisTests
{
    [Fact]
    public void ControlledGateMarksOnlyItsTargetParameterModified()
    {
        var (compilation, model) = Compile(
            """
            operation Apply(control: Qubit, target: Qubit) {
                CNOT(control, target);
            }

            operation Main() {
                use q = Qubit[2];
                Apply(q[0], q[1]);
            }
            """);

        AssertRefs(
            Summary(compilation, model, "Apply").ParamModified,
            Whole("target"));
    }

    [Fact]
    public void ModifiedParametersPropagateTransitivelyThroughCalls()
    {
        var (compilation, model) = Compile(
            """
            operation Leaf(control: Qubit, target: Qubit) {
                CNOT(control, target);
            }

            operation Middle(left: Qubit, right: Qubit) {
                Leaf(left, right);
            }

            operation Main() {
                use q = Qubit[2];
                Middle(q[0], q[1]);
            }
            """);

        AssertRefs(
            Summary(compilation, model, "Leaf").ParamModified,
            Whole("target"));
        AssertRefs(
            Summary(compilation, model, "Middle").ParamModified,
            Whole("right"));
    }

    [Fact]
    public void ParameterArrayModificationPreservesLiteralAndBlanketBreadth()
    {
        var (compilation, model) = Compile(
            """
            operation ModifyOne(values: Qubit[]) {
                X(values[1]);
            }

            operation ModifyAll(values: Qubit[]) {
                X(values);
            }

            operation Main() {
                use q = Qubit[2];
                ModifyOne(q);
                ModifyAll(q);
            }
            """);

        AssertRefs(
            Summary(compilation, model, "ModifyOne").ParamModified,
            At("values", 1));
        AssertRefs(
            Summary(compilation, model, "ModifyAll").ParamModified,
            Whole("values"));
    }

    [Fact]
    public void MeasurementAndResetBothCountAsParameterModification()
    {
        var (compilation, model) = Compile(
            """
            operation Change(measured: Qubit[], reset: Qubit) {
                var result: bit = M(measured[0]);
                Reset(reset);
            }

            operation Main() {
                use measured = Qubit[1];
                use reset = Qubit[1];
                Change(measured, reset[0]);
            }
            """);

        AssertRefs(
            Summary(compilation, model, "Change").ParamModified,
            At("measured", 0),
            Whole("reset"));
    }

    private static (Compilation Compilation, HirSemanticModel Model) Compile(
        string source)
    {
        var compilation = QoraCompiler.Compile(source);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic => diagnostic.Error)));
        return (
            compilation,
            Assert.IsType<HirSemanticArtifact>(
                compilation.Hir.EffectAnalysis).Model);
    }

    private static OpEffectSummary Summary(
        Compilation compilation,
        HirSemanticModel model,
        string callableName)
    {
        var callable = Assert.Single(
            compilation.Hir.EffectAnalysis!.Program.Callables,
            candidate =>
                (candidate.DisplayName ?? candidate.Name) == callableName);
        return Assert.IsType<OpEffectSummary>(
            model.FindOpEffects(callable.Id));
    }

    private static QubitRef At(string register, int index) =>
        new(register, index);

    private static QubitRef Whole(string register) =>
        new(register, null);

    private static void AssertRefs(
        IReadOnlySet<QubitRef> actual,
        params QubitRef[] expected) =>
        Assert.True(
            actual.SetEquals(expected),
            $"expected {{{string.Join(", ", expected)}}}, "
            + $"got {{{string.Join(", ", actual)}}}");
}
