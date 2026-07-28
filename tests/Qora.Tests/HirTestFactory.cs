using Janglim.FrontEnd.Ast;
using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// Test-only semantic construction surface for HIR. Tests describe the tree they need
/// without minting node IDs or handling construction sessions directly.
/// </summary>
internal sealed class HirTestFactory
{
    private readonly HirConstructionCore _core;
    private readonly AstToHirLoweringSession _lowering;
    private int _nextAuxiliaryDocumentId = 1;
    private bool _published;

    public HirTestFactory()
        : this(
            new SourceDocumentRef(
                CompilationId.New(),
                new CompilationRevision(0),
                new SourceDocumentId(0)))
    {
    }

    public HirTestFactory(SourceDocumentRef document)
    {
        if (document.CompilationId.Value == Guid.Empty)
            throw new ArgumentException(
                "A HIR test factory requires an exact source document.",
                nameof(document));
        CompilationId = document.CompilationId;
        CompilationRevision = document.CompilationRevision;
        Document = document;
        _core = new HirConstructionCore(
            CompilationId,
            CompilationRevision);
        _core.RegisterDocument(Document);
        _lowering = _core.BeginLowering(Document);
    }

    private CompilationId CompilationId { get; }
    private CompilationRevision CompilationRevision { get; }
    private SourceDocumentRef Document { get; }

    public void RegisterEntryDocumentAgain() =>
        _core.RegisterDocument(Document);

    public void BeginEntryDocumentLoweringAgain() =>
        _core.BeginLowering(Document);

    public void BeginEntryDocumentLoweringAfterBindingSourceSet()
    {
        var document = new SourceDocumentSnapshot(
            Document,
            string.Empty,
            path: null);
        var syntax = new SyntaxSnapshot(
            document,
            Array.Empty<QoraToken>(),
            parseTree: null,
            ast: null,
            parseTreeText: string.Empty,
            astText: string.Empty,
            diagnostics: Array.Empty<QoraError>());
        _core.BindSourceSet(
            new SourceSetSnapshot(
                CompilationId,
                CompilationRevision,
                Document,
                new[] { document },
                new[] { syntax },
                new ImportGraph(
                    new[] { Document },
                    Array.Empty<ImportEdge>())));
        _core.BeginLowering(Document);
    }

    public HirExpression Missing() =>
        _lowering.Missing();

    public HirIntegerLiteralExpression Integer(long value) =>
        (HirIntegerLiteralExpression)_lowering.Integer(value);

    public HirIntegerLiteralExpression Integer(
        long value,
        int spanStart,
        int spanEnd) =>
        (HirIntegerLiteralExpression)_lowering.Integer(
            value,
            new SourceSpan(
                Document,
                spanStart,
                spanEnd));

    public HirIntegerLiteralExpression IntegerFromSiblingSession(
        long value)
    {
        var document = new SourceDocumentRef(
            CompilationId,
            CompilationRevision,
            new SourceDocumentId(_nextAuxiliaryDocumentId++));
        _core.RegisterDocument(document);
        return (HirIntegerLiteralExpression)_core
            .BeginLowering(document)
            .Integer(value);
    }

    public HirLiteralExpression Literal(string text) =>
        (HirLiteralExpression)_lowering.Literal(text);

    public HirNameExpression Name(string name) =>
        (HirNameExpression)_lowering.Name(name);

    public HirUnaryExpression Negate(HirExpression operand) =>
        (HirUnaryExpression)_lowering.Unary(
            HirUnaryOperator.Negate,
            operand);

    public HirUnaryExpression Not(HirExpression operand) =>
        (HirUnaryExpression)_lowering.Unary(
            HirUnaryOperator.LogicalNot,
            operand);

    public HirBinaryExpression Binary(
        HirBinaryOperator @operator,
        HirExpression left,
        HirExpression right) =>
        (HirBinaryExpression)_lowering.Binary(
            @operator,
            left,
            right);

    public HirBinaryExpression Add(
        HirExpression left,
        HirExpression right) =>
        Binary(HirBinaryOperator.Add, left, right);

    public HirBinaryExpression Subtract(
        HirExpression left,
        HirExpression right) =>
        Binary(HirBinaryOperator.Subtract, left, right);

    public HirBinaryExpression Equal(
        HirExpression left,
        HirExpression right) =>
        Binary(HirBinaryOperator.Equal, left, right);

    public HirMemberAccessExpression Member(
        HirExpression receiver,
        string member) =>
        (HirMemberAccessExpression)_lowering.Member(
            receiver,
            member);

    public HirMemberAccessExpression Member(
        string receiver,
        string member) =>
        Member(Name(receiver), member);

    public HirIndexExpression Index(
        HirExpression receiver,
        HirExpression index) =>
        (HirIndexExpression)_lowering.Index(
            receiver,
            index);

    public HirIndexExpression Index(
        string receiver,
        long index) =>
        Index(Name(receiver), Integer(index));

    public HirArgument Argument(
        HirExpression expression,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly) =>
        _lowering.Argument(
            expression,
            ownership,
            access);

    public HirCallExpression Call(
        string name,
        params HirExpression[] arguments) =>
        Call(
            name,
            arguments
                .Select(expression => Argument(expression))
                .ToArray());

    public HirCallExpression Call(
        string name,
        IReadOnlyList<HirArgument> arguments) =>
        _lowering.Call(
            name,
            arguments);

    public HirCallExpression Call(
        HirCallable callee,
        IReadOnlyList<HirArgument> arguments) =>
        _lowering.Call(
            callee.Name,
            arguments,
            callee.Id);

    public HirMeasurementExpression Measurement(HirExpression target) =>
        (HirMeasurementExpression)_lowering.Measurement(target);

    public HirArrayLiteralExpression ArrayLiteral(
        IReadOnlyList<HirExpression> elements) =>
        (HirArrayLiteralExpression)_lowering.ArrayLiteral(elements);

    public HirArrayCreationExpression ArrayCreation(
        QType elementType,
        int length) =>
        (HirArrayCreationExpression)_lowering.ArrayCreation(
            elementType,
            length);

    public HirParameter Parameter(
        string name,
        QType type,
        int? registerSize = null,
        bool isArray = false,
        QOwnershipMode ownership = QOwnershipMode.Borrowed,
        QAccessMode access = QAccessMode.ReadOnly) =>
        _lowering.Parameter(
            name,
            type,
            registerSize,
            isArray,
            ownership,
            access);

    public HirBlock Block(IReadOnlyList<HirStatement> statements) =>
        _lowering.Block(statements);

    public HirCallable Callable(
        string name,
        IReadOnlyList<HirParameter>? parameters = null,
        IReadOnlyList<HirStatement>? body = null,
        bool isFunction = false,
        QType? returnType = null,
        string? displayName = null) =>
        _lowering.Callable(
            name,
            parameters ?? Array.Empty<HirParameter>(),
            Block(body ?? Array.Empty<HirStatement>()),
            isFunction,
            returnType,
            displayName);

    public HirNamespaceDeclaration Namespace(
        string name,
        IReadOnlyList<HirDeclaration> declarations,
        IReadOnlyList<HirOpenDirective>? openDirectives = null) =>
        _lowering.Namespace(
            name,
            openDirectives ?? Array.Empty<HirOpenDirective>(),
            declarations);

    public HirImportDirective Import(string target) =>
        _lowering.Import(target);

    public HirOpenDirective Open(string target) =>
        _lowering.Open(target);

    public HirProgram Program(
        IReadOnlyList<HirDeclaration> declarations,
        IReadOnlyList<HirImportDirective>? imports = null) =>
        _lowering.Program(
            declarations,
            imports);

    public HirQubitDeclarationStatement QubitDeclaration(
        string name,
        int size) =>
        _lowering.QubitDeclaration(name, size);

    public HirCallStatement Apply(
        string name,
        params HirExpression[] arguments) =>
        Apply(
            Array.Empty<QGateModifier>(),
            Call(name, arguments));

    public HirCallStatement Apply(
        IReadOnlyList<QGateModifier> modifiers,
        HirCallExpression call) =>
        _lowering.CallStatement(
            modifiers,
            call);

    public HirVariableDeclarationStatement Variable(
        string name,
        HirExpression value,
        QType? type = null,
        bool isConst = false,
        bool isArray = false) =>
        _lowering.VariableDeclaration(
            isConst,
            type,
            name,
            value,
            isArray);

    public HirAssignmentStatement Assignment(
        HirExpression target,
        HirExpression value) =>
        _lowering.Assignment(
            target,
            value);

    public HirIfStatement If(
        HirExpression condition,
        IReadOnlyList<HirStatement> then,
        IReadOnlyList<HirStatement>? @else = null) =>
        _lowering.If(
            condition,
            Block(then),
            Block(@else ?? Array.Empty<HirStatement>()));

    public HirReturnStatement Return(HirExpression value) =>
        _lowering.Return(value);

    public HirForStatement For(
        string variable,
        HirExpression from,
        HirExpression to,
        IReadOnlyList<HirStatement> body) =>
        _lowering.For(
            variable,
            from,
            to,
            Block(body));

    public HirWhileStatement While(
        HirExpression condition,
        IReadOnlyList<HirStatement> body) =>
        _lowering.While(
            condition,
            Block(body));

    public HirRepeatStatement Repeat(
        IReadOnlyList<HirStatement> body,
        HirExpression until) =>
        _lowering.Repeat(
            Block(body),
            until);

    public HirProgram Publish(HirProgram program)
    {
        if (_published)
        {
            throw new InvalidOperationException(
                "This HIR test factory has already published its program.");
        }

        var published = _lowering.Publish(program);
        _published = true;
        return published;
    }

    public HirProgram Lower(AstSymbol ast)
    {
        ArgumentNullException.ThrowIfNull(ast);
        if (_published)
        {
            throw new InvalidOperationException(
                "This HIR test factory has already published its program.");
        }

        var program = AstToHirLowering.Lower(ast, _lowering);
        _published = true;
        return program;
    }

    public HirProgram PublishProgram(
        IReadOnlyList<HirDeclaration> declarations,
        IReadOnlyList<HirImportDirective>? imports = null) =>
        Publish(
            Program(
                declarations,
                imports));

    public HirPipelineBuilder CreatePipelineBuilder()
    {
        if (!_published)
        {
            throw new InvalidOperationException(
                "Publish the test HIR program before creating its pipeline.");
        }

        return new HirPipelineBuilder(
            CompilationId,
            CompilationRevision,
            _core);
    }

    public HirPipelineBuilder CreateUnstartedPipelineBuilder()
    {
        if (_published)
        {
            throw new InvalidOperationException(
                "Use an unpublished HIR test factory for an empty pipeline.");
        }

        return new HirPipelineBuilder(
            CompilationId,
            CompilationRevision,
            _core);
    }

    /// <summary>
    /// Builds an intentionally invalid rewrite result for the publication-boundary invariant test.
    /// The returned root was published by one rewrite session, but it contains a declaration and value
    /// created by a second rewrite session that remains unpublished.
    /// </summary>
    public HirRewriteResult RewriteWithUnpublishedForeignNode(
        HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.ConstructionCore, _core))
        {
            throw new ArgumentException(
                "The source snapshot belongs to another HIR test factory.",
                nameof(source));
        }

        var callable = source.Program.Callables.FirstOrDefault()
            ?? throw new ArgumentException(
                "The source program needs a callable for the mixed-session test.",
                nameof(source));
        var primary = _core.BeginRewrite(
            source,
            "test primary rewrite");
        var foreign = _core.BeginRewrite(
            source,
            "test unpublished foreign rewrite");
        var foreignValue = foreign.SynthesizeName(
            callable,
            "test foreign initializer",
            "foreign");
        var foreignDeclaration = foreign.SynthesizeVariableDeclaration(
            callable,
            "test foreign declaration",
            isConst: false,
            QType.Int,
            "foreign",
            foreignValue);
        var body = new HirBlock(
            primary.PreserveStamp(callable.Body),
            callable.Body
                .Append<HirStatement>(foreignDeclaration)
                .ToArray());
        var rewrittenCallable = primary.RewriteCallable(
            callable,
            callable.Name,
            callable.Parameters,
            body,
            callable.IsFunction,
            callable.ReturnType,
            callable.DisplayName);
        var root = primary.ReplaceCallables(
            source.Program,
            item =>
                ReferenceEquals(item, callable)
                    ? new[] { rewrittenCallable }
                    : new[] { item });
        return primary.Publish(root);
    }

    public HirRewriteResult RewriteProgramRoot(
        HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.ConstructionCore, _core))
        {
            throw new ArgumentException(
                "The source snapshot belongs to another HIR test factory.",
                nameof(source));
        }

        var rewrite = _core.BeginRewrite(
            source,
            "test root rewrite");
        return rewrite.Publish(
            rewrite.RewriteProgram(
                source.Program,
                source.Program.Declarations,
                source.Program.Imports));
    }

    public HirRewriteResult MoveFirstStatementToSecondCallable(
        HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.ConstructionCore, _core))
        {
            throw new ArgumentException(
                "The source snapshot belongs to another HIR test factory.",
                nameof(source));
        }
        if (source.Program.Callables.Count < 2
            || source.Program.Callables[0].Body.Count == 0)
        {
            throw new ArgumentException(
                "The owner-change test requires two callables and a statement in the first.",
                nameof(source));
        }

        var rewrite = _core.BeginRewrite(
            source,
            "test statement owner change");
        var first = source.Program.Callables[0];
        var second = source.Program.Callables[1];
        var moved = first.Body[0];
        var firstBody = rewrite.RewriteBlock(
            first.Body,
            first.Body.Skip(1).ToArray());
        var secondBody = rewrite.RewriteBlock(
            second.Body,
            second.Body
                .Append(moved)
                .ToArray());
        var rewrittenFirst = rewrite.RewriteCallable(
            first,
            first.Name,
            first.Parameters,
            firstBody,
            first.IsFunction,
            first.ReturnType,
            first.DisplayName);
        var rewrittenSecond = rewrite.RewriteCallable(
            second,
            second.Name,
            second.Parameters,
            secondBody,
            second.IsFunction,
            second.ReturnType,
            second.DisplayName);
        var root = rewrite.ReplaceCallables(
            source.Program,
            callable =>
                ReferenceEquals(callable, first)
                    ? new[] { rewrittenFirst }
                    : ReferenceEquals(callable, second)
                        ? new[] { rewrittenSecond }
                        : new[] { callable });
        return rewrite.Publish(root);
    }

    public HirRewriteResult MoveFirstStatementIntoFollowingIf(
        HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.ConstructionCore, _core))
        {
            throw new ArgumentException(
                "The source snapshot belongs to another HIR test factory.",
                nameof(source));
        }

        var callable = source.Program.Callables.FirstOrDefault()
            ?? throw new ArgumentException(
                "The parent-change test requires a callable.",
                nameof(source));
        if (callable.Body.Count < 2
            || callable.Body[1] is not HirIfStatement branch)
        {
            throw new ArgumentException(
                "The parent-change test requires a statement followed by an if statement.",
                nameof(source));
        }

        var rewrite = _core.BeginRewrite(
            source,
            "test statement structural parent change");
        var moved = callable.Body[0];
        var then = rewrite.RewriteBlock(
            branch.Then,
            branch.Then
                .Prepend(moved)
                .ToArray());
        var rewrittenBranch = rewrite.RewriteIf(
            branch,
            branch.Condition,
            then,
            branch.Else);
        var body = rewrite.RewriteBlock(
            callable.Body,
            callable.Body
                .Skip(1)
                .Select(statement =>
                    ReferenceEquals(statement, branch)
                        ? rewrittenBranch
                        : statement)
                .ToArray());
        var rewrittenCallable = rewrite.RewriteCallable(
            callable,
            callable.Name,
            callable.Parameters,
            body,
            callable.IsFunction,
            callable.ReturnType,
            callable.DisplayName);
        var root = rewrite.ReplaceCallables(
            source.Program,
            item =>
                ReferenceEquals(item, callable)
                    ? new[] { rewrittenCallable }
                    : new[] { item });
        return rewrite.Publish(root);
    }

    public HirRewriteResult ChangeFirstIntegerSourceSpan(
        HirSnapshot source,
        int spanStart,
        int spanEnd)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.ConstructionCore, _core))
        {
            throw new ArgumentException(
                "The source snapshot belongs to another HIR test factory.",
                nameof(source));
        }

        var callable = source.Program.Callables.FirstOrDefault()
            ?? throw new ArgumentException(
                "The span-change test requires a callable.",
                nameof(source));
        var declaration = callable.Body
            .OfType<HirVariableDeclarationStatement>()
            .FirstOrDefault();
        if (declaration?.Value is not HirIntegerLiteralExpression integer)
        {
            throw new ArgumentException(
                "The span-change test requires an integer variable initializer.",
                nameof(source));
        }

        var rewrite = _core.BeginRewrite(
            source,
            "test implicit source span change");
        var changedInteger = new HirIntegerLiteralExpression(
            new HirNodeStamp(
                _core,
                rewrite,
                integer.Id,
                new SourceSpan(
                    Document,
                    spanStart,
                    spanEnd)),
            integer.Value);
        var rewrittenDeclaration = rewrite.RewriteVariableDeclaration(
            declaration,
            declaration.IsConst,
            declaration.Type,
            declaration.Name,
            changedInteger,
            declaration.IsArray);
        var body = rewrite.RewriteBlock(
            callable.Body,
            callable.Body
                .Select(statement =>
                    ReferenceEquals(statement, declaration)
                        ? rewrittenDeclaration
                        : statement)
                .ToArray());
        var rewrittenCallable = rewrite.RewriteCallable(
            callable,
            callable.Name,
            callable.Parameters,
            body,
            callable.IsFunction,
            callable.ReturnType,
            callable.DisplayName);
        var root = rewrite.ReplaceCallables(
            source.Program,
            item =>
                ReferenceEquals(item, callable)
                    ? new[] { rewrittenCallable }
                    : new[] { item });
        return rewrite.Publish(root);
    }

    public static Monomorphizer.Result Monomorphize(
        HirSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Monomorphizer.Run(
            source.Program,
            source.ConstructionCore.BeginRewrite(
                source,
                nameof(Monomorphizer)));
    }

    public static HirCallable DeriveCallableBody(
        HirSnapshot source,
        string callableName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var callable = source.Program.Callables.Single(
            item => item.Name == callableName);
        var rewrite = source.ConstructionCore.BeginRewrite(
            source,
            "test foreign callable tree");
        var body = rewrite.DeriveBlockTree(callable.Body);
        var rewritten = rewrite.RewriteCallable(
            callable,
            callable.Name,
            callable.Parameters,
            body,
            callable.IsFunction,
            callable.ReturnType,
            callable.DisplayName);
        var root = rewrite.ReplaceCallables(
            source.Program,
            item =>
                ReferenceEquals(item, callable)
                    ? new[] { rewritten }
                    : new[] { item });
        return rewrite.Publish(root)
            .Root
            .Callables
            .Single(item => item.Id == callable.Id);
    }
}
