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
        HirNodeId hirNodeId,
        SourceSpan? span)
    {
        HirNodeId = hirNodeId;
        Span = span;
    }

    public HirNodeId HirNodeId { get; }
    public SourceSpan? Span { get; }
}

/// <summary>One compiler-generated step whose immutable parent retains the complete earlier history.</summary>
public sealed record MirGeneratedOrigin : MirOrigin
{
    internal MirGeneratedOrigin(
        MirOrigin parent,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "a generated MIR origin requires a reason",
                nameof(reason));
        }

        Parent = parent;
        Reason = reason;
    }

    public MirOrigin Parent { get; }
    public string Reason { get; }
}
