namespace Qora.Ir.Passes;

/// <summary>
/// The import-expansion step: turns a single-file <see cref="QProgram"/> plus its <c>import</c>
/// declarations into ONE merged program covering the whole import graph, before resolution runs —
/// so the resolver/validator/emitter never know files existed.
///
/// Semantics (docs/namespaces-design.md, increment 3):
/// <list type="bullet">
///   <item><c>import "lib/gates.qor";</c> and <c>import "a b.qor";</c> use the quoted relative path
///         exactly as written, including the extension. Paths resolve against the IMPORTING file's
///         directory (the entry file uses <c>baseDir</c>), so a library's own imports keep working
///         wherever it is imported from.</item>
///   <item>Loading is transitive with diamond-sharing: each file is attempted once no matter how many
///         import paths reach it (paths are canonicalized; case-insensitive, matching Windows).
///         A cyclic back-edge is therefore harmless: its path is already registered and the import is skipped.</item>
///   <item>A missing/unreadable file is <b>QSEM020</b>. A parse error inside an imported file is reported
///         with the file name prefixed and no span (spans are offsets into the ENTRY document only).</item>
///   <item>Merged order = entry file's operations first, then imports depth-first. The entry-op rule
///         ("global <c>Main</c>, else the first operation") therefore keeps pointing at the entry
///         file. Namespaces merge across files (same name = same namespace, C#-style), opens union
///         per namespace, and duplicate names anywhere collapse into QSEM008/QSEM022 downstream.</item>
///   <item>No <c>baseDir</c> (playground, bare-stdin CLI) ⇒ every import is a clear QSEM020: imports
///         need a place to resolve from.</item>
/// </list>
/// </summary>
public static class ModuleLoader
{
    public static (QProgram Program, List<QoraError> Errors) Expand(QProgram program, string? baseDir, string? sourcePath)
    {
        var errors = new List<QoraError>();
        if (program.Imports is not { Count: > 0 }) return (program, errors);

        if (baseDir is null)
        {
            foreach (var imp in program.Imports)
                Add(errors, "QSEM020",
                    $"`import {imp.Display};` cannot be resolved here: imports load files relative to the importing file, and this input has no file context (CLI: pass a file path or --base-dir; the playground is single-file)",
                    imp.Span);
            return (program, errors);
        }

        var operations = new List<QOperation>(program.Operations);
        var opens = new Dictionary<string, List<QOpen>>();
        MergeOpens(opens, program.Opens);

        // Register every canonical path before I/O. Both a recursive back-edge and a diamond therefore
        // become the same harmless case: loaded.Add returns false and that import edge is skipped.
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryName = sourcePath is null ? "<entry>" : Path.GetFileName(sourcePath);
        if (sourcePath is not null)
        {
            var entryFull = Path.GetFullPath(sourcePath);
            loaded.Add(entryFull);
        }

        LoadAll(program.Imports, baseDir, entryName);

        return (program with
        {
            Operations = operations,
            Imports = null,
            Opens = opens.Count > 0
                ? opens.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<QOpen>)kv.Value.DistinctBy(o => o.Target).ToList())
                : null,
        }, errors);

        void LoadAll(IReadOnlyList<QImport> imports, string dir, string importer)
        {
            foreach (var imp in imports)
            {
                var rel = imp.Target; // the literal relative path the user quoted (incl. extension)
                string full;
                try { full = Path.GetFullPath(Path.Combine(dir, rel)); }
                catch (Exception)
                {
                    Add(errors, "QSEM020", $"in {importer}: `import {imp.Display};` is not a usable path (`{rel}`)", imp.Span);
                    continue;
                }
                var name = Path.GetFileName(full);
                if (!loaded.Add(full)) continue; // cycle or diamond: this canonical path was already attempted

                if (!File.Exists(full))
                {
                    Add(errors, "QSEM020", $"in {importer}: `import {imp.Display};` — file not found: {full}", imp.Span);
                    continue;
                }
                string src;
                try { src = File.ReadAllText(full); }
                catch (Exception ex)
                {
                    Add(errors, "QSEM020", $"in {importer}: `import {imp.Display};` — cannot read {full}: {ex.Message}", imp.Span);
                    continue;
                }

                var (sub, parseErrors) = QoraParser.ParseToIr(src);
                if (parseErrors.Count > 0)
                {
                    foreach (var e in parseErrors)
                        errors.Add(new QoraError($"in imported file '{name}': {e.Message}", e.Code, -1, -1));
                    continue;
                }
                if (sub is null) continue; // empty file: nothing to merge

                if (sub.Imports is { Count: > 0 })
                    LoadAll(sub.Imports, Path.GetDirectoryName(full)!, name);

                operations.AddRange(sub.Operations);
                MergeOpens(opens, sub.Opens);
            }
        }
    }

    private static void MergeOpens(Dictionary<string, List<QOpen>> into, IReadOnlyDictionary<string, IReadOnlyList<QOpen>>? opens)
    {
        if (opens is null) return;
        foreach (var (ns, list) in opens)
        {
            if (!into.TryGetValue(ns, out var existing)) into[ns] = existing = new List<QOpen>();
            existing.AddRange(list);
        }
    }

    private static void Add(List<QoraError> errors, string code, string message, QSpan? span = null) =>
        errors.Add(new QoraError(message, code, span?.Start ?? -1, span?.End ?? -1));
}
