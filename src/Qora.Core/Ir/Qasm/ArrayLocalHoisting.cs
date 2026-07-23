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
    private sealed record Hoisted(string Op, int DeclId, string Var, QType ElemType, int Len, bool Threaded);

    public static Result Run(QProgram program)
    {
        if (program.Operations.Count == 0) return new(program, Array.Empty<string>());
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];
        var opNames = program.Operations.Select(o => o.Name).ToHashSet();
        var notes = new List<string>();

        // ── 1. Collect and classify — ONE entry per DECLARATION, keyed by its stable node Id. Two
        //       declarations that merely share a name (disjoint sibling blocks) are DIFFERENT variables:
        //       merging them into one storage sized to the longest silently gave the shorter one the
        //       other's length (its `.Count` lowers to `sizeof(storage)`) and the first one's element type.
        var byOp = new Dictionary<string, List<Hoisted>>();    // op → its hoisted arrays, decl order
        var replaced = new Dictionary<string, HashSet<int>>(); // per op: DECL IDS whose sites become re-inits
        foreach (var op in program.Operations)
        {
            var decls = new List<(int DeclId, string Var, QType ElemType, int Len, bool TopLevel)>();
            Collect(op.Body, topLevel: true, decls);
            foreach (var g in decls)
            {
                var isEntry = op == entry;
                var isBit = g.ElemType == QType.Bit;
                if (g.TopLevel && (isEntry || isBit)) continue;      // already legal where it stands

                var threaded = !isEntry && !isBit;                   // R1 vs R2/R3
                (byOp.TryGetValue(op.Name, out var list) ? list : byOp[op.Name] = new())
                    .Add(new Hoisted(op.Name, g.DeclId, g.Var, g.ElemType, g.Len, threaded));
                (replaced.TryGetValue(op.Name, out var set) ? set : replaced[op.Name] = new()).Add(g.DeclId);
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
        var storageName = new Dictionary<(string Op, int DeclId), string>();
        var refName = new Dictionary<(string Op, int DeclId), string>();
        foreach (var (opName, hs) in byOp)
            foreach (var h in hs)
            {
                if (h.Threaded)                                      // R1: separate global backing + parameter
                {
                    storageName[(h.Op, h.DeclId)] = Ph($"{h.Op.Replace('.', '_')}_{h.Var}");
                    refName[(h.Op, h.DeclId)] = Ph(h.Var);
                    notes.Add($"array local `{h.Var}` in `{h.Op}` lowered to a hidden array-reference parameter backed by a global (OpenQASM: arrays enter a def only by reference)");
                }
                else                                                 // R2/R3: one placeholder, declared-as and referred-by
                {
                    var p = Ph(h.Var);
                    storageName[(h.Op, h.DeclId)] = refName[(h.Op, h.DeclId)] = p;
                    notes.Add($"array local `{h.Var}` in `{h.Op}` hoisted to the top of its scope (OpenQASM: no classical declaration inside a control-flow block)");
                }
            }

        // ── 3. Thread transitively: an op that (directly or through other defs) calls an R1 owner cannot
        //       see the global backing either, so it gains a PASS-THROUGH parameter. extras[op] fixes the
        //       appended-parameter ORDER — own arrays first (decl order), then pass-throughs (stable) —
        //       keyed by the owning (op, var) so the callee's storage is unambiguous. Fixpoint over the
        //       call DAG (cycles are QSEM011-rejected). Parameter NAMES are assigned per op in step 4.
        var extras = new Dictionary<string, List<(string Op, int DeclId)>>();   // op → appended params, in order, as owner keys
        foreach (var (opName, hs) in byOp)
            extras[opName] = hs.Where(h => h.Threaded).Select(h => (h.Op, h.DeclId)).ToList();

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
        var paramName = new Dictionary<(string Op, (string Op, int DeclId) Key), string>();  // (holder op, owner key) → slot name
        foreach (var (opName, appended) in extras)
            foreach (var key in appended)
                paramName[(opName, key)] = key.Op == opName
                    ? refName[key]
                    : Ph($"{key.Op.Replace('.', '_')}_{byOp[key.Op].First(h => h.DeclId == key.DeclId).Var}");

        // ── 5. Rewrite every op: append hidden parameters, prepend hoisted storage, turn owned declaration
        //       sites into element-wise re-initialization (under the chosen refName), and hand the right
        //       name to every call site.
        var outOps = new List<QOperation>(program.Operations.Count);
        foreach (var op in program.Operations)
        {
            var isEntry = op == entry;
            var owned = replaced.TryGetValue(op.Name, out var set) ? set : new HashSet<int>();
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
                    .Select(h => StorageDecl(h, storageName[(h.Op, h.DeclId)])));
            if (byOp.TryGetValue(op.Name, out var mine))                  // R2/R3 storage at this op's own top
                storage.AddRange(mine.Where(h => !h.Threaded)
                    .Select(h => StorageDecl(h, storageName[(h.Op, h.DeclId)])));
            if (storage.Count > 0) body = storage.Concat(body).ToList();

            var appended = extras.TryGetValue(op.Name, out var ap) ? ap : new List<(string Op, int DeclId)>();
            outOps.Add(op with
            {
                Params = appended.Count == 0
                    ? op.Params
                    : op.Params.Concat(appended.Select(key =>
                        new QParam(paramName[(op.Name, key)], byOp[key.Op].First(h => h.DeclId == key.DeclId).ElemType, null) { IsArray = true })).ToList(),
                Body = body,
            });
        }
        return new(program with { Operations = outOps }, notes);
    }

    private static QStmt StorageDecl(Hoisted h, string name) =>
        new QDecl(false, h.ElemType, name, new QArrayNew(h.ElemType, h.Len)) { IsArray = true };

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

    /// <summary>Every array-local DECLARATION in an operation, one entry each, carrying its OWN length,
    /// element type and placement. Declarations are never merged by name: two same-named arrays in disjoint
    /// blocks are distinct variables and each gets its own storage.</summary>
    private static void Collect(IReadOnlyList<QStmt> stmts, bool topLevel,
        List<(int DeclId, string Var, QType ElemType, int Len, bool TopLevel)> decls)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QDecl when IsArrayLocal(s, out var d):
                    decls.Add((d.Id, d.Name, d.Type!.Value, LengthOf(d), topLevel));
                    break;
                case QIf f: Collect(f.Then, false, decls); Collect(f.Else, false, decls); break;
                case QFor f: Collect(f.Body, false, decls); break;
                case QWhile w: Collect(w.Body, false, decls); break;
                case QRepeat r: Collect(r.Body, false, decls); break;
                case QConjugate c: Collect(c.Within, false, decls); Collect(c.Apply, false, decls); break;
            }
    }

    private static void CollectCalls(IReadOnlyList<QStmt> stmts, HashSet<string> opNames, HashSet<string> into)
    {
        foreach (var s in stmts)
        {
            // A `function` is called as a QCallNode INSIDE AN EXPRESSION, never as a QGate statement, so the
            // statement switch below cannot see it. Missing those edges left a caller unthreaded, and the
            // hidden argument it owed its callee was then never supplied. Uses the shared enumerators rather
            // than a second hand-rolled expression walk.
            foreach (var tree in QNodes.ExpressionSites(s))
                foreach (var call in QNodes.CallsIn(tree))
                    if (opNames.Contains(call.Name)) into.Add(call.Name);
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
    }

    /// <summary><paramref name="active"/> maps a shadowed source variable to its freshened array name, IN
    /// EFFECT for this statement list and everything nested (it gains an entry when an owned array's
    /// declaration is reached, so references before that decl — which mean the shadowed parameter — are
    /// untouched). <paramref name="forceApply"/> is false in the overwhelmingly common no-shadow case, where
    /// every renaming call short-circuits and statements pass through unchanged.</summary>
    private static IReadOnlyList<QStmt> Rewrite(IReadOnlyList<QStmt> stmts, QOperation op, bool isEntry,
        HashSet<int> owned,
        Dictionary<string, List<(string Op, int DeclId)>> extras,
        Dictionary<(string Op, (string Op, int DeclId) Key), string> paramName,
        Dictionary<(string Op, int DeclId), string> storageName,
        Dictionary<(string Op, int DeclId), string> refName,
        HashSet<string> opNames,
        Dictionary<string, string> inherited, bool forceApply)
    {
        var active = new Dictionary<string, string>(inherited);
        // The facts a call site needs to be COMPLETED, gathered once for this body. Statement calls are
        // completed by the QGate arm below; expression calls are completed inside the reference walk.
        var fix = new CallFix(op, isEntry, extras, paramName, storageName);
        var result = new List<QStmt>(stmts.Count);
        foreach (var s in stmts)
            switch (s)
            {
                // A declaration site becomes element-wise re-initialization of the moved name (its chosen
                // refName). Element expressions are rewritten with the CURRENT active map and emitted IN
                // PLACE, so initializers referencing other locals (or measured values) evaluate exactly
                // where the source evaluated them. The array's own rename takes effect for LATER references.
                case QDecl when IsArrayLocal(s, out var d) && owned.Contains(d.Id):
                    var target = refName.TryGetValue((op.Name, d.Id), out var rn) ? rn : d.Name;
                    switch (d.Value)
                    {
                        case QArrayLiteral l:
                            for (var i = 0; i < l.Elements.Count; i++)
                                result.Add(new QAssign(target, RenameExpr(l.Elements[i], active, forceApply, fix)) { Index = new QNumLit(i) });
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
                    var args = g.Args.Select(a => RenameArg(a, active, forceApply, fix)).ToList();
                    foreach (var key in calleeExtras)
                        args.Add(new QTextArg(new QNameRef(ArgNameFor(key, op, isEntry, paramName, storageName))));
                    result.Add(g with { Args = args });
                    break;

                case QIf i:
                    result.Add(i with
                    {
                        Cond = RenameCond(i.Cond, active, forceApply, fix),
                        Then = Rewrite(i.Then, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                        Else = Rewrite(i.Else, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                    });
                    break;
                case QFor f:
                    result.Add(f with
                    {
                        From = RenameNode(f.From, active, forceApply, fix)!, To = RenameNode(f.To, active, forceApply, fix)!,
                        Body = Rewrite(f.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                    });
                    break;
                case QWhile w:
                    result.Add(w with { Cond = RenameCond(w.Cond, active, forceApply, fix), Body = Rewrite(w.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply) });
                    break;
                case QRepeat r:
                    result.Add(r with { Body = Rewrite(r.Body, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply), Until = RenameCond(r.Until, active, forceApply, fix) });
                    break;
                case QConjugate c:
                    result.Add(c with
                    {
                        Within = Rewrite(c.Within, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                        Apply = Rewrite(c.Apply, op, isEntry, owned, extras, paramName, storageName, refName, opNames, active, forceApply),
                    });
                    break;

                default: result.Add(RenameStmt(s, active, forceApply, fix)); break;
            }
        return result;
    }

    /// <summary>The name the CALLER uses to forward a threaded array to a callee: the entry names the
    /// backing global directly; a def uses its own (owner or pass-through) parameter for that array. A def
    /// calling an owner without a matching parameter is a threading bug — fail loudly, never a dangling
    /// name.</summary>
    private static string ArgNameFor((string Op, int DeclId) key, QOperation caller, bool isEntry,
        Dictionary<(string Op, (string Op, int DeclId) Key), string> paramName,
        Dictionary<(string Op, int DeclId), string> storageName)
    {
        if (isEntry) return storageName[key];
        return paramName.TryGetValue((caller.Name, key), out var slot) ? slot
            : throw new InvalidOperationException(
                $"QINTERNAL: `{caller.Name}` calls an op needing `{storageName[key]}` but was never threaded a parameter for it");
    }

    private static QExpr Zero(QType type) =>
        new QText(type is QType.Float or QType.Angle ? new QLit("0.0") : new QNumLit(0));

    // --- reference renaming (only ever non-trivial in the rare shadow case; short-circuits otherwise) ---

    /// <summary>What COMPLETING a call needs: a callee owning array locals gained hidden reference
    /// parameters, and every call site must supply them. The statement form is completed in <c>Rewrite</c>;
    /// this carries the same facts into the reference walk so an EXPRESSION-position call (a <c>function</c>
    /// call, which is a <see cref="QCallNode"/> inside a tree) is completed identically. Default-constructed
    /// (<c>Extras</c> null) means "nothing to complete".</summary>
    private readonly record struct CallFix(
        QOperation Caller, bool IsEntry,
        Dictionary<string, List<(string Op, int DeclId)>> Extras,
        Dictionary<(string Op, (string Op, int DeclId) Key), string> ParamName,
        Dictionary<(string Op, int DeclId), string> StorageName)
    {
        /// <summary>The hidden arguments this callee needs, or null when it needs none.</summary>
        public List<(string Op, int DeclId)>? For(string callee) =>
            Extras is not null && Extras.TryGetValue(callee, out var e) && e.Count > 0 ? e : null;

        /// <summary>True when nothing in this walk can change — no rename in effect AND no call to complete —
        /// so every visitor may short-circuit. Renaming alone is the common case and used to gate the whole
        /// walk; completing a call is a separate obligation that must not be skipped along with it.</summary>
        public bool Idle(Dictionary<string, string> map, bool on) =>
            (!on || map.Count == 0) && (Extras is null || Extras.Count == 0);
    }

    private static QStmt RenameStmt(QStmt s, Dictionary<string, string> map, bool on, CallFix fix)
    {
        if (fix.Idle(map, on)) return s;
        return s switch
        {
            QGate g => g with { Args = g.Args.Select(a => RenameArg(a, map, on, fix)).ToList() },
            QAssign a => a with { Name = N(a.Name, map), Index = RenameNode(a.Index, map, on, fix), Value = RenameExpr(a.Value, map, on, fix) },
            QDecl d => d with { Value = RenameExpr(d.Value, map, on, fix) },
            // A `return` VALUE is an ordinary expression and must be renamed like any other. Omitting it let a
            // returned array reference keep its source name while a shadowing declaration had taken that name
            // over — so the function returned a DIFFERENT array's contents, with no diagnostic anywhere.
            QReturn r => r with { Value = RenameExpr(r.Value, map, on, fix) },
            _ => s,
        };
    }

    private static QArg RenameArg(QArg arg, Dictionary<string, string> map, bool on, CallFix fix)
    {
        if (fix.Idle(map, on)) return arg;
        return arg switch
        {
            QQubitArg q => new QQubitArg(N(q.Reg, map), RenameNode(q.Index, map, on, fix)!),
            QTextArg t => t with { Tree = RenameNode(t.Tree, map, on, fix) },
            _ => arg,
        };
    }

    private static QExpr RenameExpr(QExpr expr, Dictionary<string, string> map, bool on, CallFix fix)
    {
        if (fix.Idle(map, on)) return expr;
        return expr switch
        {
            QText t => t with { Tree = RenameNode(t.Tree, map, on, fix) },
            QArrayLiteral l => l with { Elements = l.Elements.Select(e => RenameExpr(e, map, on, fix)).ToList() },
            _ => expr,
        };
    }

    private static QCond RenameCond(QCond cond, Dictionary<string, string> map, bool on, CallFix fix) =>
        fix.Idle(map, on) ? cond : cond with { Tree = RenameNode(cond.Tree, map, on, fix) };

    private static QNode? RenameNode(QNode? node, Dictionary<string, string> map, bool on, CallFix fix)
    {
        if (node is null || fix.Idle(map, on)) return node;
        return node switch
        {
            QNameRef r => map.TryGetValue(r.Name, out var m) ? new QNameRef(m) : r,
            QUnary u => u with { Operand = RenameNode(u.Operand, map, on, fix)! },
            QBinOp b => b with { Left = RenameNode(b.Left, map, on, fix)!, Right = RenameNode(b.Right, map, on, fix)! },
            QMember m => m with { Base = RenameNode(m.Base, map, on, fix)! },
            QIndexNode ix => ix with { Base = RenameNode(ix.Base, map, on, fix)!, Index = RenameNode(ix.Index, map, on, fix)! },
            // Rename the written arguments, then APPEND the callee's hidden array references in its own
            // appended order — the same completion the statement arm performs in Rewrite.
            QCallNode c => c with
            {
                Args = c.Args.Select(a => RenameNode(a, map, on, fix)!)
                    .Concat((fix.For(c.Name) ?? Enumerable.Empty<(string Op, int DeclId)>())
                        .Select(key => (QNode)new QNameRef(ArgNameFor(key, fix.Caller, fix.IsEntry, fix.ParamName, fix.StorageName))))
                    .ToList(),
            },
            _ => node,   // QNumLit, QLit
        };
    }

    private static string N(string name, Dictionary<string, string> map) => map.TryGetValue(name, out var m) ? m : name;
}
