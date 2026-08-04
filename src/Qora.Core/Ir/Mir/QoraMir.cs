using System.Collections.Frozen;

namespace Qora.Ir.Mir;

// The MIR owns stable identities within an explicit program or callable authority. IDs are not required
// to be contiguous: immutable owner-local indexes provide lookup without forcing unrelated entities to
// be renumbered after deletion or partial transformation. Program-wide sites pair a callable identity
// with one owner-local identity, while every entity retains an owner-local origin for diagnostics
// and derivation. No MIR pass resolves a value, block, or resource by source spelling.
public readonly record struct MirCallableId
{
    internal MirCallableId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"c{Value}";
}

public readonly record struct MirBlockId
{
    internal MirBlockId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"b{Value}";
}

public readonly record struct MirInstructionId
{
    internal MirInstructionId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"i{Value}";
}

/// <summary>
/// The program-wide location of one instruction inside one immutable MIR snapshot. The snapshot itself
/// is supplied by the owning <see cref="MirProgram"/> or <see cref="MirSnapshot"/>.
/// </summary>
public readonly record struct MirInstructionSite(
    MirCallableId Callable,
    MirInstructionId Instruction)
{
    public override string ToString() => $"{Callable}/{Instruction}";
}

public readonly record struct MirValueId
{
    internal MirValueId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"v{Value}";
}

public readonly record struct MirQubitId
{
    internal MirQubitId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"q{Value}";
}

public readonly record struct MirQubitVersion
{
    internal MirQubitVersion(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"v{Value}";
}

/// <summary>
/// The callable-local identity of one exact version of one MIR qubit binding.
/// </summary>
public readonly record struct MirQubitKey
{
    internal MirQubitKey(MirQubitId id, MirQubitVersion version)
    {
        Id = id;
        Version = version;
    }

    public MirQubitId Id { get; }
    public MirQubitVersion Version { get; }

    public override string ToString() => $"{Id}.{Version}";
}

public readonly record struct MirStorageId
{
    internal MirStorageId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"s{Value}";
}

/// <summary>
/// A classical MIR type. Qubits deliberately do not inhabit this type system; they are versioned
/// <see cref="MirQubit"/> definitions addressed through <see cref="MirQubitAccess"/>.
/// </summary>
public readonly record struct MirType
{
    private MirType(
        QType elementType,
        bool isArray,
        int? knownLength)
    {
        if (!Enum.IsDefined(elementType) || elementType == QType.Qubit)
            throw new ArgumentOutOfRangeException(
                nameof(elementType),
                elementType,
                "a classical MIR type requires a non-qubit element type");
        if (knownLength is < 0)
            throw new ArgumentOutOfRangeException(
                nameof(knownLength),
                knownLength,
                "an array length cannot be negative");

        ElementType = elementType;
        IsArray = isArray;
        KnownLength = knownLength;
    }

    public QType ElementType { get; }
    public bool IsArray { get; }
    public int? KnownLength { get; }

    public static MirType Scalar(QType type) =>
        new(type, isArray: false, knownLength: null);

    public static MirType Array(QType elementType, int? knownLength = null) =>
        new(elementType, isArray: true, knownLength);

    public override string ToString()
    {
        var element = ElementType.ToString().ToLowerInvariant();
        if (!IsArray) return element;
        return KnownLength is int length ? $"{element}[{length}]" : $"{element}[]";
    }
}

public enum MirCallableKind
{
    Operation,
    Function,
}

/// <summary>
/// The immutable SSA/CFG payload of one exact MIR snapshot. Its exact parameterless operation entry
/// callable is always owned by <see cref="Callables"/>. Construction stays inside the compiler pipeline
/// so external callers cannot attach a different program to an existing snapshot.
/// </summary>
public sealed class MirProgram
{
    private readonly FrozenDictionary<MirCallableId, MirCallable> _callables;

    internal MirProgram(
        MirCallable entryPoint,
        IEnumerable<MirCallable> callables)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentNullException.ThrowIfNull(callables);

        Callables = MirCollections.Freeze(callables);
        _callables = IndexCallables(Callables);

        if (!_callables.TryGetValue(entryPoint.Id, out var ownedEntryPoint)
            || !ReferenceEquals(ownedEntryPoint, entryPoint))
        {
            throw new ArgumentException(
                $"entry callable {entryPoint.Id} must be the exact object owned by this MIR program",
                nameof(entryPoint));
        }

        if (entryPoint.Kind != MirCallableKind.Operation)
        {
            throw new ArgumentException(
                "the MIR entry callable must be an operation",
                nameof(entryPoint));
        }

        if (entryPoint.Parameters.Count != 0)
        {
            throw new ArgumentException(
                "the MIR entry callable must not declare parameters",
                nameof(entryPoint));
        }

        EntryPoint = entryPoint;
    }

    public MirCallable EntryPoint { get; }
    public IReadOnlyList<MirCallable> Callables { get; }

    public bool ContainsCallable(MirCallableId id) =>
        _callables.ContainsKey(id);

    public bool ContainsCallable(MirCallable? callable) =>
        callable is not null
        && _callables.TryGetValue(callable.Id, out var owned)
        && ReferenceEquals(owned, callable);

    public MirCallable RequireCallable(MirCallableId id) =>
        _callables.TryGetValue(id, out var callable)
            ? callable
            : throw new ArgumentOutOfRangeException(
                nameof(id),
                id,
                $"callable {id} does not belong to this MIR program");

    public MirCallable RequireCallable(MirCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        return ContainsCallable(callable)
            ? callable
            : throw new ArgumentException(
                $"callable {callable.Id} is not the exact object owned by this MIR program",
                nameof(callable));
    }

    public bool ContainsInstruction(MirInstructionSite site) =>
        _callables.TryGetValue(site.Callable, out var callable)
        && callable.ContainsInstruction(site.Instruction);

    public MirInstruction RequireInstruction(MirInstructionSite site) =>
        RequireCallable(site.Callable).RequireInstruction(site.Instruction);

    public (MirCallable Callable, MirBlock Block, int Index)
        RequireInstructionLocation(MirInstructionSite site)
    {
        var callable = RequireCallable(site.Callable);
        var location = callable.RequireInstructionLocation(site.Instruction);
        return (callable, location.Block, location.Index);
    }

    internal MirCallable? FindCallable(MirCallableId id) =>
        _callables.GetValueOrDefault(id);

    private static FrozenDictionary<MirCallableId, MirCallable> IndexCallables(
        IEnumerable<MirCallable> callables)
    {
        var indexed = new Dictionary<MirCallableId, MirCallable>();
        foreach (var callable in callables)
        {
            if (!indexed.TryAdd(callable.Id, callable))
            {
                throw new ArgumentException(
                    $"callable identity {callable.Id} is declared more than once",
                    nameof(callables));
            }
        }
        return indexed.ToFrozenDictionary();
    }
}

/// <summary>
/// One lowered function or operation. Classical SSA values and versioned qubits use separate identity
/// spaces. Classical values use <see cref="Values"/> as their definition table. Qubit definitions stay
/// at their semantic definition sites (parameters, use instructions, quantum instructions, and block
/// Phis); <see cref="Qubits"/> is the immutable index derived from those sites.
/// </summary>
public sealed class MirCallable
{
    private readonly FrozenDictionary<MirBlockId, MirBlock> _blocks;
    private readonly FrozenDictionary<
        MirInstructionId,
        (MirInstruction Instruction, MirBlock Block, int Index)> _instructions;
    private readonly FrozenDictionary<MirValueId, MirValue> _values;
    private readonly FrozenDictionary<MirStorageId, MirArrayStorage> _storages;
    private readonly FrozenDictionary<
        MirStorageId,
        (
            MirClassicalParameter Parameter,
            int ParameterIndex,
            int DefinitionCount)> _parameterStorageDefinitions;
    private readonly FrozenDictionary<
        MirStorageId,
        (
            MirArrayCreate Allocation,
            int DefinitionCount)> _localStorageDefinitions;
    private readonly FrozenDictionary<MirQubitKey, MirQubit> _qubits;

    internal MirCallable(
        MirCallableId id,
        string name,
        MirType? returnType,
        IReadOnlyList<IMirParameter> parameters,
        MirBlock entryBlock,
        IReadOnlyList<MirBlock> blocks,
        IReadOnlyList<MirValue> values,
        IReadOnlyList<MirArrayStorage> storages,
        MirOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(entryBlock);

        Id = id;
        Name = name;
        ReturnType = returnType;
        Parameters = MirCollections.Freeze(parameters);
        Blocks = MirCollections.Freeze(blocks);
        Values = MirCollections.Freeze(values);
        Storages = MirCollections.Freeze(storages);
        _blocks = IndexUnique(Blocks, block => block.Id, nameof(blocks), "block");

        if (!_blocks.TryGetValue(entryBlock.Id, out var ownedEntryBlock)
            || !ReferenceEquals(ownedEntryBlock, entryBlock))
        {
            throw new ArgumentException(
                $"entry block {entryBlock.Id} must be the exact object owned by this MIR callable",
                nameof(entryBlock));
        }

        EntryBlock = entryBlock;
        Qubits = CollectQubits(Parameters, Blocks);
        _instructions = IndexInstructions(Blocks);
        _values = IndexUnique(Values, value => value.Id, nameof(values), "SSA value");
        _storages = IndexUnique(Storages, storage => storage.Id, nameof(storages), "array storage");
        _parameterStorageDefinitions =
            IndexParameterStorageDefinitions(Parameters);
        _localStorageDefinitions =
            IndexLocalStorageDefinitions(Blocks);
        _qubits = IndexUnique(Qubits, qubit => qubit.Key, nameof(Qubits), "qubit version");
        Origin = origin;
    }

    public MirCallableId Id { get; }
    public string Name { get; }
    public MirCallableKind Kind =>
        ReturnType is null
            ? MirCallableKind.Operation
            : MirCallableKind.Function;
    public MirType? ReturnType { get; }
    public IReadOnlyList<IMirParameter> Parameters { get; }
    public MirBlock EntryBlock { get; }
    public IReadOnlyList<MirBlock> Blocks { get; }
    public IReadOnlyList<MirValue> Values { get; }
    public IReadOnlyList<MirArrayStorage> Storages { get; }
    public IReadOnlyList<MirQubit> Qubits { get; }
    public MirOrigin Origin { get; }

    public bool ContainsBlock(MirBlockId id) =>
        _blocks.ContainsKey(id);

    public bool ContainsBlock(MirBlock? block) =>
        block is not null
        && _blocks.TryGetValue(block.Id, out var owned)
        && ReferenceEquals(owned, block);

    public MirBlock RequireBlock(MirBlockId id) =>
        _blocks.TryGetValue(id, out var block)
            ? block
            : throw Missing(nameof(id), id, "block");

    public MirBlock RequireBlock(MirBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return ContainsBlock(block)
            ? block
            : throw Foreign(nameof(block), block.Id, "block");
    }

    public bool ContainsInstruction(MirInstructionId id) =>
        _instructions.ContainsKey(id);

    public bool ContainsInstruction(MirInstruction? instruction) =>
        instruction is not null
        && _instructions.TryGetValue(instruction.Id, out var owned)
        && ReferenceEquals(owned.Instruction, instruction);

    public MirInstruction RequireInstruction(MirInstructionId id) =>
        _instructions.TryGetValue(id, out var location)
            ? location.Instruction
            : throw Missing(nameof(id), id, "instruction");

    public MirInstruction RequireInstruction(MirInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return ContainsInstruction(instruction)
            ? instruction
            : throw Foreign(nameof(instruction), instruction.Id, "instruction");
    }

    public (MirBlock Block, int Index) RequireInstructionLocation(
        MirInstructionId id) =>
        _instructions.TryGetValue(id, out var location)
            ? (location.Block, location.Index)
            : throw Missing(nameof(id), id, "instruction");

    public bool ContainsValue(MirValueId id) =>
        _values.ContainsKey(id);

    public bool ContainsValue(MirValue? value) =>
        value is not null
        && _values.TryGetValue(value.Id, out var owned)
        && ReferenceEquals(owned, value);

    public MirValue RequireValue(MirValueId id) =>
        _values.TryGetValue(id, out var value)
            ? value
            : throw Missing(nameof(id), id, "SSA value");

    public MirValue RequireValue(MirValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ContainsValue(value)
            ? value
            : throw Foreign(nameof(value), value.Id, "SSA value");
    }

    public bool ContainsStorage(MirStorageId id) =>
        _storages.ContainsKey(id);

    public bool ContainsStorage(MirArrayStorage? storage) =>
        storage is not null
        && _storages.TryGetValue(storage.Id, out var owned)
        && ReferenceEquals(owned, storage);

    public MirArrayStorage RequireStorage(MirStorageId id) =>
        _storages.TryGetValue(id, out var storage)
            ? storage
            : throw Missing(nameof(id), id, "array storage");

    public MirArrayStorage RequireStorage(MirArrayStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return ContainsStorage(storage)
            ? storage
            : throw Foreign(nameof(storage), storage.Id, "array storage");
    }

    /// <summary>
    /// Returns whether a storage is a parameter region or a local allocation. The kind is derived
    /// from the parameter or array-create site which owns the storage identity.
    /// </summary>
    public MirArrayStorageKind StorageKindOf(MirStorageId storage) =>
        StorageKindOf(RequireStorage(storage));

    public MirArrayStorageKind StorageKindOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        var totalDefinitionCount = StorageDefinitionCountOf(storage);
        if (totalDefinitionCount != 1)
        {
            throw MalformedStorage(
                storage,
                $"has {totalDefinitionCount} defining sites");
        }

        var parameterDefinitionCount =
            _parameterStorageDefinitions.TryGetValue(
                storage.Id,
                out var parameterDefinition)
                ? parameterDefinition.DefinitionCount
                : 0;

        return parameterDefinitionCount == 1
            ? MirArrayStorageKind.Parameter
            : MirArrayStorageKind.Local;
    }

    /// <summary>
    /// Returns the authoritative type of a storage region from the SSA value which defines it.
    /// Parameter storage is typed by its parameter value; local storage is typed by its
    /// <see cref="MirArrayCreate"/> result.
    /// </summary>
    public MirType StorageTypeOf(MirStorageId storage) =>
        StorageTypeOf(RequireStorage(storage));

    public MirType StorageTypeOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        return StorageKindOf(storage) switch
        {
            MirArrayStorageKind.Parameter =>
                RequireValue(ParameterDefining(storage).Value).Type,
            MirArrayStorageKind.Local =>
                RequireValue(AllocationDefining(storage).Result).Type,
            _ => throw MalformedStorage(
                storage,
                "has an unknown derived storage kind"),
        };
    }

    /// <summary>
    /// Derives the storage aliasing contract from its owner. Local allocations are unique.
    /// Parameter regions are shared only when the parameter is borrowed and read-only.
    /// </summary>
    public MirStorageAliasMode StorageAliasModeOf(MirStorageId storage) =>
        StorageAliasModeOf(RequireStorage(storage));

    public MirStorageAliasMode StorageAliasModeOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        var kind = StorageKindOf(storage);
        if (kind == MirArrayStorageKind.Local)
            return MirStorageAliasMode.UniqueLocal;

        if (kind != MirArrayStorageKind.Parameter)
            throw MalformedStorage(
                storage,
                "has an unknown derived storage kind");

        var parameter = ParameterDefining(storage);
        return parameter.Ownership == QOwnershipMode.Borrowed
               && parameter.Access == QAccessMode.ReadOnly
            ? MirStorageAliasMode.SharedParameter
            : MirStorageAliasMode.ExclusiveParameter;
    }

    internal int StorageParameterIndexOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        StorageKindOf(storage);
        return ParameterStorageDefinition(storage).ParameterIndex;
    }

    internal MirInstructionId StorageAllocationInstructionOf(
        MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        StorageKindOf(storage);
        return AllocationDefining(storage).Id;
    }

    internal int StorageDefinitionCountOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        var parameterDefinitionCount =
            _parameterStorageDefinitions.TryGetValue(
                storage.Id,
                out var parameterDefinition)
                ? parameterDefinition.DefinitionCount
                : 0;
        var localDefinitionCount =
            _localStorageDefinitions.TryGetValue(
                storage.Id,
                out var localDefinition)
                ? localDefinition.DefinitionCount
                : 0;
        return checked(parameterDefinitionCount + localDefinitionCount);
    }

    public bool ContainsQubit(MirQubitKey key) =>
        _qubits.ContainsKey(key);

    public bool ContainsQubit(MirQubit? qubit) =>
        qubit is not null
        && _qubits.TryGetValue(qubit.Key, out var owned)
        && ReferenceEquals(owned, qubit);

    public MirQubit RequireQubit(MirQubitKey key) =>
        _qubits.TryGetValue(key, out var qubit)
            ? qubit
            : throw Missing(nameof(key), key, "qubit version");

    public MirQubit RequireQubit(MirQubit qubit)
    {
        ArgumentNullException.ThrowIfNull(qubit);
        return ContainsQubit(qubit)
            ? qubit
            : throw Foreign(nameof(qubit), qubit.Key, "qubit version");
    }

    internal MirBlock? FindBlock(MirBlockId id) =>
        _blocks.GetValueOrDefault(id);

    internal MirValue? FindValue(MirValueId id) =>
        _values.GetValueOrDefault(id);

    internal MirArrayStorage? FindStorage(MirStorageId id) =>
        _storages.GetValueOrDefault(id);

    private MirClassicalParameter ParameterDefining(MirArrayStorage storage) =>
        ParameterStorageDefinition(storage).Parameter;

    private (
        MirClassicalParameter Parameter,
        int ParameterIndex,
        int DefinitionCount) ParameterStorageDefinition(
            MirArrayStorage storage)
    {
        if (!_parameterStorageDefinitions.TryGetValue(
                storage.Id,
                out var definition)
            || definition.DefinitionCount != 1)
        {
            throw MalformedStorage(
                storage,
                "does not identify its defining array parameter");
        }

        return definition;
    }

    private MirArrayCreate AllocationDefining(MirArrayStorage storage)
    {
        if (!_localStorageDefinitions.TryGetValue(
                storage.Id,
                out var definition)
            || definition.DefinitionCount != 1)
        {
            throw MalformedStorage(
                storage,
                "does not identify its defining array-create instruction");
        }

        return definition.Allocation;
    }

    private InvalidOperationException MalformedStorage(
        MirArrayStorage storage,
        string detail) =>
        new(
            $"QINTERNAL: array storage {storage.Id} in MIR callable {Id} {detail}");

    private static IReadOnlyList<MirQubit> CollectQubits(
        IReadOnlyList<IMirParameter> parameters,
        IReadOnlyList<MirBlock> blocks)
    {
        var qubits = new List<MirQubit>();
        qubits.AddRange(parameters.OfType<MirQubitParameter>());

        foreach (var block in blocks)
        {
            qubits.AddRange(block.QubitPhis);
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case MirQubitAllocate allocation:
                        qubits.Add(allocation.Result);
                        break;
                    case MirQuantumApply apply:
                        qubits.AddRange(apply.QubitResults);
                        break;
                    case MirMeasure measure:
                        qubits.Add(measure.QubitResult);
                        break;
                }
            }
        }

        return MirCollections.Freeze(qubits);
    }

    private static FrozenDictionary<TKey, TValue> IndexUnique<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> key,
        string parameterName,
        string entityName)
        where TKey : notnull
        where TValue : class
    {
        var indexed = new Dictionary<TKey, TValue>();
        foreach (var value in values)
        {
            var identity = key(value);
            if (!indexed.TryAdd(identity, value))
            {
                throw new ArgumentException(
                    $"{entityName} identity {identity} is declared more than once",
                    parameterName);
            }
        }
        return indexed.ToFrozenDictionary();
    }

    private static FrozenDictionary<
        MirInstructionId,
        (MirInstruction Instruction, MirBlock Block, int Index)> IndexInstructions(
        IEnumerable<MirBlock> blocks)
    {
        var indexed =
            new Dictionary<
                MirInstructionId,
                (MirInstruction Instruction, MirBlock Block, int Index)>();
        foreach (var block in blocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                if (!indexed.TryAdd(instruction.Id, (instruction, block, index)))
                {
                    throw new ArgumentException(
                        $"instruction identity {instruction.Id} is declared more than once",
                        nameof(blocks));
                }
            }
        }
        return indexed.ToFrozenDictionary();
    }

    private static FrozenDictionary<
        MirStorageId,
        (
            MirClassicalParameter Parameter,
            int ParameterIndex,
            int DefinitionCount)> IndexParameterStorageDefinitions(
        IReadOnlyList<IMirParameter> parameters)
    {
        var indexed = new Dictionary<
            MirStorageId,
            (
                MirClassicalParameter Parameter,
                int ParameterIndex,
                int DefinitionCount)>();
        for (var parameterIndex = 0;
             parameterIndex < parameters.Count;
             parameterIndex++)
        {
            if (parameters[parameterIndex] is not MirClassicalParameter
                {
                    Storage: MirStorageId storage
                } parameter)
            {
                continue;
            }

            if (indexed.TryGetValue(storage, out var existing))
            {
                indexed[storage] =
                    (
                        existing.Parameter,
                        existing.ParameterIndex,
                        checked(existing.DefinitionCount + 1));
                continue;
            }

            indexed.Add(
                storage,
                (parameter, parameterIndex, DefinitionCount: 1));
        }

        return indexed.ToFrozenDictionary();
    }

    private static FrozenDictionary<
        MirStorageId,
        (
            MirArrayCreate Allocation,
            int DefinitionCount)> IndexLocalStorageDefinitions(
        IReadOnlyList<MirBlock> blocks)
    {
        var indexed = new Dictionary<
            MirStorageId,
            (
                MirArrayCreate Allocation,
                int DefinitionCount)>();
        foreach (var block in blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not MirArrayCreate allocation)
                    continue;

                if (indexed.TryGetValue(allocation.Storage, out var existing))
                {
                    indexed[allocation.Storage] =
                        (
                            existing.Allocation,
                            checked(existing.DefinitionCount + 1));
                    continue;
                }

                indexed.Add(
                    allocation.Storage,
                    (allocation, DefinitionCount: 1));
            }
        }

        return indexed.ToFrozenDictionary();
    }

    private ArgumentOutOfRangeException Missing<TId>(
        string parameter,
        TId id,
        string entity) =>
        new(
            parameter,
            id,
            $"{entity} {id} does not belong to MIR callable {Id}");

    private ArgumentException Foreign<TId>(
        string parameter,
        TId id,
        string entity) =>
        new(
            $"{entity} {id} is not the exact object owned by MIR callable {Id}",
            parameter);
}

// Parameters remain ordered because call operands are positional.
public interface IMirParameter
{
    string Name { get; }
}

public sealed record MirClassicalParameter(
    string Name,
    MirValueId Value,
    MirStorageId? Storage = null,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed,
    QAccessMode Access = QAccessMode.ReadOnly,
    int MinimumLength = 0)
    : IMirParameter;

public enum MirValueDefinitionKind
{
    Parameter,
    BlockArgument,
    InstructionResult,
}

/// <summary>
/// The one definition position of an SSA value. <see cref="Index"/> is the parameter index, block
/// argument index, or result index according to <see cref="Kind"/>. A block identifies a block
/// argument, an instruction identifies an instruction result, and the absence of both identifies a
/// parameter. An instruction result's block is derived from its instruction location.
/// </summary>
public sealed record MirValueDefinition
{
    private MirValueDefinition(
        int index,
        MirBlockId? block,
        MirInstructionId? instruction)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "an SSA definition index cannot be negative");

        Index = index;
        Block = block;
        Instruction = instruction;
    }

    public MirValueDefinitionKind Kind =>
        Block is not null
            ? MirValueDefinitionKind.BlockArgument
            : Instruction is not null
                ? MirValueDefinitionKind.InstructionResult
                : MirValueDefinitionKind.Parameter;

    public int Index { get; }
    public MirBlockId? Block { get; }
    public MirInstructionId? Instruction { get; }

    public static MirValueDefinition ParameterAt(int parameterIndex) =>
        new(parameterIndex, block: null, instruction: null);

    public static MirValueDefinition BlockArgumentAt(MirBlockId block, int argumentIndex) =>
        new(argumentIndex, block, instruction: null);

    public static MirValueDefinition InstructionResultAt(
        MirInstructionId instruction,
        int resultIndex = 0) =>
        new(resultIndex, block: null, instruction);
}

public sealed record MirValue(
    MirValueId Id,
    MirType Type,
    MirValueDefinition Definition,
    MirOrigin Origin);

public enum MirArrayStorageKind
{
    Parameter,
    Local,
}

/// <summary>
/// The aliasing contract attached to an array storage identity.
///
/// A parameter storage is a symbolic formal region, not proof of a distinct physical allocation:
/// two <see cref="SharedParameter"/> regions may denote the same caller array even when their
/// <see cref="MirStorageId"/> values differ. <see cref="ExclusiveParameter"/> relies on the validated
/// source call contract, which forbids a mutable or moved argument from overlapping any other argument
/// in that call. A <see cref="UniqueLocal"/> region is created by this callable and cannot overlap a
/// different storage region.
/// </summary>
public enum MirStorageAliasMode
{
    UniqueLocal,
    SharedParameter,
    ExclusiveParameter,
}

/// <summary>
/// Stable identity of one local classical-array allocation or one symbolic parameter region. Array SSA
/// values describe successive states; stores consume one state and produce another. A Phi can merge states
/// from several storages, so storage identity is intentionally not a mandatory field on
/// <see cref="MirValue"/>. In particular, distinct parameter storage identities are not sufficient evidence
/// that the caller supplied distinct physical arrays; consumers must interpret them through
/// <see cref="MirCallable.StorageAliasModeOf(MirArrayStorage)"/>. The defining parameter or
/// <see cref="MirArrayCreate"/> remains authoritative for kind, type, and definition position; these facts
/// are not copied onto the storage object.
/// </summary>
public sealed record MirArrayStorage(
    MirStorageId Id,
    string Name,
    MirOrigin Origin);

/// <summary>
/// One exact MIR version of a qubit binding. <see cref="Id"/> remains stable while
/// <see cref="Version"/> advances after a state-changing quantum instruction or control-flow Phi.
/// </summary>
public abstract record MirQubit
{
    internal MirQubit(
        MirQubitId id,
        MirQubitVersion version,
        MirOrigin origin)
    {
        Id = id;
        Version = version;
        Origin = origin;
    }

    public MirQubitId Id { get; }
    public MirQubitVersion Version { get; }
    public MirOrigin Origin { get; }
    public MirQubitKey Key => new(Id, Version);
}

/// <summary>The first MIR version of a qubit supplied through a callable parameter.</summary>
public sealed record MirQubitParameter : MirQubit, IMirParameter
{
    private MirQubitParameter(
        MirQubitId id,
        string name,
        bool isArray,
        int? length,
        QOwnershipMode ownership,
        MirOrigin origin)
        : base(id, new MirQubitVersion(0), origin)
    {
        Name = name;
        IsArray = isArray;
        Length = length;
        Ownership = ownership;
    }

    public string Name { get; }
    public bool IsArray { get; }
    public int? Length { get; }
    public QOwnershipMode Ownership { get; }

    internal static MirQubitParameter Single(
        MirQubitId id,
        string name,
        QOwnershipMode ownership,
        MirOrigin origin) =>
        new(id, name, isArray: false, length: null, ownership, origin);

    internal static MirQubitParameter Array(
        MirQubitId id,
        string name,
        int? length,
        QOwnershipMode ownership,
        MirOrigin origin)
    {
        if (length is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "a known qubit-array parameter length must be positive");
        }

        return new MirQubitParameter(
            id,
            name,
            isArray: true,
            length,
            ownership,
            origin);
    }
}

/// <summary>The first, clean MIR version of a local qubit binding created by a Qora use statement.</summary>
public sealed record MirQubitFromUse : MirQubit
{
    internal MirQubitFromUse(
        MirQubitId id,
        string name,
        int length,
        MirOrigin origin)
        : base(id, new MirQubitVersion(0), origin)
    {
        if (length < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "a local qubit-array length must be positive");
        }

        Name = name;
        Length = length;
    }

    public string Name { get; }
    public bool IsArray => true;
    public int Length { get; }
}

/// <summary>
/// A new version produced by the containing quantum instruction. The containing instruction is the
/// authoritative definition site, so the node does not duplicate a producer reference.
/// </summary>
public sealed record MirQubitAfterInstruction : MirQubit
{
    internal MirQubitAfterInstruction(
        MirQubitId id,
        MirQubitVersion version,
        MirOrigin origin)
        : base(id, version, origin)
    {
        if (version.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "an instruction-produced qubit version must be positive");
        }
    }
}

public readonly record struct MirControlFlowEdge
{
    internal MirControlFlowEdge(
        MirBlockId source,
        int successorOrdinal)
    {
        Source = source;
        SuccessorOrdinal = successorOrdinal;
    }

    public MirBlockId Source { get; }
    public int SuccessorOrdinal { get; }
}

public sealed record MirQubitPhiInput
{
    internal MirQubitPhiInput(
        MirControlFlowEdge edge,
        MirQubitKey qubit)
    {
        Edge = edge;
        Qubit = qubit;
    }

    public MirControlFlowEdge Edge { get; }
    public MirQubitKey Qubit { get; }
}

/// <summary>A version selected from the incoming CFG edge at a branch or loop join.</summary>
public sealed record MirQubitPhi : MirQubit
{
    private IReadOnlyList<MirQubitPhiInput> _inputs;

    internal MirQubitPhi(
        MirQubitId id,
        MirQubitVersion version,
        IReadOnlyList<MirQubitPhiInput> inputs,
        MirOrigin origin)
        : base(id, version, origin)
    {
        if (version.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "a qubit Phi version must be positive");
        }

        _inputs = MirCollections.Freeze(inputs);
    }

    public IReadOnlyList<MirQubitPhiInput> Inputs
    {
        get => _inputs;
        internal init => _inputs = MirCollections.Freeze(value);
    }
}

/// <summary>
/// A whole versioned qubit binding or one dynamically indexed element. Construction accepts a
/// <see cref="MirQubit"/>, while the immutable payload stores only its typed key.
/// </summary>
public readonly record struct MirQubitAccess
{
    internal MirQubitAccess(
        MirQubit qubit,
        MirValueId? index = null,
        MirOrigin? origin = null)
        : this(qubit.Key, index, origin ?? qubit.Origin)
    {
    }

    internal MirQubitAccess(
        MirQubitKey qubit,
        MirValueId? index,
        MirOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        Qubit = qubit;
        Index = index;
        Origin = origin;
    }

    public MirQubitKey Qubit { get; }
    public MirValueId? Index { get; }
    public MirOrigin Origin { get; }
}

/// <summary>A CFG block. Block arguments are SSA Phi results; each incoming edge supplies their values.</summary>
public sealed record MirBlock(
    MirBlockId Id,
    IReadOnlyList<MirValueId> Arguments,
    IReadOnlyList<MirInstruction> Instructions,
    MirTerminator Terminator,
    MirOrigin Origin,
    IReadOnlyList<MirQubitPhi>? QubitPhis = null)
{
    private IReadOnlyList<MirValueId> _arguments = MirCollections.Freeze(Arguments);
    private IReadOnlyList<MirInstruction> _instructions = MirCollections.Freeze(Instructions);
    private IReadOnlyList<MirQubitPhi> _qubitPhis =
        MirCollections.Freeze(QubitPhis ?? Array.Empty<MirQubitPhi>());

    public IReadOnlyList<MirValueId> Arguments
    {
        get => _arguments;
        init => _arguments = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirInstruction> Instructions
    {
        get => _instructions;
        init => _instructions = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirQubitPhi> QubitPhis
    {
        get => _qubitPhis;
        init => _qubitPhis = MirCollections.Freeze(value);
    }
}

public enum MirUnaryOperator
{
    Negate,
    LogicalNot,
}

public enum MirBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

public enum MirArrayInitialization
{
    ExplicitElements,
    ZeroInitialized,
}

public enum MirFunctor
{
    Adjoint,
    Controlled,
}

public abstract record MirCallTarget
{
    public abstract string DisplayName { get; }
}

public sealed record MirUserCallableTarget(
    MirCallableId Callable) : MirCallTarget
{
    public override string DisplayName => Callable.ToString();
}

public sealed record MirBuiltinGateTarget(
    string Name) : MirCallTarget
{
    public override string DisplayName => Name;
}

public sealed record MirBuiltinFunctionTarget(
    string Name) : MirCallTarget
{
    public override string DisplayName => Name;
}

/// <summary>
/// One positional call operand. Ownership and access remain explicit on the use; lowering has already
/// checked them against the callee contract.
/// </summary>
public abstract record MirCallOperand(
    QOwnershipMode Ownership,
    QAccessMode Access)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirQubitAccess> QubitAccesses { get; }
}

public sealed record MirClassicalCallOperand(
    MirValueId Value,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed,
    QAccessMode Access = QAccessMode.ReadOnly)
    : MirCallOperand(Ownership, Access)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Value };
    public override IReadOnlyList<MirQubitAccess> QubitAccesses => Array.Empty<MirQubitAccess>();
}

public sealed record MirQubitCallOperand : MirCallOperand
{
    internal MirQubitCallOperand(
        MirQubitAccess qubit,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly)
        : base(ownership, access)
    {
        Qubit = qubit;
    }

    public MirQubitAccess Qubit { get; }

    public override IReadOnlyList<MirValueId> InputValues =>
        Qubit.Index is MirValueId index ? new[] { index } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirQubitAccess> QubitAccesses => new[] { Qubit };
}

/// <summary>
/// A mutable borrowed array call produces a new SSA state for that exact positional operand.
/// Moved arrays are consumed and therefore do not produce a caller-visible state.
/// </summary>
public sealed record MirMutableArrayResult(
    int OperandIndex,
    MirValueId Result);

/// <summary>
/// Common instruction contract used by the verifier, def-use analysis, and future pass visitors.
/// Result order must match <see cref="MirValueDefinition.Index"/>.
/// </summary>
public abstract record MirInstruction(
    MirInstructionId Id,
    MirOrigin Origin)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirValueId> ResultValues { get; }
    public virtual IReadOnlyList<MirQubitAccess> QubitAccesses => Array.Empty<MirQubitAccess>();
}

public sealed record MirConstant(
    MirInstructionId Id,
    MirValueId Result,
    string Text,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirUnary(
    MirInstructionId Id,
    MirValueId Result,
    MirUnaryOperator Operator,
    MirValueId Operand,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Operand };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirBinary(
    MirInstructionId Id,
    MirValueId Result,
    MirBinaryOperator Operator,
    MirValueId Left,
    MirValueId Right,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Left, Right };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirConvert(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Operand,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Operand };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirArrayCreate(
    MirInstructionId Id,
    MirValueId Result,
    MirStorageId Storage,
    MirArrayInitialization Initialization,
    IReadOnlyList<MirValueId> Elements,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private IReadOnlyList<MirValueId> _elements = MirCollections.Freeze(Elements);

    public IReadOnlyList<MirValueId> Elements
    {
        get => _elements;
        init => _elements = MirCollections.Freeze(value);
    }

    public override IReadOnlyList<MirValueId> InputValues => Elements;
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirArrayLength(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Array,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Array };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirArrayLoad(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Array,
    MirValueId Index,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Array, Index };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirArrayStore(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Array,
    MirValueId Index,
    MirValueId Value,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Array, Index, Value };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirPureCall(
    MirInstructionId Id,
    MirValueId Result,
    MirCallTarget Target,
    IReadOnlyList<MirCallOperand> Operands,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private IReadOnlyList<MirCallOperand> _operands = MirCollections.Freeze(Operands);

    public IReadOnlyList<MirCallOperand> Operands
    {
        get => _operands;
        init => _operands = MirCollections.Freeze(value);
    }

    public override IReadOnlyList<MirValueId> InputValues =>
        Operands.SelectMany(operand => operand.InputValues).ToArray();

    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };

    public override IReadOnlyList<MirQubitAccess> QubitAccesses =>
        Operands.SelectMany(operand => operand.QubitAccesses).ToArray();
}

public sealed record MirQubitAllocate : MirInstruction
{
    internal MirQubitAllocate(
        MirInstructionId id,
        MirQubitFromUse result,
        MirOrigin origin)
        : base(id, origin)
    {
        Result = result;
    }

    public MirQubitFromUse Result { get; }

    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirValueId> ResultValues => Array.Empty<MirValueId>();
}

public sealed record MirQuantumApply : MirInstruction
{
    private readonly IReadOnlyList<MirCallOperand> _operands;
    private readonly IReadOnlyList<MirQubitAfterInstruction> _qubitResults;
    private readonly IReadOnlyList<MirMutableArrayResult> _mutableArrayResults;
    private readonly IReadOnlyList<MirFunctor> _functors;

    internal MirQuantumApply(
        MirInstructionId id,
        MirCallTarget target,
        IReadOnlyList<MirCallOperand> operands,
        IReadOnlyList<MirQubitAfterInstruction> qubitResults,
        IReadOnlyList<MirMutableArrayResult> mutableArrayResults,
        IReadOnlyList<MirFunctor> functors,
        MirOrigin origin)
        : base(id, origin)
    {
        Target = target;
        _operands = MirCollections.Freeze(operands);
        _qubitResults = MirCollections.Freeze(qubitResults);
        _mutableArrayResults = MirCollections.Freeze(mutableArrayResults);
        _functors = MirCollections.Freeze(functors);
    }

    public MirCallTarget Target { get; }
    public IReadOnlyList<MirCallOperand> Operands => _operands;
    public IReadOnlyList<MirQubitAfterInstruction> QubitResults => _qubitResults;
    public IReadOnlyList<MirMutableArrayResult> MutableArrayResults => _mutableArrayResults;
    public IReadOnlyList<MirFunctor> Functors => _functors;

    public override IReadOnlyList<MirValueId> InputValues =>
        Operands.SelectMany(operand => operand.InputValues).ToArray();

    public override IReadOnlyList<MirValueId> ResultValues =>
        MutableArrayResults.Select(result => result.Result).ToArray();

    public override IReadOnlyList<MirQubitAccess> QubitAccesses =>
        Operands.SelectMany(operand => operand.QubitAccesses).ToArray();
}

public sealed record MirMeasure : MirInstruction
{
    internal MirMeasure(
        MirInstructionId id,
        MirValueId result,
        MirQubitAccess qubit,
        MirQubitAfterInstruction qubitResult,
        MirOrigin origin)
        : base(id, origin)
    {
        Result = result;
        Qubit = qubit;
        QubitResult = qubitResult;
    }

    public MirValueId Result { get; }
    public MirQubitAccess Qubit { get; }
    public MirQubitAfterInstruction QubitResult { get; }

    public override IReadOnlyList<MirValueId> InputValues =>
        Qubit.Index is MirValueId index ? new[] { index } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
    public override IReadOnlyList<MirQubitAccess> QubitAccesses => new[] { Qubit };
}

/// <summary>Common terminator contract. Edge and return operands participate in normal SSA use checks.</summary>
public abstract record MirTerminator(
    MirOrigin Origin)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirBlockId> Successors { get; }
}

public sealed record MirJump(
    MirBlockId Target,
    IReadOnlyList<MirValueId> Arguments,
    MirOrigin Origin)
    : MirTerminator(Origin)
{
    private IReadOnlyList<MirValueId> _arguments = MirCollections.Freeze(Arguments);

    public IReadOnlyList<MirValueId> Arguments
    {
        get => _arguments;
        init => _arguments = MirCollections.Freeze(value);
    }

    public override IReadOnlyList<MirValueId> InputValues => Arguments;
    public override IReadOnlyList<MirBlockId> Successors => new[] { Target };
}

public sealed record MirBranch(
    MirValueId Condition,
    MirBlockId TrueTarget,
    IReadOnlyList<MirValueId> TrueArguments,
    MirBlockId FalseTarget,
    IReadOnlyList<MirValueId> FalseArguments,
    MirOrigin Origin)
    : MirTerminator(Origin)
{
    private IReadOnlyList<MirValueId> _trueArguments = MirCollections.Freeze(TrueArguments);
    private IReadOnlyList<MirValueId> _falseArguments = MirCollections.Freeze(FalseArguments);

    public IReadOnlyList<MirValueId> TrueArguments
    {
        get => _trueArguments;
        init => _trueArguments = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirValueId> FalseArguments
    {
        get => _falseArguments;
        init => _falseArguments = MirCollections.Freeze(value);
    }

    public override IReadOnlyList<MirValueId> InputValues =>
        new[] { Condition }.Concat(TrueArguments).Concat(FalseArguments).ToArray();

    public override IReadOnlyList<MirBlockId> Successors => new[] { TrueTarget, FalseTarget };
}

public sealed record MirReturn(
    MirValueId? Value,
    MirOrigin Origin)
    : MirTerminator(Origin)
{
    public override IReadOnlyList<MirValueId> InputValues =>
        Value is MirValueId value ? new[] { value } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirBlockId> Successors => Array.Empty<MirBlockId>();
}

public sealed record MirUnreachable(
    MirOrigin Origin)
    : MirTerminator(Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirBlockId> Successors => Array.Empty<MirBlockId>();
}
