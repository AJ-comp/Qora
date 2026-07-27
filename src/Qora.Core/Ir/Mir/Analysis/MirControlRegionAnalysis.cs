using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// A verified MIR graph which is valid as CFG but cannot be represented as nested structured regions.
/// Target lowerings catch this typed failure and project it into their own diagnostic domain.
/// </summary>
internal sealed class MirControlRegionException : InvalidOperationException
{
    internal MirControlRegionException(
        MirCallableId callable,
        MirOriginRef origin,
        string detail)
        : base(
            $"callable {callable} cannot be represented as structured control flow: {detail}")
    {
        Callable = callable;
        Origin = origin;
        Detail = detail;
    }

    public MirCallableId Callable { get; }
    public MirOriginRef Origin { get; }
    public string Detail { get; }
}

/// <summary>
/// The semantic reason an edge leaves a natural loop without following its ordinary continuation.
/// New non-local control-flow forms must add a distinct case instead of being guessed by a target
/// backend from the destination block's shape.
/// </summary>
public enum MirLoopSideExitKind
{
    CallableReturn,
}

/// <summary>
/// One exact CFG edge which leaves a natural loop through non-local control flow. The source and target
/// remain snapshot-qualified MIR identities; <see cref="SuccessorOrdinal"/> identifies the corresponding
/// jump/branch successor and therefore the edge's Phi arguments.
/// </summary>
public sealed class MirLoopSideExit
{
    internal MirLoopSideExit(
        MirSnapshotId snapshotId,
        MirCallableId callable,
        MirBlockId source,
        int successorOrdinal,
        MirBlockId target,
        MirLoopSideExitKind kind)
    {
        if (successorOrdinal < 0)
            throw new ArgumentOutOfRangeException(
                nameof(successorOrdinal),
                successorOrdinal,
                "a loop side-exit successor ordinal cannot be negative");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "unknown loop side-exit kind");

        Source = new MirBlockRef(snapshotId, callable, source);
        SuccessorOrdinal = successorOrdinal;
        Target = new MirBlockRef(snapshotId, callable, target);
        Kind = kind;
    }

    public MirBlockRef Source { get; }
    public int SuccessorOrdinal { get; }
    public MirBlockRef Target { get; }
    public MirLoopSideExitKind Kind { get; }
}

internal readonly record struct MirLocalLoopSideExit(
    MirBlockId Source,
    int SuccessorOrdinal,
    MirBlockId Target,
    MirLoopSideExitKind Kind);

/// <summary>
/// One single-entry natural loop recovered from canonical MIR CFG facts. The region is derived data:
/// MIR blocks and edges remain the source of truth, while target backends consume this snapshot-bound
/// view instead of retaining a second structured control-flow tree in MIR.
/// </summary>
public sealed class MirNaturalLoopRegion
{
    private readonly FrozenSet<MirBlockId> _localBlocks;
    private readonly FrozenSet<MirBlockId> _localBackedgeSources;
    private readonly FrozenDictionary<MirBlockId, MirLoopSideExitKind> _sideExitKindsByTarget;

    internal MirNaturalLoopRegion(
        MirSnapshotId snapshotId,
        MirCallableId callable,
        MirBlockId header,
        MirBlockId? normalExit,
        IEnumerable<MirLocalLoopSideExit> sideExits,
        IEnumerable<MirBlockId> blocks,
        IEnumerable<MirBlockId> backedgeSources)
    {
        SnapshotId = snapshotId;
        Callable = new MirCallableRef(snapshotId, callable);
        Header = new MirBlockRef(snapshotId, callable, header);
        NormalExit = normalExit is MirBlockId exit
            ? new MirBlockRef(snapshotId, callable, exit)
            : null;
        _localBlocks = blocks.ToFrozenSet();
        _localBackedgeSources = backedgeSources.ToFrozenSet();
        var localSideExits = sideExits
            .OrderBy(exit => exit.Source.Value)
            .ThenBy(exit => exit.SuccessorOrdinal)
            .ThenBy(exit => exit.Target.Value)
            .ToArray();
        _sideExitKindsByTarget = localSideExits
            .GroupBy(exit => exit.Target)
            .ToFrozenDictionary(
                group => group.Key,
                group =>
                {
                    var kinds = group.Select(exit => exit.Kind).Distinct().ToArray();
                    if (kinds.Length != 1)
                    {
                        throw new ArgumentException(
                            $"loop side-exit target {group.Key} has conflicting classifications",
                            nameof(sideExits));
                    }
                    return kinds[0];
                });
        Blocks = ReadOnly(
            _localBlocks
                .OrderBy(block => block.Value)
                .Select(block => new MirBlockRef(snapshotId, callable, block))
                .ToArray());
        BackedgeSources = ReadOnly(
            _localBackedgeSources
                .OrderBy(block => block.Value)
                .Select(block => new MirBlockRef(snapshotId, callable, block))
                .ToArray());
        SideExits = ReadOnly(
            localSideExits
                .Select(exit => new MirLoopSideExit(
                    snapshotId,
                    callable,
                    exit.Source,
                    exit.SuccessorOrdinal,
                    exit.Target,
                    exit.Kind))
                .ToArray());
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    public MirBlockRef Header { get; }
    public MirBlockRef? NormalExit { get; }
    public IReadOnlyList<MirBlockRef> Blocks { get; }
    public IReadOnlyList<MirBlockRef> BackedgeSources { get; }
    public IReadOnlyList<MirLoopSideExit> SideExits { get; }

    internal MirBlockId HeaderId => Header.Block;
    internal MirBlockId? NormalExitId => NormalExit?.Block;
    internal IReadOnlySet<MirBlockId> LocalBlocks => _localBlocks;
    internal bool Contains(MirBlockId block) => _localBlocks.Contains(block);
    internal bool IsBackedgeSource(MirBlockId block) => _localBackedgeSources.Contains(block);
    internal bool TryGetSideExitKind(
        MirBlockId target,
        out MirLoopSideExitKind kind) =>
        _sideExitKindsByTarget.TryGetValue(target, out kind);

    private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> items) =>
        Array.AsReadOnly(items.ToArray());
}

/// <summary>
/// Immutable structured-region facts for one callable in one exact MIR revision. This object does not
/// copy instructions or expressions. It only classifies the canonical CFG's natural-loop boundaries;
/// SESE condition joins are queried from the exact dominance/post-dominance snapshot while lowering.
/// </summary>
public sealed class MirControlRegionSnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly MirCallable _sourceCallable;
    private readonly MirControlFlowSnapshot _controlFlow;
    private readonly FrozenDictionary<MirBlockId, MirNaturalLoopRegion> _loopsByHeader;

    internal MirControlRegionSnapshot(
        MirProgram sourceProgram,
        MirCallable sourceCallable,
        MirControlFlowSnapshot controlFlow,
        IEnumerable<MirNaturalLoopRegion> loops)
    {
        _sourceProgram = sourceProgram;
        _sourceCallable = sourceCallable;
        _controlFlow = controlFlow;
        SnapshotId = sourceProgram.SnapshotId;
        Callable = new MirCallableRef(SnapshotId, sourceCallable.Id);
        _loopsByHeader = loops.ToFrozenDictionary(loop => loop.HeaderId);
        NaturalLoops = Array.AsReadOnly(
            _loopsByHeader.Values
                .OrderBy(loop => loop.Header.Block.Value)
                .ToArray());
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    public IReadOnlyList<MirNaturalLoopRegion> NaturalLoops { get; }

    internal bool IsFor(
        MirProgram program,
        MirCallableId callable,
        MirControlFlowSnapshot controlFlow) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_sourceCallable, program.FindCallable(callable))
        && ReferenceEquals(_controlFlow, controlFlow)
        && SnapshotId == program.SnapshotId
        && Callable.Callable == callable;

    internal bool TryGetLoop(
        MirBlockId header,
        out MirNaturalLoopRegion? loop) =>
        _loopsByHeader.TryGetValue(header, out loop);
}

/// <summary>
/// Recovers natural-loop regions from canonical CFG/dominance facts. Irreducible cycles are rejected
/// explicitly: silently falling back to a program-counter interpreter would make that bridge the
/// backend's de facto long-term IR instead of keeping CFG plus derived regions authoritative.
/// </summary>
internal static class MirControlRegionAnalysis
{
    internal static MirControlRegionSnapshot AnalyzeVerified(
        MirProgram program,
        MirCallable callable,
        MirControlFlowSnapshot controlFlow)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(controlFlow);
        controlFlow.EnsureFor(program, callable.Id);

        var blocks = callable.Blocks.ToDictionary(block => block.Id);
        var reachable = controlFlow.ReachableBlocks
            .Select(reference => reference.Block)
            .ToHashSet();
        RejectIrreducibleCycles(callable, controlFlow, reachable);

        var backedges = new List<(MirBlockId Tail, MirBlockId Header)>();
        foreach (var block in reachable.OrderBy(block => block.Value))
        {
            foreach (var successor in controlFlow.SuccessorsOf(block))
            {
                if (controlFlow.Dominates(successor, block))
                    backedges.Add((block, successor));
            }
        }

        var loops = new List<MirNaturalLoopRegion>();
        foreach (var group in backedges.GroupBy(edge => edge.Header))
        {
            var header = group.Key;
            var loopBlocks = new HashSet<MirBlockId> { header };
            foreach (var (tail, _) in group)
                AddNaturalLoop(tail, header, loopBlocks, controlFlow);

            ValidateSingleEntry(callable, header, loopBlocks, controlFlow);
            var exits = ClassifyExits(
                callable,
                header,
                loopBlocks,
                group.Select(edge => edge.Tail).ToArray(),
                blocks);
            loops.Add(
                new MirNaturalLoopRegion(
                    program.SnapshotId,
                    callable.Id,
                    header,
                    exits.NormalExit,
                    exits.SideExits,
                    loopBlocks,
                    group.Select(edge => edge.Tail)));
        }

        RejectOverlappingNonNestedLoops(callable, loops);
        return new MirControlRegionSnapshot(
            program,
            callable,
            controlFlow,
            loops);
    }

    private static void AddNaturalLoop(
        MirBlockId tail,
        MirBlockId header,
        ISet<MirBlockId> loop,
        MirControlFlowSnapshot controlFlow)
    {
        if (!loop.Add(tail) || tail == header) return;
        var pending = new Stack<MirBlockId>();
        pending.Push(tail);
        while (pending.TryPop(out var current))
        {
            foreach (var predecessor in controlFlow.PredecessorsOf(current))
            {
                if (!loop.Add(predecessor) || predecessor == header) continue;
                pending.Push(predecessor);
            }
        }
    }

    private static void ValidateSingleEntry(
        MirCallable callable,
        MirBlockId header,
        IReadOnlySet<MirBlockId> loop,
        MirControlFlowSnapshot controlFlow)
    {
        foreach (var block in loop)
        {
            foreach (var predecessor in controlFlow.PredecessorsOf(block))
            {
                if (loop.Contains(predecessor) || block == header) continue;
                throw Unsupported(
                    callable,
                    $"loop headed by {header} has a second entry at {block} from {predecessor}");
            }
        }
    }

    private sealed record ClassifiedLoopExits(
        MirBlockId? NormalExit,
        IReadOnlyList<MirLocalLoopSideExit> SideExits);

    private readonly record struct LoopExitEdge(
        MirBlockId Source,
        int SuccessorOrdinal,
        MirBlockId Target);

    private static ClassifiedLoopExits ClassifyExits(
        MirCallable callable,
        MirBlockId header,
        IReadOnlySet<MirBlockId> loop,
        IReadOnlyList<MirBlockId> backedgeSources,
        IReadOnlyDictionary<MirBlockId, MirBlock> blocks)
    {
        var exitEdges = loop
            .OrderBy(block => block.Value)
            .SelectMany(source => blocks[source].Terminator.Successors
                .Select((target, ordinal) => new LoopExitEdge(source, ordinal, target)))
            .Where(edge => !loop.Contains(edge.Target))
            .ToArray();

        // A pre-test loop (while/for) exits through its header. A post-test loop (repeat) exits through
        // the latch which also owns a backedge to the header. If both shapes occur because the repeat
        // body's first statement can return, the latch remains the ordinary continuation and the header
        // edge is a typed non-local side exit.
        var latchCandidates = new List<LoopExitEdge>();
        foreach (var tail in backedgeSources)
            AddBranchExitCandidate(tail, requireInsideTarget: header, latchCandidates);

        var headerCandidates = new List<LoopExitEdge>();
        AddBranchExitCandidate(header, requireInsideTarget: null, headerCandidates);
        var canonicalCandidates = latchCandidates.Count > 0
            ? latchCandidates
            : headerCandidates;
        var candidateTargets = canonicalCandidates
            .Select(edge => edge.Target)
            .Distinct()
            .ToArray();

        if (candidateTargets.Length > 1)
        {
            throw Unsupported(
                callable,
                $"loop headed by {header} has conflicting canonical exits " +
                $"{string.Join(", ", candidateTargets.OrderBy(block => block.Value))}");
        }

        MirBlockId? normalExit = candidateTargets.Length == 1
            ? candidateTargets[0]
            : null;
        if (normalExit is null)
        {
            var continuationTargets = exitEdges
                .Where(edge => blocks[edge.Target].Terminator is not MirReturn)
                .Select(edge => edge.Target)
                .Distinct()
                .ToArray();
            if (continuationTargets.Length > 1)
            {
                throw Unsupported(
                    callable,
                    $"loop headed by {header} has no unique structured exit");
            }
            if (continuationTargets.Length == 1)
                normalExit = continuationTargets[0];
        }

        var sideExits = new List<MirLocalLoopSideExit>();
        foreach (var edge in exitEdges)
        {
            if (edge.Target == normalExit) continue;
            if (blocks[edge.Target].Terminator is not MirReturn)
            {
                throw Unsupported(
                    callable,
                    $"loop headed by {header} has an unclassified side exit from " +
                    $"{edge.Source} to {edge.Target}");
            }
            sideExits.Add(
                new MirLocalLoopSideExit(
                    edge.Source,
                    edge.SuccessorOrdinal,
                    edge.Target,
                    MirLoopSideExitKind.CallableReturn));
        }

        return new ClassifiedLoopExits(normalExit, sideExits);

        void AddBranchExitCandidate(
            MirBlockId blockId,
            MirBlockId? requireInsideTarget,
            ICollection<LoopExitEdge> candidates)
        {
            if (blocks[blockId].Terminator is not MirBranch branch) return;
            var trueInside = loop.Contains(branch.TrueTarget);
            var falseInside = loop.Contains(branch.FalseTarget);
            if (trueInside == falseInside) return;

            var inside = trueInside ? branch.TrueTarget : branch.FalseTarget;
            if (requireInsideTarget is MirBlockId required && inside != required) return;
            var ordinal = trueInside ? 1 : 0;
            candidates.Add(
                new LoopExitEdge(
                    blockId,
                    ordinal,
                    trueInside ? branch.FalseTarget : branch.TrueTarget));
        }
    }

    private static void RejectOverlappingNonNestedLoops(
        MirCallable callable,
        IReadOnlyList<MirNaturalLoopRegion> loops)
    {
        for (var leftIndex = 0; leftIndex < loops.Count; leftIndex++)
        {
            var left = loops[leftIndex].LocalBlocks;
            for (var rightIndex = leftIndex + 1; rightIndex < loops.Count; rightIndex++)
            {
                var right = loops[rightIndex].LocalBlocks;
                if (!left.Overlaps(right)) continue;
                if (left.IsSubsetOf(right) || right.IsSubsetOf(left)) continue;
                throw Unsupported(
                    callable,
                    $"natural loops {loops[leftIndex].HeaderId} and " +
                    $"{loops[rightIndex].HeaderId} overlap without nesting");
            }
        }
    }

    private static void RejectIrreducibleCycles(
        MirCallable callable,
        MirControlFlowSnapshot controlFlow,
        IReadOnlySet<MirBlockId> reachable)
    {
        foreach (var component in StronglyConnectedComponents(controlFlow, reachable))
        {
            var cyclic = component.Count > 1
                         || controlFlow.SuccessorsOf(component[0]).Contains(component[0]);
            if (!cyclic) continue;

            var members = component.ToHashSet();
            var entries = component
                .Where(
                    block => controlFlow.PredecessorsOf(block)
                        .Any(predecessor => !members.Contains(predecessor)))
                .ToArray();
            if (entries.Length != 1
                || component.Any(block => !controlFlow.Dominates(entries[0], block)))
            {
                throw Unsupported(
                    callable,
                    $"irreducible CFG cycle contains blocks " +
                    $"{string.Join(", ", component.OrderBy(block => block.Value))}");
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<MirBlockId>> StronglyConnectedComponents(
        MirControlFlowSnapshot controlFlow,
        IReadOnlySet<MirBlockId> reachable)
    {
        var nextIndex = 0;
        var indexes = new Dictionary<MirBlockId, int>();
        var lowLinks = new Dictionary<MirBlockId, int>();
        var stack = new Stack<MirBlockId>();
        var onStack = new HashSet<MirBlockId>();
        var result = new List<IReadOnlyList<MirBlockId>>();

        foreach (var block in reachable.OrderBy(block => block.Value))
            if (!indexes.ContainsKey(block))
                Visit(block);
        return result;

        void Visit(MirBlockId block)
        {
            indexes[block] = nextIndex;
            lowLinks[block] = nextIndex;
            nextIndex++;
            stack.Push(block);
            onStack.Add(block);

            foreach (var successor in controlFlow.SuccessorsOf(block))
            {
                if (!reachable.Contains(successor)) continue;
                if (!indexes.ContainsKey(successor))
                {
                    Visit(successor);
                    lowLinks[block] = Math.Min(lowLinks[block], lowLinks[successor]);
                }
                else if (onStack.Contains(successor))
                {
                    lowLinks[block] = Math.Min(lowLinks[block], indexes[successor]);
                }
            }

            if (lowLinks[block] != indexes[block]) return;
            var component = new List<MirBlockId>();
            MirBlockId member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (member != block);
            result.Add(component);
        }
    }

    private static MirControlRegionException Unsupported(
        MirCallable callable,
        string detail) =>
        new(callable.Id, callable.Origin, detail);
}
