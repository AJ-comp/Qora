using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Qora.Ir;

/// <summary>
/// Freezes collections at the HIR boundary. An <see cref="IReadOnlyList{T}"/> or
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> type alone is not enough: a caller can retain and
/// mutate the original <see cref="List{T}"/> or <see cref="Dictionary{TKey,TValue}"/> after construction.
/// Every collection-bearing HIR node copies through these helpers in its internal constructor, so a
/// lowering or rewrite session cannot leak a mutable collection into a published snapshot.
/// </summary>
internal static class HirCollections
{
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is ImmutableArray<T> { IsDefault: false } immutable
            ? immutable
            : ImmutableArray.CreateRange(source);
    }

    internal static IReadOnlyList<T>? FreezeNullable<T>(IEnumerable<T>? source) =>
        source is null ? null : Freeze(source);

    internal static IReadOnlyDictionary<TKey, TValue> Freeze<TKey, TValue>(
        IEnumerable<KeyValuePair<TKey, TValue>> source)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is FrozenDictionary<TKey, TValue> frozen
            ? frozen
            : source.ToFrozenDictionary(pair => pair.Key, pair => pair.Value);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<T>>? FreezeNested<T>(
        IReadOnlyDictionary<string, IReadOnlyList<T>>? source)
    {
        if (source is null) return null;
        return source.ToFrozenDictionary(
            pair => pair.Key,
            pair => Freeze(pair.Value),
            StringComparer.Ordinal);
    }

    internal static IReadOnlySet<T> FreezeSet<T>(IEnumerable<T> source)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is FrozenSet<T> frozen
            ? frozen
            : source.ToFrozenSet();
    }
}
