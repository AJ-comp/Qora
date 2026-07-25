using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// An untyped <c>var</c>/<c>const</c> takes the type of its initializer in the semantic model. Literals,
/// names, operators, array elements and function calls all go through the same expression-type reader; the
/// emitter consumes the declaration symbol rather than guessing again. (Finding G: <c>var x = r</c> with a
/// bit <c>r</c> once emitted a mis-typed <c>int x = r;</c> and compared it as an int (<c>== 1</c>) instead of
/// a bit (<c>== true</c>).)
/// </summary>
public class TypeInferenceTests
{
    [Fact]
    public void UntypedVarFromBitIsEmittedAsBit()
    {
        // `var res = mb` where mb is a bit must emit `bit res = mb;` — not `int res = mb;` — and the condition
        // that reads it must compare as a bit (`res == true`), matching an explicitly-typed `bit res`.
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var mb: bit = M(q[0]); var res = mb; if(res==1){ X(q[0]); } }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.Contains("bit res = mb;", r.Qasm);
        Assert.DoesNotContain("int res", r.Qasm);
        Assert.Contains("res == true", r.Qasm);
    }

    [Fact]
    public void UntypedVarFromIntIsEmittedAsInt() =>
        Compiler.Emits("operation Main(){ use q=Qubit[1]; var cnt: int = 5; var got = cnt; Rx(got, q[0]); }", "int got = cnt;");

    [Fact]
    public void UntypedVarFromRealIsEmittedAsFloat() =>
        // floatness propagates from a built-in constant
        Compiler.Emits("operation Main(){ use q=Qubit[1]; var ang = pi/2; Rx(ang, q[0]); }", "float ang = pi / 2;");

    [Fact]
    public void FloatPropagatesThroughAnotherUntypedVar() =>
        // `var a = pi; var b = a / 2;` — b is a float too (the map records a as float)
        Compiler.Emits("operation Main(){ use q=Qubit[1]; var a = pi; var b = a / 2; Rx(b, q[0]); }", "float b = a / 2;");

    [Fact]
    public void UntypedVarPreservesTheAngleTypeOfAReferencedVariable() =>
        Compiler.Emits(
            "operation Main(){ use q=Qubit[1]; var sourceValue: angle = pi; var copy = sourceValue; Rx(copy, q[0]); }",
            "angle copy = sourceValue;");

    [Fact]
    public void UntypedBooleanLiteralIsEmittedAsBit() =>
        Compiler.Emits(
            "operation Main(){ use q=Qubit[1]; var truth = true; if (truth) { X(q[0]); } }",
            "bit truth = true;");

    [Theory]
    [InlineData("function giveInt(): int { return 2; }\noperation Main(){ use q=Qubit[1]; var result = giveInt(); }", "int result = giveInt();")]
    [InlineData("function giveFloat(): float { return 0.5; }\noperation Main(){ use q=Qubit[1]; var result = giveFloat(); }", "float result = giveFloat();")]
    [InlineData("function giveAngle(): angle { return pi; }\noperation Main(){ use q=Qubit[1]; var result = giveAngle(); }", "angle result = giveAngle();")]
    [InlineData("function giveBit(): bit { return 1; }\noperation Main(){ use q=Qubit[1]; var result = giveBit(); }", "bit result = giveBit();")]
    public void UntypedVarTakesAFunctionReturnType(string source, string qasm) =>
        Compiler.Emits(source, qasm);

    [Fact]
    public void InferredBitFunctionResultKeepsBitConditionRendering()
    {
        const string source =
            "function flag(): bit { return 1; }\n" +
            "operation Main(){ use q=Qubit[1]; var result = flag(); if (result == 1) { X(q[0]); } }";
        Compiler.Emits(source, "bit result = flag();");
        Compiler.Emits(source, "result == true");
    }

    [Fact]
    public void FloatFunctionResultPropagatesThroughAnArithmeticExpression() =>
        Compiler.Emits(
            "function half(): float { return 0.5; }\n" +
            "operation Main(){ use q=Qubit[1]; var result = half() + 1; Rx(result, q[0]); }",
            "float result = half() + 1;");

    [Fact]
    public void BuiltinFunctionReturnTypeIsInferredFromTheSameRegistry() =>
        Compiler.Emits(
            "operation Main(){ use q=Qubit[1]; var bits: bit[] = new bit[2]; var result = AsInt(bits); }",
            "int result = uint[2](bits);");

    [Fact]
    public void InferredTypeSurvivesTheSyntheticReturnDeclaration() =>
        Compiler.Emits(
            "function half(): float { return 0.5; }\n" +
            "function wrapper(): float { var result = half(); return result; }\n" +
            "operation Main(){ use q=Qubit[1]; var answer: float = wrapper(); }",
            "float result = half();");

    [Fact]
    public void InferredTypeSurvivesHiddenArrayStorageAddedByTheBackend() =>
        Compiler.Emits(
            "function half(): float { var values: int[] = [1, 2]; return 0.5; }\n" +
            "operation Main(){ use q=Qubit[1]; var result = half(); }",
            "float result = half(half_values);");

    [Fact]
    public void CallableLookupIsNotHiddenByALocalValueWithTheSameName()
    {
        var r = Compiler.Compile(
            "function value(): float { return 0.5; }\n" +
            "operation Main(){ use q=Qubit[1]; var value: int = 1; var result = value(); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));

        var result = r.Ir!.Operations.SelectMany(op => op.Body).OfType<QDecl>()
            .Single(d => d.Name == "result");
        Assert.Equal(QType.Float, r.Semantics!.FindSymbol(result.Id)!.Type);
    }

    [Fact]
    public void StandaloneEmitterUsesTheSameFunctionReturnLookup()
    {
        var r = Compiler.Compile(
            "function half(): float { return 0.5; }\n" +
            "operation Main(){ use q=Qubit[1]; var result = half(); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));

        Assert.Contains("float result = half();", QasmEmitter.Emit(r.Ir));
    }

    [Fact]
    public void StandaloneEmitterCanInferAnIdlessMangledNamespaceFunctionCall()
    {
        var result = Compiler.Compile("""
            namespace L {
                function half(): float { return 0.5; }
            }
            operation Main() { var result = L.half(); }
            """);
        Assert.True(result.Success,
            string.Join(" | ", result.Errors.Select(error => $"{error.Code}: {error.Message}")));

        var mangled = Qora.Ir.Passes.NameMangler.Mangle(result.Ir!, model: null).Program;
        var main = mangled.Operations.Single(operation => operation.Name == "Main");
        var declaration = main.Body.OfType<QDecl>().Single();
        var text = Assert.IsType<QText>(declaration.Value);
        var call = Assert.IsType<QCallNode>(text.Tree);
        var idlessDeclaration = declaration with
        {
            Value = text with { Tree = call with { CalleeOpId = null } },
        };
        var idlessMain = main with
        {
            Body = main.Body
                .Select(statement => statement.Id == declaration.Id
                    ? (QStmt)idlessDeclaration
                    : statement)
                .ToList(),
        };
        var idlessProgram = mangled with
        {
            Operations = mangled.Operations
                .Select(operation => operation.Id == main.Id ? idlessMain : operation)
                .ToList(),
        };

        Assert.Contains("float result = L_half();", QasmEmitter.Emit(idlessProgram));
    }

    [Fact]
    public void InferredFunctionReturnTypeIsRecordedOnTheDeclarationSymbol()
    {
        var r = Compiler.Compile(
            "function half(): float { return 0.5; }\n" +
            "operation Main(){ use q=Qubit[1]; var result = half(); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors.Select(e => $"{e.Code}: {e.Message}")));

        var decl = r.Ir!.Operations.SelectMany(op => op.Body).OfType<QDecl>().Single(d => d.Name == "result");
        var symbol = r.Semantics!.FindSymbol(decl.Id);
        Assert.NotNull(symbol);
        Assert.Equal(QType.Float, symbol!.Type);

        var function = r.Ir.Operations.Single(op => op.Name == "half");
        var callable = r.Semantics.ProgramSymbols!.LookupCallable("half");
        Assert.Equal(function.Id, callable!.DeclarationNodeId);
        Assert.Null(callable.Type);   // return type stays on QOperation; it is not copied into the symbol
    }
}
