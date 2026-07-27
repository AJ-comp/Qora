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
/// qubits use a separate linear resource space.
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
        var callableIds = hir.Operations
            .Select((operation, index) => (SourceId: operation.Id, MirId: new MirCallableId(index)))
            .ToDictionary(item => item.SourceId, item => item.MirId);
        var operationsById = hir.Operations.ToDictionary(operation => operation.Id);
        var callables = hir.Operations
            .Select(operation => new CallableLowerer(
                operation,
                callableIds[operation.Id],
                semantics,
                callableIds,
                operationsById,
                origins,
                links).Lower())
            .ToList();

        var originTable = origins.Build();
        return new MirLoweringResult(
            new MirProgram(snapshotId, originTable, callables),
            links.Build(originTable));
    }

    private sealed class CallableLowerer
    {
        private readonly QOperation _operation;
        private readonly MirCallableId _callableId;
        private readonly IHirSemanticContext _semantics;
        private readonly IReadOnlyDictionary<int, MirCallableId> _callableIds;
        private readonly IReadOnlyDictionary<int, QOperation> _operationsById;
        private readonly MirOriginTableBuilder _origins;
        private readonly MirCrossStageLinksBuilder _links;

        private readonly List<BlockBuilder> _blocks = new();
        private readonly List<MirValue> _values = new();
        private readonly List<MirArrayStorage> _storages = new();
        private readonly List<MirQubitResource> _qubits = new();
        private readonly List<MirParameter> _parameters = new();
        private readonly Dictionary<MirValueId, int> _valueIndexes = new();
        private readonly Dictionary<MirBlockId, BlockBuilder> _blockById = new();

        private int _nextInstruction;
        private int _nextStorage;
        private int _nextQubit;
        private BlockBuilder? _current;
        private FlowState _state = new();
        private readonly MirOriginRef _operationOrigin;

        public CallableLowerer(
            QOperation operation,
            MirCallableId callableId,
            IHirSemanticContext semantics,
            IReadOnlyDictionary<int, MirCallableId> callableIds,
            IReadOnlyDictionary<int, QOperation> operationsById,
            MirOriginTableBuilder origins,
            MirCrossStageLinksBuilder links)
        {
            _operation = operation;
            _callableId = callableId;
            _semantics = semantics;
            _callableIds = callableIds;
            _operationsById = operationsById;
            _origins = origins;
            _links = links;
            _operationOrigin = _origins.Hir(
                operation.Id,
                operation.Id);
        }

        public MirCallable Lower()
        {
            var operationSymbol = RequireSymbol(
                _operation.Id,
                $"callable `{_operation.Name}`");
            _links.LinkCallable(
                _operation.Id,
                operationSymbol.Id,
                _callableId);

            var entry = NewBlock(_operationOrigin);
            _current = entry;

            LowerParameters();
            HoistQubitAllocations(_operation.Body);
            LowerStatements(_operation.Body);

            if (_current is not null)
            {
                Terminate(_operation.IsFunction
                    ? new MirUnreachable(
                        _origins.Synthesized(
                            _operationOrigin,
                            "missing validated function return"))
                    : new MirReturn(
                        null,
                        _origins.Synthesized(
                            _operationOrigin,
                            "implicit operation return")));
            }

            var blocks = _blocks.Select(block => block.Build()).ToList();
            return new MirCallable(
                _callableId,
                _operation.Name,
                _operation.IsFunction ? MirCallableKind.Function : MirCallableKind.Operation,
                _operation.ReturnType is { } returns ? MirType.Scalar(returns) : null,
                _parameters,
                entry.Id,
                blocks,
                _values,
                _storages,
                _qubits,
                _operationOrigin);
        }

        private void LowerParameters()
        {
            for (var index = 0; index < _operation.Params.Count; index++)
            {
                var parameter = _operation.Params[index];
                var symbol = RequireSymbol(parameter.Id, $"parameter `{parameter.Name}`");
                var parameterOrigin = _origins.Hir(
                    _operation.Id,
                    parameter.Id);
                _state.Declare(parameter.Name, symbol.Id);

                if (parameter.Type == QType.Qubit)
                {
                    var resourceId = NextQubit();
                    var isArray = parameter.IsArray;
                    var resource = new MirQubitResource(
                        resourceId,
                        parameter.Name,
                        MirQubitResourceKind.Parameter,
                        isArray,
                        isArray ? parameter.RegisterSize : null,
                        AllocationInstruction: null,
                        parameterOrigin);
                    _qubits.Add(resource);
                    _links.LinkQubit(symbol.Id, _callableId, resourceId);
                    _state.Qubits[symbol.Id] = resourceId;
                    _parameters.Add(new MirQubitParameter(
                        parameter.Name,
                        parameterOrigin,
                        resourceId,
                        isArray,
                        isArray ? parameter.RegisterSize : null,
                        parameter.Ownership));
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

        private void HoistQubitAllocations(IReadOnlyList<QStmt> statements)
        {
            foreach (var use in statements.OfType<QUse>())
            {
                var symbol = RequireSymbol(use.Id, $"qubit allocation `{use.Name}`");
                _state.Declare(use.Name, symbol.Id);
                var instructionId = NextInstruction();
                var resourceId = NextQubit();
                var source = SourceOf(use);
                _qubits.Add(new MirQubitResource(
                    resourceId,
                    use.Name,
                    MirQubitResourceKind.Local,
                    IsArray: true,
                    use.Size,
                    instructionId,
                    source));
                _links.LinkQubit(
                    symbol.Id,
                    _callableId,
                    resourceId);
                Emit(new MirQubitAllocate(instructionId, resourceId, source));
                _state.Qubits[symbol.Id] = resourceId;
            }
        }

        private void LowerStatements(IReadOnlyList<QStmt> statements)
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

        private void MarkUnreachableDeclarations(QStmt statement)
        {
            if (_semantics.FindSymbol(statement.Id) is { } symbol)
                _links.MarkUnreachable(symbol.Id);

            switch (statement)
            {
                case QIf conditional:
                    foreach (var nested in conditional.Then)
                        MarkUnreachableDeclarations(nested);
                    foreach (var nested in conditional.Else)
                        MarkUnreachableDeclarations(nested);
                    break;
                case QFor loop:
                    foreach (var nested in loop.Body)
                        MarkUnreachableDeclarations(nested);
                    break;
                case QWhile loop:
                    foreach (var nested in loop.Body)
                        MarkUnreachableDeclarations(nested);
                    break;
                case QRepeat loop:
                    foreach (var nested in loop.Body)
                        MarkUnreachableDeclarations(nested);
                    break;
                case QConjugate conjugate:
                    foreach (var nested in conjugate.Within)
                        MarkUnreachableDeclarations(nested);
                    foreach (var nested in conjugate.Apply)
                        MarkUnreachableDeclarations(nested);
                    break;
            }
        }

        private void LowerStatement(QStmt statement)
        {
            switch (statement)
            {
                case QUse:
                    // Local qubit resources are allocated once in the callable entry block.
                    return;

                case QDecl declaration:
                    LowerDeclaration(declaration);
                    return;

                case QAssign assignment:
                    LowerAssignment(assignment);
                    return;

                case QGate gate:
                    LowerStatementCall(gate);
                    return;

                case QIf conditional:
                    LowerIf(conditional);
                    return;

                case QFor loop:
                    LowerFor(loop);
                    return;

                case QWhile loop:
                    LowerWhile(loop);
                    return;

                case QRepeat loop:
                    LowerRepeat(loop);
                    return;

                case QReturn returned:
                    LowerReturn(returned);
                    return;

                case QConjugate:
                    throw Internal("a QConjugate reached MIR lowering; run ConjugationLowering first");

                case QBreak:
                    throw Internal("a backend-only QBreak reached common MIR lowering");

                default:
                    throw Internal($"unsupported HIR statement {statement.GetType().Name}");
            }
        }

        private void LowerDeclaration(QDecl declaration)
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
                    QArrayNew allocation =>
                        (allocation.Length, MirArrayInitialization.ZeroInitialized, new List<MirValueId>()),
                    QArrayLiteral literal =>
                        (literal.Elements.Count, MirArrayInitialization.ExplicitElements,
                            literal.Elements
                                .Select(element => EnsureType(
                                    LowerExpression(element, source),
                                    MirType.Scalar(elementType),
                                    source))
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
            var value = EnsureType(
                LowerExpression(declaration.Value, source),
                MirType.Scalar(declaredType),
                source);
            AttachSymbol(value, symbol.Id);
            _state.Declare(declaration.Name, symbol.Id);
            _state.Values[symbol.Id] = value;
        }

        private void LowerAssignment(QAssign assignment)
        {
            var symbolId = _state.Resolve(assignment.Name)
                ?? throw Internal($"assignment target `{assignment.Name}` is not active");
            var source = SourceOf(assignment);

            if (assignment.Index is null)
            {
                var expected = _state.Values.TryGetValue(symbolId, out var current)
                    ? TypeOf(current)
                    : throw Internal($"assignment target `{assignment.Name}` has no SSA state");
                var value = EnsureType(LowerExpression(assignment.Value, source), expected, source);
                AttachSymbol(value, symbolId);
                _state.Values[symbolId] = value;
                return;
            }

            if (!_state.Values.TryGetValue(symbolId, out var array))
                throw Internal($"indexed assignment target `{assignment.Name}` has no array state");
            var arrayType = TypeOf(array);
            if (!arrayType.IsArray)
                throw Internal($"indexed assignment target `{assignment.Name}` is not an array");
            var index = EnsureType(
                LowerNode(assignment.Index, source),
                MirType.Scalar(QType.Int),
                source);
            var storedValue = EnsureType(
                LowerExpression(assignment.Value, source),
                MirType.Scalar(arrayType.ElementType),
                source);
            var instructionId = NextInstruction();
            var result = AddInstructionResult(instructionId, arrayType, symbolId, source);
            Emit(new MirArrayStore(instructionId, result, array, index, storedValue, source));
            _state.Values[symbolId] = result;
        }

        private void LowerStatementCall(QGate gate)
        {
            var source = SourceOf(gate);
            var callee = ResolveUserCallable(gate.CalleeOpId);
            if (callee is { IsFunction: true })
            {
                LowerPureCall(
                    new MirUserCallableTarget(_callableIds[callee.Id]),
                    callee,
                    gate.Args.Select(ArgumentNode).ToList(),
                    source);
                return;
            }

            if (callee is not null)
            {
                LowerOperationCall(gate, callee, source);
                return;
            }

            var signature = QoraGates.SigOf(
                gate.Name,
                gate.Functors.Count(functor => functor == "Controlled"))
                ?? throw Internal($"validated built-in gate `{gate.Name}` has no signature");
            var operands = new List<MirCallOperand>(gate.Args.Count);
            for (var index = 0; index < gate.Args.Count; index++)
            {
                var argument = gate.Args[index];
                var expected = signature.Params[index];
                if (expected.Type == QType.Qubit)
                {
                    operands.Add(new MirQubitCallOperand(
                        LowerQubitPlace(argument, source),
                        argument.Ownership,
                        argument.Access));
                }
                else
                {
                    var value = EnsureType(
                        LowerNode(ArgumentNode(argument), source),
                        MirType.Scalar(expected.Type),
                        source);
                    operands.Add(new MirClassicalCallOperand(
                        value,
                        argument.Ownership,
                        argument.Access));
                }
            }

            Emit(new MirQuantumApply(
                NextInstruction(),
                new MirBuiltinGateTarget(gate.Name),
                operands,
                Array.Empty<MirMutableArrayResult>(),
                LowerFunctors(gate.Functors),
                source));
        }

        private void LowerOperationCall(QGate gate, QOperation callee, MirOriginRef source)
        {
            var instructionId = NextInstruction();
            var operands = new List<MirCallOperand>(gate.Args.Count);
            var mutableResults = new List<MirMutableArrayResult>();
            var mutableBindings = new List<(SymbolId Symbol, MirValueId Result)>();

            for (var index = 0; index < gate.Args.Count; index++)
            {
                var argument = gate.Args[index];
                var parameter = callee.Params[index];
                if (parameter.Type == QType.Qubit)
                {
                    operands.Add(new MirQubitCallOperand(
                        LowerQubitPlace(argument, source),
                        argument.Ownership,
                        argument.Access));
                    continue;
                }

                var argumentNode = ArgumentNode(argument);
                var value = LowerNode(argumentNode, source);
                var expected = parameter.IsArray
                    ? MirType.Array(parameter.Type, parameter.RegisterSize)
                    : MirType.Scalar(parameter.Type);
                value = EnsureCallType(value, expected, source);
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

            Emit(new MirQuantumApply(
                instructionId,
                new MirUserCallableTarget(_callableIds[callee.Id]),
                operands,
                mutableResults,
                LowerFunctors(gate.Functors),
                source));
            foreach (var (symbol, result) in mutableBindings)
                _state.Values[symbol] = result;
        }

        private MirValueId LowerPureCall(
            MirCallTarget target,
            QOperation? callee,
            IReadOnlyList<QNode> arguments,
            MirOriginRef source)
        {
            IReadOnlyList<QParam>? parameters = callee?.Params;
            var operands = new List<MirCallOperand>(arguments.Count);
            for (var index = 0; index < arguments.Count; index++)
            {
                var value = LowerNode(arguments[index], source);
                if (parameters is not null)
                {
                    var parameter = parameters[index];
                    var expected = parameter.IsArray
                        ? MirType.Array(parameter.Type, parameter.RegisterSize)
                        : MirType.Scalar(parameter.Type);
                    value = EnsureCallType(value, expected, source);
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

        private void LowerIf(QIf conditional)
        {
            var source = SourceOf(conditional);
            var condition = EnsureType(
                LowerNode(conditional.Cond.Tree
                    ?? throw Internal("validated if condition has no expression"), source),
                MirType.Scalar(QType.Bit),
                source);
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

            var thenExit = LowerScopedBranch(thenBlock, before, conditional.Then);
            var elseExit = LowerScopedBranch(elseBlock, before, conditional.Else);

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

            thenExit.Block.Terminator = new MirJump(merge.Id, thenArguments, source);
            elseExit.Block.Terminator = new MirJump(merge.Id, elseArguments, source);
            _current = merge;
            _state = merged;
        }

        private (BlockBuilder? Block, FlowState State) LowerScopedBranch(
            BlockBuilder block,
            FlowState seed,
            IReadOnlyList<QStmt> statements)
        {
            _current = block;
            _state = seed.Clone();
            _state.PushScope();
            LowerStatements(statements);
            _state.PopScope();
            return (_current, _state);
        }

        private void LowerFor(QFor loop)
        {
            var source = SourceOf(loop);
            var from = EnsureType(LowerNode(loop.From, source), MirType.Scalar(QType.Int), source);
            var to = EnsureType(LowerNode(loop.To, source), MirType.Scalar(QType.Int), source);
            var step = EnsureType(
                LowerNode(loop.Step ?? new QNumLit(1), source),
                MirType.Scalar(QType.Int),
                source);
            var descending = loop.Step is QNumLit { Value: < 0 };
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

            headerState.PushScope();
            var loopSymbol = RequireSymbol(loop.Id, $"loop variable `{loop.Var}`");
            var loopValue = AddBlockArgument(
                header,
                MirType.Scalar(QType.Int),
                loopSymbol.Id,
                source);
            headerState.Declare(loop.Var, loopSymbol.Id);
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
            LowerStatements(loop.Body);
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
                Terminate(new MirJump(header.Id, backArguments, source));
            }

            var after = headerState.Clone();
            after.PopScope();
            _current = exit;
            _state = after;
        }

        private void LowerWhile(QWhile loop)
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
            preheader.Terminator = new MirJump(header.Id, initialArguments, source);

            _current = header;
            _state = headerState.Clone();
            var condition = EnsureType(
                LowerNode(loop.Cond.Tree
                    ?? throw Internal("validated while condition has no expression"), source),
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
            _state.PushScope();
            LowerStatements(loop.Body);
            _state.PopScope();
            if (_current is not null)
            {
                var backArguments = before.Values.Keys
                    .OrderBy(id => id.Value)
                    .Select(symbol => _state.Values[symbol])
                    .ToList();
                Terminate(new MirJump(header.Id, backArguments, source));
            }

            _current = exit;
            _state = headerState;
        }

        private void LowerRepeat(QRepeat loop)
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
            preheader.Terminator = new MirJump(header.Id, initialArguments, source);

            _current = header;
            _state = headerState.Clone();
            _state.PushScope();
            LowerStatements(loop.Body);
            if (_current is null)
            {
                _state.PopScope();
                _state = before;
                return;
            }

            var condition = EnsureType(
                LowerNode(loop.Until.Tree
                    ?? throw Internal("validated repeat condition has no expression"), source),
                MirType.Scalar(QType.Bit),
                source);
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

        private void LowerReturn(QReturn returned)
        {
            var source = SourceOf(returned);
            var value = LowerExpression(returned.Value, source);
            if (_operation.ReturnType is { } returnType)
                value = EnsureType(value, MirType.Scalar(returnType), source);
            Terminate(new MirReturn(value, source));
        }

        private MirValueId LowerExpression(QExpr expression, MirOriginRef source) => expression switch
        {
            QText text => LowerNode(
                text.Tree ?? throw Internal("validated text expression has no tree"),
                source),
            QMeasure measurement => LowerMeasurement(measurement, source),
            QArrayLiteral or QArrayNew =>
                throw Internal("an array value escaped its declaration during MIR lowering"),
            _ => throw Internal($"unsupported HIR expression {expression.GetType().Name}"),
        };

        private MirValueId LowerMeasurement(QMeasure measurement, MirOriginRef source)
        {
            var place = LowerQubitPlace(measurement.Target, source);
            var instructionId = NextInstruction();
            var result = AddInstructionResult(
                instructionId,
                MirType.Scalar(QType.Bit),
                null,
                source);
            Emit(new MirMeasure(instructionId, result, place, source));
            return result;
        }

        private MirValueId LowerNode(QNode node, MirOriginRef source)
        {
            switch (node)
            {
                case QNumLit number:
                    return EmitConstant(QType.Int, number.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), source);

                case QLit literal:
                    return EmitConstant(LiteralType(literal.Text), literal.Text, source);

                case QNameRef name when IsBuiltinLiteral(name.Name):
                    return EmitConstant(LiteralType(name.Name), name.Name, source);

                case QNameRef name:
                {
                    var symbol = _state.Resolve(name.Name)
                        ?? throw Internal($"classical name `{name.Name}` is not active");
                    if (!_state.Values.TryGetValue(symbol, out var value))
                        throw Internal($"name `{name.Name}` does not denote a classical SSA value");
                    return value;
                }

                case QUnary unary:
                {
                    var operand = LowerNode(unary.Operand, source);
                    if (unary.Op == "!")
                    {
                        operand = EnsureType(operand, MirType.Scalar(QType.Bit), source);
                        return EmitUnary(
                            MirUnaryOperator.LogicalNot,
                            operand,
                            MirType.Scalar(QType.Bit),
                            source);
                    }
                    if (unary.Op != "-")
                        throw Internal($"unsupported unary operator `{unary.Op}`");
                    var operandType = TypeOf(operand);
                    var resultType = operandType.ElementType == QType.Bit
                        ? MirType.Scalar(QType.Int)
                        : operandType;
                    if (operandType.ElementType == QType.Bit)
                        operand = EnsureType(operand, resultType, source);
                    return EmitUnary(MirUnaryOperator.Negate, operand, resultType, source);
                }

                case QBinOp binary:
                {
                    if (binary.Op is "&&" or "||")
                        return LowerShortCircuit(binary, source);

                    var left = LowerNode(binary.Left, source);
                    var right = LowerNode(binary.Right, source);
                    var op = BinaryOperator(binary.Op);
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

                case QMember { Base: { } owner, Member: "Count" }:
                {
                    if (owner is QNameRef name)
                    {
                        var symbol = _state.Resolve(name.Name)
                            ?? throw Internal(
                                $"Count receiver `{name.Name}` is not active");
                        if (_state.Qubits.TryGetValue(symbol, out var qubitId))
                        {
                            var qubit = _qubits.FirstOrDefault(
                                resource => resource.Id == qubitId)
                                ?? throw Internal(
                                    $"qubit resource {qubitId} is missing");
                            if (!qubit.IsArray)
                                throw Internal(
                                    $"Count receiver `{name.Name}` is a scalar qubit");
                            if (qubit.Length is not int knownLength)
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
                                $"Count receiver `{name.Name}` is neither a classical array nor a qubit resource");
                        }

                        return EmitArrayLength(classicalArray, name.Name, source);
                    }

                    var array = LowerNode(owner, source);
                    return EmitArrayLength(array, QNodes.Render(owner), source);
                }

                case QMember member:
                    throw Internal($"unsupported member `{member.Member}` reached MIR lowering");

                case QIndexNode index:
                {
                    var array = LowerNode(index.Base, source);
                    var arrayType = TypeOf(array);
                    if (!arrayType.IsArray)
                        throw Internal("an indexed classical expression does not have array type");
                    var offset = EnsureType(
                        LowerNode(index.Index, source),
                        MirType.Scalar(QType.Int),
                        source);
                    var instructionId = NextInstruction();
                    var result = AddInstructionResult(
                        instructionId,
                        MirType.Scalar(arrayType.ElementType),
                        null,
                        source);
                    Emit(new MirArrayLoad(instructionId, result, array, offset, source));
                    return result;
                }

                case QCallNode call:
                {
                    if (QoraGates.Functions.ContainsKey(call.Name))
                        return LowerPureCall(
                            new MirBuiltinFunctionTarget(call.Name),
                            callee: null,
                            call.Args,
                            source);
                    var callee = ResolveUserCallable(call.CalleeOpId)
                        ?? throw Internal($"resolved function `{call.Name}` has no callable");
                    if (!callee.IsFunction)
                        throw Internal($"expression call `{call.Name}` targets an operation");
                    return LowerPureCall(
                        new MirUserCallableTarget(_callableIds[callee.Id]),
                        callee,
                        call.Args,
                        source);
                }

                default:
                    throw Internal($"unsupported HIR expression node {node.GetType().Name}");
            }
        }

        /// <summary>
        /// Lowers logical conjunction/disjunction as control flow so the right operand is evaluated
        /// only on the edge which needs it. The merge block argument is the SSA result of the logical
        /// expression: the short-circuit edge supplies the already-known left value, while the other
        /// edge supplies the evaluated right value.
        /// </summary>
        private MirValueId LowerShortCircuit(QBinOp binary, MirOriginRef source)
        {
            var left = EnsureType(
                LowerNode(binary.Left, source),
                MirType.Scalar(QType.Bit),
                source);
            var branchBlock = RequireCurrent();
            var before = _state.Clone();
            var rightBlock = NewBlock(source);
            var mergeBlock = NewBlock(source);
            var result = AddBlockArgument(
                mergeBlock,
                MirType.Scalar(QType.Bit),
                symbol: null,
                source);

            if (binary.Op == "&&")
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
            var right = EnsureType(
                LowerNode(binary.Right, source),
                MirType.Scalar(QType.Bit),
                source);
            Terminate(new MirJump(mergeBlock.Id, new[] { right }, source));

            _current = mergeBlock;
            _state = before;
            return result;
        }

        private MirQubitPlace LowerQubitPlace(QArg argument, MirOriginRef source) => argument switch
        {
            QQubitArg indexed => LowerQubitPlace(
                new QIndexNode(new QNameRef(indexed.Reg), indexed.Index),
                source),
            QTextArg { Tree: { } tree } => LowerQubitPlace(tree, source),
            _ => throw Internal("validated qubit argument has no reference"),
        };

        private MirQubitPlace LowerQubitPlace(QNode node, MirOriginRef source)
        {
            switch (node)
            {
                case QNameRef name:
                {
                    var symbol = _state.Resolve(name.Name)
                        ?? throw Internal($"qubit name `{name.Name}` is not active");
                    if (!_state.Qubits.TryGetValue(symbol, out var resource))
                        throw Internal($"name `{name.Name}` does not denote a qubit resource");
                    return new MirQubitPlace(resource);
                }
                case QIndexNode { Base: QNameRef name, Index: { } index }:
                {
                    var symbol = _state.Resolve(name.Name)
                        ?? throw Internal($"qubit name `{name.Name}` is not active");
                    if (!_state.Qubits.TryGetValue(symbol, out var resource))
                        throw Internal($"name `{name.Name}` does not denote a qubit resource");
                    var offset = EnsureType(
                        LowerNode(index, source),
                        MirType.Scalar(QType.Int),
                        source);
                    return new MirQubitPlace(resource, offset);
                }
                default:
                    throw Internal($"`{QNodes.Render(node)}` is not a qubit place");
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
        private MirQubitResourceId NextQubit() => new(_nextQubit++);

        private Symbol RequireSymbol(int declarationId, string role) =>
            _semantics.FindSymbol(declarationId)
            ?? throw Internal($"{role} has no semantic symbol");

        private QOperation? ResolveUserCallable(int? sourceOperationId)
        {
            if (sourceOperationId is int id && _operationsById.TryGetValue(id, out var byId))
                return byId;
            return null;
        }

        private SymbolId? SymbolOfWholeArgument(QArg argument)
        {
            if (argument is not QTextArg { Tree: QNameRef name }) return null;
            return _state.Resolve(name.Name);
        }

        private static QNode ArgumentNode(QArg argument) => argument switch
        {
            QQubitArg indexed => new QIndexNode(new QNameRef(indexed.Reg), indexed.Index),
            QTextArg { Tree: { } tree } => tree,
            _ => throw new InvalidOperationException("QINTERNAL: a validated call argument has no expression"),
        };

        private static IReadOnlyList<MirFunctor> LowerFunctors(IReadOnlyList<string> functors) =>
            functors.Select(functor => functor switch
            {
                "Adjoint" => MirFunctor.Adjoint,
                "Controlled" => MirFunctor.Controlled,
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: unsupported functor `{functor}` reached MIR lowering"),
            }).ToList();

        private static MirBinaryOperator BinaryOperator(string op) => op switch
        {
            "+" => MirBinaryOperator.Add,
            "-" => MirBinaryOperator.Subtract,
            "*" => MirBinaryOperator.Multiply,
            "/" => MirBinaryOperator.Divide,
            "==" => MirBinaryOperator.Equal,
            "!=" => MirBinaryOperator.NotEqual,
            "<" => MirBinaryOperator.Less,
            "<=" => MirBinaryOperator.LessOrEqual,
            ">" => MirBinaryOperator.Greater,
            ">=" => MirBinaryOperator.GreaterOrEqual,
            _ => throw new InvalidOperationException(
                $"QINTERNAL: unsupported binary operator `{op}` reached MIR lowering"),
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

        private MirOriginRef SourceOf(QStmt statement) =>
            _origins.Hir(
                _operation.Id,
                statement.Id);

        private InvalidOperationException Internal(string message) =>
            new($"QINTERNAL: MIR lowering of `{_operation.Name}` failed: {message}");

        private sealed class BlockBuilder
        {
            public MirBlockId Id { get; }
            public List<MirBlockArgument> Arguments { get; } = new();
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
                Origin);
        }

        private sealed class FlowState
        {
            private readonly List<Dictionary<string, SymbolId>> _scopes;
            public Dictionary<SymbolId, MirValueId> Values { get; }
            public Dictionary<SymbolId, MirStorageId> Storages { get; }
            public Dictionary<SymbolId, MirQubitResourceId> Qubits { get; }

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
                Dictionary<SymbolId, MirQubitResourceId> qubits)
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
                new Dictionary<SymbolId, MirQubitResourceId>(Qubits));

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
