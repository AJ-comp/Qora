namespace Qora.Ir.Mir;

internal static partial class QoraMirVerifier
{
    private sealed partial class Verifier
    {
        private void VerifyBlockContents(
            MirCallable callable,
            IReadOnlyDictionary<MirBlockId, HashSet<MirBlockId>> dominators)
        {
            foreach (var block in callable.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    VerifyInstructionUses(
                        callable,
                        instruction,
                        dominators);
                    VerifyInstruction(callable, instruction);
                }

                VerifyTerminatorUses(
                    callable,
                    block,
                    dominators);
                VerifyTerminator(callable, block);
            }
        }

        private void VerifyInstruction(
            MirCallable callable,
            MirInstruction instruction)
        {
            MirType? TypeOf(MirValueId id) => callable.FindValue(id)?.Type;

            foreach (var access in instruction.QubitAccesses)
                VerifyQubitAccess(access);

            switch (instruction)
            {
                case MirConstant:
                    break;
                case MirUnary unary:
                {
                    var operand = TypeOf(unary.Operand);
                    RequireScalar(operand, "unary operand");
                    if (unary.Operator == MirUnaryOperator.LogicalNot
                        && operand is { ElementType: not QType.Bit })
                        AddAtInstruction("MIR075", "logical-not requires a bit operand", callable, unary);
                    if (unary.Operator == MirUnaryOperator.Negate
                        && operand is { } operandType
                        && unary.Result.Type != operandType)
                    {
                        AddAtInstruction("MIR075",
                            $"negate result is {unary.Result.Type}, expected its operand type {operandType}; lowering must insert an explicit conversion",
                            callable, unary);
                    }
                    break;
                }
                case MirBinary binary:
                {
                    var left = TypeOf(binary.Left);
                    var right = TypeOf(binary.Right);
                    var isComparison = binary.Operator is MirBinaryOperator.Equal
                        or MirBinaryOperator.NotEqual
                        or MirBinaryOperator.Less
                        or MirBinaryOperator.LessOrEqual
                        or MirBinaryOperator.Greater
                        or MirBinaryOperator.GreaterOrEqual;
                    var leftIsArray = left is { IsArray: true };
                    var rightIsArray = right is { IsArray: true };
                    if (leftIsArray || rightIsArray)
                    {
                        if (!leftIsArray || !rightIsArray)
                        {
                            AddAtInstruction("MIR076",
                                $"binary array comparison requires two arrays, not {left} and {right}",
                                callable, binary);
                        }
                        else if (left is { } leftArray
                                 && right is { } rightArray
                                 && leftArray.ElementType != rightArray.ElementType)
                        {
                            AddAtInstruction("MIR076",
                                $"binary array comparison has different element types {leftArray} and {rightArray}",
                                callable, binary);
                        }

                        if (binary.Operator is not (MirBinaryOperator.Equal or MirBinaryOperator.NotEqual))
                        {
                            AddAtInstruction("MIR076",
                                $"{binary.Operator} cannot consume array operands; only equal/not-equal compare array states",
                                callable, binary);
                        }
                    }
                    else
                    {
                        if (left is { } lhs && right is { } rhs && lhs != rhs)
                        {
                            AddAtInstruction("MIR076",
                                $"binary operands have different types {lhs} and {rhs}; lowering must insert an explicit conversion",
                                callable, binary);
                        }
                        if (!isComparison
                            && left is { } leftType
                            && binary.Result.Type != leftType)
                        {
                            AddAtInstruction("MIR076",
                                $"{binary.Operator} result is {binary.Result.Type}, expected its operand type {leftType}; lowering must insert an explicit conversion",
                                callable, binary);
                        }
                    }
                    break;
                }
                case MirConvert convert:
                {
                    var operand = TypeOf(convert.Operand);
                    RequireScalar(operand, "conversion operand");
                    break;
                }
                case MirArrayCreate create:
                {
                    foreach (var element in create.Elements)
                        if (TypeOf(element) is { } elementType
                            && elementType != MirType.Scalar(create.Result.Type.ElementType))
                            AddAtInstruction("MIR086",
                                $"array element {element} is {elementType}, expected {create.Result.Type.ElementType.ToString().ToLowerInvariant()}",
                                callable, create);
                    break;
                }
                case MirArrayLength length:
                {
                    if (TypeOf(length.Array) is { IsArray: false } arrayType)
                        AddAtInstruction("MIR087", $"array-length operand is scalar {arrayType}", callable, length);
                    break;
                }
                case MirArrayLoad load:
                {
                    var array = TypeOf(load.Array);
                    RequireIntIndex(TypeOf(load.Index));
                    if (array is { IsArray: false } scalar)
                        AddAtInstruction("MIR089", $"array-load source is scalar {scalar}", callable, load);
                    else if (array is { IsArray: true } arrayType
                             && load.Result.Type != MirType.Scalar(arrayType.ElementType))
                        AddAtInstruction("MIR090",
                            $"array-load result is {load.Result.Type}, expected {arrayType.ElementType.ToString().ToLowerInvariant()}",
                            callable, load);
                    break;
                }
                case MirArrayStore store:
                {
                    var array = TypeOf(store.Array);
                    RequireIntIndex(TypeOf(store.Index));
                    if (array is { IsArray: false } scalar)
                        AddAtInstruction("MIR091", $"array-store source is scalar {scalar}", callable, store);
                    else if (array is { IsArray: true } arrayType)
                    {
                        if (TypeOf(store.Value) is { } valueType
                            && valueType != MirType.Scalar(arrayType.ElementType))
                            AddAtInstruction("MIR092",
                                $"array-store value is {valueType}, expected {arrayType.ElementType.ToString().ToLowerInvariant()}",
                                callable, store);
                        if (store.Result.Type != arrayType)
                            AddAtInstruction("MIR093",
                                $"array-store result is {store.Result.Type}, expected previous state type {arrayType}",
                                callable, store);
                    }
                    break;
                }
                case MirPureCall call:
                    VerifyCall(callable, call);
                    break;
                case MirQubitAllocate:
                    break;
                case MirQuantumApply apply:
                    VerifyCall(callable, apply);
                    break;
                case MirMeasure:
                    break;
                default:
                    AddAtInstruction(
                        "MIR099",
                        $"unknown instruction kind {instruction.GetType().Name}",
                        callable,
                        instruction);
                    break;
            }

            void RequireScalar(MirType? type, string role)
            {
                if (type is { IsArray: true } array)
                    AddAtInstruction("MIR136", $"{role} is array {array}", callable, instruction);
            }

            void RequireIntIndex(MirType? type)
            {
                if (type is { } actual && actual != MirType.Scalar(QType.Int))
                    AddAtInstruction(
                        "MIR137",
                        $"array index is {actual}, expected int",
                        callable,
                        instruction);
            }

            void VerifyQubitAccess(MirQubitAccess access)
            {
                if (!callable.ContainsQubit(access.Qubit))
                {
                    AddAtInstruction(
                        "MIR121",
                        $"qubit access references missing version {access.Qubit}",
                        callable,
                        instruction);
                    return;
                }

                if (access.Index is not MirValueId index)
                    return;

                var seed = QubitSeed(callable, access.Qubit.Id);
                if (seed is not null && !QubitShape(seed).IsArray)
                {
                    AddAtInstruction(
                        "MIR122",
                        $"single qubit {access.Qubit.Id} is indexed",
                        callable,
                        instruction);
                }

                if (callable.FindValue(index) is { } value
                    && value.Type != MirType.Scalar(QType.Int))
                {
                    AddAtInstruction(
                        "MIR123",
                        $"qubit index {index} is {value.Type}, expected int",
                        callable,
                        instruction);
                }
            }
        }

        private void VerifyCall(
            MirCallable caller,
            MirInstruction instruction)
        {
            MirCallTarget target;
            IReadOnlyList<MirCallOperand> operands;
            bool expectFunction;

            switch (instruction)
            {
                case MirPureCall call:
                    target = call.Target;
                    operands = call.Operands;
                    expectFunction = true;
                    break;
                case MirQuantumApply apply:
                    target = apply.Target;
                    operands = apply.Operands;
                    expectFunction = false;
                    break;
                default:
                    throw new ArgumentException(
                        $"{instruction.GetType().Name} is not a MIR call instruction",
                        nameof(instruction));
            }

            switch (target)
            {
                case MirUserCallableTarget user:
                    if (_program.FindCallable(user.Callable) is not { } callee)
                    {
                        AddAtInstruction(
                            "MIR100",
                            $"call targets missing callable {user.Callable}",
                            caller,
                            instruction);
                        return;
                    }
                    var callableKindMatches =
                        (callee.Kind == MirCallableKind.Function) == expectFunction;
                    if (!callableKindMatches)
                        AddAtInstruction("MIR101",
                            expectFunction
                                ? $"pure call targets operation `{callee.Name}`"
                                : $"quantum apply targets function `{callee.Name}`",
                            caller, instruction);
                    if (operands.Count != callee.Parameters.Count)
                    {
                        AddAtInstruction("MIR102",
                            $"call to `{callee.Name}` has {operands.Count} operand(s), expected {callee.Parameters.Count}",
                            caller, instruction);
                        return;
                    }
                    for (var index = 0; index < operands.Count; index++)
                        VerifyUserCallOperand(callee, index);

                    if (callableKindMatches)
                    {
                        if (instruction is MirPureCall call
                            && callee.ReturnType is { } returnType
                            && call.Result.Type != returnType)
                        {
                            AddAtInstruction(
                                "MIR095",
                                $"pure-call result is {call.Result.Type}, but {callee.Name} returns {returnType}",
                                caller,
                                call);
                        }
                        else if (instruction is MirQuantumApply apply)
                        {
                            VerifyMutableResults(caller, apply, callee);
                        }
                    }
                    break;

                case MirBuiltinFunctionTarget builtin:
                    var function = QoraGates.Functions[builtin.Name];
                    var functionOperand = (MirClassicalCallOperand)operands[0];
                    var functionValue = caller.FindValue(functionOperand.Value);
                    if (function.TakesBitRegister
                        && functionValue is not null
                        && functionValue.Type is not { ElementType: QType.Bit, IsArray: true })
                        AddAtInstruction(
                            "MIR105",
                            $"built-in `{builtin.Name}` expects a whole bit array",
                            caller,
                            instruction);
                    break;

                case MirBuiltinGateTarget builtin:
                {
                    var apply = (MirQuantumApply)instruction;
                    var extraControls = apply.Functors.Count(
                        functor => functor == MirFunctor.Controlled);
                    var signature = QoraGates.SigOf(builtin.Name, extraControls)!;
                    for (var index = 0; index < operands.Count; index++)
                    {
                        var expected = signature.Parameters[index];
                        if (expected.Type != QType.Qubit
                            && operands[index] is MirClassicalCallOperand classical
                            && caller.FindValue(classical.Value) is { } operandValue
                            && operandValue.Type != MirType.Scalar(expected.Type))
                            AddAtInstruction("MIR109",
                                $"built-in gate `{builtin.Name}` operand {index} must be {expected.Type.ToString().ToLowerInvariant()}",
                                caller, instruction);
                    }
                    break;
                }
            }

            void VerifyUserCallOperand(MirCallable callee, int index)
            {
                var actual = operands[index];
                var expected = callee.Parameters[index];

                switch (actual, expected)
                {
                    case (MirClassicalCallOperand classical, MirClassicalParameter parameter):
                        if (caller.FindValue(classical.Value) is { } value
                            && !CallTypeCompatible(value.Type, parameter.Value.Type))
                        {
                            AddAtInstruction(
                                "MIR111",
                                $"call operand {index} is {value.Type}, expected {parameter.Value.Type}",
                                caller,
                                instruction);
                        }
                        if (classical.Ownership != parameter.Ownership
                            || classical.Access != parameter.Access)
                        {
                            AddAtInstruction(
                                "MIR112",
                                $"call operand {index} has {classical.Ownership}/{classical.Access}, expected {parameter.Ownership}/{parameter.Access}",
                                caller,
                                instruction);
                        }
                        break;
                    case (MirQubitCallOperand qubit, MirQubitParameter parameter):
                        if (QubitSeed(caller, qubit.Qubit.Qubit.Id) is { } seed)
                        {
                            var (isArray, length) = QubitShape(seed);
                            var passesWholeArray = qubit.Qubit.Index is null && isArray;
                            if (parameter.IsArray != passesWholeArray)
                            {
                                AddAtInstruction(
                                    "MIR113",
                                    $"qubit operand {index} shape does not match parameter `{parameter.Name}`",
                                    caller,
                                    instruction);
                            }
                            else if (parameter.IsArray
                                     && parameter.Length is int expectedLength
                                     && length != expectedLength)
                            {
                                AddAtInstruction(
                                    "MIR113",
                                    $"qubit operand {index} has length {length?.ToString() ?? "unknown"}, expected {expectedLength}",
                                    caller,
                                    instruction);
                            }
                        }
                        if (qubit.Ownership != parameter.Ownership)
                        {
                            AddAtInstruction(
                                "MIR114",
                                $"qubit operand {index} has ownership {qubit.Ownership}, expected {parameter.Ownership}",
                                caller,
                                instruction);
                        }
                        break;
                    default:
                        AddAtInstruction(
                            "MIR115",
                            $"call operand {index} kind does not match parameter `{expected.Name}`",
                            caller,
                            instruction);
                        break;
                }
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
            MirQuantumApply apply,
            MirCallable callee)
        {
            var resultOperands = new HashSet<int>();

            foreach (var result in apply.MutableArrayResults)
            {
                resultOperands.Add(result.OperandIndex);
                var operand = (MirClassicalCallOperand)apply.Operands[result.OperandIndex];
                if (caller.FindValue(operand.Value) is { } input
                    && (!input.Type.IsArray || result.Result.Type != input.Type))
                    AddAtInstruction("MIR119",
                        $"mutable result {result.Result.Id} is {result.Result.Type}, expected array state {input.Type}",
                        caller, apply);
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

                if (parameter.Value.Type.IsArray)
                    expected.Add(index);
            }

            if (!expected.SetEquals(resultOperands))
                AddAtInstruction("MIR120",
                    $"mutable result operands [{string.Join(", ", resultOperands.Order())}] do not match callee contract [{string.Join(", ", expected.Order())}]",
                    caller, apply);
        }

        private static MirQubit? QubitSeed(
            MirCallable callable,
            MirQubitId id) =>
            callable.Qubits.FirstOrDefault(
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
            MirBlock block)
        {
            var terminator = block.Terminator;

            switch (terminator)
            {
                case MirBranch branch:
                    if (callable.FindValue(branch.Condition) is { } condition
                        && condition.Type != MirType.Scalar(QType.Bit))
                    {
                        AddAtTerminator(
                            "MIR124",
                            $"branch condition {branch.Condition} is {condition.Type}, expected bit",
                            callable,
                            block);
                    }
                    break;
                case MirReturn ret:
                    if (callable.Kind == MirCallableKind.Operation)
                    {
                        if (ret.Value is not null)
                            AddAtTerminator("MIR125", "operation returns a value", callable, block);
                    }
                    else if (ret.Value is not MirValueId returnValue)
                        AddAtTerminator("MIR126", "function return has no value", callable, block);
                    else if (callable.FindValue(returnValue) is { } value
                             && value.Type != callable.ReturnType!.Value)
                    {
                        AddAtTerminator(
                            "MIR127",
                            $"function returns {value.Type}, expected {callable.ReturnType.Value}",
                            callable,
                            block);
                    }
                    break;
                case MirJump:
                case MirUnreachable:
                    break;
                default:
                    AddAtTerminator(
                        "MIR128",
                        $"unknown terminator kind {terminator.GetType().Name}",
                        callable,
                        block);
                    break;
            }
        }
    }
}
