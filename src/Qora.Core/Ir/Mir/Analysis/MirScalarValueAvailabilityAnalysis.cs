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
    private readonly MirMemoryStateSnapshot _memory;

    internal MirScalarValueAvailabilitySnapshot(MirMemoryStateSnapshot memory)
    {
        _memory = memory;
    }

    public MirCallableId Callable => _memory.Callable;
    internal MirControlFlowSnapshot ControlFlow => _memory.ControlFlow;
    internal MirMemoryStateSnapshot MemoryState => _memory;
    private MirCallable SourceCallable => ControlFlow.SourceCallable;

    public MirScalarValueAvailability CheckBeforeInstruction(
        MirValueId value,
        MirInstructionId instruction) =>
        Check(value, ControlFlow.PointBeforeInstruction(instruction));

    public MirScalarValueAvailability CheckAtTerminator(
        MirValueId value,
        MirBlockId block) =>
        Check(value, ControlFlow.TerminatorPoint(block));

    internal MirScalarValueAvailability Check(
        MirValueId value,
        MirProgramPoint point)
    {
        var definition = SourceCallable.FindValue(value)
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
                var currentValue = SourceCallable.FindValue(current);
                if (currentValue is null || currentValue.Type.IsArray)
                    return Cache(Unavailable(current));
                if (ControlFlow.IsValueAvailableAt(current, point))
                    return Cache(new MirScalarValueAvailability(
                        current,
                        MirScalarValueAvailabilityKind.Available,
                        Array.Empty<MirInstructionId>()));
                var definition = SourceCallable.DefinitionOf(currentValue);
                if (definition.Kind != MirValueDefinitionKind.InstructionResult
                    || definition.Instruction is not MirInstructionId instructionId)
                    return Cache(Unavailable(current));
                var instruction = SourceCallable.RequireInstruction(instructionId);

                var dependencies = new List<MirInstructionId>();
                foreach (var input in instruction.InputValues)
                {
                    var inputValue = SourceCallable.FindValue(input);
                    if (inputValue is null)
                        return Cache(Unavailable(current));
                    if (inputValue.Type.IsArray)
                    {
                        var memory = _memory.Check(input, point);
                        if (!memory.IsAvailable)
                            return Cache(Unavailable(current));
                        continue;
                    }

                    var inputAvailability = Resolve(input);
                    if (!inputAvailability.CanSupplyValue)
                        return Cache(Unavailable(current));
                    dependencies.AddRange(
                        inputAvailability.Recipe);
                }

                if (!IsPureRematerializable(instruction))
                    return Cache(Unavailable(current));
                dependencies.Add(instruction.Id);
                return Cache(new MirScalarValueAvailability(
                    current,
                    MirScalarValueAvailabilityKind.Rematerializable,
                    MirCollections.Freeze(dependencies.Distinct())));
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

    private MirScalarValueAvailability Unavailable(MirValueId value) =>
        new(
            value,
            MirScalarValueAvailabilityKind.Unavailable,
            Array.Empty<MirInstructionId>());
}

internal static class MirScalarValueAvailabilityAnalysis
{
    /// <summary>
    /// Builds scalar-availability queries from analysis dependencies owned by the same verified snapshot.
    /// </summary>
    internal static MirScalarValueAvailabilitySnapshot AnalyzeVerified(
        MirMemoryStateSnapshot memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return new MirScalarValueAvailabilitySnapshot(memory);
    }
}
