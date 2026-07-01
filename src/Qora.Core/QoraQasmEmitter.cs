using System.Collections.Generic;
using System.Linq;
using System.Text;
using Janglim.FrontEnd.Ast;

namespace Qora;

/// <summary>
/// Walks a Qora v0.9 AST and emits OpenQASM 3.0.
/// <list type="bullet">
///   <item>each operation becomes either the top-level program (the one named <c>Main</c>, else the
///         first/only one) or a <c>def</c> subroutine; subroutines are emitted first so they precede
///         their call sites;</item>
///   <item>an invocation <c>Foo(args)</c> emits as a gate (<c>foo args;</c>) when the name is a known
///         gate, otherwise as a subroutine call (<c>foo(args);</c>); a functor prefix maps to an
///         OpenQASM gate modifier — <c>Adjoint G</c>→<c>inv @ g</c>, <c>Controlled G</c>→<c>ctrl @ g</c>;</item>
///   <item>parameters map type-first; qubits (<c>use</c>) and measurement bits are hoisted;</item>
///   <item>conditions (== != &lt; &lt;= &gt; &gt;= &amp;&amp; || !) and control flow (if/else, for, while,
///         repeat) map straight through.</item>
/// </list>
/// </summary>
public static class QoraQasmEmitter
{
    private static readonly Dictionary<string, string> GateNames = new()
    {
        ["H"] = "h",
        ["X"] = "x",
        ["Y"] = "y",
        ["Z"] = "z",
        ["S"] = "s",        // phase (√Z)
        ["T"] = "t",        // π/8  (√S)
        ["CNOT"] = "cx",
        ["CX"] = "cx",
        ["CY"] = "cy",      // controlled-Y
        ["CZ"] = "cz",      // controlled-Z
        ["SWAP"] = "swap",  // swap two qubits
        ["CCX"] = "ccx",    // Toffoli (controlled-controlled-X)
        ["Rx"] = "rx",      // rotation about X by an angle  (parametrized)
        ["Ry"] = "ry",      // rotation about Y
        ["Rz"] = "rz",      // rotation about Z
        ["Reset"] = "reset",     // return a qubit (or register) to |0>
        ["ResetAll"] = "reset",  // reset a whole register
    };

    // parametrized rotation gates: Rx(θ, q) -> rx(θ) q;  (angle in parens, qubit after)
    private static readonly HashSet<string> RotationGates = new() { "Rx", "Ry", "Rz" };

    private static readonly HashSet<string> TypeKeywords = new() { "int", "bit" };

    // functor prefix -> OpenQASM gate modifier
    private static readonly Dictionary<string, string> Functors = new()
    {
        ["Adjoint"] = "inv @ ",
        ["Controlled"] = "ctrl @ ",
    };

    public static string Emit(AstSymbol? ast)
    {
        if (ast is not AstNonTerminal program) return string.Empty;

        var operations = program.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Operation").ToList();
        if (operations.Count == 0) return string.Empty;

        // entry point = the operation named "Main", else the first one. Every other op -> a def.
        var entry = operations.FirstOrDefault(o => OpName(o) == "Main") ?? operations[0];
        var subroutines = operations.Where(o => o != entry).ToList();

        // the set of defined operation names: an invocation of one of these is a call, not a gate.
        var ops = operations.Select(OpName).ToHashSet();

        var sb = new StringBuilder();
        sb.AppendLine("OPENQASM 3;");
        sb.AppendLine("include \"stdgates.inc\";");

        foreach (var op in subroutines)
        {
            sb.AppendLine();
            EmitDef(op, ops, sb);
        }

        sb.AppendLine();
        var statements = Body(entry).ToList();
        var decls = new StringBuilder();
        HoistDecls(statements, decls);
        var body = new StringBuilder();
        EmitStatements(statements, body, 0, ops);
        sb.Append(decls);
        sb.Append(body);

        return sb.ToString().TrimEnd();
    }

    private static void EmitDef(AstNonTerminal op, HashSet<string> ops, StringBuilder sb)
    {
        var name = OpName(op);
        var ps = string.Join(", ", Params(op).Select(EmitParam));
        var statements = Body(op).ToList();

        var decls = new StringBuilder();
        HoistDecls(statements, decls);
        var body = new StringBuilder();
        EmitStatements(statements, body, 1, ops);

        sb.AppendLine($"def {name}({ps}) {{");
        foreach (var line in decls.ToString().Split('\n'))
            if (line.Trim().Length > 0) sb.AppendLine($"  {line.TrimEnd()}");
        sb.Append(body);
        sb.AppendLine("}");
    }

    private static void HoistDecls(IEnumerable<AstNonTerminal> nodes, StringBuilder decls)
    {
        foreach (var node in nodes)
        {
            switch (node.Name)
            {
                case "Use":
                    decls.AppendLine($"qubit[{Child(node, 1)}] {Child(node, 0)};");
                    break;
                case "ConstDecl":
                case "VarDecl":
                    if (IsMeasurement(node))
                        decls.AppendLine($"{DeclType(node) ?? "bit"} {DeclName(node)};");
                    break;
                case "If":
                case "For":
                case "While":
                case "Repeat":
                    // recurse into every nested statement (both then- and else-branches; the Condition
                    // node has no declarations so it is simply skipped by the switch).
                    HoistDecls(node.Items.OfType<AstNonTerminal>(), decls);
                    break;
            }
        }
    }

    private static void EmitStatements(IEnumerable<AstNonTerminal> nodes, StringBuilder body, int indent, HashSet<string> ops)
    {
        var pad = new string(' ', indent * 2);

        foreach (var node in nodes)
        {
            switch (node.Name)
            {
                case "Use":
                    break; // hoisted

                case "Gate":
                    body.AppendLine(pad + EmitInvocation(node, ops));
                    break;

                case "ConstDecl":
                    EmitDecl(node, isConst: true, body, pad);
                    break;

                case "VarDecl":
                    EmitDecl(node, isConst: false, body, pad);
                    break;

                case "Assign":
                    body.AppendLine($"{pad}{DeclName(node)} = {EmitExpr(ExprOf(node))};");
                    break;

                case "If":
                    EmitIf(node, body, indent, ops);
                    break;

                case "For":
                    var f = Conditions(node);
                    body.AppendLine($"{pad}for int {At(f, 0)} in [{At(f, 1)}:{At(f, 2)}] {{");
                    EmitStatements(BodyStmts(node), body, indent + 1, ops);
                    body.AppendLine($"{pad}}}");
                    break;

                case "While":
                    body.AppendLine($"{pad}while ({EmitCondition(CondOf(node))}) {{");
                    EmitStatements(BodyStmts(node), body, indent + 1, ops);
                    body.AppendLine($"{pad}}}");
                    break;

                case "Repeat":
                    body.AppendLine($"{pad}while (true) {{");
                    EmitStatements(BodyStmts(node), body, indent + 1, ops);
                    body.AppendLine($"{pad}  if ({EmitCondition(CondOf(node))}) {{ break; }}");
                    body.AppendLine($"{pad}}}");
                    break;
            }
        }
    }

    /// <summary>if (cond) { … }  with an optional  else { … }  (an `else if` nests as `else { if … }`).</summary>
    private static void EmitIf(AstNonTerminal node, StringBuilder body, int indent, HashSet<string> ops)
    {
        var pad = new string(' ', indent * 2);
        var items = node.Items;

        // the `else` terminal (if present) splits the then-branch statements from the else-branch.
        int elseIdx = -1;
        for (int k = 0; k < items.Count; k++)
            if (items[k] is AstTerminal t && t.ToString() == "else") { elseIdx = k; break; }

        var thenStmts = new List<AstNonTerminal>();
        var elseStmts = new List<AstNonTerminal>();
        for (int k = 0; k < items.Count; k++)
        {
            if (items[k] is not AstNonTerminal nt || nt.Name == "Condition") continue;
            (elseIdx < 0 || k < elseIdx ? thenStmts : elseStmts).Add(nt);
        }

        body.AppendLine($"{pad}if ({EmitCondition(CondOf(node))}) {{");
        EmitStatements(thenStmts, body, indent + 1, ops);
        body.AppendLine($"{pad}}}");

        if (elseIdx >= 0)
        {
            body.AppendLine($"{pad}else {{");
            EmitStatements(elseStmts, body, indent + 1, ops);
            body.AppendLine($"{pad}}}");
        }
    }

    /// <summary>A statement-level invocation: a subroutine call, a gate, or a functor-modified gate.</summary>
    private static string EmitInvocation(AstNonTerminal node, HashSet<string> ops)
    {
        var items = node.Items;
        var head = items.Count > 0 ? items[0].ToString() ?? string.Empty : string.Empty;

        // optional functor prefix (Adjoint / Controlled) -> an OpenQASM gate modifier.
        var modifier = string.Empty;
        var start = 0;
        if (Functors.TryGetValue(head, out var mod)) { modifier = mod; start = 1; }

        var name = items.Count > start ? items[start].ToString() ?? string.Empty : string.Empty;
        var args = items.Skip(start + 1).Select(RenderArg).ToList();

        // Rx(θ, q) -> rx(θ) q;   (angle in parens, qubit after — OpenQASM's parametrized-gate form)
        if (RotationGates.Contains(name) && args.Count >= 2)
            return $"{modifier}{MapGate(name)}({args[0]}) {args[1]};";

        return ops.Contains(name)
            ? $"{modifier}{name}({string.Join(", ", args)});"          // subroutine call
            : $"{modifier}{MapGate(name)} {string.Join(", ", args)};"; // gate application
    }

    private static string RenderArg(AstSymbol sym)
    {
        if (sym is AstNonTerminal nt)
        {
            if (nt.Name == "Qubit") return Qubit(nt);   // q[0]
            if (nt.Name == "Expr") return EmitExpr(nt); // q (register) / 5 / pi / 2 / 0.5
        }
        return sym.ToString() ?? string.Empty;
    }

    private static void EmitDecl(AstNonTerminal node, bool isConst, StringBuilder body, string pad)
    {
        var name = DeclName(node);
        var exprNode = ExprOf(node);

        if (IsMeasurement(node))
        {
            body.AppendLine($"{pad}{name} = {EmitExpr(exprNode)};");
            return;
        }

        var type = DeclType(node) ?? "int";
        var prefix = isConst ? "const " : string.Empty;
        body.AppendLine($"{pad}{prefix}{type} {name} = {EmitExpr(exprNode)};");
    }

    // --- parameters ---

    /// <summary>A def parameter: <c>Qubit[2] q</c>→<c>qubit[2] q</c>, <c>Qubit q</c>→<c>qubit q</c>, <c>int n</c>→<c>int n</c>.</summary>
    private static string EmitParam(AstNonTerminal param)
    {
        var terms = param.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        var name = terms.FirstOrDefault(t => !TypeKeywords.Contains(t) && !IsNumber(t)) ?? string.Empty;
        var typeKw = terms.FirstOrDefault(t => TypeKeywords.Contains(t));   // int/bit
        var size = terms.FirstOrDefault(IsNumber);                          // qubit register size

        if (typeKw is not null) return $"{typeKw} {name}";       // int n / bit b
        if (size is not null) return $"qubit[{size}] {name}";    // Qubit[2] q
        return $"qubit {name}";                                  // Qubit q
    }

    // --- expressions & conditions ---

    private static string EmitExpr(AstNonTerminal? expr)
    {
        if (expr is null) return string.Empty;

        var call = expr.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Call");
        if (call is not null)
        {
            var qref = call.Items.OfType<AstNonTerminal>().FirstOrDefault(q => q.Name == "Qubit");
            return qref is null ? "measure" : $"measure {Qubit(qref)}";
        }

        return string.Join(" ", expr.Items.OfType<AstTerminal>().Select(t => t.ToString()));
    }

    /// <summary>A condition emitted verbatim: Expr children through <see cref="EmitExpr"/>, operators as-is.</summary>
    private static string EmitCondition(AstNonTerminal? cond)
    {
        if (cond is null) return string.Empty;

        var parts = new List<string>();
        foreach (var item in cond.Items)
        {
            if (item is AstNonTerminal nt && nt.Name == "Expr") parts.Add(EmitExpr(nt));
            else parts.Add(item.ToString() ?? string.Empty);   // ==, !=, <, &&, ! …
        }
        return string.Join(" ", parts);
    }

    // --- helpers ---

    private static string OpName(AstNonTerminal op) =>
        op.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static IEnumerable<AstNonTerminal> Params(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Param");

    private static IEnumerable<AstNonTerminal> Body(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Param");

    /// <summary>Statement children of a control-flow node (everything except its Condition).</summary>
    private static IEnumerable<AstNonTerminal> BodyStmts(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Condition");

    private static AstNonTerminal? CondOf(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Condition");

    private static AstNonTerminal? ExprOf(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Expr");

    private static string DeclName(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => !TypeKeywords.Contains(t)) ?? string.Empty;

    private static string? DeclType(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => TypeKeywords.Contains(t));

    private static bool IsMeasurement(AstNonTerminal declNode)
    {
        var expr = ExprOf(declNode);
        return expr is not null && expr.Items.OfType<AstNonTerminal>().Any(n => n.Name == "Call");
    }

    private static bool IsNumber(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static List<string> Conditions(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();

    private static string MapGate(string name) =>
        GateNames.TryGetValue(name, out var qasm) ? qasm : name.ToLowerInvariant();

    private static string Child(AstNonTerminal node, int i) =>
        i < node.Items.Count ? node.Items[i].ToString() ?? string.Empty : string.Empty;

    private static string At(List<string> list, int i) =>
        i < list.Count ? (list[i] ?? string.Empty) : string.Empty;

    private static string Qubit(AstNonTerminal qubit) => $"{Child(qubit, 0)}[{Child(qubit, 1)}]";
}
