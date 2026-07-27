using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Compiler;

/// <summary>
/// Mutable construction helper used only while one immutable Compilation is being produced. It is the
/// sole authority for HIR revisions, structural milestones, semantic artifacts, and raw pass derivations.
/// </summary>
internal sealed class HirPipelineBuilder
{
    private readonly CompilationId _compilationId;
    private readonly CompilationRevision _compilationRevision;
    private readonly List<HirSnapshot> _snapshots = new();
    private readonly Dictionary<HirStage, HirSnapshotId> _milestones = new();
    private readonly Dictionary<HirSemanticArtifactId, HirSemanticArtifact> _semantics = new();
    private readonly List<(HirSnapshotId Source, HirSnapshotId Target, NodeDerivation Derivation)>
        _derivations = new();
    private readonly List<(HirSnapshotId Source, HirSnapshotId Target, NodeSynthesis Synthesis)>
        _syntheses = new();
    private readonly List<(HirSnapshotId Source, HirSnapshotId Target, HirNodeIntroduction Introduction)>
        _introductions = new();

    public HirPipelineBuilder(
        CompilationId compilationId,
        CompilationRevision compilationRevision)
    {
        _compilationId = compilationId;
        _compilationRevision = compilationRevision;
    }

    public HirSnapshot? Latest =>
        _snapshots.Count == 0 ? null : _snapshots[^1];

    /// <summary>
    /// Record a structural stage. A pass which returned the exact input tree and no copy facts aliases the
    /// existing generation; every real tree result receives the next dense HIR revision.
    /// </summary>
    public HirSnapshot Advance(
        HirStage stage,
        QProgram program,
        IEnumerable<NodeDerivation>? derivations = null,
        IEnumerable<NodeSynthesis>? syntheses = null,
        IEnumerable<HirNodeIntroduction>? introductions = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        var copyFacts = derivations?.ToArray() ?? Array.Empty<NodeDerivation>();
        var synthesisFacts = syntheses?.ToArray() ?? Array.Empty<NodeSynthesis>();
        var introductionFacts = introductions?.ToArray() ?? Array.Empty<HirNodeIntroduction>();
        return Latest is { } latest
               && ReferenceEquals(latest.Program, program)
               && copyFacts.Length == 0
               && synthesisFacts.Length == 0
               && introductionFacts.Length == 0
            ? Alias(stage, latest)
            : Add(
                stage,
                program,
                copyFacts,
                synthesisFacts,
                introductionFacts);
    }

    /// <summary>Create a real HIR generation and mark the structural stage which produced it.</summary>
    private HirSnapshot Add(
        HirStage stage,
        QProgram program,
        IEnumerable<NodeDerivation>? derivations = null,
        IEnumerable<NodeSynthesis>? syntheses = null,
        IEnumerable<HirNodeIntroduction>? introductions = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (_milestones.ContainsKey(stage))
            throw new InvalidOperationException(
                $"HIR stage {stage} was already recorded.");

        var id = new HirSnapshotId(
            _compilationId,
            _compilationRevision,
            new HirRevision(_snapshots.Count));
        var parent = Latest?.Id;
        var snapshot = new HirSnapshot(id, stage, program, parent);
        _snapshots.Add(snapshot);
        _milestones.Add(stage, id);

        var copyFacts = derivations?.ToArray() ?? Array.Empty<NodeDerivation>();
        var synthesisFacts = syntheses?.ToArray() ?? Array.Empty<NodeSynthesis>();
        var introductionFacts = introductions?.ToArray() ?? Array.Empty<HirNodeIntroduction>();
        if (copyFacts.Length > 0
            || synthesisFacts.Length > 0
            || introductionFacts.Length > 0)
        {
            if (parent is null)
                throw new InvalidOperationException(
                    "The first HIR snapshot cannot contain transform-origin facts.");
            foreach (var derivation in copyFacts)
                _derivations.Add((parent.Value, id, derivation));
            foreach (var synthesis in synthesisFacts)
                _syntheses.Add((parent.Value, id, synthesis));
            foreach (var introduction in introductionFacts)
                _introductions.Add((parent.Value, id, introduction));
        }

        return snapshot;
    }

    /// <summary>
    /// Record that a structural pass produced no new tree. The stage names an existing exact generation
    /// instead of consuming a fake HIR revision.
    /// </summary>
    public HirSnapshot Alias(HirStage stage, HirSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_snapshots.Contains(snapshot))
            throw new ArgumentException(
                "A HIR stage alias must name a snapshot from this builder.",
                nameof(snapshot));
        if (!_milestones.TryAdd(stage, snapshot.Id))
            throw new InvalidOperationException(
                $"HIR stage {stage} was already recorded.");
        return snapshot;
    }

    /// <summary>
    /// Validate the program owned by one exact snapshot and publish any resulting semantic model back onto
    /// that same snapshot. Callers never receive a detached program/model pair.
    /// </summary>
    public HirSemanticArtifact ValidateSnapshot(HirSnapshot source)
    {
        RequireOwned(source, nameof(source));
        RequireSemanticPhaseOpen(
            source.Id,
            HirSemanticPhase.Validation);

        var diagnostics = QoraValidator.Validate(
            source,
            out var model);
        if (model is null)
        {
            throw new InvalidOperationException(
                "QINTERNAL: validation of a non-null HIR snapshot produced no semantic model.");
        }

        var artifact = new HirSemanticArtifact(
            source,
            model);
        RecordSemantics(artifact);
        return artifact;
    }

    /// <summary>
    /// Run effect analysis only from the exact validation artifact published by this builder. The analysis
    /// fork shares sealed validation facts but owns independent effect sinks and is attached to the same
    /// source snapshot under the effect-analysis phase.
    /// </summary>
    public HirSemanticArtifact AnalyzeEffects(
        HirSemanticArtifact validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.Phase != HirSemanticPhase.Validation)
            throw new ArgumentException(
                "Effect analysis requires a validation-phase semantic artifact.",
                nameof(validation));
        if (!validation.IsAccepted)
            throw new ArgumentException(
                "Effect analysis requires an accepted validation artifact.",
                nameof(validation));
        if (!_semantics.TryGetValue(validation.Id, out var recorded)
            || !ReferenceEquals(recorded, validation))
        {
            throw new ArgumentException(
                "Effect analysis requires an exact validation artifact published by this builder.",
                nameof(validation));
        }
        RequireOwned(validation.Source, nameof(validation));
        RequireSemanticPhaseOpen(
            validation.SourceId,
            HirSemanticPhase.EffectAnalysis);

        var analyzedModel = validation.Model.ForkForEffectAnalysis();
        EffectAnalysis.Run(validation.Source.Program, analyzedModel);
        analyzedModel.SealEffectAnalysisArtifact();
        var artifact = new HirSemanticArtifact(
            validation.Source,
            analyzedModel,
            validation);
        RecordSemantics(artifact);
        return artifact;
    }

    private void RecordSemantics(HirSemanticArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireOwned(artifact.Source, nameof(artifact));
        if (!_semantics.TryAdd(artifact.Id, artifact))
            throw new InvalidOperationException(
                $"Semantic artifact {artifact.Id} was already recorded.");
    }

    private void RequireOwned(
        HirSnapshot snapshot,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_snapshots.Contains(snapshot))
            throw new ArgumentException(
                "A semantic operation requires an exact HIR snapshot from this builder.",
                parameterName);
    }

    private void RequireSemanticPhaseOpen(
        HirSnapshotId source,
        HirSemanticPhase phase)
    {
        var id = new HirSemanticArtifactId(source, phase);
        if (_semantics.ContainsKey(id))
            throw new InvalidOperationException(
                $"Semantic artifact {id} was already published.");
    }

    public HirCompilation Build()
    {
        var lineage = new HirLineage(
            _snapshots,
            _derivations,
            _syntheses,
            _introductions);
        return new HirCompilation(
            _snapshots,
            _milestones,
            _semantics.Values,
            lineage);
    }
}
