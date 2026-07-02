namespace Qora.Ir;

/// <summary>
/// Qora's own intermediate representation (IR): a strongly-typed, immutable tree that the compiler
/// owns end-to-end, decoupled from the Janglim parse-AST. The pipeline is
/// <c>source → Janglim AST → <see cref="QoraLowering"/> → QProgram → …passes… → <see cref="QasmEmitter"/> → OpenQASM</c>.
/// (Not related to Microsoft's LLVM-based "QIR" standard — this is Qora's private, in-memory IR;
/// the "Q" prefix on node types just namespaces them.)
///
/// Design notes:
/// <list type="bullet">
///   <item>Statements form a discriminated union (sealed <see cref="QStmt"/> hierarchy) so passes match
///         on the node type — the compiler flags an unhandled case instead of a string <c>switch</c>.</item>
///   <item>Everything is an immutable record, so a pass is a pure IR→IR function and can rebuild a node
///         cheaply with a <c>with</c> expression.</item>
///   <item>Expressions and conditions are kept as already-rendered text (<see cref="QExpr"/>/<see cref="QCond"/>)
///         for now: the current pipeline never restructures them, only the statement/gate/control-flow
///         layer. They can graduate to a real expression tree later without touching the rest.</item>
/// </list>
/// </summary>
public sealed record QProgram(IReadOnlyList<QOperation> Operations);

public sealed record QOperation(string Name, IReadOnlyList<QParam> Params, IReadOnlyList<QStmt> Body);

public enum QType { Qubit, Int, Bit }

/// <summary>A def parameter: a qubit, a sized qubit register (<see cref="RegisterSize"/> != null), or an int/bit.</summary>
public sealed record QParam(string Name, QType Type, int? RegisterSize);

// ---- statements ----

public abstract record QStmt;

/// <summary><c>use q = Qubit[Size];</c></summary>
public sealed record QUse(string Name, int Size) : QStmt;

/// <summary>
/// A gate application, functor-modified gate, or user-operation call — all share this one shape, exactly
/// as the surface grammar does. <see cref="Functors"/> holds the prefix modifiers outermost-first
/// (e.g. <c>["Controlled"]</c> for <c>Controlled X</c>); it is 0-or-1 long today but a list so inverses
/// can stack (<c>inv @ ctrl @ …</c>). The emitter tells a gate from a call by whether <see cref="Name"/>
/// is a defined operation.
/// </summary>
public sealed record QGate(IReadOnlyList<string> Functors, string Name, IReadOnlyList<QArg> Args) : QStmt;

/// <summary><c>const int n = e;</c> / <c>int n = e;</c> / <c>bit r = M(q);</c> (measurement when Value is <see cref="QMeasure"/>).</summary>
public sealed record QDecl(bool IsConst, QType? Type, string Name, QExpr Value) : QStmt;

/// <summary><c>name = e;</c></summary>
public sealed record QAssign(string Name, QExpr Value) : QStmt;

public sealed record QIf(QCond Cond, IReadOnlyList<QStmt> Then, IReadOnlyList<QStmt> Else) : QStmt;

/// <summary>
/// <c>for Var in From..To { Body }</c> (bounds are literal numbers today, so kept as text).
/// <see cref="Step"/> has no surface syntax: the surface form always steps by +1 (null); the inversion
/// pass sets <c>"-1"</c> to run a loop backwards, which emits OpenQASM's <c>[From:Step:To]</c> range.
/// </summary>
public sealed record QFor(string Var, string From, string To, IReadOnlyList<QStmt> Body, string? Step = null) : QStmt;

public sealed record QWhile(QCond Cond, IReadOnlyList<QStmt> Body) : QStmt;

public sealed record QRepeat(IReadOnlyList<QStmt> Body, QCond Until) : QStmt;

/// <summary>
/// Synthetic node — no surface syntax produces it. A later uncompute pass injects it to mean
/// "run <see cref="Within"/>, then <see cref="Apply"/>, then the inverse of <see cref="Within"/>".
/// Present now so the IR is ready for that pass; nothing emits it yet.
/// </summary>
public sealed record QConjugate(IReadOnlyList<QStmt> Within, IReadOnlyList<QStmt> Apply) : QStmt;

// ---- arguments ----

public abstract record QArg;

/// <summary>A qubit reference <c>reg[Index]</c> (Index is a literal or a loop variable).</summary>
public sealed record QQubitArg(string Reg, string Index) : QArg;

/// <summary>
/// A non-qubit argument already rendered to text: a whole register <c>q</c>, or an angle like
/// <c>pi / 2</c>. <see cref="HasCall"/> marks that a call (e.g. <c>M(q[0])</c>) appeared inside — no
/// such argument can lower to OpenQASM, so the validator rejects it before emission.
/// </summary>
public sealed record QTextArg(string Text, bool HasCall = false) : QArg;

// ---- expressions / conditions (rendered text for now) ----

public abstract record QExpr;

/// <summary>A measurement as a whole initializer/RHS — the one legal call-in-expression form (<c>bit r = M(q[i]);</c>).</summary>
public sealed record QMeasure(QQubitArg? Target) : QExpr;

/// <summary>
/// Any other expression, kept as its rendered text (e.g. <c>pi / 4</c>, <c>count + 1</c>).
/// <see cref="HasCall"/> marks that a call appeared inside (mixed with arithmetic, or a non-<c>M</c>
/// call) — unexpressible in OpenQASM, rejected by the validator.
/// </summary>
public sealed record QText(string Text, bool HasCall = false) : QExpr;

/// <summary>
/// A boolean condition, kept as rendered text (e.g. <c>r == 1</c>). <see cref="HasCall"/> marks a call
/// inside the condition (e.g. <c>while (M(q[0]) == 0)</c>) — OpenQASM has no measurement expressions,
/// so the validator rejects it (assign to a bit first).
/// </summary>
public sealed record QCond(string Text, bool HasCall = false);

// ---- the built-in gate table (single source of truth for emitter + validator + inverter) ----

/// <summary>Everything the pipeline needs to know about one built-in gate.</summary>
/// <param name="QasmName">The OpenQASM gate (or statement) this lowers to.</param>
/// <param name="Arity">Expected argument count, angle included; <c>Controlled</c> adds one on top.</param>
/// <param name="AngleFirst">Parametrized rotation form: <c>Rx(θ, q)</c> → <c>rx(θ) q;</c>.</param>
/// <param name="Unitary">False for reset-like operations — they cannot take functors or be inverted.</param>
public sealed record GateInfo(string QasmName, int Arity, bool AngleFirst = false, bool Unitary = true);

/// <summary>
/// Qora's built-in gate registry. THE single source of truth: to add a built-in, add ONE entry here —
/// the emitter's name mapping, the validator's arity/unitarity checks, the inverter's irreversibility
/// rejection, and the reserved-name set all derive from this table automatically.
/// </summary>
public static class QoraGates
{
    public static readonly IReadOnlyDictionary<string, GateInfo> Gates = new Dictionary<string, GateInfo>
    {
        ["H"] = new("h", 1), ["X"] = new("x", 1), ["Y"] = new("y", 1), ["Z"] = new("z", 1),
        ["S"] = new("s", 1), ["T"] = new("t", 1),
        ["CNOT"] = new("cx", 2), ["CX"] = new("cx", 2), ["CY"] = new("cy", 2), ["CZ"] = new("cz", 2),
        ["SWAP"] = new("swap", 2), ["CCX"] = new("ccx", 3),
        ["Rx"] = new("rx", 2, AngleFirst: true),
        ["Ry"] = new("ry", 2, AngleFirst: true),
        ["Rz"] = new("rz", 2, AngleFirst: true),
        ["Reset"] = new("reset", 1, Unitary: false),
        ["ResetAll"] = new("reset", 1, Unitary: false),
    };

    // --- derived views (do NOT edit these; edit Gates above) ---

    /// <summary>Qora gate name → OpenQASM gate name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Names =
        Gates.ToDictionary(kv => kv.Key, kv => kv.Value.QasmName);

    /// <summary>Rotation gates take an angle first: <c>Rx(θ, q)</c> → <c>rx(θ) q;</c>.</summary>
    public static readonly IReadOnlySet<string> Rotations =
        Gates.Where(kv => kv.Value.AngleFirst).Select(kv => kv.Key).ToHashSet();

    /// <summary>Expected argument count per gate; a <c>Controlled</c> functor adds one.</summary>
    public static readonly IReadOnlyDictionary<string, int> Arity =
        Gates.ToDictionary(kv => kv.Key, kv => kv.Value.Arity);

    /// <summary>Non-unitary built-ins (reset-like): no functors, never invertible.</summary>
    public static readonly IReadOnlySet<string> NonUnitary =
        Gates.Where(kv => !kv.Value.Unitary).Select(kv => kv.Key).ToHashSet();

    /// <summary>
    /// The one registered measurement function. Only a lone <c>M(q[i])</c> is a legal value
    /// (<c>bit r = M(q[i]);</c>); no alias is accepted.
    /// </summary>
    public const string Measurement = "M";

    /// <summary>
    /// Names that read as measurement attempts. NOT legal — used only to classify errors, so a user who
    /// writes <c>Measure(q[0]);</c> gets a measurement-specific message instead of "unknown gate".
    /// </summary>
    public static readonly IReadOnlySet<string> MeasureLike = new HashSet<string> { "M", "Measure", "measure" };

    /// <summary>
    /// Identifiers a Qora program must not declare (as an operation, parameter, variable, register, or
    /// loop variable), because they collide in the emitted OpenQASM: language keywords and built-in
    /// constants, every gate name <c>stdgates.inc</c> defines (declarations share one global namespace
    /// with gates there, so <c>int t = 2;</c> would clash with the <c>t</c> gate), and — derived
    /// automatically — whatever QASM name a <see cref="Gates"/> entry lowers to.
    /// </summary>
    public static readonly IReadOnlySet<string> QasmReserved = new HashSet<string>
    {
        // OpenQASM 3 keywords, types, and built-in constants
        "OPENQASM", "include", "def", "gate", "qubit", "bit", "int", "uint", "float", "angle", "bool",
        "complex", "array", "duration", "stretch", "let", "const", "measure", "reset", "barrier",
        "delay", "if", "else", "for", "while", "in", "return", "break", "continue", "end", "input",
        "output", "extern", "box", "ctrl", "negctrl", "inv", "pow", "im", "true", "false", "pi",
        "euler", "tau", "defcal", "defcalgrammar", "cal", "durationof", "sizeof", "U", "gphase",
        // gate names defined by stdgates.inc
        "p", "x", "y", "z", "h", "s", "sdg", "t", "tdg", "sx", "rx", "ry", "rz", "cx", "cy", "cz",
        "cp", "crx", "cry", "crz", "ch", "swap", "ccx", "cswap", "cu", "CX", "phase", "cphase", "id",
        "u1", "u2", "u3",
    }.Concat(Gates.Values.Select(g => g.QasmName)).ToHashSet();
}
