using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// Whole programs that must compile, plus typed target-shape checks for measurement, bit conditions,
/// and quantum modifiers.
/// </summary>
public class ValidProgramTests
{
    [Theory]
    [InlineData("operation Main(){ use q=Qubit[2]; H(q[0]); CNOT(q[0], q[1]); var r: bit = M(q[0]); }")]
    [InlineData("operation Prep(q: Qubit[]){ H(q[0]); CNOT(q[0], q[1]); }\noperation Main(){ use q=Qubit[2]; Prep(q); var r: bit = M(q[0]); if(r==1){ X(q[1]); } }")]
    [InlineData("operation Main(){ use q=Qubit[3]; H(q[1]); CNOT(q[1],q[2]); CNOT(q[0],q[1]); H(q[0]); var m0: bit = M(q[0]); var m1: bit = M(q[1]); if(m1==1){ X(q[2]); } if(m0==1){ Z(q[2]); } }")]
    [InlineData("operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\noperation Main(){ use q=Qubit[3]; Flip(q); }")]
    [InlineData("operation Main(){ use q=Qubit[2]; for i in 0..1 { Rx(pi/2, q[i]); } }")]
    public void CompilesCleanly(string source) => Compiler.Accepts(source);

    [Fact]
    public void MeasureBitDeclarationIsHoisted()
    {
        // The measurement writes one exact scalar-bit target declaration. The declaration ID is local
        // to the entry body, so this check never compares it with IDs owned by another callable.
        var artifact = MirQasmTestModel.Compile(
            "operation Main(){ use q=Qubit[2]; var r: bit = M(q[0]); if(r==1){ X(q[1]); } }");
        var body = artifact.Program.EntryPoint.Body;
        var statements = MirQasmTestModel.Statements(body).ToArray();
        var measured = Assert.Single(
            statements.OfType<MirQasmMeasurementAssignmentStatement>());
        var target = Assert.IsType<MirQasmDeclarationReferenceExpression>(
            measured.Target);
        var declaration = Assert.Single(
            statements.OfType<MirQasmValueDeclarationStatement>(),
            candidate => candidate.Declaration == target.Declaration);
        var bit = Assert.IsType<MirQasmBitType>(declaration.Type);
        Assert.Equal(1, bit.Width);
        Assert.False(bit.IsRegister);

        var qubit = Assert.IsType<MirQasmIndexExpression>(measured.Qubit);
        Assert.Contains(
            body.DependencyClosure(qubit.Index),
            expression =>
                expression is MirQasmLiteralExpression { Text: "0" });
    }

    [Fact]
    public void BitConditionLegalizesToAnIntegerComparison()
    {
        var artifact = MirQasmTestModel.Compile(
            "operation Main(){ use q=Qubit[2]; var r: bit = M(q[0]); if(r==1){ X(q[1]); } }");
        var body = artifact.Program.EntryPoint.Body;
        var statements = MirQasmTestModel.Statements(body).ToArray();
        var measurement = Assert.Single(
            statements.OfType<MirQasmMeasurementAssignmentStatement>());
        var measured = Assert.IsType<MirQasmDeclarationReferenceExpression>(
            measurement.Target);
        var branch = Assert.Single(statements.OfType<MirQasmIfStatement>());
        var equality = Assert.Single(
            body.DependencyClosure(branch.Condition)
                .OfType<MirQasmBinaryExpression>(),
            expression =>
                expression.Operator == MirQasmBinaryOperator.Equal);
        Assert.Equal(MirQasmBinaryOperator.Equal, equality.Operator);
        Assert.True(
            body.DependsOn(
                equality.Left,
                expression =>
                    expression is MirQasmDeclarationReferenceExpression reference
                    && reference.Declaration == measured.Declaration)
            || body.DependsOn(
                equality.Right,
                expression =>
                    expression is MirQasmDeclarationReferenceExpression reference
                    && reference.Declaration == measured.Declaration));
        var sides = new[] { equality.Left, equality.Right };
        Assert.All(
            sides,
            side =>
            {
                var reference = Assert.IsType<MirQasmDeclarationReferenceExpression>(
                    side);
                var declaration = Assert.Single(
                    statements.OfType<MirQasmValueDeclarationStatement>(),
                    candidate => candidate.Declaration == reference.Declaration);
                Assert.Equal(
                    MirQasmScalarKind.Int,
                    Assert.IsType<MirQasmScalarType>(declaration.Type).Kind);
            });
        Assert.Contains(
            sides,
            side =>
                body.DependsOn(
                    side,
                    dependency =>
                        dependency is MirQasmLiteralExpression { Text: "1" }));
        Assert.Contains(
            sides,
            side =>
                body.DependsOn(
                    side,
                    dependency =>
                        dependency is MirQasmFunctionCallExpression
                        {
                            Target: MirQasmBuiltinFunctionTarget
                            {
                                EmittedName: "int",
                            },
                        }));
    }

    [Fact]
    public void ControlledLowersToTypedModifier()
    {
        var artifact = MirQasmTestModel.Compile(
            "operation Main(){ use q=Qubit[2]; Controlled X(q[0], q[1]); }");
        var apply = Assert.Single(
            MirQasmTestModel.Statements(artifact.Program.EntryPoint.Body)
                .OfType<MirQasmQuantumApplyStatement>());
        Assert.IsType<MirQasmBuiltinGateTarget>(apply.Target);
        Assert.Equal(
            new[] { MirQasmQuantumModifier.Controlled },
            apply.Modifiers);
        Assert.Equal(2, apply.Operands.Length);
    }

    [Fact]
    public void EmptyProgramCompiles()
    {
        var r = Compiler.Compile("operation Main(){ }");
        Assert.True(
            r.Succeeded,
            string.Join(
                " | ",
                r.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
    }
}
