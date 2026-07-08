namespace Qora.Ir;

/// <summary>The compiler's version, stamped into emitted QASM for provenance.</summary>
public static class QoraVersion
{
    public const string Value = "0.16";
}

/// <summary>
/// A half-open character span <c>[Start, End)</c> into the ENTRY source document — the offsets the
/// editor contract (<c>errors[].start/end</c>) speaks. Null wherever a node has no location: nodes
/// lowered from IMPORTED files (their offsets would lie about the entry document) and synthesized
/// nodes (inverse bodies, uncompute injections).
/// </summary>
public readonly record struct QSpan(int Start, int End);

/// <summary>
/// Issues the stable node identity every identity-bearing IR record stamps at construction
/// (<c>public int Id { get; init; } = QNodeIds.Next();</c>). C# record semantics do the rest: a
/// property initializer runs only in the constructor, and a <c>with</c> expression CLONES fields —
/// so <c>new</c> mints a fresh Id while every pass rewrite via <c>with</c> inherits the Id for free.
/// That inherited Id is what lets side tables (the <see cref="Passes.SemanticModel"/>) keyed by Id
/// survive the whole pipeline while node CONTENT (names, sizes) keeps changing underneath.
/// The counter is global and monotonic across compilations — only uniqueness WITHIN one program
/// matters, and a process-wide counter avoids threading an allocator through all of lowering.
/// A pass that COPIES a subtree into a second tree position must re-mint Ids via <see cref="ReId"/>;
/// <see cref="Passes.ReferentialCheck"/> fails loudly on any duplicate that slips through.
/// </summary>
public static class QNodeIds
{
    private static int _next;
    public static int Next() => System.Threading.Interlocked.Increment(ref _next);
}

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
/// <param name="Operations">The program's operations (global namespace, until resolution lands).</param>
/// <param name="Imports">
/// The file's <c>import</c> declarations, in source order. <see cref="Passes.ModuleLoader"/> consumes them
/// (loading each file and merging its operations/opens into this program); after expansion the merged
/// program carries null here. Null/empty = none.
/// </param>
/// <param name="Opens">Per-namespace <c>open</c> directives (namespace name → opened namespaces).</param>
public sealed record QProgram(
    IReadOnlyList<QOperation> Operations,
    IReadOnlyList<QImport>? Imports = null,
    IReadOnlyDictionary<string, IReadOnlyList<QOpen>>? Opens = null)
{
    /// <summary>Stable node identity (see <see cref="QNodeIds"/>): fresh at <c>new</c>, inherited by <c>with</c>.</summary>
    public int Id { get; init; } = QNodeIds.Next();
}

/// <summary>One <c>open Target;</c> directive inside a namespace block.</summary>
public sealed record QOpen(string Target, QSpan? Span = null);

/// <summary>
/// One <c>import</c> declaration. Qora has a single import form — a quoted relative path including
/// the extension: <c>import "gates lib.qor";</c> or <c>import "lib/gates.qor";</c>.
/// <see cref="Target"/> holds the path WITHOUT the surrounding quotes.
/// </summary>
public sealed record QImport(string Target)
{
    /// <summary>The declaration as the user wrote it (for error messages).</summary>
    public string Display => $"\"{Target}\"";

    public QSpan? Span { get; init; }
}

/// <param name="Namespace">The namespace the op was declared in ("" = global). After the resolver
/// pass runs, <see cref="Name"/> is the FULLY-QUALIFIED name and this records the origin.</param>
public sealed record QOperation(string Name, IReadOnlyList<QParam> Params, IReadOnlyList<QStmt> Body, string Namespace = "") : ICallableSig
{
    /// <summary>Stable node identity (see <see cref="QNodeIds"/>): fresh at <c>new</c>, inherited by <c>with</c>.</summary>
    public int Id { get; init; } = QNodeIds.Next();

    /// <summary>
    /// The name to SHOW in diagnostics, when it differs from the emitted <see cref="Name"/>. Null means
    /// "use <see cref="Name"/>". <see cref="Passes.Monomorphizer"/> sets it to the original generic op
    /// (so a size specialization <c>Foo__sz3</c> still reports errors as <c>Foo</c>). Emission always uses
    /// <see cref="Name"/>; only user-facing messages consult this.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>The name a call-site error shows for this callee (the generic origin, not a specialization).</summary>
    public string CalleeName => DisplayName ?? Name;

    bool ICallableSig.IsBuiltin => false;

    /// <summary>The signature IS the parameter list — <see cref="QParam"/> already implements
    /// <see cref="IParamSpec"/>, so this is a covariant view, not a second copy.</summary>
    IReadOnlyList<IParamSpec> ICallableSig.Params => Params;

    /// <summary>Span of the operation's NAME token (op-level errors point here).</summary>
    public QSpan? Span { get; init; }
}

public enum QType { Qubit, Int, Bit, Float, Angle }

/// <summary>
/// One parameter slot of a CALLABLE — a built-in gate or a user operation — reduced to exactly what a
/// call-site kind check needs. <see cref="Type"/> <c>== Qubit</c> is a QUBIT slot (shaped by
/// <see cref="RegisterSize"/> / <see cref="SizeParam"/> / <see cref="QubitBroadcast"/>); any other
/// <see cref="Type"/> is a VALUE slot expecting a classical of that type (an angle for a rotation, an
/// int/bit/float for a classical parameter). A qubit passed to a value slot — or a value to a qubit slot —
/// is QSEM006.
/// </summary>
public interface IParamSpec
{
    string Name { get; }
    QType Type { get; }
    int? RegisterSize { get; }   // qubit slot: exact register size (null ⇒ single qubit, unless QubitBroadcast)
    string? SizeParam { get; }   // qubit slot: symbolic (generic) register size
    bool QubitBroadcast { get; } // qubit slot: accepts ANY qubit shape (built-in gates broadcast; ops are strict)
}

/// <summary>
/// A callable's signature: its ordered parameter slots. BOTH user operations (<see cref="QOperation"/>)
/// and built-in gates (<see cref="QoraGates.SigOf"/>) expose one, so a SINGLE call-site check
/// (<c>CheckCall</c>) serves both without knowing which it is. The signature is not a stored second copy —
/// it is a view over the same <see cref="QParam"/>s / <see cref="GateInfo"/> the rest of the pipeline uses,
/// so it can never drift from the source.
/// </summary>
public interface ICallableSig
{
    string CalleeName { get; }                 // name to show in diagnostics
    IReadOnlyList<IParamSpec> Params { get; }
    bool IsBuiltin { get; }                     // gate vs user op — consulted ONLY for message phrasing, not the check
}

/// <summary>A def parameter: a qubit, a sized qubit register (<see cref="RegisterSize"/> != null), or an int/bit.
/// It IS its own signature slot (<see cref="IParamSpec"/>) — user-op qubit params are strict (no broadcast).</summary>
public sealed record QParam(string Name, QType Type, int? RegisterSize) : IParamSpec
{
    /// <summary>Stable node identity (see <see cref="QNodeIds"/>): fresh at <c>new</c>, inherited by <c>with</c>.</summary>
    public int Id { get; init; } = QNodeIds.Next();

    /// <summary>
    /// A generic register <c>Qubit[n] name</c>: the symbolic size symbol (e.g. <c>"n"</c>), bound to a
    /// concrete size per call site by <see cref="Passes.Monomorphizer"/>. Null for concrete/non-register
    /// params; while non-null, <see cref="RegisterSize"/> stays null until specialization fills it in.
    /// </summary>
    public string? SizeParam { get; init; }

    /// <summary>A user-operation qubit parameter matches its declared shape exactly — no register broadcast.</summary>
    public bool QubitBroadcast => false;

    /// <summary>Span of the parameter's NAME token.</summary>
    public QSpan? Span { get; init; }
}

// ---- statements ----

public abstract record QStmt
{
    /// <summary>Stable node identity (see <see cref="QNodeIds"/>): fresh at <c>new</c>, inherited by <c>with</c>.</summary>
    public int Id { get; init; } = QNodeIds.Next();

    /// <summary>Span of the whole statement in the entry document (see <see cref="QSpan"/>).</summary>
    public QSpan? Span { get; init; }
}

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

/// <summary>One parameter slot of a built-in gate, derived from its <see cref="GateInfo"/>. A rotation's
/// leading angle is a value slot (<see cref="QType.Angle"/>); every qubit slot broadcasts (a whole register
/// applies the gate element-wise), so <see cref="QubitBroadcast"/> is true.</summary>
public sealed record GateParam(string Name, QType Type, bool QubitBroadcast = false) : IParamSpec
{
    public int? RegisterSize => null;
    public string? SizeParam => null;
}

/// <summary>A built-in gate's signature (see <see cref="QoraGates.SigOf"/>).</summary>
public sealed record GateSig(string CalleeName, IReadOnlyList<IParamSpec> Params) : ICallableSig
{
    public bool IsBuiltin => true;
}

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

    /// <summary>
    /// The call signature of a built-in gate, derived from its <see cref="GateInfo"/>: a rotation exposes a
    /// leading angle value slot then broadcasting qubit slots, a plain gate exposes only qubit slots.
    /// <paramref name="extraControls"/> adds that many leading qubit slots for a <c>Controlled</c> functor.
    /// Returns null for an unknown gate name. This is what makes a built-in an <see cref="ICallableSig"/>,
    /// so the one call-site check treats it exactly like a user operation.
    /// </summary>
    public static ICallableSig? SigOf(string name, int extraControls = 0)
    {
        if (!Gates.TryGetValue(name, out var g)) return null;
        var slots = new List<IParamSpec>();
        var qubitSlots = g.Arity + extraControls - (g.AngleFirst ? 1 : 0);
        if (g.AngleFirst) slots.Add(new GateParam("angle", QType.Angle));
        for (var i = 0; i < qubitSlots; i++) slots.Add(new GateParam($"q{i}", QType.Qubit, QubitBroadcast: true));
        return new GateSig(name, slots);
    }

    /// <summary>Qora gate name → OpenQASM gate name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Names =
        Gates.ToDictionary(kv => kv.Key, kv => kv.Value.QasmName);

    /// <summary>Rotation gates take an angle first: <c>Rx(θ, q)</c> → <c>rx(θ) q;</c>.</summary>
    public static readonly IReadOnlySet<string> Rotations =
        Gates.Where(kv => kv.Value.AngleFirst).Select(kv => kv.Key).ToHashSet();

    /// <summary>Non-unitary built-ins (reset-like): no functors, never invertible.</summary>
    public static readonly IReadOnlySet<string> NonUnitary =
        Gates.Where(kv => !kv.Value.Unitary).Select(kv => kv.Key).ToHashSet();

    /// <summary>
    /// The one registered measurement function. Only a lone <c>M(q[i])</c> is a legal value
    /// (<c>bit r = M(q[i]);</c>); no alias is accepted.
    /// </summary>
    public const string Measurement = "M";

    /// <summary>
    /// The namespace the built-in gates live in, implicitly opened everywhere (Q#'s
    /// <c>Microsoft.Quantum.Intrinsic</c> analogue). <c>Qora.Intrinsic.H(q)</c> names the built-in
    /// explicitly — the escape hatch when a user operation shadows a gate name. Declaring this
    /// namespace is an error; the resolver rewrites qualified intrinsic calls back to the bare name.
    /// </summary>
    public const string IntrinsicNamespace = "Qora.Intrinsic";

    /// <summary>
    /// Names that read as measurement attempts. NOT legal — used only to classify errors, so a user who
    /// writes <c>Measure(q[0]);</c> gets a measurement-specific message instead of "unknown gate".
    /// </summary>
    public static readonly IReadOnlySet<string> MeasureLike = new HashSet<string> { "M", "Measure", "measure" };

    /// <summary>
    /// OpenQASM 3 keywords, types, and built-in constants — lexically reserved, so NO Qora declaration
    /// may use them anywhere (a local named <c>pi</c> or <c>def</c> cannot even parse in QASM).
    /// </summary>
    public static readonly IReadOnlySet<string> QasmKeywords = new HashSet<string>
    {
        "OPENQASM", "include", "def", "gate", "qubit", "bit", "int", "uint", "float", "angle", "bool",
        "complex", "array", "duration", "stretch", "let", "const", "measure", "reset", "barrier",
        "delay", "if", "else", "for", "while", "in", "return", "break", "continue", "end", "input",
        "output", "extern", "box", "ctrl", "negctrl", "inv", "pow", "im", "true", "false", "pi",
        "euler", "tau", "defcal", "defcalgrammar", "cal", "durationof", "sizeof", "U", "gphase",
    };

    /// <summary>
    /// Gate names <c>stdgates.inc</c> defines (plus — derived — whatever a <see cref="Gates"/> entry
    /// lowers to). These live in the QASM GLOBAL scope, so only Qora declarations that land there
    /// (operation names, the entry op's registers/top-level variables) may not collide; def-local
    /// parameters/variables/loop variables may legally shadow them (OpenQASM scoping rules).
    /// </summary>
    public static readonly IReadOnlySet<string> StdgatesNames = new HashSet<string>
    {
        "p", "x", "y", "z", "h", "s", "sdg", "t", "tdg", "sx", "rx", "ry", "rz", "cx", "cy", "cz",
        "cp", "crx", "cry", "crz", "ch", "swap", "ccx", "cswap", "cu", "CX", "phase", "cphase", "id",
        "u1", "u2", "u3",
    }.Concat(Gates.Values.Select(g => g.QasmName)).ToHashSet();

    /// <summary>Everything above combined — names that can never be a global identifier.</summary>
    public static readonly IReadOnlySet<string> QasmReserved =
        QasmKeywords.Concat(StdgatesNames).ToHashSet();
}
