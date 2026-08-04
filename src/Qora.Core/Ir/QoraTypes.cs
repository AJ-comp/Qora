namespace Qora.Ir;

/// <summary>The compiler version stamped into emitted target artifacts.</summary>
public static class QoraVersion
{
    public const string Value = "0.37.0";
}

public enum QType
{
    Qubit,
    Int,
    Bit,
    Float,
    Angle,
}

public enum QOwnershipMode
{
    Borrowed,
    Moved,
}

public enum QAccessMode
{
    ReadOnly,
    Mutable,
}

/// <summary>
/// Target-independent modifiers attached to an intrinsic or user-call expression.
/// The enum is deliberately separate from HIR node construction because it is also
/// consumed by MIR lowering and target capability checks.
/// </summary>
public enum QGateModifier
{
    Controlled,
}

/// <summary>The common callable-parameter contract consumed by call validation.</summary>
public interface IParamSpec
{
    string Name { get; }
    QType Type { get; }
    int? RegisterSize { get; }
    bool IsArray { get; }
    bool IsQubitArray { get; }
    bool QubitBroadcast { get; }
    QOwnershipMode Ownership { get; }
    QAccessMode Access { get; }
}

/// <summary>The common signature view of user callables and intrinsic gates.</summary>
public interface ICallableSig
{
    string CalleeName { get; }
    IReadOnlyList<IParamSpec> Parameters { get; }
    bool IsBuiltin { get; }
}

/// <summary>Everything target-independent analysis needs to know about one intrinsic gate.</summary>
public sealed record GateInfo(
    string QasmName,
    int Arity,
    bool AngleFirst = false,
    bool Unitary = true,
    int Controls = 0,
    bool Diagonal = false,
    bool NonQfree = false);

public sealed record GateParam(
    string Name,
    QType Type,
    bool QubitBroadcast = false) : IParamSpec
{
    public int? RegisterSize => null;
    public bool IsArray => false;
    public bool IsQubitArray => false;
    public QOwnershipMode Ownership => QOwnershipMode.Borrowed;
    public QAccessMode Access => QAccessMode.ReadOnly;
}

public sealed record GateSig(
    string CalleeName,
    IReadOnlyList<IParamSpec> Parameters) : ICallableSig
{
    private IReadOnlyList<IParamSpec> _parameters =
        HirCollections.Freeze(Parameters);

    public IReadOnlyList<IParamSpec> Parameters
    {
        get => _parameters;
        init => _parameters = HirCollections.Freeze(value);
    }

    public bool IsBuiltin => true;
}

public sealed record BuiltinFunction(
    QType Returns,
    bool TakesBitRegister);

/// <summary>The single intrinsic callable registry shared by HIR, MIR, and target lowering.</summary>
public static class QoraGates
{
    public static readonly IReadOnlyDictionary<string, GateInfo> Gates =
        HirCollections.Freeze(
            new Dictionary<string, GateInfo>
            {
                ["H"] = new("h", 1, NonQfree: true),
                ["X"] = new("x", 1),
                ["Y"] = new("y", 1, NonQfree: true),
                ["Z"] = new("z", 1, Diagonal: true),
                ["S"] = new("s", 1, Diagonal: true),
                ["T"] = new("t", 1, Diagonal: true),
                ["CNOT"] = new("cx", 2, Controls: 1),
                ["CX"] = new("cx", 2, Controls: 1),
                ["CY"] = new("cy", 2, Controls: 1, NonQfree: true),
                ["CZ"] = new("cz", 2, Controls: 1, Diagonal: true),
                ["SWAP"] = new("swap", 2),
                ["CCX"] = new("ccx", 3, Controls: 2),
                ["Rx"] = new("rx", 2, AngleFirst: true, NonQfree: true),
                ["Ry"] = new("ry", 2, AngleFirst: true, NonQfree: true),
                ["Rz"] = new("rz", 2, AngleFirst: true, Diagonal: true),
                ["Reset"] = new("reset", 1, Unitary: false),
                ["ResetAll"] = new("reset", 1, Unitary: false),
            });

    public static ICallableSig? SigOf(
        string name,
        int extraControls = 0)
    {
        if (!Gates.TryGetValue(name, out var gate))
            return null;

        var parameters = new List<IParamSpec>();
        var qubitSlots =
            gate.Arity + extraControls - (gate.AngleFirst ? 1 : 0);
        if (gate.AngleFirst)
            parameters.Add(new GateParam("angle", QType.Angle));
        for (var index = 0; index < qubitSlots; index++)
        {
            parameters.Add(
                new GateParam(
                    $"q{index}",
                    QType.Qubit,
                    QubitBroadcast: true));
        }
        return new GateSig(name, parameters);
    }

    public static readonly IReadOnlyDictionary<string, string> Names =
        HirCollections.Freeze(
            Gates.Select(pair =>
                new KeyValuePair<string, string>(
                    pair.Key,
                    pair.Value.QasmName)));

    public static readonly IReadOnlySet<string> Rotations =
        HirCollections.FreezeSet(
            Gates
                .Where(pair => pair.Value.AngleFirst)
                .Select(pair => pair.Key));

    public static readonly IReadOnlySet<string> NonUnitary =
        HirCollections.FreezeSet(
            Gates
                .Where(pair => !pair.Value.Unitary)
                .Select(pair => pair.Key));

    public const string Measurement = "M";
    public const string BitsAsInt = "AsInt";
    public const string IntrinsicNamespace = "Qora.Intrinsic";

    public static readonly IReadOnlyDictionary<string, BuiltinFunction>
        Functions =
            HirCollections.Freeze(
                new Dictionary<string, BuiltinFunction>
                {
                    [BitsAsInt] =
                        new(QType.Int, TakesBitRegister: true),
                });

    public static readonly IReadOnlySet<string> MeasureLike =
        HirCollections.FreezeSet(
            new[]
            {
                "M",
                "Measure",
                "measure",
            });

    public static readonly IReadOnlySet<string> QasmKeywords =
        HirCollections.FreezeSet(
            new[]
            {
                "OPENQASM",
                "include",
                "def",
                "gate",
                "qubit",
                "bit",
                "int",
                "uint",
                "float",
                "angle",
                "bool",
                "complex",
                "array",
                "duration",
                "stretch",
                "let",
                "const",
                "readonly",
                "mutable",
                "measure",
                "reset",
                "barrier",
                "delay",
                "if",
                "else",
                "for",
                "while",
                "in",
                "return",
                "break",
                "continue",
                "end",
                "input",
                "output",
                "extern",
                "box",
                "ctrl",
                "negctrl",
                "inv",
                "pow",
                "im",
                "true",
                "false",
                "pi",
                "euler",
                "tau",
                "defcal",
                "defcalgrammar",
                "cal",
                "durationof",
                "sizeof",
                "U",
                "gphase",
            });

    public static readonly IReadOnlySet<string> StdgatesNames =
        HirCollections.FreezeSet(
            new[]
            {
                "p",
                "x",
                "y",
                "z",
                "h",
                "s",
                "sdg",
                "t",
                "tdg",
                "sx",
                "rx",
                "ry",
                "rz",
                "cx",
                "cy",
                "cz",
                "cp",
                "crx",
                "cry",
                "crz",
                "ch",
                "swap",
                "ccx",
                "cswap",
                "cu",
                "CX",
                "phase",
                "cphase",
                "id",
                "u1",
                "u2",
                "u3",
            }.Concat(Gates.Values.Select(gate => gate.QasmName)));

    public static readonly IReadOnlySet<string> QasmReserved =
        HirCollections.FreezeSet(
            QasmKeywords.Concat(StdgatesNames));
}
