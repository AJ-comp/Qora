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
        Callable = new MirCallableRef(snapshotId, callable);
        Block = new MirBlockRef(snapshotId, callable, block);
        InstructionIndex = instructionIndex;
        Instruction = instruction is MirInstructionId id
            ? new MirInstructionRef(snapshotId, callable, id)
            : null;
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    public MirBlockRef Block { get; }

    /// <summary>
    /// The number of ordinary instructions which have completed before this point.
    /// </summary>
    public int InstructionIndex { get; }

    public MirInstructionRef? Instruction { get; }
    public bool IsBeforeInstruction => Instruction is not null;
    public bool IsTerminator => Instruction is null;

    internal bool BelongsTo(object snapshotIdentity) =>
        ReferenceEquals(_snapshotIdentity, snapshotIdentity);

    public override string ToString() =>
        Instruction is MirInstructionRef instruction
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
        Callable = new MirCallableRef(SnapshotId, sourceCallable.Id);
        EntryBlock = BlockRef(sourceCallable.EntryBlock);

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
                .Select(BlockRef)
                .ToArray());
        ReachableBlocks = ReadOnly(
            reachable
                .OrderBy(id => id.Value)
                .Select(BlockRef)
                .ToArray());
        ExitBlocks = ReadOnly(
            reachable
                .Where(block => successors[block].Count == 0)
                .OrderBy(id => id.Value)
                .Select(BlockRef)
                .ToArray());

        _instructionPoints = instructionLocations.ToFrozenDictionary(
            pair => pair.Key,
            pair => new MirProgramPoint(
                _snapshotIdentity,
                SnapshotId,
                Callable.Callable,
                pair.Value.Block,
                pair.Value.Index,
                pair.Key));
        _terminatorPoints = blocks.ToFrozenDictionary(
            pair => pair.Key,
            pair => new MirProgramPoint(
                _snapshotIdentity,
                SnapshotId,
                Callable.Callable,
                pair.Key,
                pair.Value.Instructions.Count,
                instruction: null));
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    public MirBlockRef EntryBlock { get; }
    public IReadOnlyList<MirBlockRef> Blocks { get; }
    public IReadOnlyList<MirBlockRef> ReachableBlocks { get; }

    /// <summary>
    /// Reachable blocks with no CFG successor. Both <see cref="MirReturn"/> and
    /// <see cref="MirUnreachable"/> are exits for structural post-dominance.
    /// </summary>
    public IReadOnlyList<MirBlockRef> ExitBlocks { get; }

    internal bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_sourceCallable, program.FindCallable(callable))
        && SnapshotId == program.SnapshotId
        && Callable.Callable == callable;

    internal void EnsureFor(MirProgram program, MirCallableId callable)
    {
        if (!IsFor(program, callable))
            throw new InvalidOperationException(
                $"MIR control-flow snapshot belongs to {Callable} in snapshot {SnapshotId}; " +
                $"reanalyze {callable} in snapshot {program.SnapshotId}");
    }

    public bool IsReachable(MirBlockRef block)
    {
        var local = RequireBlock(block);
        return _reachable.Contains(local);
    }

    /// <summary>
    /// True when at least one path from the block reaches a return or unreachable terminator.
    /// A false result commonly identifies a non-terminating loop.
    /// </summary>
    public bool CanReachExit(MirBlockRef block)
    {
        var local = RequireBlock(block);
        return _canReachExit.Contains(local);
    }

    public IReadOnlyList<MirBlockRef> SuccessorsOf(MirBlockRef block)
    {
        var local = RequireBlock(block);
        return ReadOnly(_successors[local].Select(BlockRef).ToArray());
    }

    public IReadOnlyList<MirBlockRef> PredecessorsOf(MirBlockRef block)
    {
        var local = RequireBlock(block);
        return ReadOnly(_predecessors[local].Select(BlockRef).ToArray());
    }

    /// <summary>
    /// True when control can reach <paramref name="target"/> from <paramref name="source"/>.
    /// A block reaches itself by the empty path. Use <see cref="IsInCycle"/> when a non-empty
    /// path back to the same block is required.
    /// </summary>
    public bool CanReach(MirBlockRef source, MirBlockRef target)
    {
        var localSource = RequireBlock(source);
        var localTarget = RequireBlock(target);
        return _reachableFrom[localSource].Contains(localTarget);
    }

    /// <summary>
    /// True when the block belongs to a non-trivial CFG cycle or has a self edge. A quantum
    /// effect in such a block can execute more than once even though it has one static instruction ID.
    /// </summary>
    public bool IsInCycle(MirBlockRef block)
    {
        var local = RequireBlock(block);
        return _successors[local].Any(
            successor => successor == local || _reachableFrom[successor].Contains(local));
    }

    public bool Dominates(MirBlockRef candidate, MirBlockRef block)
    {
        var localCandidate = RequireBlock(candidate);
        var localBlock = RequireBlock(block);
        return _dominators[localBlock].Contains(localCandidate);
    }

    public bool StrictlyDominates(MirBlockRef candidate, MirBlockRef block) =>
        candidate != block && Dominates(candidate, block);

    public bool PostDominates(MirBlockRef candidate, MirBlockRef block)
    {
        var localCandidate = RequireBlock(candidate);
        var localBlock = RequireBlock(block);
        return _postDominators[localBlock].Contains(localCandidate);
    }

    public bool StrictlyPostDominates(MirBlockRef candidate, MirBlockRef block) =>
        candidate != block && PostDominates(candidate, block);

    public MirProgramPoint PointBeforeInstruction(MirInstructionRef instruction)
    {
        var local = RequireInstruction(instruction);
        if (!_instructionPoints.TryGetValue(local, out var point))
            throw new ArgumentOutOfRangeException(
                nameof(instruction),
                instruction,
                $"instruction {instruction} does not belong to callable {Callable}");
        return point;
    }

    public bool TryGetPointBeforeInstruction(
        MirInstructionRef instruction,
        out MirProgramPoint? point)
    {
        var local = RequireInstruction(instruction);
        return _instructionPoints.TryGetValue(local, out point);
    }

    public MirProgramPoint TerminatorPoint(MirBlockRef block)
    {
        var local = RequireBlock(block);
        return _terminatorPoints[local];
    }

    /// <summary>
    /// Determines whether the exact SSA value is defined on every path reaching the point. Parameters are
    /// callable-wide. Block arguments are available from block entry. An instruction result is available
    /// only after its defining instruction, and cross-block definitions must dominate the use block.
    /// </summary>
    public bool IsValueAvailableAt(MirValueRef value, MirProgramPoint point)
    {
        EnsurePoint(point);
        var local = RequireValue(value);
        if (!_values.TryGetValue(local, out var definition))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"SSA value {value} does not belong to callable {Callable}");

        return definition.Definition.Kind switch
        {
            MirValueDefinitionKind.Parameter => true,
            MirValueDefinitionKind.BlockArgument =>
                IsBlockDefinitionAvailable(definition.Definition.Block, point.Block.Block),
            MirValueDefinitionKind.InstructionResult =>
                IsInstructionDefinitionAvailable(definition.Definition.Instruction, point),
            _ => false,
        };
    }

    public bool IsValueAvailableBeforeInstruction(
        MirValueRef value,
        MirInstructionRef instruction) =>
        IsValueAvailableAt(value, PointBeforeInstruction(instruction));

    public bool IsValueAvailableAtTerminator(
        MirValueRef value,
        MirBlockRef block) =>
        IsValueAvailableAt(value, TerminatorPoint(block));

    internal bool IsValueAvailableBeforeInstruction(
        MirValueId value,
        MirInstructionId instruction) =>
        IsValueAvailableAt(value, PointBeforeInstruction(instruction));

    internal bool IsValueAvailableAtTerminator(
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

        return definition.Block == usePoint.Block.Block
            ? definition.Index < usePoint.InstructionIndex
            : _dominators[usePoint.Block.Block].Contains(definition.Block);
    }

    private void EnsurePoint(MirProgramPoint? point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!point.BelongsTo(_snapshotIdentity))
            throw new InvalidOperationException(
                "the MIR program point belongs to a different control-flow snapshot");
    }

    internal bool IsInCycleLocal(MirBlockId block) =>
        IsInCycle(BlockRef(block));

    internal bool IsReachable(MirBlockId block) =>
        IsReachable(BlockRef(block));

    internal bool CanReachExit(MirBlockId block) =>
        CanReachExit(BlockRef(block));

    internal IReadOnlyList<MirBlockId> SuccessorsOf(MirBlockId block)
    {
        RequireBlock(BlockRef(block));
        return _successors[block];
    }

    internal IReadOnlyList<MirBlockId> PredecessorsOf(MirBlockId block)
    {
        RequireBlock(BlockRef(block));
        return _predecessors[block];
    }

    internal bool CanReach(MirBlockId source, MirBlockId target) =>
        CanReach(BlockRef(source), BlockRef(target));

    internal bool IsInCycle(MirBlockId block) =>
        IsInCycle(BlockRef(block));

    internal bool Dominates(MirBlockId candidate, MirBlockId block) =>
        Dominates(BlockRef(candidate), BlockRef(block));

    internal bool PostDominates(MirBlockId candidate, MirBlockId block) =>
        PostDominates(BlockRef(candidate), BlockRef(block));

    internal MirProgramPoint PointBeforeInstruction(MirInstructionId instruction) =>
        PointBeforeInstruction(InstructionRef(instruction));

    internal MirProgramPoint TerminatorPoint(MirBlockId block) =>
        TerminatorPoint(BlockRef(block));

    internal bool IsValueAvailableAt(MirValueId value, MirProgramPoint point) =>
        IsValueAvailableAt(ValueRef(value), point);

    internal MirProgramPoint PointBeforeInstructionLocal(MirInstructionId instruction) =>
        PointBeforeInstruction(InstructionRef(instruction));

    internal MirProgramPoint TerminatorPointLocal(MirBlockId block) =>
        TerminatorPoint(BlockRef(block));

    internal bool IsValueAvailableAtLocal(MirValueId value, MirProgramPoint point) =>
        IsValueAvailableAt(ValueRef(value), point);

    private MirBlockId RequireBlock(MirBlockRef block)
    {
        RequireCallable(block.Snapshot, block.Callable, nameof(block));
        if (!_blocks.ContainsKey(block.Block))
            throw new ArgumentOutOfRangeException(
                nameof(block),
                block,
                $"block {block} does not belong to callable {Callable}");
        return block.Block;
    }

    private MirInstructionId RequireInstruction(MirInstructionRef instruction)
    {
        RequireCallable(instruction.Snapshot, instruction.Callable, nameof(instruction));
        return instruction.Instruction;
    }

    private MirValueId RequireValue(MirValueRef value)
    {
        RequireCallable(value.Snapshot, value.Callable, nameof(value));
        return value.Value;
    }

    private void RequireCallable(
        MirSnapshotId snapshot,
        MirCallableId callable,
        string parameter)
    {
        MirReferenceValidation.RequireSnapshot(SnapshotId, snapshot, parameter);
        if (callable != Callable.Callable)
            throw new ArgumentException(
                $"MIR reference belongs to callable {callable}; expected {Callable}",
                parameter);
    }

    private MirBlockRef BlockRef(MirBlockId block) =>
        new(SnapshotId, Callable.Callable, block);

    private MirInstructionRef InstructionRef(MirInstructionId instruction) =>
        new(SnapshotId, Callable.Callable, instruction);

    private MirValueRef ValueRef(MirValueId value) =>
        new(SnapshotId, Callable.Callable, value);

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
