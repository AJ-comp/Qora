using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Qora.Ir.Passes;

/// <summary>What a declared name IS.</summary>
public enum SymbolKind
{
    Namespace,
    Parameter,
    Register,
    MeasureBit,
    Var,
    Const,
    LoopVar,
    Operation,
    BuiltinGate,
    BuiltinFunction,
}

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
    private readonly List<UseSite> _uses = new();
    private IReadOnlyList<UseSite>? _sealedUses;
    private bool _isSealed;

    /// <summary>Semantic identity inside one HIR-scope-graph snapshot, distinct from a QNode Id.</summary>
    public SymbolId Id { get; } = SemanticIds.NextSymbol();

    /// <summary>
    /// The one authoritative membership edge: the exact HIR scope in which this symbol is declared.
    /// Namespace/callable ownership and a local's enclosing callable are derived from
    /// <see cref="HirScopeGraph"/>, not copied onto every symbol.
    /// </summary>
    public ScopeId DeclaringScopeId { get; }

    public SymbolOrigin Origin { get; }

    /// <summary>The name as written in source, frozen at validation. Emitted names belong to the selected
    /// target artifact's symbol map and never mutate this HIR symbol.</summary>
    public string SourceName { get; }
    public SymbolKind Kind { get; }
    public QType? Type { get; }                 // explicit or initializer-inferred Int / Float / Angle / Bit / Qubit
    public bool IsConst { get; }
    /// <summary>
    /// Ownership/access contract of a parameter symbol. Non-parameter symbols keep the default
    /// borrowed/read-only values; callers must not infer storage ownership from them for those kinds.
    /// </summary>
    public QOwnershipMode ParameterOwnership { get; }
    public QAccessMode ParameterAccess { get; }
    public string? ConstValue { get; }          // a const's initializer text (diagnostics); null for var/measure/register
    /// <summary>The const's value, FOLDED ONCE at its declaration — in the declaring scope, by the one
    /// shared calculator (<see cref="BoundFolder"/>) over the initializer tree — and read as plain data ever
    /// after. May be a definite number OR a symbolic <c>k·array.Count + c</c> (so <c>const hi = q.Count</c>
    /// carries the count through), or null when it does not settle (or the symbol is not a const). A const
    /// can never be reassigned (QSEM024), so this value has no time axis: true wherever the symbol is visible.</summary>
    internal Bound? FoldedBound { get; init; }
    /// <summary>The const's compile-time boolean value, folded once at its declaration. This is kept
    /// separately from <see cref="FoldedBound"/> because a bit-valued comparison is not an integer bound,
    /// while control-flow analysis still needs to know that an impossible branch cannot consume ownership.</summary>
    internal bool? FoldedBoolean { get; init; }
    /// <summary>The <see cref="QParam.NeedsMonoSizing"/> answer, stamped ONCE at declaration — true only
    /// for a parameter whose length monomorphization will supply (unsized <c>Qubit[]</c> / <c>bit[]</c>).
    /// The bounds prover's deferral gates read this stamp instead of re-deriving the set, so they can
    /// never drift from the monomorphizer's own trigger.</summary>
    internal bool MonoSized { get; init; }
    public SourceSpan? DeclSpan { get; }
    /// <summary>The declaring QNode Id, or null for merged/synthetic/built-in symbols.</summary>
    public int? DeclarationNodeId { get; }

    public int? RegisterSize { get; }           // concrete qubit count: `use q = Qubit[N]` or a specialized Qubit[] param
    public bool IsArray { get; }                 // source T[] shape, independent of the element type
    public bool IsQubitArray { get; }            // convenience view for quantum passes
    public int? ArrayLength { get; }             // known length of a classical array declaration
    /// <summary>
    /// Use sites accumulated by the symbol-table builder. The public view is a defensive immutable
    /// snapshot; only the builder can append through <see cref="AddUse"/>.
    /// </summary>
    public IReadOnlyList<UseSite> Uses =>
        _sealedUses ?? HirCollections.Freeze(_uses);

    internal void AddUse(UseSite use)
    {
        if (_isSealed)
            throw new InvalidOperationException(
                "QINTERNAL: semantic symbol is sealed by an immutable HIR scope graph");
        _uses.Add(use);
    }

    internal void Seal()
    {
        if (_isSealed) return;
        _sealedUses = HirCollections.Freeze(_uses);
        _isSealed = true;
    }

    public Symbol(ScopeId declaringScopeId, string name, SymbolKind kind, QType? type = null,
        bool isConst = false, string? constValue = null,
        SourceSpan? declSpan = null, int? registerSize = null, bool isArray = false,
        int? arrayLength = null, int? declarationNodeId = null,
        SymbolOrigin origin = SymbolOrigin.Source,
        QOwnershipMode parameterOwnership = QOwnershipMode.Borrowed,
        QAccessMode parameterAccess = QAccessMode.ReadOnly)
    {
        DeclaringScopeId = declaringScopeId;
        SourceName = name;
        Kind = kind;
        Origin = origin;
        Type = type;
        IsConst = isConst;
        ParameterOwnership = parameterOwnership;
        ParameterAccess = parameterAccess;
        ConstValue = constValue;
        DeclSpan = declSpan;
        RegisterSize = registerSize;
        IsArray = isArray || registerSize is not null;
        IsQubitArray = type == QType.Qubit && IsArray;
        ArrayLength = type == QType.Qubit ? null : arrayLength;
        DeclarationNodeId = declarationNodeId;
    }
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
internal static class SymbolTableBuilder
{

    /// <summary>
    /// Build the unified HIR scope graph. A dotted namespace creates one namespace declaration and member
    /// scope per segment; callable body scopes are direct children of their declaring namespace (or the
    /// program scope). Repeated namespace blocks reuse the same scope. Built-in gates and functions are
    /// members of <c>Qora.Intrinsic</c>. <see cref="Build"/> later fills the already-created callable scopes
    /// with parameters, locals, and lexical descendants.
    /// </summary>
    public static HirScopeGraph BuildHirScopeGraph(QProgram program)
    {
        var graph = new HirScopeGraph();
        var namespacePaths = program.Operations
            .Select(operation => operation.Namespace)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (program.Opens is not null)
            foreach (var namespacePath in program.Opens.Keys)
                if (namespacePath.Length > 0)
                    namespacePaths.Add(namespacePath);

        foreach (var namespacePath in namespacePaths
                     .OrderBy(path => path.Count(character => character == '.'))
                     .ThenBy(path => path, StringComparer.Ordinal))
            graph.GetOrAddNamespace(namespacePath);

        foreach (var op in program.Operations)
        {
            var owner = op.Namespace.Length == 0
                ? graph.RootScope
                : graph.GetOrAddNamespace(op.Namespace);
            var callable = new Symbol(
                owner.Id,
                LocalCallableName(op),
                SymbolKind.Operation,
                declSpan: op.Span,
                declarationNodeId: op.Id);
            graph.RegisterDeclaredMember(callable);
            var callableScope = graph.CreateScope(
                HirScopeKind.Callable,
                owner.Id,
                callable.Id,
                new HirScopeSite(op.Id, HirScopeSiteRole.CallableBody));
            graph.RegisterCallableScope(op.Id, callableScope);
        }

        var intrinsic = graph.GetOrAddNamespace(
            QoraGates.IntrinsicNamespace,
            SymbolOrigin.Builtin);
        foreach (var name in QoraGates.Names.Keys)
            graph.RegisterDeclaredMember(new Symbol(
                intrinsic.Id,
                name,
                SymbolKind.BuiltinGate,
                origin: SymbolOrigin.Builtin));
        foreach (var name in QoraGates.Functions.Keys)
            graph.RegisterDeclaredMember(new Symbol(
                intrinsic.Id,
                name,
                SymbolKind.BuiltinFunction,
                origin: SymbolOrigin.Builtin));

        // `open` is a lookup route from the exact declaring namespace, not containment and not a
        // transitive import. Unknown targets remain absent here and are diagnosed by Resolver.
        if (program.Opens is not null)
            foreach (var (ownerPath, opens) in program.Opens)
            {
                var owner = graph.FindNamespaceScope(ownerPath);
                if (owner is null) continue;
                foreach (var open in opens)
                    if (graph.FindNamespaceScope(open.Target) is { } target)
                        graph.AddLookupEdge(owner.Id, target.Id, HirScopeEdgeKind.Import);
            }

        return graph;
    }

    public static Scope Build(QOperation op, List<QoraError> errors,
        HirScopeGraph? scopeGraph = null,
        IReadOnlyDictionary<int, QOperation>? opById = null,
        Action<int, QoraError>? typeMismatchCandidate = null,
        Action<int, QoraError>? statementError = null)
    {
        var opName = op.DisplayName ?? op.Name;
        scopeGraph ??= BuildHirScopeGraph(new QProgram(new[] { op }));
        opById ??= new Dictionary<int, QOperation>
        {
            [op.Id] = op,
        };
        var callable = scopeGraph.FindDeclaration(op.Id)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: operation `{opName}` has no HIR symbol");
        var root = scopeGraph.FindCallableScope(op.Id)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: operation `{opName}` has no callable scope");
        var callableNamespace = scopeGraph.FindDeclaringNamespace(callable)
            ?? scopeGraph.RootScope;
        var order = 0;
        var reported026 = new HashSet<SourceSpan?>();   // one diagnostic per document-qualified statement span

        bool ContainsFunctionCall(QExpr value) =>
            QNodes.TreesOf(value).SelectMany(QNodes.CallsIn).Any(call =>
                QoraGates.Functions.ContainsKey(call.Name)
                || ExpressionTypes.TryGetFunction(call, opById, out _));

        // QSEM037 belongs to the contracts introduced by functions: every return value, and a declared or
        // assigned scalar that consumes a function result. Ordinary scalar declarations keep Qora's
        // historical loose model. Shape is never loose: a whole T[] cannot silently become one T value.
        void CheckAssignable(int statementId, QValueShape target, QExpr value, Scope valueScope, SourceSpan? span,
            string targetDescription, bool isReturn = false)
        {
            if (ExpressionTypes.TypeOf(value, valueScope, opById) is not { } source)
                return;   // an existing unknown/call/qubit diagnostic owns the malformed expression
            if (source.Type == QType.Qubit)
                return;   // QSEM026 already explains why a qubit is not a classical value
            if (source is { Type: QType.Bit, IsArray: true } && !target.IsArray)
                return;   // QSEM036 gives the precise AsInt guidance

            var shapeMismatch = target.IsArray != source.IsArray;
            var functionContract = isReturn || ContainsFunctionCall(value);
            if ((!shapeMismatch && !functionContract) || ExpressionTypes.CanAssign(target, source, value))
                return;

            var reason = shapeMismatch
                ? "a whole array and one scalar value cannot be assigned to each other"
                : $"`{source}` cannot be implicitly converted to `{target}`";
            var error = new QoraError(
                $"in `{opName}`: {targetDescription} expects `{target}`, but the value is `{source}`; {reason}",
                "QSEM037",
                span);
            // Validation supplies a candidate sink so a more fundamental call-signature error can own this
            // statement. Standalone table builds have no later call-validation phase, so they receive the
            // mismatch immediately as before.
            if (typeMismatchCandidate is null) errors.Add(error);
            else typeMismatchCandidate(statementId, error);
        }

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
            Declare(root, new Symbol(root.Id, p.Name, SymbolKind.Parameter, p.Type, declSpan: p.Span,
                registerSize: p.Type == QType.Qubit ? p.RegisterSize : null,
                isArray: p.IsArray,
                // a CLASSICAL array parameter's RegisterSize is its specialized length (bit[] gets one from
                // monomorphization, like Qubit[]) — exposing it as ArrayLength gives the post-mono bounds
                // proofs the same precision they have for sized registers; null pre-mono = P4 floors as ever.
                arrayLength: p.Type != QType.Qubit ? p.RegisterSize : null,
                declarationNodeId: p.Id,
                parameterOwnership: p.Ownership,
                parameterAccess: p.Access)
            {
                MonoSized = p.NeedsMonoSizing,   // the ONE answer, stamped for the deferral gates
            });

        void SeedRegisters(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: Declare(root, new Symbol(root.Id, u.Name, SymbolKind.Register, QType.Qubit,
                        declSpan: u.Span, registerSize: u.Size, isArray: true,
                        declarationNodeId: u.Id)); break;
                    case QIf i: SeedRegisters(i.Then); SeedRegisters(i.Else); break;
                    case QFor f: SeedRegisters(f.Body); break;
                    case QWhile w: SeedRegisters(w.Body); break;
                    case QRepeat r: SeedRegisters(r.Body); break;
                }
        }
        SeedRegisters(op.Body);

        // Resolve a referenced name. Found → record a use (tagged with the using statement's node Id). Not
        // found → it is neither a hoisted name (registers/measure bits seed the root) nor an in-scope
        // classical, so it is an unknown name OR a classical used before its declaration: QSEM025.
        // Expression literals (pi/tau/euler/true/false) are legitimate non-symbols and never error.
        var currentStmtId = 0;   // set by Walk to the statement being visited, so uses carry their node Id
        var measureBits = new List<(string Name, SourceSpan? Span)>();   // every measure bit, for the post-walk top-level collision check
        var reported036 = new HashSet<SourceSpan?>();   // one diagnostic per document-qualified statement span
        void AddStatementError(string code, string message, SourceSpan? span)
        {
            var error = new QoraError(message, code, span);
            errors.Add(error);
            if (currentStmtId != 0) statementError?.Invoke(currentStmtId, error);
        }

        // A WHOLE `bit[]` register, if that is what this node denotes. The discriminator is IsArray, NOT Type:
        // a scalar measure bit and a bit register are both QType.Bit, and the scalar one is untouched by the
        // register rule (OpenQASM makes scalar `bit` interchangeable with `bool`, so it IS a value).
        Symbol? WholeBitRegister(Scope scope, QNode? node) =>
            node is QNameRef r && scope.Lookup(r.Name) is { Type: QType.Bit, IsArray: true } sym ? sym : null;
        void Record(Scope scope, string name, string detail, SourceSpan? span)
        {
            // pi/tau/euler/true/false mean something in an expression but are never declared, so they are
            // exempt from resolution entirely and checked before lookup.
            if (IsReservedName(name)) return;
            var sym = scope.Lookup(name);
            var callableValue = sym is null
                ? scopeGraph.LookupCallableOutward(callableNamespace.Id, name)
                : null;
            // An operation resolves up the scope chain but is NOT a value: it can only be called (the QGate
            // path records those uses), never referenced in an expression or used as an assignment target.
            if (callableValue is { Kind: SymbolKind.Operation })
                AddStatementError("QSEM028", $"in `{opName}`: `{name}` is an operation, not a value — it can only be called (`{name}(…)`)", span);
            else if (sym is not null) sym.AddUse(new UseSite(order, detail, currentStmtId));
            else
                AddStatementError("QSEM025", $"in `{opName}`: `{name}` is not declared in scope here — an unknown name, or a name used before its declaration", span);
        }

        // Resolve every identifier inside an EXPRESSION TREE — a condition, a range bound, a qubit index
        // (`q[i]`), an angle (`a * pi`), an initializer (`x = a + b`). Each name goes through Record, so an
        // unknown / used-before-declared name in ANY expression position is caught (QSEM025), not silently
        // emitted. <paramref name="classicalOnly"/> marks a position that must hold a CLASSICAL value; a qubit
        // there is QSEM026, raised at most ONCE per expression (mirroring the validator's FirstQubitIn, so
        // `if (q == q)` reports once — not once per token). A member access is one semantic value, not two
        // free identifiers: `q.Count` records q as used, while Count is a member (QSEM029 when it is not
        // `.Count` on an array), never a standalone name — the structure says so, no after-dot heuristic.
        // Numeric/verbatim literals aren't names; pi/tau/euler/true/false are exempt (inside Record).
        void RecordExpr(Scope scope, QNode? node, string detail, SourceSpan? span, bool classicalOnly = false,
            bool registerOk = false)
        {
            switch (node)
            {
                case null or QNumLit or QLit:
                    return;
                case QNameRef r:
                    Record(scope, r.Name, detail, span);
                    // QSEM026 at most once per statement span: `reported026.Add` is false on a repeat, so
                    // `if (q == q)` and a `for`'s `q..q` (From/To share the span) each report one diagnostic.
                    if (classicalOnly && scope.Lookup(r.Name) is { Type: QType.Qubit }
                        && reported026.Add(span))
                        AddStatementError("QSEM026", $"in `{opName}`: `{r.Name}` is a qubit and cannot be used as a classical value here — a qubit has no numeric value to compare, index, or compute with", span);
                    // QSEM036 — a WHOLE bit register is a container of bits, not a number. It reached a
                    // position that reads a VALUE, and no position hands it a meaning: `registerOk` is set by
                    // the PARENT for the four places a whole register legitimately appears (index base,
                    // `.Count` base, an argument, and `==`/`!=` against another register). Everywhere else the
                    // register would have to be read as a number, and a bit pattern carries no sign — the same
                    // bits read 2 unsigned and −2 in two's complement — so the language refuses to choose.
                    if (!registerOk && WholeBitRegister(scope, r) is not null
                        && reported036.Add(span))
                        AddStatementError("QSEM036", $"in `{opName}`: `{r.Name}` is a whole `bit[]` register, not a number — a bit pattern has no sign, so it has no numeric value on its own; write `{QoraGates.BitsAsInt}({r.Name})` to read it as an unsigned integer, or index a single bit (`{r.Name}[i]`)", span);
                    return;
                case QMember { Base: QNameRef b } m:
                    Record(scope, b.Name, detail, span);
                    if (scope.Lookup(b.Name) is not { } owner) return; // Record already produced QSEM025
                    if (m.Member != "Count")
                        AddStatementError("QSEM029", $"in `{opName}`: `{b.Name}.{m.Member}` is not a supported member; arrays expose `.Count`", span);
                    else if (!owner.IsArray)
                        AddStatementError("QSEM029", $"in `{opName}`: `{b.Name}.Count` is invalid because `{b.Name}` is not an array", span);
                    return;
                case QMember m:   // non-name base — grammar-unreachable; still resolve what is resolvable
                    RecordExpr(scope, m.Base, detail, span, classicalOnly);
                    return;
                case QUnary u:
                    RecordExpr(scope, u.Operand, detail, span, classicalOnly);
                    return;
                case QBinOp b2:
                    // Register-to-register comparison is the ONE whole-register operation that needs no
                    // numeric reading: it matches bit patterns. OpenQASM defines it only for equal widths —
                    // `bit[2] "10"` and `bit[3] "010"` are NOT equal there even though both read as 2 — so
                    // unequal widths are rejected rather than silently answering "different". ORDERING
                    // (`<`/`>`) is deliberately excluded: the target compares those NUMERICALLY, ignoring
                    // width, so `f == g` and `f < g` would disagree about the same pair. Ordering is a
                    // numeric question and must be asked with an explicit conversion on both sides.
                    if (WholeBitRegister(scope, b2.Left) is { } lhs && WholeBitRegister(scope, b2.Right) is { } rhs
                        && b2.Op is "==" or "!=")
                    {
                        if (lhs.ArrayLength is int ln && rhs.ArrayLength is int rn && ln != rn
                            && reported036.Add(span))
                            AddStatementError("QSEM036", $"in `{opName}`: `{QNodes.Render(b2.Left)}` is `bit[{ln}]` and `{QNodes.Render(b2.Right)}` is `bit[{rn}]` — registers of different widths are never equal, whatever bits they hold; compare equal-width registers, or compare their values with `{QoraGates.BitsAsInt}(…)` on both sides", span);
                        RecordExpr(scope, b2.Left, detail, span, classicalOnly, registerOk: true);
                        RecordExpr(scope, b2.Right, detail, span, classicalOnly, registerOk: true);
                        return;
                    }
                    RecordExpr(scope, b2.Left, detail, span, classicalOnly);
                    RecordExpr(scope, b2.Right, detail, span, classicalOnly);
                    return;
                case QIndexNode i:
                    // `f[0]` READS one bit out of the register — the register itself is addressed, not valued.
                    RecordExpr(scope, i.Base, detail, span, classicalOnly, registerOk: true);
                    RecordExpr(scope, i.Index, detail, span, classicalOnly);
                    return;
                case QCallNode c:
                    // Resolver is the only name-binding authority. Downstream semantic passes consume its
                    // declaration ID and must never guess a user callable from spelling. Built-ins are the
                    // sole calls without a program declaration.
                    if (c.CalleeOpId is int expressionCalleeId)
                    {
                        var callable = scopeGraph.FindDeclaration(expressionCalleeId);
                        if (callable is { Kind: SymbolKind.Operation })
                            callable.AddUse(new UseSite(order, $"call @ {c.Name}", currentStmtId));
                    }
                    // Missing/dangling user IDs deliberately produce no guessed edge here. QoraValidator
                    // owns the QINTERNAL diagnostic, and a rejected validation artifact cannot reach later
                    // analysis.

                    // Arguments are ordinary expressions, except that a whole register IS a legal ARGUMENT:
                    // the callee's signature decides what it may consume (QSEM006). The allowance covers the
                    // argument itself only — `AsInt(f + 1)` still reports because that register is nested.
                    foreach (var a in c.Args) RecordExpr(scope, a, detail, span, classicalOnly, registerOk: true);
                    return;
            }
        }


        // A block-local declaration of <paramref name="name"/> is being made in <paramref name="scope"/>.
        // If an ENCLOSING value of that name was already USED earlier in THIS block (a use whose program
        // order lies after the block began and before this declaration), then that use bound to the outer
        // value — but the completed scope dictionary the validator later reads would resolve the same name
        // to this later local, so the two passes would disagree (an out-of-bounds index folded to the wrong
        // value, or a duplicate qubit missed). Point-of-declaration scoping (which the emitted OpenQASM
        // follows) means a name may not be used before its declaration in its own scope: reject it here.
        IReadOnlyList<UseSite> UsesBeforeShadow(Scope scope, string name, int scopeStart) =>
            scope.Lookup(name) is { Kind: not SymbolKind.Operation } outer
                ? outer.Uses.Where(use => use.Order > scopeStart && use.Order < order).ToList()
                : Array.Empty<UseSite>();

        void ReportUsesBeforeShadow(Scope scope, string name, int scopeStart, SourceSpan? span)
        {
            var earlierUses = UsesBeforeShadow(scope, name, scopeStart);
            if (earlierUses.Count == 0) return;

            var error = new QoraError(
                $"in `{opName}`: `{name}` is used earlier in this block but declared here, shadowing an outer `{name}` — a name cannot be used before its declaration in its own scope; move this declaration above the first use or rename it",
                "QSEM025",
                span);
            errors.Add(error);

            // The declaration-span diagnostic is caused by each earlier statement whose apparent outer
            // lookup becomes illegal once this block declares a same-named local. Mark those statements as
            // invalid too: after the scope is complete, call checking sees the inner symbol and must not
            // commit an ownership move for a reference rejected by point-of-declaration scoping.
            foreach (var use in earlierUses)
                if (use.NodeId != 0)
                    statementError?.Invoke(use.NodeId, error);
        }

        void Walk(IReadOnlyList<QStmt> stmts, Scope scope)
        {
            var scopeStart = order;             // program order just before this block's first statement (for UsedBeforeShadow)
            foreach (var s in stmts)
            {
                order++;
                currentStmtId = s.Id;
                switch (s)
                {
                    case QGate g:
                        // an operation CALL (not a built-in gate) records a use on the callee's operation
                        // symbol — its "used-where", accumulated across every caller. A user call must carry
                        // Resolver's stable declaration ID; only a built-in gate legitimately has no ID.
                        if (g.CalleeOpId is int gateCalleeId)
                        {
                            var callee = scopeGraph.FindDeclaration(gateCalleeId);
                            if (callee is { Kind: SymbolKind.Operation })
                                callee.AddUse(new UseSite(order, $"call @ {g.Name}", g.Id));
                        }
                        // Missing/dangling user IDs deliberately produce no guessed edge here. Validation
                        // reports the broken reference before this model can feed downstream analysis.
                        foreach (var a in g.Args)
                            switch (a)
                            {
                                case QQubitArg q:
                                    Record(scope, q.Reg, $"{g.Name} @ {q.Reg}[{QNodes.Render(q.Index)}]", g.Span);  // the register itself
                                    RecordExpr(scope, q.Index, $"{g.Name} index @ {q.Reg}[{QNodes.Render(q.Index)}]", g.Span); // vars inside [ i ]
                                    break;
                                // whole register OR angle expr (theta, a*pi, …). A bare whole `bit[]` here is an
                                // ARGUMENT, so the callee's signature judges it (QSEM006) — but a register
                                // buried in an angle expression (`Rx(f * pi, q)`) is being read as a number and
                                // still reports, because registerOk covers only the argument node itself.
                                case QTextArg t:
                                    RecordExpr(scope, t.Tree, $"{g.Name} @ {QNodes.Render(t.Tree)}", g.Span, registerOk: true);
                                    break;
                            }
                        break;
                    case QDecl { Value: QMeasure } md:
                        // Record the measured target FIRST — before the bit is in scope — so `var r: bit = M(r[0])`
                        // resolves the target `r` to the register (chain lookup), not the bit declared here.
                        if (md.Value is QMeasure { Target: { } mt })
                        {
                            Record(scope, QNodes.RegOf(mt), $"measure @ {QNodes.Render(mt)}", md.Span);       // the measured qubit COLLAPSES here
                            RecordExpr(scope, QNodes.IndexOf(mt), $"measure index @ {QNodes.Render(mt)}", md.Span);
                        }
                        // The measure bit is BLOCK-SCOPED like var/const for VISIBILITY: declared into the
                        // CURRENT scope in program order, so referencing it before this line is QSEM025.
                        // But its DECLARATION is HOISTED at emission to one flat top-level `bit r;` (OpenQASM
                        // importers reject local classical declarations), together with `use` registers and
                        // parameters. So while it may shadow a block-local classical, it must NOT reuse the
                        // name of an ENCLOSING register / parameter / measure bit — those flatten to the same
                        // emitted scope and would collide. (Same-scope dups: Declare. Disjoint sibling blocks
                        // may reuse a name — they dedup into one emitted bit and never coexist.)
                        if (scope.ParentScope?.Lookup(md.Name) is { Kind: SymbolKind.Register or SymbolKind.Parameter or SymbolKind.MeasureBit } encl)
                            Add(errors, "QSEM015", $"in `{opName}`: measure bit `{md.Name}` reuses the name of an enclosing {KindLabel(encl.Kind)}; a measured result is emitted as one top-level `bit {md.Name};`, so its name must be unique across the operation's registers, parameters and measure bits — rename one", md.Span);
                        ReportUsesBeforeShadow(scope, md.Name, scopeStart, md.Span);
                        measureBits.Add((md.Name, md.Span));   // checked against the completed root scope after the walk (top-level collision)
                        Declare(scope, new Symbol(scope.Id, md.Name, SymbolKind.MeasureBit, QType.Bit,
                            isConst: md.IsConst, declSpan: md.Span, declarationNodeId: md.Id));
                        break;
                    case QDecl d:
                        RecordValue(scope, d.Value, $"init {d.Name}", d.Span, targetIsArray: d.IsArray);
                        var inferred = ExpressionTypes.TypeOf(d.Value, scope, opById);
                        // Aggregate initializers and array declarations already have the more precise QSEM029
                        // owner. This shared check handles a bare array used as a scalar and every expression
                        // that consumes a declared function result.
                        if (!(d.Value is QArrayLiteral or QArrayNew) && !d.IsArray)
                        {
                            if (d.Type is { } declaredType)
                                CheckAssignable(d.Id, new QValueShape(declaredType, IsArray: false), d.Value, scope,
                                    d.Span, $"declaration `{d.Name}`");
                            else if (inferred is { IsArray: true } inferredArray)
                                CheckAssignable(d.Id, new QValueShape(inferredArray.Type, IsArray: false), d.Value,
                                    scope, d.Span, $"declaration `{d.Name}`");
                        }
                        // Point-of-declaration scoping: this name may not have been used earlier in its own
                        // block (that use would bind to the outer value, which this local shadows). The
                        // initializer's own use of the name (order == this statement) is exempt, so a const
                        // chain reading the outer — `const n: int = n + 1` — is still fine.
                        ReportUsesBeforeShadow(scope, d.Name, scopeStart, d.Span);
                        // A const's initializer folds HERE — the owner's site, the owner's scope (earlier
                        // consts are already in scope, so chains like `const m: int = k + 1` settle too).
                        // From now on the value is DATA on the symbol; no consumer re-reads the text.
                        Declare(scope, new Symbol(scope.Id, d.Name,
                            d.IsConst ? SymbolKind.Const : SymbolKind.Var,
                            d.Type ?? inferred?.Type,
                            d.IsConst, d.IsConst && d.Value is QText qt ? QNodes.Render(qt.Tree) : null, d.Span,
                            isArray: d.IsArray, arrayLength: ArrayLengthOf(d), declarationNodeId: d.Id)
                        {
                            FoldedBound = d is { IsConst: true, IsArray: false, Value: QText ct }
                                ? BoundFolder.Fold(ct.Tree, scope) : null,
                            FoldedBoolean = d is { IsConst: true, IsArray: false, Value: QText boolConst }
                                ? BooleanFolder.Fold(boolConst.Tree, scope) : null,
                        });
                        break;
                    case QAssign { Value: QMeasure { Target: { } at } } ma:
                        Record(scope, ma.Name, $"assign {ma.Name}", ma.Span);
                        RecordExpr(scope, ma.Index, $"assign index {ma.Name}[{QNodes.Render(ma.Index)}]", ma.Span, classicalOnly: true);
                        Record(scope, QNodes.RegOf(at), $"measure @ {QNodes.Render(at)}", ma.Span);
                        RecordExpr(scope, QNodes.IndexOf(at), $"measure index @ {QNodes.Render(at)}", ma.Span);
                        break;
                    case QAssign a:
                        Record(scope, a.Name, $"assign {a.Name}", a.Span);                                          // the target is referenced (written)
                        RecordExpr(scope, a.Index, $"assign index {a.Name}[{QNodes.Render(a.Index)}]", a.Span, classicalOnly: true);
                        RecordValue(scope, a.Value, $"assign {a.Name}", a.Span,
                            targetIsArray: a.Index is null && scope.Lookup(a.Name) is { IsArray: true });
                        if (scope.Lookup(a.Name) is { Type: { } assignedType } assigned
                            && !(assigned.IsArray && a.Index is null))
                            CheckAssignable(a.Id, new QValueShape(assignedType, IsArray: false), a.Value, scope,
                                a.Span, $"assignment to `{a.Name}`");
                        break;
                    case QReturn ret:
                        // A `return` value is an ordinary value position. Without this case it was walked by
                        // nothing here, so QSEM025 (unknown name), QSEM026 (qubit as a value), QSEM028
                        // (operation as a value) and QSEM036 all silently skipped every returned expression.
                        RecordValue(scope, ret.Value, "return", ret.Span);
                        if (op.IsFunction && op.ReturnType is { } returnType)
                            CheckAssignable(ret.Id, new QValueShape(returnType, IsArray: false), ret.Value, scope,
                                ret.Span, "function return", isReturn: true);
                        break;
                    case QFor f:
                        RecordExpr(scope, f.From, $"for bound {QNodes.Render(f.From)}", f.Span, classicalOnly: true);   // range bounds are classical (read in the ENCLOSING scope)…
                        RecordExpr(scope, f.To, $"for bound {QNodes.Render(f.To)}", f.Span, classicalOnly: true);
                        var loop = scopeGraph.CreateScope(
                            HirScopeKind.Loop,
                            scope.Id,
                            site: new HirScopeSite(f.Id, HirScopeSiteRole.ForBinder));
                        Declare(loop, new Symbol(loop.Id, f.Var, SymbolKind.LoopVar, QType.Int,
                            declSpan: f.Span, declarationNodeId: f.Id));
                        var forBody = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            loop.Id,
                            site: new HirScopeSite(f.Id, HirScopeSiteRole.ForBody));
                        Walk(f.Body, forBody);
                        break;
                    case QIf i:
                        var ifCond = scopeGraph.CreateScope(
                            HirScopeKind.Condition,
                            scope.Id,
                            site: new HirScopeSite(i.Id, HirScopeSiteRole.IfCondition));
                        RecordExpr(ifCond, i.Cond.Tree, $"if ({QNodes.Render(i.Cond.Tree)})", i.Span, classicalOnly: true);  // a condition is classical (a bit/bool)
                        var thenScope = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            ifCond.Id,
                            site: new HirScopeSite(i.Id, HirScopeSiteRole.IfThen));
                        var elseScope = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            ifCond.Id,
                            site: new HirScopeSite(i.Id, HirScopeSiteRole.IfElse));
                        Walk(i.Then, thenScope);
                        Walk(i.Else, elseScope);
                        break;
                    case QWhile w:
                        var whileCond = scopeGraph.CreateScope(
                            HirScopeKind.Condition,
                            scope.Id,
                            site: new HirScopeSite(w.Id, HirScopeSiteRole.WhileCondition));
                        RecordExpr(whileCond, w.Cond.Tree, $"while ({QNodes.Render(w.Cond.Tree)})", w.Span, classicalOnly: true);
                        var whileBody = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            whileCond.Id,
                            site: new HirScopeSite(w.Id, HirScopeSiteRole.WhileBody));
                        Walk(w.Body, whileBody);
                        break;
                    case QRepeat r:
                        var repeatBody = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            scope.Id,
                            site: new HirScopeSite(r.Id, HirScopeSiteRole.RepeatBody));
                        scopeGraph.BindScopeSite(
                            new HirScopeSite(r.Id, HirScopeSiteRole.RepeatCondition),
                            repeatBody);
                        Walk(r.Body, repeatBody);
                        currentStmtId = r.Id;   // the nested Walk moved it; the until belongs to the repeat itself
                        RecordExpr(repeatBody, r.Until.Tree, $"until ({QNodes.Render(r.Until.Tree)})", r.Span, classicalOnly: true);  // until runs AFTER the body, so it sees body-local names
                        break;
                }
            }
        }

        Walk(op.Body, root);

        // QSEM015 — a measure bit HOISTS to a flat top-level `bit r;` at emission, so it shares the emitted
        // top-level namespace with root-scope classicals (const/var/array), which also emit there. A same
        // name in both is a duplicate top-level declaration OpenQASM 3 rejects — and target name allocation, keying by
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

        // <paramref name="targetIsArray"/> suppresses the whole-register rule when the value is being bound to
        // an ARRAY target (`var g: bit[] = f;`, `g = f;`). Those are already rejected, more precisely, as
        // QSEM029 ("an array must be initialized with an array literal or `new T[N]`") — the register there is
        // not being read as a number, so QSEM036's advice to write `AsInt(f)` would be wrong guidance.
        void RecordValue(Scope valueScope, QExpr value, string detail, SourceSpan? span, bool targetIsArray = false)
        {
            switch (value)
            {
                case QText text:
                    RecordExpr(valueScope, text.Tree, detail, span, classicalOnly: true, registerOk: targetIsArray);
                    break;
                case QMeasure { Target: { } target }:
                    Record(valueScope, QNodes.RegOf(target), $"measure @ {QNodes.Render(target)}", span);
                    RecordExpr(valueScope, QNodes.IndexOf(target), $"measure index @ {QNodes.Render(target)}", span);
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

    /// <summary>
    /// The name stored inside its declaring namespace scope. Before resolution this is already simple;
    /// after resolution it is the FQN suffix. A later emitted/mangled tree does not carry the namespace
    /// prefix anymore, so its current spelling remains the local symbol spelling for fallback builds.
    /// </summary>
    internal static string LocalCallableName(QOperation operation)
    {
        if (operation.Namespace.Length == 0) return operation.Name;
        var prefix = operation.Namespace + ".";
        return operation.Name.StartsWith(prefix, StringComparison.Ordinal)
            ? operation.Name[prefix.Length..]
            : operation.Name;
    }

    private static void Add(List<QoraError> errors, string code, string message, SourceSpan? span) =>
        errors.Add(new QoraError(message, code, span));

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

    // --- debug rendering (the --stages view) ---

    /// <summary>Render the symbol table of every operation as text (for the <c>--stages</c> debug view).
    /// Each op is shown as its own <see cref="SymbolKind.Operation"/> symbol line, then its scope tree
    /// indented beneath. The program and model must be the exact pair owned by one HIR snapshot; a missing
    /// operation is a stage mismatch, not a reason to rebuild a different symbol graph.</summary>
    public static string Format(QProgram program, HirSemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(model);
        if (program.Operations.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        var scopeGraph = model.ScopeGraph
            ?? throw new InvalidOperationException(
                "QINTERNAL: the HIR semantic model has no scope graph");
        foreach (var op in program.Operations)
        {
            var kind = (scopeGraph.FindDeclaration(op.Id)?.Kind
                    ?? throw new InvalidOperationException(
                        $"QINTERNAL: operation {op.Id} is absent from the supplied HIR semantic model"))
                .ToString().ToLowerInvariant();
            sb.AppendLine($"{op.DisplayName ?? op.Name}: {kind}");
            var root = model.FindRootScope(op.Id)
                ?? throw new InvalidOperationException(
                    $"QINTERNAL: operation {op.Id} has no scope in the supplied HIR semantic model");
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
            var mode = sym.Kind == SymbolKind.Parameter
                ? (sym.ParameterOwnership, sym.ParameterAccess) switch
                {
                    (QOwnershipMode.Borrowed, QAccessMode.Mutable) => " var",
                    (QOwnershipMode.Moved, QAccessMode.ReadOnly) => " move",
                    (QOwnershipMode.Moved, QAccessMode.Mutable) => " move var",
                    _ => "",
                }
                : "";
            var val = sym.IsConst && sym.ConstValue is not null ? $" = {sym.ConstValue}" : "";
            var uses = sym.Uses.Count == 0
                ? "no uses"
                : $"uses [{string.Join(", ", sym.Uses.Select(u => u.Order))}] last @ {sym.Uses[^1].Order}";
            sb.AppendLine($"{pad}{sym.SourceName}: {kind}{mode} {type}{val}  ({uses})");
        }
        foreach (var child in scope.ChildScopes) PrintScope(child, sb, depth + 1);
    }
}
