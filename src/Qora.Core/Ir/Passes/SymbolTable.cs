using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Qora.Ir.Passes;

/// <summary>What a declared name IS.</summary>
public enum SymbolKind { Parameter, Register, MeasureBit, Var, Const, LoopVar, Operation }

/// <summary>One place a name is used (a gate operand, a measurement target, an angle argument, …).
/// <see cref="Order"/> is a pre-order index over the operation's statements — monotonic in program order,
/// so the LAST use of a register is its liveness "death point" in straight-line code. <see cref="NodeId"/>
/// is the using statement's stable <see cref="QStmt.Id"/>, tying the use back to the exact IR node.</summary>
public sealed record UseSite(int Order, string Detail, int NodeId);

/// <summary>One declared name and everything the compiler knows about it. <see cref="Uses"/> accumulates as
/// the table is built. This is the single per-symbol record every semantic pass reads — duplicate/shadow
/// checking (declaration), liveness (uses), constant folding (const value), effect analysis (kind/type).</summary>
public sealed class Symbol
{
    /// <summary>The name AS WRITTEN IN SOURCE — the user's own spelling, frozen at validation and correct
    /// forever for diagnostics and symbol views. This is deliberately NOT the emitted name: after
    /// <see cref="NameMangler"/> runs, the QASM name of this declaration lives in
    /// <see cref="SemanticModel.FindEmittedName"/>. Two name domains, each with exactly one home.
    /// (Only the validation-built, model-held table carries this guarantee: a table REBUILT on demand over
    /// a later tree — the emitter's model-less fallback — sees that tree's spellings as its "source".)</summary>
    public string SourceName { get; }
    public SymbolKind Kind { get; }
    public QType? Type { get; }                 // Int / Float / Angle / Bit / Qubit (null if unknown)
    public bool IsConst { get; }
    public string? ConstValue { get; }          // a const's initializer text (diagnostics); null for var/measure/register
    /// <summary>The const's value, FOLDED ONCE at its declaration — in the declaring scope, by the one
    /// shared calculator (<see cref="BoundFolder"/>) over the initializer tree — and read as plain data ever
    /// after. May be a definite number OR a symbolic <c>k·array.Count + c</c> (so <c>const hi = q.Count</c>
    /// carries the count through), or null when it does not settle (or the symbol is not a const). A const
    /// can never be reassigned (QSEM024), so this value has no time axis: true wherever the symbol is visible.</summary>
    internal Bound? FoldedBound { get; init; }
    public QSpan? DeclSpan { get; }
    public int DeclNodeId { get; }              // stable Id of the declaring node (QParam / QUse / QDecl / QFor)
    public int? RegisterSize { get; }           // concrete qubit count: `use q = Qubit[N]` or a specialized Qubit[] param
    public bool IsArray { get; }                 // source T[] shape, independent of the element type
    public bool IsQubitArray { get; }            // convenience view for quantum passes
    public int? ArrayLength { get; }             // known length of a classical array declaration
    public List<UseSite> Uses { get; } = new();

    public Symbol(string name, SymbolKind kind, QType? type = null, bool isConst = false, string? constValue = null,
        QSpan? declSpan = null, int? registerSize = null, bool isArray = false,
        int? arrayLength = null, int declNodeId = 0)
    {
        SourceName = name;
        Kind = kind;
        Type = type;
        IsConst = isConst;
        ConstValue = constValue;
        DeclSpan = declSpan;
        RegisterSize = registerSize;
        IsArray = isArray || registerSize is not null;
        IsQubitArray = type == QType.Qubit && IsArray;
        ArrayLength = type == QType.Qubit ? null : arrayLength;
        DeclNodeId = declNodeId;
    }
}

/// <summary>
/// A lexical scope IS a symbol table: a <c>name → Symbol</c> map plus a link to its enclosing scope. Each
/// block (the operation body, each <c>for</c>/<c>while</c>/<c>repeat</c> body, each <c>if</c> branch) gets
/// its own instance; child scopes link up to their parent. <see cref="Lookup"/> walks the parent chain
/// (nearest-enclosing resolution); <see cref="LookupLocal"/> checks only this scope (same-scope duplicates).
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, Symbol> _symbols = new();   // PRIVATE: the only writer is TryAdd
    public Scope? Parent { get; }
    public List<Scope> Children { get; } = new();

    public Scope(Scope? parent = null) { Parent = parent; parent?.Children.Add(this); }

    public Symbol? LookupLocal(string name) => _symbols.TryGetValue(name, out var s) ? s : null;
    public Symbol? Lookup(string name) => LookupLocal(name) ?? Parent?.Lookup(name);

    /// <summary>This scope's OWN symbols (not descendants') — read-only, for diagnostics/rendering.</summary>
    public IEnumerable<Symbol> LocalSymbols => _symbols.Values;

    /// <summary>This scope's symbols plus every descendant's — the flat set of all names declared at or below here.</summary>
    public IEnumerable<Symbol> AllSymbols() => _symbols.Values.Concat(Children.SelectMany(c => c.AllSymbols()));

    /// <summary>The ONE way to add a symbol — the backing dictionary is private, so no code path can bypass
    /// this. Returns false if a same-name symbol already exists in THIS scope; the caller turns that into
    /// QSEM015. Centralizing insertion here means every declaration (params, registers, measure bits, vars,
    /// consts, loop vars) is duplicate-checked by the same rule — no direct write can slip a collision past.</summary>
    public bool TryAdd(Symbol sym) => _symbols.TryAdd(sym.SourceName, sym);
}

/// <summary>
/// Builds the per-operation scope tree (the unified symbol table) and, while building, reports the
/// declaration collisions the emitted OpenQASM cannot tolerate (QSEM015). One traversal produces
/// everything: the scope tree, each symbol's kind/type/const value, and each symbol's use sites.
///
/// Scope shape: <c>use</c> registers + parameters seed the ROOT; measure bits, ordinary classical
/// declarations and loop variables are block-scoped (declared in program order during the walk). A
/// <c>for</c> is two scopes — the loop variable's scope, then the body as its child (so the body may shadow
/// the loop variable). Same-scope re-declaration is an error (QSEM015); nested shadowing is allowed
/// (C++/Q#/Silq-style — only a collision within the SAME scope is rejected). One exception ties to
/// emission: a measure bit is block-scoped for VISIBILITY, but its declaration is HOISTED to a flat
/// top-level <c>bit r;</c> when emitting OpenQASM, so it may not shadow an enclosing register / parameter /
/// measure bit (those hoist to the same scope) even though it may shadow a block-local classical.
/// </summary>
public static class SymbolTableBuilder
{
    private static readonly Regex MemberAccessPattern = new(
        @"\b(?<base>[_a-zA-Z][_a-zA-Z0-9]*)\s*\.\s*(?<member>[_a-zA-Z][_a-zA-Z0-9]*)\b",
        RegexOptions.Compiled);

    /// <summary>Build the PROGRAM scope: the top-level symbol table whose entries are the operation symbols
    /// (one per op, kind <see cref="SymbolKind.Operation"/>, keyed by the op's declaring node Id). They go
    /// in through <see cref="Scope.TryAdd"/> — the SAME single insertion door every parameter, register and
    /// variable uses — so an operation is a symbol exactly like any other name, just at the program layer.
    /// A duplicate op name (already reported QSEM008/QSEM022) simply loses the TryAdd; that program does not
    /// emit anyway. Each operation's <see cref="Build"/> scope is a CHILD of this one, making it the true
    /// root of the whole symbol table: a name resolves up the chain to an operation, and Record rejects that
    /// as a value (QSEM028) — an operation may only be called, never used in an expression.</summary>
    public static Scope BuildProgramScope(QProgram program)
    {
        var scope = new Scope();
        foreach (var op in program.Operations)
            scope.TryAdd(new Symbol(op.Name, SymbolKind.Operation, declSpan: op.Span, declNodeId: op.Id));
        return scope;
    }

    public static Scope Build(QOperation op, List<QoraError> errors,
        Dictionary<IReadOnlyList<QStmt>, Scope>? scopeIndex = null,
        Scope? programScope = null)
    {
        var opName = op.DisplayName ?? op.Name;
        var root = new Scope(programScope);   // the op body's table is a CHILD of the program table, so a name resolves up to an operation (rejected as a value by Record). Null programScope (standalone Build) → parentless, unchanged.
        var order = 0;
        var reported026 = new HashSet<(int, int)>();   // QSEM026 spans already flagged — one per statement span (a `for`'s From/To share a span; a condition names a qubit twice), so the diagnostic isn't duplicated

        // THE single insertion door. EVERY declared name — parameters, `use` registers, measure bits, vars,
        // consts, loop vars — is added through here, into the scope the caller chose (root for the hoisted
        // ones, the current block for the rest). Insertion goes via Scope.TryAdd whose backing dictionary is
        // private, so NO path can bypass the same-scope duplicate rule: a collision anywhere is QSEM015.
        void Declare(Scope target, Symbol sym)
        {
            if (target.TryAdd(sym)) return;
            var existing = target.LookupLocal(sym.SourceName)!;   // TryAdd failed ⇒ a same-name symbol is already here
            Add(errors, "QSEM015", existing.Kind == sym.Kind
                ? $"in `{opName}`: the {KindLabel(sym.Kind)} name `{sym.SourceName}` is declared more than once; each name must be unique within its scope"
                : $"in `{opName}`: `{sym.SourceName}` is declared as both a {KindLabel(existing.Kind)} and a {KindLabel(sym.Kind)} in the same scope; rename one", sym.DeclSpan);
        }

        // Parameters + the HOISTED `use` registers share one emitted top-level scope, so they seed the ROOT
        // — BEFORE the walk, so a register may be forward-referenced. Routing them through Declare means a
        // duplicate among them (two `use q`, or `use q` colliding with a param) is caught as QSEM015 instead
        // of silently overwriting. Measure bits are NOT here — they are block-scoped (declared in the walk).
        foreach (var p in op.Params)
            Declare(root, new Symbol(p.Name, SymbolKind.Parameter, p.Type, declSpan: p.Span,
                registerSize: p.Type == QType.Qubit ? p.RegisterSize : null,
                isArray: p.IsArray,
                declNodeId: p.Id));

        void SeedRegisters(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: Declare(root, new Symbol(u.Name, SymbolKind.Register, QType.Qubit, declSpan: u.Span,
                        registerSize: u.Size, isArray: true, declNodeId: u.Id)); break;
                    case QIf i: SeedRegisters(i.Then); SeedRegisters(i.Else); break;
                    case QFor f: SeedRegisters(f.Body); break;
                    case QWhile w: SeedRegisters(w.Body); break;
                    case QRepeat r: SeedRegisters(r.Body); break;
                    case QConjugate c: SeedRegisters(c.Within); SeedRegisters(c.Apply); break;
                }
        }
        SeedRegisters(op.Body);

        // Resolve a referenced name. Found → record a use (tagged with the using statement's node Id). Not
        // found → it is neither a hoisted name (registers/measure bits seed the root) nor an in-scope
        // classical, so it is an unknown name OR a classical used before its declaration: QSEM025.
        // Expression literals (pi/tau/euler/true/false) are legitimate non-symbols and never error.
        var currentStmtId = 0;   // set by Walk to the statement being visited, so uses carry their node Id
        var measureBits = new List<(string Name, QSpan? Span)>();   // every measure bit, for the post-walk top-level collision check
        void Record(Scope scope, string name, string detail, QSpan? span)
        {
            // pi/tau/euler/true/false mean something in an expression but are never declared, so they are
            // exempt from resolution entirely and checked before lookup.
            if (IsReservedName(name)) return;
            var sym = scope.Lookup(name);
            // An operation resolves up the scope chain but is NOT a value: it can only be called (the QGate
            // path records those uses), never referenced in an expression or used as an assignment target.
            if (sym is { Kind: SymbolKind.Operation })
                Add(errors, "QSEM028", $"in `{opName}`: `{name}` is an operation, not a value — it can only be called (`{name}(…)`)", span);
            else if (sym is not null) sym.Uses.Add(new UseSite(order, detail, currentStmtId));
            else
                Add(errors, "QSEM025", $"in `{opName}`: `{name}` is not declared in scope here — an unknown name, or a name used before its declaration", span);
        }

        // Resolve every identifier inside a TEXT expression — a condition, a range bound, a qubit index
        // (`q[i]`), an angle (`a * pi`), an initializer (`x = a + b`). Each goes through Record, so an
        // unknown / used-before-declared name in ANY expression position is caught (QSEM025), not silently
        // emitted. <paramref name="classicalOnly"/> marks a position that must hold a CLASSICAL value; a qubit
        // there is QSEM026, raised at most ONCE per expression (mirroring the validator's FirstQubitIn, so
        // `if (q == q)` reports once — not once per token). Numeric literals aren't identifiers; pi/tau/euler
        // /true/false are exempt.
        void RecordExpr(Scope scope, string? text, string detail, QSpan? span, bool classicalOnly = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            // A member access is one semantic value, not two free identifiers. `q.Count` reads classical
            // shape metadata: q is recorded as used, while Count is not resolved as a standalone variable.
            foreach (var member in MemberAccesses(text))
            {
                Record(scope, member.Base, detail, span);
                if (scope.Lookup(member.Base) is not { } owner) continue; // Record already produced QSEM025
                if (member.Member != "Count")
                    Add(errors, "QSEM029", $"in `{opName}`: `{member.Base}.{member.Member}` is not a supported member; arrays expose `.Count`", span);
                else if (!owner.IsArray)
                    Add(errors, "QSEM029", $"in `{opName}`: `{member.Base}.Count` is invalid because `{member.Base}` is not an array", span);
            }

            foreach (var name in ExpressionIdentifiers(text))
            {
                Record(scope, name, detail, span);
                // QSEM026 at most once per statement span: `reported026.Add` is false on a repeat, so
                // `if (q == q)` and a `for`'s `q..q` (From/To share the span) each report a single diagnostic.
                if (classicalOnly && scope.Lookup(name) is { Type: QType.Qubit }
                    && reported026.Add((span?.Start ?? -1, span?.End ?? -1)))
                    Add(errors, "QSEM026", $"in `{opName}`: `{name}` is a qubit and cannot be used as a classical value here — a qubit has no numeric value to compare, index, or compute with", span);
            }
        }

        // A block-local declaration of <paramref name="name"/> is being made in <paramref name="scope"/>.
        // If an ENCLOSING value of that name was already USED earlier in THIS block (a use whose program
        // order lies after the block began and before this declaration), then that use bound to the outer
        // value — but the completed scope dictionary the validator later reads would resolve the same name
        // to this later local, so the two passes would disagree (an out-of-bounds index folded to the wrong
        // value, or a duplicate qubit missed). Point-of-declaration scoping (which the emitted OpenQASM
        // follows) means a name may not be used before its declaration in its own scope: reject it here.
        bool UsedBeforeShadow(Scope scope, string name, int scopeStart) =>
            scope.Lookup(name) is { Kind: not SymbolKind.Operation } outer
            && outer.Uses.Any(u => u.Order > scopeStart && u.Order < order);

        void Walk(IReadOnlyList<QStmt> stmts, Scope scope)
        {
            scopeIndex?.TryAdd(stmts, scope);   // map each body list -> its scope, so the validator resolves names with correct nesting
            var scopeStart = order;             // program order just before this block's first statement (for UsedBeforeShadow)
            foreach (var s in stmts)
            {
                order++;
                currentStmtId = s.Id;
                switch (s)
                {
                    case QGate g:
                        // an operation CALL (not a built-in gate) records a use on the callee's operation
                        // symbol — its "used-where", accumulated across every caller. The callee is looked up
                        // in the PROGRAM scope by name (the same symbol-table lookup any name uses); a
                        // built-in gate (H, X…) is not there → skipped. Functored calls (Adjoint Foo) name Foo.
                        if (programScope?.LookupLocal(g.Name) is { Kind: SymbolKind.Operation } callee)
                            callee.Uses.Add(new UseSite(order, $"call @ {g.Name}", g.Id));
                        foreach (var a in g.Args)
                            switch (a)
                            {
                                case QQubitArg q:
                                    Record(scope, q.Reg, $"{g.Name} @ {q.Reg}[{q.Index}]", g.Span);                 // the register itself
                                    RecordExpr(scope, q.Index, $"{g.Name} index @ {q.Reg}[{q.Index}]", g.Span);     // vars inside [ i ]
                                    break;
                                case QTextArg t:
                                    RecordExpr(scope, t.Text, $"{g.Name} @ {t.Text}", g.Span);                      // whole register OR angle expr (theta, a*pi, …)
                                    break;
                            }
                        break;
                    case QDecl { Value: QMeasure } md:
                        // Record the measured target FIRST — before the bit is in scope — so `bit r = M(r[0])`
                        // resolves the target `r` to the register (chain lookup), not the bit declared here.
                        if (md.Value is QMeasure { Target: { } mt })
                        {
                            Record(scope, mt.Reg, $"measure @ {mt.Reg}[{mt.Index}]", md.Span);                      // the measured qubit COLLAPSES here
                            RecordExpr(scope, mt.Index, $"measure index @ {mt.Reg}[{mt.Index}]", md.Span);
                        }
                        // The measure bit is BLOCK-SCOPED like var/const for VISIBILITY: declared into the
                        // CURRENT scope in program order, so referencing it before this line is QSEM025.
                        // But its DECLARATION is HOISTED at emission to one flat top-level `bit r;` (OpenQASM
                        // importers reject local classical declarations), together with `use` registers and
                        // parameters. So while it may shadow a block-local classical, it must NOT reuse the
                        // name of an ENCLOSING register / parameter / measure bit — those flatten to the same
                        // emitted scope and would collide. (Same-scope dups: Declare. Disjoint sibling blocks
                        // may reuse a name — they dedup into one emitted bit and never coexist.)
                        if (scope.Parent?.Lookup(md.Name) is { Kind: SymbolKind.Register or SymbolKind.Parameter or SymbolKind.MeasureBit } encl)
                            Add(errors, "QSEM015", $"in `{opName}`: measure bit `{md.Name}` reuses the name of an enclosing {KindLabel(encl.Kind)}; a measured result is emitted as one top-level `bit {md.Name};`, so its name must be unique across the operation's registers, parameters and measure bits — rename one", md.Span);
                        if (UsedBeforeShadow(scope, md.Name, scopeStart))
                            Add(errors, "QSEM025", $"in `{opName}`: `{md.Name}` is used earlier in this block but declared here, shadowing an outer `{md.Name}` — a name cannot be used before its declaration in its own scope; move this declaration above the first use or rename it", md.Span);
                        measureBits.Add((md.Name, md.Span));   // checked against the completed root scope after the walk (top-level collision)
                        Declare(scope, new Symbol(md.Name, SymbolKind.MeasureBit, QType.Bit, isConst: md.IsConst, declSpan: md.Span, declNodeId: md.Id));
                        break;
                    case QDecl d:
                        RecordValue(scope, d.Value, $"init {d.Name}", d.Span);
                        // Point-of-declaration scoping: this name may not have been used earlier in its own
                        // block (that use would bind to the outer value, which this local shadows). The
                        // initializer's own use of the name (order == this statement) is exempt, so a const
                        // chain reading the outer — `const int n = n + 1` — is still fine.
                        if (UsedBeforeShadow(scope, d.Name, scopeStart))
                            Add(errors, "QSEM025", $"in `{opName}`: `{d.Name}` is used earlier in this block but declared here, shadowing an outer `{d.Name}` — a name cannot be used before its declaration in its own scope; move this declaration above the first use or rename it", d.Span);
                        // A const's initializer folds HERE — the owner's site, the owner's scope (earlier
                        // consts are already in scope, so chains like `const int m = k + 1` settle too).
                        // From now on the value is DATA on the symbol; no consumer re-reads the text.
                        Declare(scope, new Symbol(d.Name, d.IsConst ? SymbolKind.Const : SymbolKind.Var, d.Type,
                            d.IsConst, d.IsConst && d.Value is QText qt ? qt.Text : null, d.Span,
                            isArray: d.IsArray, arrayLength: ArrayLengthOf(d), declNodeId: d.Id)
                        {
                            FoldedBound = d is { IsConst: true, IsArray: false, Value: QText ct }
                                ? BoundFolder.Fold(ct.Tree, scope) : null,
                        });
                        break;
                    case QAssign { Value: QMeasure { Target: { } at } } ma:
                        Record(scope, ma.Name, $"assign {ma.Name}", ma.Span);
                        RecordExpr(scope, ma.Index, $"assign index {ma.Name}[{ma.Index}]", ma.Span, classicalOnly: true);
                        Record(scope, at.Reg, $"measure @ {at.Reg}[{at.Index}]", ma.Span);
                        RecordExpr(scope, at.Index, $"measure index @ {at.Reg}[{at.Index}]", ma.Span);
                        break;
                    case QAssign a:
                        Record(scope, a.Name, $"assign {a.Name}", a.Span);                                          // the target is referenced (written)
                        RecordExpr(scope, a.Index, $"assign index {a.Name}[{a.Index}]", a.Span, classicalOnly: true);
                        RecordValue(scope, a.Value, $"assign {a.Name}", a.Span);
                        break;
                    case QFor f:
                        RecordExpr(scope, f.From, $"for bound {f.From}", f.Span, classicalOnly: true);              // range bounds are classical (read in the ENCLOSING scope)…
                        RecordExpr(scope, f.To, $"for bound {f.To}", f.Span, classicalOnly: true);
                        RecordExpr(scope, f.Step, $"for step {f.Step}", f.Span, classicalOnly: true);
                        var loop = new Scope(scope);                                                        // …then the loop var gets its own scope
                        Declare(loop, new Symbol(f.Var, SymbolKind.LoopVar, QType.Int, declSpan: f.Span, declNodeId: f.Id));
                        Walk(f.Body, new Scope(loop));                                                      // body is a CHILD of the loop-var scope
                        break;
                    case QIf i:
                        var ifCond = new Scope(scope);                                                      // the condition () IS a scope (its own table)
                        RecordExpr(ifCond, i.Cond.Text, $"if ({i.Cond.Text})", i.Span, classicalOnly: true);  // a condition is classical (a bit/bool)
                        Walk(i.Then, new Scope(ifCond));                                                    // branches nest UNDER the condition scope (C++17 if-init ready)
                        Walk(i.Else, new Scope(ifCond));
                        break;
                    case QWhile w:
                        var whileCond = new Scope(scope);
                        RecordExpr(whileCond, w.Cond.Text, $"while ({w.Cond.Text})", w.Span, classicalOnly: true);
                        Walk(w.Body, new Scope(whileCond));
                        break;
                    case QRepeat r:
                        var repeatBody = new Scope(scope);
                        Walk(r.Body, repeatBody);
                        currentStmtId = r.Id;   // the nested Walk moved it; the until belongs to the repeat itself
                        RecordExpr(repeatBody, r.Until.Text, $"until ({r.Until.Text})", r.Span, classicalOnly: true);  // until runs AFTER the body, so it sees body-local names
                        break;
                    case QConjugate c:
                        Walk(c.Within, new Scope(scope));
                        Walk(c.Apply, new Scope(scope));
                        break;
                }
            }
        }

        Walk(op.Body, root);

        // QSEM015 — a measure bit HOISTS to a flat top-level `bit r;` at emission, so it shares the emitted
        // top-level namespace with root-scope classicals (const/var/array), which also emit there. A same
        // name in both is a duplicate top-level declaration OpenQASM 3 rejects — and NameMangler, keying by
        // source name, would emit both under one name rather than renaming. Checked HERE, against the
        // COMPLETED root scope, so it fires regardless of whether the classical is declared before or after
        // the measure bit's block. (Enclosing register/parameter/measure-bit collisions are caught inline
        // during the walk; a BLOCK-local classical stays in its own emitted scope and does not collide.)
        foreach (var (mbName, mbSpan) in measureBits)
            if (root.LookupLocal(mbName) is { Kind: SymbolKind.Const or SymbolKind.Var } top)
                Add(errors, "QSEM015", $"in `{opName}`: measure bit `{mbName}` reuses the name of the top-level {KindLabel(top.Kind)} `{mbName}`; a measured result hoists to `bit {mbName};` at the top level, so its name must be unique there — rename one", mbSpan);

        // QSEM013 — every declared name is checked here, so no declaration site can bypass the
        // reserved-expression-name rule.
        foreach (var sym in root.AllSymbols())
            if (IsReservedName(sym.SourceName))
                Add(errors, "QSEM013", $"in `{opName}`: {KindLabel(sym.Kind)} name `{sym.SourceName}` shadows the built-in `{sym.SourceName}`; choose another name", sym.DeclSpan);
        return root;

        void RecordValue(Scope valueScope, QExpr value, string detail, QSpan? span)
        {
            switch (value)
            {
                case QText text:
                    RecordExpr(valueScope, text.Text, detail, span, classicalOnly: true);
                    break;
                case QMeasure { Target: { } target }:
                    Record(valueScope, target.Reg, $"measure @ {target.Reg}[{target.Index}]", span);
                    RecordExpr(valueScope, target.Index, $"measure index @ {target.Reg}[{target.Index}]", span);
                    break;
                case QArrayLiteral literal:
                    foreach (var element in literal.Elements)
                        RecordValue(valueScope, element, detail, span);
                    break;
            }
        }

        static int? ArrayLengthOf(QDecl declaration) => declaration.Value switch
        {
            QArrayLiteral literal => literal.Elements.Count,
            QArrayNew allocation when allocation.Length >= 0 => allocation.Length,
            _ => null,
        };
    }

    private static void Add(List<QoraError> errors, string code, string message, QSpan? span) =>
        errors.Add(new QoraError(message, code, span?.Start ?? -1, span?.End ?? -1));

    /// <summary>The reserved identifier-form literals — built-in constants and boolean literals — that mean
    /// something in an EXPRESSION and therefore can never be a declared name (resolution exempts them, and a
    /// declaration named one of them is QSEM013). The SINGLE source of truth, used by both the resolver here
    /// and the validator's declared-name / operation-name checks.</summary>
    internal static bool IsReservedName(string name) => name is "pi" or "tau" or "euler" or "true" or "false";

    /// <summary>A human label for a symbol's kind, for diagnostics.</summary>
    private static string KindLabel(SymbolKind k) => k switch
    {
        SymbolKind.Parameter => "parameter",
        SymbolKind.Register => "register",
        SymbolKind.MeasureBit => "measure bit",
        SymbolKind.Const => "const",
        SymbolKind.LoopVar => "loop variable",
        SymbolKind.Operation => "operation",
        _ => "variable",
    };

    /// <summary>Yield each identifier token in a text expression — a run of letters/digits/underscore that
    /// starts with a letter or underscore. Numbers, operators, brackets and whitespace are skipped. Callers
    /// filter to real symbols via <see cref="Scope.Lookup"/>, so bare literals (<c>pi</c>, <c>tau</c>, numbers)
    /// that aren't declared never produce a use. Also used by the validator to find a qubit hidden inside a
    /// value-slot expression (QSEM026).</summary>
    internal readonly record struct MemberAccess(string Base, string Member, int Start, int Length);

    // MemberAccesses + ExpressionIdentifiers scan the FINAL EMITTED (post-mangling) QASM text for
    // ReferentialCheck — where the pre-mangle expression trees no longer apply (the mangler renamed the
    // text, not the trees), so a text scan of the emitted artifact is the correct tool, not a mole. The
    // pre-mangle consumers (bounds checking, guard reading, const folding) all read the QNode tree instead.
    internal static IEnumerable<MemberAccess> MemberAccesses(string text)
    {
        foreach (Match match in MemberAccessPattern.Matches(text))
            yield return new MemberAccess(
                match.Groups["base"].Value,
                match.Groups["member"].Value,
                match.Index,
                match.Length);
    }

    /// <summary>Identifiers that are ordinary values, excluding both tokens in a member access. The
    /// owner is handled once through <see cref="MemberAccesses"/> and the member is not a free name.</summary>
    internal static IEnumerable<string> ExpressionIdentifiers(string text)
    {
        var members = MemberAccesses(text).ToList();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (!members.Any(m => start >= m.Start && start < m.Start + m.Length))
                    yield return text.Substring(start, i - start);
            }
            else i++;
        }
    }

    internal static IEnumerable<string> Identifiers(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                yield return text.Substring(start, i - start);
            }
            else i++;
        }
    }

    // --- debug rendering (the --stages view) ---

    /// <summary>Render the symbol table of every operation as text (for the <c>--stages</c> debug view).
    /// Each op is shown as its own <see cref="SymbolKind.Operation"/> symbol line, then its scope tree
    /// indented beneath. With a <see cref="SemanticModel"/>, the op symbols and each op's scope tree are
    /// READ from the model (the one validation built); an op the model has not seen — e.g. a still-generic
    /// op when the model was built post-monomorphization — falls back to a rebuild. Same output either way
    /// (operation call-site uses live on the model only, so they are not part of this view).</summary>
    public static string Format(QProgram? program, SemanticModel? model = null)
    {
        if (program is null || program.Operations.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        var sink = new List<QoraError>();                 // formatting must not surface errors
        var programScope = model?.ProgramScope ?? BuildProgramScope(program);   // op symbols: from the model, or rebuilt
        foreach (var op in program.Operations)
        {
            var kind = (programScope.LookupLocal(op.Name)?.Kind ?? SymbolKind.Operation).ToString().ToLowerInvariant();
            sb.AppendLine($"{op.DisplayName ?? op.Name}: {kind}");
            var root = model?.FindRootScope(op.Id) ?? Build(op, sink);
            PrintScope(root, sb, 1);
        }
        return sb.ToString().TrimEnd();
    }

    private static void PrintScope(Scope scope, StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 2);
        foreach (var sym in scope.LocalSymbols)
        {
            var type = sym.Type is { } t ? t.ToString().ToLowerInvariant() : "?";
            var kind = sym.Kind.ToString().ToLowerInvariant();
            var val = sym.IsConst && sym.ConstValue is not null ? $" = {sym.ConstValue}" : "";
            var uses = sym.Uses.Count == 0
                ? "no uses"
                : $"uses [{string.Join(", ", sym.Uses.Select(u => u.Order))}] last @ {sym.Uses[^1].Order}";
            sb.AppendLine($"{pad}{sym.SourceName}: {kind} {type}{val}  ({uses})");
        }
        foreach (var child in scope.Children) PrintScope(child, sb, depth + 1);
    }
}
