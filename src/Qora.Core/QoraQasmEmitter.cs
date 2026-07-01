using System.Collections.Generic;
using System.Linq;
using System.Text;
using Janglim.FrontEnd.Ast;

namespace Qora;

/// <summary>
/// Walks a Qora v0.6 AST and emits OpenQASM 3.0.
/// <list type="bullet">
///   <item>each operation becomes either the top-level program (the one named <c>Main</c>, else the
///         first/only one) or a <c>def</c> subroutine; subroutines are emitted first so they precede
///         their call sites;</item>
///   <item>an invocation <c>Foo(args)</c> emits as a gate (<c>foo args;</c>) when the name is a known
///         gate, otherwise as a subroutine call (<c>foo(args);</c>);</item>
///   <item>parameters map type-first: <c>Qubit[2] q</c>→<c>qubit[2] q</c>, <c>Qubit q</c>→<c>qubit q</c>,
///         <c>int n</c>→<c>int n</c>;</item>
///   <item>qubits (<c>use</c>) and measurement bits are hoisted within their operation's scope;</item>
///   <item>classical decls / control flow / arithmetic map straight through (see the v0.5 rules).</item>
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
        ["CZ"] = "cz",      // controlled-Z
        ["SWAP"] = "swap",  // swap two qubits
        ["CCX"] = "ccx",    // Toffoli (controlled-controlled-X)
        ["Rx"] = "rx",      // rotation about X by an angle  (parametrized)
        ["Ry"] = "ry",      // rotation about Y
        ["Rz"] = "rz",      // rotation about Z
    };

    // parametrized rotation gates: Rx(θ, q) -> rx(θ) q;  (angle in parens, qubit after)
    private static readonly HashSet<string> RotationGates = new() { "Rx", "Ry", "Rz" };

    private static readonly HashSet<string> TypeKeywords = new() { "int", "bit" };

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
                    var c = Conditions(node);
                    body.AppendLine($"{pad}if ({At(c, 0)} == {At(c, 1)}) {{");
                    EmitStatements(node.Items.OfType<AstNonTerminal>(), body, indent + 1, ops);
                    body.AppendLine($"{pad}}}");
                    break;

                case "For":
                    var f = Conditions(node);
                    body.AppendLine($"{pad}for int {At(f, 0)} in [{At(f, 1)}:{At(f, 2)}] {{");
                    EmitStatements(node.Items.OfType<AstNonTerminal>(), body, indent + 1, ops);
                    body.AppendLine($"{pad}}}");
                    break;

                case "While":
                    var w = Conditions(node);
                    body.AppendLine($"{pad}while ({At(w, 0)} == {At(w, 1)}) {{");
                    EmitStatements(node.Items.OfType<AstNonTerminal>(), body, indent + 1, ops);
                    body.AppendLine($"{pad}}}");
                    break;

                case "Repeat":
                    var rp = Conditions(node);
                    body.AppendLine($"{pad}while (true) {{");
                    EmitStatements(node.Items.OfType<AstNonTerminal>(), body, indent + 1, ops);
                    body.AppendLine($"{pad}  if ({At(rp, 0)} == {At(rp, 1)}) {{ break; }}");
                    body.AppendLine($"{pad}}}");
                    break;
            }
        }
    }

    /// <summary>A statement-level invocation: a subroutine call <c>foo(a, b);</c> or a gate <c>h q[0];</c>.</summary>
    private static string EmitInvocation(AstNonTerminal node, HashSet<string> ops)
    {
        var name = node.Items.Count > 0 ? node.Items[0].ToString() ?? string.Empty : string.Empty;
        var args = node.Items.Skip(1).Select(RenderArg).ToList();

        // Rx(θ, q) -> rx(θ) q;   (angle in parens, qubit after — OpenQASM's parametrized-gate form)
        if (RotationGates.Contains(name) && args.Count >= 2)
            return $"{MapGate(name)}({args[0]}) {args[1]};";

        return ops.Contains(name)
            ? $"{name}({string.Join(", ", args)});"          // subroutine call
            : $"{MapGate(name)} {string.Join(", ", args)};"; // gate application
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

    // --- expressions ---

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

    // --- helpers ---

    private static string OpName(AstNonTerminal op) =>
        op.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static IEnumerable<AstNonTerminal> Params(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Param");

    private static IEnumerable<AstNonTerminal> Body(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Param");

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
