namespace Qora.Ir.Mir;

/// <summary>
/// Replays provenance while a MIR pass constructs a fresh immutable snapshot. Replay preserves every
/// existing owner-local origin identity; newly synthesized entities append provenance facts.
/// </summary>
internal sealed class MirSnapshotTransformation
{
    private readonly MirSnapshot _source;
    private readonly MirOriginTableBuilder _origins;

    public MirSnapshotTransformation(
        MirSnapshot source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.Id.Revision == int.MaxValue)
            throw new InvalidOperationException(
                "a MIR snapshot at the maximum revision cannot be transformed");

        _origins = new MirOriginTableBuilder(
            source.LoweringSource.Source);
        _origins.Replay(source.Origins);
    }

    public MirSnapshotId Target => new(
        _source.Id.CompilationId,
        _source.Id.CompilationRevision,
        _source.Id.Revision + 1);

    public MirOriginId Synthesize(
        MirOriginId source,
        string reason)
    {
        if (!_source.Origins.Contains(source))
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source,
                "the source MIR origin does not belong to the transformed snapshot");
        return _origins.Synthesized(source, reason);
    }

    public MirOriginTable BuildOrigins() => _origins.Build();
}
