using System.Collections.Frozen;

namespace Qora.Ir.Mir;

/// <summary>A typed failure produced while materializing an internal MIR inverse request.</summary>
public sealed record MirAdjointMaterializationError(
    string Code,
    string Message,
    MirCallableRef Callable,
    MirOriginRef Origin);

/// <summary>
/// The result of one MIR-only adjoint materialization pass. A snapshot without internal callable-inverse
/// requests is returned unchanged; a changed result owns a fresh snapshot revision and exact parent link.
/// </summary>
public sealed class MirAdjointMaterializationResult
{
    internal MirAdjointMaterializationResult(
        MirSnapshot source,
        MirSnapshot? output,
        IReadOnlyDictionary<MirCallableRef, MirCallableRef> inverses,
        IReadOnlyList<MirAdjointMaterializationError> errors)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Output = output;
        Inverses = inverses.ToFrozenDictionary();
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public MirSnapshot Source { get; }
    public MirSnapshot? Output { get; }
    public MirSnapshot Snapshot => Output ?? Source;
    /// <summary>
    /// Exact source-callable → synthesized-output-callable relationships. Both endpoints are
    /// snapshot-qualified so a local callable ID can never be consumed in the wrong MIR revision.
    /// </summary>
    public IReadOnlyDictionary<MirCallableRef, MirCallableRef> Inverses { get; }
    public IReadOnlyList<MirAdjointMaterializationError> Errors { get; }
    public bool Succeeded => Errors.Count == 0;
    public bool Changed => Output is not null;
}

/// <summary>
/// Converts every internal inverse request on a user-defined callable into a call to one synthesized MIR
/// callable. Built-in gate adjoints remain typed gate modifiers for the target backend. The current
/// implementation deliberately accepts only a straight-line unitary callable: structured CFG inversion
/// will be added together with MIR control regions for automatic uncomputation, rather than guessed from
/// arbitrary CFG shapes.
/// </summary>
public static class MirAdjointMaterializer
{
    private const string UnsupportedCode = "MIRADJ001";

    public static MirAdjointMaterializationResult Run(MirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (FindNonUnitaryInverseRequest(source.Program) is { } invalidBuiltin)
        {
            var builtin = (MirBuiltinGateTarget)invalidBuiltin.Apply.Target;
            return new MirAdjointMaterializationResult(
                source,
                output: null,
                new Dictionary<MirCallableRef, MirCallableRef>(),
                new[]
                {
                    new MirAdjointMaterializationError(
                        UnsupportedCode,
                        $"cannot materialize inverse of non-unitary built-in `{builtin.Name}`",
                        new MirCallableRef(source.Id, invalidBuiltin.Callable.Id),
                        invalidBuiltin.Apply.Origin),
                });
        }

        var requested = RequestedCallableInverses(source.Program);
        if (!ContainsCallableAdjointMarkers(source.Program))
        {
            return new MirAdjointMaterializationResult(
                source,
                output: null,
                new Dictionary<MirCallableRef, MirCallableRef>(),
                Array.Empty<MirAdjointMaterializationError>());
        }

        var planner = new Planner(source, requested);
        return planner.Run();
    }

    /// <summary>
    /// Final target-bound MIR may retain adjoint markers only on built-in gates. A marker on a
    /// user-defined callable would mean that callable synthesis or ID relinking was skipped.
    /// </summary>
    public static void VerifyMaterialized(MirSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var remaining = snapshot.Program.Callables
            .SelectMany(callable => callable.Blocks.SelectMany(block =>
                block.Instructions
                    .OfType<MirQuantumApply>()
                    .Where(apply => apply.Target is MirUserCallableTarget
                        && ContainsAdjoint(apply.Functors))
                    .Select(apply => (callable, apply))))
            .FirstOrDefault();
        if (remaining != default)
        {
            throw new InvalidOperationException(
                $"QINTERNAL: materialized MIR still contains a callable inverse request at "
                + $"{remaining.callable.Id}/{remaining.apply.Id}");
        }

        if (FindNonUnitaryInverseRequest(snapshot.Program) is { } invalidBuiltin)
        {
            var builtin = (MirBuiltinGateTarget)invalidBuiltin.Apply.Target;
            throw new InvalidOperationException(
                $"QINTERNAL: materialized MIR still requests the inverse of non-unitary built-in "
                + $"`{builtin.Name}` at {invalidBuiltin.Callable.Id}/{invalidBuiltin.Apply.Id}");
        }
    }

    private static (MirCallable Callable, MirQuantumApply Apply)?
        FindNonUnitaryInverseRequest(MirProgram program)
    {
        foreach (var callable in program.Callables)
        {
            foreach (var apply in callable.Blocks
                         .SelectMany(block => block.Instructions)
                         .OfType<MirQuantumApply>())
            {
                if (apply.Target is MirBuiltinGateTarget builtin
                    && HasAdjoint(apply.Functors)
                    && QoraGates.NonUnitary.Contains(builtin.Name))
                {
                    return (callable, apply);
                }
            }
        }

        return null;
    }

    private static HashSet<MirCallableId> RequestedCallableInverses(MirProgram program) =>
        program.Callables
            .SelectMany(callable => callable.Blocks)
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Where(apply => apply.Target is MirUserCallableTarget
                && HasAdjoint(apply.Functors))
            .Select(apply => ((MirUserCallableTarget)apply.Target).Callable)
            .ToHashSet();

    private static bool ContainsCallableAdjointMarkers(MirProgram program) =>
        program.Callables
            .SelectMany(callable => callable.Blocks)
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Any(apply => apply.Target is MirUserCallableTarget
                && ContainsAdjoint(apply.Functors));

    private static bool HasAdjoint(IReadOnlyList<MirFunctor> functors) =>
        functors.Count(functor => functor == MirFunctor.Adjoint) % 2 == 1;

    private static bool ContainsAdjoint(IReadOnlyList<MirFunctor> functors) =>
        functors.Contains(MirFunctor.Adjoint);

    /// <summary>
    /// Internal typed seam used by the future cleanup scheduler: it marks exact MIR quantum-call sites,
    /// never source text, as inverse requests. The source language deliberately exposes no Adjoint syntax.
    /// </summary>
    internal static MirSnapshot InjectRequests(
        MirSnapshot source,
        IEnumerable<MirInstructionRef> sites)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sites);
        var requested = sites.ToHashSet();
        if (requested.Count == 0) return source;

        var introducesRequest = false;
        foreach (var site in requested)
        {
            MirReferenceValidation.RequireSnapshot(
                source.Id,
                site.Snapshot,
                nameof(sites));
            var instruction = source.Structure.RequireInstruction(site);
            if (instruction is not MirQuantumApply apply)
            {
                throw new ArgumentException(
                    $"MIR inverse request site {site} is not a quantum application",
                    nameof(sites));
            }
            if (apply.Target is MirBuiltinGateTarget builtin
                && QoraGates.NonUnitary.Contains(builtin.Name))
            {
                throw new ArgumentException(
                    $"MIR inverse request site {site} targets non-unitary built-in `{builtin.Name}`",
                    nameof(sites));
            }
            introducesRequest |= !HasAdjoint(apply.Functors);
        }
        if (!introducesRequest) return source;
        if (source.Stage != MirStage.Lowered)
        {
            throw new InvalidOperationException(
                $"new inverse requests can only be injected into {MirStage.Lowered} MIR; "
                + $"the supplied snapshot is already {source.Stage}");
        }

        var transformation = new MirSnapshotTransformation(source);
        var changed = false;
        var callables = source.Program.Callables.Select(callable =>
            Planner.CloneCallable(
                callable,
                callable.Id,
                callable.Name,
                transformation.Rebase(callable.Origin),
                parameterOrigin: transformation.Rebase,
                valueOrigin: transformation.Rebase,
                storageOrigin: transformation.Rebase,
                qubitOrigin: transformation.Rebase,
                blockOrigin: transformation.Rebase,
                instructionOrigin: transformation.Rebase,
                terminatorOrigin: transformation.Rebase,
                instructionTransform: instruction =>
                {
                    var reference = new MirInstructionRef(
                        source.Id,
                        callable.Id,
                        instruction.Id);
                    var rebound = Planner.CloneInstruction(
                        instruction,
                        transformation.Rebase(instruction.Origin));
                    if (!requested.Contains(reference)
                        || rebound is not MirQuantumApply apply
                        || HasAdjoint(apply.Functors))
                        return rebound;
                    changed = true;
                    return apply with
                    {
                        Functors = new[] { MirFunctor.Adjoint }
                            .Concat(apply.Functors)
                            .ToArray(),
                    };
                })).ToArray();

        if (!changed)
            return source;

        var origins = transformation.BuildOrigins();
        var program = new MirProgram(
            transformation.Target,
            origins,
            source.Program.EntryPoint,
            callables);
        var links = source.Links.CloneForAdditiveTransformation(
            program,
            origins,
            new Dictionary<
                MirCallableId,
                (MirCallableId DerivedFrom, MirCallableSynthesisKind Kind)>());
        return new MirSnapshot(
            transformation.Target,
            source.Profile,
            program,
            links,
            MirStage.InverseRequestsInjected,
            source,
            source.Safety.Rebase(transformation.Target));
    }

    private sealed class Planner
    {
        private readonly MirSnapshot _source;
        private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
        private readonly HashSet<MirCallableId> _required;
        private readonly List<MirAdjointMaterializationError> _errors = new();
        private readonly Dictionary<MirCallableId, VisitState> _visit = new();

        public Planner(
            MirSnapshot source,
            HashSet<MirCallableId> requested)
        {
            _source = source;
            _callables = source.Program.Callables.ToDictionary(callable => callable.Id);
            _required = requested;
        }

        public MirAdjointMaterializationResult Run()
        {
            foreach (var callable in _required.ToArray())
                Discover(callable);
            if (_errors.Count > 0)
                return Failure();

            var nextCallableId = _callables.Count == 0
                ? 0
                : _callables.Keys.Max(id => id.Value) + 1;
            var inverseIds = _required
                .OrderBy(id => id.Value)
                .ToDictionary(
                    id => id,
                    _ => new MirCallableId(nextCallableId++));

            var transformation = new MirSnapshotTransformation(_source);
            var rewritten = _source.Program.Callables
                .Select(callable => RewriteSourceCallable(
                    callable,
                    inverseIds,
                    transformation))
                .ToList();
            foreach (var original in _required.OrderBy(id => id.Value))
            {
                rewritten.Add(CreateInverseCallable(
                    _callables[original],
                    inverseIds[original],
                    inverseIds,
                    transformation));
            }

            var origins = transformation.BuildOrigins();
            var program = new MirProgram(
                transformation.Target,
                origins,
                _source.Program.EntryPoint,
                rewritten);
            var synthesized = inverseIds.ToDictionary(
                pair => pair.Value,
                pair => (
                    DerivedFrom: pair.Key,
                    Kind: MirCallableSynthesisKind.Inverse));
            var links = _source.Links.CloneForAdditiveTransformation(
                program,
                origins,
                synthesized);
            var output = new MirSnapshot(
                transformation.Target,
                _source.Profile,
                program,
                links,
                MirStage.AdjointsMaterialized,
                _source,
                _source.Safety.CloneForAdditiveTransformation(
                    transformation.Target,
                    inverseIds));
            VerifyMaterialized(output);

            var exactInverseRefs = inverseIds.ToDictionary(
                pair => new MirCallableRef(_source.Id, pair.Key),
                pair => new MirCallableRef(output.Id, pair.Value));
            return new MirAdjointMaterializationResult(
                _source,
                output,
                exactInverseRefs,
                Array.Empty<MirAdjointMaterializationError>());
        }

        private void Discover(MirCallableId callableId)
        {
            if (_visit.TryGetValue(callableId, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    var recursiveCallable = RequireCallable(callableId);
                    Error(
                        recursiveCallable,
                        recursiveCallable.Origin,
                        "recursive callable inversion is not supported");
                }
                return;
            }

            var callable = RequireCallable(callableId);
            _visit[callableId] = VisitState.Visiting;
            if (!ValidateInvertibleShape(callable))
            {
                _visit[callableId] = VisitState.Visited;
                return;
            }

            var block = callable.Blocks[0];
            foreach (var apply in block.Instructions.OfType<MirQuantumApply>())
            {
                if (apply.Target is not MirUserCallableTarget user
                    || HasAdjoint(apply.Functors))
                    continue;
                _required.Add(user.Callable);
                Discover(user.Callable);
            }
            _visit[callableId] = VisitState.Visited;
        }

        private bool ValidateInvertibleShape(MirCallable callable)
        {
            var valid = true;
            if (callable.Kind != MirCallableKind.Operation
                || callable.ReturnType is not null)
            {
                Error(
                    callable,
                    callable.Origin,
                    "only void MIR operations can be inverted");
                valid = false;
            }
            if (callable.Blocks.Count != 1
                || callable.EntryBlock != callable.Blocks[0].Id
                || callable.Blocks[0].Arguments.Count != 0
                || callable.Blocks[0].Terminator is not MirReturn { Value: null })
            {
                Error(
                    callable,
                    callable.Origin,
                    "automatic MIR inversion currently requires one straight-line block");
                valid = false;
            }
            if (callable.Qubits.Any(qubit =>
                    qubit.Kind == MirQubitResourceKind.Local))
            {
                Error(
                    callable,
                    callable.Origin,
                    "an operation with local qubit allocation cannot be inverted as one callable");
                valid = false;
            }

            if (callable.Blocks.Count != 1)
                return valid;

            foreach (var instruction in callable.Blocks[0].Instructions)
            {
                switch (instruction)
                {
                    case MirConstant
                        or MirUnary
                        or MirBinary
                        or MirConvert
                        or MirArrayCreate
                        or MirArrayLength
                        or MirArrayLoad:
                        break;

                    case MirPureCall call when call.Operands.All(IsReadOnlyBorrow):
                        break;

                    case MirQuantumApply apply
                        when IsUnitary(apply)
                            && apply.MutableArrayResults.Count == 0
                            && apply.Operands.All(IsReadOnlyBorrow):
                        break;

                    case MirArrayStore:
                        Error(
                            callable,
                            instruction.Origin,
                            "classical array mutation is not reversible");
                        valid = false;
                        break;

                    case MirMeasure:
                        Error(
                            callable,
                            instruction.Origin,
                            "measurement is not unitary and cannot be inverted");
                        valid = false;
                        break;

                    case MirQubitAllocate:
                        Error(
                            callable,
                            instruction.Origin,
                            "local qubit allocation requires an explicit cleanup region");
                        valid = false;
                        break;

                    default:
                        Error(
                            callable,
                            instruction.Origin,
                            $"instruction {instruction.GetType().Name} is not supported by MIR inversion");
                        valid = false;
                        break;
                }
            }
            return valid;
        }

        private MirCallable RequireCallable(MirCallableId id)
        {
            if (_callables.TryGetValue(id, out var callable))
                return callable;

            var owner = _source.Program.Callables[0];
            Error(
                owner,
                owner.Origin,
                $"inverse request names missing callable {id}");
            return owner;
        }

        private void Error(
            MirCallable callable,
            MirOriginRef origin,
            string message) =>
            _errors.Add(
                new MirAdjointMaterializationError(
                    UnsupportedCode,
                    $"cannot materialize inverse of `{callable.Name}`: {message}",
                    new MirCallableRef(_source.Id, callable.Id),
                    origin));

        private MirAdjointMaterializationResult Failure() =>
            new(
                _source,
                output: null,
                new Dictionary<MirCallableRef, MirCallableRef>(),
                _errors);

        private static bool IsReadOnlyBorrow(MirCallOperand operand) =>
            operand.Ownership == QOwnershipMode.Borrowed
            && operand.Access == QAccessMode.ReadOnly;

        private bool IsUnitary(MirQuantumApply apply)
        {
            if (apply.Target is MirBuiltinGateTarget builtin)
                return !QoraGates.NonUnitary.Contains(builtin.Name);
            return apply.Target is MirUserCallableTarget user
                && _callables.TryGetValue(user.Callable, out var callable)
                && callable.Kind == MirCallableKind.Operation;
        }

        private static MirCallable RewriteSourceCallable(
            MirCallable callable,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds,
            MirSnapshotTransformation transformation) =>
            CloneCallable(
                callable,
                callable.Id,
                callable.Name,
                transformation.Rebase(callable.Origin),
                parameterOrigin: transformation.Rebase,
                valueOrigin: transformation.Rebase,
                storageOrigin: transformation.Rebase,
                qubitOrigin: transformation.Rebase,
                blockOrigin: transformation.Rebase,
                instructionOrigin: transformation.Rebase,
                terminatorOrigin: transformation.Rebase,
                instructionTransform: instruction =>
                    RewriteForwardInstruction(
                        instruction,
                        inverseIds,
                        transformation));

        private static MirInstruction RewriteForwardInstruction(
            MirInstruction instruction,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds,
            MirSnapshotTransformation transformation)
        {
            var origin = transformation.Rebase(instruction.Origin);
            if (instruction is MirQuantumApply
                {
                    Target: MirUserCallableTarget user
                } apply
                && ContainsAdjoint(apply.Functors))
            {
                var target = (MirCallTarget)user;
                if (HasAdjoint(apply.Functors))
                {
                    if (!inverseIds.TryGetValue(user.Callable, out var inverse))
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: no synthesized inverse was allocated for {user.Callable}");
                    }
                    target = new MirUserCallableTarget(inverse);
                }
                return apply with
                {
                    Target = target,
                    Functors = WithoutAdjoint(apply.Functors),
                    Origin = origin,
                };
            }
            return CloneInstruction(instruction, origin);
        }

        private static MirCallable CreateInverseCallable(
            MirCallable original,
            MirCallableId inverseId,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds,
            MirSnapshotTransformation transformation)
        {
            var originalBlock = original.Blocks[0];
            var classical = originalBlock.Instructions
                .Where(instruction => instruction is not MirQuantumApply)
                .Select(instruction => CloneInstruction(
                    instruction,
                    transformation.Synthesize(
                        instruction.Origin,
                        $"classical witness for inverse of {original.Id}")));
            var quantum = originalBlock.Instructions
                .OfType<MirQuantumApply>()
                .Reverse()
                .Select(apply => InvertApply(
                    apply,
                    inverseIds,
                    transformation));
            var instructions = classical.Concat(quantum).ToArray();

            return CloneCallable(
                original,
                inverseId,
                $"__qora_inverse_{original.Id.Value}_{original.Name}",
                transformation.Synthesize(
                    original.Origin,
                    $"inverse callable of {original.Id}"),
                parameterOrigin: origin => transformation.Synthesize(
                    origin,
                    $"inverse parameter of {original.Id}"),
                valueOrigin: origin => transformation.Synthesize(
                    origin,
                    $"inverse SSA value of {original.Id}"),
                storageOrigin: origin => transformation.Synthesize(
                    origin,
                    $"inverse storage of {original.Id}"),
                qubitOrigin: origin => transformation.Synthesize(
                    origin,
                    $"inverse qubit parameter of {original.Id}"),
                blockOrigin: origin => transformation.Synthesize(
                    origin,
                    $"inverse block of {original.Id}"),
                instructionOrigin: _ => throw new InvalidOperationException(
                    "inverse instructions are supplied explicitly"),
                terminatorOrigin: origin => transformation.Synthesize(
                    origin,
                    $"inverse return of {original.Id}"),
                instructionTransform: _ => throw new InvalidOperationException(
                    "inverse instructions are supplied explicitly"),
                replacementInstructions: instructions);
        }

        private static MirQuantumApply InvertApply(
            MirQuantumApply apply,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds,
            MirSnapshotTransformation transformation)
        {
            var toggledAdjoint = !HasAdjoint(apply.Functors);
            MirCallTarget target = apply.Target;
            if (target is MirUserCallableTarget user)
            {
                if (toggledAdjoint)
                {
                    if (!inverseIds.TryGetValue(user.Callable, out var inverse))
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: no synthesized inverse was allocated for {user.Callable}");
                    }
                    target = new MirUserCallableTarget(inverse);
                }
                else
                {
                    target = new MirUserCallableTarget(user.Callable);
                }
            }

            var functors = WithoutAdjoint(apply.Functors).ToList();
            if (target is MirBuiltinGateTarget && toggledAdjoint)
                functors.Insert(0, MirFunctor.Adjoint);

            return apply with
            {
                Target = target,
                Functors = functors,
                Origin = transformation.Synthesize(
                    apply.Origin,
                    $"inverse quantum instruction {apply.Id}"),
            };
        }

        private static IReadOnlyList<MirFunctor> WithoutAdjoint(
            IReadOnlyList<MirFunctor> functors) =>
            functors.Where(functor => functor != MirFunctor.Adjoint).ToArray();

        internal static MirCallable CloneCallable(
            MirCallable source,
            MirCallableId id,
            string name,
            MirOriginRef origin,
            Func<MirOriginRef, MirOriginRef> parameterOrigin,
            Func<MirOriginRef, MirOriginRef> valueOrigin,
            Func<MirOriginRef, MirOriginRef> storageOrigin,
            Func<MirOriginRef, MirOriginRef> qubitOrigin,
            Func<MirOriginRef, MirOriginRef> blockOrigin,
            Func<MirOriginRef, MirOriginRef> instructionOrigin,
            Func<MirOriginRef, MirOriginRef> terminatorOrigin,
            Func<MirInstruction, MirInstruction> instructionTransform,
            IReadOnlyList<MirInstruction>? replacementInstructions = null)
        {
            var parameters = source.Parameters.Select(parameter => (MirParameter)(parameter switch
            {
                MirClassicalParameter classical =>
                    classical with { Origin = parameterOrigin(classical.Origin) },
                MirQubitParameter qubit =>
                    qubit with { Origin = parameterOrigin(qubit.Origin) },
                _ => throw new InvalidOperationException(
                    $"unknown MIR parameter {parameter.GetType().Name}"),
            })).ToArray();
            var values = source.Values
                .Select(value => value with { Origin = valueOrigin(value.Origin) })
                .ToArray();
            var storages = source.Storages
                .Select(storage => storage with { Origin = storageOrigin(storage.Origin) })
                .ToArray();
            var qubits = source.Qubits
                .Select(qubit => qubit with { Origin = qubitOrigin(qubit.Origin) })
                .ToArray();
            var blocks = source.Blocks.Select((block, index) =>
            {
                var instructions = replacementInstructions is not null && index == 0
                    ? replacementInstructions
                    : block.Instructions.Select(instruction =>
                    {
                        var transformed = instructionTransform(instruction);
                        return transformed.Origin.Snapshot == origin.Snapshot
                            ? transformed
                            : CloneInstruction(
                                transformed,
                                instructionOrigin(instruction.Origin));
                    }).ToArray();
                return block with
                {
                    Instructions = instructions,
                    Terminator = CloneTerminator(
                        block.Terminator,
                        terminatorOrigin(block.Terminator.Origin)),
                    Origin = blockOrigin(block.Origin),
                };
            }).ToArray();

            return source with
            {
                Id = id,
                Name = name,
                Parameters = parameters,
                Blocks = blocks,
                Values = values,
                Storages = storages,
                Qubits = qubits,
                Origin = origin,
            };
        }

        internal static MirInstruction CloneInstruction(
            MirInstruction instruction,
            MirOriginRef origin) =>
            instruction switch
            {
                MirConstant value => value with { Origin = origin },
                MirUnary value => value with { Origin = origin },
                MirBinary value => value with { Origin = origin },
                MirConvert value => value with { Origin = origin },
                MirArrayCreate value => value with { Origin = origin },
                MirArrayLength value => value with { Origin = origin },
                MirArrayLoad value => value with { Origin = origin },
                MirArrayStore value => value with { Origin = origin },
                MirPureCall value => value with { Origin = origin },
                MirQubitAllocate value => value with { Origin = origin },
                MirQuantumApply value => value with { Origin = origin },
                MirMeasure value => value with { Origin = origin },
                _ => throw new InvalidOperationException(
                    $"unknown MIR instruction {instruction.GetType().Name}"),
            };

        private static MirTerminator CloneTerminator(
            MirTerminator terminator,
            MirOriginRef origin) =>
            terminator switch
            {
                MirJump value => value with { Origin = origin },
                MirBranch value => value with { Origin = origin },
                MirReturn value => value with { Origin = origin },
                MirUnreachable value => value with { Origin = origin },
                _ => throw new InvalidOperationException(
                    $"unknown MIR terminator {terminator.GetType().Name}"),
            };

        private enum VisitState
        {
            Visiting,
            Visited,
        }
    }
}
