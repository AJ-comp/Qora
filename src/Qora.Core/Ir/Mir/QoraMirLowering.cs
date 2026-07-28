using Qora.Compiler;
using Qora.Ir.Passes;

namespace Qora.Ir.Mir;

/// <summary>
/// Lowers the validated, source-shaped Qora HIR into typed SSA/CFG MIR.
///
/// Name lookup is deliberately confined to this boundary. The lowering environment resolves each HIR
/// spelling to the declaration's <see cref="SymbolId"/> while walking lexical scopes. Symbols remain in
/// <see cref="MirCrossStageLinks"/> rather than being copied into MIR entities; every emitted MIR
/// reference is then a dense MIR ID, never a string. Classical assignments create immutable SSA states,
/// block arguments represent Phi values, arrays retain both storage identity and state versions, and
/// qubits use a separate versioned identity space.
/// </summary>
internal static class QoraMirLowering
{
    public static MirLoweringResult Lower(
        HirSemanticContext semantics,
        int revision = 0)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));

        var hir = semantics.Current.Program;
        var snapshotId = new MirSnapshotId(
            semantics.Current.Id.CompilationId,
            semantics.Current.Id.CompilationRevision,
            revision);
        var origins = new MirOriginTableBuilder(
            snapshotId,
            semantics.Current);
        var links = new MirCrossStageLinksBuilder(
            snapshotId,
            semantics.Current,
            semantics.SemanticBasis);
        var callableIds = hir.Callables
            .Select((callable, index) => (SourceId: callable.Id, MirId: new MirCallableId(index)))
            .ToDictionary(item => item.SourceId, item => item.MirId);
        var callablesById = hir.Callables.ToDictionary(callable => callable.Id);
        var boundsSites =
            new Dictionary<HirNodeId, MirIndexedAccessRef>();
        var callables = hir.Callables
            .Select(callable => new CallableLowerer(
                callable,
                callableIds[callable.Id],
                QualifiedName(hir, callable),
                snapshotId,
                semantics,
                callableIds,
                callablesById,
                origins,
                links,
                boundsSites).Lower())
            .ToList();

        var originTable = origins.Build();
        var entryCallable = hir.EntryCallable
            ?? throw new InvalidOperationException(
                "validated HIR must contain an operation before MIR lowering");
        return new MirLoweringResult(
            new MirProgram(
                snapshotId,
                originTable,
                callableIds[entryCallable.Id],
                callables),
            links.Build(originTable),
            MirSafetyFacts.FromHir(
                snapshotId,
                semantics.SourceModel,
                boundsSites));

        static string QualifiedName(
            HirProgram program,
            HirCallable callable)
        {
            var namespacePath = program.NamespaceOf(callable);
            return namespacePath.Length == 0
                ? callable.Name
                : $"{namespacePath}.{callable.Name}";
        }
    }

    private sealed class CallableLowerer
    {
        private readonly HirCallable _callable;
        private readonly MirCallableId _callableId;
        private readonly string _qualifiedName;
        private readonly MirSnapshotId _snapshotId;
        private readonly IHirSemanticContext _semantics;
        private readonly IReadOnlyDictionary<HirNodeId, MirCallableId> _callableIds;
        private readonly IReadOnlyDictionary<HirNodeId, HirCallable> _callablesById;
        private readonly MirOriginTableBuilder _origins;
        private readonly MirCrossStageLinksBuilder _links;
        private readonly Dictionary<HirNodeId, MirIndexedAccessRef> _boundsSites;

        private readonly List<BlockBuilder> _blocks = new();
        private readonly List<MirValue> _values = new();
        private readonly List<MirArrayStorage> _storages = new();
        private readonly List<IMirParameter> _parameters = new();
        private readonly Dictionary<MirQubitId, MirQubit> _qubitSeeds = new();
        private readonly Dictionary<MirQubitId, int> _nextQubitVersions = new();
        private readonly Dictionary<MirValueId, int> _valueIndexes = new();
        private readonly Dictionary<MirBlockId, BlockBuilder> _blockById = new();

        private int _nextInstruction;
        private int _nextStorage;
        private int _nextQubit;
        private BlockBuilder? _current;
        private FlowState _state = new();
        private readonly MirOriginRef _callableOrigin;

        public CallableLowerer(
            HirCallable callable,
            MirCallableId callableId,
            string qualifiedName,
            MirSnapshotId snapshotId,
            IHirSemanticContext semantics,
            IReadOnlyDictionary<HirNodeId, MirCallableId> callableIds,
            IReadOnlyDictionary<HirNodeId, HirCallable> callablesById,
            MirOriginTableBuilder origins,
            MirCrossStageLinksBuilder links,
            Dictionary<HirNodeId, MirIndexedAccessRef> boundsSites)
        {
            _callable = callable;
            _callableId = callableId;
            _qualifiedName = qualifiedName;
            _snapshotId = snapshotId;
            _semantics = semantics;
            _callableIds = callableIds;
            _callablesById = callablesById;
            _origins = origins;
            _links = links;
            _boundsSites = boundsSites;
            _callableOrigin = _origins.Hir(
                callable.Id,
                callable.Id);
        }

        public MirCallable Lower()
        {
            var callableSymbol = RequireSymbol(
                _callable.Id,
                $"callable `{_callable.Name}`");
            _links.LinkCallable(
                _callable.Id,
                callableSymbol.Id,
                _callableId);

            var entry = NewBlock(_callableOrigin);
            _current = entry;

            LowerParameters();
            HoistQubitAllocations(_callable.Body.Statements);
            LowerStatements(_callable.Body.Statements);

            if (_current is not null)
            {
                Terminate(_callable.IsFunction
                    ? new MirUnreachable(
                        _origins.Synthesized(
                            _callableOrigin,
                            "missing validated function return"))
                    : new MirReturn(
                        null,
                        _origins.Synthesized(
                            _callableOrigin,
                            "implicit operation return")));
            }

            var blocks = _blocks.Select(block => block.Build()).ToList();
            return new MirCallable(
                _callableId,
                _qualifiedName,
                _callable.IsFunction ? MirCallableKind.Function : MirCallableKind.Operation,
                _callable.ReturnType is { } returns ? MirType.Scalar(returns) : null,
                _parameters,
                entry.Id,
                blocks,
                _values,
                _storages,
                _callableOrigin);
        }

        private void LowerParameters()
        {
            for (var index = 0; index < _callable.Parameters.Count; index++)
            {
                var parameter = _callable.Parameters[index];
                var symbol = RequireSymbol(parameter.Id, $"parameter `{parameter.Name}`");
                var parameterOrigin = _origins.Hir(
                    _callable.Id,
                    parameter.Id);
                _state.Declare(parameter.Name, symbol.Id);

                if (parameter.Type == QType.Qubit)
                {
                    var qubitId = NextQubitId();
                    var isArray = parameter.IsArray;
                    var qubit = new MirQubitParameter(
                        qubitId,
                        parameter.Name,
                        isArray,
                        isArray ? parameter.RegisterSize : null,
                        parameter.Ownership,
                        parameterOrigin);
                    RegisterQubitSeed(symbol.Id, qubit);
                    _state.Qubits[symbol.Id] = qubit;
                    _parameters.Add(qubit);
                    continue;
                }

                var type = parameter.IsArray
                    ? MirType.Array(parameter.Type, parameter.RegisterSize)
                    : MirType.Scalar(parameter.Type);
                var value = AddValue(
                    type,
                    MirValueDefinition.ParameterAt(index),
                    symbol.Id,
                    parameterOrigin);
                MirStorageId? storageId = null;
                if (parameter.IsArray)
                {
                    storageId = NextStorage();
                    _storages.Add(new MirArrayStorage(
                        storageId.Value,
                        parameter.Name,
                        MirArrayStorageKind.Parameter,
                        parameter.Ownership == QOwnershipMode.Borrowed
                            && parameter.Access == QAccessMode.ReadOnly
                                ? MirStorageAliasMode.SharedParameter
                                : MirStorageAliasMode.ExclusiveParameter,
                        type,
                        index,
                        AllocationInstruction: null,
                        parameterOrigin));
                    _links.LinkStorage(
                        symbol.Id,
                        _callableId,
                        storageId.Value);
                    _state.Storages[symbol.Id] = storageId.Value;
                }
                _state.Values[symbol.Id] = value;
                _parameters.Add(new MirClassicalParameter(
                    parameter.Name,
                    parameterOrigin,
                    value,
                    type,
                    storageId,
                    parameter.Ownership,
                    parameter.Access));
            }
        }

        private void HoistQubitAllocations(IReadOnlyList<HirStatement> statements)
        {
            foreach (var use in statements.OfType<HirQubitDeclarationStatement>())
            {
                var symbol = RequireSymbol(use.Id, $"qubit allocation `{use.Name}`");
                _state.Declare(use.Name, symbol.Id);
                var instructionId = NextInstruction();
                var qubitId = NextQubitId();
                var source = SourceOf(use);
                var qubit = new MirQubitFromUse(
                    qubitId,
                    use.Name,
                    use.Size,
                    source);
                RegisterQubitSeed(symbol.Id, qubit);
                Emit(new MirQubitAllocate(instructionId, qubit, source));
                _state.Qubits[symbol.Id] = qubit;
            }
        }

        private void LowerStatements(IReadOnlyList<HirStatement> statements)
        {
            for (var index = 0; index < statements.Count; index++)
            {
                if (_current is null)
                {
                    for (; index < statements.Count; index++)
                        MarkUnreachableDeclarations(statements[index]);
                    return;
                }
                LowerStatement(statements[index]);
            }
        }

        private void MarkUnreachableDeclarations(HirStatement statement)
        {
            if (_semantics.FindSymbol(statement.Id) is { } symbol)
                _links.MarkUnreachable(symbol.Id);

            switch (statement)
            {
                case HirIfStatement conditional:
                    foreach (var nested in conditional.Then.Statements)
                        MarkUnreachableDeclarations(nested);
                    foreach (var nested in conditional.Else.Statements)
                        MarkUnreachableDeclarations(nested);
                    break;
                case HirForStatement loop:
                    foreach (var nested in loop.Body.Statements)
                        MarkUnreachableDeclarations(nested);
                    break;
                case HirWhileStatement loop:
                    foreach (var nested in loop.Body.Statements)
                        MarkUnreachableDeclarations(nested);
                    break;
                case HirRepeatStatement loop:
                    foreach (var nested in loop.Body.Statements)
                        MarkUnreachableDeclarations(nested);
                    break;
            }
        }

        private void LowerStatement(HirStatement statement)
        {
            switch (statement)
            {
                case HirQubitDeclarationStatement:
                    // Local qubits are allocated once in the callable entry block.
                    return;

                case HirVariableDeclarationStatement declaration:
                    LowerDeclaration(declaration);
                    return;

                case HirAssignmentStatement assignment:
                    LowerAssignment(assignment);
                    return;

                case HirCallStatement call:
                    LowerStatementCall(call);
                    return;

                case HirIfStatement conditional:
                    LowerIf(conditional);
                    return;

                case HirForStatement loop:
                    LowerFor(loop);
                    return;

                case HirWhileStatement loop:
                    LowerWhile(loop);
                    return;

                case HirRepeatStatement loop:
                    LowerRepeat(loop);
                    return;

                case HirReturnStatement returned:
                    LowerReturn(returned);
                    return;

                default:
                    throw Internal($"unsupported HIR statement {statement.GetType().Name}");
            }
        }

        private void LowerDeclaration(HirVariableDeclarationStatement declaration)
        {
            var symbol = RequireSymbol(declaration.Id, $"declaration `{declaration.Name}`");
            var source = SourceOf(declaration);

            if (declaration.IsArray)
            {
                var elementType = declaration.Type
                    ?? symbol.Type
                    ?? throw Internal($"array `{declaration.Name}` has no element type");
                var (length, initialization, elements) = declaration.Value switch
                {
                    HirArrayCreationExpression allocation =>
                        (allocation.Length, MirArrayInitialization.ZeroInitialized, new List<MirValueId>()),
                    HirArrayLiteralExpression literal =>
                        (literal.Elements.Count, MirArrayInitialization.ExplicitElements,
                            literal.Elements
                                .Select(element => LowerExpressionAs(
                                    element,
                                    MirType.Scalar(elementType)))
                                .ToList()),
                    _ => throw Internal($"validated array `{declaration.Name}` has a non-array initializer"),
                };

                var instructionId = NextInstruction();
                var storageId = NextStorage();
                var resultType = MirType.Array(elementType, length);
                var result = AddInstructionResult(instructionId, resultType, symbol.Id, source);
                _storages.Add(new MirArrayStorage(
                    storageId,
                    declaration.Name,
                    MirArrayStorageKind.Local,
                    MirStorageAliasMode.UniqueLocal,
                    resultType,
                    ParameterIndex: null,
                    instructionId,
                    source));
                _links.LinkStorage(
                    symbol.Id,
                    _callableId,
                    storageId);
                Emit(new MirArrayCreate(
                    instructionId,
                    result,
                    storageId,
                    elementType,
                    initialization,
                    length,
                    elements,
                    source));
                _state.Declare(declaration.Name, symbol.Id);
                _state.Values[symbol.Id] = result;
                _state.Storages[symbol.Id] = storageId;
                return;
            }

            var declaredType = symbol.Type
                ?? declaration.Type
                ?? throw Internal($"scalar `{declaration.Name}` has no inferred type");
            var value = LowerExpressionAs(
                declaration.Value,
                MirType.Scalar(declaredType));
            AttachSymbol(value, symbol.Id);
            _state.Declare(declaration.Name, symbol.Id);
            _state.Values[symbol.Id] = value;
        }

        private void LowerAssignment(HirAssignmentStatement assignment)
        {
            var source = SourceOf(assignment);

            if (assignment.Target is HirNameExpression name)
            {
                var symbolId = _state.Resolve(name.Name)
                    ?? throw Internal($"assignment target `{name.Name}` is not active");
                var expected = _state.Values.TryGetValue(symbolId, out var current)
                    ? TypeOf(current)
                    : throw Internal($"assignment target `{name.Name}` has no SSA state");
                var value = LowerExpressionAs(assignment.Value, expected);
                AttachSymbol(value, symbolId);
                _state.Values[symbolId] = value;
                return;
            }

            if (assignment.Target is not HirIndexExpression
                {
                    Receiver: HirNameExpression receiver,
                } indexedTarget)
            {
                throw Internal(
                    $"unsupported assignment target `{HirExpressions.Render(assignment.Target)}`");
            }

            var indexedSymbolId = _state.Resolve(receiver.Name)
                ?? throw Internal($"assignment target `{receiver.Name}` is not active");
            if (!_state.Values.TryGetValue(indexedSymbolId, out var array))
                throw Internal($"indexed assignment target `{receiver.Name}` has no array state");
            var arrayType = TypeOf(array);
            if (!arrayType.IsArray)
                throw Internal($"indexed assignment target `{receiver.Name}` is not an array");
            var index = LowerExpressionAs(
                indexedTarget.Index,
                MirType.Scalar(QType.Int));
            var storedValue = LowerExpressionAs(
                assignment.Value,
                MirType.Scalar(arrayType.ElementType));
            var instructionId = NextInstruction();
            var result = AddInstructionResult(
                instructionId,
                arrayType,
                indexedSymbolId,
                source);
            Emit(new MirArrayStore(instructionId, result, array, index, storedValue, source));
            RegisterBoundsSite(
                indexedTarget,
                instructionId,
                MirIndexedAccessKind.ArrayStore);
            _state.Values[indexedSymbolId] = result;
        }

        private void LowerStatementCall(HirCallStatement statement)
        {
            var source = SourceOf(statement);
            var call = statement.Call;
            var callee = ResolveUserCallable(call.CalleeId);
            if (callee is { IsFunction: true })
            {
                LowerPureCall(
                    new MirUserCallableTarget(_callableIds[callee.Id]),
                    callee,
                    call);
                return;
            }

            if (callee is not null)
            {
                LowerOperationCall(statement, callee, source);
                return;
            }

            var signature = QoraGates.SigOf(
                call.Name,
                statement.Modifiers.Count(modifier => modifier == QGateModifier.Controlled))
                ?? throw Internal($"validated built-in gate `{call.Name}` has no signature");
            var operands = new List<MirCallOperand>(call.Arguments.Count);
            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var argument = call.Arguments[index];
                var expected = signature.Parameters[index];
                if (expected.Type == QType.Qubit)
                {
                    operands.Add(new MirQubitCallOperand(
                        LowerQubitAccess(argument),
                        argument.Ownership,
                        argument.Access));
                }
                else
                {
                    var value = LowerExpressionAs(
                        argument.Expression,
                        MirType.Scalar(expected.Type));
                    operands.Add(new MirClassicalCallOperand(
                        value,
                        argument.Ownership,
                        argument.Access));
                }
            }

            var instructionId = NextInstruction();
            EmitQuantumApply(
                instructionId,
                new MirBuiltinGateTarget(call.Name),
                operands,
                Array.Empty<MirMutableArrayResult>(),
                LowerModifiers(statement.Modifiers),
                WrittenBuiltinQubitOperands(statement),
                source);
            RegisterQubitArgumentBoundsSites(
                call.Arguments,
                signature.Parameters,
                instructionId);
        }

        private void LowerOperationCall(
            HirCallStatement statement,
            HirCallable callee,
            MirOriginRef source)
        {
            var call = statement.Call;
            var instructionId = NextInstruction();
            var operands = new List<MirCallOperand>(call.Arguments.Count);
            var mutableResults = new List<MirMutableArrayResult>();
            var mutableBindings = new List<(SymbolId Symbol, MirValueId Result)>();

            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var argument = call.Arguments[index];
                var parameter = callee.Parameters[index];
                if (parameter.Type == QType.Qubit)
                {
                    operands.Add(new MirQubitCallOperand(
                        LowerQubitAccess(argument),
                        argument.Ownership,
                        argument.Access));
                    continue;
                }

                var value = LowerExpression(argument.Expression);
                var expected = parameter.IsArray
                    ? MirType.Array(parameter.Type, parameter.RegisterSize)
                    : MirType.Scalar(parameter.Type);
                value = EnsureCallType(
                    value,
                    expected,
                    SourceOf(argument.Expression));
                operands.Add(new MirClassicalCallOperand(value, argument.Ownership, argument.Access));

                if (parameter.IsArray
                    && parameter.Ownership == QOwnershipMode.Borrowed
                    && parameter.Access == QAccessMode.Mutable)
                {
                    var actualType = TypeOf(value);
                    var result = AddInstructionResult(
                        instructionId,
                        actualType,
                        SymbolOfWholeArgument(argument),
                        source,
                        mutableResults.Count);
                    mutableResults.Add(new MirMutableArrayResult(index, result));
                    var symbolId = SymbolOfWholeArgument(argument)
                        ?? throw Internal($"mutable array argument {index} of `{callee.Name}` is not a binding");
                    mutableBindings.Add((symbolId, result));
                }
            }

            EmitQuantumApply(
                instructionId,
                new MirUserCallableTarget(_callableIds[callee.Id]),
                operands,
                mutableResults,
                LowerModifiers(statement.Modifiers),
                WrittenUserQubitOperands(callee),
                source);
            RegisterQubitArgumentBoundsSites(
                call.Arguments,
                callee.Parameters,
                instructionId);
            foreach (var (symbol, result) in mutableBindings)
                _state.Values[symbol] = result;
        }

        private MirValueId LowerPureCall(
            MirCallTarget target,
            HirCallable? callee,
            HirCallExpression call)
        {
            var source = SourceOf(call);
            IReadOnlyList<HirParameter>? parameters = callee?.Parameters;
            var operands = new List<MirCallOperand>(call.Arguments.Count);
            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var argument = call.Arguments[index].Expression;
                var value = LowerExpression(argument);
                if (parameters is not null)
                {
                    var parameter = parameters[index];
                    var expected = parameter.IsArray
                        ? MirType.Array(parameter.Type, parameter.RegisterSize)
                        : MirType.Scalar(parameter.Type);
                    value = EnsureCallType(
                        value,
                        expected,
                        SourceOf(argument));
                }
                operands.Add(new MirClassicalCallOperand(value));
            }

            var resultType = target switch
            {
                MirUserCallableTarget when callee?.ReturnType is { } returns =>
                    MirType.Scalar(returns),
                MirBuiltinFunctionTarget builtin
                    when QoraGates.Functions.TryGetValue(builtin.Name, out var function) =>
                    MirType.Scalar(function.Returns),
                _ => throw Internal($"pure target `{target.DisplayName}` has no return type"),
            };
            var instructionId = NextInstruction();
            var result = AddInstructionResult(instructionId, resultType, null, source);
            Emit(new MirPureCall(instructionId, result, target, operands, source));
            return result;
        }

        private void LowerIf(HirIfStatement conditional)
        {
            var source = SourceOf(conditional);
            var condition = LowerExpressionAs(
                conditional.Condition,
                MirType.Scalar(QType.Bit));
            var branchBlock = RequireCurrent();
            var before = _state.Clone();
            var thenBlock = NewBlock(source);
            var elseBlock = NewBlock(source);
            branchBlock.Terminator = new MirBranch(
                condition,
                thenBlock.Id,
                Array.Empty<MirValueId>(),
                elseBlock.Id,
                Array.Empty<MirValueId>(),
                source);

            var thenExit = LowerScopedBranch(
                thenBlock,
                before,
                conditional.Then.Statements);
            var elseExit = LowerScopedBranch(
                elseBlock,
                before,
                conditional.Else.Statements);

            if (thenExit.Block is null && elseExit.Block is null)
            {
                _current = null;
                _state = before;
                return;
            }

            var merge = NewBlock(source);
            if (thenExit.Block is null || elseExit.Block is null)
            {
                var live = thenExit.Block is not null ? thenExit : elseExit;
                live.Block!.Terminator = new MirJump(
                    merge.Id,
                    Array.Empty<MirValueId>(),
                    source);
                _current = merge;
                _state = live.State;
                return;
            }

            var merged = before.Clone();
            var thenArguments = new List<MirValueId>();
            var elseArguments = new List<MirValueId>();
            foreach (var symbol in before.Values.Keys.OrderBy(id => id.Value))
            {
                if (!thenExit.State.Values.TryGetValue(symbol, out var thenValue)
                    || !elseExit.State.Values.TryGetValue(symbol, out var elseValue))
                    continue;
                if (thenValue == elseValue)
                {
                    merged.Values[symbol] = thenValue;
                    continue;
                }

                var type = TypeOf(thenValue);
                if (TypeOf(elseValue) != type)
                    throw Internal($"branch values for symbol {symbol} have different MIR types");
                var phi = AddBlockArgument(merge, type, symbol, source);
                thenArguments.Add(thenValue);
                elseArguments.Add(elseValue);
                merged.Values[symbol] = phi;
            }

            MergeQubitStatesAtJoin(
                merged,
                thenExit.State,
                thenExit.Block!,
                firstSuccessorOrdinal: 0,
                elseExit.State,
                elseExit.Block!,
                secondSuccessorOrdinal: 0,
                merge,
                source);

            thenExit.Block.Terminator = new MirJump(merge.Id, thenArguments, source);
            elseExit.Block.Terminator = new MirJump(merge.Id, elseArguments, source);
            _current = merge;
            _state = merged;
        }

        private (BlockBuilder? Block, FlowState State) LowerScopedBranch(
            BlockBuilder block,
            FlowState seed,
            IReadOnlyList<HirStatement> statements)
        {
            _current = block;
            _state = seed.Clone();
            _state.PushScope();
            LowerStatements(statements);
            _state.PopScope();
            return (_current, _state);
        }

        private void LowerFor(HirForStatement loop)
        {
            var source = SourceOf(loop);
            var from = LowerExpressionAs(
                loop.From,
                MirType.Scalar(QType.Int));
            var to = LowerExpressionAs(
                loop.To,
                MirType.Scalar(QType.Int));
            var step = EmitConstant(QType.Int, "1", source);
            const bool descending = false;
            var before = _state.Clone();
            var preheader = RequireCurrent();
            var header = NewBlock(source);
            var body = NewBlock(source);
            var exit = NewBlock(source);

            var headerState = before.Clone();
            var initialArguments = new List<MirValueId>();
            foreach (var symbol in before.Values.Keys.OrderBy(id => id.Value))
            {
                var initial = before.Values[symbol];
                var argument = AddBlockArgument(header, TypeOf(initial), symbol, source);
                headerState.Values[symbol] = argument;
                initialArguments.Add(initial);
            }
            var qubitPhis = CreateLoopQubitPhis(
                before,
                headerState,
                preheader,
                preheaderSuccessorOrdinal: 0,
                header,
                source);

            headerState.PushScope();
            var loopSymbol = RequireSymbol(loop.Id, $"loop variable `{loop.Variable}`");
            var loopValue = AddBlockArgument(
                header,
                MirType.Scalar(QType.Int),
                loopSymbol.Id,
                source);
            headerState.Declare(loop.Variable, loopSymbol.Id);
            headerState.Values[loopSymbol.Id] = loopValue;
            initialArguments.Add(from);
            preheader.Terminator = new MirJump(header.Id, initialArguments, source);

            _current = header;
            _state = headerState.Clone();
            var condition = EmitBinary(
                descending ? MirBinaryOperator.GreaterOrEqual : MirBinaryOperator.LessOrEqual,
                loopValue,
                to,
                MirType.Scalar(QType.Bit),
                source);
            Terminate(new MirBranch(
                condition,
                body.Id,
                Array.Empty<MirValueId>(),
                exit.Id,
                Array.Empty<MirValueId>(),
                source));

            _current = body;
            _state = headerState.Clone();
            LowerStatements(loop.Body.Statements);
            if (_current is not null)
            {
                var currentLoopValue = _state.Values[loopSymbol.Id];
                var next = EmitBinary(
                    MirBinaryOperator.Add,
                    currentLoopValue,
                    step,
                    MirType.Scalar(QType.Int),
                    source);
                var backArguments = before.Values.Keys
                    .OrderBy(id => id.Value)
                    .Select(symbol => _state.Values[symbol])
                    .Append(next)
                    .ToList();
                var backedge = RequireCurrent();
                AddLoopBackedgeInputs(
                    qubitPhis,
                    _state,
                    backedge,
                    successorOrdinal: 0,
                    header);
                Terminate(new MirJump(header.Id, backArguments, source));
            }

            var after = headerState.Clone();
            after.PopScope();
            _current = exit;
            _state = after;
        }

        private void LowerWhile(HirWhileStatement loop)
        {
            var source = SourceOf(loop);
            var before = _state.Clone();
            var preheader = RequireCurrent();
            var header = NewBlock(source);
            var body = NewBlock(source);
            var exit = NewBlock(source);
            var headerState = before.Clone();
            var initialArguments = new List<MirValueId>();
            foreach (var symbol in before.Values.Keys.OrderBy(id => id.Value))
            {
                var initial = before.Values[symbol];
                var argument = AddBlockArgument(header, TypeOf(initial), symbol, source);
                headerState.Values[symbol] = argument;
                initialArguments.Add(initial);
            }
            var qubitPhis = CreateLoopQubitPhis(
                before,
                headerState,
                preheader,
                preheaderSuccessorOrdinal: 0,
                header,
                source);
            preheader.Terminator = new MirJump(header.Id, initialArguments, source);

            _current = header;
            _state = headerState.Clone();
            var condition = LowerExpressionAs(
                loop.Condition,
                MirType.Scalar(QType.Bit));
            var conditionState = _state.Clone();
            Terminate(new MirBranch(
                condition,
                body.Id,
                Array.Empty<MirValueId>(),
                exit.Id,
                Array.Empty<MirValueId>(),
                source));

            _current = body;
            _state = conditionState.Clone();
            _state.PushScope();
            LowerStatements(loop.Body.Statements);
            _state.PopScope();
            if (_current is not null)
            {
                var backArguments = before.Values.Keys
                    .OrderBy(id => id.Value)
                    .Select(symbol => _state.Values[symbol])
                    .ToList();
                var backedge = RequireCurrent();
                AddLoopBackedgeInputs(
                    qubitPhis,
                    _state,
                    backedge,
                    successorOrdinal: 0,
                    header);
                Terminate(new MirJump(header.Id, backArguments, source));
            }

            _current = exit;
            _state = conditionState;
        }

        private void LowerRepeat(HirRepeatStatement loop)
        {
            var source = SourceOf(loop);
            var before = _state.Clone();
            var preheader = RequireCurrent();
            var header = NewBlock(source);
            var headerState = before.Clone();
            var initialArguments = new List<MirValueId>();
            foreach (var symbol in before.Values.Keys.OrderBy(id => id.Value))
            {
                var initial = before.Values[symbol];
                var argument = AddBlockArgument(header, TypeOf(initial), symbol, source);
                headerState.Values[symbol] = argument;
                initialArguments.Add(initial);
            }
            var qubitPhis = CreateLoopQubitPhis(
                before,
                headerState,
                preheader,
                preheaderSuccessorOrdinal: 0,
                header,
                source);
            preheader.Terminator = new MirJump(header.Id, initialArguments, source);

            _current = header;
            _state = headerState.Clone();
            _state.PushScope();
            LowerStatements(loop.Body.Statements);
            if (_current is null)
            {
                _state.PopScope();
                _state = before;
                return;
            }

            var condition = LowerExpressionAs(
                loop.Until,
                MirType.Scalar(QType.Bit));
            _state.PopScope();

            var exit = NewBlock(source);
            var after = before.Clone();
            var exitArguments = new List<MirValueId>();
            var backArguments = new List<MirValueId>();
            foreach (var symbol in before.Values.Keys.OrderBy(id => id.Value))
            {
                var current = _state.Values[symbol];
                var argument = AddBlockArgument(exit, TypeOf(current), symbol, source);
                after.Values[symbol] = argument;
                exitArguments.Add(current);
                backArguments.Add(current);
            }
            var backedge = RequireCurrent();
            AddLoopBackedgeInputs(
                qubitPhis,
                _state,
                backedge,
                successorOrdinal: 1,
                header);
            foreach (var symbol in before.Qubits.Keys)
                after.Qubits[symbol] = _state.Qubits[symbol];
            Terminate(new MirBranch(
                condition,
                exit.Id,
                exitArguments,
                header.Id,
                backArguments,
                source));
            _current = exit;
            _state = after;
        }

        private void LowerReturn(HirReturnStatement returned)
        {
            var source = SourceOf(returned);
            var value = _callable.ReturnType is { } returnType
                ? LowerExpressionAs(
                    returned.Value,
                    MirType.Scalar(returnType))
                : LowerExpression(returned.Value);
            Terminate(new MirReturn(value, source));
        }

        private MirValueId LowerMeasurement(
            HirMeasurementExpression measurement)
        {
            var source = SourceOf(measurement);
            var access = LowerQubitAccess(measurement.Target);
            var instructionId = NextInstruction();
            var result = AddInstructionResult(
                instructionId,
                MirType.Scalar(QType.Bit),
                null,
                source);
            var qubitResult = NextQubitAfterInstruction(access.Qubit.Id, source);
            Emit(new MirMeasure(instructionId, result, access, qubitResult, source));
            AdvanceCurrentQubit(qubitResult);
            if (measurement.Target is HirIndexExpression indexedTarget)
            {
                RegisterBoundsSite(
                    indexedTarget,
                    instructionId,
                    MirIndexedAccessKind.Measurement);
            }
            return result;
        }

        private MirValueId LowerExpression(HirExpression expression)
        {
            // Expression lowering owns its provenance boundary. Callers cannot substitute a surrounding
            // statement origin, so every emitted MIR term remains queryable by the exact HIR expression ID.
            var source = SourceOf(expression);
            switch (expression)
            {
                case HirMissingExpression:
                    throw Internal("a missing expression reached MIR lowering");

                case HirIntegerLiteralExpression number:
                    return EmitConstant(QType.Int, number.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), source);

                case HirLiteralExpression literal:
                    return EmitConstant(LiteralType(literal.Text), literal.Text, source);

                case HirNameExpression name when IsBuiltinLiteral(name.Name):
                    return EmitConstant(LiteralType(name.Name), name.Name, source);

                case HirNameExpression name:
                {
                    var symbol = _state.Resolve(name.Name)
                        ?? throw Internal($"classical name `{name.Name}` is not active");
                    if (!_state.Values.TryGetValue(symbol, out var value))
                        throw Internal($"name `{name.Name}` does not denote a classical SSA value");
                    return value;
                }

                case HirUnaryExpression unary:
                {
                    var operand = LowerExpression(unary.Operand);
                    if (unary.Operator == HirUnaryOperator.LogicalNot)
                    {
                        operand = EnsureType(operand, MirType.Scalar(QType.Bit), source);
                        return EmitUnary(
                            MirUnaryOperator.LogicalNot,
                            operand,
                            MirType.Scalar(QType.Bit),
                            source);
                    }
                    if (unary.Operator != HirUnaryOperator.Negate)
                        throw Internal($"unsupported unary operator `{unary.Operator}`");
                    var operandType = TypeOf(operand);
                    var resultType = operandType.ElementType == QType.Bit
                        ? MirType.Scalar(QType.Int)
                        : operandType;
                    if (operandType.ElementType == QType.Bit)
                        operand = EnsureType(operand, resultType, source);
                    return EmitUnary(MirUnaryOperator.Negate, operand, resultType, source);
                }

                case HirBinaryExpression binary:
                {
                    if (binary.Operator is
                        HirBinaryOperator.LogicalAnd or HirBinaryOperator.LogicalOr)
                        return LowerShortCircuit(binary);

                    var left = LowerExpression(binary.Left);
                    var right = LowerExpression(binary.Right);
                    var op = BinaryOperator(binary.Operator);
                    var leftType = TypeOf(left);
                    var rightType = TypeOf(right);
                    if (leftType.IsArray || rightType.IsArray)
                    {
                        if (op is not (MirBinaryOperator.Equal or MirBinaryOperator.NotEqual)
                            || leftType != rightType)
                            throw Internal(
                                $"validated array comparison has incompatible operands {leftType} and {rightType}");
                        return EmitBinary(
                            op,
                            left,
                            right,
                            MirType.Scalar(QType.Bit),
                            source);
                    }
                    var comparison = op is
                        MirBinaryOperator.Equal or MirBinaryOperator.NotEqual
                        or MirBinaryOperator.Less or MirBinaryOperator.LessOrEqual
                        or MirBinaryOperator.Greater or MirBinaryOperator.GreaterOrEqual;
                    var operandType = CommonNumericType(leftType, rightType, comparison);
                    left = EnsureType(left, operandType, source);
                    right = EnsureType(right, operandType, source);
                    var resultType = comparison
                        ? MirType.Scalar(QType.Bit)
                        : operandType;
                    return EmitBinary(op, left, right, resultType, source);
                }

                case HirMemberAccessExpression
                {
                    Receiver: { } receiver,
                    MemberName: "Count",
                }:
                {
                    if (receiver is HirNameExpression name)
                    {
                        var symbol = _state.Resolve(name.Name)
                            ?? throw Internal(
                                $"Count receiver `{name.Name}` is not active");
                        if (_state.Qubits.TryGetValue(symbol, out var currentQubit))
                        {
                            var (isArray, length) = QubitShape(currentQubit.Id);
                            if (!isArray)
                                throw Internal(
                                    $"Count receiver `{name.Name}` is a scalar qubit");
                            if (length is not int knownLength)
                            {
                                throw Internal(
                                    $"specialized qubit array `{name.Name}` has no known length");
                            }

                            return EmitConstant(
                                QType.Int,
                                knownLength.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                                source);
                        }

                        if (!_state.Values.TryGetValue(symbol, out var classicalArray))
                        {
                            throw Internal(
                                $"Count receiver `{name.Name}` is neither a classical array nor a qubit");
                        }

                        return EmitArrayLength(classicalArray, name.Name, source);
                    }

                    var array = LowerExpression(receiver);
                    return EmitArrayLength(
                        array,
                        HirExpressions.Render(receiver),
                        source);
                }

                case HirMemberAccessExpression member:
                    throw Internal(
                        $"unsupported member `{member.MemberName}` reached MIR lowering");

                case HirIndexExpression index:
                {
                    var array = LowerExpression(index.Receiver);
                    var arrayType = TypeOf(array);
                    if (!arrayType.IsArray)
                        throw Internal("an indexed classical expression does not have array type");
                    var offset = LowerExpressionAs(
                        index.Index,
                        MirType.Scalar(QType.Int));
                    var instructionId = NextInstruction();
                    var result = AddInstructionResult(
                        instructionId,
                        MirType.Scalar(arrayType.ElementType),
                        null,
                        source);
                    Emit(new MirArrayLoad(instructionId, result, array, offset, source));
                    RegisterBoundsSite(
                        index,
                        instructionId,
                        MirIndexedAccessKind.ArrayLoad);
                    return result;
                }

                case HirCallExpression call:
                {
                    if (QoraGates.Functions.ContainsKey(call.Name))
                        return LowerPureCall(
                            new MirBuiltinFunctionTarget(call.Name),
                            callee: null,
                            call);
                    var callee = ResolveUserCallable(call.CalleeId)
                        ?? throw Internal($"resolved function `{call.Name}` has no callable");
                    if (!callee.IsFunction)
                        throw Internal($"expression call `{call.Name}` targets an operation");
                    return LowerPureCall(
                        new MirUserCallableTarget(_callableIds[callee.Id]),
                        callee,
                        call);
                }

                case HirMeasurementExpression measurement:
                    return LowerMeasurement(measurement);

                case HirArrayLiteralExpression or HirArrayCreationExpression:
                    throw Internal(
                        "an array value escaped its declaration during MIR lowering");

                default:
                    throw Internal(
                        $"unsupported HIR expression node {expression.GetType().Name}");
            }
        }

        private MirValueId LowerExpressionAs(
            HirExpression expression,
            MirType expected) =>
            EnsureType(
                LowerExpression(expression),
                expected,
                SourceOf(expression));

        /// <summary>
        /// Lowers logical conjunction/disjunction as control flow so the right operand is evaluated
        /// only on the edge which needs it. The merge block argument is the SSA result of the logical
        /// expression: the short-circuit edge supplies the already-known left value, while the other
        /// edge supplies the evaluated right value.
        /// </summary>
        private MirValueId LowerShortCircuit(
            HirBinaryExpression binary)
        {
            var source = SourceOf(binary);
            var left = LowerExpressionAs(
                binary.Left,
                MirType.Scalar(QType.Bit));
            var branchBlock = RequireCurrent();
            var before = _state.Clone();
            var rightBlock = NewBlock(source);
            var mergeBlock = NewBlock(source);
            var result = AddBlockArgument(
                mergeBlock,
                MirType.Scalar(QType.Bit),
                symbol: null,
                source);

            if (binary.Operator == HirBinaryOperator.LogicalAnd)
            {
                branchBlock.Terminator = new MirBranch(
                    left,
                    rightBlock.Id,
                    Array.Empty<MirValueId>(),
                    mergeBlock.Id,
                    new[] { left },
                    source);
            }
            else
            {
                branchBlock.Terminator = new MirBranch(
                    left,
                    mergeBlock.Id,
                    new[] { left },
                    rightBlock.Id,
                    Array.Empty<MirValueId>(),
                    source);
            }

            _current = rightBlock;
            _state = before.Clone();
            var right = LowerExpressionAs(
                binary.Right,
                MirType.Scalar(QType.Bit));
            var rightExit = RequireCurrent();
            var rightState = _state.Clone();
            Terminate(new MirJump(mergeBlock.Id, new[] { right }, source));

            var merged = before.Clone();
            MergeQubitStatesAtJoin(
                merged,
                before,
                branchBlock,
                firstSuccessorOrdinal:
                    binary.Operator == HirBinaryOperator.LogicalAnd ? 1 : 0,
                rightState,
                rightExit,
                secondSuccessorOrdinal: 0,
                mergeBlock,
                source);
            _current = mergeBlock;
            _state = merged;
            return result;
        }

        private MirQubitAccess LowerQubitAccess(
            HirArgument argument) =>
            LowerQubitAccess(argument.Expression);

        private MirQubitAccess LowerQubitAccess(
            HirExpression expression)
        {
            switch (expression)
            {
                case HirNameExpression name:
                {
                    var symbol = _state.Resolve(name.Name)
                        ?? throw Internal($"qubit name `{name.Name}` is not active");
                    if (!_state.Qubits.TryGetValue(symbol, out var qubit))
                        throw Internal($"name `{name.Name}` does not denote a qubit");
                    return new MirQubitAccess(qubit);
                }
                case HirIndexExpression
                {
                    Receiver: HirNameExpression name,
                    Index: { } index,
                }:
                {
                    var symbol = _state.Resolve(name.Name)
                        ?? throw Internal($"qubit name `{name.Name}` is not active");
                    if (!_state.Qubits.TryGetValue(symbol, out var qubit))
                        throw Internal($"name `{name.Name}` does not denote a qubit");
                    var offset = LowerExpressionAs(
                        index,
                        MirType.Scalar(QType.Int));
                    return new MirQubitAccess(qubit, offset);
                }
                default:
                    throw Internal(
                        $"`{HirExpressions.Render(expression)}` is not a qubit access");
            }
        }

        private MirValueId EmitConstant(QType type, string text, MirOriginRef source)
        {
            var instructionId = NextInstruction();
            var result = AddInstructionResult(
                instructionId,
                MirType.Scalar(type),
                null,
                source);
            Emit(new MirConstant(
                instructionId,
                result,
                new MirConstantValue(type, text),
                source));
            return result;
        }

        private MirValueId EmitArrayLength(
            MirValueId array,
            string receiver,
            MirOriginRef source)
        {
            if (!TypeOf(array).IsArray)
                throw Internal(
                    $"Count receiver `{receiver}` is not a classical array");

            var instructionId = NextInstruction();
            var result = AddInstructionResult(
                instructionId,
                MirType.Scalar(QType.Int),
                symbol: null,
                source);
            Emit(new MirArrayLength(
                instructionId,
                result,
                array,
                source));
            return result;
        }

        private MirValueId EmitUnary(
            MirUnaryOperator op,
            MirValueId operand,
            MirType resultType,
            MirOriginRef source)
        {
            var instructionId = NextInstruction();
            var result = AddInstructionResult(instructionId, resultType, null, source);
            Emit(new MirUnary(instructionId, result, op, operand, source));
            return result;
        }

        private MirValueId EmitBinary(
            MirBinaryOperator op,
            MirValueId left,
            MirValueId right,
            MirType resultType,
            MirOriginRef source)
        {
            var instructionId = NextInstruction();
            var result = AddInstructionResult(instructionId, resultType, null, source);
            Emit(new MirBinary(instructionId, result, op, left, right, source));
            return result;
        }

        private MirValueId EnsureCallType(
            MirValueId value,
            MirType expected,
            MirOriginRef source)
        {
            var actual = TypeOf(value);
            if (actual == expected) return value;
            if (actual.IsArray && expected.IsArray
                && actual.ElementType == expected.ElementType
                && (expected.KnownLength is null || actual.KnownLength == expected.KnownLength))
                return value;
            return EnsureType(value, expected, source);
        }

        private MirValueId EnsureType(
            MirValueId value,
            MirType expected,
            MirOriginRef source)
        {
            var actual = TypeOf(value);
            if (actual == expected) return value;
            if (actual.IsArray || expected.IsArray)
                throw Internal($"MIR lowering cannot convert {actual} to {expected}");
            var instructionId = NextInstruction();
            var result = AddInstructionResult(instructionId, expected, null, source);
            Emit(new MirConvert(instructionId, result, value, expected, source));
            return result;
        }

        private MirValueId AddInstructionResult(
            MirInstructionId instruction,
            MirType type,
            SymbolId? symbol,
            MirOriginRef source,
            int resultIndex = 0) =>
            AddValue(
                type,
                MirValueDefinition.InstructionResultAt(
                    RequireCurrent().Id,
                    instruction,
                    resultIndex),
                symbol,
                source);

        private MirValueId AddBlockArgument(
            BlockBuilder block,
            MirType type,
            SymbolId? symbol,
            MirOriginRef source)
        {
            var index = block.Arguments.Count;
            var value = AddValue(
                type,
                MirValueDefinition.BlockArgumentAt(block.Id, index),
                symbol,
                source);
            block.Arguments.Add(new MirBlockArgument(value, type));
            return value;
        }

        private MirValueId AddValue(
            MirType type,
            MirValueDefinition definition,
            SymbolId? symbol,
            MirOriginRef source)
        {
            var id = new MirValueId(_values.Count);
            _valueIndexes[id] = _values.Count;
            _values.Add(new MirValue(id, type, definition, source));
            _links.RegisterTemporaryValue(_callableId, id);
            if (symbol is SymbolId sourceSymbol)
                _links.LinkValue(sourceSymbol, _callableId, id);
            return id;
        }

        private void AttachSymbol(MirValueId value, SymbolId symbol) =>
            _links.LinkValue(symbol, _callableId, value);

        private MirType TypeOf(MirValueId value) => _values[_valueIndexes[value]].Type;

        private void RegisterQubitArgumentBoundsSites(
            IReadOnlyList<HirArgument> arguments,
            IReadOnlyList<IParamSpec> parameters,
            MirInstructionId instruction)
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                if (parameters[index].Type != QType.Qubit)
                    continue;
                if (arguments[index].Expression is HirIndexExpression indexed)
                {
                    RegisterBoundsSite(
                        indexed,
                        instruction,
                        MirIndexedAccessKind.QubitOperand,
                        index);
                }
            }
        }

        private void RegisterBoundsSite(
            HirIndexExpression exactHirSite,
            MirInstructionId instruction,
            MirIndexedAccessKind kind,
            int ordinal = 0)
        {
            if (_semantics.SourceModel.UnprovenIndexSite(
                    exactHirSite.Id)
                is not { } semanticSite)
            {
                return;
            }

            var loweredSite = new MirIndexedAccessRef(
                new MirInstructionRef(
                    _snapshotId,
                    _callableId,
                    instruction),
                kind,
                ordinal);
            if (!_boundsSites.TryAdd(semanticSite, loweredSite))
            {
                throw Internal(
                    $"unproven bounds site {semanticSite} lowered more than once");
            }
        }

        private void EmitQuantumApply(
            MirInstructionId instructionId,
            MirCallTarget target,
            IReadOnlyList<MirCallOperand> operands,
            IReadOnlyList<MirMutableArrayResult> mutableArrayResults,
            IReadOnlyList<MirFunctor> functors,
            IReadOnlyList<int> writtenOperandIndexes,
            MirOriginRef source)
        {
            var results = new List<MirQubitAfterInstruction>();
            var writtenQubits = new HashSet<MirQubitId>();
            foreach (var operandIndex in writtenOperandIndexes)
            {
                if (operandIndex < 0 || operandIndex >= operands.Count)
                {
                    throw Internal(
                        $"quantum write operand {operandIndex} is outside the operand list");
                }

                if (operands[operandIndex] is not MirQubitCallOperand qubitOperand)
                {
                    throw Internal(
                        $"quantum write operand {operandIndex} is not a qubit");
                }

                var qubitId = qubitOperand.Qubit.Qubit.Id;
                if (!writtenQubits.Add(qubitId))
                    continue; // One register version covers all elements written by this instruction.

                results.Add(NextQubitAfterInstruction(qubitId, source));
            }

            Emit(new MirQuantumApply(
                instructionId,
                target,
                operands,
                results,
                mutableArrayResults,
                functors,
                source));

            foreach (var result in results)
                AdvanceCurrentQubit(result);
        }

        private IReadOnlyList<int> WrittenBuiltinQubitOperands(
            HirCallStatement statement)
        {
            var call = statement.Call;
            if (!QoraGates.Gates.TryGetValue(call.Name, out var info))
                throw Internal($"built-in gate `{call.Name}` has no effect metadata");

            var firstQubit = info.AngleFirst ? 1 : 0;
            var qubitOperands = Enumerable.Range(
                firstQubit,
                call.Arguments.Count - firstQubit).ToList();
            if (!info.Unitary)
                return qubitOperands;
            if (info.Diagonal)
                return Array.Empty<int>();

            var controlCount = info.Controls
                + statement.Modifiers.Count(
                    modifier => modifier == QGateModifier.Controlled);
            if (controlCount > qubitOperands.Count)
            {
                throw Internal(
                    $"built-in gate `{call.Name}` declares more controls than qubit operands");
            }

            return qubitOperands.Skip(controlCount).ToArray();
        }

        private IReadOnlyList<int> WrittenUserQubitOperands(HirCallable callee)
        {
            var effects = _semantics.SourceModel.FindOpEffects(callee.Id)
                ?? throw Internal($"callable `{callee.Name}` has no HIR effect summary");
            var written = new List<int>();
            for (var index = 0; index < callee.Parameters.Count; index++)
            {
                var parameter = callee.Parameters[index];
                if (parameter.Type == QType.Qubit
                    && effects.ParamModified.Any(reference =>
                        string.Equals(reference.Reg, parameter.Name, StringComparison.Ordinal)))
                {
                    written.Add(index);
                }
            }

            return written;
        }

        private void RegisterQubitSeed(SymbolId symbol, MirQubit qubit)
        {
            if (qubit.Version.Value != 0)
                throw Internal($"initial qubit {qubit.Id} does not have version zero");
            if (!_qubitSeeds.TryAdd(qubit.Id, qubit))
                throw Internal($"qubit identity {qubit.Id} was allocated more than once");
            _nextQubitVersions.Add(qubit.Id, 1);
            _links.LinkQubit(symbol, _callableId, qubit.Key);
        }

        private MirQubitAfterInstruction NextQubitAfterInstruction(
            MirQubitId id,
            MirOriginRef source)
        {
            if (!_nextQubitVersions.TryGetValue(id, out var next))
                throw Internal($"qubit identity {id} has no initial definition");
            _nextQubitVersions[id] = checked(next + 1);
            return new MirQubitAfterInstruction(
                id,
                new MirQubitVersion(next),
                source);
        }

        private MirQubitPhi NextQubitPhi(
            SymbolId symbol,
            MirQubitId id,
            BlockBuilder block,
            IReadOnlyList<MirQubitPhiInput> inputs,
            MirOriginRef source)
        {
            if (!_nextQubitVersions.TryGetValue(id, out var next))
                throw Internal($"qubit identity {id} has no initial definition");
            _nextQubitVersions[id] = checked(next + 1);
            var phi = new MirQubitPhi(
                id,
                new MirQubitVersion(next),
                block.Id,
                inputs,
                source);
            block.QubitPhis.Add(phi);
            _links.LinkQubit(symbol, _callableId, phi.Key);
            return phi;
        }

        private void MergeQubitStatesAtJoin(
            FlowState merged,
            FlowState firstState,
            BlockBuilder firstPredecessor,
            int firstSuccessorOrdinal,
            FlowState secondState,
            BlockBuilder secondPredecessor,
            int secondSuccessorOrdinal,
            BlockBuilder join,
            MirOriginRef source)
        {
            var groups = merged.Qubits
                .OrderBy(pair => pair.Key.Value)
                .GroupBy(pair => pair.Value.Id)
                .Select(group => (
                    Id: group.Key,
                    Symbols: group.Select(pair => pair.Key).ToArray()))
                .ToArray();

            foreach (var group in groups)
            {
                var first = Incoming(firstState, group.Id, group.Symbols);
                var second = Incoming(secondState, group.Id, group.Symbols);
                if (first.Key == second.Key)
                {
                    foreach (var symbol in group.Symbols)
                        merged.Qubits[symbol] = first;
                    continue;
                }

                var phi = NextQubitPhi(
                    group.Symbols[0],
                    group.Id,
                    join,
                    new[]
                    {
                        new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                firstPredecessor.Id,
                                firstSuccessorOrdinal,
                                join.Id),
                            first.Key),
                        new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                secondPredecessor.Id,
                                secondSuccessorOrdinal,
                                join.Id),
                            second.Key),
                    },
                    source);
                foreach (var symbol in group.Symbols)
                {
                    merged.Qubits[symbol] = phi;
                    if (symbol != group.Symbols[0])
                        _links.LinkQubit(symbol, _callableId, phi.Key);
                }
            }

            MirQubit Incoming(
                FlowState state,
                MirQubitId expectedId,
                IReadOnlyList<SymbolId> symbols)
            {
                if (!state.Qubits.TryGetValue(symbols[0], out var incoming)
                    || incoming.Id != expectedId)
                {
                    throw Internal(
                        $"join predecessor has no current state for qubit {expectedId}");
                }
                if (symbols.Any(symbol =>
                        !state.Qubits.TryGetValue(symbol, out var alias)
                        || alias.Key != incoming.Key))
                {
                    throw Internal(
                        $"aliases of qubit {expectedId} reach a join with different versions");
                }
                return incoming;
            }
        }

        private IReadOnlyList<LoopQubitPhiBinding> CreateLoopQubitPhis(
            FlowState before,
            FlowState headerState,
            BlockBuilder preheader,
            int preheaderSuccessorOrdinal,
            BlockBuilder header,
            MirOriginRef source)
        {
            var bindings = new List<LoopQubitPhiBinding>();
            foreach (var group in before.Qubits
                         .OrderBy(pair => pair.Key.Value)
                         .GroupBy(pair => pair.Value.Id))
            {
                var symbols = group.Select(pair => pair.Key).ToArray();
                var incoming = group.First().Value;
                if (group.Any(pair => pair.Value.Key != incoming.Key))
                {
                    throw Internal(
                        $"aliases of qubit {incoming.Id} enter a loop with different versions");
                }

                var phi = NextQubitPhi(
                    symbols[0],
                    incoming.Id,
                    header,
                    new[]
                    {
                        new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                preheader.Id,
                                preheaderSuccessorOrdinal,
                                header.Id),
                            incoming.Key),
                    },
                    source);
                foreach (var symbol in symbols)
                {
                    headerState.Qubits[symbol] = phi;
                    if (symbol != symbols[0])
                        _links.LinkQubit(symbol, _callableId, phi.Key);
                }

                bindings.Add(new LoopQubitPhiBinding(phi, symbols));
            }

            return bindings;
        }

        private void AddLoopBackedgeInputs(
            IReadOnlyList<LoopQubitPhiBinding> bindings,
            FlowState backedgeState,
            BlockBuilder backedge,
            int successorOrdinal,
            BlockBuilder header)
        {
            foreach (var binding in bindings)
            {
                var incoming = backedgeState.Qubits[binding.Symbols[0]];
                if (incoming.Id != binding.Phi.Id)
                {
                    throw Internal(
                        $"loop backedge replaces qubit {binding.Phi.Id} with {incoming.Id}");
                }
                if (binding.Symbols.Any(symbol =>
                        backedgeState.Qubits[symbol].Key != incoming.Key))
                {
                    throw Internal(
                        $"aliases of qubit {incoming.Id} leave a loop with different versions");
                }

                header.ReplacePhi(binding.Phi with
                {
                    Inputs = binding.Phi.Inputs
                        .Append(new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                backedge.Id,
                                successorOrdinal,
                                header.Id),
                            incoming.Key))
                        .ToArray(),
                });
            }
        }

        private void AdvanceCurrentQubit(MirQubit qubit)
        {
            var symbols = _state.Qubits
                .Where(pair => pair.Value.Id == qubit.Id)
                .Select(pair => pair.Key)
                .ToArray();
            if (symbols.Length == 0)
                throw Internal($"written qubit {qubit.Id} has no active HIR binding");

            foreach (var symbol in symbols)
            {
                _state.Qubits[symbol] = qubit;
                _links.LinkQubit(symbol, _callableId, qubit.Key);
            }
        }

        private (bool IsArray, int? Length) QubitShape(MirQubitId id) =>
            RequireQubitSeed(id) switch
            {
                MirQubitParameter parameter => (parameter.IsArray, parameter.Length),
                MirQubitFromUse local => (local.IsArray, local.Length),
                var seed => throw Internal(
                    $"qubit {id} has invalid initial definition {seed.GetType().Name}"),
            };

        private MirQubit RequireQubitSeed(MirQubitId id) =>
            _qubitSeeds.TryGetValue(id, out var seed)
                ? seed
                : throw Internal($"qubit identity {id} has no initial definition");

        private void Emit(MirInstruction instruction) =>
            RequireCurrent().Instructions.Add(instruction);

        private void Terminate(MirTerminator terminator)
        {
            var block = RequireCurrent();
            if (block.Terminator is not null)
                throw Internal($"block {block.Id} already has a terminator");
            block.Terminator = terminator;
            _current = null;
        }

        private BlockBuilder RequireCurrent() =>
            _current ?? throw Internal("attempted to emit into a terminated control-flow path");

        private BlockBuilder NewBlock(MirOriginRef source)
        {
            var block = new BlockBuilder(new MirBlockId(_blocks.Count), source);
            _blocks.Add(block);
            _blockById.Add(block.Id, block);
            return block;
        }

        private MirInstructionId NextInstruction() => new(_nextInstruction++);
        private MirStorageId NextStorage() => new(_nextStorage++);
        private MirQubitId NextQubitId() => new(_nextQubit++);

        private Symbol RequireSymbol(HirNodeId declarationId, string role) =>
            _semantics.FindSymbol(declarationId)
            ?? throw Internal($"{role} has no semantic symbol");

        private HirCallable? ResolveUserCallable(HirNodeId? sourceCallableId)
        {
            if (sourceCallableId is HirNodeId id
                && _callablesById.TryGetValue(id, out var byId))
            {
                return byId;
            }
            return null;
        }

        private SymbolId? SymbolOfWholeArgument(HirArgument argument)
        {
            if (argument.Expression is not HirNameExpression name) return null;
            return _state.Resolve(name.Name);
        }

        private static IReadOnlyList<MirFunctor> LowerModifiers(
            IReadOnlyList<QGateModifier> modifiers) =>
            modifiers.Select(modifier => modifier switch
            {
                QGateModifier.Controlled => MirFunctor.Controlled,
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: unsupported HIR gate modifier `{modifier}` reached MIR lowering"),
            }).ToList();

        private static MirBinaryOperator BinaryOperator(
            HirBinaryOperator @operator) => @operator switch
        {
            HirBinaryOperator.Add => MirBinaryOperator.Add,
            HirBinaryOperator.Subtract => MirBinaryOperator.Subtract,
            HirBinaryOperator.Multiply => MirBinaryOperator.Multiply,
            HirBinaryOperator.Divide => MirBinaryOperator.Divide,
            HirBinaryOperator.Equal => MirBinaryOperator.Equal,
            HirBinaryOperator.NotEqual => MirBinaryOperator.NotEqual,
            HirBinaryOperator.LessThan => MirBinaryOperator.Less,
            HirBinaryOperator.LessThanOrEqual => MirBinaryOperator.LessOrEqual,
            HirBinaryOperator.GreaterThan => MirBinaryOperator.Greater,
            HirBinaryOperator.GreaterThanOrEqual => MirBinaryOperator.GreaterOrEqual,
            _ => throw new InvalidOperationException(
                $"QINTERNAL: unsupported binary operator `{@operator}` reached MIR lowering"),
        };

        private static MirType CommonNumericType(
            MirType left,
            MirType right,
            bool comparison)
        {
            if (left.IsArray || right.IsArray)
                throw new InvalidOperationException(
                    "QINTERNAL: an array reached a scalar MIR binary instruction");
            if (left == right)
            {
                if (!comparison && left.ElementType == QType.Bit)
                    return MirType.Scalar(QType.Int);
                return left;
            }
            if (left.ElementType == QType.Float || right.ElementType == QType.Float)
                return MirType.Scalar(QType.Float);
            if (left.ElementType == QType.Angle || right.ElementType == QType.Angle)
                return MirType.Scalar(QType.Angle);
            return MirType.Scalar(QType.Int);
        }

        private static bool IsBuiltinLiteral(string name) =>
            name is "true" or "false" or "pi" or "tau" or "euler";

        private static QType LiteralType(string text) =>
            text is "true" or "false"
                ? QType.Bit
                : text is "pi" or "tau" or "euler" || text.Contains('.')
                    ? QType.Float
                    : QType.Int;

        private MirOriginRef SourceOf(HirNode node) =>
            _origins.Hir(
                _callable.Id,
                node.Id);

        private InvalidOperationException Internal(string message) =>
            new($"QINTERNAL: MIR lowering of `{_callable.Name}` failed: {message}");

        private sealed record LoopQubitPhiBinding(
            MirQubitPhi Phi,
            IReadOnlyList<SymbolId> Symbols);

        private sealed class BlockBuilder
        {
            public MirBlockId Id { get; }
            public List<MirBlockArgument> Arguments { get; } = new();
            public List<MirQubitPhi> QubitPhis { get; } = new();
            public List<MirInstruction> Instructions { get; } = new();
            public MirTerminator? Terminator { get; set; }
            public MirOriginRef Origin { get; }

            public BlockBuilder(MirBlockId id, MirOriginRef origin)
            {
                Id = id;
                Origin = origin;
            }

            public MirBlock Build() => new(
                Id,
                Arguments,
                Instructions,
                Terminator ?? new MirUnreachable(Origin),
                Origin,
                QubitPhis);

            public void ReplacePhi(MirQubitPhi phi)
            {
                var index = QubitPhis.FindIndex(existing => existing.Key == phi.Key);
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: block {Id} does not contain qubit Phi {phi.Key}");
                }

                QubitPhis[index] = phi;
            }
        }

        private sealed class FlowState
        {
            private readonly List<Dictionary<string, SymbolId>> _scopes;
            public Dictionary<SymbolId, MirValueId> Values { get; }
            public Dictionary<SymbolId, MirStorageId> Storages { get; }
            public Dictionary<SymbolId, MirQubit> Qubits { get; }

            public FlowState()
            {
                _scopes = new List<Dictionary<string, SymbolId>>
                {
                    new(StringComparer.Ordinal),
                };
                Values = new();
                Storages = new();
                Qubits = new();
            }

            private FlowState(
                List<Dictionary<string, SymbolId>> scopes,
                Dictionary<SymbolId, MirValueId> values,
                Dictionary<SymbolId, MirStorageId> storages,
                Dictionary<SymbolId, MirQubit> qubits)
            {
                _scopes = scopes;
                Values = values;
                Storages = storages;
                Qubits = qubits;
            }

            public FlowState Clone() => new(
                _scopes
                    .Select(scope => new Dictionary<string, SymbolId>(scope, StringComparer.Ordinal))
                    .ToList(),
                new Dictionary<SymbolId, MirValueId>(Values),
                new Dictionary<SymbolId, MirStorageId>(Storages),
                new Dictionary<SymbolId, MirQubit>(Qubits));

            public void PushScope() =>
                _scopes.Add(new Dictionary<string, SymbolId>(StringComparer.Ordinal));

            public void PopScope()
            {
                if (_scopes.Count == 1)
                    throw new InvalidOperationException("QINTERNAL: cannot pop the MIR root lexical scope");
                var removed = _scopes[^1];
                _scopes.RemoveAt(_scopes.Count - 1);
                foreach (var symbol in removed.Values)
                {
                    Values.Remove(symbol);
                    Storages.Remove(symbol);
                    Qubits.Remove(symbol);
                }
            }

            public void Declare(string name, SymbolId symbol) =>
                _scopes[^1][name] = symbol;

            public SymbolId? Resolve(string name)
            {
                for (var index = _scopes.Count - 1; index >= 0; index--)
                    if (_scopes[index].TryGetValue(name, out var symbol))
                        return symbol;
                return null;
            }
        }
    }
}
