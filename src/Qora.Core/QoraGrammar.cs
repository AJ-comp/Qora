using Janglim;
using Janglim.FrontEnd;
using Janglim.FrontEnd.Grammars;
using Janglim.FrontEnd.RegularGrammar;

namespace Qora;

/// <summary>
/// Qora v0.6 — a Q#/C#-flavored quantum language on the Janglim engine.
///
///   operation Bell(Qubit[2] q) {       // a subroutine, with C#-style parameters
///       H(q[0]);
///       CNOT(q[0], q[1]);
///   }
///
///   operation Main() {                  // the entry point -> OpenQASM top-level
///       use q = Qubit[2];
///       Bell(q);                        // call (whole register) — or Bell(q[0], q[1])
///       bit r = M(q[0]);
///   }
///
/// Calls and gate applications share one surface form (<c>Ident(args…)</c>); the emitter tells them
/// apart by name (a defined operation -> a call, otherwise a gate). The operation named <c>Main</c>
/// is the entry (its body becomes the QASM top-level); every other operation becomes a <c>def</c>.
/// Operations are void for now (no return value yet).
/// </summary>
public class QoraGrammar : Grammar
{
    // --- keywords (meaning=false -> excluded from AST; bWord=false -> win the lexer tie vs identifier) ---
    public Terminal Operation { get; } = new Terminal(TokenType.Keyword, "operation", false);
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

    // --- type keywords (meaning=TRUE so they stay in the AST; bWordPattern=false for keyword priority) ---
    public Terminal Int { get; } = new Terminal(TokenType.Keyword, "int", "int", true, false);
    public Terminal Bit { get; } = new Terminal(TokenType.Keyword, "bit", "bit", true, false);

    // --- identifier + numbers (meaning=true -> kept in AST). Float must out-length Num so the lexer's
    //     longest-match picks "0.5" as one Float (not Num "0" + ".5"); `pi` is just an identifier the
    //     emitter passes through (OpenQASM has a built-in pi). ---
    public Terminal Ident { get; } = new Terminal(TokenType.Identifier, "[_a-zA-Z][_a-zA-Z0-9]*", "ident", true, true);
    public Terminal Float { get; } = new Terminal(TokenType.Literal.Digit10, "[0-9]+\\.[0-9]+", "float", true, true);
    public Terminal Num { get; } = new Terminal(TokenType.Literal.Digit10, "[0-9]+", "num", true, true);

    // --- punctuation / operators. '+'/'-' are meaning=true so arithmetic survives into the AST. ---
    public Terminal LParen { get; } = new Terminal(TokenType.Operator.PairOpen, "(", false);
    public Terminal RParen { get; } = new Terminal(TokenType.Operator.PairClose, ")", false);
    public Terminal LBrace { get; } = new Terminal(TokenType.Operator.PairOpen, "{", false);
    public Terminal RBrace { get; } = new Terminal(TokenType.Operator.PairClose, "}", false);
    public Terminal LBracket { get; } = new Terminal(TokenType.Operator.PairOpen, "[", false);
    public Terminal RBracket { get; } = new Terminal(TokenType.Operator.PairClose, "]", false);
    public Terminal Comma { get; } = new Terminal(TokenType.Operator.Comma, ",", false);
    public Terminal Eq { get; } = new Terminal(TokenType.Operator, "==", false);
    public Terminal Assign { get; } = new Terminal(TokenType.Operator, "=", false);
    public Terminal DotDot { get; } = new Terminal(TokenType.Operator, "..", false);
    public Terminal Plus { get; } = new Terminal(TokenType.Operator, "+", "+", true, false);
    public Terminal Minus { get; } = new Terminal(TokenType.Operator, "-", "-", true, false);
    public Terminal Mul { get; } = new Terminal(TokenType.Operator, "*", "*", true, false);
    public Terminal Div { get; } = new Terminal(TokenType.Operator, "/", "/", true, false);
    public Terminal Semicolon { get; } = new Terminal(TokenType.Operator, ";", false);

    // --- non-terminals ---
    private NonTerminal program = new NonTerminal("program", true);
    private NonTerminal operationList = new NonTerminal("operationList");
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
    private NonTerminal expr = new NonTerminal("expr");
    private NonTerminal primary = new NonTerminal("primary");
    private NonTerminal call = new NonTerminal("call");
    private NonTerminal qubitRef = new NonTerminal("qubitRef");
    private NonTerminal index = new NonTerminal("index");

    // --- semantic tags ---
    public static MeaningUnit ProgramM { get; } = new MeaningUnit("Program");
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
    public static MeaningUnit ExprM { get; } = new MeaningUnit("Expr");
    public static MeaningUnit CallM { get; } = new MeaningUnit("Call");
    public static MeaningUnit QubitM { get; } = new MeaningUnit("Qubit");

    public override NonTerminal EbnfRoot => program;

    public QoraGrammar()
    {
        program.AddItem(operationList, ProgramM);
        operationList.AddItem(operation | operationList + operation);

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
        // apart by name. H(q[0]); CNOT(q[0],q[1]); Rx(pi/2, q[0]); Bell(q); Bell(q[0],q[1]); Reset();
        gateStmt.AddItem(Ident + LParen + RParen + Semicolon, GateM);
        gateStmt.AddItem(Ident + LParen + arg + (Comma + arg).ZeroOrMore() + RParen + Semicolon, GateM);
        arg.AddItem(qubitRef | expr);   // q[0] (qubit)  |  an expression: q (register) / 5 / pi/2 / 0.5

        // const i = expr;   /   const int i = expr;
        constDecl.AddItem(Const + Ident + Assign + expr + Semicolon, ConstDeclM);
        constDecl.AddItem(Const + typeName + Ident + Assign + expr + Semicolon, ConstDeclM);

        // var i = expr;   /   int i = expr;   /   bit r = M(q[0]);   (all mutable)
        varDecl.AddItem(Var + Ident + Assign + expr + Semicolon, VarDeclM);
        varDecl.AddItem(typeName + Ident + Assign + expr + Semicolon, VarDeclM);

        // i = expr;
        assignStmt.AddItem(Ident + Assign + expr + Semicolon, AssignM);

        ifStmt.AddItem(If + LParen + Ident + Eq + Num + RParen + LBrace + statement.ZeroOrMore() + RBrace, IfM);
        forStmt.AddItem(For + Ident + In + Num + DotDot + Num + LBrace + statement.ZeroOrMore() + RBrace, ForM);
        whileStmt.AddItem(While + LParen + Ident + Eq + Num + RParen + LBrace + statement.ZeroOrMore() + RBrace, WhileM);
        repeatStmt.AddItem(Repeat + LBrace + statement.ZeroOrMore() + RBrace + Until + LParen + Ident + Eq + Num + RParen + Semicolon, RepeatM);

        typeName.AddItem(Int | Bit);

        // expression: atoms joined by + - * /   (e.g.  i + 1 ,  pi / 2 ,  2 * pi ,  0.5 ,  M(q[0]) ).
        // Flat (no precedence) on purpose — the tokens are emitted in order and OpenQASM re-parses them
        // with its own precedence, so the string "1 + pi / 2" comes out correct regardless of tree shape.
        expr.AddItem(primary + ((Plus | Minus | Mul | Div) + primary).ZeroOrMore(), ExprM);
        primary.AddItem(Num | Float | Ident | call);
        call.AddItem(Ident + LParen + qubitRef + RParen, CallM);   // M(q[0])

        qubitRef.AddItem(Ident + LBracket + index + RBracket, QubitM);
        index.AddItem(Num | Ident);

        this.Optimization();
    }
}
