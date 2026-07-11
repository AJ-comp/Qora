using System.Text;

namespace Qora.Ir.Passes;

/// <summary>
/// The auto-uncompute DRY-RUN view (the <c>--stages</c> <c>uncompute</c> field): what rung ④ WOULD do, with
/// nothing injected. For every operation, each qubit name is classified with ONE TERM PER CONCEPT — input
/// (a parameter, caller-owned, not an ancilla), ancilla promoted to OUTPUT (measured — its value was
/// delivered, so it left the cleanup pool), or ancilla that is a CLEANUP CANDIDATE — and each candidate
/// carries its rung-③ verdict: safe to uncompute, or which clause blocked it and at which event. Pure
/// CONSUMPTION of the persistent <see cref="SemanticModel"/> (the first consumer of
/// <see cref="SemanticModel.IsCleanupCandidate"/> / <see cref="SemanticModel.UncomputeSafety"/> /
/// <see cref="SemanticModel.LiveRange"/>); the rung-④ injector will act on exactly these answers, so this
/// view IS the injection plan, readable before it exists.
/// </summary>
public static class UncomputeReport
{
    /// <summary>Render the per-operation uncompute verdicts as text (same shape as
    /// <see cref="SymbolTableBuilder.Format"/>: an op header line, its qubits indented beneath). Empty when
    /// there is nothing to say — no program, no model (semantic errors blocked analysis), or no qubits.</summary>
    public static string Format(QProgram? program, SemanticModel? model)
    {
        if (program is null || model is null || program.Operations.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var op in program.Operations)
        {
            // No scope means the model never validated this op (it is absent from the post-monomorphize
            // model this view reads — pass QoraParseResult.AnalyzedIr, whose ops all have scopes AND streams).
            var root = model.FindRootScope(op.Id);
            if (root is null) continue;
            var qubits = root.AllSymbols()
                .Where(s => s.Kind is SymbolKind.Parameter or SymbolKind.Register && s.Type == QType.Qubit)
                .ToList();
            if (qubits.Count == 0) continue;
            sb.AppendLine($"{op.DisplayName ?? op.Name}:");
            // A resolvable scope does NOT imply an analyzed op (a scope resolves through derivation lineage;
            // event streams deliberately do not) — say so instead of printing vacuously-safe verdicts.
            if (!model.WasEffectAnalyzed(op.Id))
            {
                sb.AppendLine("  (not analyzed — effect analysis did not run on this operation)");
                continue;
            }
            foreach (var sym in qubits)
                sb.AppendLine($"  {sym.SourceName}: {Describe(model, op, sym)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One qubit's classification — the code IS the terminology ladder, one rung per line:
    /// ancilla (birth: a <c>use</c> workspace)? → cleanup candidate (liveness: value never delivered)?
    /// → safe (rung ③ verdict). Each rung's failure names what the qubit IS, not just what it is not.</summary>
    private static string Describe(SemanticModel model, QOperation op, Symbol sym)
    {
        var q = new QubitRef(sym.SourceName, null);

        if (!model.IsAncilla(op.Id, q))                       // rung 0 — birth: whose qubit is this?
            return "input (parameter) — caller-owned, not an ancilla";

        if (!model.IsCleanupCandidate(op.Id, q))              // rung 1 — liveness: was the value delivered?
            return $"ancilla, measured — promoted to output, not a cleanup candidate{PerElement(model, op, sym)}";

        var life = model.LiveRange(op.Id, q) is { } r ? $" (live {r.Birth.Order}..{r.Death.Order})" : " (never used)";
        var v = model.UncomputeSafety(op, q);                 // rung 2 — safety: can the adjoint clean it?
        return v.IsSafe
            ? $"ancilla, cleanup candidate — safe to uncompute{life}"
            : $"ancilla, cleanup candidate — NOT uncomputable: {Reason(v, op, q)}{life}{PerElement(model, op, sym)}";
    }

    /// <summary>The whole-register verdict is the conservative headline, but one measured/blocked ELEMENT
    /// blankets the rest — so break the register down per element when an element's truth DIFFERS from the
    /// headline: a clean element hidden by a measured/blocked sibling is named "safe candidate", and an element
    /// blocked for a DIFFERENT reason than the whole register carries its own reason (this is the only path on
    /// which an element-level culprit — e.g. a broadcast's statement-wide write under an element query — is
    /// actually rendered). Elements blocked by the SAME reason as the register headline are omitted as
    /// redundant.</summary>
    private static string PerElement(SemanticModel model, QOperation op, Symbol sym)
    {
        if (sym.RegisterSize is not int n || n < 2) return string.Empty;
        var whole = model.UncomputeSafety(op, new QubitRef(sym.SourceName, null));
        var parts = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var e = new QubitRef(sym.SourceName, i);
            if (!model.IsCleanupCandidate(op.Id, e)) continue;       // a measured element left the pool
            var v = model.UncomputeSafety(op, e);
            // "same reason" = same blocker AND same culprit STATEMENT — two CoWrittenPartner verdicts with
            // different culprits are different stories (e.g. headline blocked by a SWAP that never touches
            // this element, the element itself blocked by a later broadcast), and omitting the element's
            // line would misattribute its blockage to a statement that never touched it
            if (v.IsSafe) parts.Add($"{sym.SourceName}[{i}] safe cleanup candidate");
            else if (v.Blocker != whole.Blocker || v.Culprit?.StmtId != whole.Culprit?.StmtId)
                parts.Add($"{sym.SourceName}[{i}] blocked: {Reason(v, op, e)}");
            // else: same blocker, same culprit statement as the register headline — redundant, omit
        }
        return parts.Count > 0 ? $" — per element: {string.Join("; ", parts)}" : string.Empty;
    }

    private static string Reason(UncomputeVerdict v, QOperation op, QubitRef q) => v.Blocker switch
    {
        // the two rung-1 rulings — the report normally short-circuits before reaching them (Describe's ladder),
        // but a verdict shown directly must still say what the qubit IS
        UncomputeBlocker.NotACleanupCandidate => "not a cleanup candidate (a caller-owned input, or a name with no recorded qubit history)",
        UncomputeBlocker.Measured => $"measured — promoted to output @ order {v.Culprit!.Order}",
        UncomputeBlocker.Irreversible => $"irreversible touch (reset, or a call that measures/resets) @ order {v.Culprit!.Order}",
        UncomputeBlocker.NonQfreeWrite => $"non-qfree write — superposition (H/Rx/Ry) or phase permutation (Y/CY) @ order {v.Culprit!.Order}",
        UncomputeBlocker.ContainedWrite => $"{ContainedReason(v.Culprit!, op)} @ order {v.Culprit!.Order}",
        // the blanket-self case: the culprit is q's OWN statement-wide write, not a distinct partner qubit
        UncomputeBlocker.CoWrittenPartner when v.Culprit!.Qubit.Index is null && v.Culprit.Qubit.Reg == q.Reg =>
            $"statement-wide write of `{q.Reg}` — its adjoint cannot be sliced to `{q}` and would rewrite the sibling elements (a broadcast, or an opaque call; the `use` birth is exempt) @ order {v.Culprit.Order}",
        UncomputeBlocker.CoWrittenPartner => $"co-written partner `{v.Culprit!.Qubit}` (a value-move like SWAP, or a call writing both) @ order {v.Culprit.Order}",
        UncomputeBlocker.SourceDied => $"a source qubit is rewritten before the death point (@ order {v.Culprit!.Order})",
        UncomputeBlocker.NotAnalyzed => "not analyzed — effect analysis did not run",
        _ => "?",
    };

    /// <summary>The contained-write reason, worded by the culprit's INNERMOST container: an if/loop write is
    /// conditional/repeated (a straight-line adjoint would not mirror it), while a write inside a
    /// within/apply conjugation is blocked for the opposite reason — the conjugation's own synthesized W†
    /// ALREADY uncomputes it, and injecting another adjoint would re-compute the restored value.</summary>
    private static string ContainedReason(QubitEvent culprit, QOperation op)
    {
        var map = ContainerMap.Build(op);
        var innermost = map.TryGetValue(culprit.StmtId, out var chain) && chain.Count > 0 ? chain[^1] : null;
        return innermost is QConjugate
            ? "write inside a within/apply conjugation (its synthesized W† already uncomputes it — another adjoint would re-compute)"
            : "write inside an if/loop (conditional or repeated compute — straight-line adjoint would not mirror it)";
    }
}
