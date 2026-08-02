using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Mir.Analysis;

namespace Qora.Tests;

public sealed class MirAdjointMaterializerTests
{
    [Fact]
    public void InjectedCallableInverseRequestBecomesAnExactInverseCallableInFreshSnapshots()
    {
        var compilation = CompileMir(
            """
            operation Worker(q: Qubit) {
                H(q);
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                Worker(q[0]);
            }
            """);
        var source = RequireMir(compilation);
        var worker = RequireCallable(source, "Worker");
        var sourceCall = Assert.Single(UserCalls(source, "Main", worker.Id));

        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", sourceCall) });

        Assert.NotSame(source, injected);
        Assert.Equal(MirStage.Lowered, source.Stage);
        Assert.Equal(MirStage.InverseRequestsInjected, injected.Stage);
        Assert.Same(source, injected.TransformationSource);
        Assert.Empty(sourceCall.Functors);
        Assert.Equal(
            new[] { MirFunctor.Adjoint },
            Assert.Single(UserCalls(injected, "Main", worker.Id)).Functors);
        var unmaterialized = Assert.Throws<InvalidOperationException>(
            () => QasmBackend.Run(injected));
        Assert.Contains(
            "callable inverse request",
            unmaterialized.Message,
            StringComparison.Ordinal);

        var result = MirAdjointMaterializer.Run(injected);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        var output = Assert.IsType<MirSnapshot>(result.Output);
        Assert.Equal(MirStage.AdjointsMaterialized, output.Stage);
        Assert.Same(injected, output.TransformationSource);
        Assert.Same(injected, result.Source);
        Assert.Same(output, result.Snapshot);

        var inverseId = result.Inverses[worker.Id];
        Assert.Equal(
            source.Program.Callables.Max(callable => callable.Id.Value) + 1,
            inverseId.Value);
        var inverse = output.Program.RequireCallable(inverseId);
        Assert.StartsWith("__qora_inverse_", inverse.Name, StringComparison.Ordinal);

        var rewrittenCall = Assert.Single(UserCalls(output, "Main", inverse.Id));
        Assert.Empty(rewrittenCall.Functors);
        var inverseGates = inverse.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .ToArray();
        Assert.Equal(
            new[] { "X", "H" },
            inverseGates.Select(GateName));
        Assert.All(
            inverseGates,
            gate => Assert.Equal(new[] { MirFunctor.Adjoint }, gate.Functors));
        var inverseParameter = Assert.Single(
            inverse.Parameters.OfType<MirQubitParameter>());
        var firstInverseInput = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(inverseGates[0].Operands));
        Assert.Equal(inverseParameter.Key, firstInverseInput.Qubit.Qubit);
        var firstInverseResult = Assert.Single(inverseGates[0].QubitResults);
        Assert.Equal(1, firstInverseResult.Version.Value);
        var secondInverseInput = Assert.IsType<MirQubitCallOperand>(
            Assert.Single(inverseGates[1].Operands));
        Assert.Equal(firstInverseResult.Key, secondInverseInput.Qubit.Qubit);
        Assert.Equal(
            2,
            Assert.Single(inverseGates[1].QubitResults).Version.Value);
        MirAdjointMaterializer.VerifyMaterialized(output);

        var originalOrigin = worker.Origin.SourceHirOrigin;
        var inverseOrigin = inverse.Origin.SourceHirOrigin;
        Assert.Equal(originalOrigin.HirNodeId, inverseOrigin.HirNodeId);
        Assert.Equal(originalOrigin.Span, inverseOrigin.Span);
        Assert.NotNull(inverseOrigin.Span);

        var backend = QasmBackend.Run(output);
        Assert.True(backend.Success);
        var target = Assert.IsType<MirOpenQasmTargetProgram>(backend.Target);
        var targetCall = Assert.Single(
            target.EntryBody.OfType<MirQasmQuantumApplyStatement>());
        var targetInverse = Assert.IsType<MirQasmUserQuantumTarget>(
            targetCall.Target);
        var targetInverseBody = Assert.Single(
            target.Definitions,
            definition => definition.Id == targetInverse.Callable);
        Assert.StartsWith(
            "__qora_inverse_",
            targetInverseBody.EmittedName,
            StringComparison.Ordinal);
        var targetGates = targetInverseBody.Body
            .OfType<MirQasmQuantumApplyStatement>()
            .ToArray();
        Assert.Equal(
            new[] { "x", "h" },
            targetGates.Select(
                gate => Assert.IsType<MirQasmBuiltinGateTarget>(
                    gate.Target).EmittedName));
        Assert.All(
            targetGates,
            gate => Assert.Equal(
                new[] { MirQasmQuantumModifier.Inverse },
                gate.Modifiers));
    }

    [Fact]
    public void MaterializationResultRejectsForeignSourceAndCallableIds()
    {
        const string sourceText =
            """
            operation Worker(q: Qubit) {
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                Worker(q[0]);
            }
            """;
        var unrelatedSource = RequireMir(CompileMir(sourceText));
        var source = RequireMir(CompileMir(sourceText));
        var worker = RequireCallable(source, "Worker");
        var sourceCall = Assert.Single(UserCalls(source, "Main", worker.Id));
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", sourceCall) });
        var valid = MirAdjointMaterializer.Run(injected);
        var output = Assert.IsType<MirSnapshot>(valid.Output);

        Assert.Throws<ArgumentException>(
            () => new MirAdjointMaterializationResult(
                unrelatedSource,
                output,
                valid.Inverses,
                Array.Empty<MirAdjointMaterializationError>()));

        var inverse = Assert.Single(valid.Inverses).Value;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MirAdjointMaterializationResult(
                injected,
                output,
                new Dictionary<MirCallableId, MirCallableId>
                {
                    [new MirCallableId(int.MaxValue)] = inverse,
                },
                Array.Empty<MirAdjointMaterializationError>()));
    }

    [Fact]
    public void DuplicateRequestsAreSetLikeAndShareOneSynthesizedInverse()
    {
        var source = RequireMir(
            CompileMir(
                """
                operation Worker(q: Qubit) {
                    X(q);
                }

                operation Main() {
                    use q = Qubit[2];
                    Worker(q[0]);
                    Worker(q[1]);
                }
                """));
        var worker = RequireCallable(source, "Worker");
        var calls = UserCalls(source, "Main", worker.Id);
        Assert.Equal(2, calls.Count);
        var firstSite = Instruction(source, "Main", calls[0]);
        var secondSite = Instruction(source, "Main", calls[1]);

        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { firstSite, firstSite, secondSite });

        var injectedCalls = UserCalls(injected, "Main", worker.Id);
        Assert.Equal(2, injectedCalls.Count);
        Assert.All(
            injectedCalls,
            call => Assert.Equal(
                1,
                call.Functors.Count(functor => functor == MirFunctor.Adjoint)));

        var currentSites = injectedCalls
            .Select(call => Instruction(injected, "Main", call))
            .ToArray();
        Assert.Same(
            injected,
            MirAdjointMaterializer.InjectRequests(injected, currentSites));

        var result = MirAdjointMaterializer.Run(injected);

        Assert.True(result.Succeeded);
        var inverse = Assert.Single(result.Inverses);
        Assert.Equal(worker.Id, inverse.Key);
        var output = Assert.IsType<MirSnapshot>(result.Output);
        var rewritten = UserCalls(output, "Main", inverse.Value);
        Assert.Equal(2, rewritten.Count);
        Assert.All(rewritten, call => Assert.Empty(call.Functors));
    }

    [Fact]
    public void NewRequestsCannotBeAppendedAfterTheInjectionStageHasClosed()
    {
        var source = RequireMir(
            CompileMir(
                """
                operation Worker(q: Qubit) {
                    X(q);
                }

                operation Main() {
                    use q = Qubit[2];
                    Worker(q[0]);
                    Worker(q[1]);
                }
                """));
        var worker = RequireCallable(source, "Worker");
        var calls = UserCalls(source, "Main", worker.Id);
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", calls[0]) });
        var remaining = Assert.Single(
            UserCalls(injected, "Main", worker.Id),
            call => !call.Functors.Contains(MirFunctor.Adjoint));

        var error = Assert.Throws<InvalidOperationException>(
            () => MirAdjointMaterializer.InjectRequests(
                injected,
                new[] { Instruction(injected, "Main", remaining) }));

        Assert.Contains(
            MirStage.InverseRequestsInjected.ToString(),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyClosureSynthesizesCalleesBeforeRelinkingTheCallerInverse()
    {
        var source = RequireMir(
            CompileMir(
                """
                operation Leaf(q: Qubit) {
                    X(q);
                }

                operation Middle(q: Qubit) {
                    H(q);
                    Leaf(q);
                }

                operation Main() {
                    use q = Qubit[1];
                    Middle(q[0]);
                }
                """));
        var leaf = RequireCallable(source, "Leaf");
        var middle = RequireCallable(source, "Middle");
        var request = Assert.Single(UserCalls(source, "Main", middle.Id));
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", request) });

        var result = MirAdjointMaterializer.Run(injected);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Inverses.Count);
        var output = Assert.IsType<MirSnapshot>(result.Output);
        var inverseLeafId = result.Inverses[leaf.Id];
        var inverseMiddleId = result.Inverses[middle.Id];
        var inverseMiddle = output.Program.RequireCallable(inverseMiddleId);
        var inverseInstructions = inverseMiddle.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .ToArray();

        var nested = Assert.IsType<MirUserCallableTarget>(
            inverseInstructions[0].Target);
        Assert.Equal(inverseLeafId, nested.Callable);
        Assert.Empty(inverseInstructions[0].Functors);
        Assert.Equal("H", GateName(inverseInstructions[1]));
        Assert.Equal(
            new[] { MirFunctor.Adjoint },
            inverseInstructions[1].Functors);

        Assert.NotEmpty(inverseMiddle.Qubits);
    }

    [Fact]
    public void BranchingCallableIsRejectedWithItsExactMirAndSourceOrigin()
    {
        const string text =
            """
            operation Branchy(flag: int, q: Qubit) {
                if (flag == 1) {
                    X(q);
                }
            }

            operation Main() {
                use q = Qubit[1];
                Branchy(1, q[0]);
            }
            """;
        var compilation = CompileMir(text);
        var source = RequireMir(compilation);
        var branchy = RequireCallable(source, "Branchy");
        var request = Assert.Single(UserCalls(source, "Main", branchy.Id));
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", request) });

        var result = MirAdjointMaterializer.Run(injected);

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Null(result.Output);
        Assert.Same(injected, result.Snapshot);
        Assert.Empty(result.Inverses);
        var error = Assert.Single(
            result.Errors,
            candidate => candidate.Message.Contains(
                "straight-line",
                StringComparison.Ordinal));
        Assert.Equal("MIRADJ001", error.Code);
        Assert.Equal(branchy.Id, error.Callable);
        Assert.Same(
            branchy.Origin.SourceHirOrigin,
            error.Origin.SourceHirOrigin);
        var resolved = error.Origin.SourceHirOrigin;
        var span = Assert.IsType<SourceSpan>(resolved.Span);
        Assert.Contains(
            "Branchy",
            TextAt(compilation, span),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MeasurementIsRejectedAtTheMeasurementSourceSpan()
    {
        const string text =
            """
            operation Read(q: Qubit) {
                var result: bit = M(q);
            }

            operation Main() {
                use q = Qubit[1];
                Read(q[0]);
            }
            """;
        var compilation = CompileMir(text);
        var source = RequireMir(compilation);
        var read = RequireCallable(source, "Read");
        var request = Assert.Single(UserCalls(source, "Main", read.Id));
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", request) });

        var result = MirAdjointMaterializer.Run(injected);

        Assert.False(result.Succeeded);
        var error = Assert.Single(
            result.Errors,
            candidate => candidate.Message.Contains(
                "measurement is not unitary",
                StringComparison.Ordinal));
        Assert.Equal(read.Id, error.Callable);
        var resolved = error.Origin.SourceHirOrigin;
        var span = Assert.IsType<SourceSpan>(resolved.Span);
        Assert.Contains("M(q)", TextAt(compilation, span), StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsAnalysisRecomputesTheSameSourceFactAcrossBothTransformations()
    {
        var compilation = CompileMir(
            """
            function idx(): int {
                return 0;
            }

            operation Worker(q: Qubit, xs: int[]) {
                var value: int = xs[idx()];
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                var xs: int[] = [1];
                Worker(q[0], xs);
            }
            """);
        var source = RequireMir(compilation);
        var worker = RequireCallable(source, "Worker");
        var sourceBounds = source.Analyses.Bounds(worker);
        var sourceFact = Assert.Single(
            sourceBounds.Results,
            result => result.Classification == MirBoundsClassification.Unproven);
        var sourceOrigin = sourceBounds.OriginFor(sourceFact).SourceHirOrigin;
        var request = Assert.Single(UserCalls(source, "Main", worker.Id));
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", request) });
        var result = MirAdjointMaterializer.Run(injected);
        var output = Assert.IsType<MirSnapshot>(result.Output);
        var injectedWorker = RequireCallable(injected, "Worker");
        var injectedBounds = injected.Analyses.Bounds(injectedWorker);
        var injectedFact = Assert.Single(
            injectedBounds.Results,
            candidate => candidate.Classification == MirBoundsClassification.Unproven);
        var injectedOrigin = injectedBounds.OriginFor(injectedFact).SourceHirOrigin;
        var outputWorker = RequireCallable(output, "Worker");
        var outputBounds = output.Analyses.Bounds(outputWorker);
        var outputFact = Assert.Single(
            outputBounds.Results,
            candidate => candidate.Classification == MirBoundsClassification.Unproven);
        var outputOrigin = outputBounds.OriginFor(outputFact).SourceHirOrigin;

        Assert.NotSame(sourceBounds, injectedBounds);
        Assert.NotSame(injectedBounds, outputBounds);
        Assert.Equal(sourceFact.Classification, injectedFact.Classification);
        Assert.Equal(sourceFact.Classification, outputFact.Classification);
        Assert.Equal(sourceOrigin.HirNodeId, injectedOrigin.HirNodeId);
        Assert.Equal(sourceOrigin.HirNodeId, outputOrigin.HirNodeId);
        Assert.Equal(sourceOrigin.Span, injectedOrigin.Span);
        Assert.Equal(sourceOrigin.Span, outputOrigin.Span);
        var sourceSpan = Assert.IsType<SourceSpan>(sourceOrigin.Span);
        Assert.Equal("xs[idx()]", TextAt(compilation, sourceSpan));
        Assert.Equal(MirStage.InverseRequestsInjected, injected.Stage);
        Assert.Equal(MirStage.AdjointsMaterialized, output.Stage);
        Assert.Same(source, injected.TransformationSource);
        Assert.Same(injected, output.TransformationSource);
        Assert.Same(
            injected,
            MirAdjointMaterializer.InjectRequests(
                injected,
                new[] { Instruction(source, "Main", request) }));
    }

    [Fact]
    public void BoundsAnalysisRetainsEachIndexedOperand()
    {
        var compilation = CompileMir(
            """
            function idx(): int {
                return 0;
            }

            operation Main() {
                use left = Qubit[2];
                use right = Qubit[2];
                CNOT(left[idx()], right[idx()]);
            }
            """);
        var source = RequireMir(compilation);

        var main = RequireCallable(source, "Main");
        var bounds = source.Analyses.Bounds(main);
        var facts = bounds.Results
            .Where(result => result.Classification == MirBoundsClassification.Unproven)
            .OrderBy(result => result.Site.OperandIndex)
            .ToArray();
        Assert.Equal(2, facts.Length);
        Assert.Equal(
            new[] { 0, 1 },
            facts.Select(fact => fact.Site.OperandIndex));
        Assert.All(
            facts,
            fact =>
            {
                Assert.Equal(MirBoundsClassification.Unproven, fact.Classification);
                Assert.Equal(2, fact.KnownLength);
                Assert.NotNull(bounds.OriginFor(fact).SourceHirOrigin.Span);
            });
        var origins = facts
            .Select(fact => bounds.OriginFor(fact).SourceHirOrigin)
            .ToArray();
        Assert.NotEqual(origins[0].HirNodeId, origins[1].HirNodeId);
        Assert.NotEqual(origins[0].Span, origins[1].Span);
        Assert.Equal(
            new[] { "left[idx()]", "right[idx()]" },
            origins.Select(origin =>
                TextAt(
                    compilation,
                    Assert.IsType<SourceSpan>(origin.Span))));
    }

    [Fact]
    public void NonUnitaryBuiltinCannotBeMarkedForInverseMaterialization()
    {
        var source = RequireMir(
            CompileMir(
                """
                operation Main() {
                    use q = Qubit[1];
                    Reset(q[0]);
                }
                """));
        var main = RequireCallable(source, "Main");
        var reset = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            instruction =>
                instruction.Target is MirBuiltinGateTarget { Name: "Reset" });
        var error = Assert.Throws<ArgumentException>(
            () => MirAdjointMaterializer.InjectRequests(
                source,
                new[] { Instruction(source, "Main", reset) }));
        Assert.Contains(
            "non-unitary",
            error.Message,
            StringComparison.Ordinal);
    }

    private static Compilation CompileMir(string source)
    {
        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(
                outputPlan: new CompilationOutputPlan(
                    produceMir: true,
                    Array.Empty<TargetBackend>())));
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        return compilation;
    }

    private static MirSnapshot RequireMir(Compilation compilation) =>
        Assert.IsType<MirSnapshot>(compilation.Mir);

    private static MirCallable RequireCallable(
        MirSnapshot snapshot,
        string name) =>
        Assert.Single(
            snapshot.Program.Callables,
            callable => callable.Name == name);

    private static IReadOnlyList<MirQuantumApply> UserCalls(
        MirSnapshot snapshot,
        string owner,
        MirCallableId target)
    {
        var callable = RequireCallable(snapshot, owner);
        return callable.Blocks
            .SelectMany(block => block.Instructions)
            .OfType<MirQuantumApply>()
            .Where(apply => apply.Target is MirUserCallableTarget user
                && user.Callable == target)
            .ToArray();
    }

    private static MirInstructionSite Instruction(
        MirSnapshot snapshot,
        string owner,
        MirQuantumApply instruction) =>
        new(RequireCallable(snapshot, owner).Id, instruction.Id);

    private static string GateName(MirQuantumApply instruction) =>
        Assert.IsType<MirBuiltinGateTarget>(instruction.Target).Name;

    private static string TextAt(
        Compilation compilation,
        SourceSpan span)
    {
        var document = Assert.Single(
            compilation.Sources.Documents,
            candidate => candidate.Ref == span.Document);
        return document.Text[span.Start..span.End];
    }
}
