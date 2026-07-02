using System.Text;

namespace Qora.Ir;

/// <summary>
/// Lowers Qora IR (<see cref="QProgram"/>) to OpenQASM 3.0 — the final, text-producing pass. Behaviour
/// matches the previous AST-walking emitter exactly; the difference is that it now walks the typed IR,
/// so transformations (e.g. the whole-operation <c>Adjoint</c> handled here) are decisions over IR nodes
/// rather than over stringly-typed parse-tree tags.
/// </summary>
public static class QIrEmitter
{
    private static readonly Dictionary<string, string> GateNames = new()
    {
        ["H"] = "h", ["X"] = "x", ["Y"] = "y", ["Z"] = "z",
        ["S"] = "s", ["T"] = "t",
        ["CNOT"] = "cx", ["CX"] = "cx", ["CY"] = "cy", ["CZ"] = "cz",
        ["SWAP"] = "swap", ["CCX"] = "ccx",
        ["Rx"] = "rx", ["Ry"] = "ry", ["Rz"] = "rz",
        ["Reset"] = "reset", ["ResetAll"] = "reset",
    };

    private static readonly HashSet<string> RotationGates = new() { "Rx", "Ry", "Rz" };

    private static readonly Dictionary<string, string> FunctorMods = new()
    {
        ["Adjoint"] = "inv @ ",
        ["Controlled"] = "ctrl @ ",
    };

    public static string Emit(QProgram? program)
    {
        if (program is null || program.Operations.Count == 0) return string.Empty;

        var operations = program.Operations;
        var entry = operations.FirstOrDefault(o => o.Name == "Main") ?? operations[0];
        var subroutines = operations.Where(o => o != entry).ToList();
        var ops = operations.Select(o => o.Name).ToHashSet();

        var opByName = new Dictionary<string, QOperation>();
        foreach (var o in operations) opByName[o.Name] = o;

        // which user ops are used with `Adjoint` and have an invertible (straight-line unitary) body.
        var adjRefs = new HashSet<string>();
        CollectAdjointRefs(entry.Body, ops, adjRefs);
        foreach (var d in subroutines) CollectAdjointRefs(d.Body, ops, adjRefs);
        var adjointed = adjRefs.Where(n => opByName.ContainsKey(n) && IsInvertible(opByName[n], ops)).ToHashSet();

        var sb = new StringBuilder();
        sb.AppendLine("OPENQASM 3;");
        sb.AppendLine("include \"stdgates.inc\";");

        foreach (var op in subroutines)
        {
            sb.AppendLine();
            EmitDef(op, ops, adjointed, sb);
        }

        // synthesized inverse defs (the inversion kernel): body reversed, each gate inv@-prefixed.
        foreach (var name in adjointed.OrderBy(n => n))
        {
            sb.AppendLine();
            EmitAdjDef(opByName[name], ops, adjointed, sb);
        }

        sb.AppendLine();
        var decls = new StringBuilder();
        HoistDecls(entry.Body, decls);
        var body = new StringBuilder();
        EmitStatements(entry.Body, body, 0, ops, adjointed);
        sb.Append(decls);
        sb.Append(body);

        return sb.ToString().TrimEnd();
    }

    private static void EmitDef(QOperation op, HashSet<string> ops, HashSet<string> adjointed, StringBuilder sb)
    {
        var ps = string.Join(", ", op.Params.Select(EmitParam));
        var decls = new StringBuilder();
        HoistDecls(op.Body, decls);
        var body = new StringBuilder();
        EmitStatements(op.Body, body, 1, ops, adjointed);

        sb.AppendLine($"def {op.Name}({ps}) {{");
        foreach (var line in decls.ToString().Split('\n'))
            if (line.Trim().Length > 0) sb.AppendLine($"  {line.TrimEnd()}");
        sb.Append(body);
        sb.AppendLine("}");
    }

    /// <summary>The inversion kernel: an op's gates in reverse order, each prefixed with <c>inv @</c>.</summary>
    private static void EmitAdjDef(QOperation op, HashSet<string> ops, HashSet<string> adjointed, StringBuilder sb)
    {
        var ps = string.Join(", ", op.Params.Select(EmitParam));
        var gates = op.Body.Reverse().ToList();

        sb.AppendLine($"def {AdjName(op.Name)}({ps}) {{");
        foreach (var g in gates)
            sb.AppendLine($"  inv @ {EmitStmtInline(g, ops, adjointed)}");
        sb.AppendLine("}");
    }

    private static void HoistDecls(IReadOnlyList<QStmt> stmts, StringBuilder decls)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QUse u:
                    decls.AppendLine($"qubit[{u.Size}] {u.Name};");
                    break;
                case QDecl d when d.Value is QMeasure:
                    decls.AppendLine($"{TypeName(d.Type) ?? "bit"} {d.Name};");
                    break;
                case QIf i:
                    HoistDecls(i.Then, decls);
                    HoistDecls(i.Else, decls);
                    break;
                case QFor f:
                    HoistDecls(f.Body, decls);
                    break;
                case QWhile w:
                    HoistDecls(w.Body, decls);
                    break;
                case QRepeat r:
                    HoistDecls(r.Body, decls);
                    break;
            }
        }
    }

    private static void EmitStatements(IReadOnlyList<QStmt> stmts, StringBuilder body, int indent, HashSet<string> ops, HashSet<string> adjointed)
    {
        var pad = new string(' ', indent * 2);

        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QUse:
                    break; // hoisted

                case QGate g:
                    body.AppendLine(pad + EmitStmtInline(g, ops, adjointed));
                    break;

                case QDecl d:
                    EmitDecl(d, body, pad);
                    break;

                case QAssign a:
                    body.AppendLine($"{pad}{a.Name} = {RenderExpr(a.Value)};");
                    break;

                case QIf i:
                    EmitIf(i, body, indent, ops, adjointed);
                    break;

                case QFor f:
                    body.AppendLine($"{pad}for int {f.Var} in [{f.From}:{f.To}] {{");
                    EmitStatements(f.Body, body, indent + 1, ops, adjointed);
                    body.AppendLine($"{pad}}}");
                    break;

                case QWhile w:
                    body.AppendLine($"{pad}while ({w.Cond.Text}) {{");
                    EmitStatements(w.Body, body, indent + 1, ops, adjointed);
                    body.AppendLine($"{pad}}}");
                    break;

                case QRepeat r:
                    body.AppendLine($"{pad}while (true) {{");
                    EmitStatements(r.Body, body, indent + 1, ops, adjointed);
                    body.AppendLine($"{pad}  if ({r.Until.Text}) {{ break; }}");
                    body.AppendLine($"{pad}}}");
                    break;
            }
        }
    }

    private static void EmitIf(QIf node, StringBuilder body, int indent, HashSet<string> ops, HashSet<string> adjointed)
    {
        var pad = new string(' ', indent * 2);

        body.AppendLine($"{pad}if ({node.Cond.Text}) {{");
        EmitStatements(node.Then, body, indent + 1, ops, adjointed);
        body.AppendLine($"{pad}}}");

        if (node.Else.Count > 0)
        {
            body.AppendLine($"{pad}else {{");
            EmitStatements(node.Else, body, indent + 1, ops, adjointed);
            body.AppendLine($"{pad}}}");
        }
    }

    /// <summary>Render one gate/call/functor invocation as a single line (used inline and inside adj defs).</summary>
    private static string EmitStmtInline(QStmt stmt, HashSet<string> ops, HashSet<string> adjointed)
    {
        if (stmt is not QGate gate) return string.Empty;

        var name = gate.Name;
        var args = gate.Args.Select(RenderArg).ToList();
        var modifier = string.Concat(gate.Functors.Select(f => FunctorMods.TryGetValue(f, out var m) ? m : string.Empty));

        // whole-operation Adjoint on a user op -> synthesized inverse def call (or a note if not invertible).
        if (gate.Functors.FirstOrDefault() == "Adjoint" && ops.Contains(name))
            return adjointed.Contains(name)
                ? $"{AdjName(name)}({string.Join(", ", args)});"
                : $"// Qora: Adjoint of `{name}` not supported yet (body is not straight-line unitary)";

        if (RotationGates.Contains(name) && args.Count >= 2)
            return $"{modifier}{MapGate(name)}({args[0]}) {args[1]};";

        return ops.Contains(name)
            ? $"{modifier}{name}({string.Join(", ", args)});"
            : $"{modifier}{MapGate(name)} {string.Join(", ", args)};";
    }

    private static void EmitDecl(QDecl d, StringBuilder body, string pad)
    {
        if (d.Value is QMeasure)
        {
            body.AppendLine($"{pad}{d.Name} = {RenderExpr(d.Value)};");
            return;
        }

        var type = TypeName(d.Type) ?? "int";
        var prefix = d.IsConst ? "const " : string.Empty;
        body.AppendLine($"{pad}{prefix}{type} {d.Name} = {RenderExpr(d.Value)};");
    }

    private static string EmitParam(QParam p) => p.Type switch
    {
        QType.Int => $"int {p.Name}",
        QType.Bit => $"bit {p.Name}",
        _ => p.RegisterSize is int n ? $"qubit[{n}] {p.Name}" : $"qubit {p.Name}",
    };

    private static string RenderArg(QArg arg) => arg switch
    {
        QQubitArg q => $"{q.Reg}[{q.Index}]",
        QTextArg t => t.Text,
        _ => string.Empty,
    };

    private static string RenderExpr(QExpr expr) => expr switch
    {
        QMeasure m => m.Target is null ? "measure" : $"measure {m.Target.Reg}[{m.Target.Index}]",
        QText t => t.Text,
        _ => string.Empty,
    };

    private static string AdjName(string name) => name + "__adj";

    private static string MapGate(string name) =>
        GateNames.TryGetValue(name, out var qasm) ? qasm : name.ToLowerInvariant();

    private static string? TypeName(QType? t) => t switch { QType.Int => "int", QType.Bit => "bit", _ => null };

    // --- IR analysis (mirrors the old CollectAdjointRefs / IsInvertible over IR nodes) ---

    private static void CollectAdjointRefs(IReadOnlyList<QStmt> stmts, HashSet<string> ops, HashSet<string> into)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case QGate g when g.Functors.FirstOrDefault() == "Adjoint" && ops.Contains(g.Name):
                    into.Add(g.Name);
                    break;
                case QIf i:
                    CollectAdjointRefs(i.Then, ops, into);
                    CollectAdjointRefs(i.Else, ops, into);
                    break;
                case QFor f:
                    CollectAdjointRefs(f.Body, ops, into);
                    break;
                case QWhile w:
                    CollectAdjointRefs(w.Body, ops, into);
                    break;
                case QRepeat r:
                    CollectAdjointRefs(r.Body, ops, into);
                    break;
            }
        }
    }

    private static bool IsInvertible(QOperation op, HashSet<string> ops)
    {
        foreach (var stmt in op.Body)
        {
            if (stmt is not QGate g) return false;              // decl / measurement / control flow / use
            if (ops.Contains(g.Name)) return false;             // nested user-op call (recursion not yet supported)
            if (g.Name is "Reset" or "ResetAll") return false;  // reset is not unitary
        }
        return true;
    }
}
