using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirPathConditionAnalysisTests
{
    [Fact]
    public void QuantumEffectInsideTrueArmKeepsTheExactBranchCondition()
    {
        var program = CompileMir("""
            operation Main() {
                use q = Qubit[1];
                var flag: int = 1;
                if (flag == 1) {
                    X(q[0]);
                }
                flag = 0;
            }
            """);
        var callable = Assert.Single(program.Callables);
        var applySite = ApplySite(callable);
        var branch = Assert.IsType<MirBranch>(
            Assert.Single(
                callable.Blocks,
                block => block.Terminator is MirBranch).Terminator);
        var paths = MirPathConditionAnalysis.Analyze(program, callable.Id);

        var condition = paths.ConditionFor(applySite.Block);
        Assert.Equal(MirPathConditionKind.Predicate, condition.Kind);
        var predicate = Assert.IsType<MirPathPredicate>(condition.Predicate);
        Assert.Equal(branch.Condition, predicate.Condition);
        Assert.True(predicate.ExpectedValue);
        Assert.Equal(branch.TrueTarget, predicate.TakenSuccessor);
        Assert.Equal(MirExecutionMultiplicity.Single, paths.MultiplicityOf(applySite.Block));
    }

    [Fact]
    public void QuantumEffectInsideElseArmKeepsFalsePolarity()
    {
        var program = CompileMir("""
            operation Main() {
                use q = Qubit[1];
                if (1 == 2) {
                    H(q[0]);
                } else {
                    X(q[0]);
                }
            }
            """);
        var callable = Assert.Single(program.Callables);
        var x = Assert.Single(
            callable.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            apply => apply.Target.DisplayName == "X");
        var site = Assert.Single(
            callable.Blocks,
            block => block.Instructions.Contains(x));
        var paths = MirPathConditionAnalysis.Analyze(program, callable.Id);

        var condition = paths.ConditionFor(site.Id);
        Assert.Equal(MirPathConditionKind.Predicate, condition.Kind);
        Assert.False(Assert.IsType<MirPathPredicate>(condition.Predicate).ExpectedValue);
    }

    [Fact]
    public void EffectAfterMergeDoesNotInheritEitherArmPredicate()
    {
        var program = CompileMir("""
            operation Main() {
                use q = Qubit[1];
                var flag: int = 0;
                if (flag == 0) {
                    flag = 1;
                } else {
                    flag = 2;
                }
                X(q[0]);
            }
            """);
        var callable = Assert.Single(program.Callables);
        var site = ApplySite(callable);
        var paths = MirPathConditionAnalysis.Analyze(program, callable.Id);

        Assert.True(paths.ConditionFor(site.Block).IsAlways);
    }

    [Fact]
    public void NestedArmsAccumulateAllNecessaryPredicates()
    {
        var program = CompileMir("""
            operation Main() {
                use q = Qubit[1];
                var left: int = 1;
                var right: int = 2;
                if (left == 1) {
                    if (right == 2) {
                        X(q[0]);
                    }
                }
            }
            """);
        var callable = Assert.Single(program.Callables);
        var site = ApplySite(callable);
        var paths = MirPathConditionAnalysis.Analyze(program, callable.Id);

        var condition = paths.ConditionFor(site.Block);
        Assert.Equal(MirPathConditionKind.All, condition.Kind);
        var predicates = condition.Predicates;
        Assert.Equal(2, predicates.Count);
        Assert.All(predicates, predicate => Assert.True(predicate.ExpectedValue));
        Assert.Equal(2, predicates.Select(predicate => predicate.Condition).Distinct().Count());
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);
        Assert.True(
            cfg.StrictlyDominates(
                predicates[0].Controller,
                predicates[1].Controller));
    }

    [Fact]
    public void LoopBodyIsMarkedLoopCarried()
    {
        var program = CompileMir("""
            operation Main() {
                use q = Qubit[1];
                var index: int = 0;
                while (index < 2) {
                    X(q[0]);
                    index = index + 1;
                }
            }
            """);
        var callable = Assert.Single(program.Callables);
        var site = ApplySite(callable);
        var paths = MirPathConditionAnalysis.Analyze(program, callable.Id);

        Assert.Equal(
            MirExecutionMultiplicity.LoopCarried,
            paths.MultiplicityOf(site.Block));
        var condition = paths.ConditionFor(site.Block);
        Assert.Equal(MirPathConditionKind.Predicate, condition.Kind);
        Assert.True(Assert.IsType<MirPathPredicate>(condition.Predicate).ExpectedValue);
    }

    [Fact]
    public void QuantumEffectFactsCarryPathConditionAndMultiplicity()
    {
        var result = Compiler.Compile("""
            operation Main() {
                use q = Qubit[1];
                var index: int = 0;
                while (index < 1) {
                    if (index == 0) {
                        X(q[0]);
                    }
                    index = index + 1;
                }
            }
            """);
        Assert.True(
            result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
        var program = Assert.IsType<MirProgram>(result.Mir);
        var effects = Assert.IsType<MirEffectSnapshot>(result.MirEffects);
        var callable = Assert.Single(program.Callables);
        var effect = Assert.Single(
            effects.Effects,
            candidate => candidate.Site.Callable == callable.Id);

        Assert.Equal(MirExecutionMultiplicity.LoopCarried, effect.ExecutionMultiplicity);
        Assert.Equal(MirPathConditionKind.All, effect.PathCondition.Kind);
        Assert.Equal(2, effect.PathCondition.Predicates.Count);
        Assert.All(effect.PathCondition.Predicates, predicate => Assert.True(predicate.ExpectedValue));
    }

    [Fact]
    public void OrTailMergeKeepsItsDisjunctiveCondition()
    {
        var program = OrTailMergeMir();
        var callable = Assert.Single(program.Callables);
        var site = ApplySite(callable);
        var cfg = MirControlFlowAnalysis.Analyze(program, callable.Id);
        var paths = MirPathConditionAnalysis.Analyze(program, callable.Id);

        // X executes either through the direct true edge (a), or through the false edge followed by
        // b's true edge (!a && b). A flat conjunction used to report no guard, which was false-safe.
        Assert.False(cfg.PostDominates(site.Block, callable.EntryBlock));
        var condition = paths.ConditionFor(site.Block);
        Assert.Equal(MirPathConditionKind.Any, condition.Kind);
        Assert.Equal(2, condition.Terms.Count);
        Assert.Contains(
            condition.Terms,
            term => term.Kind == MirPathConditionKind.Predicate);
        Assert.Contains(
            condition.Terms,
            term => term.Kind == MirPathConditionKind.All
                && term.Predicates.Count == 2);
        Assert.Equal(
            2,
            condition.Predicates
                .Select(predicate => predicate.Condition)
                .Distinct()
                .Count());

        var effect = Assert.Single(MirEffectAnalysis.Analyze(program).Effects);
        Assert.Equal(MirPathConditionKind.Any, effect.PathCondition.Kind);
        Assert.False(effect.PathCondition.IsAlways);
    }

    private static MirProgram OrTailMergeMir()
    {
        var source = MirSource.Synthetic(1);
        var callableId = new MirCallableId(0);
        var a = new MirValueId(0);
        var b = new MirValueId(1);
        var qubit = new MirQubitResourceId(0);
        var entry = new MirBlockId(0);
        var testB = new MirBlockId(1);
        var bypass = new MirBlockId(2);
        var effect = new MirBlockId(3);
        var apply = new MirQuantumApply(
            new MirInstructionId(0),
            new MirBuiltinGateTarget("X"),
            new MirCallOperand[]
            {
                new MirQubitCallOperand(new MirQubitPlace(qubit)),
            },
            Array.Empty<MirMutableArrayResult>(),
            Array.Empty<MirFunctor>(),
            source);

        return new MirProgram(
            Revision: 0,
            new[]
            {
                new MirCallable(
                    callableId,
                    SourceOperationId: 1,
                    Name: "TailMerge",
                    MirCallableKind.Operation,
                    ReturnType: null,
                    Parameters: new MirParameter[]
                    {
                        new MirClassicalParameter(
                            "a",
                            SourceSymbolId: null,
                            a,
                            MirType.Scalar(QType.Bit)),
                        new MirClassicalParameter(
                            "b",
                            SourceSymbolId: null,
                            b,
                            MirType.Scalar(QType.Bit)),
                        new MirQubitParameter(
                            "q",
                            SourceSymbolId: null,
                            qubit,
                            IsArray: false,
                            Length: null),
                    },
                    EntryBlock: entry,
                    Blocks: new[]
                    {
                        new MirBlock(
                            entry,
                            Array.Empty<MirBlockArgument>(),
                            Array.Empty<MirInstruction>(),
                            new MirBranch(
                                a,
                                effect,
                                Array.Empty<MirValueId>(),
                                testB,
                                Array.Empty<MirValueId>(),
                                source),
                            source),
                        new MirBlock(
                            testB,
                            Array.Empty<MirBlockArgument>(),
                            Array.Empty<MirInstruction>(),
                            new MirBranch(
                                b,
                                effect,
                                Array.Empty<MirValueId>(),
                                bypass,
                                Array.Empty<MirValueId>(),
                                source),
                            source),
                        new MirBlock(
                            bypass,
                            Array.Empty<MirBlockArgument>(),
                            Array.Empty<MirInstruction>(),
                            new MirReturn(Value: null, source),
                            source),
                        new MirBlock(
                            effect,
                            Array.Empty<MirBlockArgument>(),
                            new MirInstruction[] { apply },
                            new MirReturn(Value: null, source),
                            source),
                    },
                    Values: new[]
                    {
                        new MirValue(
                            a,
                            MirType.Scalar(QType.Bit),
                            MirValueDefinition.ParameterAt(0),
                            Source: source),
                        new MirValue(
                            b,
                            MirType.Scalar(QType.Bit),
                            MirValueDefinition.ParameterAt(1),
                            Source: source),
                    },
                    Storages: Array.Empty<MirArrayStorage>(),
                    Qubits: new[]
                    {
                        new MirQubitResource(
                            qubit,
                            "q",
                            MirQubitResourceKind.Parameter,
                            IsArray: false,
                            Length: null,
                            SourceSymbolId: null,
                            AllocationInstruction: null,
                            source),
                    },
                    source),
            });
    }

    private static MirProgram CompileMir(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));
        return Assert.IsType<MirProgram>(result.Mir);
    }

    private static (MirBlockId Block, MirQuantumApply Apply) ApplySite(MirCallable callable)
    {
        foreach (var block in callable.Blocks)
        {
            var apply = block.Instructions.OfType<MirQuantumApply>().SingleOrDefault();
            if (apply is not null)
                return (block.Id, apply);
        }

        throw new Xunit.Sdk.XunitException("no quantum apply in MIR callable");
    }
}
