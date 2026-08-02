using Qora.Compiler;

namespace Qora.LanguageServices;

/// <summary>
/// One compiler snapshot and the optional IDE query index collected while that exact snapshot was built.
/// </summary>
public sealed class LanguageServiceCompilation
{
    internal LanguageServiceCompilation(
        Compilation compilation,
        MirSemanticIndex? mirSemanticIndex)
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        MirSemanticIndex = mirSemanticIndex;

        if (mirSemanticIndex is null)
            return;
        if (compilation.Mir is null
            || !ReferenceEquals(compilation.Mir, mirSemanticIndex.Mir)
            || !ReferenceEquals(
                compilation.Hir.EffectAnalysis,
                mirSemanticIndex.HirArtifact))
        {
            throw new ArgumentException(
                "A language-service index must belong to its exact compiler snapshot.",
                nameof(mirSemanticIndex));
        }
    }

    public Compilation Compilation { get; }

    /// <summary>
    /// The MIR semantic index when this compilation produced MIR; otherwise <see langword="null"/>.
    /// </summary>
    public MirSemanticIndex? MirSemanticIndex { get; }
}

/// <summary>
/// Allocates compiler revisions and collects a fresh, compile-call-scoped semantic index for each one.
/// </summary>
public sealed class LanguageServiceSession
{
    private readonly CompilationSession _compiler = new();

    public CompilationId Id => _compiler.Id;

    public LanguageServiceCompilation Compile(
        string source,
        CompilationOptions? options = null)
    {
        var collector = new MirSemanticIndexCollector();
        var compilation = _compiler.Compile(
            source,
            options,
            new CompilationInstrumentation(collector));
        var semanticIndex = collector.Build(compilation);

        return new LanguageServiceCompilation(compilation, semanticIndex);
    }

    public LanguageServiceCompilation Recompile(
        LanguageServiceCompilation previous,
        string source,
        CompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!ReferenceEquals(previous.Compilation.Session, _compiler))
        {
            throw new ArgumentException(
                "A LanguageServiceSession can only recompile a snapshot it owns.",
                nameof(previous));
        }

        var collector = new MirSemanticIndexCollector();
        var compilation = _compiler.Recompile(
            previous.Compilation,
            source,
            options,
            new CompilationInstrumentation(collector));
        var semanticIndex = collector.Build(compilation);

        return new LanguageServiceCompilation(compilation, semanticIndex);
    }
}
