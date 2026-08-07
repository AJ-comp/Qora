using System.Collections.Frozen;

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
    MirInstructionId Instruction,
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
    private readonly MirControlFlowSnapshot _cfg;
    private readonly MirStorageProvenanceSnapshot _provenance;
    private readonly FrozenDictionary<MirInstructionId, IReadOnlyList<MirMemoryMutation>>
        _mutationsByInstruction;

    internal MirMemoryStateSnapshot(
        MirControlFlowSnapshot cfg,
        MirStorageProvenanceSnapshot provenance,
        IReadOnlyList<MirMemoryMutation> mutations)
    {
        _cfg = cfg;
        _provenance = provenance;
        _mutationsByInstruction = mutations
            .GroupBy(mutation => mutation.Instruction)
            .ToFrozenDictionary(
                group => group.Key,
                group => MirCollections.Freeze(group));
    }

    public MirCallableId Callable => _cfg.Callable;
    internal MirControlFlowSnapshot ControlFlow => _cfg;
    private MirCallable SourceCallable => _cfg.SourceCallable;

    public MirMemoryStateAvailability CheckBeforeInstruction(
        MirValueId state,
        MirInstructionId instruction) =>
        Check(state, _cfg.PointBeforeInstruction(instruction));

    public MirMemoryStateAvailability CheckAtTerminator(
        MirValueId state,
        MirBlockId block) =>
        Check(state, _cfg.TerminatorPoint(block));

    public MirMemoryStateAvailability Check(
        MirValueId state,
        MirProgramPoint point)
    {
        var value = SourceCallable.FindValue(state)
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
        var definition = SourceCallable.DefinitionOf(state);
        var definitionBlock = DefinitionBlock(definition);
        var requiresSameIteration =
            definitionBlock is MirBlockId block
            && definition.Kind != MirValueDefinitionKind.Parameter
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
        var current = inState[point.Block].Clone();
        if (definition.Kind == MirValueDefinitionKind.BlockArgument
            && definition.Block == point.Block)
            current.Define();

        var blockAtPoint = SourceCallable.FindBlock(point.Block)
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

    private Dictionary<MirBlockId, FlowFact> SolveMustAvailability(
        MirValueId state,
        MirStorageProvenance stateStorage)
    {
        var value = SourceCallable.FindValue(state)!;
        var inState = new Dictionary<MirBlockId, FlowFact>(SourceCallable.Blocks.Count);
        var outState = new Dictionary<MirBlockId, FlowFact>(SourceCallable.Blocks.Count);
        foreach (var block in SourceCallable.Blocks)
        {
            inState.Add(block.Id, new FlowFact(available: true));
            outState.Add(block.Id, new FlowFact(available: true));
        }
        var definition = SourceCallable.DefinitionOf(state);
        var parameterDefinition =
            definition.Kind == MirValueDefinitionKind.Parameter;

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in SourceCallable.Blocks.OrderBy(block => block.Id.Value))
            {
                if (!_cfg.IsReachable(block.Id))
                    continue;

                FlowFact incoming;
                if (block.Id == SourceCallable.EntryBlock.Id)
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
                if (definition.Kind == MirValueDefinitionKind.BlockArgument
                    && definition.Block == block.Id)
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
                        SourceCallable,
                        stateStorage,
                        mutation.Storage))
                    fact.Clobber(mutation);
        }

        // A store/mutable call first changes storage and then defines its new memory-state result.
        // Therefore its own output is current immediately after the instruction, while its input is dead.
        if (instruction.Results.Any(result => result.Id == state))
            fact.Define();
    }

    private MirBlockId? DefinitionBlock(MirValueDefinition definition) =>
        definition.Kind switch
        {
            MirValueDefinitionKind.Parameter => SourceCallable.EntryBlock.Id,
            MirValueDefinitionKind.BlockArgument => definition.Block,
            MirValueDefinitionKind.InstructionResult
                when definition.Instruction is MirInstructionId instruction =>
                SourceCallable.RequireInstructionLocation(instruction).Block.Id,
            _ => null,
        };

    private static MirMemoryStateAvailability Result(
        MirMemoryStateAvailabilityKind kind,
        bool requiresSameIteration,
        IEnumerable<MirMemoryMutation>? clobbers = null) =>
        new(
            kind,
            MirCollections.Freeze(
                (clobbers ?? Array.Empty<MirMemoryMutation>())
                    .Distinct()
                    .OrderBy(mutation => mutation.Instruction.Value)
                    .ThenBy(mutation => mutation.OperandIndex)),
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
    /// <summary>
    /// Builds memory facts from dependency snapshots owned by the same exact callable.
    /// <see cref="MirAnalysisStore"/> supplies the canonical CFG and storage-provenance instances.
    /// </summary>
    internal static MirMemoryStateSnapshot AnalyzeVerified(
        MirControlFlowSnapshot cfg,
        MirStorageProvenanceSnapshot provenance)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(provenance);
        var callable = cfg.SourceCallable;
        provenance.EnsureFor(callable);
        var mutations = CollectMutations(callable, provenance);
        return new MirMemoryStateSnapshot(
            cfg,
            provenance,
            mutations);
    }

    private static IReadOnlyList<MirMemoryMutation> CollectMutations(
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
                            store.Id,
                            MirMemoryMutationKind.ArrayStore,
                            provenance.ProvenanceOf(store.Array)));
                        break;

                    case MirPureCall call:
                        AddCallMutation(
                            call);
                        break;

                    case MirQuantumApply apply:
                        AddCallMutation(
                            apply);
                        break;
                }
            }
        }
        return mutations;

        void AddCallMutation(MirInstruction instruction)
        {
            var operands = instruction switch
            {
                MirPureCall call => call.Operands,
                MirQuantumApply apply => apply.Operands,
                _ => throw new ArgumentException(
                    $"{instruction.GetType().Name} is not a MIR call instruction",
                    nameof(instruction)),
            };

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
                    instruction.Id,
                    kind.Value,
                    provenance.ProvenanceOf(operand.Value),
                    operandIndex));
            }
        }
    }
}
