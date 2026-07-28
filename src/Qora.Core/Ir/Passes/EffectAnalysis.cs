namespace Qora.Ir.Passes;

/// <summary>
/// Computes each callable's qubit use/definition stream, effect summary, and qubit-version graph from
/// validated HIR. This pass is read-only with respect to HIR; all derived facts are stored in the
/// revision-bound semantic model under the callable's <see cref="HirNodeId"/>.
/// </summary>
internal static class EffectAnalysis
{
    public static void Run(
        HirProgram program,
        HirSemanticModel model) =>
        new Analyzer(program, model).RunAll();

    /// <summary>
    /// Test seam for feeding a deliberately corrupted event/graph pair through the same invariant verifier
    /// used by the pipeline.
    /// </summary>
    internal static void VerifySweep(
        string callableName,
        IReadOnlyCollection<string> qubitParameterRegisters,
        List<QubitEvent> events,
        QubitGraph graph) =>
        Analyzer.VerifyGraphCoherence(
            callableName,
            qubitParameterRegisters,
            events,
            graph);

    private sealed class Analyzer
    {
        private readonly HirSemanticModel _model;
        private readonly IReadOnlyList<HirCallable> _callables;
        private readonly Dictionary<HirNodeId, HirCallable> _callableById;
        private readonly Dictionary<HirNodeId, OpEffectSummary> _summaries = new();

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

            var qubitParameters =
                callable.Parameters
                    .Where(parameter => parameter.Type == QType.Qubit)
                    .ToArray();
            var log =
                new CallableEventLog(
                    qubitParameters.Select(parameter => parameter.Name));

            // Local qubit declarations are hoisted by the current language contract. Record their births
            // before the source-ordered walk, while retaining the declaration statement's identity.
            foreach (var statement in callable.Body)
            {
                if (statement is not HirQubitDeclarationStatement declaration)
                    continue;

                var born = new QubitRef(declaration.Name, null);
                log.Record(
                    declaration.Id,
                    new HashSet<QubitRef> { born },
                    new HashSet<QubitRef> { born },
                    new HashSet<QubitRef>(),
                    new HashSet<QubitRef>(),
                    irreversible: false,
                    birth: true);
            }

            var (
                touched,
                modified,
                nonQfreeWrites,
                measured,
                irreversible) =
                AnalyzeBlock(callable.Body, log);

            var parameterNames =
                qubitParameters
                    .Select(parameter => parameter.Name)
                    .ToHashSet();
            var summary =
                new OpEffectSummary(
                    touched
                        .Where(reference => parameterNames.Contains(reference.Reg))
                        .ToHashSet(),
                    modified
                        .Where(reference => parameterNames.Contains(reference.Reg))
                        .ToHashSet(),
                    nonQfreeWrites
                        .Where(reference => parameterNames.Contains(reference.Reg))
                        .ToHashSet(),
                    measured
                        .Where(reference => parameterNames.Contains(reference.Reg))
                        .ToHashSet(),
                    irreversible);

            _summaries[callableId] = summary;

            VerifyGraphCoherence(
                callable.Name,
                parameterNames,
                log.Events,
                log.Graph);

            _model.AddOpEffects(callable.Id, summary);
            _model.AddQubitEvents(callable.Id, log.Events);
            _model.AddQubitGraph(callable.Id, log.Graph);
            return summary;
        }

        private (
            HashSet<QubitRef> Touched,
            HashSet<QubitRef> Modified,
            HashSet<QubitRef> NonQfreeWrites,
            HashSet<QubitRef> Measured,
            bool Irreversible) AnalyzeBlock(
                IReadOnlyList<HirStatement> body,
                CallableEventLog log)
        {
            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            var nonQfreeWrites = new HashSet<QubitRef>();
            var measured = new HashSet<QubitRef>();
            var irreversible = false;

            foreach (var statement in body)
            {
                var (
                    statementTouched,
                    statementModified,
                    statementNonQfreeWrites,
                    statementMeasured,
                    statementIrreversible) =
                    AnalyzeStatement(statement, log);

                touched.UnionWith(statementTouched);
                modified.UnionWith(statementModified);
                nonQfreeWrites.UnionWith(statementNonQfreeWrites);
                measured.UnionWith(statementMeasured);
                irreversible |= statementIrreversible;
            }

            return (
                touched,
                modified,
                nonQfreeWrites,
                measured,
                irreversible);
        }

        private (
            HashSet<QubitRef> Touched,
            HashSet<QubitRef> Modified,
            HashSet<QubitRef> NonQfreeWrites,
            HashSet<QubitRef> Measured,
            bool Irreversible) AnalyzeStatement(
                HirStatement statement,
                CallableEventLog log)
        {
            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();
            var nonQfreeWrites = new HashSet<QubitRef>();
            var measured = new HashSet<QubitRef>();
            var irreversible = false;
            var isLeaf = false;

            switch (statement)
            {
                case HirQubitDeclarationStatement:
                    // Its hoisted birth was already recorded by Summarize.
                    break;

                case HirCallStatement call:
                    (
                        touched,
                        modified,
                        nonQfreeWrites,
                        measured,
                        irreversible) =
                        AnalyzeCall(call);
                    isLeaf = true;
                    break;

                case HirVariableDeclarationStatement declaration:
                    isLeaf =
                        ApplyMeasurements(
                            declaration.Value,
                            touched,
                            modified,
                            measured);
                    irreversible = isLeaf;
                    break;

                case HirAssignmentStatement assignment:
                    isLeaf =
                        ApplyMeasurements(
                            assignment.Value,
                            touched,
                            modified,
                            measured);
                    irreversible = isLeaf;
                    break;

                case HirIfStatement @if:
                {
                    // The guard runs before either branch. Record every measurement occurrence separately
                    // so two M(...) nodes in one condition remain two ordered qubit versions instead of one
                    // co-written event group.
                    var conditionIrreversible =
                        ApplyConditionMeasurements(
                            @if.Condition,
                            @if.Id,
                            log,
                            touched,
                            modified,
                            measured);
                    var (
                        thenTouched,
                        thenModified,
                        thenNonQfreeWrites,
                        thenMeasured,
                        thenIrreversible) =
                        AnalyzeBlock(@if.Then, log);
                    var (
                        elseTouched,
                        elseModified,
                        elseNonQfreeWrites,
                        elseMeasured,
                        elseIrreversible) =
                        AnalyzeBlock(@if.Else, log);

                    touched.UnionWith(thenTouched);
                    touched.UnionWith(elseTouched);
                    modified.UnionWith(thenModified);
                    modified.UnionWith(elseModified);
                    nonQfreeWrites.UnionWith(thenNonQfreeWrites);
                    nonQfreeWrites.UnionWith(elseNonQfreeWrites);
                    measured.UnionWith(thenMeasured);
                    measured.UnionWith(elseMeasured);
                    irreversible =
                        conditionIrreversible
                        || thenIrreversible
                        || elseIrreversible;
                    break;
                }

                case HirForStatement @for:
                    (
                        touched,
                        modified,
                        nonQfreeWrites,
                        measured,
                        irreversible) =
                        AnalyzeBlock(@for.Body, log);
                    break;

                case HirWhileStatement @while:
                {
                    // A while guard is evaluated before its body. The event stream is a conservative
                    // one-pass timeline, so it records that source order once while the summary still
                    // denotes effects that may happen on any iteration.
                    var conditionIrreversible =
                        ApplyConditionMeasurements(
                            @while.Condition,
                            @while.Id,
                            log,
                            touched,
                            modified,
                            measured);
                    var (
                        bodyTouched,
                        bodyModified,
                        bodyNonQfreeWrites,
                        bodyMeasured,
                        bodyIrreversible) =
                        AnalyzeBlock(@while.Body, log);

                    touched.UnionWith(bodyTouched);
                    modified.UnionWith(bodyModified);
                    nonQfreeWrites.UnionWith(bodyNonQfreeWrites);
                    measured.UnionWith(bodyMeasured);
                    irreversible =
                        conditionIrreversible
                        || bodyIrreversible;
                    break;
                }

                case HirRepeatStatement repeat:
                {
                    // A repeat body executes before its until guard, so analyze and record the body first.
                    (
                        touched,
                        modified,
                        nonQfreeWrites,
                        measured,
                        irreversible) =
                        AnalyzeBlock(repeat.Body, log);
                    irreversible |=
                        ApplyConditionMeasurements(
                            repeat.Until,
                            repeat.Id,
                            log,
                            touched,
                            modified,
                            measured);
                    break;
                }
            }

            if (isLeaf)
            {
                log.Record(
                    statement.Id,
                    touched,
                    modified,
                    nonQfreeWrites,
                    measured,
                    irreversible);
            }

            return (
                touched,
                modified,
                nonQfreeWrites,
                measured,
                irreversible);
        }

        /// <summary>
        /// Adds condition-measurement effects to a compound statement's aggregate and records each source
        /// measurement as its own event transaction. Keeping occurrences separate preserves their source
        /// evaluation order and prevents independent measurements in one condition from being represented
        /// as one artificial co-write in the qubit graph.
        /// </summary>
        private static bool ApplyConditionMeasurements(
            HirExpression condition,
            HirNodeId ownerStatementId,
            CallableEventLog log,
            HashSet<QubitRef> aggregateTouched,
            HashSet<QubitRef> aggregateModified,
            HashSet<QubitRef> aggregateMeasured)
        {
            var found = false;

            foreach (var measurement in
                     HirExpressions
                         .DescendantsAndSelf(condition)
                         .OfType<HirMeasurementExpression>())
            {
                var occurrenceTouched = new HashSet<QubitRef>();
                var occurrenceModified = new HashSet<QubitRef>();
                var occurrenceMeasured = new HashSet<QubitRef>();
                _ = ApplyMeasurements(
                    measurement,
                    occurrenceTouched,
                    occurrenceModified,
                    occurrenceMeasured);

                aggregateTouched.UnionWith(occurrenceTouched);
                aggregateModified.UnionWith(occurrenceModified);
                aggregateMeasured.UnionWith(occurrenceMeasured);
                log.Record(
                    ownerStatementId,
                    occurrenceTouched,
                    occurrenceModified,
                    new HashSet<QubitRef>(),
                    occurrenceMeasured,
                    irreversible: true);
                found = true;
            }

            return found;
        }

        private (
            HashSet<QubitRef> Touched,
            HashSet<QubitRef> Modified,
            HashSet<QubitRef> NonQfreeWrites,
            HashSet<QubitRef> Measured,
            bool Irreversible) AnalyzeCall(
                HirCallStatement statement)
        {
            var call = statement.Call;

            if (call.CalleeId is HirNodeId calleeId)
            {
                if (!_callableById.TryGetValue(calleeId, out var callee))
                {
                    throw new InvalidOperationException(
                        $"effect analysis: call `{call.Name}` binds CalleeId {calleeId}, "
                        + "but no such callable exists");
                }

                if (statement.Modifiers.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"effect analysis: user-call `{call.Name}` carries unsupported "
                        + $"source modifiers [{string.Join(", ", statement.Modifiers)}]");
                }

                var summary = Summarize(callee.Id);
                return (
                    Project(
                        summary.ParamTouched,
                        callee,
                        call.Arguments),
                    Project(
                        summary.ParamModified,
                        callee,
                        call.Arguments),
                    Project(
                        summary.ParamModifiedNonQfree,
                        callee,
                        call.Arguments),
                    Project(
                        summary.ParamMeasured,
                        callee,
                        call.Arguments),
                    summary.Irreversible);
            }

            if (!QoraGates.Gates.TryGetValue(call.Name, out var gate))
            {
                throw new InvalidOperationException(
                    $"effect analysis: `{call.Name}` is neither a resolved user callable "
                    + "nor a built-in gate");
            }

            var qubitArguments =
                gate.AngleFirst
                    ? call.Arguments.Skip(1).ToArray()
                    : call.Arguments.ToArray();
            var touched = new HashSet<QubitRef>();
            var modified = new HashSet<QubitRef>();

            if (!gate.Unitary)
            {
                foreach (var argument in qubitArguments)
                {
                    var reference = RefOf(argument);
                    touched.Add(reference);
                    modified.Add(reference);
                }

                return (
                    touched,
                    modified,
                    new HashSet<QubitRef>(),
                    new HashSet<QubitRef>(),
                    Irreversible: true);
            }

            var controls =
                gate.Controls
                + statement.Modifiers.Count(
                    modifier => modifier == QGateModifier.Controlled);

            for (var index = 0; index < qubitArguments.Length; index++)
            {
                var reference = RefOf(qubitArguments[index]);
                touched.Add(reference);
                if (index >= controls && !gate.Diagonal)
                    modified.Add(reference);
            }

            var nonQfreeWrites =
                gate.NonQfree
                    ? new HashSet<QubitRef>(modified)
                    : new HashSet<QubitRef>();

            return (
                touched,
                modified,
                nonQfreeWrites,
                new HashSet<QubitRef>(),
                Irreversible: false);
        }

        /// <summary>
        /// Rewrites a callee summary's formal qubit references into the caller's actual HIR operands.
        /// </summary>
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

        private static bool ApplyMeasurements(
            HirExpression expression,
            HashSet<QubitRef> touched,
            HashSet<QubitRef> modified,
            HashSet<QubitRef> measured)
        {
            var found = false;

            foreach (var measurement in
                     HirExpressions
                         .DescendantsAndSelf(expression)
                         .OfType<HirMeasurementExpression>())
            {
                var reference = RefOf(measurement.Target);
                touched.Add(reference);
                modified.Add(reference);
                measured.Add(reference);
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Preserves a literal element index and conservatively blankets every dynamic index to its whole
        /// register. Validation guarantees that the receiver is a direct register name.
        /// </summary>
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
                        $"effect analysis: unsupported qubit operand `{HirExpressions.Render(expression)}`"),
            };

        /// <summary>
        /// One callable's growing event stream and qubit graph. Each write event creates its version node
        /// in the same transaction, while a read event links to the then-current version.
        /// </summary>
        private sealed class CallableEventLog
        {
            public readonly List<QubitEvent> Events = new();
            public readonly QubitGraph Graph = new();

            private int _order;

            public CallableEventLog(
                IEnumerable<string> qubitParameterRegisters)
            {
                foreach (var register in qubitParameterRegisters)
                    Graph.AddSeed(register);
            }

            public void Record(
                HirNodeId statementId,
                HashSet<QubitRef> touched,
                HashSet<QubitRef> modified,
                HashSet<QubitRef> nonQfreeWrites,
                HashSet<QubitRef> measured,
                bool irreversible,
                bool birth = false)
            {
                var references =
                    new List<(
                        QubitRef Reference,
                        QubitEventKind Kind,
                        bool Irreversible,
                        bool NonQfree)>();

                foreach (var reference in touched)
                {
                    var isMeasured = measured.Contains(reference);
                    var isModified = modified.Contains(reference);
                    var kind =
                        isMeasured
                            ? QubitEventKind.Measure
                            : isModified
                                ? QubitEventKind.Write
                                : QubitEventKind.Read;

                    references.Add(
                        (
                            reference,
                            kind,
                            irreversible && isModified && !isMeasured,
                            nonQfreeWrites.Contains(reference)));
                }

                Stamp(statementId, references, birth);
            }

            private void Stamp(
                HirNodeId statementId,
                List<(
                    QubitRef Reference,
                    QubitEventKind Kind,
                    bool Irreversible,
                    bool NonQfree)> references,
                bool birth)
            {
                var current =
                    new Dictionary<QubitRef, int?>();

                foreach (var (reference, _, _, _) in references)
                    current[reference] = CurrentNode(reference);

                var born =
                    new Dictionary<QubitRef, int>();

                foreach (var (reference, kind, _, _) in references)
                {
                    if (kind == QubitEventKind.Read)
                        continue;

                    if (!birth
                        && current[reference] is null
                        && Graph.ParamSeed(reference.Reg) is null)
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: effect analysis wrote `{reference}` before any birth");
                    }

                    var parents = new List<QubitEdge>();

                    void AddParent(
                        int? nodeId,
                        QubitRef via)
                    {
                        if (nodeId is int id
                            && !parents.Any(
                                parent =>
                                    parent.NodeId == id
                                    && parent.Via == via))
                        {
                            parents.Add(new QubitEdge(id, via));
                        }
                    }

                    AddParent(current[reference], reference);

                    foreach (var (other, _, _, _) in references)
                    {
                        if (other != reference)
                            AddParent(current[other], other);
                    }

                    born[reference] =
                        Graph.AddNode(reference, parents);
                }

                foreach (var (
                             reference,
                             kind,
                             eventIrreversible,
                             nonQfree) in references)
                {
                    var nodeId =
                        kind == QubitEventKind.Read
                            ? current[reference]
                              ?? throw new InvalidOperationException(
                                  $"QINTERNAL: effect analysis read `{reference}` before any birth")
                            : born[reference];

                    Events.Add(
                        new QubitEvent(
                            reference,
                            kind,
                            _order,
                            statementId,
                            eventIrreversible,
                            nonQfree,
                            nodeId));
                }

                _order++;
            }

            private int? CurrentNode(QubitRef reference)
            {
                for (var index = Events.Count - 1; index >= 0; index--)
                {
                    var @event = Events[index];
                    if (@event.Kind != QubitEventKind.Read
                        && @event.Qubit.Overlaps(reference))
                    {
                        return @event.NodeId;
                    }
                }

                return Graph.ParamSeed(reference.Reg);
            }
        }

        /// <summary>
        /// Independently re-derives the event/graph relationship and fails loudly on any divergence.
        /// </summary>
        public static void VerifyGraphCoherence(
            string callableName,
            IReadOnlyCollection<string> qubitParameterRegisters,
            List<QubitEvent> events,
            QubitGraph graph)
        {
            void Fail(string detail) =>
                throw new InvalidOperationException(
                    $"QINTERNAL: qubit graph incoherent with the event stream "
                    + $"in `{callableName}`: {detail}");

            // Pass 1: every event points to an existing node with a role-compatible qubit reference.
            var creatorOrder = new Dictionary<int, int>();

            foreach (var @event in events)
            {
                if (@event.NodeId < 0
                    || @event.NodeId >= graph.Nodes.Count)
                {
                    Fail(
                        $"event at order {@event.Order} points at missing "
                        + $"node {@event.NodeId}");
                }

                var node = graph.Node(@event.NodeId);

                if (@event.Kind == QubitEventKind.Read)
                {
                    if (!node.Qubit.Overlaps(@event.Qubit))
                    {
                        Fail(
                            $"read of {@event.Qubit} at order {@event.Order} "
                            + $"is linked to unrelated node {node.Qubit}");
                    }
                }
                else
                {
                    if (node.IsParamSeed)
                    {
                        Fail(
                            $"write at order {@event.Order} is linked to a "
                            + "parameter seed node");
                    }

                    if (node.Qubit != @event.Qubit)
                    {
                        Fail(
                            $"write of {@event.Qubit} at order {@event.Order} "
                            + $"is linked to a node of {node.Qubit}");
                    }

                    if (!creatorOrder.TryAdd(@event.NodeId, @event.Order))
                    {
                        Fail(
                            $"node {@event.NodeId} has two creating events");
                    }
                }
            }

            var seedRegisters =
                graph.Nodes
                    .Where(node => node.IsParamSeed)
                    .Select(node => node.Qubit.Reg)
                    .ToHashSet();

            if (seedRegisters.Count
                != graph.Nodes.Count(node => node.IsParamSeed))
            {
                Fail("duplicate parameter seed nodes");
            }

            if (!seedRegisters.SetEquals(qubitParameterRegisters))
            {
                Fail(
                    $"seed registers [{string.Join(", ", seedRegisters)}] "
                    + "do not match the callable's qubit parameters "
                    + $"[{string.Join(", ", qubitParameterRegisters)}]");
            }

            foreach (var register in seedRegisters)
            {
                if (graph.ParamSeed(register) is not int seedId
                    || !graph.Node(seedId).IsParamSeed)
                {
                    Fail(
                        $"ParamSeed(`{register}`) does not resolve to a seed node");
                }
            }

            // Pass 2: validate node creation, parent age, and per-register version sequences.
            var versionByRegister = new Dictionary<string, int>();

            foreach (var node in graph.Nodes)
            {
                if (node.IsParamSeed
                    && (node.Parents.Count != 0
                        || node.Qubit.Index is not null))
                {
                    Fail(
                        $"seed node {node.Id} is malformed "
                        + $"({node.Qubit}, {node.Parents.Count} parents)");
                }

                if (!node.IsParamSeed
                    && !creatorOrder.ContainsKey(node.Id))
                {
                    Fail(
                        $"node {node.Id} ({node.Qubit} v{node.Version}) "
                        + "has no creating event");
                }

                foreach (var parent in node.Parents)
                {
                    if (parent.NodeId < 0
                        || parent.NodeId >= graph.Nodes.Count)
                    {
                        Fail(
                            $"node {node.Id} has missing parent {parent.NodeId}");
                    }

                    var parentOrder =
                        graph.Node(parent.NodeId).IsParamSeed
                            ? -1
                            : creatorOrder[parent.NodeId];

                    if (parentOrder >= creatorOrder[node.Id])
                    {
                        Fail(
                            $"node {node.Id} has a parent ({parent.NodeId}) "
                            + "not older than itself");
                    }
                }

                versionByRegister.TryGetValue(
                    node.Qubit.Reg,
                    out var expectedVersion);

                if (node.Version != expectedVersion)
                {
                    Fail(
                        $"node {node.Id} ({node.Qubit}) has version "
                        + $"{node.Version}; the sequence says {expectedVersion}");
                }

                versionByRegister[node.Qubit.Reg] =
                    expectedVersion + 1;
            }

            // Pass 3: re-run current-version selection per statement and compare every read link and
            // write-parent set. Event indices disambiguate multiple writes at one statement order.
            var lastByReference =
                new Dictionary<
                    QubitRef,
                    (
                        int EventIndex,
                        int NodeId)>();

            int? Current(QubitRef reference)
            {
                var bestIndex = -1;
                int? bestNode = null;

                foreach (var (
                             candidate,
                             state) in lastByReference)
                {
                    if (candidate.Overlaps(reference)
                        && state.EventIndex > bestIndex)
                    {
                        bestIndex = state.EventIndex;
                        bestNode = state.NodeId;
                    }
                }

                return bestNode
                       ?? graph.ParamSeed(reference.Reg);
            }

            for (var groupStart = 0; groupStart < events.Count;)
            {
                if (groupStart > 0
                    && events[groupStart].Order
                    < events[groupStart - 1].Order)
                {
                    Fail(
                        $"event stream is not program ordered at "
                        + $"index {groupStart}");
                }

                var groupEnd = groupStart;
                while (groupEnd < events.Count
                       && events[groupEnd].Order
                       == events[groupStart].Order)
                {
                    groupEnd++;
                }

                for (var index = groupStart; index < groupEnd; index++)
                {
                    var @event = events[index];

                    if (@event.Kind == QubitEventKind.Read)
                    {
                        var expectedNode = Current(@event.Qubit);
                        if (expectedNode != @event.NodeId)
                        {
                            Fail(
                                $"read of {@event.Qubit} at order "
                                + $"{@event.Order} links to node "
                                + $"{@event.NodeId}; re-derivation says "
                                + $"{expectedNode?.ToString() ?? "none"}");
                        }

                        continue;
                    }

                    var expectedParents = new List<QubitEdge>();

                    void Expect(QubitRef via)
                    {
                        if (Current(via) is int nodeId
                            && !expectedParents.Any(
                                parent =>
                                    parent.NodeId == nodeId
                                    && parent.Via == via))
                        {
                            expectedParents.Add(
                                new QubitEdge(nodeId, via));
                        }
                    }

                    Expect(@event.Qubit);

                    for (var other = groupStart; other < groupEnd; other++)
                    {
                        if (events[other].Qubit != @event.Qubit)
                            Expect(events[other].Qubit);
                    }

                    var actualParents =
                        graph.Node(@event.NodeId).Parents;

                    if (actualParents.Count != expectedParents.Count
                        || expectedParents.Any(
                            parent => !actualParents.Contains(parent)))
                    {
                        Fail(
                            $"node {@event.NodeId} ({@event.Qubit} at order "
                            + $"{@event.Order}) has parents "
                            + $"[{FormatEdges(actualParents)}]; "
                            + $"re-derivation says [{FormatEdges(expectedParents)}]");
                    }
                }

                for (var index = groupStart; index < groupEnd; index++)
                {
                    if (events[index].Kind != QubitEventKind.Read)
                    {
                        lastByReference[events[index].Qubit] =
                            (index, events[index].NodeId);
                    }
                }

                groupStart = groupEnd;
            }

            static string FormatEdges(
                IEnumerable<QubitEdge> edges) =>
                string.Join(
                    ", ",
                    edges.Select(
                        edge =>
                            $"{edge.NodeId} via {edge.Via}"));
        }
    }
}
