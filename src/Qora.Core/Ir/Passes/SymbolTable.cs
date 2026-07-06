using System.Linq;
using System.Text;

namespace Qora.Ir.Passes;

/// <summary>What a declared name IS.</summary>
public enum SymbolKind { Parameter, Register, MeasureBit, Var, Const, LoopVar }

/// <summary>One place a name is used (a gate operand, a measurement target, an angle argument, …).
/// <see cref="Order"/> is a pre-order index over the operation's statements — monotonic in program order,
/// so the LAST use of a register is its liveness "death point" in straight-line code.</summary>
public sealed record UseSite(int Order, string Detail);

/// <summary>One declared name and everything the compiler knows about it. <see cref="Uses"/> accumulates as
/// the table is built. This is the single per-symbol record every semantic pass reads — duplicate/shadow
/// checking (declaration), liveness (uses), constant folding (const value), effect analysis (kind/type).</summary>
public sealed class Symbol
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public QType? Type { get; }                 // Int / Float / Angle / Bit / Qubit (null if unknown)
    public bool IsConst { get; }
    public string? ConstValue { get; }          // a const's initializer text (for folding); null for var/measure/register
    public QSpan? DeclSpan { get; }
    public int? RegisterSize { get; }           // concrete qubit count: `use q = Qubit[N]` and sized qubit params; null otherwise
    public string? SizeParam { get; }           // symbolic register size name: `Qubit[n] q` (unknown until monomorphization); null otherwise
    public List<UseSite> Uses { get; } = new();

    public Symbol(string name, SymbolKind kind, QType? type = null, bool isConst = false, string? constValue = null,
        QSpan? declSpan = null, int? registerSize = null, string? sizeParam = null)
        => (Name, Kind, Type, IsConst, ConstValue, DeclSpan, RegisterSize, SizeParam)
         = (name, kind, type, isConst, constValue, declSpan, registerSize, sizeParam);
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
    public bool TryAdd(Symbol sym) => _symbols.TryAdd(sym.Name, sym);
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
    public static Scope Build(QOperation op, List<QoraError> errors,
        Dictionary<IReadOnlyList<QStmt>, Scope>? scopeIndex = null)
    {
        var opName = op.DisplayName ?? op.Name;
        var root = new Scope();
        var order = 0;
        var reported026 = new HashSet<(int, int)>();   // QSEM026 spans already flagged — one per statement span (a `for`'s From/To share a span; a condition names a qubit twice), so the diagnostic isn't duplicated

        // THE single insertion door. EVERY declared name — parameters, `use` registers, measure bits, vars,
        // consts, loop vars — is added through here, into the scope the caller chose (root for the hoisted
        // ones, the current block for the rest). Insertion goes via Scope.TryAdd whose backing dictionary is
        // private, so NO path can bypass the same-scope duplicate rule: a collision anywhere is QSEM015.
        void Declare(Scope target, Symbol sym)
        {
            if (target.TryAdd(sym)) return;
            var existing = target.LookupLocal(sym.Name)!;   // TryAdd failed ⇒ a same-name symbol is already here
            Add(errors, "QSEM015", existing.Kind == sym.Kind
                ? $"in `{opName}`: the {KindLabel(sym.Kind)} name `{sym.Name}` is declared more than once; each name must be unique within its scope"
                : $"in `{opName}`: `{sym.Name}` is declared as both a {KindLabel(existing.Kind)} and a {KindLabel(sym.Kind)} in the same scope; rename one", sym.DeclSpan);
        }

        // Parameters + the HOISTED `use` registers share one emitted top-level scope, so they seed the ROOT
        // — BEFORE the walk, so a register may be forward-referenced. Routing them through Declare means a
        // duplicate among them (two `use q`, or `use q` colliding with a param) is caught as QSEM015 instead
        // of silently overwriting. Measure bits are NOT here — they are block-scoped (declared in the walk).
        foreach (var p in op.Params)
            Declare(root, new Symbol(p.Name, SymbolKind.Parameter, p.Type, declSpan: p.Span,
                registerSize: p.Type == QType.Qubit ? p.RegisterSize : null,
                sizeParam: p.Type == QType.Qubit ? p.SizeParam : null));

        void SeedRegisters(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case QUse u: Declare(root, new Symbol(u.Name, SymbolKind.Register, QType.Qubit, declSpan: u.Span, registerSize: u.Size)); break;
                    case QIf i: SeedRegisters(i.Then); SeedRegisters(i.Else); break;
                    case QFor f: SeedRegisters(f.Body); break;
                    case QWhile w: SeedRegisters(w.Body); break;
                    case QRepeat r: SeedRegisters(r.Body); break;
                    case QConjugate c: SeedRegisters(c.Within); SeedRegisters(c.Apply); break;
                }
        }
        SeedRegisters(op.Body);

        // The symbolic register size `n` in `Qubit[n] q` is a legal in-body value (e.g. `for i in 0..n`) but
        // is NOT a declared symbol (the size-uniqueness check owns it). Exempt it from resolution so the
        // strict "unknown name" error below never fires on it.
        var sizeParams = op.Params
            .Where(p => p.Type == QType.Qubit && p.SizeParam is not null)
            .Select(p => p.SizeParam!)
            .ToHashSet();

        // Resolve a referenced name. Found → record a use. Not found → it is neither a hoisted name
        // (registers/measure bits seed the root) nor an in-scope classical, so it is an unknown name OR a
        // classical used before its declaration: QSEM025. Expression literals (pi/tau/euler/true/false) and
        // the symbolic register size are legitimate non-symbols and never error.
        void Record(Scope scope, string name, string detail, QSpan? span)
        {
            var sym = scope.Lookup(name);
            if (sym is not null) sym.Uses.Add(new UseSite(order, detail));
            else if (!IsReservedName(name) && !sizeParams.Contains(name))
                Add(errors, "QSEM025", $"in `{opName}`: `{name}` is not declared in scope here — an unknown name, or a name used before its declaration", span);
        }

        // Resolve every identifier inside a TEXT expression — a condition, a range bound, a qubit index
        // (`q[i]`), an angle (`a * pi`), an initializer (`x = a + b`). Each goes through Record, so an
        // unknown / used-before-declared name in ANY expression position is caught (QSEM025), not silently
        // emitted. <paramref name="classicalOnly"/> marks a position that must hold a CLASSICAL value; a qubit
        // there is QSEM026, raised at most ONCE per expression (mirroring the validator's FirstQubitIn, so
        // `if (q == q)` reports once — not once per token). Numeric literals aren't identifiers; pi/tau/euler
        // /true/false and the symbolic register size are exempt.
        void RecordExpr(Scope scope, string? text, string detail, QSpan? span, bool classicalOnly = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var name in Identifiers(text))
            {
                Record(scope, name, detail, span);
                // QSEM026 at most once per statement span: `reported026.Add` is false on a repeat, so
                // `if (q == q)` and a `for`'s `q..q` (From/To share the span) each report a single diagnostic.
                if (classicalOnly && scope.Lookup(name) is { Type: QType.Qubit }
                    && reported026.Add((span?.Start ?? -1, span?.End ?? -1)))
                    Add(errors, "QSEM026", $"in `{opName}`: `{name}` is a qubit and cannot be used as a classical value here — a qubit has no numeric value to compare, index, or compute with", span);
            }
        }

        void Walk(IReadOnlyList<QStmt> stmts, Scope scope)
        {
            scopeIndex?.TryAdd(stmts, scope);   // map each body list -> its scope, so the validator resolves names with correct nesting
            foreach (var s in stmts)
            {
                order++;
                switch (s)
                {
                    case QGate g:
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
                        Declare(scope, new Symbol(md.Name, SymbolKind.MeasureBit, QType.Bit, isConst: md.IsConst, declSpan: md.Span));
                        break;
                    case QDecl d:
                        if (d.Value is QText dv) RecordExpr(scope, dv.Text, $"init {d.Name} = {dv.Text}", d.Span, classicalOnly: true);  // an initializer is a classical value
                        Declare(scope, new Symbol(d.Name, d.IsConst ? SymbolKind.Const : SymbolKind.Var, d.Type,
                            d.IsConst, d.IsConst && d.Value is QText qt ? qt.Text : null, d.Span));
                        break;
                    case QAssign { Value: QMeasure { Target: { } at } } ma:
                        Record(scope, at.Reg, $"measure @ {at.Reg}[{at.Index}]", ma.Span);
                        RecordExpr(scope, at.Index, $"measure index @ {at.Reg}[{at.Index}]", ma.Span);
                        break;
                    case QAssign a:
                        Record(scope, a.Name, $"assign {a.Name}", a.Span);                                          // the target is referenced (written)
                        if (a.Value is QText av) RecordExpr(scope, av.Text, $"assign {a.Name} = {av.Text}", a.Span, classicalOnly: true);  // an assigned value is classical
                        break;
                    case QFor f:
                        RecordExpr(scope, f.From, $"for bound {f.From}", f.Span, classicalOnly: true);              // range bounds are classical (read in the ENCLOSING scope)…
                        RecordExpr(scope, f.To, $"for bound {f.To}", f.Span, classicalOnly: true);
                        RecordExpr(scope, f.Step, $"for step {f.Step}", f.Span, classicalOnly: true);
                        var loop = new Scope(scope);                                                        // …then the loop var gets its own scope
                        Declare(loop, new Symbol(f.Var, SymbolKind.LoopVar, QType.Int, declSpan: f.Span));
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

        // QSEM013 / QSEM015 — declaration-name rules the symbol table owns. EVERY declared name (all symbols,
        // plus each generic register size) is checked HERE, in one place, so no declaration site can be
        // forgotten:
        //   - a reserved expression literal (pi/tau/euler/true/false) can never be a declared name — it means
        //     something else in an expression, and a generic size named `pi` would be captured by the
        //     Monomorphizer's whole-word textual substitution (silently corrupting `pi` in angle expressions);
        //   - a generic register size must not collide with any other declared name.
        foreach (var sym in root.AllSymbols())
            if (IsReservedName(sym.Name))
                Add(errors, "QSEM013", $"in `{opName}`: {KindLabel(sym.Kind)} name `{sym.Name}` shadows the built-in `{sym.Name}`; choose another name", sym.DeclSpan);
        foreach (var p in op.Params)
            if (p.Type == QType.Qubit && p.SizeParam is { } spName)
            {
                if (IsReservedName(spName))
                    Add(errors, "QSEM013", $"in `{opName}`: the generic register size `{spName}` shadows the built-in `{spName}`; choose another name", p.Span);
                if (root.AllSymbols().Any(s => s.Name == spName))
                    Add(errors, "QSEM015", $"in `{opName}`: `{spName}` is both the generic register size in `Qubit[{spName}] {p.Name}` and a declared name — the size parameter needs a unique name", p.Span);
            }

        return root;
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
        _ => "variable",
    };

    /// <summary>Yield each identifier token in a text expression — a run of letters/digits/underscore that
    /// starts with a letter or underscore. Numbers, operators, brackets and whitespace are skipped. Callers
    /// filter to real symbols via <see cref="Scope.Lookup"/>, so bare literals (<c>pi</c>, <c>tau</c>, numbers)
    /// that aren't declared never produce a use. Also used by the validator to find a qubit hidden inside a
    /// value-slot expression (QSEM026).</summary>
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

    /// <summary>Render the symbol table of every operation as text (for the <c>--stages</c> debug view).</summary>
    public static string Format(QProgram? program)
    {
        if (program is null || program.Operations.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        var sink = new List<QoraError>();                 // formatting must not surface errors
        foreach (var op in program.Operations)
        {
            sb.AppendLine($"{op.DisplayName ?? op.Name}:");
            var root = Build(op, sink);
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
            sb.AppendLine($"{pad}{sym.Name}: {kind} {type}{val}  ({uses})");
        }
        foreach (var child in scope.Children) PrintScope(child, sb, depth + 1);
    }
}
