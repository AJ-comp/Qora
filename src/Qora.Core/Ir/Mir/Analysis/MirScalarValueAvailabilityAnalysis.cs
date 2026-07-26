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
    MirValueId Value,
    MirScalarValueAvailabilityKind Kind,
    IReadOnlyList<MirInstructionId> Recipe)
{
    private IReadOnlyList<MirInstructionId> _recipe = MirCollections.Freeze(Recipe);

    public IReadOnlyList<MirInstructionId> Recipe
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
        ProgramRevision = sourceProgram.Revision;
        Callable = callable.Id;
    }

    public int ProgramRevision { get; }
    public MirCallableId Callable { get; }

    public bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_callable, program.FindCallable(callable))
        && ProgramRevision == program.Revision
        && Callable == callable;

    public MirScalarValueAvailability CheckBeforeInstruction(
        MirValueId value,
        MirInstructionId instruction) =>
        Check(value, _cfg.PointBeforeInstruction(instruction));

    public MirScalarValueAvailability CheckAtTerminator(
        MirValueId value,
        MirBlockId block) =>
        Check(value, _cfg.TerminatorPoint(block));

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
                        current,
                        MirScalarValueAvailabilityKind.Available,
                        Array.Empty<MirInstructionId>()));
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
                            point.Block,
                            point.InstructionIndex);
                        if (!memory.IsAvailable)
                            return Cache(Unavailable(current));
                        continue;
                    }

                    var inputAvailability = Resolve(input);
                    if (!inputAvailability.CanSupplyValue)
                        return Cache(Unavailable(current));
                    dependencies.AddRange(inputAvailability.Recipe);
                }

                if (!IsPureRematerializable(instruction))
                    return Cache(Unavailable(current));
                dependencies.Add(instruction.Id);
                return Cache(new MirScalarValueAvailability(
                    current,
                    MirScalarValueAvailabilityKind.Rematerializable,
                    dependencies.Distinct().ToArray()));
            }
            finally
            {
                active.Remove(current);
            }
        }

        MirScalarValueAvailability Cache(MirScalarValueAvailability result)
        {
            cache[result.Value] = result;
            return result;
        }
    }

    private static bool IsPureRematerializable(MirInstruction instruction) =>
        instruction is MirConstant
            or MirUnary
            or MirBinary
            or MirConvert
            or MirArrayLength
            or MirArrayLoad
            or MirPureCall;

    private static MirScalarValueAvailability Unavailable(MirValueId value) =>
        new(
            value,
            MirScalarValueAvailabilityKind.Unavailable,
            Array.Empty<MirInstructionId>());
}

public static class MirScalarValueAvailabilityAnalysis
{
    public static MirScalarValueAvailabilitySnapshot Analyze(
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
        return new MirScalarValueAvailabilitySnapshot(
            program,
            callable,
            MirControlFlowAnalysis.Analyze(program, callableId),
            MirMemoryStateAnalysis.Analyze(program, callableId));
    }
}
