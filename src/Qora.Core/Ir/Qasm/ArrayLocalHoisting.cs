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
///         operation Helper(q: Qubit) {             array[int, 3] Helper_tbl = {0, 0, 0};   // global backing
///             var tbl: int[] = [1, 2, 3];   →      def Helper(qubit q, mutable array[int, #dim = 1] tbl) {
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
/// copy) are the SAME logical variable: they share one parameter / one storage sized to the LONGEST
/// declaration; each site still re-initializes its own elements, and shorter sites never index past
/// their own proven length.
///
/// NAMES ARE MINTED AS UNIQUE PLACEHOLDERS, then prettified by the mangler. Every name this pass
/// introduces — a backing global, a hidden or pass-through parameter, a hoisted storage register — is a
/// <see cref="HoistName"/> placeholder <c>#hoist#{base}#{uid}</c> whose <c>uid</c> makes it unique across
/// the whole program by construction. So two DISTINCT logical entities can never share a spelling, and a
/// placeholder can never equal a user name (its <c>#</c> is not a legal identifier character) — WITHOUT
/// this pass having to enumerate the scope's inhabitants (the step whose incompleteness reopened
/// collisions in earlier revisions). <see cref="Passes.NameMangler"/> then recovers each placeholder's
/// <c>{base}</c> and runs its ordinary freshening: because the placeholders are already distinct map keys,
/// its same-name MERGE never fires on them, and its per-key freshening turns two arrays that wanted the
/// same base into <c>x</c> / <c>x_</c> and disambiguates any clash with a user name, gate, or keyword —
/// renaming that key's references in lockstep, as it does for every name. The pass rewrites each hoisted
/// array's in-scope references to its placeholder (the re-initialization site and every read/write from
/// the declaration onward in its block); references to a same-named shadowed parameter, before or outside
/// that scope, are left alone, and the mangler then gives the placeholder and the parameter distinct
/// emitted names.
///
/// Runs FIRST in <see cref="QasmBackend"/>, before <see cref="Passes.NameMangler"/>. The validator
/// deliberately has NO placement rule for array locals (the old QSEM012 arm was this target rule leaking
/// into the language). Deletability: if a future OpenQASM allows def-local arrays, delete this file and
/// remove the one call in <see cref="QasmBackend"/>.
/// </summary>
public static class ArrayLocalHoisting
{
    public sealed record Result(QProgram Program, IReadOnlyList<string> Notes);

    /// <summary>One hoisted array, keyed by its (owning op, source variable). The placeholder names are
    /// looked up in <c>storageName</c> (where it is DECLARED — a global for R1, a scope-top decl for R2/R3)
    /// and <c>refName</c> (how the OWNER's body refers to it — the parameter for R1, the same storage for
    /// R2/R3).</summary>
    private sealed record Hoisted(string Op, string Var, QType ElemType, int MaxLen, bool Threaded);

    public static Result Run(QProgram program)
    {
        if (program.Operations.Count == 0) return new(program, Array.Empty<string>());
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];
        var opNames = program.Operations.Select(o => o.Name).ToHashSet();
        var notes = new List<string>();

        // ── 1. Collect and classify. Groups are per (op, name) — one parameter / one storage per name,
        //       sized to the longest declaration (see the header).
        var byOp = new Dictionary<string, List<Hoisted>>();       // op → its hoisted arrays, decl order
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

                var threaded = !isEntry && !isBit;                   // R1 vs R2/R3
                (byOp.TryGetValue(op.Name, out var list) ? list : byOp[op.Name] = new())
                    .Add(new Hoisted(op.Name, g.Var, g.ElemType, g.MaxLen, threaded));
                (replaced.TryGetValue(op.Name, out var set) ? set : replaced[op.Name] = new()).Add(g.Var);
            }
        }
        if (byOp.Count == 0) return new(program, Array.Empty<string>());

        // ── 2. Mint a unique placeholder for every name this pass introduces. Uniqueness is the uid
        //       counter — NOT a scan of the scope — so no scope-inhabitant list can be incomplete (the
        //       earlier failure mode). storageName: where the array is DECLARED (a distinct global backing
        //       for R1, a scope-top decl for R2/R3). refName: how the owner's body refers to it (the
        //       parameter for R1, the same storage for R2/R3). NameMangler prettifies each placeholder to
        //       its base and disambiguates any clash; see the header.
        var uid = 0;
        string Ph(string baseName) => HoistName.Make(baseName, uid++);
        var storageName = new Dictionary<(string Op, string Var), string>();
        var refName = new Dictionary<(string Op, string Var), string>();
        foreach (var (opName, hs) in byOp)
            foreach (var h in hs)
            {
                if (h.Threaded)                                      // R1: separate global backing + parameter
                {
                    storageName[(h.Op, h.Var)] = Ph($"{h.Op.Replace('.', '_')}_{h.Var}");
                    refName[(h.Op, h.Var)] = Ph(h.Var);
                    notes.Add($"array local `{h.Var}` in `{h.Op}` lowered to a hidden array-reference parameter backed by a global (OpenQASM: arrays enter a def only by reference)");
                }
                else                                                 // R2/R3: one placeholder, declared-as and referred-by
                {
                    var p = Ph(h.Var);
                    storageName[(h.Op, h.Var)] = refName[(h.Op, h.Var)] = p;
                    notes.Add($"array local `{h.Var}` in `{h.Op}` hoisted to the top of its scope (OpenQASM: no classical declaration inside a control-flow block)");
                }
            }

        // ── 3. Thread transitively: an op that (directly or through other defs) calls an R1 owner cannot
        //       see the global backing either, so it gains a PASS-THROUGH parameter. extras[op] fixes the
        //       appended-parameter ORDER — own arrays first (decl order), then pass-throughs (stable) —
        //       keyed by the owning (op, var) so the callee's storage is unambiguous. Fixpoint over the
        //       call DAG (cycles are QSEM011-rejected). Parameter NAMES are assigned per op in step 4.
        var extras = new Dictionary<string, List<(string Op, string Var)>>();   // op → appended params, in order, as owner keys
        foreach (var (opName, hs) in byOp)
            extras[opName] = hs.Where(h => h.Threaded).Select(h => (h.Op, h.Var)).ToList();

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
                    foreach (var key in calleeExtras)
                        if (!mine.Contains(key)) { mine.Add(key); changed = true; }
                }
            }
        }
        foreach (var (opName, list) in extras)                            // own params keep decl order; pass-throughs sort stably behind them
        {
            var own = list.Where(k => k.Op == opName).ToList();
            var thru = list.Where(k => k.Op != opName)
                .OrderBy(k => storageName[k], StringComparer.Ordinal).ToList();
            list.Clear(); list.AddRange(own); list.AddRange(thru);
        }

        // ── 4. Each threaded array's forwarding SLOT in each holder op gets a name. An OWNED slot reuses the
        //       array's own parameter placeholder (refName); a PASS-THROUGH gets its own placeholder based
        //       on the backing global's name, for a readable emitted signature.
        var paramName = new Dictionary<(string Op, (string Op, string Var) Key), string>();  // (holder op, owner key) → slot name
        foreach (var (opName, appended) in extras)
            foreach (var key in appended)
                paramName[(opName, key)] = key.Op == opName
                    ? refName[key]
                    : Ph($"{key.Op.Replace('.', '_')}_{key.Var}");

        // ── 5. Rewrite every op: append hidden parameters, prepend hoisted storage, turn owned declaration
        //       sites into element-wise re-initialization (under the chosen refName), and hand the right
        //       name to every call site.
        var outOps = new List<QOperation>(program.Operations.Count);
        foreach (var op in program.Operations)
        {
            var isEntry = op == entry;
            var owned = replaced.TryGetValue(op.Name, out var set) ? set : new HashSet<string>();
            // Every hoisted array's body references are rewritten from its source name to its placeholder,
            // in effect only FROM the array's declaration onward (see Rewrite): the map starts EMPTY and
            // gains each rename when its decl is reached, so a reference to a same-named shadowed PARAMETER
            // before/outside the array's scope keeps its meaning. forceApply is on whenever the op has any
            // hoisted array (a placeholder always differs from the source name).
            var body = Rewrite(op.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames,
                new Dictionary<string, string>(), forceApply: owned.Count > 0);

            var storage = new List<QStmt>();
            if (isEntry)                                                  // R1 backing globals — helper arrays, source order
                storage.AddRange(program.Operations.Where(o => o != entry)
                    .SelectMany(o => byOp.TryGetValue(o.Name, out var hs) ? hs.Where(h => h.Threaded) : Enumerable.Empty<Hoisted>())
                    .Select(h => StorageDecl(h, storageName[(h.Op, h.Var)])));
            if (byOp.TryGetValue(op.Name, out var mine))                  // R2/R3 storage at this op's own top
                storage.AddRange(mine.Where(h => !h.Threaded)
                    .Select(h => StorageDecl(h, storageName[(h.Op, h.Var)])));
            if (storage.Count > 0) body = storage.Concat(body).ToList();

            var appended = extras.TryGetValue(op.Name, out var ap) ? ap : new List<(string Op, string Var)>();
            outOps.Add(op with
            {
                Params = appended.Count == 0
                    ? op.Params
                    : op.Params.Concat(appended.Select(key =>
                        new QParam(paramName[(op.Name, key)], byOp[key.Op].First(h => h.Var == key.Var).ElemType, null) { IsArray = true })).ToList(),
                Body = body,
            });
        }
        return new(program with { Operations = outOps }, notes);
    }

    private static QStmt StorageDecl(Hoisted h, string name) =>
        new QDecl(false, h.ElemType, name, new QArrayNew(h.ElemType, h.MaxLen)) { IsArray = true };

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

    /// <summary><paramref name="active"/> maps a shadowed source variable to its freshened array name, IN
    /// EFFECT for this statement list and everything nested (it gains an entry when an owned array's
    /// declaration is reached, so references before that decl — which mean the shadowed parameter — are
    /// untouched). <paramref name="forceApply"/> is false in the overwhelmingly common no-shadow case, where
    /// every renaming call short-circuits and statements pass through unchanged.</summary>
    private static IReadOnlyList<QStmt> Rewrite(IReadOnlyList<QStmt> stmts, QOperation op, bool isEntry,
        HashSet<string> owned,
        Dictionary<string, List<(string Op, string Var)>> extras,
        Dictionary<(string Op, (string Op, string Var) Key), string> paramName,
        Dictionary<(string Op, string Var), string> storageName,
        Dictionary<(string Op, string Var), string> refName,
        HashSet<string> opNames,
        Dictionary<string, string> inherited, bool forceApply)
    {
        var active = new Dictionary<string, string>(inherited);
        var result = new List<QStmt>(stmts.Count);
        foreach (var s in stmts)
            switch (s)
            {
                // A declaration site becomes element-wise re-initialization of the moved name (its chosen
                // refName). Element expressions are rewritten with the CURRENT active map and emitted IN
                // PLACE, so initializers referencing other locals (or measured values) evaluate exactly
                // where the source evaluated them. The array's own rename takes effect for LATER references.
                case QDecl when IsArrayLocal(s, out var d) && owned.Contains(d.Name):
                    var target = refName.TryGetValue((op.Name, d.Name), out var rn) ? rn : d.Name;
                    switch (d.Value)
                    {
                        case QArrayLiteral l:
                            for (var i = 0; i < l.Elements.Count; i++)
                                result.Add(new QAssign(target, RenameExpr(l.Elements[i], active, forceApply)) { Index = new QNumLit(i) });
                            break;
                        case QArrayNew n:
                            for (var i = 0; i < n.Length; i++)
                                result.Add(new QAssign(target, Zero(n.ElementType)) { Index = new QNumLit(i) });
                            break;
                    }
                    if (target != d.Name) active[d.Name] = target;
                    break;

                // A call to an op with hidden parameters: supply them, in the callee's appended order. The
                // entry names the global backing directly; a def hands on its own forwarding parameter. The
                // original arguments are rewritten with active (they may reference a renamed local array).
                case QGate g when opNames.Contains(g.Name) && extras.TryGetValue(g.Name, out var calleeExtras) && calleeExtras.Count > 0:
                    var args = g.Args.Select(a => RenameArg(a, active, forceApply)).ToList();
                    foreach (var key in calleeExtras)
                        args.Add(new QTextArg(new QNameRef(ArgNameFor(key, op, isEntry, paramName, storageName))));
                    result.Add(g with { Args = args });
                    break;

                case QIf i:
                    result.Add(i with
                    {
                        Cond = RenameCond(i.Cond, active, forceApply),
                        Then = Rewrite(i.Then, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                        Else = Rewrite(i.Else, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                    });
                    break;
                case QFor f:
                    result.Add(f with
                    {
                        From = RenameNode(f.From, active, forceApply)!, To = RenameNode(f.To, active, forceApply)!,
                        Body = Rewrite(f.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                    });
                    break;
                case QWhile w:
                    result.Add(w with { Cond = RenameCond(w.Cond, active, forceApply), Body = Rewrite(w.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply) });
                    break;
                case QRepeat r:
                    result.Add(r with { Body = Rewrite(r.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply), Until = RenameCond(r.Until, active, forceApply) });
                    break;
                case QConjugate c:
                    result.Add(c with
                    {
                        Within = Rewrite(c.Within, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                        Apply = Rewrite(c.Apply, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                    });
                    break;

                default: result.Add(RenameStmt(s, active, forceApply)); break;
            }
        return result;
    }

    /// <summary>The name the CALLER uses to forward a threaded array to a callee: the entry names the
    /// backing global directly; a def uses its own (owner or pass-through) parameter for that array. A def
    /// calling an owner without a matching parameter is a threading bug — fail loudly, never a dangling
    /// name.</summary>
    private static string ArgNameFor((string Op, string Var) key, QOperation caller, bool isEntry,
        Dictionary<(string Op, (string Op, string Var) Key), string> paramName,
        Dictionary<(string Op, string Var), string> storageName)
    {
        if (isEntry) return storageName[key];
        return paramName.TryGetValue((caller.Name, key), out var slot) ? slot
            : throw new InvalidOperationException(
                $"QINTERNAL: `{caller.Name}` calls an op needing `{storageName[key]}` but was never threaded a parameter for it");
    }

    private static QExpr Zero(QType type) =>
        new QText(type is QType.Float or QType.Angle ? new QLit("0.0") : new QNumLit(0));

    // --- reference renaming (only ever non-trivial in the rare shadow case; short-circuits otherwise) ---

    private static QStmt RenameStmt(QStmt s, Dictionary<string, string> map, bool on)
    {
        if (!on || map.Count == 0) return s;
        return s switch
        {
            QGate g => g with { Args = g.Args.Select(a => RenameArg(a, map, on)).ToList() },
            QAssign a => a with { Name = N(a.Name, map), Index = RenameNode(a.Index, map, on), Value = RenameExpr(a.Value, map, on) },
            QDecl d => d with { Value = RenameExpr(d.Value, map, on) },
            _ => s,
        };
    }

    private static QArg RenameArg(QArg arg, Dictionary<string, string> map, bool on)
    {
        if (!on || map.Count == 0) return arg;
        return arg switch
        {
            QQubitArg q => new QQubitArg(N(q.Reg, map), RenameNode(q.Index, map, on)!),
            QTextArg t => t with { Tree = RenameNode(t.Tree, map, on) },
            _ => arg,
        };
    }

    private static QExpr RenameExpr(QExpr expr, Dictionary<string, string> map, bool on)
    {
        if (!on || map.Count == 0) return expr;
        return expr switch
        {
            QText t => t with { Tree = RenameNode(t.Tree, map, on) },
            QArrayLiteral l => l with { Elements = l.Elements.Select(e => RenameExpr(e, map, on)).ToList() },
            _ => expr,
        };
    }

    private static QCond RenameCond(QCond cond, Dictionary<string, string> map, bool on) =>
        !on || map.Count == 0 ? cond : cond with { Tree = RenameNode(cond.Tree, map, on) };

    private static QNode? RenameNode(QNode? node, Dictionary<string, string> map, bool on)
    {
        if (!on || map.Count == 0 || node is null) return node;
        return node switch
        {
            QNameRef r => map.TryGetValue(r.Name, out var m) ? new QNameRef(m) : r,
            QUnary u => u with { Operand = RenameNode(u.Operand, map, on)! },
            QBinOp b => b with { Left = RenameNode(b.Left, map, on)!, Right = RenameNode(b.Right, map, on)! },
            QMember m => m with { Base = RenameNode(m.Base, map, on)! },
            QIndexNode ix => ix with { Base = RenameNode(ix.Base, map, on)!, Index = RenameNode(ix.Index, map, on)! },
            QCallNode c => c with { Arg = RenameNode(c.Arg, map, on) },
            _ => node,   // QNumLit, QLit
        };
    }

    private static string N(string name, Dictionary<string, string> map) => map.TryGetValue(name, out var m) ? m : name;
}
