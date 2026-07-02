using Janglim.FrontEnd.Ast;

namespace Qora.Ir;

/// <summary>
/// The engine boundary: converts a Janglim parse-AST (<see cref="AstSymbol"/>, tagged by MeaningUnit
/// name) into Qora's own <see cref="QProgram"/> IR, once. After this, nothing downstream touches Janglim
/// types — passes and the emitter work purely on the IR.
///
/// Structure (statements, gates, control flow) becomes typed IR nodes; expressions and conditions are
/// flattened to their rendered text here (Qora never restructures them), mirroring the old emitter's
/// space-joined output exactly.
/// </summary>
public static class QoraLowering
{
    private static readonly HashSet<string> TypeKeywords = new() { "int", "bit" };
    private static readonly HashSet<string> FunctorNames = new() { "Adjoint", "Controlled" };

    public static QProgram? Lower(AstSymbol? ast)
    {
        if (ast is not AstNonTerminal program) return null;

        var operations = program.Items.OfType<AstNonTerminal>()
            .Where(n => n.Name == "Operation")
            .Select(LowerOperation)
            .ToList();

        return new QProgram(operations);
    }

    private static QOperation LowerOperation(AstNonTerminal op)
    {
        var name = OpName(op);
        var ps = Params(op).Select(LowerParam).ToList();
        var body = Body(op).Select(LowerStmt).ToList();
        return new QOperation(name, ps, body);
    }

    // Qubit q / Qubit[2] q / int n / bit b  (mirrors the old EmitParam token inspection).
    private static QParam LowerParam(AstNonTerminal param)
    {
        var terms = param.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        var name = terms.FirstOrDefault(t => !TypeKeywords.Contains(t) && !IsNumber(t)) ?? string.Empty;
        var typeKw = terms.FirstOrDefault(t => TypeKeywords.Contains(t));
        var size = terms.FirstOrDefault(IsNumber);

        if (typeKw == "int") return new QParam(name, QType.Int, null);
        if (typeKw == "bit") return new QParam(name, QType.Bit, null);
        if (size is not null) return new QParam(name, QType.Qubit, int.Parse(size));
        return new QParam(name, QType.Qubit, null);
    }

    private static QStmt LowerStmt(AstNonTerminal node) => node.Name switch
    {
        "Use" => new QUse(Child(node, 0), int.Parse(Child(node, 1))),
        "Gate" => LowerGate(node),
        "ConstDecl" => LowerDecl(node, isConst: true),
        "VarDecl" => LowerDecl(node, isConst: false),
        "Assign" => new QAssign(FirstIdent(node), LowerExpr(ExprOf(node))),
        "If" => LowerIf(node),
        "For" => LowerFor(node),
        "While" => new QWhile(new QCond(RenderCondition(CondOf(node))), BodyStmts(node).Select(LowerStmt).ToList()),
        "Repeat" => new QRepeat(BodyStmts(node).Select(LowerStmt).ToList(), new QCond(RenderCondition(CondOf(node)))),
        _ => new QGate(new List<string>(), node.Name, new List<QArg>()), // defensive: unknown node -> inert
    };

    private static QGate LowerGate(AstNonTerminal node)
    {
        var items = node.Items;
        var head = items.Count > 0 ? items[0].ToString() ?? string.Empty : string.Empty;

        var functors = new List<string>();
        var start = 0;
        if (FunctorNames.Contains(head)) { functors.Add(head); start = 1; }

        var name = items.Count > start ? items[start].ToString() ?? string.Empty : string.Empty;
        var args = items.Skip(start + 1).Select(LowerArg).ToList();
        return new QGate(functors, name, args);
    }

    private static QArg LowerArg(AstSymbol sym)
    {
        if (sym is AstNonTerminal nt)
        {
            if (nt.Name == "Qubit") return new QQubitArg(Child(nt, 0), Child(nt, 1));
            if (nt.Name == "Expr") return new QTextArg(RenderExprText(nt));
        }
        return new QTextArg(sym.ToString() ?? string.Empty);
    }

    private static QDecl LowerDecl(AstNonTerminal node, bool isConst)
    {
        var name = DeclName(node);
        var type = DeclType(node) switch { "int" => (QType?)QType.Int, "bit" => QType.Bit, _ => null };
        return new QDecl(isConst, type, name, LowerExpr(ExprOf(node)));
    }

    private static QIf LowerIf(AstNonTerminal node)
    {
        var items = node.Items;

        // the `else` terminal (if present) splits then-branch statements from else-branch (an `else if`
        // shows up as an "If" nonterminal in the else-branch, lowered recursively like any statement).
        int elseIdx = -1;
        for (int k = 0; k < items.Count; k++)
            if (items[k] is AstTerminal t && t.ToString() == "else") { elseIdx = k; break; }

        var thenStmts = new List<QStmt>();
        var elseStmts = new List<QStmt>();
        for (int k = 0; k < items.Count; k++)
        {
            if (items[k] is not AstNonTerminal nt || nt.Name == "Condition") continue;
            (elseIdx < 0 || k < elseIdx ? thenStmts : elseStmts).Add(LowerStmt(nt));
        }

        return new QIf(new QCond(RenderCondition(CondOf(node))), thenStmts, elseStmts);
    }

    private static QFor LowerFor(AstNonTerminal node)
    {
        var f = node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty).ToList();
        return new QFor(At(f, 0), At(f, 1), At(f, 2), BodyStmts(node).Select(LowerStmt).ToList());
    }

    // measurement (M(q) via a Call child) -> QMeasure; anything else -> its rendered text.
    private static QExpr LowerExpr(AstNonTerminal? expr)
    {
        if (expr is null) return new QText(string.Empty);

        var call = expr.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Call");
        if (call is not null)
        {
            var qref = call.Items.OfType<AstNonTerminal>().FirstOrDefault(q => q.Name == "Qubit");
            return new QMeasure(qref is null ? null : new QQubitArg(Child(qref, 0), Child(qref, 1)));
        }
        return new QText(string.Join(" ", expr.Items.OfType<AstTerminal>().Select(t => t.ToString())));
    }

    // A gate argument that is an expression renders like the old EmitExpr (space-joined tokens; a call
    // becomes `measure …` — args never carry one in practice, but kept faithful).
    private static string RenderExprText(AstNonTerminal expr)
    {
        var call = expr.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Call");
        if (call is not null)
        {
            var qref = call.Items.OfType<AstNonTerminal>().FirstOrDefault(q => q.Name == "Qubit");
            return qref is null ? "measure" : $"measure {Child(qref, 0)}[{Child(qref, 1)}]";
        }
        return string.Join(" ", expr.Items.OfType<AstTerminal>().Select(t => t.ToString()));
    }

    private static string RenderCondition(AstNonTerminal? cond)
    {
        if (cond is null) return string.Empty;

        var parts = new List<string>();
        foreach (var item in cond.Items)
        {
            if (item is AstNonTerminal nt && nt.Name == "Expr") parts.Add(RenderExprText(nt));
            else parts.Add(item.ToString() ?? string.Empty);
        }
        return string.Join(" ", parts);
    }

    // --- helpers (ported from the old emitter's AST accessors) ---

    private static string OpName(AstNonTerminal op) =>
        op.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static IEnumerable<AstNonTerminal> Params(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name == "Param");

    private static IEnumerable<AstNonTerminal> Body(AstNonTerminal op) =>
        op.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Param");

    private static IEnumerable<AstNonTerminal> BodyStmts(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().Where(n => n.Name != "Condition");

    private static AstNonTerminal? CondOf(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Condition");

    private static AstNonTerminal? ExprOf(AstNonTerminal node) =>
        node.Items.OfType<AstNonTerminal>().FirstOrDefault(n => n.Name == "Expr");

    private static string FirstIdent(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().FirstOrDefault()?.ToString() ?? string.Empty;

    private static string DeclName(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => !TypeKeywords.Contains(t)) ?? string.Empty;

    private static string? DeclType(AstNonTerminal node) =>
        node.Items.OfType<AstTerminal>().Select(t => t.ToString() ?? string.Empty)
            .FirstOrDefault(t => TypeKeywords.Contains(t));

    private static bool IsNumber(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static string Child(AstNonTerminal node, int i) =>
        i < node.Items.Count ? node.Items[i].ToString() ?? string.Empty : string.Empty;

    private static string At(List<string> list, int i) =>
        i < list.Count ? (list[i] ?? string.Empty) : string.Empty;
}
