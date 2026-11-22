namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// A dynamic width can still have a statically guaranteed minimum. For example, an operation
/// returning either two or three bits always supplies the first two. Follow by-value returns
/// across calls and use existing storage provenance for stores, calls and block arguments.
/// </summary>
internal sealed class MirArrayLengthFacts(
    MirProgram program, Func<MirCallable, MirStorageProvenanceSnapshot> provenanceOf)
{
    private readonly Dictionary<(MirCallableId, string), int> _returnMinimums = new();
    private readonly HashSet<(MirCallableId, MirStorageId)> _active = new();

    public int Minimum(MirCallable caller, MirValue value, MirStorageProvenance provenance) =>
        Minimum(caller, value, provenance, argumentMinimums: null);

    private int Minimum(
        MirCallable caller, MirValue value, MirStorageProvenance provenance, int[]? argumentMinimums)
    {
        if (value.Type.KnownLength is int known) return known;
        if (!provenance.IsComplete || provenance.PossibleStorages.Count == 0) return 0;
        return provenance.PossibleStorages.Min(id => StorageMinimum(caller, caller.RequireStorage(id), argumentMinimums));
    }

    private int StorageMinimum(MirCallable caller, MirArrayStorage storage, int[]? argumentMinimums)
    {
        if (caller.StorageTypeOf(storage).KnownLength is int known) return known;
        if (caller.StorageKindOf(storage) == MirArrayStorageKind.Parameter)
        {
            var index = caller.StorageParameterIndexOf(storage);
            var formal = (MirClassicalParameter)caller.Parameters[index];
            return Math.Max(formal.MinimumLength, argumentMinimums?[index] ?? 0);
        }

        if (caller.RequireInstruction(caller.StorageAllocationInstructionOf(storage)) is not
            MirQuantumApply { Target: MirUserCallableTarget target } apply ||
            program.FindCallable(target.Callable) is not { } callee)
            return 0;
        // A loop-carried value may depend on this same call site in a prior iteration.
        // Do not claim a bound merely on the strength of that circular dependency.
        var site = (caller.Id, storage.Id);
        if (!_active.Add(site)) return 0;
        // A passthrough operation returns the actual array, not a register shortened to
        // the formal parameter's minimum. Substitute bounds rather than fixed widths.
        var arguments = new int[callee.Parameters.Count];
        for (var index = 0; index < arguments.Length; index++)
        {
            if (callee.Parameters[index] is not MirClassicalParameter { Storage: not null } ||
                index >= apply.Operands.Count || apply.Operands[index] is not MirClassicalCallOperand operand ||
                caller.FindValue(operand.Value) is not { Type.IsArray: true } actual) continue;
            arguments[index] = Minimum(caller, actual, provenanceOf(caller).ProvenanceOf(actual.Id), argumentMinimums);
        }
        var key = (callee.Id, string.Join(',', arguments));
        if (!_returnMinimums.TryGetValue(key, out var minimum))
        {
            var origins = provenanceOf(callee);
            minimum = MirCallable.ReachableReturnValues(callee.EntryBlock, callee.RequireBlock)
                .Select(id => callee.FindValue(id) is { Type.IsArray: true } value
                    ? Minimum(callee, value, origins.ProvenanceOf(id), arguments) : 0)
                .DefaultIfEmpty(0).Min();
            _returnMinimums[key] = minimum;
        }
        _active.Remove(site);
        return minimum;
    }
}
