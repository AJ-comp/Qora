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
    Callable,
    BuiltinGate,
    BuiltinFunction,
}

/// <summary>One place a name is used (a gate operand, a measurement target, an angle argument, …).
/// <see cref="Order"/> is a pre-order index over the operation's statements — monotonic in program order,
/// so the LAST use of a register is its liveness "death point" in straight-line code. <see cref="NodeId"/>
/// is the using statement's stable <see cref="HirNode.Id"/>, tying the use back to the exact HIR node.</summary>
public sealed record UseSite(int Order, string Detail, HirNodeId NodeId);

/// <summary>One declared name and everything the compiler knows about it. <see cref="Uses"/> accumulates as
/// the table is built. This is the single per-symbol record every semantic pass reads — duplicate/shadow
/// checking (declaration), liveness (uses), constant folding (const value), effect analysis (kind/type).</summary>
public sealed class Symbol
{
    private readonly List<UseSite> _uses = new();
    private IReadOnlyList<UseSite>? _sealedUses;
    private bool _isSealed;

    /// <summary>Semantic identity inside one HIR-scope-graph snapshot, distinct from a HIR node Id.</summary>
    public SymbolId Id { get; }

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
    /// <summary>The <see cref="HirParameter.NeedsMonoSizing"/> answer, stamped ONCE at declaration — true only
    /// for a parameter whose length monomorphization will supply (unsized <c>Qubit[]</c> / <c>bit[]</c>).
    /// The bounds prover's deferral gates read this stamp instead of re-deriving the set, so they can
    /// never drift from the monomorphizer's own trigger.</summary>
    internal bool MonoSized { get; init; }
    public SourceSpan? DeclSpan { get; }
    /// <summary>The declaring HIR node Id, or null for merged/synthetic/built-in symbols.</summary>
    public HirNodeId? DeclarationNodeId { get; }

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

    internal Symbol(SymbolId id, ScopeId declaringScopeId, string name, SymbolKind kind, QType? type = null,
        bool isConst = false, string? constValue = null,
        SourceSpan? declSpan = null, int? registerSize = null, bool isArray = false,
        int? arrayLength = null, HirNodeId? declarationNodeId = null,
        SymbolOrigin origin = SymbolOrigin.Source,
        QOwnershipMode parameterOwnership = QOwnershipMode.Borrowed,
        QAccessMode parameterAccess = QAccessMode.ReadOnly,
        Bound? foldedBound = null,
        bool? foldedBoolean = null,
        bool monoSized = false)
    {
        Id = id;
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
        FoldedBound = foldedBound;
        FoldedBoolean = foldedBoolean;
        MonoSized = monoSized;
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
    public static HirScopeGraph BuildHirScopeGraph(HirProgram program)
    {
        var graph = new HirScopeGraph();
        foreach (var namespacePath in program.NamespacePaths
                     .OrderBy(path => path.Count(character => character == '.'))
                     .ThenBy(path => path, StringComparer.Ordinal))
            graph.GetOrAddNamespace(namespacePath);

        foreach (var declaration in program.Declarations)
            BindNamespaceDeclarations(
                graph,
                declaration,
                parentNamespacePath: string.Empty);

        foreach (var callable in program.Callables)
            RegisterCallableDeclaration(
                graph,
                callable,
                program.NamespaceOf(callable));

        RegisterIntrinsics(graph);

        // `open` is a lookup route from the exact declaring namespace, not containment and not a
        // transitive import. Unknown targets remain absent here and are diagnosed by Resolver.
        foreach (var (ownerPath, opens) in
                 program.OpenDirectivesByNamespace)
        {
            var owner = graph.FindNamespaceScope(ownerPath);
            if (owner is null) continue;
            foreach (var open in opens)
                if (graph.FindNamespaceScope(open.Target) is { } target)
                    graph.AddLookupEdge(
                        owner.Id,
                        target.Id,
                        HirScopeEdgeKind.Import);
        }

        return graph;
    }

    private static void BindNamespaceDeclarations(
        HirScopeGraph graph,
        HirDeclaration declaration,
        string parentNamespacePath)
    {
        switch (declaration)
        {
            case HirCallable:
                return;

            case HirNamespaceDeclaration namespaceDeclaration:
            {
                var namespacePath = parentNamespacePath.Length == 0
                    ? namespaceDeclaration.Name
                    : $"{parentNamespacePath}.{namespaceDeclaration.Name}";
                var namespaceSymbol = graph.FindNamespaceSymbol(namespacePath)
                    ?? throw new InvalidOperationException(
                        $"QINTERNAL: namespace declaration `{namespacePath}` has no semantic symbol");
                graph.BindDeclaration(namespaceDeclaration.Id, namespaceSymbol.Id);

                foreach (var member in namespaceDeclaration.Declarations)
                    BindNamespaceDeclarations(graph, member, namespacePath);
                return;
            }

            default:
                throw new InvalidOperationException(
                    $"QINTERNAL: unsupported HIR declaration `{declaration.GetType().Name}`");
        }
    }

    private static HirScopeGraph BuildStandaloneScopeGraph(HirCallable callable)
    {
        var graph = new HirScopeGraph();
        RegisterCallableDeclaration(
            graph,
            callable,
            namespacePath: string.Empty);
        RegisterIntrinsics(graph);
        return graph;
    }

    private static void RegisterCallableDeclaration(
        HirScopeGraph graph,
        HirCallable callable,
        string namespacePath)
    {
        var owner = namespacePath.Length == 0
            ? graph.RootScope
            : graph.GetOrAddNamespace(namespacePath);
        var symbol = graph.CreateSymbol(
            owner.Id,
            callable.Name,
            SymbolKind.Callable,
            declSpan: callable.Span,
            declarationNodeId: callable.Id);
        graph.RegisterDeclaredMember(symbol);
        var callableScope = graph.CreateScope(
            HirScopeKind.Callable,
            owner.Id,
            symbol.Id,
            new HirScopeSite(callable.Id, HirScopeSiteRole.CallableBody));
        graph.RegisterCallableScope(callable.Id, callableScope);
    }

    private static void RegisterIntrinsics(HirScopeGraph graph)
    {
        var intrinsic = graph.GetOrAddNamespace(
            QoraGates.IntrinsicNamespace,
            SymbolOrigin.Builtin);
        foreach (var name in QoraGates.Names.Keys)
            graph.RegisterDeclaredMember(graph.CreateSymbol(
                intrinsic.Id,
                name,
                SymbolKind.BuiltinGate,
                origin: SymbolOrigin.Builtin));
        foreach (var name in QoraGates.Functions.Keys)
            graph.RegisterDeclaredMember(graph.CreateSymbol(
                intrinsic.Id,
                name,
                SymbolKind.BuiltinFunction,
                origin: SymbolOrigin.Builtin));
    }

    public static Scope Build(HirCallable op, List<QoraError> errors,
        HirScopeGraph? scopeGraph = null,
        IReadOnlyDictionary<HirNodeId, HirCallable>? opById = null,
        Action<HirNodeId, QoraError>? typeMismatchCandidate = null,
        Action<HirNodeId, QoraError>? statementError = null)
    {
        scopeGraph ??= BuildStandaloneScopeGraph(op);
        opById ??= new Dictionary<HirNodeId, HirCallable>
        {
            [op.Id] = op,
        };
        var callable = scopeGraph.FindDeclaration(op.Id)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: callable `{op.Name}` has no HIR symbol");
        var opName =
            op.DisplayName ?? scopeGraph.QualifiedName(callable);
        var root = scopeGraph.FindCallableScope(op.Id)
            ?? throw new InvalidOperationException(
                $"QINTERNAL: operation `{opName}` has no callable scope");
        var callableNamespace = scopeGraph.FindDeclaringNamespace(callable)
            ?? scopeGraph.RootScope;
        var order = 0;
        var reported026 = new HashSet<SourceSpan?>();   // one diagnostic per document-qualified statement span

        bool ContainsFunctionCall(HirExpression value) =>
            HirExpressions.CallsIn(value).Any(call =>
                QoraGates.Functions.ContainsKey(call.Name)
                || ExpressionTypes.TryGetFunction(call, opById, out _));

        // QSEM037 belongs to the contracts introduced by functions: every return value, and a declared or
        // assigned scalar that consumes a function result. Ordinary scalar declarations keep Qora's
        // historical loose model. Shape is never loose: a whole T[] cannot silently become one T value.
        void CheckAssignable(HirNodeId statementId, QValueShape target, HirExpression value, Scope valueScope, SourceSpan? span,
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
        foreach (var p in op.Parameters)
            Declare(root, scopeGraph.CreateSymbol(root.Id, p.Name, SymbolKind.Parameter, p.Type, declSpan: p.Span,
                registerSize: p.Type == QType.Qubit ? p.RegisterSize : null,
                isArray: p.IsArray,
                // a CLASSICAL array parameter's RegisterSize is its specialized length (bit[] gets one from
                // monomorphization, like Qubit[]) — exposing it as ArrayLength gives the post-mono bounds
                // proofs the same precision they have for sized registers; null pre-mono = P4 floors as ever.
                arrayLength: p.Type != QType.Qubit ? p.RegisterSize : null,
                declarationNodeId: p.Id,
                parameterOwnership: p.Ownership,
                parameterAccess: p.Access,
                monoSized: p.NeedsMonoSizing));

        void SeedRegisters(IReadOnlyList<HirStatement> statements)
        {
            foreach (var statement in statements)
                switch (statement)
                {
                    case HirQubitDeclarationStatement declaration:
                        Declare(
                            root,
                            scopeGraph.CreateSymbol(
                                root.Id,
                                declaration.Name,
                                SymbolKind.Register,
                                QType.Qubit,
                                declSpan: declaration.Span,
                                registerSize: declaration.Size,
                                isArray: true,
                                declarationNodeId: declaration.Id));
                        break;
                    case HirIfStatement conditional:
                        SeedRegisters(conditional.Then);
                        SeedRegisters(conditional.Else);
                        break;
                    case HirForStatement loop:
                        SeedRegisters(loop.Body);
                        break;
                    case HirWhileStatement loop:
                        SeedRegisters(loop.Body);
                        break;
                    case HirRepeatStatement repeat:
                        SeedRegisters(repeat.Body);
                        break;
                }
        }
        SeedRegisters(op.Body);

        // Resolve a referenced name. Found → record a use (tagged with the using statement's node Id). Not
        // found → it is neither a hoisted name (registers/measure bits seed the root) nor an in-scope
        // classical, so it is an unknown name OR a classical used before its declaration: QSEM025.
        // Expression literals (pi/tau/euler/true/false) are legitimate non-symbols and never error.
        HirNodeId? currentStatementId = null;   // set by Walk to the statement being visited
        var measureBits = new List<(string Name, SourceSpan? Span)>();   // every measure bit, for the post-walk top-level collision check
        var reported036 = new HashSet<SourceSpan?>();   // one diagnostic per document-qualified statement span
        void AddStatementError(string code, string message, SourceSpan? span)
        {
            var error = new QoraError(message, code, span);
            errors.Add(error);
            if (currentStatementId is { } statementId)
                statementError?.Invoke(statementId, error);
        }

        // A WHOLE `bit[]` register, if that is what this node denotes. The discriminator is IsArray, NOT Type:
        // a scalar measure bit and a bit register are both QType.Bit, and the scalar one is untouched by the
        // register rule (OpenQASM makes scalar `bit` interchangeable with `bool`, so it IS a value).
        Symbol? WholeBitRegister(Scope scope, HirExpression? expression) =>
            expression is HirNameExpression name
            && scope.Lookup(name.Name) is { Type: QType.Bit, IsArray: true } symbol
                ? symbol
                : null;
        void Record(Scope scope, string name, string detail, SourceSpan? span)
        {
            // pi/tau/euler/true/false mean something in an expression but are never declared, so they are
            // exempt from resolution entirely and checked before lookup.
            if (IsReservedName(name)) return;
            var sym = scope.Lookup(name);
            var callableValue = sym is null
                ? scopeGraph.LookupCallableOutward(callableNamespace.Id, name)
                : null;
            // An operation resolves up the scope chain but is NOT a value: it can only be called (the HIR call
            // path records those uses), never referenced in an expression or used as an assignment target.
            if (callableValue is { Kind: SymbolKind.Callable })
                AddStatementError("QSEM028", $"in `{opName}`: `{name}` is an operation, not a value — it can only be called (`{name}(…)`)", span);
            else if (sym is not null)
                sym.AddUse(new UseSite(
                    order,
                    detail,
                    currentStatementId
                    ?? throw new InvalidOperationException(
                        "QINTERNAL: a HIR name use was recorded outside a statement")));
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
        void RecordExpr(Scope scope, HirExpression? expression, string detail, SourceSpan? span, bool classicalOnly = false,
            bool registerOk = false)
        {
            switch (expression)
            {
                case null
                    or HirMissingExpression
                    or HirIntegerLiteralExpression
                    or HirLiteralExpression:
                    return;
                case HirNameExpression name:
                    Record(scope, name.Name, detail, span);
                    // QSEM026 at most once per statement span: `reported026.Add` is false on a repeat, so
                    // `if (q == q)` and a `for`'s `q..q` (From/To share the span) each report one diagnostic.
                    if (classicalOnly && scope.Lookup(name.Name) is { Type: QType.Qubit }
                        && reported026.Add(span))
                        AddStatementError("QSEM026", $"in `{opName}`: `{name.Name}` is a qubit and cannot be used as a classical value here — a qubit has no numeric value to compare, index, or compute with", span);
                    // QSEM036 — a WHOLE bit register is a container of bits, not a number. It reached a
                    // position that reads a VALUE, and no position hands it a meaning: `registerOk` is set by
                    // the PARENT for the four places a whole register legitimately appears (index base,
                    // `.Count` base, an argument, and `==`/`!=` against another register). Everywhere else the
                    // register would have to be read as a number, and a bit pattern carries no sign — the same
                    // bits read 2 unsigned and −2 in two's complement — so the language refuses to choose.
                    if (!registerOk && WholeBitRegister(scope, name) is not null
                        && reported036.Add(span))
                        AddStatementError("QSEM036", $"in `{opName}`: `{name.Name}` is a whole `bit[]` register, not a number — a bit pattern has no sign, so it has no numeric value on its own; write `{QoraGates.BitsAsInt}({name.Name})` to read it as an unsigned integer, or index a single bit (`{name.Name}[i]`)", span);
                    return;
                case HirMemberAccessExpression
                {
                    Receiver: HirNameExpression receiver,
                } member:
                    Record(scope, receiver.Name, detail, span);
                    if (scope.Lookup(receiver.Name) is not { } owner)
                        return; // Record already produced QSEM025
                    if (member.MemberName != "Count")
                        AddStatementError("QSEM029", $"in `{opName}`: `{receiver.Name}.{member.MemberName}` is not a supported member; arrays expose `.Count`", span);
                    else if (!owner.IsArray)
                        AddStatementError("QSEM029", $"in `{opName}`: `{receiver.Name}.Count` is invalid because `{receiver.Name}` is not an array", span);
                    return;
                case HirMemberAccessExpression member:
                    RecordExpr(scope, member.Receiver, detail, span, classicalOnly);
                    return;
                case HirUnaryExpression unary:
                    RecordExpr(scope, unary.Operand, detail, span, classicalOnly);
                    return;
                case HirBinaryExpression binary:
                    // Register-to-register comparison is the ONE whole-register operation that needs no
                    // numeric reading: it matches bit patterns. OpenQASM defines it only for equal widths —
                    // `bit[2] "10"` and `bit[3] "010"` are NOT equal there even though both read as 2 — so
                    // unequal widths are rejected rather than silently answering "different". ORDERING
                    // (`<`/`>`) is deliberately excluded: the target compares those NUMERICALLY, ignoring
                    // width, so `f == g` and `f < g` would disagree about the same pair. Ordering is a
                    // numeric question and must be asked with an explicit conversion on both sides.
                    if (WholeBitRegister(scope, binary.Left) is { } lhs
                        && WholeBitRegister(scope, binary.Right) is { } rhs
                        && binary.Operator is HirBinaryOperator.Equal or HirBinaryOperator.NotEqual)
                    {
                        if (lhs.ArrayLength is int ln && rhs.ArrayLength is int rn && ln != rn
                            && reported036.Add(span))
                            AddStatementError("QSEM036", $"in `{opName}`: `{HirExpressions.Render(binary.Left)}` is `bit[{ln}]` and `{HirExpressions.Render(binary.Right)}` is `bit[{rn}]` — registers of different widths are never equal, whatever bits they hold; compare equal-width registers, or compare their values with `{QoraGates.BitsAsInt}(…)` on both sides", span);
                        RecordExpr(scope, binary.Left, detail, span, classicalOnly, registerOk: true);
                        RecordExpr(scope, binary.Right, detail, span, classicalOnly, registerOk: true);
                        return;
                    }
                    RecordExpr(scope, binary.Left, detail, span, classicalOnly);
                    RecordExpr(scope, binary.Right, detail, span, classicalOnly);
                    return;
                case HirIndexExpression index:
                    // `f[0]` READS one bit out of the register — the register itself is addressed, not valued.
                    RecordExpr(scope, index.Receiver, detail, span, classicalOnly, registerOk: true);
                    RecordExpr(scope, index.Index, detail, span, classicalOnly);
                    return;
                case HirCallExpression call:
                    // Resolver is the only name-binding authority. Downstream semantic passes consume its
                    // declaration ID and must never guess a user callable from spelling. Built-ins are the
                    // sole calls without a program declaration.
                    if (call.CalleeId is HirNodeId expressionCalleeId)
                    {
                        var callable = scopeGraph.FindDeclaration(expressionCalleeId);
                        if (callable is { Kind: SymbolKind.Callable })
                            callable.AddUse(new UseSite(
                                order,
                                $"call @ {call.Name}",
                                currentStatementId
                                ?? throw new InvalidOperationException(
                                    "QINTERNAL: a HIR call use was recorded outside a statement")));
                    }
                    // Missing/dangling user IDs deliberately produce no guessed edge here. QoraValidator
                    // owns the QINTERNAL diagnostic, and a rejected validation artifact cannot reach later
                    // analysis.

                    // Arguments are ordinary expressions, except that a whole register IS a legal ARGUMENT:
                    // the callee's signature decides what it may consume (QSEM006). The allowance covers the
                    // argument itself only — `AsInt(f + 1)` still reports because that register is nested.
                    foreach (var argument in call.Arguments)
                        RecordExpr(
                            scope,
                            argument.Expression,
                            detail,
                            span,
                            classicalOnly,
                            registerOk: true);
                    return;
                case HirMeasurementExpression measurement:
                    RecordMeasurementTarget(scope, measurement.Target, span);
                    return;
            }

            // Future expression forms automatically participate in use collection through the one HIR
            // child relation. Forms with special value semantics above remain explicit.
            foreach (var child in expression.Children())
                switch (child)
                {
                    case HirExpression childExpression:
                        RecordExpr(
                            scope,
                            childExpression,
                            detail,
                            span,
                            classicalOnly);
                        break;
                    case HirArgument argument:
                        RecordExpr(
                            scope,
                            argument.Expression,
                            detail,
                            span,
                            classicalOnly,
                            registerOk: true);
                        break;
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
            scope.Lookup(name) is { Kind: not SymbolKind.Callable } outer
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
                statementError?.Invoke(use.NodeId, error);
        }

        void RecordMeasurementTarget(
            Scope targetScope,
            HirExpression target,
            SourceSpan? span)
        {
            var rendered = HirExpressions.Render(target);
            var registerName = HirExpressions.RegisterNameOf(target);
            if (registerName.Length == 0)
            {
                RecordExpr(targetScope, target, $"measure @ {rendered}", span);
                return;
            }

            Record(targetScope, registerName, $"measure @ {rendered}", span);
            RecordExpr(
                targetScope,
                HirExpressions.IndexOf(target),
                $"measure index @ {rendered}",
                span);
        }

        void Walk(IReadOnlyList<HirStatement> statements, Scope scope)
        {
            var scopeStart = order;             // program order just before this block's first statement (for UsedBeforeShadow)
            foreach (var statement in statements)
            {
                order++;
                currentStatementId = statement.Id;
                switch (statement)
                {
                    case HirCallStatement callStatement:
                        // an operation CALL (not a built-in gate) records a use on the callee's operation
                        // symbol — its "used-where", accumulated across every caller. A user call must carry
                        // Resolver's stable declaration ID; only a built-in gate legitimately has no ID.
                        if (callStatement.Call.CalleeId is HirNodeId calleeId)
                        {
                            var callee = scopeGraph.FindDeclaration(calleeId);
                            if (callee is { Kind: SymbolKind.Callable })
                                callee.AddUse(new UseSite(
                                    order,
                                    $"call @ {callStatement.Name}",
                                    callStatement.Id));
                        }
                        // Missing/dangling user IDs deliberately produce no guessed edge here. Validation
                        // reports the broken reference before this model can feed downstream analysis.
                        foreach (var argument in callStatement.Call.Arguments)
                            RecordExpr(
                                scope,
                                argument.Expression,
                                $"{callStatement.Name} @ {HirExpressions.Render(argument.Expression)}",
                                callStatement.Span,
                                registerOk: true);
                        break;
                    case HirVariableDeclarationStatement
                    {
                        Value: HirMeasurementExpression measurement,
                    } declaration:
                        // Record the measured target FIRST — before the bit is in scope — so `var r: bit = M(r[0])`
                        // resolves the target `r` to the register (chain lookup), not the bit declared here.
                        RecordMeasurementTarget(
                            scope,
                            measurement.Target,
                            declaration.Span);
                        // The measure bit is BLOCK-SCOPED like var/const for VISIBILITY: declared into the
                        // CURRENT scope in program order, so referencing it before this line is QSEM025.
                        // But its DECLARATION is HOISTED at emission to one flat top-level `bit r;` (OpenQASM
                        // importers reject local classical declarations), together with `use` registers and
                        // parameters. So while it may shadow a block-local classical, it must NOT reuse the
                        // name of an ENCLOSING register / parameter / measure bit — those flatten to the same
                        // emitted scope and would collide. (Same-scope dups: Declare. Disjoint sibling blocks
                        // may reuse a name — they dedup into one emitted bit and never coexist.)
                        if (scope.ParentScope?.Lookup(declaration.Name) is
                            {
                                Kind: SymbolKind.Register
                                    or SymbolKind.Parameter
                                    or SymbolKind.MeasureBit,
                            } enclosing)
                            Add(
                                errors,
                                "QSEM015",
                                $"in `{opName}`: measure bit `{declaration.Name}` reuses the name of an enclosing {KindLabel(enclosing.Kind)}; a measured result is emitted as one top-level `bit {declaration.Name};`, so its name must be unique across the operation's registers, parameters and measure bits — rename one",
                                declaration.Span);
                        ReportUsesBeforeShadow(
                            scope,
                            declaration.Name,
                            scopeStart,
                            declaration.Span);
                        measureBits.Add((declaration.Name, declaration.Span));
                        Declare(
                            scope,
                            scopeGraph.CreateSymbol(
                                scope.Id,
                                declaration.Name,
                                SymbolKind.MeasureBit,
                                QType.Bit,
                                isConst: declaration.IsConst,
                                declSpan: declaration.Span,
                                declarationNodeId: declaration.Id));
                        break;
                    case HirVariableDeclarationStatement declaration:
                        RecordValue(
                            scope,
                            declaration.Value,
                            $"init {declaration.Name}",
                            declaration.Span,
                            targetIsArray: declaration.IsArray);
                        var inferred = ExpressionTypes.TypeOf(
                            declaration.Value,
                            scope,
                            opById);
                        // Aggregate initializers and array declarations already have the more precise QSEM029
                        // owner. This shared check handles a bare array used as a scalar and every expression
                        // that consumes a declared function result.
                        if (declaration.Value is not (
                                HirArrayLiteralExpression
                                or HirArrayCreationExpression)
                            && !declaration.IsArray)
                        {
                            if (declaration.Type is { } declaredType)
                                CheckAssignable(
                                    declaration.Id,
                                    new QValueShape(
                                        declaredType,
                                        IsArray: false),
                                    declaration.Value,
                                    scope,
                                    declaration.Span,
                                    $"declaration `{declaration.Name}`");
                            else if (inferred is { IsArray: true } inferredArray)
                                CheckAssignable(
                                    declaration.Id,
                                    new QValueShape(
                                        inferredArray.Type,
                                        IsArray: false),
                                    declaration.Value,
                                    scope,
                                    declaration.Span,
                                    $"declaration `{declaration.Name}`");
                        }
                        // Point-of-declaration scoping: this name may not have been used earlier in its own
                        // block (that use would bind to the outer value, which this local shadows). The
                        // initializer's own use of the name (order == this statement) is exempt, so a const
                        // chain reading the outer — `const n: int = n + 1` — is still fine.
                        ReportUsesBeforeShadow(
                            scope,
                            declaration.Name,
                            scopeStart,
                            declaration.Span);
                        // A const's initializer folds HERE — the owner's site, the owner's scope (earlier
                        // consts are already in scope, so chains like `const m: int = k + 1` settle too).
                        // From now on the value is DATA on the symbol; no consumer re-reads the text.
                        Declare(scope, scopeGraph.CreateSymbol(
                            scope.Id,
                            declaration.Name,
                            declaration.IsConst
                                ? SymbolKind.Const
                                : SymbolKind.Var,
                            declaration.Type ?? inferred?.Type,
                            declaration.IsConst,
                            declaration.IsConst
                                ? HirExpressions.Render(declaration.Value)
                                : null,
                            declaration.Span,
                            isArray: declaration.IsArray,
                            arrayLength: ArrayLengthOf(declaration),
                            declarationNodeId: declaration.Id,
                            foldedBound:
                                declaration.IsConst
                                && !declaration.IsArray
                                    ? BoundFolder.Fold(
                                        declaration.Value,
                                        scope)
                                    : null,
                            foldedBoolean:
                                declaration.IsConst
                                && !declaration.IsArray
                                    ? BooleanFolder.Fold(
                                        declaration.Value,
                                        scope)
                                    : null));
                        break;
                    case HirAssignmentStatement
                    {
                        Value: HirMeasurementExpression measurement,
                    } assignment:
                    {
                        var assignedName =
                            HirExpressions.AssignmentNameOf(assignment.Target)
                            ?? throw new InvalidOperationException(
                                "QINTERNAL: semantic binding received an unsupported assignment target");
                        var assignedIndex =
                            HirExpressions.AssignmentIndexOf(assignment.Target);
                        Record(
                            scope,
                            assignedName,
                            $"assign {assignedName}",
                            assignment.Span);
                        RecordExpr(
                            scope,
                            assignedIndex,
                            $"assign index {assignedName}[{HirExpressions.Render(assignedIndex)}]",
                            assignment.Span,
                            classicalOnly: true);
                        RecordMeasurementTarget(
                            scope,
                            measurement.Target,
                            assignment.Span);
                        break;
                    }
                    case HirAssignmentStatement assignment:
                    {
                        var assignedName =
                            HirExpressions.AssignmentNameOf(assignment.Target)
                            ?? throw new InvalidOperationException(
                                "QINTERNAL: semantic binding received an unsupported assignment target");
                        var assignedIndex =
                            HirExpressions.AssignmentIndexOf(assignment.Target);
                        Record(
                            scope,
                            assignedName,
                            $"assign {assignedName}",
                            assignment.Span);
                        RecordExpr(
                            scope,
                            assignedIndex,
                            $"assign index {assignedName}[{HirExpressions.Render(assignedIndex)}]",
                            assignment.Span,
                            classicalOnly: true);
                        RecordValue(
                            scope,
                            assignment.Value,
                            $"assign {assignedName}",
                            assignment.Span,
                            targetIsArray:
                                assignedIndex is null
                                && scope.Lookup(assignedName) is
                                {
                                    IsArray: true,
                                });
                        if (scope.Lookup(assignedName) is
                            {
                                Type: { } assignedType,
                            } assigned
                            && !(assigned.IsArray
                                 && assignedIndex is null))
                            CheckAssignable(
                                assignment.Id,
                                new QValueShape(
                                    assignedType,
                                    IsArray: false),
                                assignment.Value,
                                scope,
                                assignment.Span,
                                $"assignment to `{assignedName}`");
                        break;
                    }
                    case HirReturnStatement returnStatement:
                        // A `return` value is an ordinary value position. Without this case it was walked by
                        // nothing here, so QSEM025 (unknown name), QSEM026 (qubit as a value), QSEM028
                        // (operation as a value) and QSEM036 all silently skipped every returned expression.
                        RecordValue(
                            scope,
                            returnStatement.Value,
                            "return",
                            returnStatement.Span);
                        if (op.IsFunction && op.ReturnType is { } returnType)
                            CheckAssignable(
                                returnStatement.Id,
                                new QValueShape(
                                    returnType,
                                    IsArray: false),
                                returnStatement.Value,
                                scope,
                                returnStatement.Span,
                                "function return",
                                isReturn: true);
                        break;
                    case HirForStatement loopStatement:
                        RecordExpr(
                            scope,
                            loopStatement.From,
                            $"for bound {HirExpressions.Render(loopStatement.From)}",
                            loopStatement.Span,
                            classicalOnly: true);
                        RecordExpr(
                            scope,
                            loopStatement.To,
                            $"for bound {HirExpressions.Render(loopStatement.To)}",
                            loopStatement.Span,
                            classicalOnly: true);
                        var loop = scopeGraph.CreateScope(
                            HirScopeKind.Loop,
                            scope.Id,
                            site: new HirScopeSite(
                                loopStatement.Id,
                                HirScopeSiteRole.ForBinder));
                        Declare(
                            loop,
                            scopeGraph.CreateSymbol(
                                loop.Id,
                                loopStatement.Variable,
                                SymbolKind.LoopVar,
                                QType.Int,
                                declSpan: loopStatement.Span,
                                declarationNodeId: loopStatement.Id));
                        var forBody = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            loop.Id,
                            site: new HirScopeSite(
                                loopStatement.Id,
                                HirScopeSiteRole.ForBody));
                        Walk(loopStatement.Body, forBody);
                        break;
                    case HirIfStatement conditional:
                        var ifCond = scopeGraph.CreateScope(
                            HirScopeKind.Condition,
                            scope.Id,
                            site: new HirScopeSite(
                                conditional.Id,
                                HirScopeSiteRole.IfCondition));
                        RecordExpr(
                            ifCond,
                            conditional.Condition,
                            $"if ({HirExpressions.Render(conditional.Condition)})",
                            conditional.Span,
                            classicalOnly: true);
                        var thenScope = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            ifCond.Id,
                            site: new HirScopeSite(
                                conditional.Id,
                                HirScopeSiteRole.IfThen));
                        var elseScope = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            ifCond.Id,
                            site: new HirScopeSite(
                                conditional.Id,
                                HirScopeSiteRole.IfElse));
                        Walk(conditional.Then, thenScope);
                        Walk(conditional.Else, elseScope);
                        break;
                    case HirWhileStatement whileStatement:
                        var whileCond = scopeGraph.CreateScope(
                            HirScopeKind.Condition,
                            scope.Id,
                            site: new HirScopeSite(
                                whileStatement.Id,
                                HirScopeSiteRole.WhileCondition));
                        RecordExpr(
                            whileCond,
                            whileStatement.Condition,
                            $"while ({HirExpressions.Render(whileStatement.Condition)})",
                            whileStatement.Span,
                            classicalOnly: true);
                        var whileBody = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            whileCond.Id,
                            site: new HirScopeSite(
                                whileStatement.Id,
                                HirScopeSiteRole.WhileBody));
                        Walk(whileStatement.Body, whileBody);
                        break;
                    case HirRepeatStatement repeat:
                        var repeatBody = scopeGraph.CreateScope(
                            HirScopeKind.Block,
                            scope.Id,
                            site: new HirScopeSite(
                                repeat.Id,
                                HirScopeSiteRole.RepeatBody));
                        scopeGraph.BindScopeSite(
                            new HirScopeSite(
                                repeat.Id,
                                HirScopeSiteRole.RepeatCondition),
                            repeatBody);
                        Walk(repeat.Body, repeatBody);
                        currentStatementId = repeat.Id;
                        RecordExpr(
                            repeatBody,
                            repeat.Until,
                            $"until ({HirExpressions.Render(repeat.Until)})",
                            repeat.Span,
                            classicalOnly: true);
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
        void RecordValue(
            Scope valueScope,
            HirExpression value,
            string detail,
            SourceSpan? span,
            bool targetIsArray = false)
        {
            switch (value)
            {
                case HirMeasurementExpression measurement:
                    RecordMeasurementTarget(
                        valueScope,
                        measurement.Target,
                        span);
                    break;
                default:
                    RecordExpr(
                        valueScope,
                        value,
                        detail,
                        span,
                        classicalOnly: true,
                        registerOk: targetIsArray);
                    break;
            }
        }

        static int? ArrayLengthOf(
            HirVariableDeclarationStatement declaration) =>
            declaration.Value switch
        {
            HirArrayLiteralExpression literal => literal.Elements.Count,
            HirArrayCreationExpression allocation when allocation.Length >= 0 =>
                allocation.Length,
            _ => null,
        };
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
        SymbolKind.Callable => "callable",
        _ => "variable",
    };

    // --- debug rendering (the --stages view) ---

    /// <summary>Render the symbol table of every operation as text (for the <c>--stages</c> debug view).
    /// Each callable is shown as its own <see cref="SymbolKind.Callable"/> symbol line, then its scope tree
    /// indented beneath. The program and model must be the exact pair owned by one HIR snapshot; a missing
    /// operation is a stage mismatch, not a reason to rebuild a different symbol graph.</summary>
    public static string Format(HirProgram program, HirSemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(model);
        if (program.Callables.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        var scopeGraph = model.ScopeGraph
            ?? throw new InvalidOperationException(
                "QINTERNAL: the HIR semantic model has no scope graph");
        foreach (var op in program.Callables)
        {
            var callable = scopeGraph.FindDeclaration(op.Id)
                ?? throw new InvalidOperationException(
                    $"QINTERNAL: callable {op.Id} is absent from the supplied HIR semantic model");
            var kind = callable.Kind
                .ToString().ToLowerInvariant();
            sb.AppendLine(
                $"{op.DisplayName ?? scopeGraph.QualifiedName(callable)}: {kind}");
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
