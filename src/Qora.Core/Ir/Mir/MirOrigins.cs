using System.Text;
using Qora.Compiler;

namespace Qora.Ir.Mir;

/// <summary>
/// Immutable provenance attached directly to one or more MIR entities. An origin either names the
/// exact HIR node which produced the MIR or records one compiler-generated step above another origin.
/// </summary>
public abstract record MirOrigin
{
    internal MirOrigin()
    {
    }

    internal static MirHirOrigin FromHirNode(
        HirSemanticArtifact hirArtifact,
        HirNode sourceHirNode) =>
        new(hirArtifact, sourceHirNode);

    internal static MirGeneratedOrigin GeneratedFrom(
        MirOrigin parentOrigin,
        string reason) =>
        new(parentOrigin, reason);

    /// <summary>The authoritative HIR source at the root of this generation history.</summary>
    public MirHirOrigin SourceHirOrigin
    {
        get
        {
            MirOrigin current = this;
            while (current is MirGeneratedOrigin generated)
                current = generated.Parent;

            return current as MirHirOrigin
                ?? throw new InvalidOperationException(
                    "a MIR origin must terminate at an exact HIR origin");
        }
    }

    protected virtual bool PrintMembers(StringBuilder builder) => false;
}

/// <summary>The exact HIR node and source location from which MIR lowering started.</summary>
public sealed record MirHirOrigin : MirOrigin
{
    internal MirHirOrigin(
        HirSemanticArtifact hirArtifact,
        HirNode sourceHirNode)
    {
        ArgumentNullException.ThrowIfNull(hirArtifact);
        ArgumentNullException.ThrowIfNull(sourceHirNode);

        var hirSnapshot = hirArtifact.Source;
        var ownedHirNode = hirSnapshot.Structure.FindNode(sourceHirNode.Id);
        if (!ReferenceEquals(ownedHirNode, sourceHirNode))
        {
            throw new ArgumentException(
                "The source HIR node does not belong to this semantic artifact.",
                nameof(sourceHirNode));
        }

        HirArtifact = hirArtifact;
        HirNodeId = sourceHirNode.Id;
    }

    internal HirSemanticArtifact HirArtifact { get; }
    public HirNodeId HirNodeId { get; }
    public SourceSpan? Span => HirArtifact.Source.SourceMap.Find(HirNodeId);
}

/// <summary>One compiler-generated step whose immutable parent retains the complete earlier history.</summary>
public sealed record MirGeneratedOrigin : MirOrigin
{
    internal MirGeneratedOrigin(
        MirOrigin parentOrigin,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(parentOrigin);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "a generated MIR origin requires a reason",
                nameof(reason));
        }

        Parent = parentOrigin;
        Reason = reason;
    }

    public MirOrigin Parent { get; }
    public string Reason { get; }
}
