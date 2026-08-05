namespace Qora.Ir.Mir;

internal static partial class QoraMirVerifier
{
    private sealed partial class Verifier
    {
        private static Dictionary<MirBlockId, HashSet<MirBlockId>> BuildPredecessors(
            MirCallable callable)
        {
            var predecessors = callable.Blocks.ToDictionary(
                block => block.Id,
                _ => new HashSet<MirBlockId>());

            foreach (var block in callable.Blocks)
            {
                foreach (var successor in block.Terminator.Successors)
                    predecessors[successor].Add(block.Id);
            }

            return predecessors;
        }

        private static Dictionary<MirBlockId, HashSet<MirBlockId>> ComputeDominators(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> predecessors)
        {
            var entry = callable.EntryBlock.Id;
            var reachable = new HashSet<MirBlockId>();
            var pending = new Stack<MirBlockId>();
            pending.Push(entry);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!reachable.Add(current)) continue;
                var terminator = callable.RequireBlock(current).Terminator;
                foreach (var successor in terminator.Successors)
                    pending.Push(successor);
            }

            var dominators = new Dictionary<MirBlockId, HashSet<MirBlockId>>();
            foreach (var block in callable.Blocks)
                dominators[block.Id] = block.Id == entry
                    ? new HashSet<MirBlockId> { entry }
                    : reachable.Contains(block.Id)
                        ? new HashSet<MirBlockId>(reachable)
                        : new HashSet<MirBlockId> { block.Id };

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

        private void VerifyInstructionUses(
            MirCallable callable,
            MirInstruction instruction,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators)
        {
            var location = callable.RequireInstructionLocation(instruction.Id);
            foreach (var operand in instruction.InputValues.Distinct())
            {
                var error = ValueUseError(
                    callable,
                    operand,
                    location.Block.Id,
                    location.Index,
                    dominators);
                if (error is not null)
                    AddAtInstruction(error.Value.Code, error.Value.Message, callable, instruction);
            }
        }

        private void VerifyTerminatorUses(
            MirCallable callable,
            MirBlock useBlock,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators)
        {
            foreach (var operand in useBlock.Terminator.InputValues.Distinct())
            {
                var error = ValueUseError(
                    callable,
                    operand,
                    useBlock.Id,
                    useBlock.Instructions.Count,
                    dominators);
                if (error is not null)
                    AddAtTerminator(error.Value.Code, error.Value.Message, callable, useBlock);
            }
        }

        private static (string Code, string Message)? ValueUseError(
            MirCallable callable,
            MirValueId operand,
            MirBlockId useBlock,
            int useIndex,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators)
        {
            if (!callable.ContainsValue(operand))
            {
                return (
                    "MIR070",
                    $"operand references missing SSA value {operand}");
            }

            var definition = callable.DefinitionOf(operand);
            if (definition.Kind == MirValueDefinitionKind.Parameter)
                return null;

            MirBlockId definitionBlock;
            int? definitionIndex;
            if (definition.Kind == MirValueDefinitionKind.BlockArgument)
            {
                definitionBlock = definition.Block!.Value;
                definitionIndex = null;
            }
            else
            {
                var definitionInstruction = definition.Instruction!.Value;
                var location = callable.RequireInstructionLocation(definitionInstruction);
                definitionBlock = location.Block.Id;
                definitionIndex = location.Index;
            }

            if (definitionBlock == useBlock)
            {
                return definitionIndex is int index && index >= useIndex
                    ? ("MIR071", $"{operand} is used before its defining instruction in {useBlock}")
                    : null;
            }

            return !dominators[useBlock].Contains(definitionBlock)
                ? ("MIR072", $"{operand}, defined in {definitionBlock}, does not dominate its use in {useBlock}")
                : null;
        }
    }
}
