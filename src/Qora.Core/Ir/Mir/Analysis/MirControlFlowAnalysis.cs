using System.Collections.Frozen;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// A boundary in one MIR block. An instruction point is immediately before that instruction;
/// a terminator point is after every ordinary instruction and immediately before the terminator.
/// Points are issued by one <see cref="MirControlFlowSnapshot"/> and cannot be consumed by another
/// snapshot, even when both snapshots happen to use the same dense MIR identities.
/// </summary>
public sealed class MirProgramPoint
{
    private readonly MirControlFlowSnapshot _owner;

    internal MirProgramPoint(
        MirControlFlowSnapshot owner,
        MirBlockId block,
        int instructionIndex,
        MirInstructionId? instruction)
    {
        _owner = owner;
        Block = block;
        InstructionIndex = instructionIndex;
        Instruction = instruction;
    }

    public MirBlockId Block { get; }

    /// <summary>
    /// The number of ordinary instructions which have completed before this point.
    /// </summary>
    public int InstructionIndex { get; }

    public MirInstructionId? Instruction { get; }
    public bool IsBeforeInstruction => Instruction is not null;
    public bool IsTerminator => Instruction is null;

    internal bool BelongsTo(MirControlFlowSnapshot owner) =>
        ReferenceEquals(_owner, owner);

    public override string ToString() =>
        Instruction is MirInstructionId instruction
            ? $"{Block}:before {instruction}"
            : $"{Block}:terminator";
}

/// <summary>
/// Immutable control-flow, dominance, and SSA-availability facts for one exact callable object. A MIR
/// rewrite which replaces that callable must rebuild this analysis before using any old block,
/// instruction, or value as a scheduling fact.
/// </summary>
public sealed class MirControlFlowSnapshot
{
    private readonly MirCallable _sourceCallable;
    private readonly FrozenDictionary<MirBlockId, IReadOnlyList<MirBlockId>> _successors;
    private readonly FrozenDictionary<MirBlockId, IReadOnlyList<MirBlockId>> _predecessors;
    private readonly FrozenDictionary<MirBlockId, FrozenSet<MirBlockId>> _reachableFrom;
    private readonly FrozenDictionary<MirBlockId, FrozenSet<MirBlockId>> _dominators;
    private readonly FrozenDictionary<MirBlockId, FrozenSet<MirBlockId>> _postDominators;
    private readonly FrozenSet<MirBlockId> _reachable;
    private readonly FrozenDictionary<MirInstructionId, MirProgramPoint> _instructionPoints;
    private readonly FrozenDictionary<MirBlockId, MirProgramPoint> _terminatorPoints;

    internal MirControlFlowSnapshot(
        MirCallable sourceCallable,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> successors,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> predecessors,
        IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> reachableFrom,
        IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators,
        IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> postDominators,
        IReadOnlySet<MirBlockId> reachable)
    {
        _sourceCallable = sourceCallable;

        _successors = successors.ToFrozenDictionary();
        _predecessors = predecessors.ToFrozenDictionary();
        _reachableFrom = reachableFrom.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet());
        _dominators = dominators.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet());
        _postDominators = postDominators.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet());
        _reachable = reachable.ToFrozenSet();

        ReachableBlocks = Array.AsReadOnly(
            reachable
                .OrderBy(id => id.Value)
                .ToArray());

        var instructionPoints =
            new Dictionary<MirInstructionId, MirProgramPoint>();
        foreach (var block in sourceCallable.Blocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                instructionPoints.Add(
                    instruction.Id,
                    new MirProgramPoint(
                        this,
                        block.Id,
                        index,
                        instruction.Id));
            }
        }
        _instructionPoints = instructionPoints.ToFrozenDictionary();
        _terminatorPoints = sourceCallable.Blocks.ToFrozenDictionary(
            block => block.Id,
            block => new MirProgramPoint(
                this,
                block.Id,
                block.Instructions.Count,
                instruction: null));
    }

    public MirCallableId Callable => _sourceCallable.Id;
    public MirBlockId EntryBlock => _sourceCallable.EntryBlock.Id;
    public IReadOnlyList<MirBlockId> ReachableBlocks { get; }
    internal MirCallable SourceCallable => _sourceCallable;

    public bool IsReachable(MirBlockId block)
    {
        _sourceCallable.RequireBlock(block);
        return _reachable.Contains(block);
    }

    public IReadOnlyList<MirBlockId> SuccessorsOf(MirBlockId block)
    {
        _sourceCallable.RequireBlock(block);
        return _successors[block];
    }

    public IReadOnlyList<MirBlockId> PredecessorsOf(MirBlockId block)
    {
        _sourceCallable.RequireBlock(block);
        return _predecessors[block];
    }

    /// <summary>
    /// True when control can reach <paramref name="target"/> from <paramref name="source"/>.
    /// A block reaches itself by the empty path. Use <see cref="IsInCycle"/> when a non-empty
    /// path back to the same block is required.
    /// </summary>
    public bool CanReach(MirBlockId source, MirBlockId target)
    {
        _sourceCallable.RequireBlock(source);
        _sourceCallable.RequireBlock(target);
        return _reachableFrom[source].Contains(target);
    }

    /// <summary>
    /// True when the block belongs to a non-trivial CFG cycle or has a self edge. A quantum
    /// effect in such a block can execute more than once even though it has one static instruction ID.
    /// </summary>
    public bool IsInCycle(MirBlockId block)
    {
        _sourceCallable.RequireBlock(block);
        return _successors[block].Any(
            successor => successor == block || _reachableFrom[successor].Contains(block));
    }

    public bool Dominates(MirBlockId candidate, MirBlockId block)
    {
        _sourceCallable.RequireBlock(candidate);
        _sourceCallable.RequireBlock(block);
        return _dominators[block].Contains(candidate);
    }

    public bool StrictlyDominates(MirBlockId candidate, MirBlockId block) =>
        candidate != block && Dominates(candidate, block);

    public bool PostDominates(MirBlockId candidate, MirBlockId block)
    {
        _sourceCallable.RequireBlock(candidate);
        _sourceCallable.RequireBlock(block);
        return _postDominators[block].Contains(candidate);
    }

    public bool StrictlyPostDominates(MirBlockId candidate, MirBlockId block) =>
        candidate != block && PostDominates(candidate, block);

    public MirProgramPoint PointBeforeInstruction(MirInstructionId instruction)
    {
        _sourceCallable.RequireInstruction(instruction);
        return _instructionPoints[instruction];
    }

    public MirProgramPoint TerminatorPoint(MirBlockId block)
    {
        _sourceCallable.RequireBlock(block);
        return _terminatorPoints[block];
    }

    /// <summary>
    /// Determines whether the exact SSA value is defined on every path reaching the point. Parameters are
    /// callable-wide. Block arguments are available from block entry. An instruction result is available
    /// only after its defining instruction, and cross-block definitions must dominate the use block.
    /// </summary>
    public bool IsValueAvailableAt(MirValueId value, MirProgramPoint point)
    {
        EnsurePoint(point);
        var definition = _sourceCallable.DefinitionOf(value);

        return definition.Kind switch
        {
            MirValueDefinitionKind.Parameter => true,
            MirValueDefinitionKind.BlockArgument =>
                IsBlockDefinitionAvailable(definition.Block, point.Block),
            MirValueDefinitionKind.InstructionResult =>
                IsInstructionDefinitionAvailable(definition.Instruction, point),
            _ => false,
        };
    }

    public bool IsValueAvailableBeforeInstruction(
        MirValueId value,
        MirInstructionId instruction) =>
        IsValueAvailableAt(value, PointBeforeInstruction(instruction));

    public bool IsValueAvailableAtTerminator(
        MirValueId value,
        MirBlockId block) =>
        IsValueAvailableAt(value, TerminatorPoint(block));

    private bool IsBlockDefinitionAvailable(
        MirBlockId? definitionBlock,
        MirBlockId useBlock) =>
        definitionBlock is MirBlockId block
        && (block == useBlock || _dominators[useBlock].Contains(block));

    private bool IsInstructionDefinitionAvailable(
        MirInstructionId? definitionInstruction,
        MirProgramPoint usePoint)
    {
        if (definitionInstruction is not MirInstructionId instruction
            || !_sourceCallable.ContainsInstruction(instruction))
            return false;

        var definition =
            _sourceCallable.RequireInstructionLocation(instruction);
        return definition.Block.Id == usePoint.Block
            ? definition.Index < usePoint.InstructionIndex
            : _dominators[usePoint.Block].Contains(definition.Block.Id);
    }

    private void EnsurePoint(MirProgramPoint? point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!point.BelongsTo(this))
            throw new InvalidOperationException(
                "the MIR program point belongs to a different control-flow snapshot");
    }

}

/// <summary>
/// Builds reusable graph facts from verified MIR. The fixed-point algorithms operate on the public CFG
/// contract rather than duplicating the verifier's private diagnostic logic.
/// </summary>
internal static class MirControlFlowAnalysis
{
    /// <summary>
    /// Builds CFG facts without starting structural verification. MirCallable construction has already
    /// established that every CFG target belongs to the callable. The verifier and canonical analysis
    /// store use this entry point without recursively starting verification.
    /// </summary>
    internal static MirControlFlowSnapshot AnalyzeUnchecked(MirCallable callable)
    {
        var successors = BuildSuccessors(callable);
        var predecessors = BuildPredecessors(successors);

        var reachable = ReachableFrom(callable.EntryBlock.Id, successors);
        var reachableFrom = successors.Keys.ToDictionary(
            block => block,
            block => ReachableFrom(block, successors));
        var exits = reachable
            .Where(block => successors[block].Count == 0)
            .ToHashSet();
        var canReachExit = ReachableFrom(exits, predecessors);
        var dominators = ComputeDominators(
            callable.EntryBlock.Id,
            successors.Keys,
            reachable,
            predecessors);
        var postDominators = ComputePostDominators(
            successors.Keys,
            reachable,
            canReachExit,
            exits,
            successors);

        return new MirControlFlowSnapshot(
            callable,
            successors,
            predecessors,
            reachableFrom,
            dominators,
            postDominators,
            reachable);
    }

    private static IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> BuildSuccessors(
        MirCallable callable)
    {
        var successors =
            new Dictionary<MirBlockId, IReadOnlyList<MirBlockId>>(callable.Blocks.Count);

        foreach (var block in callable.Blocks)
        {
            var orderedTargets = block.Terminator.Successors
                .Distinct()
                .OrderBy(target => target.Value);

            successors.Add(block.Id, MirCollections.Freeze(orderedTargets));
        }

        return successors;
    }

    private static IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> BuildPredecessors(
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> successors)
    {
        var mutablePredecessors =
            new Dictionary<MirBlockId, HashSet<MirBlockId>>(successors.Count);

        foreach (var block in successors.Keys)
            mutablePredecessors.Add(block, new HashSet<MirBlockId>());

        foreach (var (source, targets) in successors)
        {
            foreach (var target in targets)
                mutablePredecessors[target].Add(source);
        }

        var predecessors =
            new Dictionary<MirBlockId, IReadOnlyList<MirBlockId>>(successors.Count);

        foreach (var (target, sources) in mutablePredecessors)
        {
            var orderedSources = sources.OrderBy(source => source.Value);
            predecessors.Add(target, MirCollections.Freeze(orderedSources));
        }

        return predecessors;
    }

    private static HashSet<MirBlockId> ReachableFrom(
        MirBlockId start,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> edges) =>
        ReachableFrom(new[] { start }, edges);

    private static HashSet<MirBlockId> ReachableFrom(
        IEnumerable<MirBlockId> starts,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> edges)
    {
        var reachable = new HashSet<MirBlockId>();
        var pending = new Stack<MirBlockId>(starts);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current)) continue;
            foreach (var next in edges[current])
                pending.Push(next);
        }
        return reachable;
    }

    private static Dictionary<MirBlockId, HashSet<MirBlockId>> ComputeDominators(
        MirBlockId entry,
        IEnumerable<MirBlockId> blockIds,
        IReadOnlySet<MirBlockId> reachable,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> predecessors)
    {
        var allReachable = reachable.ToHashSet();
        var result = blockIds.ToDictionary(
            block => block,
            block => !reachable.Contains(block) || block == entry
                ? new HashSet<MirBlockId> { block }
                : new HashSet<MirBlockId>(allReachable));

        IterateToFixedPoint(
            reachable.Where(block => block != entry),
            result,
            block => predecessors[block].Where(reachable.Contains),
            includeSelf: true);
        return result;
    }

    private static Dictionary<MirBlockId, HashSet<MirBlockId>> ComputePostDominators(
        IEnumerable<MirBlockId> blockIds,
        IReadOnlySet<MirBlockId> reachable,
        IReadOnlySet<MirBlockId> canReachExit,
        IReadOnlySet<MirBlockId> exits,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> successors)
    {
        var terminatingRegion = canReachExit.ToHashSet();
        var result = blockIds.ToDictionary(
            block => block,
            block => !reachable.Contains(block)
                     || !canReachExit.Contains(block)
                     || exits.Contains(block)
                ? new HashSet<MirBlockId> { block }
                : new HashSet<MirBlockId>(terminatingRegion));

        IterateToFixedPoint(
            reachable.Where(block => canReachExit.Contains(block) && !exits.Contains(block)),
            result,
            block => successors[block],
            includeSelf: true);
        return result;
    }

    private static void IterateToFixedPoint(
        IEnumerable<MirBlockId> blocks,
        IDictionary<MirBlockId, HashSet<MirBlockId>> sets,
        Func<MirBlockId, IEnumerable<MirBlockId>> adjacent,
        bool includeSelf)
    {
        var ordered = blocks.OrderBy(block => block.Value).ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in ordered)
            {
                var neighbors = adjacent(block).ToArray();
                var next = neighbors.Length == 0
                    ? new HashSet<MirBlockId>()
                    : new HashSet<MirBlockId>(sets[neighbors[0]]);
                foreach (var neighbor in neighbors.Skip(1))
                    next.IntersectWith(sets[neighbor]);
                if (includeSelf) next.Add(block);
                if (next.SetEquals(sets[block])) continue;
                sets[block] = next;
                changed = true;
            }
        }
    }
}
