namespace Qora.Ir.Mir;

// The MIR owns its own dense callable-local identities. References that cross the immutable program
// boundary always carry the exact MirSnapshotId. Every entity retains a snapshot-qualified origin for
// diagnostics and derivation; no MIR pass resolves a value, block, or resource by source spelling.
public readonly record struct MirCallableId(int Value)
{
    public override string ToString() => $"c{Value}";
}

public readonly record struct MirBlockId(int Value)
{
    public override string ToString() => $"b{Value}";
}

public readonly record struct MirInstructionId(int Value)
{
    public override string ToString() => $"i{Value}";
}

public readonly record struct MirValueId(int Value)
{
    public override string ToString() => $"v{Value}";
}

public readonly record struct MirQubitId
{
    internal MirQubitId(int value) => Value = value;

    public int Value { get; }

    public override string ToString() => $"q{Value}";
}

public readonly record struct MirQubitVersion
{
    internal MirQubitVersion(int value) => Value = value;

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

public readonly record struct MirStorageId(int Value)
{
    public override string ToString() => $"s{Value}";
}

/// <summary>
/// A classical MIR type. Qubits deliberately do not inhabit this type system; they are versioned
/// <see cref="MirQubit"/> definitions addressed through <see cref="MirQubitAccess"/>.
/// </summary>
public readonly record struct MirType(
    QType ElementType,
    bool IsArray = false,
    int? KnownLength = null)
{
    public static MirType Scalar(QType type) => new(type);
    public static MirType Array(QType elementType, int? knownLength = null) =>
        new(elementType, IsArray: true, KnownLength: knownLength);

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
/// The immutable SSA/CFG payload of one exact MIR snapshot. Construction stays inside the compiler
/// pipeline so external callers cannot attach a different program to an existing snapshot identity.
/// </summary>
public sealed class MirProgram
{
    internal MirProgram(
        MirSnapshotId snapshotId,
        MirOriginTable origins,
        MirCallableId entryPoint,
        IEnumerable<MirCallable> callables)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(callables);
        if (origins.SnapshotId != snapshotId)
            throw new ArgumentException(
                "the origin table belongs to a different MIR snapshot",
                nameof(origins));

        SnapshotId = snapshotId;
        Origins = origins;
        Callables = MirCollections.Freeze(callables);
        EntryPoint = entryPoint;
    }

    public MirSnapshotId SnapshotId { get; }
    public MirOriginTable Origins { get; }
    public MirCallableId EntryPoint { get; }
    public IReadOnlyList<MirCallable> Callables { get; }

    internal int Revision => SnapshotId.Revision;

    internal MirCallable? FindCallable(MirCallableId id) =>
        Callables.FirstOrDefault(callable => callable.Id == id);
}

/// <summary>
/// One lowered function or operation. Classical SSA values and versioned qubits use separate identity
/// spaces. Classical values use <see cref="Values"/> as their definition table. Qubit definitions stay
/// at their semantic definition sites (parameters, use instructions, quantum instructions, and block
/// Phis); <see cref="Qubits"/> is the immutable index derived from those sites.
/// </summary>
public sealed class MirCallable
{
    internal MirCallable(
        MirCallableId id,
        string name,
        MirCallableKind kind,
        MirType? returnType,
        IReadOnlyList<IMirParameter> parameters,
        MirBlockId entryBlock,
        IReadOnlyList<MirBlock> blocks,
        IReadOnlyList<MirValue> values,
        IReadOnlyList<MirArrayStorage> storages,
        MirOriginRef origin)
    {
        Id = id;
        Name = name;
        Kind = kind;
        ReturnType = returnType;
        Parameters = MirCollections.Freeze(parameters);
        EntryBlock = entryBlock;
        Blocks = MirCollections.Freeze(blocks);
        Values = MirCollections.Freeze(values);
        Storages = MirCollections.Freeze(storages);
        Qubits = CollectQubits(Parameters, Blocks);
        Origin = origin;
    }

    public MirCallableId Id { get; }
    public string Name { get; }
    public MirCallableKind Kind { get; }
    public MirType? ReturnType { get; }
    public IReadOnlyList<IMirParameter> Parameters { get; }
    public MirBlockId EntryBlock { get; }
    public IReadOnlyList<MirBlock> Blocks { get; }
    public IReadOnlyList<MirValue> Values { get; }
    public IReadOnlyList<MirArrayStorage> Storages { get; }
    public IReadOnlyList<MirQubit> Qubits { get; }
    public MirOriginRef Origin { get; }

    internal MirBlock? FindBlock(MirBlockId id) =>
        Blocks.FirstOrDefault(block => block.Id == id);

    internal MirValue? FindValue(MirValueId id) =>
        Values.FirstOrDefault(value => value.Id == id);

    internal MirArrayStorage? FindStorage(MirStorageId id) =>
        Storages.FirstOrDefault(storage => storage.Id == id);

    internal MirQubit? FindQubit(MirQubitKey key) =>
        Qubits.FirstOrDefault(qubit => qubit.Key == key);

    internal MirQubit? FindInitialQubit(MirQubitId id) =>
        Qubits.FirstOrDefault(qubit => qubit.Id == id && qubit.Version.Value == 0);

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
}

// Parameters remain ordered because call operands are positional.
public interface IMirParameter
{
    string Name { get; }
    MirOriginRef Origin { get; }
}

public sealed record MirClassicalParameter(
    string Name,
    MirOriginRef Origin,
    MirValueId Value,
    MirType Type,
    MirStorageId? Storage = null,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed,
    QAccessMode Access = QAccessMode.ReadOnly)
    : IMirParameter;

public enum MirValueDefinitionKind
{
    Parameter,
    BlockArgument,
    InstructionResult,
}

/// <summary>
/// The one definition position of an SSA value. <see cref="Index"/> is the parameter index, block
/// argument index, or result index according to <see cref="Kind"/>.
/// </summary>
public sealed record MirValueDefinition(
    MirValueDefinitionKind Kind,
    int Index,
    MirBlockId? Block = null,
    MirInstructionId? Instruction = null)
{
    public static MirValueDefinition ParameterAt(int parameterIndex) =>
        new(MirValueDefinitionKind.Parameter, parameterIndex);

    public static MirValueDefinition BlockArgumentAt(MirBlockId block, int argumentIndex) =>
        new(MirValueDefinitionKind.BlockArgument, argumentIndex, Block: block);

    public static MirValueDefinition InstructionResultAt(
        MirBlockId block,
        MirInstructionId instruction,
        int resultIndex = 0) =>
        new(MirValueDefinitionKind.InstructionResult, resultIndex, Block: block, Instruction: instruction);
}

public sealed record MirValue(
    MirValueId Id,
    MirType Type,
    MirValueDefinition Definition,
    MirOriginRef Origin);

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
/// <see cref="AliasMode"/>.
/// </summary>
public sealed record MirArrayStorage(
    MirStorageId Id,
    string Name,
    MirArrayStorageKind Kind,
    MirStorageAliasMode AliasMode,
    MirType Type,
    int? ParameterIndex,
    MirInstructionId? AllocationInstruction,
    MirOriginRef Origin);

/// <summary>
/// One exact MIR version of a qubit binding. <see cref="Id"/> remains stable while
/// <see cref="Version"/> advances after a state-changing quantum instruction or control-flow Phi.
/// </summary>
public abstract record MirQubit
{
    internal MirQubit(
        MirQubitId id,
        MirQubitVersion version,
        MirOriginRef origin)
    {
        Id = id;
        Version = version;
        Origin = origin;
    }

    public MirQubitId Id { get; }
    public MirQubitVersion Version { get; }
    public MirOriginRef Origin { get; }
    public MirQubitKey Key => new(Id, Version);
}

/// <summary>The first MIR version of a qubit supplied through a callable parameter.</summary>
public sealed record MirQubitParameter : MirQubit, IMirParameter
{
    internal MirQubitParameter(
        MirQubitId id,
        string name,
        bool isArray,
        int? length,
        QOwnershipMode ownership,
        MirOriginRef origin)
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
}

/// <summary>The first, clean MIR version of a local qubit binding created by a Qora use statement.</summary>
public sealed record MirQubitFromUse : MirQubit
{
    internal MirQubitFromUse(
        MirQubitId id,
        string name,
        int length,
        MirOriginRef origin)
        : base(id, new MirQubitVersion(0), origin)
    {
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
        MirOriginRef origin)
        : base(id, version, origin)
    {
    }
}

public readonly record struct MirControlFlowEdge
{
    internal MirControlFlowEdge(
        MirBlockId source,
        int successorOrdinal,
        MirBlockId target)
    {
        Source = source;
        SuccessorOrdinal = successorOrdinal;
        Target = target;
    }

    public MirBlockId Source { get; }
    public int SuccessorOrdinal { get; }
    public MirBlockId Target { get; }
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
        MirBlockId block,
        IReadOnlyList<MirQubitPhiInput> inputs,
        MirOriginRef origin)
        : base(id, version, origin)
    {
        Block = block;
        _inputs = MirCollections.Freeze(inputs);
    }

    public MirBlockId Block { get; }
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
    internal MirQubitAccess(MirQubit qubit, MirValueId? index = null)
        : this(qubit.Key, index)
    {
    }

    internal MirQubitAccess(MirQubitKey qubit, MirValueId? index = null)
    {
        Qubit = qubit;
        Index = index;
    }

    public MirQubitKey Qubit { get; }
    public MirValueId? Index { get; }
}

/// <summary>A CFG block. Block arguments are SSA Phi results; each incoming edge supplies their values.</summary>
public sealed record MirBlock(
    MirBlockId Id,
    IReadOnlyList<MirBlockArgument> Arguments,
    IReadOnlyList<MirInstruction> Instructions,
    MirTerminator Terminator,
    MirOriginRef Origin,
    IReadOnlyList<MirQubitPhi>? QubitPhis = null)
{
    private IReadOnlyList<MirBlockArgument> _arguments = MirCollections.Freeze(Arguments);
    private IReadOnlyList<MirInstruction> _instructions = MirCollections.Freeze(Instructions);
    private IReadOnlyList<MirQubitPhi> _qubitPhis =
        MirCollections.Freeze(QubitPhis ?? Array.Empty<MirQubitPhi>());

    public IReadOnlyList<MirBlockArgument> Arguments
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

public sealed record MirBlockArgument(
    MirValueId Value,
    MirType Type);

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

/// <summary>
/// Typed constant payload. Text is retained exactly/canonically so real and named constants do not lose
/// precision through an intermediate host-language floating-point conversion.
/// </summary>
public sealed record MirConstantValue(
    QType Type,
    string Text);

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
    MirOriginRef Origin)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirValueId> ResultValues { get; }
    public virtual IReadOnlyList<MirQubitAccess> QubitAccesses => Array.Empty<MirQubitAccess>();
}

public sealed record MirConstant(
    MirInstructionId Id,
    MirValueId Result,
    MirConstantValue Constant,
    MirOriginRef Origin)
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
    MirOriginRef Origin)
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
    MirOriginRef Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Left, Right };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirConvert(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Operand,
    MirType TargetType,
    MirOriginRef Origin)
    : MirInstruction(Id, Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Operand };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirArrayCreate(
    MirInstructionId Id,
    MirValueId Result,
    MirStorageId Storage,
    QType ElementType,
    MirArrayInitialization Initialization,
    int Length,
    IReadOnlyList<MirValueId> Elements,
    MirOriginRef Origin)
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
    MirOriginRef Origin)
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
    MirOriginRef Origin)
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
    MirOriginRef Origin)
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
    MirOriginRef Origin)
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
        MirOriginRef origin)
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
        MirOriginRef origin)
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
        MirOriginRef origin)
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
    MirOriginRef Origin)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirBlockId> Successors { get; }
}

public sealed record MirJump(
    MirBlockId Target,
    IReadOnlyList<MirValueId> Arguments,
    MirOriginRef Origin)
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
    MirOriginRef Origin)
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
    MirOriginRef Origin)
    : MirTerminator(Origin)
{
    public override IReadOnlyList<MirValueId> InputValues =>
        Value is MirValueId value ? new[] { value } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirBlockId> Successors => Array.Empty<MirBlockId>();
}

public sealed record MirUnreachable(
    MirOriginRef Origin)
    : MirTerminator(Origin)
{
    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirBlockId> Successors => Array.Empty<MirBlockId>();
}
