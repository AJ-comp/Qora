using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirBoundsAnalysisTests
{
    [Fact]
    public void KnownClassicalArrayLengthClassifiesConstantAndDynamicIndexes()
    {
        var proven = CompileMir("""
            operation Main() {
                var values: int[] = [10, 20];
                var value: int = values[1];
            }
            """);
        var provenMain = Callable(proven.Program, "Main");
        var provenResult = Assert.Single(proven.Analyses.Bounds(provenMain).Results);

        Assert.Equal(2, provenResult.KnownLength);
        Assert.Equal(MirBoundsClassification.Proven, provenResult.Classification);

        var unproven = CompileMir("""
            function chooseIndex(): int {
                return 0;
            }

            operation Main() {
                var values: int[] = [10, 20];
                var index: int = chooseIndex();
                var value: int = values[index];
            }
            """);
        var unprovenMain = Callable(unproven.Program, "Main");
        var unprovenResult = Assert.Single(unproven.Analyses.Bounds(unprovenMain).Results);

        Assert.Equal(2, unprovenResult.KnownLength);
        Assert.Equal(MirBoundsClassification.Unproven, unprovenResult.Classification);
    }

    [Fact]
    public void ConstantOutsideKnownLengthIsInvalidInMir()
    {
        var source = CompileMir("""
            operation Main() {
                var values: int[] = [10, 20];
                var value: int = values[1];
            }
            """);
        var main = Callable(source.Program, "Main");
        var load = Assert.Single(Instructions(main).OfType<MirArrayLoad>());
        var index = main.RequireValue(load.Index);
        var indexInstruction = Assert.IsType<MirConstant>(
            main.RequireInstruction(main.DefinitionOf(index.Id).Instruction!.Value));
        var rewritten = RewriteInstruction(
            source.Program,
            main,
            indexInstruction with { Text = "2" });

        var analyses = new MirAnalysisStore(rewritten);
        var rewrittenMain = rewritten.RequireCallable(main.Id);
        var result = Assert.Single(analyses.Bounds(rewrittenMain).Results);

        Assert.Equal(MirBoundsClassification.Invalid, result.Classification);
    }

    [Fact]
    public void PotentiallyOverflowingIndexArithmeticStaysUnproven()
    {
        var source = CompileMir("""
            operation Main() {
                var values: int[] = [10, 20];
                var value: int = values[values.Count - 1];
            }
            """);
        var main = Callable(source.Program, "Main");
        var load = Assert.Single(Instructions(main).OfType<MirArrayLoad>());
        var subtraction = Assert.IsType<MirBinary>(
            main.RequireInstruction(
                main.DefinitionOf(load.Index).Instruction!.Value));
        var subtrahend = Assert.IsType<MirConstant>(
            main.RequireInstruction(
                main.DefinitionOf(subtraction.Right).Instruction!.Value));
        var rewritten = RewriteInstruction(
            source.Program,
            main,
            subtrahend with { Text = long.MinValue.ToString() });

        var analyses = new MirAnalysisStore(rewritten);
        var rewrittenMain = rewritten.RequireCallable(main.Id);
        var result = Assert.Single(analyses.Bounds(rewrittenMain).Results);

        Assert.Equal(MirBoundsClassification.Unproven, result.Classification);
    }

    [Fact]
    public void BranchGuardProvesBothBoundsForTheExactSsaIndex()
    {
        var mir = CompileMir("""
            function chooseIndex(): int {
                return 0;
            }

            operation Main() {
                var values: int[] = [10, 20];
                var index: int = chooseIndex();
                if (0 <= index && index < values.Count) {
                    var value: int = values[index];
                }
            }
            """);
        var main = Callable(mir.Program, "Main");

        var result = Assert.Single(mir.Analyses.Bounds(main).Results);

        Assert.Equal(MirBoundsClassification.Proven, result.Classification);
    }

    [Fact]
    public void BranchKnownToBeBelowZeroIsInvalid()
    {
        var mir = CompileMirIgnoringDiagnostics("""
            function chooseIndex(): int {
                return 0;
            }

            operation Main() {
                var values: int[] = [10, 20];
                var index: int = chooseIndex();
                if (index < 0) {
                    var value: int = values[index];
                }
            }
            """);
        var main = Callable(mir.Program, "Main");

        var result = Assert.Single(mir.Analyses.Bounds(main).Results);

        Assert.Equal(MirBoundsClassification.Invalid, result.Classification);
    }

    [Fact]
    public void AccessOnAnImpossiblePathIsVacuouslyProven()
    {
        var source = CompileMir("""
            operation Main() {
                var values: int[] = [10];
                if (1 == 0) {
                    var value: int = values[0];
                }
            }
            """);
        var main = Callable(source.Program, "Main");
        var load = Assert.Single(Instructions(main).OfType<MirArrayLoad>());
        var index = main.RequireValue(load.Index);
        var indexInstruction = Assert.IsType<MirConstant>(
            main.RequireInstruction(main.DefinitionOf(index.Id).Instruction!.Value));
        var rewritten = RewriteInstruction(
            source.Program,
            main,
            indexInstruction with { Text = "9" });

        var analyses = new MirAnalysisStore(rewritten);
        var rewrittenMain = rewritten.RequireCallable(main.Id);
        var result = Assert.Single(analyses.Bounds(rewrittenMain).Results);

        Assert.Equal(MirBoundsClassification.Proven, result.Classification);
    }

    [Fact]
    public void ParameterMinimumLengthProvesTheAccessThatEstablishedItsContract()
    {
        var mir = CompileMir("""
            function first(values: int[]): int {
                return values[0];
            }

            operation Main() {
                var values: int[] = [10];
                var value: int = first(values);
            }
            """);
        var first = Callable(mir.Program, "first");
        var parameter = Assert.Single(first.Parameters.OfType<MirClassicalParameter>());

        var result = Assert.Single(mir.Analyses.Bounds(first).Results);

        Assert.Equal(1, parameter.MinimumLength);
        Assert.Null(result.KnownLength);
        Assert.Equal(MirBoundsClassification.Proven, result.Classification);
    }

    [Fact]
    public void IndependentArrayPhisDoNotShareLengthIdentityFromTheSameProvenanceSet()
    {
        var context = MirTestContext.Create();
        var origin = context.Origin();
        var callableId = new MirCallableId(0);
        var entryCallableId = new MirCallableId(1);
        var entryBlockId = MirTestContext.BlockId(0);
        var trueBlockId = MirTestContext.BlockId(1);
        var falseBlockId = MirTestContext.BlockId(2);
        var joinBlockId = MirTestContext.BlockId(3);
        var firstParameterValueId = new MirValueId(0);
        var secondParameterValueId = new MirValueId(1);
        var conditionValueId = new MirValueId(2);
        var firstPhiValueId = new MirValueId(3);
        var secondPhiValueId = new MirValueId(4);
        var lengthValueId = new MirValueId(5);
        var oneValueId = new MirValueId(6);
        var indexValueId = new MirValueId(7);
        var loadedValueId = new MirValueId(8);
        var firstStorageId = new MirStorageId(0);
        var secondStorageId = new MirStorageId(1);
        var lengthInstructionId = new MirInstructionId(0);
        var oneInstructionId = new MirInstructionId(1);
        var indexInstructionId = new MirInstructionId(2);
        var loadInstructionId = new MirInstructionId(3);
        var arrayType = MirType.Array(QType.Int);
        var intType = MirType.Scalar(QType.Int);
        var firstStorage = new MirArrayStorage(firstStorageId, "first", origin);
        var secondStorage = new MirArrayStorage(secondStorageId, "second", origin);
        var firstParameterValue = new MirValue(firstParameterValueId, arrayType, origin);
        var secondParameterValue = new MirValue(secondParameterValueId, arrayType, origin);
        var conditionValue = new MirValue(
            conditionValueId,
            MirType.Scalar(QType.Bit),
            origin);
        var firstPhiValue = new MirValue(firstPhiValueId, arrayType, origin);
        var secondPhiValue = new MirValue(secondPhiValueId, arrayType, origin);
        var lengthValue = new MirValue(lengthValueId, intType, origin);
        var oneValue = new MirValue(oneValueId, intType, origin);
        var indexValue = new MirValue(indexValueId, intType, origin);
        var loadedValue = new MirValue(loadedValueId, intType, origin);
        var workerEntryBlock = new MirBlock(
            entryBlockId,
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirBranch(
                conditionValueId,
                trueBlockId,
                Array.Empty<MirValueId>(),
                falseBlockId,
                Array.Empty<MirValueId>(),
                origin),
            origin);

        var callable = new MirCallable(
            callableId,
            "Worker",
            returnType: null,
            parameters: new IMirParameter[]
            {
                MirClassicalParameter.Array(
                    "first",
                    firstParameterValue,
                    firstStorage,
                    minimumLength: 1),
                MirClassicalParameter.Array(
                    "second",
                    secondParameterValue,
                    secondStorage,
                    minimumLength: 1),
                MirClassicalParameter.Scalar("condition", conditionValue),
            },
            entryBlock: workerEntryBlock,
            blocks: new[]
            {
                workerEntryBlock,
                new MirBlock(
                    trueBlockId,
                    Array.Empty<MirValue>(),
                    Array.Empty<MirInstruction>(),
                    new MirJump(
                        joinBlockId,
                        new[] { firstParameterValueId, secondParameterValueId },
                        origin),
                    origin),
                new MirBlock(
                    falseBlockId,
                    Array.Empty<MirValue>(),
                    Array.Empty<MirInstruction>(),
                    new MirJump(
                        joinBlockId,
                        new[] { secondParameterValueId, firstParameterValueId },
                        origin),
                    origin),
                new MirBlock(
                    joinBlockId,
                    new[] { firstPhiValue, secondPhiValue },
                    new MirInstruction[]
                    {
                        new MirArrayLength(
                            lengthInstructionId,
                            lengthValue,
                            firstPhiValueId,
                            origin),
                        new MirConstant(oneInstructionId, oneValue, "1", origin),
                        new MirBinary(
                            indexInstructionId,
                            indexValue,
                            MirBinaryOperator.Subtract,
                            lengthValueId,
                            oneValueId,
                            origin),
                        new MirArrayLoad(
                            loadInstructionId,
                            loadedValue,
                            secondPhiValueId,
                            indexValueId,
                            origin),
                    },
                    new MirReturn(null, origin),
                    origin),
            },
            origin);
        var mainEntryBlock = new MirBlock(
            entryBlockId,
            Array.Empty<MirValue>(),
            Array.Empty<MirInstruction>(),
            new MirReturn(null, origin),
            origin);
        var entryCallable = new MirCallable(
            entryCallableId,
            "Main",
            returnType: null,
            parameters: Array.Empty<IMirParameter>(),
            entryBlock: mainEntryBlock,
            blocks: new[] { mainEntryBlock },
            origin);
        var program = context.Program(entryCallable, new[] { callable, entryCallable });

        var analyses = new MirAnalysisStore(program);
        var result = Assert.Single(analyses.Bounds(callable).Results);

        Assert.Equal(MirBoundsClassification.Unproven, result.Classification);
    }

    [Fact]
    public void BranchPhiWithOnlySafeIncomingConstantsIsProven()
    {
        var mir = CompileMir("""
            function chooseFlag(): bit {
                return 0;
            }

            operation Main() {
                var values: int[] = [10, 20];
                var index: int = 0;
                var flag: bit = chooseFlag();
                if (flag == 1) {
                    index = 1;
                }
                var value: int = values[index];
            }
            """);
        var main = Callable(mir.Program, "Main");

        var result = Assert.Single(mir.Analyses.Bounds(main).Results);

        Assert.Equal(MirBoundsClassification.Proven, result.Classification);
    }

    [Fact]
    public void AscendingLoopPhiAndHeaderGuardProveKnownArrayBounds()
    {
        var mir = CompileMir("""
            operation Main() {
                var values: int[] = [10, 20, 30];
                for index in 0..values.Count - 1 {
                    var value: int = values[index];
                }
            }
            """);
        var main = Callable(mir.Program, "Main");

        var result = Assert.Single(mir.Analyses.Bounds(main).Results);

        Assert.Equal(MirBoundsClassification.Proven, result.Classification);
    }

    [Fact]
    public void ShortCircuitGuardContainingMeasurementProvesTheAccess()
    {
        var mir = CompileMirIgnoringDiagnostics("""
            operation Main() {
                use q = Qubit[2];
                H(q[0]);
                var measured: bit = M(q[0]);
                var index: int = measured;
                var values: int[] = [10, 20, 30];
                if (M(q[1]) == 1 && 0 <= index && index < values.Count) {
                    values[index] = 1;
                }
            }
            """);

        AssertAllBoundsProven(mir, "Main");
    }

    [Fact]
    public void ConstantLoopBoundUsesTheArrayParameterMinimumLength()
    {
        var mir = CompileMirIgnoringDiagnostics("""
            operation Fill(var values: int[]) {
                for index in 0..5 {
                    values[index] = 1;
                }
            }

            operation Main() {
                var values: int[] = [1, 2, 3, 4, 5, 6];
                Fill(var values);
            }
            """);

        AssertAllBoundsProven(mir, "Fill");
    }

    [Fact]
    public void NestedShadowingLoopUsesTheInnerInductionBounds()
    {
        var mir = CompileMirIgnoringDiagnostics("""
            operation Fill(var values: int[]) {
                for index in 0..5 {
                    for index in 0..1 {
                        values[index] = 1;
                    }
                }
            }

            operation Main() {
                var values: int[] = [1, 2];
                Fill(var values);
            }
            """);

        AssertAllBoundsProven(mir, "Fill");
    }

    [Fact]
    public void SpecializedQubitCountLoopUsesTheClassicalParameterContract()
    {
        var mir = CompileMirIgnoringDiagnostics("""
            operation Fill(q: Qubit[], var values: int[]) {
                const last: int = q.Count;
                for index in 0..last {
                    values[index] = 1;
                }
            }

            operation Main() {
                use q = Qubit[3];
                var values: int[] = [1, 2, 3, 4, 5];
                Fill(q, var values);
            }
            """);

        AssertAllBoundsProven(mir, "Fill__sz3");
    }

    [Fact]
    public void OneQuantumInstructionRetainsSeparateIndexedOperandSites()
    {
        var mir = CompileMir("""
            function chooseLeftIndex(): int {
                return 0;
            }

            function chooseRightIndex(): int {
                return 0;
            }

            operation Main() {
                use left = Qubit[2];
                use right = Qubit[3];
                var leftIndex: int = chooseLeftIndex();
                var rightIndex: int = chooseRightIndex();
                CNOT(left[leftIndex], right[rightIndex]);
            }
            """);
        var main = Callable(mir.Program, "Main");
        var apply = Assert.Single(Instructions(main).OfType<MirQuantumApply>());

        var bounds = mir.Analyses.Bounds(main);
        var results = bounds.Results
            .Where(result => result.Site.Instruction.Instruction == apply.Id)
            .OrderBy(result => result.Site.OperandIndex)
            .ToArray();

        Assert.Equal(2, results.Length);
        Assert.Equal(0, results[0].Site.OperandIndex);
        Assert.Equal(2, results[0].KnownLength);
        Assert.Equal(MirBoundsClassification.Unproven, results[0].Classification);
        Assert.Equal(1, results[1].Site.OperandIndex);
        Assert.Equal(3, results[1].KnownLength);
        Assert.Equal(MirBoundsClassification.Unproven, results[1].Classification);
        Assert.Same(apply.QubitAccesses[0].Origin, bounds.OriginFor(results[0]));
        Assert.Same(apply.QubitAccesses[1].Origin, bounds.OriginFor(results[1]));
    }

    [Fact]
    public void BoundsResultsAreCanonicalWithinOneMirSnapshot()
    {
        var mir = CompileMir("""
            operation Main() {
                var values: int[] = [10];
                var value: int = values[0];
            }
            """);
        var main = Callable(mir.Program, "Main");

        var first = mir.Analyses.Bounds(main);
        var second = mir.Analyses.Bounds(main);
        var result = Assert.Single(first.Results);

        Assert.Same(first, second);
        Assert.Same(result, first.ResultFor(result.Site));
        var instruction = main.RequireInstruction(result.Site.Instruction.Instruction);
        Assert.Same(instruction.Origin, first.OriginFor(result));
        Assert.Throws<ArgumentException>(
            () => first.OriginFor(result with
            {
                Site = new MirIndexedAccessSite(
                    result.Site.Instruction,
                    result.Site.OperandIndex + 1),
            }));
    }

    private static MirSnapshot CompileMir(string source)
    {
        var result = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));
        Assert.True(
            result.Succeeded,
            string.Join(
                " | ",
                result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return Assert.IsType<MirSnapshot>(result.Mir);
    }

    private static MirSnapshot CompileMirIgnoringDiagnostics(string source)
    {
        var result = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));
        return Assert.IsType<MirSnapshot>(result.Mir);
    }

    private static MirCallable Callable(MirProgram program, string name) =>
        Assert.Single(program.Callables, callable => callable.Name == name);

    private static void AssertAllBoundsProven(MirSnapshot mir, string callableName)
    {
        var callable = Callable(mir.Program, callableName);
        var bounds = mir.Analyses.Bounds(callable);
        Assert.True(
            bounds.Results.All(result =>
                result.Classification == MirBoundsClassification.Proven),
            MirPrinter.Print(mir.Program));
    }

    private static IEnumerable<MirInstruction> Instructions(MirCallable callable) =>
        callable.Blocks.SelectMany(block => block.Instructions);

    private static MirProgram RewriteInstruction(
        MirProgram program,
        MirCallable callable,
        MirInstruction replacement)
    {
        var blocks = new List<MirBlock>(callable.Blocks.Count);
        MirBlock? rewrittenEntryBlock = null;
        foreach (var block in callable.Blocks)
        {
            var instructions = block.Instructions
                .Select(instruction => instruction.Id == replacement.Id
                    ? replacement
                    : instruction)
                .ToArray();
            var rewrittenBlock = block with { Instructions = instructions };
            blocks.Add(rewrittenBlock);

            if (ReferenceEquals(block, callable.EntryBlock))
                rewrittenEntryBlock = rewrittenBlock;
        }

        var rewrittenCallable = new MirCallable(
            callable.Id,
            callable.Name,
            callable.ReturnType,
            callable.Parameters,
            rewrittenEntryBlock
                ?? throw new InvalidOperationException(
                    "MIR rewrite dropped the callable entry block"),
            blocks,
            callable.Origin);
        var rewrittenCallables = program.Callables
            .Select(candidate => candidate.Id == callable.Id
                ? rewrittenCallable
                : candidate)
            .ToArray();
        var rewrittenEntryPoint = ReferenceEquals(program.EntryPoint, callable)
            ? rewrittenCallable
            : program.EntryPoint;
        return new MirProgram(rewrittenEntryPoint, rewrittenCallables);
    }
}
