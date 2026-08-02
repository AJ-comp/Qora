using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public sealed class HirImmutabilityTests
{
    [Fact]
    public void ConcreteHirNodesExposeNoPublicInstanceConstructors()
    {
        var concreteNodeTypes = typeof(HirNode)
            .Assembly
            .GetTypes()
            .Where(type =>
                typeof(HirNode).IsAssignableFrom(type)
                && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(typeof(HirProgram), concreteNodeTypes);
        Assert.Contains(typeof(HirNamespaceDeclaration), concreteNodeTypes);
        Assert.Contains(typeof(HirCallable), concreteNodeTypes);
        Assert.Contains(typeof(HirBlock), concreteNodeTypes);
        Assert.Contains(
            concreteNodeTypes,
            type => typeof(HirStatement).IsAssignableFrom(type));
        Assert.Contains(
            concreteNodeTypes,
            type => typeof(HirExpression).IsAssignableFrom(type));
        Assert.Contains(typeof(HirArgument), concreteNodeTypes);
        Assert.Contains(typeof(HirParameter), concreteNodeTypes);

        var exposedConstructors = concreteNodeTypes
            .SelectMany(type =>
                type.GetConstructors()
                    .Select(constructor =>
                        $"{type.FullName}: {constructor}"))
            .ToArray();

        Assert.True(
            exposedConstructors.Length == 0,
            "HIR nodes must be constructed only by lowering/rewrite authorities: "
            + string.Join(" | ", exposedConstructors));
    }

    [Fact]
    public void ConstructionAuthorityRejectsRegisteringOneDocumentTwice()
    {
        var hir = new HirTestFactory();

        var error = Assert.Throws<InvalidOperationException>(
            hir.RegisterEntryDocumentAgain);

        Assert.Contains("already registered", error.Message);
    }

    [Fact]
    public void ConstructionAuthorityRejectsTwoLoweringSessionsForOneDocument()
    {
        var hir = new HirTestFactory();

        var error = Assert.Throws<InvalidOperationException>(
            hir.BeginEntryDocumentLoweringAgain);

        Assert.Contains("already began", error.Message);
    }

    [Fact]
    public void ConstructionAuthorityRejectsLoweringAfterSourceSetBinding()
    {
        var hir = new HirTestFactory();

        var error = Assert.Throws<InvalidOperationException>(
            hir.BeginEntryDocumentLoweringAfterBindingSourceSet);

        Assert.Contains("source set is sealed", error.Message);
    }

    [Fact]
    public void CollectionBearingHirNodesDefensivelyFreezeConstructionInputs()
    {
        var hir = new HirTestFactory();
        var parameter = hir.Parameter("p", QType.Int);
        var statement = hir.Variable(
            "x",
            hir.Integer(1),
            QType.Int);
        var argument = hir.Argument(hir.Integer(1));
        var expression = hir.Integer(1);

        var parameters = new List<HirParameter> { parameter };
        var body = new List<HirStatement> { statement };
        var operation = hir.Callable("F", parameters, body);
        var imports = new List<HirImportDirective>
        {
            hir.Import("lib.qor"),
        };
        var openDirectives = new List<HirOpenDirective>
        {
            hir.Open("Lib"),
        };
        var namespaceDeclarations = new List<HirDeclaration>
        {
            operation,
        };
        var app = hir.Namespace(
            "App",
            namespaceDeclarations,
            openDirectives);
        var declarations = new List<HirDeclaration> { app };
        var program = hir.Program(declarations, imports);

        var modifiers = new List<QGateModifier> { QGateModifier.Controlled };
        var arguments = new List<HirArgument> { argument };
        var gate = hir.Apply(
            modifiers,
            hir.Call("F", arguments));
        var thenBody = new List<HirStatement> { statement };
        var elseBody = new List<HirStatement> { statement };
        var conditional = hir.If(
            hir.Integer(1),
            thenBody,
            elseBody);
        var forBody = new List<HirStatement> { statement };
        var loop = hir.For(
            "i",
            hir.Integer(0),
            hir.Integer(1),
            forBody);
        var whileBody = new List<HirStatement> { statement };
        var whileLoop = hir.While(
            hir.Integer(1),
            whileBody);
        var repeatBody = new List<HirStatement> { statement };
        var repeat = hir.Repeat(
            repeatBody,
            hir.Integer(1));
        var elements = new List<HirExpression> { expression };
        var literal = hir.ArrayLiteral(elements);
        var callArguments = new List<HirArgument>
        {
            hir.Argument(hir.Integer(1)),
        };
        var call = hir.Call("F", callArguments);
        var slots = new List<IParamSpec> { parameter };
        var signature = new GateSig("F", slots);
        _ = hir.Publish(program);

        parameters.Clear();
        body.Clear();
        declarations.Clear();
        namespaceDeclarations.Clear();
        imports.Clear();
        openDirectives.Clear();
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

        Assert.Single(operation.Parameters);
        Assert.Single(operation.Body);
        Assert.Single(program.Declarations);
        Assert.Single(program.Callables);
        Assert.Single(program.Imports);
        Assert.Single(app.OpenDirectives);
        Assert.Single(app.Declarations);
        Assert.Same(operation, program.Callables.Single());
        Assert.Equal("App", program.NamespaceOf(operation));
        Assert.Single(gate.Modifiers);
        Assert.Single(gate.Call.Arguments);
        Assert.Single(conditional.Then);
        Assert.Single(conditional.Else);
        Assert.Single(loop.Body);
        Assert.Single(whileLoop.Body);
        Assert.Single(repeat.Body);
        Assert.Single(literal.Elements);
        Assert.Single(call.Arguments);
        Assert.Single(signature.Parameters);
    }

    [Fact]
    public void PublishedHirCannotBeExtendedThroughItsConstructionAuthority()
    {
        var hir = new HirTestFactory();
        var declaration = hir.Variable(
            "value",
            hir.Integer(1),
            QType.Int);
        var main = hir.Callable(
            "Main",
            body: new HirStatement[] { declaration });
        var program = hir.PublishProgram(new[] { main });
        var pipeline = hir.CreatePipelineBuilder();
        var snapshot = pipeline.PublishLowered(program);
        var publishedIds = snapshot.Structure.NodeIds.ToArray();

        Assert.Throws<InvalidOperationException>(
            () => hir.Integer(2));
        Assert.Throws<InvalidOperationException>(
            () => hir.Publish(program));
        Assert.Equal(
            publishedIds,
            snapshot.Structure.NodeIds);
        Assert.Same(
            declaration,
            snapshot.Structure.RequireNode(declaration.Id));
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
        var main = analyzed.Program.Callables.Single(
            callable => callable.Name == "Main");
        var declaration = main.Body
            .OfType<HirVariableDeclarationStatement>()
            .Single(item => item.Name == "x");
        var symbol = Assert.IsType<Symbol>(model.FindSymbol(declaration.Id));
        var touch = analyzed.Program.Callables.Single(
            callable => callable.Name == "Touch");
        var effects = Assert.IsType<OpEffectSummary>(
            model.FindOpEffects(touch.Id));
        var scopeGraph = Assert.IsType<HirScopeGraph>(model.ScopeGraph);

        AssertCannotAdd(
            symbol.Uses,
            new UseSite(
                -1,
                "mutation",
                new HirNodeId(int.MaxValue)));
        AssertSetCannotRemove(
            effects.ParamModified,
            new QubitRef("p", null));
        AssertDictionaryCannotRemove(scopeGraph.Symbols, symbol.Id);
        AssertDictionaryCannotRemove(scopeGraph.Scopes, scopeGraph.RootScope.Id);
        AssertDictionaryCannotRemove(scopeGraph.CallableScopes, main.Id);

        Assert.Single(symbol.Uses);
        Assert.Single(effects.ParamModified);
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
        var main = validation.Program.Callables.Single();
        var callableScope = Assert.IsType<Scope>(
            graph.FindCallableScope(main.Id));
        var callableSymbol = Assert.IsType<Symbol>(
            graph.FindDeclaration(main.Id));
        var usesBefore = callableSymbol.Uses;
        var lateRootSymbol = graph.CreateSymbol(
            graph.RootScope.Id,
            "Late",
            SymbolKind.Callable,
            declarationNodeId: main.Id);
        var lateLocalSymbol = graph.CreateSymbol(
            callableScope.Id,
            "late",
            SymbolKind.Var,
            QType.Int);
        var nonDeclarationId = Assert
            .IsType<HirVariableDeclarationStatement>(
                Assert.Single(main.Body))
            .Value
            .Id;

        Assert.True(graph.IsSealed);
        AssertSealed(() => graph.GetOrAddNamespace(string.Empty));
        AssertSealed(() => graph.RegisterDeclaredMember(lateRootSymbol));
        AssertSealed(() => graph.CreateScope(
            HirScopeKind.Block,
            callableScope.Id));
        AssertSealed(() => graph.BindScopeSite(
            new HirScopeSite(main.Id, HirScopeSiteRole.IfThen),
            callableScope));
        AssertSealed(() => graph.RegisterCallableScope(main.Id, callableScope));
        AssertSealed(() => graph.AddLookupEdge(
            graph.RootScope.Id,
            callableScope.Id,
            HirScopeEdgeKind.Import));
        AssertSealed(() => callableScope.TryAdd(lateLocalSymbol));
        AssertSealed(() => callableScope.Bind(lateLocalSymbol));
        AssertSealed(() => callableSymbol.AddUse(
            new UseSite(-1, "late mutation", main.Id)));
        AssertSealed(() => graph.RecordUse(
            callableSymbol,
            new UseSite(-1, "late mutation", main.Id),
            nonDeclarationId));

        Assert.Null(graph.FindDeclaration(nonDeclarationId));
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

        var symbol = graph.CreateSymbol(
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
        var hir = new HirTestFactory();
        var callable = hir.Callable("F");
        var validation = new HirSemanticModel();
        var required = new Dictionary<string, long> { ["xs"] = 2 };
        validation.SetRequiredArgLengths(callable.Id, required);
        validation.AddDeferredSizeCheck(new DeferredSizeCheck("F", "xs", "xs[i]", "size", null));
        required["xs"] = 99;
        required["ys"] = 1;

        var needs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, long>>(
            validation.RequiredArgLengths(callable.Id));
        Assert.Equal(2, needs["xs"]);
        Assert.False(needs.ContainsKey("ys"));
        AssertDictionaryCannotRemove(needs, "xs");
        AssertCannotAdd(
            validation.DeferredSizeChecks,
            new DeferredSizeCheck("Injected", "ys", "ys[0]", "mutation", null));

        var modified = new HashSet<QubitRef> { new("q", null) };
        var summary = new OpEffectSummary(modified);
        modified.Clear();
        Assert.Single(summary.ParamModified);
        AssertSetCannotRemove(
            summary.ParamModified,
            new QubitRef("q", null));
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
        var main = analyzed.Program.Callables.Single();

        Assert.NotSame(specialized.Model, analyzed.Model);
        Assert.Same(specialized.Model.ScopeGraph, analyzed.Model.ScopeGraph);
        Assert.Null(specialized.Model.FindOpEffects(main.Id));
        Assert.NotNull(analyzed.Model.FindOpEffects(main.Id));
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
