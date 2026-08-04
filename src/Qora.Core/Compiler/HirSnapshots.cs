using System.Collections.Frozen;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Compiler;

/// <summary>
/// A structural HIR pipeline milestone. Several milestones may name the same snapshot when a pass proves
/// that no tree rewrite was necessary; analysis itself is not a HIR stage because it does not create a
/// new <see cref="HirProgram"/>.
/// </summary>
public enum HirStage
{
    Lowered,
    ImportsExpanded,
    Resolved,
    Specialized,
}

/// <summary>The kind of stable-ID-bearing node stored in a HIR snapshot.</summary>
public enum HirNodeKind
{
    Program,
    Declaration,
    NamespaceDeclaration,
    Callable,
    Parameter,
    Block,
    Statement,
    Import,
    Open,
    Argument,
    Expression,
}

/// <summary>
/// Immutable structural view of one exact HIR generation. The child relation on <see cref="HirNode"/> is
/// the sole tree authority; this index derives membership, parents, owning callables, and node kinds from
/// the selected root. It therefore rejects a duplicate occurrence, a shared child, a cycle, or a node
/// stamped by another construction authority before any semantic side table can observe the tree.
/// </summary>
public sealed class HirStructuralIndex
{
    private readonly FrozenDictionary<HirNodeId, HirNode> _nodes;
    private readonly FrozenDictionary<HirNodeId, HirNodeKind> _kinds;
    private readonly FrozenDictionary<HirNodeId, HirNodeId> _parents;
    private readonly FrozenDictionary<HirNodeId, HirNodeId> _owningCallables;

    internal HirStructuralIndex(HirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var core = program.Core;
        var nodes = new Dictionary<HirNodeId, HirNode>();
        var kinds = new Dictionary<HirNodeId, HirNodeKind>();
        var parents = new Dictionary<HirNodeId, HirNodeId>();
        var owningCallables = new Dictionary<HirNodeId, HirNodeId>();
        var active = new HashSet<HirNode>(ReferenceEqualityComparer.Instance);

        void Visit(
            HirNode node,
            HirNodeId? parent,
            HirNodeId? owningCallable)
        {
            if (node is null)
                throw new ArgumentException(
                    "A HIR child relation contains a null node.",
                    nameof(program));
            if (!ReferenceEquals(node.Core, core))
            {
                throw new ArgumentException(
                    $"HIR node {node.Id} belongs to another construction authority.",
                    nameof(program));
            }
            if (!node.CreationSession.IsSealed)
            {
                throw new ArgumentException(
                    $"HIR node {node.Id} belongs to a construction session that has not published "
                    + "its result.",
                    nameof(program));
            }
            if (!active.Add(node))
            {
                throw new ArgumentException(
                    $"HIR node {node.Id} forms a cycle in one snapshot.",
                    nameof(program));
            }
            if (!nodes.TryAdd(node.Id, node))
            {
                active.Remove(node);
                throw new ArgumentException(
                    $"HIR node identity {node.Id} occurs more than once in one snapshot; "
                    + "one source occurrence cannot have multiple parents.",
                    nameof(program));
            }

            var kind = KindOf(node);
            kinds.Add(node.Id, kind);
            if (parent is { } parentId)
                parents.Add(node.Id, parentId);

            var effectiveOwner = node is HirCallable
                ? node.Id
                : owningCallable;
            if (effectiveOwner is { } callableId)
                owningCallables.Add(node.Id, callableId);

            IEnumerable<HirNode> children;
            try
            {
                children = node.Children()
                    ?? throw new ArgumentException(
                        $"HIR node {node.Id} returned a null child sequence.",
                        nameof(program));
                foreach (var child in children)
                {
                    if (child is null)
                        throw new ArgumentException(
                            $"HIR node {node.Id} contains a null child.",
                            nameof(program));
                    Visit(child, node.Id, effectiveOwner);
                }
            }
            finally
            {
                active.Remove(node);
            }
        }

        Visit(program, parent: null, owningCallable: null);

        RootId = program.Id;
        _nodes = nodes.ToFrozenDictionary();
        _kinds = kinds.ToFrozenDictionary();
        _parents = parents.ToFrozenDictionary();
        _owningCallables = owningCallables.ToFrozenDictionary();
    }

    public HirNodeId RootId { get; }
    public IReadOnlyCollection<HirNodeId> NodeIds => _nodes.Keys;
    public IReadOnlyDictionary<HirNodeId, HirNode> Nodes => _nodes;
    public IReadOnlyDictionary<HirNodeId, HirNodeId> Parents => _parents;
    public IReadOnlyDictionary<HirNodeId, HirNodeId> OwningCallables =>
        _owningCallables;

    public bool Contains(HirNodeId nodeId) => _nodes.ContainsKey(nodeId);

    public HirNode? FindNode(HirNodeId nodeId) =>
        _nodes.GetValueOrDefault(nodeId);

    public HirNode RequireNode(HirNodeId nodeId) =>
        FindNode(nodeId)
        ?? throw new ArgumentOutOfRangeException(
            nameof(nodeId),
            nodeId,
            "the HIR node does not belong to this snapshot");

    public HirNodeKind RequireKind(HirNodeId nodeId) =>
        _kinds.TryGetValue(nodeId, out var kind)
            ? kind
            : throw new ArgumentOutOfRangeException(
                nameof(nodeId),
                nodeId,
                "the HIR node does not belong to this snapshot");

    public HirNodeId? ParentOf(HirNodeId nodeId)
    {
        _ = RequireNode(nodeId);
        return _parents.TryGetValue(nodeId, out var parent)
            ? parent
            : null;
    }

    public HirNodeId RequireParent(HirNodeId nodeId) =>
        ParentOf(nodeId)
        ?? throw new ArgumentOutOfRangeException(
            nameof(nodeId),
            nodeId,
            "the HIR root node has no parent");

    public HirNodeId? OwningCallableOf(HirNodeId nodeId)
    {
        _ = RequireNode(nodeId);
        return _owningCallables.TryGetValue(nodeId, out var owner)
            ? owner
            : null;
    }

    public HirNodeId RequireOwningCallable(HirNodeId nodeId) =>
        OwningCallableOf(nodeId)
        ?? throw new ArgumentOutOfRangeException(
            nameof(nodeId),
            nodeId,
            "the HIR node has no owning callable");

    private static HirNodeKind KindOf(HirNode node) => node switch
    {
        HirProgram => HirNodeKind.Program,
        HirNamespaceDeclaration => HirNodeKind.NamespaceDeclaration,
        HirCallable => HirNodeKind.Callable,
        HirDeclaration => HirNodeKind.Declaration,
        HirParameter => HirNodeKind.Parameter,
        HirBlock => HirNodeKind.Block,
        HirStatement => HirNodeKind.Statement,
        HirImportDirective => HirNodeKind.Import,
        HirOpenDirective => HirNodeKind.Open,
        HirArgument => HirNodeKind.Argument,
        HirExpression => HirNodeKind.Expression,
        _ => throw new ArgumentException(
            $"Unknown HIR node kind `{node.GetType().Name}`.",
            nameof(node)),
    };
}

/// <summary>
/// Immutable source map for every identity-bearing HIR node which still has a source location. The key is
/// deliberately the general HIR node identity rather than a statement-specific type, so operations,
/// parameters, and future identity-bearing expressions use the same query surface.
/// </summary>
public sealed class HirSourceMap
{
    private readonly FrozenDictionary<HirNodeId, SourceSpan> _spans;

    internal HirSourceMap(
        HirSnapshotId snapshot,
        HirStructuralIndex structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var spans = new Dictionary<HirNodeId, SourceSpan>();
        foreach (var nodeId in structure.NodeIds)
        {
            var node = structure.RequireNode(nodeId);
            if (node.Span is not { } source)
                continue;
            if (source.Document.CompilationId != snapshot.CompilationId
                || source.Document.CompilationRevision != snapshot.CompilationRevision)
            {
                throw new ArgumentException(
                    $"HIR node {nodeId} carries a source span from another Compilation snapshot.",
                    nameof(structure));
            }
            spans.Add(nodeId, source);
        }

        _spans = spans.ToFrozenDictionary();
    }

    public IReadOnlyDictionary<HirNodeId, SourceSpan> Spans => _spans;

    public SourceSpan? Find(HirNodeId nodeId) =>
        _spans.TryGetValue(nodeId, out var span) ? span : null;

    public SourceSpan Require(HirNodeId nodeId) =>
        Find(nodeId)
        ?? throw new ArgumentOutOfRangeException(
            nameof(nodeId),
            nodeId,
            "the HIR node has no source span");
}

/// <summary>
/// One immutable HIR tree generation. Semantic and effect facts are separate artifacts qualified by
/// <see cref="Id"/>; attaching a later analysis can therefore never change an earlier tree snapshot.
/// </summary>
public sealed class HirSnapshot
{
    internal HirSnapshot(
        HirSnapshotId id,
        HirStage producedBy,
        HirProgram program,
        HirSnapshotId? parent)
    {
        if (!Enum.IsDefined(producedBy))
            throw new ArgumentOutOfRangeException(
                nameof(producedBy),
                producedBy,
                "unknown HIR stage");

        Id = id;
        ProducedBy = producedBy;
        Program = program ?? throw new ArgumentNullException(nameof(program));
        Parent = parent;

        if (program.Core.CompilationId != id.CompilationId
            || program.Core.CompilationRevision != id.CompilationRevision)
        {
            throw new ArgumentException(
                "A HIR root must belong to the exact Compilation revision named by its snapshot.",
                nameof(program));
        }
        if (!program.CreationSession.IsSealed)
        {
            throw new ArgumentException(
                "A HIR snapshot can only publish a root from a sealed construction session.",
                nameof(program));
        }
        if (parent is { } parentId
            && (parentId.CompilationId != id.CompilationId
                || parentId.CompilationRevision != id.CompilationRevision))
        {
            throw new ArgumentException(
                "A HIR snapshot parent must belong to the same Compilation snapshot.",
                nameof(parent));
        }

        Structure = new HirStructuralIndex(program);
        SourceMap = new HirSourceMap(id, Structure);
    }

    public HirSnapshotId Id { get; }

    /// <summary>The pass which first created this tree generation.</summary>
    public HirStage ProducedBy { get; }

    public HirProgram Program { get; }
    public HirSnapshotId? Parent { get; }
    public HirStructuralIndex Structure { get; }
    public HirSourceMap SourceMap { get; }
    internal HirConstructionCore ConstructionCore => Program.Core;
}

/// <summary>
/// The semantic fact set attached to one exact HIR generation. Validation and effect analysis are separate
/// artifacts even when they share immutable validation facts internally.
/// </summary>
public enum HirSemanticPhase
{
    Validation,
    EffectAnalysis,
}

/// <summary>Identity of one semantic artifact over an exact HIR snapshot.</summary>
public readonly record struct HirSemanticArtifactId(
    HirSnapshotId Source,
    HirSemanticPhase Phase);

/// <summary>The immutable disposition of one exact HIR validation pass.</summary>
public enum HirValidationStatus
{
    Accepted,
    Rejected,
}

/// <summary>
/// The one authoritative validation outcome for a semantic artifact. A rejected outcome owns the exact
/// immutable diagnostics that caused rejection; an accepted outcome is represented by an empty diagnostic
/// list. Compilation aggregates project this data with stage/origin metadata instead of maintaining a
/// second validation truth.
/// </summary>
public sealed class HirValidationOutcome
{
    private readonly IReadOnlyList<QoraError> _diagnostics;

    internal HirValidationOutcome(IEnumerable<QoraError> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var captured = diagnostics.ToArray();
        _diagnostics = Array.AsReadOnly(captured);
        Status = captured.Length == 0
            ? HirValidationStatus.Accepted
            : HirValidationStatus.Rejected;
    }

    public HirValidationStatus Status { get; }
    public bool IsAccepted => Status == HirValidationStatus.Accepted;
    public IReadOnlyList<QoraError> Diagnostics => _diagnostics;
}

/// <summary>
/// An immutable, snapshot-qualified semantic artifact. <see cref="Program"/> is a convenience view of the
/// exact source tree; consumers never receive a detached program/model pair.
/// </summary>
public sealed class HirSemanticArtifact
{
    internal HirSemanticArtifact(
        HirSnapshot source,
        HirSemanticModel model)
        : this(
            source,
            HirSemanticPhase.Validation,
            model,
            validationBasis: null)
    {
    }

    internal HirSemanticArtifact(
        HirSnapshot source,
        HirSemanticModel model,
        HirSemanticArtifact validationBasis)
        : this(
            source,
            HirSemanticPhase.EffectAnalysis,
            model,
            validationBasis ?? throw new ArgumentNullException(nameof(validationBasis)))
    {
    }

    private HirSemanticArtifact(
        HirSnapshot source,
        HirSemanticPhase phase,
        HirSemanticModel model,
        HirSemanticArtifact? validationBasis)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(
                nameof(phase),
                phase,
                "unknown HIR semantic phase");
        if (!model.IsBoundTo(source, phase))
        {
            throw new ArgumentException(
                "A HIR semantic model can only be published for the exact snapshot and phase "
                + "which allocated it.",
                nameof(model));
        }
        if (!model.IsSealedForArtifact(phase))
        {
            throw new ArgumentException(
                "A HIR semantic model must complete and seal its phase before publication.",
                nameof(model));
        }
        switch (phase)
        {
            case HirSemanticPhase.Validation when validationBasis is not null:
                throw new ArgumentException(
                    "A validation artifact cannot name another validation artifact as its basis.",
                    nameof(validationBasis));

            case HirSemanticPhase.EffectAnalysis:
                if (validationBasis is null
                    || validationBasis.Phase != HirSemanticPhase.Validation
                    || !ReferenceEquals(validationBasis.Source, source)
                    || !validationBasis.IsAccepted)
                {
                    throw new ArgumentException(
                        "An effect artifact requires the exact accepted validation artifact for "
                        + "the same HIR snapshot.",
                        nameof(validationBasis));
                }
                break;
        }

        ValidationOutcome = phase == HirSemanticPhase.Validation
            ? model.ValidationOutcome
                ?? throw new ArgumentException(
                    "A HIR semantic model must complete and seal validation before publication.",
                    nameof(model))
            : validationBasis!.ValidationOutcome;
        ValidationBasisArtifact = validationBasis;
        Id = new HirSemanticArtifactId(source.Id, phase);
    }

    public HirSemanticArtifactId Id { get; }
    public HirSnapshot Source { get; }
    public HirSemanticPhase Phase => Id.Phase;
    public HirSnapshotId SourceId => Id.Source;
    public HirProgram Program => Source.Program;
    public HirSemanticModel Model { get; }
    public HirValidationOutcome ValidationOutcome { get; }
    public bool IsAccepted => ValidationOutcome.IsAccepted;
    public bool IsReadyForMirLowering => Phase == HirSemanticPhase.EffectAnalysis && IsAccepted;
    public IReadOnlyList<QoraError> Diagnostics => ValidationOutcome.Diagnostics;

    /// <summary>
    /// The exact accepted validation artifact consumed by effect analysis. Validation-phase artifacts have
    /// no basis; effect artifacts always expose this typed edge.
    /// </summary>
    public HirSemanticArtifactId? ValidationBasis => ValidationBasisArtifact?.Id;

    internal HirSemanticArtifact? ValidationBasisArtifact { get; }
}

/// <summary>
/// Chronological HIR generations, structural stage milestones, and semantic artifacts for one Compilation
/// snapshot. A structural pass may alias an existing generation; analysis never manufactures a fake HIR
/// revision.
/// </summary>
public sealed class HirCompilation
{
    private static readonly HirStage[] CanonicalStageOrder =
    {
        HirStage.Lowered,
        HirStage.ImportsExpanded,
        HirStage.Resolved,
        HirStage.Specialized,
    };

    private readonly IReadOnlyList<HirSnapshot> _snapshots;
    private readonly FrozenDictionary<HirRevision, HirSnapshot> _byRevision;
    private readonly FrozenDictionary<HirSnapshotId, HirSnapshot> _byId;
    private readonly FrozenDictionary<HirStage, HirSnapshot> _milestones;
    private readonly FrozenDictionary<HirSemanticArtifactId, HirSemanticArtifact> _semantics;

    internal HirCompilation(
        IEnumerable<HirSnapshot> snapshots,
        IReadOnlyDictionary<HirStage, HirSnapshotId> milestones,
        IEnumerable<HirSemanticArtifact> semantics,
        HirLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(milestones);
        ArgumentNullException.ThrowIfNull(semantics);
        Lineage = lineage ?? throw new ArgumentNullException(nameof(lineage));

        var ordered = snapshots.OrderBy(snapshot => snapshot.Id.Revision.Value).ToArray();
        _snapshots = Array.AsReadOnly(ordered);

        for (var index = 0; index < ordered.Length; index++)
        {
            var snapshot = ordered[index];
            if (snapshot.Id.Revision.Value != index)
                throw new ArgumentException(
                    "HIR revisions must be dense, unique, and start at zero.",
                    nameof(snapshots));

            HirSnapshotId? expectedParent =
                index == 0 ? null : ordered[index - 1].Id;
            if (snapshot.Parent != expectedParent)
                throw new ArgumentException(
                    $"HIR snapshot {snapshot.Id} does not name the immediately preceding generation as its parent.",
                    nameof(snapshots));

            if (index > 0
                && (snapshot.Id.CompilationId != ordered[0].Id.CompilationId
                    || snapshot.Id.CompilationRevision != ordered[0].Id.CompilationRevision))
            {
                throw new ArgumentException(
                    "All HIR snapshots must belong to one Compilation snapshot.",
                    nameof(snapshots));
            }
            if (index > 0
                && !ReferenceEquals(
                    snapshot.ConstructionCore,
                    ordered[0].ConstructionCore))
            {
                throw new ArgumentException(
                    "All HIR snapshots in one history must share the exact construction authority.",
                    nameof(snapshots));
            }
        }
        Lineage.VerifyExactSnapshots(_snapshots);

        _byRevision = ordered.ToFrozenDictionary(snapshot => snapshot.Id.Revision);
        _byId = ordered.ToFrozenDictionary(snapshot => snapshot.Id);

        var milestoneMap = new Dictionary<HirStage, HirSnapshot>();
        foreach (var (stage, snapshotId) in milestones)
        {
            if (!Enum.IsDefined(stage))
                throw new ArgumentOutOfRangeException(
                    nameof(milestones),
                    stage,
                    "unknown HIR milestone stage");
            if (!_byId.TryGetValue(snapshotId, out var snapshot))
                throw new ArgumentException(
                    $"HIR stage {stage} names unknown snapshot {snapshotId}.",
                    nameof(milestones));
            milestoneMap.Add(stage, snapshot);
        }
        VerifyCanonicalMilestoneOrder(milestoneMap);
        _milestones = milestoneMap.ToFrozenDictionary();

        var semanticMap = new Dictionary<HirSemanticArtifactId, HirSemanticArtifact>();
        foreach (var artifact in semantics)
        {
            if (!_byId.TryGetValue(artifact.SourceId, out var source)
                || !ReferenceEquals(source, artifact.Source))
            {
                throw new ArgumentException(
                    $"Semantic artifact {artifact.Id} is detached from this HIR history.",
                    nameof(semantics));
            }
            semanticMap.Add(artifact.Id, artifact);
        }
        foreach (var artifact in semanticMap.Values)
        {
            if (artifact.Phase != HirSemanticPhase.EffectAnalysis)
                continue;
            var expectedBasisId = new HirSemanticArtifactId(
                artifact.SourceId,
                HirSemanticPhase.Validation);
            if (!semanticMap.TryGetValue(expectedBasisId, out var recordedBasis)
                || !ReferenceEquals(recordedBasis, artifact.ValidationBasisArtifact))
            {
                throw new ArgumentException(
                    $"Effect artifact {artifact.Id} is detached from its exact accepted validation basis.",
                    nameof(semantics));
            }
        }
        _semantics = semanticMap.ToFrozenDictionary();
    }

    private static void VerifyCanonicalMilestoneOrder(
        IReadOnlyDictionary<HirStage, HirSnapshot> milestones)
    {
        HirSnapshot? previous = null;
        HirStage? previousStage = null;
        foreach (var stage in CanonicalStageOrder)
        {
            if (!milestones.TryGetValue(stage, out var current))
                continue;

            if (previous is not null
                && current.Id.Revision.Value < previous.Id.Revision.Value)
            {
                throw new ArgumentException(
                    $"HIR milestone {stage} ({current.Id}) precedes canonical predecessor "
                    + $"{previousStage} ({previous.Id}).",
                    nameof(milestones));
            }

            previous = current;
            previousStage = stage;
        }
    }

    public IReadOnlyList<HirSnapshot> Snapshots => _snapshots;
    public IReadOnlyDictionary<HirStage, HirSnapshot> Milestones => _milestones;
    public IReadOnlyDictionary<HirSemanticArtifactId, HirSemanticArtifact> SemanticArtifacts =>
        _semantics;
    public HirLineage Lineage { get; }

    public HirSnapshot? Find(HirRevision revision) =>
        _byRevision.TryGetValue(revision, out var snapshot) ? snapshot : null;

    public HirSnapshot? Find(HirSnapshotId id) =>
        _byId.TryGetValue(id, out var snapshot) ? snapshot : null;

    public HirSnapshot? Find(HirStage stage) =>
        _milestones.TryGetValue(stage, out var snapshot) ? snapshot : null;

    public HirSnapshot Require(HirStage stage) =>
        Find(stage)
        ?? throw new InvalidOperationException($"Compilation did not reach HIR stage {stage}.");

    public HirSemanticArtifact? FindSemantics(
        HirSnapshotId source,
        HirSemanticPhase phase) =>
        _semantics.TryGetValue(new HirSemanticArtifactId(source, phase), out var artifact)
            ? artifact
            : null;

    public HirSemanticArtifact RequireSemantics(
        HirSnapshotId source,
        HirSemanticPhase phase) =>
        FindSemantics(source, phase)
        ?? throw new InvalidOperationException(
            $"HIR snapshot {source} has no {phase} semantic artifact.");

    public HirSnapshot? Lowered => Find(HirStage.Lowered);
    public HirSnapshot? ImportsExpanded => Find(HirStage.ImportsExpanded);
    public HirSnapshot? Resolved => Find(HirStage.Resolved);
    public HirSnapshot? Specialized => Find(HirStage.Specialized);

    public HirSemanticArtifact? ResolvedValidation =>
        Resolved is { } snapshot
            ? FindSemantics(snapshot.Id, HirSemanticPhase.Validation)
            : null;

    public HirSemanticArtifact? SpecializedValidation =>
        Specialized is { } snapshot
            ? FindSemantics(snapshot.Id, HirSemanticPhase.Validation)
            : null;

    public HirSemanticArtifact? EffectAnalysis =>
        Specialized is { } snapshot
            ? FindSemantics(snapshot.Id, HirSemanticPhase.EffectAnalysis)
            : null;
}

/// <summary>
/// Identity-preserving copy relation between adjacent structural generations. Both endpoints are
/// revision-local node identities; <see cref="HirLineage"/> qualifies them with the transition snapshots.
/// </summary>
public readonly record struct NodeDerivation(
    HirNodeId SourceNodeId,
    HirNodeId DerivedNodeId);

/// <summary>
/// Provenance-only relation for a genuinely new occurrence synthesized from an existing HIR node.
/// Synthesis never grants semantic identity inheritance, and its non-empty reason remains queryable in
/// the lineage rather than being discarded after construction.
/// </summary>
public readonly record struct NodeSynthesis
{
    public NodeSynthesis(
        HirNodeId sourceNodeId,
        HirNodeId synthesizedNodeId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A synthesized HIR node requires a reason.",
                nameof(reason));
        SourceNodeId = sourceNodeId;
        SynthesizedNodeId = synthesizedNodeId;
        Reason = reason;
    }

    public HirNodeId SourceNodeId { get; }
    public HirNodeId SynthesizedNodeId { get; }
    public string Reason { get; }
}

/// <summary>A revision-qualified HIR node reference.</summary>
public readonly record struct HirNodeRef(
    HirSnapshotId Snapshot,
    HirNodeId NodeId);

/// <summary>The source-level reason a node first enters an existing HIR history.</summary>
public enum HirNodeIntroductionKind
{
    ImportedDocument,
}

/// <summary>
/// A source declaration imported into a HIR history rather than synthesized from another HIR node.
/// Its document-qualified source span remains the detailed source map; this fact classifies the
/// parent-to-child structural delta.
/// </summary>
public readonly record struct HirNodeIntroduction(
    HirNodeId IntroducedNodeId,
    SourceDocumentRef Document,
    HirNodeIntroductionKind Kind);

/// <summary>A revision-qualified synthesis origin and the compiler reason which created it.</summary>
public readonly record struct HirNodeSynthesisOrigin(
    HirNodeRef Source,
    string Reason);

/// <summary>
/// Cross-generation HIR lineage. Identity-preserving derivations and provenance-only synthesis origins are
/// stored as different typed edge sets: semantic lookup may follow only the former, while source/provenance
/// tooling may explicitly follow both.
/// </summary>
public sealed class HirLineage
{
    private readonly FrozenDictionary<HirSnapshotId, HirSnapshot> _snapshots;
    private readonly FrozenDictionary<HirSnapshotId, HirSnapshotId?> _parents;
    private readonly FrozenDictionary<HirNodeRef, HirNodeRef> _directOrigins;
    private readonly FrozenDictionary<HirNodeRef, HirNodeSynthesisOrigin> _synthesisOrigins;
    private readonly FrozenDictionary<HirNodeRef, HirNodeIntroduction> _introductions;

    internal HirLineage(
        IEnumerable<HirSnapshot> snapshots,
        IEnumerable<(HirSnapshotId Source, HirSnapshotId Target, NodeDerivation Derivation)> derivations,
        IEnumerable<(HirSnapshotId Source, HirSnapshotId Target, NodeSynthesis Synthesis)>? syntheses = null,
        IEnumerable<(HirSnapshotId Source, HirSnapshotId Target, HirNodeIntroduction Introduction)>? introductions = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(derivations);
        syntheses ??= Array.Empty<(
            HirSnapshotId Source,
            HirSnapshotId Target,
            NodeSynthesis Synthesis)>();
        introductions ??= Array.Empty<(
            HirSnapshotId Source,
            HirSnapshotId Target,
            HirNodeIntroduction Introduction)>();

        var snapshotMap = snapshots.ToDictionary(snapshot => snapshot.Id);
        var constructionCore = snapshotMap.Values
            .Select(snapshot => snapshot.ConstructionCore)
            .FirstOrDefault();
        if (constructionCore is not null
            && snapshotMap.Values.Any(
                snapshot => !ReferenceEquals(
                    snapshot.ConstructionCore,
                    constructionCore)))
        {
            throw new ArgumentException(
                "One HIR lineage cannot mix construction authorities.",
                nameof(snapshots));
        }
        _snapshots = snapshotMap.ToFrozenDictionary();
        _parents = snapshotMap.Values.ToFrozenDictionary(
            snapshot => snapshot.Id,
            snapshot => snapshot.Parent);

        var direct = new Dictionary<HirNodeRef, HirNodeRef>();
        foreach (var transition in derivations.GroupBy(item => (item.Source, item.Target)))
        {
            if (!snapshotMap.TryGetValue(transition.Key.Source, out var sourceSnapshot)
                || !snapshotMap.TryGetValue(transition.Key.Target, out var targetSnapshot))
            {
                throw new ArgumentException(
                    "HIR derivation names a snapshot outside this lineage.",
                    nameof(derivations));
            }
            if (targetSnapshot.Parent != sourceSnapshot.Id)
                throw new ArgumentException(
                    $"HIR derivation transition {sourceSnapshot.Id} -> {targetSnapshot.Id} is not a parent edge.",
                    nameof(derivations));

            var rawByDerived = new Dictionary<HirNodeId, HirNodeId>();
            foreach (var item in transition)
            {
                if (!rawByDerived.TryAdd(
                        item.Derivation.DerivedNodeId,
                        item.Derivation.SourceNodeId))
                {
                    throw new ArgumentException(
                        $"Duplicate pass-local HIR origin for node {item.Derivation.DerivedNodeId}.",
                        nameof(derivations));
                }
            }

            foreach (var (derivedId, rawSourceId) in rawByDerived)
            {
                // A pass may create an intermediate copy and copy it again without installing the first
                // copy in the target tree. Only installed target nodes belong in cross-snapshot lineage.
                if (!targetSnapshot.Structure.Contains(derivedId))
                    continue;

                var sourceId = rawSourceId;
                var visited = new HashSet<HirNodeId>();
                while (!sourceSnapshot.Structure.Contains(sourceId))
                {
                    if (!visited.Add(sourceId))
                        throw new ArgumentException(
                            $"Cycle detected in pass-local HIR derivations at node {sourceId}.",
                            nameof(derivations));
                    if (!rawByDerived.TryGetValue(sourceId, out sourceId))
                    {
                        throw new ArgumentException(
                            $"HIR node {derivedId} derives from missing parent/intermediate node {rawSourceId}.",
                            nameof(derivations));
                    }
                }

                var sourceKind = sourceSnapshot.Structure.RequireKind(sourceId);
                var targetKind = targetSnapshot.Structure.RequireKind(derivedId);
                if (sourceKind != targetKind)
                    throw new ArgumentException(
                        $"HIR derivation changes node kind from {sourceKind} to {targetKind}.",
                        nameof(derivations));

                var derived = new HirNodeRef(targetSnapshot.Id, derivedId);
                var source = new HirNodeRef(sourceSnapshot.Id, sourceId);
                if (!direct.TryAdd(derived, source))
                    throw new ArgumentException(
                        $"Duplicate normalized HIR origin for {derived}.",
                        nameof(derivations));
            }
        }

        _directOrigins = direct.ToFrozenDictionary();

        var synthesized = new Dictionary<HirNodeRef, HirNodeSynthesisOrigin>();
        foreach (var transition in syntheses.GroupBy(item => (item.Source, item.Target)))
        {
            if (!snapshotMap.TryGetValue(transition.Key.Source, out var sourceSnapshot)
                || !snapshotMap.TryGetValue(transition.Key.Target, out var targetSnapshot))
            {
                throw new ArgumentException(
                    "HIR synthesis origin names a snapshot outside this lineage.",
                    nameof(syntheses));
            }
            if (targetSnapshot.Parent != sourceSnapshot.Id)
            {
                throw new ArgumentException(
                    $"HIR synthesis transition {sourceSnapshot.Id} -> {targetSnapshot.Id} is not a parent edge.",
                    nameof(syntheses));
            }

            foreach (var item in transition)
            {
                var sourceId = item.Synthesis.SourceNodeId;
                var synthesizedId = item.Synthesis.SynthesizedNodeId;
                if (string.IsNullOrWhiteSpace(item.Synthesis.Reason))
                {
                    throw new ArgumentException(
                        $"HIR synthesis target {synthesizedId} has no reason.",
                        nameof(syntheses));
                }
                _ = sourceSnapshot.Structure.RequireKind(sourceId);
                _ = targetSnapshot.Structure.RequireKind(synthesizedId);

                if (sourceSnapshot.Structure.Contains(synthesizedId))
                {
                    throw new ArgumentException(
                        $"HIR synthesis target {synthesizedId} already exists in the parent snapshot.",
                        nameof(syntheses));
                }

                var target = new HirNodeRef(targetSnapshot.Id, synthesizedId);
                var source = new HirNodeRef(sourceSnapshot.Id, sourceId);
                if (direct.ContainsKey(target))
                {
                    throw new ArgumentException(
                        $"HIR node {target} cannot be both an identity copy and a synthesis.",
                        nameof(syntheses));
                }
                if (!synthesized.TryAdd(
                        target,
                        new HirNodeSynthesisOrigin(
                            source,
                            item.Synthesis.Reason)))
                {
                    throw new ArgumentException(
                        $"Duplicate HIR synthesis origin for {target}.",
                        nameof(syntheses));
                }
            }
        }

        _synthesisOrigins = synthesized.ToFrozenDictionary();

        var introduced = new Dictionary<HirNodeRef, HirNodeIntroduction>();
        foreach (var transition in introductions.GroupBy(item => (item.Source, item.Target)))
        {
            if (!snapshotMap.TryGetValue(transition.Key.Source, out var sourceSnapshot)
                || !snapshotMap.TryGetValue(transition.Key.Target, out var targetSnapshot))
            {
                throw new ArgumentException(
                    "HIR source introduction names a snapshot outside this lineage.",
                    nameof(introductions));
            }
            if (targetSnapshot.Parent != sourceSnapshot.Id)
            {
                throw new ArgumentException(
                    $"HIR introduction transition {sourceSnapshot.Id} -> {targetSnapshot.Id} is not a parent edge.",
                    nameof(introductions));
            }

            foreach (var item in transition)
            {
                var introduction = item.Introduction;
                if (!Enum.IsDefined(introduction.Kind))
                {
                    throw new ArgumentException(
                        $"HIR node introduction declares unknown kind {introduction.Kind}.",
                        nameof(introductions));
                }
                if (introduction.Document.CompilationId != targetSnapshot.Id.CompilationId
                    || introduction.Document.CompilationRevision
                    != targetSnapshot.Id.CompilationRevision)
                {
                    throw new ArgumentException(
                        "HIR node introduction belongs to another Compilation snapshot.",
                        nameof(introductions));
                }

                var nodeId = introduction.IntroducedNodeId;
                _ = targetSnapshot.Structure.RequireKind(nodeId);
                if (sourceSnapshot.Structure.Contains(nodeId))
                {
                    throw new ArgumentException(
                        $"HIR introduced node {nodeId} already exists in the parent snapshot.",
                        nameof(introductions));
                }
                var span = targetSnapshot.SourceMap.Find(nodeId)
                    ?? throw new ArgumentException(
                        $"Source-introduced HIR node {nodeId} has no source span.",
                        nameof(introductions));
                if (span.Document != introduction.Document)
                {
                    throw new ArgumentException(
                        $"HIR introduced node {nodeId} disagrees with its source document.",
                        nameof(introductions));
                }

                var target = new HirNodeRef(targetSnapshot.Id, nodeId);
                if (direct.ContainsKey(target) || synthesized.ContainsKey(target))
                {
                    throw new ArgumentException(
                        $"HIR node {target} has more than one origin classification.",
                        nameof(introductions));
                }
                if (!introduced.TryAdd(target, introduction))
                {
                    throw new ArgumentException(
                        $"Duplicate HIR source introduction for {target}.",
                        nameof(introductions));
                }
            }
        }

        _introductions = introduced.ToFrozenDictionary();
        VerifyTotalOriginCoverage(snapshotMap, direct, synthesized, introduced);
    }

    private static void VerifyTotalOriginCoverage(
        IReadOnlyDictionary<HirSnapshotId, HirSnapshot> snapshots,
        IReadOnlyDictionary<HirNodeRef, HirNodeRef> derivations,
        IReadOnlyDictionary<HirNodeRef, HirNodeSynthesisOrigin> syntheses,
        IReadOnlyDictionary<HirNodeRef, HirNodeIntroduction> introductions)
    {
        foreach (var targetSnapshot in snapshots.Values)
        {
            if (targetSnapshot.Parent is not { } parentId)
                continue;

            var parentSnapshot = snapshots[parentId];
            foreach (var nodeId in targetSnapshot.Structure.NodeIds)
            {
                var target = new HirNodeRef(targetSnapshot.Id, nodeId);
                var classificationCount =
                    (derivations.ContainsKey(target) ? 1 : 0)
                    + (syntheses.ContainsKey(target) ? 1 : 0)
                    + (introductions.ContainsKey(target) ? 1 : 0);
                var existed = parentSnapshot.Structure.Contains(nodeId);

                if (existed && classificationCount != 0)
                {
                    throw new ArgumentException(
                        $"HIR node {target} keeps its parent identity but also declares an explicit origin.");
                }
                if (existed
                    && parentSnapshot.Structure.RequireKind(nodeId)
                        != targetSnapshot.Structure.RequireKind(nodeId))
                {
                    throw new ArgumentException(
                        $"HIR node {nodeId} changes kind from "
                        + $"{parentSnapshot.Structure.RequireKind(nodeId)} to "
                        + $"{targetSnapshot.Structure.RequireKind(nodeId)} across an implicit "
                        + "same-identity parent edge.");
                }
                if (existed
                    && parentSnapshot.SourceMap.Find(nodeId)
                        != targetSnapshot.SourceMap.Find(nodeId))
                {
                    throw new ArgumentException(
                        $"HIR node {nodeId} changes its source span across an implicit "
                        + "same-identity parent edge.");
                }
                if (existed
                    && parentSnapshot.Structure.OwningCallableOf(nodeId)
                        != targetSnapshot.Structure.OwningCallableOf(nodeId))
                {
                    throw new ArgumentException(
                        $"HIR node {nodeId} changes its owning callable across an implicit "
                        + "same-identity parent edge.");
                }
                if (existed
                    && parentSnapshot.Structure.ParentOf(nodeId)
                        != targetSnapshot.Structure.ParentOf(nodeId))
                {
                    throw new ArgumentException(
                        $"HIR node {nodeId} changes its structural parent across an implicit "
                        + "same-identity parent edge.");
                }
                if (!existed && classificationCount != 1)
                {
                    throw new ArgumentException(
                        $"New HIR node {target} requires exactly one identity derivation, synthesis origin, " +
                        $"or source introduction; found {classificationCount}.");
                }
            }
        }
    }

    public bool Contains(HirSnapshotId snapshot) => _snapshots.ContainsKey(snapshot);

    public HirNodeIntroduction? FindIntroduction(HirNodeRef node)
    {
        if (!_snapshots.ContainsKey(node.Snapshot))
        {
            throw new ArgumentOutOfRangeException(
                nameof(node),
                node,
                "HIR node reference does not belong to this lineage");
        }
        _ = _snapshots[node.Snapshot].Structure.RequireKind(node.NodeId);
        return _introductions.TryGetValue(node, out var introduction)
            ? introduction
            : null;
    }

    public HirNodeSynthesisOrigin? FindSynthesisOrigin(HirNodeRef node)
    {
        if (!_snapshots.ContainsKey(node.Snapshot))
        {
            throw new ArgumentOutOfRangeException(
                nameof(node),
                node,
                "HIR node reference does not belong to this lineage");
        }
        _ = _snapshots[node.Snapshot].Structure.RequireKind(node.NodeId);
        return _synthesisOrigins.TryGetValue(node, out var origin)
            ? origin
            : null;
    }

    /// <summary>
    /// Proves that this lineage and a HIR aggregate share one exact snapshot object set. Matching IDs are
    /// insufficient: otherwise structural queries could read one tree while lineage resolution walks a
    /// detached tree carrying the same revision identity.
    /// </summary>
    internal void VerifyExactSnapshots(IReadOnlyList<HirSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (_snapshots.Count != snapshots.Count)
        {
            throw new ArgumentException(
                "HIR lineage and HIR history contain different snapshot sets.",
                nameof(snapshots));
        }

        foreach (var snapshot in snapshots)
        {
            if (!_snapshots.TryGetValue(snapshot.Id, out var recorded)
                || !ReferenceEquals(recorded, snapshot))
            {
                throw new ArgumentException(
                    $"HIR lineage is detached from snapshot {snapshot.Id}.",
                    nameof(snapshots));
            }
            if (_parents[snapshot.Id] != snapshot.Parent)
            {
                throw new ArgumentException(
                    $"HIR lineage parent for {snapshot.Id} disagrees with the HIR history.",
                    nameof(snapshots));
            }
        }
    }

    /// <summary>
    /// Resolve source provenance across structural HIR generations. This query may cross a
    /// <see cref="NodeSynthesis"/> edge and therefore returns a full node reference whose ID and kind may
    /// differ from the synthesized node.
    /// </summary>
    public HirNodeRef ResolveProvenance(
        HirSnapshotId from,
        HirSnapshotId ancestor,
        HirNodeId nodeId)
    {
        if (from.CompilationId != ancestor.CompilationId
            || from.CompilationRevision != ancestor.CompilationRevision)
        {
            throw new ArgumentException(
                "HIR provenance cannot cross Compilation snapshots.",
                nameof(ancestor));
        }
        if (!_snapshots.TryGetValue(from, out var fromSnapshot))
            throw new ArgumentOutOfRangeException(
                nameof(from),
                from,
                "source HIR snapshot does not belong to this lineage");
        if (!_snapshots.ContainsKey(ancestor))
            throw new ArgumentOutOfRangeException(
                nameof(ancestor),
                ancestor,
                "ancestor HIR snapshot does not belong to this lineage");
        _ = fromSnapshot.Structure.RequireKind(nodeId);

        var current = new HirNodeRef(from, nodeId);
        var visited = new HashSet<HirNodeRef>();
        while (current.Snapshot != ancestor)
        {
            if (!visited.Add(current))
            {
                throw new InvalidOperationException(
                    $"Cycle detected in HIR provenance at {current}.");
            }

            if (_directOrigins.TryGetValue(current, out var derivation))
            {
                current = derivation;
                continue;
            }
            if (_synthesisOrigins.TryGetValue(current, out var synthesis))
            {
                current = synthesis.Source;
                continue;
            }

            if (!_parents.TryGetValue(current.Snapshot, out var parent) || parent is null)
            {
                throw new InvalidOperationException(
                    $"{ancestor} is not an ancestor of {from}.");
            }

            var parentSnapshot = _snapshots[parent.Value];
            if (!parentSnapshot.Structure.Contains(current.NodeId))
            {
                throw new InvalidOperationException(
                    $"HIR node {current.NodeId} was synthesized in {current.Snapshot}, " +
                    "but no synthesis origin connects it to the parent snapshot.");
            }

            var currentKind = _snapshots[current.Snapshot].Structure.RequireKind(current.NodeId);
            var parentKind = parentSnapshot.Structure.RequireKind(current.NodeId);
            if (currentKind != parentKind)
            {
                throw new InvalidOperationException(
                    $"HIR node {current.NodeId} changes kind from {parentKind} to {currentKind} " +
                    "across an implicit same-identity provenance edge.");
            }

            current = new HirNodeRef(parent.Value, current.NodeId);
        }

        _ = _snapshots[ancestor].Structure.RequireKind(current.NodeId);
        return current;
    }
}
