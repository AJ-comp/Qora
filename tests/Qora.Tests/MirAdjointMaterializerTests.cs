using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Mir;

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
        Assert.Same(source, injected.Parent);
        Assert.Equal(source.Id.Revision + 1, injected.Id.Revision);
        Assert.True(injected.DescendsFrom(source.Id));
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
        Assert.Same(injected, output.Parent);
        Assert.Equal(injected.Id.Revision + 1, output.Id.Revision);
        Assert.True(output.DescendsFrom(source.Id));
        Assert.Same(injected, result.Source);
        Assert.Same(output, result.Snapshot);

        var inverseId = result.Inverses[worker.Id];
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

        var originalOrigin = source.Origins.ResolveHir(worker.Origin);
        var inverseOrigin = output.Origins.ResolveHir(inverse.Origin);
        Assert.Equal(originalOrigin.HirCallableId, inverseOrigin.HirCallableId);
        Assert.Equal(originalOrigin.HirNodeId, inverseOrigin.HirNodeId);
        Assert.Equal(originalOrigin.Span, inverseOrigin.Span);
        Assert.NotNull(inverseOrigin.Span);

        var backend = QasmBackend.Run(output);
        Assert.True(backend.Success);
        var target = Assert.IsType<MirOpenQasmTargetProgram>(backend.Target);
        var targetCall = Assert.Single(
            target.EntryPoint.Body.OfType<MirQasmQuantumApplyStatement>());
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
        Assert.Equal(injected.Id, error.Origin.Snapshot);
        var resolved = injected.Origins.ResolveHir(error.Origin);
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
        var resolved = injected.Origins.ResolveHir(error.Origin);
        var span = Assert.IsType<SourceSpan>(resolved.Span);
        Assert.Contains("M(q)", TextAt(compilation, span), StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyFactsAndExactRevisionAuthoritySurviveBothTransformations()
    {
        var source = RequireMir(
            CompileMir(
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
                """));
        var obligation = Assert.Single(source.Safety.UnprovenBounds);
        var worker = RequireCallable(source, "Worker");
        var request = Assert.Single(UserCalls(source, "Main", worker.Id));
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { Instruction(source, "Main", request) });
        var result = MirAdjointMaterializer.Run(injected);
        var output = Assert.IsType<MirSnapshot>(result.Output);
        var inverseWorker = result.Inverses[worker.Id];

        Assert.Equal(source.Id, source.Safety.SnapshotId);
        Assert.Equal(injected.Id, injected.Safety.SnapshotId);
        Assert.Equal(output.Id, output.Safety.SnapshotId);
        var injectedObligation = Assert.Single(
            injected.Safety.UnprovenBounds);
        Assert.Equal(
            obligation.Site.Instruction.Callable,
            injectedObligation.Site.Instruction.Callable);
        Assert.Equal(
            obligation.Site.Instruction.Instruction,
            injectedObligation.Site.Instruction.Instruction);
        var outputObligations = output.Safety.UnprovenBounds;
        Assert.Equal(2, outputObligations.Count);
        Assert.Contains(
            outputObligations,
            candidate => candidate.Site.Instruction.Callable == worker.Id
                && candidate.Site.Instruction.Instruction
                == obligation.Site.Instruction.Instruction);
        Assert.Contains(
            outputObligations,
            candidate => candidate.Site.Instruction.Callable
                    == inverseWorker
                && candidate.Site.Instruction.Instruction
                == obligation.Site.Instruction.Instruction);
        Assert.All(
            outputObligations,
            candidate =>
            {
                Assert.IsType<MirArrayLoad>(
                    output.Program.RequireInstruction(
                        candidate.Site.Instruction));
            });
        Assert.Same(source, injected.Parent);
        Assert.Same(injected, output.Parent);
        Assert.Equal(source.Id.Revision + 1, injected.Id.Revision);
        Assert.Equal(injected.Id.Revision + 1, output.Id.Revision);

        Assert.Throws<ArgumentException>(
            () => source.Safety.Rebase(
                new MirSnapshotId(
                    source.Id.CompilationId,
                    source.Id.CompilationRevision,
                    source.Id.Revision + 2)));
        Assert.Throws<ArgumentException>(
            () => source.Safety.Rebase(
                new MirSnapshotId(
                    CompilationId.New(),
                    source.Id.CompilationRevision,
                    source.Id.Revision + 1)));
        Assert.Same(
            injected,
            MirAdjointMaterializer.InjectRequests(
                injected,
                new[] { Instruction(source, "Main", request) }));
    }

    [Fact]
    public void BoundsObligationsDistinguishIndexedOperandsOnOneInstruction()
    {
        var source = RequireMir(
            CompileMir(
                """
                function idx(): int {
                    return 0;
                }

                operation Main() {
                    use left = Qubit[2];
                    use right = Qubit[2];
                    CNOT(left[idx()], right[idx()]);
                }
                """));

        var obligations = source.Safety.UnprovenBounds
            .OrderBy(obligation => obligation.Site.Ordinal)
            .ToArray();
        Assert.Equal(2, obligations.Length);
        Assert.All(
            obligations,
            obligation => Assert.Equal(
                MirIndexedAccessKind.QubitOperand,
                obligation.Site.Kind));
        Assert.Equal(new[] { 0, 1 }, obligations.Select(
            obligation => obligation.Site.Ordinal));
        Assert.Equal(
            obligations[0].Site.Instruction,
            obligations[1].Site.Instruction);
        Assert.IsType<MirQuantumApply>(
            source.Program.RequireInstruction(
                obligations[0].Site.Instruction));
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
