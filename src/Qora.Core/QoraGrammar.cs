using Janglim;
using Janglim.FrontEnd;
using Janglim.FrontEnd.Grammars;
using Janglim.FrontEnd.RegularGrammar;

namespace Qora;

/// <summary>
/// Qora v0.12 — a Q#/C#-flavored quantum language on the Janglim engine.
///
///   operation Bell(Qubit[2] q) {       // a subroutine, with C#-style parameters
///       H(q[0]);
///       Controlled X(q[0], q[1]);       // add a control -> ctrl @ x  (CNOT)
///   }
///
///   operation Main() {                  // the entry point -> OpenQASM top-level
///       use q = Qubit[2];
///       Bell(q);                        // call (whole register) — or Bell(q[0], q[1])
///       bit r = M(q[0]);
///       if (r == 1) { Adjoint S(q[1]); } else { S(q[1]); }   // functor + if/else
///   }
///
/// Calls and gate applications share one surface form (<c>Ident(args…)</c>); the emitter tells them
/// apart by name (a defined operation -> a call, otherwise a gate). The operation named <c>Main</c>
/// is the entry (its body becomes the QASM top-level); every other operation becomes a <c>def</c>.
///
/// v0.8 added: single-gate functors <c>Adjoint G(...)</c> / <c>Controlled G(...)</c> (-> OpenQASM
/// <c>inv @</c> / <c>ctrl @</c>), richer conditions (== != &lt; &lt;= &gt; &gt;= &amp;&amp; || !),
/// <c>if/else</c> (and <c>else if</c>), and first-class <c>Reset</c>.
/// v0.9 added: <c>//</c> line comments (block <c>/* */</c> comments still pending engine support).
/// v0.10 added: whole-operation <c>Adjoint</c> (synthesized inverse defs, via the typed IR pipeline in
/// <c>Ir/</c> with semantic validation QSEM001-015), unary minus in expressions, and zero-argument
/// functor calls.
/// v0.11 added: hardened validation (argument kinds for built-ins, full user-op call signatures,
/// index bounds QSEM016, measure-into-bit QSEM017; QSEM013 relaxed so def-locals may shadow stdgates
/// names), the module-system grammar (<c>import</c> / <c>namespace</c> / <c>open</c>, parsed and gated
/// as in-progress — see docs/namespaces-design.md), and string literals (Janglim 0.3.0-preview.1's
/// raw-regex StringLiteral token).
/// v0.12 adds: the module system for real — namespaces/opens/qualified calls resolve
/// (<c>Ir/Passes/Resolver.cs</c>, QSEM018/019/022), <c>import</c> loads files (<c>Ir/Passes/ModuleLoader.cs</c>,
/// QSEM020/021, CLI <c>--base-dir</c>), and emission name-mangles every user name
/// (<c>Ir/Passes/NameMangler.cs</c>: <c>q</c>→<c>q_</c>, <c>MyLib.Bell</c>→<c>MyLib__Bell_</c>).
/// Built-in gate names relaxed Q#-style: a NAMESPACED op may reuse one (ambiguous use ⇒ QSEM018,
/// qualify via <c>L.Rx</c> / <c>Qora.Intrinsic.Rx</c>); measurement family + pi/tau/euler + global
/// gate-named ops stay reserved (QSEM013). Operations are still void (no return value yet).
/// </summary>
public class QoraGrammar : Grammar
{
    // --- keywords (meaning=false -> excluded from AST; bWord=false -> win the lexer tie vs identifier) ---
    public Terminal Operation { get; } = new Terminal(TokenType.Keyword, "operation", false);
    public Terminal Namespace { get; } = new Terminal(TokenType.Keyword, "namespace", false);
    public Terminal Open { get; } = new Terminal(TokenType.Keyword, "open", false);
    public Terminal Import { get; } = new Terminal(TokenType.Keyword, "import", false);
    public Terminal Use { get; } = new Terminal(TokenType.Keyword, "use", false);
    public Terminal Qubit { get; } = new Terminal(TokenType.Keyword, "Qubit", false);
    public Terminal Const { get; } = new Terminal(TokenType.Keyword, "const", false);
    public Terminal Var { get; } = new Terminal(TokenType.Keyword, "var", false);
    public Terminal If { get; } = new Terminal(TokenType.Keyword, "if", false);
    public Terminal For { get; } = new Terminal(TokenType.Keyword, "for", false);
    public Terminal In { get; } = new Terminal(TokenType.Keyword, "in", false);
    public Terminal While { get; } = new Terminal(TokenType.Keyword, "while", false);
    public Terminal Repeat { get; } = new Terminal(TokenType.Keyword, "repeat", false);
    public Terminal Until { get; } = new Terminal(TokenType.Keyword, "until", false);

    // else stays in the AST (meaning=true) so the emitter can split the then-branch from the else-branch.
    public Terminal Else { get; } = new Terminal(TokenType.Keyword, "else", "else", true, false);

    // functors: meaning=true so the gate name that follows can be distinguished from the prefix.
    public Terminal Adjoint { get; } = new Terminal(TokenType.Keyword, "Adjoint", "Adjoint", true, false);
    public Terminal Controlled { get; } = new Terminal(TokenType.Keyword, "Controlled", "Controlled", true, false);

    // --- type keywords (meaning=TRUE so they stay in the AST; bWordPattern=false for keyword priority) ---
    public Terminal Int { get; } = new Terminal(TokenType.Keyword, "int", "int", true, false);
    public Terminal Bit { get; } = new Terminal(TokenType.Keyword, "bit", "bit", true, false);

    // Line comments: `//` to end of line. Value is a RAW regex — Janglim >=0.2.0-preview.3 uses a
    // comment terminal's Value verbatim (no \b wrapping), so "//.*$" wins the lexer's longest-match over
    // the "/" operator; meaning=false + TokenType.SpecialToken.Comment -> the lexer filters it out of the
    // parse stream. (Block comments `/* */` still need the engine's ScopeInfo path wired into this Lexer,
    // so they stay deferred — see AJPGS/docs/TODO.md.)
    public Terminal LineComment { get; } = new Terminal(TokenType.SpecialToken.Comment, "//.*$", false, true);

    // --- identifier + numbers (meaning=true -> kept in AST). Float must out-length Num so the lexer's
    //     longest-match picks "0.5" as one Float (not Num "0" + ".5"); `pi` is just an identifier the
    //     emitter passes through (OpenQASM has a built-in pi). ---
    public Terminal Ident { get; } = new Terminal(TokenType.Identifier, "[_a-zA-Z][_a-zA-Z0-9]*", "ident", true, true);
    public Terminal Float { get; } = new Terminal(TokenType.Literal.Digit10, "[0-9]+\\.[0-9]+", "float", true, true);
    public Terminal Num { get; } = new Terminal(TokenType.Literal.Digit10, "[0-9]+", "num", true, true);

    // --- punctuation / operators. Arithmetic (+ - * /) and now the comparison/boolean operators are
    //     meaning=true so they survive into the AST and get emitted verbatim (OpenQASM re-parses precedence). ---
    public Terminal LParen { get; } = new Terminal(TokenType.Operator.PairOpen, "(", false);
    public Terminal RParen { get; } = new Terminal(TokenType.Operator.PairClose, ")", false);
    public Terminal LBrace { get; } = new Terminal(TokenType.Operator.PairOpen, "{", false);
    public Terminal RBrace { get; } = new Terminal(TokenType.Operator.PairClose, "}", false);
    public Terminal LBracket { get; } = new Terminal(TokenType.Operator.PairOpen, "[", false);
    public Terminal RBracket { get; } = new Terminal(TokenType.Operator.PairClose, "]", false);
    public Terminal Comma { get; } = new Terminal(TokenType.Operator.Comma, ",", false);
    public Terminal Assign { get; } = new Terminal(TokenType.Operator, "=", false);
    public Terminal DotDot { get; } = new Terminal(TokenType.Operator, "..", false);
    // single dot for qualified names (Lib.Op); ".." wins by longest match, "0.5" lexes as one Float.
    public Terminal Dot { get; } = new Terminal(TokenType.Operator, ".", "." , true, false);
    // Quote-delimited string (raw regex — Janglim >=0.3.0-preview.1's StringLiteral token type skips
    // the \b wrapping that made this impossible before; see docs/janglim-request-raw-regex-terminal.md).
    public Terminal StringLit { get; } = new Terminal(TokenType.Literal.StringLiteral, "\"[^\"]*\"", "string", true, true);

    // comparison + boolean operators (meaning=true; the lexer's longest-match keeps == / != / <= / >= whole)
    public Terminal Eq { get; } = new Terminal(TokenType.Operator, "==", "==", true, false);
    public Terminal Neq { get; } = new Terminal(TokenType.Operator, "!=", "!=", true, false);
    public Terminal Le { get; } = new Terminal(TokenType.Operator, "<=", "<=", true, false);
    public Terminal Ge { get; } = new Terminal(TokenType.Operator, ">=", ">=", true, false);
    public Terminal Lt { get; } = new Terminal(TokenType.Operator, "<", "<", true, false);
    public Terminal Gt { get; } = new Terminal(TokenType.Operator, ">", ">", true, false);
    public Terminal And { get; } = new Terminal(TokenType.Operator, "&&", "&&", true, false);
    public Terminal Or { get; } = new Terminal(TokenType.Operator, "||", "||", true, false);
    public Terminal Not { get; } = new Terminal(TokenType.Operator, "!", "!", true, false);

    public Terminal Plus { get; } = new Terminal(TokenType.Operator, "+", "+", true, false);
    public Terminal Minus { get; } = new Terminal(TokenType.Operator, "-", "-", true, false);
    public Terminal Mul { get; } = new Terminal(TokenType.Operator, "*", "*", true, false);
    public Terminal Div { get; } = new Terminal(TokenType.Operator, "/", "/", true, false);
    public Terminal Semicolon { get; } = new Terminal(TokenType.Operator, ";", false);

    // --- non-terminals ---
    private NonTerminal program = new NonTerminal("program", true);
    private NonTerminal operationList = new NonTerminal("operationList");
    private NonTerminal topItem = new NonTerminal("topItem");
    private NonTerminal importStmt = new NonTerminal("importStmt");
    private NonTerminal namespaceDecl = new NonTerminal("namespaceDecl");
    private NonTerminal nsItem = new NonTerminal("nsItem");
    private NonTerminal openStmt = new NonTerminal("openStmt");
    private NonTerminal qname = new NonTerminal("qname");
    private NonTerminal operation = new NonTerminal("operation");
    private NonTerminal paramList = new NonTerminal("paramList");
    private NonTerminal param = new NonTerminal("param");
    private NonTerminal statement = new NonTerminal("statement");
    private NonTerminal useStmt = new NonTerminal("useStmt");
    private NonTerminal gateStmt = new NonTerminal("gateStmt");
    private NonTerminal arg = new NonTerminal("arg");
    private NonTerminal constDecl = new NonTerminal("constDecl");
    private NonTerminal varDecl = new NonTerminal("varDecl");
    private NonTerminal assignStmt = new NonTerminal("assignStmt");
    private NonTerminal ifStmt = new NonTerminal("ifStmt");
    private NonTerminal forStmt = new NonTerminal("forStmt");
    private NonTerminal whileStmt = new NonTerminal("whileStmt");
    private NonTerminal repeatStmt = new NonTerminal("repeatStmt");
    private NonTerminal typeName = new NonTerminal("typeName");
    private NonTerminal condition = new NonTerminal("condition");
    private NonTerminal condAtom = new NonTerminal("condAtom");
    private NonTerminal expr = new NonTerminal("expr");
    private NonTerminal primary = new NonTerminal("primary");
    private NonTerminal call = new NonTerminal("call");
    private NonTerminal qubitRef = new NonTerminal("qubitRef");
    private NonTerminal index = new NonTerminal("index");

    // --- semantic tags ---
    public static MeaningUnit ProgramM { get; } = new MeaningUnit("Program");
    public static MeaningUnit ImportM { get; } = new MeaningUnit("Import");
    public static MeaningUnit NamespaceM { get; } = new MeaningUnit("Namespace");
    public static MeaningUnit OpenM { get; } = new MeaningUnit("Open");
    public static MeaningUnit OperationM { get; } = new MeaningUnit("Operation");
    public static MeaningUnit ParamM { get; } = new MeaningUnit("Param");
    public static MeaningUnit UseM { get; } = new MeaningUnit("Use");
    public static MeaningUnit GateM { get; } = new MeaningUnit("Gate");
    public static MeaningUnit ConstDeclM { get; } = new MeaningUnit("ConstDecl");
    public static MeaningUnit VarDeclM { get; } = new MeaningUnit("VarDecl");
    public static MeaningUnit AssignM { get; } = new MeaningUnit("Assign");
    public static MeaningUnit IfM { get; } = new MeaningUnit("If");
    public static MeaningUnit ForM { get; } = new MeaningUnit("For");
    public static MeaningUnit WhileM { get; } = new MeaningUnit("While");
    public static MeaningUnit RepeatM { get; } = new MeaningUnit("Repeat");
    public static MeaningUnit ConditionM { get; } = new MeaningUnit("Condition");
    public static MeaningUnit ExprM { get; } = new MeaningUnit("Expr");
    public static MeaningUnit CallM { get; } = new MeaningUnit("Call");
    public static MeaningUnit QubitM { get; } = new MeaningUnit("Qubit");

    public override NonTerminal EbnfRoot => program;

    public QoraGrammar()
    {
        program.AddItem(operationList, ProgramM);
        operationList.AddItem(topItem | operationList + topItem);
        topItem.AddItem(operation | importStmt | namespaceDecl);

        // module system (in progress — parsed and surfaced, gated by the validator until resolution lands):
        //   import gates_lib;        // bare module name -> gates_lib.qor (dots become directories later)
        //   import "gates lib.qor";  // string path form (for names a bare identifier can't spell)
        //   namespace Lib.Sub { open Other; operation Foo() { … } }
        importStmt.AddItem(Import + qname + Semicolon, ImportM);
        importStmt.AddItem(Import + StringLit + Semicolon, ImportM);
        namespaceDecl.AddItem(Namespace + qname + LBrace + nsItem.ZeroOrMore() + RBrace, NamespaceM);
        nsItem.AddItem(openStmt | operation);
        openStmt.AddItem(Open + qname + Semicolon, OpenM);
        qname.AddItem(Ident + (Dot + Ident).ZeroOrMore());

        // operation Name() { … }   /   operation Name(params) { … }
        operation.AddItem(Operation + Ident + LParen + RParen + LBrace + statement.ZeroOrMore() + RBrace, OperationM);
        operation.AddItem(Operation + Ident + LParen + paramList + RParen + LBrace + statement.ZeroOrMore() + RBrace, OperationM);

        paramList.AddItem(param + (Comma + param).ZeroOrMore());
        // Qubit q  /  Qubit[2] q  /  int n  /  bit b   (Qubit keyword is dropped; a register keeps its size Num)
        param.AddItem(Qubit + Ident, ParamM);
        param.AddItem(Qubit + LBracket + Num + RBracket + Ident, ParamM);
        param.AddItem(typeName + Ident, ParamM);

        statement.AddItem(useStmt | gateStmt | constDecl | varDecl | assignStmt | ifStmt | forStmt | whileStmt | repeatStmt);

        // use q = Qubit[2];
        useStmt.AddItem(Use + Ident + Assign + Qubit + LBracket + Num + RBracket + Semicolon, UseM);

        // gate / rotation / operation call — all share one form Ident(args…); the emitter tells them
        // apart by name. H(q[0]); CNOT(q[0],q[1]); Rx(pi/2, q[0]); Bell(q); Reset(q[0]);
        // A functor prefix (Adjoint / Controlled) applies to a single built-in gate:
        //   Adjoint S(q)      -> inv @ s q;
        //   Controlled X(c,t) -> ctrl @ x c, t;
        // call/gate names may be namespace-qualified (MyLib.Bell) — qname = Ident (Dot Ident)*
        gateStmt.AddItem(qname + LParen + RParen + Semicolon, GateM);
        gateStmt.AddItem(qname + LParen + arg + (Comma + arg).ZeroOrMore() + RParen + Semicolon, GateM);
        gateStmt.AddItem(Adjoint + qname + LParen + RParen + Semicolon, GateM);
        gateStmt.AddItem(Adjoint + qname + LParen + arg + (Comma + arg).ZeroOrMore() + RParen + Semicolon, GateM);
        gateStmt.AddItem(Controlled + qname + LParen + RParen + Semicolon, GateM);
        gateStmt.AddItem(Controlled + qname + LParen + arg + (Comma + arg).ZeroOrMore() + RParen + Semicolon, GateM);
        arg.AddItem(qubitRef | expr);   // q[0] (qubit)  |  an expression: q (register) / 5 / pi/2 / 0.5

        // const i = expr;   /   const int i = expr;
        constDecl.AddItem(Const + Ident + Assign + expr + Semicolon, ConstDeclM);
        constDecl.AddItem(Const + typeName + Ident + Assign + expr + Semicolon, ConstDeclM);

        // var i = expr;   /   int i = expr;   /   bit r = M(q[0]);   (all mutable)
        varDecl.AddItem(Var + Ident + Assign + expr + Semicolon, VarDeclM);
        varDecl.AddItem(typeName + Ident + Assign + expr + Semicolon, VarDeclM);

        // i = expr;
        assignStmt.AddItem(Ident + Assign + expr + Semicolon, AssignM);

        // if (cond) { … }   /   if (cond) { … } else { … }   /   if (cond) { … } else if (…) { … }
        ifStmt.AddItem(If + LParen + condition + RParen + LBrace + statement.ZeroOrMore() + RBrace, IfM);
        ifStmt.AddItem(If + LParen + condition + RParen + LBrace + statement.ZeroOrMore() + RBrace + Else + LBrace + statement.ZeroOrMore() + RBrace, IfM);
        ifStmt.AddItem(If + LParen + condition + RParen + LBrace + statement.ZeroOrMore() + RBrace + Else + ifStmt, IfM);

        forStmt.AddItem(For + Ident + In + Num + DotDot + Num + LBrace + statement.ZeroOrMore() + RBrace, ForM);
        whileStmt.AddItem(While + LParen + condition + RParen + LBrace + statement.ZeroOrMore() + RBrace, WhileM);
        repeatStmt.AddItem(Repeat + LBrace + statement.ZeroOrMore() + RBrace + Until + LParen + condition + RParen + Semicolon, RepeatM);

        typeName.AddItem(Int | Bit);

        // condition: a flat boolean expression joined by comparison/boolean operators. Flat on purpose —
        // the tokens emit in order and OpenQASM re-parses with its own precedence, so "a == 1 && b == 0"
        // comes out as (a==1) && (b==0) without needing inner parens.
        condition.AddItem(condAtom + ((Eq | Neq | Le | Ge | Lt | Gt | And | Or) + condAtom).ZeroOrMore(), ConditionM);
        condAtom.AddItem(expr);
        condAtom.AddItem(Not + expr);

        // expression: atoms joined by + - * /   (e.g.  i + 1 ,  pi / 2 ,  2 * pi ,  0.5 ,  M(q[0]) ).
        // A leading unary minus is its own production (e.g. -pi/2 for rotation angles).
        expr.AddItem(primary + ((Plus | Minus | Mul | Div) + primary).ZeroOrMore(), ExprM);
        primary.AddItem(Num | Float | Ident | call);
        primary.AddItem(Minus + primary);
        call.AddItem(Ident + LParen + qubitRef + RParen, CallM);   // M(q[0])

        qubitRef.AddItem(Ident + LBracket + index + RBracket, QubitM);
        index.AddItem(Num | Ident);

        this.Optimization();
    }
}
