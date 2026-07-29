using System.Collections.Immutable;
using Qora.Compiler;
using Qora.Ir.Passes;

namespace Qora.Ir.Mir;

/// <summary>
/// Stable identity of one unresolved bounds-proof obligation inside one exact MIR snapshot. It is a
/// semantic obligation rather than a string-keyed array lookup; target policies decide whether to reject
/// it or lower it to a checked access.
/// </summary>
public readonly record struct MirBoundsObligationId(int Value)
{
    public override string ToString() => $"bound{Value}";
}

/// <summary>The indexed sub-access owned by one MIR instruction.</summary>
public enum MirIndexedAccessKind
{
    ArrayLoad,
    ArrayStore,
    QubitOperand,
    Measurement,
}

/// <summary>
/// Exact identity of one indexed access. An instruction site alone is insufficient because one
/// quantum application can contain several independently indexed qubit operands. <see cref="Ordinal"/>
/// is zero for the single index of an array load/store or measurement, and is the call operand slot for
/// <see cref="MirIndexedAccessKind.QubitOperand"/>.
/// </summary>
public readonly record struct MirIndexedAccessSite
{
    public MirIndexedAccessSite(
        MirInstructionSite instruction,
        MirIndexedAccessKind kind,
        int ordinal)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "unknown MIR indexed-access kind");
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (kind != MirIndexedAccessKind.QubitOperand && ordinal != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                $"{kind} owns exactly one index at ordinal zero");
        }

        Instruction = instruction;
        Kind = kind;
        Ordinal = ordinal;
    }

    public MirInstructionSite Instruction { get; }
    public MirIndexedAccessKind Kind { get; }
    public int Ordinal { get; }

    public override string ToString() =>
        $"{Instruction}/{Kind}[{Ordinal}]";
}

/// <summary>
/// One bounds proof that the common front end could neither prove safe nor prove invalid. Diagnostic
/// context is retained at the HIR-to-MIR boundary so every backend can operate from MIR alone. Future MIR
/// rewrites which introduce indexed accesses must either prove them or add new typed obligations here.
/// </summary>
public sealed record MirBoundsObligation(
    MirBoundsObligationId Id,
    MirIndexedAccessSite Site,
    string Operation,
    string Aggregate,
    string Index,
    string? LoopBound,
    SourceSpan? Span);

/// <summary>Immutable target-independent safety facts owned by one exact MIR snapshot.</summary>
public sealed class MirSafetyFacts
{
    private readonly ImmutableArray<MirBoundsObligation> _bounds;

    internal MirSafetyFacts(
        MirSnapshotId snapshotId,
        IEnumerable<MirBoundsObligation> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        SnapshotId = snapshotId;
        _bounds = bounds.ToImmutableArray();
        for (var index = 0; index < _bounds.Length; index++)
        {
            var obligation = _bounds[index]
                ?? throw new ArgumentException(
                    $"MIR bounds obligation slot {index} is null",
                    nameof(bounds));
            if (obligation.Id != new MirBoundsObligationId(index))
            {
                throw new ArgumentException(
                    $"MIR bounds obligation slot {index} has identity {obligation.Id}",
                    nameof(bounds));
            }
        }
    }

    public MirSnapshotId SnapshotId { get; }
    public IReadOnlyList<MirBoundsObligation> UnprovenBounds => _bounds;

    internal static MirSafetyFacts FromHir(
        MirSnapshotId snapshotId,
        HirSemanticModel semantics,
        IReadOnlyDictionary<HirNodeId, MirIndexedAccessSite> loweredSites)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(loweredSites);
        var expectedSites = semantics.UnprovenIndexes
            .Select(unproven => unproven.Site)
            .ToHashSet();
        if (!expectedSites.SetEquals(loweredSites.Keys))
        {
            throw new InvalidOperationException(
                "QINTERNAL: HIR-to-MIR lowering did not translate every unproven bounds site exactly once");
        }
        return new MirSafetyFacts(
            snapshotId,
            semantics.UnprovenIndexes.Select(
                (unproven, index) => new MirBoundsObligation(
                    new MirBoundsObligationId(index),
                    loweredSites[unproven.Site],
                    unproven.Op,
                    unproven.Array,
                    unproven.Index,
                    unproven.LoopBound,
                    unproven.Span)));
    }

    internal MirSafetyFacts Rebase(MirSnapshotId snapshotId)
    {
        if (snapshotId.CompilationId != SnapshotId.CompilationId
            || snapshotId.CompilationRevision != SnapshotId.CompilationRevision
            || SnapshotId.Revision == int.MaxValue
            || snapshotId.Revision != SnapshotId.Revision + 1)
        {
            throw new ArgumentException(
                "MIR safety facts can only be rebound to the immediate next snapshot revision",
                nameof(snapshotId));
        }

        return new MirSafetyFacts(
            snapshotId,
            _bounds);
    }

    /// <summary>
    /// Rebase existing obligations and clone the obligations of every callable duplicated by an additive
    /// MIR transformation. The relation is keyed by callable IDs and exact instruction IDs; diagnostic
    /// names never decide which proof obligation follows a synthesized inverse.
    /// </summary>
    internal MirSafetyFacts CloneForAdditiveTransformation(
        MirSnapshotId snapshotId,
        IReadOnlyDictionary<MirCallableId, MirCallableId> duplicatedCallables)
    {
        ArgumentNullException.ThrowIfNull(duplicatedCallables);
        var rebased = Rebase(snapshotId).UnprovenBounds.ToList();
        foreach (var obligation in _bounds)
        {
            if (!duplicatedCallables.TryGetValue(
                    obligation.Site.Instruction.Callable,
                    out var duplicate))
            {
                continue;
            }

            rebased.Add(
                obligation with
                {
                    Id = new MirBoundsObligationId(rebased.Count),
                    Site = new MirIndexedAccessSite(
                        new MirInstructionSite(
                            duplicate,
                            obligation.Site.Instruction.Instruction),
                        obligation.Site.Kind,
                        obligation.Site.Ordinal),
                });
        }

        return new MirSafetyFacts(snapshotId, rebased);
    }

    internal static MirSafetyFacts Empty(MirSnapshotId snapshotId) =>
        new(snapshotId, Array.Empty<MirBoundsObligation>());
}
