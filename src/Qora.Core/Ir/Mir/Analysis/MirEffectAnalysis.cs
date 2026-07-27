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
        MirBlockRef block,
        MirInstructionRef instruction)
    {
        if (block.Snapshot != instruction.Snapshot
            || block.Callable != instruction.Callable)
        {
            throw new ArgumentException(
                "an effect block and instruction must belong to the same MIR snapshot and callable");
        }

        Block = block;
        Instruction = instruction;
    }

    public MirBlockRef Block { get; }
    public MirInstructionRef Instruction { get; }
    public MirSnapshotId Snapshot => Instruction.Snapshot;
    public MirCallableRef Callable => Instruction.CallableRef;
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
    /// The call consumes the caller's ownership token for this qubit place. This is not a physical
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
    MirValueRef Value,
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
    IReadOnlyList<MirStorageRef> PossibleStorages,
    bool IsComplete)
{
    private IReadOnlyList<MirStorageRef> _possibleStorages =
        MirCollections.Freeze(PossibleStorages);

    public IReadOnlyList<MirStorageRef> PossibleStorages
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
    MirValueRef InputState,
    MirValueRef? OutputState,
    MirType Type,
    QOwnershipMode Ownership,
    QAccessMode Access,
    MirStorageProvenance Storage);

/// <summary>
/// One positional qubit argument and its effect. The place always retains the resource identity and the
/// exact dynamic index SSA value, even when a user-call summary conservatively aggregates the callee's
/// element-level accesses to the whole formal resource.
/// </summary>
public sealed record MirQubitOperandEffect(
    int? OperandIndex,
    MirQubitPlaceRef Place,
    MirQubitEffectFlags Flags);

/// <summary>A call target resolved within the exact MIR snapshot used by an effect result.</summary>
public abstract record MirEffectCallTarget
{
    public abstract string DisplayName { get; }
}

public sealed record MirEffectUserCallableTarget(
    MirCallableRef Callable) : MirEffectCallTarget
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
    IReadOnlyList<MirValueRef> Results,
    bool IsIrreversible,
    bool TransfersOwnership,
    MirOriginRef Origin)
{
    private IReadOnlyList<MirFunctor> _functors = MirCollections.Freeze(Functors);
    private IReadOnlyList<MirQubitOperandEffect> _qubits = MirCollections.Freeze(Qubits);
    private IReadOnlyList<MirClassicalWitness> _classicalWitnesses =
        MirCollections.Freeze(ClassicalWitnesses);
    private IReadOnlyList<MirArrayStateOperand> _arrayStates = MirCollections.Freeze(ArrayStates);
    private IReadOnlyList<MirValueRef> _results = MirCollections.Freeze(Results);

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

    public IReadOnlyList<MirValueRef> Results
    {
        get => _results;
        init => _results = MirCollections.Freeze(value);
    }
}

/// <summary>
/// Conservative effect summary over one callable's formal qubit resources. This is sufficient to project
/// a user-operation call onto its actual <see cref="MirQubitPlace"/> operands without resolving names.
/// Element-sensitive interprocedural substitution is intentionally deferred.
/// </summary>
public sealed record MirFormalQubitEffect(
    MirQubitResourceRef Resource,
    MirQubitEffectFlags Flags);

public sealed record MirCallableEffectSummary(
    MirCallableRef Callable,
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

    public MirQubitEffectFlags EffectOf(MirQubitResourceRef resource)
    {
        MirReferenceValidation.RequireSnapshot(
            Callable.Snapshot,
            resource.Snapshot,
            nameof(resource));
        if (resource.Callable != Callable.Callable)
            throw new ArgumentException(
                $"qubit resource belongs to callable {resource.Callable}; expected {Callable}",
                nameof(resource));
        return EffectOf(resource.Resource);
    }

    internal MirQubitEffectFlags EffectOf(MirQubitResourceId resource) =>
        FormalQubits.FirstOrDefault(effect => effect.Resource.Resource == resource)?.Flags
        ?? MirQubitEffectFlags.None;
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
    private readonly FrozenDictionary<MirCallableRef, MirCallableEffectSummary> _summaryByCallable;

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
    {
        MirReferenceValidation.RequireSnapshot(
            SnapshotId,
            site.Snapshot,
            nameof(site));
        return _effectBySite.GetValueOrDefault(site);
    }

    public MirCallableEffectSummary? SummaryOf(MirCallableRef callable)
    {
        MirReferenceValidation.RequireSnapshot(
            SnapshotId,
            callable.Snapshot,
            nameof(callable));
        return _summaryByCallable.GetValueOrDefault(callable);
    }

    internal MirCallableEffectSummary? SummaryOf(MirCallableId callable) =>
        _summaryByCallable.GetValueOrDefault(new MirCallableRef(SnapshotId, callable));

}

/// <summary>
/// First MIR-native quantum-effect layer. It captures direct SSA witnesses, array memory states and qubit
/// places, and computes interprocedural formal-resource summaries. It does not decide cleanup safety,
/// choose a global cleanup order, or mutate MIR.
/// </summary>
internal static class MirEffectAnalysis
{
    internal static MirEffectSnapshot Analyze(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        QoraMirVerifier.VerifyOrThrow(program);
        return new Analyzer(
            program,
            callable => MirStorageProvenanceAnalysis.Analyze(program, callable),
            callable => MirPathConditionAnalysis.Analyze(program, callable)).Run();
    }

    /// <summary>
    /// Computes effects from dependency providers owned by the same verified MIR snapshot.
    /// </summary>
    internal static MirEffectSnapshot AnalyzeVerified(
        MirProgram program,
        Func<MirCallableId, MirStorageProvenanceSnapshot> storageProvenance,
        Func<MirCallableId, MirPathConditionSnapshot> pathConditions)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(storageProvenance);
        ArgumentNullException.ThrowIfNull(pathConditions);
        return new Analyzer(program, storageProvenance, pathConditions).Run();
    }

    private sealed class Analyzer
    {
        private readonly MirProgram _program;
        private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
        private readonly Func<MirCallableId, MirStorageProvenanceSnapshot> _storageProvenance;
        private readonly Func<MirCallableId, MirPathConditionSnapshot> _pathConditions;
        private readonly Dictionary<MirCallableId, MirCallableEffectSummary> _summaries = new();
        private readonly HashSet<MirCallableId> _summaryStack = new();

        public Analyzer(
            MirProgram program,
            Func<MirCallableId, MirStorageProvenanceSnapshot> storageProvenance,
            Func<MirCallableId, MirPathConditionSnapshot> pathConditions)
        {
            _program = program;
            _callables = program.Callables.ToDictionary(callable => callable.Id);
            _storageProvenance = storageProvenance;
            _pathConditions = pathConditions;
        }

        public MirEffectSnapshot Run()
        {
            // Finish summaries first so instruction facts can project every user call in one deterministic
            // program-order pass. Validated HIR forbids recursive operation calls; a MIR cycle fails loudly.
            foreach (var callable in _program.Callables)
                Summarize(callable);

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

            var orderedSummaries = _program.Callables
                .Select(callable => _summaries[callable.Id])
                .ToArray();
            return new MirEffectSnapshot(_program, effects, orderedSummaries);
        }

        private MirCallableEffectSummary Summarize(MirCallable callable)
        {
            if (_summaries.TryGetValue(callable.Id, out var complete))
                return complete;
            if (!_summaryStack.Add(callable.Id))
                throw new InvalidOperationException(
                    $"QINTERNAL: recursive MIR call graph reached `{callable.Name}` during effect analysis");

            try
            {
                var formalResources = callable.Parameters
                    .OfType<MirQubitParameter>()
                    .Select(parameter => parameter.Resource)
                    .ToHashSet();
                var flagsByResource = formalResources.ToDictionary(
                    resource => resource,
                    _ => MirQubitEffectFlags.None);
                var irreversible = false;
                var transfersOwnership = false;

                foreach (var block in callable.Blocks)
                {
                    foreach (var instruction in block.Instructions)
                    {
                        IReadOnlyList<MirQubitOperandEffect> effects;
                        bool instructionIrreversible;
                        bool instructionTransfersOwnership;
                        switch (instruction)
                        {
                            case MirQuantumApply apply:
                                (effects, instructionIrreversible, instructionTransfersOwnership) =
                                    ClassifyApply(callable, apply);
                                break;
                            case MirMeasure measure:
                                effects = new[]
                                {
                                    new MirQubitOperandEffect(
                                        OperandIndex: null,
                                        QubitPlace(callable, measure.Place),
                                        MirQubitEffectFlags.Write
                                        | MirQubitEffectFlags.Measure
                                        | MirQubitEffectFlags.Irreversible),
                                };
                                instructionIrreversible = true;
                                instructionTransfersOwnership = false;
                                break;
                            default:
                                continue;
                        }

                        irreversible |= instructionIrreversible;
                        transfersOwnership |= instructionTransfersOwnership;
                        foreach (var effect in effects)
                            if (flagsByResource.ContainsKey(effect.Place.Resource.Resource))
                                flagsByResource[effect.Place.Resource.Resource] |= effect.Flags;
                    }
                }

                var formalEffects = callable.Parameters
                    .OfType<MirQubitParameter>()
                    .Select(parameter => new MirFormalQubitEffect(
                        new MirQubitResourceRef(
                            _program.SnapshotId,
                            callable.Id,
                            parameter.Resource),
                        flagsByResource[parameter.Resource]))
                    .ToArray();
                complete = new MirCallableEffectSummary(
                    new MirCallableRef(_program.SnapshotId, callable.Id),
                    ReadOnly(formalEffects),
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

        private MirQuantumInstructionEffect DescribeApply(
            MirCallable callable,
            MirBlock block,
            MirQuantumApply apply,
            MirStorageProvenanceSnapshot storage,
            MirPathConditionSnapshot paths)
        {
            var (qubits, irreversible, transfersOwnership) = ClassifyApply(callable, apply);
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
                                Value(callable, classical.Value),
                                mutableResultByOperand.TryGetValue(
                                    operandIndex,
                                    out var mutableResult)
                                    ? Value(callable, mutableResult)
                                    : null,
                                value.Type,
                                classical.Ownership,
                                classical.Access,
                                storage.ProvenanceOf(classical.Value)));
                        }
                        else
                        {
                            witnesses.Add(new MirClassicalWitness(
                                Value(callable, classical.Value),
                                value.Type,
                                MirClassicalWitnessRole.CallOperand,
                                operandIndex));
                        }
                        break;
                    }
                    case MirQubitCallOperand { Place.Index: MirValueId index }:
                    {
                        var value = RequiredValue(callable, index, apply.Id);
                        witnesses.Add(new MirClassicalWitness(
                            Value(callable, index),
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
                ReadOnly(qubits),
                ReadOnly(witnesses),
                ReadOnly(arrays),
                paths.ConditionFor(block.Id),
                paths.MultiplicityOf(block.Id),
                ReadOnly(apply.ResultValues.Select(value => Value(callable, value)).ToArray()),
                irreversible,
                transfersOwnership,
                apply.Origin);
        }

        private MirQuantumInstructionEffect DescribeMeasure(
            MirCallable callable,
            MirBlock block,
            MirMeasure measure,
            MirPathConditionSnapshot paths)
        {
            var witnesses = new List<MirClassicalWitness>();
            if (measure.Place.Index is MirValueId index)
            {
                var value = RequiredValue(callable, index, measure.Id);
                witnesses.Add(new MirClassicalWitness(
                    Value(callable, index),
                    value.Type,
                    MirClassicalWitnessRole.QubitIndex,
                    OperandIndex: null));
            }

            var qubits = new[]
            {
                new MirQubitOperandEffect(
                    OperandIndex: null,
                    QubitPlace(callable, measure.Place),
                    MirQubitEffectFlags.Write
                    | MirQubitEffectFlags.Measure
                    | MirQubitEffectFlags.Irreversible),
            };
            return new MirQuantumInstructionEffect(
                Site(callable, block, measure),
                MirQuantumInstructionKind.Measure,
                Target: null,
                Functors: Array.Empty<MirFunctor>(),
                Qubits: ReadOnly(qubits),
                ClassicalWitnesses: ReadOnly(witnesses),
                ArrayStates: Array.Empty<MirArrayStateOperand>(),
                PathCondition: paths.ConditionFor(block.Id),
                ExecutionMultiplicity: paths.MultiplicityOf(block.Id),
                Results: ReadOnly(
                    measure.ResultValues.Select(value => Value(callable, value)).ToArray()),
                IsIrreversible: true,
                TransfersOwnership: false,
                measure.Origin);
        }

        private (
            IReadOnlyList<MirQubitOperandEffect> Effects,
            bool Irreversible,
            bool TransfersOwnership)
            ClassifyApply(
                MirCallable callable,
                MirQuantumApply apply)
        {
            var (baseEffects, irreversible, calleeTransfersOwnership) = apply.Target switch
            {
                MirBuiltinGateTarget builtin =>
                    AddTransferResult(
                        ClassifyBuiltin(callable, apply, builtin),
                        transfersOwnership: false),
                MirUserCallableTarget user => ClassifyUserCall(callable, apply, user),
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: quantum apply {apply.Id} uses non-quantum target `{apply.Target.DisplayName}`"),
            };

            var directTransfer = apply.Operands.Any(
                operand => operand.Ownership == QOwnershipMode.Moved);
            if (!directTransfer)
                return (baseEffects, irreversible, calleeTransfersOwnership);

            var effects = baseEffects
                .Select(effect =>
                {
                    if (effect.OperandIndex is not int operandIndex
                        || operandIndex < 0
                        || operandIndex >= apply.Operands.Count
                        || apply.Operands[operandIndex].Ownership != QOwnershipMode.Moved)
                        return effect;
                    return effect with
                    {
                        Flags = effect.Flags | MirQubitEffectFlags.OwnershipTransfer,
                    };
                })
                .ToArray();
            return (ReadOnly(effects), irreversible, TransfersOwnership: true);
        }

        private static (
            IReadOnlyList<MirQubitOperandEffect> Effects,
            bool Irreversible,
            bool TransfersOwnership)
            AddTransferResult(
                (IReadOnlyList<MirQubitOperandEffect> Effects, bool Irreversible) classified,
                bool transfersOwnership) =>
            (classified.Effects, classified.Irreversible, transfersOwnership);

        private (IReadOnlyList<MirQubitOperandEffect> Effects, bool Irreversible)
            ClassifyBuiltin(
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
                    if (info.NonQfree) flags |= MirQubitEffectFlags.NonQfree;
                }
                effects.Add(new MirQubitOperandEffect(
                    operandIndex,
                    QubitPlace(callable, operand.Place),
                    flags));
            }

            return (ReadOnly(effects), !info.Unitary);
        }

        private (
            IReadOnlyList<MirQubitOperandEffect> Effects,
            bool Irreversible,
            bool TransfersOwnership)
            ClassifyUserCall(
                MirCallable caller,
                MirQuantumApply apply,
                MirUserCallableTarget target)
        {
            if (!_callables.TryGetValue(target.Callable, out var callee))
                throw new InvalidOperationException(
                    $"QINTERNAL: quantum apply {apply.Id} targets missing callable {target.Callable}");
            var summary = Summarize(callee);
            var effects = new List<MirQubitOperandEffect>();

            for (var operandIndex = 0; operandIndex < callee.Parameters.Count; operandIndex++)
            {
                if (callee.Parameters[operandIndex] is not MirQubitParameter parameter
                    || apply.Operands[operandIndex] is not MirQubitCallOperand actual)
                    continue;

                var flags = summary.EffectOf(parameter.Resource);
                // A call with any irreversible body cannot be replayed to undo an otherwise ordinary write.
                // This matches the current HIR effect contract while keeping measurement explicit.
                if (summary.IsIrreversible
                    && flags.HasFlag(MirQubitEffectFlags.Write)
                    && !flags.HasFlag(MirQubitEffectFlags.Measure))
                    flags |= MirQubitEffectFlags.Irreversible;
                effects.Add(new MirQubitOperandEffect(
                    operandIndex,
                    QubitPlace(caller, actual.Place),
                    flags));
            }

            return (
                ReadOnly(effects),
                summary.IsIrreversible,
                summary.TransfersOwnership);
        }

        private static MirValue RequiredValue(
            MirCallable callable,
            MirValueId value,
            MirInstructionId instruction) =>
            callable.FindValue(value)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: instruction {instruction} in `{callable.Name}` references missing value {value}");

        private MirEffectSite Site(
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction) =>
            new(
                new MirBlockRef(_program.SnapshotId, callable.Id, block.Id),
                new MirInstructionRef(_program.SnapshotId, callable.Id, instruction.Id));

        private MirValueRef Value(
            MirCallable callable,
            MirValueId value) =>
            new(_program.SnapshotId, callable.Id, value);

        private MirQubitPlaceRef QubitPlace(
            MirCallable callable,
            MirQubitPlace place) =>
            new(
                new MirQubitResourceRef(
                    _program.SnapshotId,
                    callable.Id,
                    place.Resource),
                place.Index is MirValueId index
                    ? Value(callable, index)
                    : null);

        private MirEffectCallTarget Target(MirCallTarget target) =>
            target switch
            {
                MirUserCallableTarget user =>
                    new MirEffectUserCallableTarget(
                        new MirCallableRef(_program.SnapshotId, user.Callable)),
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
