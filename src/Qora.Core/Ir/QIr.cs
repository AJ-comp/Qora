namespace Qora.Ir;

/// <summary>
/// Qora's own intermediate representation (IR): a strongly-typed, immutable tree that the compiler
/// owns end-to-end, decoupled from the Janglim parse-AST. The pipeline is
/// <c>source → Janglim AST → <see cref="QoraLowering"/> → QProgram → …passes… → <see cref="QIrEmitter"/> → OpenQASM</c>.
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

/// <summary><c>for Var in From..To { Body }</c> (bounds are literal numbers today, so kept as text).</summary>
public sealed record QFor(string Var, string From, string To, IReadOnlyList<QStmt> Body) : QStmt;

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

/// <summary>A non-qubit argument already rendered to text: a whole register <c>q</c>, or an angle like <c>pi / 2</c>.</summary>
public sealed record QTextArg(string Text) : QArg;

// ---- expressions / conditions (rendered text for now) ----

public abstract record QExpr;

/// <summary>A measurement expression <c>M(q[i])</c> — the only call-in-expression form Qora has.</summary>
public sealed record QMeasure(QQubitArg? Target) : QExpr;

/// <summary>Any other expression, kept as its rendered text (e.g. <c>pi / 4</c>, <c>0.5</c>, <c>count + 1</c>).</summary>
public sealed record QText(string Text) : QExpr;

/// <summary>A boolean condition, kept as rendered text (e.g. <c>r == 1</c>, <c>count &lt; 2</c>).</summary>
public sealed record QCond(string Text);
