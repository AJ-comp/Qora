using Qora.Compiler;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

/// <summary>
/// The pass milestone owned by one immutable MIR snapshot. HIR lowering creates the root snapshot;
/// each MIR transformation advances to the next supported stage and retains its exact source.
/// </summary>
public enum MirStage
{
    Lowered,
    InverseRequestsInjected,
    AdjointsMaterialized,
}

/// <summary>
/// One immutable MIR artifact tied to the exact final HIR semantic artifact which produced it.
/// Callable-local structural indexes and analyses are owned by this snapshot rather than by a
/// mutable global model.
/// </summary>
public sealed class MirSnapshot
{
    internal static MirSnapshot CreateLowered(MirProgram program) => new(program);

    internal static MirSnapshot CreateTransformed(
        MirProgram program,
        MirSnapshot previousSnapshot) =>
        new(program, previousSnapshot);

    private MirSnapshot(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var hirArtifact = program.HirArtifact;

        if (!hirArtifact.IsReadyForMirLowering)
        {
            throw new ArgumentException(
                "MIR lowering requires a HIR artifact ready for MIR lowering.",
                nameof(program));
        }
        QoraMirVerifier.VerifyOrThrow(program);

        Stage = MirStage.Lowered;
        Program = program;
        HirArtifact = hirArtifact;
        PreviousSnapshot = null;
        Analyses = new MirAnalysisStore(program);
    }

    private MirSnapshot(MirProgram program, MirSnapshot previousSnapshot)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(previousSnapshot);
        if (ReferenceEquals(program, previousSnapshot.Program))
        {
            throw new ArgumentException(
                "A transformed MIR snapshot requires a newly constructed MIR program.",
                nameof(program));
        }

        if (!ReferenceEquals(program.HirArtifact, previousSnapshot.HirArtifact))
        {
            throw new ArgumentException(
                "A transformed MIR program must retain its source HIR artifact.",
                nameof(program));
        }

        Stage = previousSnapshot.Stage switch
        {
            MirStage.Lowered => MirStage.InverseRequestsInjected,
            MirStage.InverseRequestsInjected => MirStage.AdjointsMaterialized,
            _ => throw new InvalidOperationException(
                $"MIR stage {previousSnapshot.Stage} has no supported successor"),
        };
        VerifyIdentityContinuity(program, previousSnapshot);
        QoraMirVerifier.VerifyOrThrow(program);

        Program = program;
        HirArtifact = previousSnapshot.HirArtifact;
        PreviousSnapshot = previousSnapshot;
        Analyses = new MirAnalysisStore(program);
    }

    private static void VerifyIdentityContinuity(
        MirProgram transformedProgram,
        MirSnapshot previousSnapshot)
    {
        var sourceCallables = previousSnapshot.Program.Callables.ToDictionary(
            callable => callable.Id);
        var historicalCallableIds = HistoricalSnapshots(previousSnapshot)
            .SelectMany(snapshot => snapshot.Program.Callables)
            .Select(callable => callable.Id)
            .ToHashSet();

        foreach (var transformedCallable in transformedProgram.Callables)
        {
            if (!sourceCallables.TryGetValue(
                    transformedCallable.Id,
                    out var sourceCallable))
            {
                RequireFreshIdentity(
                    "callable",
                    transformedCallable.Id,
                    transformedCallable.Origin,
                    historicalCallableIds.Contains(transformedCallable.Id));
                continue;
            }

            RequirePreservedIdentity(
                "callable",
                transformedCallable.Id,
                sourceCallable.Origin,
                transformedCallable.Origin);
            VerifyCallableIdentityContinuity(
                transformedCallable,
                sourceCallable,
                previousSnapshot);
        }
    }

    private static void VerifyCallableIdentityContinuity(
        MirCallable transformedCallable,
        MirCallable sourceCallable,
        MirSnapshot previousSnapshot)
    {
        var historicalCallables = HistoricalSnapshots(previousSnapshot)
            .Select(snapshot => snapshot.Program.FindCallable(transformedCallable.Id))
            .Where(callable => callable is not null)
            .Cast<MirCallable>()
            .ToArray();

        VerifyCallableLocalIdentities(
            "block",
            transformedCallable,
            sourceCallable,
            historicalCallables,
            callable => callable.Blocks,
            block => block.Id,
            block => block.Origin);
        VerifyCallableLocalIdentities(
            "instruction",
            transformedCallable,
            sourceCallable,
            historicalCallables,
            callable => callable.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Id,
            instruction => instruction.Origin);
        VerifyCallableLocalIdentities(
            "SSA value",
            transformedCallable,
            sourceCallable,
            historicalCallables,
            callable => callable.Values,
            value => value.Id,
            value => value.Origin);
        VerifyCallableLocalIdentities(
            "storage",
            transformedCallable,
            sourceCallable,
            historicalCallables,
            callable => callable.Storages,
            storage => storage.Id,
            storage => storage.Origin);
        VerifyCallableLocalIdentities(
            "qubit version",
            transformedCallable,
            sourceCallable,
            historicalCallables,
            callable => callable.Qubits,
            qubit => qubit.Key,
            qubit => qubit.Origin);

        var sourceQubitIds = sourceCallable.Qubits
            .Select(qubit => qubit.Id)
            .ToHashSet();
        var historicalQubitIds = historicalCallables
            .SelectMany(callable => callable.Qubits)
            .Select(qubit => qubit.Id)
            .ToHashSet();
        foreach (var qubitId in transformedCallable.Qubits
                     .Select(qubit => qubit.Id)
                     .Distinct())
        {
            if (!sourceQubitIds.Contains(qubitId)
                && historicalQubitIds.Contains(qubitId))
            {
                throw new ArgumentException(
                    $"MIR transformation cannot reuse deleted qubit identity {qubitId} "
                    + $"in callable {transformedCallable.Id}.",
                    "program");
            }
        }
    }

    private static void VerifyCallableLocalIdentities<TId, TEntity>(
        string entityKind,
        MirCallable transformedCallable,
        MirCallable sourceCallable,
        IReadOnlyList<MirCallable> historicalCallables,
        Func<MirCallable, IEnumerable<TEntity>> entitiesOf,
        Func<TEntity, TId> idOf,
        Func<TEntity, MirOrigin> originOf)
        where TId : notnull
    {
        var sourceById = entitiesOf(sourceCallable).ToDictionary(idOf);
        var historicalIds = historicalCallables
            .SelectMany(entitiesOf)
            .Select(idOf)
            .ToHashSet();

        foreach (var transformedEntity in entitiesOf(transformedCallable))
        {
            var id = idOf(transformedEntity);
            var origin = originOf(transformedEntity);
            if (sourceById.TryGetValue(id, out var sourceEntity))
            {
                RequirePreservedIdentity(
                    $"{entityKind} in callable {transformedCallable.Id}",
                    id,
                    originOf(sourceEntity),
                    origin);
                continue;
            }

            RequireFreshIdentity(
                $"{entityKind} in callable {transformedCallable.Id}",
                id,
                origin,
                historicalIds.Contains(id));
        }
    }

    private static void RequirePreservedIdentity<TId>(
        string entityKind,
        TId id,
        MirOrigin sourceOrigin,
        MirOrigin transformedOrigin)
    {
        if (ReferenceEquals(sourceOrigin, transformedOrigin)) return;

        throw new ArgumentException(
            $"MIR transformation cannot rebind {entityKind} identity {id}; "
            + "a preserved identity must retain its exact origin.",
            "program");
    }

    private static void RequireFreshIdentity<TId>(
        string entityKind,
        TId id,
        MirOrigin origin,
        bool wasPreviouslyUsed)
    {
        if (wasPreviouslyUsed)
        {
            throw new ArgumentException(
                $"MIR transformation cannot reuse deleted {entityKind} identity {id}.",
                "program");
        }
        if (origin is MirGeneratedOrigin) return;

        throw new ArgumentException(
            $"MIR transformation cannot renumber an existing entity as {entityKind} identity {id}; "
            + "a fresh identity must carry a compiler-generated origin.",
            "program");
    }

    private static IEnumerable<MirSnapshot> HistoricalSnapshots(MirSnapshot source)
    {
        for (MirSnapshot? current = source;
             current is not null;
             current = current.PreviousSnapshot)
        {
            yield return current;
        }
    }

    public MirStage Stage { get; }
    public MirProgram Program { get; }
    public MirSnapshot? PreviousSnapshot { get; }
    internal HirSemanticArtifact HirArtifact { get; }
    public MirAnalysisStore Analyses { get; }
}
