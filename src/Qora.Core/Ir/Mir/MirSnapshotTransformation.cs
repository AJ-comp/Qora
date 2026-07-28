namespace Qora.Ir.Mir;

/// <summary>
/// Owns provenance rebasing while a MIR pass constructs a fresh immutable snapshot. MIR origin
/// references are snapshot-qualified, so even unchanged entities must be rebound; synthesized entities
/// then point to those rebound facts instead of copying source coordinates.
/// </summary>
internal sealed class MirSnapshotTransformation
{
    private readonly MirSnapshot _source;
    private readonly MirSnapshotId _target;
    private readonly MirOriginTableBuilder _origins;
    private readonly Dictionary<MirOriginRef, MirOriginRef> _rebased = new();

    public MirSnapshotTransformation(
        MirSnapshot source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.Id.Revision == int.MaxValue)
            throw new InvalidOperationException(
                "a MIR snapshot at the maximum revision cannot be transformed");

        _target = new MirSnapshotId(
            source.Id.CompilationId,
            source.Id.CompilationRevision,
            source.Id.Revision + 1);
        _origins = new MirOriginTableBuilder(
            _target,
            source.Links.LoweredFromSnapshot);

        // Parent origins precede children by contract, so one forward traversal can reproduce the
        // entire origin DAG with target-snapshot-qualified references.
        foreach (var origin in source.Origins.Origins)
        {
            var rebound = origin.Kind switch
            {
                MirOriginKind.HirNode =>
                    _origins.Hir(
                        origin.HirCallableId!.Value,
                        origin.HirNodeId!.Value),
                MirOriginKind.Synthesized =>
                    _origins.Synthesized(
                        RequireRebased(origin.Parent!.Value),
                        origin.SynthesisReason!),
                _ => throw new InvalidOperationException(
                    $"unknown MIR origin kind {origin.Kind}"),
            };
            _rebased.Add(origin.Id, rebound);
        }
    }

    public MirSnapshotId Target => _target;

    public MirOriginRef Rebase(MirOriginRef source)
    {
        MirReferenceValidation.RequireSnapshot(
            _source.Id,
            source.Snapshot,
            nameof(source));
        return RequireRebased(source);
    }

    public MirOriginRef Synthesize(
        MirOriginRef source,
        string reason) =>
        _origins.Synthesized(Rebase(source), reason);

    public MirOriginTable BuildOrigins() => _origins.Build();

    private MirOriginRef RequireRebased(MirOriginRef source) =>
        _rebased.TryGetValue(source, out var rebound)
            ? rebound
            : throw new ArgumentOutOfRangeException(
                nameof(source),
                source,
                "the source MIR origin does not belong to the transformed snapshot");
}
