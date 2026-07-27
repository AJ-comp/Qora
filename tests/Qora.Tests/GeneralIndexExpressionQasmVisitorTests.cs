using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// General index expressions must remain ordinary expression trees throughout the OpenQASM backend.
/// These tests exercise each rewriting visitor directly so an unresolved bounds fact cannot hide a
/// broken tree transformation behind the backend's QSEM030 disposition.
/// </summary>
public class GeneralIndexExpressionQasmVisitorTests
{
    [Fact]
    public void NameManglerRecursesThroughAssignmentAndQubitArgumentIndexes()
    {
        var index = new QCallNode(QoraGates.BitsAsInt, new QNode[]
        {
            new QBinOp("+", new QNameRef("x"), new QNumLit(1)),
        });
        var program = Program(
            new QUse("q", 2),
            new QDecl(false, QType.Int, "x", new QText(new QNumLit(0))),
            new QAssign("xs", new QText(new QNumLit(1))) { Index = index },
            new QGate(Array.Empty<string>(), "H", new QArg[] { new QQubitArg("q", index) }));

        var mangling = NameMangler.Mangle(program);
        var mangled = mangling.Program;
        var body = mangled.Operations.Single().Body;
        Assert.Equal("x_", Assert.IsType<QDecl>(body[1]).Name);
        Assert.Equal("x_", mangling.Symbols.GetEmittedName(program.Operations.Single().Body[1].Id));

        var assignment = Assert.IsType<QAssign>(body[2]);
        AssertRenamedCallArgument(assignment.Index);

        var gate = Assert.IsType<QGate>(body[3]);
        var qubit = Assert.IsType<QQubitArg>(Assert.Single(gate.Args));
        AssertRenamedCallArgument(qubit.Index);
    }

    [Fact]
    public void ArrayLocalHoistingRenamesInsideFunctionArgumentsInAMeasurementTarget()
    {
        var indexes = new QDecl(
            false,
            QType.Int,
            "indexes",
            new QArrayLiteral(new QExpr[] { new QText(new QNumLit(0)) }))
        {
            IsArray = true,
        };
        var target = new QIndexNode(
            new QNameRef("q"),
            new QCallNode(QoraGates.BitsAsInt, new QNode[]
            {
                new QIndexNode(new QNameRef("indexes"), new QNumLit(0)),
            }));
        var worker = new QOperation(
            "Worker",
            new[] { new QParam("q", QType.Qubit, null) },
            new QStmt[]
            {
                indexes,
                new QDecl(false, QType.Bit, "measured", new QMeasure(target)),
            });
        var main = new QOperation(
            "Main",
            Array.Empty<QParam>(),
            new QStmt[]
            {
                new QUse("q", 1),
                new QGate(Array.Empty<string>(), "Worker", new QArg[] { new QQubitArg("q", "0") })
                {
                    CalleeOpId = worker.Id,
                },
            });

        var hoisted = ArrayLocalHoisting.Run(new QProgram(new[] { worker, main })).Program;
        var loweredWorker = hoisted.Operations.Single(operation => operation.Name == "Worker");
        var hiddenArray = loweredWorker.Params.Single(parameter => parameter is { Type: QType.Int, IsArray: true });
        var measurement = Assert.IsType<QMeasure>(
            loweredWorker.Body.OfType<QDecl>().Single(declaration => declaration.Name == "measured").Value);
        var measuredElement = Assert.IsType<QIndexNode>(measurement.Target);
        var call = Assert.IsType<QCallNode>(measuredElement.Index);
        var arrayElement = Assert.IsType<QIndexNode>(Assert.Single(call.Args));

        Assert.Equal(hiddenArray.Name, Assert.IsType<QNameRef>(arrayElement.Base).Name);
        Assert.NotEqual("indexes", hiddenArray.Name);
    }

    [Fact]
    public void OpenQasmLoweringCastsCallsNestedInAMeasurementIndex()
    {
        var target = new QIndexNode(
            new QNameRef("q"),
            new QCallNode(QoraGates.BitsAsInt, new QNode[] { new QNameRef("flags") }));
        var program = Program(
            new QUse("q", 4),
            new QDecl(false, QType.Bit, "flags", new QArrayNew(QType.Bit, 2)) { IsArray = true },
            new QDecl(false, QType.Bit, "measured", new QMeasure(target)));

        var errors = QoraValidator.Validate(program, out var semantics);
        Assert.Empty(errors);

        var lowered = OpenQasmLowering.Run(
            program,
            new ExactHirSemanticContext(
                Assert.IsType<HirSemanticModel>(semantics)));
        var declaration = lowered.Operations.Single().Body.OfType<QDecl>()
            .Single(item => item.Name == "measured");
        var measurement = Assert.IsType<QMeasure>(declaration.Value);
        var measuredElement = Assert.IsType<QIndexNode>(measurement.Target);
        var cast = Assert.IsType<OpenQasmUnsignedCastNode>(measuredElement.Index);

        Assert.Equal(2, cast.Width);
        Assert.Equal("flags", Assert.IsType<QNameRef>(cast.Operand).Name);
    }

    private static QProgram Program(params QStmt[] body) =>
        new(new[] { new QOperation("Main", Array.Empty<QParam>(), body) });

    private static void AssertRenamedCallArgument(QNode? node)
    {
        var call = Assert.IsType<QCallNode>(node);
        var addition = Assert.IsType<QBinOp>(Assert.Single(call.Args));
        Assert.Equal("x_", Assert.IsType<QNameRef>(addition.Left).Name);
    }
}
