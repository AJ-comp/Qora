using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirMemoryStateAnalysisTests
{
    [Fact]
    public void LaterStoreKillsTheEarlierArrayStateButNotItsOwnResult()
    {
        var program = CompileMir("""
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
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var oldState = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        var store = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirArrayStore>());
        var analysis = MirMemoryStateAnalysis.Analyze(program, main.Id);
        var exit = ExitBlock(main);

        var oldAvailability = analysis.CheckAtTerminator(oldState, exit.Id);
        Assert.Equal(
            MirMemoryStateAvailabilityKind.Clobbered,
            oldAvailability.Kind);
        Assert.Equal(
            store.Id,
            Assert.Single(oldAvailability.ClobberingMutations).Instruction);

        var currentAvailability = analysis.CheckAtTerminator(store.Result, exit.Id);
        Assert.True(currentAvailability.IsAvailable);
    }

    [Fact]
    public void StoreToDistinctLocalStorageDoesNotKillReadOnlyWitness()
    {
        var program = CompileMir("""
            operation Observe(values: int[], target: Qubit) {
                if (values[0] == 1) {
                    X(target);
                }
            }

            operation Main() {
                use q = Qubit[1];
                var left: int[] = [1];
                var right: int[] = [2];
                Observe(left, q[0]);
                right[0] = 3;
            }
            """);
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var state = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        var analysis = MirMemoryStateAnalysis.Analyze(program, main.Id);

        Assert.True(
            analysis.CheckAtTerminator(state, ExitBlock(main).Id).IsAvailable);
    }

    [Fact]
    public void MutationOnOneBranchMakesOldStateUnavailableAfterMerge()
    {
        var program = CompileMir("""
            operation Observe(values: int[], target: Qubit) {
                if (values[0] == 1) {
                    X(target);
                }
            }

            operation Main() {
                use q = Qubit[1];
                var values: int[] = [1];
                Observe(values, q[0]);
                if (1 == 1) {
                    values[0] = 2;
                }
                H(q[0]);
            }
            """);
        var main = Callable(program, "Main");
        var calls = main.Blocks.SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .ToArray();
        var observe = Assert.Single(
            calls,
            call => call.Target is MirUserCallableTarget);
        var state = Assert.IsType<MirClassicalCallOperand>(observe.Operands[0]).Value;
        var later = Assert.Single(
            calls,
            call => call.Target.DisplayName == "H");
        var analysis = MirMemoryStateAnalysis.Analyze(program, main.Id);

        Assert.Equal(
            MirMemoryStateAvailabilityKind.Clobbered,
            analysis.CheckBeforeInstruction(state, later.Id).Kind);
    }

    [Fact]
    public void MutableCallKillsItsInputStateAndDefinesACurrentOutputState()
    {
        var program = CompileMir("""
            operation Touch(var values: int[], target: Qubit) {
                values[0] = values[0] + 1;
                X(target);
            }

            operation Main() {
                use q = Qubit[1];
                var values: int[] = [1];
                Touch(var values, q[0]);
            }
            """);
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var input = Assert.IsType<MirClassicalCallOperand>(call.Operands[0]).Value;
        var output = Assert.Single(call.MutableArrayResults).Result;
        var analysis = MirMemoryStateAnalysis.Analyze(program, main.Id);
        var exit = ExitBlock(main);

        var killed = analysis.CheckAtTerminator(input, exit.Id);
        Assert.Equal(MirMemoryStateAvailabilityKind.Clobbered, killed.Kind);
        Assert.Equal(
            MirMemoryMutationKind.MutableCall,
            Assert.Single(killed.ClobberingMutations).Kind);
        Assert.True(analysis.CheckAtTerminator(output, exit.Id).IsAvailable);
    }

    [Fact]
    public void OneCallCanIndependentlyTransitionSeveralMutableArrayStates()
    {
        var program = CompileMir("""
            operation Touch(var left: int[], var right: int[], target: Qubit) {
                left[0] = left[0] + 1;
                right[0] = right[0] + 1;
                X(target);
            }

            operation Main() {
                use q = Qubit[1];
                var left: int[] = [1];
                var right: int[] = [2];
                Touch(var left, var right, q[0]);
            }
            """);
        var main = Callable(program, "Main");
        var call = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var inputs = call.Operands
            .OfType<MirClassicalCallOperand>()
            .Select(operand => operand.Value)
            .ToArray();
        var outputs = call.MutableArrayResults
            .Select(result => result.Result)
            .ToArray();
        var analysis = MirMemoryStateAnalysis.Analyze(program, main.Id);
        var exit = ExitBlock(main);

        Assert.Equal(2, inputs.Length);
        Assert.Equal(2, outputs.Length);
        var unavailableInputs = inputs
            .Select(input => analysis.CheckAtTerminator(input, exit.Id))
            .ToArray();
        Assert.All(
            unavailableInputs,
            availability => Assert.Equal(
                MirMemoryStateAvailabilityKind.Clobbered,
                availability.Kind));
        Assert.All(
            outputs,
            output => Assert.True(
                analysis.CheckAtTerminator(output, exit.Id).IsAvailable));
        Assert.Equal(
            new int?[] { 0, 1 },
            unavailableInputs
                .SelectMany(availability => availability.ClobberingMutations)
                .Where(mutation => mutation.Instruction == call.Id)
                .Select(mutation => mutation.OperandIndex)
                .OrderBy(index => index)
                .ToArray());
    }

    [Fact]
    public void LoopHeaderPhiIsCurrentButMarkedAsIterationSensitive()
    {
        var program = CompileMir("""
            operation Main() {
                var values: int[] = [0];
                var index: int = 0;
                while (index < 2) {
                    values[0] = index;
                    index = index + 1;
                }
            }
            """);
        var main = Callable(program, "Main");
        var headerState = Assert.Single(
            main.Values,
            value => value.Type.IsArray
                && value.Definition.Kind == MirValueDefinitionKind.BlockArgument);
        var exit = Assert.Single(
            main.Blocks,
            block => block.Terminator is MirReturn);
        var analysis = MirMemoryStateAnalysis.Analyze(program, main.Id);
        var availability = analysis.CheckAtTerminator(
            headerState.Id,
            exit.Id);

        Assert.True(availability.IsAvailable);
        Assert.True(availability.RequiresSameIteration);
    }

    [Fact]
    public void VerifierRejectsAStaleArrayStatePassedIntoBranchMergePhi()
    {
        var program = CompileMir("""
            operation Main() {
                var values: int[] = [0];
                if (1 == 1) {
                    values[0] = 1;
                }
            }
            """);
        var main = Callable(program, "Main");
        var store = Assert.Single(
            main.Blocks.SelectMany(block => block.Instructions)
                .OfType<MirArrayStore>());
        var merge = Assert.Single(
            main.Blocks,
            block => block.Arguments.Any(
                argument => main.RequireValue(argument).Type.IsArray));
        var argumentIndex = merge.Arguments
            .Select((argument, index) => (argument, index))
            .Single(item => main.RequireValue(item.argument).Type.IsArray)
            .index;
        var storeBlock = Assert.Single(
            main.Blocks,
            block => block.Instructions.Any(
                instruction => instruction.Id == store.Id));
        var jump = Assert.IsType<MirJump>(storeBlock.Terminator);
        Assert.Equal(merge.Id, jump.Target);
        Assert.Equal(store.Result, jump.Arguments[argumentIndex]);

        var staleJump = jump with
        {
            Arguments = jump.Arguments
                .Select((value, index) => index == argumentIndex
                    ? store.Array
                    : value)
                .ToArray(),
        };
        var malformed = RewriteProgram(
            program,
            main,
            storeBlock with { Terminator = staleJump });

        Assert.Contains(
            QoraMirVerifier.Verify(malformed),
            error => error.Code == "MIR141"
                && error.Message.Contains("memory Phi"));
    }

    [Fact]
    public void VerifierRejectsAStaleArrayStateOnALoopBackedge()
    {
        var program = CompileMir("""
            operation Main() {
                var values: int[] = [0];
                var index: int = 0;
                while (index < 2) {
                    values[0] = index;
                    index = index + 1;
                }
            }
            """);
        var main = Callable(program, "Main");
        var header = Assert.Single(
            main.Blocks,
            block => block.Arguments.Any(
                argument => main.RequireValue(argument).Type.IsArray));
        var argumentIndex = header.Arguments
            .Select((argument, index) => (argument, index))
            .Single(item => main.RequireValue(item.argument).Type.IsArray)
            .index;
        var headerState = header.Arguments[argumentIndex];
        var cfg = MirControlFlowAnalysis.Analyze(program, main.Id);
        var backedge = Assert.Single(
            main.Blocks,
            block => block.Id != header.Id
                && block.Terminator is MirJump jump
                && jump.Target == header.Id
                && cfg.CanReach(header.Id, block.Id));
        var jump = Assert.IsType<MirJump>(backedge.Terminator);
        Assert.NotEqual(headerState, jump.Arguments[argumentIndex]);

        var staleJump = jump with
        {
            Arguments = jump.Arguments
                .Select((value, index) => index == argumentIndex
                    ? headerState
                    : value)
                .ToArray(),
        };
        var malformed = RewriteProgram(
            program,
            main,
            backedge with { Terminator = staleJump });

        Assert.Contains(
            QoraMirVerifier.Verify(malformed),
            error => error.Code == "MIR141"
                && error.Message.Contains("memory Phi"));
    }

    private static MirProgram CompileMir(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(
            result.Succeeded,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList().Select(error => $"{error.Code}: {error.Message}")));
        return Assert.IsType<MirProgram>(result.Mir?.Program);
    }

    private static MirCallable Callable(MirProgram program, string name) =>
        Assert.Single(program.Callables, callable => callable.Name == name);

    private static MirBlock ExitBlock(MirCallable callable) =>
        Assert.Single(
            callable.Blocks,
            block => block.Terminator is MirReturn);

    private static MirProgram RewriteProgram(
        MirProgram program,
        MirCallable callable,
        MirBlock replacement)
    {
        var rewrittenBlocks = callable.Blocks
            .Select(block => block.Id == replacement.Id
                ? replacement
                : block)
            .ToArray();
        var rewrittenEntryBlock = Assert.Single(
            rewrittenBlocks,
            block => block.Id == callable.EntryBlock.Id);
        var rewrittenCallable = new MirCallable(
            callable.Id,
            callable.Name,
            callable.ReturnType,
            callable.Parameters,
            rewrittenEntryBlock,
            rewrittenBlocks,
            callable.Values,
            callable.Storages,
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
