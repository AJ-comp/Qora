using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirWitnessAvailabilityAnalysisTests
{
    [Fact]
    public void ScalarReassignmentDoesNotDestroyTheExactForwardValue()
    {
        var (program, effects) = Compile("""
            operation FlipIf(flag: int, target: Qubit) {
                if (flag == 1) {
                    X(target);
                }
            }

            operation Main() {
                use q = Qubit[1];
                var flag: int = 1;
                FlipIf(flag, q[0]);
                flag = 0;
            }
            """);
        var main = Callable(program, "Main");
        var effect = EffectOf(effects, main);
        var analysis = MirWitnessAvailabilityAnalysis.Analyze(
            program,
            effects,
            main.Id);
        var result = analysis.CheckAtTerminator(
            effect.Site,
            ExitBlock(main).Id);

        Assert.True(result.AllWitnessesAvailable);
        Assert.False(result.RequiresIterationLocalPlacement);
        Assert.True(result.RequiresBoundsRevalidation);
    }

    [Fact]
    public void ConditionalEffectRetainsItsExactPathBit()
    {
        var (program, effects) = Compile("""
            operation Main() {
                use q = Qubit[1];
                var flag: int = 1;
                if (flag == 1) {
                    X(q[0]);
                }
                flag = 0;
            }
            """);
        var main = Callable(program, "Main");
        var effect = EffectOf(effects, main);
        var analysis = MirWitnessAvailabilityAnalysis.Analyze(
            program,
            effects,
            main.Id);
        var result = analysis.CheckAtTerminator(
            effect.Site,
            ExitBlock(main).Id);

        Assert.Equal(MirPathConditionKind.Predicate, effect.PathCondition.Kind);
        Assert.True(result.AllWitnessesAvailable);
        var rematerialized = Assert.Single(result.Rematerializations);
        Assert.Equal(MirScalarValueAvailabilityKind.Rematerializable, rematerialized.Kind);
        Assert.NotEmpty(rematerialized.Recipe);
    }

    [Fact]
    public void ArrayMutationReportsTheExactUnavailableMemoryState()
    {
        var (program, effects) = Compile("""
            operation Observe(values: int[], target: Qubit) {
                if (values[0] == 1) {
                    X(target);
                }
            }

            operation Main() {
                use q = Qubit[1];
                var values: int[] = [1];
                Observe(values, q[0]);
                values[0] = 2;
            }
            """);
        var main = Callable(program, "Main");
        var effect = EffectOf(effects, main);
        var analysis = MirWitnessAvailabilityAnalysis.Analyze(
            program,
            effects,
            main.Id);
        var result = analysis.CheckAtTerminator(
            effect.Site,
            ExitBlock(main).Id);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(MirWitnessIssueKind.ArrayStateUnavailable, issue.Kind);
        var memory = Assert.IsType<MirMemoryStateAvailability>(issue.Memory);
        Assert.Equal(MirMemoryStateAvailabilityKind.Clobbered, memory.Kind);
        Assert.Equal(
            MirMemoryMutationKind.ArrayStore,
            Assert.Single(memory.ClobberingMutations).Kind);
    }

    [Fact]
    public void LoopEffectRequiresIterationLocalPlacement()
    {
        var (program, effects) = Compile("""
            operation Main() {
                use q = Qubit[1];
                var index: int = 0;
                while (index < 1) {
                    X(q[0]);
                    index = index + 1;
                }
            }
            """);
        var main = Callable(program, "Main");
        var effect = EffectOf(effects, main);
        var analysis = MirWitnessAvailabilityAnalysis.Analyze(
            program,
            effects,
            main.Id);
        var result = analysis.CheckAtTerminator(
            effect.Site,
            ExitBlock(main).Id);

        Assert.True(result.RequiresIterationLocalPlacement);
    }

    [Fact]
    public void MeasurementDependentPathCannotBeRematerializedOutsideItsBranch()
    {
        var (program, effects) = Compile("""
            operation Main() {
                use q = Qubit[2];
                var outer: int = 1;
                if (outer == 1) {
                    var measured: bit = M(q[0]);
                    if (measured == 1) {
                        X(q[1]);
                    }
                }
            }
            """);
        var main = Callable(program, "Main");
        var effect = Assert.Single(
            effects.Effects,
            candidate => candidate.Site.Callable == main.Id
                && candidate.Target?.DisplayName == "X");
        var analysis = MirWitnessAvailabilityAnalysis.Analyze(
            program,
            effects,
            main.Id);
        var result = analysis.CheckAtTerminator(
            effect.Site,
            ExitBlock(main).Id);

        Assert.Contains(
            result.Issues,
            issue => issue.Kind == MirWitnessIssueKind.PathPredicateUnavailable);
    }

    [Fact]
    public void UnchangedArrayLoadCanRematerializeABranchCondition()
    {
        var (program, effects) = Compile("""
            operation Main() {
                use q = Qubit[1];
                var values: int[] = [1];
                var outer: int = 1;
                if (outer == 1) {
                    if (values[0] == 1) {
                        X(q[0]);
                    }
                }
            }
            """);
        var main = Callable(program, "Main");
        var effect = EffectOf(effects, main);
        var analysis = MirWitnessAvailabilityAnalysis.Analyze(
            program,
            effects,
            main.Id);
        var result = analysis.CheckAtTerminator(
            effect.Site,
            ExitBlock(main).Id);

        Assert.True(result.AllWitnessesAvailable);
        Assert.Contains(
            result.Rematerializations.SelectMany(value => value.Recipe),
            instruction =>
                main.Blocks.SelectMany(block => block.Instructions)
                    .OfType<MirArrayLoad>()
                    .Any(load => load.Id == instruction));
    }

    private static (MirProgram Program, MirEffectSnapshot Effects) Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
        return (
            Assert.IsType<MirProgram>(result.Mir?.Program),
            Assert.IsType<MirEffectSnapshot>(result.Mir!.Analyses.Effects));
    }

    private static MirCallable Callable(MirProgram program, string name) =>
        Assert.Single(program.Callables, callable => callable.Name == name);

    private static MirQuantumInstructionEffect EffectOf(
        MirEffectSnapshot effects,
        MirCallable callable) =>
        Assert.Single(
            effects.Effects,
            effect => effect.Site.Callable == callable.Id);

    private static MirBlock ExitBlock(MirCallable callable) =>
        Assert.Single(
            callable.Blocks,
            block => block.Terminator is MirReturn);
}
