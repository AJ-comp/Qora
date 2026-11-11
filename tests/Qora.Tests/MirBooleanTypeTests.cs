using Qora.Ir;
using Qora.Ir.Mir;

namespace Qora.Tests;

public sealed class MirBooleanTypeTests
{
    [Fact]
    public void LogicalAndComparisonInstructionsRejectBitResultsAtConstruction()
    {
        var origin = MirTestContext.Create().Origin();
        var bit = new MirValue(new MirValueId(0), MirType.Scalar(QType.Bit), origin);

        Assert.Throws<ArgumentException>(() => new MirUnary(
            new MirInstructionId(0), bit, MirUnaryOperator.LogicalNot, bit.Id, origin));
        Assert.Throws<ArgumentException>(() => new MirBinary(
            new MirInstructionId(1), bit, MirBinaryOperator.Equal, bit.Id, bit.Id, origin));
    }

    [Fact]
    public void MeasurementStaysBitWhileComparisonAndLogicalResultsAreBool()
    {
        var program = Compile("""
            operation Main(): bool {
                use q = Qubit[1];
                var measured: bit = M(q[0]);
                var matches: bool = measured == 1;
                if (!matches || true) { return true; }
                return false;
            }
            """);
        var callable = program.EntryPoint;
        var instructions = callable.Blocks.SelectMany(block => block.Instructions).ToArray();

        Assert.Equal(MirType.Scalar(QType.Bit), Assert.Single(instructions.OfType<MirMeasure>()).Result.Type);
        Assert.All(instructions.OfType<MirBinary>(), binary =>
            Assert.Equal(MirType.Scalar(QType.Bool), binary.Result.Type));
        Assert.All(instructions.OfType<MirUnary>(), unary =>
            Assert.Equal(MirType.Scalar(QType.Bool), unary.Result.Type));
        Assert.All(callable.Blocks.Select(block => block.Terminator).OfType<MirBranch>(), branch =>
            Assert.Equal(MirType.Scalar(QType.Bool), callable.RequireValue(branch.Condition).Type));
        Assert.Equal(MirType.Scalar(QType.Bool), callable.ReturnType);
        Assert.Empty(QoraMirVerifier.Verify(program));
    }

    [Theory]
    [InlineData(MirBinaryOperator.Less)]
    [InlineData(MirBinaryOperator.GreaterOrEqual)]
    [InlineData(MirBinaryOperator.Add)]
    public void VerifierRejectsNumericOperationsOnBoolOperands(MirBinaryOperator operation)
    {
        var program = Compile("operation Main() { var same: bool = true == false; }");
        var callable = program.EntryPoint;
        var binary = Assert.Single(callable.Blocks.SelectMany(block => block.Instructions).OfType<MirBinary>());
        var result = operation == MirBinaryOperator.Add
            ? new MirValue(binary.Result.Id, MirType.Scalar(QType.Int), binary.Origin)
            : binary.Result;
        var malformed = ReplaceInstruction(program, new MirBinary(
            binary.Id, result, operation, binary.Left, binary.Right, binary.Origin));

        Assert.Contains(QoraMirVerifier.Verify(malformed), error =>
            error.Code == "MIR076" && error.Message.Contains("bool operands", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bit", "1")]
    [InlineData("int", "1")]
    [InlineData("float", "1.0")]
    public void VerifierRejectsConversionsFromBool(string destinationType, string value)
    {
        var program = Compile($"operation Main() {{ var flag: bool = true; var value: {destinationType} = {value}; }}");
        var callable = program.EntryPoint;
        var instructions = callable.Blocks.SelectMany(block => block.Instructions).ToArray();
        var flag = Assert.Single(instructions.OfType<MirConstant>(), constant => constant.Result.Type.ElementType == QType.Bool);
        var destination = instructions.Last(instruction => instruction.Results.Count != 0).Results[0];
        var destinationInstruction = instructions.Last(instruction => instruction.Results.Contains(destination));
        var malformed = ReplaceInstruction(program, new MirConvert(
            destinationInstruction.Id, destination, flag.Result.Id, destinationInstruction.Origin));

        Assert.Contains(QoraMirVerifier.Verify(malformed), error =>
            error.Code == "MIR075" && error.Message.Contains("bool", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bit")]
    [InlineData("int")]
    public void VerifierRejectsNonBoolBranchConditions(string sourceType)
    {
        var program = Compile($"operation Main() {{ var value: {sourceType} = 1; if (true) {{ }} }}");
        var callable = program.EntryPoint;
        var condition = callable.Values.Last(value => value.Type.ElementType.ToString().Equals(sourceType, StringComparison.OrdinalIgnoreCase));
        var blocks = callable.Blocks.Select(block => block.Terminator is MirBranch branch
            ? block with { Terminator = branch with { Condition = condition.Id } }
            : block).ToArray();
        var malformedCallable = new MirCallable(callable.Id, callable.Name, callable.Kind, callable.ReturnType,
            callable.Parameters, blocks, callable.Origin);
        var malformed = new MirProgram(malformedCallable, new[] { malformedCallable });

        Assert.Contains(QoraMirVerifier.Verify(malformed), error =>
            error.Code == "MIR124" && error.Message.Contains("bool", StringComparison.Ordinal));
    }

    [Fact]
    public void BoolArraysUseClassicalArrayStorageAndFalseInitialization()
    {
        var artifact = MirQasmTestModel.Compile("""
            function First(values: bool[]): bool { return values[0]; }
            operation Main(): bool {
                var flags: bool[] = new bool[2];
                flags[1] = true;
                return First(flags);
            }
            """);

        Assert.Contains("array[bool, 2]", artifact.Text);
        Assert.Contains("false", artifact.Text);
        Assert.Contains("output bool", artifact.Text);
        Assert.DoesNotContain("bit[2]", artifact.Text);
        var function = Assert.Single(artifact.Source.Program.Callables, callable => callable.Kind == MirCallableKind.Function);
        Assert.Equal(MirType.Scalar(QType.Bool), function.ReturnType);
        var parameter = Assert.IsType<MirClassicalParameter>(Assert.Single(function.Parameters));
        Assert.Equal(QType.Bool, parameter.Value.Type.ElementType);
        Assert.True(parameter.Value.Type.IsArray);
    }

    private static MirProgram Compile(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Error)));
        return Assert.IsType<MirSnapshot>(result.Mir).Program;
    }

    private static MirProgram ReplaceInstruction(MirProgram program, MirInstruction replacement)
    {
        var callable = program.EntryPoint;
        var blocks = callable.Blocks.Select(block => block with
        {
            Instructions = block.Instructions.Select(instruction => instruction.Id == replacement.Id
                ? replacement
                : instruction).ToArray(),
        }).ToArray();
        var rewritten = new MirCallable(callable.Id, callable.Name, callable.Kind, callable.ReturnType,
            callable.Parameters, blocks, callable.Origin);
        return new MirProgram(rewritten, new[] { rewritten });
    }
}
