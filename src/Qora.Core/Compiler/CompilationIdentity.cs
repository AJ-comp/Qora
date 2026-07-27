namespace Qora.Compiler;

/// <summary>
/// Identifies one logical compilation. A recompilation of the same documents keeps this identity and
/// advances <see cref="CompilationRevision"/>; an unrelated compilation receives a new identity.
/// </summary>
public readonly record struct CompilationId(Guid Value)
{
    public static CompilationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Identifies one immutable aggregate snapshot within a logical compilation.
/// </summary>
public readonly record struct CompilationRevision
{
    public CompilationRevision(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies one HIR generation within a compilation snapshot.
/// </summary>
public readonly record struct HirRevision
{
    public HirRevision(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => $"h{Value}";
}

/// <summary>
/// Globally unambiguous reference to an immutable HIR generation.
/// </summary>
public readonly record struct HirSnapshotId(
    CompilationId CompilationId,
    CompilationRevision CompilationRevision,
    HirRevision Revision)
{
    public override string ToString() =>
        $"{CompilationId}@{CompilationRevision}/{Revision}";
}
