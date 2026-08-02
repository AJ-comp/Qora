using Qora.Compiler;
using Qora.Ir.Passes;

namespace Qora.Ir.Mir;

/// <summary>
/// Lowers Qora's final effect-analyzed, source-shaped HIR into typed SSA/CFG MIR.
///
/// HIR name resolution is complete before this boundary. Lowering consumes the exact use-site
/// <see cref="SymbolId"/> recorded by the semantic model and only tracks that symbol's current MIR state;
/// it never resolves source spelling again. Symbols are not copied into MIR entities; an optional
/// <see cref="IMirLoweringTraceSink"/> may observe exact source relationships for an upper query layer.
/// Every emitted MIR reference is a typed MIR ID, never a string.
/// Classical assignments create immutable SSA states, block arguments represent Phi values, arrays retain
/// both storage identity and state versions, and qubits use a separate versioned identity space.
/// </summary>
internal static class QoraMirLowering
{
    /// <summary>
    /// Converts the compilation's final effect-analyzed HIR. The <see cref="HirCompilation"/> owns the
    /// canonical final artifact, so callers cannot combine a HIR snapshot with another snapshot's model.
    /// </summary>
    public static MirSnapshot ToMir(
        this HirCompilation hirCompilation,
        IMirLoweringTraceSink? traceSink = null)
    {
        ArgumentNullException.ThrowIfNull(hirCompilation);

        var finalHirArtifact = hirCompilation.EffectAnalysis
            ?? throw new InvalidOperationException(
                "MIR lowering requires the compilation's final effect-analyzed HIR");
        return LowerFinalHirArtifact(finalHirArtifact, traceSink);
    }

    private static MirSnapshot LowerFinalHirArtifact(
        HirSemanticArtifact finalHirArtifact,
        IMirLoweringTraceSink? traceSink)
    {
        ArgumentNullException.ThrowIfNull(finalHirArtifact);

        var hirSnapshot = finalHirArtifact.Source;
        var hirProgram = hirSnapshot.Program;
        var mirCallables =
            new List<MirCallable>(hirProgram.Callables.Count);

        foreach (var hirCallable in hirProgram.Callables)
        {
            var mirCallableLowerer = new HirCallableToMirLowerer(
                hirCallable,
                finalHirArtifact,
                traceSink);
            var mirCallable =
                mirCallableLowerer.LowerToMirCallable();

            mirCallables.Add(mirCallable);
        }

        var hirEntryCallable = hirProgram.EntryCallable
            ?? throw new InvalidOperationException(
                "validated HIR must contain an operation before MIR lowering");
        var mirEntryCallableId =
            new MirCallableId(hirEntryCallable.Id.Value);
        var mirProgram = new MirProgram(
            mirEntryCallableId,
            mirCallables);
        return MirSnapshot.CreateLowered(
            mirProgram,
            finalHirArtifact);
    }

    private sealed class HirCallableToMirLowerer
    {
        private readonly HirCallable _hirCallable;
        private readonly MirCallableId _mirCallableId;
        private readonly HirSemanticArtifact _finalHirArtifact;
        private readonly IMirLoweringTraceSink? _traceSink;
        private readonly List<MirBlockBuilder> _mirBlockBuilders = new();
        private readonly List<MirValue> _mirValues = new();
        private readonly List<MirArrayStorage> _mirArrayStorages = new();
        private readonly List<IMirParameter> _mirParameters = new();
        private readonly Dictionary<MirQubitId, MirQubit>
            _initialMirQubitsById = new();
        private readonly Dictionary<MirQubitId, int>
            _nextMirQubitVersionById = new();

        private int _nextMirInstructionIdValue;
        private MirBlockBuilder? _currentMirBlockBuilder;
        private LoweringFlowState _currentFlowState = new();

        public HirCallableToMirLowerer(
            HirCallable hirCallable,
            HirSemanticArtifact finalHirArtifact,
            IMirLoweringTraceSink? traceSink)
        {
            _hirCallable = hirCallable;
            _finalHirArtifact = finalHirArtifact;
            _mirCallableId =
                new MirCallableId(hirCallable.Id.Value);
            _traceSink = traceSink;
        }

        public MirCallable LowerToMirCallable()
        {
            var mirCallableOrigin = MirOriginFor(_hirCallable);
            var hirCallableSymbol = RequireHirDeclarationSymbol(
                _hirCallable.Id,
                $"callable `{_hirCallable.Name}`");
            var hirScopeGraph = _finalHirArtifact.Model.ScopeGraph
                ?? throw MirLoweringError(
                    "the final HIR semantic model has no scope graph");
            var qualifiedSourceCallableName =
                hirScopeGraph.QualifiedName(hirCallableSymbol);

            var mirEntryBlock = CreateMirBlock(mirCallableOrigin);
            _currentMirBlockBuilder = mirEntryBlock;

            LowerParameters();
            HoistQubitAllocations(_hirCallable.Body.Statements);
            LowerStatements(_hirCallable.Body.Statements);

            if (_currentMirBlockBuilder is not null)
            {
                TerminateCurrentMirBlock(_hirCallable.IsFunction
                    ? new MirUnreachable(
                        new MirGeneratedOrigin(
                            mirCallableOrigin,
                            "missing validated function return"))
                    : new MirReturn(
                        null,
                        new MirGeneratedOrigin(
                            mirCallableOrigin,
                            "implicit operation return")));
            }

            var mirBlocks = _mirBlockBuilders
                .Select(blockBuilder => blockBuilder.Build())
                .ToList();
            return new MirCallable(
                _mirCallableId,
                qualifiedSourceCallableName,
                _hirCallable.ReturnType is { } returns
                    ? MirType.Scalar(returns)
                    : null,
                _mirParameters,
                mirEntryBlock.Id,
                mirBlocks,
                _mirValues,
                _mirArrayStorages,
                mirCallableOrigin);
        }

        private void LowerParameters()
        {
            var requiredArrayLengths =
                _finalHirArtifact.Model.RequiredArgLengths(_hirCallable.Id);

            for (var hirParameterIndex = 0;
                 hirParameterIndex < _hirCallable.Parameters.Count;
                 hirParameterIndex++)
            {
                var hirParameter = _hirCallable.Parameters[hirParameterIndex];
                var hirParameterSymbol = RequireHirDeclarationSymbol(
                    hirParameter.Id,
                    $"parameter `{hirParameter.Name}`");
                var mirParameterOrigin = MirOriginFor(hirParameter);
                _currentFlowState.TrackDeclaredHirSymbol(hirParameterSymbol.Id);

                if (hirParameter.Type == QType.Qubit)
                {
                    var mirQubitId = AllocateMirQubitId();
                    var isQubitRegister = hirParameter.IsArray;
                    var mirQubitParameter = new MirQubitParameter(
                        mirQubitId,
                        hirParameter.Name,
                        isQubitRegister,
                        isQubitRegister ? hirParameter.RegisterSize : null,
                        hirParameter.Ownership,
                        mirParameterOrigin);
                    RegisterInitialMirQubit(
                        hirParameterSymbol.Id,
                        mirQubitParameter);
                    _currentFlowState.CurrentMirQubitByHirSymbolId[
                            hirParameterSymbol.Id] =
                        mirQubitParameter;
                    _mirParameters.Add(mirQubitParameter);
                    continue;
                }

                var mirParameterType = hirParameter.IsArray
                    ? MirType.Array(hirParameter.Type, hirParameter.RegisterSize)
                    : MirType.Scalar(hirParameter.Type);
                var mirParameterValueId = AddValue(
                    mirParameterType,
                    MirValueDefinition.ParameterAt(hirParameterIndex),
                    hirParameterSymbol.Id,
                    mirParameterOrigin);
                MirStorageId? mirArrayStorageId = null;
                if (hirParameter.IsArray)
                {
                    mirArrayStorageId = AllocateMirStorageId();
                    _mirArrayStorages.Add(new MirArrayStorage(
                        mirArrayStorageId.Value,
                        hirParameter.Name,
                        mirParameterOrigin));
                    _traceSink?.LinkStorage(
                        hirParameterSymbol.Id,
                        _mirCallableId,
                        mirArrayStorageId.Value);
                }
                var minimumLength = hirParameter.IsArray ? 1 : 0;
                if (hirParameter.IsArray
                    && requiredArrayLengths is not null
                    && requiredArrayLengths.TryGetValue(
                        hirParameter.Name,
                        out var requiredLength))
                {
                    if (requiredLength < 0 || requiredLength > int.MaxValue)
                    {
                        throw MirLoweringError(
                            $"array parameter `{hirParameter.Name}` has invalid "
                            + $"minimum length {requiredLength}");
                    }

                    minimumLength = Math.Max(minimumLength, (int)requiredLength);
                }
                _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirParameterSymbol.Id] =
                    mirParameterValueId;
                _mirParameters.Add(new MirClassicalParameter(
                    hirParameter.Name,
                    mirParameterValueId,
                    mirArrayStorageId,
                    hirParameter.Ownership,
                    hirParameter.Access,
                    minimumLength));
            }
        }

        private void HoistQubitAllocations(IReadOnlyList<HirStatement> hirStatements)
        {
            foreach (var hirQubitDeclaration in
                     hirStatements.OfType<HirQubitDeclarationStatement>())
            {
                var hirQubitSymbol = RequireHirDeclarationSymbol(
                    hirQubitDeclaration.Id,
                    $"qubit allocation `{hirQubitDeclaration.Name}`");
                _currentFlowState.TrackDeclaredHirSymbol(hirQubitSymbol.Id);
                var mirAllocationInstructionId = AllocateMirInstructionId();
                var mirQubitId = AllocateMirQubitId();
                var mirOrigin = MirOriginFor(hirQubitDeclaration);
                var mirAllocatedQubit = new MirQubitFromUse(
                    mirQubitId,
                    hirQubitDeclaration.Name,
                    hirQubitDeclaration.Size,
                    mirOrigin);
                RegisterInitialMirQubit(
                    hirQubitSymbol.Id,
                    mirAllocatedQubit);
                EmitMirInstruction(new MirQubitAllocate(
                    mirAllocationInstructionId,
                    mirAllocatedQubit,
                    mirOrigin));
                _currentFlowState.CurrentMirQubitByHirSymbolId[
                        hirQubitSymbol.Id] =
                    mirAllocatedQubit;
            }
        }

        private void LowerStatements(IReadOnlyList<HirStatement> hirStatements)
        {
            for (var hirStatementIndex = 0;
                 hirStatementIndex < hirStatements.Count;
                 hirStatementIndex++)
            {
                if (_currentMirBlockBuilder is null)
                {
                    for (;
                         hirStatementIndex < hirStatements.Count;
                         hirStatementIndex++)
                    {
                        MarkUnreachableDeclarations(
                            hirStatements[hirStatementIndex]);
                    }
                    return;
                }
                LowerStatement(hirStatements[hirStatementIndex]);
            }
        }

        private void MarkUnreachableDeclarations(HirStatement hirStatement)
        {
            if (_traceSink is null)
                return;

            if (_finalHirArtifact.Model.FindSymbol(hirStatement.Id)
                is { } hirDeclaredSymbol)
                _traceSink.MarkUnreachable(hirDeclaredSymbol.Id);

            switch (hirStatement)
            {
                case HirIfStatement hirIf:
                    foreach (var hirNestedStatement in hirIf.Then.Statements)
                        MarkUnreachableDeclarations(hirNestedStatement);
                    foreach (var hirNestedStatement in hirIf.Else.Statements)
                        MarkUnreachableDeclarations(hirNestedStatement);
                    break;
                case HirForStatement hirFor:
                    foreach (var hirNestedStatement in hirFor.Body.Statements)
                        MarkUnreachableDeclarations(hirNestedStatement);
                    break;
                case HirWhileStatement hirWhile:
                    foreach (var hirNestedStatement in hirWhile.Body.Statements)
                        MarkUnreachableDeclarations(hirNestedStatement);
                    break;
                case HirRepeatStatement hirRepeat:
                    foreach (var hirNestedStatement in hirRepeat.Body.Statements)
                        MarkUnreachableDeclarations(hirNestedStatement);
                    break;
            }
        }

        private void LowerStatement(HirStatement hirStatement)
        {
            switch (hirStatement)
            {
                case HirQubitDeclarationStatement:
                    // Local qubits are allocated once in the callable entry block.
                    return;

                case HirVariableDeclarationStatement hirDeclaration:
                    LowerDeclaration(hirDeclaration);
                    return;

                case HirAssignmentStatement hirAssignment:
                    LowerAssignment(hirAssignment);
                    return;

                case HirCallStatement hirCallStatement:
                    LowerStatementCall(hirCallStatement);
                    return;

                case HirIfStatement hirIf:
                    LowerIf(hirIf);
                    return;

                case HirForStatement hirFor:
                    LowerFor(hirFor);
                    return;

                case HirWhileStatement hirWhile:
                    LowerWhile(hirWhile);
                    return;

                case HirRepeatStatement hirRepeat:
                    LowerRepeat(hirRepeat);
                    return;

                case HirReturnStatement hirReturn:
                    LowerReturn(hirReturn);
                    return;

                default:
                    throw MirLoweringError(
                        $"unsupported HIR statement {hirStatement.GetType().Name}");
            }
        }

        private void LowerDeclaration(HirVariableDeclarationStatement hirDeclaration)
        {
            var hirDeclaredSymbol = RequireHirDeclarationSymbol(
                hirDeclaration.Id,
                $"declaration `{hirDeclaration.Name}`");
            var mirOrigin = MirOriginFor(hirDeclaration);

            if (hirDeclaration.IsArray)
            {
                var declaredElementQType = hirDeclaration.Type
                    ?? hirDeclaredSymbol.Type
                    ?? throw MirLoweringError(
                        $"array `{hirDeclaration.Name}` has no element type");
                int declaredArrayLength;
                MirArrayInitialization mirArrayInitialization;
                List<MirValueId> mirElementValueIds;

                switch (hirDeclaration.Value)
                {
                    case HirArrayCreationExpression hirArrayCreation:
                        declaredArrayLength = hirArrayCreation.Length;
                        mirArrayInitialization =
                            MirArrayInitialization.ZeroInitialized;
                        mirElementValueIds = new List<MirValueId>();
                        break;

                    case HirArrayLiteralExpression hirArrayLiteral:
                        declaredArrayLength = hirArrayLiteral.Elements.Count;
                        mirArrayInitialization =
                            MirArrayInitialization.ExplicitElements;
                        mirElementValueIds =
                            new List<MirValueId>(hirArrayLiteral.Elements.Count);
                        foreach (var hirElementExpression in hirArrayLiteral.Elements)
                        {
                            var mirElementValueId = LowerExpressionAs(
                                hirElementExpression,
                                MirType.Scalar(declaredElementQType));
                            mirElementValueIds.Add(mirElementValueId);
                        }
                        break;

                    default:
                        throw MirLoweringError(
                            $"validated array `{hirDeclaration.Name}` "
                            + "has a non-array initializer");
                }

                var mirArrayCreationInstructionId =
                    AllocateMirInstructionId();
                var mirArrayStorageId = AllocateMirStorageId();
                var mirArrayType = MirType.Array(
                    declaredElementQType,
                    declaredArrayLength);
                var mirArrayValueId = AddInstructionResult(
                    mirArrayCreationInstructionId,
                    mirArrayType,
                    hirDeclaredSymbol.Id,
                    mirOrigin);
                _mirArrayStorages.Add(new MirArrayStorage(
                    mirArrayStorageId,
                    hirDeclaration.Name,
                    mirOrigin));
                _traceSink?.LinkStorage(
                    hirDeclaredSymbol.Id,
                    _mirCallableId,
                    mirArrayStorageId);
                EmitMirInstruction(new MirArrayCreate(
                    mirArrayCreationInstructionId,
                    mirArrayValueId,
                    mirArrayStorageId,
                    mirArrayInitialization,
                    mirElementValueIds,
                    mirOrigin));
                _currentFlowState.TrackDeclaredHirSymbol(
                    hirDeclaredSymbol.Id);
                _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirDeclaredSymbol.Id] =
                    mirArrayValueId;
                return;
            }

            var declaredScalarQType = hirDeclaredSymbol.Type
                ?? hirDeclaration.Type
                ?? throw MirLoweringError(
                    $"scalar `{hirDeclaration.Name}` has no inferred type");
            var mirInitialValueId = LowerExpressionAs(
                hirDeclaration.Value,
                MirType.Scalar(declaredScalarQType));
            TraceMirValueForHirSymbol(
                mirInitialValueId,
                hirDeclaredSymbol.Id);
            _currentFlowState.TrackDeclaredHirSymbol(hirDeclaredSymbol.Id);
            _currentFlowState.CurrentMirValueByHirSymbolId[
                    hirDeclaredSymbol.Id] =
                mirInitialValueId;
        }

        private void LowerAssignment(HirAssignmentStatement hirAssignment)
        {
            var mirOrigin = MirOriginFor(hirAssignment);

            if (hirAssignment.Target is HirNameExpression hirNameTarget)
            {
                var hirTargetSymbolId = SymbolIdOf(
                    hirNameTarget,
                    $"assignment target `{hirNameTarget.Name}`");
                var mirTargetType =
                    _currentFlowState.CurrentMirValueByHirSymbolId.TryGetValue(
                        hirTargetSymbolId,
                        out var currentMirValueId)
                    ? MirTypeOf(currentMirValueId)
                    : throw MirLoweringError(
                        $"assignment target `{hirNameTarget.Name}` "
                        + "has no SSA state");
                var mirAssignedValueId = LowerExpressionAs(
                    hirAssignment.Value,
                    mirTargetType);
                TraceMirValueForHirSymbol(
                    mirAssignedValueId,
                    hirTargetSymbolId);
                _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirTargetSymbolId] =
                    mirAssignedValueId;
                return;
            }

            if (hirAssignment.Target is not HirIndexExpression
                {
                    Receiver: HirNameExpression hirArrayReceiver,
                } hirIndexedTarget)
            {
                throw MirLoweringError(
                    "unsupported assignment target "
                    + $"`{HirExpressions.Render(hirAssignment.Target)}`");
            }

            var hirArraySymbolId = SymbolIdOf(
                hirArrayReceiver,
                $"assignment target `{hirArrayReceiver.Name}`");
            if (!_currentFlowState.CurrentMirValueByHirSymbolId.TryGetValue(
                    hirArraySymbolId,
                    out var currentMirArrayValueId))
            {
                throw MirLoweringError(
                    $"indexed assignment target `{hirArrayReceiver.Name}` "
                    + "has no array state");
            }
            var mirArrayType = MirTypeOf(currentMirArrayValueId);
            if (!mirArrayType.IsArray)
            {
                throw MirLoweringError(
                    $"indexed assignment target `{hirArrayReceiver.Name}` "
                    + "is not an array");
            }
            var mirIndexValueId = LowerExpressionAs(
                hirIndexedTarget.Index,
                MirType.Scalar(QType.Int));
            var mirStoredValueId = LowerExpressionAs(
                hirAssignment.Value,
                MirType.Scalar(mirArrayType.ElementType));
            var mirIndexedTargetOrigin = MirOriginFor(hirIndexedTarget);
            var mirArrayStoreInstructionId = AllocateMirInstructionId();
            var mirUpdatedArrayValueId = AddInstructionResult(
                mirArrayStoreInstructionId,
                mirArrayType,
                hirArraySymbolId,
                mirOrigin);
            EmitMirInstruction(new MirArrayStore(
                mirArrayStoreInstructionId,
                mirUpdatedArrayValueId,
                currentMirArrayValueId,
                mirIndexValueId,
                mirStoredValueId,
                mirIndexedTargetOrigin));
            _currentFlowState.CurrentMirValueByHirSymbolId[
                    hirArraySymbolId] =
                mirUpdatedArrayValueId;
        }

        private void LowerStatementCall(
            HirCallStatement hirCallStatement)
        {
            var mirCallOrigin = MirOriginFor(hirCallStatement);
            var hirCall = hirCallStatement.Call;
            var semanticCallTargetSymbol =
                RequireHirCallTargetSymbol(hirCall);
            if (semanticCallTargetSymbol.Kind == SymbolKind.Callable)
            {
                var hirUserCallable = RequireHirCallableDeclaration(
                    semanticCallTargetSymbol);
                if (hirUserCallable.IsFunction)
                {
                    var mirUserCallableId =
                        new MirCallableId(hirUserCallable.Id.Value);
                    LowerPureCall(
                        new MirUserCallableTarget(
                            mirUserCallableId),
                        hirUserCallable,
                        hirCall);
                    return;
                }

                LowerUserOperationCall(
                    hirCallStatement,
                    hirUserCallable,
                    mirCallOrigin);
                return;
            }

            if (semanticCallTargetSymbol.Kind
                != SymbolKind.BuiltinGate)
            {
                throw MirLoweringError(
                    $"statement call `{hirCall.Name}` targets semantic symbol kind "
                    + semanticCallTargetSymbol.Kind);
            }
            var builtinGateName =
                semanticCallTargetSymbol.SourceName;
            var builtinGateSignature = QoraGates.SigOf(
                builtinGateName,
                hirCallStatement.Modifiers.Count(
                    modifier => modifier == QGateModifier.Controlled))
                ?? throw MirLoweringError(
                    $"validated built-in gate `{builtinGateName}` has no signature");
            var mirCallOperands =
                new List<MirCallOperand>(hirCall.Arguments.Count);
            for (var argumentIndex = 0;
                 argumentIndex < hirCall.Arguments.Count;
                 argumentIndex++)
            {
                var hirArgument =
                    hirCall.Arguments[argumentIndex];
                var builtinParameterSpec =
                    builtinGateSignature.Parameters[argumentIndex];
                if (builtinParameterSpec.Type == QType.Qubit)
                {
                    mirCallOperands.Add(new MirQubitCallOperand(
                        LowerQubitAccess(hirArgument),
                        hirArgument.Ownership,
                        hirArgument.Access));
                }
                else
                {
                    var mirArgumentValueId = LowerExpressionAs(
                        hirArgument.Expression,
                        MirType.Scalar(builtinParameterSpec.Type));
                    mirCallOperands.Add(new MirClassicalCallOperand(
                        mirArgumentValueId,
                        hirArgument.Ownership,
                        hirArgument.Access));
                }
            }

            var mirCallInstructionId = AllocateMirInstructionId();
            EmitMirQuantumApply(
                mirCallInstructionId,
                new MirBuiltinGateTarget(builtinGateName),
                mirCallOperands,
                Array.Empty<MirMutableArrayResult>(),
                LowerModifiersToMirFunctors(hirCallStatement.Modifiers),
                WrittenBuiltinQubitOperandIndexes(
                    hirCallStatement,
                    builtinGateName),
                mirCallOrigin);
        }

        private void LowerUserOperationCall(
            HirCallStatement hirCallStatement,
            HirCallable hirUserOperation,
            MirOrigin mirCallOrigin)
        {
            var hirCall = hirCallStatement.Call;
            var mirCallInstructionId = AllocateMirInstructionId();
            var mirCallOperands =
                new List<MirCallOperand>(hirCall.Arguments.Count);
            var mirMutableArrayResults =
                new List<MirMutableArrayResult>();
            var mirMutableArrayValueUpdates =
                new List<(
                    SymbolId HirSymbolId,
                    MirValueId UpdatedMirArrayValueId)>();

            for (var argumentIndex = 0;
                 argumentIndex < hirCall.Arguments.Count;
                 argumentIndex++)
            {
                var hirArgument =
                    hirCall.Arguments[argumentIndex];
                var hirParameter =
                    hirUserOperation.Parameters[argumentIndex];
                if (hirParameter.Type == QType.Qubit)
                {
                    mirCallOperands.Add(new MirQubitCallOperand(
                        LowerQubitAccess(hirArgument),
                        hirArgument.Ownership,
                        hirArgument.Access));
                    continue;
                }

                var mirArgumentValueId =
                    LowerExpression(hirArgument.Expression);
                var expectedMirArgumentType = hirParameter.IsArray
                    ? MirType.Array(
                        hirParameter.Type,
                        hirParameter.RegisterSize)
                    : MirType.Scalar(hirParameter.Type);
                mirArgumentValueId = EnsureCallType(
                    mirArgumentValueId,
                    expectedMirArgumentType,
                    MirOriginFor(hirArgument.Expression));
                mirCallOperands.Add(new MirClassicalCallOperand(
                    mirArgumentValueId,
                    hirArgument.Ownership,
                    hirArgument.Access));

                if (hirParameter.IsArray
                    && hirParameter.Ownership == QOwnershipMode.Borrowed
                    && hirParameter.Access == QAccessMode.Mutable)
                {
                    var actualMirArgumentType =
                        MirTypeOf(mirArgumentValueId);
                    var hirArgumentSymbolId =
                        SymbolIdOfWholeArgument(hirArgument)
                        ?? throw MirLoweringError(
                            $"mutable array argument {argumentIndex} of "
                            + $"`{hirUserOperation.Name}` is not a binding");
                    var mirUpdatedArrayValueId =
                        AddInstructionResult(
                            mirCallInstructionId,
                            actualMirArgumentType,
                            hirArgumentSymbolId,
                            mirCallOrigin,
                            mirMutableArrayResults.Count);
                    mirMutableArrayResults.Add(
                        new MirMutableArrayResult(
                            argumentIndex,
                            mirUpdatedArrayValueId));
                    mirMutableArrayValueUpdates.Add((
                        hirArgumentSymbolId,
                        mirUpdatedArrayValueId));
                }
            }

            var mirUserOperationId =
                new MirCallableId(hirUserOperation.Id.Value);
            EmitMirQuantumApply(
                mirCallInstructionId,
                new MirUserCallableTarget(
                    mirUserOperationId),
                mirCallOperands,
                mirMutableArrayResults,
                LowerModifiersToMirFunctors(hirCallStatement.Modifiers),
                WrittenUserQubitOperandIndexes(hirUserOperation),
                mirCallOrigin);
            foreach (var (
                         hirSymbolId,
                         updatedMirArrayValueId) in mirMutableArrayValueUpdates)
            {
                _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    updatedMirArrayValueId;
            }
        }

        private MirValueId LowerPureCall(
            MirCallTarget mirCallTarget,
            HirCallable? hirUserFunction,
            HirCallExpression hirCall)
        {
            var mirCallOrigin = MirOriginFor(hirCall);
            IReadOnlyList<HirParameter>? hirUserFunctionParameters =
                hirUserFunction?.Parameters;
            var mirCallOperands =
                new List<MirCallOperand>(hirCall.Arguments.Count);
            for (var argumentIndex = 0;
                 argumentIndex < hirCall.Arguments.Count;
                 argumentIndex++)
            {
                var hirArgumentExpression =
                    hirCall.Arguments[argumentIndex].Expression;
                var mirArgumentValueId = LowerExpression(
                    hirArgumentExpression);
                if (hirUserFunctionParameters is not null)
                {
                    var hirParameter =
                        hirUserFunctionParameters[argumentIndex];
                    var expectedMirArgumentType =
                        hirParameter.IsArray
                        ? MirType.Array(
                            hirParameter.Type,
                            hirParameter.RegisterSize)
                        : MirType.Scalar(hirParameter.Type);
                    mirArgumentValueId = EnsureCallType(
                        mirArgumentValueId,
                        expectedMirArgumentType,
                        MirOriginFor(hirArgumentExpression));
                }
                mirCallOperands.Add(
                    new MirClassicalCallOperand(
                        mirArgumentValueId));
            }

            var mirCallResultType = mirCallTarget switch
            {
                MirUserCallableTarget
                    when hirUserFunction?.ReturnType
                        is { } hirReturnQType =>
                    MirType.Scalar(hirReturnQType),
                MirBuiltinFunctionTarget mirBuiltinTarget
                    when QoraGates.Functions.TryGetValue(
                        mirBuiltinTarget.Name,
                        out var builtinFunctionSignature) =>
                    MirType.Scalar(builtinFunctionSignature.Returns),
                _ => throw MirLoweringError(
                    $"pure target `{mirCallTarget.DisplayName}` has no return type"),
            };
            var mirCallInstructionId = AllocateMirInstructionId();
            var mirCallResultValueId = AddInstructionResult(
                mirCallInstructionId,
                mirCallResultType,
                null,
                mirCallOrigin);
            EmitMirInstruction(new MirPureCall(
                mirCallInstructionId,
                mirCallResultValueId,
                mirCallTarget,
                mirCallOperands,
                mirCallOrigin));
            return mirCallResultValueId;
        }

        private void LowerIf(HirIfStatement hirIf)
        {
            var mirOrigin = MirOriginFor(hirIf);
            var mirConditionValueId = LowerExpressionAs(
                hirIf.Condition,
                MirType.Scalar(QType.Bit));
            var mirBranchBlock = RequireCurrentMirBlock();
            var flowStateBeforeBranch = _currentFlowState.Clone();
            var mirThenBlock = CreateMirBlock(mirOrigin);
            var mirElseBlock = CreateMirBlock(mirOrigin);
            mirBranchBlock.Terminator = new MirBranch(
                mirConditionValueId,
                mirThenBlock.Id,
                Array.Empty<MirValueId>(),
                mirElseBlock.Id,
                Array.Empty<MirValueId>(),
                mirOrigin);

            var thenBranchExit = LowerScopedBranch(
                mirThenBlock,
                flowStateBeforeBranch,
                hirIf.Then.Statements);
            var elseBranchExit = LowerScopedBranch(
                mirElseBlock,
                flowStateBeforeBranch,
                hirIf.Else.Statements);

            if (thenBranchExit.ExitBlock is null
                && elseBranchExit.ExitBlock is null)
            {
                _currentMirBlockBuilder = null;
                _currentFlowState = flowStateBeforeBranch;
                return;
            }

            var mirMergeBlock = CreateMirBlock(mirOrigin);
            if (thenBranchExit.ExitBlock is null
                || elseBranchExit.ExitBlock is null)
            {
                var survivingBranchExit =
                    thenBranchExit.ExitBlock is not null
                        ? thenBranchExit
                        : elseBranchExit;
                survivingBranchExit.ExitBlock!.Terminator = new MirJump(
                    mirMergeBlock.Id,
                    Array.Empty<MirValueId>(),
                    mirOrigin);
                _currentMirBlockBuilder = mirMergeBlock;
                _currentFlowState = survivingBranchExit.ExitFlowState;
                return;
            }

            var mergedFlowState = flowStateBeforeBranch.Clone();
            var mirThenPhiArguments = new List<MirValueId>();
            var mirElsePhiArguments = new List<MirValueId>();
            foreach (var hirSymbolId in
                     flowStateBeforeBranch.CurrentMirValueByHirSymbolId.Keys.OrderBy(
                         id => id.Value))
            {
                if (!thenBranchExit.ExitFlowState
                        .CurrentMirValueByHirSymbolId.TryGetValue(
                        hirSymbolId,
                        out var mirThenValueId)
                    || !elseBranchExit.ExitFlowState
                        .CurrentMirValueByHirSymbolId.TryGetValue(
                        hirSymbolId,
                        out var mirElseValueId))
                {
                    continue;
                }
                if (mirThenValueId == mirElseValueId)
                {
                    mergedFlowState.CurrentMirValueByHirSymbolId[
                            hirSymbolId] =
                        mirThenValueId;
                    continue;
                }

                var mirValueType = MirTypeOf(mirThenValueId);
                if (MirTypeOf(mirElseValueId) != mirValueType)
                {
                    throw MirLoweringError(
                        $"branch values for symbol {hirSymbolId} "
                        + "have different MIR types");
                }
                var mirPhiValueId = AddBlockArgument(
                    mirMergeBlock,
                    mirValueType,
                    hirSymbolId,
                    mirOrigin);
                mirThenPhiArguments.Add(mirThenValueId);
                mirElsePhiArguments.Add(mirElseValueId);
                mergedFlowState.CurrentMirValueByHirSymbolId[hirSymbolId] =
                    mirPhiValueId;
            }

            MergeQubitStatesAtJoin(
                mergedFlowState,
                thenBranchExit.ExitFlowState,
                thenBranchExit.ExitBlock!,
                firstSuccessorOrdinal: 0,
                elseBranchExit.ExitFlowState,
                elseBranchExit.ExitBlock!,
                secondSuccessorOrdinal: 0,
                mirMergeBlock,
                mirOrigin);

            thenBranchExit.ExitBlock.Terminator = new MirJump(
                mirMergeBlock.Id,
                mirThenPhiArguments,
                mirOrigin);
            elseBranchExit.ExitBlock.Terminator = new MirJump(
                mirMergeBlock.Id,
                mirElsePhiArguments,
                mirOrigin);
            _currentMirBlockBuilder = mirMergeBlock;
            _currentFlowState = mergedFlowState;
        }

        private (
            MirBlockBuilder? ExitBlock,
            LoweringFlowState ExitFlowState) LowerScopedBranch(
            MirBlockBuilder mirBranchBlock,
            LoweringFlowState entryFlowState,
            IReadOnlyList<HirStatement> hirStatements)
        {
            _currentMirBlockBuilder = mirBranchBlock;
            _currentFlowState = entryFlowState.Clone();
            _currentFlowState.PushHirSymbolLifetimeFrame();
            LowerStatements(hirStatements);
            _currentFlowState.PopHirSymbolLifetimeFrame();
            return (_currentMirBlockBuilder, _currentFlowState);
        }

        private void LowerFor(HirForStatement hirFor)
        {
            var mirOrigin = MirOriginFor(hirFor);
            var mirRangeStartValueId = LowerExpressionAs(
                hirFor.From,
                MirType.Scalar(QType.Int));
            var mirRangeEndValueId = LowerExpressionAs(
                hirFor.To,
                MirType.Scalar(QType.Int));
            var mirStepValueId = EmitConstant(QType.Int, "1", mirOrigin);
            const bool isDescendingRange = false;
            var flowStateBeforeLoop = _currentFlowState.Clone();
            var mirPreheaderBlock = RequireCurrentMirBlock();
            var mirHeaderBlock = CreateMirBlock(mirOrigin);
            var mirBodyBlock = CreateMirBlock(mirOrigin);
            var mirExitBlock = CreateMirBlock(mirOrigin);

            var loopHeaderFlowState = flowStateBeforeLoop.Clone();
            var mirPreheaderArguments = new List<MirValueId>();
            foreach (var hirSymbolId in
                     flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Keys.OrderBy(
                         id => id.Value))
            {
                var mirInitialValueId =
                    flowStateBeforeLoop.CurrentMirValueByHirSymbolId[
                        hirSymbolId];
                var mirHeaderPhiValueId = AddBlockArgument(
                    mirHeaderBlock,
                    MirTypeOf(mirInitialValueId),
                    hirSymbolId,
                    mirOrigin);
                loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirHeaderPhiValueId;
                mirPreheaderArguments.Add(mirInitialValueId);
            }
            var loopQubitPhiBindings = CreateLoopQubitPhiBindings(
                flowStateBeforeLoop,
                loopHeaderFlowState,
                mirPreheaderBlock,
                preheaderSuccessorOrdinal: 0,
                mirHeaderBlock,
                mirOrigin);

            loopHeaderFlowState.PushHirSymbolLifetimeFrame();
            var hirLoopVariableSymbol = RequireHirDeclarationSymbol(
                hirFor.Id,
                $"loop variable `{hirFor.Variable}`");
            var mirLoopVariableValueId = AddBlockArgument(
                mirHeaderBlock,
                MirType.Scalar(QType.Int),
                hirLoopVariableSymbol.Id,
                mirOrigin);
            loopHeaderFlowState.TrackDeclaredHirSymbol(
                hirLoopVariableSymbol.Id);
            loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                    hirLoopVariableSymbol.Id] =
                mirLoopVariableValueId;
            mirPreheaderArguments.Add(mirRangeStartValueId);
            mirPreheaderBlock.Terminator = new MirJump(
                mirHeaderBlock.Id,
                mirPreheaderArguments,
                mirOrigin);

            _currentMirBlockBuilder = mirHeaderBlock;
            _currentFlowState = loopHeaderFlowState.Clone();
            var mirLoopConditionValueId = EmitBinary(
                isDescendingRange
                    ? MirBinaryOperator.GreaterOrEqual
                    : MirBinaryOperator.LessOrEqual,
                mirLoopVariableValueId,
                mirRangeEndValueId,
                MirType.Scalar(QType.Bit),
                mirOrigin);
            TerminateCurrentMirBlock(new MirBranch(
                mirLoopConditionValueId,
                mirBodyBlock.Id,
                Array.Empty<MirValueId>(),
                mirExitBlock.Id,
                Array.Empty<MirValueId>(),
                mirOrigin));

            _currentMirBlockBuilder = mirBodyBlock;
            _currentFlowState = loopHeaderFlowState.Clone();
            LowerStatements(hirFor.Body.Statements);
            if (_currentMirBlockBuilder is not null)
            {
                var currentMirLoopVariableValueId =
                    _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirLoopVariableSymbol.Id];
                var nextMirLoopVariableValueId = EmitBinary(
                    MirBinaryOperator.Add,
                    currentMirLoopVariableValueId,
                    mirStepValueId,
                    MirType.Scalar(QType.Int),
                    mirOrigin);
                var mirBackedgeArguments = new List<MirValueId>(
                    flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Count + 1);
                foreach (var hirSymbolId in
                         flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Keys
                             .OrderBy(
                             id => id.Value))
                {
                    mirBackedgeArguments.Add(
                        _currentFlowState.CurrentMirValueByHirSymbolId[
                            hirSymbolId]);
                }
                mirBackedgeArguments.Add(nextMirLoopVariableValueId);
                var mirBackedgeBlock = RequireCurrentMirBlock();
                AddLoopBackedgeInputs(
                    loopQubitPhiBindings,
                    _currentFlowState,
                    mirBackedgeBlock,
                    successorOrdinal: 0,
                    mirHeaderBlock);
                TerminateCurrentMirBlock(new MirJump(
                    mirHeaderBlock.Id,
                    mirBackedgeArguments,
                    mirOrigin));
            }

            var flowStateAfterLoop = loopHeaderFlowState.Clone();
            flowStateAfterLoop.PopHirSymbolLifetimeFrame();
            _currentMirBlockBuilder = mirExitBlock;
            _currentFlowState = flowStateAfterLoop;
        }

        private void LowerWhile(HirWhileStatement hirWhile)
        {
            var mirOrigin = MirOriginFor(hirWhile);
            var flowStateBeforeLoop = _currentFlowState.Clone();
            var mirPreheaderBlock = RequireCurrentMirBlock();
            var mirHeaderBlock = CreateMirBlock(mirOrigin);
            var mirBodyBlock = CreateMirBlock(mirOrigin);
            var mirExitBlock = CreateMirBlock(mirOrigin);
            var loopHeaderFlowState = flowStateBeforeLoop.Clone();
            var mirPreheaderArguments = new List<MirValueId>();
            foreach (var hirSymbolId in
                     flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Keys.OrderBy(
                         id => id.Value))
            {
                var mirInitialValueId =
                    flowStateBeforeLoop.CurrentMirValueByHirSymbolId[
                        hirSymbolId];
                var mirHeaderPhiValueId = AddBlockArgument(
                    mirHeaderBlock,
                    MirTypeOf(mirInitialValueId),
                    hirSymbolId,
                    mirOrigin);
                loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirHeaderPhiValueId;
                mirPreheaderArguments.Add(mirInitialValueId);
            }
            var loopQubitPhiBindings = CreateLoopQubitPhiBindings(
                flowStateBeforeLoop,
                loopHeaderFlowState,
                mirPreheaderBlock,
                preheaderSuccessorOrdinal: 0,
                mirHeaderBlock,
                mirOrigin);
            mirPreheaderBlock.Terminator = new MirJump(
                mirHeaderBlock.Id,
                mirPreheaderArguments,
                mirOrigin);

            _currentMirBlockBuilder = mirHeaderBlock;
            _currentFlowState = loopHeaderFlowState.Clone();
            var mirLoopConditionValueId = LowerExpressionAs(
                hirWhile.Condition,
                MirType.Scalar(QType.Bit));
            var flowStateAfterCondition = _currentFlowState.Clone();
            TerminateCurrentMirBlock(new MirBranch(
                mirLoopConditionValueId,
                mirBodyBlock.Id,
                Array.Empty<MirValueId>(),
                mirExitBlock.Id,
                Array.Empty<MirValueId>(),
                mirOrigin));

            _currentMirBlockBuilder = mirBodyBlock;
            _currentFlowState = flowStateAfterCondition.Clone();
            _currentFlowState.PushHirSymbolLifetimeFrame();
            LowerStatements(hirWhile.Body.Statements);
            _currentFlowState.PopHirSymbolLifetimeFrame();
            if (_currentMirBlockBuilder is not null)
            {
                var mirBackedgeArguments = new List<MirValueId>(
                    flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Count);
                foreach (var hirSymbolId in
                         flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Keys
                             .OrderBy(
                             id => id.Value))
                {
                    mirBackedgeArguments.Add(
                        _currentFlowState.CurrentMirValueByHirSymbolId[
                            hirSymbolId]);
                }
                var mirBackedgeBlock = RequireCurrentMirBlock();
                AddLoopBackedgeInputs(
                    loopQubitPhiBindings,
                    _currentFlowState,
                    mirBackedgeBlock,
                    successorOrdinal: 0,
                    mirHeaderBlock);
                TerminateCurrentMirBlock(new MirJump(
                    mirHeaderBlock.Id,
                    mirBackedgeArguments,
                    mirOrigin));
            }

            _currentMirBlockBuilder = mirExitBlock;
            _currentFlowState = flowStateAfterCondition;
        }

        private void LowerRepeat(HirRepeatStatement hirRepeat)
        {
            var mirOrigin = MirOriginFor(hirRepeat);
            var flowStateBeforeLoop = _currentFlowState.Clone();
            var mirPreheaderBlock = RequireCurrentMirBlock();
            var mirHeaderBlock = CreateMirBlock(mirOrigin);
            var loopHeaderFlowState = flowStateBeforeLoop.Clone();
            var mirPreheaderArguments = new List<MirValueId>();
            foreach (var hirSymbolId in
                     flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Keys.OrderBy(
                         id => id.Value))
            {
                var mirInitialValueId =
                    flowStateBeforeLoop.CurrentMirValueByHirSymbolId[
                        hirSymbolId];
                var mirHeaderPhiValueId = AddBlockArgument(
                    mirHeaderBlock,
                    MirTypeOf(mirInitialValueId),
                    hirSymbolId,
                    mirOrigin);
                loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirHeaderPhiValueId;
                mirPreheaderArguments.Add(mirInitialValueId);
            }
            var loopQubitPhiBindings = CreateLoopQubitPhiBindings(
                flowStateBeforeLoop,
                loopHeaderFlowState,
                mirPreheaderBlock,
                preheaderSuccessorOrdinal: 0,
                mirHeaderBlock,
                mirOrigin);
            mirPreheaderBlock.Terminator = new MirJump(
                mirHeaderBlock.Id,
                mirPreheaderArguments,
                mirOrigin);

            _currentMirBlockBuilder = mirHeaderBlock;
            _currentFlowState = loopHeaderFlowState.Clone();
            _currentFlowState.PushHirSymbolLifetimeFrame();
            LowerStatements(hirRepeat.Body.Statements);
            if (_currentMirBlockBuilder is null)
            {
                _currentFlowState.PopHirSymbolLifetimeFrame();
                _currentFlowState = flowStateBeforeLoop;
                return;
            }

            var mirUntilConditionValueId = LowerExpressionAs(
                hirRepeat.Until,
                MirType.Scalar(QType.Bit));
            _currentFlowState.PopHirSymbolLifetimeFrame();

            var mirExitBlock = CreateMirBlock(mirOrigin);
            var flowStateAfterLoop = flowStateBeforeLoop.Clone();
            var mirExitArguments = new List<MirValueId>();
            var mirBackedgeArguments = new List<MirValueId>();
            foreach (var hirSymbolId in
                     flowStateBeforeLoop.CurrentMirValueByHirSymbolId.Keys.OrderBy(
                         id => id.Value))
            {
                var currentMirValueId =
                    _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId];
                var mirExitPhiValueId = AddBlockArgument(
                    mirExitBlock,
                    MirTypeOf(currentMirValueId),
                    hirSymbolId,
                    mirOrigin);
                flowStateAfterLoop.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirExitPhiValueId;
                mirExitArguments.Add(currentMirValueId);
                mirBackedgeArguments.Add(currentMirValueId);
            }
            var mirBackedgeBlock = RequireCurrentMirBlock();
            AddLoopBackedgeInputs(
                loopQubitPhiBindings,
                _currentFlowState,
                mirBackedgeBlock,
                successorOrdinal: 1,
                mirHeaderBlock);
            foreach (var hirSymbolId in
                     flowStateBeforeLoop.CurrentMirQubitByHirSymbolId.Keys)
            {
                flowStateAfterLoop.CurrentMirQubitByHirSymbolId[
                        hirSymbolId] =
                    _currentFlowState.CurrentMirQubitByHirSymbolId[
                        hirSymbolId];
            }
            TerminateCurrentMirBlock(new MirBranch(
                mirUntilConditionValueId,
                mirExitBlock.Id,
                mirExitArguments,
                mirHeaderBlock.Id,
                mirBackedgeArguments,
                mirOrigin));
            _currentMirBlockBuilder = mirExitBlock;
            _currentFlowState = flowStateAfterLoop;
        }

        private void LowerReturn(HirReturnStatement hirReturn)
        {
            var mirOrigin = MirOriginFor(hirReturn);
            var mirReturnValueId = _hirCallable.ReturnType is { } hirReturnType
                ? LowerExpressionAs(
                    hirReturn.Value,
                    MirType.Scalar(hirReturnType))
                : LowerExpression(hirReturn.Value);
            TerminateCurrentMirBlock(
                new MirReturn(mirReturnValueId, mirOrigin));
        }

        private MirValueId LowerMeasurement(
            HirMeasurementExpression hirMeasurement)
        {
            var mirOrigin = MirOriginFor(hirMeasurement);
            var mirMeasuredQubitAccess = LowerQubitAccess(
                hirMeasurement.Target);
            var mirMeasurementInstructionId = AllocateMirInstructionId();
            var mirMeasurementResultValueId = AddInstructionResult(
                mirMeasurementInstructionId,
                MirType.Scalar(QType.Bit),
                null,
                mirOrigin);
            var mirQubitAfterMeasurement = CreateQubitAfterInstruction(
                mirMeasuredQubitAccess.Qubit.Id,
                mirOrigin);
            EmitMirInstruction(new MirMeasure(
                mirMeasurementInstructionId,
                mirMeasurementResultValueId,
                mirMeasuredQubitAccess,
                mirQubitAfterMeasurement,
                mirOrigin));
            UpdateCurrentQubitAfterWrite(mirQubitAfterMeasurement);
            return mirMeasurementResultValueId;
        }

        private MirValueId LowerExpression(HirExpression hirExpression)
        {
            // Expression lowering owns its provenance boundary. Callers cannot substitute a surrounding
            // statement origin, so every emitted MIR term remains queryable by the exact HIR expression ID.
            var mirOrigin = MirOriginFor(hirExpression);
            switch (hirExpression)
            {
                case HirMissingExpression:
                    throw MirLoweringError(
                        "a missing expression reached MIR lowering");

                case HirIntegerLiteralExpression hirIntegerLiteral:
                    return EmitConstant(QType.Int, hirIntegerLiteral.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), mirOrigin);

                case HirLiteralExpression hirLiteral:
                    return EmitConstant(
                        QTypeOfLiteralText(hirLiteral.Text),
                        hirLiteral.Text,
                        mirOrigin);

                case HirNameExpression hirName
                    when IsBuiltinLiteralName(hirName.Name):
                    return EmitConstant(
                        QTypeOfLiteralText(hirName.Name),
                        hirName.Name,
                        mirOrigin);

                case HirNameExpression hirName:
                {
                    var hirSymbolId = SymbolIdOf(
                        hirName,
                        $"classical name `{hirName.Name}`");
                    if (!_currentFlowState.CurrentMirValueByHirSymbolId
                        .TryGetValue(
                            hirSymbolId,
                            out var mirValueId))
                    {
                        throw MirLoweringError(
                            $"name `{hirName.Name}` does not denote a classical SSA value");
                    }

                    return mirValueId;
                }

                case HirUnaryExpression hirUnary:
                {
                    var mirOperandValueId = LowerExpression(hirUnary.Operand);
                    if (hirUnary.Operator == HirUnaryOperator.LogicalNot)
                    {
                        mirOperandValueId = EnsureType(
                            mirOperandValueId,
                            MirType.Scalar(QType.Bit),
                            mirOrigin);
                        return EmitUnary(
                            MirUnaryOperator.LogicalNot,
                            mirOperandValueId,
                            MirType.Scalar(QType.Bit),
                            mirOrigin);
                    }
                    if (hirUnary.Operator != HirUnaryOperator.Negate)
                    {
                        throw MirLoweringError(
                            $"unsupported unary operator `{hirUnary.Operator}`");
                    }

                    var mirOperandType = MirTypeOf(mirOperandValueId);
                    var mirResultType = mirOperandType.ElementType == QType.Bit
                        ? MirType.Scalar(QType.Int)
                        : mirOperandType;
                    if (mirOperandType.ElementType == QType.Bit)
                    {
                        mirOperandValueId = EnsureType(
                            mirOperandValueId,
                            mirResultType,
                            mirOrigin);
                    }

                    return EmitUnary(
                        MirUnaryOperator.Negate,
                        mirOperandValueId,
                        mirResultType,
                        mirOrigin);
                }

                case HirBinaryExpression hirBinary:
                {
                    if (hirBinary.Operator is
                        HirBinaryOperator.LogicalAnd or HirBinaryOperator.LogicalOr)
                    {
                        return LowerShortCircuit(hirBinary);
                    }

                    var mirLeftValueId = LowerExpression(hirBinary.Left);
                    var mirRightValueId = LowerExpression(hirBinary.Right);
                    var mirBinaryOperator =
                        MirBinaryOperatorOf(hirBinary.Operator);
                    var mirLeftType = MirTypeOf(mirLeftValueId);
                    var mirRightType = MirTypeOf(mirRightValueId);
                    if (mirLeftType.IsArray || mirRightType.IsArray)
                    {
                        if (mirBinaryOperator is not (
                                MirBinaryOperator.Equal
                                or MirBinaryOperator.NotEqual)
                            || mirLeftType != mirRightType)
                        {
                            throw MirLoweringError(
                                "validated array comparison has incompatible "
                                + $"operands {mirLeftType} and {mirRightType}");
                        }

                        return EmitBinary(
                            mirBinaryOperator,
                            mirLeftValueId,
                            mirRightValueId,
                            MirType.Scalar(QType.Bit),
                            mirOrigin);
                    }

                    var isComparison = mirBinaryOperator is
                        MirBinaryOperator.Equal or MirBinaryOperator.NotEqual
                        or MirBinaryOperator.Less or MirBinaryOperator.LessOrEqual
                        or MirBinaryOperator.Greater or MirBinaryOperator.GreaterOrEqual;
                    var mirOperandType = CommonMirNumericType(
                        mirLeftType,
                        mirRightType,
                        isComparison);
                    mirLeftValueId = EnsureType(
                        mirLeftValueId,
                        mirOperandType,
                        mirOrigin);
                    mirRightValueId = EnsureType(
                        mirRightValueId,
                        mirOperandType,
                        mirOrigin);
                    var mirResultType = isComparison
                        ? MirType.Scalar(QType.Bit)
                        : mirOperandType;
                    return EmitBinary(
                        mirBinaryOperator,
                        mirLeftValueId,
                        mirRightValueId,
                        mirResultType,
                        mirOrigin);
                }

                case HirMemberAccessExpression
                {
                    Receiver: { } hirReceiver,
                    MemberName: "Count",
                }:
                {
                    if (hirReceiver is HirNameExpression hirReceiverName)
                    {
                        var hirReceiverSymbolId = SymbolIdOf(
                            hirReceiverName,
                            $"Count receiver `{hirReceiverName.Name}`");
                        if (_currentFlowState.CurrentMirQubitByHirSymbolId
                            .TryGetValue(
                                hirReceiverSymbolId,
                                out var currentMirQubit))
                        {
                            var (
                                isQubitArray,
                                knownQubitArrayLength) =
                                MirQubitShapeOf(currentMirQubit.Id);
                            if (!isQubitArray)
                            {
                                throw MirLoweringError(
                                    $"Count receiver `{hirReceiverName.Name}` "
                                    + "is a scalar qubit");
                            }

                            if (knownQubitArrayLength
                                is not int concreteQubitArrayLength)
                            {
                                throw MirLoweringError(
                                    "specialized qubit array "
                                    + $"`{hirReceiverName.Name}` has no known length");
                            }

                            return EmitConstant(
                                QType.Int,
                                concreteQubitArrayLength.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                                mirOrigin);
                        }

                        if (!_currentFlowState.CurrentMirValueByHirSymbolId
                            .TryGetValue(
                                hirReceiverSymbolId,
                                out var mirClassicalArrayValueId))
                        {
                            throw MirLoweringError(
                                $"Count receiver `{hirReceiverName.Name}` "
                                + "is neither a classical array nor a qubit");
                        }

                        return EmitArrayLength(
                            mirClassicalArrayValueId,
                            hirReceiverName.Name,
                            mirOrigin);
                    }

                    var mirArrayValueId = LowerExpression(hirReceiver);
                    return EmitArrayLength(
                        mirArrayValueId,
                        HirExpressions.Render(hirReceiver),
                        mirOrigin);
                }

                case HirMemberAccessExpression hirMemberAccess:
                    throw MirLoweringError(
                        $"unsupported member `{hirMemberAccess.MemberName}` "
                        + "reached MIR lowering");

                case HirIndexExpression hirIndex:
                {
                    var mirArrayValueId = LowerExpression(hirIndex.Receiver);
                    var mirArrayType = MirTypeOf(mirArrayValueId);
                    if (!mirArrayType.IsArray)
                    {
                        throw MirLoweringError(
                            "an indexed classical expression does not have array type");
                    }

                    var mirIndexValueId = LowerExpressionAs(
                        hirIndex.Index,
                        MirType.Scalar(QType.Int));
                    var mirInstructionId = AllocateMirInstructionId();
                    var mirResultValueId = AddInstructionResult(
                        mirInstructionId,
                        MirType.Scalar(mirArrayType.ElementType),
                        null,
                        mirOrigin);
                    EmitMirInstruction(new MirArrayLoad(
                        mirInstructionId,
                        mirResultValueId,
                        mirArrayValueId,
                        mirIndexValueId,
                        mirOrigin));
                    return mirResultValueId;
                }

                case HirCallExpression hirCall:
                {
                    var semanticCallTargetSymbol =
                        RequireHirCallTargetSymbol(hirCall);
                    if (semanticCallTargetSymbol.Kind
                        == SymbolKind.BuiltinFunction)
                    {
                        return LowerPureCall(
                            new MirBuiltinFunctionTarget(
                                semanticCallTargetSymbol.SourceName),
                            hirUserFunction: null,
                            hirCall);
                    }
                    if (semanticCallTargetSymbol.Kind
                        != SymbolKind.Callable)
                    {
                        throw MirLoweringError(
                            $"expression call `{hirCall.Name}` targets semantic symbol kind "
                            + semanticCallTargetSymbol.Kind);
                    }
                    var hirUserFunction = RequireHirCallableDeclaration(
                        semanticCallTargetSymbol);
                    if (!hirUserFunction.IsFunction)
                    {
                        throw MirLoweringError(
                            $"expression call `{hirCall.Name}` targets an operation");
                    }
                    var mirUserFunctionId =
                        new MirCallableId(hirUserFunction.Id.Value);
                    return LowerPureCall(
                        new MirUserCallableTarget(
                            mirUserFunctionId),
                        hirUserFunction,
                        hirCall);
                }

                case HirMeasurementExpression hirMeasurement:
                    return LowerMeasurement(hirMeasurement);

                case HirArrayLiteralExpression or HirArrayCreationExpression:
                    throw MirLoweringError(
                        "an array value escaped its declaration during MIR lowering");

                default:
                    throw MirLoweringError(
                        "unsupported HIR expression node "
                        + hirExpression.GetType().Name);
            }
        }

        private MirValueId LowerExpressionAs(
            HirExpression hirExpression,
            MirType expectedMirType) =>
            EnsureType(
                LowerExpression(hirExpression),
                expectedMirType,
                MirOriginFor(hirExpression));

        /// <summary>
        /// Lowers logical conjunction/disjunction as control flow so the right operand is evaluated
        /// only on the edge which needs it. The merge block argument is the SSA result of the logical
        /// expression: the short-circuit edge supplies the already-known left value, while the other
        /// edge supplies the evaluated right value.
        /// </summary>
        private MirValueId LowerShortCircuit(
            HirBinaryExpression hirBinary)
        {
            var mirOrigin = MirOriginFor(hirBinary);
            var mirLeftValueId = LowerExpressionAs(
                hirBinary.Left,
                MirType.Scalar(QType.Bit));
            var mirBranchBlock = RequireCurrentMirBlock();
            var flowStateBeforeRightOperand = _currentFlowState.Clone();
            var mirRightOperandBlock = CreateMirBlock(mirOrigin);
            var mirMergeBlock = CreateMirBlock(mirOrigin);
            var mirResultValueId = AddBlockArgument(
                mirMergeBlock,
                MirType.Scalar(QType.Bit),
                hirSymbolId: null,
                mirOrigin);

            if (hirBinary.Operator == HirBinaryOperator.LogicalAnd)
            {
                mirBranchBlock.Terminator = new MirBranch(
                    mirLeftValueId,
                    mirRightOperandBlock.Id,
                    Array.Empty<MirValueId>(),
                    mirMergeBlock.Id,
                    new[] { mirLeftValueId },
                    mirOrigin);
            }
            else
            {
                mirBranchBlock.Terminator = new MirBranch(
                    mirLeftValueId,
                    mirMergeBlock.Id,
                    new[] { mirLeftValueId },
                    mirRightOperandBlock.Id,
                    Array.Empty<MirValueId>(),
                    mirOrigin);
            }

            _currentMirBlockBuilder = mirRightOperandBlock;
            _currentFlowState = flowStateBeforeRightOperand.Clone();
            var mirRightValueId = LowerExpressionAs(
                hirBinary.Right,
                MirType.Scalar(QType.Bit));
            var mirRightExitBlock = RequireCurrentMirBlock();
            var flowStateAfterRightOperand = _currentFlowState.Clone();
            TerminateCurrentMirBlock(new MirJump(
                mirMergeBlock.Id,
                new[] { mirRightValueId },
                mirOrigin));

            var mergedFlowState = flowStateBeforeRightOperand.Clone();
            MergeQubitStatesAtJoin(
                mergedFlowState,
                flowStateBeforeRightOperand,
                mirBranchBlock,
                firstSuccessorOrdinal:
                    hirBinary.Operator == HirBinaryOperator.LogicalAnd ? 1 : 0,
                flowStateAfterRightOperand,
                mirRightExitBlock,
                secondSuccessorOrdinal: 0,
                mirMergeBlock,
                mirOrigin);
            _currentMirBlockBuilder = mirMergeBlock;
            _currentFlowState = mergedFlowState;
            return mirResultValueId;
        }

        private MirQubitAccess LowerQubitAccess(
            HirArgument hirArgument) =>
            LowerQubitAccess(hirArgument.Expression);

        private MirQubitAccess LowerQubitAccess(
            HirExpression hirExpression)
        {
            switch (hirExpression)
            {
                case HirNameExpression hirName:
                {
                    var hirSymbolId = SymbolIdOf(
                        hirName,
                        $"qubit name `{hirName.Name}`");
                    if (!_currentFlowState.CurrentMirQubitByHirSymbolId
                        .TryGetValue(
                            hirSymbolId,
                            out var currentMirQubit))
                    {
                        throw MirLoweringError(
                            $"name `{hirName.Name}` does not denote a qubit");
                    }

                    return new MirQubitAccess(
                        currentMirQubit,
                        origin: MirOriginFor(hirExpression));
                }
                case HirIndexExpression
                {
                    Receiver: HirNameExpression hirReceiverName,
                    Index: { } hirIndex,
                }:
                {
                    var hirSymbolId = SymbolIdOf(
                        hirReceiverName,
                        $"qubit name `{hirReceiverName.Name}`");
                    if (!_currentFlowState.CurrentMirQubitByHirSymbolId
                        .TryGetValue(
                            hirSymbolId,
                            out var currentMirQubit))
                    {
                        throw MirLoweringError(
                            $"name `{hirReceiverName.Name}` does not denote a qubit");
                    }

                    var mirIndexValueId = LowerExpressionAs(
                        hirIndex,
                        MirType.Scalar(QType.Int));
                    return new MirQubitAccess(
                        currentMirQubit,
                        mirIndexValueId,
                        MirOriginFor(hirExpression));
                }
                default:
                    throw MirLoweringError(
                        $"`{HirExpressions.Render(hirExpression)}` "
                        + "is not a qubit access");
            }
        }

        private MirValueId EmitConstant(
            QType scalarType,
            string literalText,
            MirOrigin mirOrigin)
        {
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValueId = AddInstructionResult(
                mirInstructionId,
                MirType.Scalar(scalarType),
                null,
                mirOrigin);
            EmitMirInstruction(new MirConstant(
                mirInstructionId,
                mirResultValueId,
                literalText,
                mirOrigin));
            return mirResultValueId;
        }

        private MirValueId EmitArrayLength(
            MirValueId mirArrayValueId,
            string hirReceiverDisplayName,
            MirOrigin mirOrigin)
        {
            if (!MirTypeOf(mirArrayValueId).IsArray)
            {
                throw MirLoweringError(
                    $"Count receiver `{hirReceiverDisplayName}` "
                    + "is not a classical array");
            }

            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValueId = AddInstructionResult(
                mirInstructionId,
                MirType.Scalar(QType.Int),
                hirSymbolId: null,
                mirOrigin);
            EmitMirInstruction(new MirArrayLength(
                mirInstructionId,
                mirResultValueId,
                mirArrayValueId,
                mirOrigin));
            return mirResultValueId;
        }

        private MirValueId EmitUnary(
            MirUnaryOperator mirOperator,
            MirValueId mirOperandValueId,
            MirType mirResultType,
            MirOrigin mirOrigin)
        {
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValueId = AddInstructionResult(
                mirInstructionId,
                mirResultType,
                null,
                mirOrigin);
            EmitMirInstruction(new MirUnary(
                mirInstructionId,
                mirResultValueId,
                mirOperator,
                mirOperandValueId,
                mirOrigin));
            return mirResultValueId;
        }

        private MirValueId EmitBinary(
            MirBinaryOperator mirOperator,
            MirValueId mirLeftValueId,
            MirValueId mirRightValueId,
            MirType mirResultType,
            MirOrigin mirOrigin)
        {
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValueId = AddInstructionResult(
                mirInstructionId,
                mirResultType,
                null,
                mirOrigin);
            EmitMirInstruction(new MirBinary(
                mirInstructionId,
                mirResultValueId,
                mirOperator,
                mirLeftValueId,
                mirRightValueId,
                mirOrigin));
            return mirResultValueId;
        }

        private MirValueId EnsureCallType(
            MirValueId mirValueId,
            MirType expectedMirType,
            MirOrigin mirOrigin)
        {
            var actualMirType = MirTypeOf(mirValueId);
            if (actualMirType == expectedMirType)
                return mirValueId;
            if (actualMirType.IsArray
                && expectedMirType.IsArray
                && actualMirType.ElementType == expectedMirType.ElementType
                && (expectedMirType.KnownLength is null
                    || actualMirType.KnownLength
                    == expectedMirType.KnownLength))
            {
                return mirValueId;
            }
            return EnsureType(mirValueId, expectedMirType, mirOrigin);
        }

        private MirValueId EnsureType(
            MirValueId mirValueId,
            MirType expectedMirType,
            MirOrigin mirOrigin)
        {
            var actualMirType = MirTypeOf(mirValueId);
            if (actualMirType == expectedMirType)
                return mirValueId;
            if (actualMirType.IsArray || expectedMirType.IsArray)
            {
                throw MirLoweringError(
                    $"MIR lowering cannot convert {actualMirType} "
                    + $"to {expectedMirType}");
            }
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValueId = AddInstructionResult(
                mirInstructionId,
                expectedMirType,
                null,
                mirOrigin);
            EmitMirInstruction(new MirConvert(
                mirInstructionId,
                mirResultValueId,
                mirValueId,
                mirOrigin));
            return mirResultValueId;
        }

        private MirValueId AddInstructionResult(
            MirInstructionId mirInstructionId,
            MirType mirType,
            SymbolId? hirSymbolId,
            MirOrigin mirOrigin,
            int mirResultIndex = 0) =>
            AddValue(
                mirType,
                MirValueDefinition.InstructionResultAt(
                    mirInstructionId,
                    mirResultIndex),
                hirSymbolId,
                mirOrigin);

        private MirValueId AddBlockArgument(
            MirBlockBuilder mirBlockBuilder,
            MirType mirType,
            SymbolId? hirSymbolId,
            MirOrigin mirOrigin)
        {
            var mirBlockArgumentIndex = mirBlockBuilder.Arguments.Count;
            var mirValueId = AddValue(
                mirType,
                MirValueDefinition.BlockArgumentAt(
                    mirBlockBuilder.Id,
                    mirBlockArgumentIndex),
                hirSymbolId,
                mirOrigin);
            mirBlockBuilder.Arguments.Add(mirValueId);
            return mirValueId;
        }

        private MirValueId AddValue(
            MirType mirType,
            MirValueDefinition mirValueDefinition,
            SymbolId? hirSymbolId,
            MirOrigin mirOrigin)
        {
            var mirValueId = new MirValueId(_mirValues.Count);
            _mirValues.Add(new MirValue(
                mirValueId,
                mirType,
                mirValueDefinition,
                mirOrigin));
            if (hirSymbolId is SymbolId sourceHirSymbolId)
            {
                _traceSink?.LinkValue(
                    sourceHirSymbolId,
                    _mirCallableId,
                    mirValueId);
            }
            return mirValueId;
        }

        private void TraceMirValueForHirSymbol(
            MirValueId mirValueId,
            SymbolId hirSymbolId) =>
            _traceSink?.LinkValue(
                hirSymbolId,
                _mirCallableId,
                mirValueId);

        private MirType MirTypeOf(MirValueId mirValueId) =>
            _mirValues[mirValueId.Value].Type;

        private void EmitMirQuantumApply(
            MirInstructionId mirInstructionId,
            MirCallTarget mirCallTarget,
            IReadOnlyList<MirCallOperand> mirCallOperands,
            IReadOnlyList<MirMutableArrayResult> mirMutableArrayResults,
            IReadOnlyList<MirFunctor> mirFunctors,
            IReadOnlyList<int> writtenQubitOperandIndexes,
            MirOrigin mirOrigin)
        {
            var mirQubitResults =
                new List<MirQubitAfterInstruction>();
            var writtenMirQubitIds = new HashSet<MirQubitId>();
            foreach (var operandIndex in writtenQubitOperandIndexes)
            {
                if (operandIndex < 0
                    || operandIndex >= mirCallOperands.Count)
                {
                    throw MirLoweringError(
                        $"quantum write operand {operandIndex} "
                        + "is outside the operand list");
                }

                if (mirCallOperands[operandIndex]
                    is not MirQubitCallOperand mirQubitOperand)
                {
                    throw MirLoweringError(
                        $"quantum write operand {operandIndex} is not a qubit");
                }

                var mirQubitId = mirQubitOperand.Qubit.Qubit.Id;
                if (!writtenMirQubitIds.Add(mirQubitId))
                    continue; // One register version covers all elements written by this instruction.

                mirQubitResults.Add(
                    CreateQubitAfterInstruction(mirQubitId, mirOrigin));
            }

            EmitMirInstruction(new MirQuantumApply(
                mirInstructionId,
                mirCallTarget,
                mirCallOperands,
                mirQubitResults,
                mirMutableArrayResults,
                mirFunctors,
                mirOrigin));

            foreach (var mirQubitResult in mirQubitResults)
                UpdateCurrentQubitAfterWrite(mirQubitResult);
        }

        private IReadOnlyList<int> WrittenBuiltinQubitOperandIndexes(
            HirCallStatement hirCallStatement,
            string builtinGateName)
        {
            var hirCall = hirCallStatement.Call;
            if (!QoraGates.Gates.TryGetValue(
                    builtinGateName,
                    out var builtinGateInfo))
            {
                throw MirLoweringError(
                    $"built-in gate `{builtinGateName}` has no effect metadata");
            }

            var firstQubitOperandIndex =
                builtinGateInfo.AngleFirst ? 1 : 0;
            var qubitOperandIndexes = Enumerable.Range(
                firstQubitOperandIndex,
                hirCall.Arguments.Count - firstQubitOperandIndex).ToList();
            if (!builtinGateInfo.Unitary)
                return qubitOperandIndexes;
            if (builtinGateInfo.Diagonal)
                return Array.Empty<int>();

            var controlQubitCount = builtinGateInfo.Controls
                + hirCallStatement.Modifiers.Count(
                    modifier => modifier == QGateModifier.Controlled);
            if (controlQubitCount > qubitOperandIndexes.Count)
            {
                throw MirLoweringError(
                    $"built-in gate `{builtinGateName}` declares more controls "
                    + "than qubit operands");
            }

            return qubitOperandIndexes.Skip(controlQubitCount).ToArray();
        }

        private IReadOnlyList<int> WrittenUserQubitOperandIndexes(
            HirCallable hirUserOperation)
        {
            var hirOperationEffects =
                _finalHirArtifact.Model.FindOpEffects(hirUserOperation.Id)
                ?? throw MirLoweringError(
                    $"callable `{hirUserOperation.Name}` "
                    + "has no HIR effect summary");
            var writtenQubitOperandIndexes = new List<int>();
            for (var hirParameterIndex = 0;
                 hirParameterIndex < hirUserOperation.Parameters.Count;
                 hirParameterIndex++)
            {
                var hirParameter =
                    hirUserOperation.Parameters[hirParameterIndex];
                if (hirParameter.Type == QType.Qubit
                    && hirOperationEffects.ParamModified.Any(
                        modifiedParameterReference =>
                        string.Equals(
                            modifiedParameterReference.Reg,
                            hirParameter.Name,
                            StringComparison.Ordinal)))
                {
                    writtenQubitOperandIndexes.Add(hirParameterIndex);
                }
            }

            return writtenQubitOperandIndexes;
        }

        private void RegisterInitialMirQubit(
            SymbolId hirSymbolId,
            MirQubit initialMirQubit)
        {
            if (initialMirQubit.Version.Value != 0)
            {
                throw MirLoweringError(
                    $"initial qubit {initialMirQubit.Id} "
                    + "does not have version zero");
            }
            if (!_initialMirQubitsById.TryAdd(
                    initialMirQubit.Id,
                    initialMirQubit))
            {
                throw MirLoweringError(
                    $"qubit identity {initialMirQubit.Id} "
                    + "was allocated more than once");
            }
            _nextMirQubitVersionById.Add(initialMirQubit.Id, 1);
            _traceSink?.LinkQubit(
                hirSymbolId,
                _mirCallableId,
                initialMirQubit.Key);
        }

        private MirQubitAfterInstruction CreateQubitAfterInstruction(
            MirQubitId mirQubitId,
            MirOrigin mirOrigin)
        {
            if (!_nextMirQubitVersionById.TryGetValue(
                    mirQubitId,
                    out var nextMirQubitVersionValue))
            {
                throw MirLoweringError(
                    $"qubit identity {mirQubitId} has no initial definition");
            }
            _nextMirQubitVersionById[mirQubitId] =
                checked(nextMirQubitVersionValue + 1);
            return new MirQubitAfterInstruction(
                mirQubitId,
                new MirQubitVersion(nextMirQubitVersionValue),
                mirOrigin);
        }

        private MirQubitPhi CreateQubitPhi(
            SymbolId hirSymbolId,
            MirQubitId mirQubitId,
            MirBlockBuilder mirJoinBlock,
            IReadOnlyList<MirQubitPhiInput> mirPhiInputs,
            MirOrigin mirOrigin)
        {
            if (!_nextMirQubitVersionById.TryGetValue(
                    mirQubitId,
                    out var nextMirQubitVersionValue))
            {
                throw MirLoweringError(
                    $"qubit identity {mirQubitId} has no initial definition");
            }
            _nextMirQubitVersionById[mirQubitId] =
                checked(nextMirQubitVersionValue + 1);
            var mirQubitPhi = new MirQubitPhi(
                mirQubitId,
                new MirQubitVersion(nextMirQubitVersionValue),
                mirPhiInputs,
                mirOrigin);
            mirJoinBlock.QubitPhis.Add(mirQubitPhi);
            _traceSink?.LinkQubit(
                hirSymbolId,
                _mirCallableId,
                mirQubitPhi.Key);
            return mirQubitPhi;
        }

        private void MergeQubitStatesAtJoin(
            LoweringFlowState mergedFlowState,
            LoweringFlowState firstPredecessorFlowState,
            MirBlockBuilder firstPredecessorBlock,
            int firstSuccessorOrdinal,
            LoweringFlowState secondPredecessorFlowState,
            MirBlockBuilder secondPredecessorBlock,
            int secondSuccessorOrdinal,
            MirBlockBuilder mirJoinBlock,
            MirOrigin mirOrigin)
        {
            var mirQubitAliasGroups =
                mergedFlowState.CurrentMirQubitByHirSymbolId
                .OrderBy(pair => pair.Key.Value)
                .GroupBy(pair => pair.Value.Id)
                .Select(group => (
                    MirQubitId: group.Key,
                    HirSymbolIds: group.Select(pair => pair.Key).ToArray()))
                .ToArray();

            foreach (var mirQubitAliasGroup in mirQubitAliasGroups)
            {
                var firstIncomingMirQubit = RequireIncomingMirQubit(
                    firstPredecessorFlowState,
                    mirQubitAliasGroup.MirQubitId,
                    mirQubitAliasGroup.HirSymbolIds);
                var secondIncomingMirQubit = RequireIncomingMirQubit(
                    secondPredecessorFlowState,
                    mirQubitAliasGroup.MirQubitId,
                    mirQubitAliasGroup.HirSymbolIds);
                if (firstIncomingMirQubit.Key == secondIncomingMirQubit.Key)
                {
                    foreach (var hirSymbolId
                             in mirQubitAliasGroup.HirSymbolIds)
                    {
                        mergedFlowState.CurrentMirQubitByHirSymbolId[
                                hirSymbolId] =
                            firstIncomingMirQubit;
                    }
                    continue;
                }

                var mirQubitPhi = CreateQubitPhi(
                    mirQubitAliasGroup.HirSymbolIds[0],
                    mirQubitAliasGroup.MirQubitId,
                    mirJoinBlock,
                    new[]
                    {
                        new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                firstPredecessorBlock.Id,
                                firstSuccessorOrdinal),
                            firstIncomingMirQubit.Key),
                        new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                secondPredecessorBlock.Id,
                                secondSuccessorOrdinal),
                            secondIncomingMirQubit.Key),
                    },
                    mirOrigin);
                foreach (var hirSymbolId
                         in mirQubitAliasGroup.HirSymbolIds)
                {
                    mergedFlowState.CurrentMirQubitByHirSymbolId[
                            hirSymbolId] =
                        mirQubitPhi;
                    if (hirSymbolId
                        != mirQubitAliasGroup.HirSymbolIds[0])
                    {
                        _traceSink?.LinkQubit(
                            hirSymbolId,
                            _mirCallableId,
                            mirQubitPhi.Key);
                    }
                }
            }

            MirQubit RequireIncomingMirQubit(
                LoweringFlowState predecessorFlowState,
                MirQubitId expectedMirQubitId,
                IReadOnlyList<SymbolId> hirSymbolIds)
            {
                if (!predecessorFlowState.CurrentMirQubitByHirSymbolId
                        .TryGetValue(
                            hirSymbolIds[0],
                            out var incomingMirQubit)
                    || incomingMirQubit.Id != expectedMirQubitId)
                {
                    throw MirLoweringError(
                        "join predecessor has no current state for qubit "
                        + expectedMirQubitId);
                }
                foreach (var hirSymbolId in hirSymbolIds)
                {
                    if (!predecessorFlowState.CurrentMirQubitByHirSymbolId
                            .TryGetValue(
                                hirSymbolId,
                                out var aliasMirQubit)
                        || aliasMirQubit.Key != incomingMirQubit.Key)
                    {
                        throw MirLoweringError(
                            $"aliases of qubit {expectedMirQubitId} "
                            + "reach a join with different versions");
                    }
                }

                return incomingMirQubit;
            }
        }

        private IReadOnlyList<LoopQubitPhiBinding>
            CreateLoopQubitPhiBindings(
            LoweringFlowState flowStateBeforeLoop,
            LoweringFlowState loopHeaderFlowState,
            MirBlockBuilder mirPreheaderBlock,
            int preheaderSuccessorOrdinal,
            MirBlockBuilder mirHeaderBlock,
            MirOrigin mirOrigin)
        {
            var loopQubitPhiBindings = new List<LoopQubitPhiBinding>();
            foreach (var mirQubitAliasGroup
                     in flowStateBeforeLoop.CurrentMirQubitByHirSymbolId
                         .OrderBy(pair => pair.Key.Value)
                         .GroupBy(pair => pair.Value.Id))
            {
                var hirSymbolIds =
                    mirQubitAliasGroup.Select(pair => pair.Key).ToArray();
                var incomingMirQubit = mirQubitAliasGroup.First().Value;
                if (mirQubitAliasGroup.Any(
                        pair => pair.Value.Key != incomingMirQubit.Key))
                {
                    throw MirLoweringError(
                        $"aliases of qubit {incomingMirQubit.Id} "
                        + "enter a loop with different versions");
                }

                var mirQubitPhi = CreateQubitPhi(
                    hirSymbolIds[0],
                    incomingMirQubit.Id,
                    mirHeaderBlock,
                    new[]
                    {
                        new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                mirPreheaderBlock.Id,
                                preheaderSuccessorOrdinal),
                            incomingMirQubit.Key),
                    },
                    mirOrigin);
                foreach (var hirSymbolId in hirSymbolIds)
                {
                    loopHeaderFlowState.CurrentMirQubitByHirSymbolId[
                            hirSymbolId] =
                        mirQubitPhi;
                    if (hirSymbolId != hirSymbolIds[0])
                    {
                        _traceSink?.LinkQubit(
                            hirSymbolId,
                            _mirCallableId,
                            mirQubitPhi.Key);
                    }
                }

                loopQubitPhiBindings.Add(
                    new LoopQubitPhiBinding(
                        mirQubitPhi,
                        hirSymbolIds));
            }

            return loopQubitPhiBindings;
        }

        private void AddLoopBackedgeInputs(
            IReadOnlyList<LoopQubitPhiBinding> loopQubitPhiBindings,
            LoweringFlowState backedgeFlowState,
            MirBlockBuilder mirBackedgeBlock,
            int successorOrdinal,
            MirBlockBuilder mirHeaderBlock)
        {
            foreach (var loopQubitPhiBinding in loopQubitPhiBindings)
            {
                var incomingMirQubit =
                    backedgeFlowState.CurrentMirQubitByHirSymbolId[
                        loopQubitPhiBinding.HirSymbolIds[0]];
                if (incomingMirQubit.Id
                    != loopQubitPhiBinding.MirQubitPhi.Id)
                {
                    throw MirLoweringError(
                        "loop backedge replaces qubit "
                        + $"{loopQubitPhiBinding.MirQubitPhi.Id} with "
                        + incomingMirQubit.Id);
                }
                if (loopQubitPhiBinding.HirSymbolIds.Any(
                        hirSymbolId =>
                            backedgeFlowState
                                .CurrentMirQubitByHirSymbolId[
                                    hirSymbolId].Key
                            != incomingMirQubit.Key))
                {
                    throw MirLoweringError(
                        $"aliases of qubit {incomingMirQubit.Id} "
                        + "leave a loop with different versions");
                }

                mirHeaderBlock.ReplaceQubitPhi(
                    loopQubitPhiBinding.MirQubitPhi with
                {
                    Inputs = loopQubitPhiBinding.MirQubitPhi.Inputs
                        .Append(new MirQubitPhiInput(
                            new MirControlFlowEdge(
                                mirBackedgeBlock.Id,
                                successorOrdinal),
                            incomingMirQubit.Key))
                        .ToArray(),
                });
            }
        }

        private void UpdateCurrentQubitAfterWrite(MirQubit updatedMirQubit)
        {
            var hirSymbolIds =
                _currentFlowState.CurrentMirQubitByHirSymbolId
                .Where(pair => pair.Value.Id == updatedMirQubit.Id)
                .Select(pair => pair.Key)
                .ToArray();
            if (hirSymbolIds.Length == 0)
            {
                throw MirLoweringError(
                    $"written qubit {updatedMirQubit.Id} "
                    + "has no active HIR binding");
            }

            foreach (var hirSymbolId in hirSymbolIds)
            {
                _currentFlowState.CurrentMirQubitByHirSymbolId[
                        hirSymbolId] =
                    updatedMirQubit;
                _traceSink?.LinkQubit(
                    hirSymbolId,
                    _mirCallableId,
                    updatedMirQubit.Key);
            }
        }

        private (bool IsArray, int? KnownLength) MirQubitShapeOf(
            MirQubitId mirQubitId) =>
            RequireInitialMirQubit(mirQubitId) switch
            {
                MirQubitParameter mirQubitParameter => (
                    mirQubitParameter.IsArray,
                    mirQubitParameter.Length),
                MirQubitFromUse mirLocalQubit => (
                    mirLocalQubit.IsArray,
                    mirLocalQubit.Length),
                var invalidInitialMirQubit => throw MirLoweringError(
                    $"qubit {mirQubitId} has invalid initial definition "
                    + invalidInitialMirQubit.GetType().Name),
            };

        private MirQubit RequireInitialMirQubit(MirQubitId mirQubitId) =>
            _initialMirQubitsById.TryGetValue(
                mirQubitId,
                out var initialMirQubit)
                ? initialMirQubit
                : throw MirLoweringError(
                    $"qubit identity {mirQubitId} has no initial definition");

        private void EmitMirInstruction(MirInstruction mirInstruction) =>
            RequireCurrentMirBlock().Instructions.Add(mirInstruction);

        private void TerminateCurrentMirBlock(MirTerminator mirTerminator)
        {
            var currentMirBlock = RequireCurrentMirBlock();
            if (currentMirBlock.Terminator is not null)
            {
                throw MirLoweringError(
                    $"block {currentMirBlock.Id} already has a terminator");
            }
            currentMirBlock.Terminator = mirTerminator;
            _currentMirBlockBuilder = null;
        }

        private MirBlockBuilder RequireCurrentMirBlock() =>
            _currentMirBlockBuilder
            ?? throw MirLoweringError(
                "attempted to emit into a terminated control-flow path");

        private MirBlockBuilder CreateMirBlock(MirOrigin mirOrigin)
        {
            var mirBlockBuilder = new MirBlockBuilder(
                new MirBlockId(_mirBlockBuilders.Count),
                mirOrigin);
            _mirBlockBuilders.Add(mirBlockBuilder);
            return mirBlockBuilder;
        }

        private MirInstructionId AllocateMirInstructionId() =>
            new(_nextMirInstructionIdValue++);

        private MirStorageId AllocateMirStorageId() =>
            new(_mirArrayStorages.Count);

        private MirQubitId AllocateMirQubitId() =>
            new(_initialMirQubitsById.Count);

        private Symbol RequireHirDeclarationSymbol(
            HirNodeId hirDeclarationNodeId,
            string diagnosticRole) =>
            _finalHirArtifact.Model.FindSymbol(hirDeclarationNodeId)
            ?? throw MirLoweringError(
                $"{diagnosticRole} has no semantic symbol");

        private SymbolId SymbolIdOf(
            HirNameExpression hirNameReference,
            string diagnosticRole) =>
            _finalHirArtifact.Model
                .FindReferencedSymbol(hirNameReference.Id)?.Id
            ?? throw MirLoweringError(
                $"{diagnosticRole} has no semantic binding");

        private Symbol RequireHirCallTargetSymbol(
            HirCallExpression hirCall) =>
            _finalHirArtifact.Model
                .FindReferencedSymbol(hirCall.Callee.Id)
            ?? throw MirLoweringError(
                $"call target `{hirCall.Name}` has no semantic binding");

        private HirCallable RequireHirCallableDeclaration(
            Symbol semanticCallTargetSymbol)
        {
            if (semanticCallTargetSymbol.Kind != SymbolKind.Callable)
            {
                throw MirLoweringError(
                    $"symbol `{semanticCallTargetSymbol.SourceName}` "
                    + "is not a user callable");
            }

            var hirDeclarationNodeId =
                semanticCallTargetSymbol.DeclarationNodeId
                ?? throw MirLoweringError(
                    $"callable symbol `{semanticCallTargetSymbol.SourceName}` "
                    + "has no declaration node");
            return _finalHirArtifact.Source.Structure
                .FindNode(hirDeclarationNodeId)
                as HirCallable
                ?? throw MirLoweringError(
                    $"callable symbol `{semanticCallTargetSymbol.SourceName}` points to "
                    + "a non-callable HIR node");
        }

        private SymbolId? SymbolIdOfWholeArgument(HirArgument hirArgument)
        {
            if (hirArgument.Expression
                is not HirNameExpression hirArgumentName)
            {
                return null;
            }

            return SymbolIdOf(
                hirArgumentName,
                $"argument `{hirArgumentName.Name}`");
        }

        private static IReadOnlyList<MirFunctor> LowerModifiersToMirFunctors(
            IReadOnlyList<QGateModifier> hirModifiers) =>
            hirModifiers.Select(hirModifier => hirModifier switch
            {
                QGateModifier.Controlled => MirFunctor.Controlled,
                _ => throw new InvalidOperationException(
                    "QINTERNAL: unsupported HIR gate modifier "
                    + $"`{hirModifier}` reached MIR lowering"),
            }).ToList();

        private static MirBinaryOperator MirBinaryOperatorOf(
            HirBinaryOperator hirOperator) => hirOperator switch
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
                $"QINTERNAL: unsupported binary operator `{hirOperator}` "
                + "reached MIR lowering"),
        };

        private static MirType CommonMirNumericType(
            MirType mirLeftType,
            MirType mirRightType,
            bool isComparison)
        {
            if (mirLeftType.IsArray || mirRightType.IsArray)
            {
                throw new InvalidOperationException(
                    "QINTERNAL: an array reached a scalar MIR binary instruction");
            }
            if (mirLeftType == mirRightType)
            {
                if (!isComparison
                    && mirLeftType.ElementType == QType.Bit)
                {
                    return MirType.Scalar(QType.Int);
                }
                return mirLeftType;
            }
            if (mirLeftType.ElementType == QType.Float
                || mirRightType.ElementType == QType.Float)
            {
                return MirType.Scalar(QType.Float);
            }
            if (mirLeftType.ElementType == QType.Angle
                || mirRightType.ElementType == QType.Angle)
            {
                return MirType.Scalar(QType.Angle);
            }
            return MirType.Scalar(QType.Int);
        }

        private static bool IsBuiltinLiteralName(
            string sourceLiteralName) =>
            sourceLiteralName
                is "true" or "false" or "pi" or "tau" or "euler";

        private static QType QTypeOfLiteralText(string literalText) =>
            literalText is "true" or "false"
                ? QType.Bit
                : literalText is "pi" or "tau" or "euler"
                    || literalText.Contains('.')
                    ? QType.Float
                    : QType.Int;

        private MirOrigin MirOriginFor(HirNode hirNode)
        {
            var sourceSpan = _finalHirArtifact.Source.SourceMap.Find(hirNode.Id);

            return new MirHirOrigin(hirNode.Id, sourceSpan);
        }

        private InvalidOperationException MirLoweringError(string message) =>
            new($"QINTERNAL: MIR lowering of `{_hirCallable.Name}` failed: {message}");

        private sealed record LoopQubitPhiBinding(
            MirQubitPhi MirQubitPhi,
            IReadOnlyList<SymbolId> HirSymbolIds);

        private sealed class MirBlockBuilder
        {
            public MirBlockId Id { get; }
            public List<MirValueId> Arguments { get; } = new();
            public List<MirQubitPhi> QubitPhis { get; } = new();
            public List<MirInstruction> Instructions { get; } = new();
            public MirTerminator? Terminator { get; set; }
            public MirOrigin Origin { get; }

            public MirBlockBuilder(
                MirBlockId mirBlockId,
                MirOrigin mirOrigin)
            {
                Id = mirBlockId;
                Origin = mirOrigin;
            }

            public MirBlock Build() => new(
                Id,
                Arguments,
                Instructions,
                Terminator ?? new MirUnreachable(Origin),
                Origin,
                QubitPhis);

            public void ReplaceQubitPhi(MirQubitPhi mirQubitPhi)
            {
                var mirQubitPhiIndex = QubitPhis.FindIndex(
                    existing => existing.Key == mirQubitPhi.Key);
                if (mirQubitPhiIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: block {Id} does not contain qubit Phi "
                        + mirQubitPhi.Key);
                }

                QubitPhis[mirQubitPhiIndex] = mirQubitPhi;
            }
        }

        private sealed class LoweringFlowState
        {
            // These are lifetime frames, not name-resolution scopes. HIR has already resolved every use to
            // a SymbolId; the frames only identify states that must disappear when a lexical block exits.
            private readonly List<HashSet<SymbolId>>
                _hirSymbolLifetimeFrames;
            public Dictionary<SymbolId, MirValueId>
                CurrentMirValueByHirSymbolId { get; }
            public Dictionary<SymbolId, MirQubit>
                CurrentMirQubitByHirSymbolId { get; }

            public LoweringFlowState()
            {
                _hirSymbolLifetimeFrames = new List<HashSet<SymbolId>>
                {
                    new(),
                };
                CurrentMirValueByHirSymbolId = new();
                CurrentMirQubitByHirSymbolId = new();
            }

            private LoweringFlowState(
                List<HashSet<SymbolId>> hirSymbolLifetimeFrames,
                Dictionary<SymbolId, MirValueId>
                    currentMirValueByHirSymbolId,
                Dictionary<SymbolId, MirQubit>
                    currentMirQubitByHirSymbolId)
            {
                _hirSymbolLifetimeFrames = hirSymbolLifetimeFrames;
                CurrentMirValueByHirSymbolId =
                    currentMirValueByHirSymbolId;
                CurrentMirQubitByHirSymbolId =
                    currentMirQubitByHirSymbolId;
            }

            public LoweringFlowState Clone() => new(
                _hirSymbolLifetimeFrames
                    .Select(frame => new HashSet<SymbolId>(frame))
                    .ToList(),
                new Dictionary<SymbolId, MirValueId>(
                    CurrentMirValueByHirSymbolId),
                new Dictionary<SymbolId, MirQubit>(
                    CurrentMirQubitByHirSymbolId));

            public void PushHirSymbolLifetimeFrame() =>
                _hirSymbolLifetimeFrames.Add(new HashSet<SymbolId>());

            public void PopHirSymbolLifetimeFrame()
            {
                if (_hirSymbolLifetimeFrames.Count == 1)
                {
                    throw new InvalidOperationException(
                        "QINTERNAL: cannot pop the MIR root lexical scope");
                }
                var removedHirSymbolIds = _hirSymbolLifetimeFrames[^1];
                _hirSymbolLifetimeFrames.RemoveAt(
                    _hirSymbolLifetimeFrames.Count - 1);
                foreach (var hirSymbolId in removedHirSymbolIds)
                {
                    CurrentMirValueByHirSymbolId.Remove(hirSymbolId);
                    CurrentMirQubitByHirSymbolId.Remove(hirSymbolId);
                }
            }

            public void TrackDeclaredHirSymbol(SymbolId hirSymbolId)
            {
                if (!_hirSymbolLifetimeFrames[^1].Add(hirSymbolId))
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: semantic symbol {hirSymbolId} "
                        + "was declared twice in one MIR scope");
                }
            }
        }
    }
}
