using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

public class MeasureConditionLineageTests
{
    [Fact]
    public void ANewHirNodeCannotBePublishedWithoutOneTypedOrigin()
    {
        var parent = Program(
            new QGate(
                Array.Empty<string>(),
                "X",
                new QArg[] { new QQubitArg("q", "0") }));
        var operation = parent.Operations.Single();
        var child = parent with
        {
            Operations = new[]
            {
                operation with
                {
                    Body = operation.Body
                        .Append<QStmt>(
                            new QDecl(
                                false,
                                QType.Int,
                                "temporary",
                                new QText(new QNumLit(0))))
                        .ToArray(),
                },
            },
        };
        var builder = new HirPipelineBuilder(
            CompilationId.New(),
            new CompilationRevision(0));
        _ = builder.Advance(HirStage.Lowered, parent);
        _ = builder.Advance(HirStage.MeasurementLowered, child);

        var error = Assert.Throws<ArgumentException>(builder.Build);
        Assert.Contains("exactly one", error.Message);
    }

    [Fact]
    public void CompilationKeepsSynthesisProvenanceSeparateFromSemanticIdentity()
    {
        var compilation = QoraCompiler.Compile(
            """
            operation Main() {
                use q = Qubit[1];
                if (M(q[0]) == 1) {
                    X(q[0]);
                }
            }
            """);
        Assert.True(compilation.Succeeded);

        var source = compilation.Hir.Require(HirStage.ImportsExpanded);
        var lowered = compilation.Hir.Require(HirStage.MeasurementLowered);
        var sourceIf = Assert.IsType<QIf>(
            source.Program.Operations.Single().Body[1]);
        var declaration = Assert.IsType<QDecl>(
            lowered.Program.Operations.Single().Body[1]);
        var loweredIf = Assert.IsType<QIf>(
            lowered.Program.Operations.Single().Body[2]);

        Assert.Equal(sourceIf.Id, loweredIf.Id);
        Assert.Throws<InvalidOperationException>(
            () => compilation.Hir.Lineage.ResolveNodeId(
                lowered.Id,
                source.Id,
                declaration.Id));
        Assert.Equal(
            new HirNodeRef(source.Id, sourceIf.Id),
            compilation.Hir.Lineage.ResolveProvenance(
                lowered.Id,
                source.Id,
                declaration.Id));
    }

    [Fact]
    public void IfPreservesOwnerIdAndLinksSynthesizedDeclaration()
    {
        var source = new QIf(
            MeasurementCondition(),
            new QStmt[]
            {
                new QGate(
                    Array.Empty<string>(),
                    "X",
                    new QArg[] { new QQubitArg("q", "0") }),
            },
            Array.Empty<QStmt>());

        var lowered = MeasureConditionLowering.Run(Program(source));
        var body = lowered.Program.Operations.Single().Body;
        var declaration = Assert.IsType<QDecl>(body[0]);
        var rewritten = Assert.IsType<QIf>(body[1]);

        Assert.Equal(source.Id, rewritten.Id);
        Assert.False(lowered.Syntheses.IsDefault);
        Assert.Collection(
            lowered.Syntheses,
            synthesis => Assert.Equal(
                new NodeSynthesis(source.Id, declaration.Id),
                synthesis));
    }

    [Fact]
    public void WhilePreservesOwnerIdAndLinksDeclarationAndRemeasurement()
    {
        var source = new QWhile(
            MeasurementCondition(),
            new QStmt[]
            {
                new QGate(
                    Array.Empty<string>(),
                    "X",
                    new QArg[] { new QQubitArg("q", "0") }),
            });

        var lowered = MeasureConditionLowering.Run(Program(source));
        var body = lowered.Program.Operations.Single().Body;
        var declaration = Assert.IsType<QDecl>(body[0]);
        var rewritten = Assert.IsType<QWhile>(body[1]);
        var remeasurement = Assert.IsType<QAssign>(rewritten.Body[^1]);

        Assert.Equal(source.Id, rewritten.Id);
        Assert.False(lowered.Syntheses.IsDefault);
        Assert.Collection(
            lowered.Syntheses,
            synthesis => Assert.Equal(
                new NodeSynthesis(source.Id, declaration.Id),
                synthesis),
            synthesis => Assert.Equal(
                new NodeSynthesis(source.Id, remeasurement.Id),
                synthesis));
    }

    [Fact]
    public void RepeatPreservesOwnerIdAndLinksSynthesizedDeclaration()
    {
        var source = new QRepeat(
            new QStmt[]
            {
                new QGate(
                    Array.Empty<string>(),
                    "X",
                    new QArg[] { new QQubitArg("q", "0") }),
            },
            MeasurementCondition());

        var lowered = MeasureConditionLowering.Run(Program(source));
        var rewritten = Assert.IsType<QRepeat>(lowered.Program.Operations.Single().Body.Single());
        var declaration = Assert.IsType<QDecl>(rewritten.Body[^1]);

        Assert.Equal(source.Id, rewritten.Id);
        Assert.False(lowered.Syntheses.IsDefault);
        Assert.Collection(
            lowered.Syntheses,
            synthesis => Assert.Equal(
                new NodeSynthesis(source.Id, declaration.Id),
                synthesis));
    }

    private static QProgram Program(QStmt statement) =>
        new(new[]
        {
            new QOperation(
                "Main",
                Array.Empty<QParam>(),
                new QStmt[] { statement }),
        });

    private static QCond MeasurementCondition() =>
        new(new QBinOp(
            "==",
            new QCallNode(
                QoraGates.Measurement,
                new QIndexNode(new QNameRef("q"), new QNumLit(0))),
            new QNumLit(1)));
}
