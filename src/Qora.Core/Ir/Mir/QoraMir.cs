using System.Collections.Frozen;
using Qora.Compiler;

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
    private MirBlockId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"b{Value}";

    internal sealed class Allocator
    {
        private int _nextValue;

        public MirBlockId Allocate() =>
            new(_nextValue++);
    }
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
/// callable is always owned by <see cref="Callables"/>. Each parameter, block, instruction, SSA value,
/// storage, and qubit object belongs to exactly one callable, and every origin retains the same exact
/// HIR artifact. Construction stays inside the compiler pipeline so external callers cannot attach a
/// different program to an existing snapshot.
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

        HirArtifact = RequireSingleHirArtifact(entryPoint, Callables);
        EnsureExclusiveEntityOwnership(Callables);
        EntryPoint = entryPoint;
    }

    internal HirSemanticArtifact HirArtifact { get; }
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

    private static HirSemanticArtifact RequireSingleHirArtifact(
        MirCallable entryPoint,
        IReadOnlyList<MirCallable> callables)
    {
        var hirArtifact = entryPoint.Origin.SourceHirOrigin.HirArtifact;

        foreach (var callable in callables)
        {
            RequireSameHirArtifact(callable.Origin);

            foreach (var value in callable.Values)
                RequireSameHirArtifact(value.Origin);

            foreach (var storage in callable.Storages)
                RequireSameHirArtifact(storage.Origin);

            foreach (var qubit in callable.Qubits)
                RequireSameHirArtifact(qubit.Origin);

            foreach (var block in callable.Blocks)
            {
                RequireSameHirArtifact(block.Origin);
                RequireSameHirArtifact(block.Terminator.Origin);

                foreach (var instruction in block.Instructions)
                {
                    RequireSameHirArtifact(instruction.Origin);

                    foreach (var qubitAccess in instruction.QubitAccesses)
                        RequireSameHirArtifact(qubitAccess.Origin);
                }
            }
        }

        return hirArtifact;

        void RequireSameHirArtifact(MirOrigin origin)
        {
            if (!ReferenceEquals(origin.SourceHirOrigin.HirArtifact, hirArtifact))
            {
                throw new ArgumentException(
                    "A MIR program cannot combine origins from different HIR artifacts.",
                    nameof(callables));
            }
        }
    }

    private static void EnsureExclusiveEntityOwnership(
        IReadOnlyList<MirCallable> callables)
    {
        var owners = new Dictionary<object, MirCallableId>(
            ReferenceEqualityComparer.Instance);

        foreach (var callable in callables)
        {
            foreach (var parameter in callable.Parameters)
                Claim(parameter, "parameter", callable.Id);
            foreach (var block in callable.Blocks)
            {
                Claim(block, "block", callable.Id);
                foreach (var instruction in block.Instructions)
                    Claim(instruction, "instruction", callable.Id);
            }
            foreach (var value in callable.Values)
                Claim(value, "SSA value", callable.Id);
            foreach (var storage in callable.Storages)
                Claim(storage, "array storage", callable.Id);
            foreach (var qubit in callable.Qubits)
                Claim(qubit, "qubit version", callable.Id);
        }

        void Claim(
            object entity,
            string role,
            MirCallableId owner)
        {
            if (owners.TryAdd(entity, owner) || owners[entity] == owner)
                return;

            throw new ArgumentException(
                $"{role} object is owned by both {owners[entity]} and {owner}",
                nameof(callables));
        }
    }
}

/// <summary>
/// One lowered function or operation. Classical SSA values, array storages, and versioned qubits stay
/// at their semantic definition sites. The callable derives immutable lookup indexes from those owned
/// definitions instead of accepting parallel value or storage tables.
/// </summary>
public sealed class MirCallable
{
    private readonly FrozenDictionary<MirBlockId, MirBlock> _blocks;
    private readonly FrozenDictionary<
        MirInstructionId,
        (MirInstruction Instruction, MirBlock Block, int Index)> _instructions;
    private readonly FrozenDictionary<
        MirValueId,
        (MirValue Value, MirValueDefinition Definition)> _valueSites;
    private readonly FrozenDictionary<
        MirStorageId,
        (
            MirArrayStorage Storage,
            MirClassicalParameter? Parameter,
            int? ParameterIndex,
            MirArrayCreate? Allocation)> _storageSites;
    private readonly FrozenDictionary<MirQubitKey, MirQubit> _qubits;

    internal MirCallable(
        MirCallableId id,
        string name,
        MirType? returnType,
        IReadOnlyList<IMirParameter> parameters,
        MirBlock entryBlock,
        IReadOnlyList<MirBlock> blocks,
        MirOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(entryBlock);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(origin);

        Id = id;
        Name = name;
        ReturnType = returnType;
        Parameters = MirCollections.Freeze(parameters);
        ValidateParameters(Parameters, returnType);
        Blocks = MirCollections.Freeze(blocks);
        _blocks = IndexUnique(Blocks, block => block.Id, nameof(blocks), "block");

        if (!_blocks.TryGetValue(entryBlock.Id, out var ownedEntryBlock)
            || !ReferenceEquals(ownedEntryBlock, entryBlock))
        {
            throw new ArgumentException(
                $"entry block {entryBlock.Id} must be the exact object owned by this MIR callable",
                nameof(entryBlock));
        }

        EntryBlock = entryBlock;
        _instructions = IndexInstructions(Blocks);
        (Values, _valueSites) = CollectValues(Parameters, Blocks);
        ValidateControlFlow();
        (Storages, _storageSites) = CollectStorages(Parameters, Blocks);
        Qubits = CollectQubits(Parameters, Blocks);
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

    public MirBlock RequireBlock(MirBlockId id) =>
        _blocks.TryGetValue(id, out var block)
            ? block
            : throw Missing(nameof(id), id, "block");

    public bool ContainsInstruction(MirInstructionId id) =>
        _instructions.ContainsKey(id);

    public MirInstruction RequireInstruction(MirInstructionId id) =>
        _instructions.TryGetValue(id, out var location)
            ? location.Instruction
            : throw Missing(nameof(id), id, "instruction");

    public (MirBlock Block, int Index) RequireInstructionLocation(
        MirInstructionId id) =>
        _instructions.TryGetValue(id, out var location)
            ? (location.Block, location.Index)
            : throw Missing(nameof(id), id, "instruction");

    public bool ContainsValue(MirValueId id) =>
        _valueSites.ContainsKey(id);

    public bool ContainsValue(MirValue? value) =>
        value is not null
        && _valueSites.TryGetValue(value.Id, out var site)
        && ReferenceEquals(site.Value, value);

    public MirValue RequireValue(MirValueId id) =>
        _valueSites.TryGetValue(id, out var site)
            ? site.Value
            : throw Missing(nameof(id), id, "SSA value");

    public MirValueDefinition DefinitionOf(MirValueId value) =>
        _valueSites.TryGetValue(value, out var site)
            ? site.Definition
            : throw Missing(nameof(value), value, "SSA value");

    public bool ContainsStorage(MirStorageId id) =>
        _storageSites.ContainsKey(id);

    public bool ContainsStorage(MirArrayStorage? storage) =>
        storage is not null
        && _storageSites.TryGetValue(storage.Id, out var site)
        && ReferenceEquals(site.Storage, storage);

    public MirArrayStorage RequireStorage(MirStorageId id) =>
        _storageSites.TryGetValue(id, out var site)
            ? site.Storage
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
    public MirArrayStorageKind StorageKindOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        return _storageSites[storage.Id].Parameter is not null
            ? MirArrayStorageKind.Parameter
            : MirArrayStorageKind.Local;
    }

    /// <summary>
    /// Returns the authoritative type of a storage region from the SSA value which defines it.
    /// Parameter storage is typed by its parameter value; local storage is typed by its
    /// <see cref="MirArrayCreate"/> result.
    /// </summary>
    public MirType StorageTypeOf(MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        return StorageKindOf(storage) switch
        {
            MirArrayStorageKind.Parameter =>
                ParameterDefining(storage).Value.Type,
            MirArrayStorageKind.Local =>
                AllocationDefining(storage).Result.Type,
            _ => throw MalformedStorage(
                storage,
                "has an unknown derived storage kind"),
        };
    }

    /// <summary>
    /// Derives the storage aliasing contract from its owner. Local allocations are unique.
    /// Parameter regions are shared only when the parameter is borrowed and read-only.
    /// </summary>
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
        return ParameterStorageDefinition(storage).ParameterIndex;
    }

    internal MirInstructionId StorageAllocationInstructionOf(
        MirArrayStorage storage)
    {
        storage = RequireStorage(storage);
        return AllocationDefining(storage).Id;
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
        _valueSites.TryGetValue(id, out var site) ? site.Value : null;

    internal MirArrayStorage? FindStorage(MirStorageId id) =>
        _storageSites.TryGetValue(id, out var site) ? site.Storage : null;

    private MirClassicalParameter ParameterDefining(MirArrayStorage storage) =>
        ParameterStorageDefinition(storage).Parameter;

    private (MirClassicalParameter Parameter, int ParameterIndex) ParameterStorageDefinition(
            MirArrayStorage storage)
    {
        var definition = _storageSites[storage.Id];
        if (definition.Parameter is null || definition.ParameterIndex is not int parameterIndex)
        {
            throw MalformedStorage(
                storage,
                "does not identify its defining array parameter");
        }

        return (definition.Parameter, parameterIndex);
    }

    private MirArrayCreate AllocationDefining(MirArrayStorage storage)
    {
        var definition = _storageSites[storage.Id];
        if (definition.Allocation is not { } allocation)
        {
            throw MalformedStorage(
                storage,
                "does not identify its defining array-create instruction");
        }

        return allocation;
    }

    private InvalidOperationException MalformedStorage(
        MirArrayStorage storage,
        string detail) =>
        new(
            $"QINTERNAL: array storage {storage.Id} in MIR callable {Id} {detail}");

    private static (
        IReadOnlyList<MirValue> Values,
        FrozenDictionary<MirValueId, (MirValue Value, MirValueDefinition Definition)> Sites) CollectValues(
        IReadOnlyList<IMirParameter> parameters,
        IReadOnlyList<MirBlock> blocks)
    {
        var values = new List<MirValue>();
        var sites = new Dictionary<MirValueId, (MirValue Value, MirValueDefinition Definition)>();

        void Add(MirValue value, MirValueDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!sites.TryAdd(value.Id, (value, definition)))
            {
                throw new ArgumentException(
                    $"SSA value identity {value.Id} is defined more than once",
                    nameof(blocks));
            }
            values.Add(value);
        }

        for (var parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
        {
            if (parameters[parameterIndex] is MirClassicalParameter parameter)
                Add(parameter.Value, MirValueDefinition.ParameterAt(parameterIndex));
        }

        foreach (var block in blocks)
        {
            for (var argumentIndex = 0; argumentIndex < block.Arguments.Count; argumentIndex++)
            {
                Add(
                    block.Arguments[argumentIndex],
                    MirValueDefinition.BlockArgumentAt(block.Id, argumentIndex));
            }

            foreach (var instruction in block.Instructions)
            {
                var results = instruction.Results;
                for (var resultIndex = 0; resultIndex < results.Count; resultIndex++)
                {
                    Add(
                        results[resultIndex],
                        MirValueDefinition.InstructionResultAt(instruction.Id, resultIndex));
                }
            }
        }

        return (
            MirCollections.Freeze(values.OrderBy(value => value.Id.Value)),
            sites.ToFrozenDictionary());
    }

    private static (
        IReadOnlyList<MirArrayStorage> Storages,
        FrozenDictionary<
            MirStorageId,
            (
                MirArrayStorage Storage,
                MirClassicalParameter? Parameter,
                int? ParameterIndex,
                MirArrayCreate? Allocation)> Sites) CollectStorages(
        IReadOnlyList<IMirParameter> parameters,
        IReadOnlyList<MirBlock> blocks)
    {
        var storages = new List<MirArrayStorage>();
        var sites = new Dictionary<
            MirStorageId,
            (
                MirArrayStorage Storage,
                MirClassicalParameter? Parameter,
                int? ParameterIndex,
                MirArrayCreate? Allocation)>();

        for (var parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
        {
            if (parameters[parameterIndex] is not MirClassicalParameter
                {
                    Storage: { } storage,
                } parameter)
            {
                continue;
            }

            if (!sites.TryAdd(
                    storage.Id,
                    (storage, parameter, parameterIndex, Allocation: null)))
            {
                throw new ArgumentException(
                    $"array storage identity {storage.Id} is defined more than once",
                    nameof(parameters));
            }
            storages.Add(storage);
        }

        foreach (var block in blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not MirArrayCreate allocation)
                    continue;

                var storage = allocation.Storage;
                ArgumentNullException.ThrowIfNull(storage);
                if (!sites.TryAdd(
                        storage.Id,
                        (storage, Parameter: null, ParameterIndex: null, allocation)))
                {
                    throw new ArgumentException(
                        $"array storage identity {storage.Id} is defined more than once",
                        nameof(blocks));
                }
                storages.Add(storage);
            }
        }

        return (
            MirCollections.Freeze(storages.OrderBy(storage => storage.Id.Value)),
            sites.ToFrozenDictionary());
    }

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
                qubits.AddRange(instruction.QubitResults);
        }

        return MirCollections.Freeze(qubits);
    }

    private void ValidateControlFlow()
    {
        var entryPredecessors = new HashSet<MirBlockId>();

        foreach (var sourceBlock in Blocks)
        {
            foreach (var successor in sourceBlock.Terminator.Successors)
            {
                if (!_blocks.ContainsKey(successor))
                {
                    throw new ArgumentException(
                        $"terminator in block {sourceBlock.Id} targets block {successor}, "
                        + $"which does not belong to MIR callable {Id}");
                }

                if (successor == EntryBlock.Id)
                    entryPredecessors.Add(sourceBlock.Id);
            }

            switch (sourceBlock.Terminator)
            {
                case MirJump jump:
                    ValidateControlFlowEdge(
                        sourceBlock,
                        jump.Target,
                        jump.Arguments,
                        "jump");
                    break;

                case MirBranch branch:
                    ValidateControlFlowEdge(
                        sourceBlock,
                        branch.TrueTarget,
                        branch.TrueArguments,
                        "true edge");
                    ValidateControlFlowEdge(
                        sourceBlock,
                        branch.FalseTarget,
                        branch.FalseArguments,
                        "false edge");
                    break;
            }
        }

        if (entryPredecessors.Count != 0)
        {
            throw new ArgumentException(
                $"entry block {EntryBlock.Id} has predecessor(s) "
                + $"[{string.Join(", ", entryPredecessors.OrderBy(id => id.Value))}]; "
                + "a callable entry block must be a unique CFG root");
        }
    }

    private void ValidateControlFlowEdge(
        MirBlock sourceBlock,
        MirBlockId targetBlockId,
        IReadOnlyList<MirValueId> arguments,
        string edgeName)
    {
        var targetBlock = _blocks[targetBlockId];
        if (arguments.Count != targetBlock.Arguments.Count)
        {
            throw new ArgumentException(
                $"{edgeName} from {sourceBlock.Id} supplies {arguments.Count} value(s), "
                + $"but {targetBlockId} expects {targetBlock.Arguments.Count}");
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var argumentId = arguments[index];
            if (!_valueSites.TryGetValue(argumentId, out var argumentSite))
            {
                throw new ArgumentException(
                    $"{edgeName} argument {index} from {sourceBlock.Id} references SSA value "
                    + $"{argumentId}, which does not belong to MIR callable {Id}");
            }

            var targetArgument = targetBlock.Arguments[index];
            if (argumentSite.Value.Type != targetArgument.Type)
            {
                throw new ArgumentException(
                    $"{edgeName} argument {index} from {sourceBlock.Id} is "
                    + $"{argumentSite.Value.Type}, but {targetBlockId} expects {targetArgument.Type}");
            }
        }
    }

    private static void ValidateParameters(
        IReadOnlyList<IMirParameter> parameters,
        MirType? returnType)
    {
        foreach (var parameter in parameters)
        {
            switch (parameter)
            {
                case MirClassicalParameter classical:
                    if (returnType is not null
                        && (classical.Ownership != QOwnershipMode.Borrowed
                            || classical.Access != QAccessMode.ReadOnly))
                    {
                        throw new ArgumentException(
                            $"function parameter `{classical.Name}` must be borrowed and read-only",
                            nameof(parameters));
                    }
                    break;

                case MirQubitParameter qubit when returnType is not null:
                    throw new ArgumentException(
                        $"function parameter `{qubit.Name}` cannot be a qubit",
                        nameof(parameters));

                case MirQubitParameter:
                    break;

                default:
                    throw new ArgumentException(
                        $"unsupported MIR parameter type {parameter.GetType().Name}",
                        nameof(parameters));
            }
        }
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

public sealed record MirClassicalParameter : IMirParameter
{
    private MirClassicalParameter(
        string name,
        MirValue value,
        MirArrayStorage? storage,
        QOwnershipMode ownership,
        QAccessMode access,
        int minimumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Type.IsArray != (storage is not null))
        {
            throw new ArgumentException(
                "an array parameter must own storage and a scalar parameter cannot own storage",
                nameof(storage));
        }
        if (minimumLength < 0
            || (!value.Type.IsArray && minimumLength != 0)
            || (value.Type.KnownLength is int knownLength && minimumLength > knownLength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumLength),
                minimumLength,
                "the parameter minimum length is incompatible with its MIR type");
        }
        if (!Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership), ownership, "unknown ownership mode");
        if (!Enum.IsDefined(access))
            throw new ArgumentOutOfRangeException(nameof(access), access, "unknown access mode");

        Name = name;
        Value = value;
        Storage = storage;
        Ownership = ownership;
        Access = access;
        MinimumLength = minimumLength;
    }

    public string Name { get; }
    public MirValue Value { get; }
    public MirArrayStorage? Storage { get; }
    public QOwnershipMode Ownership { get; }
    public QAccessMode Access { get; }
    public int MinimumLength { get; }

    internal static MirClassicalParameter Scalar(
        string name,
        MirValue value,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly) =>
        new(name, value, storage: null, ownership, access, minimumLength: 0);

    internal static MirClassicalParameter Array(
        string name,
        MirValue value,
        MirArrayStorage storage,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly,
        int minimumLength = 0) =>
        new(name, value, storage, ownership, access, minimumLength);
}

public enum MirValueDefinitionKind
{
    Parameter,
    BlockArgument,
    InstructionResult,
}

/// <summary>
/// The definition position derived by a <see cref="MirCallable"/> from the site which owns an SSA
/// value. It is query metadata, not a second independently supplied definition table.
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
        int resultIndex) =>
        new(resultIndex, block: null, instruction);
}

public sealed record MirValue
{
    internal MirValue(
        MirValueId id,
        MirType type,
        MirOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        Id = id;
        Type = type;
        Origin = origin;
    }

    public MirValueId Id { get; }
    public MirType Type { get; }
    public MirOrigin Origin { get; }
}

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
public sealed record MirArrayStorage
{
    internal MirArrayStorage(
        MirStorageId id,
        string name,
        MirOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(origin);
        Id = id;
        Name = name;
        Origin = origin;
    }

    public MirStorageId Id { get; }
    public string Name { get; }
    public MirOrigin Origin { get; }
}

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
        ArgumentNullException.ThrowIfNull(origin);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership), ownership, "unknown ownership mode");

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
        ArgumentOutOfRangeException.ThrowIfNegative(successorOrdinal);
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
        MirQubit qubit)
    {
        ArgumentNullException.ThrowIfNull(qubit);
        Edge = edge;
        Qubit = qubit.Key;
    }

    public MirControlFlowEdge Edge { get; }
    public MirQubitKey Qubit { get; }
}

/// <summary>A version selected from the incoming CFG edge at a branch or loop join.</summary>
public sealed record MirQubitPhi : MirQubit
{
    private IReadOnlyList<MirQubitPhiInput> _inputs;

    internal MirQubitPhi(
        MirQubitVersion version,
        IReadOnlyList<MirQubitPhiInput> inputs,
        MirOrigin origin)
        : base(QubitIdOf(inputs), version, origin)
    {
        if (version.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "a qubit Phi version must be positive");
        }

        _inputs = FreezeInputs(inputs);
    }

    public IReadOnlyList<MirQubitPhiInput> Inputs
    {
        get => _inputs;
        internal init => _inputs = FreezeInputs(value);
    }

    private static MirQubitId QubitIdOf(IReadOnlyList<MirQubitPhiInput> inputs) =>
        RequireFirstInput(inputs).Qubit.Id;

    private static MirQubitPhiInput RequireFirstInput(
        IReadOnlyList<MirQubitPhiInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentException(
                "a qubit Phi requires at least one incoming value",
                nameof(inputs));
        }

        return inputs[0];
    }

    private IReadOnlyList<MirQubitPhiInput> FreezeInputs(
        IReadOnlyList<MirQubitPhiInput> inputs)
    {
        var frozen = MirCollections.Freeze(inputs);
        _ = RequireFirstInput(frozen);
        var edges = new HashSet<MirControlFlowEdge>();
        foreach (var input in frozen)
        {
            if (!edges.Add(input.Edge))
            {
                throw new ArgumentException(
                    $"qubit Phi {Id} contains duplicate input edge {input.Edge}",
                    nameof(inputs));
            }
            if (input.Qubit.Id != Id)
            {
                throw new ArgumentException(
                    $"qubit Phi {Id} receives different identity {input.Qubit}",
                    nameof(inputs));
            }
        }
        return frozen;
    }
}

/// <summary>
/// A whole versioned qubit binding or one dynamically indexed element. Construction accepts a
/// <see cref="MirQubit"/>, while the immutable payload stores only its typed key.
/// </summary>
public sealed record MirQubitAccess
{
    internal MirQubitAccess(
        MirQubit qubit,
        MirValueId? index = null,
        MirOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(qubit);
        Qubit = qubit.Key;
        Index = index;
        Origin = origin ?? qubit.Origin;
    }

    public MirQubitKey Qubit { get; }
    public MirValueId? Index { get; }
    public MirOrigin Origin { get; }
}

/// <summary>A CFG block. Block arguments are SSA Phi results; each incoming edge supplies their values.</summary>
public sealed record MirBlock(
    MirBlockId Id,
    IReadOnlyList<MirValue> Arguments,
    IReadOnlyList<MirInstruction> Instructions,
    MirTerminator Terminator,
    MirOrigin Origin,
    IReadOnlyList<MirQubitPhi>? QubitPhis = null)
{
    private IReadOnlyList<MirValue> _arguments = MirCollections.Freeze(Arguments);
    private IReadOnlyList<MirInstruction> _instructions = MirCollections.Freeze(Instructions);
    private MirTerminator _terminator =
        Terminator ?? throw new ArgumentNullException(nameof(Terminator));
    private MirOrigin _origin = Origin ?? throw new ArgumentNullException(nameof(Origin));
    private IReadOnlyList<MirQubitPhi> _qubitPhis =
        FreezeQubitPhis(QubitPhis ?? Array.Empty<MirQubitPhi>());

    public IReadOnlyList<MirValue> Arguments
    {
        get => _arguments;
        init => _arguments = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirInstruction> Instructions
    {
        get => _instructions;
        init => _instructions = MirCollections.Freeze(value);
    }

    public MirTerminator Terminator
    {
        get => _terminator;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _terminator = value;
        }
    }

    public MirOrigin Origin
    {
        get => _origin;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _origin = value;
        }
    }

    public IReadOnlyList<MirQubitPhi> QubitPhis
    {
        get => _qubitPhis;
        init => _qubitPhis = FreezeQubitPhis(value);
    }

    private static IReadOnlyList<MirQubitPhi> FreezeQubitPhis(
        IReadOnlyList<MirQubitPhi> phis)
    {
        var frozen = MirCollections.Freeze(phis);
        var ids = new HashSet<MirQubitId>();
        foreach (var phi in frozen)
        {
            if (!ids.Add(phi.Id))
            {
                throw new ArgumentException(
                    $"a block cannot define more than one current-state Phi for qubit {phi.Id}",
                    nameof(phis));
            }
        }
        return frozen;
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
    internal MirCallTarget()
    {
    }

    public abstract string DisplayName { get; }
}

public sealed record MirUserCallableTarget : MirCallTarget
{
    internal MirUserCallableTarget(MirCallableId callable)
    {
        Callable = callable;
    }

    public MirCallableId Callable { get; }
    public override string DisplayName => Callable.ToString();
}

public sealed record MirBuiltinGateTarget : MirCallTarget
{
    internal MirBuiltinGateTarget(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!QoraGates.Gates.ContainsKey(name))
            throw new ArgumentException($"unknown built-in gate `{name}`", nameof(name));
        Name = name;
    }

    public string Name { get; }
    public override string DisplayName => Name;
}

public sealed record MirBuiltinFunctionTarget : MirCallTarget
{
    internal MirBuiltinFunctionTarget(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!QoraGates.Functions.ContainsKey(name))
            throw new ArgumentException($"unknown built-in function `{name}`", nameof(name));
        Name = name;
    }

    public string Name { get; }
    public override string DisplayName => Name;
}

/// <summary>
/// One positional call operand. Ownership and access remain explicit on the use; lowering has already
/// checked them against the callee contract.
/// </summary>
public abstract record MirCallOperand
{
    internal MirCallOperand(
        QOwnershipMode ownership,
        QAccessMode access)
    {
        if (!Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership), ownership, "unknown ownership mode");
        if (!Enum.IsDefined(access))
            throw new ArgumentOutOfRangeException(nameof(access), access, "unknown access mode");

        Ownership = ownership;
        Access = access;
    }

    public QOwnershipMode Ownership { get; }
    public QAccessMode Access { get; }
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirQubitAccess> QubitAccesses { get; }
}

public sealed record MirClassicalCallOperand : MirCallOperand
{
    internal MirClassicalCallOperand(
        MirValueId value,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly)
        : base(ownership, access)
    {
        Value = value;
    }

    public MirValueId Value { get; }
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
        ArgumentNullException.ThrowIfNull(qubit);
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
public sealed record MirMutableArrayResult
{
    internal MirMutableArrayResult(
        int operandIndex,
        MirValue result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operandIndex);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Type.IsArray)
        {
            throw new ArgumentException(
                "a mutable array result must have array type",
                nameof(result));
        }

        OperandIndex = operandIndex;
        Result = result;
    }

    public int OperandIndex { get; }
    public MirValue Result { get; }
}

/// <summary>
/// Common instruction contract used by the verifier, def-use analysis, and pass visitors. Each
/// instruction owns the SSA values it defines; ordinary operands continue to reference values by ID.
/// </summary>
public abstract record MirInstruction(
    MirInstructionId Id,
    MirOrigin Origin)
{
    private MirOrigin _origin = Origin ?? throw new ArgumentNullException(nameof(Origin));

    public MirOrigin Origin
    {
        get => _origin;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _origin = value;
        }
    }

    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirValue> Results { get; }
    public virtual IReadOnlyList<MirQubitAccess> QubitAccesses => Array.Empty<MirQubitAccess>();
    public virtual IReadOnlyList<MirQubit> QubitResults => Array.Empty<MirQubit>();

    protected static MirValue RequireScalarResult(MirValue result, string role)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Type.IsArray)
            throw new ArgumentException($"{role} result must be scalar, not {result.Type}", nameof(result));
        return result;
    }

    protected static MirValue RequireArrayResult(MirValue result, string role)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Type.IsArray)
            throw new ArgumentException($"{role} result must be an array, not {result.Type}", nameof(result));
        return result;
    }

    protected static MirValue RequireResultType(
        MirValue result,
        MirType expected,
        string role)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Type != expected)
        {
            throw new ArgumentException(
                $"{role} result must be {expected}, not {result.Type}",
                nameof(result));
        }
        return result;
    }
}

public sealed record MirConstant(
    MirInstructionId Id,
    MirValue Result,
    string Text,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireScalarResult(Result, "constant");

    public MirValue Result => _result;

    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirValue> Results => new[] { Result };
}

public sealed record MirUnary(
    MirInstructionId Id,
    MirValue Result,
    MirUnaryOperator Operator,
    MirValueId Operand,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireUnaryResult(Result, Operator);
    private readonly MirUnaryOperator _operator = RequireOperator(Operator);

    public MirValue Result => _result;
    public MirUnaryOperator Operator => _operator;

    public override IReadOnlyList<MirValueId> InputValues => new[] { Operand };
    public override IReadOnlyList<MirValue> Results => new[] { Result };

    private static MirUnaryOperator RequireOperator(MirUnaryOperator value)
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "unknown MIR unary operator");
        return value;
    }

    private static MirValue RequireUnaryResult(
        MirValue result,
        MirUnaryOperator unaryOperator)
    {
        RequireScalarResult(result, "unary instruction");
        if (unaryOperator == MirUnaryOperator.LogicalNot
            && result.Type != MirType.Scalar(QType.Bit))
        {
            throw new ArgumentException(
                $"logical-not result must be bit, not {result.Type}",
                nameof(result));
        }
        return result;
    }
}

public sealed record MirBinary(
    MirInstructionId Id,
    MirValue Result,
    MirBinaryOperator Operator,
    MirValueId Left,
    MirValueId Right,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireBinaryResult(Result, Operator);
    private readonly MirBinaryOperator _operator = RequireOperator(Operator);

    public MirValue Result => _result;
    public MirBinaryOperator Operator => _operator;

    public override IReadOnlyList<MirValueId> InputValues => new[] { Left, Right };
    public override IReadOnlyList<MirValue> Results => new[] { Result };

    private static MirBinaryOperator RequireOperator(MirBinaryOperator value)
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "unknown MIR binary operator");
        return value;
    }

    private static MirValue RequireBinaryResult(
        MirValue result,
        MirBinaryOperator binaryOperator)
    {
        RequireScalarResult(result, "binary instruction");
        if (binaryOperator is MirBinaryOperator.Equal
                or MirBinaryOperator.NotEqual
                or MirBinaryOperator.Less
                or MirBinaryOperator.LessOrEqual
                or MirBinaryOperator.Greater
                or MirBinaryOperator.GreaterOrEqual
            && result.Type != MirType.Scalar(QType.Bit))
        {
            throw new ArgumentException(
                $"{binaryOperator} result must be bit, not {result.Type}",
                nameof(result));
        }
        return result;
    }
}

public sealed record MirConvert(
    MirInstructionId Id,
    MirValue Result,
    MirValueId Operand,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireScalarResult(Result, "conversion");

    public MirValue Result => _result;

    public override IReadOnlyList<MirValueId> InputValues => new[] { Operand };
    public override IReadOnlyList<MirValue> Results => new[] { Result };
}

public sealed record MirArrayCreate : MirInstruction
{
    private MirValue _result;
    private MirArrayStorage _storage;
    private readonly IReadOnlyList<MirValueId> _elements;

    internal MirArrayCreate(
        MirInstructionId id,
        MirValue result,
        MirArrayStorage storage,
        MirArrayInitialization initialization,
        IReadOnlyList<MirValueId> elements,
        MirOrigin origin)
        : base(id, origin)
    {
        if (!Enum.IsDefined(initialization))
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialization),
                initialization,
                "unknown MIR array initialization");
        }
        var length = RequireKnownArrayLength(result);
        ArgumentNullException.ThrowIfNull(storage);

        var frozenElements = MirCollections.Freeze(elements);
        ValidateElementCount(initialization, frozenElements, length);

        _result = result;
        _storage = storage;
        Initialization = initialization;
        _elements = frozenElements;
    }

    public MirValue Result
    {
        get => _result;
        internal init
        {
            var length = RequireKnownArrayLength(value);
            ValidateElementCount(Initialization, Elements, length);
            _result = value;
        }
    }

    public MirArrayStorage Storage
    {
        get => _storage;
        internal init
        {
            ArgumentNullException.ThrowIfNull(value);
            _storage = value;
        }
    }

    public MirArrayInitialization Initialization { get; }

    public IReadOnlyList<MirValueId> Elements => _elements;

    public override IReadOnlyList<MirValueId> InputValues => Elements;
    public override IReadOnlyList<MirValue> Results => new[] { Result };

    private static int RequireKnownArrayLength(MirValue result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Type.IsArray || result.Type.KnownLength is not int length)
        {
            throw new ArgumentException(
                $"array creation result must have a known-length array type, not {result.Type}",
                nameof(result));
        }
        return length;
    }

    private static void ValidateElementCount(
        MirArrayInitialization initialization,
        IReadOnlyList<MirValueId> elements,
        int length)
    {
        if (initialization == MirArrayInitialization.ExplicitElements
            && elements.Count != length)
        {
            throw new ArgumentException(
                $"explicit array creation has {elements.Count} element(s), expected {length}",
                nameof(elements));
        }
        if (initialization == MirArrayInitialization.ZeroInitialized
            && elements.Count != 0)
        {
            throw new ArgumentException(
                "zero-initialized array creation cannot carry explicit elements",
                nameof(elements));
        }
    }
}

public sealed record MirArrayLength(
    MirInstructionId Id,
    MirValue Result,
    MirValueId Array,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireResultType(
        Result,
        MirType.Scalar(QType.Int),
        "array-length");

    public MirValue Result => _result;

    public override IReadOnlyList<MirValueId> InputValues => new[] { Array };
    public override IReadOnlyList<MirValue> Results => new[] { Result };
}

public sealed record MirArrayLoad(
    MirInstructionId Id,
    MirValue Result,
    MirValueId Array,
    MirValueId Index,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireScalarResult(Result, "array-load");

    public MirValue Result => _result;

    public override IReadOnlyList<MirValueId> InputValues => new[] { Array, Index };
    public override IReadOnlyList<MirValue> Results => new[] { Result };
}

public sealed record MirArrayStore(
    MirInstructionId Id,
    MirValue Result,
    MirValueId Array,
    MirValueId Index,
    MirValueId Value,
    MirOrigin Origin)
    : MirInstruction(Id, Origin)
{
    private readonly MirValue _result = RequireArrayResult(Result, "array-store");

    public MirValue Result => _result;

    public override IReadOnlyList<MirValueId> InputValues => new[] { Array, Index, Value };
    public override IReadOnlyList<MirValue> Results => new[] { Result };
}

public sealed record MirPureCall : MirInstruction
{
    private readonly IReadOnlyList<MirCallOperand> _operands;

    internal MirPureCall(
        MirInstructionId id,
        MirValue result,
        MirCallTarget target,
        IReadOnlyList<MirCallOperand> operands,
        MirOrigin origin)
        : base(id, origin)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(target);
        if (target is not (MirUserCallableTarget or MirBuiltinFunctionTarget))
        {
            throw new ArgumentException(
                $"pure call cannot use target `{target.DisplayName}`",
                nameof(target));
        }

        var frozenOperands = MirCollections.Freeze(operands);
        if (frozenOperands.Any(operand => operand is not MirClassicalCallOperand))
        {
            throw new ArgumentException(
                "a pure call can carry only classical operands",
                nameof(operands));
        }
        if (target is MirBuiltinFunctionTarget builtin)
        {
            var function = QoraGates.Functions[builtin.Name];
            if (frozenOperands.Count != 1)
            {
                throw new ArgumentException(
                    $"built-in function `{builtin.Name}` expects one operand",
                    nameof(operands));
            }
            var expectedResultType = MirType.Scalar(function.Returns);
            if (result.Type != expectedResultType)
            {
                throw new ArgumentException(
                    $"built-in function `{builtin.Name}` result must be {expectedResultType}, not {result.Type}",
                    nameof(result));
            }
        }

        Result = result;
        Target = target;
        _operands = frozenOperands;
    }

    public MirValue Result { get; }
    public MirCallTarget Target { get; }

    public IReadOnlyList<MirCallOperand> Operands => _operands;

    public override IReadOnlyList<MirValueId> InputValues =>
        Operands.SelectMany(operand => operand.InputValues).ToArray();

    public override IReadOnlyList<MirValue> Results => new[] { Result };

    public override IReadOnlyList<MirQubitAccess> QubitAccesses =>
        Array.Empty<MirQubitAccess>();
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
    public override IReadOnlyList<MirValue> Results => Array.Empty<MirValue>();
    public override IReadOnlyList<MirQubit> QubitResults => new[] { Result };
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
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        _operands = MirCollections.Freeze(operands);
        _qubitResults = MirCollections.Freeze(qubitResults);
        _mutableArrayResults = MirCollections.Freeze(mutableArrayResults);
        _functors = MirCollections.Freeze(functors);
        ValidateFunctors(_functors);
        ValidateTargetAndOperands(target, _operands, _functors);
        ValidateMutableArrayResults(target, _operands, _mutableArrayResults);
        ValidateQubitResults(_operands, _qubitResults);
    }

    public MirCallTarget Target { get; }
    public IReadOnlyList<MirCallOperand> Operands => _operands;
    public override IReadOnlyList<MirQubitAfterInstruction> QubitResults => _qubitResults;
    public IReadOnlyList<MirMutableArrayResult> MutableArrayResults => _mutableArrayResults;
    public IReadOnlyList<MirFunctor> Functors => _functors;

    public override IReadOnlyList<MirValueId> InputValues =>
        Operands.SelectMany(operand => operand.InputValues).ToArray();

    public override IReadOnlyList<MirValue> Results =>
        MutableArrayResults.Select(result => result.Result).ToArray();

    public override IReadOnlyList<MirQubitAccess> QubitAccesses =>
        Operands.SelectMany(operand => operand.QubitAccesses).ToArray();

    private static void ValidateFunctors(IReadOnlyList<MirFunctor> functors)
    {
        var seen = new HashSet<MirFunctor>();
        for (var index = 0; index < functors.Count; index++)
        {
            var functor = functors[index];
            if (!Enum.IsDefined(functor))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(functors),
                    functor,
                    "unknown MIR functor");
            }
            if (!seen.Add(functor)
                || functor == MirFunctor.Adjoint && index != 0)
            {
                throw new ArgumentException(
                    "each MIR functor may appear once and Adjoint must precede Controlled",
                    nameof(functors));
            }
        }
    }

    private static void ValidateTargetAndOperands(
        MirCallTarget target,
        IReadOnlyList<MirCallOperand> operands,
        IReadOnlyList<MirFunctor> functors)
    {
        if (target is MirUserCallableTarget)
            return;
        if (target is not MirBuiltinGateTarget builtin)
        {
            throw new ArgumentException(
                $"quantum apply cannot use target `{target.DisplayName}`",
                nameof(target));
        }

        var gate = QoraGates.Gates[builtin.Name];
        if (functors.Count != 0 && !gate.Unitary)
        {
            throw new ArgumentException(
                $"non-unitary built-in gate `{builtin.Name}` cannot carry MIR functors",
                nameof(functors));
        }

        var extraControls = functors.Count(functor => functor == MirFunctor.Controlled);
        var signature = QoraGates.SigOf(builtin.Name, extraControls)!;
        if (operands.Count != signature.Parameters.Count)
        {
            throw new ArgumentException(
                $"built-in gate `{builtin.Name}` has {operands.Count} operand(s), expected {signature.Parameters.Count}",
                nameof(operands));
        }
        for (var index = 0; index < operands.Count; index++)
        {
            var matchesExpectedKind = signature.Parameters[index].Type == QType.Qubit
                ? operands[index] is MirQubitCallOperand
                : operands[index] is MirClassicalCallOperand;
            if (!matchesExpectedKind)
            {
                throw new ArgumentException(
                    $"built-in gate `{builtin.Name}` operand {index} has the wrong MIR operand kind",
                    nameof(operands));
            }
        }
    }

    private static void ValidateMutableArrayResults(
        MirCallTarget target,
        IReadOnlyList<MirCallOperand> operands,
        IReadOnlyList<MirMutableArrayResult> results)
    {
        if (target is not MirUserCallableTarget && results.Count != 0)
        {
            throw new ArgumentException(
                "only a user callable can produce mutable array states",
                nameof(results));
        }

        var operandIndexes = new HashSet<int>();
        foreach (var result in results)
        {
            if (!operandIndexes.Add(result.OperandIndex))
            {
                throw new ArgumentException(
                    $"mutable array operand {result.OperandIndex} has more than one result",
                    nameof(results));
            }
            if (result.OperandIndex >= operands.Count)
            {
                throw new ArgumentException(
                    $"mutable array result names missing operand {result.OperandIndex}",
                    nameof(results));
            }
            if (operands[result.OperandIndex] is not MirClassicalCallOperand
                {
                    Ownership: QOwnershipMode.Borrowed,
                    Access: QAccessMode.Mutable,
                })
            {
                throw new ArgumentException(
                    $"mutable array result {result.Result.Id} does not correspond to a borrowed mutable classical operand",
                    nameof(results));
            }
        }
    }

    private static void ValidateQubitResults(
        IReadOnlyList<MirCallOperand> operands,
        IReadOnlyList<MirQubitAfterInstruction> results)
    {
        var inputIds = operands
            .OfType<MirQubitCallOperand>()
            .Select(operand => operand.Qubit.Qubit.Id)
            .ToHashSet();
        var resultIds = new HashSet<MirQubitId>();
        foreach (var result in results)
        {
            if (!resultIds.Add(result.Id))
            {
                throw new ArgumentException(
                    $"quantum apply produces more than one version of qubit {result.Id}",
                    nameof(results));
            }
            if (!inputIds.Contains(result.Id))
            {
                throw new ArgumentException(
                    $"quantum apply produces {result.Key} without reading the same qubit identity",
                    nameof(results));
            }
        }
    }
}

public sealed record MirMeasure : MirInstruction
{
    internal MirMeasure(
        MirInstructionId id,
        MirValue result,
        MirQubitAccess qubit,
        MirQubitAfterInstruction qubitResult,
        MirOrigin origin)
        : base(id, origin)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(qubit);
        ArgumentNullException.ThrowIfNull(qubitResult);
        if (result.Type != MirType.Scalar(QType.Bit))
        {
            throw new ArgumentException(
                $"measurement result must be bit, not {result.Type}",
                nameof(result));
        }
        if (qubitResult.Id != qubit.Qubit.Id)
        {
            throw new ArgumentException(
                $"measurement reads {qubit.Qubit} but produces {qubitResult.Key}",
                nameof(qubitResult));
        }

        Result = result;
        Qubit = qubit;
        QubitResult = qubitResult;
    }

    public MirValue Result { get; }
    public MirQubitAccess Qubit { get; }
    public MirQubitAfterInstruction QubitResult { get; }

    public override IReadOnlyList<MirValueId> InputValues =>
        Qubit.Index is MirValueId index ? new[] { index } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirValue> Results => new[] { Result };
    public override IReadOnlyList<MirQubitAccess> QubitAccesses => new[] { Qubit };
    public override IReadOnlyList<MirQubit> QubitResults => new[] { QubitResult };
}

/// <summary>Common terminator contract. Edge and return operands participate in normal SSA use checks.</summary>
public abstract record MirTerminator(
    MirOrigin Origin)
{
    private MirOrigin _origin = Origin ?? throw new ArgumentNullException(nameof(Origin));

    public MirOrigin Origin
    {
        get => _origin;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _origin = value;
        }
    }

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
