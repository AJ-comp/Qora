using System.Text;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

public sealed record MirVerificationError(
    string Code,
    string Message,
    MirCallableId? Callable = null,
    MirBlockId? Block = null,
    MirInstructionId? Instruction = null,
    MirSource? Source = null)
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
/// Always-on structural verification for Qora MIR. It verifies identity uniqueness, SSA definition/use
/// integrity, CFG edge contracts, dominance, instruction typing, call contracts, and classical-array /
/// qubit resource references. A failed verification is a compiler defect, not a source-language error.
/// </summary>
public static class QoraMirVerifier
{
    public static IReadOnlyList<MirVerificationError> Verify(MirProgram? program)
    {
        if (program is null)
            return new[]
            {
                new MirVerificationError("MIR000", "the MIR program is null"),
            };

        return new Verifier(program).Run();
    }

    public static void VerifyOrThrow(MirProgram program)
    {
        var errors = Verify(program);
        if (errors.Count == 0) return;

        var message = new StringBuilder("QINTERNAL: invalid Qora MIR");
        foreach (var error in errors) message.AppendLine().Append("  ").Append(error);
        throw new InvalidOperationException(message.ToString());
    }

    private sealed class Verifier
    {
        private readonly MirProgram _program;
        private readonly List<MirVerificationError> _errors = new();
        private readonly Dictionary<MirCallableId, MirCallable> _callables = new();

        public Verifier(MirProgram program)
        {
            _program = program;
        }

        public IReadOnlyList<MirVerificationError> Run()
        {
            if (_program.Revision < 0)
                Add("MIR001", $"program revision {_program.Revision} is negative");

            if (_program.Callables is null)
            {
                Add("MIR002", "program callable collection is null");
                return _errors;
            }

            foreach (var callable in _program.Callables)
            {
                if (callable is null)
                {
                    Add("MIR002", "program contains a null callable");
                    continue;
                }

                if (callable.Id.Value < 0)
                    Add("MIR003", $"callable `{callable.Name}` has negative identity {callable.Id}", callable);
                if (!_callables.TryAdd(callable.Id, callable))
                    Add("MIR004", $"callable identity {callable.Id} is declared more than once", callable);
            }

            foreach (var callable in _callables.Values.OrderBy(callable => callable.Id.Value))
                VerifyCallable(callable);

            return _errors;
        }

        private void VerifyCallable(MirCallable callable)
        {
            var callableErrorStart = _errors.Count;
            if (string.IsNullOrWhiteSpace(callable.Name))
                Add("MIR005", "callable name is empty", callable);

            if (callable.SourceOperationId != callable.Source.SourceOperationId)
                Add("MIR006",
                    $"callable source operation {callable.SourceOperationId} disagrees with origin {callable.Source.SourceOperationId}",
                    callable);

            if (callable.Kind == MirCallableKind.Function)
            {
                if (callable.ReturnType is not { } returnType)
                    Add("MIR007", "function has no return type", callable);
                else
                    CheckClassicalType(returnType, "function return type", callable);
            }
            else if (callable.ReturnType is not null)
                Add("MIR008", "operation carries a non-void return type", callable);

            var blocks = Unique(
                callable.Blocks,
                block => block.Id,
                "MIR010",
                "block",
                callable);
            var values = Unique(
                callable.Values,
                value => value.Id,
                "MIR011",
                "SSA value",
                callable);
            var storages = Unique(
                callable.Storages,
                storage => storage.Id,
                "MIR012",
                "array storage",
                callable);
            var qubits = Unique(
                callable.Qubits,
                qubit => qubit.Id,
                "MIR013",
                "qubit resource",
                callable);

            if (!blocks.ContainsKey(callable.EntryBlock))
                Add("MIR014", $"entry block {callable.EntryBlock} does not exist", callable);

            foreach (var value in values.Values)
            {
                CheckClassicalType(value.Type, $"type of {value.Id}", callable);
                CheckDefinitionShape(value, callable);
            }

            var instructionLocations = new Dictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)>();
            foreach (var block in blocks.Values)
            {
                if (block.Id.Value < 0)
                    Add("MIR015", $"block has negative identity {block.Id}", callable, block);
                if (block.Terminator is null)
                {
                    Add("MIR016", "block has no terminator", callable, block);
                    continue;
                }

                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    var instruction = block.Instructions[index];
                    if (instruction is null)
                    {
                        Add("MIR017", $"instruction slot {index} is null", callable, block);
                        continue;
                    }
                    if (instruction.Id.Value < 0)
                        Add("MIR018", $"instruction has negative identity {instruction.Id}", callable, block, instruction);
                    if (!instructionLocations.TryAdd(instruction.Id, (block, index, instruction)))
                        Add("MIR019", $"instruction identity {instruction.Id} is used more than once",
                            callable, block, instruction);
                }
            }

            VerifyParameters(callable, values, storages, qubits);
            VerifyBlockArguments(callable, blocks, values);
            VerifyInstructionResults(callable, blocks, values, instructionLocations);
            VerifyStorageDefinitions(callable, storages, instructionLocations);
            VerifyQubitDefinitions(callable, qubits, instructionLocations);

            var predecessors = VerifyControlFlow(callable, blocks, values);
            var dominators = ComputeDominators(callable.EntryBlock, blocks, predecessors);

            foreach (var block in blocks.Values)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    if (block.Instructions[index] is not { } instruction) continue;
                    VerifyUses(callable, block, index, instruction.InputValues, values, instructionLocations, dominators,
                        instruction.Id, instruction.Source);
                    VerifyInstruction(callable, block, instruction, values, storages, qubits);
                }

                if (block.Terminator is not { } terminator) continue;
                VerifyUses(callable, block, block.Instructions.Count, terminator.InputValues, values,
                    instructionLocations, dominators, instruction: null, terminator.Source);
                VerifyTerminator(callable, block, terminator, blocks, values);
            }

            if (_errors.Count == callableErrorStart)
                VerifyGraphContracts(callable, blocks, values);
        }

        /// <summary>
        /// Verifies contracts which require whole-CFG facts after the local structural and type checks
        /// have succeeded. Keeping this as a second phase avoids treating an incomplete graph as analyzable
        /// and avoids verifier-analysis recursion.
        /// </summary>
        private void VerifyGraphContracts(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, MirBlock> blocks,
            IReadOnlyDictionary<MirValueId, MirValue> values)
        {
            var cfg = MirControlFlowAnalysis.AnalyzeUnchecked(_program, callable);
            var provenance = MirStorageProvenanceAnalysis.AnalyzeUnchecked(_program, callable);
            VerifyExclusiveCallOperands(callable, values, provenance);
            VerifyCurrentArrayStates(callable, blocks, values, cfg);
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
                        if (!target.Arguments[index].Type.IsArray)
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
                            source: block.Terminator.Source);
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

        private void VerifyParameters(
            MirCallable callable,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirStorageId, MirArrayStorage> storages,
            IReadOnlyDictionary<MirQubitResourceId, MirQubitResource> qubits)
        {
            if (callable.Parameters is null)
            {
                Add("MIR020", "parameter collection is null", callable);
                return;
            }

            for (var index = 0; index < callable.Parameters.Count; index++)
            {
                var parameter = callable.Parameters[index];
                switch (parameter)
                {
                    case MirClassicalParameter classical:
                    {
                        CheckClassicalType(classical.Type, $"parameter `{classical.Name}`", callable);
                        if (!values.TryGetValue(classical.Value, out var value))
                            Add("MIR021", $"classical parameter `{classical.Name}` references missing value {classical.Value}",
                                callable);
                        else
                        {
                            if (value.Type != classical.Type)
                                Add("MIR022",
                                    $"parameter `{classical.Name}` declares {classical.Type}, but {classical.Value} is {value.Type}",
                                    callable);
                            if (value.Definition is not
                                {
                                    Kind: MirValueDefinitionKind.Parameter,
                                    Index: var definitionIndex,
                                    Block: null,
                                    Instruction: null
                                }
                                || definitionIndex != index)
                                Add("MIR023",
                                    $"{classical.Value} is not defined by parameter slot {index}",
                                    callable);
                        }

                        if (classical.Type.IsArray)
                        {
                            if (classical.Storage is not MirStorageId storageId)
                                Add("MIR024", $"array parameter `{classical.Name}` has no storage identity", callable);
                            else if (!storages.TryGetValue(storageId, out var storage))
                                Add("MIR025",
                                    $"array parameter `{classical.Name}` references missing storage {storageId}",
                                    callable);
                            else if (storage.Kind != MirArrayStorageKind.Parameter
                                     || storage.ParameterIndex != index
                                     || storage.Type != classical.Type)
                                Add("MIR026",
                                    $"storage {storageId} does not describe array parameter slot {index} with type {classical.Type}",
                                    callable);
                        }
                        else if (classical.Storage is not null)
                            Add("MIR027", $"scalar parameter `{classical.Name}` carries an array storage identity", callable);

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
                        if (!qubits.TryGetValue(qubit.Resource, out var resource))
                            Add("MIR030",
                                $"qubit parameter `{qubit.Name}` references missing resource {qubit.Resource}",
                                callable);
                        else if (resource.Kind != MirQubitResourceKind.Parameter
                                 || resource.IsArray != qubit.IsArray
                                 || resource.Length != qubit.Length
                                 || resource.AllocationInstruction is not null)
                            Add("MIR031",
                                $"resource {qubit.Resource} does not match qubit parameter `{qubit.Name}`",
                                callable);
                        if (!qubit.IsArray && qubit.Length is not null)
                            Add("MIR032", $"single-qubit parameter `{qubit.Name}` carries an array length", callable);
                        if (qubit.Length is < 1)
                            Add("MIR033", $"qubit parameter `{qubit.Name}` has invalid length {qubit.Length}", callable);
                        break;
                    }

                    case null:
                        Add("MIR034", $"parameter slot {index} is null", callable);
                        break;

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
                    if (!seen.Add(argument.Value))
                        Add("MIR040", $"block argument value {argument.Value} appears more than once",
                            callable, block);
                    CheckClassicalType(argument.Type, $"block argument {argument.Value}", callable, block);
                    if (!values.TryGetValue(argument.Value, out var value))
                    {
                        Add("MIR041", $"block argument references missing value {argument.Value}", callable, block);
                        continue;
                    }
                    if (value.Type != argument.Type)
                        Add("MIR042",
                            $"block argument {argument.Value} declares {argument.Type}, but its value table entry is {value.Type}",
                            callable, block);
                    if (value.Definition is not
                        {
                            Kind: MirValueDefinitionKind.BlockArgument,
                            Index: var definitionIndex,
                            Block: var definitionBlock,
                            Instruction: null
                        }
                        || definitionIndex != index || definitionBlock != block.Id)
                        Add("MIR043",
                            $"{argument.Value} is not defined by argument {index} of {block.Id}",
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
                            Block: var definitionBlock,
                            Instruction: var definitionInstruction
                        }
                        || definitionIndex != index
                        || definitionBlock != location.Block.Id
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
                        if (value.Definition.Index < 0 || value.Definition.Index >= callable.Parameters.Count)
                            Add("MIR047", $"{value.Id} names missing parameter slot {value.Definition.Index}", callable);
                        break;
                    case MirValueDefinitionKind.BlockArgument:
                        if (value.Definition.Block is not MirBlockId blockId
                            || !blocks.TryGetValue(blockId, out var block)
                            || value.Definition.Index < 0
                            || value.Definition.Index >= block.Arguments.Count
                            || block.Arguments[value.Definition.Index].Value != value.Id)
                            Add("MIR048", $"{value.Id} has a dangling block-argument definition", callable);
                        break;
                    case MirValueDefinitionKind.InstructionResult:
                        if (value.Definition.Instruction is not MirInstructionId instructionId
                            || !instructions.TryGetValue(instructionId, out var location)
                            || value.Definition.Block != location.Block.Id
                            || value.Definition.Index < 0
                            || value.Definition.Index >= location.Instruction.ResultValues.Count
                            || location.Instruction.ResultValues[value.Definition.Index] != value.Id)
                            Add("MIR049", $"{value.Id} has a dangling instruction-result definition", callable);
                        break;
                    default:
                        Add("MIR050", $"{value.Id} has unknown definition kind {value.Definition.Kind}", callable);
                        break;
                }
            }
        }

        private void VerifyStorageDefinitions(
            MirCallable callable,
            IReadOnlyDictionary<MirStorageId, MirArrayStorage> storages,
            IReadOnlyDictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)> instructions)
        {
            foreach (var storage in storages.Values)
            {
                if (storage.Id.Value < 0)
                    Add("MIR051", $"array storage has negative identity {storage.Id}", callable);
                CheckClassicalType(storage.Type, $"array storage {storage.Id}", callable);
                if (!storage.Type.IsArray)
                    Add("MIR052", $"array storage {storage.Id} has scalar type {storage.Type}", callable);

                if (storage.Kind == MirArrayStorageKind.Parameter)
                {
                    if (storage.ParameterIndex is not int parameterIndex
                        || parameterIndex < 0
                        || parameterIndex >= callable.Parameters.Count)
                        Add("MIR053", $"parameter storage {storage.Id} has no valid parameter slot", callable);
                    else if (callable.Parameters[parameterIndex] is not MirClassicalParameter
                             {
                                 Type.IsArray: true,
                                 Storage: var parameterStorage
                             } parameter
                             || parameterStorage != storage.Id
                             || parameter.Type != storage.Type)
                        Add("MIR053",
                            $"parameter storage {storage.Id} is not owned by array parameter slot {parameterIndex}",
                            callable);
                    if (storage.AllocationInstruction is not null)
                        Add("MIR054", $"parameter storage {storage.Id} has an allocation instruction", callable);

                    if (storage.ParameterIndex is int aliasParameterIndex
                        && aliasParameterIndex >= 0
                        && aliasParameterIndex < callable.Parameters.Count
                        && callable.Parameters[aliasParameterIndex] is MirClassicalParameter aliasParameter)
                    {
                        var expectedAliasMode =
                            aliasParameter.Ownership == QOwnershipMode.Borrowed
                            && aliasParameter.Access == QAccessMode.ReadOnly
                                ? MirStorageAliasMode.SharedParameter
                                : MirStorageAliasMode.ExclusiveParameter;
                        if (storage.AliasMode != expectedAliasMode)
                            Add("MIR138",
                                $"parameter storage {storage.Id} has alias mode {storage.AliasMode}, expected {expectedAliasMode} for {aliasParameter.Ownership}/{aliasParameter.Access}",
                                callable);
                    }
                }
                else
                {
                    if (storage.ParameterIndex is not null)
                        Add("MIR055", $"local storage {storage.Id} carries a parameter slot", callable);
                    if (storage.AllocationInstruction is not MirInstructionId instructionId
                        || !instructions.TryGetValue(instructionId, out var location)
                         || location.Instruction is not MirArrayCreate create
                         || create.Storage != storage.Id)
                        Add("MIR056", $"local storage {storage.Id} has no matching array-create instruction", callable);
                    if (storage.AliasMode != MirStorageAliasMode.UniqueLocal)
                        Add("MIR138",
                            $"local storage {storage.Id} has alias mode {storage.AliasMode}, expected {MirStorageAliasMode.UniqueLocal}",
                            callable);
                }
            }
        }

        private void VerifyQubitDefinitions(
            MirCallable callable,
            IReadOnlyDictionary<MirQubitResourceId, MirQubitResource> qubits,
            IReadOnlyDictionary<MirInstructionId, (MirBlock Block, int Index, MirInstruction Instruction)> instructions)
        {
            foreach (var qubit in qubits.Values)
            {
                if (qubit.Id.Value < 0)
                    Add("MIR057", $"qubit resource has negative identity {qubit.Id}", callable);
                if (!qubit.IsArray && qubit.Length is not null)
                    Add("MIR058", $"single-qubit resource {qubit.Id} carries an array length", callable);
                if (qubit.Length is < 1)
                    Add("MIR059", $"qubit resource {qubit.Id} has invalid length {qubit.Length}", callable);

                if (qubit.Kind == MirQubitResourceKind.Parameter)
                {
                    if (qubit.AllocationInstruction is not null)
                        Add("MIR060", $"qubit parameter resource {qubit.Id} has an allocation instruction", callable);
                    if (callable.Parameters.Count(parameter =>
                            parameter is MirQubitParameter qubitParameter
                            && qubitParameter.Resource == qubit.Id) != 1)
                        Add("MIR060",
                            $"qubit parameter resource {qubit.Id} is not owned by exactly one parameter slot",
                            callable);
                }
                else if (qubit.AllocationInstruction is not MirInstructionId instructionId
                         || !instructions.TryGetValue(instructionId, out var location)
                         || location.Instruction is not MirQubitAllocate allocate
                         || allocate.Resource != qubit.Id)
                    Add("MIR061", $"local qubit resource {qubit.Id} has no matching allocation instruction", callable);
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
                            source: terminator.Source);
                        continue;
                    }
                    predecessors[successor].Add(block.Id);
                }

                switch (terminator)
                {
                    case MirJump jump:
                        VerifyEdge(callable, block, jump.Target, jump.Arguments, blocks, values, "jump", jump.Source);
                        break;
                    case MirBranch branch:
                        VerifyEdge(callable, block, branch.TrueTarget, branch.TrueArguments, blocks, values,
                            "true edge", branch.Source);
                        VerifyEdge(callable, block, branch.FalseTarget, branch.FalseArguments, blocks, values,
                            "false edge", branch.Source);
                        break;
                }
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
            MirSource source)
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
                if (value.Type != target.Arguments[index].Type)
                    Add("MIR064",
                        $"{edge} argument {index} is {value.Type}, but {targetId} expects {target.Arguments[index].Type}",
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
            MirSource source)
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
                        CheckDominates(value.Id, value.Definition.Block, instructionIndex: null);
                        break;
                    case MirValueDefinitionKind.InstructionResult:
                        if (value.Definition.Instruction is not MirInstructionId definitionInstruction
                            || !instructions.TryGetValue(definitionInstruction, out var location))
                            break; // dangling-definition error is emitted separately
                        CheckDominates(value.Id, location.Block.Id, location.Index);
                        break;
                }
            }

            void CheckDominates(MirValueId value, MirBlockId? definitionBlock, int? instructionIndex)
            {
                if (definitionBlock is not MirBlockId blockId) return;
                if (blockId == useBlock.Id)
                {
                    if (instructionIndex is int definitionIndex && definitionIndex >= useIndex)
                        Add("MIR071",
                            $"{value} is used before its defining instruction in {useBlock.Id}",
                            callable, useBlock, instruction is { } id ? FindInstruction(useBlock, id) : null, source);
                    return;
                }

                if (!dominators.TryGetValue(useBlock.Id, out var set) || !set.Contains(blockId))
                    Add("MIR072",
                        $"{value}, defined in {blockId}, does not dominate its use in {useBlock.Id}",
                        callable, useBlock, instruction is { } id ? FindInstruction(useBlock, id) : null, source);
            }
        }

        private void VerifyInstruction(
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirStorageId, MirArrayStorage> storages,
            IReadOnlyDictionary<MirQubitResourceId, MirQubitResource> qubits)
        {
            MirType? TypeOf(MirValueId id) => values.TryGetValue(id, out var value) ? value.Type : null;

            foreach (var place in instruction.QubitPlaces)
                VerifyQubitPlace(callable, block, instruction, place, values, qubits);

            switch (instruction)
            {
                case MirConstant constant:
                {
                    var result = TypeOf(constant.Result);
                    if (constant.Constant.Type == QType.Qubit)
                        Add("MIR073", "constant payload has qubit type", callable, block, constant);
                    if (result is { } type
                        && (type.IsArray || type.ElementType != constant.Constant.Type))
                        Add("MIR074",
                            $"constant {constant.Constant.Text} has payload type {constant.Constant.Type}, but result is {type}",
                            callable, block, constant);
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
                    if (left is { } lhs && right is { } rhs && lhs != rhs)
                        Add("MIR076",
                            $"binary operands have different types {lhs} and {rhs}; lowering must insert an explicit conversion",
                            callable, block, binary);
                    var hasArrayOperand = left is { IsArray: true } || right is { IsArray: true };
                    if (hasArrayOperand
                        && binary.Operator is not (MirBinaryOperator.Equal or MirBinaryOperator.NotEqual))
                        Add("MIR076",
                            $"{binary.Operator} cannot consume array operands; only equal/not-equal compare array states",
                            callable, block, binary);
                    if (!hasArrayOperand)
                    {
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
                    RequireScalar(convert.TargetType, "conversion target", callable, block, convert);
                    if (result is { } type && type != convert.TargetType)
                        Add("MIR079",
                            $"conversion result is {type}, but its target type is {convert.TargetType}",
                            callable, block, convert);
                    break;
                }
                case MirArrayCreate create:
                {
                    if (!storages.TryGetValue(create.Storage, out var storage))
                        Add("MIR080", $"array creation references missing storage {create.Storage}",
                            callable, block, create);
                    var result = TypeOf(create.Result);
                    var expected = MirType.Array(create.ElementType, create.Length);
                    if (result is { } createdResultType && createdResultType != expected)
                        Add("MIR081", $"array creation result is {createdResultType}, expected {expected}",
                            callable, block, create);
                    if (storage is not null
                        && (storage.Kind != MirArrayStorageKind.Local
                            || storage.Type != expected
                            || storage.AllocationInstruction != create.Id))
                        Add("MIR082", $"storage {create.Storage} does not match its array creation",
                            callable, block, create);
                    if (create.Length < 0)
                        Add("MIR083", $"array creation has negative length {create.Length}",
                            callable, block, create);
                    if (create.Initialization == MirArrayInitialization.ExplicitElements
                        && create.Elements.Count != create.Length)
                        Add("MIR084",
                            $"explicit array creation has {create.Elements.Count} element(s), expected {create.Length}",
                            callable, block, create);
                    if (create.Initialization == MirArrayInitialization.ZeroInitialized
                        && create.Elements.Count != 0)
                        Add("MIR085", "zero-initialized array creation carries explicit elements",
                            callable, block, create);
                    foreach (var element in create.Elements)
                        if (TypeOf(element) is { } elementType
                            && elementType != MirType.Scalar(create.ElementType))
                            Add("MIR086",
                                $"array element {element} is {elementType}, expected {create.ElementType.ToString().ToLowerInvariant()}",
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
                        && _callables.TryGetValue(user.Callable, out var callee)
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
                case MirQubitAllocate allocation:
                    if (!qubits.TryGetValue(allocation.Resource, out var allocated)
                        || allocated.Kind != MirQubitResourceKind.Local
                        || allocated.AllocationInstruction != allocation.Id)
                        Add("MIR097",
                            $"qubit allocation does not match local resource {allocation.Resource}",
                            callable, block, allocation);
                    break;
                case MirQuantumApply apply:
                    VerifyCall(callable, block, apply, apply.Target, apply.Operands, values, qubits,
                        expectFunction: false);
                    VerifyMutableResults(callable, block, apply, values);
                    break;
                case MirMeasure measure:
                    if (TypeOf(measure.Result) is { } measureType
                        && measureType != MirType.Scalar(QType.Bit))
                        Add("MIR098", $"measurement result is {measureType}, expected bit",
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
            IReadOnlyDictionary<MirQubitResourceId, MirQubitResource> qubits,
            bool expectFunction)
        {
            switch (target)
            {
                case MirUserCallableTarget user:
                    if (!_callables.TryGetValue(user.Callable, out var callee))
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
                            callee.Parameters[index], values, qubits);
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
                    if (operands.Count != signature.Params.Count)
                    {
                        Add("MIR107",
                            $"built-in gate `{builtin.Name}` has {operands.Count} operand(s), expected {signature.Params.Count}",
                            caller, block, instruction);
                        return;
                    }
                    for (var index = 0; index < operands.Count; index++)
                    {
                        var expected = signature.Params[index];
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
            MirParameter expected,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirQubitResourceId, MirQubitResource> qubits)
        {
            switch (actual, expected)
            {
                case (MirClassicalCallOperand classical, MirClassicalParameter parameter):
                    if (values.TryGetValue(classical.Value, out var value)
                        && !CallTypeCompatible(value.Type, parameter.Type))
                        Add("MIR111",
                            $"call operand {index} is {value.Type}, expected {parameter.Type}",
                            caller, block, instruction);
                    if (classical.Ownership != parameter.Ownership || classical.Access != parameter.Access)
                        Add("MIR112",
                            $"call operand {index} has {classical.Ownership}/{classical.Access}, expected {parameter.Ownership}/{parameter.Access}",
                            caller, block, instruction);
                    break;
                case (MirQubitCallOperand qubit, MirQubitParameter parameter):
                    if (qubits.TryGetValue(qubit.Place.Resource, out var resource))
                    {
                        var passesWholeArray = qubit.Place.Index is null && resource.IsArray;
                        if (parameter.IsArray != passesWholeArray)
                            Add("MIR113",
                                $"qubit operand {index} shape does not match parameter `{parameter.Name}`",
                                caller, block, instruction);
                        else if (parameter.IsArray
                                 && parameter.Length is int expectedLength
                                 && resource.Length != expectedLength)
                            Add("MIR113",
                                $"qubit operand {index} has length {resource.Length?.ToString() ?? "unknown"}, expected {expectedLength}",
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
                || !_callables.TryGetValue(user.Callable, out var callee))
            {
                if (apply.MutableArrayResults.Count != 0)
                    Add("MIR120",
                        "a built-in or unresolved quantum target cannot produce mutable array states",
                        caller, block, apply);
                return;
            }
            var expected = callee.Parameters
                .Select((parameter, index) => (parameter, index))
                .Where(item => item.parameter is MirClassicalParameter
                {
                    Type.IsArray: true,
                    Ownership: QOwnershipMode.Borrowed,
                    Access: QAccessMode.Mutable
                })
                .Select(item => item.index)
                .ToHashSet();
            if (!expected.SetEquals(seenOperands))
                Add("MIR120",
                    $"mutable result operands [{string.Join(", ", seenOperands.Order())}] do not match callee contract [{string.Join(", ", expected.Order())}]",
                    caller, block, apply);
        }

        private void VerifyQubitPlace(
            MirCallable callable,
            MirBlock block,
            MirInstruction instruction,
            MirQubitPlace place,
            IReadOnlyDictionary<MirValueId, MirValue> values,
            IReadOnlyDictionary<MirQubitResourceId, MirQubitResource> qubits)
        {
            if (!qubits.TryGetValue(place.Resource, out var resource))
            {
                Add("MIR121", $"qubit place references missing resource {place.Resource}",
                    callable, block, instruction);
                return;
            }
            if (place.Index is MirValueId index)
            {
                if (!resource.IsArray)
                    Add("MIR122", $"single-qubit resource {place.Resource} is indexed",
                        callable, block, instruction);
                if (values.TryGetValue(index, out var value)
                    && value.Type != MirType.Scalar(QType.Int))
                    Add("MIR123", $"qubit index {index} is {value.Type}, expected int",
                        callable, block, instruction);
            }
        }

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
                            callable, block, source: branch.Source);
                    break;
                case MirReturn ret:
                    if (callable.Kind == MirCallableKind.Operation)
                    {
                        if (ret.Value is not null)
                            Add("MIR125", "operation returns a value", callable, block, source: ret.Source);
                    }
                    else if (ret.Value is not MirValueId returnValue)
                        Add("MIR126", "function return has no value", callable, block, source: ret.Source);
                    else if (values.TryGetValue(returnValue, out var value)
                             && callable.ReturnType is { } expected
                             && value.Type != expected)
                        Add("MIR127",
                            $"function returns {value.Type}, expected {expected}",
                            callable, block, source: ret.Source);
                    break;
                case MirJump:
                case MirUnreachable:
                    break;
                default:
                    Add("MIR128", $"unknown terminator kind {terminator.GetType().Name}",
                        callable, block, source: terminator.Source);
                    break;
            }
        }

        private static MirInstruction? FindInstruction(MirBlock block, MirInstructionId id) =>
            block.Instructions.FirstOrDefault(instruction => instruction?.Id == id);

        private void CheckDefinitionShape(MirValue value, MirCallable callable)
        {
            if (value.Id.Value < 0)
                Add("MIR130", $"SSA value has negative identity {value.Id}", callable);
            if (value.Definition.Index < 0)
                Add("MIR131", $"{value.Id} has negative definition index {value.Definition.Index}", callable);
            var valid = value.Definition.Kind switch
            {
                MirValueDefinitionKind.Parameter =>
                    value.Definition.Block is null && value.Definition.Instruction is null,
                MirValueDefinitionKind.BlockArgument =>
                    value.Definition.Block is not null && value.Definition.Instruction is null,
                MirValueDefinitionKind.InstructionResult =>
                    value.Definition.Block is not null && value.Definition.Instruction is not null,
                _ => false,
            };
            if (!valid)
                Add("MIR132", $"{value.Id} has malformed {value.Definition.Kind} definition coordinates", callable);
        }

        private void CheckClassicalType(
            MirType type,
            string role,
            MirCallable callable,
            MirBlock? block = null)
        {
            if (type.ElementType == QType.Qubit)
                Add("MIR133", $"{role} uses qubit as a classical MIR type", callable, block);
            if (!type.IsArray && type.KnownLength is not null)
                Add("MIR134", $"{role} is scalar but carries length {type.KnownLength}", callable, block);
            if (type.KnownLength is < 0)
                Add("MIR135", $"{role} has negative length {type.KnownLength}", callable, block);
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

        private Dictionary<TId, TItem> Unique<TId, TItem>(
            IReadOnlyList<TItem> items,
            Func<TItem, TId> id,
            string code,
            string label,
            MirCallable callable)
            where TId : notnull
            where TItem : class
        {
            var result = new Dictionary<TId, TItem>();
            if (items is null)
            {
                Add(code, $"{label} collection is null", callable);
                return result;
            }
            foreach (var item in items)
            {
                if (item is null)
                {
                    Add(code, $"{label} collection contains null", callable);
                    continue;
                }
                var key = id(item);
                if (!result.TryAdd(key, item))
                    Add(code, $"{label} identity {key} is declared more than once", callable);
            }
            return result;
        }

        private void Add(
            string code,
            string message,
            MirCallable? callable = null,
            MirBlock? block = null,
            MirInstruction? instruction = null,
            MirSource? source = null) =>
            _errors.Add(new MirVerificationError(
                code,
                message,
                callable?.Id,
                block?.Id,
                instruction?.Id,
                source ?? instruction?.Source ?? block?.Source ?? callable?.Source));
    }
}
