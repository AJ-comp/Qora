using System.Collections.ObjectModel;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir;

internal sealed record MirOpenQasmLoweringError(
    string Code,
    string Message,
    MirOriginRef? Origin);

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
        private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
        private readonly IReadOnlyDictionary<MirCallableId, MirQasmCallableId> _targetCallables;
        private readonly IReadOnlyDictionary<MirCallableId, string> _callableNames;
        private readonly HiddenArrayPlan _hiddenArrays;

        public ProgramLowerer(MirSnapshot source)
        {
            _source = source;
            _callables = source.Program.Callables.ToDictionary(callable => callable.Id);
            if (!_callables.TryGetValue(source.Program.EntryPoint, out var entry)
                || entry.Kind != MirCallableKind.Operation)
            {
                throw Internal(
                    $"MIR entry point {source.Program.EntryPoint} is missing or is not an operation");
            }

            _targetCallables = source.Program.Callables
                .OrderBy(callable => callable.Id.Value)
                .Select(
                    (callable, index) =>
                        (callable.Id, Target: new MirQasmCallableId(index)))
                .ToDictionary(pair => pair.Id, pair => pair.Target);
            _callableNames = AllocateCallableNames(source.Program.Callables);
            _hiddenArrays = HiddenArrayPlan.Build(source.Program, _callables);
        }

        public MirOpenQasmTargetProgram Lower()
        {
            var entrySource = _callables[_source.Program.EntryPoint];
            var entry = new CallableLowerer(
                    _source,
                    entrySource,
                    isEntry: true,
                    _callables,
                    _targetCallables,
                    _callableNames,
                    _hiddenArrays)
                .LowerEntry();

            var definitions = _source.Program.Callables
                .Where(callable => callable.Id != _source.Program.EntryPoint)
                .OrderBy(callable => callable.Id.Value)
                .Select(
                    callable => new CallableLowerer(
                            _source,
                            callable,
                            isEntry: false,
                            _callables,
                            _targetCallables,
                            _callableNames,
                            _hiddenArrays)
                        .LowerDefinition())
                .ToArray();

            return new MirOpenQasmTargetProgram(
                entry,
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
        private readonly MirSnapshot _snapshot;
        private readonly MirCallable _callable;
        private readonly bool _isEntry;
        private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
        private readonly IReadOnlyDictionary<MirCallableId, MirQasmCallableId> _targetCallables;
        private readonly IReadOnlyDictionary<MirCallableId, string> _callableNames;
        private readonly HiddenArrayPlan _hiddenArrays;
        private readonly MirCallableRef _callableRef;
        private readonly MirControlFlowSnapshot _controlFlow;
        private readonly MirControlRegionSnapshot _regions;
        private readonly MirStorageProvenanceSnapshot _storageProvenance;
        private readonly IReadOnlyDictionary<MirBlockId, MirBlock> _blocks;
        private readonly IReadOnlyDictionary<MirValueId, MirValue> _values;
        private readonly IReadOnlyDictionary<MirStorageId, MirArrayStorage> _storages;
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
        private int _nextStatement;

        public CallableLowerer(
            MirSnapshot snapshot,
            MirCallable callable,
            bool isEntry,
            IReadOnlyDictionary<MirCallableId, MirCallable> callables,
            IReadOnlyDictionary<MirCallableId, MirQasmCallableId> targetCallables,
            IReadOnlyDictionary<MirCallableId, string> callableNames,
            HiddenArrayPlan hiddenArrays)
        {
            _snapshot = snapshot;
            _callable = callable;
            _isEntry = isEntry;
            _callables = callables;
            _targetCallables = targetCallables;
            _callableNames = callableNames;
            _hiddenArrays = hiddenArrays;
            _callableRef = new MirCallableRef(snapshot.Id, callable.Id);
            _controlFlow = snapshot.Analyses.ControlFlow(_callableRef);
            _regions = snapshot.Analyses.ControlRegions(_callableRef);
            _storageProvenance = snapshot.Analyses.StorageProvenance(_callableRef);
            _blocks = callable.Blocks.ToDictionary(block => block.Id);
            _values = callable.Values.ToDictionary(value => value.Id);
            _storages = callable.Storages.ToDictionary(storage => storage.Id);

            var targetReserved = isEntry
                ? QoraGates.QasmReserved
                : QoraGates.QasmKeywords;
            var reserved = new HashSet<string>(
                targetReserved.Concat(callableNames.Values),
                StringComparer.Ordinal);
            _names = new NameAllocator(reserved);
            PrepareBindings();
        }

        public MirQasmEntryPoint LowerEntry()
        {
            if (!_isEntry) throw Internal("a definition lowerer was asked for an entry point");
            var body = LowerBody();
            return new MirQasmEntryPoint(
                TargetCallable(_callable.Id),
                _callableNames[_callable.Id],
                body);
        }

        public MirQasmCallableDefinition LowerDefinition()
        {
            if (_isEntry) throw Internal("the entry lowerer was asked for a definition");
            var body = LowerBody();
            return new MirQasmCallableDefinition(
                TargetCallable(_callable.Id),
                _callableNames[_callable.Id],
                _callable.Kind == MirCallableKind.Function
                    ? MirQasmCallableKind.Function
                    : MirQasmCallableKind.Operation,
                _parameters,
                _callable.ReturnType is MirType returns
                    ? TargetType(returns, declaration: false)
                    : null,
                body);
        }

        private IReadOnlyList<MirQasmStatement> LowerBody()
        {
            var flow = new List<MirQasmStatement>();
            var outcome = EmitPath(
                _callable.EntryBlock,
                stop: null,
                regionLoop: null,
                breakableLoop: null,
                flow);

            if (outcome.Kind == PathOutcomeKind.ReachedStop)
                throw Internal($"callable `{_callable.Name}` unexpectedly reached a missing outer join");

            var reachable = _controlFlow.ReachableBlocks
                .Select(reference => reference.Block)
                .ToHashSet();
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

            if (_callable.Kind == MirCallableKind.Function)
            {
                var returnStorage = _returnStorage
                    ?? throw Internal($"function `{_callable.Name}` has no target return storage");
                flow.Add(
                    new MirQasmReturnStatement(
                        NextStatement(),
                        new MirQasmDeclarationReferenceExpression(returnStorage)));
            }

            return _prologue.Concat(flow).ToArray();
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
                        var targetType = TargetType(classical.Type, declaration: false);
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
                        if (classical.Type.IsArray)
                        {
                            if (classical.Storage is not MirStorageId storage)
                            {
                                throw Internal(
                                    $"array parameter `{classical.Name}` has no MIR storage identity");
                            }
                            _storagePlaces.Add(storage, place);
                        }
                        else
                        {
                            _scalarValues.Add(classical.Value, place);
                        }
                        break;
                    }

                    case MirQubitParameter qubit:
                    {
                        var count = qubit.IsArray
                            ? qubit.Length
                              ?? throw Unsupported(
                                  $"qubit-array parameter `{qubit.Name}` has unknown target width")
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
                var storage = _hiddenArrays.RequireStorage(key);
                var targetType = RequireGeneralArrayType(storage.Type, storage.Name);
                MirQasmExpression place;
                if (_isEntry)
                {
                    var declaration = NextDeclaration();
                    var name = _names.Fresh(
                        Identifier.Sanitize(
                            $"{_callableNames[key.Owner]}_{storage.Name}_storage",
                            $"array_{key.Owner.Value}_{key.Storage.Value}"));
                    _prologue.Add(
                        new MirQasmArrayDeclarationStatement(
                            NextStatement(),
                            declaration,
                            name,
                            targetType,
                            Enumerable.Repeat(
                                DefaultLiteral(storage.Type.ElementType),
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
                         .Where(storage => storage.Kind == MirArrayStorageKind.Local)
                         .OrderBy(storage => storage.Id.Value))
            {
                if (storage.Type.ElementType != QType.Bit)
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

                var width = storage.Type.KnownLength
                    ?? throw Unsupported(
                        $"bit-array storage `{storage.Name}` has unknown target width");
                var declaration = NextDeclaration();
                var name = _names.Fresh(
                    Identifier.Sanitize(storage.Name, $"bits_{storage.Id.Value}"));
                _prologue.Add(
                    new MirQasmValueDeclarationStatement(
                        NextStatement(),
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
                        NextStatement(),
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
                        NextStatement(),
                        declaration,
                        name,
                        TargetType(value.Type, declaration: true)));
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
                    case MirJump jump:
                        PrepareEdge(block.Id, 0, jump.Target, jump.Arguments);
                        break;
                    case MirBranch branch:
                        PrepareEdge(
                            block.Id,
                            0,
                            branch.TrueTarget,
                            branch.TrueArguments);
                        PrepareEdge(
                            block.Id,
                            1,
                            branch.FalseTarget,
                            branch.FalseArguments);
                        break;
                }
            }
        }

        private void PrepareEdge(
            MirBlockId source,
            int successorOrdinal,
            MirBlockId target,
            IReadOnlyList<MirValueId> arguments)
        {
            var parameters = _blocks[target].Arguments;
            for (var index = 0; index < arguments.Count; index++)
            {
                if (parameters[index].Type.IsArray) continue;
                var declaration = NextDeclaration();
                var name = _names.Fresh(
                    $"edge_{source.Value}_{successorOrdinal}_{index}");
                _prologue.Add(
                    new MirQasmValueDeclarationStatement(
                        NextStatement(),
                        declaration,
                        name,
                        TargetType(parameters[index].Type, declaration: true)));
                _edgeTemporaries.Add(
                    new EdgeArgumentKey(source, successorOrdinal, index),
                    declaration);
            }
        }

        private void PrepareFunctionReturnState()
        {
            if (_callable.Kind != MirCallableKind.Function) return;
            if (_callable.ReturnType is not MirType returnType)
                throw Internal($"function `{_callable.Name}` has no MIR return type");

            _returnStorage = NextDeclaration();
            _prologue.Add(
                new MirQasmValueDeclarationStatement(
                    NextStatement(),
                    _returnStorage.Value,
                    _names.Fresh("ret"),
                    TargetType(returnType, declaration: true),
                    DefaultLiteral(returnType.ElementType)));

            // The flag is needed only to propagate a return across one or more target while scopes.
            // Keeping the decision structural avoids a source-language heuristic.
            if (_regions.NaturalLoops.Count == 0) return;
            _returnDone = NextDeclaration();
            _prologue.Add(
                new MirQasmValueDeclarationStatement(
                    NextStatement(),
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
                    && current == regionLoop.HeaderId
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
                    if (regionLoop.NormalExitId == current)
                    {
                        output.Add(new MirQasmBreakStatement(NextStatement()));
                        return new PathOutcome(
                            PathOutcomeKind.LoopBoundary,
                            ContainsReturn: false);
                    }

                    if (!regionLoop.TryGetSideExitKind(current, out var sideExitKind))
                    {
                        throw UnsupportedCfg(
                            $"loop {regionLoop.HeaderId} reached unclassified outside block {current}");
                    }

                    switch (sideExitKind)
                    {
                        case MirLoopSideExitKind.CallableReturn:
                        {
                            var terminal = EmitPath(
                                current,
                                stop: null,
                                regionLoop: null,
                                breakableLoop,
                                output);
                            if (terminal.Kind != PathOutcomeKind.Terminated)
                            {
                                throw UnsupportedCfg(
                                    $"loop {regionLoop.HeaderId} has a non-terminal callable-return " +
                                    $"side exit at {current}");
                            }
                            return terminal;
                        }

                        default:
                            throw Internal(
                                $"unknown MIR loop side-exit kind {sideExitKind}");
                    }
                }

                if (_regions.TryGetLoop(current, out var nested)
                    && nested is not null
                    && (regionLoop is null || nested.HeaderId != regionLoop.HeaderId))
                {
                    var loopBody = new List<MirQasmStatement>();
                    var loopOutcome = EmitPath(
                        nested.HeaderId,
                        stop: null,
                        regionLoop: nested,
                        breakableLoop: nested,
                        loopBody);
                    if (loopOutcome.Kind == PathOutcomeKind.ReachedStop)
                    {
                        throw UnsupportedCfg(
                            $"loop {nested.HeaderId} reached an unowned join");
                    }
                    output.Add(
                        new MirQasmWhileStatement(
                            NextStatement(),
                            new MirQasmLiteralExpression("true"),
                            loopBody));

                    if (nested.NormalExitId is not MirBlockId loopExit)
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
                                new MirQasmBreakStatement(NextStatement()));
                        output.Add(
                            new MirQasmIfStatement(
                                NextStatement(),
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

                var block = _blocks[current];
                EmitInstructions(block, output);
                switch (block.Terminator)
                {
                    case MirReturn returned:
                        return EmitReturn(returned, breakableLoop, output);

                    case MirUnreachable:
                        throw UnsupportedCfg(
                            $"reachable block {current} ends in `unreachable`");

                    case MirJump jump:
                        EmitEdge(
                            current,
                            successorOrdinal: 0,
                            jump.Target,
                            jump.Arguments,
                            output);
                        if (regionLoop is not null
                            && jump.Target == regionLoop.HeaderId)
                        {
                            return new PathOutcome(
                                PathOutcomeKind.LoopBoundary,
                                ContainsReturn: false);
                        }
                        if (regionLoop is not null
                            && regionLoop.NormalExitId == jump.Target)
                        {
                            output.Add(
                                new MirQasmBreakStatement(NextStatement()));
                            return new PathOutcome(
                                PathOutcomeKind.LoopBoundary,
                                ContainsReturn: false);
                        }
                        current = jump.Target;
                        continue;

                    case MirBranch branch:
                        return EmitBranch(
                            current,
                            branch,
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
            MirBlockId source,
            MirBranch branch,
            MirBlockId? outerStop,
            MirNaturalLoopRegion? regionLoop,
            MirNaturalLoopRegion? breakableLoop,
            List<MirQasmStatement> output)
        {
            var join = FindStructuredJoin(
                source,
                branch.TrueTarget,
                branch.FalseTarget,
                outerStop,
                regionLoop);
            var thenBody = new List<MirQasmStatement>();
            var elseBody = new List<MirQasmStatement>();
            EmitEdge(
                source,
                0,
                branch.TrueTarget,
                branch.TrueArguments,
                thenBody);
            EmitEdge(
                source,
                1,
                branch.FalseTarget,
                branch.FalseArguments,
                elseBody);

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
                        NextStatement(),
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
                            new MirQasmBreakStatement(NextStatement()));
                    output.Add(
                        new MirQasmIfStatement(
                            NextStatement(),
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
                    NextStatement(),
                    ValueExpression(branch.Condition),
                    thenBody,
                    elseBody));
            return CombineBranchOutcomes(trueOutcome, falseOutcome);
        }

        private MirBlockId? FindStructuredJoin(
            MirBlockId branch,
            MirBlockId trueTarget,
            MirBlockId falseTarget,
            MirBlockId? outerStop,
            MirNaturalLoopRegion? loop)
        {
            var candidates = _controlFlow.ReachableBlocks
                .Select(reference => reference.Block)
                .Where(candidate => candidate != branch)
                .Where(candidate => !_emittedBlocks.Contains(candidate))
                .Where(
                    candidate => loop is null
                                 || loop.Contains(candidate)
                                 && candidate != loop.HeaderId)
                .Where(candidate => loop?.NormalExitId != candidate)
                .Where(
                    candidate => _controlFlow.CanReach(trueTarget, candidate)
                                 && _controlFlow.CanReach(falseTarget, candidate))
                .Where(
                    candidate => _controlFlow.PostDominates(candidate, trueTarget)
                                 && _controlFlow.PostDominates(candidate, falseTarget))
                .ToArray();
            if (candidates.Length == 0 && _returnDone is not null)
            {
                // A return inside a loop prevents the normal continuation from structurally
                // post-dominating the branch, even though every non-returning path still joins there.
                // The target return_done state guards that continuation, so recover the nearest common
                // reachable block without weakening CFG authority or duplicating it.
                candidates = _controlFlow.ReachableBlocks
                    .Select(reference => reference.Block)
                    .Where(candidate => candidate != branch)
                    .Where(candidate => !_emittedBlocks.Contains(candidate))
                    .Where(
                        candidate => loop is null
                                     || loop.Contains(candidate)
                                     && candidate != loop.HeaderId)
                    .Where(candidate => loop?.NormalExitId != candidate)
                    .Where(
                        candidate => _controlFlow.CanReach(trueTarget, candidate)
                                     && _controlFlow.CanReach(falseTarget, candidate))
                    .ToArray();
            }
            if (outerStop is MirBlockId stop
                && !_emittedBlocks.Contains(stop)
                && (loop is null || loop.Contains(stop))
                && _controlFlow.CanReach(trueTarget, stop)
                && _controlFlow.CanReach(falseTarget, stop)
                && _controlFlow.PostDominates(stop, trueTarget)
                && _controlFlow.PostDominates(stop, falseTarget))
            {
                candidates = candidates.Append(stop).Distinct().ToArray();
            }
            if (candidates.Length == 0) return null;

            return candidates
                .Select(
                    candidate => new
                    {
                        Block = candidate,
                        TrueDistance = Distance(trueTarget, candidate, loop),
                        FalseDistance = Distance(falseTarget, candidate, loop),
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
                            || successor == loop.HeaderId
                            || successor == loop.NormalExitId))
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
            if (_callable.Kind == MirCallableKind.Operation)
            {
                if (returned.Value is not null)
                    throw Internal($"operation `{_callable.Name}` returns a MIR value");
                return new PathOutcome(
                    PathOutcomeKind.Terminated,
                    ContainsReturn: false);
            }

            if (returned.Value is not MirValueId value
                || _returnStorage is not MirQasmDeclarationId target)
            {
                throw Internal($"function `{_callable.Name}` has an invalid MIR return");
            }
            output.Add(
                new MirQasmAssignmentStatement(
                    NextStatement(),
                    new MirQasmDeclarationReferenceExpression(target),
                    ValueExpression(value)));

            if (_returnDone is MirQasmDeclarationId done)
            {
                output.Add(
                    new MirQasmAssignmentStatement(
                        NextStatement(),
                        new MirQasmDeclarationReferenceExpression(done),
                        new MirQasmLiteralExpression("1")));
            }

            if (breakableLoop is not null)
            {
                if (_returnDone is null)
                    throw Internal(
                        $"loop return in `{_callable.Name}` has no propagation flag");
                output.Add(new MirQasmBreakStatement(NextStatement()));
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
                            constant.Result,
                            new MirQasmLiteralExpression(constant.Constant.Text),
                            output);
                        break;

                    case MirUnary unary:
                        Assign(
                            unary.Result,
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
                        Assign(
                            binary.Result,
                            new MirQasmBinaryExpression(
                                BinaryOperator(binary.Operator),
                                ValueExpression(binary.Left),
                                ValueExpression(binary.Right)),
                            output);
                        break;

                    case MirConvert convert:
                        Assign(
                            convert.Result,
                            ConvertExpression(
                                convert.TargetType,
                                ValueExpression(convert.Operand)),
                            output);
                        break;

                    case MirArrayCreate create:
                        EmitArrayInitialization(create, output);
                        break;

                    case MirArrayLength length:
                    {
                        var arrayType = RequireValue(length.Array).Type;
                        MirQasmExpression expression = arrayType.KnownLength is int known
                            ? new MirQasmLiteralExpression(
                                known.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture))
                            : arrayType.ElementType == QType.Bit
                                ? throw Unsupported(
                                    "a dynamic-width bit register has no OpenQASM sizeof form")
                                : new MirQasmSizeOfExpression(
                                    ValueExpression(length.Array));
                        Assign(length.Result, expression, output);
                        break;
                    }

                    case MirArrayLoad load:
                        Assign(
                            load.Result,
                            new MirQasmIndexExpression(
                                ValueExpression(load.Array),
                                ValueExpression(load.Index)),
                            output);
                        break;

                    case MirArrayStore store:
                        output.Add(
                            new MirQasmAssignmentStatement(
                                NextStatement(),
                                new MirQasmIndexExpression(
                                    ValueExpression(store.Array),
                                    ValueExpression(store.Index)),
                                ValueExpression(store.Value)));
                        RequireSameArrayPlace(store.Array, store.Result, "array.store");
                        break;

                    case MirPureCall call:
                        Assign(
                            call.Result,
                            PureCallExpression(call),
                            output);
                        break;

                    case MirQubitAllocate allocation:
                        // Physical qubits are declared in the target prologue. The instruction remains
                        // the MIR lifetime marker but has no second textual OpenQASM operation.
                        _ = QubitBindingExpression(allocation.Result.Id);
                        break;

                    case MirQuantumApply apply:
                        RequireVersionErasureCompatibility(
                            apply.QubitAccesses,
                            apply.QubitResults,
                            "quantum apply");
                        output.Add(LowerQuantumApply(apply));
                        foreach (var result in apply.MutableArrayResults)
                        {
                            if (apply.Operands[result.OperandIndex]
                                is not MirClassicalCallOperand input)
                            {
                                throw Internal(
                                    $"mutable result {result.Result} does not correspond to an array operand");
                            }
                            RequireSameArrayPlace(
                                input.Value,
                                result.Result,
                                "mutable call result");
                        }
                        break;

                    case MirMeasure measure:
                        RequireVersionErasureCompatibility(
                            measure.Qubit,
                            measure.QubitResult,
                            "measurement");
                        output.Add(
                            new MirQasmMeasurementAssignmentStatement(
                                NextStatement(),
                                ValueExpression(measure.Result),
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
            if (!_storagePlaces.TryGetValue(create.Storage, out var storage))
                throw Internal($"array.create references missing storage {create.Storage}");
            for (var index = 0; index < create.Length; index++)
            {
                var value = create.Initialization == MirArrayInitialization.ExplicitElements
                    ? ValueExpression(create.Elements[index])
                    : DefaultLiteral(create.ElementType);
                output.Add(
                    new MirQasmAssignmentStatement(
                        NextStatement(),
                        new MirQasmIndexExpression(
                            storage,
                            new MirQasmLiteralExpression(
                                index.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture))),
                        value));
            }
            RequireSameArrayPlaceByStorage(create.Result, create.Storage, "array.create");
        }

        private MirQasmExpression PureCallExpression(MirPureCall call)
        {
            var arguments = call.Operands
                .Select(CallOperandExpression)
                .ToList();
            switch (call.Target)
            {
                case MirUserCallableTarget user:
                    RequireUserCall(user.Callable, MirCallableKind.Function, "pure call");
                    if (user.Callable == _snapshot.Program.EntryPoint)
                        throw Unsupported("the MIR entry point cannot be called as a function");
                    arguments.AddRange(HiddenArgumentsFor(user.Callable));
                    return new MirQasmFunctionCallExpression(
                        new MirQasmUserFunctionTarget(TargetCallable(user.Callable)),
                        arguments);

                case MirBuiltinFunctionTarget { Name: var name }
                    when name == QoraGates.BitsAsInt:
                {
                    if (call.Operands is not [MirClassicalCallOperand operand])
                        throw Internal($"`{name}` has an invalid MIR operand list");
                    var width = RequireValue(operand.Value).Type.KnownLength
                        ?? throw Unsupported(
                            $"`{name}` requires a fixed-width bit register");
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
                    RequireUserCall(user.Callable, MirCallableKind.Operation, "quantum call");
                    if (apply.Functors.Count != 0)
                    {
                        var modifiers = string.Join(", ", apply.Functors);
                        throw Unsupported(
                            $"user callable {user.Callable} still carries unmaterialized modifier(s): " +
                            modifiers);
                    }
                    if (user.Callable == _snapshot.Program.EntryPoint)
                        throw Unsupported("the MIR entry point cannot be called");
                    allArguments.AddRange(HiddenArgumentsFor(user.Callable));
                    return new MirQasmQuantumApplyStatement(
                        NextStatement(),
                        new MirQasmUserQuantumTarget(TargetCallable(user.Callable)),
                        Array.Empty<MirQasmExpression>(),
                        allArguments);

                case MirBuiltinGateTarget builtin:
                {
                    if (!QoraGates.Gates.TryGetValue(builtin.Name, out var gate))
                        throw Unsupported($"unknown OpenQASM built-in gate `{builtin.Name}`");
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
                        NextStatement(),
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
            MirBlockId source,
            int successorOrdinal,
            MirBlockId target,
            IReadOnlyList<MirValueId> arguments,
            List<MirQasmStatement> output)
        {
            var parameters = _blocks[target].Arguments;
            var scalarTransfers = new List<(
                MirQasmDeclarationId Temporary,
                MirQasmExpression Destination,
                MirQasmExpression Source)>();
            for (var index = 0; index < arguments.Count; index++)
            {
                var destination = parameters[index];
                if (destination.Type.IsArray)
                {
                    RequireSameArrayPlace(
                        arguments[index],
                        destination.Value,
                        $"edge {source}->{target}");
                    continue;
                }

                var key = new EdgeArgumentKey(source, successorOrdinal, index);
                if (!_edgeTemporaries.TryGetValue(key, out var temporary))
                    throw Internal($"edge argument {key} has no target temporary");
                scalarTransfers.Add(
                    (temporary,
                        ValueExpression(destination.Value),
                        ValueExpression(arguments[index])));
            }

            // Read every source before writing any Phi destination. This preserves simultaneous edge
            // assignment even for swaps such as (x, y) -> (y, x).
            foreach (var transfer in scalarTransfers)
            {
                output.Add(
                    new MirQasmAssignmentStatement(
                        NextStatement(),
                        new MirQasmDeclarationReferenceExpression(
                            transfer.Temporary),
                        transfer.Source));
            }
            foreach (var transfer in scalarTransfers)
            {
                output.Add(
                    new MirQasmAssignmentStatement(
                        NextStatement(),
                        transfer.Destination,
                        new MirQasmDeclarationReferenceExpression(
                            transfer.Temporary)));
            }
        }

        private void Assign(
            MirValueId result,
            MirQasmExpression value,
            List<MirQasmStatement> output) =>
            output.Add(
                new MirQasmAssignmentStatement(
                    NextStatement(),
                    ValueExpression(result),
                    value));

        private MirQasmExpression ValueExpression(MirValueId value)
        {
            var type = RequireValue(value).Type;
            if (!type.IsArray)
            {
                return _scalarValues.TryGetValue(value, out var scalar)
                    ? scalar
                    : throw Internal($"scalar value {value} has no target binding");
            }

            var provenance = _storageProvenance.ProvenanceOf(
                new MirValueRef(_snapshot.Id, _callable.Id, value));
            if (!provenance.IsComplete
                || provenance.PossibleStorages.Count != 1)
            {
                throw Unsupported(
                    $"array SSA value {value} has " +
                    $"{(provenance.IsComplete ? "ambiguous" : "incomplete")} storage provenance");
            }
            var storage = provenance.PossibleStorages[0].Storage;
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

        private void RequireVersionErasureCompatibility(
            IReadOnlyList<MirQubitAccess> accesses,
            IReadOnlyList<MirQubitAfterInstruction> results,
            string role)
        {
            var accessedIds = accesses
                .Select(access => access.Qubit.Id)
                .ToHashSet();
            foreach (var access in accesses)
                _ = QubitBindingExpression(access.Qubit.Id);
            foreach (var result in results)
            {
                if (!accessedIds.Contains(result.Id))
                {
                    throw Internal(
                        $"{role} produces {result.Key} without accessing qubit {result.Id}");
                }
                _ = QubitBindingExpression(result.Id);
            }
        }

        private void RequireVersionErasureCompatibility(
            MirQubitAccess access,
            MirQubitAfterInstruction result,
            string role)
        {
            if (access.Qubit.Id != result.Id)
            {
                throw Internal(
                    $"{role} changes qubit identity from {access.Qubit} to {result.Key}");
            }
            _ = QubitBindingExpression(result.Id);
        }

        private void RequireSameArrayPlace(
            MirValueId left,
            MirValueId right,
            string role)
        {
            var leftPlace = ValueExpression(left);
            var rightPlace = ValueExpression(right);
            if (leftPlace != rightPlace)
            {
                throw Unsupported(
                    $"{role} would require a dynamic OpenQASM array alias");
            }
        }

        private void RequireSameArrayPlaceByStorage(
            MirValueId value,
            MirStorageId storage,
            string role)
        {
            if (!_storagePlaces.TryGetValue(storage, out var storagePlace)
                || ValueExpression(value) != storagePlace)
            {
                throw Internal(
                    $"{role} result {value} disagrees with storage {storage}");
            }
        }

        private MirQasmExpression ConvertExpression(
            MirType target,
            MirQasmExpression operand)
        {
            if (target.IsArray)
                throw Unsupported($"OpenQASM cannot convert a value to `{target}`");
            var name = target.ElementType switch
            {
                QType.Int => "int",
                QType.Float => "float",
                QType.Angle => "angle",
                QType.Bit => "bit",
                _ => throw Unsupported(
                    $"OpenQASM cannot convert a classical value to `{target.ElementType}`"),
            };
            return new MirQasmFunctionCallExpression(
                new MirQasmBuiltinFunctionTarget(name),
                new[] { operand });
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

        private MirValue RequireValue(MirValueId value) =>
            _values.TryGetValue(value, out var found)
                ? found
                : throw Internal(
                    $"callable `{_callable.Name}` has no MIR value {value}");

        private void RequireUserCall(
            MirCallableId target,
            MirCallableKind expected,
            string role)
        {
            if (!_callables.TryGetValue(target, out var callable))
                throw Internal($"{role} targets missing callable {target}");
            if (callable.Kind != expected)
            {
                throw Internal(
                    $"{role} targets {callable.Kind} `{callable.Name}`, expected {expected}");
            }
        }

        private MirQasmCallableId TargetCallable(MirCallableId source) =>
            _targetCallables.TryGetValue(source, out var target)
                ? target
                : throw Internal($"MIR callable {source} has no target identity");

        private MirQasmParameterId NextParameter() =>
            new(_nextParameter++);

        private MirQasmDeclarationId NextDeclaration() =>
            new(_nextDeclaration++);

        private MirQasmStatementId NextStatement() =>
            new(_nextStatement++);

        private MirOpenQasmUnsupportedException UnsupportedCfg(string detail) =>
            new(
                "QASM001",
                $"MIR control flow cannot be structured for OpenQASM: {detail}",
                _callable.Origin);

        private MirOpenQasmUnsupportedException Unsupported(string detail) =>
            new(
                "QASM002",
                $"callable `{_callable.Name}` cannot be lowered to OpenQASM: {detail}",
                _callable.Origin);
    }

    private sealed class HiddenArrayPlan
    {
        private readonly IReadOnlyDictionary<MirCallableId, IReadOnlyList<HiddenStorageKey>>
            _required;
        private readonly IReadOnlyDictionary<HiddenStorageKey, MirArrayStorage> _storages;

        private HiddenArrayPlan(
            IReadOnlyDictionary<MirCallableId, IReadOnlyList<HiddenStorageKey>> required,
            IReadOnlyDictionary<HiddenStorageKey, MirArrayStorage> storages,
            IReadOnlyList<string> notes)
        {
            _required = required;
            _storages = storages;
            Notes = notes;
        }

        public IReadOnlyList<string> Notes { get; }

        public IReadOnlyList<HiddenStorageKey> RequiredBy(MirCallableId callable) =>
            _required.TryGetValue(callable, out var required)
                ? required
                : Array.Empty<HiddenStorageKey>();

        public MirArrayStorage RequireStorage(HiddenStorageKey key) =>
            _storages.TryGetValue(key, out var storage)
                ? storage
                : throw Internal(
                    $"hidden OpenQASM array key {key.Owner}/{key.Storage} has no storage");

        public static HiddenArrayPlan Build(
            MirProgram program,
            IReadOnlyDictionary<MirCallableId, MirCallable> callables)
        {
            var storages = new Dictionary<HiddenStorageKey, MirArrayStorage>();
            var required = callables.Keys.ToDictionary(
                callable => callable,
                _ => new HashSet<HiddenStorageKey>());
            var callees = callables.Keys.ToDictionary(
                callable => callable,
                _ => new HashSet<MirCallableId>());

            foreach (var callable in callables.Values)
            {
                foreach (var storage in callable.Storages)
                {
                    if (storage.Kind != MirArrayStorageKind.Local
                        || storage.Type.ElementType == QType.Bit)
                        continue;
                    var key = new HiddenStorageKey(callable.Id, storage.Id);
                    storages.Add(key, storage);
                    required[callable.Id].Add(key);
                }

                foreach (var target in callable.Blocks
                             .SelectMany(block => block.Instructions)
                             .Select(
                                 instruction => instruction switch
                                 {
                                     MirPureCall
                                     {
                                         Target: MirUserCallableTarget user
                                     } => (MirCallableId?)user.Callable,
                                     MirQuantumApply
                                     {
                                         Target: MirUserCallableTarget user
                                     } => user.Callable,
                                     _ => null,
                                 })
                             .Where(target => target is not null)
                             .Select(target => target!.Value))
                {
                    if (!callables.ContainsKey(target))
                        throw Internal(
                            $"MIR hidden-array planning found missing callable {target}");
                    callees[callable.Id].Add(target);
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var caller in callables.Keys.OrderBy(id => id.Value))
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
            var notes = storages.Keys
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
                new ReadOnlyDictionary<HiddenStorageKey, MirArrayStorage>(storages),
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

    private static MirQasmType TargetType(
        MirType type,
        bool declaration)
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
            MirOriginRef origin)
            : base(message)
        {
            Code = code;
            Origin = origin;
        }

        public string Code { get; }
        public MirOriginRef Origin { get; }
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
