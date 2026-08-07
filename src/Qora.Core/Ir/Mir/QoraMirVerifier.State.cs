using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

internal static partial class QoraMirVerifier
{
    private sealed partial class Verifier
    {
        private void VerifyQubitWriteContracts()
        {
            MirFormalQubitEffectQuery effects;
            try
            {
                var callGraph =
                    MirCallGraphAnalysis.AnalyzeVerified(_program);
                effects =
                    MirEffectAnalysis.CreateFormalQubitEffectQueryUnchecked(
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

                        AddAtInstruction(
                            "MIR142",
                            $"quantum apply results "
                            + $"[{string.Join(", ", actual.OrderBy(id => id.Value))}] "
                            + $"do not match semantic write set "
                            + $"[{string.Join(", ", expected.OrderBy(id => id.Value))}]",
                            caller,
                            apply);
                    }
                }
            }
        }

        /// <summary>
        /// Verifies contracts which require whole-CFG facts after the local structural and type checks
        /// have succeeded. Keeping this as a second phase avoids treating an incomplete graph as analyzable.
        /// </summary>
        private void VerifyGraphContracts(
            MirCallable callable,
            MirControlFlowSnapshot controlFlow)
        {
            var provenance = MirStorageProvenanceAnalysis.AnalyzeUnchecked(callable);
            var memory = MirMemoryStateAnalysis.AnalyzeVerified(
                controlFlow,
                provenance);
            VerifyExclusiveCallOperands(callable, provenance);
            VerifyArrayCallMinimumLengths(callable, provenance);
            VerifyCurrentArrayStates(callable, controlFlow, memory);
            VerifyCurrentQubitStates(callable, controlFlow);
        }

        /// <summary>
        /// Read-only formals may share a caller allocation. As soon as one slot can mutate or consume
        /// an array, however, every other actual in that call must be provably disjoint. Callee-local
        /// storage IDs alone cannot establish this because two actual SSA states may have the same
        /// caller provenance.
        /// </summary>
        private void VerifyExclusiveCallOperands(
            MirCallable callable,
            MirStorageProvenanceSnapshot provenance)
        {
            foreach (var block in callable.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    IReadOnlyList<MirCallOperand> operands;
                    switch (instruction)
                    {
                        case MirPureCall call:
                            operands = call.Operands;
                            break;
                        case MirQuantumApply apply:
                            operands = apply.Operands;
                            break;
                        default:
                            continue;
                    }

                    var arrays = new List<(MirClassicalCallOperand Operand, int Index)>();
                    for (var operandIndex = 0; operandIndex < operands.Count; operandIndex++)
                    {
                        if (operands[operandIndex] is MirClassicalCallOperand classical
                            && callable.RequireValue(classical.Value).Type.IsArray)
                        {
                            arrays.Add((classical, operandIndex));
                        }
                    }

                    for (var leftIndex = 0; leftIndex < arrays.Count; leftIndex++)
                    {
                        for (var rightIndex = leftIndex + 1; rightIndex < arrays.Count; rightIndex++)
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

                            AddAtInstruction(
                                "MIR142",
                                $"array call operands {left.Index} and {right.Index} may alias, but at least one operand is mutable or moved",
                                callable,
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
            MirStorageProvenanceSnapshot provenance)
        {
            foreach (var block in caller.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    switch (instruction)
                    {
                        case MirPureCall
                        {
                            Target: MirUserCallableTarget,
                        } call:
                            VerifyUserCall(call);
                            break;

                        case MirQuantumApply
                        {
                            Target: MirUserCallableTarget,
                        } apply:
                            VerifyUserCall(apply);
                            break;
                    }
                }
            }

            void VerifyUserCall(MirInstruction instruction)
            {
                var (target, operands) = instruction switch
                {
                    MirPureCall
                    {
                        Target: MirUserCallableTarget user,
                    } call => (user, call.Operands),
                    MirQuantumApply
                    {
                        Target: MirUserCallableTarget user,
                    } apply => (user, apply.Operands),
                    _ => throw new ArgumentException(
                        $"{instruction.GetType().Name} is not a user-call instruction",
                        nameof(instruction)),
                };
                var callee = _program.RequireCallable(target.Callable);
                for (var index = 0; index < operands.Count; index++)
                {
                    if (callee.Parameters[index] is not MirClassicalParameter
                        {
                            MinimumLength: > 0,
                        } expected)
                    {
                        continue;
                    }

                    var actual = (MirClassicalCallOperand)operands[index];
                    var actualValue = caller.RequireValue(actual.Value);
                    var actualMinimumLength = GuaranteedMinimumArrayLength(
                        caller,
                        actualValue,
                        provenance);
                    if (actualMinimumLength >= expected.MinimumLength)
                        continue;

                    AddAtInstruction(
                        "MIR152",
                        $"array call operand {index} guarantees length at least {actualMinimumLength}, "
                        + $"but parameter `{expected.Name}` requires at least {expected.MinimumLength}",
                        caller,
                        instruction);
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
            MirControlFlowSnapshot cfg,
            MirMemoryStateSnapshot memory)
        {
            foreach (var block in callable.Blocks)
            {
                if (!cfg.IsReachable(block.Id))
                    continue;

                foreach (var instruction in block.Instructions)
                {
                    foreach (var input in instruction.InputValues.Distinct())
                    {
                        if (!callable.RequireValue(input).Type.IsArray)
                            continue;
                        var availability = memory.CheckBeforeInstruction(
                            input,
                            instruction.Id);
                        if (!IsInvalidMemoryState(availability))
                            continue;
                        AddAtInstruction(
                            "MIR141",
                            $"array state {input} is not the current memory version before {instruction.Id} ({availability.Kind})",
                            callable,
                            instruction);
                    }
                }

                foreach (var edge in OutgoingEdges(block.Terminator))
                {
                    var target = callable.RequireBlock(edge.Target);
                    for (var index = 0; index < target.Arguments.Count; index++)
                    {
                        var targetArgument = target.Arguments[index];
                        if (!targetArgument.Type.IsArray)
                            continue;
                        var incoming = edge.Arguments[index];
                        var availability = memory.CheckAtTerminator(
                            incoming,
                            block.Id);
                        if (!IsInvalidMemoryState(availability))
                            continue;
                        AddAtTerminator(
                            "MIR141",
                            $"edge {block.Id} -> {target.Id} passes stale array state {incoming} into memory Phi argument {index} ({availability.Kind})",
                            callable,
                            block);
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
            MirControlFlowSnapshot cfg)
        {
            var parameterEntryState = callable.Parameters
                .OfType<MirQubitParameter>()
                .ToDictionary(
                    parameter => parameter.Id,
                    parameter => (MirQubitKey?)parameter.Key);
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
                if (!cfg.IsReachable(blockId))
                    continue;
                var block = callable.RequireBlock(blockId);

                Dictionary<MirQubitId, MirQubitKey?> entry;
                if (blockId == callable.EntryBlock.Id)
                {
                    entry = new Dictionary<MirQubitId, MirQubitKey?>(parameterEntryState);
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

                foreach (var phi in block.QubitPhis)
                    entry[phi.Id] = phi.Key;

                entryStates[blockId] = entry;
                var exit = new Dictionary<MirQubitId, MirQubitKey?>(entry);
                foreach (var instruction in block.Instructions)
                    ApplyQubitResults(exit, instruction);

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
                if (!cfg.IsReachable(block.Id))
                    continue;

                var blockEntry = entryStates[block.Id];

                var predecessorStates = cfg.PredecessorsOf(block.Id)
                    .Where(cfg.IsReachable)
                    .Select(predecessor => exitStates[predecessor])
                    .ToArray();
                if (predecessorStates.Length > 1)
                {
                    var unnormalized = Merge(predecessorStates)
                        .Where(pair => pair.Value is null)
                        .Select(pair => pair.Key);
                    var uniquePhis = block.QubitPhis
                        .Select(phi => phi.Id)
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

                foreach (var phi in block.QubitPhis)
                {
                    foreach (var input in phi.Inputs)
                    {
                        if (!cfg.IsReachable(input.Edge.Source))
                            continue;

                        var predecessorState = exitStates[input.Edge.Source];

                        var error = CurrentStateError(
                            predecessorState,
                            input.Qubit);
                        if (error is not null)
                        {
                            AddAtQubitPhi(
                                "MIR146",
                                $"qubit Phi {phi.Key} input on edge "
                                + $"{input.Edge.Source}:{input.Edge.SuccessorOrdinal} -> "
                                + $"{block.Id} {error}",
                                callable,
                                block,
                                phi);
                        }
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
                            AddAtInstruction(
                                "MIR146",
                                $"instruction {instruction.Id} accesses qubit {group.Key} "
                                + "through multiple input versions "
                                + $"[{string.Join(", ", versions)}]",
                                callable,
                                instruction);
                            continue;
                        }

                        var error = CurrentStateError(current, versions[0]);
                        if (error is not null)
                        {
                            AddAtInstruction(
                                "MIR146",
                                $"instruction {instruction.Id} {error}",
                                callable,
                                instruction);
                        }
                    }

                    ApplyQubitResults(current, instruction);
                }
            }

            bool RequiresCurrentStateAtEntry(
                MirQubitId id,
                MirBlockId block)
            {
                if (parameterEntryState.ContainsKey(id))
                    return true;
                return localSeedBlocks.TryGetValue(id, out var seedBlock)
                    && seedBlock != block
                    && cfg.Dominates(seedBlock, block);
            }

            static string? CurrentStateError(
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> state,
                MirQubitKey actual)
            {
                if (!state.TryGetValue(actual.Id, out var expected))
                {
                    return $"accesses {actual}, but qubit {actual.Id} has no current state on this path";
                }

                if (expected is not MirQubitKey current)
                {
                    return $"accesses {actual}, but incoming paths have no unique current "
                        + $"version of qubit {actual.Id}";
                }

                if (current != actual)
                    return $"accesses stale version {actual}; current version is {current}";

                return null;
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

            static void ApplyQubitResults(
                IDictionary<MirQubitId, MirQubitKey?> state,
                MirInstruction instruction)
            {
                foreach (var result in instruction.QubitResults)
                    state[result.Id] = result.Key;
            }

            static bool SameState(
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> left,
                IReadOnlyDictionary<MirQubitId, MirQubitKey?> right) =>
                left.Count == right.Count
                && left.All(pair =>
                    right.TryGetValue(pair.Key, out var value)
                    && value == pair.Value);
        }

        private void VerifyQubitSeeds(MirCallable callable)
        {
            foreach (var group in callable.Qubits.GroupBy(qubit => qubit.Id))
            {
                if (!group.Any(qubit => qubit.Version.Value == 0))
                {
                    Add("MIR060",
                        $"qubit {group.Key} has no initial version-zero definition",
                        callable);
                }
            }
        }

        private void VerifyQubitFlow(
            MirCallable callable,
            MirControlFlowSnapshot controlFlow)
        {
            var definitions =
                new Dictionary<MirQubitKey, (MirBlockId? Block, int? InstructionIndex)>();
            foreach (var parameter in callable.Parameters.OfType<MirQubitParameter>())
                definitions.Add(parameter.Key, (null, null));
            foreach (var block in callable.Blocks)
            {
                foreach (var phi in block.QubitPhis)
                    definitions.Add(phi.Key, (block.Id, null));

                for (var instructionIndex = 0;
                     instructionIndex < block.Instructions.Count;
                     instructionIndex++)
                {
                    var instruction = block.Instructions[instructionIndex];
                    foreach (var result in instruction.QubitResults)
                        definitions.Add(result.Key, (block.Id, instructionIndex));
                }
            }

            var incomingEdges = callable.Blocks.ToDictionary(
                block => block.Id,
                _ => new HashSet<MirControlFlowEdge>());
            foreach (var source in callable.Blocks)
            {
                foreach (var (edge, target) in OutgoingQubitEdges(source))
                    if (incomingEdges.TryGetValue(target, out var incoming))
                        incoming.Add(edge);
            }

            foreach (var block in callable.Blocks)
            {
                foreach (var phi in block.QubitPhis)
                {
                    var seenEdges = new HashSet<MirControlFlowEdge>();
                    foreach (var input in phi.Inputs)
                    {
                        seenEdges.Add(input.Edge);
                        if (!incomingEdges[block.Id].Contains(input.Edge))
                        {
                            AddAtQubitPhi(
                                "MIR144",
                                $"qubit Phi {phi.Key} references a nonincoming edge {input.Edge}",
                                callable,
                                block,
                                phi);
                        }
                        if (!callable.ContainsQubit(input.Qubit))
                        {
                            AddAtQubitPhi(
                                "MIR144",
                                $"qubit Phi {phi.Key} receives missing version {input.Qubit}",
                                callable,
                                block,
                                phi);
                            continue;
                        }
                        if (callable.FindBlock(input.Edge.Source) is { } source)
                        {
                            var error = QubitAvailabilityErrorAtBlockEnd(
                                input.Qubit,
                                source);
                            if (error is not null)
                            {
                                AddAtQubitPhi(
                                    "MIR145",
                                    $"Phi {phi.Key} input {error}",
                                    callable,
                                    block,
                                    phi);
                            }
                        }
                    }

                    if (!seenEdges.SetEquals(incomingEdges[block.Id]))
                    {
                        AddAtQubitPhi(
                            "MIR144",
                            $"qubit Phi {phi.Key} covers [{string.Join(", ", seenEdges)}], expected every incoming edge [{string.Join(", ", incomingEdges[block.Id])}]",
                            callable,
                            block,
                            phi);
                    }
                }

                for (var instructionIndex = 0;
                     instructionIndex < block.Instructions.Count;
                     instructionIndex++)
                {
                    var instruction = block.Instructions[instructionIndex];
                    foreach (var access in instruction.QubitAccesses)
                    {
                        if (callable.ContainsQubit(access.Qubit))
                        {
                            var error = QubitAvailabilityError(
                                access.Qubit,
                                block.Id,
                                instructionIndex);
                            if (error is not null)
                            {
                                AddAtInstruction(
                                    "MIR145",
                                    $"instruction {instruction.Id} {error}",
                                    callable,
                                    instruction);
                            }
                        }
                    }
                }
            }

            string? QubitAvailabilityErrorAtBlockEnd(
                MirQubitKey qubit,
                MirBlock block) =>
                QubitAvailabilityError(
                    qubit,
                    block.Id,
                    block.Instructions.Count);

            string? QubitAvailabilityError(
                MirQubitKey qubit,
                MirBlockId useBlock,
                int useIndex)
            {
                var definition = definitions[qubit];

                if (definition.Block is not MirBlockId definitionBlock)
                    return null; // Parameter versions are available at callable entry.

                if (definitionBlock == useBlock)
                {
                    if (definition.InstructionIndex is int definitionIndex
                        && definitionIndex >= useIndex)
                        return $"uses qubit {qubit} before its defining instruction";
                    return null;
                }

                if (!controlFlow.Dominates(definitionBlock, useBlock))
                {
                    return $"uses qubit {qubit}, defined in {definitionBlock}, whose definition "
                        + $"does not dominate {useBlock}";
                }

                return null;
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

    }
}
