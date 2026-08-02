using System.Collections.Frozen;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

/// <summary>A typed failure produced while materializing an internal MIR inverse request.</summary>
public sealed record MirAdjointMaterializationError(
    string Code,
    string Message,
    MirCallableId Callable,
    MirOrigin Origin);

/// <summary>
/// The result of one MIR-only adjoint materialization pass. A snapshot without internal callable-inverse
/// requests is returned unchanged; a changed result owns the next supported MIR stage and retains its
/// exact source snapshot.
/// </summary>
public sealed class MirAdjointMaterializationResult
{
    internal MirAdjointMaterializationResult(
        MirSnapshot source,
        MirSnapshot? output,
        IReadOnlyDictionary<MirCallableId, MirCallableId> inverses,
        IReadOnlyList<MirAdjointMaterializationError> errors)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(inverses);
        ArgumentNullException.ThrowIfNull(errors);
        Output = output;
        Inverses = inverses.ToFrozenDictionary();
        Errors = Array.AsReadOnly(errors.ToArray());

        if (Output is null)
        {
            if (Inverses.Count != 0)
            {
                throw new ArgumentException(
                    "An unchanged or failed adjoint materialization cannot publish inverse callables.",
                    nameof(inverses));
            }
        }
        else
        {
            if (Errors.Count != 0)
            {
                throw new ArgumentException(
                    "A failed adjoint materialization cannot publish an output snapshot.",
                    nameof(errors));
            }
            if (Output.Stage != MirStage.AdjointsMaterialized
                || !ReferenceEquals(Output.TransformationSource, Source))
            {
                throw new ArgumentException(
                    "The materialized output must be the exact adjoint stage transformed from Source.",
                    nameof(output));
            }

            foreach (var (sourceCallable, inverseCallable) in Inverses)
            {
                _ = Source.Program.RequireCallable(sourceCallable);
                _ = Output.Program.RequireCallable(inverseCallable);
            }
        }

        foreach (var error in Errors)
        {
            ArgumentNullException.ThrowIfNull(error);
            _ = Source.Program.RequireCallable(error.Callable);
            ArgumentNullException.ThrowIfNull(error.Origin);
        }
    }

    public MirSnapshot Source { get; }
    public MirSnapshot? Output { get; }
    public MirSnapshot Snapshot => Output ?? Source;
    /// <summary>
    /// Source-callable → synthesized-output-callable relationships. <see cref="Source"/> owns every
    /// key and <see cref="Output"/> owns every value, so the result supplies both exact snapshots.
    /// </summary>
    public IReadOnlyDictionary<MirCallableId, MirCallableId> Inverses { get; }
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
                new Dictionary<MirCallableId, MirCallableId>(),
                new[]
                {
                    new MirAdjointMaterializationError(
                        UnsupportedCode,
                        $"cannot materialize inverse of non-unitary built-in `{builtin.Name}`",
                        invalidBuiltin.Callable.Id,
                        invalidBuiltin.Apply.Origin),
                });
        }

        var requested = RequestedCallableInverses(source);
        if (!ContainsCallableAdjointMarkers(source))
        {
            return new MirAdjointMaterializationResult(
                source,
                output: null,
                new Dictionary<MirCallableId, MirCallableId>(),
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
        if (FindUserCallableInverseRequest(snapshot.Program) is { } remaining)
        {
            throw new InvalidOperationException(
                $"QINTERNAL: materialized MIR still contains a callable inverse request at "
                + $"{remaining.Callable.Id}/{remaining.Apply.Id}");
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
        FindUserCallableInverseRequest(MirProgram program)
    {
        foreach (var callable in program.Callables)
        {
            foreach (var block in callable.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is MirQuantumApply
                        {
                            Target: MirUserCallableTarget,
                        } apply
                        && ContainsAdjoint(apply.Functors))
                    {
                        return (callable, apply);
                    }
                }
            }
        }

        return null;
    }

    private static (MirCallable Callable, MirQuantumApply Apply)?
        FindNonUnitaryInverseRequest(MirProgram program)
    {
        foreach (var callable in program.Callables)
        {
            foreach (var block in callable.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is MirQuantumApply
                        {
                            Target: MirBuiltinGateTarget builtin,
                        } apply
                        && HasAdjoint(apply.Functors)
                        && QoraGates.NonUnitary.Contains(builtin.Name))
                    {
                        return (callable, apply);
                    }
                }
            }
        }

        return null;
    }

    private static HashSet<MirCallableId> RequestedCallableInverses(
        MirSnapshot snapshot) =>
        UserCallableApplies(snapshot)
            .Where(call => HasAdjoint(call.Apply.Functors))
            .Select(call => call.Site.Callee)
            .ToHashSet();

    private static bool ContainsCallableAdjointMarkers(MirSnapshot snapshot) =>
        UserCallableApplies(snapshot)
            .Any(call => ContainsAdjoint(call.Apply.Functors));

    private static IEnumerable<(MirCallSite Site, MirQuantumApply Apply)>
        UserCallableApplies(MirSnapshot snapshot)
    {
        foreach (var caller in snapshot.Program.Callables)
        {
            foreach (var site in snapshot.Analyses.CallGraph.CallsFrom(caller.Id))
            {
                var instruction = caller.RequireInstruction(
                    site.Instruction.Instruction);
                if (instruction is MirQuantumApply apply)
                    yield return (site, apply);
            }
        }
    }

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
        IEnumerable<MirInstructionSite> sites)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sites);
        var requested = sites.ToHashSet();
        if (requested.Count == 0) return source;

        var introducesRequest = false;
        foreach (var site in requested)
        {
            var callable = source.Program.RequireCallable(site.Callable);
            var instruction = callable.RequireInstruction(site.Instruction);
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

        var changed = false;
        var callables =
            new List<MirCallable>(source.Program.Callables.Count);
        foreach (var callable in source.Program.Callables)
        {
            MirInstruction AddRequestIfSelected(MirInstruction instruction)
            {
                var site = new MirInstructionSite(
                    callable.Id,
                    instruction.Id);
                var cloned = Planner.CloneInstruction(
                    instruction,
                    instruction.Origin);
                if (!requested.Contains(site)
                    || cloned is not MirQuantumApply apply
                    || HasAdjoint(apply.Functors))
                {
                    return cloned;
                }

                changed = true;
                var functors =
                    new List<MirFunctor>(apply.Functors.Count + 1)
                    {
                        MirFunctor.Adjoint,
                    };
                functors.AddRange(apply.Functors);
                return Planner.CloneApply(
                    apply,
                    apply.Target,
                    functors,
                    apply.Operands,
                    apply.QubitResults,
                    apply.Origin);
            }

            var rewrittenCallable = Planner.CloneCallable(
                callable,
                callable.Id,
                callable.Name,
                callable.Origin,
                instructionTransform: AddRequestIfSelected);
            callables.Add(rewrittenCallable);
        }

        if (!changed)
            return source;

        var program = new MirProgram(
            source.Program.EntryPoint,
            callables);
        return MirSnapshot.CreateTransformed(
            program,
            MirStage.InverseRequestsInjected,
            source);
    }

    private sealed class Planner
    {
        private readonly MirSnapshot _source;
        private readonly MirCallGraph _callGraph;
        private readonly HashSet<MirCallableId> _required;
        private readonly List<MirAdjointMaterializationError> _errors = new();
        private readonly Dictionary<MirCallableId, VisitState> _visit = new();

        public Planner(
            MirSnapshot source,
            HashSet<MirCallableId> requested)
        {
            _source = source;
            _callGraph = source.Analyses.CallGraph;
            _required = requested;
        }

        public MirAdjointMaterializationResult Run()
        {
            foreach (var callable in _required.ToArray())
                Discover(callable);
            if (_errors.Count > 0)
                return Failure();

            long nextCallableId = _source.Program.Callables.Count == 0
                ? 0
                : (long)_source.Program.Callables.Max(
                    callable => callable.Id.Value) + 1;
            var inverseIds =
                new Dictionary<MirCallableId, MirCallableId>(_required.Count);
            foreach (var originalId in _required.OrderBy(id => id.Value))
            {
                if (nextCallableId > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "QINTERNAL: MIR callable identity space is exhausted");
                }

                var inverseId =
                    new MirCallableId((int)nextCallableId);
                nextCallableId++;
                inverseIds.Add(originalId, inverseId);
            }

            var rewritten = new List<MirCallable>(
                _source.Program.Callables.Count + _required.Count);
            foreach (var callable in _source.Program.Callables)
            {
                var rewrittenCallable = RewriteSourceCallable(
                    callable,
                    inverseIds);
                rewritten.Add(rewrittenCallable);
            }

            foreach (var original in _required.OrderBy(id => id.Value))
            {
                var inverseCallable = CreateInverseCallable(
                    _source.Program.RequireCallable(original),
                    inverseIds[original],
                    inverseIds);
                rewritten.Add(inverseCallable);
            }

            var program = new MirProgram(
                _source.Program.EntryPoint,
                rewritten);
            var output = MirSnapshot.CreateTransformed(
                program,
                MirStage.AdjointsMaterialized,
                _source);
            VerifyMaterialized(output);

            return new MirAdjointMaterializationResult(
                _source,
                output,
                inverseIds,
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

            foreach (var site in _callGraph.CallsFrom(callableId))
            {
                if (callable.RequireInstruction(site.Instruction.Instruction)
                    is not MirQuantumApply apply)
                {
                    continue;
                }
                if (HasAdjoint(apply.Functors))
                    continue;
                _required.Add(site.Callee);
                Discover(site.Callee);
            }
            _visit[callableId] = VisitState.Visited;
        }

        private bool ValidateInvertibleShape(MirCallable callable)
        {
            var valid = true;
            if (callable.Kind != MirCallableKind.Operation)
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
            if (callable.Qubits.Any(qubit => qubit is MirQubitFromUse))
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
            if (_source.Program.FindCallable(id) is { } callable)
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
            MirOrigin origin,
            string message) =>
            _errors.Add(
                new MirAdjointMaterializationError(
                    UnsupportedCode,
                    $"cannot materialize inverse of `{callable.Name}`: {message}",
                    callable.Id,
                    origin));

        private MirAdjointMaterializationResult Failure() =>
            new(
                _source,
                output: null,
                new Dictionary<MirCallableId, MirCallableId>(),
                _errors);

        private static bool IsReadOnlyBorrow(MirCallOperand operand) =>
            operand.Ownership == QOwnershipMode.Borrowed
            && operand.Access == QAccessMode.ReadOnly;

        private bool IsUnitary(MirQuantumApply apply)
        {
            if (apply.Target is MirBuiltinGateTarget builtin)
                return !QoraGates.NonUnitary.Contains(builtin.Name);
            return apply.Target is MirUserCallableTarget user
                && _source.Program.FindCallable(user.Callable) is { } callable
                && callable.Kind == MirCallableKind.Operation;
        }

        private static MirCallable RewriteSourceCallable(
            MirCallable callable,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds) =>
            CloneCallable(
                callable,
                callable.Id,
                callable.Name,
                callable.Origin,
                instructionTransform: instruction =>
                    RewriteForwardInstruction(
                        instruction,
                        inverseIds));

        private static MirInstruction RewriteForwardInstruction(
            MirInstruction instruction,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds)
        {
            var origin = instruction.Origin;
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
                return CloneApply(
                    apply,
                    target,
                    WithoutAdjoint(apply.Functors),
                    apply.Operands,
                    CloneQubitResults(apply.QubitResults, _ => origin),
                    origin);
            }
            return CloneInstruction(instruction, origin);
        }

        private static MirCallable CreateInverseCallable(
            MirCallable original,
            MirCallableId inverseId,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds)
        {
            var originalBlock = original.Blocks[0];
            var qubitVersions = new InverseQubitVersions(
                original.Parameters.OfType<MirQubitParameter>());
            var instructions =
                new List<MirInstruction>(originalBlock.Instructions.Count);

            foreach (var instruction in originalBlock.Instructions)
            {
                if (instruction is MirQuantumApply)
                    continue;

                var origin = new MirGeneratedOrigin(
                    instruction.Origin,
                    $"classical witness for inverse of {original.Id}");
                var classicalWitness = CloneInstruction(
                    instruction,
                    origin);
                instructions.Add(classicalWitness);
            }

            for (var index = originalBlock.Instructions.Count - 1;
                 index >= 0;
                 index--)
            {
                if (originalBlock.Instructions[index] is not MirQuantumApply apply)
                    continue;

                var inverseApply = InvertApply(
                    apply,
                    inverseIds,
                    qubitVersions);
                instructions.Add(inverseApply);
            }

            return CloneCallable(
                original,
                inverseId,
                $"__qora_inverse_{original.Id.Value}_{original.Name}",
                new MirGeneratedOrigin(
                    original.Origin,
                    $"inverse callable of {original.Id}"),
                instructionTransform: _ => throw new InvalidOperationException(
                    "inverse instructions are supplied explicitly"),
                replacementInstructions: instructions,
                originForRole: (origin, role) =>
                    new MirGeneratedOrigin(
                        origin,
                        $"inverse {role} of {original.Id}"));
        }

        private static MirQuantumApply InvertApply(
            MirQuantumApply apply,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds,
            InverseQubitVersions qubitVersions)
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

            var origin = new MirGeneratedOrigin(
                apply.Origin,
                $"inverse quantum instruction {apply.Id}");
            var operands = qubitVersions.RewriteOperands(apply.Operands);
            var results = qubitVersions.CreateResults(apply.QubitResults, origin);
            return CloneApply(
                apply,
                target,
                functors,
                operands,
                results,
                origin);
        }

        internal static MirQuantumApply CloneApply(
            MirQuantumApply source,
            MirCallTarget target,
            IReadOnlyList<MirFunctor> functors,
            IReadOnlyList<MirCallOperand> operands,
            IReadOnlyList<MirQubitAfterInstruction> qubitResults,
            MirOrigin origin) =>
            new(
                source.Id,
                target,
                operands,
                qubitResults,
                source.MutableArrayResults,
                functors,
                origin);

        private static IReadOnlyList<MirQubitAfterInstruction> CloneQubitResults(
            IReadOnlyList<MirQubitAfterInstruction> results,
            Func<MirOrigin, MirOrigin> origin) =>
            results
                .Select(result => new MirQubitAfterInstruction(
                    result.Id,
                    result.Version,
                    origin(result.Origin)))
                .ToArray();

        /// <summary>
        /// Rebuilds the callable-local qubit SSA chain while forward instructions are emitted in reverse
        /// order. Stable qubit identities are retained, but every inverse instruction consumes the
        /// version current at its new position and allocates fresh versions for its writes.
        /// </summary>
        private sealed class InverseQubitVersions
        {
            private readonly Dictionary<MirQubitId, MirQubitKey> _current = new();
            private readonly Dictionary<MirQubitId, int> _nextVersion = new();

            public InverseQubitVersions(IEnumerable<MirQubitParameter> parameters)
            {
                foreach (var parameter in parameters)
                {
                    if (!_current.TryAdd(parameter.Id, parameter.Key))
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: inverse callable has duplicate qubit parameter {parameter.Id}");
                    }
                    _nextVersion.Add(parameter.Id, 1);
                }
            }

            public IReadOnlyList<MirCallOperand> RewriteOperands(
                IReadOnlyList<MirCallOperand> operands)
            {
                var rewritten =
                    new MirCallOperand[operands.Count];
                for (var index = 0; index < operands.Count; index++)
                {
                    var operand = operands[index];
                    if (operand is not MirQubitCallOperand qubit)
                    {
                        rewritten[index] = operand;
                        continue;
                    }

                    var qubitId = qubit.Qubit.Qubit.Id;
                    if (!_current.TryGetValue(qubitId, out var current))
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: inverse instruction accesses nonparameter qubit {qubitId}");
                    }

                    rewritten[index] = new MirQubitCallOperand(
                        new MirQubitAccess(
                            current,
                            qubit.Qubit.Index,
                            qubit.Qubit.Origin),
                        qubit.Ownership,
                        qubit.Access);
                }

                return rewritten;
            }

            public IReadOnlyList<MirQubitAfterInstruction> CreateResults(
                IReadOnlyList<MirQubitAfterInstruction> forwardResults,
                MirOrigin origin)
            {
                var results = new List<MirQubitAfterInstruction>(forwardResults.Count);
                var written = new HashSet<MirQubitId>();
                foreach (var forward in forwardResults)
                {
                    if (!written.Add(forward.Id))
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: inverse instruction writes qubit {forward.Id} more than once");
                    }
                    if (!_nextVersion.TryGetValue(forward.Id, out var next))
                    {
                        throw new InvalidOperationException(
                            $"QINTERNAL: inverse instruction writes nonparameter qubit {forward.Id}");
                    }

                    var result = new MirQubitAfterInstruction(
                        forward.Id,
                        new MirQubitVersion(next),
                        origin);
                    _nextVersion[forward.Id] = checked(next + 1);
                    _current[forward.Id] = result.Key;
                    results.Add(result);
                }

                return results;
            }
        }

        private static IReadOnlyList<MirFunctor> WithoutAdjoint(
            IReadOnlyList<MirFunctor> functors) =>
            functors.Where(functor => functor != MirFunctor.Adjoint).ToArray();

        internal static MirCallable CloneCallable(
            MirCallable source,
            MirCallableId id,
            string name,
            MirOrigin origin,
            Func<MirInstruction, MirInstruction> instructionTransform,
            IReadOnlyList<MirInstruction>? replacementInstructions = null,
            Func<MirOrigin, string, MirOrigin>? originForRole = null)
        {
            MirOrigin OriginForRole(
                MirOrigin sourceOrigin,
                string role) =>
                originForRole is null
                    ? sourceOrigin
                    : originForRole(sourceOrigin, role);

            var parameters =
                new IMirParameter[source.Parameters.Count];
            for (var index = 0; index < source.Parameters.Count; index++)
            {
                var sourceParameter = source.Parameters[index];
                IMirParameter clonedParameter;
                switch (sourceParameter)
                {
                    case MirClassicalParameter classical:
                        clonedParameter = classical with { };
                        break;

                    case MirQubitParameter qubit:
                        clonedParameter = new MirQubitParameter(
                            qubit.Id,
                            qubit.Name,
                            qubit.IsArray,
                            qubit.Length,
                            qubit.Ownership,
                            OriginForRole(
                                qubit.Origin,
                                "qubit parameter"));
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"unknown MIR parameter {sourceParameter.GetType().Name}");
                }

                parameters[index] = clonedParameter;
            }

            var values = source.Values
                .Select(value => value with
                {
                    Origin = OriginForRole(value.Origin, "SSA value"),
                })
                .ToArray();
            var storages = source.Storages
                .Select(storage => storage with
                {
                    Origin = OriginForRole(storage.Origin, "storage"),
                })
                .ToArray();
            var blocks = new MirBlock[source.Blocks.Count];
            for (var blockIndex = 0;
                 blockIndex < source.Blocks.Count;
                 blockIndex++)
            {
                var sourceBlock = source.Blocks[blockIndex];
                IReadOnlyList<MirInstruction> instructions;
                if (replacementInstructions is not null && blockIndex == 0)
                {
                    instructions = replacementInstructions;
                }
                else
                {
                    var clonedInstructions =
                        new MirInstruction[sourceBlock.Instructions.Count];
                    for (var instructionIndex = 0;
                         instructionIndex < sourceBlock.Instructions.Count;
                         instructionIndex++)
                    {
                        var sourceInstruction =
                            sourceBlock.Instructions[instructionIndex];
                        var clonedInstruction =
                            instructionTransform(sourceInstruction);
                        clonedInstructions[instructionIndex] =
                            clonedInstruction;
                    }

                    instructions = clonedInstructions;
                }

                var qubitPhis =
                    new MirQubitPhi[sourceBlock.QubitPhis.Count];
                for (var phiIndex = 0;
                     phiIndex < sourceBlock.QubitPhis.Count;
                     phiIndex++)
                {
                    var sourcePhi = sourceBlock.QubitPhis[phiIndex];
                    qubitPhis[phiIndex] = new MirQubitPhi(
                        sourcePhi.Id,
                        sourcePhi.Version,
                        sourcePhi.Inputs,
                        OriginForRole(
                            sourcePhi.Origin,
                            "qubit Phi"));
                }

                blocks[blockIndex] = sourceBlock with
                {
                    QubitPhis = qubitPhis,
                    Instructions = instructions,
                    Terminator = CloneTerminator(
                        sourceBlock.Terminator,
                        OriginForRole(
                            sourceBlock.Terminator.Origin,
                            "return")),
                    Origin = OriginForRole(
                        sourceBlock.Origin,
                        "block"),
                };
            }

            return new MirCallable(
                id,
                name,
                source.ReturnType,
                parameters,
                source.EntryBlock,
                blocks,
                values,
                storages,
                origin);
        }

        internal static MirInstruction CloneInstruction(
            MirInstruction instruction,
            MirOrigin origin,
            Func<MirOrigin, MirOrigin>? qubitOrigin = null)
        {
            qubitOrigin ??= _ => origin;
            return instruction switch
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
                MirQubitAllocate value => new MirQubitAllocate(
                    value.Id,
                    new MirQubitFromUse(
                        value.Result.Id,
                        value.Result.Name,
                        value.Result.Length,
                        qubitOrigin(value.Result.Origin)),
                    origin),
                MirQuantumApply value => CloneApply(
                    value,
                    value.Target,
                    value.Functors,
                    value.Operands,
                    CloneQubitResults(value.QubitResults, qubitOrigin),
                    origin),
                MirMeasure value => new MirMeasure(
                    value.Id,
                    value.Result,
                    value.Qubit,
                    new MirQubitAfterInstruction(
                        value.QubitResult.Id,
                        value.QubitResult.Version,
                        qubitOrigin(value.QubitResult.Origin)),
                    origin),
                _ => throw new InvalidOperationException(
                    $"unknown MIR instruction {instruction.GetType().Name}"),
            };
        }

        private static MirTerminator CloneTerminator(
            MirTerminator terminator,
            MirOrigin origin) =>
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
