using Qora.Compiler;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// Queries the typed OpenQASM target model without depending on source spellings or on how SSA values are
/// serialized into temporary declarations.
/// </summary>
internal static class MirQasmTestModel
{
    public static OpenQasmArtifact Compile(string source)
    {
        var compilation = Compiler.Compile(source);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Error.Code}: {diagnostic.Error.Message}")));
        Assert.NotNull(compilation.Mir);
        return Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
    }

    public static IEnumerable<MirQasmStatement> Statements(
        this MirOpenQasmTargetProgram program) =>
        Statements(program.EntryBody)
            .Concat(
                program.Definitions.SelectMany(
                    definition => Statements(definition.Body)));

    public static IEnumerable<MirQasmStatement> Statements(
        IEnumerable<MirQasmStatement> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            switch (statement)
            {
                case MirQasmIfStatement branch:
                    foreach (var nested in Statements(branch.Then))
                        yield return nested;
                    foreach (var nested in Statements(branch.Else))
                        yield return nested;
                    break;
                case MirQasmWhileStatement loop:
                    foreach (var nested in Statements(loop.Body))
                        yield return nested;
                    break;
            }
        }
    }

    public static IEnumerable<MirQasmExpression> Expressions(
        this MirOpenQasmTargetProgram program) =>
        program.Statements().SelectMany(Expressions);

    public static IEnumerable<MirQasmExpression> Expressions(
        MirQasmStatement statement)
    {
        switch (statement)
        {
            case MirQasmValueDeclarationStatement { Initializer: { } initializer }:
                return Expressions(initializer);
            case MirQasmArrayDeclarationStatement declaration:
                return declaration.Elements.SelectMany(Expressions);
            case MirQasmAssignmentStatement assignment:
                return Expressions(assignment.Target)
                    .Concat(Expressions(assignment.Value));
            case MirQasmMeasurementAssignmentStatement measurement:
                return Expressions(measurement.Target)
                    .Concat(Expressions(measurement.Qubit));
            case MirQasmQuantumApplyStatement apply:
                return apply.GateParameters
                    .Concat(apply.Operands)
                    .SelectMany(Expressions);
            case MirQasmIfStatement branch:
                return Expressions(branch.Condition);
            case MirQasmWhileStatement loop:
                return Expressions(loop.Condition);
            case MirQasmReturnStatement { Value: { } value }:
                return Expressions(value);
            default:
                return Array.Empty<MirQasmExpression>();
        }
    }

    public static IEnumerable<MirQasmExpression> Expressions(
        MirQasmExpression expression)
    {
        yield return expression;
        switch (expression)
        {
            case MirQasmUnaryExpression unary:
                foreach (var nested in Expressions(unary.Operand))
                    yield return nested;
                break;
            case MirQasmBinaryExpression binary:
                foreach (var nested in Expressions(binary.Left))
                    yield return nested;
                foreach (var nested in Expressions(binary.Right))
                    yield return nested;
                break;
            case MirQasmIndexExpression index:
                foreach (var nested in Expressions(index.Base))
                    yield return nested;
                foreach (var nested in Expressions(index.Index))
                    yield return nested;
                break;
            case MirQasmSizeOfExpression size:
                foreach (var nested in Expressions(size.Operand))
                    yield return nested;
                break;
            case MirQasmUnsignedCastExpression cast:
                foreach (var nested in Expressions(cast.Operand))
                    yield return nested;
                break;
            case MirQasmFunctionCallExpression call:
                foreach (var nested in call.Arguments.SelectMany(Expressions))
                    yield return nested;
                break;
        }
    }

    public static MirQasmCallableDefinition RequireDefinition(
        this MirOpenQasmTargetProgram program,
        Func<MirQasmCallableDefinition, bool> predicate) =>
        Assert.Single(program.Definitions.Where(predicate));

    public static MirQasmCallableDefinition Resolve(
        this MirOpenQasmTargetProgram program,
        MirQasmUserFunctionTarget target) =>
        Assert.Single(
            program.Definitions.Where(
                definition => definition.Id == target.Callable));

    public static MirQasmCallableDefinition Resolve(
        this MirOpenQasmTargetProgram program,
        MirQasmUserQuantumTarget target) =>
        Assert.Single(
            program.Definitions.Where(
                definition => definition.Id == target.Callable));

    public static bool DependsOn(
        this IEnumerable<MirQasmStatement> ownerBody,
        MirQasmExpression expression,
        Func<MirQasmExpression, bool> predicate) =>
        DependencyClosure(ownerBody, expression).Any(predicate);

    public static IEnumerable<MirQasmExpression> DependencyClosure(
        this IEnumerable<MirQasmStatement> ownerBody,
        MirQasmExpression expression)
    {
        var definitions =
            new Dictionary<MirQasmDeclarationId, List<MirQasmExpression>>();
        foreach (var statement in Statements(ownerBody))
        {
            switch (statement)
            {
                case MirQasmValueDeclarationStatement
                {
                    Initializer: { } initializer,
                } declaration:
                    Add(declaration.Declaration, initializer);
                    break;
                case MirQasmAssignmentStatement
                {
                    Target: MirQasmDeclarationReferenceExpression target,
                } assignment:
                    Add(target.Declaration, assignment.Value);
                    break;
            }
        }
        return Visit(expression, new HashSet<MirQasmDeclarationId>());

        void Add(
            MirQasmDeclarationId declaration,
            MirQasmExpression value)
        {
            if (!definitions.TryGetValue(declaration, out var values))
            {
                values = new List<MirQasmExpression>();
                definitions.Add(declaration, values);
            }
            values.Add(value);
        }

        IEnumerable<MirQasmExpression> Visit(
            MirQasmExpression current,
            HashSet<MirQasmDeclarationId> path)
        {
            yield return current;

            if (current is MirQasmDeclarationReferenceExpression reference
                && definitions.TryGetValue(reference.Declaration, out var values)
                && path.Add(reference.Declaration))
            {
                try
                {
                    foreach (var value in values)
                        foreach (var nested in Visit(value, path))
                            yield return nested;
                }
                finally
                {
                    path.Remove(reference.Declaration);
                }
            }

            foreach (var child in DirectChildren(current))
                foreach (var nested in Visit(child, path))
                    yield return nested;
        }
    }

    public static IEnumerable<MirQasmExpression> DirectChildren(
        MirQasmExpression expression) =>
        expression switch
        {
            MirQasmUnaryExpression unary => new[] { unary.Operand },
            MirQasmBinaryExpression binary => new[] { binary.Left, binary.Right },
            MirQasmIndexExpression index => new[] { index.Base, index.Index },
            MirQasmSizeOfExpression size => new[] { size.Operand },
            MirQasmUnsignedCastExpression cast => new[] { cast.Operand },
            MirQasmFunctionCallExpression call => call.Arguments,
            _ => Array.Empty<MirQasmExpression>(),
        };
}
