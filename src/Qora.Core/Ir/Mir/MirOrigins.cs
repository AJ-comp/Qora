using Qora.Compiler;

namespace Qora.Ir.Mir;

/// <summary>Identifies whether an origin comes directly from HIR or was synthesized in MIR.</summary>
public enum MirOriginKind
{
    HirNode,
    Synthesized,
}

/// <summary>
/// One interned MIR provenance fact. A HIR origin names a node inside the HIR snapshot from which the
/// enclosing MIR snapshot was lowered. A synthesized origin points to the fact which caused it to exist
/// instead of copying that fact's source coordinates.
/// </summary>
public sealed record MirOrigin(
    MirOriginRef Id,
    MirOriginKind Kind,
    Qora.Ir.HirNodeId? HirCallableId,
    Qora.Ir.HirNodeId? HirNodeId,
    SourceSpan? Span,
    MirOriginRef? Parent,
    string? SynthesisReason)
{
    internal static MirOrigin FromHir(
        MirOriginRef id,
        Qora.Ir.HirNodeId callableId,
        Qora.Ir.HirNodeId nodeId,
        SourceSpan? span = null) =>
        new(
            id,
            MirOriginKind.HirNode,
            callableId,
            nodeId,
            span,
            Parent: null,
            SynthesisReason: null);

    internal static MirOrigin Synthesized(
        MirOriginRef id,
        MirOriginRef parent,
        string reason) =>
        new(
            id,
            MirOriginKind.Synthesized,
            HirCallableId: null,
            HirNodeId: null,
            Span: null,
            parent,
            reason);
}

/// <summary>
/// Immutable, program-owned provenance table. IDs are dense and parents always precede children, which
/// makes origin resolution deterministic and rules out cycles by construction.
/// </summary>
public sealed class MirOriginTable
{
    private readonly IReadOnlyList<MirOrigin> _origins;

    internal MirOriginTable(
        MirSnapshotId snapshotId,
        IEnumerable<MirOrigin> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        SnapshotId = snapshotId;
        var frozen = MirCollections.Freeze(origins);

        for (var index = 0; index < frozen.Count; index++)
        {
            var origin = frozen[index]
                ?? throw new ArgumentException(
                    $"origin slot {index} is null",
                    nameof(origins));
            var expected = new MirOriginRef(snapshotId, index);
            if (origin.Id != expected)
                throw new ArgumentException(
                    $"origin slot {index} has identity {origin.Id}; expected {expected}",
                    nameof(origins));
            if (!Enum.IsDefined(origin.Kind))
                throw new ArgumentException(
                    $"origin {origin.Id} has unknown kind {origin.Kind}",
                    nameof(origins));

            switch (origin.Kind)
            {
                case MirOriginKind.HirNode:
                    if (origin.HirCallableId is null || origin.HirNodeId is null)
                        throw new ArgumentException(
                            $"HIR origin {origin.Id} has no callable/node identity",
                            nameof(origins));
                    if (origin.Parent is not null || origin.SynthesisReason is not null)
                        throw new ArgumentException(
                            $"HIR origin {origin.Id} carries synthesized-origin fields",
                            nameof(origins));
                    break;

                case MirOriginKind.Synthesized:
                    if (origin.Parent is not MirOriginRef parent
                        || parent.Snapshot != snapshotId
                        || parent.Value >= index)
                        throw new ArgumentException(
                            $"synthesized origin {origin.Id} must name an earlier parent",
                            nameof(origins));
                    if (origin.HirCallableId is not null
                        || origin.HirNodeId is not null
                        || origin.Span is not null)
                    {
                        throw new ArgumentException(
                            $"synthesized origin {origin.Id} duplicates HIR source coordinates",
                            nameof(origins));
                    }
                    if (string.IsNullOrWhiteSpace(origin.SynthesisReason))
                        throw new ArgumentException(
                            $"synthesized origin {origin.Id} has no reason",
                            nameof(origins));
                    break;
            }
        }

        _origins = frozen;
    }

    public MirSnapshotId SnapshotId { get; }
    public IReadOnlyList<MirOrigin> Origins => _origins;

    public MirOrigin Require(MirOriginRef reference)
    {
        MirOriginValidation.RequireSnapshot(
            SnapshotId,
            reference.Snapshot,
            nameof(reference));
        return (uint)reference.Value < (uint)_origins.Count
            ? _origins[reference.Value]
            : throw new ArgumentOutOfRangeException(
                nameof(reference),
                reference,
                $"origin {reference} does not belong to snapshot {SnapshotId}");
    }

    public bool Contains(MirOriginRef reference)
    {
        MirOriginValidation.RequireSnapshot(
            SnapshotId,
            reference.Snapshot,
            nameof(reference));
        return (uint)reference.Value < (uint)_origins.Count;
    }

    /// <summary>Follows synthesized parents to the one authoritative HIR source fact.</summary>
    public MirOrigin ResolveHir(MirOriginRef reference)
    {
        var current = Require(reference);
        while (current.Kind == MirOriginKind.Synthesized)
            current = Require(current.Parent!.Value);
        return current;
    }
}

/// <summary>Mutable only while one MIR program is being lowered.</summary>
internal sealed class MirOriginTableBuilder
{
    private readonly MirSnapshotId _snapshotId;
    private readonly HirSnapshot _source;
    private readonly List<MirOrigin> _origins = new();
    private readonly Dictionary<HirOriginKey, MirOriginRef> _hirOrigins = new();
    private readonly Dictionary<SynthesizedOriginKey, MirOriginRef> _synthesizedOrigins = new();

    public MirOriginTableBuilder(
        MirSnapshotId snapshotId,
        HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (snapshotId.CompilationId != source.Id.CompilationId
            || snapshotId.CompilationRevision != source.Id.CompilationRevision)
        {
            throw new ArgumentException(
                "A MIR origin table cannot read source locations from another Compilation snapshot.",
                nameof(source));
        }

        _snapshotId = snapshotId;
        _source = source;
    }

    public MirOriginRef Hir(
        HirNodeId callableId,
        HirNodeId nodeId)
    {
        if (_source.Structure.RequireKind(callableId) != HirNodeKind.Callable)
            throw new ArgumentException(
                $"HIR origin owner {callableId} is not a callable.",
                nameof(callableId));
        if (_source.Structure.RequireOwningCallable(nodeId) != callableId)
            throw new ArgumentException(
                $"HIR node {nodeId} does not belong to callable {callableId}.",
                nameof(nodeId));

        var span = _source.SourceMap.Find(nodeId);
        var key = new HirOriginKey(callableId, nodeId, span);
        if (_hirOrigins.TryGetValue(key, out var existing)) return existing;

        var id = new MirOriginRef(_snapshotId, _origins.Count);
        _origins.Add(MirOrigin.FromHir(id, callableId, nodeId, span));
        _hirOrigins.Add(key, id);
        return id;
    }

    public MirOriginRef Synthesized(
        MirOriginRef parent,
        string reason)
    {
        MirOriginValidation.RequireSnapshot(
            _snapshotId,
            parent.Snapshot,
            nameof(parent));
        if ((uint)parent.Value >= (uint)_origins.Count)
            throw new ArgumentOutOfRangeException(
                nameof(parent),
                parent,
                "a synthesized origin parent must already exist");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "a synthesized origin requires a reason",
                nameof(reason));

        var key = new SynthesizedOriginKey(parent, reason);
        if (_synthesizedOrigins.TryGetValue(key, out var existing)) return existing;

        var id = new MirOriginRef(_snapshotId, _origins.Count);
        _origins.Add(MirOrigin.Synthesized(id, parent, reason));
        _synthesizedOrigins.Add(key, id);
        return id;
    }

    public MirOriginTable Build() => new(_snapshotId, _origins);

    private readonly record struct HirOriginKey(
        HirNodeId CallableId,
        HirNodeId NodeId,
        SourceSpan? Span);

    private readonly record struct SynthesizedOriginKey(
        MirOriginRef Parent,
        string Reason);
}
