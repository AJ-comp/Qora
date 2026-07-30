using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// One-dimensional classical arrays: explicit <c>T[]</c> declarations and parameters, literals,
/// zero-initialized <c>new T[N]</c>, element reads/writes, and <c>Count</c>.
/// <para><c>int[]</c>, <c>float[]</c> and <c>angle[]</c> lower to OpenQASM's general <c>array[T, N]</c>
/// type. <c>bit[]</c> cannot: bit is the one element type OpenQASM forbids as an array base type ("bit,
/// bit[n] and stretch are not valid array base types"), because it has a dedicated register type. So a
/// <c>bit[]</c> lowers to <c>bit[N]</c>, and its <c>Count</c> folds to a literal rather than <c>sizeof</c>,
/// which is likewise undefined on a bit register.</para>
/// </summary>
public class ClassicalArrayTests
{
    [Theory]
    [InlineData("int", "1, 2", "var x: int = values[0]")]
    [InlineData("float", "1.0, 2.5", "var x: float = values[0]")]
    [InlineData("bit", "0, 1", "var x: bit = values[0]")]
    [InlineData("angle", "0.0, pi/2", "var x: angle = values[0]")]
    public void AcceptsExplicitArrayParametersDeclarationsAndLiterals(
        string type, string elements, string read)
    {
        var source = $$"""
            operation Read(values: {{type}}[]) {
                {{read}};
            }
            operation Main() {
                var values: {{type}}[] = [{{elements}}];
                Read(values);
            }
            """;

        Compiler.Accepts(source);
    }

    [Theory]
    [InlineData("int", "0")]
    [InlineData("float", "0.0")]
    [InlineData("bit", "0")]
    [InlineData("angle", "0.0")]
    public void AcceptsPositiveLiteralNewForEveryArrayType(string type, string assignedValue) =>
        Compiler.Accepts($"operation Main(){{ var values: {type}[] = new {type}[3]; values[2] = {assignedValue}; }}");

    [Fact]
    public void AcceptsElementReadsWritesAndCount()
    {
        Compiler.Accepts("""
            operation Main() {
                var values: int[] = [1, 2, 3];
                var saved: int = values[1];
                values[0] = saved;
                for i in 0..values.Count-1 {
                    values[i] = values[i] + 1;
                }
            }
            """);
    }

    [Fact]
    public void AcceptsArrayElementAsScalarCallArgument()
    {
        var result = CompileSuccessfully("operation Take(value: int){} operation Main(){ var values: int[] = [1,2]; Take(values[0]); }");

        var program = result.Targets.OpenQasm!.Program;
        var call = Assert.Single(
            TargetStatements(program.EntryBody)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var target = Assert.IsType<MirQasmUserQuantumTarget>(call.Target);
        var callee = Resolve(program, target.Callable);
        Assert.Equal(MirQasmCallableKind.Operation, callee.Kind);
        Assert.IsType<MirQasmScalarType>(
            Assert.Single(callee.Parameters).Type);
        var operand = Assert.Single(call.Operands);
        var argument = Assert.Single(
            program.EntryBody.DependencyClosure(operand)
                .OfType<MirQasmIndexExpression>());
        var array = Assert.IsType<MirQasmArrayType>(
            TargetPlaceType(program.EntryBody, argument.Base));
        Assert.Equal(MirQasmScalarKind.Int, array.ElementType.Kind);
        Assert.Equal(2, array.Length);
        Assert.Equal(
            "0",
            Assert.Single(
                program.EntryBody.DependencyClosure(argument.Index)
                    .OfType<MirQasmLiteralExpression>()).Text);
    }

    [Fact]
    public void AcceptsMeasurementIntoBitArrayElement()
    {
        Compiler.Accepts("""
            operation Main() {
                use q = Qubit[2];
                var results: bit[] = new bit[2];
                results[0] = M(q[0]);
                results[1] = M(q[1]);
            }
            """);
    }

    /// <summary>Array locals go wherever a scalar goes — a helper op, a branch, a loop. The old QSEM012
    /// arm rejecting these was OpenQASM's placement rule leaking into the language; the QASM backend's
    /// MIR-to-OpenQASM lowering now absorbs it (hidden-parameter threading / scope-top hoisting), so the
    /// language accepts all three shapes it once rejected.</summary>
    [Theory]
    [InlineData("operation Work(){ var values: int[] = [1,2]; } operation Main(){ Work(); }")]
    [InlineData("operation Main(){ var flag: int = 1; if(flag==1){ var values: int[] = [1,2]; } }")]
    [InlineData("operation Main(){ for i in 0..1 { var values: int[] = [1,2]; } }")]
    public void AcceptsArrayDeclarationsOutsideMainTopLevel(string source) => Compiler.Accepts(source);

    [Theory]
    [InlineData("operation Main(){ var values: int[] = new int[0]; }")]
    [InlineData("operation Main(){ var values: int[] = new int[-1]; }")]
    [InlineData("operation Main(){ var n: int = 3; var values: int[] = new int[n]; }")]
    public void RejectsNewWithoutAPositiveLiteralLength(string source) => RejectsCleanly(source);

    [Theory]
    [InlineData("operation Take(values: int[]){} operation Main(){ var value: int = 1; Take(value); }")]
    [InlineData("operation Take(value: int){} operation Main(){ var values: int[] = [1,2]; Take(values); }")]
    [InlineData("operation Main(){ var value: int = 1; var copy: int = value[0]; }")]
    [InlineData("operation Main(){ var value: int = 1; value[0]=2; }")]
    [InlineData("operation Main(){ var values: int[] = 1; }")]
    [InlineData("operation Main(){ var value: int = [1,2]; }")]
    public void RejectsScalarArrayShapeMismatch(string source) => RejectsCleanly(source);

    [Fact]
    public void RejectsMismatchedArrayElementType() =>
        RejectsCleanly("operation Take(values: int[]){} operation Main(){ var values: float[] = [1.0,2.0]; Take(values); }");

    [Theory]
    [InlineData("operation Main(){ var values: int[] = [1,2]; var x: int = values[2]; }")]
    [InlineData("operation Main(){ var values: int[] = [1,2]; values[2]=3; }")]
    [InlineData("operation Main(){ var values: int[] = new int[1]; var x: int = values[1]; }")]
    public void RejectsLiteralIndexOutsideKnownBounds(string source) => RejectsCleanly(source);

    [Fact]
    public void RejectsCountOnScalar() =>
        RejectsCleanly("operation Main(){ var value: int = 1; for i in 0..value.Count-1 { value=i; } }");

    [Fact]
    public void RejectsSameArrayPassedToReadonlyAndMutableParameters()
    {
        RejectsCleanly("""
            operation Copy(var left: int[], right: int[]) {
                left[0] = right[0];
            }
            operation Main() {
                var values: int[] = [1, 2];
                Copy(var values, values);
            }
            """);
    }

    [Theory]
    [InlineData("operation Main(){ const values: int[] = [1,2]; values[0]=3; }")]
    [InlineData("operation Change(var values: int[]){ values[0]=3; } operation Main(){ const values: int[] = [1,2]; Change(var values); }")]
    public void RejectsMutationOfConstArray(string source)
    {
        var result = Compiler.Compile(source);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), error => error.Code == "QSEM024");
    }

    /// <summary>Every element type EXCEPT bit takes the general array form. See <see cref="EmitsBitArrayAsNativeBitRegister"/>.</summary>
    [Theory]
    [InlineData("int", "1, 2", MirQasmScalarKind.Int)]
    [InlineData("float", "1.0, 2.5", MirQasmScalarKind.Float)]
    [InlineData("angle", "0.0, pi/2", MirQasmScalarKind.Angle)]
    public void EmitsGeneralOpenQasmArrayAndBraceLiteral(
        string type,
        string sourceElements,
        MirQasmScalarKind targetKind)
    {
        var result = CompileSuccessfully($"operation Main(){{ var values: {type}[] = [{sourceElements}]; }}");

        var declaration = Assert.Single(
            TargetStatements(result.Targets.OpenQasm!.Program.EntryBody)
                .OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(targetKind, declaration.Type.ElementType.Kind);
        Assert.Equal(2, declaration.Type.Length);
        Assert.Equal(2, declaration.Elements.Length);
        if (targetKind == MirQasmScalarKind.Angle)
        {
            Assert.Equal(
                "0.0",
                Assert.IsType<MirQasmLiteralExpression>(
                    declaration.Elements[0]).Text);
            Assert.IsType<MirQasmLiteralExpression>(
                declaration.Elements[1]);
        }
    }

    /// <summary>
    /// A literal is written element by element, NEVER as a bitstring: the spec's prose puts element 0 at the
    /// right (least-significant) end while Braket reads it at the left, so a non-uniform bitstring would
    /// silently reverse the array. Indexed writes mean the same thing on both.
    /// </summary>
    [Fact]
    public void EmitsBitArrayAsNativeBitRegister()
    {
        var result = CompileSuccessfully("operation Main(){ var flags: bit[] = [0,1]; }");

        var body = result.Targets.OpenQasm!.Program.EntryBody;
        var statements = TargetStatements(body);
        var declaration = Assert.Single(
            statements.OfType<MirQasmValueDeclarationStatement>(),
            value => value.Type is MirQasmBitType { Width: 2 });
        var bit = Assert.IsType<MirQasmBitType>(declaration.Type);
        Assert.True(bit.IsRegister);
        Assert.DoesNotContain(
            statements.OfType<MirQasmArrayDeclarationStatement>(),
            array => array.Type.ElementType.Kind == MirQasmScalarKind.Int);
        var writes = statements
            .OfType<MirQasmAssignmentStatement>()
            .Where(
                assignment =>
                    assignment.Target is MirQasmIndexExpression
                    {
                        Base: MirQasmDeclarationReferenceExpression reference,
                    }
                    && reference.Declaration == declaration.Declaration)
            .ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Contains(
            writes,
            write => IsLiteralIndexWrite(body, write, "0", "0"));
        Assert.Contains(
            writes,
            write => IsLiteralIndexWrite(body, write, "1", "1"));
    }

    /// <summary>
    /// <c>new bit[N]</c> keeps Qora's zero-initialization promise explicitly: a bare <c>bit[N] f;</c> is
    /// UNDEFINED rather than zeroed. All-zeros is uniform, so the element-order divergence cannot bite it.
    /// </summary>
    [Fact]
    public void EmitsNewBitArrayAsZeroInitializedBitRegister()
    {
        var result = CompileSuccessfully("operation Main(){ var flags: bit[] = new bit[3]; }");

        var statements = TargetStatements(
            result.Targets.OpenQasm!.Program.EntryBody);
        var declaration = Assert.Single(
            statements.OfType<MirQasmValueDeclarationStatement>(),
            value => value.Type is MirQasmBitType { Width: 3 });
        Assert.IsType<MirQasmBitType>(declaration.Type);
        Assert.True(
            declaration.Initializer is MirQasmLiteralExpression
            {
                Text: "\"000\"",
            }
            || statements
                .OfType<MirQasmAssignmentStatement>()
                .Count(
                    assignment =>
                        assignment.Target is MirQasmIndexExpression
                        {
                            Base: MirQasmDeclarationReferenceExpression reference,
                        }
                        && reference.Declaration == declaration.Declaration
                        && assignment.Value is MirQasmLiteralExpression { Text: "0" })
                == 3);
        Assert.Empty(statements.OfType<MirQasmArrayDeclarationStatement>());
    }

    /// <summary><c>sizeof</c> is not defined on a bit register, so a bit array's Count must fold to a literal.
    /// Contrast <see cref="EmitsCountAsSizeof"/>, which pins the general-array behaviour for <c>int[]</c>.</summary>
    [Fact]
    public void FoldsBitArrayCountToALiteralRatherThanSizeof()
    {
        var result = CompileSuccessfully("""
            operation Main() {
                use q = Qubit[2];
                var flags: bit[] = new bit[2];
                for i in 0..flags.Count-1 {
                    flags[i] = M(q[i]);
                }
            }
            """);

        var body = result.Targets.OpenQasm!.Program.EntryBody;
        var loop = Assert.Single(
            TargetStatements(body).OfType<MirQasmWhileStatement>());
        var dependencies = TargetLoopDependencies(body, loop).ToArray();
        Assert.Contains(
            dependencies,
            expression =>
                expression is MirQasmLiteralExpression { Text: "2" });
        Assert.DoesNotContain(
            dependencies,
            expression => expression is MirQasmSizeOfExpression);
    }

    [Fact]
    public void EmitsMutableOneDimensionalArrayParameter()
    {
        var result = CompileSuccessfully("""
            operation SetFirst(var values: int[]) {
                values[0] = 7;
            }
            operation Main() {
                var values: int[] = [1, 2];
                SetFirst(var values);
            }
            """);

        var parameter = Assert.Single(
            result.Targets.OpenQasm!.Program.Definitions
                .SelectMany(definition => definition.Parameters),
            candidate =>
                candidate.Access == MirQasmParameterAccess.Mutable
                && candidate.Type is MirQasmArrayType
                {
                    ElementType.Kind: MirQasmScalarKind.Int,
                    Length: null,
                });
        Assert.IsType<MirQasmArrayType>(parameter.Type);
    }

    [Fact]
    public void EmitsCountAsSizeof()
    {
        var result = CompileSuccessfully("""
            operation Visit(var values: int[]) {
                for i in 0..values.Count-1 {
                    values[i] = values[i] + 1;
                }
            }
            operation Main() {
                var values: int[] = [1, 2, 3];
                Visit(var values);
            }
            """);

        var definition = Assert.Single(
            result.Targets.OpenQasm!.Program.Definitions.Where(
                candidate =>
                    candidate.Parameters.Any(
                        parameter =>
                            parameter.Access == MirQasmParameterAccess.Mutable
                            && parameter.Type is MirQasmArrayType
                            {
                                ElementType.Kind: MirQasmScalarKind.Int,
                            })));
        var array = Assert.Single(
            definition.Parameters.Where(
                parameter => parameter.Type is MirQasmArrayType));
        var loop = Assert.Single(
            TargetStatements(definition.Body)
                .OfType<MirQasmWhileStatement>());
        Assert.Contains(
            TargetLoopDependencies(definition.Body, loop),
            expression =>
                    expression is MirQasmSizeOfExpression
                    {
                        Operand: MirQasmParameterReferenceExpression reference,
                    }
                    && reference.Parameter == array.Id);
    }

    [Fact]
    public void EmitsNewAsZeroInitializedArray()
    {
        var result = CompileSuccessfully("operation Main(){ var values: int[] = new int[3]; }");

        var array = Assert.Single(
            TargetStatements(result.Targets.OpenQasm!.Program.EntryBody)
                .OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(MirQasmScalarKind.Int, array.Type.ElementType.Kind);
        Assert.Equal(3, array.Type.Length);
        Assert.All(
            array.Elements,
            element =>
                Assert.Equal(
                    "0",
                    Assert.IsType<MirQasmLiteralExpression>(element).Text));
    }

    [Fact]
    public void EmitsIndexedReadsAndWrites()
    {
        var result = CompileSuccessfully("operation Main(){ var values: int[] = [1,2]; var saved: int = values[1]; values[0]=saved; }");

        var body = result.Targets.OpenQasm!.Program.EntryBody;
        var statements = TargetStatements(body);
        var array = Assert.Single(
            statements.OfType<MirQasmArrayDeclarationStatement>());
        var read = Assert.Single(
            statements.OfType<MirQasmAssignmentStatement>(),
            assignment =>
                assignment.Value is MirQasmIndexExpression
                {
                    Base: MirQasmDeclarationReferenceExpression reference,
                } index
                && reference.Declaration == array.Declaration
                && body.DependsOn(
                    index.Index,
                    dependency =>
                        dependency is MirQasmLiteralExpression { Text: "1" }));
        var saved = Assert.IsType<MirQasmDeclarationReferenceExpression>(
            read.Target);
        Assert.IsType<MirQasmScalarType>(
            TargetPlaceType(body, saved));
        Assert.Contains(
            statements.OfType<MirQasmAssignmentStatement>(),
            assignment =>
                assignment.Target is MirQasmIndexExpression
                {
                    Base: MirQasmDeclarationReferenceExpression reference,
                } index
                && reference.Declaration == array.Declaration
                && body.DependsOn(
                    index.Index,
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "0" })
                && body.DependsOn(
                    assignment.Value,
                    expression =>
                        expression is MirQasmDeclarationReferenceExpression value
                        && value.Declaration == saved.Declaration));
    }

    [Fact]
    public void EmitsBitArrayElementConditionsAsIntegerComparisons()
    {
        var result = CompileSuccessfully("operation Main(){ var flags: bit[] = [0,1]; if(flags[1]==1){ flags[0]=1; } }");

        var body = result.Targets.OpenQasm!.Program.EntryBody;
        var branch = Assert.Single(
            TargetStatements(body).OfType<MirQasmIfStatement>());
        var equality = Assert.Single(
            body.DependencyClosure(branch.Condition)
                .OfType<MirQasmBinaryExpression>(),
            expression =>
                expression.Operator == MirQasmBinaryOperator.Equal);
        var sides = new[] { equality.Left, equality.Right };
        Assert.All(
            sides,
            side =>
                Assert.Equal(
                    MirQasmScalarKind.Int,
                    Assert.IsType<MirQasmScalarType>(
                        TargetPlaceType(body, side)).Kind));
        Assert.Contains(
            sides,
            side =>
                body.DependencyClosure(side)
                    .OfType<MirQasmIndexExpression>()
                    .Any(
                        index =>
                            body.DependsOn(
                                index.Index,
                                expression =>
                                    expression is MirQasmLiteralExpression
                                    {
                                        Text: "1",
                                    })));
        Assert.Contains(
            sides,
            side =>
                body.DependsOn(
                    side,
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "1" }));
        Assert.Contains(
            sides,
            side =>
                body.DependsOn(
                    side,
                    expression =>
                        expression is MirQasmFunctionCallExpression
                        {
                            Target: MirQasmBuiltinFunctionTarget
                            {
                                EmittedName: "int",
                            },
                        }));
    }

    private static Compilation CompileSuccessfully(string source)
    {
        var result = Compiler.Compile(source);
        Assert.True(result.Succeeded, Explain(result));
        Assert.NotNull(result.Targets.OpenQasm);
        Assert.NotNull(result.Targets.OpenQasm!.Program);
        return result;
    }

    private static void RejectsCleanly(string source)
    {
        var result = Compiler.Compile(source);
        Assert.False(
            result.Succeeded,
            $"expected the array program to be rejected, but it compiled:\n{source}");
        Assert.NotEmpty(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList());
        Assert.DoesNotContain(result.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code is "QORA0000" or "QINTERNAL");
    }

    // --- literal out-of-bounds THROUGH a parameter. A `T[]` parameter carries no length of its own (the
    //     length arrives with the argument and differs per call), so a literal `x[5]` in the body is a
    //     PRECONDITION — "the array passed here needs at least 6 elements" — and the only place to check it is
    //     the CALL. Without that check the identical access written inline is rejected (QSEM016) while the
    //     helper form silently emits an out-of-bounds write. ---

    [Fact]
    public void RejectsLiteralOutOfBoundsThroughAnArrayParameter() =>
        Compiler.Rejects("""
            operation Helper(var x: int[]) { x[5] = 99; }
            operation Main() { use q=Qubit[1]; var a: int[] = [1, 2, 3]; Helper(var a); H(q[0]); }
            """, "QSEM016");

    /// <summary>The requirement folds through a CHAIN: Middle never indexes `y` itself, but it hands it to
    /// Deep, so the length Deep needs is demanded of Middle's caller — where the concrete array enters.</summary>
    [Fact]
    public void RejectsLiteralOutOfBoundsThroughAChainOfArrayParameters() =>
        Compiler.Rejects("""
            operation Deep(var x: int[]) { x[5] = 99; }
            operation Middle(var y: int[]) { Deep(var y); }
            operation Main() { use q=Qubit[1]; var a: int[] = [1, 2, 3]; Middle(var a); H(q[0]); }
            """, "QSEM016");

    /// <summary>NOT over-broad: an array long enough for the callee's literal index is fine.</summary>
    [Fact]
    public void AcceptsAnArrayLongEnoughForTheCalleeLiteralIndex() =>
        Compiler.Accepts("""
            operation Helper(var x: int[]) { x[5] = 99; }
            operation Main() { use q=Qubit[1]; var a: int[] = new int[8]; Helper(var a); H(q[0]); }
            """);

    /// <summary>A DYNAMIC index imposes no static floor — it stays a runtime concern, exactly as it does for a
    /// local array (and as it does in every other language). Only literal indices are checked.</summary>
    [Fact]
    public void AcceptsDynamicIndexingOfAnArrayParameter() =>
        Compiler.Accepts("""
            operation Helper(var x: int[]) { for i in 0..x.Count-1 { x[i] = 1; } }
            operation Main() { use q=Qubit[1]; var a: int[] = [1, 2, 3]; Helper(var a); H(q[0]); }
            """);

    // --- A WHOLE bit[] register is a CONTAINER OF BITS, not a number (QSEM036) ---
    //
    // A bit pattern carries no sign, so it has no single numeric meaning: the same "10" reads 2 unsigned and
    // −2 in two's complement (both verified on the Braket oracle). Rather than pick one silently, every
    // numeric use of a whole register is rejected and `AsInt(f)` is the one way to ask for a reading. What a
    // register may still do without any numeric interpretation: be indexed (`f[0]`), report `.Count`, be
    // passed as an argument, and be compared to another register OF THE SAME WIDTH.

    /// <summary>Every position that would read a whole register AS A NUMBER is a compile error — the rule is
    /// on the VALUE, not on a list of syntactic positions, so assignment, arithmetic, a bare truth condition
    /// and comparison-against-a-number are all one rule. Before this, `int n = f;` compiled and silently
    /// evaluated to −2 on the execution oracle.</summary>
    [Theory]
    [InlineData("var n: int = f;", "int assignment")]
    [InlineData("var n: int = f + 1;", "arithmetic")]
    [InlineData("if (f) { X(q[0]); }", "bare truth condition")]
    [InlineData("if (f == 1) { X(q[0]); }", "comparison against a number")]
    [InlineData("if (f > 1) { X(q[0]); }", "ordering against a number")]
    [InlineData("for i in 0..f { X(q[0]); }", "range bound")]
    [InlineData("Rx(f * pi, q[0]);", "angle expression")]
    public void RejectsAWholeBitRegisterUsedAsANumber(string statement, string why)
    {
        var r = Compiler.Compile($"operation Main(){{ use q=Qubit[1]; var f: bit[] = new bit[2]; {statement} }}");
        Assert.False(r.Succeeded, $"{why}: expected QSEM036 but the program compiled");
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM036");
    }

    /// <summary>`AsInt(f)` is the ONE reading, and it emits the WIDTH-QUALIFIED unsigned cast: the spec allows
    /// `bit[n]` → `uint[m]` only when `n == m`, and Braket accepts a wrong width silently, so the compiler
    /// must supply it. UNSIGNED because `int[2]("10")` is −2 while `uint[2]("10")` is 2.</summary>
    [Fact]
    public void AsIntEmitsTheWidthQualifiedUnsignedCast()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; if (AsInt(f) == 1) { X(q[0]); } }");
        Assert.True(r.Succeeded, Explain(r));
        var body = r.Targets.OpenQasm!.Program.EntryBody;
        var cast = Assert.Single(
            TargetStatements(body)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmUnsignedCastExpression>());
        Assert.Equal(2, cast.Width);
        var source = Assert.IsType<MirQasmBitType>(
            TargetPlaceType(body, cast.Operand));
        Assert.Equal(2, source.Width);
        Assert.True(source.IsRegister);
    }

    /// <summary>The width follows the REGISTER, not a single op-wide guess — two same-named registers in
    /// disjoint blocks are different arrays, and each cast carries its own width.</summary>
    [Fact]
    public void AsIntTakesTheWidthOfTheNearestDeclaration()
    {
        var r = Compiler.Compile("""
            operation Main() {
                use q = Qubit[1];
                if (1 == 1) { var f: bit[] = new bit[2]; if (AsInt(f) == 1) { X(q[0]); } }
                else        { var f: bit[] = new bit[5]; if (AsInt(f) == 1) { X(q[0]); } }
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        var body = r.Targets.OpenQasm!.Program.EntryBody;
        var casts = TargetStatements(body)
            .SelectMany(MirQasmTestModel.Expressions)
            .OfType<MirQasmUnsignedCastExpression>()
            .ToArray();
        Assert.Equal(new[] { 2, 5 }, casts.Select(cast => cast.Width).Order());
        Assert.All(
            casts,
            cast =>
            {
                var source = Assert.IsType<MirQasmBitType>(
                    TargetPlaceType(body, cast.Operand));
                Assert.Equal(cast.Width, source.Width);
            });
    }

    /// <summary>Register-to-register comparison needs no numeric reading — it matches bit patterns — so it
    /// stays legal and emits BARE, with no cast at all.</summary>
    [Fact]
    public void EqualWidthRegistersCompareDirectly()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var g: bit[] = new bit[2]; if (f == g) { X(q[0]); } }");
        Assert.True(r.Succeeded, Explain(r));
        var body = r.Targets.OpenQasm!.Program.EntryBody;
        var branch = Assert.Single(
            TargetStatements(body).OfType<MirQasmIfStatement>());
        MirQasmBinaryExpression? registerEquality = null;
        Assert.True(
            body.DependsOn(
                branch.Condition,
                expression =>
                {
                    if (expression is not MirQasmBinaryExpression
                        {
                            Operator: MirQasmBinaryOperator.Equal,
                        } equality)
                    {
                        return false;
                    }
                    if (equality.Left is not MirQasmDeclarationReferenceExpression
                        || equality.Right is not MirQasmDeclarationReferenceExpression)
                    {
                        return false;
                    }
                    registerEquality = equality;
                    return true;
                }));
        Assert.NotNull(registerEquality);
        var left = Assert.IsType<MirQasmBitType>(
            TargetPlaceType(body, registerEquality!.Left));
        var right = Assert.IsType<MirQasmBitType>(
            TargetPlaceType(body, registerEquality.Right));
        Assert.Equal(2, left.Width);
        Assert.Equal(2, right.Width);
        Assert.False(
            SameTargetPlace(registerEquality.Left, registerEquality.Right));
        Assert.DoesNotContain(
            TargetStatements(body).SelectMany(MirQasmTestModel.Expressions),
            expression => expression is MirQasmUnsignedCastExpression);
    }

    /// <summary>Different widths are never equal in OpenQASM whatever bits they hold — `bit[2] "10"` and
    /// `bit[3] "010"` both read as 2 yet compare unequal — so the comparison is rejected instead of silently
    /// answering "different".</summary>
    [Fact]
    public void RejectsComparingRegistersOfDifferentWidths()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var g: bit[] = new bit[3]; if (f == g) { X(q[0]); } }");
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM036");
    }

    /// <summary>ORDERING between registers is rejected even at equal width: the target compares `&lt;`/`&gt;`
    /// NUMERICALLY while `==` compares bit patterns, so the two would disagree about the same pair. Ordering
    /// is a numeric question and must be asked with an explicit reading on both sides.</summary>
    [Fact]
    public void RejectsOrderingBetweenRegisters()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; var f: bit[] = new bit[2]; var g: bit[] = new bit[2]; if (f < g) { X(q[0]); } }");
        Assert.False(r.Succeeded);
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM036");
    }

    /// <summary>`AsInt` reads a whole register; anything else has no width to cast with, so it is refused at
    /// the source rather than becoming a QINTERNAL in the target lowering. A single bit is already a value.</summary>
    [Theory]
    [InlineData("var n: int = AsInt(k);", "an int variable")]
    [InlineData("var n: int = AsInt(f[0]);", "a single bit")]
    [InlineData("var n: int = AsInt(4);", "a literal")]
    [InlineData("var n: int = AsInt(f, f);", "two arguments")]
    public void RejectsAsIntOnAnythingButAWholeRegister(string statement, string why)
    {
        var r = Compiler.Compile($"operation Main(){{ use q=Qubit[1]; var f: bit[] = new bit[2]; var k: int = 3; {statement} }}");
        Assert.False(r.Succeeded, $"{why}: expected QSEM006 but the program compiled");
        Assert.Contains(r.Diagnostics.Select(diagnostic => diagnostic.Error).ToList(), e => e.Code == "QSEM006");
    }

    /// <summary>The built-in name is reserved: an expression-position call is spelled by BARE name, so no
    /// qualifier could ever disambiguate a user callable that reused it.</summary>
    [Fact]
    public void RejectsAUserCallableNamedAsInt() =>
        Compiler.Rejects("operation AsInt() { } operation Main(){ use q=Qubit[1]; X(q[0]); }", "QSEM013");

    /// <summary>A `repeat`'s `until` runs AFTER the body, so it reads the body's names — including a register
    /// the body declared. Resolving it against the ENCLOSING scope instead made a legal body-local argument
    /// look like something that is not a register at all.</summary>
    [Fact]
    public void AnUntilConditionSeesARegisterTheRepeatBodyDeclared()
    {
        var r = Compiler.Compile("operation Main(){ use q=Qubit[1]; repeat { var f: bit[] = new bit[2]; f[0] = M(q[0]); } until (AsInt(f) == 1); }");
        Assert.True(r.Succeeded, Explain(r));
        var body = r.Targets.OpenQasm!.Program.EntryBody;
        var loop = Assert.Single(
            TargetStatements(body).OfType<MirQasmWhileStatement>());
        Assert.Contains(
            TargetLoopDependencies(body, loop),
            expression =>
                expression is MirQasmUnsignedCastExpression { Width: 2 });
    }

    /// <summary>A whole register passed as an ARGUMENT is judged by the callee's signature, not by the rule —
    /// a `bit[]` parameter still accepts it.</summary>
    [Fact]
    public void AWholeRegisterIsStillALegalArgument() =>
        Compiler.Accepts("""
            operation Helper(f: bit[], q: Qubit) { if (f[0] == 1) { X(q); } }
            operation Main() { use q=Qubit[1]; var f: bit[] = new bit[2]; Helper(f, q[0]); }
            """);

    // --- bit[] PARAMETERS: length-specialized like Qubit[] (bit is not a valid array base type, so the
    //     only legal QASM parameter form is the sized register `bit[N]`) ---

    [Fact]
    public void BitArrayParameterSpecializesToASizedRegister()
    {
        var r = Compiler.Compile("""
            operation Read(values: bit[], q: Qubit) {
                if (values[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                var f: bit[] = new bit[2];
                Read(f, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        var definition = Assert.Single(
            r.Targets.OpenQasm!.Program.Definitions.Where(
                candidate =>
                    candidate.Parameters.Any(
                        parameter =>
                            parameter.Type is MirQasmBitType
                            {
                                Width: 2,
                                IsRegister: true,
                            })));
        Assert.Contains(
            definition.Parameters,
            parameter =>
                parameter.Type is MirQasmQubitType
                {
                    Count: 1,
                    IsRegister: false,
                });
        Assert.DoesNotContain(
            definition.Parameters,
            parameter => parameter.Type is MirQasmArrayType);
    }

    [Fact]
    public void BitArrayParameterCountFoldsToALiteralNotSizeof()
    {
        // sizeof is undefined on a bit register, so `f.Count` must fold to the specialized length.
        var r = Compiler.Compile("""
            operation Scan(f: bit[], q: Qubit) {
                for i in 0..f.Count-1 { if (f[i] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                var f: bit[] = new bit[3];
                Scan(f, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        var definition = Assert.Single(
            r.Targets.OpenQasm!.Program.Definitions.Where(
                candidate =>
                    candidate.Parameters.Any(
                        parameter =>
                            parameter.Type is MirQasmBitType
                            {
                                Width: 3,
                                IsRegister: true,
                            })));
        var loop = Assert.Single(
            TargetStatements(definition.Body)
                .OfType<MirQasmWhileStatement>());
        var dependencies = TargetLoopDependencies(
            definition.Body,
            loop).ToArray();
        Assert.Contains(
            dependencies,
            expression =>
                expression is MirQasmLiteralExpression { Text: "3" });
        Assert.DoesNotContain(
            dependencies,
            expression => expression is MirQasmSizeOfExpression);
    }

    [Fact]
    public void TwoBitArrayLengthsMakeTwoSpecializations()
    {
        var r = Compiler.Compile("""
            operation Read(values: bit[], q: Qubit) {
                if (values[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[2];
                var a: bit[] = new bit[2];
                var b: bit[] = new bit[3];
                Read(a, q[0]);
                Read(b, q[1]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
        var widths = r.Targets.OpenQasm!.Program.Definitions
            .SelectMany(definition => definition.Parameters)
            .Select(parameter => parameter.Type)
            .OfType<MirQasmBitType>()
            .Where(bit => bit.IsRegister)
            .Select(bit => bit.Width)
            .Order()
            .ToArray();
        Assert.Equal(new[] { 2, 3 }, widths);
    }

    /// <summary>Functions are called as <see cref="HirCallExpression"/> values inside expression trees rather than
    /// as <see cref="HirCallStatement"/> nodes. The monomorphizer must therefore find calls nested under both a
    /// declaration initializer and another function call. Equal widths reuse one specialization, while a
    /// distinct width creates a second one.</summary>
    [Fact]
    public void BitArrayFunctionCallsInNestedInitializersSpecializePerLengthAndReuse()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            function Increment(value: int): int { return value + 1; }
            operation Main() {
                use q = Qubit[1];
                var first: bit[] = new bit[2];
                var second: bit[] = new bit[3];
                var sameWidth: bit[] = new bit[2];
                var a: int = CountBits(first);
                var b: int = CountBits(second);
                var c: int = Increment(CountBits(sameWidth)) + CountBits(first);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var specs = r.Hir.EffectAnalysis!.Program!.Callables.Where(o => o.DisplayName == "CountBits").ToList();
        Assert.Equal(2, specs.Count);
        Assert.Equal(new[] { 2, 3 },
            specs.Select(o => o.Parameters.Single().RegisterSize!.Value).Order().ToArray());
        Assert.All(specs, specialization =>
        {
            Assert.True(specialization.IsFunction);
            Assert.Equal(QType.Int, specialization.ReturnType);
        });
        var program = r.Targets.OpenQasm!.Program;
        var countDefinitions = program.Definitions
            .Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.ReturnType is MirQasmScalarType
                    {
                        Kind: MirQasmScalarKind.Int,
                    }
                    && definition.Parameters.Length == 1
                    && definition.Parameters[0].Type is MirQasmBitType
                    {
                        IsRegister: true,
                    })
            .ToDictionary(
                definition => definition.Id,
                definition =>
                    ((MirQasmBitType)definition.Parameters[0].Type).Width);
        Assert.Equal(new[] { 2, 3 }, countDefinitions.Values.Order());
        var increment = Assert.Single(
            program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.Length == 1
                    && definition.Parameters[0].Type is MirQasmScalarType
                    {
                        Kind: MirQasmScalarKind.Int,
                    }));
        var entry = program.EntryBody;
        var calls = TargetStatements(entry)
            .SelectMany(MirQasmTestModel.Expressions)
            .OfType<MirQasmFunctionCallExpression>()
            .Where(
                call =>
                    call.Target is MirQasmUserFunctionTarget target
                    && countDefinitions.ContainsKey(target.Callable))
            .ToArray();
        Assert.Equal(
            new[] { 2, 2, 2, 3 },
            calls.Select(
                    call =>
                        countDefinitions[
                            ((MirQasmUserFunctionTarget)call.Target).Callable])
                .Order());
        Assert.All(
            calls,
            call =>
            {
                var width = countDefinitions[
                    ((MirQasmUserFunctionTarget)call.Target).Callable];
                var argument = Assert.Single(call.Arguments);
                var bits = Assert.IsType<MirQasmBitType>(
                    TargetPlaceType(entry, argument));
                Assert.Equal(width, bits.Width);
            });
        var incrementCall = Assert.Single(
            TargetStatements(entry)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>(),
            call =>
                call.Target is MirQasmUserFunctionTarget target
                && target.Callable == increment.Id);
        Assert.True(
            entry.DependsOn(
                Assert.Single(incrementCall.Arguments),
                expression =>
                    expression is MirQasmFunctionCallExpression
                    {
                        Target: MirQasmUserFunctionTarget target,
                    }
                    && countDefinitions.TryGetValue(target.Callable, out var width)
                    && width == 2));
    }

    [Fact]
    public void BitArrayFunctionCallOnAnAssignmentRhsIsSpecialized()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[2];
                var count: int = 0;
                count = CountBits(flags);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var program = r.Targets.OpenQasm!.Program;
        var count = Assert.Single(
            program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.SingleOrDefault()?.Type is
                    MirQasmBitType
                    {
                        Width: 2,
                        IsRegister: true,
                    }));
        var body = program.EntryBody;
        var call = Assert.Single(
            TargetStatements(body)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>(),
            expression =>
                expression.Target is MirQasmUserFunctionTarget target
                && target.Callable == count.Id);
        var argument = Assert.Single(call.Arguments);
        Assert.Equal(
            2,
            Assert.IsType<MirQasmBitType>(
                TargetPlaceType(body, argument)).Width);
        Assert.Contains(
            TargetStatements(body).OfType<MirQasmAssignmentStatement>(),
            assignment =>
                body.DependsOn(
                    assignment.Value,
                    expression => ReferenceEquals(expression, call)
                        || expression == call));
    }

    /// <summary>A function specialization can itself expose another expression-position call. Rewriting
    /// continues through the specialized body, so the inner function receives the outer parameter's now
    /// concrete width before the unsized originals are removed.</summary>
    [Fact]
    public void BitArrayFunctionCallInAReturnExpressionIsSpecializedTransitively()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            function ForwardCount(flags: bit[]): int { return CountBits(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[3];
                var count: int = ForwardCount(flags);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var program = r.Targets.OpenQasm!.Program;
        var functions = program.Definitions
            .Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.SingleOrDefault()?.Type is
                    MirQasmBitType
                    {
                        Width: 3,
                        IsRegister: true,
                    })
            .ToArray();
        Assert.Equal(2, functions.Length);
        var forwarding = Assert.Single(
            functions,
            definition =>
                TargetStatements(definition.Body)
                    .SelectMany(MirQasmTestModel.Expressions)
                    .OfType<MirQasmFunctionCallExpression>()
                    .Any(
                        call =>
                            call.Target is MirQasmUserFunctionTarget target
                            && functions.Any(
                                candidate =>
                                    candidate.Id == target.Callable)));
        var innerCall = Assert.Single(
            TargetStatements(forwarding.Body)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>(),
            call => call.Target is MirQasmUserFunctionTarget);
        var inner = Resolve(
            program,
            ((MirQasmUserFunctionTarget)innerCall.Target).Callable);
        Assert.Contains(inner, functions);
        var entryCall = Assert.Single(
            TargetStatements(program.EntryBody)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>(),
            call =>
                call.Target is MirQasmUserFunctionTarget target
                && target.Callable == forwarding.Id);
        Assert.Equal(
            3,
            Assert.IsType<MirQasmBitType>(
                TargetPlaceType(
                    program.EntryBody,
                    Assert.Single(entryCall.Arguments))).Width);
    }

    [Fact]
    public void BitArrayFunctionCallsInAConditionAndGateArgumentAreSpecialized()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[2];
                if (CountBits(flags) == 0) { Rx(CountBits(flags), q[0]); }
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var program = r.Targets.OpenQasm!.Program;
        var count = Assert.Single(
            program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.SingleOrDefault()?.Type is
                    MirQasmBitType
                    {
                        Width: 2,
                        IsRegister: true,
                    }));
        var body = program.EntryBody;
        var branch = Assert.Single(
            TargetStatements(body).OfType<MirQasmIfStatement>());
        Assert.True(
            body.DependsOn(
                branch.Condition,
                expression =>
                    expression is MirQasmFunctionCallExpression
                    {
                        Target: MirQasmUserFunctionTarget target,
                    }
                    && target.Callable == count.Id));
        var rotation = Assert.Single(
            TargetStatements(branch.Then)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply =>
                apply.Target is MirQasmBuiltinGateTarget
                && apply.GateParameters.Length == 1);
        Assert.True(
            body.DependsOn(
                Assert.Single(rotation.GateParameters),
                expression =>
                    expression is MirQasmFunctionCallExpression
                    {
                        Target: MirQasmUserFunctionTarget target,
                    }
                    && target.Callable == count.Id));
    }

    [Fact]
    public void RepeatUntilSpecializesWithABitArrayDeclaredInTheBodyScope()
    {
        var r = Compiler.Compile("""
            function CountBits(flags: bit[]): int { return AsInt(flags); }
            operation Main() {
                use q = Qubit[1];
                var flags: bit[] = new bit[3];
                repeat {
                    var flags: bit[] = new bit[2];
                    flags[0] = 1;
                } until (CountBits(flags) == 2);
                var outerCount: int = CountBits(flags);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var program = r.Targets.OpenQasm!.Program;
        var countByWidth = program.Definitions
            .Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.SingleOrDefault()?.Type is
                    MirQasmBitType { IsRegister: true })
            .ToDictionary(
                definition =>
                    ((MirQasmBitType)definition.Parameters[0].Type).Width);
        Assert.Equal(new[] { 2, 3 }, countByWidth.Keys.Order());
        var body = program.EntryBody;
        Assert.Equal(
            new[] { 2, 3 },
            TargetStatements(body)
                .OfType<MirQasmValueDeclarationStatement>()
                .Select(declaration => declaration.Type)
                .OfType<MirQasmBitType>()
                .Where(bit => bit.IsRegister)
                .Select(bit => bit.Width)
                .Order());
        var loop = Assert.Single(
            TargetStatements(body).OfType<MirQasmWhileStatement>());
        var loopDependencies = TargetLoopDependencies(body, loop).ToArray();
        Assert.Contains(
            loopDependencies,
            expression =>
                    expression is MirQasmFunctionCallExpression
                    {
                        Target: MirQasmUserFunctionTarget target,
                    }
                    && target.Callable == countByWidth[2].Id);
        Assert.DoesNotContain(
            loopDependencies,
            expression =>
                    expression is MirQasmFunctionCallExpression
                    {
                        Target: MirQasmUserFunctionTarget target,
                    }
                    && target.Callable == countByWidth[3].Id);
        Assert.Contains(
            TargetStatements(body)
                .SelectMany(MirQasmTestModel.Expressions)
                .OfType<MirQasmFunctionCallExpression>(),
            call =>
                call.Target is MirQasmUserFunctionTarget target
                && target.Callable == countByWidth[3].Id);
    }

    [Fact]
    public void RepeatUntilFunctionCallUsesTheBodyLocalAfterScalarShadowing()
    {
        var r = Compiler.Compile("""
            function Echo(value: int): int { return value; }
            operation Main() {
                use q = Qubit[1];
                var value: int = 0;
                repeat {
                    var value: int = 1;
                } until (Echo(value) == 1);
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var program = r.Targets.OpenQasm!.Program;
        var echo = Assert.Single(
            program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.SingleOrDefault()?.Type is
                    MirQasmScalarType { Kind: MirQasmScalarKind.Int }));
        var body = program.EntryBody;
        var loop = Assert.Single(
            TargetStatements(body).OfType<MirQasmWhileStatement>());
        var conditionCall = Assert.Single(
            TargetLoopDependencies(body, loop)
                .OfType<MirQasmFunctionCallExpression>()
                .Where(
                    call =>
                        call.Target is MirQasmUserFunctionTarget target
                        && target.Callable == echo.Id)
                .Distinct());
        var argument = Assert.Single(conditionCall.Arguments);
        Assert.True(
            body.DependsOn(
                argument,
                expression =>
                    expression is MirQasmLiteralExpression { Text: "1" }));
        Assert.False(
            body.DependsOn(
                argument,
                expression =>
                    expression is MirQasmLiteralExpression { Text: "0" }));
    }

    [Fact]
    public void HoistedArrayRenameDoesNotCaptureRepeatOrForScalarShadows()
    {
        var r = Compiler.Compile("""
            function Echo(value: int): int { return value; }
            operation Worker() {
                var value: int[] = [7];
                repeat {
                    var value: int = 1;
                } until (Echo(value) == 1);
                for value in 0..0 {
                    var seen: int = Echo(value);
                }
            }
            operation Main() {
                use q = Qubit[1];
                Worker();
            }
            """);

        Assert.True(r.Succeeded, Explain(r));
        var program = r.Targets.OpenQasm!.Program;
        var echo = Assert.Single(
            program.Definitions.Where(
                definition =>
                    definition.Kind == MirQasmCallableKind.Function
                    && definition.Parameters.SingleOrDefault()?.Type is
                    MirQasmScalarType { Kind: MirQasmScalarKind.Int }));
        var worker = Assert.Single(
            program.Definitions.Where(
                definition =>
                    definition.Parameters.Any(
                        parameter =>
                            parameter.Access == MirQasmParameterAccess.Mutable
                            && parameter.Type is MirQasmArrayType)));
        var echoCalls = TargetStatements(worker.Body)
            .SelectMany(MirQasmTestModel.Expressions)
            .OfType<MirQasmFunctionCallExpression>()
            .Where(
                call =>
                    call.Target is MirQasmUserFunctionTarget target
                    && target.Callable == echo.Id)
            .ToArray();
        Assert.Equal(2, echoCalls.Length);
        var arguments = echoCalls
            .Select(call => Assert.Single(call.Arguments))
            .ToArray();
        Assert.All(
            arguments,
            argument =>
                Assert.IsType<MirQasmDeclarationReferenceExpression>(argument));
        Assert.False(SameTargetPlace(arguments[0], arguments[1]));
        Assert.Contains(
            arguments,
            argument =>
                worker.Body.DependsOn(
                    argument,
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "1" }));
    }

    /// <summary>A bit[] parameter is READ-ONLY: its QASM form is a by-value register, so a write would
    /// silently never reach the caller — rejected loudly instead (int[] passes by mutable reference; the
    /// asymmetry is OpenQASM's, and the ban keeps it unobservable at the Qora surface).</summary>
    [Theory]
    [InlineData("operation Zero(f: bit[], q: Qubit){ f[0] = 0; H(q); }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[1]; Zero(f, q[0]); }")]
    [InlineData("operation Store(f: bit[], qs: Qubit[]){ f[0] = M(qs[0]); }\noperation Main(){ use q=Qubit[1]; var f: bit[] = new bit[1]; Store(f, q); }")]
    public void RejectsWritingToABitArrayParameter(string source) =>
        Compiler.Rejects(source, "QSEM032");

    /// <summary>bit[] parameters are read-only by-value registers (QSEM032), so the MUTABLE-array rules
    /// must not apply to them: the same register may feed two bit[] slots (reads cannot conflict), and a
    /// const array is a perfectly fine argument.</summary>
    [Fact]
    public void DuplicateBitArrayArgumentsAreAcceptedReadsCannotConflict()
    {
        var r = Compiler.Compile("""
            operation Both(a: bit[], b: bit[], q: Qubit) { if (a[0] == 1) { X(q); } if (b[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; var f: bit[] = new bit[2]; Both(f, f, q[0]); }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    [Fact]
    public void ConstBitArrayIsAValidArgumentToAReadOnlyBitParameter()
    {
        var r = Compiler.Compile("""
            operation Read(f: bit[], q: Qubit) { if (f[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; const f: bit[] = [0, 1]; Read(f, q[0]); }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    /// <summary>bit[] parameters specialize like Qubit[], so their bounds facts DEFER to the post-mono
    /// re-check the same way: a loop bounded by ANOTHER bit[] param's Count, and a constant guard
    /// `n &lt; K`, must both prove post-specialization instead of rejecting pre-mono (QSEM030).</summary>
    [Fact]
    public void BitArrayCrossCountLoopDefersAndProvesPostMono()
    {
        var r = Compiler.Compile("""
            operation Zip(a: bit[], b: bit[], q: Qubit) {
                for i in 0..a.Count-1 { if (b[i] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                var a: bit[] = new bit[2];
                var b: bit[] = new bit[2];
                Zip(a, b, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    [Fact]
    public void BitArrayConstGuardDefersAndProvesPostMono()
    {
        var r = Compiler.Compile("""
            operation Pick(f: bit[], n: int, q: Qubit) {
                if (0 <= n && n < 2) { if (f[n] == 1) { X(q); } }
            }
            operation Main() {
                use q = Qubit[1];
                var f: bit[] = new bit[2];
                var n: int = 1;
                Pick(f, n, q[0]);
            }
            """);
        Assert.True(r.Succeeded, Explain(r));
    }

    // --- MIR target array legalization: a classical-array LOCAL in a def-emitted op is inexpressible in OpenQASM
    //     (arrays are global-or-parameter only, and defs cannot see mutable globals — scope.rst), so the
    //     QASM backend threads it as a hidden array-reference parameter backed by a global, with the
    //     declaration site becoming element-wise re-initialization (fresh value on every entry). ---

    [Fact]
    public void ThreadsHelperArrayLocalAsHiddenParameter()
    {
        var result = CompileSuccessfully("""
            operation SetTable(q: Qubit) {
                var tbl: int[] = [1, 2, 3];
                if (tbl[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                SetTable(q[0]);
            }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var entry = program.EntryBody;
        var backing = Assert.Single(
            entry.OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(MirQasmScalarKind.Int, backing.Type.ElementType.Kind);
        Assert.Equal(3, backing.Type.Length);
        Assert.All(
            backing.Elements,
            element =>
                Assert.Equal(
                    "0",
                    Assert.IsType<MirQasmLiteralExpression>(element).Text));
        var call = Assert.Single(
            entry.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var callee = Resolve(
            program,
            ((MirQasmUserQuantumTarget)call.Target).Callable);
        var hidden = Assert.Single(
            callee.Parameters,
            parameter =>
                parameter.Access == MirQasmParameterAccess.Mutable
                && parameter.Type is MirQasmArrayType
                {
                    ElementType.Kind: MirQasmScalarKind.Int,
                });
        Assert.Contains(
            call.Operands,
            operand =>
                operand is MirQasmDeclarationReferenceExpression reference
                && reference.Declaration == backing.Declaration);
        Assert.Empty(
            TargetStatements(callee.Body)
                .OfType<MirQasmArrayDeclarationStatement>());
        var writes = TargetStatements(callee.Body)
            .OfType<MirQasmAssignmentStatement>()
            .Where(
                assignment =>
                    assignment.Target is MirQasmIndexExpression
                    {
                        Base: MirQasmParameterReferenceExpression reference,
                    }
                    && reference.Parameter == hidden.Id)
            .ToArray();
        Assert.Contains(
            writes,
            write => IsLiteralIndexWrite(callee.Body, write, "0", "1"));
        Assert.Contains(
            writes,
            write => IsLiteralIndexWrite(callee.Body, write, "2", "3"));
        Assert.True(
            entry.IndexOf(backing) < entry.IndexOf(call),
            "the entry-owned backing declaration must precede the typed call that borrows it");
    }

    [Fact]
    public void ThreadsHiddenParameterTransitivelyThroughIntermediateDefs()
    {
        var result = CompileSuccessfully("""
            operation Inner(q: Qubit) {
                var t: int[] = [4, 5];
                if (t[1] == 5) { X(q); }
            }
            operation Outer(q: Qubit) {
                Inner(q);
            }
            operation Main() {
                use q = Qubit[1];
                Outer(q[0]);
            }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var entry = program.EntryBody;
        var backing = Assert.Single(
            entry.OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(2, backing.Type.Length);
        var outerCall = Assert.Single(
            entry.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        Assert.Contains(
            outerCall.Operands,
            operand =>
                operand is MirQasmDeclarationReferenceExpression reference
                && reference.Declaration == backing.Declaration);
        var outer = Resolve(
            program,
            ((MirQasmUserQuantumTarget)outerCall.Target).Callable);
        var passThrough = Assert.Single(
            outer.Parameters,
            parameter =>
                parameter.Access == MirQasmParameterAccess.Mutable
                && parameter.Type is MirQasmArrayType);
        var innerCall = Assert.Single(
            TargetStatements(outer.Body)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        Assert.Contains(
            innerCall.Operands,
            operand =>
                operand is MirQasmParameterReferenceExpression reference
                && reference.Parameter == passThrough.Id);
        var inner = Resolve(
            program,
            ((MirQasmUserQuantumTarget)innerCall.Target).Callable);
        Assert.Contains(
            inner.Parameters,
            parameter =>
                parameter.Access == MirQasmParameterAccess.Mutable
                && parameter.Type is MirQasmArrayType
                {
                    ElementType.Kind: MirQasmScalarKind.Int,
                });
    }

    [Fact]
    public void HoistsEntryNestedArrayDeclarationToTheGlobalTop()
    {
        var result = CompileSuccessfully("""
            operation Main() {
                use q = Qubit[1];
                var b: bit = M(q[0]);
                var n: int = b;
                if (n == 1) {
                    var a: int[] = [7, 8];
                    var x: int = a[0];
                }
            }
            """);

        var body = result.Targets.OpenQasm!.Program.EntryBody;
        var backing = Assert.Single(
            body.OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(2, backing.Type.Length);
        var branch = Assert.Single(
            body.OfType<MirQasmIfStatement>());
        var writes = TargetStatements(branch.Then)
            .OfType<MirQasmAssignmentStatement>()
            .Where(
                assignment =>
                    assignment.Target is MirQasmIndexExpression
                    {
                        Base: MirQasmDeclarationReferenceExpression reference,
                    }
                    && reference.Declaration == backing.Declaration)
            .ToArray();
        Assert.Contains(
            writes,
            write => IsLiteralIndexWrite(body, write, "0", "7"));
        Assert.Contains(
            writes,
            write => IsLiteralIndexWrite(body, write, "1", "8"));
        Assert.True(body.IndexOf(backing) < body.IndexOf(branch));
    }

    /// <summary>bit[] locals are sized REGISTERS in OpenQASM — legal inside a def, not "arrays" — so the
    /// hoisting pass must leave them exactly where they are.</summary>
    [Fact]
    public void LeavesBitArrayLocalsInsideDefsUntouched()
    {
        var result = CompileSuccessfully("""
            operation Flag(q: Qubit) {
                var f: bit[] = new bit[2];
                f[0] = 1;
                if (f[0] == 1) { X(q); }
            }
            operation Main() {
                use q = Qubit[1];
                Flag(q[0]);
            }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var call = Assert.Single(
            program.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var definition = Resolve(
            program,
            ((MirQasmUserQuantumTarget)call.Target).Callable);
        var bits = Assert.Single(
            definition.Body.OfType<MirQasmValueDeclarationStatement>(),
            declaration =>
                declaration.Type is MirQasmBitType
                {
                    Width: 2,
                    IsRegister: true,
                });
        Assert.DoesNotContain(
            definition.Parameters,
            parameter => parameter.Type is MirQasmArrayType);
        Assert.Empty(
            program.EntryBody.OfType<MirQasmArrayDeclarationStatement>());
        Assert.Contains(
            TargetStatements(definition.Body)
                .OfType<MirQasmAssignmentStatement>(),
            write =>
                write.Target is MirQasmIndexExpression
                {
                    Base: MirQasmDeclarationReferenceExpression reference,
                }
                && reference.Declaration == bits.Declaration
                && IsLiteralIndexWrite(definition.Body, write, "0", "1"));
    }

    /// <summary>A bit[] NESTED in a control-flow block hoists only to the top of its own op (importers
    /// reject classical declarations inside blocks) — a register declaration is legal at def scope, so
    /// no threading and no backing global.</summary>
    [Fact]
    public void HoistsNestedBitArrayToItsOwnOpTop()
    {
        var result = CompileSuccessfully("""
            operation Tally(q: Qubit, n: int) {
                if (n == 1) {
                    var f: bit[] = new bit[2];
                    f[0] = 1;
                    if (f[0] == 1) { X(q); }
                }
            }
            operation Main() {
                use q = Qubit[1];
                var b: bit = M(q[0]);
                var n: int = b;
                Tally(q[0], n);
            }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var call = Assert.Single(
            program.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var definition = Resolve(
            program,
            ((MirQasmUserQuantumTarget)call.Target).Callable);
        Assert.Equal(2, definition.Parameters.Length);
        Assert.DoesNotContain(
            definition.Parameters,
            parameter => parameter.Type is MirQasmArrayType);
        var bits = Assert.Single(
            definition.Body.OfType<MirQasmValueDeclarationStatement>(),
            declaration =>
                declaration.Type is MirQasmBitType
                {
                    Width: 2,
                    IsRegister: true,
                });
        var branch = Assert.Single(
            definition.Body.OfType<MirQasmIfStatement>());
        Assert.True(definition.Body.IndexOf(bits) < definition.Body.IndexOf(branch));
        Assert.Contains(
            TargetStatements(branch.Then)
                .OfType<MirQasmAssignmentStatement>(),
            write =>
                write.Target is MirQasmIndexExpression
                {
                    Base: MirQasmDeclarationReferenceExpression reference,
                }
                && reference.Declaration == bits.Declaration
                && IsLiteralIndexWrite(definition.Body, write, "0", "0"));
    }

    // --- MIR target name uniqueness (R13/R14): lowering mints every global / parameter / storage
    //     as a UNIQUE placeholder (#hoist#base#uid), so two distinct entities can never share a spelling
    //     and a placeholder can never equal a user name — without enumerating the scope. Target lowering then
    //     turns each placeholder into a pretty, collision-free name (distinct placeholders never trigger
    //     its same-name MERGE; its per-key freshening splits a shared base into `x`/`x_`). These pin the
    //     collision vectors that once emitted invalid QASM with success=true; semantics are Braket-verified
    //     separately. When a base clashes, whichever name the mangler reaches SECOND takes the `_` — which
    //     for a hoisted-vs-user clash is often the USER's name (the mangler notes the rename), and that is
    //     fine: the two just need to differ and bind correctly. ---

    /// <summary>Vector 2 — the `{op}_{var}` base is ambiguous (`A`+`b_c` and `A_b`+`c` both yield base
    /// `A_b_c`); distinct placeholders make the mangler split them into two globals rather than merge them
    /// into one storage.</summary>
    [Fact]
    public void MintedGlobalsThatWouldConcatenateAlikeAreDisambiguated()
    {
        var result = CompileSuccessfully("""
            operation A(q: Qubit) { var b_c: int[] = [1, 1]; if (b_c[1] == 1) { X(q); } }
            operation A_b(q: Qubit) { var c: int[] = [9]; if (c[0] == 9) { X(q); } }
            operation Main() { use q = Qubit[2]; A(q[0]); A_b(q[1]); }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var arrays = program.EntryBody
            .OfType<MirQasmArrayDeclarationStatement>()
            .OrderBy(array => array.Type.Length)
            .ToArray();
        Assert.Equal(new int?[] { 1, 2 }, arrays.Select(array => array.Type.Length));
        Assert.NotEqual(arrays[0].Declaration, arrays[1].Declaration);
        var calls = program.EntryBody
            .OfType<MirQasmQuantumApplyStatement>()
            .Where(apply => apply.Target is MirQasmUserQuantumTarget)
            .ToArray();
        Assert.Equal(2, calls.Length);
        var suppliedArrays = calls
            .SelectMany(call => call.Operands)
            .OfType<MirQasmDeclarationReferenceExpression>()
            .Where(
                reference =>
                    arrays.Any(array => array.Declaration == reference.Declaration))
            .Select(reference => reference.Declaration)
            .ToHashSet();
        Assert.Equal(
            arrays.Select(array => array.Declaration).ToHashSet(),
            suppliedArrays);
        Assert.All(
            calls,
            call =>
                Assert.Contains(
                    Resolve(
                            program,
                            ((MirQasmUserQuantumTarget)call.Target).Callable)
                        .Parameters,
                    parameter =>
                        parameter.Access == MirQasmParameterAccess.Mutable
                        && parameter.Type is MirQasmArrayType));
    }

    /// <summary>Vector 1 — a minted backing global and a user top-level variable of the same spelling get
    /// DISTINCT emitted names (here the user scalar takes the `_`), never one merged declaration.</summary>
    [Fact]
    public void MintedGlobalAndUserTopLevelNameGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation Foo(q: Qubit) { var bar: int[] = [1]; if (bar[0] == 1) { X(q); } }
            operation Main() { use q = Qubit[1]; var Foo_bar: int = 5; Foo(q[0]); if (Foo_bar == 5) { X(q[0]); } }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var body = program.EntryBody;
        var backing = Assert.Single(
            body.OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(1, backing.Type.Length);
        var call = Assert.Single(
            body.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        Assert.Contains(
            call.Operands,
            operand =>
                operand is MirQasmDeclarationReferenceExpression reference
                && reference.Declaration == backing.Declaration);
        var branch = Assert.Single(body.OfType<MirQasmIfStatement>());
        Assert.True(
            body.DependsOn(
                branch.Condition,
                expression =>
                    expression is MirQasmLiteralExpression { Text: "5" }));
        Assert.False(
            body.DependsOn(
                branch.Condition,
                expression =>
                    expression is MirQasmDeclarationReferenceExpression reference
                    && reference.Declaration == backing.Declaration));
    }

    /// <summary>Vector 3 — a pass-through parameter (named after the global it forwards) must be freshened
    /// away from an owned parameter of the same spelling, so the def has no duplicate parameter.</summary>
    [Fact]
    public void PassThroughParameterIsFreshenedAwayFromAnOwnedParameter()
    {
        var result = CompileSuccessfully("""
            operation D(q: Qubit) { var g: int[] = [1]; if (g[0] == 1) { X(q); } }
            operation Mid(q: Qubit) { var D_g: int[] = [7]; if (D_g[0] == 7) { X(q); } D(q); }
            operation Main() { use q = Qubit[1]; Mid(q[0]); }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var entryCall = Assert.Single(
            program.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var mid = Resolve(
            program,
            ((MirQasmUserQuantumTarget)entryCall.Target).Callable);
        var arrays = mid.Parameters
            .Where(
                parameter =>
                    parameter.Access == MirQasmParameterAccess.Mutable
                    && parameter.Type is MirQasmArrayType)
            .ToArray();
        Assert.Equal(2, arrays.Length);
        Assert.NotEqual(arrays[0].Id, arrays[1].Id);
        var entryArrays = entryCall.Operands
            .OfType<MirQasmDeclarationReferenceExpression>()
            .Where(
                reference =>
                    TargetDeclaration(
                        program.EntryBody,
                        reference.Declaration) is MirQasmArrayDeclarationStatement)
            .Select(reference => reference.Declaration)
            .ToArray();
        Assert.Equal(2, entryArrays.Distinct().Count());
        var forwardedCall = Assert.Single(
            TargetStatements(mid.Body)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var forwarded = Assert.Single(
            forwardedCall.Operands
                .OfType<MirQasmParameterReferenceExpression>(),
            reference =>
                arrays.Any(array => array.Id == reference.Parameter));
        Assert.Contains(arrays, array => array.Id == forwarded.Parameter);
        Assert.Contains(
            arrays,
            array =>
                array.Id != forwarded.Parameter
                && TargetStatements(mid.Body)
                    .OfType<MirQasmAssignmentStatement>()
                    .Any(
                        assignment =>
                            assignment.Target is MirQasmIndexExpression
                            {
                                Base: MirQasmParameterReferenceExpression reference,
                            }
                            && reference.Parameter == array.Id));
    }

    /// <summary>Vector 4 — an array local that shadows a same-named parameter gets a freshened parameter,
    /// and ONLY the array's in-scope references are rewritten to it; the shadowed parameter's own
    /// references (here the enclosing `if (a == 0)`) are left intact.</summary>
    [Fact]
    public void ArrayLocalShadowingAParameterRewritesOnlyItsOwnReferences()
    {
        var result = CompileSuccessfully("""
            operation Helper(q: Qubit[], a: int) {
                if (a == 0) {
                    var a: int[] = [1, 2];
                    if (a[0] == 1) { X(q[0]); }
                }
            }
            operation Main() { use q = Qubit[1]; Helper(q, 0); }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var call = Assert.Single(
            program.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var helper = Resolve(
            program,
            ((MirQasmUserQuantumTarget)call.Target).Callable);
        var scalar = Assert.Single(
            helper.Parameters,
            parameter =>
                parameter.Type is MirQasmScalarType
                {
                    Kind: MirQasmScalarKind.Int,
                });
        var array = Assert.Single(
            helper.Parameters,
            parameter =>
                parameter.Access == MirQasmParameterAccess.Mutable
                && parameter.Type is MirQasmArrayType);
        var branches = TargetStatements(helper.Body)
            .OfType<MirQasmIfStatement>()
            .ToArray();
        Assert.Contains(
            branches,
            branch =>
                helper.Body.DependsOn(
                    branch.Condition,
                    expression =>
                        expression is MirQasmParameterReferenceExpression reference
                        && reference.Parameter == scalar.Id));
        Assert.Contains(
            branches,
            branch =>
                helper.Body.DependsOn(
                    branch.Condition,
                    expression =>
                        expression is MirQasmIndexExpression
                        {
                            Base: MirQasmParameterReferenceExpression reference,
                        }
                        && reference.Parameter == array.Id));
        Assert.DoesNotContain(
            branches,
            branch =>
                helper.Body.DependsOn(
                    branch.Condition,
                    expression =>
                        expression is MirQasmIndexExpression
                        {
                            Base: MirQasmParameterReferenceExpression reference,
                        }
                        && reference.Parameter == scalar.Id));
    }

    // --- MIR target name-allocation completeness (R14): the allocator must account for
    //     with EVERY inhabitant of the emission scope — the full target binding set — not just op
    //     names and parameters. A body-declared local (loop variable, scalar, measure bit) or a NESTED
    //     entry declaration is in that scope too; omitting it let a minted name collide with it and the
    //     mangler then merged the two. These pin the collisions against body-declared names. ---

    /// <summary>A hidden parameter and a LOOP VARIABLE the body declares get DISTINCT names — the mangler
    /// splits the placeholder-derived base from the loop variable (here the parameter base `g` avoids the
    /// operation name `g` → `g_`, and the loop variable `g_` then avoids that → `g__`).</summary>
    [Fact]
    public void HiddenParameterAndABodyLoopVariableGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation g(q: Qubit) { X(q); }
            operation Helper(q: Qubit) {
                var g: int[] = [1];
                for g_ in 0..0 { if (g[0] == 1) { X(q); } }
            }
            operation Main() { use q = Qubit[1]; Helper(q[0]); }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var entryCall = Assert.Single(
            program.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var helper = Resolve(
            program,
            ((MirQasmUserQuantumTarget)entryCall.Target).Callable);
        var array = Assert.Single(
            helper.Parameters,
            parameter =>
                parameter.Access == MirQasmParameterAccess.Mutable
                && parameter.Type is MirQasmArrayType);
        var backing = Assert.Single(
            entryCall.Operands.OfType<MirQasmDeclarationReferenceExpression>(),
            reference =>
                TargetDeclaration(
                    program.EntryBody,
                    reference.Declaration) is MirQasmArrayDeclarationStatement);
        Assert.NotNull(TargetDeclaration(
            program.EntryBody,
            backing.Declaration));
        var loop = Assert.Single(
            TargetStatements(helper.Body)
                .OfType<MirQasmWhileStatement>());
        Assert.Contains(
            TargetLoopDependencies(helper.Body, loop),
            expression =>
                expression is MirQasmIndexExpression
                {
                    Base: MirQasmParameterReferenceExpression reference,
                }
                && reference.Parameter == array.Id);
        Assert.Contains(
            TargetLoopDependencies(helper.Body, loop),
            expression =>
                expression is MirQasmDeclarationReferenceExpression);
    }

    /// <summary>A minted backing global and a user variable declared inside a NESTED block of the entry op
    /// get DISTINCT names — the mangler flattens the whole entry body into one global scope, so the two
    /// same-spelled entities are split (here the nested user scalar takes the `_`).</summary>
    [Fact]
    public void MintedGlobalAndNestedEntryDeclarationGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation SetTable(q: Qubit) { var tbl: int[] = [1, 2, 3]; if (tbl[0] == 1) { X(q); } }
            operation Main() {
                use q = Qubit[2];
                var flag: int = 1;
                if (flag == 1) { var SetTable_tbl: int = 1; if (SetTable_tbl == 1) { X(q[1]); } }
                SetTable(q[0]);
            }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var body = program.EntryBody;
        var backing = Assert.Single(
            body.OfType<MirQasmArrayDeclarationStatement>());
        Assert.Equal(3, backing.Type.Length);
        var call = Assert.Single(
            body.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        Assert.Contains(
            call.Operands,
            operand =>
                operand is MirQasmDeclarationReferenceExpression reference
                && reference.Declaration == backing.Declaration);
        var branches = TargetStatements(body)
            .OfType<MirQasmIfStatement>()
            .ToArray();
        Assert.Contains(
            branches,
            branch =>
                body.DependsOn(
                    branch.Condition,
                    expression =>
                        expression is MirQasmLiteralExpression { Text: "1" }));
        Assert.DoesNotContain(
            branches,
            branch =>
                body.DependsOn(
                    branch.Condition,
                    expression =>
                        expression is MirQasmDeclarationReferenceExpression reference
                        && reference.Declaration == backing.Declaration));
    }

    /// <summary>A pass-through parameter and a caller body-local of the same spelling get DISTINCT names
    /// (here the body-local takes the `_`), so the def has no duplicate name.</summary>
    [Fact]
    public void PassThroughParameterAndCallerBodyLocalGetDistinctNames()
    {
        var result = CompileSuccessfully("""
            operation Inner(q: Qubit) { var t: int[] = [1, 2]; if (t[0] == 1) { X(q); } }
            operation Outer(q: Qubit) { var Inner_t: int = 0; Inner(q); if (Inner_t == 0) { X(q); } }
            operation Main() { use q = Qubit[1]; Outer(q[0]); }
            """);

        var program = result.Targets.OpenQasm!.Program;
        var entryCall = Assert.Single(
            program.EntryBody.OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        var outer = Resolve(
            program,
            ((MirQasmUserQuantumTarget)entryCall.Target).Callable);
        var passThrough = Assert.Single(
            outer.Parameters,
            parameter =>
                parameter.Access == MirQasmParameterAccess.Mutable
                && parameter.Type is MirQasmArrayType);
        var innerCall = Assert.Single(
            TargetStatements(outer.Body)
                .OfType<MirQasmQuantumApplyStatement>(),
            apply => apply.Target is MirQasmUserQuantumTarget);
        Assert.Contains(
            innerCall.Operands,
            operand =>
                operand is MirQasmParameterReferenceExpression reference
                && reference.Parameter == passThrough.Id);
        var branch = Assert.Single(
            TargetStatements(outer.Body).OfType<MirQasmIfStatement>());
        Assert.True(
            outer.Body.DependsOn(
                branch.Condition,
                expression =>
                    expression is MirQasmLiteralExpression { Text: "0" }));
        Assert.False(
            outer.Body.DependsOn(
                branch.Condition,
                expression =>
                    expression is MirQasmParameterReferenceExpression reference
                    && reference.Parameter == passThrough.Id));
    }

    private static IReadOnlyList<MirQasmStatement> TargetStatements(
        IEnumerable<MirQasmStatement> body) =>
        MirQasmTestModel.Statements(body).ToArray();

    private static bool IsLiteralIndexWrite(
        IEnumerable<MirQasmStatement> ownerBody,
        MirQasmAssignmentStatement assignment,
        string index,
        string value) =>
        assignment.Target is MirQasmIndexExpression
        {
            Index: var targetIndex,
        }
        && ownerBody.DependsOn(
            targetIndex,
            expression =>
                expression is MirQasmLiteralExpression { Text: var actualIndex }
                && actualIndex == index)
        && ownerBody.DependsOn(
            assignment.Value,
            expression =>
                expression is MirQasmLiteralExpression { Text: var actualValue }
                && actualValue == value);

    private static IEnumerable<MirQasmExpression> TargetLoopDependencies(
        IEnumerable<MirQasmStatement> ownerBody,
        MirQasmWhileStatement loop)
    {
        foreach (var expression in MirQasmTestModel.Expressions(loop.Condition))
            foreach (var dependency in ownerBody.DependencyClosure(expression))
                yield return dependency;
        foreach (var statement in TargetStatements(loop.Body))
            foreach (var expression in MirQasmTestModel.Expressions(statement))
                foreach (var dependency in ownerBody.DependencyClosure(expression))
                    yield return dependency;
    }

    private static MirQasmStatement TargetDeclaration(
        IEnumerable<MirQasmStatement> ownerBody,
        MirQasmDeclarationId id) =>
        Assert.Single(
            TargetStatements(ownerBody),
            statement => statement switch
                {
                    MirQasmValueDeclarationStatement declaration =>
                        declaration.Declaration == id,
                    MirQasmArrayDeclarationStatement declaration =>
                        declaration.Declaration == id,
                    MirQasmQubitDeclarationStatement declaration =>
                        declaration.Declaration == id,
                    _ => false,
                });

    private static MirQasmType TargetPlaceType(
        IEnumerable<MirQasmStatement> ownerBody,
        MirQasmExpression expression) =>
        expression switch
        {
            MirQasmDeclarationReferenceExpression reference =>
                TargetDeclaration(ownerBody, reference.Declaration) switch
                {
                    MirQasmValueDeclarationStatement value => value.Type,
                    MirQasmArrayDeclarationStatement array => array.Type,
                    MirQasmQubitDeclarationStatement qubit => qubit.Type,
                    _ => throw new InvalidOperationException(),
                },
            MirQasmIndexExpression index =>
                TargetPlaceType(ownerBody, index.Base) switch
                {
                    MirQasmArrayType array => array.ElementType,
                    MirQasmBitType => new MirQasmBitType(),
                    MirQasmQubitType => new MirQasmQubitType(),
                    var type => throw new Xunit.Sdk.XunitException(
                        $"cannot index target type {type.GetType().Name}"),
                },
            _ => throw new Xunit.Sdk.XunitException(
                $"expected a declaration-backed target place, got {expression.GetType().Name}"),
        };

    private static bool SameTargetPlace(
        MirQasmExpression left,
        MirQasmExpression right) =>
        (left, right) switch
        {
            (MirQasmDeclarationReferenceExpression a, MirQasmDeclarationReferenceExpression b) =>
                a.Declaration == b.Declaration,
            (MirQasmParameterReferenceExpression a, MirQasmParameterReferenceExpression b) =>
                a.Parameter == b.Parameter,
            _ => false,
        };

    private static MirQasmCallableDefinition Resolve(
        MirOpenQasmTargetProgram program,
        MirQasmCallableId id) =>
        Assert.Single(
            program.Definitions.Where(definition => definition.Id == id));

    private static string Explain(Compilation result) =>
        string.Join(
            " | ",
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Error.Code}: {diagnostic.Error.Message}"));
}
