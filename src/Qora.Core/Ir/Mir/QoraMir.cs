using Qora.Ir.Passes;

namespace Qora.Ir.Mir;

// The MIR owns its own dense identities. HIR QNode/QStmt identities are retained only in MirSource
// for diagnostics and derivation; no MIR pass resolves a value, block, or resource by source spelling.
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

public readonly record struct MirQubitResourceId(int Value)
{
    public override string ToString() => $"q{Value}";
}

public readonly record struct MirStorageId(int Value)
{
    public override string ToString() => $"s{Value}";
}

/// <summary>
/// The HIR origin of one MIR fact. A synthesized fact keeps the operation identity while
/// <see cref="SourceStatementId"/> and <see cref="Span"/> may be null.
/// </summary>
public sealed record MirSource(
    int SourceOperationId,
    int? SourceStatementId = null,
    QSpan? Span = null)
{
    public static MirSource Synthetic(int sourceOperationId) => new(sourceOperationId);
}

/// <summary>
/// A classical MIR type. Qubits deliberately do not inhabit this type system; they are linear
/// <see cref="MirQubitResource"/>s addressed through <see cref="MirQubitPlace"/>.
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
/// An immutable SSA/CFG program snapshot. A transformation returns a new program with a greater
/// <see cref="Revision"/>; analysis results are valid only for the exact revision they were built from.
/// </summary>
public sealed record MirProgram(
    int Revision,
    IReadOnlyList<MirCallable> Callables)
{
    private IReadOnlyList<MirCallable> _callables = MirCollections.Freeze(Callables);

    public IReadOnlyList<MirCallable> Callables
    {
        get => _callables;
        init => _callables = MirCollections.Freeze(value);
    }

    public MirCallable? FindCallable(MirCallableId id) =>
        Callables.FirstOrDefault(callable => callable.Id == id);
}

/// <summary>
/// One lowered function or operation. Classical SSA values and qubit resources use separate identity
/// spaces. <see cref="Values"/> is the authoritative type/definition table; instructions and block
/// arguments merely reference it.
/// </summary>
public sealed record MirCallable(
    MirCallableId Id,
    int SourceOperationId,
    string Name,
    MirCallableKind Kind,
    MirType? ReturnType,
    IReadOnlyList<MirParameter> Parameters,
    MirBlockId EntryBlock,
    IReadOnlyList<MirBlock> Blocks,
    IReadOnlyList<MirValue> Values,
    IReadOnlyList<MirArrayStorage> Storages,
    IReadOnlyList<MirQubitResource> Qubits,
    MirSource Source)
{
    private IReadOnlyList<MirParameter> _parameters = MirCollections.Freeze(Parameters);
    private IReadOnlyList<MirBlock> _blocks = MirCollections.Freeze(Blocks);
    private IReadOnlyList<MirValue> _values = MirCollections.Freeze(Values);
    private IReadOnlyList<MirArrayStorage> _storages = MirCollections.Freeze(Storages);
    private IReadOnlyList<MirQubitResource> _qubits = MirCollections.Freeze(Qubits);

    public IReadOnlyList<MirParameter> Parameters
    {
        get => _parameters;
        init => _parameters = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirBlock> Blocks
    {
        get => _blocks;
        init => _blocks = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirValue> Values
    {
        get => _values;
        init => _values = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirArrayStorage> Storages
    {
        get => _storages;
        init => _storages = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirQubitResource> Qubits
    {
        get => _qubits;
        init => _qubits = MirCollections.Freeze(value);
    }

    public MirBlock? FindBlock(MirBlockId id) =>
        Blocks.FirstOrDefault(block => block.Id == id);

    public MirValue? FindValue(MirValueId id) =>
        Values.FirstOrDefault(value => value.Id == id);

    public MirArrayStorage? FindStorage(MirStorageId id) =>
        Storages.FirstOrDefault(storage => storage.Id == id);

    public MirQubitResource? FindQubit(MirQubitResourceId id) =>
        Qubits.FirstOrDefault(resource => resource.Id == id);
}

// Parameters remain ordered because call operands are positional.
public abstract record MirParameter(
    string Name,
    SymbolId? SourceSymbolId);

public sealed record MirClassicalParameter(
    string Name,
    SymbolId? SourceSymbolId,
    MirValueId Value,
    MirType Type,
    MirStorageId? Storage = null,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed,
    QAccessMode Access = QAccessMode.ReadOnly)
    : MirParameter(Name, SourceSymbolId);

public sealed record MirQubitParameter(
    string Name,
    SymbolId? SourceSymbolId,
    MirQubitResourceId Resource,
    bool IsArray,
    int? Length,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed)
    : MirParameter(Name, SourceSymbolId);

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
    SymbolId? SourceSymbolId = null,
    MirSource? Source = null);

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
    SymbolId? SourceSymbolId,
    int? ParameterIndex,
    MirInstructionId? AllocationInstruction,
    MirSource Source);

public enum MirQubitResourceKind
{
    Parameter,
    Local,
}

/// <summary>
/// A physical qubit binding. Gate application changes its state but not its resource identity; the
/// derived qubit-effect graph creates state-version nodes later.
/// </summary>
public sealed record MirQubitResource(
    MirQubitResourceId Id,
    string Name,
    MirQubitResourceKind Kind,
    bool IsArray,
    int? Length,
    SymbolId? SourceSymbolId,
    MirInstructionId? AllocationInstruction,
    MirSource Source);

/// <summary>
/// A whole qubit resource or one dynamically indexed element. Null index means the whole binding;
/// the resource metadata distinguishes one scalar qubit from a whole register.
/// </summary>
public readonly record struct MirQubitPlace(
    MirQubitResourceId Resource,
    MirValueId? Index = null);

/// <summary>A CFG block. Block arguments are SSA Phi results; each incoming edge supplies their values.</summary>
public sealed record MirBlock(
    MirBlockId Id,
    IReadOnlyList<MirBlockArgument> Arguments,
    IReadOnlyList<MirInstruction> Instructions,
    MirTerminator Terminator,
    MirSource Source)
{
    private IReadOnlyList<MirBlockArgument> _arguments = MirCollections.Freeze(Arguments);
    private IReadOnlyList<MirInstruction> _instructions = MirCollections.Freeze(Instructions);

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
}

public sealed record MirBlockArgument(
    MirValueId Value,
    MirType Type,
    SymbolId? SourceSymbolId = null);

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
    public abstract IReadOnlyList<MirQubitPlace> QubitPlaces { get; }
}

public sealed record MirClassicalCallOperand(
    MirValueId Value,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed,
    QAccessMode Access = QAccessMode.ReadOnly)
    : MirCallOperand(Ownership, Access)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Value };
    public override IReadOnlyList<MirQubitPlace> QubitPlaces => Array.Empty<MirQubitPlace>();
}

public sealed record MirQubitCallOperand(
    MirQubitPlace Place,
    QOwnershipMode Ownership = QOwnershipMode.Borrowed,
    QAccessMode Access = QAccessMode.ReadOnly)
    : MirCallOperand(Ownership, Access)
{
    public override IReadOnlyList<MirValueId> InputValues =>
        Place.Index is MirValueId index ? new[] { index } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirQubitPlace> QubitPlaces => new[] { Place };
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
    MirSource Source)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirValueId> ResultValues { get; }
    public virtual IReadOnlyList<MirQubitPlace> QubitPlaces => Array.Empty<MirQubitPlace>();
}

public sealed record MirConstant(
    MirInstructionId Id,
    MirValueId Result,
    MirConstantValue Constant,
    MirSource Source)
    : MirInstruction(Id, Source)
{
    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirUnary(
    MirInstructionId Id,
    MirValueId Result,
    MirUnaryOperator Operator,
    MirValueId Operand,
    MirSource Source)
    : MirInstruction(Id, Source)
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
    MirSource Source)
    : MirInstruction(Id, Source)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Left, Right };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirConvert(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Operand,
    MirType TargetType,
    MirSource Source)
    : MirInstruction(Id, Source)
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
    MirSource Source)
    : MirInstruction(Id, Source)
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
    MirSource Source)
    : MirInstruction(Id, Source)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Array };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirArrayLoad(
    MirInstructionId Id,
    MirValueId Result,
    MirValueId Array,
    MirValueId Index,
    MirSource Source)
    : MirInstruction(Id, Source)
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
    MirSource Source)
    : MirInstruction(Id, Source)
{
    public override IReadOnlyList<MirValueId> InputValues => new[] { Array, Index, Value };
    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
}

public sealed record MirPureCall(
    MirInstructionId Id,
    MirValueId Result,
    MirCallTarget Target,
    IReadOnlyList<MirCallOperand> Operands,
    MirSource Source)
    : MirInstruction(Id, Source)
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

    public override IReadOnlyList<MirQubitPlace> QubitPlaces =>
        Operands.SelectMany(operand => operand.QubitPlaces).ToArray();
}

public sealed record MirQubitAllocate(
    MirInstructionId Id,
    MirQubitResourceId Resource,
    MirSource Source)
    : MirInstruction(Id, Source)
{
    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirValueId> ResultValues => Array.Empty<MirValueId>();
}

public sealed record MirQuantumApply(
    MirInstructionId Id,
    MirCallTarget Target,
    IReadOnlyList<MirCallOperand> Operands,
    IReadOnlyList<MirMutableArrayResult> MutableArrayResults,
    IReadOnlyList<MirFunctor> Functors,
    MirSource Source)
    : MirInstruction(Id, Source)
{
    private IReadOnlyList<MirCallOperand> _operands = MirCollections.Freeze(Operands);
    private IReadOnlyList<MirMutableArrayResult> _mutableArrayResults =
        MirCollections.Freeze(MutableArrayResults);
    private IReadOnlyList<MirFunctor> _functors = MirCollections.Freeze(Functors);

    public IReadOnlyList<MirCallOperand> Operands
    {
        get => _operands;
        init => _operands = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirMutableArrayResult> MutableArrayResults
    {
        get => _mutableArrayResults;
        init => _mutableArrayResults = MirCollections.Freeze(value);
    }

    public IReadOnlyList<MirFunctor> Functors
    {
        get => _functors;
        init => _functors = MirCollections.Freeze(value);
    }

    public override IReadOnlyList<MirValueId> InputValues =>
        Operands.SelectMany(operand => operand.InputValues).ToArray();

    public override IReadOnlyList<MirValueId> ResultValues =>
        MutableArrayResults.Select(result => result.Result).ToArray();

    public override IReadOnlyList<MirQubitPlace> QubitPlaces =>
        Operands.SelectMany(operand => operand.QubitPlaces).ToArray();
}

public sealed record MirMeasure(
    MirInstructionId Id,
    MirValueId Result,
    MirQubitPlace Place,
    MirSource Source)
    : MirInstruction(Id, Source)
{
    public override IReadOnlyList<MirValueId> InputValues =>
        Place.Index is MirValueId index ? new[] { index } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirValueId> ResultValues => new[] { Result };
    public override IReadOnlyList<MirQubitPlace> QubitPlaces => new[] { Place };
}

/// <summary>Common terminator contract. Edge and return operands participate in normal SSA use checks.</summary>
public abstract record MirTerminator(
    MirSource Source)
{
    public abstract IReadOnlyList<MirValueId> InputValues { get; }
    public abstract IReadOnlyList<MirBlockId> Successors { get; }
}

public sealed record MirJump(
    MirBlockId Target,
    IReadOnlyList<MirValueId> Arguments,
    MirSource Source)
    : MirTerminator(Source)
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
    MirSource Source)
    : MirTerminator(Source)
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
    MirSource Source)
    : MirTerminator(Source)
{
    public override IReadOnlyList<MirValueId> InputValues =>
        Value is MirValueId value ? new[] { value } : Array.Empty<MirValueId>();

    public override IReadOnlyList<MirBlockId> Successors => Array.Empty<MirBlockId>();
}

public sealed record MirUnreachable(
    MirSource Source)
    : MirTerminator(Source)
{
    public override IReadOnlyList<MirValueId> InputValues => Array.Empty<MirValueId>();
    public override IReadOnlyList<MirBlockId> Successors => Array.Empty<MirBlockId>();
}
