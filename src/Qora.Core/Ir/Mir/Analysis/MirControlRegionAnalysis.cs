using System.Collections.Frozen;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// A verified MIR graph which is valid as CFG but cannot be represented as nested structured regions.
/// Target lowerings catch this typed failure and project it into their own diagnostic domain.
/// </summary>
internal sealed class MirControlRegionException : InvalidOperationException
{
    internal MirControlRegionException(
        MirOriginId origin,
        string detail)
        : base($"MIR cannot be represented as structured control flow: {detail}")
    {
        Origin = origin;
        Detail = detail;
    }

    public MirOriginId Origin { get; }
    public string Detail { get; }
}

/// <summary>
/// One single-entry natural loop recovered from canonical MIR CFG facts. The region is derived data:
/// MIR blocks and edges remain the source of truth, while target backends consume this snapshot-bound
/// view instead of retaining a second structured control-flow tree in MIR.
/// </summary>
internal sealed class MirNaturalLoopRegion
{
    private readonly FrozenSet<MirBlockId> _blocks;
    private readonly FrozenSet<MirBlockId> _callableReturnTargets;

    internal MirNaturalLoopRegion(
        MirBlockId header,
        MirBlockId? normalExit,
        IEnumerable<MirBlockId> blocks,
        IEnumerable<MirBlockId> callableReturnTargets)
    {
        Header = header;
        NormalExit = normalExit;
        _blocks = blocks.ToFrozenSet();
        _callableReturnTargets = callableReturnTargets.ToFrozenSet();
    }

    internal MirBlockId Header { get; }
    internal MirBlockId? NormalExit { get; }
    internal bool Contains(MirBlockId block) => _blocks.Contains(block);
    internal bool Overlaps(MirNaturalLoopRegion other) =>
        _blocks.Overlaps(other._blocks);
    internal bool IsSubsetOf(MirNaturalLoopRegion other) =>
        _blocks.IsSubsetOf(other._blocks);
    internal bool IsCallableReturnSideExit(MirBlockId target) =>
        _callableReturnTargets.Contains(target);
}

/// <summary>
/// Immutable structured-region facts for one callable in one exact MIR revision. This object does not
/// copy instructions or expressions. It only classifies the canonical CFG's natural-loop boundaries;
/// SESE condition joins are queried from the exact dominance/post-dominance snapshot while lowering.
/// </summary>
public sealed class MirControlRegionSnapshot
{
    private readonly MirControlFlowSnapshot _controlFlow;
    private readonly FrozenDictionary<MirBlockId, MirNaturalLoopRegion> _loopsByHeader;

    internal MirControlRegionSnapshot(
        MirControlFlowSnapshot controlFlow,
        IEnumerable<MirNaturalLoopRegion> loops)
    {
        _controlFlow = controlFlow;
        _loopsByHeader = loops.ToFrozenDictionary(loop => loop.Header);
    }

    public MirSnapshotId SnapshotId => _controlFlow.SnapshotId;
    public MirCallableId Callable => _controlFlow.Callable;
    internal bool HasNaturalLoops => _loopsByHeader.Count != 0;

    internal bool IsFor(
        MirProgram program,
        MirCallableId callable,
        MirControlFlowSnapshot controlFlow) =>
        ReferenceEquals(_controlFlow, controlFlow)
        && _controlFlow.IsFor(program, callable);

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

        var reachable = controlFlow.ReachableBlocks
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
                group.Select(edge => edge.Tail).ToArray());
            loops.Add(
                new MirNaturalLoopRegion(
                    header,
                    exits.NormalExit,
                    loopBlocks,
                    exits.CallableReturnTargets));
        }

        RejectOverlappingNonNestedLoops(callable, loops);
        return new MirControlRegionSnapshot(
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
        IReadOnlyCollection<MirBlockId> CallableReturnTargets);

    private readonly record struct LoopExitEdge(
        MirBlockId Source,
        MirBlockId Target);

    private static ClassifiedLoopExits ClassifyExits(
        MirCallable callable,
        MirBlockId header,
        IReadOnlySet<MirBlockId> loop,
        IReadOnlyList<MirBlockId> backedgeSources)
    {
        var exitEdges = loop
            .OrderBy(block => block.Value)
            .SelectMany(source => callable.RequireBlock(source).Terminator.Successors
                .Select(target => new LoopExitEdge(source, target)))
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
                .Where(edge => callable.RequireBlock(edge.Target).Terminator is not MirReturn)
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

        var callableReturnTargets = new HashSet<MirBlockId>();
        foreach (var edge in exitEdges)
        {
            if (edge.Target == normalExit) continue;
            if (callable.RequireBlock(edge.Target).Terminator is not MirReturn)
            {
                throw Unsupported(
                    callable,
                    $"loop headed by {header} has an unclassified side exit from " +
                    $"{edge.Source} to {edge.Target}");
            }
            callableReturnTargets.Add(edge.Target);
        }

        return new ClassifiedLoopExits(normalExit, callableReturnTargets);

        void AddBranchExitCandidate(
            MirBlockId blockId,
            MirBlockId? requireInsideTarget,
            ICollection<LoopExitEdge> candidates)
        {
            if (callable.RequireBlock(blockId).Terminator is not MirBranch branch) return;
            var trueInside = loop.Contains(branch.TrueTarget);
            var falseInside = loop.Contains(branch.FalseTarget);
            if (trueInside == falseInside) return;

            var inside = trueInside ? branch.TrueTarget : branch.FalseTarget;
            if (requireInsideTarget is MirBlockId required && inside != required) return;
            candidates.Add(
                new LoopExitEdge(
                    blockId,
                    trueInside ? branch.FalseTarget : branch.TrueTarget));
        }
    }

    private static void RejectOverlappingNonNestedLoops(
        MirCallable callable,
        IReadOnlyList<MirNaturalLoopRegion> loops)
    {
        for (var leftIndex = 0; leftIndex < loops.Count; leftIndex++)
        {
            var left = loops[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < loops.Count; rightIndex++)
            {
                var right = loops[rightIndex];
                if (!left.Overlaps(right)) continue;
                if (left.IsSubsetOf(right) || right.IsSubsetOf(left)) continue;
                throw Unsupported(
                    callable,
                    $"natural loops {loops[leftIndex].Header} and " +
                    $"{loops[rightIndex].Header} overlap without nesting");
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
        new(callable.Origin, detail);
}
