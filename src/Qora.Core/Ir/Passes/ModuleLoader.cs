using Qora.Compiler;

namespace Qora.Ir.Passes;

/// <summary>
/// Pure import-merge step. File reading and parsing are owned by the source graph loader; this pass
/// consumes only immutable document programs and graph edges and produces one merged HIR program.
/// Cycles and diamonds are handled by document identity, never by names or repeated file I/O.
/// </summary>
internal static class ModuleLoader
{
    public sealed record Result(HirRewriteResult Rewrite)
    {
        public HirProgram Program => Rewrite.Root;
    }

    public static Result Expand(
        LoadedSourceGraph loaded,
        HirRewriteSession rewrite)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(rewrite);
        var program = loaded.EntryProgram
            ?? throw new ArgumentException(
                "Module expansion requires a successfully lowered entry document.",
                nameof(loaded));
        if (!ReferenceEquals(rewrite.Source.Program, program))
            throw new ArgumentException(
                "Module expansion must rewrite the exact lowered entry snapshot.",
                nameof(rewrite));
        var declarations =
            new List<HirDeclaration>(program.Declarations);
        var visited = new HashSet<SourceDocumentRef>
        {
            loaded.Sources.Entry,
        };

        MergeImports(loaded.Sources.Entry);

        var expanded = rewrite.RewriteProgram(
            program,
            declarations,
            Array.Empty<HirImportDirective>());
        return new Result(
            rewrite.Publish(expanded));

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

                foreach (var declaration in importedProgram.Declarations)
                {
                    rewrite.AdoptImportedSubtree(
                        declaration,
                        imported);
                    declarations.Add(declaration);
                }
            }
        }
    }
}
