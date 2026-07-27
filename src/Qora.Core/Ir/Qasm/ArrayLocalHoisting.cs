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
/// Same-named declarations in disjoint lexical scopes are distinct variables. Stable declaration Ids,
/// rather than source spelling, key their storage and hidden parameters, so neither declaration can
/// borrow the other one's element type or length.
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
/// Runs at the OpenQASM target boundary, before <see cref="Passes.NameMangler"/>. The validator
/// deliberately has NO placement rule for array locals (the old QSEM012 arm was this target rule leaking
/// into the language). Deletability: if a future OpenQASM allows def-local arrays, delete this file and
/// remove the one call in <see cref="QasmBackend"/>.
/// </summary>
internal static class ArrayLocalHoisting
{
    public sealed record Result(
        QProgram Program,
        IReadOnlyList<string> Notes,
        OpenQasmTargetFacts Facts);

    /// <summary>One hoisted array, keyed by its owning operation Id and declaration Id. The placeholder names are
    /// looked up in <c>storageName</c> (where it is DECLARED — a global for R1, a scope-top decl for R2/R3)
    /// and <c>refName</c> (how the OWNER's body refers to it — the parameter for R1, the same storage for
    /// R2/R3).</summary>
    private readonly record struct OwnerKey(int OperationId, int DeclarationId);

    private sealed record Hoisted(
        int OperationId,
        int DeclarationId,
        string VariableName,
        QType ElementType,
        int Length,
        bool Threaded);

    public static Result Run(QProgram program)
    {
        var facts = new OpenQasmTargetFactBuilder();
        if (program.Operations.Count == 0)
            return new(program, Array.Empty<string>(), facts.Build());
        var entry = program.Operations.FirstOrDefault(o => o.Name == "Main") ?? program.Operations[0];
        var operationsById = new Dictionary<int, QOperation>();
        foreach (var operation in program.Operations)
            if (!operationsById.TryAdd(operation.Id, operation))
                throw new InvalidOperationException(
                    $"QINTERNAL: duplicate operation Id {operation.Id} reached array-local hoisting");
        var calleesByOperation = new Dictionary<int, HashSet<int>>();
        foreach (var operation in program.Operations)
        {
            var callees = new HashSet<int>();
            CollectCalls(operation.Body, operationsById, callees);
            calleesByOperation.Add(operation.Id, callees);
        }
        var notes = new List<string>();

        // ── 1. Collect and classify — ONE entry per DECLARATION, keyed by its stable node Id. Two
        //       declarations that merely share a name (disjoint sibling blocks) are DIFFERENT variables:
        //       merging them into one storage sized to the longest silently gave the shorter one the
        //       other's length (its `.Count` lowers to `sizeof(storage)`) and the first one's element type.
        var byOp = new Dictionary<int, List<Hoisted>>();
        var replaced = new Dictionary<int, HashSet<int>>();
        foreach (var op in program.Operations)
        {
            var decls = new List<(int DeclId, string Var, QType ElemType, int Len, bool TopLevel)>();
            Collect(op.Body, topLevel: true, decls);
            foreach (var g in decls)
            {
                var isEntry = op.Id == entry.Id;
                var isBit = g.ElemType == QType.Bit;
                if (g.TopLevel && (isEntry || isBit)) continue;      // already legal where it stands

                var threaded = !isEntry && !isBit;                   // R1 vs R2/R3
                (byOp.TryGetValue(op.Id, out var list) ? list : byOp[op.Id] = new())
                    .Add(new Hoisted(op.Id, g.DeclId, g.Var, g.ElemType, g.Len, threaded));
                (replaced.TryGetValue(op.Id, out var set) ? set : replaced[op.Id] = new()).Add(g.DeclId);
            }
        }
        if (byOp.Count == 0)
            return new(program, Array.Empty<string>(), facts.Build());

        // ── 2. Mint a unique placeholder for every name this pass introduces. Uniqueness is the uid
        //       counter — NOT a scan of the scope — so no scope-inhabitant list can be incomplete (the
        //       earlier failure mode). storageName: where the array is DECLARED (a distinct global backing
        //       for R1, a scope-top decl for R2/R3). refName: how the owner's body refers to it (the
        //       parameter for R1, the same storage for R2/R3). NameMangler prettifies each placeholder to
        //       its base and disambiguates any clash; see the header.
        var uid = 0;
        string Ph(string baseName) => HoistName.Make(baseName, uid++);
        var storageName = new Dictionary<OwnerKey, string>();
        var refName = new Dictionary<OwnerKey, string>();
        foreach (var (_, hs) in byOp)
            foreach (var h in hs)
            {
                var key = new OwnerKey(h.OperationId, h.DeclarationId);
                var operationName = operationsById[h.OperationId].Name;
                if (h.Threaded)                                      // R1: separate global backing + parameter
                {
                    storageName[key] = Ph($"{operationName.Replace('.', '_')}_{h.VariableName}");
                    refName[key] = Ph(h.VariableName);
                    notes.Add($"array local `{h.VariableName}` in `{operationName}` lowered to a hidden array-reference parameter backed by a global (OpenQASM: arrays enter a def only by reference)");
                }
                else                                                 // R2/R3: one placeholder, declared-as and referred-by
                {
                    var p = Ph(h.VariableName);
                    storageName[key] = refName[key] = p;
                    notes.Add($"array local `{h.VariableName}` in `{operationName}` hoisted to the top of its scope (OpenQASM: no classical declaration inside a control-flow block)");
                }
            }

        // ── 3. Thread transitively: an op that (directly or through other defs) calls an R1 owner cannot
        //       see the global backing either, so it gains a PASS-THROUGH parameter. extras[op] fixes the
        //       appended-parameter ORDER — own arrays first (decl order), then pass-throughs (stable) —
        //       keyed by the owning (operation Id, declaration Id) so the callee's storage is unambiguous. Fixpoint over the
        //       call DAG (cycles are QSEM011-rejected). Parameter NAMES are assigned per op in step 4.
        var extras = new Dictionary<int, List<OwnerKey>>();
        foreach (var (operationId, hs) in byOp)
            extras[operationId] = hs
                .Where(h => h.Threaded)
                .Select(h => new OwnerKey(h.OperationId, h.DeclarationId))
                .ToList();

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var op in program.Operations)
            {
                if (op.Id == entry.Id) continue;                           // the entry names globals directly
                foreach (var calleeId in calleesByOperation[op.Id])
                {
                    if (calleeId == op.Id || !extras.TryGetValue(calleeId, out var calleeExtras)) continue;
                    var mine = extras.TryGetValue(op.Id, out var list) ? list : extras[op.Id] = new();
                    foreach (var key in calleeExtras)
                        if (!mine.Contains(key)) { mine.Add(key); changed = true; }
                }
            }
        }
        foreach (var (operationId, list) in extras)                       // own params keep decl order; pass-throughs sort stably behind them
        {
            var own = list.Where(k => k.OperationId == operationId).ToList();
            var thru = list.Where(k => k.OperationId != operationId)
                .OrderBy(k => storageName[k], StringComparer.Ordinal).ToList();
            list.Clear(); list.AddRange(own); list.AddRange(thru);
        }

        // ── 4. Each threaded array's forwarding SLOT in each holder op gets a name. An OWNED slot reuses the
        //       array's own parameter placeholder (refName); a PASS-THROUGH gets its own placeholder based
        //       on the backing global's name, for a readable emitted signature.
        var paramName = new Dictionary<(int HolderOperationId, OwnerKey Key), string>();
        foreach (var (operationId, appended) in extras)
            foreach (var key in appended)
                paramName[(operationId, key)] = key.OperationId == operationId
                    ? refName[key]
                    : Ph($"{operationsById[key.OperationId].Name.Replace('.', '_')}_{byOp[key.OperationId].First(h => h.DeclarationId == key.DeclarationId).VariableName}");

        // ── 5. Rewrite every op: append hidden parameters, prepend hoisted storage, turn owned declaration
        //       sites into element-wise re-initialization (under the chosen refName), and hand the right
        //       name to every call site.
        var outOps = new List<QOperation>(program.Operations.Count);
        foreach (var op in program.Operations)
        {
            var isEntry = op.Id == entry.Id;
            var owned = replaced.TryGetValue(op.Id, out var set) ? set : new HashSet<int>();
            // Every hoisted array's body references are rewritten from its source name to its placeholder,
            // in effect only FROM the array's declaration onward (see Rewrite): the map starts EMPTY and
            // gains each rename when its decl is reached, so a reference to a same-named shadowed PARAMETER
            // before/outside the array's scope keeps its meaning. forceApply is on whenever the op has any
            // hoisted array (a placeholder always differs from the source name).
            var body = Rewrite(op.Body, op, isEntry, owned, extras, paramName, storageName, refName, operationsById,
                new Dictionary<string, string>(), owned.Count > 0, facts);

            var storage = new List<QStmt>();
            if (isEntry)                                                  // R1 backing globals — helper arrays, source order
                storage.AddRange(program.Operations.Where(o => o.Id != entry.Id)
                    .SelectMany(o => byOp.TryGetValue(o.Id, out var hs) ? hs.Where(h => h.Threaded) : Enumerable.Empty<Hoisted>())
                    .Select(h => StorageDecl(
                        h,
                        storageName[new OwnerKey(h.OperationId, h.DeclarationId)],
                        facts)));
            if (byOp.TryGetValue(op.Id, out var mine))                    // R2/R3 storage at this op's own top
                storage.AddRange(mine.Where(h => !h.Threaded)
                    .Select(h => StorageDecl(
                        h,
                        storageName[new OwnerKey(h.OperationId, h.DeclarationId)],
                        facts)));
            if (storage.Count > 0) body = storage.Concat(body).ToList();

            var appended = extras.TryGetValue(op.Id, out var ap) ? ap : new List<OwnerKey>();
            outOps.Add(op with
            {
                Params = appended.Count == 0
                    ? op.Params
                    : op.Params.Concat(appended.Select(key =>
                    {
                        var elementType = byOp[key.OperationId]
                            .First(h => h.DeclarationId == key.DeclarationId)
                            .ElementType;
                        var parameter = new QParam(
                            paramName[(op.Id, key)],
                            elementType,
                            null)
                        {
                            IsArray = true,
                            // Compiler-generated backing storage is explicitly threaded for writes. This
                            // parameter does not exist in source, but its backend contract is still mutable.
                            Ownership = QOwnershipMode.Borrowed,
                            Access = QAccessMode.Mutable,
                        };
                        facts.Record(
                            parameter.Id,
                            key.DeclarationId,
                            OpenQasmSynthesisKind.HoistedArrayParameter,
                            new OpenQasmClassicalType(
                                elementType,
                                isArray: true));
                        return parameter;
                    })).ToList(),
                Body = body,
            });
        }
        return new(
            program with { Operations = outOps },
            notes,
            facts.Build());
    }

    private static QStmt StorageDecl(
        Hoisted h,
        string name,
        OpenQasmTargetFactBuilder facts)
    {
        var declaration =
            new QDecl(
                false,
                h.ElementType,
                name,
                new QArrayNew(h.ElementType, h.Length))
            {
                IsArray = true,
            };
        facts.Record(
            declaration.Id,
            h.DeclarationId,
            OpenQasmSynthesisKind.HoistedArrayStorage,
            new OpenQasmClassicalType(h.ElementType, isArray: true));
        return declaration;
    }

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

    private static void CollectCalls(
        IReadOnlyList<QStmt> stmts,
        IReadOnlyDictionary<int, QOperation> operationsById,
        HashSet<int> into)
    {
        foreach (var s in stmts)
        {
            // A `function` is called as a QCallNode INSIDE AN EXPRESSION, never as a QGate statement, so the
            // statement switch below cannot see it. Missing those edges left a caller unthreaded, and the
            // hidden argument it owed its callee was then never supplied. Uses the shared enumerators rather
            // than a second hand-rolled expression walk.
            foreach (var tree in QNodes.ExpressionSites(s))
                foreach (var call in QNodes.CallsIn(tree))
                    if (RequireBoundCallee(call, operationsById) is int calleeId)
                        into.Add(calleeId);
            switch (s)
            {
                case QGate g:
                    if (RequireBoundCallee(g, operationsById) is int gateCalleeId)
                        into.Add(gateCalleeId);
                    break;
                case QIf i:
                    CollectCalls(i.Then, operationsById, into);
                    CollectCalls(i.Else, operationsById, into);
                    break;
                case QFor f: CollectCalls(f.Body, operationsById, into); break;
                case QWhile w: CollectCalls(w.Body, operationsById, into); break;
                case QRepeat r: CollectCalls(r.Body, operationsById, into); break;
                case QConjugate c:
                    CollectCalls(c.Within, operationsById, into);
                    CollectCalls(c.Apply, operationsById, into);
                    break;
            }
        }
    }

    private static int? RequireBoundCallee(
        QGate gate,
        IReadOnlyDictionary<int, QOperation> operationsById)
    {
        if (gate.CalleeOpId is int calleeId)
        {
            if (!operationsById.ContainsKey(calleeId))
                throw new InvalidOperationException(
                    $"QINTERNAL: call `{gate.Name}` carries dangling CalleeOpId {calleeId} during array-local hoisting");
            return calleeId;
        }

        if (QoraGates.Names.ContainsKey(gate.Name)
            || QoraGates.MeasureLike.Contains(gate.Name)
            || QoraGates.NonUnitary.Contains(gate.Name)
            || gate.Name == "reset")
            return null;

        throw new InvalidOperationException(
            $"QINTERNAL: user-callable statement `{gate.Name}` reached array-local hoisting without CalleeOpId");
    }

    private static int? RequireBoundCallee(
        QCallNode call,
        IReadOnlyDictionary<int, QOperation> operationsById)
    {
        if (call.CalleeOpId is int calleeId)
        {
            if (!operationsById.ContainsKey(calleeId))
                throw new InvalidOperationException(
                    $"QINTERNAL: call `{call.Name}` carries dangling CalleeOpId {calleeId} during array-local hoisting");
            return calleeId;
        }

        if (QoraGates.Functions.ContainsKey(call.Name)
            || QoraGates.MeasureLike.Contains(call.Name))
            return null;

        throw new InvalidOperationException(
            $"QINTERNAL: user-callable expression `{call.Name}` reached array-local hoisting without CalleeOpId");
    }

    /// <summary><paramref name="active"/> maps a shadowed source variable to its freshened array name, IN
    /// EFFECT for this statement list and everything nested (it gains an entry when an owned array's
    /// declaration is reached, so references before that decl — which mean the shadowed parameter — are
    /// untouched). <paramref name="forceApply"/> is false in the overwhelmingly common no-shadow case, where
    /// every renaming call short-circuits and statements pass through unchanged.</summary>
    private static IReadOnlyList<QStmt> Rewrite(IReadOnlyList<QStmt> stmts, QOperation op, bool isEntry,
        HashSet<int> owned,
        Dictionary<int, List<OwnerKey>> extras,
        Dictionary<(int HolderOperationId, OwnerKey Key), string> paramName,
        Dictionary<OwnerKey, string> storageName,
        Dictionary<OwnerKey, string> refName,
        IReadOnlyDictionary<int, QOperation> operationsById,
        Dictionary<string, string> inherited, bool forceApply,
        OpenQasmTargetFactBuilder facts,
        Dictionary<string, string>? finalActive = null)
    {
        var active = new Dictionary<string, string>(inherited);
        // The facts a call site needs to be COMPLETED, gathered once for this body. Statement calls are
        // completed by the QGate arm below; expression calls are completed inside the reference walk.
        var fix = new CallFix(op, isEntry, extras, paramName, storageName, operationsById);
        var result = new List<QStmt>(stmts.Count);
        foreach (var s in stmts)
            switch (s)
            {
                // A declaration site becomes element-wise re-initialization of the moved name (its chosen
                // refName). Element expressions are rewritten with the CURRENT active map and emitted IN
                // PLACE, so initializers referencing other locals (or measured values) evaluate exactly
                // where the source evaluated them. The array's own rename takes effect for LATER references.
                case QDecl when IsArrayLocal(s, out var d) && owned.Contains(d.Id):
                    var target = refName.TryGetValue(new OwnerKey(op.Id, d.Id), out var rn) ? rn : d.Name;
                    switch (d.Value)
                    {
                        case QArrayLiteral l:
                            for (var i = 0; i < l.Elements.Count; i++)
                                result.Add(Reinitialization(
                                    target,
                                    RenameExpr(
                                        l.Elements[i],
                                        active,
                                        forceApply,
                                        fix),
                                    i,
                                    d.Id,
                                    facts));
                            break;
                        case QArrayNew n:
                            for (var i = 0; i < n.Length; i++)
                                result.Add(Reinitialization(
                                    target,
                                    Zero(n.ElementType),
                                    i,
                                    d.Id,
                                    facts));
                            break;
                    }
                    if (target != d.Name) active[d.Name] = target;
                    else active.Remove(d.Name);
                    break;

                // Any declaration shadows the enclosing meaning from this point onward. Its initializer
                // still resolves through the pre-declaration map, so rewrite it before removing a possible
                // inherited hoisted-array replacement for the same source name.
                case QDecl d:
                    result.Add(RenameStmt(d, active, forceApply, fix));
                    active.Remove(d.Name);
                    break;

                // A call to an op with hidden parameters: supply them, in the callee's appended order. The
                // entry names the global backing directly; a def hands on its own forwarding parameter. The
                // original arguments are rewritten with active (they may reference a renamed local array).
                case QGate g:
                    var args = g.Args.Select(a => RenameArg(a, active, forceApply, fix)).ToList();
                    foreach (var key in fix.For(g) ?? Enumerable.Empty<OwnerKey>())
                        args.Add(new QTextArg(new QNameRef(ArgNameFor(key, op, isEntry, paramName, storageName)))
                        {
                            Ownership = QOwnershipMode.Borrowed,
                            Access = QAccessMode.Mutable,
                        });
                    result.Add(g with { Args = args });
                    break;

                case QIf i:
                    result.Add(i with
                    {
                        Cond = RenameCond(i.Cond, active, forceApply, fix),
                        Then = Rewrite(i.Then, op, isEntry, owned, extras, paramName, storageName, refName, operationsById, active, forceApply, facts),
                        Else = Rewrite(i.Else, op, isEntry, owned, extras, paramName, storageName, refName, operationsById, active, forceApply, facts),
                    });
                    break;
                case QFor f:
                    var forActive = new Dictionary<string, string>(active);
                    forActive.Remove(f.Var);
                    result.Add(f with
                    {
                        From = RenameNode(f.From, active, forceApply, fix)!, To = RenameNode(f.To, active, forceApply, fix)!,
                        Step = RenameNode(f.Step, active, forceApply, fix),
                        Body = Rewrite(f.Body, op, isEntry, owned, extras, paramName, storageName, refName,
                            operationsById, forActive, forceApply, facts),
                    });
                    break;
                case QWhile w:
                    result.Add(w with
                    {
                        Cond = RenameCond(w.Cond, active, forceApply, fix),
                        Body = Rewrite(w.Body, op, isEntry, owned, extras, paramName, storageName, refName, operationsById, active, forceApply, facts),
                    });
                    break;
                case QRepeat r:
                    // Unlike while, `until` resolves AFTER the repeat body and in that body's scope.
                    // Preserve the rename map after its top-level declarations so a hoisted array that
                    // shadows an enclosing name remains the value the condition actually references.
                    var untilActive = new Dictionary<string, string>();
                    var repeatBody = Rewrite(r.Body, op, isEntry, owned, extras, paramName, storageName,
                        refName, operationsById, active, forceApply, facts, untilActive);
                    result.Add(r with
                    {
                        Body = repeatBody,
                        Until = RenameCond(r.Until, untilActive, forceApply, fix),
                    });
                    break;
                case QConjugate c:
                    result.Add(c with
                    {
                        Within = Rewrite(c.Within, op, isEntry, owned, extras, paramName, storageName, refName, operationsById, active, forceApply, facts),
                        Apply = Rewrite(c.Apply, op, isEntry, owned, extras, paramName, storageName, refName, operationsById, active, forceApply, facts),
                    });
                    break;

                default: result.Add(RenameStmt(s, active, forceApply, fix)); break;
            }
        if (finalActive is not null)
        {
            finalActive.Clear();
            foreach (var (name, replacement) in active) finalActive[name] = replacement;
        }
        return result;
    }

    private static QAssign Reinitialization(
        string target,
        QExpr value,
        int index,
        int sourceDeclarationId,
        OpenQasmTargetFactBuilder facts)
    {
        var assignment = new QAssign(target, value)
        {
            Index = new QNumLit(index),
        };
        facts.Record(
            assignment.Id,
            sourceDeclarationId,
            OpenQasmSynthesisKind.ArrayReinitialization);
        return assignment;
    }

    /// <summary>The name the CALLER uses to forward a threaded array to a callee: the entry names the
    /// backing global directly; a def uses its own (owner or pass-through) parameter for that array. A def
    /// calling an owner without a matching parameter is a threading bug — fail loudly, never a dangling
    /// name.</summary>
    private static string ArgNameFor(
        OwnerKey key,
        QOperation caller,
        bool isEntry,
        Dictionary<(int HolderOperationId, OwnerKey Key), string> paramName,
        Dictionary<OwnerKey, string> storageName)
    {
        if (isEntry) return storageName[key];
        return paramName.TryGetValue((caller.Id, key), out var slot) ? slot
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
        Dictionary<int, List<OwnerKey>> Extras,
        Dictionary<(int HolderOperationId, OwnerKey Key), string> ParamName,
        Dictionary<OwnerKey, string> StorageName,
        IReadOnlyDictionary<int, QOperation> OperationsById)
    {
        /// <summary>The hidden arguments this callee needs, or null when it needs none.</summary>
        public List<OwnerKey>? For(QGate gate)
        {
            var calleeId = RequireBoundCallee(gate, OperationsById);
            return calleeId is int id
                && Extras is not null
                && Extras.TryGetValue(id, out var extra)
                && extra.Count > 0
                    ? extra
                    : null;
        }

        public List<OwnerKey>? For(QCallNode call)
        {
            var calleeId = RequireBoundCallee(call, OperationsById);
            return calleeId is int id
                && Extras is not null
                && Extras.TryGetValue(id, out var extra)
                && extra.Count > 0
                    ? extra
                    : null;
        }

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
            QQubitArg q => new QQubitArg(N(q.Reg, map), RenameNode(q.Index, map, on, fix)!)
            {
                Ownership = q.Ownership,
                Access = q.Access,
            },
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
            QMeasure m => m with { Target = RenameNode(m.Target, map, on, fix)! },
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
            OpenQasmUnsignedCastNode cast => cast with
            {
                Operand = RenameNode(cast.Operand, map, on, fix)!,
            },
            // Rename the written arguments, then APPEND the callee's hidden array references in its own
            // appended order — the same completion the statement arm performs in Rewrite.
            QCallNode c => c with
            {
                Args = c.Args.Select(a => RenameNode(a, map, on, fix)!)
                    .Concat((fix.For(c) ?? Enumerable.Empty<OwnerKey>())
                        .Select(key => (QNode)new QNameRef(ArgNameFor(key, fix.Caller, fix.IsEntry, fix.ParamName, fix.StorageName))))
                    .ToList(),
            },
            _ => node,   // QNumLit, QLit
        };
    }

    private static string N(string name, Dictionary<string, string> map) => map.TryGetValue(name, out var m) ? m : name;
}
