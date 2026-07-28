using System.Text;

namespace Qora.Ir.Mir;

/// <summary>A deterministic, human-readable MIR rendering intended for stage views and golden tests.</summary>
public static class MirPrinter
{
    public static string Print(MirProgram? program)
    {
        if (program is null) return string.Empty;

        var writer = new Writer(program);
        return writer.Print();
    }

    private sealed class Writer
    {
        private readonly MirProgram _program;
        private readonly StringBuilder _text = new();
        private readonly IReadOnlyDictionary<MirCallableId, MirCallable> _callables;
        private IReadOnlyDictionary<MirValueId, MirValue> _values =
            new Dictionary<MirValueId, MirValue>();

        public Writer(MirProgram program)
        {
            _program = program;
            _callables = program.Callables
                .GroupBy(callable => callable.Id)
                .ToDictionary(group => group.Key, group => group.First());
        }

        public string Print()
        {
            _text.AppendLine(
                $"mir snapshot {_program.SnapshotId} entry @{_program.EntryPoint}");
            foreach (var callable in _program.Callables.OrderBy(callable => callable.Id.Value))
            {
                _text.AppendLine();
                PrintCallable(callable);
            }
            return _text.ToString().TrimEnd();
        }

        private void PrintCallable(MirCallable callable)
        {
            _values = callable.Values
                .GroupBy(value => value.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var kind = callable.Kind == MirCallableKind.Function ? "function" : "operation";
            var returns = callable.ReturnType is { } returnType ? $" -> {returnType}" : string.Empty;
            _text.AppendLine(
                $"{kind} @{callable.Id} {callable.Name}({string.Join(", ", callable.Parameters.Select(Parameter))}){returns} {Origin(callable.Origin)}");
            _text.AppendLine($"  entry {Block(callable.EntryBlock)}");

            foreach (var storage in callable.Storages.OrderBy(storage => storage.Id.Value))
            {
                var definition = storage.Kind == MirArrayStorageKind.Parameter
                    ? $"parameter {storage.ParameterIndex}"
                    : $"allocate %{storage.AllocationInstruction}";
                _text.AppendLine(
                    $"  storage ${storage.Id} {storage.Name}: {storage.Type} [{storage.Kind.ToString().ToLowerInvariant()}, {storage.AliasMode.ToString().ToLowerInvariant()}, {definition}] {Origin(storage.Origin)}");
            }

            foreach (var qubit in callable.Qubits
                         .OrderBy(qubit => qubit.Id.Value)
                         .ThenBy(qubit => qubit.Version.Value))
            {
                _text.AppendLine(
                    $"  qubit {Qubit(qubit.Key)} {QubitDefinition(qubit)} {Origin(qubit.Origin)}");
            }

            foreach (var block in callable.Blocks.OrderBy(block => block.Id.Value))
                PrintBlock(block);
        }

        private void PrintBlock(MirBlock block)
        {
            var arguments = string.Join(", ", block.Arguments.Select(argument =>
                $"{Value(argument.Value)}: {argument.Type}"));
            _text.AppendLine($"  {Block(block.Id)}({arguments}): {Origin(block.Origin)}");
            foreach (var instruction in block.Instructions)
                _text.AppendLine($"    {Instruction(instruction)} {Origin(instruction.Origin)}");
            _text.AppendLine($"    {Terminator(block.Terminator)} {Origin(block.Terminator.Origin)}");
        }

        private string Instruction(MirInstruction instruction) => instruction switch
        {
            MirConstant constant =>
                $"{Result(constant.Result)} = const {constant.Constant.Type.ToString().ToLowerInvariant()} {constant.Constant.Text}",

            MirUnary unary =>
                $"{Result(unary.Result)} = {Unary(unary.Operator)} {Value(unary.Operand)}",

            MirBinary binary =>
                $"{Result(binary.Result)} = {Binary(binary.Operator)} {Value(binary.Left)}, {Value(binary.Right)}",

            MirConvert convert =>
                $"{Result(convert.Result)} = convert {Value(convert.Operand)} to {convert.TargetType}",

            MirArrayCreate create =>
                $"{Result(create.Result)} = array.create ${create.Storage} {create.ElementType.ToString().ToLowerInvariant()}[{create.Length}]"
                + (create.Initialization == MirArrayInitialization.ZeroInitialized
                    ? " zero"
                    : $" {{{string.Join(", ", create.Elements.Select(Value))}}}"),

            MirArrayLength length =>
                $"{Result(length.Result)} = array.length {Value(length.Array)}",

            MirArrayLoad load =>
                $"{Result(load.Result)} = array.load {Value(load.Array)}[{Value(load.Index)}]",

            MirArrayStore store =>
                $"{Result(store.Result)} = array.store {Value(store.Array)}[{Value(store.Index)}], {Value(store.Value)}",

            MirPureCall call =>
                $"{Result(call.Result)} = call {Target(call.Target)}({string.Join(", ", call.Operands.Select(Operand))})",

            MirQubitAllocate allocation =>
                $"qubit.allocate {Qubit(allocation.Result.Key)}",

            MirQuantumApply apply =>
                $"{ApplyResults(apply)}apply {Functors(apply.Functors)}{Target(apply.Target)}"
                + $"({string.Join(", ", apply.Operands.Select(Operand))})"
                + MutableResults(apply.MutableArrayResults),

            MirMeasure measure =>
                $"({Result(measure.Result)}, {Qubit(measure.QubitResult.Key)})"
                + $" = measure {Access(measure.Qubit)}",

            _ => $"<{instruction.GetType().Name}>",
        };

        private string Terminator(MirTerminator terminator) => terminator switch
        {
            MirJump jump =>
                $"jump {Block(jump.Target)}({string.Join(", ", jump.Arguments.Select(Value))})",

            MirBranch branch =>
                $"branch {Value(branch.Condition)}, "
                + $"{Block(branch.TrueTarget)}({string.Join(", ", branch.TrueArguments.Select(Value))}), "
                + $"{Block(branch.FalseTarget)}({string.Join(", ", branch.FalseArguments.Select(Value))})",

            MirReturn { Value: MirValueId value } =>
                $"return {Value(value)}",

            MirReturn =>
                "return",

            MirUnreachable =>
                "unreachable",

            _ => $"<{terminator.GetType().Name}>",
        };

        private string Parameter(IMirParameter parameter) => parameter switch
        {
            MirClassicalParameter classical =>
                $"{Mode(classical.Ownership, classical.Access)}{Value(classical.Value)} {classical.Name}: {classical.Type}"
                + (classical.Storage is MirStorageId storage ? $" storage ${storage}" : string.Empty),

            MirQubitParameter qubit =>
                $"{Mode(qubit.Ownership, QAccessMode.ReadOnly)}{Qubit(qubit.Key)} {qubit.Name}: "
                + (qubit.IsArray
                    ? qubit.Length is int length ? $"qubit[{length}]" : "qubit[]"
                    : "qubit"),

            _ => $"<{parameter.GetType().Name}>",
        };

        private string Operand(MirCallOperand operand) => operand switch
        {
            MirClassicalCallOperand classical =>
                $"{Mode(classical.Ownership, classical.Access)}{Value(classical.Value)}",
            MirQubitCallOperand qubit =>
                $"{Mode(qubit.Ownership, qubit.Access)}{Access(qubit.Qubit)}",
            _ => $"<{operand.GetType().Name}>",
        };

        private string Target(MirCallTarget target) => target switch
        {
            MirUserCallableTarget user when _callables.TryGetValue(user.Callable, out var callable) =>
                $"@{user.Callable}:{callable.Name}",
            MirUserCallableTarget user =>
                $"@{user.Callable}",
            MirBuiltinGateTarget builtin =>
                $"builtin.gate::{builtin.Name}",
            MirBuiltinFunctionTarget builtin =>
                $"builtin.function::{builtin.Name}",
            _ => target.DisplayName,
        };

        private string Result(MirValueId id) =>
            _values.TryGetValue(id, out var value)
                ? $"{Value(id)}: {value.Type}"
                : $"{Value(id)}: ?";

        private static string Value(MirValueId id) => $"%{id}";
        private static string Block(MirBlockId id) => $"^{id}";
        private static string Qubit(MirQubitKey key) => $"&{key}";

        private static string Access(MirQubitAccess access) =>
            access.Index is MirValueId index
                ? $"{Qubit(access.Qubit)}[{Value(index)}]"
                : Qubit(access.Qubit);

        private static string ApplyResults(MirQuantumApply apply)
        {
            var results = apply.QubitResults
                .Select(result => Qubit(result.Key))
                .Concat(apply.MutableArrayResults.Select(result => ResultName(result.Result)))
                .ToArray();
            return results.Length == 0
                ? string.Empty
                : $"({string.Join(", ", results)}) = ";
        }

        private static string QubitDefinition(MirQubit qubit) => qubit switch
        {
            MirQubitParameter parameter =>
                $"{parameter.Name}: {QubitShape(parameter.IsArray, parameter.Length)} [parameter]",
            MirQubitFromUse local =>
                $"{local.Name}: {QubitShape(local.IsArray, local.Length)} [use]",
            MirQubitAfterInstruction =>
                "[instruction-result]",
            MirQubitPhi phi =>
                $"[phi {Block(phi.Block)}"
                + (phi.Inputs.Count == 0
                    ? "]"
                    : $" <- {string.Join(", ", phi.Inputs.Select(input =>
                        $"{Block(input.Edge.Source)}#{input.Edge.SuccessorOrdinal}:{Qubit(input.Qubit)}"))}]"),
            _ => $"[unknown {qubit.GetType().Name}]",
        };

        private static string QubitShape(bool isArray, int? length) =>
            isArray
                ? length is int count ? $"qubit[{count}]" : "qubit[]"
                : "qubit";

        private static string ResultName(MirValueId id) => $"%{id}";

        private static string MutableResults(IReadOnlyList<MirMutableArrayResult> results) =>
            results.Count == 0
                ? string.Empty
                : $" mutable [{string.Join(", ", results.Select(result => $"{result.OperandIndex}->{Value(result.Result)}"))}]";

        private static string Functors(IReadOnlyList<MirFunctor> functors) =>
            functors.Count == 0
                ? string.Empty
                : string.Join(" ", functors.Select(functor => functor.ToString().ToLowerInvariant())) + " ";

        private static string Unary(MirUnaryOperator op) => op switch
        {
            MirUnaryOperator.Negate => "neg",
            MirUnaryOperator.LogicalNot => "not",
            _ => op.ToString().ToLowerInvariant(),
        };

        private static string Binary(MirBinaryOperator op) => op switch
        {
            MirBinaryOperator.Add => "add",
            MirBinaryOperator.Subtract => "sub",
            MirBinaryOperator.Multiply => "mul",
            MirBinaryOperator.Divide => "div",
            MirBinaryOperator.Equal => "eq",
            MirBinaryOperator.NotEqual => "ne",
            MirBinaryOperator.Less => "lt",
            MirBinaryOperator.LessOrEqual => "le",
            MirBinaryOperator.Greater => "gt",
            MirBinaryOperator.GreaterOrEqual => "ge",
            _ => op.ToString().ToLowerInvariant(),
        };

        private static string Mode(QOwnershipMode ownership, QAccessMode access) =>
            (ownership, access) switch
            {
                (QOwnershipMode.Borrowed, QAccessMode.Mutable) => "var ",
                (QOwnershipMode.Moved, QAccessMode.ReadOnly) => "move ",
                (QOwnershipMode.Moved, QAccessMode.Mutable) => "move var ",
                _ => string.Empty,
            };

        private string Origin(MirOriginRef id)
        {
            var origin = _program.Origins.Require(id);
            var hir = _program.Origins.ResolveHir(id);
            var node = hir.HirNodeId is HirNodeId nodeId
                ? $",node={nodeId}"
                : string.Empty;
            var span = hir.Span is SourceSpan location
                ? $",span={location}"
                : string.Empty;
            var synthesized = origin.Kind == MirOriginKind.Synthesized
                ? $",synth={origin.SynthesisReason}"
                : string.Empty;
            return $"@{id}:hir(callable={hir.HirCallableId}{node}{span}{synthesized})";
        }
    }
}
