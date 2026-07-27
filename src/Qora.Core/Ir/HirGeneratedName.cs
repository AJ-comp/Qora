namespace Qora.Ir;

/// <summary>
/// Names compiler-generated HIR bindings outside the source identifier namespace. The marker contains a
/// character the grammar never accepts, while the numeric identity keeps generated bindings distinct
/// without scanning or mutating a scope.
/// </summary>
internal static class HirGeneratedName
{
    private const string Marker = "#hir#";

    public static string Create(string displayBase, int identity)
    {
        if (string.IsNullOrWhiteSpace(displayBase) || displayBase.Contains('#'))
            throw new ArgumentException(
                "a generated HIR display name must be a non-empty source-style identifier",
                nameof(displayBase));
        if (identity < 0)
            throw new ArgumentOutOfRangeException(nameof(identity));
        return $"{Marker}{identity}#{displayBase}";
    }

    /// <summary>The human-readable base carried by a generated name, or null for a source name.</summary>
    public static string? DisplayBase(string name)
    {
        if (!name.StartsWith(Marker, StringComparison.Ordinal))
            return null;
        var separator = name.IndexOf('#', Marker.Length);
        return separator < 0 || separator == name.Length - 1
            ? null
            : name[(separator + 1)..];
    }
}
