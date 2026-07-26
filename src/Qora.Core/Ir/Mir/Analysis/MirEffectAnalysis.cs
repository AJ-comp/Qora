using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// The stable address of one quantum MIR instruction. Instruction identities are local to a callable,
/// therefore both identities are required when an analysis result is used as a later rewrite anchor.
/// </summary>
public readonly record struct MirEffectSite(
    MirCallableId Callable,
    MirBlockId Block,
    MirInstructionId Instruction);

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
/// One positional qubit argument and its effect. The place always retains the resource identity and the
/// exact dynamic index SSA value, even when a user-call summary conservatively aggregates the callee's
/// element-level accesses to the whole formal resource.
/// </summary>
public sealed record MirQubitOperandEffect(
    int? OperandIndex,
    MirQubitPlace Place,
    MirQubitEffectFlags Flags);

/// <summary>
/// All replay-relevant facts extracted from one <see cref="MirQuantumApply"/> or
/// <see cref="MirMeasure"/>. It deliberately contains facts only; scheduling and insertion belong to
/// later passes.
/// </summary>
public sealed record MirQuantumInstructionEffect(
    MirEffectSite Site,
    MirQuantumInstructionKind Kind,
    MirCallTarget? Target,
    IReadOnlyList<MirFunctor> Functors,
    IReadOnlyList<MirQubitOperandEffect> Qubits,
    IReadOnlyList<MirClassicalWitness> ClassicalWitnesses,
    IReadOnlyList<MirArrayStateOperand> ArrayStates,
    MirPathCondition PathCondition,
    MirExecutionMultiplicity ExecutionMultiplicity,
    IReadOnlyList<MirValueId> Results,
    bool IsIrreversible,
    bool TransfersOwnership,
    MirSource Source)
{
    private IReadOnlyList<MirFunctor> _functors = MirCollections.Freeze(Functors);
    private IReadOnlyList<MirQubitOperandEffect> _qubits = MirCollections.Freeze(Qubits);
    private IReadOnlyList<MirClassicalWitness> _classicalWitnesses =
        MirCollections.Freeze(ClassicalWitnesses);
    private IReadOnlyList<MirArrayStateOperand> _arrayStates = MirCollections.Freeze(ArrayStates);
    private IReadOnlyList<MirValueId> _results = MirCollections.Freeze(Results);

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
}

/// <summary>
/// Conservative effect summary over one callable's formal qubit resources. This is sufficient to project
/// a user-operation call onto its actual <see cref="MirQubitPlace"/> operands without resolving names.
/// Element-sensitive interprocedural substitution is intentionally deferred.
/// </summary>
public sealed record MirFormalQubitEffect(
    MirQubitResourceId Resource,
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

    public MirQubitEffectFlags EffectOf(MirQubitResourceId resource) =>
        FormalQubits.FirstOrDefault(effect => effect.Resource == resource)?.Flags
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
    private readonly FrozenDictionary<MirCallableId, MirCallableEffectSummary> _summaryByCallable;

    internal MirEffectSnapshot(
        MirProgram sourceProgram,
        IReadOnlyList<MirQuantumInstructionEffect> effects,
        IReadOnlyList<MirCallableEffectSummary> summaries)
    {
        _sourceProgram = sourceProgram;
        ProgramRevision = sourceProgram.Revision;
        Effects = MirCollections.Freeze(effects);
        CallableSummaries = MirCollections.Freeze(summaries);
        _effectBySite = Effects.ToFrozenDictionary(effect => effect.Site);
        _summaryByCallable = CallableSummaries.ToFrozenDictionary(summary => summary.Callable);
    }

    public int ProgramRevision { get; }
    public IReadOnlyList<MirQuantumInstructionEffect> Effects { get; }
    public IReadOnlyList<MirCallableEffectSummary> CallableSummaries { get; }

    public bool IsFor(MirProgram program) =>
        ReferenceEquals(_sourceProgram, program) && ProgramRevision == program.Revision;

    public void EnsureFor(MirProgram program)
    {
        if (!IsFor(program))
            throw new InvalidOperationException(
                $"MIR effect snapshot belongs to revision {ProgramRevision} of a different program instance; " +
                $"reanalyze revision {program.Revision} before consuming it");
    }

    public MirQuantumInstructionEffect? EffectAt(MirEffectSite site) =>
        _effectBySite.GetValueOrDefault(site);

    public MirCallableEffectSummary? SummaryOf(MirCallableId callable) =>
        _summaryByCallable.GetValueOrDefault(callable);

}

/// <summary>
/// First MIR-native quantum-effect layer. It captures direct SSA witnesses, array memory states and qubit
/// places, and computes interprocedural formal-resource summaries. It does not decide cleanup safety,
/// choose a global cleanup order, or mutate MIR.
/// </summary>
public static class MirEffectAnalysis
{
    public static MirEffectSnapshot Analyze(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        QoraMirVerifier.VerifyOrThrow(program);
        return new Analyzer(program).Run();
    }

    private sealed class Analyzer
    {
        private readonly MirProgram _program;
        private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
        private readonly Dictionary<MirCallableId, MirCallableEffectSummary> _summaries = new();
        private readonly HashSet<MirCallableId> _summaryStack = new();

        public Analyzer(MirProgram program)
        {
            _program = program;
            _callables = program.Callables.ToDictionary(callable => callable.Id);
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
                var storage = MirStorageProvenanceAnalysis.Analyze(
                    _program,
                    callable.Id);
                var paths = MirPathConditionAnalysis.Analyze(_program, callable.Id);
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
                                    ClassifyApply(apply);
                                break;
                            case MirMeasure measure:
                                effects = new[]
                                {
                                    new MirQubitOperandEffect(
                                        OperandIndex: null,
                                        measure.Place,
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
                            if (flagsByResource.ContainsKey(effect.Place.Resource))
                                flagsByResource[effect.Place.Resource] |= effect.Flags;
                    }
                }

                var formalEffects = callable.Parameters
                    .OfType<MirQubitParameter>()
                    .Select(parameter => new MirFormalQubitEffect(
                        parameter.Resource,
                        flagsByResource[parameter.Resource]))
                    .ToArray();
                complete = new MirCallableEffectSummary(
                    callable.Id,
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
            var (qubits, irreversible, transfersOwnership) = ClassifyApply(apply);
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
                                mutableResultByOperand.GetValueOrDefault(operandIndex),
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
                    case MirQubitCallOperand { Place.Index: MirValueId index }:
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
                new MirEffectSite(callable.Id, block.Id, apply.Id),
                MirQuantumInstructionKind.Apply,
                apply.Target,
                ReadOnly(apply.Functors),
                ReadOnly(qubits),
                ReadOnly(witnesses),
                ReadOnly(arrays),
                paths.ConditionFor(block.Id),
                paths.MultiplicityOf(block.Id),
                ReadOnly(apply.ResultValues),
                irreversible,
                transfersOwnership,
                apply.Source);
        }

        private static MirQuantumInstructionEffect DescribeMeasure(
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
                    index,
                    value.Type,
                    MirClassicalWitnessRole.QubitIndex,
                    OperandIndex: null));
            }

            var qubits = new[]
            {
                new MirQubitOperandEffect(
                    OperandIndex: null,
                    measure.Place,
                    MirQubitEffectFlags.Write
                    | MirQubitEffectFlags.Measure
                    | MirQubitEffectFlags.Irreversible),
            };
            return new MirQuantumInstructionEffect(
                new MirEffectSite(callable.Id, block.Id, measure.Id),
                MirQuantumInstructionKind.Measure,
                Target: null,
                Functors: Array.Empty<MirFunctor>(),
                Qubits: ReadOnly(qubits),
                ClassicalWitnesses: ReadOnly(witnesses),
                ArrayStates: Array.Empty<MirArrayStateOperand>(),
                PathCondition: paths.ConditionFor(block.Id),
                ExecutionMultiplicity: paths.MultiplicityOf(block.Id),
                Results: ReadOnly(measure.ResultValues),
                IsIrreversible: true,
                TransfersOwnership: false,
                measure.Source);
        }

        private (
            IReadOnlyList<MirQubitOperandEffect> Effects,
            bool Irreversible,
            bool TransfersOwnership)
            ClassifyApply(MirQuantumApply apply)
        {
            var (baseEffects, irreversible, calleeTransfersOwnership) = apply.Target switch
            {
                MirBuiltinGateTarget builtin =>
                    AddTransferResult(ClassifyBuiltin(apply, builtin), transfersOwnership: false),
                MirUserCallableTarget user => ClassifyUserCall(apply, user),
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

        private static (IReadOnlyList<MirQubitOperandEffect> Effects, bool Irreversible)
            ClassifyBuiltin(MirQuantumApply apply, MirBuiltinGateTarget target)
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
                effects.Add(new MirQubitOperandEffect(operandIndex, operand.Place, flags));
            }

            return (ReadOnly(effects), !info.Unitary);
        }

        private (
            IReadOnlyList<MirQubitOperandEffect> Effects,
            bool Irreversible,
            bool TransfersOwnership)
            ClassifyUserCall(MirQuantumApply apply, MirUserCallableTarget target)
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
                effects.Add(new MirQubitOperandEffect(operandIndex, actual.Place, flags));
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

        private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> items) =>
            Array.AsReadOnly(items.ToArray());
    }

    private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> items) =>
        Array.AsReadOnly(items.ToArray());
}
