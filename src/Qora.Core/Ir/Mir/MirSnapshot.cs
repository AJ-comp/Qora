using System.Collections.Frozen;
using Qora.Compiler;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

/// <summary>
/// Identifies one immutable MIR generation inside one exact compilation snapshot.
/// </summary>
public readonly record struct MirSnapshotId
{
    public MirSnapshotId(
        CompilationId compilationId,
        CompilationRevision compilationRevision,
        int revision)
    {
        if (compilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "a MIR snapshot requires a non-empty compilation identity",
                nameof(compilationId));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));

        CompilationId = compilationId;
        CompilationRevision = compilationRevision;
        Revision = revision;
    }

    public CompilationId CompilationId { get; }
    public CompilationRevision CompilationRevision { get; }
    public int Revision { get; }

    public override string ToString() =>
        $"{CompilationId}@{CompilationRevision}/m{Revision}";
}

/// <summary>
/// Identifies the lowering contract which produced a MIR snapshot. A new profile must be introduced
/// whenever lowering changes facts that MIR analyses can observe.
/// </summary>
public enum MirLoweringProfile
{
    CanonicalV1,
}

/// <summary>
/// The exact location of an instruction within its callable and block.
/// </summary>
public readonly record struct MirInstructionLocation(
    MirBlockRef Block,
    int Index);

/// <summary>
/// Immutable structural indexes for one exact <see cref="MirProgram"/>. Every callable-local identity
/// is keyed through its program-wide composite reference, so equal dense IDs in two callables cannot
/// resolve to the wrong object.
/// </summary>
public sealed class MirStructuralIndex
{
    private readonly MirSnapshotId _snapshotId;
    private readonly FrozenDictionary<MirCallableId, MirCallable> _callables;
    private readonly FrozenDictionary<MirBlockRef, MirBlock> _blocks;
    private readonly FrozenDictionary<MirInstructionRef, MirInstruction> _instructions;
    private readonly FrozenDictionary<MirInstructionRef, MirInstructionLocation> _instructionLocations;
    private readonly FrozenDictionary<MirValueRef, MirValue> _values;
    private readonly FrozenDictionary<MirStorageRef, MirArrayStorage> _storages;
    private readonly FrozenDictionary<MirQubitResourceRef, MirQubitResource> _qubits;

    internal MirStructuralIndex(
        MirSnapshotId snapshotId,
        MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.SnapshotId != snapshotId)
            throw new ArgumentException(
                "the MIR program belongs to a different snapshot",
                nameof(program));

        _snapshotId = snapshotId;
        _callables = program.Callables.ToFrozenDictionary(callable => callable.Id);

        var blocks = new Dictionary<MirBlockRef, MirBlock>();
        var instructions = new Dictionary<MirInstructionRef, MirInstruction>();
        var instructionLocations =
            new Dictionary<MirInstructionRef, MirInstructionLocation>();
        var values = new Dictionary<MirValueRef, MirValue>();
        var storages = new Dictionary<MirStorageRef, MirArrayStorage>();
        var qubits = new Dictionary<MirQubitResourceRef, MirQubitResource>();

        foreach (var callable in program.Callables)
        {
            foreach (var block in callable.Blocks)
            {
                var blockRef = new MirBlockRef(snapshotId, callable.Id, block.Id);
                blocks.Add(blockRef, block);

                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    var instruction = block.Instructions[index];
                    var instructionRef =
                        new MirInstructionRef(snapshotId, callable.Id, instruction.Id);
                    instructions.Add(instructionRef, instruction);
                    instructionLocations.Add(
                        instructionRef,
                        new MirInstructionLocation(blockRef, index));
                }
            }

            foreach (var value in callable.Values)
                values.Add(new MirValueRef(snapshotId, callable.Id, value.Id), value);
            foreach (var storage in callable.Storages)
                storages.Add(new MirStorageRef(snapshotId, callable.Id, storage.Id), storage);
            foreach (var qubit in callable.Qubits)
                qubits.Add(new MirQubitResourceRef(snapshotId, callable.Id, qubit.Id), qubit);
        }

        _blocks = blocks.ToFrozenDictionary();
        _instructions = instructions.ToFrozenDictionary();
        _instructionLocations = instructionLocations.ToFrozenDictionary();
        _values = values.ToFrozenDictionary();
        _storages = storages.ToFrozenDictionary();
        _qubits = qubits.ToFrozenDictionary();
    }

    public MirSnapshotId SnapshotId => _snapshotId;
    public IReadOnlyCollection<MirCallableRef> Callables =>
        _callables.Keys.Select(callable => new MirCallableRef(_snapshotId, callable)).ToArray();
    public IReadOnlyCollection<MirBlockRef> Blocks => _blocks.Keys;
    public IReadOnlyCollection<MirInstructionRef> Instructions => _instructions.Keys;
    public IReadOnlyCollection<MirValueRef> Values => _values.Keys;
    public IReadOnlyCollection<MirStorageRef> Storages => _storages.Keys;
    public IReadOnlyCollection<MirQubitResourceRef> Qubits => _qubits.Keys;

    public MirCallable RequireCallable(MirCallableRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _callables.TryGetValue(reference.Callable, out var callable)
            ? callable
            : throw Missing(nameof(reference), reference);
    }

    internal MirCallable RequireCallableLocal(MirCallableId id) =>
        _callables.TryGetValue(id, out var callable)
            ? callable
            : throw Missing(nameof(id), id);

    public MirBlock RequireBlock(MirBlockRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _blocks.TryGetValue(reference, out var block)
            ? block
            : throw Missing(nameof(reference), reference);
    }

    public MirInstruction RequireInstruction(MirInstructionRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _instructions.TryGetValue(reference, out var instruction)
            ? instruction
            : throw Missing(nameof(reference), reference);
    }

    public MirInstructionLocation RequireInstructionLocation(MirInstructionRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _instructionLocations.TryGetValue(reference, out var location)
            ? location
            : throw Missing(nameof(reference), reference);
    }

    public MirValue RequireValue(MirValueRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _values.TryGetValue(reference, out var value)
            ? value
            : throw Missing(nameof(reference), reference);
    }

    public MirArrayStorage RequireStorage(MirStorageRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _storages.TryGetValue(reference, out var storage)
            ? storage
            : throw Missing(nameof(reference), reference);
    }

    public MirQubitResource RequireQubit(MirQubitResourceRef reference)
    {
        RequireSnapshot(reference.Snapshot, nameof(reference));
        return _qubits.TryGetValue(reference, out var qubit)
            ? qubit
            : throw Missing(nameof(reference), reference);
    }

    private void RequireSnapshot(MirSnapshotId actual, string parameter) =>
        MirReferenceValidation.RequireSnapshot(_snapshotId, actual, parameter);

    private static ArgumentOutOfRangeException Missing<T>(string parameter, T value) =>
        new(parameter, value, $"MIR reference {value} does not belong to this snapshot");
}

/// <summary>
/// One immutable MIR artifact tied to the exact HIR snapshot and lowering profile which produced it.
/// Structural indexes and analyses are owned by this snapshot rather than by a mutable global model.
/// </summary>
public sealed class MirSnapshot
{
    internal MirSnapshot(
        MirSnapshotId id,
        MirLoweringProfile profile,
        MirProgram program,
        MirCrossStageLinks links)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(links);

        if (id.CompilationId != links.LoweredFrom.CompilationId
            || id.CompilationRevision != links.LoweredFrom.CompilationRevision)
            throw new ArgumentException(
                "the MIR snapshot and its HIR origin must belong to the same compilation snapshot",
                nameof(links));
        if (id != program.SnapshotId)
            throw new ArgumentException(
                $"MIR snapshot identity {id} disagrees with program identity {program.SnapshotId}",
                nameof(program));
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "unknown MIR lowering profile");
        if (links.MirSnapshot != id)
            throw new ArgumentException(
                "the cross-stage links do not belong to this MIR snapshot",
                nameof(links));
        if (!ReferenceEquals(program.Origins, links.Origins))
            throw new ArgumentException(
                "the MIR program and its cross-stage links must share one origin table",
                nameof(links));

        QoraMirVerifier.VerifyOrThrow(program);

        Id = id;
        Profile = profile;
        Program = program;
        Links = links;
        Structure = new MirStructuralIndex(id, program);
        links.VerifyAgainst(Structure);
        Analyses = new MirAnalysisStore(this);
    }

    public MirSnapshotId Id { get; }
    public HirSnapshotId LoweredFrom => Links.LoweredFrom;
    public MirLoweringProfile Profile { get; }
    public MirProgram Program { get; }
    public MirOriginTable Origins => Program.Origins;
    public MirCrossStageLinks Links { get; }
    public MirStructuralIndex Structure { get; }
    public MirAnalysisStore Analyses { get; }
}
