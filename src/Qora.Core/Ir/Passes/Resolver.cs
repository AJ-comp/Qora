namespace Qora.Ir.Passes;

/// <summary>
/// The name-resolution pass (IR→IR): turns every user-operation reference into its FULLY-QUALIFIED
/// name, so every later stage (validation, inversion, mangling, emission) works on one unambiguous
/// name and the half-shadowing class of bug is structurally impossible.
///
/// The algorithm (docs/namespaces-design.md — standard C#/Q# rules) for an unqualified callee used
/// inside namespace <c>NS</c>:
/// <code>
/// 0. a measurement-family name (M/Measure/measure) → always the built-in (fully reserved: QSEM013)
/// 1. NS's own declarations (NS.name exists)  → the local namespace wins
/// 2. the enclosing global namespace (name)   → like C#'s outward search
/// 3. the namespaces NS opened:
///      exactly one match  → use it
///      two or more        → QSEM018 ambiguity error (candidates listed, qualify to fix)
///      none               → left as written (the validator reports QSEM007 unknown-name)
/// </code>
/// A qualified name (<c>MyLib.Bell</c>) bypasses all steps and must exist (else QSEM019). <c>open</c>
/// is non-transitive by construction: only NS's own opens are consulted.
///
/// BUILT-IN GATE NAMES (Q#-style, the "declaration allowed, ambiguous use is an error" rule): the
/// built-ins live in the implicit <see cref="QoraGates.IntrinsicNamespace"/>, open everywhere. A
/// namespaced operation MAY reuse a gate name (<c>namespace L { operation Rx … }</c>), but an
/// unqualified use that could mean both the user op and the built-in NEVER silently picks one — it is
/// a QSEM018 ambiguity, resolved by qualifying (<c>L.Rx(…)</c> for the user op,
/// <c>Qora.Intrinsic.Rx(…)</c> for the gate). With no user candidate in scope, the bare name is the
/// built-in, exactly as the book teaches. Global operations cannot take gate names (they share one
/// scope with the built-ins and have no qualifier — the validator's QSEM013 explains).
///
/// After this pass, <see cref="QOperation.Name"/> is the FQN (global ops keep their plain name, so a
/// namespace-free program is byte-identical through the rest of the pipeline), and the entry op is
/// the global <c>Main</c> as before.
/// </summary>
public static class Resolver
{
    public static (QProgram Program, List<QoraError> Errors) Resolve(QProgram program)
    {
        var errors = new List<QoraError>();

        // symbol table: fully-qualified name -> declared (namespaces contribute "NS.Op", global "Op").
        var table = new HashSet<string>();
        foreach (var op in program.Operations) table.Add(Fqn(op));

        // every namespace the program KNOWS: ones that contain operations, plus every declared block —
        // lowering records a key in Opens for each `namespace N { … }`, so an empty (or opens-only)
        // namespace still counts as existing and `open`ing it is not an error.
        var namespaces = program.Operations.Select(o => o.Namespace).Where(n => n.Length > 0).ToHashSet();
        if (program.Opens is not null)
            foreach (var declared in program.Opens.Keys) namespaces.Add(declared);

        // the built-ins' home may not be (re)declared — users cannot add to or shadow the intrinsics.
        foreach (var ns in namespaces.Where(n =>
                     n == QoraGates.IntrinsicNamespace || n.StartsWith(QoraGates.IntrinsicNamespace + ".", StringComparison.Ordinal)))
            Add(errors, "QSEM013", $"namespace `{ns}` is reserved for the built-in gates; choose another name",
                program.Operations.FirstOrDefault(o => o.Namespace == ns)?.Span);

        // …but it EXISTS everywhere: `open Qora.Intrinsic;` is legal (and a no-op — it is already open).
        namespaces.Add(QoraGates.IntrinsicNamespace);

        // `open X;` must name a namespace that exists in this program — i.e. declared in the entry
        // file or in an IMPORTED one. The most common miss is forgetting the import, so say so.
        if (program.Opens is not null)
            foreach (var (ns, opens) in program.Opens)
                foreach (var open in opens.DistinctBy(o => o.Target).Where(o => !namespaces.Contains(o.Target)))
                    Add(errors, "QSEM019",
                        $"in namespace `{ns}`: `open {open.Target};` names an unknown namespace — `open` only makes loaded names shorter; if `{open.Target}` lives in another file, `import` that file first",
                        open.Span);

        var resolvedOps = program.Operations
            .Select(op => (op with { Name = Fqn(op) })
                with { Body = ResolveBody(op.Body, op.Namespace, op.Name, program, table, namespaces, errors) })
            .ToList();

        return (program with { Operations = resolvedOps }, errors);
    }

    private static string Fqn(QOperation op) => op.Namespace.Length > 0 ? $"{op.Namespace}.{op.Name}" : op.Name;

    private static IReadOnlyList<QStmt> ResolveBody(IReadOnlyList<QStmt> stmts, string ns, string opName,
        QProgram program, HashSet<string> table, HashSet<string> namespaces, List<QoraError> errors) =>
        stmts.Select(s => ResolveStmt(s, ns, opName, program, table, namespaces, errors)).ToList();

    private static QStmt ResolveStmt(QStmt s, string ns, string opName,
        QProgram program, HashSet<string> table, HashSet<string> namespaces, List<QoraError> errors) => s switch
    {
        QGate g => g with { Name = ResolveCallee(g.Name, ns, opName, program, table, namespaces, errors, g.Span) },
        QIf i => i with
        {
            Then = ResolveBody(i.Then, ns, opName, program, table, namespaces, errors),
            Else = ResolveBody(i.Else, ns, opName, program, table, namespaces, errors),
        },
        QFor f => f with { Body = ResolveBody(f.Body, ns, opName, program, table, namespaces, errors) },
        QWhile w => w with { Body = ResolveBody(w.Body, ns, opName, program, table, namespaces, errors) },
        QRepeat r => r with { Body = ResolveBody(r.Body, ns, opName, program, table, namespaces, errors) },
        QConjugate c => c with
        {
            Within = ResolveBody(c.Within, ns, opName, program, table, namespaces, errors),
            Apply = ResolveBody(c.Apply, ns, opName, program, table, namespaces, errors),
        },
        _ => s,
    };

    private static string ResolveCallee(string name, string ns, string opName,
        QProgram program, HashSet<string> table, HashSet<string> namespaces, List<QoraError> errors, QSpan? span)
    {
        // qualified name: resolve directly, bypassing scope search (and its ambiguity handling).
        if (name.Contains('.'))
        {
            // Qora.Intrinsic.H names the BUILT-IN explicitly — rewrite to the bare (canonical) name.
            if (name.StartsWith(QoraGates.IntrinsicNamespace + ".", StringComparison.Ordinal))
            {
                var member = name[(QoraGates.IntrinsicNamespace.Length + 1)..];
                if (QoraGates.Names.ContainsKey(member)) return member;
                Add(errors, "QSEM019", $"in `{opName}`: namespace `{QoraGates.IntrinsicNamespace}` has no operation `{member}`", span);
                return name;
            }
            if (table.Contains(name)) return name;
            var lastDot = name.LastIndexOf('.');
            var nsPart = name[..lastDot];
            Add(errors, "QSEM019", namespaces.Contains(nsPart)
                ? $"in `{opName}`: namespace `{nsPart}` has no operation `{name[(lastDot + 1)..]}`"
                : $"in `{opName}`: unknown namespace `{nsPart}` in `{name}` — if it lives in another file, `import` that file first", span);
            return name;
        }

        // 0) the measurement family is fully reserved (no user op can take these names — QSEM013).
        if (QoraGates.MeasureLike.Contains(name)) return name;

        var isBuiltinGate = QoraGates.Names.ContainsKey(name);

        // 1) the local namespace wins — among USER names. A gate-named hit never wins silently:
        //    the built-in is in scope too, so the use is ambiguous (Q#-style; qualify to pick).
        if (ns.Length > 0 && table.Contains($"{ns}.{name}"))
        {
            if (!isBuiltinGate) return $"{ns}.{name}";
            Add(errors, "QSEM018", Ambiguous(opName, name, $"`{ns}.{name}`"), span);
            return name;
        }

        // 2) the enclosing global namespace. Gate-named GLOBAL ops are declaration errors (QSEM013 —
        //    the global scope has no qualifier), so the built-in meaning is not consulted here.
        if (!isBuiltinGate && table.Contains(name)) return name;

        // 3) opened namespaces — exactly one match resolves; several is an ambiguity error, and for a
        //    gate name the implicitly-open built-in is always one of the candidates.
        var opens = program.Opens is not null && ns.Length > 0 && program.Opens.TryGetValue(ns, out var o)
            ? o : (IReadOnlyList<QOpen>)Array.Empty<QOpen>();
        var candidates = opens.Where(open => table.Contains($"{open.Target}.{name}"))
                              .Select(open => $"{open.Target}.{name}")
                              .Distinct().ToList();
        if (isBuiltinGate)
        {
            if (candidates.Count == 0) return name; // no user candidate in scope: the built-in, as taught
            Add(errors, "QSEM018", Ambiguous(opName, name, string.Join(" or ", candidates.Select(c => $"`{c}`"))), span);
            return name;
        }
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
            Add(errors, "QSEM018",
                $"in `{opName}`: `{name}` is ambiguous here: it could be {string.Join(" or ", candidates.Select(c => $"`{c}`"))} — qualify the call (e.g. `{candidates[0]}(...)`)", span);

        // none: leave as written; the validator reports it as an unknown name (QSEM007).
        return name;
    }

    /// <summary>The user-op-vs-built-in ambiguity message (Q#-style: never silently pick either).</summary>
    private static string Ambiguous(string opName, string name, string userCandidates) =>
        $"in `{opName}`: `{name}` is ambiguous here: it could be {userCandidates} or the built-in `{name}` — qualify the call (`{QoraGates.IntrinsicNamespace}.{name}(...)` names the built-in)";

    private static void Add(List<QoraError> errors, string code, string message, QSpan? span = null) =>
        errors.Add(new QoraError(message, code, span?.Start ?? -1, span?.End ?? -1));
}
