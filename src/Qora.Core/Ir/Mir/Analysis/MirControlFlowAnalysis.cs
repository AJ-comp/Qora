using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// A boundary in one MIR block. An instruction point is immediately before that instruction;
/// a terminator point is after every ordinary instruction and immediately before the terminator.
/// Points are issued by one <see cref="MirControlFlowSnapshot"/> and cannot be consumed by another
/// snapshot, even when both snapshots happen to use the same dense MIR identities.
/// </summary>
public sealed class MirProgramPoint
{
    private readonly object _snapshotIdentity;

    internal MirProgramPoint(
        object snapshotIdentity,
        MirSnapshotId snapshotId,
        MirCallableId callable,
        MirBlockId block,
        int instructionIndex,
        MirInstructionId? instruction)
    {
        _snapshotIdentity = snapshotIdentity;
        SnapshotId = snapshotId;
        Callable = callable;
        Block = block;
        InstructionIndex = instructionIndex;
        Instruction = instruction;
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableId Callable { get; }
    public MirBlockId Block { get; }

    /// <summary>
    /// The number of ordinary instructions which have completed before this point.
    /// </summary>
    public int InstructionIndex { get; }

    public MirInstructionId? Instruction { get; }
    public bool IsBeforeInstruction => Instruction is not null;
    public bool IsTerminator => Instruction is null;

    internal bool BelongsTo(object snapshotIdentity) =>
        ReferenceEquals(_snapshotIdentity, snapshotIdentity);

    public override string ToString() =>
        Instruction is MirInstructionId instruction
            ? $"{Callable}/{Block}:before {instruction}"
            : $"{Callable}/{Block}:terminator";
}

/// <summary>
/// Immutable control-flow, dominance, and SSA-availability facts for one callable in one exact MIR
/// program revision. A MIR rewrite creates a new program, so callers must rebuild this snapshot before
/// using any old block, instruction, or value identity as a scheduling fact.
/// </summary>
public sealed class MirControlFlowSnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly MirCallable _sourceCallable;
    private readonly object _snapshotIdentity = new();
    private readonly FrozenDictionary<MirBlockId, MirBlock> _blocks;
    private readonly FrozenDictionary<MirValueId, MirValue> _values;
    private readonly FrozenDictionary<MirBlockId, IReadOnlyList<MirBlockId>> _successors;
    private readonly FrozenDictionary<MirBlockId, IReadOnlyList<MirBlockId>> _predecessors;
    private readonly FrozenDictionary<MirBlockId, FrozenSet<MirBlockId>> _reachableFrom;
    private readonly FrozenDictionary<MirBlockId, FrozenSet<MirBlockId>> _dominators;
    private readonly FrozenDictionary<MirBlockId, FrozenSet<MirBlockId>> _postDominators;
    private readonly FrozenSet<MirBlockId> _reachable;
    private readonly FrozenSet<MirBlockId> _canReachExit;
    private readonly FrozenDictionary<MirInstructionId, InstructionLocation> _instructionLocations;
    private readonly FrozenDictionary<MirInstructionId, MirProgramPoint> _instructionPoints;
    private readonly FrozenDictionary<MirBlockId, MirProgramPoint> _terminatorPoints;

    internal MirControlFlowSnapshot(
        MirProgram sourceProgram,
        MirCallable sourceCallable,
        IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
        IReadOnlyDictionary<MirValueId, MirValue> values,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> successors,
        IReadOnlyDictionary<MirBlockId, IReadOnlyList<MirBlockId>> predecessors,
        IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> reachableFrom,
        IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators,
        IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> postDominators,
        IReadOnlySet<MirBlockId> reachable,
        IReadOnlySet<MirBlockId> canReachExit,
        IReadOnlyDictionary<MirInstructionId, InstructionLocation> instructionLocations)
    {
        _sourceProgram = sourceProgram;
        _sourceCallable = sourceCallable;
        SnapshotId = sourceProgram.SnapshotId;
        Callable = sourceCallable.Id;
        EntryBlock = sourceCallable.EntryBlock;

        _blocks = blocks.ToFrozenDictionary();
        _values = values.ToFrozenDictionary();
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
        _canReachExit = canReachExit.ToFrozenSet();
        _instructionLocations = instructionLocations.ToFrozenDictionary();

        Blocks = ReadOnly(
            blocks.Keys
                .OrderBy(id => id.Value)
                .ToArray());
        ReachableBlocks = ReadOnly(
            reachable
                .OrderBy(id => id.Value)
                .ToArray());
        ExitBlocks = ReadOnly(
            reachable
                .Where(block => successors[block].Count == 0)
                .OrderBy(id => id.Value)
                .ToArray());

        _instructionPoints = instructionLocations.ToFrozenDictionary(
            pair => pair.Key,
            pair => new MirProgramPoint(
                _snapshotIdentity,
                SnapshotId,
                Callable,
                pair.Value.Block,
                pair.Value.Index,
                pair.Key));
        _terminatorPoints = blocks.ToFrozenDictionary(
            pair => pair.Key,
            pair => new MirProgramPoint(
                _snapshotIdentity,
                SnapshotId,
                Callable,
                pair.Key,
                pair.Value.Instructions.Count,
                instruction: null));
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableId Callable { get; }
    public MirBlockId EntryBlock { get; }
    public IReadOnlyList<MirBlockId> Blocks { get; }
    public IReadOnlyList<MirBlockId> ReachableBlocks { get; }

    /// <summary>
    /// Reachable blocks with no CFG successor. Both <see cref="MirReturn"/> and
    /// <see cref="MirUnreachable"/> are exits for structural post-dominance.
    /// </summary>
    public IReadOnlyList<MirBlockId> ExitBlocks { get; }

    internal bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_sourceCallable, program.FindCallable(callable))
        && SnapshotId == program.SnapshotId
        && Callable == callable;

    internal void EnsureFor(MirProgram program, MirCallableId callable)
    {
        if (!IsFor(program, callable))
            throw new InvalidOperationException(
                $"MIR control-flow snapshot belongs to {Callable} in snapshot {SnapshotId}; " +
                $"reanalyze {callable} in snapshot {program.SnapshotId}");
    }

    public bool IsReachable(MirBlockId block)
    {
        RequireBlock(block);
        return _reachable.Contains(block);
    }

    /// <summary>
    /// True when at least one path from the block reaches a return or unreachable terminator.
    /// A false result commonly identifies a non-terminating loop.
    /// </summary>
    public bool CanReachExit(MirBlockId block)
    {
        RequireBlock(block);
        return _canReachExit.Contains(block);
    }

    public IReadOnlyList<MirBlockId> SuccessorsOf(MirBlockId block)
    {
        RequireBlock(block);
        return _successors[block];
    }

    public IReadOnlyList<MirBlockId> PredecessorsOf(MirBlockId block)
    {
        RequireBlock(block);
        return _predecessors[block];
    }

    /// <summary>
    /// True when control can reach <paramref name="target"/> from <paramref name="source"/>.
    /// A block reaches itself by the empty path. Use <see cref="IsInCycle"/> when a non-empty
    /// path back to the same block is required.
    /// </summary>
    public bool CanReach(MirBlockId source, MirBlockId target)
    {
        RequireBlock(source);
        RequireBlock(target);
        return _reachableFrom[source].Contains(target);
    }

    /// <summary>
    /// True when the block belongs to a non-trivial CFG cycle or has a self edge. A quantum
    /// effect in such a block can execute more than once even though it has one static instruction ID.
    /// </summary>
    public bool IsInCycle(MirBlockId block)
    {
        RequireBlock(block);
        return _successors[block].Any(
            successor => successor == block || _reachableFrom[successor].Contains(block));
    }

    public bool Dominates(MirBlockId candidate, MirBlockId block)
    {
        RequireBlock(candidate);
        RequireBlock(block);
        return _dominators[block].Contains(candidate);
    }

    public bool StrictlyDominates(MirBlockId candidate, MirBlockId block) =>
        candidate != block && Dominates(candidate, block);

    public bool PostDominates(MirBlockId candidate, MirBlockId block)
    {
        RequireBlock(candidate);
        RequireBlock(block);
        return _postDominators[block].Contains(candidate);
    }

    public bool StrictlyPostDominates(MirBlockId candidate, MirBlockId block) =>
        candidate != block && PostDominates(candidate, block);

    public MirProgramPoint PointBeforeInstruction(MirInstructionId instruction)
    {
        RequireInstruction(instruction);
        if (!_instructionPoints.TryGetValue(instruction, out var point))
            throw new ArgumentOutOfRangeException(
                nameof(instruction),
                instruction,
                $"instruction {instruction} does not belong to callable {Callable}");
        return point;
    }

    public bool TryGetPointBeforeInstruction(
        MirInstructionId instruction,
        out MirProgramPoint? point)
    {
        RequireInstruction(instruction);
        return _instructionPoints.TryGetValue(instruction, out point);
    }

    public MirProgramPoint TerminatorPoint(MirBlockId block)
    {
        RequireBlock(block);
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
        RequireValue(value);
        if (!_values.TryGetValue(value, out var definition))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"SSA value {value} does not belong to callable {Callable}");

        return definition.Definition.Kind switch
        {
            MirValueDefinitionKind.Parameter => true,
            MirValueDefinitionKind.BlockArgument =>
                IsBlockDefinitionAvailable(definition.Definition.Block, point.Block),
            MirValueDefinitionKind.InstructionResult =>
                IsInstructionDefinitionAvailable(definition.Definition.Instruction, point),
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
            || !_instructionLocations.TryGetValue(instruction, out var definition))
            return false;

        return definition.Block == usePoint.Block
            ? definition.Index < usePoint.InstructionIndex
            : _dominators[usePoint.Block].Contains(definition.Block);
    }

    private void EnsurePoint(MirProgramPoint? point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!point.BelongsTo(_snapshotIdentity))
            throw new InvalidOperationException(
                "the MIR program point belongs to a different control-flow snapshot");
    }

    internal MirProgramPoint PointBeforeInstructionLocal(MirInstructionId instruction) =>
        PointBeforeInstruction(instruction);

    internal MirProgramPoint TerminatorPointLocal(MirBlockId block) =>
        TerminatorPoint(block);

    internal bool IsValueAvailableAtLocal(MirValueId value, MirProgramPoint point) =>
        IsValueAvailableAt(value, point);

    private void RequireBlock(MirBlockId block)
    {
        if (!_blocks.ContainsKey(block))
            throw new ArgumentOutOfRangeException(
                nameof(block),
                block,
                $"block {block} does not belong to callable {Callable}");
    }

    private void RequireInstruction(MirInstructionId instruction)
    {
        if (!_instructionLocations.ContainsKey(instruction))
            throw new ArgumentOutOfRangeException(
                nameof(instruction),
                instruction,
                $"instruction {instruction} does not belong to callable {Callable}");
    }

    private void RequireValue(MirValueId value)
    {
        if (!_values.ContainsKey(value))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"SSA value {value} does not belong to callable {Callable}");
    }

    private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> items) =>
        Array.AsReadOnly(items.ToArray());

    internal readonly record struct InstructionLocation(
        MirBlockId Block,
        int Index);
}

/// <summary>
/// Builds reusable graph facts from verified MIR. The fixed-point algorithms operate on the public CFG
/// contract rather than duplicating the verifier's private diagnostic logic.
/// </summary>
internal static class MirControlFlowAnalysis
{
    internal static MirControlFlowSnapshot Analyze(
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
    /// Builds CFG facts after the structural verifier has already established that all referenced
    /// blocks, edges, values, and instruction identities exist. This entry point lets the verifier's
    /// second phase validate graph-wide contracts without recursively invoking itself.
    /// </summary>
    internal static MirControlFlowSnapshot AnalyzeUnchecked(
        MirProgram program,
        MirCallable callable)
    {
        var blocks = callable.Blocks.ToDictionary(block => block.Id);
        var values = callable.Values.ToDictionary(value => value.Id);
        var successors = blocks.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirBlockId>)Array.AsReadOnly(
                pair.Value.Terminator.Successors
                    .Distinct()
                    .OrderBy(id => id.Value)
                    .ToArray()));
        var mutablePredecessors = blocks.Keys.ToDictionary(
            block => block,
            _ => new HashSet<MirBlockId>());
        foreach (var (block, targets) in successors)
            foreach (var target in targets)
                mutablePredecessors[target].Add(block);
        var predecessors = mutablePredecessors.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MirBlockId>)Array.AsReadOnly(
                pair.Value.OrderBy(id => id.Value).ToArray()));

        var reachable = ReachableFrom(callable.EntryBlock, successors);
        var reachableFrom = blocks.Keys.ToDictionary(
            block => block,
            block => ReachableFrom(block, successors));
        var exits = reachable
            .Where(block => successors[block].Count == 0)
            .ToHashSet();
        var canReachExit = ReachableFrom(exits, predecessors);
        var dominators = ComputeDominators(
            callable.EntryBlock,
            blocks.Keys,
            reachable,
            predecessors);
        var postDominators = ComputePostDominators(
            blocks.Keys,
            reachable,
            canReachExit,
            exits,
            successors);

        var instructionLocations =
            new Dictionary<MirInstructionId, MirControlFlowSnapshot.InstructionLocation>();
        foreach (var block in callable.Blocks)
            for (var index = 0; index < block.Instructions.Count; index++)
                instructionLocations.Add(
                    block.Instructions[index].Id,
                    new MirControlFlowSnapshot.InstructionLocation(block.Id, index));

        return new MirControlFlowSnapshot(
            program,
            callable,
            blocks,
            values,
            successors,
            predecessors,
            reachableFrom,
            dominators,
            postDominators,
            reachable,
            canReachExit,
            instructionLocations);
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
