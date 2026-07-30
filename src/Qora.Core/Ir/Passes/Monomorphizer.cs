namespace Qora.Ir.Passes;

/// <summary>
/// Specializes source-level unsized <c>Qubit[]</c>/<c>bit[]</c> callables by concrete call-site widths.
/// Statement and value calls share <see cref="HirCallExpression"/>, so one worklist handles both forms.
/// </summary>
internal static class Monomorphizer
{
    public sealed record Result(HirRewriteResult Rewrite)
    {
        public HirProgram Program => Rewrite.Root;
    }

    public static Result Run(
        HirProgram program,
        HirRewriteSession rewrite)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(rewrite);
        if (!ReferenceEquals(rewrite.Source.Program, program))
            throw new ArgumentException(
                "Monomorphization must consume the rewrite session's exact source program.",
                nameof(program));

        static bool IsUnsizedArray(HirParameter parameter) =>
            parameter.NeedsMonoSizing;

        static bool NeedsSpecialization(HirCallable callable) =>
            callable.Parameters.Any(IsUnsizedArray);

        var genericById = program.Callables
            .Where(NeedsSpecialization)
            .ToDictionary(callable => callable.Id);
        if (genericById.Count == 0)
            return new Result(rewrite.Publish(program));

        var concrete = program.Callables
            .Where(callable => !NeedsSpecialization(callable))
            .ToList();
        var namesByNamespace = program.Callables
            .GroupBy(
                callable => program.NamespaceOf(callable),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(
                    group.Select(callable => callable.Name),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        var specializations = new Dictionary<string, HirCallable>(
            StringComparer.Ordinal);
        var specializationsBySourceId = genericById.Keys
            .ToDictionary(
                callableId => callableId,
                _ => new List<HirCallable>());
        HirCallable? GenericCallee(HirCallExpression call) =>
            call.CalleeId is { } id
            && genericById.TryGetValue(id, out var callable)
                ? callable
                : null;

        Dictionary<string, int> ConcreteRegisters(HirCallable callable)
        {
            var registers = new Dictionary<string, int>(
                StringComparer.Ordinal);
            foreach (var parameter in callable.Parameters)
            {
                if ((parameter.IsQubitArray
                     || parameter is
                     {
                         Type: QType.Bit,
                         IsArray: true,
                     })
                    && parameter.RegisterSize is int size)
                {
                    registers[parameter.Name] = size;
                }
            }

            CollectUses(callable.Body, registers);
            return registers;
        }

        static void CollectUses(
            HirBlock body,
            Dictionary<string, int> registers)
        {
            foreach (var statement in body)
            {
                switch (statement)
                {
                    case HirQubitDeclarationStatement declaration:
                        registers[declaration.Name] = declaration.Size;
                        break;
                    case HirIfStatement branch:
                        CollectUses(branch.Then, registers);
                        CollectUses(branch.Else, registers);
                        break;
                    case HirForStatement loop:
                        CollectUses(loop.Body, registers);
                        break;
                    case HirWhileStatement loop:
                        CollectUses(loop.Body, registers);
                        break;
                    case HirRepeatStatement loop:
                        CollectUses(loop.Body, registers);
                        break;
                }
            }
        }

        HirExpression RewriteExpression(
            HirExpression source,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            HirExpression result = source switch
            {
                HirUnaryExpression unary =>
                    RewriteUnary(unary, registers, copy),
                HirBinaryExpression binary =>
                    RewriteBinary(binary, registers, copy),
                HirMemberAccessExpression member =>
                    RewriteMember(member, registers, copy),
                HirIndexExpression index =>
                    RewriteIndex(index, registers, copy),
                HirCallExpression call =>
                    RewriteCall(call, registers, copy),
                HirMeasurementExpression measurement =>
                    RewriteMeasurement(measurement, registers, copy),
                HirArrayLiteralExpression literal =>
                    RewriteArrayLiteral(literal, registers, copy),
                _ => copy
                    ? rewrite.DeriveExpression(source)
                    : source,
            };
            return result;
        }

        HirUnaryExpression RewriteUnary(
            HirUnaryExpression unary,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var operand = RewriteExpression(
                unary.Operand,
                registers,
                copy);
            return copy
                ? rewrite.DeriveUnary(
                    unary,
                    unary.Operator,
                    operand)
                : ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : rewrite.RewriteUnary(
                        unary,
                        unary.Operator,
                        operand);
        }

        HirBinaryExpression RewriteBinary(
            HirBinaryExpression binary,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var left = RewriteExpression(binary.Left, registers, copy);
            var right = RewriteExpression(binary.Right, registers, copy);
            return copy
                ? rewrite.DeriveBinary(
                    binary,
                    binary.Operator,
                    left,
                    right)
                : ReferenceEquals(left, binary.Left)
                  && ReferenceEquals(right, binary.Right)
                    ? binary
                    : rewrite.RewriteBinary(
                        binary,
                        binary.Operator,
                        left,
                        right);
        }

        HirMemberAccessExpression RewriteMember(
            HirMemberAccessExpression member,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var receiver = RewriteExpression(
                member.Receiver,
                registers,
                copy);
            return copy
                ? rewrite.DeriveMember(
                    member,
                    receiver,
                    member.MemberName)
                : ReferenceEquals(receiver, member.Receiver)
                    ? member
                    : rewrite.RewriteMember(
                        member,
                        receiver,
                        member.MemberName);
        }

        HirIndexExpression RewriteIndex(
            HirIndexExpression index,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var receiver = RewriteExpression(
                index.Receiver,
                registers,
                copy);
            var indexValue = RewriteExpression(
                index.Index,
                registers,
                copy);
            return copy
                ? rewrite.DeriveIndex(
                    index,
                    receiver,
                    indexValue)
                : ReferenceEquals(receiver, index.Receiver)
                  && ReferenceEquals(indexValue, index.Index)
                    ? index
                    : rewrite.RewriteIndex(
                        index,
                        receiver,
                        indexValue);
        }

        HirMeasurementExpression RewriteMeasurement(
            HirMeasurementExpression measurement,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var target = RewriteExpression(
                measurement.Target,
                registers,
                copy);
            return copy
                ? rewrite.DeriveMeasurement(measurement, target)
                : ReferenceEquals(target, measurement.Target)
                    ? measurement
                    : rewrite.RewriteMeasurement(
                        measurement,
                        target);
        }

        HirArrayLiteralExpression RewriteArrayLiteral(
            HirArrayLiteralExpression literal,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var elements = new HirExpression[literal.Elements.Count];
            var elementChanged = false;
            for (var index = 0; index < literal.Elements.Count; index++)
            {
                var sourceElement = literal.Elements[index];
                var rewrittenElement = RewriteExpression(
                    sourceElement,
                    registers,
                    copy);

                elements[index] = rewrittenElement;
                elementChanged |= !ReferenceEquals(
                    rewrittenElement,
                    sourceElement);
            }

            if (copy)
                return rewrite.DeriveArrayLiteral(literal, elements);
            if (elementChanged)
                return rewrite.RewriteArrayLiteral(literal, elements);
            return literal;
        }

        HirCallExpression RewriteCall(
            HirCallExpression call,
            IReadOnlyDictionary<string, int> registers,
            bool copy)
        {
            var arguments = new HirArgument[call.Arguments.Count];
            var argumentChanged = false;
            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var sourceArgument = call.Arguments[index];
                var expression = RewriteExpression(
                    sourceArgument.Expression,
                    registers,
                    copy);

                HirArgument rewrittenArgument;
                if (copy)
                {
                    rewrittenArgument = rewrite.DeriveArgument(
                        sourceArgument,
                        expression);
                }
                else if (ReferenceEquals(
                             expression,
                             sourceArgument.Expression))
                {
                    rewrittenArgument = sourceArgument;
                }
                else
                {
                    rewrittenArgument = rewrite.RewriteArgument(
                        sourceArgument,
                        expression,
                        sourceArgument.Ownership,
                        sourceArgument.Access);
                }

                arguments[index] = rewrittenArgument;
                argumentChanged |= !ReferenceEquals(
                    rewrittenArgument,
                    sourceArgument);
            }

            var generic = GenericCallee(call);
            HirCallable? specialization = null;
            if (generic is not null)
            {
                var actualNames = new string?[arguments.Length];
                for (var index = 0; index < arguments.Length; index++)
                {
                    actualNames[index] =
                        arguments[index].Expression is HirNameExpression name
                            ? name.Name
                            : null;
                }

                specialization = SpecializationFor(
                    generic,
                    actualNames,
                    registers);
            }

            HirExpression callee;
            if (specialization is null)
            {
                callee = copy
                    ? RewriteExpression(call.Callee, registers, copy: true)
                    : call.Callee;
            }
            else
            {
                var specializationName = QualifiedNameInSourceNamespace(
                    generic!,
                    specialization.Name);
                callee = copy
                    ? rewrite.DeriveQualifiedCallee(
                        call.Callee,
                        specializationName)
                    : rewrite.RewriteQualifiedCallee(
                        call.Callee,
                        specializationName);
            }

            var calleeId = specialization?.Id ?? call.CalleeId;

            if (copy)
            {
                return rewrite.DeriveCall(
                    call,
                    callee,
                    arguments,
                    calleeId);
            }

            var changed = specialization is not null || argumentChanged;
            return changed
                ? rewrite.RewriteCall(
                    call,
                    callee,
                    arguments,
                    calleeId)
                : call;
        }

        HirVariableDeclarationStatement RewriteDeclaration(
            HirVariableDeclarationStatement declaration,
            Dictionary<string, int> registers,
            bool copy)
        {
            var value = RewriteExpression(
                declaration.Value,
                registers,
                copy);
            if (declaration is
                {
                    IsArray: true,
                    Type: QType.Bit,
                }
                && BitArrayLength(declaration) is int length)
            {
                registers[declaration.Name] = length;
            }
            else
            {
                registers.Remove(declaration.Name);
            }

            return copy
                ? rewrite.DeriveVariableDeclaration(
                    declaration,
                    value)
                : ReferenceEquals(value, declaration.Value)
                    ? declaration
                    : rewrite.RewriteVariableDeclaration(
                        declaration,
                        declaration.IsConst,
                        declaration.Type,
                        declaration.Name,
                        value,
                        declaration.IsArray);
        }

        static Dictionary<string, int> Shadow(
            Dictionary<string, int> outer,
            string name)
        {
            var inner = new Dictionary<string, int>(
                outer,
                StringComparer.Ordinal);
            inner.Remove(name);
            return inner;
        }

        HirBlock RewriteBlock(
            HirBlock body,
            Dictionary<string, int> outer,
            bool copy,
            Dictionary<string, int>? finalRegisters = null)
        {
            var registers = new Dictionary<string, int>(
                outer,
                StringComparer.Ordinal);
            var statements = new List<HirStatement>(body.Count);
            var changed = copy;

            foreach (var statement in body)
            {
                HirStatement rewritten;
                switch (statement)
                {
                    case HirCallStatement call:
                    {
                        var rewrittenCall = RewriteCall(
                            call.Call,
                            registers,
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveCallStatement(
                                call,
                                rewrittenCall)
                            : ReferenceEquals(rewrittenCall, call.Call)
                                ? call
                                : rewrite.RewriteCallStatement(
                                    call,
                                    call.Modifiers,
                                    rewrittenCall);
                        break;
                    }

                    case HirVariableDeclarationStatement declaration:
                        rewritten = RewriteDeclaration(
                            declaration,
                            registers,
                            copy);
                        break;

                    case HirAssignmentStatement assignment:
                    {
                        var target = RewriteExpression(
                            assignment.Target,
                            registers,
                            copy);
                        var value = RewriteExpression(
                            assignment.Value,
                            registers,
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveAssignment(
                                assignment,
                                target,
                                value)
                            : ReferenceEquals(value, assignment.Value)
                              && ReferenceEquals(target, assignment.Target)
                                ? assignment
                                : rewrite.RewriteAssignment(
                                    assignment,
                                    target,
                                    value);
                        break;
                    }

                    case HirReturnStatement returned:
                    {
                        var value = RewriteExpression(
                            returned.Value,
                            registers,
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveReturn(returned, value)
                            : ReferenceEquals(value, returned.Value)
                                ? returned
                                : rewrite.RewriteReturn(
                                    returned,
                                    value);
                        break;
                    }

                    case HirIfStatement branch:
                    {
                        var condition = RewriteExpression(
                            branch.Condition,
                            registers,
                            copy);
                        var then = RewriteBlock(
                            branch.Then,
                            registers,
                            copy);
                        var @else = RewriteBlock(
                            branch.Else,
                            registers,
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveIf(
                                branch,
                                condition,
                                then,
                                @else)
                            : ReferenceEquals(
                                  condition,
                                  branch.Condition)
                              && ReferenceEquals(then, branch.Then)
                              && ReferenceEquals(@else, branch.Else)
                                ? branch
                                : rewrite.RewriteIf(
                                    branch,
                                    condition,
                                    then,
                                    @else);
                        break;
                    }

                    case HirForStatement loop:
                    {
                        var from = RewriteExpression(
                            loop.From,
                            registers,
                            copy);
                        var to = RewriteExpression(
                            loop.To,
                            registers,
                            copy);
                        var nested = RewriteBlock(
                            loop.Body,
                            Shadow(registers, loop.Variable),
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveFor(
                                loop,
                                from,
                                to,
                                nested)
                            : ReferenceEquals(from, loop.From)
                              && ReferenceEquals(to, loop.To)
                              && ReferenceEquals(nested, loop.Body)
                                ? loop
                                : rewrite.RewriteFor(
                                    loop,
                                    loop.Variable,
                                    from,
                                    to,
                                    nested);
                        break;
                    }

                    case HirWhileStatement loop:
                    {
                        var condition = RewriteExpression(
                            loop.Condition,
                            registers,
                            copy);
                        var nested = RewriteBlock(
                            loop.Body,
                            registers,
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveWhile(
                                loop,
                                condition,
                                nested)
                            : ReferenceEquals(
                                  condition,
                                  loop.Condition)
                              && ReferenceEquals(nested, loop.Body)
                                ? loop
                                : rewrite.RewriteWhile(
                                    loop,
                                    condition,
                                    nested);
                        break;
                    }

                    case HirRepeatStatement loop:
                    {
                        var bodyFinal =
                            new Dictionary<string, int>(
                                StringComparer.Ordinal);
                        var nested = RewriteBlock(
                            loop.Body,
                            registers,
                            copy,
                            bodyFinal);
                        var until = RewriteExpression(
                            loop.Until,
                            bodyFinal,
                            copy);
                        rewritten = copy
                            ? rewrite.DeriveRepeat(
                                loop,
                                nested,
                                until)
                            : ReferenceEquals(nested, loop.Body)
                              && ReferenceEquals(until, loop.Until)
                                ? loop
                                : rewrite.RewriteRepeat(
                                    loop,
                                    nested,
                                    until);
                        break;
                    }

                    case HirQubitDeclarationStatement qubit:
                        rewritten = copy
                            ? rewrite.DeriveQubitDeclaration(qubit)
                            : qubit;
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"QINTERNAL: monomorphization does not handle {statement.GetType().Name}.");
                }

                statements.Add(rewritten);
                changed |= !ReferenceEquals(rewritten, statement);
            }

            if (finalRegisters is not null)
            {
                finalRegisters.Clear();
                foreach (var (name, size) in registers)
                    finalRegisters[name] = size;
            }

            return copy
                ? rewrite.DeriveBlock(body, statements)
                : changed
                    ? rewrite.RewriteBlock(body, statements)
                    : body;
        }

        HirCallable SpecializationFor(
            HirCallable callee,
            IReadOnlyList<string?> actualNames,
            IReadOnlyDictionary<string, int> registers)
        {
            var bindings = new Dictionary<HirNodeId, int>();
            for (var index = 0;
                 index < callee.Parameters.Count;
                 index++)
            {
                var parameter = callee.Parameters[index];
                if (!IsUnsizedArray(parameter))
                    continue;
                var actualName = index < actualNames.Count
                    ? actualNames[index]
                    : null;
                if (actualName is null
                    || !registers.TryGetValue(
                        actualName,
                        out var size))
                {
                    throw new InvalidOperationException(
                        $"QINTERNAL: call to `{callee.Name}` cannot bind the size of array parameter `{parameter.Name}` after validation");
                }
                bindings[parameter.Id] = size;
            }

            var arrays = callee.Parameters
                .Where(IsUnsizedArray)
                .ToArray();
            if (bindings.Count != arrays.Length)
            {
                throw new InvalidOperationException(
                    $"QINTERNAL: call to `{callee.Name}` bound {bindings.Count} of {arrays.Length} array parameter sizes");
            }

            var sizes = arrays
                .Select(parameter => bindings[parameter.Id])
                .ToArray();
            var key =
                $"{callee.Id.Value}|{string.Join(",", sizes)}";
            if (!specializations.TryGetValue(
                    key,
                    out var specialization))
            {
                var specializationName = MakeName(
                    program.NamespaceOf(callee),
                    callee.Name,
                    sizes);
                specialization = Specialize(
                    callee,
                    bindings,
                    specializationName);
                specializations.Add(key, specialization);
                specializationsBySourceId[callee.Id].Add(
                    specialization);
            }

            return specialization;
        }

        string MakeName(
            string namespacePath,
            string baseName,
            IReadOnlyList<int> sizes)
        {
            if (!namesByNamespace.TryGetValue(
                    namespacePath,
                    out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                namesByNamespace.Add(namespacePath, names);
            }

            var name =
                baseName + "__sz" + string.Join("_", sizes);
            while (names.Contains(name))
                name += "_";
            names.Add(name);
            return name;
        }

        string QualifiedNameInSourceNamespace(
            HirCallable source,
            string localName)
        {
            var namespacePath = program.NamespaceOf(source);
            return namespacePath.Length == 0
                ? localName
                : $"{namespacePath}.{localName}";
        }

        HirCallable Specialize(
            HirCallable source,
            IReadOnlyDictionary<HirNodeId, int> bindings,
            string specializationName)
        {
            var parameters = new HirParameter[source.Parameters.Count];
            for (var index = 0; index < source.Parameters.Count; index++)
            {
                var sourceParameter = source.Parameters[index];
                var registerSize = IsUnsizedArray(sourceParameter)
                    ? bindings[sourceParameter.Id]
                    : sourceParameter.RegisterSize;

                parameters[index] = rewrite.DeriveParameter(
                    sourceParameter,
                    registerSize);
            }

            var registers = new Dictionary<string, int>(
                StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if ((parameter.IsQubitArray
                     || parameter is
                     {
                         Type: QType.Bit,
                         IsArray: true,
                     })
                    && parameter.RegisterSize is int size)
                {
                    registers[parameter.Name] = size;
                }
            }
            CollectUses(source.Body, registers);
            var body = RewriteBlock(
                source.Body,
                registers,
                copy: true);
            return rewrite.DeriveCallable(
                source,
                specializationName,
                parameters,
                body,
                source.IsFunction,
                source.ReturnType,
                source.DisplayName
                ?? QualifiedNameInSourceNamespace(
                    source,
                    source.Name));
        }

        var concreteReplacements =
            new Dictionary<HirNodeId, HirCallable>();
        foreach (var callable in concrete)
        {
            var body = RewriteBlock(
                callable.Body,
                ConcreteRegisters(callable),
                copy: false);
            HirCallable replacement;
            if (ReferenceEquals(body, callable.Body))
            {
                replacement = callable;
            }
            else
            {
                replacement = rewrite.RewriteCallable(
                    callable,
                    callable.Name,
                    callable.Parameters,
                    body,
                    callable.IsFunction,
                    callable.ReturnType,
                    callable.DisplayName);
            }

            concreteReplacements.Add(callable.Id, replacement);
        }

        var result = rewrite.ReplaceCallables(
            program,
            callable =>
                genericById.ContainsKey(callable.Id)
                    ? specializationsBySourceId[callable.Id]
                    : new[]
                    {
                        concreteReplacements[callable.Id],
                    });
        var genericIds = genericById.Keys.ToHashSet();
        if (HasGenericCall(result, genericIds))
        {
            throw new InvalidOperationException(
                "QINTERNAL: monomorphization removed a generic callable while a call still points to it");
        }
        if (result.Callables
            .SelectMany(callable => callable.Parameters)
            .Any(IsUnsizedArray))
        {
            throw new InvalidOperationException(
                "QINTERNAL: monomorphization left an unresolved Qubit[]/bit[] parameter in the specialized HIR");
        }

        return new Result(rewrite.Publish(result));
    }

    private static int? BitArrayLength(
        HirVariableDeclarationStatement declaration) =>
        declaration.Value switch
        {
            HirArrayLiteralExpression literal =>
                literal.Elements.Count,
            HirArrayCreationExpression allocation =>
                allocation.Length,
            _ => null,
        };

    private static bool HasGenericCall(
        HirProgram program,
        IReadOnlySet<HirNodeId> genericIds) =>
        program.Callables.Any(callable =>
            HirExpressions
                .DescendantsAndSelf(callable.Body)
                .OfType<HirCallExpression>()
                .Any(call =>
                    call.CalleeId is { } id
                    && genericIds.Contains(id)));
}
