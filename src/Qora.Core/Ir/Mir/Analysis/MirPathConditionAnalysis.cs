using System.Collections.Frozen;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// One branch outcome used by an exact MIR execution guard. The condition is the scalar SSA value
/// evaluated by the controlling branch, not a reconstructed source expression.
/// </summary>
public sealed record MirPathPredicate(
    MirBlockRef Controller,
    MirValueRef Condition,
    bool ExpectedValue,
    MirBlockRef TakenSuccessor);

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
        IReadOnlyList<MirPathCondition> terms,
        string key)
    {
        Kind = kind;
        Predicate = predicate;
        _terms = Array.AsReadOnly(terms.ToArray());
        _predicates = Array.AsReadOnly(
            terms
                .SelectMany(term => term.Predicates)
                .Concat(predicate is null
                    ? Array.Empty<MirPathPredicate>()
                    : new[] { predicate })
                .Distinct()
                .OrderBy(item => item.Controller.Block.Value)
                .ThenBy(item => item.Condition.Value.Value)
                .ThenBy(item => item.ExpectedValue)
                .ThenBy(item => item.TakenSuccessor.Block.Value)
                .ToArray());
        Key = key;
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
        Array.Empty<MirPathCondition>(),
        "0");

    public static MirPathCondition Always { get; } = new(
        MirPathConditionKind.Always,
        predicate: null,
        Array.Empty<MirPathCondition>(),
        "1");

    internal static MirPathCondition Test(MirPathPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new MirPathCondition(
            MirPathConditionKind.Predicate,
            predicate,
            Array.Empty<MirPathCondition>(),
            $"p:{predicate.Condition.Value.Value}:{predicate.ExpectedValue}:{predicate.Controller.Block.Value}:{predicate.TakenSuccessor.Block.Value}");
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
        var terms = flattened
            .Where(condition => condition != identity)
            .GroupBy(condition => condition.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(condition => condition.Key, StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0) return identity;
        if (terms.Length == 1) return terms[0];

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
            terms,
            $"{(isAll ? "&" : "|")}({string.Join(",", terms.Select(term => term.Key))})");
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
/// Exact block-execution conditions and multiplicity facts for one callable in one immutable MIR revision.
/// </summary>
public sealed class MirPathConditionSnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly MirCallable _sourceCallable;
    private readonly MirControlFlowSnapshot _cfg;
    private readonly FrozenDictionary<MirBlockId, MirPathCondition> _conditions;
    private readonly FrozenDictionary<MirBlockId, MirExecutionMultiplicity> _multiplicity;

    internal MirPathConditionSnapshot(
        MirProgram sourceProgram,
        MirCallable sourceCallable,
        MirControlFlowSnapshot cfg,
        IReadOnlyDictionary<MirBlockId, MirPathCondition> conditions,
        IReadOnlyDictionary<MirBlockId, MirExecutionMultiplicity> multiplicity)
    {
        _sourceProgram = sourceProgram;
        _sourceCallable = sourceCallable;
        _cfg = cfg;
        SnapshotId = sourceProgram.SnapshotId;
        Callable = new MirCallableRef(SnapshotId, sourceCallable.Id);
        _conditions = conditions.ToFrozenDictionary();
        _multiplicity = multiplicity.ToFrozenDictionary();
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    internal MirControlFlowSnapshot ControlFlow => _cfg;

    internal bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_sourceCallable, program.FindCallable(callable))
        && SnapshotId == program.SnapshotId
        && Callable.Callable == callable;

    internal void EnsureFor(MirProgram program, MirCallableId callable)
    {
        if (!IsFor(program, callable))
            throw new InvalidOperationException(
                $"MIR path-condition snapshot belongs to {Callable} in snapshot {SnapshotId}; " +
                $"reanalyze {callable} in snapshot {program.SnapshotId}");
    }

    public MirPathCondition ConditionFor(MirBlockRef block)
    {
        var local = Require(block);
        return ConditionFor(local);
    }

    internal MirPathCondition ConditionFor(MirBlockId block) =>
        _conditions.TryGetValue(block, out var condition)
            ? condition
            : throw new ArgumentOutOfRangeException(
                nameof(block),
                block,
                $"block {block} does not belong to callable {Callable}");

    public MirExecutionMultiplicity MultiplicityOf(MirBlockRef block)
    {
        var local = Require(block);
        return MultiplicityOf(local);
    }

    internal MirExecutionMultiplicity MultiplicityOf(MirBlockId block) =>
        _multiplicity.TryGetValue(block, out var multiplicity)
            ? multiplicity
            : throw new ArgumentOutOfRangeException(
                nameof(block),
                block,
                $"block {block} does not belong to callable {Callable}");

    private MirBlockId Require(MirBlockRef block)
    {
        MirReferenceValidation.RequireSnapshot(
            SnapshotId,
            block.Snapshot,
            nameof(block));
        if (block.Callable != Callable.Callable)
            throw new ArgumentException(
                $"MIR block belongs to callable {block.Callable}; expected {Callable}",
                nameof(block));
        return block.Block;
    }
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
        QoraMirVerifier.VerifyOrThrow(program);

        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        var cfg = MirControlFlowAnalysis.Analyze(program, callableId);
        return AnalyzeVerified(program, callable, cfg);
    }

    /// <summary>
    /// Computes path facts from the canonical CFG of an already verified MIR snapshot.
    /// </summary>
    internal static MirPathConditionSnapshot AnalyzeVerified(
        MirProgram program,
        MirCallable callable,
        MirControlFlowSnapshot cfg)
    {
        cfg.EnsureFor(program, callable.Id);
        var blocks = callable.Blocks.ToDictionary(block => block.Id);
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
        conditions[callable.EntryBlock] = MirPathCondition.Always;

        foreach (var blockId in topological)
        {
            var condition = conditions[blockId];
            if (blockId != callable.EntryBlock
                && cfg.PostDominates(blockId, callable.EntryBlock))
            {
                condition = MirPathCondition.Always;
                conditions[blockId] = condition;
            }

            foreach (var (successor, edgeCondition) in Edges(
                         program.SnapshotId,
                         callable.Id,
                         blockId,
                         blocks[blockId].Terminator))
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

        var multiplicity = callable.Blocks.ToDictionary(
            block => block.Id,
            block => cfg.IsReachable(block.Id) && cfg.IsInCycle(block.Id)
                ? MirExecutionMultiplicity.LoopCarried
                : MirExecutionMultiplicity.Single);
        return new MirPathConditionSnapshot(
            program,
            callable,
            cfg,
            conditions,
            multiplicity);
    }

    private static IEnumerable<(MirBlockId Successor, MirPathCondition Condition)> Edges(
        MirSnapshotId snapshotId,
        MirCallableId callable,
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
                        new MirBlockRef(snapshotId, callable, controller),
                        new MirValueRef(snapshotId, callable, branch.Condition),
                        ExpectedValue: true,
                        new MirBlockRef(snapshotId, callable, branch.TrueTarget))));
                yield return (
                    branch.FalseTarget,
                    MirPathCondition.Test(new MirPathPredicate(
                        new MirBlockRef(snapshotId, callable, controller),
                        new MirValueRef(snapshotId, callable, branch.Condition),
                        ExpectedValue: false,
                        new MirBlockRef(snapshotId, callable, branch.FalseTarget))));
                break;
        }
    }
}
