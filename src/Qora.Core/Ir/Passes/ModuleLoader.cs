using Qora.Compiler;

namespace Qora.Ir.Passes;

/// <summary>
/// Pure import-merge step. File reading and parsing are owned by the source graph loader; this pass
/// consumes only immutable document programs and graph edges and produces one merged HIR program.
/// Cycles and diamonds are handled by document identity, never by names or repeated file I/O.
/// </summary>
internal static class ModuleLoader
{
    public sealed record Result(
        QProgram Program,
        IReadOnlyList<HirNodeIntroduction> Introductions);

    public static Result Expand(LoadedSourceGraph loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var program = loaded.EntryProgram
            ?? throw new ArgumentException(
                "Module expansion requires a successfully lowered entry document.",
                nameof(loaded));
        var operations = new List<QOperation>(program.Operations);
        var introductions = new List<HirNodeIntroduction>();
        var opens = new Dictionary<string, List<QOpen>>();
        MergeOpens(opens, program.Opens);
        var visited = new HashSet<SourceDocumentRef>
        {
            loaded.Sources.Entry,
        };

        MergeImports(loaded.Sources.Entry);

        return new Result(
            program with
            {
                Operations = operations,
                Imports = null,
                Opens = opens.Count > 0
                    ? opens.ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<QOpen>)pair.Value
                            .DistinctBy(open => open.Target)
                            .ToList())
                    : null,
            },
            introductions.AsReadOnly());

        void MergeImports(SourceDocumentRef importer)
        {
            foreach (var edge in loaded.Sources.Imports.Outgoing(importer))
            {
                if (edge.Imported is not { } imported
                    || !visited.Add(imported))
                {
                    continue;
                }

                // Preserve the established deterministic order: entry declarations first, followed by
                // imported subtrees in depth-first post-order.
                MergeImports(imported);
                if (!loaded.LoweredDocuments.TryGetValue(imported, out var importedProgram))
                    continue;

                operations.AddRange(importedProgram.Operations);
                var importedStructure = new HirStructuralIndex(importedProgram);
                introductions.AddRange(
                    importedStructure.NodeIds
                        .Where(nodeId => nodeId != importedProgram.Id)
                        .Select(nodeId => new HirNodeIntroduction(
                            nodeId,
                            imported,
                            HirNodeIntroductionKind.ImportedDocument)));
                MergeOpens(opens, importedProgram.Opens);
            }
        }
    }

    private static void MergeOpens(
        Dictionary<string, List<QOpen>> into,
        IReadOnlyDictionary<string, IReadOnlyList<QOpen>>? opens)
    {
        if (opens is null)
            return;

        foreach (var (namespaceName, directives) in opens)
        {
            if (!into.TryGetValue(namespaceName, out var existing))
                into.Add(namespaceName, existing = new List<QOpen>());
            existing.AddRange(directives);
        }
    }
}
