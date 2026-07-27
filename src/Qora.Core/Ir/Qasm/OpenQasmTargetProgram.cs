using System.Collections.Frozen;
using System.Collections.Immutable;
using Qora.Ir.Passes;

namespace Qora.Ir;

/// <summary>
/// The OpenQASM spelling assigned to every declaration in one target program. The map belongs to the
/// target artifact, not to the HIR semantic model: changing target naming policy must never mutate facts
/// proved for the source program.
/// </summary>
public sealed class OpenQasmSymbolMap
{
    private readonly FrozenDictionary<int, string> _emittedNameByDeclaration;

    internal OpenQasmSymbolMap(IEnumerable<KeyValuePair<int, string>> emittedNames)
    {
        ArgumentNullException.ThrowIfNull(emittedNames);
        _emittedNameByDeclaration = emittedNames.ToFrozenDictionary();
    }

    /// <summary>Every declaration-node Id and its final OpenQASM spelling.</summary>
    public IReadOnlyDictionary<int, string> Declarations => _emittedNameByDeclaration;

    public bool TryGetEmittedName(int declarationNodeId, out string emittedName) =>
        _emittedNameByDeclaration.TryGetValue(declarationNodeId, out emittedName!);

    public string GetEmittedName(int declarationNodeId) =>
        _emittedNameByDeclaration.TryGetValue(declarationNodeId, out var emittedName)
            ? emittedName
            : throw new KeyNotFoundException(
                $"declaration node {declarationNodeId} has no OpenQASM symbol");
}

/// <summary>
/// One classical declaration's target type. Array shape is kept separate from its element type because
/// OpenQASM renders scalar <c>bit</c>, bit register <c>bit[N]</c>, and general
/// <c>array[T, N]</c> declarations differently.
/// </summary>
public readonly record struct OpenQasmClassicalType
{
    public OpenQasmClassicalType(QType elementType, bool isArray)
    {
        if (elementType == QType.Qubit)
            throw new ArgumentOutOfRangeException(
                nameof(elementType),
                elementType,
                "qubits are not classical OpenQASM values");

        ElementType = elementType;
        IsArray = isArray;
    }

    public QType ElementType { get; }

    public bool IsArray { get; }
}

/// <summary>
/// Immutable target-side type facts used by the text emitter. This is built once while the backend still
/// has both authorities which meet at the target boundary: original declarations read only the exact
/// validated HIR semantic model, while target-synthesized declarations read only
/// <see cref="OpenQasmTargetFacts"/>. The emitter consequently never rebuilds a symbol table and never
/// guesses an untyped declaration's type from its initializer.
/// </summary>
public sealed class OpenQasmTypeEnvironment
{
    private readonly FrozenDictionary<int, OpenQasmClassicalType> _typesByDeclaration;

    private OpenQasmTypeEnvironment(
        IEnumerable<KeyValuePair<int, OpenQasmClassicalType>> types)
    {
        _typesByDeclaration = types.ToFrozenDictionary();
    }

    /// <summary>Every classical declaration-node Id and its final target type.</summary>
    public IReadOnlyDictionary<int, OpenQasmClassicalType> Declarations =>
        _typesByDeclaration;

    public bool TryGetType(
        int declarationNodeId,
        out OpenQasmClassicalType type) =>
        _typesByDeclaration.TryGetValue(declarationNodeId, out type);

    public OpenQasmClassicalType GetType(int declarationNodeId) =>
        _typesByDeclaration.TryGetValue(declarationNodeId, out var type)
            ? type
            : throw new KeyNotFoundException(
                $"declaration node {declarationNodeId} has no OpenQASM classical type");

    internal static BuildResult Build(
        QProgram targetProgram,
        IHirSemanticContext semantics,
        OpenQasmTargetFacts targetFacts)
    {
        ArgumentNullException.ThrowIfNull(targetProgram);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(targetFacts);

        var types = new Dictionary<int, OpenQasmClassicalType>();
        var errors = new List<QoraError>();

        void Add(
            int nodeId,
            string name,
            SourceSpan? span)
        {
            OpenQasmClassicalType targetType;
            if (targetFacts.TryGetSynthesizedNode(nodeId, out var synthesized))
            {
                if (synthesized.DeclaredType is not { } declaredType)
                {
                    errors.Add(Internal(
                        $"OpenQASM target declaration `{name}` is synthesized as " +
                        $"{synthesized.Kind}, but its lowering fact has no declared type",
                        span));
                    return;
                }

                targetType = declaredType;
            }
            else
            {
                // This is deliberately not a fallback chain. Absence from target facts classifies the
                // node as original HIR, so its authoritative type must exist in the exact semantic
                // artifact. A target pass which forgot to record a synthesized declaration therefore
                // fails at this boundary instead of borrowing copied syntax fields as an accidental type.
                var sourceSymbol = semantics.FindSymbol(nodeId);
                if (sourceSymbol?.Type is not { } sourceType)
                {
                    errors.Add(Internal(
                        $"original HIR declaration `{name}` has no validated semantic type",
                        span));
                    return;
                }

                if (sourceType == QType.Qubit)
                    return;

                targetType = new OpenQasmClassicalType(
                    sourceType,
                    sourceSymbol.IsArray);
            }

            if (!types.TryAdd(nodeId, targetType) && types[nodeId] != targetType)
                errors.Add(Internal(
                    $"declaration node {nodeId} has conflicting OpenQASM target types",
                    span));
        }

        void Visit(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case QDecl declaration:
                        Add(
                            declaration.Id,
                            declaration.Name,
                            declaration.Span);
                        break;
                    case QFor loop:
                        Add(
                            loop.Id,
                            loop.Var,
                            loop.Span);
                        Visit(loop.Body);
                        break;
                    case QIf branch:
                        Visit(branch.Then);
                        Visit(branch.Else);
                        break;
                    case QWhile loop:
                        Visit(loop.Body);
                        break;
                    case QRepeat loop:
                        Visit(loop.Body);
                        break;
                    case QConjugate conjugation:
                        Visit(conjugation.Within);
                        Visit(conjugation.Apply);
                        break;
                }
            }
        }

        foreach (var operation in targetProgram.Operations)
        {
            foreach (var parameter in operation.Params)
                Add(
                    parameter.Id,
                    parameter.Name,
                    parameter.Span);
            Visit(operation.Body);
        }

        return errors.Count == 0
            ? new BuildResult(
                new OpenQasmTypeEnvironment(types),
                ImmutableArray<QoraError>.Empty)
            : new BuildResult(
                null,
                errors.ToImmutableArray());
    }

    /// <summary>
    /// Build target facts for a hand-authored, already-legalized test tree. Production compilation must
    /// use <see cref="Build"/> so inferred source types come only from semantic validation.
    /// </summary>
    internal static OpenQasmTypeEnvironment BuildExplicitForTesting(
        QProgram targetProgram)
    {
        var types = new Dictionary<int, OpenQasmClassicalType>();

        void Add(int nodeId, QType? type, bool isArray, string name)
        {
            if (type is null)
                throw new InvalidOperationException(
                    $"test target declaration `{name}` needs an explicit type");
            if (type != QType.Qubit)
                types.Add(
                    nodeId,
                    new OpenQasmClassicalType(type.Value, isArray));
        }

        void Visit(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case QDecl declaration:
                        Add(
                            declaration.Id,
                            declaration.Type,
                            declaration.IsArray,
                            declaration.Name);
                        break;
                    case QFor loop:
                        Add(loop.Id, QType.Int, false, loop.Var);
                        Visit(loop.Body);
                        break;
                    case QIf branch:
                        Visit(branch.Then);
                        Visit(branch.Else);
                        break;
                    case QWhile loop:
                        Visit(loop.Body);
                        break;
                    case QRepeat loop:
                        Visit(loop.Body);
                        break;
                    case QConjugate conjugation:
                        Visit(conjugation.Within);
                        Visit(conjugation.Apply);
                        break;
                }
            }
        }

        foreach (var operation in targetProgram.Operations)
        {
            foreach (var parameter in operation.Params)
                Add(
                    parameter.Id,
                    parameter.Type,
                    parameter.IsArray,
                    parameter.Name);
            Visit(operation.Body);
        }

        return new OpenQasmTypeEnvironment(types);
    }

    internal sealed record BuildResult(
        OpenQasmTypeEnvironment? Environment,
        IReadOnlyList<QoraError> Errors);

    private static QoraError Internal(string message, SourceSpan? span) =>
        new(
            $"internal compiler error: {message}",
            "QINTERNAL",
            span);
}

/// <summary>
/// A fully legalized OpenQASM target program. The underlying tree is still Qora's structured HIR shape
/// during the current backend migration, but it is no longer a common-HIR snapshot: every target-only
/// rewrite has run, all declarations carry their final emitted names in <see cref="Symbols"/>, all
/// classical types needed by emission live in <see cref="Types"/>, and every synthesized identity is
/// described by <see cref="Facts"/>.
///
/// The exact source <c>HirSnapshotId</c> is intentionally owned by the compiler-level target artifact,
/// not by this target model, so the QASM layer remains independent of compilation identities.
/// </summary>
public sealed class OpenQasmTargetProgram
{
    internal OpenQasmTargetProgram(
        QProgram program,
        OpenQasmSymbolMap symbols,
        OpenQasmTypeEnvironment types,
        OpenQasmTargetFacts facts,
        IEnumerable<string> notes)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(notes);

        Program = program;
        Symbols = symbols;
        Types = types;
        Facts = facts;
        Notes = notes.ToImmutableArray();

        VerifyDeclarationFacts();
        VerifyCallableFacts();
        VerifySynthesizedFacts();
    }

    /// <summary>
    /// The legalized target tree. It reuses the HIR node vocabulary only as a transitional structured
    /// representation; it must not be treated as the validated source HIR or as MIR.
    /// </summary>
    public QProgram Program { get; }

    public OpenQasmSymbolMap Symbols { get; }

    public OpenQasmTypeEnvironment Types { get; }

    /// <summary>
    /// Provenance and declaration facts for identities introduced after validated HIR. Original HIR
    /// identities are intentionally absent and remain owned by the source semantic artifact.
    /// </summary>
    public OpenQasmTargetFacts Facts { get; }

    public ImmutableArray<string> Notes { get; }

    /// <summary>Create an explicit, already-legalized target only for focused emitter tests.</summary>
    internal static OpenQasmTargetProgram CreateExplicitForTesting(
        QProgram program,
        IEnumerable<string>? notes = null)
    {
        var identity = new Dictionary<int, string>();

        void Visit(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case QUse use:
                        identity.Add(use.Id, use.Name);
                        break;
                    case QDecl declaration:
                        identity.Add(declaration.Id, declaration.Name);
                        break;
                    case QFor loop:
                        identity.Add(loop.Id, loop.Var);
                        Visit(loop.Body);
                        break;
                    case QIf branch:
                        Visit(branch.Then);
                        Visit(branch.Else);
                        break;
                    case QWhile loop:
                        Visit(loop.Body);
                        break;
                    case QRepeat loop:
                        Visit(loop.Body);
                        break;
                    case QConjugate conjugation:
                        Visit(conjugation.Within);
                        Visit(conjugation.Apply);
                        break;
                }
            }
        }

        foreach (var operation in program.Operations)
        {
            identity.Add(operation.Id, operation.Name);
            foreach (var parameter in operation.Params)
                identity.Add(parameter.Id, parameter.Name);
            Visit(operation.Body);
        }

        return new OpenQasmTargetProgram(
            program,
            new OpenQasmSymbolMap(identity),
            OpenQasmTypeEnvironment.BuildExplicitForTesting(program),
            OpenQasmTargetFacts.Empty,
            notes ?? Array.Empty<string>());
    }

    private void VerifyCallableFacts()
    {
        var operationsById = Program.Operations.ToDictionary(operation => operation.Id);

        void RequireExpressionCall(QCallNode call)
        {
            if (call.CalleeOpId is not int operationId)
                throw new ArgumentException(
                    $"OpenQASM target expression call `{call.Name}` has no user-callable identity.",
                    nameof(Program));
            if (!operationsById.TryGetValue(operationId, out var callee))
                throw new ArgumentException(
                    $"OpenQASM target expression call `{call.Name}` carries dangling CalleeOpId " +
                    $"{operationId}.",
                    nameof(Program));
            if (!callee.IsFunction)
                throw new ArgumentException(
                    $"OpenQASM target expression call `{call.Name}` points to non-function " +
                    $"`{callee.Name}`.",
                    nameof(Program));
            if (!string.Equals(call.Name, callee.Name, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"OpenQASM target expression call `{call.Name}` disagrees with CalleeOpId " +
                    $"{operationId}, whose final name is `{callee.Name}`.",
                    nameof(Program));
        }

        void Visit(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                foreach (var tree in QNodes.ExpressionSites(statement))
                    foreach (var call in QNodes.CallsIn(tree))
                        RequireExpressionCall(call);

                switch (statement)
                {
                    case QGate { CalleeOpId: int operationId } gate:
                        if (!operationsById.TryGetValue(operationId, out var callee))
                            throw new ArgumentException(
                                $"OpenQASM target call `{gate.Name}` carries dangling CalleeOpId " +
                                $"{operationId}.",
                                nameof(Program));
                        if (!string.Equals(gate.Name, callee.Name, StringComparison.Ordinal))
                            throw new ArgumentException(
                                $"OpenQASM target call `{gate.Name}` disagrees with CalleeOpId " +
                                $"{operationId}, whose final name is `{callee.Name}`.",
                                nameof(Program));
                        break;

                    case QGate gate when !IsBuiltinStatement(gate.Name):
                        throw new ArgumentException(
                            $"OpenQASM target user call `{gate.Name}` has no CalleeOpId.",
                            nameof(Program));

                    case QIf branch:
                        Visit(branch.Then);
                        Visit(branch.Else);
                        break;
                    case QFor loop:
                        Visit(loop.Body);
                        break;
                    case QWhile loop:
                        Visit(loop.Body);
                        break;
                    case QRepeat loop:
                        Visit(loop.Body);
                        break;
                    case QConjugate conjugation:
                        Visit(conjugation.Within);
                        Visit(conjugation.Apply);
                        break;
                }
            }
        }

        foreach (var operation in Program.Operations)
            Visit(operation.Body);
    }

    private static bool IsBuiltinStatement(string name) =>
        QoraGates.Names.ContainsKey(name)
        || QoraGates.MeasureLike.Contains(name)
        || QoraGates.NonUnitary.Contains(name)
        || name == "reset";

    private void VerifyDeclarationFacts()
    {
        var declarationIds = new HashSet<int>();
        var classicalDeclarationIds = new HashSet<int>();

        void VerifyName(int nodeId, string currentName)
        {
            if (!declarationIds.Add(nodeId))
                throw new InvalidOperationException(
                    $"OpenQASM target declaration identity {nodeId} occurs more than once");
            if (!Symbols.TryGetEmittedName(nodeId, out var mappedName))
                throw new InvalidOperationException(
                    $"OpenQASM target declaration {nodeId} `{currentName}` has no symbol-map entry");
            if (!string.Equals(mappedName, currentName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"OpenQASM symbol map names declaration {nodeId} `{mappedName}`, " +
                    $"but the target tree contains `{currentName}`");
        }

        void VerifyClassicalType(int nodeId, string currentName)
        {
            if (!classicalDeclarationIds.Add(nodeId))
                throw new InvalidOperationException(
                    $"OpenQASM classical declaration identity {nodeId} occurs more than once");
            if (!Types.TryGetType(nodeId, out _))
                throw new InvalidOperationException(
                    $"OpenQASM classical declaration {nodeId} `{currentName}` has no type entry");
        }

        void Visit(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case QUse use:
                        VerifyName(use.Id, use.Name);
                        break;
                    case QDecl declaration:
                        VerifyName(declaration.Id, declaration.Name);
                        VerifyClassicalType(declaration.Id, declaration.Name);
                        break;
                    case QFor loop:
                        VerifyName(loop.Id, loop.Var);
                        VerifyClassicalType(loop.Id, loop.Var);
                        Visit(loop.Body);
                        break;
                    case QIf branch:
                        Visit(branch.Then);
                        Visit(branch.Else);
                        break;
                    case QWhile loop:
                        Visit(loop.Body);
                        break;
                    case QRepeat loop:
                        Visit(loop.Body);
                        break;
                    case QConjugate conjugation:
                        Visit(conjugation.Within);
                        Visit(conjugation.Apply);
                        break;
                }
            }
        }

        foreach (var operation in Program.Operations)
        {
            VerifyName(operation.Id, operation.Name);
            foreach (var parameter in operation.Params)
            {
                VerifyName(parameter.Id, parameter.Name);
                if (parameter.Type != QType.Qubit)
                    VerifyClassicalType(parameter.Id, parameter.Name);
            }
            Visit(operation.Body);
        }

        if (!declarationIds.SetEquals(Symbols.Declarations.Keys))
        {
            var extras = Symbols.Declarations.Keys
                .Where(nodeId => !declarationIds.Contains(nodeId));
            throw new InvalidOperationException(
                "OpenQASM symbol map contains declarations absent from the final target tree: " +
                string.Join(", ", extras));
        }

        if (!classicalDeclarationIds.SetEquals(Types.Declarations.Keys))
        {
            var extras = Types.Declarations.Keys
                .Where(nodeId => !classicalDeclarationIds.Contains(nodeId));
            throw new InvalidOperationException(
                "OpenQASM type environment contains declarations absent from the final target tree: " +
                string.Join(", ", extras));
        }
    }

    private void VerifySynthesizedFacts()
    {
        var targetNodeIds = new HashSet<int>();

        void Visit(IReadOnlyList<QStmt> statements)
        {
            foreach (var statement in statements)
            {
                targetNodeIds.Add(statement.Id);
                switch (statement)
                {
                    case QIf branch:
                        Visit(branch.Then);
                        Visit(branch.Else);
                        break;
                    case QFor loop:
                        Visit(loop.Body);
                        break;
                    case QWhile loop:
                        Visit(loop.Body);
                        break;
                    case QRepeat loop:
                        Visit(loop.Body);
                        break;
                    case QConjugate conjugation:
                        Visit(conjugation.Within);
                        Visit(conjugation.Apply);
                        break;
                }
            }
        }

        foreach (var operation in Program.Operations)
        {
            targetNodeIds.Add(operation.Id);
            foreach (var parameter in operation.Params)
                targetNodeIds.Add(parameter.Id);
            Visit(operation.Body);
        }

        foreach (var fact in Facts.SynthesizedNodes.Values)
        {
            if (!targetNodeIds.Contains(fact.NodeId))
                throw new InvalidOperationException(
                    $"OpenQASM target fact names synthesized node {fact.NodeId} ({fact.Kind}), " +
                    "but that identity is absent from the final target tree");

            if (fact.DeclaredType is not { } declaredType)
                continue;

            if (!Types.TryGetType(fact.NodeId, out var emittedType))
                throw new InvalidOperationException(
                    $"OpenQASM synthesized declaration {fact.NodeId} ({fact.Kind}) " +
                    "has a declared lowering type but no emitter type");

            if (emittedType != declaredType)
                throw new InvalidOperationException(
                    $"OpenQASM synthesized declaration {fact.NodeId} ({fact.Kind}) " +
                    $"owns lowering type {declaredType}, but the emitter sees {emittedType}");
        }
    }
}
