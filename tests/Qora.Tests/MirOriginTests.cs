using Qora.Compiler;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MirOriginTests
{
    [Fact]
    public void GeneratedOriginSharesItsParentAndPreservesTheRootHirSource()
    {
        var source = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
            }
            """);
        var main = Assert.Single(source.Program.Callables);
        var gate = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var root = gate.Origin.SourceHirOrigin;
        var generated = new MirGeneratedOrigin(
            gate.Origin,
            "first generated step");
        var nested = new MirGeneratedOrigin(
            generated,
            "second generated step");

        Assert.Same(gate.Origin, generated.Parent);
        Assert.Same(generated, nested.Parent);
        Assert.Same(root, generated.SourceHirOrigin);
        Assert.Same(root, nested.SourceHirOrigin);
        Assert.Equal(root.HirNodeId, nested.SourceHirOrigin.HirNodeId);
        Assert.Equal(root.Span, nested.SourceHirOrigin.Span);
        Assert.NotNull(root.Span);
    }

    [Fact]
    public void OriginFormattingDoesNotPrintTheComputedSourceOrigin()
    {
        var source = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                H(q[0]);
            }
            """);
        var main = Assert.Single(source.Program.Callables);
        var gate = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var root = gate.Origin.SourceHirOrigin;
        var generated = new MirGeneratedOrigin(root, "generated test step");
        var qubitAccess = Assert.Single(
            gate.Operands
                .OfType<MirQubitCallOperand>())
            .Qubit;

        var rootText = root.ToString();
        var generatedText = generated.ToString();
        var accessText = qubitAccess.ToString();

        Assert.Contains(nameof(MirHirOrigin.HirNodeId), rootText);
        Assert.Contains(nameof(MirHirOrigin.Span), rootText);
        Assert.Contains("generated test step", generatedText);
        Assert.Contains(nameof(MirQubitAccess.Origin), accessText);
        Assert.DoesNotContain(nameof(MirOrigin.SourceHirOrigin), rootText);
        Assert.DoesNotContain(nameof(MirOrigin.SourceHirOrigin), generatedText);
        Assert.DoesNotContain(nameof(MirOrigin.SourceHirOrigin), accessText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GeneratedOriginRejectsAnEmptyReason(string reason)
    {
        var source = CompileMir(
            """
            operation Main() {
                use q = Qubit[1];
                X(q[0]);
            }
            """);
        var parent = Assert.Single(source.Program.Callables).Origin;

        Assert.Throws<ArgumentException>(
            () => new MirGeneratedOrigin(parent, reason));
    }

    [Fact]
    public void MaterializedAdjointKeepsTheOriginalCallableAndInstructionOriginChains()
    {
        var source = CompileMir(
            """
            operation Worker(q: Qubit) {
                X(q);
            }

            operation Main() {
                use q = Qubit[1];
                Worker(q[0]);
            }
            """);
        var worker = Assert.Single(
            source.Program.Callables,
            callable => callable.Name == "Worker");
        var main = Assert.Single(
            source.Program.Callables,
            callable => callable.Name == "Main");
        var request = Assert.Single(
            main.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>(),
            instruction => instruction.Target is MirUserCallableTarget target
                && target.Callable == worker.Id);
        var requestSite = new MirInstructionSite(main.Id, request.Id);
        var injected = MirAdjointMaterializer.InjectRequests(
            source,
            new[] { requestSite });
        var result = MirAdjointMaterializer.Run(injected);
        var output = Assert.IsType<MirSnapshot>(result.Output);
        var inverse = output.Program.RequireCallable(result.Inverses[worker.Id]);

        var callableOrigin = Assert.IsType<MirGeneratedOrigin>(inverse.Origin);
        Assert.Same(worker.Origin, callableOrigin.Parent);
        Assert.Same(
            worker.Origin.SourceHirOrigin,
            callableOrigin.SourceHirOrigin);

        var originalGate = Assert.Single(
            worker.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var inverseGate = Assert.Single(
            inverse.Blocks
                .SelectMany(block => block.Instructions)
                .OfType<MirQuantumApply>());
        var instructionOrigin = Assert.IsType<MirGeneratedOrigin>(
            inverseGate.Origin);
        Assert.Same(originalGate.Origin, instructionOrigin.Parent);
        Assert.Same(
            originalGate.Origin.SourceHirOrigin,
            instructionOrigin.SourceHirOrigin);
    }

    private static MirSnapshot CompileMir(string source)
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
        return Assert.IsType<MirSnapshot>(compilation.Mir);
    }
}
