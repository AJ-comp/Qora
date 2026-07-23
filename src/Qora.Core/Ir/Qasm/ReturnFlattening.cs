using System.Linq;

namespace Qora.Ir.Passes;

/// <summary>
/// TARGET lowering for OpenQASM 3: rewrite a function so it has EXACTLY ONE <c>return</c>, as its last
/// statement. Qora lets <c>return</c> stand anywhere a statement may — that is what every language does, and
/// the OpenQASM 3 grammar allows it too — but the execution target cannot leave a <c>def</c> from inside a
/// nested block (the Braket local simulator crashes on it), so the SHAPE is adapted here rather than the
/// language being narrowed to what the target happens to run.
///
/// This whole pass is a TARGET workaround: a target that runs a <c>return</c> from a nested block needs none
/// of it. Removing it is deleting this file and its one line in <see cref="QasmBackend"/> — nothing in the
/// language layer depends on it. The coverage rule it reads (<see cref="QoraValidator.AlwaysReturns"/>) is
/// the LANGUAGE's, not this pass's, and lives there so it survives the pass's removal.
///
/// The rewrite rests on one observation: <b>"return here" means "produce this value and do nothing further"</b>.
/// So a return becomes an assignment to one result variable, and everything it would have skipped is placed
/// where it cannot run:
/// <list type="bullet">
///   <item>Code after a <c>return</c> IN THE SAME BLOCK is unreachable, and is dropped.</item>
///   <item>Code after an <c>if</c> whose <c>then</c> always returns moves INTO its <c>else</c> — the branch
///         that did not return is exactly the path that should still run it. No bookkeeping is needed.</item>
///   <item>A return inside a LOOP <c>break</c>s out of it, which is why no later iteration can overwrite the
///         result. What <c>break</c> cannot say is "the code AFTER the loop must not run either" — the
///         statements following a loop are not inside any branch to move into — so this one case also sets a
///         done flag, which guards exactly those statements (and breaks out of each ENCLOSING loop, since a
///         <c>break</c> leaves only the innermost). The flag is minted ONLY when a loop contains a return.</item>
/// </list>
/// Both minted names are <see cref="HoistName"/> placeholders, so they cannot collide with a user name and
/// the <see cref="NameMangler"/> gives them their final readable spelling — the same convention
/// <see cref="ArrayLocalHoisting"/> uses. This pass therefore runs BEFORE mangling.
///
/// Operations are untouched: an <c>operation</c> is void, and a <c>return</c> in one is QSEM035.
/// </summary>
public static class ReturnFlattening
{
    public static QProgram Run(QProgram program) =>
        program with { Operations = program.Operations.Select(Flatten).ToList() };

    private static QOperation Flatten(QOperation op)
    {
        if (!op.IsFunction || op.ReturnType is not { } returnType) return op;
        if (!Contains(op.Body, r => true)) return op;   // no return at all — QSEM035's business, not ours

        var uid = 0;
        var result = HoistName.Make("ret", uid++);
        var done = HoistName.Make("done", uid++);
        var needsFlag = ContainsLoopReturn(op.Body);

        var body = new List<QStmt>
        {
            new QDecl(false, returnType, result, Zero(returnType)),
        };
        if (needsFlag) body.Add(new QDecl(false, QType.Int, done, Zero(QType.Int)));

        body.AddRange(Rewrite(op.Body, result, done, needsFlag, inLoop: false));
        body.Add(new QReturn(new QText(new QNameRef(result))));
        return op with { Body = body };
    }

    /// <summary>Rewrite a statement list so it contains no <c>return</c>: each one becomes an assignment to
    /// <paramref name="result"/>, and whatever it would have skipped is moved somewhere it cannot run.
    /// <paramref name="inLoop"/> says whether this list is (directly or not) a loop body, which is what makes
    /// <c>break</c> the right way to stop.</summary>
    private static List<QStmt> Rewrite(IReadOnlyList<QStmt> stmts, string result, string done, bool flag, bool inLoop)
    {
        var output = new List<QStmt>(stmts.Count);
        for (var i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            var tail = stmts.Skip(i + 1).ToList();

            switch (s)
            {
                // `return e;` — produce the value. Anything after it in this block is unreachable. Inside a
                // loop, leaving it IS the "do nothing further" part, so break out; the flag then tells the
                // statements after the loop (which no break can reach) not to run.
                case QReturn ret:
                    output.Add(new QAssign(result, ret.Value));
                    // The flag exists to stop LOOPS and to skip what follows one; a return reached outside any
                    // loop has nothing left to tell — the statements it skipped are already in a branch that
                    // cannot run — so setting it there would be a write nothing reads.
                    if (flag && inLoop)
                    {
                        output.Add(new QAssign(done, One()));
                        output.Add(new QBreak());
                    }
                    return output;

                // An `if` whose THEN always returns: the statements after the `if` belong to the path that did
                // NOT return, so they move into the `else`. This is why the ordinary case needs no flag — the
                // structure already says "one of these two happened". "Always returns" is the LANGUAGE rule
                // (QoraValidator.AlwaysReturns), read here rather than redefined — the two must agree.
                case QIf branch when QoraValidator.AlwaysReturns(branch.Then):
                    output.Add(branch with
                    {
                        Then = Rewrite(branch.Then, result, done, flag, inLoop),
                        Else = Rewrite(branch.Else.Concat(tail).ToList(), result, done, flag, inLoop),
                    });
                    return output;

                // The mirror case: the ELSE always returns, so the tail belongs to the `then` path.
                case QIf branch when branch.Else.Count > 0 && QoraValidator.AlwaysReturns(branch.Else):
                    output.Add(branch with
                    {
                        Then = Rewrite(branch.Then.Concat(tail).ToList(), result, done, flag, inLoop),
                        Else = Rewrite(branch.Else, result, done, flag, inLoop),
                    });
                    return output;

                // A statement that may return THROUGH A LOOP — whether it IS the loop or merely CONTAINS one
                // (a loop inside an `if`, a loop inside a loop). A break can leave only the innermost loop it
                // sits in, so it cannot reach past this statement; from here on `done` carries "a loop already
                // returned". It guards every following statement in this block, and — if this block is itself a
                // loop body — breaks out of the enclosing loop too. This is the general form of the loop case:
                // the earlier `if`-moves-its-tail trick does not apply, because a loop's tail is not inside any
                // branch to move into.
                case var _ when ContainsLoopReturn(new[] { s }):
                    output.Add(Descend(s, result, done, flag, inLoop));
                    if (inLoop) output.Add(IfDone(new List<QStmt> { new QBreak() }, done));
                    if (tail.Count > 0) output.Add(NotDone(Rewrite(tail, result, done, flag, inLoop), done));
                    return output;

                // Nothing here returns through a loop; recurse into any nested body so a return deeper inside
                // (reachable by moving a tail into a branch) is still found.
                default:
                    output.Add(Descend(s, result, done, flag, inLoop));
                    break;
            }
        }
        return output;
    }

    /// <summary>Recurse into a statement's nested bodies without changing its own shape. A LOOP body is
    /// entered with <c>inLoop: true</c>, so a return anywhere inside it becomes "produce the value, mark done,
    /// leave the loop"; every other nested body keeps the caller's <paramref name="inLoop"/>.</summary>
    private static QStmt Descend(QStmt s, string result, string done, bool flag, bool inLoop) => s switch
    {
        QIf i => i with
        {
            Then = Rewrite(i.Then, result, done, flag, inLoop),
            Else = Rewrite(i.Else, result, done, flag, inLoop),
        },
        QFor f => f with { Body = Rewrite(f.Body, result, done, flag, inLoop: true) },
        QWhile w => w with { Body = Rewrite(w.Body, result, done, flag, inLoop: true) },
        QRepeat r => r with { Body = Rewrite(r.Body, result, done, flag, inLoop: true) },
        QConjugate c => c with
        {
            Within = Rewrite(c.Within, result, done, flag, inLoop),
            Apply = Rewrite(c.Apply, result, done, flag, inLoop),
        },
        _ => s,
    };

    private static QStmt NotDone(IReadOnlyList<QStmt> body, string done) =>
        new QIf(new QCond(new QBinOp("==", new QNameRef(done), new QNumLit(0))), body, new List<QStmt>());

    private static QStmt IfDone(IReadOnlyList<QStmt> body, string done) =>
        new QIf(new QCond(new QBinOp("==", new QNameRef(done), new QNumLit(1))), body, new List<QStmt>());

    private static bool ContainsReturn(QStmt s) => Contains(new[] { s }, _ => true);

    /// <summary>Whether any <c>return</c> sits inside a LOOP — the one shape that needs the done flag.</summary>
    private static bool ContainsLoopReturn(IReadOnlyList<QStmt> body) =>
        body.Any(s => s switch
        {
            QFor f => ContainsReturn(f) || ContainsLoopReturn(f.Body),
            QWhile w => ContainsReturn(w) || ContainsLoopReturn(w.Body),
            QRepeat r => ContainsReturn(r) || ContainsLoopReturn(r.Body),
            QIf i => ContainsLoopReturn(i.Then) || ContainsLoopReturn(i.Else),
            QConjugate c => ContainsLoopReturn(c.Within) || ContainsLoopReturn(c.Apply),
            _ => false,
        });

    private static bool Contains(IReadOnlyList<QStmt> body, Func<QReturn, bool> pred) =>
        body.Any(s => s switch
        {
            QReturn r => pred(r),
            QIf i => Contains(i.Then, pred) || Contains(i.Else, pred),
            QFor f => Contains(f.Body, pred),
            QWhile w => Contains(w.Body, pred),
            QRepeat r => Contains(r.Body, pred),
            QConjugate c => Contains(c.Within, pred) || Contains(c.Apply, pred),
            _ => false,
        });

    private static QExpr Zero(QType type) =>
        new QText(type is QType.Float or QType.Angle ? new QLit("0.0") : new QNumLit(0));

    private static QExpr One() => new QText(new QNumLit(1));
}
