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
    private MirAdjointMaterializationResult(
        MirSnapshot source,
        MirSnapshot? output,
        IReadOnlyDictionary<MirCallableId, MirCallableId> inverses,
        IReadOnlyList<MirAdjointMaterializationError> errors)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Output = output;
        Inverses = inverses.ToFrozenDictionary();
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    internal static MirAdjointMaterializationResult Unchanged(MirSnapshot source) =>
        new(
            source,
            output: null,
            new Dictionary<MirCallableId, MirCallableId>(),
            Array.Empty<MirAdjointMaterializationError>());

    internal static MirAdjointMaterializationResult Failure(
        MirSnapshot source,
        IReadOnlyList<MirAdjointMaterializationError> errors)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
            throw new ArgumentException("A failed materialization requires at least one error.", nameof(errors));

        foreach (var error in errors)
        {
            ArgumentNullException.ThrowIfNull(error);
            _ = source.Program.RequireCallable(error.Callable);
            ArgumentNullException.ThrowIfNull(error.Origin);
        }

        return new MirAdjointMaterializationResult(
            source,
            output: null,
            new Dictionary<MirCallableId, MirCallableId>(),
            errors);
    }

    internal static MirAdjointMaterializationResult Success(
        MirSnapshot output,
        IReadOnlyDictionary<MirCallableId, MirCallableId> inverses)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(inverses);
        if (output.Stage != MirStage.AdjointsMaterialized
            || output.PreviousSnapshot is not { } source)
        {
            throw new ArgumentException(
                "The materialized output must be an adjoint stage transformed from a source snapshot.",
                nameof(output));
        }

        foreach (var (sourceCallable, inverseCallable) in inverses)
        {
            _ = source.Program.RequireCallable(sourceCallable);
            _ = output.Program.RequireCallable(inverseCallable);
        }

        return new MirAdjointMaterializationResult(
            source,
            output,
            inverses,
            Array.Empty<MirAdjointMaterializationError>());
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
        var planner = new Planner(source);
        return planner.HasRequests
            ? planner.Run()
            : MirAdjointMaterializationResult.Unchanged(source);
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

    private static HashSet<MirCallableId> RequestedCallableInverses(
        MirSnapshot snapshot) =>
        UserCallableApplies(snapshot)
            .Where(call => ContainsAdjoint(call.Apply.Functors))
            .Select(call => call.Site.Callee)
            .ToHashSet();

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
            introducesRequest |= !ContainsAdjoint(apply.Functors);
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
        MirCallable? rewrittenEntryPoint = null;
        foreach (var callable in source.Program.Callables)
        {
            MirInstruction AddRequestIfSelected(MirInstruction instruction)
            {
                var site = new MirInstructionSite(
                    callable.Id,
                    instruction.Id);
                if (!requested.Contains(site)
                    || instruction is not MirQuantumApply apply
                    || ContainsAdjoint(apply.Functors))
                {
                    return Planner.CloneInstruction(instruction);
                }

                changed = true;
                var functors =
                    new List<MirFunctor>(apply.Functors.Count + 1)
                    {
                        MirFunctor.Adjoint,
                    };
                functors.AddRange(apply.Functors);
                return Planner.CloneApply(apply, apply.Target, functors);
            }

            var rewrittenCallable = Planner.CloneCallable(callable, AddRequestIfSelected);
            callables.Add(rewrittenCallable);

            if (ReferenceEquals(callable, source.Program.EntryPoint))
                rewrittenEntryPoint = rewrittenCallable;
        }

        if (!changed)
            return source;

        var program = new MirProgram(
            rewrittenEntryPoint ?? throw new InvalidOperationException(
                "QINTERNAL: MIR inverse-request injection dropped the entry point"),
            callables);
        return MirSnapshot.CreateTransformed(
            program,
            source);
    }

    private sealed class Planner
    {
        private readonly MirSnapshot _source;
        private readonly MirCallGraph _callGraph;
        private readonly HashSet<MirCallableId> _required;
        private readonly List<MirAdjointMaterializationError> _errors = new();
        private readonly Dictionary<MirCallableId, VisitState> _visit = new();

        public Planner(MirSnapshot source)
        {
            _source = source;
            _callGraph = source.Analyses.CallGraph;
            _required = RequestedCallableInverses(source);
        }

        public bool HasRequests => _required.Count != 0;

        public MirAdjointMaterializationResult Run()
        {
            foreach (var callable in _required.ToArray())
                Discover(callable);
            if (_errors.Count > 0)
                return Failure();

            var nextCallableId = (long)_source.Program.Callables.Max(
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
            MirCallable? rewrittenEntryPoint = null;
            foreach (var callable in _source.Program.Callables)
            {
                var rewrittenCallable = RewriteSourceCallable(
                    callable,
                    inverseIds);
                rewritten.Add(rewrittenCallable);

                if (ReferenceEquals(callable, _source.Program.EntryPoint))
                    rewrittenEntryPoint = rewrittenCallable;
            }

            foreach (var original in _required.OrderBy(id => id.Value))
            {
                var inverseCallable = CreateInverseCallable(
                    _source.Program.RequireCallable(original),
                    inverseIds);
                rewritten.Add(inverseCallable);
            }

            var program = new MirProgram(
                rewrittenEntryPoint ?? throw new InvalidOperationException(
                    "QINTERNAL: MIR adjoint materialization dropped the entry point"),
                rewritten);
            var output = MirSnapshot.CreateTransformed(
                program,
                _source);
            VerifyMaterialized(output);

            return MirAdjointMaterializationResult.Success(output, inverseIds);
        }

        private void Discover(MirCallableId callableId)
        {
            if (_visit.TryGetValue(callableId, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    var recursiveCallable = _source.Program.RequireCallable(callableId);
                    Error(
                        recursiveCallable,
                        "recursive callable inversion is not supported");
                }
                return;
            }

            var callable = _source.Program.RequireCallable(callableId);
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
                if (ContainsAdjoint(apply.Functors))
                    continue;
                _required.Add(site.Callee);
                Discover(site.Callee);
            }
            _visit[callableId] = VisitState.Visited;
        }

        private bool ValidateInvertibleShape(MirCallable callable)
        {
            var valid = true;
            if (callable.Blocks.Count != 1
                || callable.Blocks[0].Arguments.Count != 0
                || callable.Blocks[0].Terminator is not MirReturn { Value: null })
            {
                Error(
                    callable,
                    "automatic MIR inversion currently requires one straight-line block");
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
                            instruction,
                            "classical array mutation is not reversible");
                        valid = false;
                        break;

                    case MirMeasure:
                        Error(
                            callable,
                            instruction,
                            "measurement is not unitary and cannot be inverted");
                        valid = false;
                        break;

                    case MirQubitAllocate:
                        Error(
                            callable,
                            instruction,
                            "local qubit allocation requires an explicit cleanup region");
                        valid = false;
                        break;

                    default:
                        Error(
                            callable,
                            instruction,
                            $"instruction {instruction.GetType().Name} is not supported by MIR inversion");
                        valid = false;
                        break;
                }
            }
            return valid;
        }

        private void Error(
            MirCallable callable,
            string message) =>
            AddError(callable, callable.Origin, message);

        private void Error(
            MirCallable callable,
            MirInstruction instruction,
            string message) =>
            AddError(callable, instruction.Origin, message);

        private void AddError(
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
            MirAdjointMaterializationResult.Failure(_source, _errors);

        private static bool IsReadOnlyBorrow(MirCallOperand operand) =>
            operand.Ownership == QOwnershipMode.Borrowed
            && operand.Access == QAccessMode.ReadOnly;

        private bool IsUnitary(MirQuantumApply apply)
        {
            if (apply.Target is MirBuiltinGateTarget builtin)
                return !QoraGates.NonUnitary.Contains(builtin.Name);
            return apply.Target is MirUserCallableTarget;
        }

        private static MirCallable RewriteSourceCallable(
            MirCallable callable,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds) =>
            CloneCallable(
                callable,
                instruction => RewriteForwardInstruction(instruction, inverseIds));

        private static MirInstruction RewriteForwardInstruction(
            MirInstruction instruction,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds)
        {
            if (instruction is MirQuantumApply
                {
                    Target: MirUserCallableTarget user
                } apply
                && ContainsAdjoint(apply.Functors))
            {
                if (!inverseIds.TryGetValue(user.Callable, out var inverse))
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: no synthesized inverse was allocated for {user.Callable}");
                }
                return CloneApply(
                    apply,
                    new MirUserCallableTarget(inverse),
                    WithoutAdjoint(apply.Functors));
            }
            return CloneInstruction(instruction);
        }

        private static MirCallable CreateInverseCallable(
            MirCallable original,
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

                var origin = MirOrigin.GeneratedFrom(
                    instruction.Origin,
                    $"classical witness for inverse of {original.Id}");
                var classicalWitness = CloneInstructionWithOrigin(
                    instruction,
                    origin,
                    definitionOrigin: _ => origin);
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

            return CloneCallableWithInstructions(
                original,
                inverseIds[original.Id],
                $"__qora_inverse_{original.Id.Value}_{original.Name}",
                MirOrigin.GeneratedFrom(
                    original.Origin,
                    $"inverse callable of {original.Id}"),
                instructions,
                originForRole: (origin, role) =>
                    MirOrigin.GeneratedFrom(
                        origin,
                        $"inverse {role} of {original.Id}"));
        }

        private static MirQuantumApply InvertApply(
            MirQuantumApply apply,
            IReadOnlyDictionary<MirCallableId, MirCallableId> inverseIds,
            InverseQubitVersions qubitVersions)
        {
            var toggledAdjoint = !ContainsAdjoint(apply.Functors);
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

            var origin = MirOrigin.GeneratedFrom(
                apply.Origin,
                $"inverse quantum instruction {apply.Id}");
            var operands = qubitVersions.RewriteOperands(apply.Operands);
            var results = qubitVersions.CreateResults(apply.QubitResults, origin);
            return CloneApplyCore(
                apply,
                target,
                functors,
                operands,
                results,
                origin,
                definitionOrigin: _ => origin);
        }

        internal static MirQuantumApply CloneApply(
            MirQuantumApply source,
            MirCallTarget target,
            IReadOnlyList<MirFunctor> functors) =>
            CloneApplyCore(
                source,
                target,
                functors,
                source.Operands,
                CloneQubitResults(
                    source.QubitResults,
                    static sourceOrigin => sourceOrigin),
                source.Origin);

        private static MirQuantumApply CloneApplyCore(
            MirQuantumApply source,
            MirCallTarget target,
            IReadOnlyList<MirFunctor> functors,
            IReadOnlyList<MirCallOperand> operands,
            IReadOnlyList<MirQubitAfterInstruction> qubitResults,
            MirOrigin origin,
            Func<MirOrigin, MirOrigin>? definitionOrigin = null)
        {
            definitionOrigin ??= static sourceOrigin => sourceOrigin;
            var mutableArrayResults =
                new MirMutableArrayResult[source.MutableArrayResults.Count];
            for (var index = 0; index < source.MutableArrayResults.Count; index++)
            {
                var sourceResult = source.MutableArrayResults[index];
                mutableArrayResults[index] = new MirMutableArrayResult(
                    sourceResult.OperandIndex,
                    CloneValue(
                        sourceResult.Result,
                        definitionOrigin(sourceResult.Result.Origin)));
            }

            return new MirQuantumApply(
                source.Id,
                target,
                operands,
                qubitResults,
                mutableArrayResults,
                functors,
                origin);
        }

        private static IReadOnlyList<MirQubitAfterInstruction> CloneQubitResults(
            IReadOnlyList<MirQubitAfterInstruction> results,
            Func<MirOrigin, MirOrigin> origin) =>
            MirCollections.Freeze(results
                .Select(result => new MirQubitAfterInstruction(
                    result.Id,
                    result.Version,
                    origin(result.Origin))));

        /// <summary>
        /// Rebuilds the callable-local qubit SSA chain while forward instructions are emitted in reverse
        /// order. Stable qubit identities are retained, but every inverse instruction consumes the
        /// version current at its new position and allocates fresh versions for its writes.
        /// </summary>
        private sealed class InverseQubitVersions
        {
            private readonly Dictionary<MirQubitId, MirQubit> _current = new();
            private readonly Dictionary<MirQubitId, int> _nextVersion = new();

            public InverseQubitVersions(IEnumerable<MirQubitParameter> parameters)
            {
                foreach (var parameter in parameters)
                {
                    if (!_current.TryAdd(parameter.Id, parameter))
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
                    _current[forward.Id] = result;
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
            Func<MirInstruction, MirInstruction> instructionTransform) =>
            CloneCallableCore(
                source,
                source.Id,
                source.Name,
                source.Origin,
                block => CloneInstructions(block.Instructions, instructionTransform));

        private static MirCallable CloneCallableWithInstructions(
            MirCallable source,
            MirCallableId id,
            string name,
            MirOrigin origin,
            IReadOnlyList<MirInstruction> instructions,
            Func<MirOrigin, string, MirOrigin> originForRole)
        {
            if (source.Blocks.Count != 1)
            {
                throw new ArgumentException(
                    "An inverse callable can only replace the instructions of one straight-line source block.",
                    nameof(source));
            }

            return CloneCallableCore(
                source,
                id,
                name,
                origin,
                _ => instructions,
                originForRole);
        }

        private static IReadOnlyList<MirInstruction> CloneInstructions(
            IReadOnlyList<MirInstruction> source,
            Func<MirInstruction, MirInstruction> transform)
        {
            var cloned = new MirInstruction[source.Count];
            for (var index = 0; index < source.Count; index++)
                cloned[index] = transform(source[index]);
            return cloned;
        }

        private static MirCallable CloneCallableCore(
            MirCallable source,
            MirCallableId id,
            string name,
            MirOrigin origin,
            Func<MirBlock, IReadOnlyList<MirInstruction>> instructionsForBlock,
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
                    {
                        var clonedValue = CloneValue(
                            classical.Value,
                            OriginForRole(
                                classical.Value.Origin,
                                "SSA value"));
                        if (classical.Storage is { } sourceStorage)
                        {
                            var clonedStorage = CloneStorage(
                                sourceStorage,
                                OriginForRole(
                                    sourceStorage.Origin,
                                    "storage"));
                            clonedParameter = MirClassicalParameter.Array(
                                classical.Name,
                                clonedValue,
                                clonedStorage,
                                classical.Ownership,
                                classical.Access,
                                classical.MinimumLength);
                        }
                        else
                        {
                            clonedParameter = MirClassicalParameter.Scalar(
                                classical.Name,
                                clonedValue,
                                classical.Ownership,
                                classical.Access);
                        }
                        break;
                    }

                    case MirQubitParameter qubit:
                        var clonedQubitOrigin = OriginForRole(
                            qubit.Origin,
                            "qubit parameter");
                        clonedParameter = qubit.IsArray
                            ? MirQubitParameter.Array(
                                qubit.Id,
                                qubit.Name,
                                qubit.Length,
                                qubit.Ownership,
                                clonedQubitOrigin)
                            : MirQubitParameter.Single(
                                qubit.Id,
                                qubit.Name,
                                qubit.Ownership,
                                clonedQubitOrigin);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"unknown MIR parameter {sourceParameter.GetType().Name}");
                }

                parameters[index] = clonedParameter;
            }

            var blocks = new MirBlock[source.Blocks.Count];
            MirBlock? entryBlock = null;
            for (var blockIndex = 0;
                 blockIndex < source.Blocks.Count;
                 blockIndex++)
            {
                var sourceBlock = source.Blocks[blockIndex];
                var arguments = new MirValue[sourceBlock.Arguments.Count];
                for (var argumentIndex = 0;
                     argumentIndex < sourceBlock.Arguments.Count;
                     argumentIndex++)
                {
                    var sourceArgument = sourceBlock.Arguments[argumentIndex];
                    arguments[argumentIndex] = CloneValue(
                        sourceArgument,
                        OriginForRole(
                            sourceArgument.Origin,
                            "SSA value"));
                }

                var instructions = instructionsForBlock(sourceBlock);

                var qubitPhis =
                    new MirQubitPhi[sourceBlock.QubitPhis.Count];
                for (var phiIndex = 0;
                     phiIndex < sourceBlock.QubitPhis.Count;
                     phiIndex++)
                {
                    var sourcePhi = sourceBlock.QubitPhis[phiIndex];
                    qubitPhis[phiIndex] = new MirQubitPhi(
                        sourcePhi.Version,
                        sourcePhi.Inputs,
                        OriginForRole(
                            sourcePhi.Origin,
                            "qubit Phi"));
                }

                var clonedBlock = sourceBlock with
                {
                    Arguments = arguments,
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
                blocks[blockIndex] = clonedBlock;
                if (ReferenceEquals(sourceBlock, source.EntryBlock))
                    entryBlock = clonedBlock;
            }

            if (entryBlock is null)
                throw new InvalidOperationException("the MIR entry block was not cloned");

            return new MirCallable(
                id,
                name,
                source.ReturnType,
                parameters,
                entryBlock,
                blocks,
                origin);
        }

        internal static MirInstruction CloneInstruction(MirInstruction instruction) =>
            CloneInstructionWithOrigin(instruction, instruction.Origin);

        private static MirInstruction CloneInstructionWithOrigin(
            MirInstruction instruction,
            MirOrigin origin,
            Func<MirOrigin, MirOrigin>? definitionOrigin = null)
        {
            definitionOrigin ??= static sourceOrigin => sourceOrigin;
            return instruction switch
            {
                MirConstant value => new MirConstant(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Text,
                    origin),
                MirUnary value => new MirUnary(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Operator,
                    value.Operand,
                    origin),
                MirBinary value => new MirBinary(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Operator,
                    value.Left,
                    value.Right,
                    origin),
                MirConvert value => new MirConvert(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Operand,
                    origin),
                MirArrayCreate value => new MirArrayCreate(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    CloneStorage(
                        value.Storage,
                        definitionOrigin(value.Storage.Origin)),
                    value.Initialization,
                    value.Elements,
                    origin),
                MirArrayLength value => new MirArrayLength(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Array,
                    origin),
                MirArrayLoad value => new MirArrayLoad(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Array,
                    value.Index,
                    origin),
                MirArrayStore value => new MirArrayStore(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Array,
                    value.Index,
                    value.Value,
                    origin),
                MirPureCall value => new MirPureCall(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Target,
                    value.Operands,
                    origin),
                MirQubitAllocate value => new MirQubitAllocate(
                    value.Id,
                    new MirQubitFromUse(
                        value.Result.Id,
                        value.Result.Name,
                        value.Result.Length,
                        definitionOrigin(value.Result.Origin)),
                    origin),
                MirQuantumApply value => CloneApplyCore(
                    value,
                    value.Target,
                    value.Functors,
                    value.Operands,
                    CloneQubitResults(value.QubitResults, definitionOrigin),
                    origin,
                    definitionOrigin),
                MirMeasure value => new MirMeasure(
                    value.Id,
                    CloneValue(value.Result, definitionOrigin(value.Result.Origin)),
                    value.Qubit,
                    new MirQubitAfterInstruction(
                        value.QubitResult.Id,
                        value.QubitResult.Version,
                        definitionOrigin(value.QubitResult.Origin)),
                    origin),
                _ => throw new InvalidOperationException(
                    $"unknown MIR instruction {instruction.GetType().Name}"),
            };
        }

        private static MirValue CloneValue(
            MirValue source,
            MirOrigin origin) =>
            new(
                source.Id,
                source.Type,
                origin);

        private static MirArrayStorage CloneStorage(
            MirArrayStorage source,
            MirOrigin origin) =>
            new(
                source.Id,
                source.Name,
                origin);

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
