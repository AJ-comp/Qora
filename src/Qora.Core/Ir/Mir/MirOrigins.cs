using Qora.Compiler;

namespace Qora.Ir.Mir;

/// <summary>
/// One interned MIR provenance fact. A HIR origin names a node inside the HIR snapshot from which the
/// enclosing MIR snapshot was lowered. A synthesized origin points to the fact which caused it to exist
/// instead of copying that fact's source coordinates.
/// </summary>
public sealed record MirOrigin
{
    private MirOrigin(
        Qora.Ir.HirNodeId? hirNodeId,
        SourceSpan? span,
        MirOriginId? parent,
        string? synthesisReason)
    {
        HirNodeId = hirNodeId;
        Span = span;
        Parent = parent;
        SynthesisReason = synthesisReason;
    }

    public Qora.Ir.HirNodeId? HirNodeId { get; }
    public SourceSpan? Span { get; }
    public MirOriginId? Parent { get; }
    public string? SynthesisReason { get; }

    internal static MirOrigin FromHir(
        Qora.Ir.HirNodeId nodeId,
        SourceSpan? span = null) =>
        new(
            nodeId,
            span,
            parent: null,
            synthesisReason: null);

    internal static MirOrigin Synthesized(
        MirOriginId parent,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "a synthesized origin requires a reason",
                nameof(reason));

        return new(
            hirNodeId: null,
            span: null,
            parent,
            reason);
    }
}

/// <summary>
/// Immutable, program-owned provenance table. IDs are dense and parents always precede children, which
/// makes origin resolution deterministic and rules out cycles by construction.
/// </summary>
public sealed class MirOriginTable
{
    private readonly IReadOnlyList<MirOrigin> _origins;

    internal MirOriginTable(IEnumerable<MirOrigin> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var frozen = MirCollections.Freeze(origins);

        for (var index = 0; index < frozen.Count; index++)
        {
            var origin = frozen[index]
                ?? throw new ArgumentException(
                    $"origin slot {index} is null",
                    nameof(origins));
            var id = new MirOriginId(index);
            if (origin.Parent is MirOriginId parent
                && (uint)parent.Value >= (uint)index)
            {
                throw new ArgumentException(
                    $"synthesized origin {id} must name an earlier parent",
                    nameof(origins));
            }
        }

        _origins = frozen;
    }

    public IReadOnlyList<MirOrigin> Origins => _origins;

    public MirOrigin Require(MirOriginId id)
    {
        return (uint)id.Value < (uint)_origins.Count
            ? _origins[id.Value]
            : throw new ArgumentOutOfRangeException(
                nameof(id),
                id,
                $"origin {id} does not belong to this MIR origin table");
    }

    public bool Contains(MirOriginId id) =>
        (uint)id.Value < (uint)_origins.Count;

    /// <summary>Follows synthesized parents to the one authoritative HIR source fact.</summary>
    public MirOrigin ResolveHir(MirOriginId id)
    {
        var current = Require(id);
        while (current.Parent is MirOriginId parent)
            current = Require(parent);
        return current;
    }
}

/// <summary>Mutable only while one MIR program is being lowered.</summary>
internal sealed class MirOriginTableBuilder
{
    private readonly HirSnapshot _source;
    private readonly List<MirOrigin> _origins = new();
    private readonly Dictionary<HirNodeId, MirOriginId> _hirOrigins = new();
    private readonly Dictionary<SynthesizedOriginKey, MirOriginId> _synthesizedOrigins = new();

    public MirOriginTableBuilder(HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public MirOriginId Hir(HirNodeId nodeId)
    {
        ValidateHirOrigin(nodeId);

        if (_hirOrigins.TryGetValue(nodeId, out var existing)) return existing;

        return AppendHirOrigin(nodeId);
    }

    public MirOriginId Synthesized(
        MirOriginId parent,
        string reason)
    {
        if ((uint)parent.Value >= (uint)_origins.Count)
            throw new ArgumentOutOfRangeException(
                nameof(parent),
                parent,
                "a synthesized origin parent must already exist");

        var key = new SynthesizedOriginKey(parent, reason);
        if (_synthesizedOrigins.TryGetValue(key, out var existing)) return existing;

        return AppendSynthesizedOrigin(parent, reason);
    }

    public MirOriginTable Build() => new(_origins);

    public void Replay(MirOriginTable source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_origins.Count != 0)
            throw new InvalidOperationException(
                "origins can only be replayed into an empty builder");
        foreach (var origin in source.Origins)
        {
            if (origin.HirNodeId is HirNodeId nodeId)
            {
                ValidateHirOrigin(nodeId);
                AppendHirOrigin(nodeId);
                continue;
            }

            AppendSynthesizedOrigin(
                origin.Parent!.Value,
                origin.SynthesisReason!);
        }
    }

    private void ValidateHirOrigin(HirNodeId nodeId)
    {
        var callableId = _source.Structure.RequireOwningCallable(nodeId);
        if (_source.Structure.RequireKind(callableId) != HirNodeKind.Callable)
            throw new ArgumentException(
                $"HIR origin owner {callableId} is not a callable.",
                nameof(nodeId));
    }

    private MirOriginId AppendHirOrigin(HirNodeId nodeId)
    {
        var id = new MirOriginId(_origins.Count);
        var span = _source.SourceMap.Find(nodeId);
        _origins.Add(MirOrigin.FromHir(nodeId, span));
        _hirOrigins.TryAdd(nodeId, id);
        return id;
    }

    private MirOriginId AppendSynthesizedOrigin(
        MirOriginId parent,
        string reason)
    {
        var id = new MirOriginId(_origins.Count);
        _origins.Add(MirOrigin.Synthesized(parent, reason));
        _synthesizedOrigins.TryAdd(
            new SynthesizedOriginKey(parent, reason),
            id);
        return id;
    }

    private readonly record struct SynthesizedOriginKey(
        MirOriginId Parent,
        string Reason);
}
