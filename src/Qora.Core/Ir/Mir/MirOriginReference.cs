namespace Qora.Ir.Mir;

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

internal static class MirOriginValidation
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
