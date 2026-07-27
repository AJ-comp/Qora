using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Qora.Ir.Mir.Analysis;

public enum MirMemoryMutationKind
{
    ArrayStore,
    MutableCall,
    OwnershipTransfer,
}

/// <summary>
/// One instruction which changes or consumes the physical contents behind an array-state value.
/// </summary>
public sealed record MirMemoryMutation(
    MirBlockRef Block,
    MirInstructionRef Instruction,
    MirMemoryMutationKind Kind,
    MirStorageProvenance Storage,
    int? OperandIndex = null);

public enum MirMemoryStateAvailabilityKind
{
    Available,
    UnreachablePoint,
    SsaValueUnavailable,
    UnknownStorageProvenance,
    Clobbered,
}

/// <summary>
/// Result of asking whether one exact array-state SSA version still denotes the runtime buffer contents
/// at a program point. <see cref="RequiresSameIteration"/> is separate from availability: a state defined
/// in a CFG cycle can be current at an iteration-local point, but a scheduler must not confuse different
/// dynamic executions of the same static ValueId.
/// </summary>
public sealed record MirMemoryStateAvailability(
    MirMemoryStateAvailabilityKind Kind,
    IReadOnlyList<MirMemoryMutation> ClobberingMutations,
    bool RequiresSameIteration)
{
    private IReadOnlyList<MirMemoryMutation> _clobberingMutations =
        MirCollections.Freeze(ClobberingMutations);

    public IReadOnlyList<MirMemoryMutation> ClobberingMutations
    {
        get => _clobberingMutations;
        init => _clobberingMutations = MirCollections.Freeze(value);
    }

    public bool IsAvailable => Kind == MirMemoryStateAvailabilityKind.Available;
}

/// <summary>
/// Path-sensitive memory-version facts for one callable. Unlike scalar SSA availability, this analysis
/// models destructive array storage: a store or mutable/moved call kills every older state whose symbolic
/// storage may alias the written region. Block arguments redefine memory Phi states at block entry, so
/// loop-carried versions are handled by a must-dataflow fixed point instead of source-order heuristics.
/// </summary>
public sealed class MirMemoryStateSnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly MirCallable _callable;
    private readonly MirControlFlowSnapshot _cfg;
    private readonly MirStorageProvenanceSnapshot _provenance;
    private readonly IReadOnlyList<MirMemoryMutation> _mutations;
    private readonly FrozenDictionary<MirInstructionId, IReadOnlyList<MirMemoryMutation>>
        _mutationsByInstruction;

    internal MirMemoryStateSnapshot(
        MirProgram sourceProgram,
        MirCallable callable,
        MirControlFlowSnapshot cfg,
        MirStorageProvenanceSnapshot provenance,
        IReadOnlyList<MirMemoryMutation> mutations)
    {
        _sourceProgram = sourceProgram;
        _callable = callable;
        _cfg = cfg;
        _provenance = provenance;
        SnapshotId = sourceProgram.SnapshotId;
        Callable = new MirCallableRef(SnapshotId, callable.Id);
        _mutations = MirCollections.Freeze(mutations);
        _mutationsByInstruction = mutations
            .GroupBy(mutation => mutation.Instruction.Instruction)
            .ToFrozenDictionary(
                group => group.Key,
                group => MirCollections.Freeze(group));
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    public IReadOnlyList<MirMemoryMutation> Mutations => _mutations;
    internal MirControlFlowSnapshot ControlFlow => _cfg;
    internal MirStorageProvenanceSnapshot StorageProvenance => _provenance;

    public MirMemoryStateAvailability CheckBeforeInstruction(
        MirValueRef state,
        MirInstructionRef instruction) =>
        Check(state, _cfg.PointBeforeInstruction(instruction));

    public MirMemoryStateAvailability CheckAtTerminator(
        MirValueRef state,
        MirBlockRef block) =>
        Check(state, _cfg.TerminatorPoint(block));

    internal MirMemoryStateAvailability CheckBeforeInstruction(
        MirValueId state,
        MirInstructionId instruction) =>
        Check(state, _cfg.PointBeforeInstruction(instruction));

    internal MirMemoryStateAvailability CheckAtTerminator(
        MirValueId state,
        MirBlockId block) =>
        Check(state, _cfg.TerminatorPoint(block));

    internal MirMemoryStateAvailability CheckAtLocation(
        MirValueId state,
        MirBlockId block,
        int instructionIndex)
    {
        var targetBlock = _callable.FindBlock(block)
            ?? throw new ArgumentOutOfRangeException(
                nameof(block),
                block,
                $"block {block} does not belong to callable {Callable}");
        if (instructionIndex < 0 || instructionIndex > targetBlock.Instructions.Count)
            throw new ArgumentOutOfRangeException(
                nameof(instructionIndex),
                instructionIndex,
                $"program point index is outside block {block}");
        return instructionIndex == targetBlock.Instructions.Count
            ? CheckAtTerminator(state, block)
            : CheckBeforeInstruction(
                state,
                targetBlock.Instructions[instructionIndex].Id);
    }

    internal bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_callable, program.FindCallable(callable))
        && SnapshotId == program.SnapshotId
        && Callable.Callable == callable;

    internal void EnsureFor(MirProgram program, MirCallableId callable)
    {
        if (!IsFor(program, callable))
            throw new InvalidOperationException(
                $"MIR memory-state snapshot belongs to {Callable} in snapshot {SnapshotId}; " +
                $"reanalyze {callable} in snapshot {program.SnapshotId}");
    }

    public MirMemoryStateAvailability Check(
        MirValueRef state,
        MirProgramPoint point)
    {
        Require(state);
        return Check(state.Value, point);
    }

    internal MirMemoryStateAvailability Check(
        MirValueId state,
        MirProgramPoint point)
    {
        var value = _callable.FindValue(state)
            ?? throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                $"value {state} does not belong to callable {Callable}");
        if (!value.Type.IsArray)
            throw new ArgumentException(
                $"value {state} has scalar type {value.Type}; memory-state availability requires an array",
                nameof(state));

        // This also rejects a point issued by another CFG snapshot.
        var ssaAvailable = _cfg.IsValueAvailableAt(state, point);
        var definitionBlock = DefinitionBlock(value);
        var requiresSameIteration =
            definitionBlock is MirBlockId block
            && value.Definition.Kind != MirValueDefinitionKind.Parameter
            && _cfg.IsInCycle(block);

        if (!_cfg.IsReachable(point.Block))
            return Result(
                MirMemoryStateAvailabilityKind.UnreachablePoint,
                requiresSameIteration);
        if (!ssaAvailable)
            return Result(
                MirMemoryStateAvailabilityKind.SsaValueUnavailable,
                requiresSameIteration);

        var stateStorage = _provenance.ProvenanceOf(state);
        if (!stateStorage.IsComplete || stateStorage.PossibleStorages.Count == 0)
            return Result(
                MirMemoryStateAvailabilityKind.UnknownStorageProvenance,
                requiresSameIteration);

        var inState = SolveMustAvailability(state, stateStorage);
        var current = inState[point.Block.Block].Clone();
        if (value.Definition.Kind == MirValueDefinitionKind.BlockArgument
            && value.Definition.Block == point.Block.Block)
            current.Define();

        var blockAtPoint = _callable.FindBlock(point.Block.Block)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: memory point references missing block {point.Block}");
        for (var index = 0; index < point.InstructionIndex; index++)
            Transfer(
                state,
                stateStorage,
                blockAtPoint.Instructions[index],
                current);

        return current.Available
            ? Result(MirMemoryStateAvailabilityKind.Available, requiresSameIteration)
            : Result(
                MirMemoryStateAvailabilityKind.Clobbered,
                requiresSameIteration,
                current.Clobbers);
    }

    private void Require(MirValueRef state)
    {
        MirReferenceValidation.RequireSnapshot(
            SnapshotId,
            state.Snapshot,
            nameof(state));
        if (state.Callable != Callable.Callable)
            throw new ArgumentException(
                $"MIR value belongs to callable {state.Callable}; expected {Callable}",
                nameof(state));
    }

    private Dictionary<MirBlockId, FlowFact> SolveMustAvailability(
        MirValueId state,
        MirStorageProvenance stateStorage)
    {
        var value = _callable.FindValue(state)!;
        var blocks = _callable.Blocks.ToDictionary(block => block.Id);
        var inState = blocks.Keys.ToDictionary(
            block => block,
            _ => new FlowFact(available: true));
        var outState = blocks.Keys.ToDictionary(
            block => block,
            _ => new FlowFact(available: true));
        var parameterDefinition =
            value.Definition.Kind == MirValueDefinitionKind.Parameter;

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in _callable.Blocks.OrderBy(block => block.Id.Value))
            {
                if (!_cfg.IsReachable(block.Id))
                    continue;

                FlowFact incoming;
                if (block.Id == _callable.EntryBlock)
                {
                    incoming = new FlowFact(parameterDefinition);
                }
                else
                {
                    var predecessors = _cfg.PredecessorsOf(block.Id)
                        .Where(_cfg.IsReachable)
                        .ToArray();
                    incoming = predecessors.Length == 0
                        ? new FlowFact(available: false)
                        : FlowFact.Meet(predecessors.Select(predecessor => outState[predecessor]));
                }

                if (!inState[block.Id].SameAs(incoming))
                {
                    inState[block.Id] = incoming.Clone();
                    changed = true;
                }

                var outgoing = incoming.Clone();
                if (value.Definition.Kind == MirValueDefinitionKind.BlockArgument
                    && value.Definition.Block == block.Id)
                    outgoing.Define();
                foreach (var instruction in block.Instructions)
                    Transfer(state, stateStorage, instruction, outgoing);

                if (!outState[block.Id].SameAs(outgoing))
                {
                    outState[block.Id] = outgoing;
                    changed = true;
                }
            }
        }

        return inState;
    }

    private void Transfer(
        MirValueId state,
        MirStorageProvenance stateStorage,
        MirInstruction instruction,
        FlowFact fact)
    {
        if (_mutationsByInstruction.TryGetValue(instruction.Id, out var mutations))
        {
            foreach (var mutation in mutations)
                if (MirStorageAliasAnalysis.MayAlias(
                        _callable,
                        stateStorage,
                        mutation.Storage))
                    fact.Clobber(mutation);
        }

        // A store/mutable call first changes storage and then defines its new memory-state result.
        // Therefore its own output is current immediately after the instruction, while its input is dead.
        if (instruction.ResultValues.Contains(state))
            fact.Define();
    }

    private MirBlockId? DefinitionBlock(MirValue value) =>
        value.Definition.Kind switch
        {
            MirValueDefinitionKind.Parameter => _callable.EntryBlock,
            _ => value.Definition.Block,
        };

    private static MirMemoryStateAvailability Result(
        MirMemoryStateAvailabilityKind kind,
        bool requiresSameIteration,
        IEnumerable<MirMemoryMutation>? clobbers = null) =>
        new(
            kind,
            new ReadOnlyCollection<MirMemoryMutation>(
                (clobbers ?? Array.Empty<MirMemoryMutation>())
                    .Distinct()
                    .OrderBy(mutation => mutation.Instruction.Instruction.Value)
                    .ThenBy(mutation => mutation.OperandIndex)
                    .ToArray()),
            requiresSameIteration);

    private sealed class FlowFact
    {
        public FlowFact(bool available)
        {
            Available = available;
        }

        private FlowFact(
            bool available,
            IEnumerable<MirMemoryMutation> clobbers)
        {
            Available = available;
            Clobbers.UnionWith(clobbers);
        }

        public bool Available { get; private set; }
        public HashSet<MirMemoryMutation> Clobbers { get; } = new();

        public void Clobber(MirMemoryMutation mutation)
        {
            Available = false;
            Clobbers.Add(mutation);
        }

        public void Define()
        {
            Available = true;
            Clobbers.Clear();
        }

        public FlowFact Clone() => new(Available, Clobbers);

        public bool SameAs(FlowFact other) =>
            Available == other.Available
            && Clobbers.SetEquals(other.Clobbers);

        public static FlowFact Meet(IEnumerable<FlowFact> predecessors)
        {
            var states = predecessors.ToArray();
            var available = states.All(state => state.Available);
            return new FlowFact(
                available,
                states
                    .Where(state => !state.Available)
                    .SelectMany(state => state.Clobbers));
        }
    }
}

internal static class MirMemoryStateAnalysis
{
    internal static MirMemoryStateSnapshot Analyze(
        MirProgram program,
        MirCallableId callableId)
    {
        ArgumentNullException.ThrowIfNull(program);
        QoraMirVerifier.VerifyOrThrow(program);

        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        return AnalyzeUnchecked(program, callable);
    }

    /// <summary>
    /// Builds memory-state facts after structural MIR verification. The verifier uses this form for
    /// memory-Phi and call-alias contracts, avoiding a recursive Verify → Analyze → Verify cycle.
    /// </summary>
    internal static MirMemoryStateSnapshot AnalyzeUnchecked(
        MirProgram program,
        MirCallable callable)
    {
        var cfg = MirControlFlowAnalysis.AnalyzeUnchecked(program, callable);
        var provenance = MirStorageProvenanceAnalysis.AnalyzeUnchecked(program, callable);
        return AnalyzeVerified(program, callable, cfg, provenance);
    }

    /// <summary>
    /// Builds memory facts from dependency snapshots owned by the same verified MIR snapshot.
    /// <see cref="MirAnalysisStore"/> uses this entry point so all downstream analyses share one CFG
    /// and one storage-provenance instance per callable.
    /// </summary>
    internal static MirMemoryStateSnapshot AnalyzeVerified(
        MirProgram program,
        MirCallable callable,
        MirControlFlowSnapshot cfg,
        MirStorageProvenanceSnapshot provenance)
    {
        cfg.EnsureFor(program, callable.Id);
        provenance.EnsureFor(program, callable.Id);
        var mutations = CollectMutations(program, callable, provenance);
        return new MirMemoryStateSnapshot(
            program,
            callable,
            cfg,
            provenance,
            mutations);
    }

    private static IReadOnlyList<MirMemoryMutation> CollectMutations(
        MirProgram program,
        MirCallable callable,
        MirStorageProvenanceSnapshot provenance)
    {
        var mutations = new List<MirMemoryMutation>();
        foreach (var block in callable.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case MirArrayStore store:
                        mutations.Add(new MirMemoryMutation(
                            new MirBlockRef(program.SnapshotId, callable.Id, block.Id),
                            new MirInstructionRef(program.SnapshotId, callable.Id, store.Id),
                            MirMemoryMutationKind.ArrayStore,
                            provenance.ProvenanceOf(store.Array)));
                        break;

                    case MirPureCall call:
                        AddCallMutation(
                            program.SnapshotId,
                            block.Id,
                            call.Id,
                            call.Operands,
                            callable,
                            provenance,
                            mutations);
                        break;

                    case MirQuantumApply apply:
                        AddCallMutation(
                            program.SnapshotId,
                            block.Id,
                            apply.Id,
                            apply.Operands,
                            callable,
                            provenance,
                            mutations);
                        break;
                }
            }
        }
        return new ReadOnlyCollection<MirMemoryMutation>(mutations.ToArray());
    }

    private static void AddCallMutation(
        MirSnapshotId snapshotId,
        MirBlockId block,
        MirInstructionId instruction,
        IReadOnlyList<MirCallOperand> operands,
        MirCallable callable,
        MirStorageProvenanceSnapshot provenance,
        ICollection<MirMemoryMutation> mutations)
    {
        for (var operandIndex = 0; operandIndex < operands.Count; operandIndex++)
        {
            if (operands[operandIndex] is not MirClassicalCallOperand operand)
                continue;
            var value = callable.FindValue(operand.Value);
            if (value is not { Type.IsArray: true })
                continue;

            MirMemoryMutationKind? kind = operand.Ownership == QOwnershipMode.Moved
                ? MirMemoryMutationKind.OwnershipTransfer
                : operand.Access == QAccessMode.Mutable
                    ? MirMemoryMutationKind.MutableCall
                    : null;
            if (kind is null)
                continue;

            mutations.Add(new MirMemoryMutation(
                new MirBlockRef(snapshotId, callable.Id, block),
                new MirInstructionRef(snapshotId, callable.Id, instruction),
                kind.Value,
                provenance.ProvenanceOf(operand.Value),
                operandIndex));
        }
    }
}
