using System.Collections.Frozen;

namespace Qora.Ir.Mir.Analysis;

/// <summary>The MIR instruction kind which creates one user-callable edge.</summary>
public enum MirCallKind
{
    PureCall,
    QuantumApply,
}

/// <summary>
/// One policy-free user-callable edge. The owning <see cref="MirCallGraph"/> supplies the exact snapshot;
/// block and instruction identities are local to <see cref="Caller"/>.
/// </summary>
public readonly record struct MirCallSite(
    MirInstructionSite Instruction,
    MirBlockId Block,
    MirCallableId Callee,
    MirCallKind Kind)
{
    public MirCallableId Caller => Instruction.Callable;
}

/// <summary>
/// Immutable direct-call facts for one exact MIR snapshot. This graph records only structural edges; it
/// does not decide reachability, recursion policy, inlining, inversion, or scheduling.
/// </summary>
public sealed class MirCallGraph
{
    private readonly MirProgram _program;
    private readonly FrozenDictionary<MirCallableId, IReadOnlyList<MirCallSite>> _callsFrom;
    private readonly FrozenDictionary<MirCallableId, IReadOnlyList<MirCallSite>> _callsTo;

    internal MirCallGraph(
        MirProgram program,
        IEnumerable<MirCallSite> calls)
    {
        _program = program ?? throw new ArgumentNullException(nameof(program));
        ArgumentNullException.ThrowIfNull(calls);

        Calls = MirCollections.Freeze(calls);
        _callsFrom = GroupBy(Calls, call => call.Caller);
        _callsTo = GroupBy(Calls, call => call.Callee);
    }

    public MirSnapshotId SnapshotId => _program.SnapshotId;
    public IReadOnlyList<MirCallSite> Calls { get; }

    internal void EnsureFor(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!ReferenceEquals(_program, program))
        {
            throw new InvalidOperationException(
                $"MIR call graph belongs to {SnapshotId}; rebuild it for {program.SnapshotId}");
        }
    }

    public IReadOnlyList<MirCallSite> CallsFrom(MirCallableId caller)
    {
        _program.RequireCallable(caller);
        return _callsFrom.GetValueOrDefault(caller)
            ?? Array.Empty<MirCallSite>();
    }

    public IReadOnlyList<MirCallSite> CallsFrom(MirCallable caller) =>
        CallsFrom(_program.RequireCallable(caller).Id);

    public IReadOnlyList<MirCallSite> CallsTo(MirCallableId callee)
    {
        _program.RequireCallable(callee);
        return _callsTo.GetValueOrDefault(callee)
            ?? Array.Empty<MirCallSite>();
    }

    public IReadOnlyList<MirCallSite> CallsTo(MirCallable callee) =>
        CallsTo(_program.RequireCallable(callee).Id);

    public IReadOnlyList<MirCallableId> CalleesOf(
        MirCallableId caller,
        MirCallKind? kind = null) =>
        MirCollections.Freeze(
            CallsFrom(caller)
                .Where(call => kind is null || call.Kind == kind)
                .Select(call => call.Callee)
                .Distinct()
                .OrderBy(callee => callee.Value));

    public IReadOnlyList<MirCallableId> CalleesOf(
        MirCallable caller,
        MirCallKind? kind = null) =>
        CalleesOf(_program.RequireCallable(caller).Id, kind);

    private static FrozenDictionary<MirCallableId, IReadOnlyList<MirCallSite>> GroupBy(
        IEnumerable<MirCallSite> calls,
        Func<MirCallSite, MirCallableId> key) =>
        calls
            .GroupBy(key)
            .ToFrozenDictionary(
                group => group.Key,
                group => MirCollections.Freeze(group));
}

internal static class MirCallGraphAnalysis
{
    public static MirCallGraph AnalyzeVerified(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var calls = new List<MirCallSite>();

        foreach (var caller in program.Callables)
        {
            foreach (var block in caller.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    var edge = instruction switch
                    {
                        MirPureCall
                        {
                            Target: MirUserCallableTarget target,
                        } pure =>
                            (target.Callable, MirCallKind.PureCall, pure.Id),
                        MirQuantumApply
                        {
                            Target: MirUserCallableTarget target,
                        } quantum =>
                            (target.Callable, MirCallKind.QuantumApply, quantum.Id),
                        _ => ((MirCallableId Callable, MirCallKind Kind, MirInstructionId Instruction)?)null,
                    };
                    if (edge is not { } resolved)
                        continue;

                    program.RequireCallable(resolved.Callable);
                    calls.Add(
                        new MirCallSite(
                            new MirInstructionSite(caller.Id, resolved.Instruction),
                            block.Id,
                            resolved.Callable,
                            resolved.Kind));
                }
            }
        }

        return new MirCallGraph(program, calls);
    }
}
