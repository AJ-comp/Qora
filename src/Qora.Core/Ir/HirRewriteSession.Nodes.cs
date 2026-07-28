namespace Qora.Ir;

internal sealed partial class HirRewriteSession
{
    public HirProgram RewriteProgram(
        HirProgram source,
        IReadOnlyList<HirDeclaration> declarations,
        IReadOnlyList<HirImportDirective> imports)
    {
        RequireAllOwned(declarations, nameof(declarations));
        RequireAllOwned(imports, nameof(imports));
        return new HirProgram(
            PreserveStamp(source),
            declarations,
            imports);
    }

    public HirNamespaceDeclaration RewriteNamespace(
        HirNamespaceDeclaration source,
        string name,
        IReadOnlyList<HirOpenDirective> openDirectives,
        IReadOnlyList<HirDeclaration> declarations)
    {
        RequireAllOwned(openDirectives, nameof(openDirectives));
        RequireAllOwned(declarations, nameof(declarations));
        return new HirNamespaceDeclaration(
            PreserveStamp(source),
            name,
            openDirectives,
            declarations);
    }

    public HirCallable RewriteCallable(
        HirCallable source,
        string name,
        IReadOnlyList<HirParameter> parameters,
        HirBlock body,
        bool isFunction,
        QType? returnType,
        string? displayName)
    {
        RequireAllOwned(parameters, nameof(parameters));
        RequireOwned(body, nameof(body));
        return new HirCallable(
            PreserveStamp(source),
            name,
            parameters,
            body,
            isFunction,
            returnType,
            displayName);
    }

    /// <summary>
    /// Replaces callable declarations while preserving their namespace-tree placement. Namespace wrappers
    /// are rewritten only when a descendant changes; unknown declaration kinds fail explicitly.
    /// </summary>
    public HirProgram ReplaceCallables(
        HirProgram source,
        Func<HirCallable, IReadOnlyList<HirCallable>> replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacement);

        var declarations = ReplaceDeclarations(
            source.Declarations,
            out var changed);
        return changed
            ? RewriteProgram(source, declarations, source.Imports)
            : source;

        IReadOnlyList<HirDeclaration> ReplaceDeclarations(
            IReadOnlyList<HirDeclaration> input,
            out bool listChanged)
        {
            var output = new List<HirDeclaration>();
            listChanged = false;
            foreach (var declaration in input)
            {
                switch (declaration)
                {
                    case HirCallable callable:
                        var replacements = replacement(callable)
                            ?? throw new InvalidOperationException(
                                "A callable replacement callback returned null.");
                        RequireAllOwned(replacements, nameof(replacement));
                        output.AddRange(replacements);
                        listChanged |= replacements.Count != 1
                                       || !ReferenceEquals(
                                           replacements[0],
                                           callable);
                        break;

                    case HirNamespaceDeclaration @namespace:
                        var nested = ReplaceDeclarations(
                            @namespace.Declarations,
                            out var nestedChanged);
                        output.Add(
                            nestedChanged
                                ? RewriteNamespace(
                                    @namespace,
                                    @namespace.Name,
                                    @namespace.OpenDirectives,
                                    nested)
                                : @namespace);
                        listChanged |= nestedChanged;
                        break;

                    default:
                        throw Unhandled(declaration);
                }
            }
            return output;
        }
    }

    public HirParameter RewriteParameter(
        HirParameter source,
        string name,
        QType type,
        int? registerSize,
        bool isArray,
        QOwnershipMode ownership,
        QAccessMode access) =>
        new(
            PreserveStamp(source),
            name,
            type,
            registerSize,
            isArray,
            ownership,
            access);

    public HirBlock RewriteBlock(
        HirBlock source,
        IReadOnlyList<HirStatement> statements)
    {
        RequireAllOwned(statements, nameof(statements));
        return new HirBlock(
            PreserveStamp(source),
            statements);
    }

    public HirQubitDeclarationStatement RewriteQubitDeclaration(
        HirQubitDeclarationStatement source,
        string name,
        int size) =>
        new(PreserveStamp(source), name, size);

    public HirCallStatement RewriteCallStatement(
        HirCallStatement source,
        IReadOnlyList<QGateModifier> modifiers,
        HirCallExpression call)
    {
        RequireOwned(call, nameof(call));
        return new HirCallStatement(
            PreserveStamp(source),
            modifiers,
            call);
    }

    public HirVariableDeclarationStatement RewriteVariableDeclaration(
        HirVariableDeclarationStatement source,
        bool isConst,
        QType? type,
        string name,
        HirExpression value,
        bool isArray)
    {
        RequireOwned(value, nameof(value));
        return new HirVariableDeclarationStatement(
            PreserveStamp(source),
            isConst,
            type,
            name,
            value,
            isArray);
    }

    public HirAssignmentStatement RewriteAssignment(
        HirAssignmentStatement source,
        HirExpression target,
        HirExpression value)
    {
        RequireOwned(target, nameof(target));
        RequireOwned(value, nameof(value));
        return new HirAssignmentStatement(
            PreserveStamp(source),
            target,
            value);
    }

    public HirIfStatement RewriteIf(
        HirIfStatement source,
        HirExpression condition,
        HirBlock then,
        HirBlock @else)
    {
        RequireOwned(condition, nameof(condition));
        RequireOwned(then, nameof(then));
        RequireOwned(@else, nameof(@else));
        return new HirIfStatement(
            PreserveStamp(source),
            condition,
            then,
            @else);
    }

    public HirReturnStatement RewriteReturn(
        HirReturnStatement source,
        HirExpression value)
    {
        RequireOwned(value, nameof(value));
        return new HirReturnStatement(
            PreserveStamp(source),
            value);
    }

    public HirForStatement RewriteFor(
        HirForStatement source,
        string variable,
        HirExpression from,
        HirExpression to,
        HirBlock body)
    {
        RequireOwned(from, nameof(from));
        RequireOwned(to, nameof(to));
        RequireOwned(body, nameof(body));
        return new HirForStatement(
            PreserveStamp(source),
            variable,
            from,
            to,
            body);
    }

    public HirWhileStatement RewriteWhile(
        HirWhileStatement source,
        HirExpression condition,
        HirBlock body)
    {
        RequireOwned(condition, nameof(condition));
        RequireOwned(body, nameof(body));
        return new HirWhileStatement(
            PreserveStamp(source),
            condition,
            body);
    }

    public HirRepeatStatement RewriteRepeat(
        HirRepeatStatement source,
        HirBlock body,
        HirExpression until)
    {
        RequireOwned(body, nameof(body));
        RequireOwned(until, nameof(until));
        return new HirRepeatStatement(
            PreserveStamp(source),
            body,
            until);
    }

    public HirArgument RewriteArgument(
        HirArgument source,
        HirExpression expression,
        QOwnershipMode ownership,
        QAccessMode access)
    {
        RequireOwned(expression, nameof(expression));
        return new HirArgument(
            PreserveStamp(source),
            expression,
            ownership,
            access);
    }

    public HirExpression RewriteExpression(
        HirExpression source) =>
        source switch
        {
            HirMissingExpression => new HirMissingExpression(PreserveStamp(source)),
            HirIntegerLiteralExpression integer =>
                new HirIntegerLiteralExpression(PreserveStamp(source), integer.Value),
            HirLiteralExpression literal =>
                new HirLiteralExpression(PreserveStamp(source), literal.Text),
            HirNameExpression name =>
                new HirNameExpression(PreserveStamp(source), name.Name),
            HirUnaryExpression unary =>
                RewriteUnary(unary, unary.Operator, unary.Operand),
            HirBinaryExpression binary =>
                RewriteBinary(binary, binary.Operator, binary.Left, binary.Right),
            HirMemberAccessExpression member =>
                RewriteMember(member, member.Receiver, member.MemberName),
            HirIndexExpression index =>
                RewriteIndex(index, index.Receiver, index.Index),
            HirCallExpression call =>
                RewriteCall(call, call.Callee, call.Arguments, call.CalleeId),
            HirMeasurementExpression measurement =>
                RewriteMeasurement(measurement, measurement.Target),
            HirArrayLiteralExpression literal =>
                RewriteArrayLiteral(literal, literal.Elements),
            HirArrayCreationExpression creation =>
                new HirArrayCreationExpression(
                    PreserveStamp(source),
                    creation.ElementType,
                    creation.Length),
            _ => throw Unhandled(source),
        };

    public HirNameExpression RewriteName(
        HirNameExpression source,
        string name) =>
        new(PreserveStamp(source), name);

    /// <summary>
    /// Rewrites a resolved callable spelling as an actual name/member tree. The root keeps the source
    /// occurrence identity while any newly introduced namespace prefixes receive synthesis lineage.
    /// </summary>
    public HirExpression RewriteQualifiedCallee(
        HirExpression source,
        string qualifiedName)
    {
        if (HirExpressions.QualifiedNameOf(source) == qualifiedName)
            return source;
        return BuildQualifiedCallee(
            source,
            qualifiedName,
            deriveRoot: false);
    }

    public HirUnaryExpression RewriteUnary(
        HirUnaryExpression source,
        HirUnaryOperator @operator,
        HirExpression operand)
    {
        RequireOwned(operand, nameof(operand));
        return new HirUnaryExpression(
            PreserveStamp(source),
            @operator,
            operand);
    }

    public HirBinaryExpression RewriteBinary(
        HirBinaryExpression source,
        HirBinaryOperator @operator,
        HirExpression left,
        HirExpression right)
    {
        RequireOwned(left, nameof(left));
        RequireOwned(right, nameof(right));
        return new HirBinaryExpression(
            PreserveStamp(source),
            @operator,
            left,
            right);
    }

    public HirMemberAccessExpression RewriteMember(
        HirMemberAccessExpression source,
        HirExpression receiver,
        string member)
    {
        RequireOwned(receiver, nameof(receiver));
        return new HirMemberAccessExpression(
            PreserveStamp(source),
            receiver,
            member);
    }

    public HirIndexExpression RewriteIndex(
        HirIndexExpression source,
        HirExpression receiver,
        HirExpression index)
    {
        RequireOwned(receiver, nameof(receiver));
        RequireOwned(index, nameof(index));
        return new HirIndexExpression(
            PreserveStamp(source),
            receiver,
            index);
    }

    public HirCallExpression RewriteCall(
        HirCallExpression source,
        HirExpression callee,
        IReadOnlyList<HirArgument> arguments,
        HirNodeId? calleeId)
    {
        RequireOwned(callee, nameof(callee));
        RequireAllOwned(arguments, nameof(arguments));
        return new HirCallExpression(
            PreserveStamp(source),
            callee,
            arguments,
            calleeId);
    }

    public HirMeasurementExpression RewriteMeasurement(
        HirMeasurementExpression source,
        HirExpression target)
    {
        RequireOwned(target, nameof(target));
        return new HirMeasurementExpression(
            PreserveStamp(source),
            target);
    }

    public HirArrayLiteralExpression RewriteArrayLiteral(
        HirArrayLiteralExpression source,
        IReadOnlyList<HirExpression> elements)
    {
        RequireAllOwned(elements, nameof(elements));
        return new HirArrayLiteralExpression(
            PreserveStamp(source),
            elements);
    }

    public HirCallable DeriveCallable(
        HirCallable source,
        string name,
        IReadOnlyList<HirParameter> parameters,
        HirBlock body,
        bool isFunction,
        QType? returnType,
        string? displayName)
    {
        RequireAllOwned(parameters, nameof(parameters));
        RequireOwned(body, nameof(body));
        return new HirCallable(
            DeriveStamp(source),
            name,
            parameters,
            body,
            isFunction,
            returnType,
            displayName);
    }

    public HirNamespaceDeclaration DeriveNamespace(
        HirNamespaceDeclaration source,
        string name,
        IReadOnlyList<HirOpenDirective> openDirectives,
        IReadOnlyList<HirDeclaration> declarations)
    {
        RequireAllOwned(openDirectives, nameof(openDirectives));
        RequireAllOwned(declarations, nameof(declarations));
        return new HirNamespaceDeclaration(
            DeriveStamp(source),
            name,
            openDirectives,
            declarations);
    }

    public HirParameter DeriveParameter(
        HirParameter source,
        int? registerSize) =>
        new(
            DeriveStamp(source),
            source.Name,
            source.Type,
            registerSize,
            source.IsArray,
            source.Ownership,
            source.Access);

    public HirBlock DeriveBlock(
        HirBlock source,
        IReadOnlyList<HirStatement> statements)
    {
        RequireAllOwned(statements, nameof(statements));
        return new HirBlock(
            DeriveStamp(source),
            statements);
    }

    public HirStatement DeriveStatement(
        HirStatement source,
        Func<HirExpression, HirExpression>? expressionRewrite = null)
    {
        HirExpression Expr(HirExpression expression) =>
            expressionRewrite?.Invoke(expression)
            ?? DeriveExpression(expression);

        return source switch
        {
            HirQubitDeclarationStatement qubit =>
                new HirQubitDeclarationStatement(
                    DeriveStamp(qubit),
                    qubit.Name,
                    qubit.Size),
            HirCallStatement call =>
                new HirCallStatement(
                    DeriveStamp(call),
                    call.Modifiers,
                    (HirCallExpression)Expr(call.Call)),
            HirVariableDeclarationStatement declaration =>
                new HirVariableDeclarationStatement(
                    DeriveStamp(declaration),
                    declaration.IsConst,
                    declaration.Type,
                    declaration.Name,
                    Expr(declaration.Value),
                    declaration.IsArray),
            HirAssignmentStatement assignment =>
                new HirAssignmentStatement(
                    DeriveStamp(assignment),
                    Expr(assignment.Target),
                    Expr(assignment.Value)),
            HirIfStatement branch =>
                new HirIfStatement(
                    DeriveStamp(branch),
                    Expr(branch.Condition),
                    DeriveBlockTree(branch.Then, expressionRewrite),
                    DeriveBlockTree(branch.Else, expressionRewrite)),
            HirReturnStatement @return =>
                new HirReturnStatement(
                    DeriveStamp(@return),
                    Expr(@return.Value)),
            HirForStatement loop =>
                new HirForStatement(
                    DeriveStamp(loop),
                    loop.Variable,
                    Expr(loop.From),
                    Expr(loop.To),
                    DeriveBlockTree(loop.Body, expressionRewrite)),
            HirWhileStatement loop =>
                new HirWhileStatement(
                    DeriveStamp(loop),
                    Expr(loop.Condition),
                    DeriveBlockTree(loop.Body, expressionRewrite)),
            HirRepeatStatement loop =>
                new HirRepeatStatement(
                    DeriveStamp(loop),
                    DeriveBlockTree(loop.Body, expressionRewrite),
                    Expr(loop.Until)),
            _ => throw Unhandled(source),
        };
    }

    public HirBlock DeriveBlockTree(
        HirBlock source,
        Func<HirExpression, HirExpression>? expressionRewrite = null)
    {
        var statements = source
            .Select(statement => DeriveStatement(statement, expressionRewrite))
            .ToArray();
        return DeriveBlock(source, statements);
    }

    public HirExpression DeriveExpression(HirExpression source)
    {
        return source switch
        {
            HirMissingExpression =>
                new HirMissingExpression(DeriveStamp(source)),
            HirIntegerLiteralExpression integer =>
                new HirIntegerLiteralExpression(DeriveStamp(source), integer.Value),
            HirLiteralExpression literal =>
                new HirLiteralExpression(DeriveStamp(source), literal.Text),
            HirNameExpression name =>
                new HirNameExpression(DeriveStamp(source), name.Name),
            HirUnaryExpression unary =>
                new HirUnaryExpression(
                    DeriveStamp(source),
                    unary.Operator,
                    DeriveExpression(unary.Operand)),
            HirBinaryExpression binary =>
                new HirBinaryExpression(
                    DeriveStamp(source),
                    binary.Operator,
                    DeriveExpression(binary.Left),
                    DeriveExpression(binary.Right)),
            HirMemberAccessExpression member =>
                new HirMemberAccessExpression(
                    DeriveStamp(source),
                    DeriveExpression(member.Receiver),
                    member.MemberName),
            HirIndexExpression index =>
                new HirIndexExpression(
                    DeriveStamp(source),
                    DeriveExpression(index.Receiver),
                    DeriveExpression(index.Index)),
            HirCallExpression call =>
                new HirCallExpression(
                    DeriveStamp(source),
                    DeriveExpression(call.Callee),
                    call.Arguments.Select(DeriveArgument).ToArray(),
                    call.CalleeId),
            HirMeasurementExpression measurement =>
                new HirMeasurementExpression(
                    DeriveStamp(source),
                    DeriveExpression(measurement.Target)),
            HirArrayLiteralExpression literal =>
                new HirArrayLiteralExpression(
                    DeriveStamp(source),
                    literal.Elements.Select(DeriveExpression).ToArray()),
            HirArrayCreationExpression creation =>
                new HirArrayCreationExpression(
                    DeriveStamp(source),
                    creation.ElementType,
                    creation.Length),
            _ => throw Unhandled(source),
        };
    }

    public HirArgument DeriveArgument(HirArgument source) =>
        new(
            DeriveStamp(source),
            DeriveExpression(source.Expression),
            source.Ownership,
            source.Access);

    public HirNameExpression DeriveName(
        HirNameExpression source,
        string name) =>
        new(DeriveStamp(source), name);

    /// <summary>
    /// Copies a resolved callable spelling as an actual name/member tree. Existing canonical trees retain
    /// per-node derivation lineage; namespace prefixes introduced by qualification are synthesized.
    /// </summary>
    public HirExpression DeriveQualifiedCallee(
        HirExpression source,
        string qualifiedName)
    {
        if (HirExpressions.QualifiedNameOf(source) == qualifiedName)
            return DeriveExpression(source);
        return BuildQualifiedCallee(
            source,
            qualifiedName,
            deriveRoot: true);
    }

    private HirExpression BuildQualifiedCallee(
        HirExpression source,
        string qualifiedName,
        bool deriveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        var segments = qualifiedName.Split('.');
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A resolved callable name must contain non-empty namespace/name segments.",
                nameof(qualifiedName));
        }

        var rootStamp = deriveRoot
            ? DeriveStamp(source)
            : PreserveStamp(source);
        if (segments.Length == 1)
            return new HirNameExpression(rootStamp, segments[0]);

        HirExpression prefix = new HirNameExpression(
            SynthesizeStamp(
                source,
                "resolved callable namespace prefix",
                source.Span),
            segments[0]);
        for (var index = 1; index < segments.Length - 1; index++)
        {
            prefix = new HirMemberAccessExpression(
                SynthesizeStamp(
                    source,
                    "resolved callable namespace prefix",
                    source.Span),
                prefix,
                segments[index]);
        }

        return new HirMemberAccessExpression(
            rootStamp,
            prefix,
            segments[^1]);
    }

    public HirUnaryExpression DeriveUnary(
        HirUnaryExpression source,
        HirUnaryOperator @operator,
        HirExpression operand)
    {
        RequireOwned(operand, nameof(operand));
        return new HirUnaryExpression(
            DeriveStamp(source),
            @operator,
            operand);
    }

    public HirBinaryExpression DeriveBinary(
        HirBinaryExpression source,
        HirBinaryOperator @operator,
        HirExpression left,
        HirExpression right)
    {
        RequireOwned(left, nameof(left));
        RequireOwned(right, nameof(right));
        return new HirBinaryExpression(
            DeriveStamp(source),
            @operator,
            left,
            right);
    }

    public HirMemberAccessExpression DeriveMember(
        HirMemberAccessExpression source,
        HirExpression receiver,
        string member)
    {
        RequireOwned(receiver, nameof(receiver));
        return new HirMemberAccessExpression(
            DeriveStamp(source),
            receiver,
            member);
    }

    public HirIndexExpression DeriveIndex(
        HirIndexExpression source,
        HirExpression receiver,
        HirExpression index)
    {
        RequireOwned(receiver, nameof(receiver));
        RequireOwned(index, nameof(index));
        return new HirIndexExpression(
            DeriveStamp(source),
            receiver,
            index);
    }

    public HirCallExpression DeriveCall(
        HirCallExpression source,
        HirExpression callee,
        IReadOnlyList<HirArgument> arguments,
        HirNodeId? calleeId)
    {
        RequireOwned(callee, nameof(callee));
        RequireAllOwned(arguments, nameof(arguments));
        return new HirCallExpression(
            DeriveStamp(source),
            callee,
            arguments,
            calleeId);
    }

    public HirArgument DeriveArgument(
        HirArgument source,
        HirExpression expression)
    {
        RequireOwned(expression, nameof(expression));
        return new HirArgument(
            DeriveStamp(source),
            expression,
            source.Ownership,
            source.Access);
    }

    public HirQubitDeclarationStatement DeriveQubitDeclaration(
        HirQubitDeclarationStatement source) =>
        new(DeriveStamp(source), source.Name, source.Size);

    public HirCallStatement DeriveCallStatement(
        HirCallStatement source,
        HirCallExpression call)
    {
        RequireOwned(call, nameof(call));
        return new HirCallStatement(
            DeriveStamp(source),
            source.Modifiers,
            call);
    }

    public HirVariableDeclarationStatement DeriveVariableDeclaration(
        HirVariableDeclarationStatement source,
        HirExpression value)
    {
        RequireOwned(value, nameof(value));
        return new HirVariableDeclarationStatement(
            DeriveStamp(source),
            source.IsConst,
            source.Type,
            source.Name,
            value,
            source.IsArray);
    }

    public HirAssignmentStatement DeriveAssignment(
        HirAssignmentStatement source,
        HirExpression target,
        HirExpression value)
    {
        RequireOwned(target, nameof(target));
        RequireOwned(value, nameof(value));
        return new HirAssignmentStatement(
            DeriveStamp(source),
            target,
            value);
    }

    public HirIfStatement DeriveIf(
        HirIfStatement source,
        HirExpression condition,
        HirBlock then,
        HirBlock @else)
    {
        RequireOwned(condition, nameof(condition));
        RequireOwned(then, nameof(then));
        RequireOwned(@else, nameof(@else));
        return new HirIfStatement(
            DeriveStamp(source),
            condition,
            then,
            @else);
    }

    public HirReturnStatement DeriveReturn(
        HirReturnStatement source,
        HirExpression value)
    {
        RequireOwned(value, nameof(value));
        return new HirReturnStatement(
            DeriveStamp(source),
            value);
    }

    public HirForStatement DeriveFor(
        HirForStatement source,
        HirExpression from,
        HirExpression to,
        HirBlock body)
    {
        RequireOwned(from, nameof(from));
        RequireOwned(to, nameof(to));
        RequireOwned(body, nameof(body));
        return new HirForStatement(
            DeriveStamp(source),
            source.Variable,
            from,
            to,
            body);
    }

    public HirWhileStatement DeriveWhile(
        HirWhileStatement source,
        HirExpression condition,
        HirBlock body)
    {
        RequireOwned(condition, nameof(condition));
        RequireOwned(body, nameof(body));
        return new HirWhileStatement(
            DeriveStamp(source),
            condition,
            body);
    }

    public HirRepeatStatement DeriveRepeat(
        HirRepeatStatement source,
        HirBlock body,
        HirExpression until)
    {
        RequireOwned(body, nameof(body));
        RequireOwned(until, nameof(until));
        return new HirRepeatStatement(
            DeriveStamp(source),
            body,
            until);
    }

    public HirMeasurementExpression DeriveMeasurement(
        HirMeasurementExpression source,
        HirExpression target)
    {
        RequireOwned(target, nameof(target));
        return new HirMeasurementExpression(
            DeriveStamp(source),
            target);
    }

    public HirArrayLiteralExpression DeriveArrayLiteral(
        HirArrayLiteralExpression source,
        IReadOnlyList<HirExpression> elements)
    {
        RequireAllOwned(elements, nameof(elements));
        return new HirArrayLiteralExpression(
            DeriveStamp(source),
            elements);
    }

    public HirExpression SynthesizeName(
        HirNode origin,
        string reason,
        string name,
        SourceSpan? span = null) =>
        new HirNameExpression(
            SynthesizeStamp(origin, reason, span),
            name);

    public HirMeasurementExpression SynthesizeMeasurement(
        HirNode origin,
        string reason,
        HirExpression target,
        SourceSpan? span = null)
    {
        RequireOwned(target, nameof(target));
        return new HirMeasurementExpression(
            SynthesizeStamp(origin, reason, span),
            target);
    }

    public HirVariableDeclarationStatement SynthesizeVariableDeclaration(
        HirNode origin,
        string reason,
        bool isConst,
        QType? type,
        string name,
        HirExpression value,
        bool isArray = false,
        SourceSpan? span = null)
    {
        RequireOwned(value, nameof(value));
        return new HirVariableDeclarationStatement(
            SynthesizeStamp(origin, reason, span),
            isConst,
            type,
            name,
            value,
            isArray);
    }

    public HirAssignmentStatement SynthesizeAssignment(
        HirNode origin,
        string reason,
        HirExpression target,
        HirExpression value,
        SourceSpan? span = null)
    {
        RequireOwned(target, nameof(target));
        RequireOwned(value, nameof(value));
        return new HirAssignmentStatement(
            SynthesizeStamp(origin, reason, span),
            target,
            value);
    }

    private static InvalidOperationException Unhandled(HirNode node) =>
        new(
            $"QINTERNAL: {nameof(HirRewriteSession)} does not handle HIR node " +
            node.GetType().Name);
}
