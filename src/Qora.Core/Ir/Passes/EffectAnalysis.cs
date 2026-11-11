namespace Qora.Ir.Passes;

/// <summary>
/// Computes the formal qubit parameters each callable may modify, including modifications made
/// transitively through user-callable invocations. HIR-to-MIR lowering uses this summary to create new
/// qubit versions for the corresponding actual operands. Detailed effects and cleanup facts are MIR-owned.
/// </summary>
internal static class EffectAnalysis
{
    public static void Run(
        HirProgram program,
        HirSemanticModel model) =>
        new Analyzer(program, model).RunAll();

    private sealed class Analyzer
    {
        private readonly HirSemanticModel _model;
        private readonly IReadOnlyList<HirCallable> _callables;
        private readonly IReadOnlyDictionary<HirNodeId, HirCallable> _callableById;
        private readonly Dictionary<HirNodeId, OpEffectSummary> _summaries = new();
        private readonly HashSet<HirNodeId> _inProgress = new();

        public Analyzer(
            HirProgram program,
            HirSemanticModel model)
        {
            _model = model;
            _callables = program.Callables;
            _callableById =
                program.Callables.ToDictionary(callable => callable.Id);
        }

        public void RunAll()
        {
            foreach (var callable in _callables)
                Summarize(callable.Id);
        }

        private OpEffectSummary Summarize(HirNodeId callableId)
        {
            if (_summaries.TryGetValue(callableId, out var cached))
                return cached;

            if (!_callableById.TryGetValue(callableId, out var callable))
            {
                throw new InvalidOperationException(
                    $"effect analysis: dangling callable identity {callableId}");
            }

            if (!_inProgress.Add(callableId))
            {
                throw new InvalidOperationException(
                    $"effect analysis: callable cycle reached `{callable.Name}` after validation");
            }

            try
            {
                var parameterNames =
                    callable.Parameters
                        .Where(parameter => parameter.Type == QType.Qubit)
                        .Select(parameter => parameter.Name)
                        .ToHashSet(StringComparer.Ordinal);
                var summary =
                    new OpEffectSummary(
                        AnalyzeBlock(callable.Body)
                            .Where(reference => parameterNames.Contains(reference.Reg))
                            .ToHashSet());

                _summaries.Add(callableId, summary);
                _model.AddOpEffects(callableId, summary);
                return summary;
            }
            finally
            {
                _inProgress.Remove(callableId);
            }
        }

        private HashSet<QubitRef> AnalyzeBlock(
            IReadOnlyList<HirStatement> body)
        {
            var modified = new HashSet<QubitRef>();
            foreach (var statement in body)
                modified.UnionWith(AnalyzeStatement(statement));
            return modified;
        }

        private HashSet<QubitRef> AnalyzeStatement(
            HirStatement statement)
        {
            var modified = new HashSet<QubitRef>();
            foreach (var expression in HirExpressions.DirectExpressionSites(statement))
                AddExpressionEffects(expression, modified);

            switch (statement)
            {
                case HirCallStatement call when call.Call.CalleeId is null:
                    modified.UnionWith(AnalyzeIntrinsicCall(call));
                    break;

                case HirIfStatement @if:
                    modified.UnionWith(AnalyzeBlock(@if.Then));
                    modified.UnionWith(AnalyzeBlock(@if.Else));
                    break;

                case HirForStatement @for:
                    modified.UnionWith(AnalyzeBlock(@for.Body));
                    break;

                case HirWhileStatement @while:
                    modified.UnionWith(AnalyzeBlock(@while.Body));
                    break;

                case HirRepeatStatement repeat:
                    modified.UnionWith(AnalyzeBlock(repeat.Body));
                    break;
            }

            return modified;
        }

        private static HashSet<QubitRef> AnalyzeIntrinsicCall(
            HirCallStatement statement)
        {
            var call = statement.Call;
            if (!QoraGates.Gates.TryGetValue(call.Name, out var gate))
            {
                throw new InvalidOperationException(
                    $"effect analysis: unresolved intrinsic call `{call.Name}` is not a built-in gate");
            }

            var qubitArguments =
                gate.AngleFirst
                    ? call.Arguments.Skip(1).ToArray()
                    : call.Arguments.ToArray();
            if (!gate.Unitary)
                return qubitArguments.Select(RefOf).ToHashSet();
            if (gate.Diagonal)
                return new HashSet<QubitRef>();

            var controls =
                gate.Controls
                + statement.Modifiers.Count(
                    modifier => modifier == QGateModifier.Controlled);
            return qubitArguments
                .Skip(controls)
                .Select(RefOf)
                .ToHashSet();
        }

        private static HashSet<QubitRef> Project(
            IReadOnlySet<QubitRef> references,
            HirCallable callee,
            IReadOnlyList<HirArgument> arguments)
        {
            if (arguments.Count != callee.Parameters.Count)
            {
                throw new InvalidOperationException(
                    $"effect analysis: call to `{callee.Name}` has {arguments.Count} "
                    + $"argument(s) for {callee.Parameters.Count} parameter(s)");
            }

            var result = new HashSet<QubitRef>();
            for (var index = 0; index < callee.Parameters.Count; index++)
            {
                var parameter = callee.Parameters[index];
                if (parameter.Type != QType.Qubit)
                    continue;

                switch (arguments[index].Expression)
                {
                    case HirNameExpression whole:
                        foreach (var reference in references)
                        {
                            if (reference.Reg == parameter.Name)
                                result.Add(reference with { Reg = whole.Name });
                        }
                        break;

                    case HirIndexExpression element:
                    {
                        var target = RefOf(element);
                        foreach (var reference in references)
                        {
                            if (reference.Reg != parameter.Name)
                                continue;
                            if (reference.Index is not null)
                            {
                                throw new InvalidOperationException(
                                    "effect analysis: a single-qubit actual received an "
                                    + $"indexed effect `{reference}` from `{parameter.Name}`");
                            }

                            result.Add(target);
                        }
                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            "effect analysis: a validated qubit argument is neither a "
                            + "register name nor an indexed register element");
                }
            }

            return result;
        }

        private void AddExpressionEffects(
            HirExpression expression,
            HashSet<QubitRef> modified)
        {
            foreach (var node in HirExpressions.DescendantsAndSelf(expression))
            {
                if (node is HirMeasurementExpression measurement)
                    modified.Add(RefOf(measurement.Target));
                else if (node is HirCallExpression { CalleeId: { } calleeId } call)
                {
                    if (!_callableById.TryGetValue(calleeId, out var callee))
                        throw new InvalidOperationException(
                            $"effect analysis: call `{call.Name}` binds missing callable {calleeId}");
                    modified.UnionWith(Project(
                        Summarize(calleeId).ParamModified,
                        callee,
                        call.Arguments));
                }
            }
        }

        private static QubitRef RefOf(HirArgument argument) =>
            RefOf(argument.Expression);

        private static QubitRef RefOf(HirExpression expression) =>
            expression switch
            {
                HirNameExpression name =>
                    new QubitRef(name.Name, null),

                HirIndexExpression
                {
                    Receiver: HirNameExpression register,
                    Index: HirIntegerLiteralExpression
                    {
                        Value: >= int.MinValue and <= int.MaxValue,
                    } index,
                } =>
                    new QubitRef(register.Name, (int)index.Value),

                HirIndexExpression
                {
                    Receiver: HirNameExpression register,
                } =>
                    new QubitRef(register.Name, null),

                _ =>
                    throw new InvalidOperationException(
                        $"effect analysis: unsupported qubit operand "
                        + $"`{HirExpressions.Render(expression)}`"),
            };
    }
}
