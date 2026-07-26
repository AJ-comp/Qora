using System.Collections.Immutable;

namespace Qora.Ir.Mir;

/// <summary>
/// Freezes collections at MIR object boundaries. <see cref="IReadOnlyList{T}"/> prevents mutation
/// through the exposed property, but it does not prevent a caller from retaining and changing the
/// original <see cref="List{T}"/>. Every collection-bearing MIR record uses this helper in both
/// construction and <c>with</c>-expression initialization.
/// </summary>
internal static class MirCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is ImmutableArray<T> { IsDefault: false } immutable
            ? immutable
            : ImmutableArray.CreateRange(source);
    }
}
