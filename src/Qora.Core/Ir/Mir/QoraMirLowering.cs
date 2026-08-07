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

        var hirArtifact = hirCompilation.EffectAnalysis
            ?? throw new InvalidOperationException(
                "MIR lowering requires the compilation's final effect-analyzed HIR");
        return LowerHirArtifact(hirArtifact, traceSink);
    }

    /// <summary>
    /// Lowers every callable in the final semantically analyzed HIR into MIR and creates the initial MIR snapshot.
    /// 의미 분석이 끝난 최종 HIR의 모든 callable을 MIR로 변환하고 최초 MIR snapshot을 만듭니다.
    /// </summary>
    /// <example>
    /// <code>
    /// operation Main() {
    ///     var xs: int[] = [10, 20];
    ///     var i: int = 0;
    ///     var value: int = xs[i];
    /// }
    ///
    /// HIR Main = h42
    /// MIR Main = c42
    /// </code>
    /// </example>
    private static MirSnapshot LowerHirArtifact(
        HirSemanticArtifact hirArtifact,
        IMirLoweringTraceSink? traceSink)
    {
        // hirArtifact
        // ├─ Source.Program.Callables = [Main(h42)]
        // └─ Model
        //    ├─ Main declaration → Main symbol
        //    ├─ xs use           → xs symbol
        //    └─ i use            → i symbol
        ArgumentNullException.ThrowIfNull(hirArtifact);

        // hirArtifact.Source
        // └─ hirSnapshot
        //    └─ Program
        //       └─ Main(h42)
        //          ├─ xs = [10, 20]
        //          ├─ i = 0
        //          └─ value = xs[i]
        var hirSnapshot = hirArtifact.Source;

        var hirProgram = hirSnapshot.Program;

        // hirProgram.EntryCallable = Main(h42)
        var hirEntryCallable = hirProgram.EntryCallable
            ?? throw new InvalidOperationException(
                "validated HIR must contain an operation before MIR lowering");

        var mirCallables = new List<MirCallable>();
        MirCallable? mirEntryCallable = null;

        foreach (var hirCallable in hirProgram.Callables)
        {
            var mirCallableLowerer = new HirCallableToMirLowerer(
                hirCallable,        // Main(h42)
                hirArtifact,
                traceSink);

            // Main(h42)
            //     ↓ LowerToMirCallable()
            // Main(c42)
            // └─ Blocks / Instructions / SSA Values
            //    └─ xs[i] → MirArrayLoad
            var mirCallable = mirCallableLowerer.LowerToMirCallable();

            // mirCallables: [] → [Main(c42)]
            mirCallables.Add(mirCallable);

            // hirCallable      = Main(h42)
            // hirEntryCallable = Main(h42), the exact same HIR object
            //     ↓
            // mirEntryCallable = Main(c42), the exact lowered MIR object
            if (ReferenceEquals(hirCallable, hirEntryCallable))
                mirEntryCallable = mirCallable;
        }

        // mirProgram
        // ├─ EntryPoint = Main(c42), the exact object in Callables
        // └─ Callables
        //    └─ Main(c42)
        var mirProgram = new MirProgram(
            mirEntryCallable
                ?? throw new InvalidOperationException(
                    "validated HIR entry callable must be lowered into MIR"),
            mirCallables);

        // Result:
        //
        // MirSnapshot
        // ├─ Stage          = Lowered
        // ├─ Program        = mirProgram
        // ├─ HirArtifact    = hirArtifact
        // └─ Analyses       = new MirAnalysisStore(mirProgram)
        return MirSnapshot.CreateLowered(mirProgram);
    }

    private sealed class HirCallableToMirLowerer
    {
        private readonly HirCallable _hirCallable;
        private readonly MirCallableId _mirCallableId;
        private readonly HirSemanticArtifact _hirArtifact;
        private readonly IMirLoweringTraceSink? _traceSink;
        private readonly List<MirBlockBuilder> _mirBlockBuilders = new();
        private readonly MirBlockId.Allocator _mirBlockIds = new();
        private readonly List<MirValue> _mirValues = new();
        private readonly List<IMirParameter> _mirParameters = new();
        private readonly Dictionary<MirQubitId, MirQubit>
            _initialMirQubitsById = new();
        private readonly Dictionary<MirQubitId, int>
            _nextMirQubitVersionById = new();

        private int _nextMirInstructionIdValue;
        private int _nextMirStorageIdValue;
        private MirBlockBuilder? _currentMirBlockBuilder;
        private LoweringFlowState _currentFlowState = new();

        public HirCallableToMirLowerer(
            HirCallable hirCallable,
            HirSemanticArtifact hirArtifact,
            IMirLoweringTraceSink? traceSink)
        {
            _hirCallable = hirCallable;
            _hirArtifact = hirArtifact;
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
            var hirScopeGraph = _hirArtifact.Model.ScopeGraph
                ?? throw MirLoweringError(
                    "the final HIR semantic model has no scope graph");
            var qualifiedSourceCallableName =
                hirScopeGraph.QualifiedName(hirCallableSymbol);

            var mirEntryBlockBuilder = CreateMirBlock(mirCallableOrigin);
            _currentMirBlockBuilder = mirEntryBlockBuilder;

            LowerParameters();
            LowerStatements(_hirCallable.Body.Statements);

            if (_currentMirBlockBuilder is not null)
            {
                if (_hirCallable.IsFunction)
                {
                    MarkCurrentMirBlockUnreachable(
                        MirOrigin.GeneratedFrom(
                            mirCallableOrigin,
                            "missing validated function return"));
                }
                else
                {
                    ReturnFromCurrentMirBlock(
                        value: null,
                        MirOrigin.GeneratedFrom(
                            mirCallableOrigin,
                            "implicit operation return"));
                }
            }

            var mirBlocks = new List<MirBlock>(_mirBlockBuilders.Count);
            MirBlock? mirEntryBlock = null;
            foreach (var mirBlockBuilder in _mirBlockBuilders)
            {
                var mirBlock = mirBlockBuilder.Build();
                mirBlocks.Add(mirBlock);
                if (ReferenceEquals(mirBlockBuilder, mirEntryBlockBuilder))
                    mirEntryBlock = mirBlock;
            }

            if (mirEntryBlock is null)
                throw MirLoweringError("the MIR entry block was not emitted");

            return new MirCallable(
                _mirCallableId,
                qualifiedSourceCallableName,
                _hirCallable.ReturnType is { } returns
                    ? MirType.Scalar(returns)
                    : null,
                _mirParameters,
                mirEntryBlock,
                mirBlocks,
                mirCallableOrigin);
        }

        private void LowerParameters()
        {
            var requiredArrayLengths =
                _hirArtifact.Model.RequiredArgLengths(_hirCallable.Id);

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
                    var mirQubitParameter = hirParameter.IsArray
                        ? MirQubitParameter.Array(
                            mirQubitId,
                            hirParameter.Name,
                            hirParameter.RegisterSize,
                            hirParameter.Ownership,
                            mirParameterOrigin)
                        : MirQubitParameter.Single(
                            mirQubitId,
                            hirParameter.Name,
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
                var mirParameterValue = CreateMirValue(
                    mirParameterType,
                    hirParameterSymbol.Id,
                    mirParameterOrigin);
                MirArrayStorage? mirArrayStorage = null;
                if (hirParameter.IsArray)
                {
                    mirArrayStorage = new MirArrayStorage(
                        AllocateMirStorageId(),
                        hirParameter.Name,
                        mirParameterOrigin);
                    _traceSink?.LinkStorage(
                        hirParameterSymbol.Id,
                        _mirCallableId,
                        mirArrayStorage.Id);
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
                    mirParameterValue.Id;
                var mirParameter = mirArrayStorage is null
                    ? MirClassicalParameter.Scalar(
                        hirParameter.Name,
                        mirParameterValue,
                        hirParameter.Ownership,
                        hirParameter.Access)
                    : MirClassicalParameter.Array(
                        hirParameter.Name,
                        mirParameterValue,
                        mirArrayStorage,
                        hirParameter.Ownership,
                        hirParameter.Access,
                        minimumLength);
                _mirParameters.Add(mirParameter);
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

            if (_hirArtifact.Model.FindSymbol(hirStatement.Id)
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
                case HirQubitDeclarationStatement hirQubitDeclaration:
                    LowerQubitDeclaration(hirQubitDeclaration);
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

        private void LowerQubitDeclaration(
            HirQubitDeclarationStatement hirQubitDeclaration)
        {
            var hirQubitSymbol = RequireHirDeclarationSymbol(
                hirQubitDeclaration.Id,
                $"qubit allocation `{hirQubitDeclaration.Name}`");
            _currentFlowState.TrackDeclaredHirSymbol(hirQubitSymbol.Id);

            var mirOrigin = MirOriginFor(hirQubitDeclaration);
            var mirAllocatedQubit = new MirQubitFromUse(
                AllocateMirQubitId(),
                hirQubitDeclaration.Name,
                hirQubitDeclaration.Size,
                mirOrigin);
            RegisterInitialMirQubit(hirQubitSymbol.Id, mirAllocatedQubit);
            EmitMirInstruction(new MirQubitAllocate(
                AllocateMirInstructionId(),
                mirAllocatedQubit,
                mirOrigin));
            _currentFlowState.CurrentMirQubitByHirSymbolId[hirQubitSymbol.Id] =
                mirAllocatedQubit;
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
                var mirArrayStorage = new MirArrayStorage(
                    AllocateMirStorageId(),
                    hirDeclaration.Name,
                    mirOrigin);
                var mirArrayType = MirType.Array(
                    declaredElementQType,
                    declaredArrayLength);
                var mirArrayValue = CreateMirValue(
                    mirArrayType,
                    hirDeclaredSymbol.Id,
                    mirOrigin);
                _traceSink?.LinkStorage(
                    hirDeclaredSymbol.Id,
                    _mirCallableId,
                    mirArrayStorage.Id);
                EmitMirInstruction(new MirArrayCreate(
                    mirArrayCreationInstructionId,
                    mirArrayValue,
                    mirArrayStorage,
                    mirArrayInitialization,
                    mirElementValueIds,
                    mirOrigin));
                _currentFlowState.TrackDeclaredHirSymbol(
                    hirDeclaredSymbol.Id);
                _currentFlowState.CurrentMirValueByHirSymbolId[
                        hirDeclaredSymbol.Id] =
                    mirArrayValue.Id;
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
            var mirUpdatedArrayValue = CreateMirValue(
                mirArrayType,
                hirArraySymbolId,
                mirOrigin);
            EmitMirInstruction(new MirArrayStore(
                mirArrayStoreInstructionId,
                mirUpdatedArrayValue,
                currentMirArrayValueId,
                mirIndexValueId,
                mirStoredValueId,
                mirIndexedTargetOrigin));
            _currentFlowState.CurrentMirValueByHirSymbolId[
                    hirArraySymbolId] =
                mirUpdatedArrayValue.Id;
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
                    LowerPureCall(hirCall);
                    return;
                }

                LowerUserOperationCall(
                    hirCallStatement,
                    hirUserCallable);
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
                        LowerQubitAccess(hirArgument.Expression),
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

            EmitMirQuantumApply(
                new MirBuiltinGateTarget(builtinGateName),
                mirCallOperands,
                Array.Empty<MirMutableArrayResult>(),
                LowerModifiersToMirFunctors(hirCallStatement.Modifiers),
                mirCallOrigin);
        }

        private void LowerUserOperationCall(
            HirCallStatement hirCallStatement,
            HirCallable hirUserOperation)
        {
            var hirCall = hirCallStatement.Call;
            var mirCallOrigin = MirOriginFor(hirCallStatement);
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
                        LowerQubitAccess(hirArgument.Expression),
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
                RequireCallType(mirArgumentValueId, expectedMirArgumentType);
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
                    var mirUpdatedArrayValue =
                        CreateMirValue(
                            actualMirArgumentType,
                            hirArgumentSymbolId,
                            mirCallOrigin);
                    mirMutableArrayResults.Add(
                        new MirMutableArrayResult(
                            argumentIndex,
                            mirUpdatedArrayValue));
                    mirMutableArrayValueUpdates.Add((
                        hirArgumentSymbolId,
                        mirUpdatedArrayValue.Id));
                }
            }

            var mirUserOperationId =
                new MirCallableId(hirUserOperation.Id.Value);
            EmitMirQuantumApply(
                new MirUserCallableTarget(
                    mirUserOperationId),
                mirCallOperands,
                mirMutableArrayResults,
                LowerModifiersToMirFunctors(hirCallStatement.Modifiers),
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

        private MirValueId LowerPureCall(HirCallExpression hirCall)
        {
            var semanticCallTargetSymbol = RequireHirCallTargetSymbol(hirCall);
            HirCallable? hirUserFunction = null;
            MirCallTarget mirCallTarget;
            if (semanticCallTargetSymbol.Kind == SymbolKind.BuiltinFunction)
            {
                mirCallTarget = new MirBuiltinFunctionTarget(
                    semanticCallTargetSymbol.SourceName);
            }
            else if (semanticCallTargetSymbol.Kind == SymbolKind.Callable)
            {
                hirUserFunction = RequireHirCallableDeclaration(
                    semanticCallTargetSymbol);
                if (!hirUserFunction.IsFunction)
                {
                    throw MirLoweringError(
                        $"expression call `{hirCall.Name}` targets an operation");
                }

                mirCallTarget = new MirUserCallableTarget(
                    new MirCallableId(hirUserFunction.Id.Value));
            }
            else
            {
                throw MirLoweringError(
                    $"pure call `{hirCall.Name}` targets semantic symbol kind "
                    + semanticCallTargetSymbol.Kind);
            }

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
                    RequireCallType(mirArgumentValueId, expectedMirArgumentType);
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
            var mirCallResultValue = CreateMirValue(
                mirCallResultType,
                null,
                mirCallOrigin);
            EmitMirInstruction(new MirPureCall(
                mirCallInstructionId,
                mirCallResultValue,
                mirCallTarget,
                mirCallOperands,
                mirCallOrigin));
            return mirCallResultValue.Id;
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
            mirBranchBlock.BranchTo(
                mirConditionValueId,
                mirThenBlock,
                Array.Empty<MirValueId>(),
                mirElseBlock,
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
                survivingBranchExit.ExitBlock!.JumpTo(
                    mirMergeBlock,
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
                    hirSymbolId);
                mirThenPhiArguments.Add(mirThenValueId);
                mirElsePhiArguments.Add(mirElseValueId);
                mergedFlowState.CurrentMirValueByHirSymbolId[hirSymbolId] =
                    mirPhiValueId;
            }

            var mirThenEdge = thenBranchExit.ExitBlock.JumpTo(
                mirMergeBlock,
                mirThenPhiArguments,
                mirOrigin);
            var mirElseEdge = elseBranchExit.ExitBlock.JumpTo(
                mirMergeBlock,
                mirElsePhiArguments,
                mirOrigin);
            MergeQubitStatesAtJoin(
                mergedFlowState,
                thenBranchExit.ExitFlowState,
                mirThenEdge,
                elseBranchExit.ExitFlowState,
                mirElseEdge,
                mirMergeBlock);
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
                    hirSymbolId);
                loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirHeaderPhiValueId;
                mirPreheaderArguments.Add(mirInitialValueId);
            }
            loopHeaderFlowState.PushHirSymbolLifetimeFrame();
            var hirLoopVariableSymbol = RequireHirDeclarationSymbol(
                hirFor.Id,
                $"loop variable `{hirFor.Variable}`");
            var mirLoopVariableValueId = AddBlockArgument(
                mirHeaderBlock,
                MirType.Scalar(QType.Int),
                hirLoopVariableSymbol.Id);
            loopHeaderFlowState.TrackDeclaredHirSymbol(
                hirLoopVariableSymbol.Id);
            loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                    hirLoopVariableSymbol.Id] =
                mirLoopVariableValueId;
            mirPreheaderArguments.Add(mirRangeStartValueId);
            var mirPreheaderEdge = mirPreheaderBlock.JumpTo(
                mirHeaderBlock,
                mirPreheaderArguments,
                mirOrigin);
            var loopQubitPhiBindings = CreateLoopQubitPhiBindings(
                flowStateBeforeLoop,
                loopHeaderFlowState,
                mirPreheaderEdge,
                mirHeaderBlock);

            _currentMirBlockBuilder = mirHeaderBlock;
            _currentFlowState = loopHeaderFlowState.Clone();
            var mirLoopConditionValueId = EmitBinary(
                isDescendingRange
                    ? MirBinaryOperator.GreaterOrEqual
                    : MirBinaryOperator.LessOrEqual,
                mirLoopVariableValueId,
                mirRangeEndValueId,
                mirOrigin);
            BranchFromCurrentMirBlockTo(
                mirLoopConditionValueId,
                mirBodyBlock,
                Array.Empty<MirValueId>(),
                mirExitBlock,
                Array.Empty<MirValueId>(),
                mirOrigin);

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
                var mirBackedgeEdge = JumpFromCurrentMirBlockTo(
                    mirHeaderBlock,
                    mirBackedgeArguments,
                    mirOrigin);
                AddLoopBackedgeInputs(
                    loopQubitPhiBindings,
                    _currentFlowState,
                    mirBackedgeEdge,
                    mirHeaderBlock);
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
                    hirSymbolId);
                loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirHeaderPhiValueId;
                mirPreheaderArguments.Add(mirInitialValueId);
            }
            var mirPreheaderEdge = mirPreheaderBlock.JumpTo(
                mirHeaderBlock,
                mirPreheaderArguments,
                mirOrigin);
            var loopQubitPhiBindings = CreateLoopQubitPhiBindings(
                flowStateBeforeLoop,
                loopHeaderFlowState,
                mirPreheaderEdge,
                mirHeaderBlock);

            _currentMirBlockBuilder = mirHeaderBlock;
            _currentFlowState = loopHeaderFlowState.Clone();
            var mirLoopConditionValueId = LowerExpressionAs(
                hirWhile.Condition,
                MirType.Scalar(QType.Bit));
            var flowStateAfterCondition = _currentFlowState.Clone();
            BranchFromCurrentMirBlockTo(
                mirLoopConditionValueId,
                mirBodyBlock,
                Array.Empty<MirValueId>(),
                mirExitBlock,
                Array.Empty<MirValueId>(),
                mirOrigin);

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
                var mirBackedgeEdge = JumpFromCurrentMirBlockTo(
                    mirHeaderBlock,
                    mirBackedgeArguments,
                    mirOrigin);
                AddLoopBackedgeInputs(
                    loopQubitPhiBindings,
                    _currentFlowState,
                    mirBackedgeEdge,
                    mirHeaderBlock);
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
                    hirSymbolId);
                loopHeaderFlowState.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirHeaderPhiValueId;
                mirPreheaderArguments.Add(mirInitialValueId);
            }
            var mirPreheaderEdge = mirPreheaderBlock.JumpTo(
                mirHeaderBlock,
                mirPreheaderArguments,
                mirOrigin);
            var loopQubitPhiBindings = CreateLoopQubitPhiBindings(
                flowStateBeforeLoop,
                loopHeaderFlowState,
                mirPreheaderEdge,
                mirHeaderBlock);

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
                    hirSymbolId);
                flowStateAfterLoop.CurrentMirValueByHirSymbolId[
                        hirSymbolId] =
                    mirExitPhiValueId;
                mirExitArguments.Add(currentMirValueId);
                mirBackedgeArguments.Add(currentMirValueId);
            }
            foreach (var hirSymbolId in
                     flowStateBeforeLoop.CurrentMirQubitByHirSymbolId.Keys)
            {
                flowStateAfterLoop.CurrentMirQubitByHirSymbolId[
                        hirSymbolId] =
                    _currentFlowState.CurrentMirQubitByHirSymbolId[
                        hirSymbolId];
            }
            var mirLoopTailEdges = BranchFromCurrentMirBlockTo(
                mirUntilConditionValueId,
                mirExitBlock,
                mirExitArguments,
                mirHeaderBlock,
                mirBackedgeArguments,
                mirOrigin);
            AddLoopBackedgeInputs(
                loopQubitPhiBindings,
                _currentFlowState,
                mirLoopTailEdges.FalseEdge,
                mirHeaderBlock);
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
            ReturnFromCurrentMirBlock(mirReturnValueId, mirOrigin);
        }

        private MirValueId LowerMeasurement(
            HirMeasurementExpression hirMeasurement)
        {
            var mirOrigin = MirOriginFor(hirMeasurement);
            var mirMeasuredQubitAccess = LowerQubitAccess(
                hirMeasurement.Target);
            var mirMeasurementInstructionId = AllocateMirInstructionId();
            var mirMeasurementResultValue = CreateMirValue(
                MirType.Scalar(QType.Bit),
                null,
                mirOrigin);
            var mirQubitAfterMeasurement = CreateQubitAfterInstruction(
                mirMeasuredQubitAccess.Qubit.Id,
                mirOrigin);
            EmitMirInstruction(new MirMeasure(
                mirMeasurementInstructionId,
                mirMeasurementResultValue,
                mirMeasuredQubitAccess,
                mirQubitAfterMeasurement,
                mirOrigin));
            UpdateCurrentQubitAfterWrite(mirQubitAfterMeasurement);
            return mirMeasurementResultValue.Id;
        }

        private MirValueId LowerExpression(HirExpression hirExpression)
        {
            var mirValueId = LowerExpressionValue(hirExpression);
            return ApplyApprovedImplicitConversion(hirExpression, mirValueId);
        }

        private MirValueId LowerExpressionValue(HirExpression hirExpression)
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
                        ExpressionTypes.LiteralType(hirLiteral.Text).Type,
                        hirLiteral.Text,
                        mirOrigin);

                case HirNameExpression hirName
                    when IsBuiltinLiteralName(hirName.Name):
                    return EmitConstant(
                        ExpressionTypes.LiteralType(hirName.Name).Type,
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
                        RequireExactMirType(
                            hirUnary.Operand,
                            mirOperandValueId,
                            MirType.Scalar(QType.Bit));
                        return EmitUnary(
                            MirUnaryOperator.LogicalNot,
                            mirOperandValueId,
                            mirOrigin);
                    }
                    if (hirUnary.Operator != HirUnaryOperator.Negate)
                    {
                        throw MirLoweringError(
                            $"unsupported unary operator `{hirUnary.Operator}`");
                    }

                    var mirOperandType = MirTypeOf(mirOperandValueId);
                    if (mirOperandType.ElementType == QType.Bit)
                        throw MirLoweringError(
                            "validated bit negation reached MIR without its approved int conversion");

                    return EmitUnary(
                        MirUnaryOperator.Negate,
                        mirOperandValueId,
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
                            || !mirLeftType.IsArray
                            || !mirRightType.IsArray
                            || mirLeftType.ElementType
                            != mirRightType.ElementType)
                        {
                            throw MirLoweringError(
                                "validated array comparison has incompatible "
                                + $"operands {mirLeftType} and {mirRightType}");
                        }

                        return EmitBinary(
                            mirBinaryOperator,
                            mirLeftValueId,
                            mirRightValueId,
                            mirOrigin);
                    }

                    if (mirLeftType != mirRightType)
                    {
                        throw MirLoweringError(
                            "validated scalar operator reached MIR with incompatible "
                            + $"operands {mirLeftType} and {mirRightType}");
                    }
                    return EmitBinary(
                        mirBinaryOperator,
                        mirLeftValueId,
                        mirRightValueId,
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
                    var mirResultValue = CreateMirValue(
                        MirType.Scalar(mirArrayType.ElementType),
                        null,
                        mirOrigin);
                    EmitMirInstruction(new MirArrayLoad(
                        mirInstructionId,
                        mirResultValue,
                        mirArrayValueId,
                        mirIndexValueId,
                        mirOrigin));
                    return mirResultValue.Id;
                }

                case HirCallExpression hirCall:
                    return LowerPureCall(hirCall);

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
            MirType expectedMirType)
        {
            var mirValueId = LowerExpression(hirExpression);
            RequireExactMirType(hirExpression, mirValueId, expectedMirType);
            return mirValueId;
        }

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
                hirSymbolId: null);

            MirControlFlowEdge mirShortCircuitEdge;
            if (hirBinary.Operator == HirBinaryOperator.LogicalAnd)
            {
                var mirBranchEdges = mirBranchBlock.BranchTo(
                    mirLeftValueId,
                    mirRightOperandBlock,
                    Array.Empty<MirValueId>(),
                    mirMergeBlock,
                    new[] { mirLeftValueId },
                    mirOrigin);
                mirShortCircuitEdge = mirBranchEdges.FalseEdge;
            }
            else
            {
                var mirBranchEdges = mirBranchBlock.BranchTo(
                    mirLeftValueId,
                    mirMergeBlock,
                    new[] { mirLeftValueId },
                    mirRightOperandBlock,
                    Array.Empty<MirValueId>(),
                    mirOrigin);
                mirShortCircuitEdge = mirBranchEdges.TrueEdge;
            }

            _currentMirBlockBuilder = mirRightOperandBlock;
            _currentFlowState = flowStateBeforeRightOperand.Clone();
            var mirRightValueId = LowerExpressionAs(
                hirBinary.Right,
                MirType.Scalar(QType.Bit));
            var flowStateAfterRightOperand = _currentFlowState.Clone();
            var mirRightEdge = JumpFromCurrentMirBlockTo(
                mirMergeBlock,
                new[] { mirRightValueId },
                mirOrigin);

            var mergedFlowState = flowStateBeforeRightOperand.Clone();
            MergeQubitStatesAtJoin(
                mergedFlowState,
                flowStateBeforeRightOperand,
                mirShortCircuitEdge,
                flowStateAfterRightOperand,
                mirRightEdge,
                mirMergeBlock);
            _currentMirBlockBuilder = mirMergeBlock;
            _currentFlowState = mergedFlowState;
            return mirResultValueId;
        }

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
            var mirResultValue = CreateMirValue(
                MirType.Scalar(scalarType),
                null,
                mirOrigin);
            EmitMirInstruction(new MirConstant(
                mirInstructionId,
                mirResultValue,
                literalText,
                mirOrigin));
            return mirResultValue.Id;
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
            var mirResultValue = CreateMirValue(
                MirType.Scalar(QType.Int),
                hirSymbolId: null,
                mirOrigin);
            EmitMirInstruction(new MirArrayLength(
                mirInstructionId,
                mirResultValue,
                mirArrayValueId,
                mirOrigin));
            return mirResultValue.Id;
        }

        private MirValueId EmitUnary(
            MirUnaryOperator mirOperator,
            MirValueId mirOperandValueId,
            MirOrigin mirOrigin)
        {
            var mirResultType = mirOperator switch
            {
                MirUnaryOperator.LogicalNot => MirType.Scalar(QType.Bit),
                MirUnaryOperator.Negate => MirTypeOf(mirOperandValueId),
                _ => throw MirLoweringError(
                    $"unsupported MIR unary operator `{mirOperator}`"),
            };
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValue = CreateMirValue(
                mirResultType,
                null,
                mirOrigin);
            EmitMirInstruction(new MirUnary(
                mirInstructionId,
                mirResultValue,
                mirOperator,
                mirOperandValueId,
                mirOrigin));
            return mirResultValue.Id;
        }

        private MirValueId EmitBinary(
            MirBinaryOperator mirOperator,
            MirValueId mirLeftValueId,
            MirValueId mirRightValueId,
            MirOrigin mirOrigin)
        {
            var mirResultType = mirOperator switch
            {
                MirBinaryOperator.Equal
                    or MirBinaryOperator.NotEqual
                    or MirBinaryOperator.Less
                    or MirBinaryOperator.LessOrEqual
                    or MirBinaryOperator.Greater
                    or MirBinaryOperator.GreaterOrEqual =>
                    MirType.Scalar(QType.Bit),
                MirBinaryOperator.Add
                    or MirBinaryOperator.Subtract
                    or MirBinaryOperator.Multiply
                    or MirBinaryOperator.Divide =>
                    MirTypeOf(mirLeftValueId),
                _ => throw MirLoweringError(
                    $"unsupported MIR binary operator `{mirOperator}`"),
            };
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValue = CreateMirValue(
                mirResultType,
                null,
                mirOrigin);
            EmitMirInstruction(new MirBinary(
                mirInstructionId,
                mirResultValue,
                mirOperator,
                mirLeftValueId,
                mirRightValueId,
                mirOrigin));
            return mirResultValue.Id;
        }

        private void RequireCallType(
            MirValueId mirValueId,
            MirType expectedMirType)
        {
            var actualMirType = MirTypeOf(mirValueId);
            if (actualMirType == expectedMirType)
                return;
            if (actualMirType.IsArray
                && expectedMirType.IsArray
                && actualMirType.ElementType == expectedMirType.ElementType
                && (expectedMirType.KnownLength is null
                    || actualMirType.KnownLength
                    == expectedMirType.KnownLength))
            {
                return;
            }
            throw MirLoweringError(
                $"validated call argument has type {actualMirType}, expected {expectedMirType}");
        }

        private MirValueId ApplyApprovedImplicitConversion(
            HirExpression hirExpression,
            MirValueId mirValueId)
        {
            var targetType = _hirArtifact.Model.FindImplicitConversionTarget(hirExpression.Id);
            if (targetType is null)
                return mirValueId;

            var actualMirType = MirTypeOf(mirValueId);
            var expectedMirType = MirType.Scalar(targetType.Value);
            if (actualMirType == expectedMirType)
            {
                throw MirLoweringError(
                    $"HIR recorded a redundant conversion of expression {hirExpression.Id} to {expectedMirType}");
            }
            if (actualMirType.IsArray)
            {
                throw MirLoweringError(
                    $"HIR approved an array conversion of expression {hirExpression.Id} from {actualMirType} to {expectedMirType}");
            }

            var mirOrigin = MirOriginFor(hirExpression);
            var mirInstructionId = AllocateMirInstructionId();
            var mirResultValue = CreateMirValue(
                expectedMirType,
                null,
                mirOrigin);
            EmitMirInstruction(new MirConvert(
                mirInstructionId,
                mirResultValue,
                mirValueId,
                mirOrigin));
            return mirResultValue.Id;
        }

        private void RequireExactMirType(
            HirExpression hirExpression,
            MirValueId mirValueId,
            MirType expectedMirType)
        {
            var actualMirType = MirTypeOf(mirValueId);
            if (actualMirType == expectedMirType)
                return;

            throw MirLoweringError(
                $"validated expression {hirExpression.Id} has MIR type {actualMirType}, expected {expectedMirType}");
        }

        private MirValueId AddBlockArgument(
            MirBlockBuilder mirBlockBuilder,
            MirType mirType,
            SymbolId? hirSymbolId)
        {
            var mirValue = CreateMirValue(mirType, hirSymbolId, mirBlockBuilder.Origin);
            mirBlockBuilder.AddArgument(mirValue);
            return mirValue.Id;
        }

        private MirValue CreateMirValue(
            MirType mirType,
            SymbolId? hirSymbolId,
            MirOrigin mirOrigin)
        {
            var mirValueId = new MirValueId(_mirValues.Count);
            var mirValue = new MirValue(mirValueId, mirType, mirOrigin);
            _mirValues.Add(mirValue);
            if (hirSymbolId is SymbolId sourceHirSymbolId)
            {
                _traceSink?.LinkValue(
                    sourceHirSymbolId,
                    _mirCallableId,
                    mirValueId);
            }
            return mirValue;
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
            MirCallTarget mirCallTarget,
            IReadOnlyList<MirCallOperand> mirCallOperands,
            IReadOnlyList<MirMutableArrayResult> mirMutableArrayResults,
            IReadOnlyList<MirFunctor> mirFunctors,
            MirOrigin mirOrigin)
        {
            var mirInstructionId = AllocateMirInstructionId();
            var mirQubitResults =
                new List<MirQubitAfterInstruction>();
            var writtenMirQubitIds = new HashSet<MirQubitId>();
            var writtenQubitOperandIndexes = WrittenQubitOperandIndexes(
                mirCallTarget,
                mirCallOperands,
                mirFunctors);
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

        private IReadOnlyList<int> WrittenQubitOperandIndexes(
            MirCallTarget mirCallTarget,
            IReadOnlyList<MirCallOperand> mirCallOperands,
            IReadOnlyList<MirFunctor> mirFunctors)
        {
            if (mirCallTarget is MirUserCallableTarget mirUserTarget)
            {
                var hirUserOperation = _hirArtifact.Source.Structure.FindNode(
                    new HirNodeId(mirUserTarget.Callable.Value)) as HirCallable
                    ?? throw MirLoweringError(
                        $"user operation target {mirUserTarget.Callable} "
                        + "has no matching HIR callable");
                return WrittenUserQubitOperandIndexes(hirUserOperation);
            }
            if (mirCallTarget is not MirBuiltinGateTarget mirBuiltinTarget
                || !QoraGates.Gates.TryGetValue(
                    mirBuiltinTarget.Name,
                    out var builtinGateInfo))
            {
                throw MirLoweringError(
                    $"quantum target `{mirCallTarget.DisplayName}` "
                    + "has no effect metadata");
            }

            var firstQubitOperandIndex =
                builtinGateInfo.AngleFirst ? 1 : 0;
            var qubitOperandIndexes = Enumerable.Range(
                firstQubitOperandIndex,
                mirCallOperands.Count - firstQubitOperandIndex).ToList();
            if (!builtinGateInfo.Unitary)
                return qubitOperandIndexes;
            if (builtinGateInfo.Diagonal)
                return Array.Empty<int>();

            var controlQubitCount = builtinGateInfo.Controls
                + mirFunctors.Count(
                    functor => functor == MirFunctor.Controlled);
            if (controlQubitCount > qubitOperandIndexes.Count)
            {
                throw MirLoweringError(
                    $"built-in gate `{mirBuiltinTarget.Name}` declares more controls "
                    + "than qubit operands");
            }

            return qubitOperandIndexes.Skip(controlQubitCount).ToArray();
        }

        private IReadOnlyList<int> WrittenUserQubitOperandIndexes(
            HirCallable hirUserOperation)
        {
            var hirOperationEffects =
                _hirArtifact.Model.FindOpEffects(hirUserOperation.Id)
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
            MirBlockBuilder mirJoinBlock,
            IReadOnlyList<MirQubitPhiInput> mirPhiInputs)
        {
            if (mirPhiInputs.Count == 0)
            {
                throw MirLoweringError(
                    "a qubit Phi requires at least one incoming value");
            }

            var mirQubitId = mirPhiInputs[0].Qubit.Id;
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
                new MirQubitVersion(nextMirQubitVersionValue),
                mirPhiInputs,
                mirJoinBlock.Origin);
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
            MirControlFlowEdge firstIncomingEdge,
            LoweringFlowState secondPredecessorFlowState,
            MirControlFlowEdge secondIncomingEdge,
            MirBlockBuilder mirJoinBlock)
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
                    mirJoinBlock,
                    new[]
                    {
                        new MirQubitPhiInput(
                            firstIncomingEdge,
                            firstIncomingMirQubit),
                        new MirQubitPhiInput(
                            secondIncomingEdge,
                            secondIncomingMirQubit),
                    });
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
            MirControlFlowEdge mirPreheaderEdge,
            MirBlockBuilder mirHeaderBlock)
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
                    mirHeaderBlock,
                    new[]
                    {
                        new MirQubitPhiInput(
                            mirPreheaderEdge,
                            incomingMirQubit),
                    });
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
            MirControlFlowEdge mirBackedgeEdge,
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
                        Inputs = MirCollections.Freeze(
                            loopQubitPhiBinding.MirQubitPhi.Inputs
                                .Append(new MirQubitPhiInput(
                                    mirBackedgeEdge,
                                    incomingMirQubit))),
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

        private MirControlFlowEdge JumpFromCurrentMirBlockTo(
            MirBlockBuilder targetMirBlock,
            IReadOnlyList<MirValueId> arguments,
            MirOrigin mirOrigin)
        {
            var currentMirBlock = RequireCurrentMirBlock();
            var mirControlFlowEdge = currentMirBlock.JumpTo(
                targetMirBlock,
                arguments,
                mirOrigin);
            return mirControlFlowEdge;
        }

        private (
            MirControlFlowEdge TrueEdge,
            MirControlFlowEdge FalseEdge) BranchFromCurrentMirBlockTo(
            MirValueId condition,
            MirBlockBuilder trueTargetMirBlock,
            IReadOnlyList<MirValueId> trueArguments,
            MirBlockBuilder falseTargetMirBlock,
            IReadOnlyList<MirValueId> falseArguments,
            MirOrigin mirOrigin)
        {
            var currentMirBlock = RequireCurrentMirBlock();
            var mirControlFlowEdges = currentMirBlock.BranchTo(
                condition,
                trueTargetMirBlock,
                trueArguments,
                falseTargetMirBlock,
                falseArguments,
                mirOrigin);
            return mirControlFlowEdges;
        }

        private void ReturnFromCurrentMirBlock(
            MirValueId? value,
            MirOrigin mirOrigin)
        {
            var currentMirBlock = RequireCurrentMirBlock();
            currentMirBlock.Return(value, mirOrigin);
        }

        private void MarkCurrentMirBlockUnreachable(MirOrigin mirOrigin)
        {
            var currentMirBlock = RequireCurrentMirBlock();
            currentMirBlock.MarkUnreachable(mirOrigin);
        }

        private void OnMirBlockTerminated(MirBlockBuilder terminatedMirBlock)
        {
            if (ReferenceEquals(_currentMirBlockBuilder, terminatedMirBlock))
                _currentMirBlockBuilder = null;
        }

        private MirBlockBuilder RequireCurrentMirBlock() =>
            _currentMirBlockBuilder
            ?? throw MirLoweringError(
                "attempted to emit into a terminated control-flow path");

        private MirBlockBuilder CreateMirBlock(MirOrigin mirOrigin)
        {
            var mirBlockBuilder = new MirBlockBuilder(
                this,
                AllocateMirBlockId(),
                mirOrigin);
            _mirBlockBuilders.Add(mirBlockBuilder);
            return mirBlockBuilder;
        }

        private MirBlockId AllocateMirBlockId() =>
            _mirBlockIds.Allocate();

        private MirInstructionId AllocateMirInstructionId() =>
            new(_nextMirInstructionIdValue++);

        private MirStorageId AllocateMirStorageId() =>
            new(_nextMirStorageIdValue++);

        private MirQubitId AllocateMirQubitId() =>
            new(_initialMirQubitsById.Count);

        private Symbol RequireHirDeclarationSymbol(
            HirNodeId hirDeclarationNodeId,
            string diagnosticRole) =>
            _hirArtifact.Model.FindSymbol(hirDeclarationNodeId)
            ?? throw MirLoweringError(
                $"{diagnosticRole} has no semantic symbol");

        private SymbolId SymbolIdOf(
            HirNameExpression hirNameReference,
            string diagnosticRole) =>
            _hirArtifact.Model
                .FindReferencedSymbol(hirNameReference.Id)?.Id
            ?? throw MirLoweringError(
                $"{diagnosticRole} has no semantic binding");

        private Symbol RequireHirCallTargetSymbol(
            HirCallExpression hirCall) =>
            _hirArtifact.Model
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
            return _hirArtifact.Source.Structure
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

        private static bool IsBuiltinLiteralName(
            string sourceLiteralName) =>
            sourceLiteralName
                is "true" or "false" or "pi" or "tau" or "euler";

        private MirOrigin MirOriginFor(HirNode sourceHirNode) =>
            MirOrigin.FromHirNode(_hirArtifact, sourceHirNode);

        private InvalidOperationException MirLoweringError(string message) =>
            new($"QINTERNAL: MIR lowering of `{_hirCallable.Name}` failed: {message}");

        private sealed record LoopQubitPhiBinding(
            MirQubitPhi MirQubitPhi,
            IReadOnlyList<SymbolId> HirSymbolIds);

        private sealed class MirBlockBuilder
        {
            private readonly HirCallableToMirLowerer _owner;
            private readonly List<MirValue> _arguments = new();
            private bool _hasIncomingControlFlow;
            private MirTerminator? _terminator;

            private MirBlockId Id { get; }
            public List<MirQubitPhi> QubitPhis { get; } = new();
            public List<MirInstruction> Instructions { get; } = new();
            public MirOrigin Origin { get; }

            public MirBlockBuilder(
                HirCallableToMirLowerer owner,
                MirBlockId mirBlockId,
                MirOrigin mirOrigin)
            {
                _owner = owner;
                Id = mirBlockId;
                Origin = mirOrigin;
            }

            public void AddArgument(MirValue mirValue)
            {
                ArgumentNullException.ThrowIfNull(mirValue);
                if (_hasIncomingControlFlow)
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: MIR block {Id} cannot add an argument after receiving an edge");
                }

                _arguments.Add(mirValue);
            }

            public MirControlFlowEdge JumpTo(
                MirBlockBuilder targetMirBlock,
                IReadOnlyList<MirValueId> arguments,
                MirOrigin mirOrigin)
            {
                ValidateTarget(targetMirBlock);
                Terminate(new MirJump(
                    targetMirBlock.Id,
                    arguments,
                    mirOrigin));
                targetMirBlock._hasIncomingControlFlow = true;
                return new MirControlFlowEdge(Id, successorOrdinal: 0);
            }

            public (
                MirControlFlowEdge TrueEdge,
                MirControlFlowEdge FalseEdge) BranchTo(
                MirValueId condition,
                MirBlockBuilder trueTargetMirBlock,
                IReadOnlyList<MirValueId> trueArguments,
                MirBlockBuilder falseTargetMirBlock,
                IReadOnlyList<MirValueId> falseArguments,
                MirOrigin mirOrigin)
            {
                ValidateTarget(trueTargetMirBlock);
                ValidateTarget(falseTargetMirBlock);
                Terminate(new MirBranch(
                    condition,
                    trueTargetMirBlock.Id,
                    trueArguments,
                    falseTargetMirBlock.Id,
                    falseArguments,
                    mirOrigin));
                trueTargetMirBlock._hasIncomingControlFlow = true;
                falseTargetMirBlock._hasIncomingControlFlow = true;
                return (
                    new MirControlFlowEdge(Id, successorOrdinal: 0),
                    new MirControlFlowEdge(Id, successorOrdinal: 1));
            }

            public void Return(
                MirValueId? value,
                MirOrigin mirOrigin) =>
                Terminate(new MirReturn(value, mirOrigin));

            public void MarkUnreachable(MirOrigin mirOrigin) =>
                Terminate(new MirUnreachable(mirOrigin));

            public MirBlock Build()
            {
                if (_terminator is null)
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: MIR block {Id} has no terminator");
                }

                return new MirBlock(
                    Id,
                    _arguments,
                    Instructions,
                    _terminator,
                    Origin,
                    QubitPhis);
            }

            private void Terminate(MirTerminator mirTerminator)
            {
                ArgumentNullException.ThrowIfNull(mirTerminator);
                ValidateRegistration(this);
                if (_terminator is not null)
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: MIR block {Id} already has a terminator");
                }

                _terminator = mirTerminator;
                _owner.OnMirBlockTerminated(this);
            }

            private void ValidateTarget(MirBlockBuilder targetMirBlock)
            {
                ArgumentNullException.ThrowIfNull(targetMirBlock);
                if (!ReferenceEquals(_owner, targetMirBlock._owner))
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: MIR block {targetMirBlock.Id} belongs to another callable lowerer");
                }
                ValidateRegistration(targetMirBlock);

                if (ReferenceEquals(
                        _owner._mirBlockBuilders[0],
                        targetMirBlock))
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: MIR entry block {targetMirBlock.Id} cannot have a predecessor");
                }
            }

            private void ValidateRegistration(MirBlockBuilder mirBlockBuilder)
            {
                var mirBlockIndex = mirBlockBuilder.Id.Value;
                if (mirBlockIndex >= _owner._mirBlockBuilders.Count
                    || !ReferenceEquals(
                        _owner._mirBlockBuilders[mirBlockIndex],
                        mirBlockBuilder))
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: MIR block {mirBlockBuilder.Id} is not registered in its callable lowerer");
                }
            }

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
