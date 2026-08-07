using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

internal static partial class QoraMirVerifier
{
    private sealed partial class Verifier
    {
        private void VerifyInstructionUses(
            MirCallable callable,
            MirInstruction instruction,
            MirControlFlowSnapshot controlFlow)
        {
            var location = callable.RequireInstructionLocation(instruction.Id);
            foreach (var operand in instruction.InputValues.Distinct())
            {
                var error = ValueUseError(
                    callable,
                    operand,
                    location.Block.Id,
                    location.Index,
                    controlFlow);
                if (error is not null)
                    AddAtInstruction(error.Value.Code, error.Value.Message, callable, instruction);
            }
        }

        private void VerifyTerminatorUses(
            MirCallable callable,
            MirBlock useBlock,
            MirControlFlowSnapshot controlFlow)
        {
            foreach (var operand in useBlock.Terminator.InputValues.Distinct())
            {
                var error = ValueUseError(
                    callable,
                    operand,
                    useBlock.Id,
                    useBlock.Instructions.Count,
                    controlFlow);
                if (error is not null)
                    AddAtTerminator(error.Value.Code, error.Value.Message, callable, useBlock);
            }
        }

        private static (string Code, string Message)? ValueUseError(
            MirCallable callable,
            MirValueId operand,
            MirBlockId useBlock,
            int useIndex,
            MirControlFlowSnapshot controlFlow)
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

            return !controlFlow.Dominates(definitionBlock, useBlock)
                ? ("MIR072", $"{operand}, defined in {definitionBlock}, does not dominate its use in {useBlock}")
                : null;
        }
    }
}
