namespace Qora.Ir.Mir.Analysis;

/// <summary>
/// Interprets array-state provenance together with the callable's storage contracts.
///
/// Storage identities name MIR regions; they are not all physical allocation identities. In
/// particular, separate read-only formal parameters may receive the same caller array. This analysis
/// is the single place where consumers turn provenance into a disjointness decision, so they cannot
/// accidentally treat different formal <see cref="MirStorageId"/> values as proof of non-aliasing.
/// </summary>
internal static class MirStorageAliasAnalysis
{
    /// <summary>
    /// Returns whether the two array states can refer to any common physical storage.
    /// Incomplete, empty, missing, or malformed provenance remains conservatively aliasing.
    /// </summary>
    public static bool MayAlias(
        MirCallable callable,
        MirStorageProvenance left,
        MirStorageProvenance right)
    {
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!left.IsComplete
            || !right.IsComplete
            || left.PossibleStorages.Count == 0
            || right.PossibleStorages.Count == 0)
            return true;

        foreach (var leftId in left.PossibleStorages)
        {
            foreach (var rightId in right.PossibleStorages)
            {
                if (leftId == rightId) return true;

                var leftStorage = callable.FindStorage(leftId);
                var rightStorage = callable.FindStorage(rightId);
                if (leftStorage is null || rightStorage is null)
                    return true;

                var leftType = callable.StorageTypeOf(leftStorage);
                var rightType = callable.StorageTypeOf(rightStorage);

                if (leftType.ElementType != rightType.ElementType
                    || leftType.KnownLength is int leftLength
                    && rightType.KnownLength is int rightLength
                    && leftLength != rightLength)
                    continue;

                var leftAliasMode =
                    callable.StorageAliasModeOf(leftStorage);
                var rightAliasMode =
                    callable.StorageAliasModeOf(rightStorage);

                // Distinct read-only formal regions may still be two views of one caller allocation.
                if (leftAliasMode == MirStorageAliasMode.SharedParameter
                    && rightAliasMode == MirStorageAliasMode.SharedParameter)
                    return true;

                // A local allocation is unique. An exclusive parameter is disjoint from every other
                // formal region because source validation rejects overlapping mutable/moved call slots.
            }
        }

        return false;
    }
}
