namespace Qora.Ir.Mir;

/// <summary>
/// A callable address which remains unambiguous across all compilations and MIR revisions.
/// <see cref="MirCallableId"/> is only a dense identity local to one MIR snapshot.
/// </summary>
public readonly record struct MirCallableRef(
    MirSnapshotId Snapshot,
    MirCallableId Callable)
{
    public override string ToString() => $"{Snapshot}/{Callable}";
}

/// <summary>
/// A block address which remains unambiguous across all compilations and MIR revisions.
/// <see cref="MirBlockId"/> values are dense identities local to one callable.
/// </summary>
public readonly record struct MirBlockRef(
    MirSnapshotId Snapshot,
    MirCallableId Callable,
    MirBlockId Block)
{
    public MirCallableRef CallableRef => new(Snapshot, Callable);

    public override string ToString() => $"{Snapshot}/{Callable}/{Block}";
}

/// <summary>
/// An instruction address which remains unambiguous across all compilations and MIR revisions.
/// <see cref="MirInstructionId"/> values are dense identities local to one callable.
/// </summary>
public readonly record struct MirInstructionRef(
    MirSnapshotId Snapshot,
    MirCallableId Callable,
    MirInstructionId Instruction)
{
    public MirCallableRef CallableRef => new(Snapshot, Callable);

    public override string ToString() => $"{Snapshot}/{Callable}/{Instruction}";
}

/// <summary>
/// An SSA value address which remains unambiguous across all compilations and MIR revisions.
/// <see cref="MirValueId"/> values are dense identities local to one callable.
/// </summary>
public readonly record struct MirValueRef(
    MirSnapshotId Snapshot,
    MirCallableId Callable,
    MirValueId Value)
{
    public MirCallableRef CallableRef => new(Snapshot, Callable);

    public override string ToString() => $"{Snapshot}/{Callable}/{Value}";
}

/// <summary>
/// An array-storage address which remains unambiguous across all compilations and MIR revisions.
/// <see cref="MirStorageId"/> values are dense identities local to one callable.
/// </summary>
public readonly record struct MirStorageRef(
    MirSnapshotId Snapshot,
    MirCallableId Callable,
    MirStorageId Storage)
{
    public MirCallableRef CallableRef => new(Snapshot, Callable);

    public override string ToString() => $"{Snapshot}/{Callable}/{Storage}";
}

/// <summary>
/// The exact snapshot-qualified address of one version of one qubit binding. The raw callable-local
/// identity and version remain readable for analysis queries, while construction stays inside the
/// compiler so external callers cannot manufacture detached MIR identities.
/// </summary>
public readonly record struct MirQubitRef
{
    internal MirQubitRef(
        MirSnapshotId snapshot,
        MirCallableId callable,
        MirQubitId id,
        MirQubitVersion version)
    {
        Snapshot = snapshot;
        Callable = callable;
        Id = id;
        Version = version;
    }

    internal MirQubitRef(
        MirSnapshotId snapshot,
        MirCallableId callable,
        MirQubitKey key)
        : this(snapshot, callable, key.Id, key.Version)
    {
    }

    public MirSnapshotId Snapshot { get; }
    public MirCallableId Callable { get; }
    public MirQubitId Id { get; }
    public MirQubitVersion Version { get; }
    public MirQubitKey Key => new(Id, Version);
    public MirCallableRef CallableRef => new(Snapshot, Callable);

    public override string ToString() => $"{Snapshot}/{Callable}/{Key}";
}

/// <summary>
/// A versioned qubit access projected across the MIR snapshot boundary. A dynamic index, when present,
/// must be an SSA value from the same snapshot and callable as the accessed qubit version.
/// </summary>
public readonly record struct MirQubitAccessRef
{
    internal MirQubitAccessRef(
        MirQubitRef qubit,
        MirValueRef? index = null)
    {
        if (index is MirValueRef value
            && (value.Snapshot != qubit.Snapshot
                || value.Callable != qubit.Callable))
        {
            throw new ArgumentException(
                "a qubit-access index must belong to the same MIR snapshot and callable as its qubit",
                nameof(index));
        }

        Qubit = qubit;
        Index = index;
    }

    public MirQubitRef Qubit { get; }
    public MirValueRef? Index { get; }
    public MirSnapshotId Snapshot => Qubit.Snapshot;
    public MirCallableId Callable => Qubit.Callable;
}

/// <summary>
/// An origin address which is valid only in the exact immutable MIR snapshot that owns its table.
/// The integer component remains dense, but it is never exposed without its snapshot identity.
/// </summary>
public readonly record struct MirOriginRef(
    MirSnapshotId Snapshot,
    int Value)
{
    public override string ToString() => $"{Snapshot}/o{Value}";
}

internal static class MirReferenceValidation
{
    public static void RequireSnapshot(
        MirSnapshotId expected,
        MirSnapshotId actual,
        string parameterName)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                $"MIR reference belongs to snapshot {actual}; expected {expected}",
                parameterName);
        }
    }
}
