namespace Qora.Ir;

/// <summary>The compiler's version, stamped into emitted QASM for provenance.</summary>
public static class QoraVersion
{
    public const string Value = "0.27";
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
///   <item>Expressions and conditions are parsed ONCE at lowering into <see cref="QNode"/> trees — the
///         single stored representation. Any spelling a consumer needs (diagnostics, the IR printer, QASM
///         emission) is rendered from the tree on demand (<see cref="QNodes.Render"/> / the emitter's own
///         renderer), so no second text ledger exists to fall out of sync.</item>
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
    /// True for a <c>function</c> (classical, pure, value-returning) rather than an <c>operation</c>
    /// (quantum, void). A function takes only classical parameters, its body applies no gates / no
    /// <c>use</c> / no measurement / no operation calls, and it <c>return</c>s a value of
    /// <see cref="ReturnType"/>. Because it is pure, a function CALL is a legal value anywhere in an
    /// expression (unlike an operation, whose call is statement-only). Emitted as an OpenQASM
    /// <c>def Name(...) -&gt; T { … }</c>.
    /// </summary>
    public bool IsFunction { get; init; }

    /// <summary>The value type a <c>function</c> returns (<see cref="IsFunction"/> is true); null for an
    /// <c>operation</c> (void). A scalar classical type only — <c>int</c>/<c>bit</c>/<c>float</c>/<c>angle</c>.</summary>
    public QType? ReturnType { get; init; }

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
/// <see cref="RegisterSize"/> / <see cref="IsQubitArray"/> / <see cref="QubitBroadcast"/>); any other
/// <see cref="Type"/> is a VALUE slot expecting a classical of that type (an angle for a rotation, an
/// int/bit/float for a classical parameter). A qubit passed to a value slot — or a value to a qubit slot —
/// is QSEM006.
/// </summary>
public interface IParamSpec
{
    string Name { get; }
    QType Type { get; }
    int? RegisterSize { get; }   // qubit-array slot: concrete size after specialization; null before it
    bool IsArray { get; }        // source shape: T[] rather than one scalar value
    bool IsQubitArray { get; }   // source shape: Qubit[] rather than one Qubit
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

/// <summary>A def parameter: one qubit, a qubit array, or a classical scalar/array value.
/// It IS its own signature slot (<see cref="IParamSpec"/>) — user-op qubit params are strict (no broadcast).</summary>
public sealed record QParam(string Name, QType Type, int? RegisterSize) : IParamSpec
{
    /// <summary>Stable node identity (see <see cref="QNodeIds"/>): fresh at <c>new</c>, inherited by <c>with</c>.</summary>
    public int Id { get; init; } = QNodeIds.Next();

    /// <summary>
    /// True for any source <c>T[]</c> parameter. A source <c>Qubit[]</c> — and a <c>bit[]</c>, whose only
    /// legal OpenQASM parameter form is the sized register <c>bit[N]</c> — deliberately carries no length;
    /// <see cref="Passes.Monomorphizer"/> fills <see cref="RegisterSize"/> only on the hidden per-call-size
    /// copy used by the OpenQASM backend. A concrete register size also implies array shape for hand-built IR.
    /// </summary>
    public bool IsArray { get; init; } = RegisterSize is not null;

    /// <summary>Convenience view used by quantum passes; classical arrays keep this false.</summary>
    public bool IsQubitArray => Type == QType.Qubit && IsArray;

    /// <summary>
    /// THE single definition of "monomorphization supplies this parameter's length": an unsized
    /// <c>Qubit[]</c> (OpenQASM registers need concrete widths) or a <c>bit[]</c> (its only legal QASM
    /// parameter form is the sized register <c>bit[N]</c>). Everyone who needs the answer ASKS here —
    /// the monomorphizer's specialization trigger, the validator's generic-op test, and (stamped onto the
    /// symbol as <c>Symbol.MonoSized</c>) the bounds prover's deferral gates — so the set can never drift
    /// apart across sites again. <c>int[]</c>/<c>float[]</c>/<c>angle[]</c> are NEVER specialized: their
    /// length-generic array form is legal QASM, so a deferred fact about them would have no re-check.
    ///
    /// The SET's membership is TARGET POLICY, not a language truth — full QIR would specialize nothing
    /// (dynamic arrays, <c>i1</c> arrays legal), hardware QIR profiles roughly the Qubit[] half. With one
    /// backend this encodes the OpenQASM requirements; a second backend parameterizes THIS one property
    /// (the recorded multi-backend seam, alongside "does the mono/re-validate sandwich run at all") —
    /// having exactly one place to parameterize is why the definition was centralized.
    /// </summary>
    public bool NeedsMonoSizing => (IsQubitArray || Type == QType.Bit && IsArray) && RegisterSize is null;

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
public sealed record QGate(IReadOnlyList<string> Functors, string Name, IReadOnlyList<QArg> Args) : QStmt
{
    /// <summary>For a user-operation CALL, the stable node Id of the callee operation — bound ONCE at name
    /// resolution (<see cref="Passes.Resolver"/>) and re-pointed when the call is rewritten (monomorphization
    /// aims it at the size specialization). Null for a built-in gate (X, CNOT, …), which resolves to no
    /// operation, so <c>CalleeOpId is int</c> ⟺ "this gate is a user-op call". Consumers that need the callee
    /// FOLLOW this reference instead of re-matching <see cref="Name"/>, which is domain-dependent (a generic
    /// call is <c>Loop</c> pre-mono, <c>Loop__sz2</c> post-mono, a third form post-mangle). Cleared to null by
    /// <see cref="Passes.AdjointMaterializer"/> when it rewrites <c>Adjoint Foo</c> to a <c>Foo__adj</c> call:
    /// the synthesized inverse's Id is not yet minted at that point, and no post-adjoint consumer needs the
    /// reference (analysis runs earlier), so null (= "unbound, resolve by name") is honest, never a stale Id.</summary>
    public int? CalleeOpId { get; init; }
}

/// <summary><c>const n: int = e;</c> / <c>var n: int = e;</c> / <c>var r: bit = M(q);</c> (measurement when Value is <see cref="QMeasure"/>).</summary>
public sealed record QDecl(bool IsConst, QType? Type, string Name, QExpr Value) : QStmt
{
    /// <summary>True when <see cref="Type"/> is the element type of a source <c>T[]</c>.</summary>
    public bool IsArray { get; init; }
}

/// <summary><c>name = e;</c>, or <c>name[index] = e;</c> when <see cref="Index"/> is present.
/// The index is a grammar-atomic token — a <see cref="QNumLit"/> or <see cref="QNameRef"/>.</summary>
public sealed record QAssign(string Name, QExpr Value) : QStmt
{
    public QNode? Index { get; init; }
}

public sealed record QIf(QCond Cond, IReadOnlyList<QStmt> Then, IReadOnlyList<QStmt> Else) : QStmt;

/// <summary><c>return e;</c> — only inside a <c>function</c>; <see cref="Value"/> is the returned expression,
/// whose type must match the function's declared <see cref="QOperation.ReturnType"/>. Emits <c>return …;</c>.</summary>
public sealed record QReturn(QExpr Value) : QStmt;

/// <summary>
/// <c>break;</c> — leave the innermost enclosing loop. NOT a Qora statement: there is no source syntax for
/// it, and no front-end pass ever sees one. It is minted only by <see cref="Passes.ReturnFlattening"/>, the
/// first step of the OpenQASM backend, to stop a loop whose body has already produced the function's result.
/// Deliberately kept out of the language: every statement kind is one more shape each pass must handle, and
/// this one needs to travel through five backend passes only, none of which read or rename anything in it.
/// </summary>
public sealed record QBreak : QStmt;

/// <summary>
/// <c>for Var in From..To { Body }</c>. Bounds are expression TREES (<c>0</c>, <c>values.Count - 1</c>),
/// parsed once at lowering; the bounds prover folds them and every consumer renders its own spelling.
/// <see cref="Step"/> has no surface syntax: the surface form always steps by +1 (null); the inversion
/// pass sets <c>-1</c> to run a loop backwards, which emits OpenQASM's <c>[From:Step:To]</c> range.
/// </summary>
public sealed record QFor(string Var, QNode From, QNode To, IReadOnlyList<QStmt> Body, QNode? Step = null) : QStmt;

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

/// <summary>
/// A syntactically indexed argument <c>name[Index]</c>. It usually denotes a qubit; after arrays were
/// added it may also denote a classical array element in a classical parameter slot. Validation resolves
/// the base symbol before quantum analyses run. The index is grammar-atomic — a <see cref="QNumLit"/> or
/// <see cref="QNameRef"/>, settled ONCE here so no consumer re-derives number-vs-name from a spelling.
/// </summary>
public sealed record QQubitArg(string Reg, QNode Index) : QArg
{
    /// <summary>Construction convenience (tests, hand-built IR): the token is atomized immediately —
    /// nothing stores the string.</summary>
    public QQubitArg(string reg, string index) : this(reg, ExprTree.Atom(index)) { }
}

/// <summary>
/// A non-qubit argument as its expression tree: a whole register (<see cref="QNameRef"/> <c>q</c>) or an
/// angle expression (<c>pi / 2</c>). <see cref="HasCall"/> derives from the tree — a call inside an
/// argument (e.g. <c>M(q[0])</c>) cannot lower to OpenQASM, so the validator rejects it before emission.
/// Null tree = synthesized empty argument.
/// </summary>
public sealed record QTextArg(QNode? Tree = null) : QArg
{
    public bool HasCall => QNodes.ContainsCall(Tree);
}

// ---- expressions / conditions (parsed trees) ----

public abstract record QExpr;

/// <summary>
/// A measurement as a whole initializer/RHS — the one legal call-in-expression form
/// (<c>var r: bit = M(q[i]);</c> or <c>var r: bit = M(a);</c> on a single-qubit parameter).
/// <para><see cref="Target"/> is the measured QUBIT REFERENCE in the IR's ONE canonical reference form:
/// a <see cref="QNameRef"/> for a whole single qubit (<c>M(a)</c>) or a <see cref="QIndexNode"/> for a
/// register element (<c>M(q[i])</c>). It is NOT nullable: a measurement without a qubit is not a thing,
/// so the state is unrepresentable rather than guarded for at every consumer (an earlier nullable target
/// let <c>M(a)</c> lower to "no target" and emit the operand-less <c>measure;</c> with no diagnostic).
/// Lowering only builds a QMeasure when the argument IS one of those two reference shapes; anything else
/// stays a call for the validator to reject.</para>
/// </summary>
public sealed record QMeasure(QNode Target) : QExpr;

/// <summary>
/// Any other expression, as its parsed tree (e.g. <c>pi / 4</c>, <c>count + 1</c>), built once at
/// lowering; the folder folds it and the emitter renders it — nothing re-parses a spelling.
/// <see cref="HasCall"/> derives from the tree: a call mixed into an expression (or a non-<c>M</c> call)
/// is unexpressible in OpenQASM, rejected by the validator. Null tree = empty initializer.
/// </summary>
public sealed record QText(QNode? Tree = null) : QExpr
{
    public bool HasCall => QNodes.ContainsCall(Tree);
}

/// <summary>A source array initializer such as <c>[1, 2, 3]</c>.</summary>
public sealed record QArrayLiteral(IReadOnlyList<QExpr> Elements) : QExpr;

/// <summary>A zero-initialized source array allocation such as <c>new float[4]</c>.</summary>
public sealed record QArrayNew(QType ElementType, int Length) : QExpr;

/// <summary>
/// A boolean condition, as its parsed tree (e.g. <c>r == 1</c>), built once at lowering.
/// <see cref="HasCall"/> derives from the tree: a call left in a condition (a non-measurement one —
/// <c>M(q[i])</c> is desugared to a bit by <see cref="Passes.MeasureConditionLowering"/> first) has no
/// OpenQASM form and is rejected by the validator. Null tree = an empty (missing) condition.
/// </summary>
public sealed record QCond(QNode? Tree = null)
{
    public bool HasCall => QNodes.ContainsCall(Tree);
}

// ---- expression tree (QNode) ----
//
// The engine's grammar gives an `Expr` as a FLAT token run (`a . Count - 1` -> [a, ., Count, -, 1]); only
// IndexAccess and Call are grouped nonterminals. Historically lowering re-flattened that to a string and
// every consumer (the bounds folder, the guard parser, the `.Count`/index regexes, the emitter) re-derived
// structure from the text. QNode is that structure recovered ONCE, at lowering, by a standard-precedence
// parse (`* /` above `+ -`, matching how OpenQASM re-evaluates the emitted tokens). Downstream reads the
// tree instead of re-parsing text — so no two readings of one expression can disagree, and a shadowed name
// or an arithmetic index is a node to resolve, never a string to pattern-match.

/// <summary>One node of a parsed expression or condition.</summary>
public abstract record QNode;

/// <summary>An integer literal (<c>5</c>, <c>0</c>) — the only literal the bounds folder evaluates.</summary>
public sealed record QNumLit(long Value) : QNode;

/// <summary>A non-integer literal or built-in constant kept verbatim: a float (<c>0.5</c>), a rotation
/// constant (<c>pi</c>, <c>tau</c>, <c>euler</c>), or a boolean (<c>true</c>, <c>false</c>). Not foldable
/// to an integer index/bound; carried for emission and type diagnostics.</summary>
public sealed record QLit(string Text) : QNode;

/// <summary>A bare identifier — a const, var, loop variable, register, or parameter. Resolved to a
/// <c>Symbol</c> at the USE site by the reader, so shadowing is settled by identity, not spelling.</summary>
public sealed record QNameRef(string Name) : QNode;

/// <summary>A unary operator: <c>-</c> (negation) or <c>!</c> (logical not).</summary>
public sealed record QUnary(string Op, QNode Operand) : QNode;

/// <summary>A binary operator — arithmetic (<c>+ - * /</c>), comparison (<c>== != &lt; &lt;= &gt; &gt;=</c>),
/// or boolean (<c>&amp;&amp; ||</c>) — with standard precedence already applied by the parse.</summary>
public sealed record QBinOp(string Op, QNode Left, QNode Right) : QNode;

/// <summary>A member access <c>Base.Member</c>. Only <c>.Count</c> on an array is meaningful; any other
/// member is a diagnostic the reader raises (the grammar stays general for a precise error).</summary>
public sealed record QMember(QNode Base, string Member) : QNode;

/// <summary>An indexed access <c>Base[Index]</c>. The grammar restricts <c>Index</c> to a number or bare
/// identifier today (so <c>a[k+1]</c> is a parse error), but the node admits any expression for when that
/// restriction is lifted.</summary>
public sealed record QIndexNode(QNode Base, QNode Index) : QNode;

/// <summary>A call <c>Name(Args…)</c> inside an expression. Two kinds share this node: the measurement
/// <c>M(q[i])</c> (exactly one <see cref="QIndexNode"/> argument — lowered to <see cref="QMeasure"/> when it
/// is a whole decl/assign RHS, otherwise carried here and either desugared out of a condition or rejected),
/// and a <c>function</c> call <c>Foo(a, b)</c> (a legal value — the reader resolves it to a declared
/// function and type-checks its arguments). A call to an <c>operation</c> or an unknown name left here is
/// rejected (no OpenQASM expression form). <see cref="Args"/> is empty for a zero-argument call.</summary>
public sealed record QCallNode(string Name, IReadOnlyList<QNode> Args) : QNode
{
    /// <summary>Construction convenience for the single-argument measurement form <c>M(q[i])</c>.</summary>
    public QCallNode(string name, QNode? arg) : this(name, arg is null ? System.Array.Empty<QNode>() : new[] { arg }) { }
}

// ---- the built-in gate table (single source of truth for emitter + validator + inverter) ----

/// <summary>Everything the pipeline needs to know about one built-in gate.</summary>
/// <param name="QasmName">The OpenQASM gate (or statement) this lowers to.</param>
/// <param name="Arity">Expected argument count, angle included; <c>Controlled</c> adds one on top.</param>
/// <param name="AngleFirst">Parametrized rotation form: <c>Rx(θ, q)</c> → <c>rx(θ) q;</c>.</param>
/// <param name="Unitary">False for reset-like operations — they cannot take functors or be inverted.</param>
/// <param name="Controls">How many LEADING qubit slots are controls: they steer the gate but their own
/// computational-basis value never changes (effect analysis marks them touched, not modified).</param>
/// <param name="Diagonal">Diagonal in the computational basis: targets keep their 0/1 value, only the
/// phase may change. CAUTION for later passes: phase kickback (e.g. CZ) still entangles — diagonal-only
/// contact does NOT make a qubit safely discardable.</param>
/// <param name="NonQfree">This write is NOT safely uncomputable by whole-statement adjoint injection when its
/// target is entangled — rung ③ rejects any ancilla that carries one. TWO gate families set it, for two
/// distinct reasons, both established by state-vector verification (adversarial round 5):
/// <list type="bullet">
/// <item>SUPERPOSITION — <c>H</c>, <c>Rx</c>/<c>Ry</c> at a general angle turn a basis state into a genuine
/// superposition; the ancilla then carries a fresh quantum degree of freedom the injected adjoint cannot fold
/// back once a surviving qubit has recorded it.</item>
/// <item>PHASE PERMUTATION — <c>Y</c>, <c>CY</c> permute the basis while attaching a basis-VALUE-dependent
/// phase (Y: <c>+i</c> on 0→1, <c>−i</c> on 1→0). On a definite basis value that phase is global and the
/// adjoint undoes it — but when the target is ENTANGLED the phase differs per survivor branch, becoming a
/// RELATIVE phase the documented "replace q with |0⟩" keeps and the injected adjoint strips, flipping a
/// downstream measurement (repro: <c>H(b);CNOT(b,a);Y(a);H(b);M(b)</c>). Matches Silq's exclusion of Y from
/// qfree; an earlier "broader than Silq — Y is safe" claim was a verified bug.</item>
/// </list>
/// A phase-free basis PERMUTATION — X, CNOT, CCX, SWAP — does NOT set this: its target stays a definite basis
/// value the adjoint cleanly undoes. Diagonal/phase gates (Z, S, T, CZ, Rz) never write a value (their targets
/// are Reads), so this flag is irrelevant to them and they stay uncompute-safe. Default false; a NEW gate
/// added without a deliberate value is caught by a table-invariant test, so the false default can never
/// silently green-light an un-classified non-qfree gate. (A future taint-refinement could re-admit Y/CY on
/// provably basis-constant ancestry — registered as a precision gap, validated by the fuzzer.)</param>
public sealed record GateInfo(string QasmName, int Arity, bool AngleFirst = false, bool Unitary = true,
    int Controls = 0, bool Diagonal = false, bool NonQfree = false);

/// <summary>One parameter slot of a built-in gate, derived from its <see cref="GateInfo"/>. A rotation's
/// leading angle is a value slot (<see cref="QType.Angle"/>); every qubit slot broadcasts (a whole register
/// applies the gate element-wise), so <see cref="QubitBroadcast"/> is true.</summary>
public sealed record GateParam(string Name, QType Type, bool QubitBroadcast = false) : IParamSpec
{
    public int? RegisterSize => null;
    public bool IsArray => false;
    public bool IsQubitArray => false;
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
/// <summary>
/// One built-in FUNCTION. <see cref="TakesBitRegister"/> marks a callable whose single argument is a WHOLE
/// <c>bit[]</c> register — a shape no ordinary value slot can express, precisely because a whole register is
/// not a value of any scalar type (that is what the conversion exists to bridge).
/// </summary>
public sealed record BuiltinFunction(QType Returns, bool TakesBitRegister);

public static class QoraGates
{
    public static readonly IReadOnlyDictionary<string, GateInfo> Gates = new Dictionary<string, GateInfo>
    {
        ["H"] = new("h", 1, NonQfree: true), ["X"] = new("x", 1), ["Y"] = new("y", 1, NonQfree: true),
        ["Z"] = new("z", 1, Diagonal: true),
        ["S"] = new("s", 1, Diagonal: true), ["T"] = new("t", 1, Diagonal: true),
        ["CNOT"] = new("cx", 2, Controls: 1), ["CX"] = new("cx", 2, Controls: 1),
        // CY is NOT diagonal: Y flips the target's basis value (only the control slot is value-preserving).
        ["CY"] = new("cy", 2, Controls: 1, NonQfree: true),
        ["CZ"] = new("cz", 2, Controls: 1, Diagonal: true),
        ["SWAP"] = new("swap", 2), ["CCX"] = new("ccx", 3, Controls: 2),
        ["Rx"] = new("rx", 2, AngleFirst: true, NonQfree: true),
        ["Ry"] = new("ry", 2, AngleFirst: true, NonQfree: true),
        ["Rz"] = new("rz", 2, AngleFirst: true, Diagonal: true),
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
    /// (<c>var r: bit = M(q[i]);</c>); no alias is accepted.
    /// </summary>
    public const string Measurement = "M";

    /// <summary>
    /// Qora's built-in FUNCTION registry — classical, pure, value-returning callables usable anywhere an
    /// expression is legal. THE single source of truth: to add one, add ONE entry here, and the reserved-name
    /// rule, the call check and the target lowering all follow. Kept separate from <see cref="Gates"/> because
    /// a gate is a void quantum statement while a function IS a value.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, BuiltinFunction> Functions =
        new Dictionary<string, BuiltinFunction>
        {
            [BitsAsInt] = new(QType.Int, TakesBitRegister: true),
        };

    /// <summary>
    /// The reading of a whole <c>bit[]</c> register as a number — the ONE explicit conversion, because a bit
    /// register carries no sign and so has no single numeric meaning on its own (the same pattern reads 2
    /// unsigned and −2 in two's complement). Every implicit reading is a compile error (QSEM036); this is how
    /// a program says which one it means. Lowers to OpenQASM's width-qualified unsigned cast, <c>uint[N](f)</c>.
    /// </summary>
    public const string BitsAsInt = "AsInt";

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
        "complex", "array", "duration", "stretch", "let", "const", "readonly", "mutable", "measure", "reset", "barrier",
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
