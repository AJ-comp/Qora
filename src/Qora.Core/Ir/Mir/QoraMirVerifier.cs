using System.Text;
using Qora.Compiler;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

internal sealed record MirVerificationError(
    string Code,
    string Message,
    MirCallableId? Callable = null,
    MirBlockId? Block = null,
    MirInstructionId? Instruction = null,
    MirOrigin? Origin = null)
{
    public override string ToString()
    {
        var where = new List<string>();
        if (Callable is { } callable) where.Add(callable.ToString());
        if (Block is { } block) where.Add(block.ToString());
        if (Instruction is { } instruction) where.Add(instruction.ToString());
        return where.Count == 0
            ? $"{Code}: {Message}"
            : $"{Code} [{string.Join("/", where)}]: {Message}";
    }
}

/// <summary>
/// Structural verification at every MIR snapshot publication boundary. It verifies identity uniqueness,
/// SSA definition/use integrity, CFG edge contracts, dominance, instruction typing, call contracts, and classical-array /
/// versioned-qubit references. A failed verification is a compiler defect, not a source-language error.
/// </summary>
internal static class QoraMirVerifier
{
    public static IReadOnlyList<MirVerificationError> Verify(
        MirProgram? program,
        HirSnapshot? hirSnapshot = null)
    {
        if (program is null)
            return new[]
            {
                new MirVerificationError("MIR000", "the MIR program is null"),
            };

        return new Verifier(program, hirSnapshot).Run();
    }

    public static void VerifyOrThrow(
        MirProgram program,
        HirSnapshot? hirSnapshot = null)
    {
        var errors = Verify(program, hirSnapshot);
        if (errors.Count == 0) return;

        var message = new StringBuilder("QINTERNAL: invalid Qora MIR");
        foreach (var error in errors) message.AppendLine().Append("  ").Append(error);
        throw new InvalidOperationException(message.ToString());
    }

    private sealed class Verifier
    {
        private readonly MirProgram _program;
        private readonly HirSnapshot? _hirSnapshot;
        private readonly List<MirVerificationError> _errors = new();
        private readonly Dictionary<object, MirCallableId> _entityOwners =
            new(ReferenceEqualityComparer.Instance);

        public Verifier(
            MirProgram program,
            HirSnapshot? hirSnapshot)
        {
            _program = program;
            _hirSnapshot = hirSnapshot;
        }

        public IReadOnlyList<MirVerificationError> Run()
        {
            foreach (var callable in _program.Callables.OrderBy(callable => callable.Id.Value))
                VerifyCallable(callable);

            // Interprocedural qubit write contracts are checked only after every callable has passed
            // local structure, arity, typing, CFG, and SSA verification. The shared effect query does
            // not read QubitResults, so the declarations are compared against an independent semantic
            // source of truth rather than validating themselves.
            if (_errors.Count == 0)
                VerifyQubitWriteContracts();

            return _errors;
        }

        private void VerifyQubitWriteContracts()
        {
            MirFormalQubitEffectQuery effects;
            try
            {
                var callGraph =
                    MirCallGraphAnalysis.AnalyzeVerified(_program);
                effects =
                    MirEffectAnalysis.CreateFormalQubitEffectQueryUnchecked(
                        _program,
                        callGraph);
                effects.SummarizeAll();
            }
            catch (InvalidOperationException failure)
            {
                Add(
                    "MIR148",
                    $"qubit effect contracts could not be resolved: {failure.Message}");
                return;
            }

            foreach (var caller in _program.Callables.OrderBy(callable => callable.Id.Value))
            {
                foreach (var block in caller.Blocks)
                {
                    foreach (var apply in block.Instructions.OfType<MirQuantumApply>())
                    {
                        var expected = effects.ClassifyApply(caller, apply).Effects
                            .Where(effect =>
                                effect.Flags.HasFlag(MirQubitEffectFlags.Write))
                            .Select(effect => effect.Access.Qubit.Id)
                            .ToHashSet();
                        var actual = apply.QubitResults
                            .Select(result => result.Id)
                            .ToHashSet();
                        if (expected.SetEquals(actual))
                            continue;

                        Add(
                            "MIR142",
                            $"quantum apply results "
                            + $"[{string.Join(", ", actual.OrderBy(id => id.Value))}] "
                            + $"do not match semantic write set "
                            + $"[{string.Join(", ", expected.OrderBy(id => id.Value))}]",
                            caller,
                            block,
                            apply);
                    }
                }
            }
        }

        private void VerifyCallable(MirCallable callable)
        {
            var callableErrorStart = _errors.Count;
            if (string.IsNullOrWhiteSpace(callable.Name))
                Add("MIR005", "callable name is empty", callable);

            CheckOrigin(callable.Origin, "callable", callable);

            if (callable.ReturnType is { } returnType)
                CheckClassicalType(returnType, "function return type", callable);

            var blocks = callable.Blocks.ToDictionary(block => block.Id);
            var values = callable.Values.ToDictionary(value => value.Id);
            var storages = callable.Storages.ToDictionary(storage => storage.Id);
            var qubits = callable.Qubits.ToDictionary(qubit => qubit.Key);

            VerifyLocalOwnership(callable);

            foreach (var value in values.Values)
                CheckOrigin(value.Origin, $"SSA value {value.Id}", callable);
            foreach (var storage in storages.Values)
                CheckOrigin(storage.Origin, $"array storage {storage.Id}", callable);
            foreach (var qubit in qubits.Values)
                CheckOrigin(qubit.Origin, $"qubit {qubit.Key}", callable);
            foreach (var block in blocks.Values)
            {
                CheckOrigin(block.Origin, $"block {block.Id}", callable, block);
                foreach (var instruction in block.Instructions)
                {
                    CheckOrigin(
                        instruction.Origin,
                        $"instruction {instruction.Id}",
                        callable,
                        block,
                        instruction);
                    foreach (var access in instruction.QubitAccesses)
                    {
                        CheckOrigin(
                            access.Origin,
                            $"qubit access in instruction {instruction.Id}",
                            callable,
                            block,
                            instruction);
                    }
                }
                if (block.Terminator is { } terminator)
                    CheckOrigin(
                        terminator.Origin,
                        $"terminator of {block.Id}",
                        callable,
                        block);
            }

            foreach (var value in values.Values)
                CheckClassicalType(value.Type, $"type of {value.Id}", callable);

            var instructionLocations = new Dictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)>();
            foreach (var block in blocks.Values)
            {
                if (block.Terminator is null)
                {
                    Add("MIR016", "block has no terminator", callable, block);
                    continue;
                }

                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    var instruction = block.Instructions[index];
                    instructionLocations.Add(instruction.Id, (block, index, instruction));
                }
            }

            VerifyParameters(callable, values, storages);
            VerifyBlockArguments(callable, blocks, values);
            VerifyInstructionResults(callable, blocks, values, instructionLocations);
            VerifyStorageDefinitions(callable, storages);
            VerifyQubitSeeds(callable, qubits);

            var predecessors = VerifyControlFlow(callable, blocks, values);
            var dominators = ComputeDominators(callable.EntryBlock.Id, blocks, predecessors);
            VerifyQubitFlow(
                callable,
                blocks,
                qubits,
                instructionLocations,
                dominators);

            foreach (var block in blocks.Values)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    var instruction = block.Instructions[index];
                    VerifyUses(callable, block, index, instruction.InputValues, values, instructionLocations, dominators,
                        instruction.Id, instruction.Origin);
                    VerifyInstruction(callable, block, instruction, values, storages, qubits);
                }

                if (block.Terminator is not { } terminator) continue;
                VerifyUses(callable, block, block.Instructions.Count, terminator.InputValues, values,
                    instructionLocations, dominators, instruction: null, terminator.Origin);
                VerifyTerminator(callable, block, terminator, blocks, values);
            }

            if (_errors.Count == callableErrorStart)
                VerifyGraphContracts(callable, blocks, values);
        }

        private void VerifyLocalOwnership(MirCallable callable)
        {
            foreach (var parameter in callable.Parameters)
                ClaimOwner(callable, parameter, "parameter");
            foreach (var block in callable.Blocks)
            {
                ClaimOwner(callable, block, "block");
                foreach (var instruction in block.Instructions)
                    ClaimOwner(callable, instruction, "instruction");
            }
            foreach (var value in callable.Values)
                ClaimOwner(callable, value, "SSA value");
            foreach (var storage in callable.Storages)
                ClaimOwner(callable, storage, "array storage");
            foreach (var qubit in callable.Qubits)
                ClaimOwner(callable, qubit, "qubit version");
        }

        private void ClaimOwner(
            MirCallable callable,
            object entity,
            string role)
        {
            if (!_entityOwners.TryGetValue(entity, out var owner))
            {
                _entityOwners.Add(entity, callable.Id);
                return;
            }
            if (owner != callable.Id)
            {
                Add(
                    "MIR149",
                    $"{role} object is owned by both {owner} and {callable.Id}",
                    callable);
            }
        }

        /// <summary>
        /// Verifies contracts which require whole-CFG facts after the local structural and type checks
        /// have succeeded. Keeping this as a second phase avoids treating an incomplete graph as analyzable.
        /// </summary>
        private void VerifyGraphContracts(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values)
        {
            var cfg = MirControlFlowAnalysis.AnalyzeUnchecked(_program, callable);
            var provenance = MirStorageProvenanceAnalysis.AnalyzeUnchecked(_program, callable);
            VerifyExclusiveCallOperands(callable, values, provenance);
            VerifyArrayCallMinimumLengths(callable, values, provenance);
            VerifyCurrentArrayStates(callable, blocks, values, cfg);
            VerifyCurrentQubitStates(callable, blocks, cfg);
        }

        /// <summary>
        /// Read-only formals may share a caller allocation. As soon as one slot can mutate or consume
        /// an array, however, every other actual in that call must be provably disjoint. Callee-local
        /// storage IDs alone cannot establish this because two actual SSA states may have the same
        /// caller provenance.
        /// </summary>
        private void VerifyExclusiveCallOperands(
            MirCallable callable,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            MirStorageProvenanceSnapshot provenance)
        {
            foreach (var block in callable.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    var operands = instruction switch
                    {
                        MirPureCall call => call.Operands,
                        MirQuantumApply apply => apply.Operands,
                        _ => Array.Empty<MirCallOperand>(),
                    };
                    var arrays = operands
                        .Select((operand, index) => (operand, index))
                        .Where(item => item.operand is MirClassicalCallOperand classical
                            && values.TryGetValue(classical.Value, out var value)
                            && value.Type.IsArray)
                        .Select(item => (
                            Operand: (MirClassicalCallOperand)item.operand,
                            item.index))
                        .ToArray();

                    for (var leftIndex = 0; leftIndex < arrays.Length; leftIndex++)
                    {
                        for (var rightIndex = leftIndex + 1; rightIndex < arrays.Length; rightIndex++)
                        {
                            var left = arrays[leftIndex];
                            var right = arrays[rightIndex];
                            if (!RequiresExclusiveActual(left.Operand)
                                && !RequiresExclusiveActual(right.Operand))
                                continue;

                            var leftStorage = provenance.ProvenanceOf(left.Operand.Value);
                            var rightStorage = provenance.ProvenanceOf(right.Operand.Value);
                            if (!MirStorageAliasAnalysis.MayAlias(
                                    callable,
                                    leftStorage,
                                    rightStorage))
                                continue;

                            Add(
                                "MIR142",
                                $"array call operands {left.index} and {right.index} may alias, but at least one operand is mutable or moved",
                                callable,
                                block,
                                instruction);
                        }
                    }
                }
            }

            static bool RequiresExclusiveActual(MirClassicalCallOperand operand) =>
                operand.Access == QAccessMode.Mutable
                || operand.Ownership == QOwnershipMode.Moved;
        }

        private void VerifyArrayCallMinimumLengths(
            MirCallable caller,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            MirStorageProvenanceSnapshot provenance)
        {
            foreach (var block in caller.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    var (target, operands) = instruction switch
                    {
                        MirPureCall call => (call.Target, call.Operands),
                        MirQuantumApply apply => (apply.Target, apply.Operands),
                        _ => (null, null),
                    };
                    if (target is not MirUserCallableTarget userTarget
                        || operands is null
                        || _program.FindCallable(userTarget.Callable) is not { } callee)
                        continue;

                    var operandCount = Math.Min(operands.Count, callee.Parameters.Count);
                    for (var index = 0; index < operandCount; index++)
                    {
                        if (operands[index] is not MirClassicalCallOperand actual
                            || callee.Parameters[index] is not MirClassicalParameter expected
                            || expected.MinimumLength <= 0
                            || !values.TryGetValue(actual.Value, out var actualValue)
                            || !actualValue.Type.IsArray
                            || callee.FindValue(expected.Value) is not { Type.IsArray: true } expectedValue
                            || expectedValue.Type.KnownLength is int expectedLength
                                && expected.MinimumLength > expectedLength)
                            continue;

                        var actualMinimumLength = GuaranteedMinimumArrayLength(
                            caller,
                            actualValue,
                            provenance);
                        if (actualMinimumLength >= expected.MinimumLength)
                            continue;

                        Add(
                            "MIR152",
                            $"array call operand {index} guarantees length at least {actualMinimumLength}, "
                            + $"but parameter `{expected.Name}` requires at least {expected.MinimumLength}",
                            caller,
                            block,
                            instruction);
                    }
                }
            }
        }

        private static int GuaranteedMinimumArrayLength(
            MirCallable caller,
            MirValue value,
            MirStorageProvenanceSnapshot provenance)
        {
            if (value.Type.KnownLength is int knownLength)
                return knownLength;

            var possibleStorages = provenance.ProvenanceOf(value.Id);
            if (!possibleStorages.IsComplete || possibleStorages.PossibleStorages.Count == 0)
                return 0;

            var guaranteedMinimumLength = int.MaxValue;
            foreach (var storageId in possibleStorages.PossibleStorages)
            {
                var storage = caller.RequireStorage(storageId);
                var storageMinimumLength = caller.StorageKindOf(storage) switch
                {
                    MirArrayStorageKind.Parameter =>
                        ((MirClassicalParameter)caller.Parameters[
                            caller.StorageParameterIndexOf(storage)]).MinimumLength,
                    MirArrayStorageKind.Local => caller.StorageTypeOf(storage).KnownLength ?? 0,
                    _ => 0,
                };
                guaranteedMinimumLength = Math.Min(
                    guaranteedMinimumLength,
                    storageMinimumLength);
            }

            return guaranteedMinimumLength;
        }

        /// <summary>
        /// Validates the memory-SSA contract. Scalar dominance only proves that an old array ValueId is
        /// in scope; it does not prove that the physical buffer still contains that version. Every use,
        /// and especially every Phi incoming edge, must therefore carry the current state after stores
        /// and mutable calls on that path.
        /// </summary>
        private void VerifyCurrentArrayStates(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            MirControlFlowSnapshot cfg)
        {
            var memory = MirMemoryStateAnalysis.AnalyzeUnchecked(_program, callable);

            foreach (var block in callable.Blocks)
            {
                if (!cfg.IsReachable(block.Id))
                    continue;

                foreach (var instruction in block.Instructions)
                {
                    foreach (var input in instruction.InputValues.Distinct())
                    {
                        if (!values.TryGetValue(input, out var value)
                            || !value.Type.IsArray)
                            continue;
                        var availability = memory.CheckBeforeInstruction(
                            input,
                            instruction.Id);
                        if (!IsInvalidMemoryState(availability))
                            continue;
                        Add(
                            "MIR141",
                            $"array state {input} is not the current memory version before {instruction.Id} ({availability.Kind})",
                            callable,
                            block,
                            instruction);
                    }
                }

                foreach (var edge in OutgoingEdges(block.Terminator))
                {
                    if (!blocks.TryGetValue(edge.Target, out var target)
                        || !cfg.IsReachable(target.Id))
                        continue;

                    for (var index = 0;
                         index < target.Arguments.Count && index < edge.Arguments.Count;
                         index++)
                    {
                        if (!values.TryGetValue(
                                target.Arguments[index],
                                out var targetArgument)
                            || !targetArgument.Type.IsArray)
                            continue;
                        var incoming = edge.Arguments[index];
                        var availability = memory.CheckAtTerminator(
                            incoming,
                            block.Id);
                        if (!IsInvalidMemoryState(availability))
                            continue;
                        Add(
                            "MIR141",
                            $"edge {block.Id} -> {target.Id} passes stale array state {incoming} into memory Phi argument {index} ({availability.Kind})",
                            callable,
                            block,
                            source: block.Terminator.Origin);
                    }
                }
            }

            // Ownership consumption is a separate linear-resource fact. Until MIR carries explicit
            // ownership tokens, do not misclassify a transfer as a destructive memory write here.
            static bool IsInvalidMemoryState(MirMemoryStateAvailability availability) =>
                !availability.IsAvailable
                && (availability.Kind != MirMemoryStateAvailabilityKind.Clobbered
                    || availability.ClobberingMutations.Any(
                        mutation => mutation.Kind != MirMemoryMutationKind.OwnershipTransfer));

            static IEnumerable<(MirBlockId Target, IReadOnlyList<MirValueId> Arguments)>
                OutgoingEdges(MirTerminator terminator)
            {
                switch (terminator)
                {
                    case MirJump jump:
                        yield return (jump.Target, jump.Arguments);
                        break;
                    case MirBranch branch:
                        yield return (branch.TrueTarget, branch.TrueArguments);
                        yield return (branch.FalseTarget, branch.FalseArguments);
                        break;
                }
            }
        }

        /// <summary>
        /// Validates the current-version contract for versioned qubits. Dominance only proves that
        /// an older version still has a definition; unlike an immutable scalar, that historical state is
        /// no longer physically available after the same qubit identity advances. A quantum instruction
        /// may access several elements of one register, but every such access must therefore name the same
        /// incoming register version. Phi inputs must likewise name the state current on their exact edge.
        /// </summary>
        private void VerifyCurrentQubitStates(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            MirControlFlowSnapshot cfg)
        {
            var parameterQubits = callable.Parameters
                .OfType<MirQubitParameter>()
                .Select(parameter => parameter.Id)
                .ToHashSet();
            var localSeedBlocks = callable.Blocks
                .SelectMany(block => block.Instructions
                    .OfType<MirQubitAllocate>()
                    .Select(allocation => (allocation.Result.Id, Block: block.Id)))
                .ToDictionary(item => item.Id, item => item.Block);
            var entryStates =
                new Dictionary<MirBlockId, Dictionary<MirQubitId, MirQubitKey?>>();
            var exitStates =
                new Dictionary<MirBlockId, Dictionary<MirQubitId, MirQubitKey?>>();
            var pending = new Queue<MirBlockId>();
            var queued = new HashSet<MirBlockId>();

            Enqueue(callable.EntryBlock.Id);
            while (pending.Count > 0)
            {
                var blockId = pending.Dequeue();
                queued.Remove(blockId);
                if (!blocks.TryGetValue(blockId, out var block)
                    || !cfg.IsReachable(blockId))
                {
                    continue;
                }

                Dictionary<MirQubitId, MirQubitKey?> entry;
                if (blockId == callable.EntryBlock.Id)
                {
                    entry = callable.Parameters
                        .OfType<MirQubitParameter>()
                        .ToDictionary(
                            parameter => parameter.Id,
                            parameter => (MirQubitKey?)parameter.Key);
                }
                else
                {
                    var predecessorStates = cfg.PredecessorsOf(blockId)
                        .Where(exitStates.ContainsKey)
                        .Select(predecessor => exitStates[predecessor])
                        .ToArray();
                    if (predecessorStates.Length == 0)
                        continue;
                    entry = Merge(predecessorStates);
                }

                foreach (var group in block.QubitPhis.GroupBy(phi => phi.Id))
                {
                    entry[group.Key] = group.Count() == 1
                        ? group.Single().Key
                        : null;
                }

                entryStates[blockId] = entry;
                var exit = Transfer(entry, block.Instructions);
                if (exitStates.TryGetValue(blockId, out var previous)
                    && SameState(previous, exit))
                {
                    continue;
                }

                exitStates[blockId] = exit;
                foreach (var successor in cfg.SuccessorsOf(blockId))
                    Enqueue(successor);
            }

            foreach (var block in callable.Blocks)
            {
                if (!cfg.IsReachable(block.Id)
                    || !entryStates.TryGetValue(block.Id, out var blockEntry))
                {
                    continue;
                }

                var predecessorStates = cfg.PredecessorsOf(block.Id)
                    .Where(exitStates.ContainsKey)
                    .Select(predecessor => exitStates[predecessor])
                    .ToArray();
                if (predecessorStates.Length > 1)
                {
                    var unnormalized = Merge(predecessorStates)
                        .Where(pair => pair.Value is null)
                        .Select(pair => pair.Key)
                        .ToArray();
                    var uniquePhis = block.QubitPhis
                        .GroupBy(phi => phi.Id)
                        .Where(group => group.Count() == 1)
                        .Select(group => group.Key)
                        .ToHashSet();
                    foreach (var id in unnormalized)
                    {
                        if (uniquePhis.Contains(id)
                            || !RequiresCurrentStateAtEntry(id, block.Id))
                        {
                            continue;
                        }

                        Add(
                            "MIR146",
                            $"reachable join {block.Id} has no unique current version of qubit {id}; "
                            + "predecessor states must agree or be normalized by exactly one Phi",
                            callable,
                            block);
                    }
                }

                foreach (var group in block.QubitPhis.GroupBy(phi => phi.Id))
                {
                    if (group.Count() > 1)
                    {
                        Add(
                            "MIR146",
                            $"block {block.Id} defines more than one current-state Phi for qubit {group.Key}",
                            callable,
                            block);
                    }
                }

                foreach (var phi in block.QubitPhis)
                {
                    foreach (var input in phi.Inputs)
                    {
                        if (!cfg.IsReachable(input.Edge.Source)
                            || !exitStates.TryGetValue(
                                input.Edge.Source,
                                out var predecessorState))
                        {
                            continue;
                        }

                        VerifyCurrent(
                            predecessorState,
                            input.Qubit,
                            $"qubit Phi {phi.Key} input on edge "
                            + $"{input.Edge.Source}:{input.Edge.SuccessorOrdinal} -> "
                            + $"{block.Id}",
                            block,
                            instruction: null,
                            phi.Origin);
                    }
                }

                var current =
                    new Dictionary<MirQubitId, MirQubitKey?>(blockEntry);
                foreach (var instruction in block.Instructions)
                {
                    foreach (var group in instruction.QubitAccesses
                                 .GroupBy(access => access.Qubit.Id))
                    {
                        var versions = group
                            .Select(access => access.Qubit)
                            .Distinct()
                            .ToArray();
                        if (versions.Length != 1)
                        {
                            Add(
                                "MIR146",
                                $"instruction {instruction.Id} accesses qubit {group.Key} "
                                + "through multiple input versions "
                                + $"[{string.Join(", ", versions)}]",
                                callable,
                                block,
                                instruction);
                            continue;
                        }

                        VerifyCurrent(
                            current,
                            versions[0],
                            $"instruction {instruction.Id}",
                            block,
                            instruction,
                            instruction.Origin);
                    }

                    current = Transfer(current, new[] { instruction });
                }
            }

            bool RequiresCurrentStateAtEntry(
                MirQubitId id,
                MirBlockId block)
            {
                if (parameterQubits.Contains(id))
                    return true;
                return localSeedBlocks.TryGetValue(id, out var seedBlock)
                    && seedBlock != block
                    && cfg.Dominates(seedBlock, block);
            }

            void VerifyCurrent(
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> state,
                MirQubitKey actual,
                string role,
                MirBlock block,
                MirInstruction? instruction,
                MirOrigin origin)
            {
                if (!state.TryGetValue(actual.Id, out var expected))
                {
                    Add(
                        "MIR146",
                        $"{role} accesses {actual}, but qubit {actual.Id} has no current state on this path",
                        callable,
                        block,
                        instruction,
                        origin);
                    return;
                }
                if (expected is not MirQubitKey current)
                {
                    Add(
                        "MIR146",
                        $"{role} accesses {actual}, but incoming paths have no unique current "
                        + $"version of qubit {actual.Id}",
                        callable,
                        block,
                        instruction,
                        origin);
                    return;
                }
                if (current != actual)
                {
                    Add(
                        "MIR146",
                        $"{role} accesses stale version {actual}; current version is {current}",
                        callable,
                        block,
                        instruction,
                        origin);
                }
            }

            void Enqueue(MirBlockId block)
            {
                if (queued.Add(block))
                    pending.Enqueue(block);
            }

            static Dictionary<MirQubitId, MirQubitKey?> Merge(
                IReadOnlyList<Dictionary<MirQubitId, MirQubitKey?>> states)
            {
                var merged = new Dictionary<MirQubitId, MirQubitKey?>();
                foreach (var id in states.SelectMany(state => state.Keys).Distinct())
                {
                    MirQubitKey? candidate = null;
                    var conflict = false;
                    foreach (var state in states)
                    {
                        if (!state.TryGetValue(id, out var incoming)
                            || incoming is not MirQubitKey key)
                        {
                            conflict = true;
                            break;
                        }
                        if (candidate is MirQubitKey existing && existing != key)
                        {
                            conflict = true;
                            break;
                        }
                        candidate = key;
                    }
                    merged[id] = conflict ? null : candidate;
                }
                return merged;
            }

            static Dictionary<MirQubitId, MirQubitKey?> Transfer(
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> entry,
                IReadOnlyList<MirInstruction> instructions)
            {
                var state = new Dictionary<MirQubitId, MirQubitKey?>(entry);
                foreach (var instruction in instructions)
                {
                    foreach (var result in QubitResultsOf(instruction))
                        state[result.Id] = result.Key;
                }
                return state;
            }

            static IEnumerable<MirQubit> QubitResultsOf(
                MirInstruction instruction) =>
                instruction switch
                {
                    MirQubitAllocate allocation =>
                        new MirQubit[] { allocation.Result },
                    MirQuantumApply apply =>
                        apply.QubitResults,
                    MirMeasure measure =>
                        new MirQubit[] { measure.QubitResult },
                    _ =>
                        Array.Empty<MirQubit>(),
                };

            static bool SameState(
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> left,
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> right) =>
                left.Count == right.Count
                && left.All(pair =>
                    right.TryGetValue(pair.Key, out var value)
                    && value == pair.Value);
        }

        private void VerifyParameters(
            MirCallable callable,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirStorageId, MirArrayStorage> storages)
        {
            for (var index = 0; index < callable.Parameters.Count; index++)
            {
                var parameter = callable.Parameters[index];
                switch (parameter)
                {
                    case MirClassicalParameter classical:
                    {
                        if (!values.TryGetValue(classical.Value, out var value))
                        {
                            Add("MIR021", $"classical parameter `{classical.Name}` references missing value {classical.Value}",
                                callable);
                        }
                        else
                        {
                            CheckClassicalType(
                                value.Type,
                                $"parameter `{classical.Name}`",
                                callable);
                            if (value.Definition is not
                                {
                                    Kind: MirValueDefinitionKind.Parameter,
                                    Index: var definitionIndex
                                }
                                || definitionIndex != index)
                                Add("MIR023",
                                    $"{classical.Value} is not defined by parameter slot {index}",
                                    callable);

                            if (value.Type.IsArray)
                            {
                                if (classical.Storage is not MirStorageId storageId)
                                    Add("MIR024", $"array parameter `{classical.Name}` has no storage identity", callable);
                                else if (!storages.ContainsKey(storageId))
                                    Add("MIR025",
                                        $"array parameter `{classical.Name}` references missing storage {storageId}",
                                        callable);
                                if (classical.MinimumLength < 0
                                    || (value.Type.KnownLength is int knownLength
                                        && classical.MinimumLength > knownLength))
                                {
                                    Add(
                                        "MIR151",
                                        $"array parameter `{classical.Name}` has invalid minimum length "
                                        + classical.MinimumLength,
                                        callable);
                                }
                            }
                            else if (classical.Storage is not null)
                            {
                                Add(
                                    "MIR027",
                                    $"scalar parameter `{classical.Name}` carries an array storage identity",
                                    callable);
                            }
                            else if (classical.MinimumLength != 0)
                            {
                                Add(
                                    "MIR151",
                                    $"scalar parameter `{classical.Name}` carries minimum array length "
                                    + classical.MinimumLength,
                                    callable);
                            }
                        }

                        if (callable.Kind == MirCallableKind.Function
                            && (classical.Ownership != QOwnershipMode.Borrowed
                                || classical.Access != QAccessMode.ReadOnly))
                            Add("MIR028",
                                $"function parameter `{classical.Name}` is not borrowed/read-only",
                                callable);
                        break;
                    }

                    case MirQubitParameter qubit:
                    {
                        if (callable.Kind == MirCallableKind.Function)
                            Add("MIR029", $"function has qubit parameter `{qubit.Name}`", callable);
                        break;
                    }

                    default:
                        Add("MIR035", $"parameter slot {index} has unknown kind {parameter.GetType().Name}", callable);
                        break;
                }
            }
        }

        private void VerifyBlockArguments(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values)
        {
            foreach (var block in blocks.Values)
            {
                var seen = new HashSet<MirValueId>();
                for (var index = 0; index < block.Arguments.Count; index++)
                {
                    var argument = block.Arguments[index];
                    if (!seen.Add(argument))
                        Add("MIR040", $"block argument value {argument} appears more than once",
                            callable, block);
                    if (!values.TryGetValue(argument, out var value))
                    {
                        Add("MIR041", $"block argument references missing value {argument}", callable, block);
                        continue;
                    }
                    CheckClassicalType(
                        value.Type,
                        $"block argument {argument}",
                        callable,
                        block);
                    if (value.Definition is not
                        {
                            Kind: MirValueDefinitionKind.BlockArgument,
                            Index: var definitionIndex,
                            Block: var definitionBlock
                        }
                        || definitionIndex != index || definitionBlock != block.Id)
                        Add("MIR043",
                            $"{argument} is not defined by argument {index} of {block.Id}",
                            callable, block);
                }
            }
        }

        private void VerifyInstructionResults(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)> instructions)
        {
            var definitions = new Dictionary<MirValueId, (MirBlockId Block, MirInstructionId Instruction, int Index)>();
            foreach (var (instructionId, location) in instructions)
            {
                var results = location.Instruction.ResultValues;
                for (var index = 0; index < results.Count; index++)
                {
                    var result = results[index];
                    if (!definitions.TryAdd(result, (location.Block.Id, instructionId, index)))
                        Add("MIR044", $"SSA value {result} is produced by more than one instruction",
                            callable, location.Block, location.Instruction);
                    if (!values.TryGetValue(result, out var value))
                    {
                        Add("MIR045", $"instruction result {result} is absent from the value table",
                            callable, location.Block, location.Instruction);
                        continue;
                    }
                    if (value.Definition is not
                        {
                            Kind: MirValueDefinitionKind.InstructionResult,
                            Index: var definitionIndex,
                            Instruction: var definitionInstruction
                        }
                        || definitionIndex != index
                        || definitionInstruction != instructionId)
                        Add("MIR046",
                            $"{result} does not point back to result {index} of {instructionId} in {location.Block.Id}",
                            callable, location.Block, location.Instruction);
                }
            }

            foreach (var value in values.Values)
            {
                switch (value.Definition.Kind)
                {
                    case MirValueDefinitionKind.Parameter:
                        if (value.Definition.Index >= callable.Parameters.Count)
                            Add("MIR047", $"{value.Id} names missing parameter slot {value.Definition.Index}", callable);
                        break;
                    case MirValueDefinitionKind.BlockArgument:
                    {
                        var blockId = value.Definition.Block!.Value;
                        if (!blocks.TryGetValue(blockId, out var block)
                            || value.Definition.Index >= block.Arguments.Count
                            || block.Arguments[value.Definition.Index] != value.Id)
                            Add("MIR048", $"{value.Id} has a dangling block-argument definition", callable);
                        break;
                    }
                    case MirValueDefinitionKind.InstructionResult:
                    {
                        var instructionId = value.Definition.Instruction!.Value;
                        if (!instructions.TryGetValue(instructionId, out var location)
                            || value.Definition.Index >= location.Instruction.ResultValues.Count
                            || location.Instruction.ResultValues[value.Definition.Index] != value.Id)
                            Add("MIR049", $"{value.Id} has a dangling instruction-result definition", callable);
                        break;
                    }
                }
            }
        }

        private void VerifyStorageDefinitions(
            MirCallable callable,
            IReadOnlyDictionary<MirStorageId, MirArrayStorage> storages)
        {
            foreach (var storage in storages.Values)
            {
                var definitionCount =
                    callable.StorageDefinitionCountOf(storage);
                if (definitionCount != 1)
                {
                    Add(
                        "MIR056",
                        $"array storage {storage.Id} has {definitionCount} defining sites; expected exactly one parameter or array-create owner",
                        callable);
                }
            }
        }

        private void VerifyQubitSeeds(
            MirCallable callable,
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits)
        {
            foreach (var group in qubits.Values.GroupBy(qubit => qubit.Id))
            {
                if (!group.Any(qubit => qubit.Version.Value == 0))
                {
                    Add("MIR060",
                        $"qubit {group.Key} has no initial version-zero definition",
                        callable);
                }
            }
        }

        private Dictionary<MirBlockId, HashSet<MirBlockId>> VerifyControlFlow(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values)
        {
            var predecessors = blocks.Keys.ToDictionary(id => id, _ => new HashSet<MirBlockId>());
            foreach (var block in blocks.Values)
            {
                if (block.Terminator is not { } terminator) continue;
                foreach (var successor in terminator.Successors)
                {
                    if (!blocks.ContainsKey(successor))
                    {
                        Add("MIR062", $"terminator targets missing block {successor}", callable, block,
                            source: terminator.Origin);
                        continue;
                    }
                    predecessors[successor].Add(block.Id);
                }

                switch (terminator)
                {
                    case MirJump jump:
                        VerifyEdge(callable, block, jump.Target, jump.Arguments, blocks, values, "jump", jump.Origin);
                        break;
                    case MirBranch branch:
                        VerifyEdge(callable, block, branch.TrueTarget, branch.TrueArguments, blocks, values,
                            "true edge", branch.Origin);
                        VerifyEdge(callable, block, branch.FalseTarget, branch.FalseArguments, blocks, values,
                            "false edge", branch.Origin);
                        break;
                }
            }

            if (predecessors.TryGetValue(callable.EntryBlock.Id, out var entryPredecessors)
                && entryPredecessors.Count != 0)
            {
                Add(
                    "MIR147",
                    $"entry block {callable.EntryBlock.Id} has predecessor(s) "
                    + $"[{string.Join(", ", entryPredecessors.OrderBy(id => id.Value))}]; "
                    + "a callable entry block must be a unique CFG root",
                    callable,
                    callable.EntryBlock);
            }

            return predecessors;
        }

        private void VerifyEdge(
            MirCallable callable,
            MirBlock sourceBlock,
            MirBlockId targetId,
            IReadOnlyList<MirValueId> arguments,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            string edge,
            MirOrigin source)
        {
            if (!blocks.TryGetValue(targetId, out var target)) return;
            if (arguments.Count != target.Arguments.Count)
            {
                Add("MIR063",
                    $"{edge} from {sourceBlock.Id} supplies {arguments.Count} value(s), but {targetId} expects {target.Arguments.Count}",
                    callable, sourceBlock, source: source);
                return;
            }

            for (var index = 0; index < arguments.Count; index++)
            {
                if (!values.TryGetValue(arguments[index], out var value)) continue;
                if (!values.TryGetValue(
                        target.Arguments[index],
                        out var targetArgument))
                {
                    continue;
                }
                if (value.Type != targetArgument.Type)
                    Add("MIR064",
                        $"{edge} argument {index} is {value.Type}, but {targetId} expects {targetArgument.Type}",
                        callable, sourceBlock, source: source);
            }
        }

        private static Dictionary<MirBlockId, HashSet<MirBlockId>> ComputeDominators(
            MirBlockId entry,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> predecessors)
        {
            var reachable = new HashSet<MirBlockId>();
            if (blocks.ContainsKey(entry))
            {
                var pending = new Stack<MirBlockId>();
                pending.Push(entry);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (!reachable.Add(current)) continue;
                    if (blocks[current].Terminator is not { } terminator) continue;
                    foreach (var successor in terminator.Successors)
                        if (blocks.ContainsKey(successor)) pending.Push(successor);
                }
            }

            var dominators = new Dictionary<MirBlockId, HashSet<MirBlockId>>();
            foreach (var block in blocks.Keys)
                dominators[block] = block == entry
                    ? new HashSet<MirBlockId> { entry }
                    : reachable.Contains(block)
                        ? new HashSet<MirBlockId>(reachable)
                        : new HashSet<MirBlockId> { block };

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in reachable)
                {
                    if (block == entry) continue;
                    var reachablePredecessors = predecessors[block].Where(reachable.Contains).ToList();
                    var next = reachablePredecessors.Count == 0
                        ? new HashSet<MirBlockId>()
                        : new HashSet<MirBlockId>(dominators[reachablePredecessors[0]]);
                    foreach (var predecessor in reachablePredecessors.Skip(1))
                        next.IntersectWith(dominators[predecessor]);
                    next.Add(block);
                    if (!next.SetEquals(dominators[block]))
                    {
                        dominators[block] = next;
                        changed = true;
                    }
                }
            }
            return dominators;
        }

        private void VerifyUses(
            MirCallable callable,
            MirBlock useBlock,
            int useIndex,
            IReadOnlyList<MirValueId> operands,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)> instructions,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators,
            MirInstructionId? instruction,
            MirOrigin source)
        {
            foreach (var operand in operands)
            {
                if (!values.TryGetValue(operand, out var value))
                {
                    Add("MIR070", $"operand references missing SSA value {operand}",
                        callable, useBlock, instruction is { } id ? FindInstruction(useBlock, id) : null,
                        source);
                    continue;
                }

                switch (value.Definition.Kind)
                {
                    case MirValueDefinitionKind.Parameter:
                        break;
                    case MirValueDefinitionKind.BlockArgument:
                        CheckDominates(
                            value.Id,
                            value.Definition.Block!.Value,
                            instructionIndex: null);
                        break;
                    case MirValueDefinitionKind.InstructionResult:
                    {
                        var definitionInstruction =
                            value.Definition.Instruction!.Value;
                        if (!instructions.TryGetValue(definitionInstruction, out var location))
                            break; // dangling-definition error is emitted separately
                        CheckDominates(value.Id, location.Block.Id, location.Index);
                        break;
                    }
                }
            }

            void CheckDominates(
                MirValueId value,
                MirBlockId definitionBlock,
                int? instructionIndex)
            {
                if (definitionBlock == useBlock.Id)
                {
                    if (instructionIndex is int definitionIndex && definitionIndex >= useIndex)
                        Add("MIR071",
                            $"{value} is used before its defining instruction in {useBlock.Id}",
                            callable, useBlock, instruction is { } id ? FindInstruction(useBlock, id) : null, source);
                    return;
                }

                if (!dominators.TryGetValue(useBlock.Id, out var set)
                    || !set.Contains(definitionBlock))
                    Add("MIR072",
                        $"{value}, defined in {definitionBlock}, does not dominate its use in {useBlock.Id}",
                        callable, useBlock, instruction is { } id ? FindInstruction(useBlock, id) : null, source);
            }
        }

        private void VerifyQubitFlow(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits,
            IReadOnlyDictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)> instructions,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators)
        {
            var definitions =
                new Dictionary<MirQubitKey, (MirBlockId? Block, int? InstructionIndex)>();
            foreach (var parameter in callable.Parameters.OfType<MirQubitParameter>())
                definitions.TryAdd(parameter.Key, (null, null));
            foreach (var block in blocks.Values)
            {
                foreach (var phi in block.QubitPhis)
                {
                    definitions.TryAdd(phi.Key, (block.Id, null));
                }
            }
            foreach (var location in instructions.Values)
            {
                IEnumerable<MirQubit> results = location.Instruction switch
                {
                    MirQubitAllocate allocation => new MirQubit[] { allocation.Result },
                    MirQuantumApply apply => apply.QubitResults,
                    MirMeasure measure => new MirQubit[] { measure.QubitResult },
                    _ => Array.Empty<MirQubit>(),
                };
                foreach (var result in results)
                    definitions.TryAdd(
                        result.Key,
                        (location.Block.Id, location.Index));
            }

            var incomingEdges = blocks.Keys.ToDictionary(
                block => block,
                _ => new HashSet<MirControlFlowEdge>());
            foreach (var source in blocks.Values)
            {
                foreach (var (edge, target) in OutgoingQubitEdges(source))
                    if (incomingEdges.TryGetValue(target, out var incoming))
                        incoming.Add(edge);
            }

            foreach (var block in blocks.Values)
            {
                foreach (var phi in block.QubitPhis)
                {
                    var seenEdges = new HashSet<MirControlFlowEdge>();
                    foreach (var input in phi.Inputs)
                    {
                        if (!seenEdges.Add(input.Edge))
                            Add("MIR144",
                                $"qubit Phi {phi.Key} contains duplicate input edge {input.Edge}",
                                callable, block);
                        if (!incomingEdges[block.Id].Contains(input.Edge))
                            Add("MIR144",
                                $"qubit Phi {phi.Key} references a nonincoming edge {input.Edge}",
                                callable, block);
                        if (input.Qubit.Id != phi.Id)
                            Add("MIR144",
                                $"qubit Phi {phi.Key} receives different identity {input.Qubit}",
                                callable, block);
                        if (!qubits.ContainsKey(input.Qubit))
                        {
                            Add("MIR144",
                                $"qubit Phi {phi.Key} receives missing version {input.Qubit}",
                                callable, block);
                            continue;
                        }
                        if (blocks.TryGetValue(input.Edge.Source, out var source))
                        {
                            CheckAvailable(
                                input.Qubit,
                                source,
                                source.Instructions.Count,
                                phi.Origin,
                                $"Phi {phi.Key} input");
                        }
                    }

                    if (!seenEdges.SetEquals(incomingEdges[block.Id]))
                        Add("MIR144",
                            $"qubit Phi {phi.Key} covers [{string.Join(", ", seenEdges)}], expected every incoming edge [{string.Join(", ", incomingEdges[block.Id])}]",
                            callable, block);
                }

                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    var instruction = block.Instructions[index];
                    foreach (var access in instruction.QubitAccesses)
                        if (qubits.ContainsKey(access.Qubit))
                            CheckAvailable(
                                access.Qubit,
                                block,
                                index,
                                instruction.Origin,
                                $"instruction {instruction.Id}");
                }
            }

            void CheckAvailable(
                MirQubitKey qubit,
                MirBlock useBlock,
                int useIndex,
                MirOrigin source,
                string role)
            {
                if (!definitions.TryGetValue(qubit, out var definition))
                {
                    Add("MIR145",
                        $"{role} references qubit {qubit} without a definition",
                        callable, useBlock, source: source);
                    return;
                }
                if (definition.Block is not MirBlockId definitionBlock)
                    return; // Parameter versions are available at callable entry.
                if (definitionBlock == useBlock.Id)
                {
                    if (definition.InstructionIndex is int definitionIndex
                        && definitionIndex >= useIndex)
                        Add("MIR145",
                            $"{role} uses qubit {qubit} before its defining instruction",
                            callable, useBlock, source: source);
                    return;
                }
                if (!dominators.TryGetValue(useBlock.Id, out var set)
                    || !set.Contains(definitionBlock))
                    Add("MIR145",
                        $"qubit {qubit}, defined in {definitionBlock}, does not dominate {role} in {useBlock.Id}",
                        callable, useBlock, source: source);
            }

            static IEnumerable<(MirControlFlowEdge Edge, MirBlockId Target)>
                OutgoingQubitEdges(MirBlock block)
            {
                switch (block.Terminator)
                {
                    case MirJump jump:
                        yield return (
                            new MirControlFlowEdge(block.Id, 0),
                            jump.Target);
                        break;
                    case MirBranch branch:
                        yield return (
                            new MirControlFlowEdge(block.Id, 0),
                            branch.TrueTarget);
                        yield return (
                            new MirControlFlowEdge(block.Id, 1),
                            branch.FalseTarget);
                        break;
                }
            }
        }

        private void VerifyInstruction(
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirStorageId, MirArrayStorage> storages,
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits)
        {
            MirType? TypeOf(MirValueId id) => values.TryGetValue(id, out var value) ? value.Type : null;

            foreach (var access in instruction.QubitAccesses)
                VerifyQubitAccess(callable, block, instruction, access, values, qubits);

            switch (instruction)
            {
                case MirConstant constant:
                {
                    var result = TypeOf(constant.Result);
                    RequireScalar(
                        result,
                        $"constant {constant.Text} result",
                        callable,
                        block,
                        constant);
                    break;
                }
                case MirUnary unary:
                {
                    var operand = TypeOf(unary.Operand);
                    var result = TypeOf(unary.Result);
                    RequireScalar(operand, "unary operand", callable, block, unary);
                    RequireScalar(result, "unary result", callable, block, unary);
                    if (unary.Operator == MirUnaryOperator.LogicalNot
                        && (operand is { ElementType: not QType.Bit }
                            || result is { ElementType: not QType.Bit }))
                        Add("MIR075", "logical-not requires bit operand and result", callable, block, unary);
                    break;
                }
                case MirBinary binary:
                {
                    var left = TypeOf(binary.Left);
                    var right = TypeOf(binary.Right);
                    var result = TypeOf(binary.Result);
                    RequireScalar(result, "binary result", callable, block, binary);
                    var leftIsArray = left is { IsArray: true };
                    var rightIsArray = right is { IsArray: true };
                    if (leftIsArray || rightIsArray)
                    {
                        if (!leftIsArray || !rightIsArray)
                        {
                            Add("MIR076",
                                $"binary array comparison requires two arrays, not {left} and {right}",
                                callable, block, binary);
                        }
                        else if (left is { } leftArray
                                 && right is { } rightArray
                                 && leftArray.ElementType != rightArray.ElementType)
                        {
                            Add("MIR076",
                                $"binary array comparison has different element types {leftArray} and {rightArray}",
                                callable, block, binary);
                        }

                        if (binary.Operator is not (MirBinaryOperator.Equal or MirBinaryOperator.NotEqual))
                        {
                            Add("MIR076",
                                $"{binary.Operator} cannot consume array operands; only equal/not-equal compare array states",
                                callable, block, binary);
                        }
                    }
                    else
                    {
                        if (left is { } lhs && right is { } rhs && lhs != rhs)
                        {
                            Add("MIR076",
                                $"binary operands have different types {lhs} and {rhs}; lowering must insert an explicit conversion",
                                callable, block, binary);
                        }
                        RequireScalar(left, "binary left operand", callable, block, binary);
                        RequireScalar(right, "binary right operand", callable, block, binary);
                    }
                    var producesBit = binary.Operator is
                        MirBinaryOperator.Equal or MirBinaryOperator.NotEqual
                        or MirBinaryOperator.Less or MirBinaryOperator.LessOrEqual
                        or MirBinaryOperator.Greater or MirBinaryOperator.GreaterOrEqual;
                    if (producesBit && result is { } binaryResultType
                        && binaryResultType != MirType.Scalar(QType.Bit))
                        Add("MIR077", $"{binary.Operator} must produce bit, not {binaryResultType}",
                            callable, block, binary);
                    break;
                }
                case MirConvert convert:
                {
                    var operand = TypeOf(convert.Operand);
                    var result = TypeOf(convert.Result);
                    RequireScalar(operand, "conversion operand", callable, block, convert);
                    RequireScalar(result, "conversion result", callable, block, convert);
                    break;
                }
                case MirArrayCreate create:
                {
                    if (!storages.ContainsKey(create.Storage))
                        Add("MIR080", $"array creation references missing storage {create.Storage}",
                            callable, block, create);
                    var result = TypeOf(create.Result);
                    if (result is not
                        {
                            IsArray: true,
                            KnownLength: int length,
                        } expected)
                    {
                        Add(
                            "MIR081",
                            $"array creation result must have a known-length array type, not {result}",
                            callable,
                            block,
                            create);
                        break;
                    }
                    if (create.Initialization == MirArrayInitialization.ExplicitElements
                        && create.Elements.Count != length)
                        Add("MIR084",
                            $"explicit array creation has {create.Elements.Count} element(s), expected {length}",
                            callable, block, create);
                    if (create.Initialization == MirArrayInitialization.ZeroInitialized
                        && create.Elements.Count != 0)
                        Add("MIR085", "zero-initialized array creation carries explicit elements",
                            callable, block, create);
                    foreach (var element in create.Elements)
                        if (TypeOf(element) is { } elementType
                            && elementType != MirType.Scalar(expected.ElementType))
                            Add("MIR086",
                                $"array element {element} is {elementType}, expected {expected.ElementType.ToString().ToLowerInvariant()}",
                                callable, block, create);
                    break;
                }
                case MirArrayLength length:
                {
                    if (TypeOf(length.Array) is { IsArray: false } arrayType)
                        Add("MIR087", $"array-length operand is scalar {arrayType}", callable, block, length);
                    if (TypeOf(length.Result) is { } lengthResultType
                        && lengthResultType != MirType.Scalar(QType.Int))
                        Add("MIR088", $"array length result is {lengthResultType}, expected int",
                            callable, block, length);
                    break;
                }
                case MirArrayLoad load:
                {
                    var array = TypeOf(load.Array);
                    RequireIntIndex(TypeOf(load.Index), callable, block, load);
                    if (array is { IsArray: false } scalar)
                        Add("MIR089", $"array-load source is scalar {scalar}", callable, block, load);
                    else if (array is { IsArray: true } arrayType
                             && TypeOf(load.Result) is { } loadResultType
                             && loadResultType != MirType.Scalar(arrayType.ElementType))
                        Add("MIR090",
                            $"array-load result is {loadResultType}, expected {arrayType.ElementType.ToString().ToLowerInvariant()}",
                            callable, block, load);
                    break;
                }
                case MirArrayStore store:
                {
                    var array = TypeOf(store.Array);
                    RequireIntIndex(TypeOf(store.Index), callable, block, store);
                    if (array is { IsArray: false } scalar)
                        Add("MIR091", $"array-store source is scalar {scalar}", callable, block, store);
                    else if (array is { IsArray: true } arrayType)
                    {
                        if (TypeOf(store.Value) is { } valueType
                            && valueType != MirType.Scalar(arrayType.ElementType))
                            Add("MIR092",
                                $"array-store value is {valueType}, expected {arrayType.ElementType.ToString().ToLowerInvariant()}",
                                callable, block, store);
                        if (TypeOf(store.Result) is { } storedResultType && storedResultType != arrayType)
                            Add("MIR093",
                                $"array-store result is {storedResultType}, expected previous state type {arrayType}",
                                callable, block, store);
                    }
                    break;
                }
                case MirPureCall call:
                    if (call.Operands.Any(operand => operand is not MirClassicalCallOperand))
                        Add("MIR094", "pure call carries a qubit operand", callable, block, call);
                    VerifyCall(callable, block, call, call.Target, call.Operands, values, qubits,
                        expectFunction: true);
                    if (call.Target is MirUserCallableTarget user
                        && _program.FindCallable(user.Callable) is { } callee
                        && callee.ReturnType is { } returnType
                        && TypeOf(call.Result) is { } callResultType
                        && callResultType != returnType)
                        Add("MIR095",
                            $"pure-call result is {callResultType}, but {callee.Name} returns {returnType}",
                            callable, block, call);
                    if (call.Target is MirBuiltinFunctionTarget builtin
                        && QoraGates.Functions.TryGetValue(builtin.Name, out var function)
                        && TypeOf(call.Result) is { } builtinResult
                        && builtinResult != MirType.Scalar(function.Returns))
                        Add("MIR096",
                            $"built-in `{builtin.Name}` result is {builtinResult}, expected {function.Returns.ToString().ToLowerInvariant()}",
                            callable, block, call);
                    break;
                case MirQubitAllocate:
                    break;
                case MirQuantumApply apply:
                    for (var index = 0; index < apply.Functors.Count; index++)
                    {
                        if (!Enum.IsDefined(apply.Functors[index]))
                            Add(
                                "MIR140",
                                $"quantum apply carries unknown functor value {(int)apply.Functors[index]}",
                                callable,
                                block,
                                apply);
                    }
                    var adjointCount = apply.Functors.Count(
                        functor => functor == MirFunctor.Adjoint);
                    if (apply.Functors.Distinct().Count()
                            != apply.Functors.Count
                        || adjointCount == 1
                        && apply.Functors[0] != MirFunctor.Adjoint)
                    {
                        Add(
                            "MIR141",
                            "quantum functors are not canonical; each functor may appear once and Adjoint must precede Controlled",
                            callable,
                            block,
                            apply);
                    }
                    VerifyCall(callable, block, apply, apply.Target, apply.Operands, values, qubits,
                        expectFunction: false);
                    VerifyMutableResults(callable, block, apply, values);
                    VerifyQubitResults(callable, block, apply);
                    break;
                case MirMeasure measure:
                    if (TypeOf(measure.Result) is { } measureType
                        && measureType != MirType.Scalar(QType.Bit))
                        Add("MIR098", $"measurement result is {measureType}, expected bit",
                            callable, block, measure);
                    if (measure.QubitResult.Id != measure.Qubit.Qubit.Id)
                        Add("MIR098",
                            $"measurement reads {measure.Qubit.Qubit} but produces {measure.QubitResult.Key}",
                            callable, block, measure);
                    break;
                default:
                    Add("MIR099", $"unknown instruction kind {instruction.GetType().Name}",
                        callable, block, instruction);
                    break;
            }
        }

        private void VerifyCall(
            MirCallable caller,
            MirBlock block,
            MirInstruction instruction,
            MirCallTarget target,
            IReadOnlyList<MirCallOperand> operands,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits,
            bool expectFunction)
        {
            switch (target)
            {
                case MirUserCallableTarget user:
                    if (_program.FindCallable(user.Callable) is not { } callee)
                    {
                        Add("MIR100", $"call targets missing callable {user.Callable}",
                            caller, block, instruction);
                        return;
                    }
                    if ((callee.Kind == MirCallableKind.Function) != expectFunction)
                        Add("MIR101",
                            expectFunction
                                ? $"pure call targets operation `{callee.Name}`"
                                : $"quantum apply targets function `{callee.Name}`",
                            caller, block, instruction);
                    if (operands.Count != callee.Parameters.Count)
                    {
                        Add("MIR102",
                            $"call to `{callee.Name}` has {operands.Count} operand(s), expected {callee.Parameters.Count}",
                            caller, block, instruction);
                        return;
                    }
                    for (var index = 0; index < operands.Count; index++)
                        VerifyCallOperand(caller, block, instruction, index, operands[index],
                            callee, callee.Parameters[index], values, qubits);
                    break;

                case MirBuiltinFunctionTarget builtin when expectFunction:
                    if (!QoraGates.Functions.TryGetValue(builtin.Name, out var function))
                        Add("MIR103", $"unknown built-in function `{builtin.Name}`",
                            caller, block, instruction);
                    else if (operands.Count != 1)
                        Add("MIR104", $"built-in `{builtin.Name}` expects one operand",
                            caller, block, instruction);
                    else if (function.TakesBitRegister
                             && operands[0] is MirClassicalCallOperand classical
                             && values.TryGetValue(classical.Value, out var value)
                             && value.Type is not { ElementType: QType.Bit, IsArray: true })
                        Add("MIR105", $"built-in `{builtin.Name}` expects a whole bit array",
                            caller, block, instruction);
                    break;

                case MirBuiltinGateTarget builtin when !expectFunction:
                {
                    if (instruction is MirQuantumApply
                        {
                            Functors.Count: > 0
                        } modified
                        && QoraGates.Gates.TryGetValue(
                            builtin.Name,
                            out var gate)
                        && !gate.Unitary)
                    {
                        Add(
                            "MIR139",
                            $"non-unitary built-in gate `{builtin.Name}` cannot carry MIR functors",
                            caller,
                            block,
                            modified);
                    }
                    var extraControls = instruction is MirQuantumApply apply
                        ? apply.Functors.Count(functor => functor == MirFunctor.Controlled)
                        : 0;
                    var signature = QoraGates.SigOf(builtin.Name, extraControls);
                    if (signature is null)
                    {
                        Add("MIR106", $"unknown built-in gate `{builtin.Name}`",
                            caller, block, instruction);
                        return;
                    }
                    if (operands.Count != signature.Parameters.Count)
                    {
                        Add("MIR107",
                            $"built-in gate `{builtin.Name}` has {operands.Count} operand(s), expected {signature.Parameters.Count}",
                            caller, block, instruction);
                        return;
                    }
                    for (var index = 0; index < operands.Count; index++)
                    {
                        var expected = signature.Parameters[index];
                        if (expected.Type == QType.Qubit)
                        {
                            if (operands[index] is not MirQubitCallOperand)
                                Add("MIR108",
                                    $"built-in gate `{builtin.Name}` operand {index} must be a qubit",
                                    caller, block, instruction);
                        }
                        else if (operands[index] is not MirClassicalCallOperand classical
                                 || !values.TryGetValue(classical.Value, out var value)
                                 || value.Type != MirType.Scalar(expected.Type))
                            Add("MIR109",
                                $"built-in gate `{builtin.Name}` operand {index} must be {expected.Type.ToString().ToLowerInvariant()}",
                                caller, block, instruction);
                    }
                    break;
                }

                default:
                    Add("MIR110",
                        expectFunction
                            ? $"pure call uses non-function target `{target.DisplayName}`"
                            : $"quantum apply uses non-gate target `{target.DisplayName}`",
                        caller, block, instruction);
                    break;
            }
        }

        private void VerifyCallOperand(
            MirCallable caller,
            MirBlock block,
            MirInstruction instruction,
            int index,
            MirCallOperand actual,
            MirCallable callee,
            IMirParameter expected,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits)
        {
            switch (actual, expected)
            {
                case (MirClassicalCallOperand classical, MirClassicalParameter parameter):
                    if (values.TryGetValue(classical.Value, out var value)
                        && callee.FindValue(parameter.Value) is { } parameterValue
                        && !CallTypeCompatible(value.Type, parameterValue.Type))
                    {
                        Add(
                            "MIR111",
                            $"call operand {index} is {value.Type}, expected {parameterValue.Type}",
                            caller,
                            block,
                            instruction);
                    }
                    if (classical.Ownership != parameter.Ownership || classical.Access != parameter.Access)
                        Add("MIR112",
                            $"call operand {index} has {classical.Ownership}/{classical.Access}, expected {parameter.Ownership}/{parameter.Access}",
                            caller, block, instruction);
                    break;
                case (MirQubitCallOperand qubit, MirQubitParameter parameter):
                    if (QubitSeed(qubits, qubit.Qubit.Qubit.Id) is { } seed)
                    {
                        var (isArray, length) = QubitShape(seed);
                        var passesWholeArray = qubit.Qubit.Index is null && isArray;
                        if (parameter.IsArray != passesWholeArray)
                            Add("MIR113",
                                $"qubit operand {index} shape does not match parameter `{parameter.Name}`",
                                caller, block, instruction);
                        else if (parameter.IsArray
                                 && parameter.Length is int expectedLength
                                 && length != expectedLength)
                            Add("MIR113",
                                $"qubit operand {index} has length {length?.ToString() ?? "unknown"}, expected {expectedLength}",
                                caller, block, instruction);
                    }
                    if (qubit.Ownership != parameter.Ownership)
                        Add("MIR114",
                            $"qubit operand {index} has ownership {qubit.Ownership}, expected {parameter.Ownership}",
                            caller, block, instruction);
                    break;
                default:
                    Add("MIR115", $"call operand {index} kind does not match parameter `{expected.Name}`",
                        caller, block, instruction);
                    break;
            }
        }

        private static bool CallTypeCompatible(MirType actual, MirType expected)
        {
            if (actual == expected) return true;
            return actual.IsArray
                   && expected.IsArray
                   && actual.ElementType == expected.ElementType
                   && (expected.KnownLength is null || actual.KnownLength == expected.KnownLength);
        }

        private void VerifyMutableResults(
            MirCallable caller,
            MirBlock block,
            MirQuantumApply apply,
            IReadOnlyDictionary<MirValueId, MirValue> values)
        {
            var seenOperands = new HashSet<int>();
            foreach (var result in apply.MutableArrayResults)
            {
                if (!seenOperands.Add(result.OperandIndex))
                    Add("MIR116",
                        $"mutable array operand {result.OperandIndex} has more than one result",
                        caller, block, apply);
                if (result.OperandIndex < 0 || result.OperandIndex >= apply.Operands.Count)
                {
                    Add("MIR117",
                        $"mutable array result names missing operand {result.OperandIndex}",
                        caller, block, apply);
                    continue;
                }
                if (apply.Operands[result.OperandIndex] is not MirClassicalCallOperand
                    {
                        Access: QAccessMode.Mutable,
                        Ownership: QOwnershipMode.Borrowed
                    } operand)
                {
                    Add("MIR118",
                        $"mutable array result {result.Result} does not correspond to a borrowed mutable classical operand",
                        caller, block, apply);
                    continue;
                }
                if (values.TryGetValue(operand.Value, out var input)
                    && values.TryGetValue(result.Result, out var output)
                    && (!input.Type.IsArray || output.Type != input.Type))
                    Add("MIR119",
                        $"mutable result {result.Result} is {output.Type}, expected array state {input.Type}",
                        caller, block, apply);
            }

            if (apply.Target is not MirUserCallableTarget user
                || _program.FindCallable(user.Callable) is not { } callee)
            {
                if (apply.MutableArrayResults.Count != 0)
                    Add("MIR120",
                        "a built-in or unresolved quantum target cannot produce mutable array states",
                        caller, block, apply);
                return;
            }
            var expected = new HashSet<int>();
            for (var index = 0; index < callee.Parameters.Count; index++)
            {
                if (callee.Parameters[index] is not MirClassicalParameter
                    {
                        Ownership: QOwnershipMode.Borrowed,
                        Access: QAccessMode.Mutable
                    } parameter)
                {
                    continue;
                }

                if (callee.FindValue(parameter.Value) is { Type.IsArray: true })
                    expected.Add(index);
            }

            if (!expected.SetEquals(seenOperands))
                Add("MIR120",
                    $"mutable result operands [{string.Join(", ", seenOperands.Order())}] do not match callee contract [{string.Join(", ", expected.Order())}]",
                    caller, block, apply);
        }

        private void VerifyQubitResults(
            MirCallable caller,
            MirBlock block,
            MirQuantumApply apply)
        {
            var seenIds = new HashSet<MirQubitId>();
            var inputIds = apply.Operands
                .OfType<MirQubitCallOperand>()
                .Select(operand => operand.Qubit.Qubit.Id)
                .ToHashSet();

            foreach (var result in apply.QubitResults)
            {
                if (!seenIds.Add(result.Id))
                    Add("MIR142",
                        $"quantum apply writes qubit {result.Id} through more than one result",
                        caller, block, apply);
                if (!inputIds.Contains(result.Id))
                    Add("MIR142",
                        $"quantum apply produces {result.Key} without reading the same qubit identity",
                        caller, block, apply);
            }
        }

        private void VerifyQubitAccess(
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction,
            MirQubitAccess access,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits)
        {
            if (!qubits.ContainsKey(access.Qubit))
            {
                Add("MIR121", $"qubit access references missing version {access.Qubit}",
                    callable, block, instruction);
                return;
            }
            if (access.Index is MirValueId index)
            {
                var seed = QubitSeed(qubits, access.Qubit.Id);
                if (seed is not null && !QubitShape(seed).IsArray)
                    Add("MIR122", $"single qubit {access.Qubit.Id} is indexed",
                        callable, block, instruction);
                if (values.TryGetValue(index, out var value)
                    && value.Type != MirType.Scalar(QType.Int))
                    Add("MIR123", $"qubit index {index} is {value.Type}, expected int",
                        callable, block, instruction);
            }
        }

        private static MirQubit? QubitSeed(
            IReadOnlyDictionary<MirQubitKey, MirQubit> qubits,
            MirQubitId id) =>
            qubits.Values.FirstOrDefault(
                qubit => qubit.Id == id && qubit.Version.Value == 0);

        private static (bool IsArray, int? Length) QubitShape(MirQubit seed) =>
            seed switch
            {
                MirQubitParameter parameter => (parameter.IsArray, parameter.Length),
                MirQubitFromUse local => (local.IsArray, local.Length),
                _ => (false, null),
            };

        private void VerifyTerminator(
            MirCallable callable,
            MirBlock block,
            MirTerminator terminator,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values)
        {
            switch (terminator)
            {
                case MirBranch branch:
                    if (values.TryGetValue(branch.Condition, out var condition)
                        && condition.Type != MirType.Scalar(QType.Bit))
                        Add("MIR124",
                            $"branch condition {branch.Condition} is {condition.Type}, expected bit",
                            callable, block, source: branch.Origin);
                    break;
                case MirReturn ret:
                    if (callable.Kind == MirCallableKind.Operation)
                    {
                        if (ret.Value is not null)
                            Add("MIR125", "operation returns a value", callable, block, source: ret.Origin);
                    }
                    else if (ret.Value is not MirValueId returnValue)
                        Add("MIR126", "function return has no value", callable, block, source: ret.Origin);
                    else if (values.TryGetValue(returnValue, out var value)
                             && callable.ReturnType is { } expected
                             && value.Type != expected)
                        Add("MIR127",
                            $"function returns {value.Type}, expected {expected}",
                            callable, block, source: ret.Origin);
                    break;
                case MirJump:
                case MirUnreachable:
                    break;
                default:
                    Add("MIR128", $"unknown terminator kind {terminator.GetType().Name}",
                        callable, block, source: terminator.Origin);
                    break;
            }
        }

        private static MirInstruction? FindInstruction(MirBlock block, MirInstructionId id) =>
            block.Instructions.FirstOrDefault(instruction => instruction?.Id == id);

        private void CheckClassicalType(
            MirType type,
            string role,
            MirCallable callable,
            MirBlock? block = null)
        {
            if (type.ElementType == QType.Qubit)
                Add("MIR133", $"{role} uses qubit as a classical MIR type", callable, block);
        }

        private void RequireScalar(
            MirType? type,
            string role,
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction)
        {
            if (type is { IsArray: true } array)
                Add("MIR136", $"{role} is array {array}", callable, block, instruction);
        }

        private void RequireIntIndex(
            MirType? type,
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction)
        {
            if (type is { } actual && actual != MirType.Scalar(QType.Int))
                Add("MIR137", $"array index is {actual}, expected int", callable, block, instruction);
        }

        private void CheckOrigin(
            MirOrigin? origin,
            string role,
            MirCallable? callable = null,
            MirBlock? block = null,
            MirInstruction? instruction = null)
        {
            if (origin is null)
            {
                Add(
                    "MIR006",
                    $"{role} has no origin",
                    callable,
                    block,
                    instruction);
                return;
            }

            if (_hirSnapshot is null)
                return;

            var source = origin.SourceHirOrigin;
            if (!_hirSnapshot.Structure.Contains(source.HirNodeId))
            {
                Add(
                    "MIR006",
                    $"{role} refers to HIR node {source.HirNodeId} outside the exact HIR snapshot",
                    callable,
                    block,
                    instruction,
                    origin);
                return;
            }

            var expectedSpan = _hirSnapshot.SourceMap.Find(source.HirNodeId);
            if (source.Span != expectedSpan)
            {
                Add(
                    "MIR006",
                    $"{role} origin span {source.Span?.ToString() ?? "<none>"} does not match "
                    + $"HIR node {source.HirNodeId} span {expectedSpan?.ToString() ?? "<none>"}",
                    callable,
                    block,
                    instruction,
                    origin);
            }
        }

        private void Add(
            string code,
            string message,
            MirCallable? callable = null,
            MirBlock? block = null,
            MirInstruction? instruction = null,
            MirOrigin? source = null) =>
            _errors.Add(new MirVerificationError(
                code,
                message,
                callable?.Id,
                block?.Id,
                instruction?.Id,
                source ?? instruction?.Origin ?? block?.Origin ?? callable?.Origin));
    }
}
