using System.Collections.Frozen;
using Qora.Ir;

namespace Qora.Compiler;

/// <summary>
/// Common target-artifact identity. Source-stage provenance remains target-specific: a backend may own
/// a HIR source, a MIR source, or another lowered representation without weakening this aggregate.
/// </summary>
public interface ITargetArtifact
{
    TargetBackend Backend { get; }
}

/// <summary>
/// One OpenQASM result and the exact HIR generation that the current HIR-backed target pipeline consumed.
/// The source is explicit so a future MIR-backed backend can change the relationship without pretending the
/// current backend already emits from MIR.
/// </summary>
public sealed class OpenQasmArtifact : ITargetArtifact
{
    internal OpenQasmArtifact(
        QasmBackend.Result backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (!backend.Success || backend.Target is null)
            throw new ArgumentException(
                "An OpenQASM artifact requires one successful backend result.",
                nameof(backend));

        var source = backend.Source;
        var semanticBasis = backend.SemanticBasis;
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(semanticBasis);
        if (source.Id.CompilationId != semanticBasis.SourceId.CompilationId
            || source.Id.CompilationRevision != semanticBasis.SourceId.CompilationRevision)
        {
            throw new ArgumentException(
                "An OpenQASM artifact cannot combine HIR facts from different Compilation snapshots.",
                nameof(semanticBasis));
        }

        SourceSnapshot = source;
        SemanticArtifact = semanticBasis;
        Program = backend.Target;
        Text = QasmEmitter.Emit(Program);

        VerifyTargetOrigins(source);
    }

    internal HirSnapshot SourceSnapshot { get; }
    internal HirSemanticArtifact SemanticArtifact { get; }
    public HirSnapshotId Source => SourceSnapshot.Id;
    public HirSemanticArtifactId SemanticBasis => SemanticArtifact.Id;
    public OpenQasmTargetProgram Program { get; }
    public string Text { get; }
    public TargetBackend Backend => TargetBackend.OpenQasm;

    private void VerifyTargetOrigins(HirSnapshot source)
    {
        var target = new HirStructuralIndex(Program.Program);
        foreach (var nodeId in target.NodeIds)
        {
            var isSourceNode = source.Structure.Contains(nodeId);
            var isSynthesized =
                Program.Facts.TryGetSynthesizedNode(nodeId, out _);

            if (isSourceNode == isSynthesized)
            {
                throw new ArgumentException(
                    isSourceNode
                        ? $"OpenQASM node {nodeId} is already present in source HIR but is also marked synthesized."
                        : $"OpenQASM node {nodeId} is absent from source HIR and has no target synthesis fact.",
                    nameof(Program));
            }
        }

        foreach (var fact in Program.Facts.SynthesizedNodes.Values)
        {
            if (!source.Structure.Contains(fact.SourceHirNodeId))
            {
                throw new ArgumentException(
                    $"OpenQASM synthesized node {fact.NodeId} names missing source HIR node " +
                    $"{fact.SourceHirNodeId}.",
                    nameof(Program));
            }
        }
    }
}

/// <summary>Target artifacts produced for one immutable Compilation snapshot.</summary>
public sealed class TargetArtifactSet
{
    private readonly FrozenDictionary<TargetBackend, ITargetArtifact> _artifacts;

    internal TargetArtifactSet(IEnumerable<ITargetArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var byBackend = new Dictionary<TargetBackend, ITargetArtifact>();
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!Enum.IsDefined(artifact.Backend))
                throw new ArgumentException(
                    $"Target artifact declares unknown backend {artifact.Backend}.",
                    nameof(artifacts));
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
