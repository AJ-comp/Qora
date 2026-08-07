using Qora.Compiler;

namespace Qora.Ir.Passes;

/// <summary>
/// One formal qubit location in an <see cref="OpEffectSummary"/>. A null <see cref="Index"/> denotes the
/// whole formal register. Dynamic indexes are conservatively represented as the whole register because
/// HIR-to-MIR lowering only needs to know whether an actual qubit operand may receive a new version.
/// </summary>
public readonly record struct QubitRef(string Reg, int? Index)
{
    public override string ToString() => Index is int i ? $"{Reg}[{i}]" : Reg;
}

/// <summary>
/// The formal qubit parameters whose value a callable may modify, including modifications made transitively
/// through user-callable invocations. This is the only HIR quantum-effect fact required by MIR lowering;
/// detailed quantum effects and cleanup analysis are derived from MIR.
/// </summary>
public sealed record OpEffectSummary(
    IReadOnlySet<QubitRef> ParamModified)
{
    private IReadOnlySet<QubitRef> _paramModified = HirCollections.FreezeSet(ParamModified);

    public IReadOnlySet<QubitRef> ParamModified
    {
        get => _paramModified;
        init => _paramModified = HirCollections.FreezeSet(value);
    }
}

/// <summary>One outstanding "re-check after monomorphization" PROMISE (the deferral ledger): a size-dependent
/// judgement the validator postponed because <see cref="Array"/> is a parameter whose length only
/// specialization can supply (<see cref="HirParameter.NeedsMonoSizing"/>). On the pipeline that runs the
/// specialize/re-validate pair, every entry is resolved there or MOOTED there — an uncalled generic op is
/// DROPPED by the Monomorphizer, so its promises die with the op (no size ever exists to judge against;
/// the code is never emitted) — so the FINAL model's ledger is empty and
/// the list means "promises still outstanding". Any pipeline that deliberately preserves dynamic array
/// lengths must explicitly consume these obligations; otherwise the postponed judgements would silently
/// disappear. Deferred aliasing precision (QSEM014's post-specialization domain re-check) is not ledgered
/// here.</summary>
public sealed record DeferredSizeCheck(string Op, string Array, string Access, string Reason, SourceSpan? Span);

/// <summary>
/// The persistent semantic side table produced for one exact HIR generation. Cross-generation node
/// lineage belongs to Compilation's HIR graph, and emitted names belong to each target artifact. This
/// model contains HIR facts only: scopes, symbols, validation results, and the callable parameter-write
/// summary required to construct correct MIR qubit versions.
/// </summary>
public sealed class HirSemanticModel
{
    private static readonly IReadOnlyDictionary<ScopeId, Scope> EmptyScopes =
        HirCollections.Freeze(new Dictionary<ScopeId, Scope>());
    private static readonly IReadOnlyDictionary<HirNodeId, Scope> EmptyRootScopes =
        HirCollections.Freeze(new Dictionary<HirNodeId, Scope>());

    /// <summary>
    /// Validation-owned facts shared by the validation snapshot and its effect-analysis fork. Effect
    /// analysis never writes this state; its parameter-write summary lives on each model instance below.
    /// Grouping the facts makes the sharing boundary explicit and prevents a fork from copying a second,
    /// drift-prone symbol/scope truth.
    /// </summary>
    private sealed class ValidationFacts
    {
        internal readonly List<DeferredSizeCheck> DeferredSizeChecks = new();
        internal readonly Dictionary<HirNodeId, bool> WillBeRecheckedByCallable = new();
        internal readonly Dictionary<HirNodeId, IReadOnlyDictionary<string, long>>
            RequiredArgLengthsByCallable = new();
        internal readonly Dictionary<HirNodeId, QType> ImplicitConversionTargets = new();
        internal HirScopeGraph? ScopeGraph;
        internal HirValidationOutcome? Outcome;
        internal bool ProducerCompleted;
        internal bool IsSealed;
    }

    private readonly Dictionary<HirNodeId, OpEffectSummary>
        _effectSummaryByCallableId = new();
    private readonly ValidationFacts _validation;
    private readonly HirSnapshot? _sourceSnapshot;
    private readonly HirSemanticPhase _artifactPhase;
    private bool _effectAnalysisCompleted;
    private bool _effectsSealed;

    /// <summary>
    /// Creates detached mutable facts for focused pass tests. A detached model can never be published as a
    /// <see cref="HirSemanticArtifact"/>; production validation must use the snapshot-bound constructor.
    /// </summary>
    internal HirSemanticModel()
        : this(
            sourceSnapshot: null,
            HirSemanticPhase.Validation,
            new ValidationFacts())
    {
    }

    internal HirSemanticModel(HirSnapshot sourceSnapshot)
        : this(
            sourceSnapshot ?? throw new ArgumentNullException(nameof(sourceSnapshot)),
            HirSemanticPhase.Validation,
            new ValidationFacts())
    {
    }

    private HirSemanticModel(
        HirSnapshot? sourceSnapshot,
        HirSemanticPhase artifactPhase,
        ValidationFacts validation)
    {
        if (!Enum.IsDefined(artifactPhase))
            throw new ArgumentOutOfRangeException(
                nameof(artifactPhase),
                artifactPhase,
                "unknown HIR semantic phase");

        _sourceSnapshot = sourceSnapshot;
        _artifactPhase = artifactPhase;
        _validation = validation;
    }

    /// <summary>
    /// Proves that this model was allocated for one exact HIR snapshot and semantic phase. Snapshot IDs are
    /// deliberately insufficient because a detached tree may reuse an otherwise valid ID.
    /// </summary>
    internal bool IsBoundTo(
        HirSnapshot sourceSnapshot,
        HirSemanticPhase artifactPhase) =>
        ReferenceEquals(_sourceSnapshot, sourceSnapshot)
        && _artifactPhase == artifactPhase;

    internal HirValidationOutcome? ValidationOutcome => _validation.Outcome;

    /// <summary>
    /// A model becomes publishable only after the phase-specific producer has sealed all of its sinks.
    /// This prevents an exact source token from being attached before validation/effect analysis completed.
    /// </summary>
    internal bool IsSealedForArtifact(HirSemanticPhase artifactPhase)
    {
        if (_artifactPhase != artifactPhase
            || !_validation.ProducerCompleted
            || _validation.Outcome is null
            || !_validation.IsSealed
            || _validation.ScopeGraph is not { IsSealed: true }
            || !_effectsSealed)
        {
            return false;
        }

        return artifactPhase switch
        {
            HirSemanticPhase.Validation => !_effectAnalysisCompleted,
            HirSemanticPhase.EffectAnalysis => _effectAnalysisCompleted,
            _ => false,
        };
    }

    /// <summary>
    /// Create the semantic model for the effect-analyzed HIR snapshot. Validation facts and the unified
    /// scope graph remain the one shared authority, while the effect-summary table starts empty. Therefore
    /// running <see cref="EffectAnalysis"/> on the returned model cannot mutate the validation-only model.
    /// </summary>
    internal HirSemanticModel ForkForEffectAnalysis()
    {
        if (!_validation.IsSealed
            || _validation.ScopeGraph is not { IsSealed: true }
            || _validation.Outcome is not { IsAccepted: true })
            throw new InvalidOperationException(
                "QINTERNAL: accepted validation facts and their scope graph must be sealed "
                + "before effect analysis is forked");
        if (_artifactPhase != HirSemanticPhase.Validation
            || _sourceSnapshot is null)
        {
            throw new InvalidOperationException(
                "QINTERNAL: only an exact snapshot-bound validation artifact can start effect analysis");
        }
        if (_effectSummaryByCallableId.Count != 0
            || _effectAnalysisCompleted)
            throw new InvalidOperationException(
                "QINTERNAL: only a validation-only HIR semantic model can be forked for effect analysis");
        return new HirSemanticModel(
            _sourceSnapshot,
            HirSemanticPhase.EffectAnalysis,
            _validation);
    }

    /// <summary>
    /// Freeze a validation result before a <c>HirSemanticArtifact</c> publishes it. The validation-only
    /// model cannot later acquire a parameter-write summary; effect analysis must use a dedicated fork.
    /// </summary>
    internal void SealValidationArtifact(IEnumerable<QoraError> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        RequireValidationOpen();
        if (_artifactPhase != HirSemanticPhase.Validation
            || _sourceSnapshot is null
            || !_validation.ProducerCompleted)
        {
            throw new InvalidOperationException(
                "QINTERNAL: a validation artifact requires one completed snapshot-bound validation pass");
        }
        var scopeGraph = _validation.ScopeGraph
            ?? throw new InvalidOperationException(
                "QINTERNAL: a validation artifact requires a completed HIR scope graph");
        if (_effectSummaryByCallableId.Count != 0)
        {
            throw new InvalidOperationException(
                "QINTERNAL: a validation artifact cannot contain effect-analysis facts");
        }

        _validation.Outcome = new HirValidationOutcome(diagnostics);
        scopeGraph.Seal();
        _validation.IsSealed = true;
        _effectsSealed = true;
    }

    /// <summary>Freeze an effect-analysis fork before publishing its semantic artifact.</summary>
    internal void SealEffectAnalysisArtifact()
    {
        if (_artifactPhase != HirSemanticPhase.EffectAnalysis
            || _sourceSnapshot is null
            || !_validation.IsSealed
            || _validation.ScopeGraph is not { IsSealed: true })
        {
            throw new InvalidOperationException(
                "QINTERNAL: an effect artifact requires an exact source and sealed validation facts");
        }
        if (_effectsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: effect-analysis facts were already sealed");

        var expectedCallables = _sourceSnapshot.Program.Callables
            .Select(callable => callable.Id)
            .ToHashSet();
        if (!expectedCallables.SetEquals(_effectSummaryByCallableId.Keys))
        {
            throw new InvalidOperationException(
                "QINTERNAL: effect analysis did not publish one summary for every source callable");
        }
        _effectAnalysisCompleted = true;
        _effectsSealed = true;
    }

    /// <summary>
    /// Mark the end of the validation producer after its collect-all walk has populated every callable's
    /// scope and recheck verdict. Sealing is deliberately separate: this completion proof is recorded by
    /// the producer, while publication later freezes the graph and all fact sinks.
    /// </summary>
    internal void CompleteValidation(HirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        RequireValidationOpen();
        if (_artifactPhase != HirSemanticPhase.Validation)
            throw new InvalidOperationException(
                "QINTERNAL: only a validation-phase model can complete validation");
        if (_validation.ProducerCompleted)
            throw new InvalidOperationException(
                "QINTERNAL: validation was already marked complete");
        if (_sourceSnapshot is { } source
            && !ReferenceEquals(source.Program, program))
        {
            throw new InvalidOperationException(
                "QINTERNAL: validation completed against a different HIR program than its exact source");
        }

        var scopeGraph = _validation.ScopeGraph
            ?? throw new InvalidOperationException(
                "QINTERNAL: validation completed without a HIR scope graph");
        var expectedCallables = program.Callables
            .Select(callable => callable.Id)
            .ToHashSet();
        if (!expectedCallables.SetEquals(_validation.WillBeRecheckedByCallable.Keys))
        {
            throw new InvalidOperationException(
                "QINTERNAL: validation did not publish one recheck verdict for every source callable");
        }
        foreach (var callable in program.Callables)
        {
            if (scopeGraph.FindCallableScope(callable.Id) is null
                || scopeGraph.FindDeclaration(callable.Id) is not
                    { Kind: SymbolKind.Callable })
            {
                throw new InvalidOperationException(
                    $"QINTERNAL: validation did not publish the callable symbol and scope for "
                    + $"callable {callable.Id}");
            }
        }

        _validation.ProducerCompleted = true;
    }

    /// <summary>
    /// Register the one HIR scope graph. It already owns the scope, symbol, declaration, callable-root, and
    /// source-site indexes, so this model must not mirror those tables.
    /// </summary>
    internal void SetScopeGraph(HirScopeGraph scopeGraph)
    {
        ArgumentNullException.ThrowIfNull(scopeGraph);
        RequireValidationOpen();
        if (scopeGraph.IsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: a mutable validation model cannot attach an already-published HIR scope graph");
        if (_validation.ScopeGraph is not null
            && !ReferenceEquals(_validation.ScopeGraph, scopeGraph))
            throw new InvalidOperationException(
                "QINTERNAL: HirSemanticModel already owns a different HIR scope graph");
        _validation.ScopeGraph = scopeGraph;
    }

    /// <summary>Record an operation's array-argument CONTRACT (rung B′/P4): the minimum length each of its
    /// classical-array parameters requires, settled after call-graph propagation. Single producer
    /// (<see cref="QoraValidator"/>, once per validation), add-only like every other fact.</summary>
    internal void SetRequiredArgLengths(
        HirNodeId opId,
        IReadOnlyDictionary<string, long> needs)
    {
        RequireValidationOpen();
        if (!_validation.RequiredArgLengthsByCallable.TryAdd(
                opId,
                HirCollections.Freeze(needs)))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an array-argument contract — re-validation would silently replace add-only facts");
    }

    /// <summary>The operation's array-argument contract — parameter name → minimum required length — or null
    /// when the op demands nothing. The call-site QSEM016s are DERIVED from this table; consumers (signature
    /// help, docs, backends) can read the same contract.</summary>
    public IReadOnlyDictionary<string, long>? RequiredArgLengths(
        HirNodeId opId) =>
        _validation.RequiredArgLengthsByCallable.TryGetValue(opId, out var needs) ? needs : null;

    /// <summary>The deferral ledger (see <see cref="DeferredSizeCheck"/>): the size-dependent judgements
    /// THIS validation postponed to the post-monomorphization re-check, in walk order. On a SUCCESSFUL
    /// compile the pipeline's surviving model always holds an EMPTY ledger — either the post-mono
    /// re-validation ran on concrete sizes (nothing left to postpone) or the program had no generics to
    /// postpone for. A program rejected AT the pre-mono validation keeps that model, so its ledger may
    /// carry the promises still outstanding at rejection; a program rejected at the POST-mono
    /// re-validation keeps the post-mono model, whose ledger is empty like any sized program's. A backend
    /// that skips specialization must dispose of every entry (runtime checks) instead of letting them
    /// evaporate.</summary>
    public IReadOnlyList<DeferredSizeCheck> DeferredSizeChecks =>
        HirCollections.Freeze(_validation.DeferredSizeChecks);

    /// <summary>Deferral-ledger sink (rung B′): single producer (<see cref="QoraValidator"/>), recorded
    /// during the bounds walk, add-only like every other fact.</summary>
    internal void AddDeferredSizeCheck(DeferredSizeCheck deferred)
    {
        RequireValidationOpen();
        _validation.DeferredSizeChecks.Add(deferred);
    }

    /// <summary>Will the post-monomorphization re-validation come for this operation? The validator's own
    /// PREDICTION, made BEFORE the Monomorphizer runs, from the same transitive reachability (concrete ops
    /// outward through the call graph) the Monomorphizer acts on — so it cannot disagree with the actual
    /// drop. True: a concrete op (its checks complete without deferral) or a generic reached from concrete
    /// code (its specializations get the re-check). FALSE: a dead generic — dropped, so its postponed
    /// judgements are MOOTED, never judged at any size (harmless: the code is never emitted). Null: an op
    /// this validation never saw. This is the ledger's companion question — a <see cref="DeferredSizeCheck"/>
    /// whose op answers false is a promise nothing will ever answer.</summary>
    public bool? WillBeRechecked(HirNodeId opId) =>
        _validation.WillBeRecheckedByCallable.TryGetValue(opId, out var will) ? will : null;

    /// <summary>Liveness-prediction sink (rung B′): single producer (<see cref="QoraValidator"/>, once per
    /// op), add-only — recording the same op twice is a pipeline bug, not a merge.</summary>
    internal void SetWillBeRechecked(
        HirNodeId opId,
        bool willBeRechecked)
    {
        RequireValidationOpen();
        if (!_validation.WillBeRecheckedByCallable.TryAdd(opId, willBeRechecked))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has a WillBeRechecked verdict — re-recording would silently replace an add-only fact");
    }

    internal void AddOpEffects(
        HirNodeId opId,
        OpEffectSummary summary)
    {
        RequireEffectsOpen();
        if (!_effectSummaryByCallableId.TryAdd(opId, summary))
            throw new System.InvalidOperationException(
                $"QINTERNAL: op {opId} already has an effect summary — re-analysis would silently replace add-only facts");
    }

    /// <summary>
    /// Records the exact scalar target approved for one HIR expression. Repeating the same fact is
    /// harmless; assigning two contextual targets to one expression is an invalid HIR tree.
    /// </summary>
    internal void RecordImplicitConversion(
        HirNodeId expressionId,
        QType targetType)
    {
        RequireValidationOpen();
        if (!Enum.IsDefined(targetType) || targetType == QType.Qubit)
            throw new ArgumentOutOfRangeException(
                nameof(targetType),
                targetType,
                "an implicit HIR conversion requires a classical scalar target");
        if (_validation.ImplicitConversionTargets.TryGetValue(expressionId, out var existingTarget))
        {
            if (existingTarget == targetType) return;
            throw new InvalidOperationException(
                $"QINTERNAL: expression {expressionId} has conflicting implicit conversions to {existingTarget} and {targetType}");
        }

        _validation.ImplicitConversionTargets.Add(expressionId, targetType);
    }

    /// <summary>The scalar target HIR approved for this expression, or null when no conversion is needed.</summary>
    internal QType? FindImplicitConversionTarget(HirNodeId expressionId) =>
        _validation.ImplicitConversionTargets.TryGetValue(expressionId, out var type)
            ? type
            : null;

    /// <summary>The symbol declared by this node in this exact HIR generation, if any.</summary>
    public Symbol? FindSymbol(HirNodeId nodeId) =>
        _validation.ScopeGraph?.FindDeclaration(nodeId);

    /// <summary>
    /// The symbol selected for this exact HIR use-site expression, if any. This is the authoritative
    /// result of HIR name resolution; later stages must not repeat lookup from source spelling.
    /// </summary>
    public Symbol? FindReferencedSymbol(HirNodeId nodeId) =>
        _validation.ScopeGraph?.FindReferencedSymbol(nodeId);

    /// <summary>The callable scope of this operation (or of the operation it was derived from), if any.</summary>
    public Scope? FindRootScope(HirNodeId opId) =>
        _validation.ScopeGraph?.FindCallableScope(opId);

    /// <summary>Every HIR scope in the model, keyed by semantic scope identity.</summary>
    public IReadOnlyDictionary<ScopeId, Scope> Scopes =>
        _validation.ScopeGraph?.Scopes ?? EmptyScopes;

    /// <summary>This operation's effect summary (or its derivation source's), if any.</summary>
    public OpEffectSummary? FindOpEffects(HirNodeId opId) =>
        _effectSummaryByCallableId.TryGetValue(opId, out var summary)
            ? summary
            : null;

    /// <summary>Every operation root scope in the model, keyed by operation Id.</summary>
    public IReadOnlyDictionary<HirNodeId, Scope> RootScopes =>
        _validation.ScopeGraph?.CallableScopes ?? EmptyRootScopes;

    /// <summary>
    /// The unified HIR scope graph containing program, namespace, callable, and lexical scopes.
    /// </summary>
    public HirScopeGraph? ScopeGraph => _validation.ScopeGraph;

    private void RequireValidationOpen()
    {
        if (_validation.IsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: validation facts are sealed by an immutable HIR semantic artifact");
    }

    private void RequireEffectsOpen()
    {
        if (_effectsSealed)
            throw new InvalidOperationException(
                "QINTERNAL: effect facts are sealed by an immutable HIR semantic artifact");
    }

}
