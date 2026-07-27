using System.Collections.Frozen;

namespace Qora.Compiler;

/// <summary>The canonical front-end terminal required from a successful Compilation.</summary>
public enum HirCompilationGoal
{
    EffectAnalyzedCanonical,
}

/// <summary>
/// The exact set of downstream artifacts requested for one Compilation revision.
/// HIR is always built as far as diagnostics permit. MIR is an explicit output or an owned prerequisite
/// of any requested target; a backend never lowers from HIR through a private side branch.
/// </summary>
public sealed class CompilationOutputPlan
{
    private readonly FrozenSet<TargetBackend> _targets;

    public CompilationOutputPlan(
        bool produceMir,
        IEnumerable<TargetBackend> targets,
        HirCompilationGoal hirGoal = HirCompilationGoal.EffectAnalyzedCanonical)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!Enum.IsDefined(hirGoal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(hirGoal),
                hirGoal,
                "unknown HIR compilation goal");
        }

        var requestedTargets = new HashSet<TargetBackend>();
        foreach (var backend in targets)
        {
            if (!Enum.IsDefined(backend))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targets),
                    backend,
                    "unknown target backend");
            }

            requestedTargets.Add(backend);
        }

        ProduceMir = produceMir;
        HirGoal = hirGoal;
        _targets = requestedTargets.ToFrozenSet();
    }

    /// <summary>The existing full compiler pipeline: MIR analysis plus OpenQASM emission.</summary>
    public static CompilationOutputPlan Default { get; } =
        new(
            produceMir: true,
            new[] { TargetBackend.OpenQasm });

    /// <summary>Stop after HIR and its semantic artifacts.</summary>
    public static CompilationOutputPlan HirOnly { get; } =
        new(
            produceMir: false,
            Array.Empty<TargetBackend>());

    public bool ProduceMir { get; }
    public bool RequiresMir => ProduceMir || _targets.Count > 0;
    public HirCompilationGoal HirGoal { get; }

    /// <summary>
    /// An immutable backend set. The constructor copies its input, so later caller mutations cannot
    /// change the contract of an existing Compilation revision.
    /// </summary>
    public IReadOnlySet<TargetBackend> Targets => _targets;

    public bool Requests(TargetBackend backend)
    {
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "unknown target backend");
        }

        return _targets.Contains(backend);
    }
}
