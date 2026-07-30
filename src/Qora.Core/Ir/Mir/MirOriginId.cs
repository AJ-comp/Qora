namespace Qora.Ir.Mir;

/// <summary>
/// The owner-local identity of one entry in a <see cref="MirOriginTable"/>.
/// Only compiler-owned builders can allocate an origin identity.
/// </summary>
public readonly record struct MirOriginId
{
    internal MirOriginId(int value) => Value = value;

    public int Value { get; }

    public override string ToString() => $"o{Value}";
}
