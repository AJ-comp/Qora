using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class HirImmutabilityTests
{
    [Fact]
    public void CollectionBearingHirRecordsDefensivelyFreezeConstructorInputs()
    {
        var parameter = new QParam("p", QType.Int, null);
        var statement = new QDecl(false, QType.Int, "x", new QText(new QNumLit(1)));
        var argument = new QTextArg(new QNumLit(1));
        var expression = new QText(new QNumLit(1));

        var parameters = new List<QParam> { parameter };
        var body = new List<QStmt> { statement };
        var operation = new QOperation("F", parameters, body);
        var operations = new List<QOperation> { operation };
        var imports = new List<QImport> { new("lib.qor") };
        var openDirectives = new List<QOpen> { new("Lib") };
        var opens = new Dictionary<string, IReadOnlyList<QOpen>>
        {
            ["App"] = openDirectives,
        };
        var program = new QProgram(operations, imports, opens);

        var modifiers = new List<QGateModifier> { QGateModifier.Controlled };
        var arguments = new List<QArg> { argument };
        var gate = new QGate(modifiers, "F", arguments);
        var thenBody = new List<QStmt> { statement };
        var elseBody = new List<QStmt> { statement };
        var conditional = new QIf(new QCond(new QNumLit(1)), thenBody, elseBody);
        var forBody = new List<QStmt> { statement };
        var loop = new QFor("i", new QNumLit(0), new QNumLit(1), forBody);
        var whileBody = new List<QStmt> { statement };
        var whileLoop = new QWhile(new QCond(new QNumLit(1)), whileBody);
        var repeatBody = new List<QStmt> { statement };
        var repeat = new QRepeat(repeatBody, new QCond(new QNumLit(1)));
        var elements = new List<QExpr> { expression };
        var literal = new QArrayLiteral(elements);
        var callArguments = new List<QNode> { new QNumLit(1) };
        var call = new QCallNode("F", callArguments);
        var slots = new List<IParamSpec> { parameter };
        var signature = new GateSig("F", slots);

        parameters.Clear();
        body.Clear();
        operations.Clear();
        imports.Clear();
        openDirectives.Clear();
        opens.Clear();
        modifiers.Clear();
        arguments.Clear();
        thenBody.Clear();
        elseBody.Clear();
        forBody.Clear();
        whileBody.Clear();
        repeatBody.Clear();
        elements.Clear();
        callArguments.Clear();
        slots.Clear();

        Assert.Single(operation.Params);
        Assert.Single(operation.Body);
        Assert.Single(program.Operations);
        Assert.Single(program.Imports!);
        Assert.Single(program.Opens!);
        Assert.Single(program.Opens!["App"]);
        Assert.Single(gate.Modifiers);
        Assert.Single(gate.Args);
        Assert.Single(conditional.Then);
        Assert.Single(conditional.Else);
        Assert.Single(loop.Body);
        Assert.Single(whileLoop.Body);
        Assert.Single(repeat.Body);
        Assert.Single(literal.Elements);
        Assert.Single(call.Args);
        Assert.Single(signature.Params);
    }

    [Fact]
    public void WithExpressionsCannotReintroduceMutableCollectionAliases()
    {
        var parameter = new QParam("p", QType.Int, null);
        var statement = new QDecl(false, QType.Int, "x", new QText(new QNumLit(1)));
        var argument = new QTextArg(new QNumLit(1));
        var operation = new QOperation("F", Array.Empty<QParam>(), Array.Empty<QStmt>());
        var program = new QProgram(Array.Empty<QOperation>());
        var gate = new QGate(Array.Empty<QGateModifier>(), "F", Array.Empty<QArg>());
        var conditional = new QIf(
            new QCond(new QNumLit(1)),
            Array.Empty<QStmt>(),
            Array.Empty<QStmt>());
        var loop = new QFor(
            "i",
            new QNumLit(0),
            new QNumLit(1),
            Array.Empty<QStmt>());
        var whileLoop = new QWhile(new QCond(new QNumLit(1)), Array.Empty<QStmt>());
        var repeat = new QRepeat(Array.Empty<QStmt>(), new QCond(new QNumLit(1)));
        var literal = new QArrayLiteral(Array.Empty<QExpr>());
        var call = new QCallNode("F", Array.Empty<QNode>());
        var signature = new GateSig("F", Array.Empty<IParamSpec>());

        var parameters = new List<QParam> { parameter };
        var body = new List<QStmt> { statement };
        var operations = new List<QOperation> { operation };
        var imports = new List<QImport> { new("lib.qor") };
        var openDirectives = new List<QOpen> { new("Lib") };
        var opens = new Dictionary<string, IReadOnlyList<QOpen>>
        {
            ["App"] = openDirectives,
        };
        var modifiers = new List<QGateModifier> { QGateModifier.Controlled };
        var arguments = new List<QArg> { argument };
        var thenBody = new List<QStmt> { statement };
        var elseBody = new List<QStmt> { statement };
        var forBody = new List<QStmt> { statement };
        var whileBody = new List<QStmt> { statement };
        var repeatBody = new List<QStmt> { statement };
        var elements = new List<QExpr> { new QText(new QNumLit(1)) };
        var callArguments = new List<QNode> { new QNumLit(1) };
        var slots = new List<IParamSpec> { parameter };

        var rewrittenOperation = operation with { Params = parameters, Body = body };
        var rewrittenProgram = program with
        {
            Operations = operations,
            Imports = imports,
            Opens = opens,
        };
        var rewrittenGate = gate with { Modifiers = modifiers, Args = arguments };
        var rewrittenConditional = conditional with { Then = thenBody, Else = elseBody };
        var rewrittenLoop = loop with { Body = forBody };
        var rewrittenWhile = whileLoop with { Body = whileBody };
        var rewrittenRepeat = repeat with { Body = repeatBody };
        var rewrittenLiteral = literal with { Elements = elements };
        var rewrittenCall = call with { Args = callArguments };
        var rewrittenSignature = signature with { Params = slots };

        parameters.Clear();
        body.Clear();
        operations.Clear();
        imports.Clear();
        openDirectives.Clear();
        opens.Clear();
        modifiers.Clear();
        arguments.Clear();
        thenBody.Clear();
        elseBody.Clear();
        forBody.Clear();
        whileBody.Clear();
        repeatBody.Clear();
        elements.Clear();
        callArguments.Clear();
        slots.Clear();

        Assert.Single(rewrittenOperation.Params);
        Assert.Single(rewrittenOperation.Body);
        Assert.Single(rewrittenProgram.Operations);
        Assert.Single(rewrittenProgram.Imports!);
        Assert.Single(rewrittenProgram.Opens!["App"]);
        Assert.Single(rewrittenGate.Modifiers);
        Assert.Single(rewrittenGate.Args);
        Assert.Single(rewrittenConditional.Then);
        Assert.Single(rewrittenConditional.Else);
        Assert.Single(rewrittenLoop.Body);
        Assert.Single(rewrittenWhile.Body);
        Assert.Single(rewrittenRepeat.Body);
        Assert.Single(rewrittenLiteral.Elements);
        Assert.Single(rewrittenCall.Args);
        Assert.Single(rewrittenSignature.Params);
    }

    [Fact]
    public void PublishedSemanticCollectionsCannotBeChangedThroughCasts()
    {
        var compilation = QoraCompiler.Compile(
            """
            operation Touch(p: Qubit) {
                X(p);
            }

            operation Main() {
                use q = Qubit[1];
                var x: int = 1;
                var y: int = x;
                Touch(q[0]);
            }
            """);
        Assert.True(compilation.Succeeded);

        var analyzedArtifact = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var analyzed = analyzedArtifact.Source;
        var model = analyzedArtifact.Model;
        var main = analyzed.Program.Operations.Single(operation => operation.Name == "Main");
        var declaration = main.Body.OfType<QDecl>().Single(item => item.Name == "x");
        var symbol = Assert.IsType<Symbol>(model.FindSymbol(declaration.Id));
        var graph = Assert.IsType<QubitGraph>(model.Graph(main.Id));
        var scopeGraph = Assert.IsType<HirScopeGraph>(model.ScopeGraph);

        AssertCannotAdd(symbol.Uses, new UseSite(-1, "mutation", -1));
        AssertCannotAdd(graph.Nodes, graph.Nodes[0]);
        AssertDictionaryCannotRemove(scopeGraph.Symbols, symbol.Id);
        AssertDictionaryCannotRemove(scopeGraph.Scopes, scopeGraph.RootScope.Id);
        AssertDictionaryCannotRemove(scopeGraph.CallableScopes, main.Id);

        Assert.Single(symbol.Uses);
        Assert.NotEmpty(graph.Nodes);
        Assert.Same(symbol, scopeGraph.Symbols[symbol.Id]);
        Assert.Same(scopeGraph.RootScope, scopeGraph.Scopes[scopeGraph.RootScope.Id]);
        Assert.Same(model.FindRootScope(main.Id), scopeGraph.CallableScopes[main.Id]);

        AssertDictionaryCannotRemove(QoraGates.Gates, "H");
        AssertDictionaryCannotRemove(QoraGates.Names, "H");
        AssertDictionaryCannotRemove(QoraGates.Functions, QoraGates.BitsAsInt);
        AssertSetCannotRemove(QoraGates.Rotations, "Rx");
        AssertSetCannotRemove(QoraGates.QasmReserved, "def");
        Assert.True(QoraGates.Gates.ContainsKey("H"));
        Assert.Contains("Rx", QoraGates.Rotations);
    }

    [Fact]
    public void PublishedScopeGraphRejectsEveryInternalMutationPath()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { var x: int = 1; }");
        Assert.True(compilation.Succeeded);

        var validation = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);
        var model = validation.Model;
        var graph = Assert.IsType<HirScopeGraph>(model.ScopeGraph);
        var main = validation.Program.Operations.Single();
        var callableScope = Assert.IsType<Scope>(
            graph.FindCallableScope(main.Id));
        var callableSymbol = Assert.IsType<Symbol>(
            graph.FindDeclaration(main.Id));
        var usesBefore = callableSymbol.Uses;
        var lateRootSymbol = new Symbol(
            graph.RootScope.Id,
            "Late",
            SymbolKind.Operation,
            declarationNodeId: -1);
        var lateLocalSymbol = new Symbol(
            callableScope.Id,
            "late",
            SymbolKind.Var,
            QType.Int);

        Assert.True(graph.IsSealed);
        AssertSealed(() => graph.GetOrAddNamespace(string.Empty));
        AssertSealed(() => graph.RegisterDeclaredMember(lateRootSymbol));
        AssertSealed(() => graph.CreateScope(
            HirScopeKind.Block,
            callableScope.Id));
        AssertSealed(() => graph.BindScopeSite(
            new HirScopeSite(-1, HirScopeSiteRole.IfThen),
            callableScope));
        AssertSealed(() => graph.RegisterCallableScope(main.Id, callableScope));
        AssertSealed(() => graph.AddLookupEdge(
            graph.RootScope.Id,
            callableScope.Id,
            HirScopeEdgeKind.Import));
        AssertSealed(() => callableScope.TryAdd(lateLocalSymbol));
        AssertSealed(() => callableScope.Bind(lateLocalSymbol));
        AssertSealed(() => callableSymbol.AddUse(
            new UseSite(-1, "late mutation", -1)));

        Assert.Null(graph.FindDeclaration(-1));
        Assert.Null(graph.LookupMember(callableScope.Id, "late"));
        Assert.Same(usesBefore, callableSymbol.Uses);
    }

    [Fact]
    public void AggregateViewsCannotBeCachedBeforeScopeGraphPublication()
    {
        var graph = new HirScopeGraph();

        Assert.False(graph.IsSealed);
        Assert.Throws<InvalidOperationException>(() => { _ = graph.Symbols; });
        Assert.Throws<InvalidOperationException>(() => { _ = graph.Scopes; });
        Assert.Throws<InvalidOperationException>(() => { _ = graph.CallableScopes; });
        Assert.Throws<InvalidOperationException>(() => { _ = graph.AllSymbols; });

        var symbol = new Symbol(
            graph.RootScope.Id,
            "N",
            SymbolKind.Namespace);
        graph.RegisterDeclaredMember(symbol);
        var namespaceScope = graph.CreateScope(
            HirScopeKind.Namespace,
            graph.RootScope.Id,
            symbol.Id);
        graph.Seal();

        Assert.True(graph.IsSealed);
        var symbols = graph.Symbols;
        var scopes = graph.Scopes;
        var callableScopes = graph.CallableScopes;

        Assert.Same(symbols, graph.Symbols);
        Assert.Same(scopes, graph.Scopes);
        Assert.Same(callableScopes, graph.CallableScopes);
        Assert.Same(symbol, symbols[symbol.Id]);
        Assert.Same(namespaceScope, scopes[namespaceScope.Id]);
        Assert.Empty(callableScopes);
        Assert.Single(graph.AllSymbols);
    }

    [Fact]
    public void SemanticFactInputsAndEffectRecordsAreDefensivelyFrozen()
    {
        var validation = new HirSemanticModel();
        var required = new Dictionary<string, long> { ["xs"] = 2 };
        validation.SetRequiredArgLengths(7, required);
        validation.AddUnprovenIndex(
            new UnprovenIndex(
                new HirIndexAccessId(0),
                "F",
                "xs",
                "i",
                null,
                null),
            owningStatementId: 1,
            new object());
        validation.AddDeferredSizeCheck(new DeferredSizeCheck("F", "xs", "xs[i]", "size", null));
        required["xs"] = 99;
        required["ys"] = 1;

        var needs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, long>>(
            validation.RequiredArgLengths(7));
        Assert.Equal(2, needs["xs"]);
        Assert.False(needs.ContainsKey("ys"));
        AssertDictionaryCannotRemove(needs, "xs");
        AssertCannotAdd(
            validation.UnprovenIndexes,
            new UnprovenIndex(
                new HirIndexAccessId(1),
                "Injected",
                "ys",
                "0",
                null,
                null));
        AssertCannotAdd(
            validation.DeferredSizeChecks,
            new DeferredSizeCheck("Injected", "ys", "ys[0]", "mutation", null));

        var parents = new List<QubitEdge>
        {
            new(0, new QubitRef("q", 0)),
        };
        var node = new QubitNode(1, new QubitRef("q", 0), 1, parents, false);
        parents.Clear();
        Assert.Single(node.Parents);
        AssertCannotAdd(node.Parents, new QubitEdge(2, new QubitRef("q", 0)));

        var touched = new HashSet<QubitRef> { new("q", null) };
        var summary = new OpEffectSummary(
            touched,
            touched,
            touched,
            touched,
            Irreversible: false);
        touched.Clear();
        Assert.Single(summary.ParamTouched);
        AssertSetCannotRemove(summary.ParamTouched, new QubitRef("q", null));
    }

    [Fact]
    public void EffectSnapshotForkSharesValidationAuthorityWithoutMutatingIt()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main() { use q = Qubit[1]; X(q[0]); }");
        Assert.True(compilation.Succeeded);

        var specialized = Assert.IsType<HirSemanticArtifact>(
            compilation.Hir.SpecializedValidation);
        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var main = analyzed.Program.Operations.Single();

        Assert.NotSame(specialized.Model, analyzed.Model);
        Assert.Same(specialized.Model.ScopeGraph, analyzed.Model.ScopeGraph);
        Assert.False(specialized.Model.WasEffectAnalyzed(main.Id));
        Assert.True(analyzed.Model.WasEffectAnalyzed(main.Id));
        Assert.Null(specialized.Model.Graph(main.Id));
        Assert.NotNull(analyzed.Model.Graph(main.Id));
    }

    private static void AssertCannotAdd<T>(IReadOnlyList<T> values, T value)
    {
        Assert.IsNotType<List<T>>(values);
        if (values is ICollection<T> collection)
            Assert.ThrowsAny<NotSupportedException>(() => collection.Add(value));
    }

    private static void AssertDictionaryCannotRemove<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> values,
        TKey key)
        where TKey : notnull
    {
        Assert.IsNotType<Dictionary<TKey, TValue>>(values);
        if (values is IDictionary<TKey, TValue> dictionary)
            Assert.ThrowsAny<NotSupportedException>(() => dictionary.Remove(key));
    }

    private static void AssertSetCannotRemove<T>(IReadOnlySet<T> values, T value)
    {
        Assert.IsNotType<HashSet<T>>(values);
        if (values is ISet<T> set)
            Assert.ThrowsAny<NotSupportedException>(() => set.Remove(value));
    }

    private static void AssertSealed(Action mutation)
    {
        var error = Assert.Throws<InvalidOperationException>(mutation);
        Assert.True(
            error.Message.Contains("sealed", StringComparison.OrdinalIgnoreCase),
            error.Message);
    }
}
