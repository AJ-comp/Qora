using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.LanguageServices.Tests;

public sealed class MirSemanticIndexTests
{
    [Fact]
    public void AliasedDeclarationsProduceAManyToManyValueIndex()
    {
        var result = Compile(
            """
            operation Main() {
                var x: int = 1;
                var y: int = x;
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var callable = Assert.Single(index.Mir.Program.Callables);
        var callableIndex = index.Callable(callable);
        var x = Symbol(index, "x");
        var y = Symbol(index, "y");

        var xValue = Assert.Single(callableIndex.ValuesFor(x));
        var yValue = Assert.Single(callableIndex.ValuesFor(y));

        Assert.Same(xValue, yValue);
        Assert.Equal(
            new[] { x, y }.OrderBy(symbol => symbol.Id.Value),
            callableIndex.SymbolsFor(xValue).OrderBy(symbol => symbol.Id.Value));
        Assert.False(callableIndex.IsCompilerGenerated(xValue));
    }

    [Fact]
    public void ShadowedArraysKeepDistinctSymbolAndStorageIdentity()
    {
        var result = Compile(
            """
            operation Main() {
                var values: int[] = [1];
                if (1 == 1) {
                    var values: int[] = [2];
                    values[0] = 3;
                }
                values[0] = 4;
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var callable = Assert.Single(index.Mir.Program.Callables);
        var callableIndex = index.Callable(callable);
        var values = Symbols(index, "values");

        Assert.Equal(2, values.Length);
        var outerStorage = Assert.Single(callableIndex.StoragesFor(values[0]));
        var innerStorage = Assert.Single(callableIndex.StoragesFor(values[1]));

        Assert.NotEqual(outerStorage.Id, innerStorage.Id);
        Assert.Same(values[0], Assert.Single(callableIndex.SymbolsFor(outerStorage)));
        Assert.Same(values[1], Assert.Single(callableIndex.SymbolsFor(innerStorage)));
    }

    [Fact]
    public void QubitPhiAndEveryVersionRetainTheirSourceSymbol()
    {
        var result = Compile(
            """
            operation Main() {
                use control = Qubit[1];
                use target = Qubit[1];
                var measured: bit = M(control[0]);
                if (measured == 1) {
                    X(target[0]);
                } else {
                    H(target[0]);
                }
                Z(target[0]);
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var callable = Assert.Single(index.Mir.Program.Callables);
        var callableIndex = index.Callable(callable);
        var target = Symbol(index, "target");
        var phi = Assert.Single(callable.Qubits.OfType<MirQubitPhi>());

        Assert.Contains(phi, callableIndex.QubitsFor(target));
        Assert.Contains(target, callableIndex.SymbolsFor(phi));
        Assert.False(callableIndex.IsCompilerGenerated(phi));
        Assert.All(
            callableIndex.QubitsFor(target),
            qubit => Assert.Contains(target, callableIndex.SymbolsFor(qubit)));
    }

    [Fact]
    public void UnreachableDeclarationsBelongToTheirCallableIndex()
    {
        var result = Compile(
            """
            function f(): int {
                return 1;
                var dead: int = 2;
            }

            operation Main() {
                var result: int = f();
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var dead = Symbol(index, "dead");
        var f = index.Mir.Program.Callables.Single(callable => callable.Name == "f");
        var callableIndex = index.Callable(f);

        Assert.True(callableIndex.IsUnreachable(dead));
        Assert.Empty(callableIndex.ValuesFor(dead));
    }

    [Fact]
    public void UnlinkedEntitiesAreDerivedAsCompilerGeneratedAndEqualCopiesAreRejected()
    {
        var result = Compile(
            """
            operation Main() {
                var x: int = 1 + 2;
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var callable = Assert.Single(index.Mir.Program.Callables);
        var callableIndex = index.Callable(callable);
        var temporaries = callable.Values
            .Where(value => callableIndex.SymbolsFor(value).Count == 0)
            .ToArray();
        Assert.NotEmpty(temporaries);
        var temporary = temporaries[0];
        var equalCopy = temporary with { };

        Assert.True(callableIndex.IsCompilerGenerated(temporary));
        Assert.Equal(temporary, equalCopy);
        Assert.Throws<ArgumentException>(
            () => callableIndex.SymbolsFor(equalCopy));
    }

    [Fact]
    public void EqualDenseLocalIdsStayInsideTheirExactCallable()
    {
        var result = Compile(
            """
            operation First(q: Qubit) {
                var values: int[] = [1];
                var scalar: int = values[0];
                X(q);
            }

            operation Second(q: Qubit) {
                var values: int[] = [2];
                var scalar: int = values[0];
                H(q);
            }

            operation Main() {
                use q = Qubit[2];
                First(q[0]);
                Second(q[1]);
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var first = index.Mir.Program.Callables.Single(callable => callable.Name == "First");
        var second = index.Mir.Program.Callables.Single(callable => callable.Name == "Second");
        var firstIndex = index.Callable(first);
        var secondIndex = index.Callable(second);
        var firstScalar = firstIndex.Symbols.Single(symbol => symbol.SourceName == "scalar");
        var secondScalar = secondIndex.Symbols.Single(symbol => symbol.SourceName == "scalar");
        var firstValues = firstIndex.Symbols.Single(symbol => symbol.SourceName == "values");
        var secondValues = secondIndex.Symbols.Single(symbol => symbol.SourceName == "values");
        var firstQubitSymbol = firstIndex.Symbols.Single(symbol => symbol.SourceName == "q");
        var secondQubitSymbol = secondIndex.Symbols.Single(symbol => symbol.SourceName == "q");
        var firstValue = Assert.Single(firstIndex.ValuesFor(firstScalar));
        var secondValue = Assert.Single(secondIndex.ValuesFor(secondScalar));
        var firstStorage = Assert.Single(firstIndex.StoragesFor(firstValues));
        var secondStorage = Assert.Single(secondIndex.StoragesFor(secondValues));
        var firstQubit = firstIndex.QubitsFor(firstQubitSymbol)
            .Single(qubit => qubit.Version.Value == 0);
        var secondQubit = secondIndex.QubitsFor(secondQubitSymbol)
            .Single(qubit => qubit.Version.Value == 0);

        Assert.Equal(firstValue.Id, secondValue.Id);
        Assert.Equal(firstStorage.Id, secondStorage.Id);
        Assert.Equal(firstQubit.Key, secondQubit.Key);
        Assert.Throws<ArgumentException>(() => secondIndex.ValuesFor(firstScalar));
        Assert.Throws<ArgumentException>(() => secondIndex.SymbolsFor(firstValue));
        Assert.Throws<ArgumentException>(() => secondIndex.SymbolsFor(firstStorage));
        Assert.Throws<ArgumentException>(() => secondIndex.SymbolsFor(firstQubit));
    }

    [Fact]
    public void CollectorRejectsAnEntityLinkFromAnotherCallableOwner()
    {
        var collector = new MirSemanticIndexCollector();
        var compilation = QoraCompiler.Compile(
            """
            operation First() {
                var firstValue: int = 1;
            }

            operation Second() {
                var secondValue: int = 2;
            }
            """,
            instrumentation: new CompilationInstrumentation(collector));
        Assert.True(compilation.Succeeded);
        var graph = compilation.Hir.EffectAnalysis!.Model.ScopeGraph!;
        var firstSymbol = graph.AllSymbols.Single(
            symbol => symbol.SourceName == "firstValue");
        var second = compilation.Mir!.Program.Callables.Single(
            callable => callable.Name == "Second");
        var secondValue = Assert.Single(second.Values);

        collector.LinkValue(firstSymbol.Id, second.Id, secondValue.Id);

        var error = Assert.Throws<InvalidOperationException>(
            () => collector.Build(compilation));
        Assert.Contains("belongs to callable symbol", error.Message);
    }

    [Fact]
    public void CollectorRejectsAMissingPrimaryEntityLink()
    {
        var collector = new MirSemanticIndexCollector();
        var instrumentation = new CompilationInstrumentation(
            new DroppingValueTrace(collector));
        var compilation = QoraCompiler.Compile(
            """
            operation Main() {
                var value: int = 1;
            }
            """,
            instrumentation: instrumentation);
        Assert.True(compilation.Succeeded);

        var error = Assert.Throws<InvalidOperationException>(
            () => collector.Build(compilation));
        Assert.Contains("has no primary MIR value", error.Message);
    }

    [Fact]
    public void FinalHirSpecializationsEachOwnOneMirCallable()
    {
        var result = Compile(
            """
            operation Apply(q: Qubit[]) {
                X(q[0]);
            }

            operation Main() {
                use one = Qubit[1];
                use two = Qubit[2];
                Apply(one);
                Apply(two);
            }
            """);
        var index = Assert.IsType<MirSemanticIndex>(result.MirSemanticIndex);
        var specializations = index.HirArtifact.Program.Callables
            .Where(callable => callable.Name.StartsWith("Apply__sz", StringComparison.Ordinal))
            .OrderBy(callable => callable.Name)
            .ToArray();

        Assert.Equal(2, specializations.Length);
        var lowered = specializations.Select(index.CallableFor).ToArray();
        Assert.Equal(2, lowered.Select(callable => callable.Id).Distinct().Count());
    }

    [Fact]
    public void RecompileBuildsANewIndexAndRejectsObjectsFromThePreviousRevision()
    {
        var session = new LanguageServiceSession();
        var first = session.Compile(
            """
            operation Main() {
                var value: int = 1;
            }
            """);
        var second = session.Recompile(
            first,
            """
            operation Main() {
                var value: int = 2;
            }
            """);
        var firstIndex = Assert.IsType<MirSemanticIndex>(first.MirSemanticIndex);
        var secondIndex = Assert.IsType<MirSemanticIndex>(second.MirSemanticIndex);
        var firstCallable = Assert.Single(firstIndex.Mir.Program.Callables);
        var secondCallable = Assert.Single(secondIndex.Mir.Program.Callables);
        var firstSymbol = Symbol(firstIndex, "value");

        Assert.Equal(first.Compilation.Id, second.Compilation.Id);
        Assert.NotEqual(first.Compilation.Revision, second.Compilation.Revision);
        Assert.Equal(firstCallable.Id, secondCallable.Id);
        Assert.Same(first.Compilation.Mir, firstIndex.Mir);
        Assert.Same(second.Compilation.Mir, secondIndex.Mir);
        Assert.Throws<ArgumentException>(
            () => new LanguageServiceCompilation(
                second.Compilation,
                firstIndex));
        Assert.Throws<ArgumentException>(() => secondIndex.Callable(firstCallable));
        Assert.Throws<ArgumentException>(
            () => secondIndex.Callable(secondCallable).ValuesFor(firstSymbol));
    }

    [Fact]
    public void RootIndexRejectsACallableIndexFromAnotherMirProgram()
    {
        var first = Compile("operation Main() { }");
        var second = Compile("operation Main() { }");
        var firstIndex = Assert.IsType<MirSemanticIndex>(first.MirSemanticIndex);
        var secondIndex = Assert.IsType<MirSemanticIndex>(second.MirSemanticIndex);
        var firstCallable = Assert.Single(firstIndex.Mir.Program.Callables);
        var secondCallable = Assert.Single(secondIndex.Mir.Program.Callables);
        Assert.Equal(firstCallable.Id, secondCallable.Id);
        Assert.NotSame(firstCallable, secondCallable);

        var detachedCallableIndex = new MirCallableSemanticIndex(
            secondIndex.HirArtifact.Model.ScopeGraph!,
            firstCallable,
            Array.Empty<SymbolId>(),
            EmptyLinks<SymbolId, MirValueId>(),
            EmptyLinks<MirValueId, SymbolId>(),
            EmptyLinks<SymbolId, MirStorageId>(),
            EmptyLinks<MirStorageId, SymbolId>(),
            EmptyLinks<SymbolId, MirQubitKey>(),
            EmptyLinks<MirQubitKey, SymbolId>(),
            Array.Empty<SymbolId>());

        Assert.Throws<ArgumentException>(
            () => new MirSemanticIndex(
                second.Compilation,
                secondIndex.HirArtifact,
                secondIndex.Mir,
                new Dictionary<HirNodeId, MirCallableId>(),
                EmptyLinks<SymbolId, MirCallableId>(),
                new Dictionary<MirCallableId, MirCallableSemanticIndex>
                {
                    [secondCallable.Id] = detachedCallableIndex,
                }));
    }

    [Fact]
    public void HirOnlyLanguageCompilationHasNoMirIndex()
    {
        var result = new LanguageServiceSession().Compile(
            "operation Main() {}",
            new CompilationOptions(
                outputPlan: CompilationOutputPlan.HirOnly));

        Assert.Null(result.Compilation.Mir);
        Assert.Null(result.MirSemanticIndex);
    }

    [Fact]
    public void OrdinaryCompilerCompilationDoesNotOwnALanguageServiceIndex()
    {
        var compilation = QoraCompiler.Compile("operation Main() {}");

        Assert.True(compilation.Succeeded);
        Assert.DoesNotContain(
            typeof(Compilation).GetProperties(),
            property => property.PropertyType == typeof(MirSemanticIndex)
                || property.Name.Contains(
                    nameof(MirSemanticIndex),
                    StringComparison.Ordinal));
    }

    private static LanguageServiceCompilation Compile(string source)
    {
        var result = new LanguageServiceSession().Compile(source);
        Assert.True(
            result.Compilation.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return result;
    }

    private static Symbol Symbol(MirSemanticIndex index, string name) =>
        Assert.Single(Symbols(index, name));

    private static Symbol[] Symbols(MirSemanticIndex index, string name) =>
        index.HirArtifact.Model.ScopeGraph!.AllSymbols
            .Where(symbol => symbol.SourceName == name)
            .OrderBy(symbol => symbol.Id.Value)
            .ToArray();

    private static Dictionary<TKey, IReadOnlyList<TValue>> EmptyLinks<TKey, TValue>()
        where TKey : notnull =>
        new();

    private sealed class DroppingValueTrace(IMirLoweringTraceSink inner)
        : IMirLoweringTraceSink
    {
        public void LinkValue(
            SymbolId symbol,
            MirCallableId callable,
            MirValueId value)
        {
        }

        public void LinkStorage(
            SymbolId symbol,
            MirCallableId callable,
            MirStorageId storage) =>
            inner.LinkStorage(symbol, callable, storage);

        public void LinkQubit(
            SymbolId symbol,
            MirCallableId callable,
            MirQubitKey qubit) =>
            inner.LinkQubit(symbol, callable, qubit);

        public void MarkUnreachable(SymbolId symbol) =>
            inner.MarkUnreachable(symbol);
    }
}
