using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// Storage provenance for every classical array-state SSA value in one callable and MIR revision.
/// The result names all symbolic storage regions which can reach a state through stores, mutable-call
/// transitions, and memory Phi arguments. It does not by itself prove that different regions are
/// disjoint; consumers must pass the result through <see cref="MirStorageAliasAnalysis"/>.
/// </summary>
public sealed class MirStorageProvenanceSnapshot
{
    private readonly MirProgram _sourceProgram;
    private readonly MirCallable _sourceCallable;
    private readonly FrozenDictionary<MirValueId, MirStorageProvenance> _provenance;

    internal MirStorageProvenanceSnapshot(
        MirProgram sourceProgram,
        MirCallable sourceCallable,
        IReadOnlyDictionary<MirValueId, MirStorageProvenance> provenance)
    {
        _sourceProgram = sourceProgram;
        _sourceCallable = sourceCallable;
        SnapshotId = sourceProgram.SnapshotId;
        Callable = new MirCallableRef(SnapshotId, sourceCallable.Id);
        _provenance = provenance.ToFrozenDictionary();
    }

    public MirSnapshotId SnapshotId { get; }
    public MirCallableRef Callable { get; }

    internal bool IsFor(MirProgram program, MirCallableId callable) =>
        ReferenceEquals(_sourceProgram, program)
        && ReferenceEquals(_sourceCallable, program.FindCallable(callable))
        && SnapshotId == program.SnapshotId
        && Callable.Callable == callable;

    internal void EnsureFor(MirProgram program, MirCallableId callable)
    {
        if (!IsFor(program, callable))
            throw new InvalidOperationException(
                $"MIR storage-provenance snapshot belongs to {Callable} in snapshot {SnapshotId}; " +
                $"reanalyze {callable} in snapshot {program.SnapshotId}");
    }

    public MirStorageProvenance ProvenanceOf(MirValueRef value)
    {
        Require(value);
        return ProvenanceOf(value.Value);
    }

    internal MirStorageProvenance ProvenanceOf(MirValueId value) =>
        _provenance.TryGetValue(value, out var provenance)
            ? provenance
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"value {value} is not an array state in callable {Callable}");

    private void Require(MirValueRef value)
    {
        MirReferenceValidation.RequireSnapshot(
            SnapshotId,
            value.Snapshot,
            nameof(value));
        if (value.Callable != Callable.Callable)
            throw new ArgumentException(
                $"MIR value belongs to callable {value.Callable}; expected {Callable}",
                nameof(value));
    }
}

internal static class MirStorageProvenanceAnalysis
{
    internal static MirStorageProvenanceSnapshot Analyze(
        MirProgram program,
        MirCallableId callableId)
    {
        ArgumentNullException.ThrowIfNull(program);
        QoraMirVerifier.VerifyOrThrow(program);

        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        return AnalyzeUnchecked(program, callable);
    }

    /// <summary>
    /// Resolves provenance after structural MIR validation. Kept separate from <see cref="Analyze"/>
    /// so graph-wide verifier checks can consume provenance without verifier-analysis recursion.
    /// </summary>
    internal static MirStorageProvenanceSnapshot AnalyzeUnchecked(
        MirProgram program,
        MirCallable callable)
    {
        var resolver = new Resolver(program.SnapshotId, callable);
        var provenance = callable.Values
            .Where(value => value.Type.IsArray)
            .ToDictionary(value => value.Id, value => resolver.Resolve(value.Id));
        return new MirStorageProvenanceSnapshot(
            program,
            callable,
            provenance);
    }

    private sealed class Resolver
    {
        private readonly MirCallable _callable;
        private readonly MirSnapshotId _snapshotId;
        private readonly Dictionary<MirValueId, MirStorageId> _seeds = new();
        private readonly Dictionary<MirValueId, List<MirValueId>> _dependencies = new();
        private readonly HashSet<MirValueId> _unknownRoots = new();
        private readonly Dictionary<MirValueId, MirStorageProvenance> _cache = new();

        public Resolver(
            MirSnapshotId snapshotId,
            MirCallable callable)
        {
            _snapshotId = snapshotId;
            _callable = callable;
            Build();
        }

        internal MirStorageProvenance Resolve(MirValueId value)
        {
            if (_cache.TryGetValue(value, out var cached))
                return cached;

            var storages = new HashSet<MirStorageId>();
            var visited = new HashSet<MirValueId>();
            var complete = true;

            void Walk(MirValueId current)
            {
                if (!visited.Add(current)) return;
                if (_seeds.TryGetValue(current, out var storage))
                {
                    storages.Add(storage);
                    return;
                }
                if (_unknownRoots.Contains(current)
                    || !_dependencies.TryGetValue(current, out var dependencies)
                    || dependencies.Count == 0)
                {
                    complete = false;
                    return;
                }
                foreach (var dependency in dependencies)
                    Walk(dependency);
            }

            Walk(value);
            if (storages.Count == 0) complete = false;
            var result = new MirStorageProvenance(
                new ReadOnlyCollection<MirStorageRef>(
                    storages
                        .OrderBy(storage => storage.Value)
                        .Select(storage => new MirStorageRef(
                            _snapshotId,
                            _callable.Id,
                            storage))
                        .ToArray()),
                complete);
            _cache.Add(value, result);
            return result;
        }

        private void Build()
        {
            var incoming = IncomingBlockArguments();
            var instructionById = _callable.Blocks
                .SelectMany(block => block.Instructions)
                .ToDictionary(instruction => instruction.Id);

            foreach (var parameter in _callable.Parameters.OfType<MirClassicalParameter>())
            {
                if (!parameter.Type.IsArray) continue;
                if (parameter.Storage is MirStorageId storage)
                    _seeds[parameter.Value] = storage;
                else
                    _unknownRoots.Add(parameter.Value);
            }

            foreach (var value in _callable.Values.Where(value => value.Type.IsArray))
            {
                if (_seeds.ContainsKey(value.Id)) continue;
                switch (value.Definition.Kind)
                {
                    case MirValueDefinitionKind.BlockArgument
                        when value.Definition.Block is MirBlockId block:
                        if (incoming.TryGetValue(
                                (block, value.Definition.Index),
                                out var arguments)
                            && arguments.Count != 0)
                            _dependencies[value.Id] = arguments;
                        else
                            _unknownRoots.Add(value.Id);
                        break;

                    case MirValueDefinitionKind.InstructionResult
                        when value.Definition.Instruction is MirInstructionId instructionId
                             && instructionById.TryGetValue(instructionId, out var instruction):
                        AddInstructionDefinition(value.Id, instruction);
                        break;

                    case MirValueDefinitionKind.Parameter:
                        _unknownRoots.Add(value.Id);
                        break;

                    default:
                        _unknownRoots.Add(value.Id);
                        break;
                }
            }
        }

        private void AddInstructionDefinition(MirValueId result, MirInstruction instruction)
        {
            switch (instruction)
            {
                case MirArrayCreate create when create.Result == result:
                    _seeds[result] = create.Storage;
                    break;
                case MirArrayStore store when store.Result == result:
                    _dependencies[result] = new List<MirValueId> { store.Array };
                    break;
                case MirConvert convert when convert.Result == result:
                    _dependencies[result] = new List<MirValueId> { convert.Operand };
                    break;
                case MirQuantumApply apply:
                {
                    var transition = apply.MutableArrayResults
                        .FirstOrDefault(candidate => candidate.Result == result);
                    if (transition is not null
                        && transition.OperandIndex >= 0
                        && transition.OperandIndex < apply.Operands.Count
                        && apply.Operands[transition.OperandIndex] is MirClassicalCallOperand operand)
                        _dependencies[result] = new List<MirValueId> { operand.Value };
                    else
                        _unknownRoots.Add(result);
                    break;
                }
                default:
                    _unknownRoots.Add(result);
                    break;
            }
        }

        private Dictionary<(MirBlockId Block, int Argument), List<MirValueId>>
            IncomingBlockArguments()
        {
            var incoming = new Dictionary<(MirBlockId, int), List<MirValueId>>();

            void Add(MirBlockId target, IReadOnlyList<MirValueId> arguments)
            {
                for (var index = 0; index < arguments.Count; index++)
                {
                    var key = (target, index);
                    if (!incoming.TryGetValue(key, out var values))
                        incoming.Add(key, values = new List<MirValueId>());
                    values.Add(arguments[index]);
                }
            }

            foreach (var block in _callable.Blocks)
            {
                switch (block.Terminator)
                {
                    case MirJump jump:
                        Add(jump.Target, jump.Arguments);
                        break;
                    case MirBranch branch:
                        Add(branch.TrueTarget, branch.TrueArguments);
                        Add(branch.FalseTarget, branch.FalseArguments);
                        break;
                }
            }
            return incoming;
        }
    }
}
