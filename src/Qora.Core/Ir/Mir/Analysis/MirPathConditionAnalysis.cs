using System.Collections.Frozen;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// One branch outcome used by an exact MIR execution guard. The condition is the scalar SSA value
/// evaluated by the controlling branch, not a reconstructed source expression.
/// </summary>
public sealed record MirPathPredicate(
    MirBlockId Controller,
    MirValueId Condition,
    bool ExpectedValue);

/// <summary>
/// The shape of an exact block-execution condition. <see cref="All"/> and <see cref="Any"/> are
/// conjunction and disjunction respectively; keeping both is essential because a tail merge can be
/// reached through <c>a</c> OR <c>!a &amp;&amp; b</c>, which cannot be represented by one flat list of
/// required predicates.
/// </summary>
public enum MirPathConditionKind
{
    Never,
    Always,
    Predicate,
    All,
    Any,
}

/// <summary>
/// An immutable Boolean expression describing exactly when one static MIR block is entered.
/// A flat predicate list would silently turn a disjunctive merge into either an empty condition
/// ("always") or one merely sufficient arm. This expression keeps that distinction structural.
/// </summary>
public sealed class MirPathCondition
{
    private readonly IReadOnlyList<MirPathCondition> _terms;
    private readonly IReadOnlyList<MirPathPredicate> _predicates;

    private MirPathCondition(
        MirPathConditionKind kind,
        MirPathPredicate? predicate,
        IReadOnlyList<MirPathCondition> terms)
    {
        Kind = kind;
        Predicate = predicate;
        _terms = MirCollections.Freeze(terms);
        _predicates = MirCollections.Freeze(
            terms
                .SelectMany(term => term.Predicates)
                .Concat(predicate is null
                    ? Array.Empty<MirPathPredicate>()
                    : new[] { predicate })
                .Distinct()
                .OrderBy(item => item.Controller.Value)
                .ThenBy(item => item.Condition.Value)
                .ThenBy(item => item.ExpectedValue));
        Key = kind switch
        {
            MirPathConditionKind.Never => "0",
            MirPathConditionKind.Always => "1",
            MirPathConditionKind.Predicate when predicate is not null =>
                $"p:{predicate.Condition.Value}:{predicate.ExpectedValue}:{predicate.Controller.Value}",
            MirPathConditionKind.All =>
                $"&({string.Join(",", terms.Select(term => term.Key))})",
            MirPathConditionKind.Any =>
                $"|({string.Join(",", terms.Select(term => term.Key))})",
            _ => throw new ArgumentException(
                $"path condition {kind} has an invalid predicate/term shape"),
        };
    }

    public MirPathConditionKind Kind { get; }
    public MirPathPredicate? Predicate { get; }
    public IReadOnlyList<MirPathCondition> Terms => _terms;

    /// <summary>
    /// Every predicate leaf occurring in the expression. This is a witness inventory, not a conjunction;
    /// consumers deciding control flow must inspect <see cref="Kind"/> and <see cref="Terms"/>.
    /// </summary>
    public IReadOnlyList<MirPathPredicate> Predicates => _predicates;

    public bool IsNever => Kind == MirPathConditionKind.Never;
    public bool IsAlways => Kind == MirPathConditionKind.Always;

    internal string Key { get; }

    public static MirPathCondition Never { get; } = new(
        MirPathConditionKind.Never,
        predicate: null,
        Array.Empty<MirPathCondition>());

    public static MirPathCondition Always { get; } = new(
        MirPathConditionKind.Always,
        predicate: null,
        Array.Empty<MirPathCondition>());

    internal static MirPathCondition Test(MirPathPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new MirPathCondition(
            MirPathConditionKind.Predicate,
            predicate,
            Array.Empty<MirPathCondition>());
    }

    internal static MirPathCondition And(params MirPathCondition[] conditions) =>
        Combine(MirPathConditionKind.All, conditions);

    internal static MirPathCondition Or(params MirPathCondition[] conditions) =>
        Combine(MirPathConditionKind.Any, conditions);

    private static MirPathCondition Combine(
        MirPathConditionKind kind,
        IEnumerable<MirPathCondition> conditions)
    {
        var isAll = kind == MirPathConditionKind.All;
        var flattened = conditions
            .SelectMany(condition => condition.Kind == kind
                ? condition.Terms
                : new[] { condition })
            .ToArray();

        if (isAll && flattened.Any(condition => condition.IsNever))
            return Never;
        if (!isAll && flattened.Any(condition => condition.IsAlways))
            return Always;

        var identity = isAll ? Always : Never;
        var terms = MirCollections.Freeze(flattened
            .Where(condition => condition != identity)
            .GroupBy(condition => condition.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(condition => condition.Key, StringComparer.Ordinal));
        if (terms.Count == 0) return identity;
        if (terms.Count == 1) return terms[0];

        // One immutable SSA Boolean cannot be both true and false. For conjunction this is impossible;
        // for disjunction the two direct alternatives cover every value and therefore form a tautology.
        var directPredicates = terms
            .Where(condition => condition.Kind == MirPathConditionKind.Predicate)
            .Select(condition => condition.Predicate!)
            .ToArray();
        if (directPredicates
            .GroupBy(predicate => predicate.Condition)
            .Any(group => group.Select(predicate => predicate.ExpectedValue).Distinct().Count() == 2))
            return isAll ? Never : Always;

        return new MirPathCondition(
            kind,
            predicate: null,
            terms);
    }
}

/// <summary>
/// Describes whether one static MIR block can execute once or can be revisited through a CFG cycle.
/// A loop-carried effect needs a dynamic execution count (or an iteration-local cleanup point); one
/// static instruction ID is not enough to replay all of its dynamic executions.
/// </summary>
public enum MirExecutionMultiplicity
{
    Single,
    LoopCarried,
}

/// <summary>
/// Exact block-execution conditions and multiplicity facts for one callable in one immutable MIR program.
/// </summary>
public sealed class MirPathConditionSnapshot
{
    private readonly MirControlFlowSnapshot _cfg;
    private readonly FrozenDictionary<MirBlockId, MirPathCondition> _conditions;

    internal MirPathConditionSnapshot(
        MirControlFlowSnapshot cfg,
        IReadOnlyDictionary<MirBlockId, MirPathCondition> conditions)
    {
        _cfg = cfg;
        _conditions = conditions.ToFrozenDictionary();
    }

    public MirCallableId Callable => _cfg.Callable;
    internal MirControlFlowSnapshot ControlFlow => _cfg;

    internal void EnsureFor(MirProgram program, MirCallableId callable)
    {
        if (!ReferenceEquals(_cfg.SourceProgram, program)
            || !ReferenceEquals(_cfg.SourceCallable, program.FindCallable(callable)))
            throw new InvalidOperationException(
                $"the MIR path-condition analysis does not belong to callable {callable} "
                + $"in the requested MIR program; it was created for callable {Callable}");
    }

    public MirPathCondition ConditionFor(MirBlockId block) =>
        _conditions.TryGetValue(block, out var condition)
            ? condition
            : throw new ArgumentOutOfRangeException(
                nameof(block),
                block,
                $"block {block} does not belong to callable {Callable}");

    public MirExecutionMultiplicity MultiplicityOf(MirBlockId block) =>
        _cfg.IsReachable(block) && _cfg.IsInCycle(block)
            ? MirExecutionMultiplicity.LoopCarried
            : MirExecutionMultiplicity.Single;

}

/// <summary>
/// Computes each block's exact execution guard by propagating a Boolean expression through the CFG:
/// <c>Reach(successor) |= Reach(block) &amp;&amp; edgePredicate</c>. Natural-loop backedges are removed for
/// this static first-entry condition; <see cref="MirExecutionMultiplicity.LoopCarried"/> separately records
/// that the block can execute again. A block which post-dominates entry is normalized to
/// <see cref="MirPathCondition.Always"/>, while a conditional tail merge retains its disjunction.
/// </summary>
internal static class MirPathConditionAnalysis
{
    internal static MirPathConditionSnapshot Analyze(
        MirProgram program,
        MirCallableId callableId)
    {
        ArgumentNullException.ThrowIfNull(program);

        var cfg = MirControlFlowAnalysis.Analyze(program, callableId);
        return AnalyzeVerified(cfg);
    }

    /// <summary>
    /// Computes path facts from the canonical CFG of an already verified MIR snapshot.
    /// </summary>
    internal static MirPathConditionSnapshot AnalyzeVerified(
        MirControlFlowSnapshot cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var callable = cfg.SourceCallable;
        var reachable = callable.Blocks
            .Where(block => cfg.IsReachable(block.Id))
            .Select(block => block.Id)
            .ToHashSet();
        var backEdges = reachable
            .SelectMany(source => cfg.SuccessorsOf(source)
                .Where(target => reachable.Contains(target) && cfg.Dominates(target, source))
                .Select(target => (Source: source, Target: target)))
            .ToHashSet();

        var acyclicSuccessors = reachable.ToDictionary(
            block => block,
            block => cfg.SuccessorsOf(block)
                .Where(successor => reachable.Contains(successor)
                    && !backEdges.Contains((block, successor)))
                .Distinct()
                .ToArray());
        var indegree = reachable.ToDictionary(block => block, _ => 0);
        foreach (var successors in acyclicSuccessors.Values)
            foreach (var successor in successors)
                indegree[successor]++;

        var pending = new PriorityQueue<MirBlockId, int>();
        foreach (var (block, degree) in indegree)
            if (degree == 0)
                pending.Enqueue(block, block.Value);
        var topological = new List<MirBlockId>(reachable.Count);
        while (pending.TryDequeue(out var block, out _))
        {
            topological.Add(block);
            foreach (var successor in acyclicSuccessors[block])
                if (--indegree[successor] == 0)
                    pending.Enqueue(successor, successor.Value);
        }
        if (topological.Count != reachable.Count)
            throw new InvalidOperationException(
                $"QINTERNAL: callable `{callable.Name}` has an irreducible control-flow cycle; " +
                "exact MIR path conditions require reducible loops");

        var conditions = callable.Blocks.ToDictionary(
            block => block.Id,
            _ => MirPathCondition.Never);
        conditions[callable.EntryBlock.Id] = MirPathCondition.Always;

        foreach (var blockId in topological)
        {
            var condition = conditions[blockId];
            if (blockId != callable.EntryBlock.Id
                && cfg.PostDominates(blockId, callable.EntryBlock.Id))
            {
                condition = MirPathCondition.Always;
                conditions[blockId] = condition;
            }

            foreach (var (successor, edgeCondition) in Edges(
                         blockId,
                         callable.RequireBlock(blockId).Terminator))
            {
                if (!reachable.Contains(successor)
                    || backEdges.Contains((blockId, successor)))
                    continue;
                conditions[successor] = MirPathCondition.Or(
                    conditions[successor],
                    MirPathCondition.And(condition, edgeCondition));
            }
        }

        foreach (var block in reachable)
            if (conditions[block].IsNever)
                throw new InvalidOperationException(
                    $"QINTERNAL: reachable block {block} in `{callable.Name}` has no acyclic entry path");

        return new MirPathConditionSnapshot(
            cfg,
            conditions);
    }

    private static IEnumerable<(MirBlockId Successor, MirPathCondition Condition)> Edges(
        MirBlockId controller,
        MirTerminator terminator)
    {
        switch (terminator)
        {
            case MirJump jump:
                yield return (jump.Target, MirPathCondition.Always);
                break;

            case MirBranch branch:
                yield return (
                    branch.TrueTarget,
                    MirPathCondition.Test(new MirPathPredicate(
                        controller,
                        branch.Condition,
                        ExpectedValue: true)));
                yield return (
                    branch.FalseTarget,
                    MirPathCondition.Test(new MirPathPredicate(
                        controller,
                        branch.Condition,
                        ExpectedValue: false)));
                break;
        }
    }
}
