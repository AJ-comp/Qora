namespace Qora.Ir;

/// <summary>
/// The placeholder-name convention shared by <see cref="ArrayLocalHoisting"/> (which MINTS placeholders)
/// and <see cref="Passes.NameMangler"/> (which turns them into pretty emitted names). A placeholder is
/// <c>#hoist#{base}#{uid}</c>: the <c>uid</c> makes the WHOLE string unique across the program, so two
/// distinct hoisted arrays can never share a spelling — uniqueness comes from the counter, not from
/// scanning the scope, which is why the hoisting pass no longer needs to enumerate every scope inhabitant
/// (the fragile step that reopened collisions in earlier revisions). The <c>#</c> delimiter cannot appear
/// in a Qora identifier, so a placeholder can never collide with a user name either. The mangler recovers
/// <c>{base}</c> — the desired human-readable name — and runs its ordinary freshening on it, so the final
/// QASM reads <c>Helper_tbl</c>, not the placeholder; the placeholder lives only between these two passes.
/// </summary>
internal static class HoistName
{
    private const string Marker = "#hoist#";

    /// <summary>A unique placeholder carrying the desired emitted base name. <paramref name="baseName"/>
    /// must not contain <c>#</c> (it is an operation/variable name, which cannot).</summary>
    public static string Make(string baseName, int uid) => Marker + baseName + "#" + uid;

    /// <summary>The desired base name inside a placeholder, or null when <paramref name="name"/> is an
    /// ordinary (user or operation) name.</summary>
    public static string? Base(string name)
    {
        if (!name.StartsWith(Marker, System.StringComparison.Ordinal)) return null;
        var rest = name.Substring(Marker.Length);
        var end = rest.IndexOf('#');
        return end < 0 ? rest : rest.Substring(0, end);
    }
}
