using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// The stable address of one quantum MIR instruction. Instruction identities are local to a callable,
/// therefore both identities are required when an analysis result is used as a later rewrite anchor.
/// </summary>
public readonly record struct MirEffectSite
{
    public MirEffectSite(
        MirInstructionSite instruction,
        MirBlockId block)
    {
        Instruction = instruction;
        Block = block;
    }

    public MirInstructionSite Instruction { get; }
    public MirBlockId Block { get; }
    public MirCallableId Callable => Instruction.Callable;
}

/// <summary>
/// Orthogonal properties of one qubit operand. A value can be read at one point and written at another
/// inside a called operation, so a flag set is more precise than forcing an entire call into one role.
/// </summary>
[Flags]
public enum MirQubitEffectFlags
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Measure = 1 << 2,
    Irreversible = 1 << 3,
    NonQfree = 1 << 4,
    /// <summary>
    /// The call consumes the caller's ownership token for this qubit access. This is not a physical
    /// non-unitary effect, but replaying the call for cleanup is illegal because ownership restoration
    /// cannot be synthesized.
    /// </summary>
    OwnershipTransfer = 1 << 5,
}

public enum MirClassicalWitnessRole
{
    /// <summary>A scalar argument consumed by the gate or called operation.</summary>
    CallOperand,

    /// <summary>The SSA value selecting one element of a qubit register.</summary>
    QubitIndex,
}

public enum MirQuantumInstructionKind
{
    Apply,
    Measure,
}

/// <summary>
/// One exact scalar SSA value that must remain available if a later cleanup replays this instruction.
/// The value is captured by identity, not by source variable spelling, so a later assignment cannot
/// silently substitute a newer value.
/// </summary>
public sealed record MirClassicalWitness(
    MirValueId Value,
    MirType Type,
    MirClassicalWitnessRole Role,
    int? OperandIndex);

/// <summary>
/// Physical allocations which may back an array SSA state. More than one storage is possible after a
/// control-flow merge. <see cref="IsComplete"/> is false when provenance reaches an unsupported producer;
/// consumers must not interpret an empty or incomplete set as proof that no alias exists. Distinct parameter
/// storage IDs are also not disjointness proof; use <see cref="MirStorageAliasAnalysis.MayAlias"/> to
/// interpret this provenance together with the formal-region alias contracts.
/// </summary>
public sealed record MirStorageProvenance(
    IReadOnlyList<MirStorageId> PossibleStorages,
    bool IsComplete)
{
    private IReadOnlyList<MirStorageId> _possibleStorages =
        MirCollections.Freeze(PossibleStorages);

    public IReadOnlyList<MirStorageId> PossibleStorages
    {
        get => _possibleStorages;
        init => _possibleStorages = MirCollections.Freeze(value);
    }
}

/// <summary>
/// One array argument at a quantum call boundary. <see cref="InputState"/> is the exact pre-call SSA memory
/// state. A mutable borrowed argument also has an <see cref="OutputState"/> produced by the call.
/// </summary>
public sealed record MirArrayStateOperand(
    int OperandIndex,
    MirValueId InputState,
    MirValueId? OutputState,
    MirType Type,
    QOwnershipMode Ownership,
    QAccessMode Access,
    MirStorageProvenance Storage);

/// <summary>
/// One positional qubit argument and its effect. The access always retains the exact qubit version and the
/// exact dynamic index SSA value, even when a user-call summary conservatively aggregates the callee's
/// element-level accesses to the whole formal qubit.
/// </summary>
public sealed record MirQubitOperandEffect(
    int? OperandIndex,
    MirQubitAccess Access,
    MirQubitEffectFlags Flags);

/// <summary>A call target resolved within the exact MIR snapshot used by an effect result.</summary>
public abstract record MirEffectCallTarget
{
    public abstract string DisplayName { get; }
}

public sealed record MirEffectUserCallableTarget(
    MirCallableId Callable) : MirEffectCallTarget
{
    public override string DisplayName => Callable.ToString();
}

public sealed record MirEffectBuiltinGateTarget(
    string Name) : MirEffectCallTarget
{
    public override string DisplayName => Name;
}

/// <summary>
/// All replay-relevant facts extracted from one <see cref="MirQuantumApply"/> or
/// <see cref="MirMeasure"/>. It deliberately contains facts only; scheduling and insertion belong to
/// later passes.
/// </summary>
public sealed record MirQuantumInstructionEffect(
    MirEffectSite Site,
    MirQuantumInstructionKind Kind,
    MirEffectCallTarget? Target,
    IReadOnlyList<MirFunctor> Functors,
    IReadOnlyList<MirQubitOperandEffect> Qubits,
    IReadOnlyList<MirClassicalWitness> ClassicalWitnesses,
    IReadOnlyList<MirArrayStateOperand> ArrayStates,
    MirPathCondition PathCondition,
    MirExecutionMultiplicity ExecutionMultiplicity,
    IReadOnlyList<MirValueId> Results,
    IReadOnlyList<MirQubitKey> QubitResults,
    bool IsIrreversible,
    bool TransfersOwnership,
    MirOriginRef Origin)
{
    private IReadOnlyList<MirFunctor> _functors = MirCollections.Freeze(Functors);
    private IReadOnlyList<MirQubitOperandEffect> _qubits = MirCollections.Freeze(Qubits);
    private IReadOnlyList<MirClassicalWitness> _classicalWitnesses =
        MirCollections.Freeze(ClassicalWitnesses);
    private IReadOnlyList<MirArrayStateOperand> _arrayStates = MirCollections.Freeze(ArrayStates);
    private IReadOnlyList<MirValueId> _results = MirCollections.Freeze(Results);
    private IReadOnlyList<MirQubitKey> _qubitResults = MirCollections.Freeze(QubitResults);

    public IReadOnlyList<MirFunctor> Functors
    {
        get => _functors;
        init => _functors = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirQubitOperandEffect> Qubits
    {
        get => _qubits;
        init => _qubits = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirClassicalWitness> ClassicalWitnesses
    {
        get => _classicalWitnesses;
        init => _classicalWitnesses = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirArrayStateOperand> ArrayStates
    {
        get => _arrayStates;
        init => _arrayStates = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirValueId> Results
    {
        get => _results;
        init => _results = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirQubitKey> QubitResults
    {
        get => _qubitResults;
        init => _qubitResults = MirCollections.Freeze(value);
    }
}

/// <summary>
/// Conservative effect summary over one callable's formal qubits. This is sufficient to project
/// a user-operation call onto its actual <see cref="MirQubitAccess"/> operands without resolving names.
/// Element-sensitive interprocedural substitution is intentionally deferred.
/// </summary>
public sealed record MirFormalQubitEffect(
    MirQubitId Qubit,
    MirQubitEffectFlags Flags);

public sealed record MirCallableEffectSummary(
    MirCallableId Callable,
    IReadOnlyList<MirFormalQubitEffect> FormalQubits,
    bool IsIrreversible,
    bool TransfersOwnership)
{
    private IReadOnlyList<MirFormalQubitEffect> _formalQubits =
        MirCollections.Freeze(FormalQubits);

    public IReadOnlyList<MirFormalQubitEffect> FormalQubits
    {
        get => _formalQubits;
        init => _formalQubits = MirCollections.Freeze(value);
    }

    public MirQubitEffectFlags EffectOf(MirQubitId qubit) =>
        FormalQubits.FirstOrDefault(effect => effect.Qubit == qubit)?.Flags
        ?? MirQubitEffectFlags.None;
}

/// <summary>
/// Qubit semantics for one MIR quantum instruction, derived without consulting its declared
/// <see cref="MirQuantumApply.QubitResults"/>. The verifier can therefore compare
/// <see cref="WrittenQubits"/> with the instruction's declarations without making the semantic query
/// depend on the fact being verified.
/// </summary>
internal sealed class MirQubitEffectClassification
{
    internal MirQubitEffectClassification(
        IReadOnlyList<MirQubitOperandEffect> effects,
        bool isIrreversible,
        bool transfersOwnership)
    {
        Effects = MirCollections.Freeze(effects);
        WrittenQubits = effects
            .Where(effect => effect.Flags.HasFlag(MirQubitEffectFlags.Write))
            .Select(effect => effect.Access.Qubit.Id)
            .ToFrozenSet();
        IsIrreversible = isIrreversible;
        TransfersOwnership = transfersOwnership;
    }

    internal IReadOnlyList<MirQubitOperandEffect> Effects { get; }
    internal IReadOnlySet<MirQubitId> WrittenQubits { get; }
    internal bool IsIrreversible { get; }
    internal bool TransfersOwnership { get; }
}

/// <summary>
/// Snapshot-local, MIR-native authority for formal qubit effects and instruction write sets.
/// Construction is deliberately unchecked: callers must first establish structural MIR validity.
/// No query in this type reads <see cref="MirQuantumApply.QubitResults"/>, so the verifier can use it
/// to validate those declarations without a circular dependency.
/// </summary>
internal sealed class MirFormalQubitEffectQuery
{
    private readonly MirProgram _program;
    private readonly MirCallGraph _callGraph;
    private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
    private readonly FrozenDictionary<MirInstructionSite, MirCallSite> _quantumCalls;
    private readonly Dictionary<MirCallableId, MirCallableEffectSummary> _summaries = new();
    private readonly HashSet<MirCallableId> _summaryStack = new();

    internal MirFormalQubitEffectQuery(
        MirProgram program,
        MirCallGraph callGraph)
    {
        _program = program ?? throw new ArgumentNullException(nameof(program));
        _callGraph = callGraph ?? throw new ArgumentNullException(nameof(callGraph));
        _callGraph.EnsureFor(program);
        _callables = program.Callables.ToDictionary(callable => callable.Id);
        _quantumCalls = callGraph.Calls
            .Where(call => call.Kind == MirCallKind.QuantumApply)
            .ToFrozenDictionary(call => call.Instruction);
    }

    internal IReadOnlyList<MirCallableEffectSummary> SummarizeAll()
    {
        foreach (var callable in _program.Callables)
            SummaryOf(callable);
        return MirCollections.Freeze(
            _program.Callables
                .Select(callable => _summaries[callable.Id])
                .ToArray());
    }

    internal MirCallableEffectSummary SummaryOf(MirCallableId callable)
    {
        if (!_callables.TryGetValue(callable, out var definition))
            throw new InvalidOperationException(
                $"QINTERNAL: effect query targets missing callable {callable}");
        return SummaryOf(definition);
    }

    internal MirQubitEffectClassification ClassifyApply(
        MirCallable caller,
        MirQuantumApply apply)
    {
        var classified = apply.Target switch
        {
            MirBuiltinGateTarget builtin =>
                ClassifyBuiltin(caller, apply, builtin),
            MirUserCallableTarget user =>
                ClassifyUserCall(caller, apply, user),
            _ => throw new InvalidOperationException(
                $"QINTERNAL: quantum apply {apply.Id} uses non-quantum target " +
                $"`{apply.Target.DisplayName}`"),
        };

        if (!apply.Operands.Any(operand => operand.Ownership == QOwnershipMode.Moved))
            return classified;

        var effects = classified.Effects
            .Select(effect =>
            {
                if (effect.OperandIndex is not int operandIndex
                    || operandIndex < 0
                    || operandIndex >= apply.Operands.Count
                    || apply.Operands[operandIndex].Ownership != QOwnershipMode.Moved)
                {
                    return effect;
                }

                return effect with
                {
                    Flags = effect.Flags | MirQubitEffectFlags.OwnershipTransfer,
                };
            })
            .ToArray();
        return new MirQubitEffectClassification(
            effects,
            classified.IsIrreversible,
            transfersOwnership: true);
    }

    internal MirQubitEffectClassification ClassifyMeasure(
        MirCallable callable,
        MirMeasure measure) =>
        new(
            new[]
            {
                new MirQubitOperandEffect(
                    OperandIndex: null,
                    QubitAccess(callable, measure.Qubit),
                    MirQubitEffectFlags.Write
                    | MirQubitEffectFlags.Measure
                    | MirQubitEffectFlags.Irreversible),
            },
            isIrreversible: true,
            transfersOwnership: false);

    private MirCallableEffectSummary SummaryOf(MirCallable callable)
    {
        if (_summaries.TryGetValue(callable.Id, out var complete))
            return complete;
        if (!_summaryStack.Add(callable.Id))
            throw new InvalidOperationException(
                $"QINTERNAL: recursive MIR call graph reached `{callable.Name}` during effect analysis");

        try
        {
            foreach (var dependency in _callGraph.CallsFrom(callable.Id)
                         .Where(call => call.Kind == MirCallKind.QuantumApply)
                         .Select(call => call.Callee)
                         .Distinct())
            {
                SummaryOf(dependency);
            }

            var formalQubits = callable.Parameters
                .OfType<MirQubitParameter>()
                .Select(parameter => parameter.Id)
                .ToHashSet();
            var flagsByQubit = formalQubits.ToDictionary(
                qubit => qubit,
                _ => MirQubitEffectFlags.None);
            var irreversible = false;
            var transfersOwnership = false;

            foreach (var instruction in callable.Blocks.SelectMany(block => block.Instructions))
            {
                var classified = instruction switch
                {
                    MirQuantumApply apply => ClassifyApply(callable, apply),
                    MirMeasure measure => ClassifyMeasure(callable, measure),
                    _ => null,
                };
                if (classified is null)
                    continue;

                irreversible |= classified.IsIrreversible;
                transfersOwnership |= classified.TransfersOwnership;
                foreach (var effect in classified.Effects)
                {
                    if (flagsByQubit.ContainsKey(effect.Access.Qubit.Id))
                        flagsByQubit[effect.Access.Qubit.Id] |= effect.Flags;
                }
            }

            var formalEffects = callable.Parameters
                .OfType<MirQubitParameter>()
                .Select(parameter => new MirFormalQubitEffect(
                    parameter.Id,
                    flagsByQubit[parameter.Id]))
                .ToArray();
            complete = new MirCallableEffectSummary(
                callable.Id,
                formalEffects,
                irreversible,
                transfersOwnership);
            _summaries.Add(callable.Id, complete);
            return complete;
        }
        finally
        {
            _summaryStack.Remove(callable.Id);
        }
    }

    private MirQubitEffectClassification ClassifyBuiltin(
        MirCallable callable,
        MirQuantumApply apply,
        MirBuiltinGateTarget target)
    {
        if (!QoraGates.Gates.TryGetValue(target.Name, out var info))
            throw new InvalidOperationException(
                $"QINTERNAL: quantum apply {apply.Id} targets unknown built-in gate `{target.Name}`");

        var qubitOperands = apply.Operands
            .Select((operand, index) => (operand, index))
            .Where(item => item.operand is MirQubitCallOperand)
            .Select(item => ((MirQubitCallOperand)item.operand, item.index))
            .ToArray();
        var controlCount = info.Controls
            + apply.Functors.Count(functor => functor == MirFunctor.Controlled);
        var effects = new List<MirQubitOperandEffect>(qubitOperands.Length);

        for (var qubitIndex = 0; qubitIndex < qubitOperands.Length; qubitIndex++)
        {
            var (operand, operandIndex) = qubitOperands[qubitIndex];
            MirQubitEffectFlags flags;
            if (!info.Unitary)
            {
                flags = MirQubitEffectFlags.Write | MirQubitEffectFlags.Irreversible;
            }
            else if (qubitIndex < controlCount || info.Diagonal)
            {
                flags = MirQubitEffectFlags.Read;
            }
            else
            {
                flags = MirQubitEffectFlags.Write;
                if (info.NonQfree)
                    flags |= MirQubitEffectFlags.NonQfree;
            }

            effects.Add(new MirQubitOperandEffect(
                operandIndex,
                QubitAccess(callable, operand.Qubit),
                flags));
        }

        return new MirQubitEffectClassification(
            effects,
            isIrreversible: !info.Unitary,
            transfersOwnership: false);
    }

    private MirQubitEffectClassification ClassifyUserCall(
        MirCallable caller,
        MirQuantumApply apply,
        MirUserCallableTarget target)
    {
        if (!_callables.TryGetValue(target.Callable, out var callee))
            throw new InvalidOperationException(
                $"QINTERNAL: quantum apply {apply.Id} targets missing callable {target.Callable}");
        var instruction = new MirInstructionSite(caller.Id, apply.Id);
        if (!_quantumCalls.TryGetValue(instruction, out var call)
            || call.Callee != target.Callable)
        {
            throw new InvalidOperationException(
                $"QINTERNAL: quantum apply {instruction} disagrees with the MIR call graph");
        }
        var summary = SummaryOf(callee);
        var effects = new List<MirQubitOperandEffect>();

        for (var operandIndex = 0; operandIndex < callee.Parameters.Count; operandIndex++)
        {
            if (callee.Parameters[operandIndex] is not MirQubitParameter parameter
                || apply.Operands[operandIndex] is not MirQubitCallOperand actual)
            {
                continue;
            }

            var flags = summary.EffectOf(parameter.Id);
            // A call with any irreversible body cannot be replayed to undo an otherwise ordinary write.
            // This matches the current HIR effect contract while keeping measurement explicit.
            if (summary.IsIrreversible
                && flags.HasFlag(MirQubitEffectFlags.Write)
                && !flags.HasFlag(MirQubitEffectFlags.Measure))
            {
                flags |= MirQubitEffectFlags.Irreversible;
            }

            effects.Add(new MirQubitOperandEffect(
                operandIndex,
                QubitAccess(caller, actual.Qubit),
                flags));
        }

        return new MirQubitEffectClassification(
            effects,
            summary.IsIrreversible,
            summary.TransfersOwnership);
    }

    private static MirQubitAccess QubitAccess(
        MirCallable callable,
        MirQubitAccess access)
    {
        callable.RequireQubit(access.Qubit);
        if (access.Index is MirValueId index)
            callable.RequireValue(index);
        return access;
    }
}

/// <summary>
/// Immutable analysis data tied to one exact MIR program revision. A transformed program must be analyzed
/// again; this prevents a cleanup pass from accidentally consuming instruction/value identities from a
/// stale snapshot.
/// </summary>
public sealed class MirEffectSnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly FrozenDictionary<MirEffectSite, MirQuantumInstructionEffect> _effectBySite;
    private readonly FrozenDictionary<MirCallableId, MirCallableEffectSummary> _summaryByCallable;

    internal MirEffectSnapshot(
        MirProgram sourceProgram,
        IReadOnlyList<MirQuantumInstructionEffect> effects,
        IReadOnlyList<MirCallableEffectSummary> summaries)
    {
        _sourceProgram = sourceProgram;
        SnapshotId = sourceProgram.SnapshotId;
        Effects = MirCollections.Freeze(effects);
        CallableSummaries = MirCollections.Freeze(summaries);
        _effectBySite = Effects.ToFrozenDictionary(effect => effect.Site);
        _summaryByCallable = CallableSummaries.ToFrozenDictionary(summary => summary.Callable);
    }

    public MirSnapshotId SnapshotId { get; }
    public IReadOnlyList<MirQuantumInstructionEffect> Effects { get; }
    public IReadOnlyList<MirCallableEffectSummary> CallableSummaries { get; }

    internal bool IsFor(MirProgram program) =>
        ReferenceEquals(_sourceProgram, program) && SnapshotId == program.SnapshotId;

    internal void EnsureFor(MirProgram program)
    {
        if (!IsFor(program))
            throw new InvalidOperationException(
                $"MIR effect snapshot belongs to {SnapshotId} of a different program instance; " +
                $"reanalyze snapshot {program.SnapshotId} before consuming it");
    }

    public MirQuantumInstructionEffect? EffectAt(MirEffectSite site)
        => _effectBySite.GetValueOrDefault(site);

    public MirCallableEffectSummary? SummaryOf(MirCallableId callable) =>
        _summaryByCallable.GetValueOrDefault(callable);

}

/// <summary>
/// First MIR-native quantum-effect layer. It captures direct SSA witnesses, array memory states and qubit
/// accesses, and computes interprocedural formal-qubit summaries. It does not decide cleanup safety,
/// choose a global cleanup order, or mutate MIR.
/// </summary>
internal static class MirEffectAnalysis
{
    /// <summary>
    /// Creates the semantic query used both by MIR verification and by the full effect analysis.
    /// The caller must first validate structural references, operand arity and operand kinds.
    /// </summary>
    internal static MirFormalQubitEffectQuery CreateFormalQubitEffectQueryUnchecked(
        MirProgram program) =>
        CreateFormalQubitEffectQueryUnchecked(
            program,
            MirCallGraphAnalysis.AnalyzeVerified(program));

    internal static MirFormalQubitEffectQuery CreateFormalQubitEffectQueryUnchecked(
        MirProgram program,
        MirCallGraph callGraph)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(callGraph);
        callGraph.EnsureFor(program);
        return new MirFormalQubitEffectQuery(program, callGraph);
    }

    internal static MirEffectSnapshot Analyze(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        QoraMirVerifier.VerifyOrThrow(program);
        var callGraph = MirCallGraphAnalysis.AnalyzeVerified(program);
        return new Analyzer(
            program,
            callGraph,
            callable => MirStorageProvenanceAnalysis.Analyze(program, callable),
            callable => MirPathConditionAnalysis.Analyze(program, callable)).Run();
    }

    /// <summary>
    /// Computes effects from dependency providers owned by the same verified MIR snapshot.
    /// </summary>
    internal static MirEffectSnapshot AnalyzeVerified(
        MirProgram program,
        MirCallGraph callGraph,
        Func<MirCallableId, MirStorageProvenanceSnapshot> storageProvenance,
        Func<MirCallableId, MirPathConditionSnapshot> pathConditions)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(callGraph);
        ArgumentNullException.ThrowIfNull(storageProvenance);
        ArgumentNullException.ThrowIfNull(pathConditions);
        callGraph.EnsureFor(program);
        return new Analyzer(program, callGraph, storageProvenance, pathConditions).Run();
    }

    private sealed class Analyzer
    {
        private readonly MirProgram _program;
        private readonly MirFormalQubitEffectQuery _qubitEffects;
        private readonly Func<MirCallableId, MirStorageProvenanceSnapshot> _storageProvenance;
        private readonly Func<MirCallableId, MirPathConditionSnapshot> _pathConditions;

        public Analyzer(
            MirProgram program,
            MirCallGraph callGraph,
            Func<MirCallableId, MirStorageProvenanceSnapshot> storageProvenance,
            Func<MirCallableId, MirPathConditionSnapshot> pathConditions)
        {
            _program = program;
            _qubitEffects = CreateFormalQubitEffectQueryUnchecked(program, callGraph);
            _storageProvenance = storageProvenance;
            _pathConditions = pathConditions;
        }

        public MirEffectSnapshot Run()
        {
            // Finish summaries first so instruction facts can project every user call in one deterministic
            // program-order pass. Validated HIR forbids recursive operation calls; a MIR cycle fails loudly.
            var orderedSummaries = _qubitEffects.SummarizeAll();

            var effects = new List<MirQuantumInstructionEffect>();
            foreach (var callable in _program.Callables)
            {
                var storage = _storageProvenance(callable.Id);
                storage.EnsureFor(_program, callable.Id);
                var paths = _pathConditions(callable.Id);
                paths.EnsureFor(_program, callable.Id);
                foreach (var block in callable.Blocks)
                {
                    foreach (var instruction in block.Instructions)
                    {
                        switch (instruction)
                        {
                            case MirQuantumApply apply:
                                effects.Add(DescribeApply(
                                    callable,
                                    block,
                                    apply,
                                    storage,
                                    paths));
                                break;
                            case MirMeasure measure:
                                effects.Add(DescribeMeasure(
                                    callable,
                                    block,
                                    measure,
                                    paths));
                                break;
                        }
                    }
                }
            }

            return new MirEffectSnapshot(_program, effects, orderedSummaries);
        }

        private MirQuantumInstructionEffect DescribeApply(
            MirCallable callable,
            MirBlock block,
            MirQuantumApply apply,
            MirStorageProvenanceSnapshot storage,
            MirPathConditionSnapshot paths)
        {
            var classified = _qubitEffects.ClassifyApply(callable, apply);
            var witnesses = new List<MirClassicalWitness>();
            var arrays = new List<MirArrayStateOperand>();
            var mutableResultByOperand = apply.MutableArrayResults
                .ToDictionary(result => result.OperandIndex, result => result.Result);

            for (var operandIndex = 0; operandIndex < apply.Operands.Count; operandIndex++)
            {
                switch (apply.Operands[operandIndex])
                {
                    case MirClassicalCallOperand classical:
                    {
                        var value = RequiredValue(callable, classical.Value, apply.Id);
                        if (value.Type.IsArray)
                        {
                            arrays.Add(new MirArrayStateOperand(
                                operandIndex,
                                classical.Value,
                                mutableResultByOperand.TryGetValue(
                                    operandIndex,
                                    out var mutableResult)
                                    ? mutableResult
                                    : null,
                                value.Type,
                                classical.Ownership,
                                classical.Access,
                                storage.ProvenanceOf(classical.Value)));
                        }
                        else
                        {
                            witnesses.Add(new MirClassicalWitness(
                                classical.Value,
                                value.Type,
                                MirClassicalWitnessRole.CallOperand,
                                operandIndex));
                        }
                        break;
                    }
                    case MirQubitCallOperand { Qubit.Index: MirValueId index }:
                    {
                        var value = RequiredValue(callable, index, apply.Id);
                        witnesses.Add(new MirClassicalWitness(
                            index,
                            value.Type,
                            MirClassicalWitnessRole.QubitIndex,
                            operandIndex));
                        break;
                    }
                }
            }

            return new MirQuantumInstructionEffect(
                Site(callable, block, apply),
                MirQuantumInstructionKind.Apply,
                Target(apply.Target),
                ReadOnly(apply.Functors),
                classified.Effects,
                ReadOnly(witnesses),
                ReadOnly(arrays),
                paths.ConditionFor(block.Id),
                paths.MultiplicityOf(block.Id),
                ReadOnly(apply.ResultValues.ToArray()),
                ReadOnly(apply.QubitResults.Select(qubit => qubit.Key).ToArray()),
                classified.IsIrreversible,
                classified.TransfersOwnership,
                apply.Origin);
        }

        private MirQuantumInstructionEffect DescribeMeasure(
            MirCallable callable,
            MirBlock block,
            MirMeasure measure,
            MirPathConditionSnapshot paths)
        {
            var classified = _qubitEffects.ClassifyMeasure(callable, measure);
            var witnesses = new List<MirClassicalWitness>();
            if (measure.Qubit.Index is MirValueId index)
            {
                var value = RequiredValue(callable, index, measure.Id);
                witnesses.Add(new MirClassicalWitness(
                    index,
                    value.Type,
                    MirClassicalWitnessRole.QubitIndex,
                    OperandIndex: null));
            }

            return new MirQuantumInstructionEffect(
                Site(callable, block, measure),
                MirQuantumInstructionKind.Measure,
                Target: null,
                Functors: Array.Empty<MirFunctor>(),
                Qubits: classified.Effects,
                ClassicalWitnesses: ReadOnly(witnesses),
                ArrayStates: Array.Empty<MirArrayStateOperand>(),
                PathCondition: paths.ConditionFor(block.Id),
                ExecutionMultiplicity: paths.MultiplicityOf(block.Id),
                Results: ReadOnly(measure.ResultValues.ToArray()),
                QubitResults: ReadOnly(new[] { measure.QubitResult.Key }),
                IsIrreversible: classified.IsIrreversible,
                TransfersOwnership: classified.TransfersOwnership,
                measure.Origin);
        }

        private static MirValue RequiredValue(
            MirCallable callable,
            MirValueId value,
            MirInstructionId instruction) =>
            callable.FindValue(value)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: instruction {instruction} in `{callable.Name}` references missing value {value}");

        private static MirEffectSite Site(
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction) =>
            new(
                new MirInstructionSite(callable.Id, instruction.Id),
                block.Id);

        private MirEffectCallTarget Target(MirCallTarget target) =>
            target switch
            {
                MirUserCallableTarget user =>
                    new MirEffectUserCallableTarget(user.Callable),
                MirBuiltinGateTarget builtin =>
                    new MirEffectBuiltinGateTarget(builtin.Name),
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: unsupported quantum effect target `{target.DisplayName}`"),
            };

        private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> items) =>
            Array.AsReadOnly(items.ToArray());
    }

    private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> items) =>
        Array.AsReadOnly(items.ToArray());
}
