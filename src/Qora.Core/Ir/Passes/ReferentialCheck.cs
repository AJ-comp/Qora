namespace Qora.Ir.Passes;

/// <summary>
/// Referential-integrity gate — a post-mangle safety net. After <see cref="NameMangler"/> has given every
/// name its final emitted form, this pass checks that every identifier the program USES resolves to
/// something DECLARED: a parameter / register / variable / loop variable in scope, an operation, or a
/// built-in gate/keyword/constant. A dangling reference means some pass renamed a declaration but not one
/// of its uses (a name-map inconsistency) — which would otherwise silently emit invalid QASM that only
/// fails at execution. This turns that whole class of internal bug into a loud compile error (QINTERNAL).
///
/// It is CONSERVATIVE: an identifier is flagged only when it matches NONE of the known sets, so a valid
/// program never trips it. Runs on the MANGLED program, right before emission.
/// </summary>
public static class ReferentialCheck
{
    // The only bare identifiers valid in a VALUE position (index / for-bound / expression) besides the
    // op's own declared names. Gate names and keywords are NOT values — they are call targets — so they
    // are deliberately excluded here (a stdgate name like `s` in a bound is a dangling reference, not a gate).
    private static readonly HashSet<string> Constants = new() { "pi", "tau", "euler", "true", "false" };

    public static List<QoraError> Verify(QProgram program)
    {
        var errors = new List<QoraError>();
        if (program.Operations.Count == 0) return errors;
        var opNames = program.Operations.Select(o => o.Name).ToHashSet();

        CheckIdUniqueness(program, errors);

        foreach (var op in program.Operations)
        {
            // this op's in-scope declared names (mangled). Since NameMangler renames any user name that
            // would collide with a reserved word, a declared name is never itself a reserved word.
            var declared = new HashSet<string>();
            foreach (var p in op.Params) declared.Add(p.Name);
            CollectDecls(op.Body, declared);

            CheckBody(op.Body, op.Name, declared, opNames, errors);
        }
        return errors;
    }

    /// <summary>
    /// Sweep every op / param / statement in the final program and flag any <see cref="QNodeIds">node
    /// Id</see> that appears twice. A duplicate means a pass duplicated a subtree with <c>with</c> and
    /// installed the copy without <see cref="ReId"/> — which would silently corrupt every side table
    /// keyed by Id (the <see cref="SymbolTableBuilder"/>-derived semantic model first among them).
    /// </summary>
    private static void CheckIdUniqueness(QProgram program, List<QoraError> errors)
    {
        var seen = new HashSet<int>();
        void Visit(int id, QSpan? span)
        {
            if (!seen.Add(id))
                errors.Add(new QoraError(
                    $"internal compiler error: duplicate node id {id} — a pass copied a subtree without ReId — please report this",
                    "QINTERNAL", span?.Start ?? -1, span?.End ?? -1));
        }
        void Walk(IReadOnlyList<QStmt> stmts)
        {
            foreach (var s in stmts)
            {
                Visit(s.Id, s.Span);
                switch (s)
                {
                    case QIf i: Walk(i.Then); Walk(i.Else); break;
                    case QFor f: Walk(f.Body); break;
                    case QWhile w: Walk(w.Body); break;
                    case QRepeat r: Walk(r.Body); break;
                    case QConjugate c: Walk(c.Within); Walk(c.Apply); break;
                }
            }
        }
        Visit(program.Id, null);
        foreach (var op in program.Operations)
        {
            Visit(op.Id, op.Span);
            foreach (var p in op.Params) Visit(p.Id, op.Span);
            Walk(op.Body);
        }
    }

    private static void CollectDecls(IReadOnlyList<QStmt> stmts, HashSet<string> into)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QUse u: into.Add(u.Name); break;
                case QDecl d: into.Add(d.Name); break;
                case QFor f: into.Add(f.Var); CollectDecls(f.Body, into); break;
                case QIf i: CollectDecls(i.Then, into); CollectDecls(i.Else, into); break;
                case QWhile w: CollectDecls(w.Body, into); break;
                case QRepeat r: CollectDecls(r.Body, into); break;
                case QConjugate c: CollectDecls(c.Within, into); CollectDecls(c.Apply, into); break;
            }
    }

    private static void CheckBody(IReadOnlyList<QStmt> stmts, string opName, HashSet<string> known, HashSet<string> opNames, List<QoraError> errors)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case QGate g:
                    // a call target must be a user operation or a built-in gate.
                    if (!opNames.Contains(g.Name) && !QoraGates.Names.ContainsKey(g.Name))
                        Report(g.Name, opName, "call target", g.Span, errors);
                    foreach (var a in g.Args) CheckArg(a, opName, known, errors, g.Span);
                    break;
                case QDecl d:
                    CheckExpr(d.Value, opName, known, errors, d.Span);
                    break;
                case QAssign a:
                    Check(a.Name, opName, known, errors, a.Span);
                    if (a.Index is not null) CheckTokens(a.Index, opName, known, errors, a.Span);
                    CheckExpr(a.Value, opName, known, errors, a.Span);
                    break;
                case QIf i:
                    CheckTokens(i.Cond.Text, opName, known, errors, i.Span);
                    CheckBody(i.Then, opName, known, opNames, errors);
                    CheckBody(i.Else, opName, known, opNames, errors);
                    break;
                case QFor f:
                    CheckTokens(f.From, opName, known, errors, f.Span);
                    CheckTokens(f.To, opName, known, errors, f.Span);
                    CheckBody(f.Body, opName, known, opNames, errors);
                    break;
                case QWhile w:
                    CheckTokens(w.Cond.Text, opName, known, errors, w.Span);
                    CheckBody(w.Body, opName, known, opNames, errors);
                    break;
                case QRepeat r:
                    CheckBody(r.Body, opName, known, opNames, errors);
                    CheckTokens(r.Until.Text, opName, known, errors, r.Span);
                    break;
                case QConjugate c:
                    // ConjugationLowering flattens every QConjugate into straight-line gates + a synthesized
                    // inverse BEFORE mangling. One surviving to here means that pass was skipped or a later
                    // pass minted a fresh conjugation — a compiler bug, not a user error. Fail loudly rather
                    // than silently dropping its gates at emission (the emitter has no QConjugate case).
                    errors.Add(new QoraError(
                        $"internal compiler error: in `{opName}`, a within/apply block reached emission un-flattened (ConjugationLowering did not run) — please report this",
                        "QINTERNAL", c.Span?.Start ?? -1, c.Span?.End ?? -1));
                    break;
            }
    }

    private static void CheckArg(QArg arg, string opName, HashSet<string> known, List<QoraError> errors, QSpan? span)
    {
        switch (arg)
        {
            case QQubitArg q:
                Check(q.Reg, opName, known, errors, span);
                Check(q.Index, opName, known, errors, span);
                break;
            case QTextArg t:
                CheckTokens(t.Text, opName, known, errors, span);
                break;
        }
    }

    private static void CheckExpr(QExpr expr, string opName, HashSet<string> known, List<QoraError> errors, QSpan? span)
    {
        switch (expr)
        {
            case QMeasure { Target: { } t }:
                Check(t.Reg, opName, known, errors, span);
                Check(t.Index, opName, known, errors, span);
                break;
            case QText t:
                CheckTokens(t.Text, opName, known, errors, span);
                break;
            case QArrayLiteral literal:
                foreach (var element in literal.Elements)
                    CheckExpr(element, opName, known, errors, span);
                break;
        }
    }

    private static void CheckTokens(string text, string opName, HashSet<string> known, List<QoraError> errors, QSpan? span)
    {
        foreach (var member in SymbolTableBuilder.MemberAccesses(text))
            Check(member.Base, opName, known, errors, span);
        foreach (var name in SymbolTableBuilder.ExpressionIdentifiers(text))
            Check(name, opName, known, errors, span);
    }

    /// <summary>Check ONE token: strip surrounding parentheses, and flag it only if the core is an
    /// identifier that is not known here. Numbers, operators and punctuation are ignored.</summary>
    private static void Check(string tok, string opName, HashSet<string> declared, List<QoraError> errors, QSpan? span)
    {
        var core = tok.Trim('(', ')');
        if (IsIdentifier(core) && !declared.Contains(core) && !Constants.Contains(core))
            Report(core, opName, "reference", span, errors);
    }

    private static void Report(string name, string opName, string kind, QSpan? span, List<QoraError> errors) =>
        errors.Add(new QoraError(
            $"internal compiler error: in `{opName}`, the emitted-QASM {kind} `{name}` is not declared (a name was not renamed consistently) — please report this",
            "QINTERNAL", span?.Start ?? -1, span?.End ?? -1));

    private static bool IsIdentifier(string tok) =>
        tok.Length > 0 && (char.IsLetter(tok[0]) || tok[0] == '_')
                       && tok.All(c => char.IsLetterOrDigit(c) || c == '_');
}
