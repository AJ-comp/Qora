namespace Qora.Compiler;

/// <summary>
/// The revision-allocation authority for one logical compilation. Immutable <see cref="Compilation"/>
/// snapshots never derive a new revision number from a possibly stale parent; every branch asks this shared
/// session for a unique monotonic revision.
/// </summary>
public sealed class CompilationSession
{
    private long _lastRevision = -1;

    public CompilationSession()
        : this(CompilationId.New())
    {
    }

    internal CompilationSession(CompilationId id)
    {
        if (id.Value == Guid.Empty)
            throw new ArgumentException(
                "A CompilationSession requires a non-empty identity.",
                nameof(id));
        Id = id;
    }

    public CompilationId Id { get; }

    /// <summary>Compile an independent root snapshot owned by this session.</summary>
    public Compilation Compile(
        string source,
        CompilationOptions? options = null,
        CompilationInstrumentation? instrumentation = null) =>
        CompileCore(
            source,
            options ?? new CompilationOptions(),
            instrumentation,
            parent: null);

    /// <summary>
    /// Compile a child of an existing snapshot. Branching from the same parent is valid; every child still
    /// receives a distinct revision because this session, rather than the parent snapshot, issues IDs.
    /// </summary>
    public Compilation Recompile(
        Compilation previous,
        string source,
        CompilationOptions? options = null,
        CompilationInstrumentation? instrumentation = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!ReferenceEquals(previous.Session, this))
        {
            throw new ArgumentException(
                "A CompilationSession can only recompile a snapshot it owns.",
                nameof(previous));
        }

        return CompileCore(
            source,
            options ?? previous.Options,
            instrumentation,
            previous.Revision);
    }

    private Compilation CompileCore(
        string source,
        CompilationOptions options,
        CompilationInstrumentation? instrumentation,
        CompilationRevision? parent)
    {
        var revisionValue = Interlocked.Increment(ref _lastRevision);
        if (revisionValue > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The logical compilation exhausted its revision identity space.");
        }

        return QoraCompiler.CompileSnapshot(
            source,
            options,
            instrumentation,
            this,
            new CompilationRevision((int)revisionValue),
            parent);
    }
}
