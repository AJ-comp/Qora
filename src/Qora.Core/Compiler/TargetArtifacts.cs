using System.Collections.Frozen;
using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Compiler;

/// <summary>
/// Common target-artifact identity. Every backend starts from the Compilation's one canonical MIR
/// snapshot; target-specific models may add their own IDs but cannot silently choose a parallel HIR input.
/// </summary>
public interface ITargetArtifact
{
    TargetBackend Backend { get; }
    MirSnapshotId Source { get; }
}

/// <summary>
/// One OpenQASM result and the exact immutable MIR snapshot consumed by its lowering. The target model
/// contains only target identities and final serialization data; source-stage ownership remains here.
/// </summary>
public sealed class OpenQasmArtifact : ITargetArtifact
{
    internal OpenQasmArtifact(QasmBackend.Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
            throw new ArgumentException(
                "An OpenQASM artifact requires one successful backend result.",
                nameof(result));
        SourceSnapshot = result.Source;
        Program = result.Target
            ?? throw new ArgumentException(
                "A successful OpenQASM backend result has no target program.",
                nameof(result));
        Text = MirQasmEmitter.Emit(Program);
    }

    internal MirSnapshot SourceSnapshot { get; }
    public MirSnapshotId Source => SourceSnapshot.Id;
    public MirOpenQasmTargetProgram Program { get; }
    public string Text { get; }
    public TargetBackend Backend => TargetBackend.OpenQasm;
}

/// <summary>Target artifacts produced for one immutable Compilation snapshot.</summary>
public sealed class TargetArtifactSet
{
    private readonly FrozenDictionary<TargetBackend, ITargetArtifact> _artifacts;

    internal TargetArtifactSet(IEnumerable<ITargetArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var byBackend = new Dictionary<TargetBackend, ITargetArtifact>();
        MirSnapshotId? sharedSource = null;
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!Enum.IsDefined(artifact.Backend))
                throw new ArgumentException(
                    $"Target artifact declares unknown backend {artifact.Backend}.",
                    nameof(artifacts));
            if (sharedSource is { } expected && artifact.Source != expected)
            {
                throw new ArgumentException(
                    $"Target artifacts cannot mix MIR sources {expected} and {artifact.Source}.",
                    nameof(artifacts));
            }
            sharedSource ??= artifact.Source;
            if (artifact.Backend == TargetBackend.OpenQasm
                && artifact is not OpenQasmArtifact)
            {
                throw new ArgumentException(
                    "The OpenQasm backend key requires an OpenQasmArtifact.",
                    nameof(artifacts));
            }
            if (!byBackend.TryAdd(artifact.Backend, artifact))
                throw new ArgumentException(
                    $"Target backend {artifact.Backend} produced more than one artifact.",
                    nameof(artifacts));
        }

        _artifacts = byBackend.ToFrozenDictionary();
    }

    public IReadOnlyDictionary<TargetBackend, ITargetArtifact> Artifacts =>
        _artifacts;

    public ITargetArtifact? Find(TargetBackend backend)
    {
        if (!Enum.IsDefined(backend))
            throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "unknown target backend");
        return _artifacts.GetValueOrDefault(backend);
    }

    public OpenQasmArtifact? OpenQasm =>
        Find(TargetBackend.OpenQasm) as OpenQasmArtifact;
}
