namespace Qora.Ir;

/// <summary>
/// Array-local hoisting (IR→IR, OpenQASM-only): makes every array LOCAL the language allows expressible
/// in OpenQASM 3, which restricts where such declarations may appear (types.rst / scope.rst):
///
///   1. "Arrays cannot be declared inside the body of a function or gate. All arrays must be declared
///      within the global scope of the program." — so a def-local <c>int[]/float[]/angle[]</c> cannot
///      stay a declaration at all;
///   2. "Globally scoped variables without the `const` modifier are not visible inside the definition."
///      — so a def cannot simply USE a hoisted global either; the ONLY door an array has into a def is
///      an array REFERENCE parameter (<c>mutable array[T, #dim = 1]</c>), the same door <c>int[]</c>
///      parameters already use;
///   3. classical declarations inside a CONTROL-FLOW block are rejected by the dominant importers, so a
///      nested declaration of any classical kind must sit at the top of its scope (the same rule the
///      emitter already applies to measure bits).
///
/// Three recipes, one principle — the DECLARATION moves, the SITE becomes element-wise
/// re-initialization (so "fresh value on every entry/iteration" survives the move; OpenQASM has no
/// recursion — QSEM011 — so shared storage never sees two live instances):
///
///   R1  classical array (<c>int[]/float[]/angle[]</c>) in a def-emitted op — hidden-parameter threading
///       (the C++ hidden-<c>this</c> shape):
///         operation Helper(Qubit q) {              array[int, 3] Helper_tbl = {0, 0, 0};   // global backing
///             int[] tbl = [1, 2, 3];        →      def Helper(qubit q, mutable array[int, #dim = 1] tbl) {
///             …                                        tbl[0] = 1; tbl[1] = 2; tbl[2] = 3; // re-init in place
///         }                                            …
///         Helper(q[0]);                            Helper(q[0], Helper_tbl);               // caller supplies it
///       A def that (transitively) calls an owner gains PASS-THROUGH parameters — the call graph is a
///       DAG, so the threading always reaches the entry op, whose body IS the global scope.
///   R2  classical array nested in a block of the ENTRY op — visibility is free there, so only rule 3
///       applies: the declaration hoists (default-initialized) to the top of the entry body.
///   R3  <c>bit[]</c> nested in a block of ANY op — a sized REGISTER, legal at def scope, so it hoists
///       to the top of its own op only; a top-level <c>bit[]</c> local needs nothing and is untouched.
///
/// Same-named declarations in one op (disjoint sibling blocks, or a conjugation inverse's re-emitted
/// copy) share one parameter / one storage sized to the LONGEST declaration — mirroring exactly how
/// <see cref="Passes.NameMangler"/> collapses same-named locals into one emitted name; each site still
/// re-initializes its own elements, and shorter sites never index past their own proven length.
///
/// Runs FIRST in <see cref="QasmBackend"/>, before <see cref="Passes.NameMangler"/>: every name this
/// pass mints is declared through ordinary IR nodes, so mangling and <see cref="Passes.ReferentialCheck"/>
/// treat them exactly like user-written names — no collision logic lives here. The validator deliberately
/// has NO placement rule for array locals (the old QSEM012 arm was this target rule leaking into the
/// language). Deletability: if a future OpenQASM allows def-local arrays, delete this file and remove the
/// one call in <see cref="QasmBackend"/>.
/// </summary>
public static class ArrayLocalHoisting
{
    public sealed record Result(QProgram Program, IReadOnlyList<string> Notes);

    /// <summary>One hoisted array: the op that declared it, the source variable name (also the hidden
    /// parameter's name in the owner), the storage name (a minted global for R1; the variable's own name
    /// for R2/R3), and the storage shape.</summary>
    private sealed record Hoisted(string Op, string Var, string StorageName, QType ElemType, int MaxLen);

    public static Result Run(QProgram program)
    {
        if (program.Operations.Count == 0) return new(program, Array.Empty<string>());
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];
        var opNames = program.Operations.Select(o => o.Name).ToHashSet();
        var notes = new List<string>();

        // ── 1. Collect and classify. Groups are per (op, name) — one parameter / one storage per name,
        //       sized to the longest declaration (see the header).
        var threaded = new Dictionary<string, List<Hoisted>>();   // R1: helper op → its threaded arrays (decl order)
        var topHoisted = new Dictionary<string, List<Hoisted>>(); // R2/R3: op → storage decls for its own top
        var replaced = new Dictionary<string, HashSet<string>>(); // per op: names whose decl sites become re-inits
        foreach (var op in program.Operations)
        {
            var groups = new List<(string Var, QType ElemType, int MaxLen, bool AllTopLevel)>();
            Collect(op.Body, topLevel: true, groups);
            foreach (var g in groups)
            {
                var isEntry = op == entry;
                var isBit = g.ElemType == QType.Bit;
                if (g.AllTopLevel && (isEntry || isBit)) continue;   // already legal where it stands

                if (!isEntry && !isBit)
                {
                    // R1 — threading; storage is a minted global in the entry body.
                    var h = new Hoisted(op.Name, g.Var, $"{op.Name.Replace('.', '_')}_{g.Var}", g.ElemType, g.MaxLen);
                    (threaded.TryGetValue(op.Name, out var list) ? list : threaded[op.Name] = new()).Add(h);
                    notes.Add($"array local `{h.Var}` in `{op.Name}` lowered to a hidden array parameter backed by global `{h.StorageName}` (OpenQASM: arrays enter a def only by reference)");
                }
                else
                {
                    // R2/R3 — the declaration hoists to the top of its own scope under its own name.
                    var h = new Hoisted(op.Name, g.Var, g.Var, g.ElemType, g.MaxLen);
                    (topHoisted.TryGetValue(op.Name, out var list) ? list : topHoisted[op.Name] = new()).Add(h);
                    notes.Add($"array local `{h.Var}` in `{op.Name}` hoisted to the top of its scope (OpenQASM: no classical declarations inside control-flow blocks)");
                }
                (replaced.TryGetValue(op.Name, out var set) ? set : replaced[op.Name] = new()).Add(g.Var);
            }
        }
        if (threaded.Count == 0 && topHoisted.Count == 0) return new(program, Array.Empty<string>());

        // ── 2. Thread transitively: an op that (directly or through other defs) calls an R1 owner cannot
        //       see the global backing either, so it gains pass-through parameters. extras[op] is the op's
        //       appended-parameter plan, in order: own arrays first (param name = the source variable
        //       name, so body references need no rewriting), then pass-throughs (param name = the global's
        //       minted name), sorted for determinism. Fixpoint over the call DAG (cycles are QSEM011-rejected).
        var extras = new Dictionary<string, List<(string ParamName, Hoisted H)>>();
        foreach (var (opName, own) in threaded)
            extras[opName] = own.Select(h => (h.Var, h)).ToList();

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var op in program.Operations)
            {
                if (op == entry) continue;                                // the entry names globals directly
                var callees = new HashSet<string>();
                CollectCalls(op.Body, opNames, callees);
                foreach (var callee in callees)
                {
                    if (callee == op.Name || !extras.TryGetValue(callee, out var calleeExtras)) continue;
                    var mine = extras.TryGetValue(op.Name, out var list) ? list : extras[op.Name] = new();
                    foreach (var (_, h) in calleeExtras)
                        if (!mine.Any(e => e.H.StorageName == h.StorageName))
                        {
                            mine.Add((h.StorageName, h));                 // pass-through: named after the global it carries
                            changed = true;
                        }
                }
            }
        }
        foreach (var (opName, list) in extras)                            // own params keep decl order; pass-throughs sort stably behind them
        {
            var own = list.Where(e => e.H.Op == opName).ToList();
            var thru = list.Where(e => e.H.Op != opName).OrderBy(e => e.H.StorageName, StringComparer.Ordinal).ToList();
            list.Clear(); list.AddRange(own); list.AddRange(thru);
        }

        // ── 3. Rewrite every op: append hidden parameters, prepend hoisted storage, turn owned
        //       declaration sites into element-wise re-initialization, and hand the right name to every
        //       call site.
        var outOps = new List<QOperation>(program.Operations.Count);
        foreach (var op in program.Operations)
        {
            var isEntry = op == entry;
            var owned = replaced.TryGetValue(op.Name, out var set) ? set : new HashSet<string>();
            var myExtras = extras.TryGetValue(op.Name, out var ex) ? ex : new List<(string, Hoisted)>();

            var body = Rewrite(op.Body, op, isEntry, owned, extras, opNames);
            var storage = new List<QStmt>();
            if (isEntry)
                // R1 backing globals — helpers' arrays, in op source order — live at the global top.
                storage.AddRange(program.Operations.Where(o => o != entry)
                    .SelectMany(o => threaded.TryGetValue(o.Name, out var g) ? g : Enumerable.Empty<Hoisted>())
                    .Select(StorageDecl));
            if (topHoisted.TryGetValue(op.Name, out var tops))            // R2/R3 storage at this op's own top
                storage.AddRange(tops.Select(StorageDecl));
            if (storage.Count > 0) body = storage.Concat(body).ToList();

            outOps.Add(op with
            {
                Params = myExtras.Count == 0
                    ? op.Params
                    : op.Params.Concat(myExtras.Select(e => new QParam(e.Item1, e.Item2.ElemType, null) { IsArray = true })).ToList(),
                Body = body,
            });
        }
        return new(program with { Operations = outOps }, notes);
    }

    private static QStmt StorageDecl(Hoisted h) =>
        new QDecl(false, h.ElemType, h.StorageName, new QArrayNew(h.ElemType, h.MaxLen)) { IsArray = true };

    /// <summary>The declarations this pass owns: any typed array local. (An untyped or uninitialized
    /// array is QSEM029 — validation rejects it long before the backend runs.)</summary>
    private static bool IsArrayLocal(QStmt s, out QDecl decl)
    {
        decl = (s as QDecl)!;
        return s is QDecl { IsArray: true, Type: not null };
    }

    private static int LengthOf(QDecl d) => d.Value switch
    {
        QArrayLiteral l => l.Elements.Count,
        QArrayNew n => n.Length,
        _ => throw new InvalidOperationException($"QINTERNAL: array `{d.Name}` reached hoisting without an array initializer"),
    };

    private static void Collect(IReadOnlyList<QStmt> stmts, bool topLevel,
        List<(string Var, QType ElemType, int MaxLen, bool AllTopLevel)> groups)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QDecl when IsArrayLocal(s, out var d):
                    var i = groups.FindIndex(g => g.Var == d.Name);
                    if (i < 0) groups.Add((d.Name, d.Type!.Value, LengthOf(d), topLevel));
                    else groups[i] = (d.Name, groups[i].ElemType, Math.Max(groups[i].MaxLen, LengthOf(d)), false);
                    break;
                case QIf f: Collect(f.Then, false, groups); Collect(f.Else, false, groups); break;
                case QFor f: Collect(f.Body, false, groups); break;
                case QWhile w: Collect(w.Body, false, groups); break;
                case QRepeat r: Collect(r.Body, false, groups); break;
                case QConjugate c: Collect(c.Within, false, groups); Collect(c.Apply, false, groups); break;
            }
    }

    private static void CollectCalls(IReadOnlyList<QStmt> stmts, HashSet<string> opNames, HashSet<string> into)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QGate g when opNames.Contains(g.Name): into.Add(g.Name); break;
                case QIf i: CollectCalls(i.Then, opNames, into); CollectCalls(i.Else, opNames, into); break;
                case QFor f: CollectCalls(f.Body, opNames, into); break;
                case QWhile w: CollectCalls(w.Body, opNames, into); break;
                case QRepeat r: CollectCalls(r.Body, opNames, into); break;
                case QConjugate c: CollectCalls(c.Within, opNames, into); CollectCalls(c.Apply, opNames, into); break;
            }
    }

    private static IReadOnlyList<QStmt> Rewrite(IReadOnlyList<QStmt> stmts, QOperation op, bool isEntry,
        HashSet<string> owned,
        Dictionary<string, List<(string ParamName, Hoisted H)>> extras, HashSet<string> opNames)
    {
        var result = new List<QStmt>(stmts.Count);
        foreach (var s in stmts)
            switch (s)
            {
                // A declaration site becomes element-wise re-initialization of the moved name — the
                // element expressions are emitted IN PLACE, so initializers referencing locals (or
                // measured values) evaluate exactly where the source evaluated them.
                case QDecl when IsArrayLocal(s, out var d) && owned.Contains(d.Name):
                    switch (d.Value)
                    {
                        case QArrayLiteral l:
                            for (var i = 0; i < l.Elements.Count; i++)
                                result.Add(new QAssign(d.Name, l.Elements[i]) { Index = new QNumLit(i) });
                            break;
                        case QArrayNew n:
                            for (var i = 0; i < n.Length; i++)
                                result.Add(new QAssign(d.Name, Zero(n.ElementType)) { Index = new QNumLit(i) });
                            break;
                    }
                    break;

                // A call to an op with hidden parameters: supply them, in the callee's appended order.
                // The entry names the global backing directly (its body IS the global scope); a def hands
                // on its own parameter that carries the same global.
                case QGate g when opNames.Contains(g.Name) && extras.TryGetValue(g.Name, out var calleeExtras) && calleeExtras.Count > 0:
                    var args = g.Args.ToList();
                    foreach (var (_, h) in calleeExtras)
                        args.Add(new QTextArg(new QNameRef(ArgNameFor(h, op, isEntry, extras))));
                    result.Add(g with { Args = args });
                    break;

                case QIf i: result.Add(i with { Then = Rewrite(i.Then, op, isEntry, owned, extras, opNames), Else = Rewrite(i.Else, op, isEntry, owned, extras, opNames) }); break;
                case QFor f: result.Add(f with { Body = Rewrite(f.Body, op, isEntry, owned, extras, opNames) }); break;
                case QWhile w: result.Add(w with { Body = Rewrite(w.Body, op, isEntry, owned, extras, opNames) }); break;
                case QRepeat r: result.Add(r with { Body = Rewrite(r.Body, op, isEntry, owned, extras, opNames) }); break;
                case QConjugate c: result.Add(c with { Within = Rewrite(c.Within, op, isEntry, owned, extras, opNames), Apply = Rewrite(c.Apply, op, isEntry, owned, extras, opNames) }); break;
                default: result.Add(s); break;
            }
        return result;
    }

    /// <summary>The name THIS op uses for the global <paramref name="h"/> carries: the entry names the
    /// backing array itself; a def must use its own (owner or pass-through) parameter. A def calling an
    /// owner without carrying the global is a threading bug — fail loudly, never emit a dangling name.</summary>
    private static string ArgNameFor(Hoisted h, QOperation caller, bool isEntry,
        Dictionary<string, List<(string ParamName, Hoisted H)>> extras)
    {
        if (isEntry) return h.StorageName;
        var mine = extras.TryGetValue(caller.Name, out var list) ? list : null;
        return mine?.FirstOrDefault(e => e.H.StorageName == h.StorageName).ParamName
            ?? throw new InvalidOperationException(
                $"QINTERNAL: `{caller.Name}` calls an op needing `{h.StorageName}` but was never threaded a parameter for it");
    }

    private static QExpr Zero(QType type) =>
        new QText(type is QType.Float or QType.Angle ? new QLit("0.0") : new QNumLit(0));
}
