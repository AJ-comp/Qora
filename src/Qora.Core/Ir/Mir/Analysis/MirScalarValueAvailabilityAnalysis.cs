namespace Qora.Ir.Mir.Analysis;

public enum MirScalarValueAvailabilityKind
{
    Available,
    Rematerializable,
    Unavailable,
}

/// <summary>
/// Availability of one exact scalar SSA value at a program point. A non-dominating pure expression can
/// still be rematerialized from its immutable inputs; <see cref="Recipe"/> lists those MIR instructions
/// in dependency order. Measurements and values depending on unavailable Phi inputs are not replayed.
/// </summary>
public sealed record MirScalarValueAvailability(
    MirValueRef Value,
    MirScalarValueAvailabilityKind Kind,
    IReadOnlyList<MirInstructionRef> Recipe)
{
    private IReadOnlyList<MirInstructionRef> _recipe = MirCollections.Freeze(Recipe);

    public IReadOnlyList<MirInstructionRef> Recipe
    {
        get => _recipe;
        init => _recipe = MirCollections.Freeze(value);
    }

    public bool CanSupplyValue =>
        Kind is MirScalarValueAvailabilityKind.Available
            or MirScalarValueAvailabilityKind.Rematerializable;
}

/// <summary>
/// Separates physical scalar SSA availability from pure rematerialization. This is the MIR replacement
/// for source-level rules such as "only literals and const are stable": the decision follows the actual
/// value-definition graph, including pure calls and array reads whose exact memory state is still current.
/// </summary>
public sealed class MirScalarValueAvailabilitySnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly MirCallable _callable;
    private readonly MirControlFlowSnapshot _cfg;
    private readonly MirMemoryStateSnapshot _memory;
    private readonly IReadOnlyDictionary<MirInstructionId, MirInstruction> _instructions;

    internal MirScalarValueAvailabilitySnapshot(
        MirProgram sourceProgram,
        MirCallable callable,
        MirControlFlowSnapshot cfg,
        MirMemoryStateSnapshot memory)
    {
        _sourceProgram = sourceProgram;
        _callable = callable;
        _cfg = cfg;
        _memory = memory;
        _instructions = callable.Blocks
            .SelectMany(block => block.Instructions)
            .ToDictionary(instruction => instruction.Id);
        SnapshotId = sourceProgram.SnapshotId;
        Callable = new MirCallableRef(SnapshotId, callable.Id);
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }
    internal MirControlFlowSnapshot ControlFlow => _cfg;
    internal MirMemoryStateSnapshot MemoryState => _memory;

    internal bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_callable, program.FindCallable(callable))
        && SnapshotId == program.SnapshotId
        && Callable.Callable == callable;

    public MirScalarValueAvailability CheckBeforeInstruction(
        MirValueRef value,
        MirInstructionRef instruction)
    {
        Require(value);
        return Check(value.Value, _cfg.PointBeforeInstruction(instruction));
    }

    public MirScalarValueAvailability CheckAtTerminator(
        MirValueRef value,
        MirBlockRef block)
    {
        Require(value);
        return Check(value.Value, _cfg.TerminatorPoint(block));
    }

    internal MirScalarValueAvailability Check(
        MirValueId value,
        MirProgramPoint point)
    {
        var definition = _callable.FindValue(value)
            ?? throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"value {value} does not belong to callable {Callable}");
        if (definition.Type.IsArray)
            throw new ArgumentException(
                $"value {value} has array type {definition.Type}; scalar availability requires a scalar",
                nameof(value));

        var cache = new Dictionary<MirValueId, MirScalarValueAvailability>();
        var active = new HashSet<MirValueId>();
        return Resolve(value);

        MirScalarValueAvailability Resolve(MirValueId current)
        {
            if (cache.TryGetValue(current, out var cached))
                return cached;
            if (!active.Add(current))
                return Unavailable(current);

            try
            {
                var currentValue = _callable.FindValue(current);
                if (currentValue is null || currentValue.Type.IsArray)
                    return Cache(Unavailable(current));
                if (_cfg.IsValueAvailableAt(current, point))
                    return Cache(new MirScalarValueAvailability(
                        ValueRef(current),
                        MirScalarValueAvailabilityKind.Available,
                        Array.Empty<MirInstructionRef>()));
                if (currentValue.Definition.Kind != MirValueDefinitionKind.InstructionResult
                    || currentValue.Definition.Instruction is not MirInstructionId instructionId
                    || !_instructions.TryGetValue(instructionId, out var instruction))
                    return Cache(Unavailable(current));

                var dependencies = new List<MirInstructionId>();
                foreach (var input in instruction.InputValues)
                {
                    var inputValue = _callable.FindValue(input);
                    if (inputValue is null)
                        return Cache(Unavailable(current));
                    if (inputValue.Type.IsArray)
                    {
                        var memory = _memory.CheckAtLocation(
                            input,
                            point.Block.Block,
                            point.InstructionIndex);
                        if (!memory.IsAvailable)
                            return Cache(Unavailable(current));
                        continue;
                    }

                    var inputAvailability = Resolve(input);
                    if (!inputAvailability.CanSupplyValue)
                        return Cache(Unavailable(current));
                    dependencies.AddRange(
                        inputAvailability.Recipe.Select(reference => reference.Instruction));
                }

                if (!IsPureRematerializable(instruction))
                    return Cache(Unavailable(current));
                dependencies.Add(instruction.Id);
                return Cache(new MirScalarValueAvailability(
                    ValueRef(current),
                    MirScalarValueAvailabilityKind.Rematerializable,
                    dependencies
                        .Distinct()
                        .Select(InstructionRef)
                        .ToArray()));
            }
            finally
            {
                active.Remove(current);
            }
        }

        MirScalarValueAvailability Cache(MirScalarValueAvailability result)
        {
            cache[result.Value.Value] = result;
            return result;
        }
    }

    private void Require(MirValueRef value)
    {
        MirReferenceValidation.RequireSnapshot(
            SnapshotId,
            value.Snapshot,
            nameof(value));
        if (value.Callable != Callable.Callable)
            throw new ArgumentException(
                $"MIR value belongs to callable {value.Callable}; expected {Callable}",
                nameof(value));
    }

    private MirValueRef ValueRef(MirValueId value) =>
        new(SnapshotId, Callable.Callable, value);

    private MirInstructionRef InstructionRef(MirInstructionId instruction) =>
        new(SnapshotId, Callable.Callable, instruction);

    private static bool IsPureRematerializable(MirInstruction instruction) =>
        instruction is MirConstant
            or MirUnary
            or MirBinary
            or MirConvert
            or MirArrayLength
            or MirArrayLoad
            or MirPureCall;

    private MirScalarValueAvailability Unavailable(MirValueId value) =>
        new(
            ValueRef(value),
            MirScalarValueAvailabilityKind.Unavailable,
            Array.Empty<MirInstructionRef>());
}

internal static class MirScalarValueAvailabilityAnalysis
{
    internal static MirScalarValueAvailabilitySnapshot Analyze(
        MirProgram program,
        MirCallableId callableId)
    {
        ArgumentNullException.ThrowIfNull(program);
        QoraMirVerifier.VerifyOrThrow(program);
        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        return AnalyzeVerified(
            program,
            callable,
            MirControlFlowAnalysis.Analyze(program, callableId),
            MirMemoryStateAnalysis.Analyze(program, callableId));
    }

    /// <summary>
    /// Builds scalar-availability queries from analysis dependencies owned by the same verified snapshot.
    /// </summary>
    internal static MirScalarValueAvailabilitySnapshot AnalyzeVerified(
        MirProgram program,
        MirCallable callable,
        MirControlFlowSnapshot cfg,
        MirMemoryStateSnapshot memory)
    {
        cfg.EnsureFor(program, callable.Id);
        memory.EnsureFor(program, callable.Id);
        return new MirScalarValueAvailabilitySnapshot(
            program,
            callable,
            cfg,
            memory);
    }
}
