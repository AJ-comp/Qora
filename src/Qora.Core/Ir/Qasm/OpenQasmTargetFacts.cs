using System.Collections.Frozen;

namespace Qora.Ir;

/// <summary>The OpenQASM target rewrite which introduced an identity-bearing node.</summary>
public enum OpenQasmSynthesisKind
{
    ReturnResultStorage,
    ReturnDoneStorage,
    ReturnValueAssignment,
    ReturnDoneAssignment,
    ReturnBreak,
    ReturnGuard,
    FinalReturn,
    HoistedArrayStorage,
    HoistedArrayParameter,
    ArrayReinitialization,
}

/// <summary>
/// Provenance and declaration facts for one node introduced after the HIR-to-OpenQASM boundary.
/// <see cref="SourceHirNodeId"/> is always expressed in the exact materialized HIR snapshot recorded by
/// <see cref="Qora.Compiler.OpenQasmArtifact.Source"/>. <see cref="DeclaredType"/> is present only when the
/// synthesized node declares a classical value.
/// </summary>
public sealed record OpenQasmSynthesizedNodeFact(
    int NodeId,
    int SourceHirNodeId,
    OpenQasmSynthesisKind Kind,
    OpenQasmClassicalType? DeclaredType);

/// <summary>
/// Immutable target-owned facts for every identity-bearing node synthesized after validated HIR.
/// Original HIR nodes are deliberately absent: their types come only from the exact semantic artifact.
/// This separation prevents an explicit field copied into a target-shaped node from silently overriding
/// source semantics, while also preventing a synthesized target node from being queried as if it belonged
/// to the HIR snapshot.
/// </summary>
public sealed class OpenQasmTargetFacts
{
    private readonly FrozenDictionary<int, OpenQasmSynthesizedNodeFact> _synthesizedNodes;

    internal OpenQasmTargetFacts(
        IEnumerable<OpenQasmSynthesizedNodeFact> synthesizedNodes)
    {
        ArgumentNullException.ThrowIfNull(synthesizedNodes);
        var facts = new Dictionary<int, OpenQasmSynthesizedNodeFact>();
        foreach (var fact in synthesizedNodes)
        {
            if (fact.NodeId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(synthesizedNodes),
                    fact.NodeId,
                    "a synthesized target node needs a positive identity");
            if (fact.SourceHirNodeId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(synthesizedNodes),
                    fact.SourceHirNodeId,
                    "a synthesized target node needs a positive HIR origin identity");
            if (!facts.TryAdd(fact.NodeId, fact))
                throw new ArgumentException(
                    $"OpenQASM synthesized node {fact.NodeId} was recorded more than once.",
                    nameof(synthesizedNodes));
        }

        _synthesizedNodes = facts.ToFrozenDictionary();
    }

    internal static OpenQasmTargetFacts Empty { get; } =
        new(Array.Empty<OpenQasmSynthesizedNodeFact>());

    /// <summary>Synthesized target-node Id → its exact type/provenance fact.</summary>
    public IReadOnlyDictionary<int, OpenQasmSynthesizedNodeFact> SynthesizedNodes =>
        _synthesizedNodes;

    public bool TryGetSynthesizedNode(
        int nodeId,
        out OpenQasmSynthesizedNodeFact fact) =>
        _synthesizedNodes.TryGetValue(nodeId, out fact!);

    internal static OpenQasmTargetFacts Merge(
        params OpenQasmTargetFacts[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new OpenQasmTargetFacts(
            sources.SelectMany(source =>
                (source
                 ?? throw new ArgumentException(
                     "an OpenQASM target-fact source cannot be null",
                     nameof(sources)))
                .SynthesizedNodes.Values));
    }
}

/// <summary>Pass-local mutable collector; only its immutable result crosses a target-pass boundary.</summary>
internal sealed class OpenQasmTargetFactBuilder
{
    private readonly List<OpenQasmSynthesizedNodeFact> _facts = new();

    public void Record(
        int nodeId,
        int sourceHirNodeId,
        OpenQasmSynthesisKind kind,
        OpenQasmClassicalType? declaredType = null) =>
        _facts.Add(
            new OpenQasmSynthesizedNodeFact(
                nodeId,
                sourceHirNodeId,
                kind,
                declaredType));

    public OpenQasmTargetFacts Build() =>
        _facts.Count == 0
            ? OpenQasmTargetFacts.Empty
            : new OpenQasmTargetFacts(_facts);
}
