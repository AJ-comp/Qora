using System.Collections.ObjectModel;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir;

internal sealed record MirOpenQasmLoweringError(
    string Code,
    string Message,
    MirOrigin Origin);

internal sealed class MirOpenQasmLoweringResult
{
    private MirOpenQasmLoweringResult(
        MirOpenQasmTargetProgram? target,
        IReadOnlyList<MirOpenQasmLoweringError> errors)
    {
        Target = target;
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public MirOpenQasmTargetProgram? Target { get; }
    public IReadOnlyList<MirOpenQasmLoweringError> Errors { get; }
    public bool Success => Target is not null && Errors.Count == 0;

    internal static MirOpenQasmLoweringResult Succeeded(
        MirOpenQasmTargetProgram target) =>
        new(target, Array.Empty<MirOpenQasmLoweringError>());

    internal static MirOpenQasmLoweringResult Failed(
        MirOpenQasmLoweringError error) =>
        new(null, new[] { error });
}

/// <summary>
/// Lowers one exact, materialized MIR snapshot into the typed OpenQASM target model. MIR callable,
/// value, storage, qubit, and block identities are the only semantic references consumed here; final
/// spellings are allocated only after every reference has already been resolved.
///
/// Canonical CFG remains authoritative. Structured <c>if</c>/<c>while</c> statements are recovered from
/// snapshot-bound dominance, post-dominance, and natural-loop facts. Irreducible control flow fails
/// explicitly instead of being hidden behind a program-counter interpreter.
/// </summary>
internal static class MirOpenQasmLowering
{
    public static MirOpenQasmLoweringResult Lower(MirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            return MirOpenQasmLoweringResult.Succeeded(
                new ProgramLowerer(source).Lower());
        }
        catch (MirControlRegionException failure)
        {
            return MirOpenQasmLoweringResult.Failed(
                new MirOpenQasmLoweringError(
                    "QASM001",
                    $"MIR control flow cannot be structured for OpenQASM: {failure.Detail}",
                    failure.Origin));
        }
        catch (MirOpenQasmUnsupportedException failure)
        {
            return MirOpenQasmLoweringResult.Failed(
                new MirOpenQasmLoweringError(
                    failure.Code,
                    failure.Message,
                    failure.Origin));
        }
    }

    private sealed class ProgramLowerer
    {
        private readonly MirSnapshot _source;
        private readonly IReadOnlyDictionary<MirCallableId, MirQasmCallableId> _targetCallables;
        private readonly IReadOnlyDictionary<MirCallableId, string> _callableNames;
        private readonly HiddenArrayPlan _hiddenArrays;

        public ProgramLowerer(MirSnapshot source)
        {
            _source = source;
            var orderedCallables = source.Program.Callables
                .Where(callable => callable.Id != source.Program.EntryPoint.Id)
                .OrderBy(callable => callable.Id.Value)
                .ToArray();
            var targetCallables =
                new Dictionary<MirCallableId, MirQasmCallableId>(
                    orderedCallables.Length);
            for (var index = 0; index < orderedCallables.Length; index++)
            {
                var callable = orderedCallables[index];
                var targetId = new MirQasmCallableId(index);

                targetCallables.Add(callable.Id, targetId);
            }
            _targetCallables = targetCallables;
            _callableNames = AllocateCallableNames(source.Program.Callables);
            _hiddenArrays = HiddenArrayPlan.Build(source);
        }

        public MirOpenQasmTargetProgram Lower()
        {
            var entrySource = _source.Program.EntryPoint;
            var entryLowerer = new CallableLowerer(
                _source,
                entrySource,
                _targetCallables,
                _callableNames,
                _hiddenArrays);
            var entryBody = entryLowerer.LowerEntry();

            var definitions = new List<MirQasmCallableDefinition>(
                _source.Program.Callables.Count - 1);
            var definitionSources = _source.Program.Callables
                .Where(callable => callable.Id != _source.Program.EntryPoint.Id)
                .OrderBy(callable => callable.Id.Value);
            foreach (var callable in definitionSources)
            {
                var lowerer = new CallableLowerer(
                    _source,
                    callable,
                    _targetCallables,
                    _callableNames,
                    _hiddenArrays);
                var definition = lowerer.LowerDefinition();

                definitions.Add(definition);
            }

            return new MirOpenQasmTargetProgram(
                entryBody,
                definitions,
                _hiddenArrays.Notes);
        }

        private static IReadOnlyDictionary<MirCallableId, string> AllocateCallableNames(
            IReadOnlyList<MirCallable> callables)
        {
            var used = new HashSet<string>(
                QoraGates.QasmReserved,
                StringComparer.Ordinal);
            var names = new Dictionary<MirCallableId, string>();
            foreach (var callable in callables.OrderBy(callable => callable.Id.Value))
            {
                var stem = Identifier.Sanitize(callable.Name, $"callable_{callable.Id.Value}");
                names.Add(callable.Id, Identifier.Fresh(stem, used));
            }
            return new ReadOnlyDictionary<MirCallableId, string>(names);
        }
    }

    private sealed class CallableLowerer
    {
        private const int MaxExpandedArrayComparisonElements = 4096;

        private readonly MirProgram _program;
        private readonly MirCallable _callable;
        private readonly IReadOnlyDictionary<MirCallableId, MirQasmCallableId> _targetCallables;
        private readonly IReadOnlyDictionary<MirCallableId, string> _callableNames;
        private readonly HiddenArrayPlan _hiddenArrays;
        private readonly MirControlFlowSnapshot _controlFlow;
        private readonly MirControlRegionSnapshot _regions;
        private readonly MirStorageProvenanceSnapshot _storageProvenance;
        private readonly NameAllocator _names;

        private readonly List<MirQasmParameter> _parameters = new();
        private readonly List<MirQasmStatement> _prologue = new();
        private readonly Dictionary<MirValueId, MirQasmExpression> _scalarValues = new();
        private readonly Dictionary<MirStorageId, MirQasmExpression> _storagePlaces = new();
        private readonly Dictionary<MirQubitId, MirQasmExpression> _qubitBindings = new();
        private readonly Dictionary<HiddenStorageKey, MirQasmExpression> _hiddenPlaces = new();
        private readonly Dictionary<EdgeArgumentKey, MirQasmDeclarationId> _edgeTemporaries = new();
        private readonly HashSet<MirBlockId> _emittedBlocks = new();

        private MirQasmDeclarationId? _returnStorage;
        private MirQasmDeclarationId? _returnDone;
        private int _nextParameter;
        private int _nextDeclaration;

        public CallableLowerer(
            MirSnapshot snapshot,
            MirCallable callable,
            IReadOnlyDictionary<MirCallableId, MirQasmCallableId> targetCallables,
            IReadOnlyDictionary<MirCallableId, string> callableNames,
            HiddenArrayPlan hiddenArrays)
        {
            _program = snapshot.Program;
            _callable = _program.RequireCallable(callable);
            _targetCallables = targetCallables;
            _callableNames = callableNames;
            _hiddenArrays = hiddenArrays;
            _controlFlow = snapshot.Analyses.ControlFlow(callable);
            _regions = snapshot.Analyses.ControlRegions(callable);
            _storageProvenance = snapshot.Analyses.StorageProvenance(callable);

            var targetReserved = callable.Id == _program.EntryPoint.Id
                ? QoraGates.QasmReserved
                : QoraGates.QasmKeywords;
            var reserved = new HashSet<string>(
                targetReserved.Concat(callableNames.Values),
                StringComparer.Ordinal);
            _names = new NameAllocator(reserved);
            PrepareBindings();
        }

        public IReadOnlyList<MirQasmStatement> LowerEntry()
        {
            if (_callable.Id != _program.EntryPoint.Id)
                throw Internal("a definition lowerer was asked for an entry point");
            return LowerBody();
        }

        public MirQasmCallableDefinition LowerDefinition()
        {
            if (_callable.Id == _program.EntryPoint.Id)
                throw Internal("the entry lowerer was asked for a definition");
            var body = LowerBody();
            return new MirQasmCallableDefinition(
                TargetCallable(_callable.Id),
                _callableNames[_callable.Id],
                _parameters,
                _callable.ReturnType is MirType returns
                    ? TargetType(returns)
                    : null,
                body);
        }

        private IReadOnlyList<MirQasmStatement> LowerBody()
        {
            RejectOversizedArrayComparisonExpansions();

            var flow = new List<MirQasmStatement>();
            var outcome = EmitPath(
                _callable.EntryBlock.Id,
                stop: null,
                regionLoop: null,
                breakableLoop: null,
                flow);

            if (outcome.Kind == PathOutcomeKind.ReachedStop)
                throw Internal($"callable `{_callable.Name}` unexpectedly reached a missing outer join");

            var reachable = _controlFlow.ReachableBlocks.ToHashSet();
            var missing = reachable
                .Where(block => !_emittedBlocks.Contains(block))
                .OrderBy(block => block.Value)
                .ToArray();
            if (missing.Length != 0)
            {
                throw UnsupportedCfg(
                    $"structured lowering did not consume reachable block(s) " +
                    $"{string.Join(", ", missing)}");
            }

            if (_returnStorage is MirQasmDeclarationId returnStorage)
            {
                flow.Add(
                    new MirQasmReturnStatement(
                        new MirQasmDeclarationReferenceExpression(returnStorage)));
            }

            return _prologue.Concat(flow).ToArray();
        }

        private void RejectOversizedArrayComparisonExpansions()
        {
            foreach (var blockId in _controlFlow.ReachableBlocks)
            {
                var block = _callable.RequireBlock(blockId);
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is not MirBinary binary
                        || binary.Operator is not (
                            MirBinaryOperator.Equal or MirBinaryOperator.NotEqual))
                    {
                        continue;
                    }

                    var leftType = _callable.RequireValue(binary.Left).Type;
                    var rightType = _callable.RequireValue(binary.Right).Type;
                    if (!leftType.IsArray
                        || !rightType.IsArray
                        || leftType.ElementType == QType.Bit
                        || leftType.KnownLength is not int leftLength
                        || rightType.KnownLength != leftLength
                        || leftLength <= MaxExpandedArrayComparisonElements)
                    {
                        continue;
                    }

                    throw Unsupported(
                        $"array comparison {binary.Id} has {leftLength} elements; "
                        + "OpenQASM elementwise comparison expansion is limited to "
                        + $"{MaxExpandedArrayComparisonElements} elements",
                        binary.Origin);
                }
            }
        }

        private void PrepareBindings()
        {
            PrepareOriginalParameters();
            PrepareHiddenArrayBindings();
            PrepareLocalStorages();
            PrepareLocalQubitBindings();
            PrepareScalarValues();
            PrepareEdgeTemporaries();
            PrepareFunctionReturnState();
        }

        private void PrepareOriginalParameters()
        {
            foreach (var parameter in _callable.Parameters)
            {
                var targetId = NextParameter();
                var targetName = _names.Fresh(
                    Identifier.Sanitize(parameter.Name, $"p{targetId.Value}"));
                switch (parameter)
                {
                    case MirClassicalParameter classical:
                    {
                        var classicalType = classical.Value.Type;
                        if (classicalType is { IsArray: true, ElementType: QType.Bit }
                            && classical.Ownership == QOwnershipMode.Borrowed
                            && classical.Access == QAccessMode.Mutable)
                        {
                            throw Unsupported(
                                $"mutable bit-array parameter `{classical.Name}` requires caller-visible writes, " +
                                "but OpenQASM bit-register parameters are passed by value",
                                classical.Value.Origin);
                        }
                        var targetType = TargetType(classicalType);
                        var access = targetType is MirQasmArrayType
                            ? classical.Access == QAccessMode.Mutable
                                ? MirQasmParameterAccess.Mutable
                                : MirQasmParameterAccess.ReadOnly
                            : MirQasmParameterAccess.Value;
                        _parameters.Add(
                            new MirQasmParameter(
                                targetId,
                                targetName,
                                targetType,
                                access));
                        var place = new MirQasmParameterReferenceExpression(targetId);
                        if (classicalType.IsArray)
                        {
                            var storage = classical.Storage!;
                            _storagePlaces.Add(storage.Id, place);
                        }
                        else
                        {
                            _scalarValues.Add(classical.Value.Id, place);
                        }
                        break;
                    }

                    case MirQubitParameter qubit:
                    {
                        var count = qubit.IsArray
                            ? qubit.Length
                              ?? throw Unsupported(
                                  $"qubit-array parameter `{qubit.Name}` has unknown target width",
                                  qubit.Origin)
                            : 1;
                        _parameters.Add(
                            new MirQasmParameter(
                                targetId,
                                targetName,
                                new MirQasmQubitType(
                                    count,
                                    isRegister: qubit.IsArray)));
                        _qubitBindings.Add(
                            qubit.Id,
                            new MirQasmParameterReferenceExpression(targetId));
                        break;
                    }

                    default:
                        throw Internal(
                            $"unknown MIR parameter {parameter.GetType().Name}");
                }
            }
        }

        private void PrepareHiddenArrayBindings()
        {
            foreach (var key in _hiddenArrays.RequiredBy(_callable.Id))
            {
                var storageOwner = _program.RequireCallable(key.Owner);
                var storage = storageOwner.RequireStorage(key.Storage);
                var storageType = storageOwner.StorageTypeOf(storage);
                var targetType = RequireGeneralArrayType(storageType, storage.Name);
                MirQasmExpression place;
                if (_callable.Id == _program.EntryPoint.Id)
                {
                    var declaration = NextDeclaration();
                    var name = _names.Fresh(
                        Identifier.Sanitize(
                            $"{_callableNames[key.Owner]}_{storage.Name}_storage",
                            $"array_{key.Owner.Value}_{key.Storage.Value}"));
                    _prologue.Add(
                        new MirQasmArrayDeclarationStatement(
                            declaration,
                            name,
                            targetType,
                            Enumerable.Repeat(
                                DefaultLiteral(storageType.ElementType),
                                targetType.Length!.Value)));
                    place = new MirQasmDeclarationReferenceExpression(declaration);
                }
                else
                {
                    var parameter = NextParameter();
                    var name = _names.Fresh(
                        Identifier.Sanitize(
                            $"{_callableNames[key.Owner]}_{storage.Name}_storage",
                            $"array_{key.Owner.Value}_{key.Storage.Value}"));
                    _parameters.Add(
                        new MirQasmParameter(
                            parameter,
                            name,
                            targetType,
                            MirQasmParameterAccess.Mutable));
                    place = new MirQasmParameterReferenceExpression(parameter);
                }
                _hiddenPlaces.Add(key, place);
            }
        }

        private void PrepareLocalStorages()
        {
            foreach (var storage in _callable.Storages
                         .OrderBy(storage => storage.Id.Value))
            {
                if (_callable.StorageKindOf(storage)
                    != MirArrayStorageKind.Local)
                {
                    continue;
                }

                var storageType = _callable.StorageTypeOf(storage);
                if (storageType.ElementType != QType.Bit)
                {
                    var key = new HiddenStorageKey(_callable.Id, storage.Id);
                    if (!_hiddenPlaces.TryGetValue(key, out var hidden))
                    {
                        throw Internal(
                            $"local array storage {storage.Id} has no hidden OpenQASM backing");
                    }
                    _storagePlaces.Add(storage.Id, hidden);
                    continue;
                }

                var width = storageType.KnownLength
                    ?? throw Unsupported(
                        $"bit-array storage `{storage.Name}` has unknown target width",
                        storage.Origin);
                var declaration = NextDeclaration();
                var name = _names.Fresh(
                    Identifier.Sanitize(storage.Name, $"bits_{storage.Id.Value}"));
                _prologue.Add(
                    new MirQasmValueDeclarationStatement(
                        declaration,
                        name,
                        new MirQasmBitType(width, isRegister: true)));
                _storagePlaces.Add(
                    storage.Id,
                    new MirQasmDeclarationReferenceExpression(declaration));
            }
        }

        private void PrepareLocalQubitBindings()
        {
            foreach (var qubit in _callable.Qubits
                         .OfType<MirQubitFromUse>()
                         .OrderBy(qubit => qubit.Id.Value))
            {
                var count = qubit.Length;
                var declaration = NextDeclaration();
                var name = _names.Fresh(
                    Identifier.Sanitize(qubit.Name, $"q_{qubit.Id.Value}"));
                _prologue.Add(
                    new MirQasmQubitDeclarationStatement(
                        declaration,
                        name,
                        new MirQasmQubitType(
                            count,
                            isRegister: qubit.IsArray)));
                _qubitBindings.Add(
                    qubit.Id,
                    new MirQasmDeclarationReferenceExpression(declaration));
            }
        }

        private void PrepareScalarValues()
        {
            foreach (var value in _callable.Values
                         .Where(value => !value.Type.IsArray)
                         .OrderBy(value => value.Id.Value))
            {
                if (_scalarValues.ContainsKey(value.Id)) continue;
                var declaration = NextDeclaration();
                var name = _names.Fresh($"v{value.Id.Value}");
                _prologue.Add(
                    new MirQasmValueDeclarationStatement(
                        declaration,
                        name,
                        TargetType(value.Type)));
                _scalarValues.Add(
                    value.Id,
                    new MirQasmDeclarationReferenceExpression(declaration));
            }
        }

        private void PrepareEdgeTemporaries()
        {
            foreach (var block in _callable.Blocks.OrderBy(block => block.Id.Value))
            {
                switch (block.Terminator)
                {
                    case MirJump:
                        PrepareEdge(block, 0);
                        break;
                    case MirBranch:
                        PrepareEdge(block, 0);
                        PrepareEdge(block, 1);
                        break;
                }
            }
        }

        private void PrepareEdge(
            MirBlock sourceBlock,
            int successorOrdinal)
        {
            var (target, arguments) = EdgeOf(sourceBlock, successorOrdinal);
            var parameters = _callable.RequireBlock(target).Arguments;
            for (var index = 0; index < arguments.Count; index++)
            {
                var parameterType = parameters[index].Type;
                if (parameterType.IsArray) continue;
                var declaration = NextDeclaration();
                var name = _names.Fresh(
                    $"edge_{sourceBlock.Id.Value}_{successorOrdinal}_{index}");
                _prologue.Add(
                    new MirQasmValueDeclarationStatement(
                        declaration,
                        name,
                        TargetType(parameterType)));
                _edgeTemporaries.Add(
                    new EdgeArgumentKey(sourceBlock.Id, successorOrdinal, index),
                    declaration);
            }
        }

        private void PrepareFunctionReturnState()
        {
            if (_callable.ReturnType is not MirType returnType) return;

            var returnStorage = NextDeclaration();
            _returnStorage = returnStorage;
            _prologue.Add(
                new MirQasmValueDeclarationStatement(
                    returnStorage,
                    _names.Fresh("ret"),
                    TargetType(returnType),
                    DefaultLiteral(returnType.ElementType)));

            // The flag is needed only to propagate a return across one or more target while scopes.
            // Keeping the decision structural avoids a source-language heuristic.
            if (!_regions.HasNaturalLoops) return;
            _returnDone = NextDeclaration();
            _prologue.Add(
                new MirQasmValueDeclarationStatement(
                    _returnDone.Value,
                    _names.Fresh("return_done"),
                    new MirQasmScalarType(MirQasmScalarKind.Int),
                    new MirQasmLiteralExpression("0")));
        }

        private PathOutcome EmitPath(
            MirBlockId start,
            MirBlockId? stop,
            MirNaturalLoopRegion? regionLoop,
            MirNaturalLoopRegion? breakableLoop,
            List<MirQasmStatement> output)
        {
            var current = start;
            while (true)
            {
                if (stop is MirBlockId join && current == join)
                    return new PathOutcome(PathOutcomeKind.ReachedStop, ContainsReturn: false);

                if (regionLoop is not null
                    && current == regionLoop.Header
                    && _emittedBlocks.Contains(current))
                {
                    // A branch backedge reaches the loop header through an arm rather than a MirJump.
                    // Its Phi transfers were emitted before entering this path; ending the target while
                    // body now starts the next dynamic iteration without duplicating the static header.
                    return new PathOutcome(
                        PathOutcomeKind.LoopBoundary,
                        ContainsReturn: false);
                }

                if (regionLoop is not null && !regionLoop.Contains(current))
                {
                    if (regionLoop.NormalExit == current)
                    {
                        output.Add(new MirQasmBreakStatement());
                        return new PathOutcome(
                            PathOutcomeKind.LoopBoundary,
                            ContainsReturn: false);
                    }

                    if (!regionLoop.IsCallableReturnSideExit(current))
                    {
                        throw UnsupportedCfg(
                            $"loop {regionLoop.Header} reached unclassified outside block {current}");
                    }

                    var terminal = EmitPath(
                        current,
                        stop: null,
                        regionLoop: null,
                        breakableLoop,
                        output);
                    if (terminal.Kind != PathOutcomeKind.Terminated)
                    {
                        throw UnsupportedCfg(
                            $"loop {regionLoop.Header} has a non-terminal callable-return " +
                            $"side exit at {current}");
                    }
                    return terminal;
                }

                if (_regions.TryGetLoop(current, out var nested)
                    && nested is not null
                    && (regionLoop is null || nested.Header != regionLoop.Header))
                {
                    var loopBody = new List<MirQasmStatement>();
                    var loopOutcome = EmitPath(
                        nested.Header,
                        stop: null,
                        regionLoop: nested,
                        breakableLoop: nested,
                        loopBody);
                    if (loopOutcome.Kind == PathOutcomeKind.ReachedStop)
                    {
                        throw UnsupportedCfg(
                            $"loop {nested.Header} reached an unowned join");
                    }
                    output.Add(
                        new MirQasmWhileStatement(
                            new MirQasmLiteralExpression("true"),
                            loopBody));

                    if (nested.NormalExit is not MirBlockId loopExit)
                        return new PathOutcome(
                            PathOutcomeKind.Terminated,
                            loopOutcome.ContainsReturn);

                    if (loopOutcome.ContainsReturn)
                    {
                        var continuation = new List<MirQasmStatement>();
                        var tail = EmitPath(
                            loopExit,
                            stop,
                            regionLoop,
                            breakableLoop,
                            continuation);
                        var returnedBranch = new List<MirQasmStatement>();
                        if (breakableLoop is not null)
                            returnedBranch.Add(
                                new MirQasmBreakStatement());
                        output.Add(
                            new MirQasmIfStatement(
                                ReturnDoneCondition(),
                                returnedBranch,
                                continuation));
                        return breakableLoop is null && stop is null
                            ? new PathOutcome(
                                PathOutcomeKind.Terminated,
                                ContainsReturn: true)
                            : tail with { ContainsReturn = true };
                    }

                    current = loopExit;
                    continue;
                }

                if (!_emittedBlocks.Add(current))
                {
                    throw UnsupportedCfg(
                        $"block {current} would be emitted more than once outside a loop backedge");
                }

                var block = _callable.RequireBlock(current);
                EmitInstructions(block, output);
                switch (block.Terminator)
                {
                    case MirReturn returned:
                        return EmitReturn(returned, breakableLoop, output);

                    case MirUnreachable:
                        throw UnsupportedCfg(
                            $"reachable block {current} ends in `unreachable`");

                    case MirJump jump:
                        EmitEdge(block, successorOrdinal: 0, output);
                        if (regionLoop is not null
                            && jump.Target == regionLoop.Header)
                        {
                            return new PathOutcome(
                                PathOutcomeKind.LoopBoundary,
                                ContainsReturn: false);
                        }
                        if (regionLoop is not null
                            && regionLoop.NormalExit == jump.Target)
                        {
                            output.Add(
                                new MirQasmBreakStatement());
                            return new PathOutcome(
                                PathOutcomeKind.LoopBoundary,
                                ContainsReturn: false);
                        }
                        current = jump.Target;
                        continue;

                    case MirBranch:
                        return EmitBranch(
                            block,
                            stop,
                            regionLoop,
                            breakableLoop,
                            output);

                    default:
                        throw Internal(
                            $"unknown MIR terminator {block.Terminator.GetType().Name}");
                }
            }
        }

        private PathOutcome EmitBranch(
            MirBlock sourceBlock,
            MirBlockId? outerStop,
            MirNaturalLoopRegion? regionLoop,
            MirNaturalLoopRegion? breakableLoop,
            List<MirQasmStatement> output)
        {
            var branch = (MirBranch)sourceBlock.Terminator;
            var source = sourceBlock.Id;
            var join = FindStructuredJoin(
                sourceBlock,
                outerStop,
                regionLoop);
            var thenBody = new List<MirQasmStatement>();
            var elseBody = new List<MirQasmStatement>();
            EmitEdge(sourceBlock, 0, thenBody);
            EmitEdge(sourceBlock, 1, elseBody);

            if (join is MirBlockId merge)
            {
                var thenOutcome = EmitPath(
                    branch.TrueTarget,
                    merge,
                    regionLoop,
                    breakableLoop,
                    thenBody);
                var elseOutcome = EmitPath(
                    branch.FalseTarget,
                    merge,
                    regionLoop,
                    breakableLoop,
                    elseBody);
                if (thenOutcome.Kind != PathOutcomeKind.ReachedStop
                    || elseOutcome.Kind != PathOutcomeKind.ReachedStop)
                {
                    throw UnsupportedCfg(
                        $"candidate join {merge} does not close both arms of branch {source}");
                }

                output.Add(
                    new MirQasmIfStatement(
                        ValueExpression(branch.Condition),
                        thenBody,
                        elseBody));
                var continuation = new List<MirQasmStatement>();
                var tail = EmitPath(
                    merge,
                    outerStop,
                    regionLoop,
                    breakableLoop,
                    continuation);
                var containsReturn =
                    tail.ContainsReturn
                    || thenOutcome.ContainsReturn
                    || elseOutcome.ContainsReturn;
                if (thenOutcome.ContainsReturn || elseOutcome.ContainsReturn)
                {
                    if (_returnDone is null)
                    {
                        throw UnsupportedCfg(
                            $"branch {source} has a partial return but no return-flow state");
                    }
                    var returnedBranch = new List<MirQasmStatement>();
                    if (breakableLoop is not null)
                        returnedBranch.Add(
                            new MirQasmBreakStatement());
                    output.Add(
                        new MirQasmIfStatement(
                            ReturnDoneCondition(),
                            returnedBranch,
                            continuation));
                    return breakableLoop is null && outerStop is null
                        ? new PathOutcome(
                            PathOutcomeKind.Terminated,
                            ContainsReturn: true)
                        : tail with { ContainsReturn = true };
                }

                output.AddRange(continuation);
                return tail with { ContainsReturn = containsReturn };
            }

            var trueStop = outerStop;
            var falseStop = outerStop;
            if (outerStop is MirBlockId stop)
            {
                var trueReaches = _controlFlow.CanReach(branch.TrueTarget, stop);
                var falseReaches = _controlFlow.CanReach(branch.FalseTarget, stop);
                if (trueReaches != falseReaches && _returnDone is null)
                {
                    // One arm returns or leaves the active loop. Consume the remaining continuation inside
                    // the other arm so the terminated path cannot fall through into it after target emission.
                    trueStop = null;
                    falseStop = null;
                }
            }

            var trueOutcome = EmitPath(
                branch.TrueTarget,
                trueStop,
                regionLoop,
                breakableLoop,
                thenBody);
            var falseOutcome = EmitPath(
                branch.FalseTarget,
                falseStop,
                regionLoop,
                breakableLoop,
                elseBody);
            output.Add(
                new MirQasmIfStatement(
                    ValueExpression(branch.Condition),
                    thenBody,
                    elseBody));
            return CombineBranchOutcomes(trueOutcome, falseOutcome);
        }

        private MirBlockId? FindStructuredJoin(
            MirBlock sourceBlock,
            MirBlockId? outerStop,
            MirNaturalLoopRegion? loop)
        {
            var branch = (MirBranch)sourceBlock.Terminator;
            var source = sourceBlock.Id;
            var candidates = _controlFlow.ReachableBlocks
                .Where(candidate => candidate != source)
                .Where(candidate => !_emittedBlocks.Contains(candidate))
                .Where(
                    candidate => loop is null
                                 || loop.Contains(candidate)
                                 && candidate != loop.Header)
                .Where(candidate => loop?.NormalExit != candidate)
                .Where(
                    candidate => _controlFlow.CanReach(branch.TrueTarget, candidate)
                                 && _controlFlow.CanReach(branch.FalseTarget, candidate))
                .Where(
                    candidate => _controlFlow.PostDominates(candidate, branch.TrueTarget)
                                 && _controlFlow.PostDominates(candidate, branch.FalseTarget))
                .ToArray();
            if (candidates.Length == 0 && _returnDone is not null)
            {
                // A return inside a loop prevents the normal continuation from structurally
                // post-dominating the branch, even though every non-returning path still joins there.
                // The target return_done state guards that continuation, so recover the nearest common
                // reachable block without weakening CFG authority or duplicating it.
                candidates = _controlFlow.ReachableBlocks
                    .Where(candidate => candidate != source)
                    .Where(candidate => !_emittedBlocks.Contains(candidate))
                    .Where(
                        candidate => loop is null
                                     || loop.Contains(candidate)
                                     && candidate != loop.Header)
                    .Where(candidate => loop?.NormalExit != candidate)
                    .Where(
                        candidate => _controlFlow.CanReach(branch.TrueTarget, candidate)
                                     && _controlFlow.CanReach(branch.FalseTarget, candidate))
                    .ToArray();
            }
            if (outerStop is MirBlockId stop
                && !_emittedBlocks.Contains(stop)
                && (loop is null || loop.Contains(stop))
                && _controlFlow.CanReach(branch.TrueTarget, stop)
                && _controlFlow.CanReach(branch.FalseTarget, stop)
                && _controlFlow.PostDominates(stop, branch.TrueTarget)
                && _controlFlow.PostDominates(stop, branch.FalseTarget))
            {
                candidates = candidates.Append(stop).Distinct().ToArray();
            }
            if (candidates.Length == 0) return null;

            return candidates
                .Select(
                    candidate => new
                    {
                        Block = candidate,
                        TrueDistance = Distance(branch.TrueTarget, candidate, loop),
                        FalseDistance = Distance(branch.FalseTarget, candidate, loop),
                    })
                .Where(item => item.TrueDistance is not null && item.FalseDistance is not null)
                .OrderBy(
                    item => Math.Max(
                        item.TrueDistance!.Value,
                        item.FalseDistance!.Value))
                .ThenBy(
                    item => item.TrueDistance!.Value + item.FalseDistance!.Value)
                .ThenBy(item => item.Block.Value)
                .Select(item => (MirBlockId?)item.Block)
                .FirstOrDefault();
        }

        private int? Distance(
            MirBlockId start,
            MirBlockId target,
            MirNaturalLoopRegion? loop)
        {
            var pending = new Queue<(MirBlockId Block, int Distance)>();
            var visited = new HashSet<MirBlockId>();
            pending.Enqueue((start, 0));
            while (pending.TryDequeue(out var current))
            {
                if (!visited.Add(current.Block)) continue;
                if (current.Block == target) return current.Distance;
                foreach (var successor in _controlFlow.SuccessorsOf(current.Block))
                {
                    if (loop is not null
                        && (!loop.Contains(successor)
                            || successor == loop.Header
                            || successor == loop.NormalExit))
                        continue;
                    pending.Enqueue((successor, checked(current.Distance + 1)));
                }
            }
            return null;
        }

        private static PathOutcome CombineBranchOutcomes(
            PathOutcome left,
            PathOutcome right)
        {
            var containsReturn = left.ContainsReturn || right.ContainsReturn;
            if (left.Kind == right.Kind)
                return new PathOutcome(left.Kind, containsReturn);
            if (left.Kind == PathOutcomeKind.Terminated)
                return right with { ContainsReturn = containsReturn };
            if (right.Kind == PathOutcomeKind.Terminated)
                return left with { ContainsReturn = containsReturn };
            if (left.Kind == PathOutcomeKind.LoopBoundary
                || right.Kind == PathOutcomeKind.LoopBoundary)
            {
                return new PathOutcome(
                    PathOutcomeKind.LoopBoundary,
                    containsReturn);
            }
            return new PathOutcome(PathOutcomeKind.ReachedStop, containsReturn);
        }

        private PathOutcome EmitReturn(
            MirReturn returned,
            MirNaturalLoopRegion? breakableLoop,
            List<MirQasmStatement> output)
        {
            if (returned.Value is not MirValueId returnedValue)
            {
                return new PathOutcome(
                    PathOutcomeKind.Terminated,
                    ContainsReturn: false);
            }
            if (_returnStorage is not MirQasmDeclarationId returnStorage)
                throw Internal($"function `{_callable.Name}` has no return storage");

            output.Add(
                new MirQasmAssignmentStatement(
                    new MirQasmDeclarationReferenceExpression(returnStorage),
                    ValueExpression(returnedValue)));

            if (_returnDone is MirQasmDeclarationId done)
            {
                output.Add(
                    new MirQasmAssignmentStatement(
                        new MirQasmDeclarationReferenceExpression(done),
                        new MirQasmLiteralExpression("1")));
            }

            if (breakableLoop is not null)
            {
                if (_returnDone is null)
                    throw Internal(
                        $"loop return in `{_callable.Name}` has no propagation flag");
                output.Add(new MirQasmBreakStatement());
            }
            return new PathOutcome(
                PathOutcomeKind.Terminated,
                ContainsReturn: true);
        }

        private MirQasmExpression ReturnDoneCondition()
        {
            if (_returnDone is not MirQasmDeclarationId done)
                throw Internal($"function `{_callable.Name}` has no return propagation flag");
            return new MirQasmBinaryExpression(
                MirQasmBinaryOperator.Equal,
                new MirQasmDeclarationReferenceExpression(done),
                new MirQasmLiteralExpression("1"));
        }

        private void EmitInstructions(
            MirBlock block,
            List<MirQasmStatement> output)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case MirConstant constant:
                        Assign(
                            constant.Result.Id,
                            new MirQasmLiteralExpression(constant.Text),
                            output);
                        break;

                    case MirUnary unary:
                        Assign(
                            unary.Result.Id,
                            new MirQasmUnaryExpression(
                                unary.Operator switch
                                {
                                    MirUnaryOperator.Negate =>
                                        MirQasmUnaryOperator.Negate,
                                    MirUnaryOperator.LogicalNot =>
                                        MirQasmUnaryOperator.LogicalNot,
                                    _ => throw Internal(
                                        $"unknown MIR unary operator {unary.Operator}"),
                                },
                                ValueExpression(unary.Operand)),
                            output);
                        break;

                    case MirBinary binary:
                        EmitBinary(binary, output);
                        break;

                    case MirConvert convert:
                        Assign(
                            convert.Result.Id,
                            ConvertExpression(convert),
                            output);
                        break;

                    case MirArrayCreate create:
                        EmitArrayInitialization(create, output);
                        break;

                    case MirArrayLength length:
                    {
                        var arrayType =
                            _callable.RequireValue(length.Array).Type;
                        MirQasmExpression expression = arrayType.KnownLength is int known
                            ? new MirQasmLiteralExpression(
                                known.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture))
                            : arrayType.ElementType == QType.Bit
                                ? throw Unsupported(
                                    "a dynamic-width bit register has no OpenQASM sizeof form",
                                    length.Origin)
                                : new MirQasmSizeOfExpression(
                                    ValueExpression(length.Array));
                        Assign(length.Result.Id, expression, output);
                        break;
                    }

                    case MirArrayLoad load:
                        Assign(
                            load.Result.Id,
                            new MirQasmIndexExpression(
                                ValueExpression(load.Array),
                                ValueExpression(load.Index)),
                            output);
                        break;

                    case MirArrayStore store:
                        output.Add(
                            new MirQasmAssignmentStatement(
                                new MirQasmIndexExpression(
                                    ValueExpression(store.Array),
                                    ValueExpression(store.Index)),
                                ValueExpression(store.Value)));
                        RequireSameArrayPlace(
                            store.Array,
                            store.Result.Id,
                            "array.store",
                            store.Origin);
                        break;

                    case MirPureCall call:
                        Assign(
                            call.Result.Id,
                            PureCallExpression(call),
                            output);
                        break;

                    case MirQubitAllocate allocation:
                        // Physical qubits are declared in the target prologue. The instruction remains
                        // the MIR lifetime marker but has no second textual OpenQASM operation.
                        _ = QubitBindingExpression(allocation.Result.Id);
                        break;

                    case MirQuantumApply apply:
                        output.Add(LowerQuantumApply(apply));
                        foreach (var result in apply.MutableArrayResults)
                        {
                            var input = (MirClassicalCallOperand)
                                apply.Operands[result.OperandIndex];
                            RequireSameArrayPlace(
                                input.Value,
                                result.Result.Id,
                                "mutable call result",
                                apply.Origin);
                        }
                        break;

                    case MirMeasure measure:
                        output.Add(
                            new MirQasmMeasurementAssignmentStatement(
                                ValueExpression(measure.Result.Id),
                                QubitAccessExpression(measure.Qubit)));
                        break;

                    default:
                        throw Internal(
                            $"unknown MIR instruction {instruction.GetType().Name}");
                }
            }
        }

        private void EmitArrayInitialization(
            MirArrayCreate create,
            List<MirQasmStatement> output)
        {
            if (!_storagePlaces.TryGetValue(create.Storage.Id, out var storage))
                throw Internal($"array.create references missing storage {create.Storage.Id}");
            var arrayType = create.Result.Type;
            var length = arrayType.KnownLength!.Value;

            for (var index = 0; index < length; index++)
            {
                var value = create.Initialization == MirArrayInitialization.ExplicitElements
                    ? ValueExpression(create.Elements[index])
                    : DefaultLiteral(arrayType.ElementType);
                output.Add(
                    new MirQasmAssignmentStatement(
                        new MirQasmIndexExpression(
                            storage,
                            new MirQasmLiteralExpression(
                                index.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture))),
                        value));
            }
        }

        private MirQasmExpression PureCallExpression(MirPureCall call)
        {
            var arguments = call.Operands
                .Select(CallOperandExpression)
                .ToList();
            switch (call.Target)
            {
                case MirUserCallableTarget user:
                    if (user.Callable == _program.EntryPoint.Id)
                    {
                        throw Unsupported(
                            "the MIR entry point cannot be called as a function",
                            call.Origin);
                    }
                    arguments.AddRange(HiddenArgumentsFor(user.Callable));
                    return new MirQasmFunctionCallExpression(
                        new MirQasmUserFunctionTarget(TargetCallable(user.Callable)),
                        arguments);

                case MirBuiltinFunctionTarget { Name: var name }
                    when name == QoraGates.BitsAsInt:
                {
                    var operand = (MirClassicalCallOperand)call.Operands[0];
                    var width = _callable
                        .RequireValue(operand.Value)
                        .Type
                        .KnownLength
                        ?? throw Unsupported(
                            $"`{name}` requires a fixed-width bit register",
                            call.Origin);
                    return new MirQasmUnsignedCastExpression(
                        width,
                        ValueExpression(operand.Value));
                }

                case MirBuiltinFunctionTarget builtin:
                    return new MirQasmFunctionCallExpression(
                        new MirQasmBuiltinFunctionTarget(
                            Identifier.Sanitize(builtin.Name, "builtin")),
                        arguments);

                default:
                    throw Internal(
                        $"invalid pure-call target {call.Target.GetType().Name}");
            }
        }

        private MirQasmQuantumApplyStatement LowerQuantumApply(
            MirQuantumApply apply)
        {
            var allArguments = apply.Operands
                .Select(CallOperandExpression)
                .ToList();
            switch (apply.Target)
            {
                case MirUserCallableTarget user:
                    if (apply.Functors.Count != 0)
                    {
                        var modifiers = string.Join(", ", apply.Functors);
                        throw Unsupported(
                            $"user callable {user.Callable} still carries unmaterialized modifier(s): " +
                            modifiers,
                            apply.Origin);
                    }
                    if (user.Callable == _program.EntryPoint.Id)
                    {
                        throw Unsupported(
                            "the MIR entry point cannot be called",
                            apply.Origin);
                    }
                    allArguments.AddRange(HiddenArgumentsFor(user.Callable));
                    return new MirQasmQuantumApplyStatement(
                        new MirQasmUserQuantumTarget(TargetCallable(user.Callable)),
                        Array.Empty<MirQasmExpression>(),
                        allArguments);

                case MirBuiltinGateTarget builtin:
                {
                    if (!QoraGates.Gates.TryGetValue(builtin.Name, out var gate))
                    {
                        throw Unsupported(
                            $"unknown OpenQASM built-in gate `{builtin.Name}`",
                            apply.Origin);
                    }
                    var gateParameters = new List<MirQasmExpression>();
                    var operands = new List<MirQasmExpression>();
                    for (var index = 0; index < apply.Operands.Count; index++)
                    {
                        if (gate.AngleFirst && index == 0)
                            gateParameters.Add(allArguments[index]);
                        else
                            operands.Add(allArguments[index]);
                    }
                    var modifiers = apply.Functors.Select(
                        functor => functor switch
                        {
                            MirFunctor.Adjoint =>
                                MirQasmQuantumModifier.Inverse,
                            MirFunctor.Controlled =>
                                MirQasmQuantumModifier.Controlled,
                            _ => throw Internal(
                                $"unknown MIR functor {functor}"),
                        });
                    return new MirQasmQuantumApplyStatement(
                        new MirQasmBuiltinGateTarget(gate.QasmName),
                        gateParameters,
                        operands,
                        modifiers);
                }

                default:
                    throw Internal(
                        $"invalid quantum-call target {apply.Target.GetType().Name}");
            }
        }

        private IEnumerable<MirQasmExpression> HiddenArgumentsFor(
            MirCallableId callee)
        {
            foreach (var key in _hiddenArrays.RequiredBy(callee))
            {
                if (!_hiddenPlaces.TryGetValue(key, out var place))
                {
                    throw Internal(
                        $"caller `{_callable.Name}` has no forwarded backing for " +
                        $"{key.Owner}/{key.Storage}");
                }
                yield return place;
            }
        }

        private void EmitEdge(
            MirBlock sourceBlock,
            int successorOrdinal,
            List<MirQasmStatement> output)
        {
            var source = sourceBlock.Id;
            var (target, arguments) = EdgeOf(sourceBlock, successorOrdinal);
            var parameters = _callable.RequireBlock(target).Arguments;
            var scalarTransfers = new List<(
                MirQasmDeclarationId Temporary,
                MirQasmExpression Destination,
                MirQasmExpression Source)>();
            for (var index = 0; index < arguments.Count; index++)
            {
                var destination = parameters[index];
                var destinationType = destination.Type;
                if (destinationType.IsArray)
                {
                    RequireSameArrayPlace(
                        arguments[index],
                        destination.Id,
                        $"edge {source}->{target}",
                        sourceBlock.Terminator.Origin);
                    continue;
                }

                var key = new EdgeArgumentKey(source, successorOrdinal, index);
                if (!_edgeTemporaries.TryGetValue(key, out var temporary))
                    throw Internal($"edge argument {key} has no target temporary");
                scalarTransfers.Add(
                    (temporary,
                        ValueExpression(destination.Id),
                        ValueExpression(arguments[index])));
            }

            // Read every source before writing any Phi destination. This preserves simultaneous edge
            // assignment even for swaps such as (x, y) -> (y, x).
            foreach (var transfer in scalarTransfers)
            {
                output.Add(
                    new MirQasmAssignmentStatement(
                        new MirQasmDeclarationReferenceExpression(
                            transfer.Temporary),
                        transfer.Source));
            }
            foreach (var transfer in scalarTransfers)
            {
                output.Add(
                    new MirQasmAssignmentStatement(
                        transfer.Destination,
                        new MirQasmDeclarationReferenceExpression(
                            transfer.Temporary)));
            }
        }

        private (MirBlockId Target, IReadOnlyList<MirValueId> Arguments) EdgeOf(
            MirBlock sourceBlock,
            int successorOrdinal)
        {
            return sourceBlock.Terminator switch
            {
                MirJump jump when successorOrdinal == 0 =>
                    (jump.Target, jump.Arguments),
                MirBranch branch when successorOrdinal == 0 =>
                    (branch.TrueTarget, branch.TrueArguments),
                MirBranch branch when successorOrdinal == 1 =>
                    (branch.FalseTarget, branch.FalseArguments),
                _ => throw Internal(
                    $"block {sourceBlock.Id} has no successor {successorOrdinal}"),
            };
        }

        private void Assign(
            MirValueId result,
            MirQasmExpression value,
            List<MirQasmStatement> output) =>
            output.Add(
                new MirQasmAssignmentStatement(
                    ValueExpression(result),
                    value));

        private void EmitBinary(
            MirBinary binary,
            List<MirQasmStatement> output)
        {
            var leftType = _callable.RequireValue(binary.Left).Type;
            var rightType = _callable.RequireValue(binary.Right).Type;
            if (!leftType.IsArray && !rightType.IsArray)
            {
                Assign(
                    binary.Result.Id,
                    new MirQasmBinaryExpression(
                        BinaryOperator(binary.Operator),
                        ValueExpression(binary.Left),
                        ValueExpression(binary.Right)),
                    output);
                return;
            }

            if (!leftType.IsArray
                || !rightType.IsArray
                || leftType.ElementType != rightType.ElementType
                || binary.Operator is not (
                    MirBinaryOperator.Equal or MirBinaryOperator.NotEqual))
            {
                throw Internal(
                    $"invalid MIR array comparison {binary.Operator} "
                    + $"between {leftType} and {rightType}");
            }

            if (leftType.KnownLength is not int leftLength
                || rightType.KnownLength is not int rightLength)
            {
                throw Unsupported(
                    $"array comparison {binary.Id} requires fixed operand lengths",
                    binary.Origin);
            }

            if (leftLength != rightLength)
            {
                var result = binary.Operator == MirBinaryOperator.Equal ? "0" : "1";
                Assign(binary.Result.Id, new MirQasmLiteralExpression(result), output);
                return;
            }

            var left = ValueExpression(binary.Left);
            var right = ValueExpression(binary.Right);
            if (leftType.ElementType == QType.Bit)
            {
                Assign(
                    binary.Result.Id,
                    new MirQasmBinaryExpression(
                        BinaryOperator(binary.Operator),
                        left,
                        right),
                    output);
                return;
            }

            if (leftLength > MaxExpandedArrayComparisonElements)
            {
                throw Unsupported(
                    $"array comparison {binary.Id} has {leftLength} elements; "
                    + "OpenQASM elementwise comparison expansion is limited to "
                    + $"{MaxExpandedArrayComparisonElements} elements",
                    binary.Origin);
            }

            Assign(
                binary.Result.Id,
                FixedArrayComparisonExpression(
                    binary.Operator,
                    left,
                    right,
                    leftLength),
                output);
        }

        private static MirQasmExpression FixedArrayComparisonExpression(
            MirBinaryOperator comparison,
            MirQasmExpression left,
            MirQasmExpression right,
            int length)
        {
            if (length == 0)
            {
                return new MirQasmLiteralExpression(
                    comparison == MirBinaryOperator.Equal ? "1" : "0");
            }

            var elementComparison = BinaryOperator(comparison);
            var combination = comparison == MirBinaryOperator.Equal
                ? MirQasmBinaryOperator.LogicalAnd
                : MirQasmBinaryOperator.LogicalOr;
            MirQasmExpression result = CompareElement(0);
            for (var index = 1; index < length; index++)
            {
                result = new MirQasmBinaryExpression(
                    combination,
                    result,
                    CompareElement(index));
            }
            return result;

            MirQasmExpression CompareElement(int index)
            {
                var literalIndex = new MirQasmLiteralExpression(
                    index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                return new MirQasmBinaryExpression(
                    elementComparison,
                    new MirQasmIndexExpression(left, literalIndex),
                    new MirQasmIndexExpression(right, literalIndex));
            }
        }

        private MirQasmExpression ValueExpression(MirValueId value)
        {
            var mirValue = _callable.RequireValue(value);
            var type = mirValue.Type;
            if (!type.IsArray)
            {
                return _scalarValues.TryGetValue(value, out var scalar)
                    ? scalar
                    : throw Internal($"scalar value {value} has no target binding");
            }

            var provenance = _storageProvenance.ProvenanceOf(value);
            if (!provenance.IsComplete
                || provenance.PossibleStorages.Count != 1)
            {
                throw Unsupported(
                    $"array SSA value {value} has " +
                    $"{(provenance.IsComplete ? "ambiguous" : "incomplete")} storage provenance",
                    mirValue.Origin);
            }
            var storage = provenance.PossibleStorages[0];
            return _storagePlaces.TryGetValue(storage, out var place)
                ? place
                : throw Internal(
                    $"array value {value} resolves to unbound storage {storage}");
        }

        private MirQasmExpression QubitBindingExpression(MirQubitId qubit)
        {
            return _qubitBindings.TryGetValue(qubit, out var binding)
                ? binding
                : throw Internal($"qubit {qubit} has no target binding");
        }

        private MirQasmExpression QubitAccessExpression(MirQubitAccess access)
        {
            var binding = QubitBindingExpression(access.Qubit.Id);
            return access.Index is MirValueId index
                ? new MirQasmIndexExpression(binding, ValueExpression(index))
                : binding;
        }

        private MirQasmExpression CallOperandExpression(
            MirCallOperand operand) =>
            operand switch
            {
                MirClassicalCallOperand classical =>
                    ValueExpression(classical.Value),
                MirQubitCallOperand qubit =>
                    QubitAccessExpression(qubit.Qubit),
                _ => throw Internal(
                    $"unknown MIR call operand {operand.GetType().Name}"),
            };

        private void RequireSameArrayPlace(
            MirValueId left,
            MirValueId right,
            string role,
            MirOrigin origin)
        {
            var leftPlace = ValueExpression(left);
            var rightPlace = ValueExpression(right);
            if (leftPlace != rightPlace)
            {
                throw Unsupported(
                    $"{role} would require a dynamic OpenQASM array alias",
                    origin);
            }
        }

        private MirQasmExpression ConvertExpression(MirConvert convert)
        {
            var target = convert.Result.Type;
            var name = target.ElementType switch
            {
                QType.Int => "int",
                QType.Float => "float",
                QType.Angle => "angle",
                QType.Bit => "bit",
                _ => throw Unsupported(
                    $"OpenQASM cannot convert a classical value to `{target.ElementType}`",
                    convert.Origin),
            };
            return new MirQasmFunctionCallExpression(
                new MirQasmBuiltinFunctionTarget(name),
                new[] { ValueExpression(convert.Operand) });
        }

        private static MirQasmBinaryOperator BinaryOperator(
            MirBinaryOperator @operator) =>
            @operator switch
            {
                MirBinaryOperator.Add => MirQasmBinaryOperator.Add,
                MirBinaryOperator.Subtract => MirQasmBinaryOperator.Subtract,
                MirBinaryOperator.Multiply => MirQasmBinaryOperator.Multiply,
                MirBinaryOperator.Divide => MirQasmBinaryOperator.Divide,
                MirBinaryOperator.Equal => MirQasmBinaryOperator.Equal,
                MirBinaryOperator.NotEqual => MirQasmBinaryOperator.NotEqual,
                MirBinaryOperator.Less => MirQasmBinaryOperator.Less,
                MirBinaryOperator.LessOrEqual =>
                    MirQasmBinaryOperator.LessOrEqual,
                MirBinaryOperator.Greater => MirQasmBinaryOperator.Greater,
                MirBinaryOperator.GreaterOrEqual =>
                    MirQasmBinaryOperator.GreaterOrEqual,
                _ => throw Internal(
                    $"unknown MIR binary operator {@operator}"),
            };

        private MirQasmCallableId TargetCallable(MirCallableId source) =>
            _targetCallables.TryGetValue(source, out var target)
                ? target
                : throw Internal($"MIR callable {source} has no target identity");

        private MirQasmParameterId NextParameter() =>
            new(_nextParameter++);

        private MirQasmDeclarationId NextDeclaration() =>
            new(_nextDeclaration++);

        private MirOpenQasmUnsupportedException UnsupportedCfg(string detail) =>
            new(
                "QASM001",
                $"MIR control flow cannot be structured for OpenQASM: {detail}",
                _callable.Origin);

        private MirOpenQasmUnsupportedException Unsupported(
            string detail,
            MirOrigin origin) =>
            new(
                "QASM002",
                $"callable `{_callable.Name}` cannot be lowered to OpenQASM: {detail}",
                origin);
    }

    private sealed class HiddenArrayPlan
    {
        private readonly IReadOnlyDictionary<MirCallableId, IReadOnlyList<HiddenStorageKey>>
            _required;

        private HiddenArrayPlan(
            IReadOnlyDictionary<MirCallableId, IReadOnlyList<HiddenStorageKey>> required,
            IReadOnlyList<string> notes)
        {
            _required = required;
            Notes = notes;
        }

        public IReadOnlyList<string> Notes { get; }

        public IReadOnlyList<HiddenStorageKey> RequiredBy(MirCallableId callable) =>
            _required[callable];

        public static HiddenArrayPlan Build(MirSnapshot source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var program = source.Program;
            var callGraph = source.Analyses.CallGraph;
            var hiddenStorages = new List<HiddenStorageKey>();
            var required = program.Callables.ToDictionary(
                callable => callable.Id,
                _ => new HashSet<HiddenStorageKey>());
            var callees = program.Callables.ToDictionary(
                callable => callable.Id,
                callable => callGraph.CallsFrom(callable.Id)
                    .Select(site => site.Callee)
                    .ToHashSet());

            foreach (var callable in program.Callables)
            {
                foreach (var storage in callable.Storages)
                {
                    if (callable.StorageKindOf(storage)
                        != MirArrayStorageKind.Local)
                    {
                        continue;
                    }

                    var storageType = callable.StorageTypeOf(storage);
                    if (storageType.ElementType == QType.Bit)
                        continue;
                    var key = new HiddenStorageKey(callable.Id, storage.Id);
                    hiddenStorages.Add(key);
                    required[callable.Id].Add(key);
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var caller in program.Callables
                             .Select(callable => callable.Id)
                             .OrderBy(id => id.Value))
                {
                    foreach (var callee in callees[caller])
                    {
                        foreach (var key in required[callee])
                            changed |= required[caller].Add(key);
                    }
                }
            }

            var frozen = required.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<HiddenStorageKey>)Array.AsReadOnly(
                    pair.Value
                        .OrderBy(key => key.Owner.Value)
                        .ThenBy(key => key.Storage.Value)
                        .ToArray()));
            var notes = hiddenStorages
                .OrderBy(key => key.Owner.Value)
                .ThenBy(key => key.Storage.Value)
                .Select(
                    key =>
                        $"MIR local array {key.Owner}/{key.Storage} uses " +
                        "a global OpenQASM backing; dependent defs receive typed hidden parameters")
                .ToArray();
            return new HiddenArrayPlan(
                new ReadOnlyDictionary<MirCallableId, IReadOnlyList<HiddenStorageKey>>(
                    frozen),
                Array.AsReadOnly(notes));
        }
    }

    private sealed class NameAllocator
    {
        private readonly HashSet<string> _used;

        public NameAllocator(IEnumerable<string> reserved) =>
            _used = new HashSet<string>(reserved, StringComparer.Ordinal);

        public string Fresh(string stem) => Identifier.Fresh(stem, _used);
    }

    private static class Identifier
    {
        public static string Sanitize(string source, string fallback)
        {
            source = HirGeneratedName.DisplayBase(source) ?? source;
            if (string.IsNullOrWhiteSpace(source)) source = fallback;
            var characters = source
                .Select(
                    character => char.IsAsciiLetterOrDigit(character)
                                 || character == '_'
                        ? character
                        : '_')
                .ToArray();
            var result = new string(characters);
            if (result.Length == 0) result = fallback;
            if (!(char.IsAsciiLetter(result[0]) || result[0] == '_'))
                result = "_" + result;
            return result;
        }

        public static string Fresh(string stem, ISet<string> used)
        {
            if (used.Add(stem)) return stem;
            for (var suffix = 1; ; suffix++)
            {
                var candidate = $"{stem}_{suffix}";
                if (used.Add(candidate)) return candidate;
            }
        }
    }

    private static MirQasmType TargetType(MirType type)
    {
        if (type.IsArray)
        {
            if (type.ElementType == QType.Bit)
            {
                var width = type.KnownLength
                    ?? throw Internal(
                        "a bit-register target type requires a known width");
                return new MirQasmBitType(width, isRegister: true);
            }
            return new MirQasmArrayType(
                ScalarType(type.ElementType),
                type.KnownLength);
        }
        return type.ElementType == QType.Bit
            ? new MirQasmBitType()
            : ScalarType(type.ElementType);
    }

    private static MirQasmArrayType RequireGeneralArrayType(
        MirType type,
        string role)
    {
        if (!type.IsArray || type.ElementType == QType.Bit)
            throw Internal($"`{role}` is not a general classical array");
        if (type.KnownLength is not int length)
            throw Internal($"general array `{role}` has no concrete local length");
        return new MirQasmArrayType(ScalarType(type.ElementType), length);
    }

    private static MirQasmScalarType ScalarType(QType type) =>
        new(
            type switch
            {
                QType.Int => MirQasmScalarKind.Int,
                QType.Float => MirQasmScalarKind.Float,
                QType.Angle => MirQasmScalarKind.Angle,
                QType.Bit => MirQasmScalarKind.Bool,
                _ => throw Internal(
                    $"Qora type `{type}` is not an OpenQASM classical scalar"),
            });

    private static MirQasmLiteralExpression DefaultLiteral(QType type) =>
        new(type is QType.Float or QType.Angle ? "0.0" : "0");

    private static InvalidOperationException Internal(string detail) =>
        new($"QINTERNAL: MIR OpenQASM lowering: {detail}");

    private sealed class MirOpenQasmUnsupportedException : Exception
    {
        public MirOpenQasmUnsupportedException(
            string code,
            string message,
            MirOrigin origin)
            : base(message)
        {
            Code = code;
            Origin = origin;
        }

        public string Code { get; }
        public MirOrigin Origin { get; }
    }

    private readonly record struct HiddenStorageKey(
        MirCallableId Owner,
        MirStorageId Storage);

    private readonly record struct EdgeArgumentKey(
        MirBlockId Source,
        int SuccessorOrdinal,
        int ArgumentIndex);

    private enum PathOutcomeKind
    {
        ReachedStop,
        Terminated,
        LoopBoundary,
    }

    private readonly record struct PathOutcome(
        PathOutcomeKind Kind,
        bool ContainsReturn);
}
