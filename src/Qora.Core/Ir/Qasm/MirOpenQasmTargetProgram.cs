using System.Collections.Immutable;

namespace Qora.Ir;

/// <summary>
/// Target-owned callable identity. It is intentionally unrelated to HIR node IDs and MIR callable IDs:
/// the MIR-to-target boundary will create the mapping, while every reference inside this model uses only
/// this identity.
/// </summary>
public readonly record struct MirQasmCallableId
{
    public MirQasmCallableId(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "a target callable ID cannot be negative");
        Value = value;
    }

    public int Value { get; }
    public override string ToString() => $"oqc{Value}";
}

/// <summary>Target-owned parameter identity, local to one callable or entry body.</summary>
public readonly record struct MirQasmParameterId
{
    public MirQasmParameterId(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "a target parameter ID cannot be negative");
        Value = value;
    }

    public int Value { get; }
    public override string ToString() => $"oqp{Value}";
}

/// <summary>Target-owned local/global declaration identity, local to one callable or entry body.</summary>
public readonly record struct MirQasmDeclarationId
{
    public MirQasmDeclarationId(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "a target declaration ID cannot be negative");
        Value = value;
    }

    public int Value { get; }
    public override string ToString() => $"oqd{Value}";
}

public enum MirQasmScalarKind
{
    Int,
    UInt,
    Float,
    Angle,
    Bool,
}

/// <summary>
/// A final OpenQASM type. This is target data rather than a copied Qora type: the emitter never consults
/// HIR/MIR to decide whether a declaration is scalar, a register, a qubit binding, or a general array.
/// </summary>
public abstract record MirQasmType;

public sealed record MirQasmScalarType : MirQasmType
{
    public MirQasmScalarType(MirQasmScalarKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown OpenQASM scalar kind");
        Kind = kind;
    }

    public MirQasmScalarKind Kind { get; }
}

/// <summary>One scalar bit or a fixed-width OpenQASM bit register.</summary>
public sealed record MirQasmBitType : MirQasmType
{
    public MirQasmBitType(int width = 1)
        : this(width, isRegister: width > 1)
    {
    }

    public MirQasmBitType(
        int width,
        bool isRegister)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "a bit width must be positive");
        if (!isRegister && width != 1)
            throw new ArgumentException(
                "a scalar bit has width one; wider bit types must be registers",
                nameof(width));
        Width = width;
        IsRegister = isRegister;
    }

    public int Width { get; }
    public bool IsRegister { get; }
}

/// <summary>One qubit or a fixed-width OpenQASM qubit register.</summary>
public sealed record MirQasmQubitType : MirQasmType
{
    public MirQasmQubitType(int count = 1)
        : this(count, isRegister: count > 1)
    {
    }

    public MirQasmQubitType(
        int count,
        bool isRegister)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "a qubit count must be positive");
        if (!isRegister && count != 1)
            throw new ArgumentException(
                "a scalar qubit has count one; wider qubit types must be registers",
                nameof(count));
        Count = count;
        IsRegister = isRegister;
    }

    public int Count { get; }
    public bool IsRegister { get; }
}

/// <summary>
/// A one-dimensional general classical array. A declaration requires a concrete <see cref="Length"/>;
/// an array-reference parameter may leave it null and emits <c>#dim = 1</c>.
/// </summary>
public sealed record MirQasmArrayType : MirQasmType
{
    public MirQasmArrayType(
        MirQasmScalarType elementType,
        int? length = null)
    {
        ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        if (length is <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "an array length must be positive");
        Length = length;
    }

    public MirQasmScalarType ElementType { get; }
    public int? Length { get; }
}

public enum MirQasmParameterAccess
{
    Value,
    ReadOnly,
    Mutable,
}

public sealed record MirQasmParameter
{
    public MirQasmParameter(
        MirQasmParameterId id,
        string emittedName,
        MirQasmType type,
        MirQasmParameterAccess access = MirQasmParameterAccess.Value)
    {
        Id = id;
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));
        Type = type ?? throw new ArgumentNullException(nameof(type));
        if (!Enum.IsDefined(access))
            throw new ArgumentOutOfRangeException(nameof(access), access, "unknown parameter access");
        if (type is MirQasmArrayType && access == MirQasmParameterAccess.Value)
            throw new ArgumentException(
                "a general OpenQASM array parameter requires readonly or mutable access",
                nameof(access));
        if (type is not MirQasmArrayType && access != MirQasmParameterAccess.Value)
            throw new ArgumentException(
                "readonly/mutable access applies only to general array parameters",
                nameof(access));
        Access = access;
    }

    public MirQasmParameterId Id { get; }
    public string EmittedName { get; }
    public MirQasmType Type { get; }
    public MirQasmParameterAccess Access { get; }
}

public abstract record MirQasmExpression;

/// <summary>A fully legalized OpenQASM literal token such as <c>1</c>, <c>pi</c>, or <c>"001"</c>.</summary>
public sealed record MirQasmLiteralExpression : MirQasmExpression
{
    public MirQasmLiteralExpression(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("a target literal cannot be blank", nameof(text));
        if (text.Contains('\r') || text.Contains('\n') || text.Contains(';'))
            throw new ArgumentException("a target literal must be one expression token", nameof(text));
        Text = text;
    }

    public string Text { get; }
}

public sealed record MirQasmParameterReferenceExpression(
    MirQasmParameterId Parameter) : MirQasmExpression;

public sealed record MirQasmDeclarationReferenceExpression(
    MirQasmDeclarationId Declaration) : MirQasmExpression;

public enum MirQasmUnaryOperator
{
    Negate,
    LogicalNot,
}

public sealed record MirQasmUnaryExpression : MirQasmExpression
{
    public MirQasmUnaryExpression(
        MirQasmUnaryOperator @operator,
        MirQasmExpression operand)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "unknown unary operator");
        Operator = @operator;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    public MirQasmUnaryOperator Operator { get; }
    public MirQasmExpression Operand { get; }
}

public enum MirQasmBinaryOperator
{
    LogicalOr,
    LogicalAnd,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
}

public sealed record MirQasmBinaryExpression : MirQasmExpression
{
    public MirQasmBinaryExpression(
        MirQasmBinaryOperator @operator,
        MirQasmExpression left,
        MirQasmExpression right)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "unknown binary operator");
        Operator = @operator;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }

    public MirQasmBinaryOperator Operator { get; }
    public MirQasmExpression Left { get; }
    public MirQasmExpression Right { get; }
}

public sealed record MirQasmIndexExpression : MirQasmExpression
{
    public MirQasmIndexExpression(
        MirQasmExpression @base,
        MirQasmExpression index)
    {
        Base = @base ?? throw new ArgumentNullException(nameof(@base));
        Index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public MirQasmExpression Base { get; }
    public MirQasmExpression Index { get; }
}

public sealed record MirQasmSizeOfExpression : MirQasmExpression
{
    public MirQasmSizeOfExpression(MirQasmExpression operand) =>
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));

    public MirQasmExpression Operand { get; }
}

public sealed record MirQasmUnsignedCastExpression : MirQasmExpression
{
    public MirQasmUnsignedCastExpression(
        int width,
        MirQasmExpression operand)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "an unsigned cast width must be positive");
        Width = width;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    public int Width { get; }
    public MirQasmExpression Operand { get; }
}

public abstract record MirQasmFunctionTarget;

public sealed record MirQasmBuiltinFunctionTarget : MirQasmFunctionTarget
{
    public MirQasmBuiltinFunctionTarget(string emittedName) =>
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));

    public string EmittedName { get; }
}

public sealed record MirQasmUserFunctionTarget(
    MirQasmCallableId Callable) : MirQasmFunctionTarget;

public sealed record MirQasmFunctionCallExpression : MirQasmExpression
{
    public MirQasmFunctionCallExpression(
        MirQasmFunctionTarget target,
        IEnumerable<MirQasmExpression> arguments)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Arguments = MirQasmCollections.Freeze(arguments, nameof(arguments));
    }

    public MirQasmFunctionTarget Target { get; }
    public ImmutableArray<MirQasmExpression> Arguments { get; }
}

public abstract record MirQasmQuantumTarget;

/// <summary>A final stdgate/custom-gate spelling which has no target callable definition.</summary>
public sealed record MirQasmBuiltinGateTarget : MirQasmQuantumTarget
{
    public MirQasmBuiltinGateTarget(string emittedName) =>
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));

    public string EmittedName { get; }
}

/// <summary>A call to an operation definition in this exact target program.</summary>
public sealed record MirQasmUserQuantumTarget(
    MirQasmCallableId Callable) : MirQasmQuantumTarget;

public enum MirQasmQuantumModifier
{
    Inverse,
    Controlled,
}

public abstract record MirQasmStatement;

/// <summary>A scalar or bit/register declaration. Qubit and general-array declarations have distinct nodes.</summary>
public sealed record MirQasmValueDeclarationStatement : MirQasmStatement
{
    public MirQasmValueDeclarationStatement(
        MirQasmDeclarationId declaration,
        string emittedName,
        MirQasmType type,
        MirQasmExpression? initializer = null,
        bool isConst = false)
    {
        if (type is not (MirQasmScalarType or MirQasmBitType))
            throw new ArgumentException(
                "a value declaration requires a scalar or bit/register type",
                nameof(type));
        Declaration = declaration;
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));
        Type = type;
        Initializer = initializer;
        IsConst = isConst;
    }

    public MirQasmDeclarationId Declaration { get; }
    public string EmittedName { get; }
    public MirQasmType Type { get; }
    public MirQasmExpression? Initializer { get; }
    public bool IsConst { get; }
}

public sealed record MirQasmArrayDeclarationStatement : MirQasmStatement
{
    public MirQasmArrayDeclarationStatement(
        MirQasmDeclarationId declaration,
        string emittedName,
        MirQasmArrayType type,
        IEnumerable<MirQasmExpression> elements)
    {
        Declaration = declaration;
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));
        Type = type ?? throw new ArgumentNullException(nameof(type));
        if (type.Length is null)
            throw new ArgumentException(
                "a general array declaration requires a concrete target length",
                nameof(type));
        Elements = MirQasmCollections.Freeze(elements, nameof(elements));
        if (Elements.Length != type.Length)
            throw new ArgumentException(
                $"array declaration `{emittedName}` has {Elements.Length} initializer element(s), " +
                $"expected {type.Length}",
                nameof(elements));
    }

    public MirQasmDeclarationId Declaration { get; }
    public string EmittedName { get; }
    public MirQasmArrayType Type { get; }
    public ImmutableArray<MirQasmExpression> Elements { get; }
}

public sealed record MirQasmQubitDeclarationStatement : MirQasmStatement
{
    public MirQasmQubitDeclarationStatement(
        MirQasmDeclarationId declaration,
        string emittedName,
        MirQasmQubitType type)
    {
        Declaration = declaration;
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public MirQasmDeclarationId Declaration { get; }
    public string EmittedName { get; }
    public MirQasmQubitType Type { get; }
}

public sealed record MirQasmAssignmentStatement : MirQasmStatement
{
    public MirQasmAssignmentStatement(
        MirQasmExpression target,
        MirQasmExpression value)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public MirQasmExpression Target { get; }
    public MirQasmExpression Value { get; }
}

public sealed record MirQasmMeasurementAssignmentStatement : MirQasmStatement
{
    public MirQasmMeasurementAssignmentStatement(
        MirQasmExpression target,
        MirQasmExpression qubit)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Qubit = qubit ?? throw new ArgumentNullException(nameof(qubit));
    }

    public MirQasmExpression Target { get; }
    public MirQasmExpression Qubit { get; }
}

/// <summary>
/// One fully legalized quantum invocation. Built-in gates keep gate parameters separate from qubit
/// operands so the emitter can print <c>rx(theta) q</c>. User-operation calls put every positional
/// argument in <see cref="Operands"/> and leave <see cref="GateParameters"/> empty.
/// </summary>
public sealed record MirQasmQuantumApplyStatement : MirQasmStatement
{
    public MirQasmQuantumApplyStatement(
        MirQasmQuantumTarget target,
        IEnumerable<MirQasmExpression> gateParameters,
        IEnumerable<MirQasmExpression> operands,
        IEnumerable<MirQasmQuantumModifier>? modifiers = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        GateParameters = MirQasmCollections.Freeze(gateParameters, nameof(gateParameters));
        Operands = MirQasmCollections.Freeze(operands, nameof(operands));
        Modifiers = modifiers is null
            ? ImmutableArray<MirQasmQuantumModifier>.Empty
            : MirQasmCollections.Freeze(modifiers, nameof(modifiers));
        foreach (var modifier in Modifiers)
            if (!Enum.IsDefined(modifier))
                throw new ArgumentOutOfRangeException(
                    nameof(modifiers),
                    modifier,
                    "unknown quantum modifier");
        if (Modifiers.Distinct().Count() != Modifiers.Length)
            throw new ArgumentException(
                "a fully legalized quantum application cannot repeat a modifier",
                nameof(modifiers));
        if (target is MirQasmUserQuantumTarget
            && (GateParameters.Length != 0 || Modifiers.Length != 0))
        {
            throw new ArgumentException(
                "a user-operation target must already have inverse/control materialized as a plain callable",
                nameof(target));
        }
    }

    public MirQasmQuantumTarget Target { get; }
    public ImmutableArray<MirQasmExpression> GateParameters { get; }
    public ImmutableArray<MirQasmExpression> Operands { get; }
    public ImmutableArray<MirQasmQuantumModifier> Modifiers { get; }
}

public sealed record MirQasmIfStatement : MirQasmStatement
{
    public MirQasmIfStatement(
        MirQasmExpression condition,
        IEnumerable<MirQasmStatement> thenStatements,
        IEnumerable<MirQasmStatement>? elseStatements = null)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Then = MirQasmCollections.Freeze(thenStatements, nameof(thenStatements));
        Else = elseStatements is null
            ? ImmutableArray<MirQasmStatement>.Empty
            : MirQasmCollections.Freeze(elseStatements, nameof(elseStatements));
    }

    public MirQasmExpression Condition { get; }
    public ImmutableArray<MirQasmStatement> Then { get; }
    public ImmutableArray<MirQasmStatement> Else { get; }
}

public sealed record MirQasmWhileStatement : MirQasmStatement
{
    public MirQasmWhileStatement(
        MirQasmExpression condition,
        IEnumerable<MirQasmStatement> body)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Body = MirQasmCollections.Freeze(body, nameof(body));
    }

    public MirQasmExpression Condition { get; }
    public ImmutableArray<MirQasmStatement> Body { get; }
}

public sealed record MirQasmReturnStatement : MirQasmStatement
{
    public MirQasmReturnStatement(
        MirQasmExpression? value = null)
    {
        Value = value;
    }

    public MirQasmExpression? Value { get; }
}

public sealed record MirQasmBreakStatement : MirQasmStatement;

public enum MirQasmCallableKind
{
    Operation,
    Function,
}

public sealed record MirQasmCallableDefinition
{
    public MirQasmCallableDefinition(
        MirQasmCallableId id,
        string emittedName,
        IEnumerable<MirQasmParameter> parameters,
        MirQasmType? returnType,
        IEnumerable<MirQasmStatement> body)
    {
        if (returnType is MirQasmQubitType or MirQasmArrayType
            || returnType is MirQasmBitType { IsRegister: true })
        {
            throw new ArgumentException(
                "the current OpenQASM target supports only scalar function returns",
                nameof(returnType));
        }

        Id = id;
        EmittedName = MirQasmNames.RequireIdentifier(emittedName, nameof(emittedName));
        Parameters = MirQasmCollections.Freeze(parameters, nameof(parameters));
        ReturnType = returnType;
        Body = MirQasmCollections.Freeze(body, nameof(body));
    }

    public MirQasmCallableId Id { get; }
    public string EmittedName { get; }
    public MirQasmCallableKind Kind =>
        ReturnType is null
            ? MirQasmCallableKind.Operation
            : MirQasmCallableKind.Function;
    public ImmutableArray<MirQasmParameter> Parameters { get; }
    public MirQasmType? ReturnType { get; }
    public ImmutableArray<MirQasmStatement> Body { get; }
}

/// <summary>
/// Immutable, fully legalized OpenQASM target AST produced from MIR. It intentionally carries no
/// MIR source ownership: the compiler artifact owns the exact source snapshot, while this model owns only
/// target identities, final names, final types, and serialization structure.
/// </summary>
public sealed class MirOpenQasmTargetProgram
{
    public MirOpenQasmTargetProgram(
        IEnumerable<MirQasmStatement> entryBody,
        IEnumerable<MirQasmCallableDefinition> definitions,
        IEnumerable<string>? notes = null)
    {
        EntryBody = MirQasmCollections.Freeze(entryBody, nameof(entryBody));
        Definitions = MirQasmCollections.Freeze(definitions, nameof(definitions));
        Notes = notes is null
            ? ImmutableArray<string>.Empty
            : MirQasmCollections.Freeze(notes, nameof(notes));
        foreach (var note in Notes)
        {
            if (string.IsNullOrWhiteSpace(note))
                throw new ArgumentException("a target note cannot be blank", nameof(notes));
            if (note.Contains('\r') || note.Contains('\n'))
                throw new ArgumentException("a target note must fit on one line", nameof(notes));
        }

        MirQasmTargetVerifier.VerifyOrThrow(this);
    }

    public ImmutableArray<MirQasmStatement> EntryBody { get; }
    public ImmutableArray<MirQasmCallableDefinition> Definitions { get; }
    public ImmutableArray<string> Notes { get; }
}

internal static class MirQasmCollections
{
    public static ImmutableArray<T> Freeze<T>(
        IEnumerable<T> source,
        string parameter)
    {
        ArgumentNullException.ThrowIfNull(source, parameter);
        var result = source.ToImmutableArray();
        if (result.Any(item => item is null))
            throw new ArgumentException("a target collection cannot contain null", parameter);
        return result;
    }
}

internal static class MirQasmNames
{
    public static string RequireIdentifier(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("an emitted identifier cannot be blank", parameter);
        if (!IsIdentifier(value))
            throw new ArgumentException($"`{value}` is not a valid OpenQASM identifier", parameter);
        return value;
    }

    private static bool IsIdentifier(string value)
    {
        if (!(value[0] == '_' || char.IsAsciiLetter(value[0]))) return false;
        for (var index = 1; index < value.Length; index++)
            if (!(value[index] == '_' || char.IsAsciiLetterOrDigit(value[index])))
                return false;
        return true;
    }
}

/// <summary>
/// Structural verifier for the target model itself. It resolves only target IDs and therefore cannot
/// accidentally recover missing information from HIR, MIR, a semantic model, or source spelling.
/// </summary>
internal static class MirQasmTargetVerifier
{
    public static void VerifyOrThrow(MirOpenQasmTargetProgram program)
    {
        var callables = new Dictionary<MirQasmCallableId, MirQasmCallableDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in program.Definitions)
        {
            if (!callables.TryAdd(definition.Id, definition))
                throw new ArgumentException(
                    $"target callable ID {definition.Id} occurs more than once",
                    nameof(program));
            if (!names.Add(definition.EmittedName))
                throw new ArgumentException(
                    $"emitted callable name `{definition.EmittedName}` occurs more than once",
                    nameof(program));
        }

        VerifyBody(
            "<entry>",
            MirQasmCallableKind.Operation,
            allowReturn: false,
            Array.Empty<MirQasmParameter>(),
            program.EntryBody,
            callables);
        foreach (var definition in program.Definitions)
            VerifyBody(
                definition.EmittedName,
                definition.Kind,
                allowReturn: true,
                definition.Parameters,
                definition.Body,
                callables);

        var entryNames = DeclaredNames(program.EntryBody);
        foreach (var definition in program.Definitions)
            if (entryNames.Contains(definition.EmittedName))
                throw new ArgumentException(
                    $"global declaration `{definition.EmittedName}` collides with a callable definition",
                    nameof(program));
    }

    private static void VerifyBody(
        string callableName,
        MirQasmCallableKind kind,
        bool allowReturn,
        IReadOnlyList<MirQasmParameter> parameters,
        IReadOnlyList<MirQasmStatement> body,
        IReadOnlyDictionary<MirQasmCallableId, MirQasmCallableDefinition> callables)
    {
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        var parameterIds = new HashSet<MirQasmParameterId>();
        foreach (var parameter in parameters)
        {
            if (!parameterIds.Add(parameter.Id))
                throw Invalid(callableName, $"parameter ID {parameter.Id} occurs more than once");
            if (!parameterNames.Add(parameter.EmittedName))
                throw Invalid(callableName, $"parameter name `{parameter.EmittedName}` occurs more than once");
        }

        var declarations = new Dictionary<MirQasmDeclarationId, string>();
        var declarationNames = new HashSet<string>(parameterNames, StringComparer.Ordinal);
        CollectDeclarations(body);
        VerifyStatements(body, loopDepth: 0);
        return;

        void CollectDeclarations(IReadOnlyList<MirQasmStatement> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case MirQasmValueDeclarationStatement declaration:
                        AddDeclaration(declaration.Declaration, declaration.EmittedName);
                        break;
                    case MirQasmArrayDeclarationStatement declaration:
                        AddDeclaration(declaration.Declaration, declaration.EmittedName);
                        break;
                    case MirQasmQubitDeclarationStatement declaration:
                        AddDeclaration(declaration.Declaration, declaration.EmittedName);
                        break;
                    case MirQasmIfStatement branch:
                        CollectDeclarations(branch.Then);
                        CollectDeclarations(branch.Else);
                        break;
                    case MirQasmWhileStatement loop:
                        CollectDeclarations(loop.Body);
                        break;
                }
            }
        }

        void AddDeclaration(MirQasmDeclarationId id, string name)
        {
            if (!declarations.TryAdd(id, name))
                throw Invalid(callableName, $"declaration ID {id} occurs more than once");
            if (!declarationNames.Add(name))
                throw Invalid(callableName, $"emitted binding name `{name}` occurs more than once");
        }

        void VerifyStatements(
            IReadOnlyList<MirQasmStatement> statements,
            int loopDepth)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case MirQasmValueDeclarationStatement declaration:
                        if (declaration.Initializer is { } value)
                            VerifyExpression(value);
                        break;
                    case MirQasmArrayDeclarationStatement declaration:
                        foreach (var element in declaration.Elements)
                            VerifyExpression(element);
                        break;
                    case MirQasmQubitDeclarationStatement:
                        break;
                    case MirQasmAssignmentStatement assignment:
                        RequireAssignable(assignment.Target);
                        VerifyExpression(assignment.Target);
                        VerifyExpression(assignment.Value);
                        break;
                    case MirQasmMeasurementAssignmentStatement measurement:
                        RequireAssignable(measurement.Target);
                        VerifyExpression(measurement.Target);
                        VerifyExpression(measurement.Qubit);
                        break;
                    case MirQasmQuantumApplyStatement apply:
                        if (apply.Target is MirQasmUserQuantumTarget user)
                        {
                            if (!callables.TryGetValue(user.Callable, out var callee))
                                throw Invalid(callableName, $"quantum call targets missing {user.Callable}");
                            if (callee.Kind != MirQasmCallableKind.Operation)
                                throw Invalid(
                                    callableName,
                                    $"quantum call targets function `{callee.EmittedName}`");
                            if (apply.Operands.Length != callee.Parameters.Length)
                            {
                                throw Invalid(
                                    callableName,
                                    $"quantum call to `{callee.EmittedName}` has "
                                    + $"{apply.Operands.Length} operand(s), expected "
                                    + $"{callee.Parameters.Length}");
                            }
                        }
                        foreach (var parameter in apply.GateParameters)
                            VerifyExpression(parameter);
                        foreach (var operand in apply.Operands)
                            VerifyExpression(operand);
                        break;
                    case MirQasmIfStatement branch:
                        VerifyExpression(branch.Condition);
                        VerifyStatements(branch.Then, loopDepth);
                        VerifyStatements(branch.Else, loopDepth);
                        break;
                    case MirQasmWhileStatement loop:
                        VerifyExpression(loop.Condition);
                        VerifyStatements(loop.Body, checked(loopDepth + 1));
                        break;
                    case MirQasmReturnStatement returned:
                        if (!allowReturn)
                            throw Invalid(callableName, "the entry top-level cannot contain return");
                        if (kind == MirQasmCallableKind.Function && returned.Value is null)
                            throw Invalid(callableName, "a function return requires a value");
                        if (kind == MirQasmCallableKind.Operation && returned.Value is not null)
                            throw Invalid(callableName, "an operation return cannot carry a value");
                        if (returned.Value is { } returnedValue)
                            VerifyExpression(returnedValue);
                        break;
                    case MirQasmBreakStatement:
                        if (loopDepth == 0)
                            throw Invalid(callableName, "break occurs outside a while body");
                        break;
                    default:
                        throw Invalid(
                            callableName,
                            $"unknown target statement {statement.GetType().Name}");
                }
            }
        }

        void VerifyExpression(MirQasmExpression expression)
        {
            switch (expression)
            {
                case MirQasmLiteralExpression:
                    break;
                case MirQasmParameterReferenceExpression parameter:
                    if (!parameterIds.Contains(parameter.Parameter))
                        throw Invalid(
                            callableName,
                            $"expression references missing parameter {parameter.Parameter}");
                    break;
                case MirQasmDeclarationReferenceExpression declaration:
                    if (!declarations.ContainsKey(declaration.Declaration))
                        throw Invalid(
                            callableName,
                            $"expression references missing declaration {declaration.Declaration}");
                    break;
                case MirQasmUnaryExpression unary:
                    VerifyExpression(unary.Operand);
                    break;
                case MirQasmBinaryExpression binary:
                    VerifyExpression(binary.Left);
                    VerifyExpression(binary.Right);
                    break;
                case MirQasmIndexExpression index:
                    VerifyExpression(index.Base);
                    VerifyExpression(index.Index);
                    break;
                case MirQasmSizeOfExpression size:
                    VerifyExpression(size.Operand);
                    break;
                case MirQasmUnsignedCastExpression cast:
                    VerifyExpression(cast.Operand);
                    break;
                case MirQasmFunctionCallExpression call:
                    if (call.Target is MirQasmUserFunctionTarget user)
                    {
                        if (!callables.TryGetValue(user.Callable, out var callee))
                            throw Invalid(callableName, $"function call targets missing {user.Callable}");
                        if (callee.Kind != MirQasmCallableKind.Function)
                            throw Invalid(
                                callableName,
                                $"function call targets operation `{callee.EmittedName}`");
                        if (call.Arguments.Length != callee.Parameters.Length)
                        {
                            throw Invalid(
                                callableName,
                                $"function call to `{callee.EmittedName}` has "
                                + $"{call.Arguments.Length} argument(s), expected "
                                + $"{callee.Parameters.Length}");
                        }
                    }
                    foreach (var argument in call.Arguments)
                        VerifyExpression(argument);
                    break;
                default:
                    throw Invalid(
                        callableName,
                        $"unknown target expression {expression.GetType().Name}");
            }
        }

        void RequireAssignable(MirQasmExpression expression)
        {
            if (expression is MirQasmParameterReferenceExpression
                or MirQasmDeclarationReferenceExpression)
                return;
            if (expression is MirQasmIndexExpression index)
            {
                RequireAssignable(index.Base);
                return;
            }
            throw Invalid(callableName, "an assignment target is not a parameter/declaration place");
        }
    }

    private static HashSet<string> DeclaredNames(
        IReadOnlyList<MirQasmStatement> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case MirQasmValueDeclarationStatement declaration:
                    names.Add(declaration.EmittedName);
                    break;
                case MirQasmArrayDeclarationStatement declaration:
                    names.Add(declaration.EmittedName);
                    break;
                case MirQasmQubitDeclarationStatement declaration:
                    names.Add(declaration.EmittedName);
                    break;
                case MirQasmIfStatement branch:
                    names.UnionWith(DeclaredNames(branch.Then));
                    names.UnionWith(DeclaredNames(branch.Else));
                    break;
                case MirQasmWhileStatement loop:
                    names.UnionWith(DeclaredNames(loop.Body));
                    break;
            }
        }
        return names;
    }

    private static ArgumentException Invalid(
        string callable,
        string message) =>
        new($"invalid OpenQASM target body `{callable}`: {message}");
}
