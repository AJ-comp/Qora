using System.Collections.Frozen;
using System.Numerics;

namespace Qora.Ir.Mir.Analysis;

public enum MirBoundsClassification
{
    Proven,
    Invalid,
    Unproven,
}

/// <summary>
/// The exact position of one indexed operand. The instruction site identifies the owning callable and
/// instruction; the operand index distinguishes several indexed qubit operands in one call.
/// Classical array loads and stores use operand index zero.
/// </summary>
public readonly record struct MirIndexedAccessSite
{
    public MirIndexedAccessSite(
        MirInstructionSite instruction,
        int operandIndex)
    {
        if (operandIndex < 0)
            throw new ArgumentOutOfRangeException(
                nameof(operandIndex),
                operandIndex,
                "an indexed operand position cannot be negative");

        Instruction = instruction;
        OperandIndex = operandIndex;
    }

    public MirInstructionSite Instruction { get; }
    public int OperandIndex { get; }

    public override string ToString() => $"{Instruction}/operand{OperandIndex}";
}

public sealed record MirBoundsResult(
    MirIndexedAccessSite Site,
    int? KnownLength,
    MirBoundsClassification Classification);

/// <summary>
/// Bounds classifications for every classical-array and qubit-array index in one callable.
/// The result is derived from the exact immutable MIR program and cannot be reused with another one.
/// </summary>
public sealed class MirBoundsSnapshot
{
    private readonly MirControlFlowSnapshot _cfg;
    private readonly FrozenDictionary<MirIndexedAccessSite, MirBoundsResult> _resultsBySite;

    internal MirBoundsSnapshot(
        MirControlFlowSnapshot cfg,
        IReadOnlyList<MirBoundsResult> results)
    {
        _cfg = cfg;
        Results = MirCollections.Freeze(results);

        var indexed = new Dictionary<MirIndexedAccessSite, MirBoundsResult>();
        foreach (var result in Results)
        {
            if (!indexed.TryAdd(result.Site, result))
            {
                throw new InvalidOperationException(
                    $"QINTERNAL: MIR indexed access {result.Site} was classified more than once");
            }
        }
        _resultsBySite = indexed.ToFrozenDictionary();
    }

    public MirCallableId Callable => _cfg.Callable;
    public IReadOnlyList<MirBoundsResult> Results { get; }

    public MirBoundsResult ResultFor(MirIndexedAccessSite site) =>
        _resultsBySite.TryGetValue(site, out var result)
            ? result
            : throw new ArgumentOutOfRangeException(
                nameof(site),
                site,
                $"indexed access {site} does not belong to callable {Callable}");

    public MirOrigin OriginFor(MirBoundsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_resultsBySite.TryGetValue(result.Site, out var owned)
            || !ReferenceEquals(owned, result))
        {
            throw new ArgumentException(
                "the bounds result does not belong to this MIR bounds snapshot",
                nameof(result));
        }

        var instruction = _cfg.SourceCallable.RequireInstruction(
            result.Site.Instruction.Instruction);
        if (instruction is MirArrayLoad or MirArrayStore)
            return instruction.Origin;

        if (result.Site.OperandIndex >= instruction.QubitAccesses.Count
            || instruction.QubitAccesses[result.Site.OperandIndex].Index is null)
        {
            throw new InvalidOperationException(
                $"QINTERNAL: MIR bounds result {result.Site} does not identify an indexed operand");
        }
        return instruction.QubitAccesses[result.Site.OperandIndex].Origin;
    }
}

internal static class MirBoundsAnalysis
{
    internal static MirBoundsSnapshot Analyze(
        MirProgram program,
        MirCallableId callableId)
    {
        ArgumentNullException.ThrowIfNull(program);

        var callable = program.FindCallable(callableId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(callableId),
                callableId,
                $"callable {callableId} does not belong to the MIR program");
        var cfg = MirControlFlowAnalysis.AnalyzeUnchecked(program, callable);
        var paths = MirPathConditionAnalysis.AnalyzeVerified(cfg);
        var storage = MirStorageProvenanceAnalysis.AnalyzeUnchecked(program, callable);
        return AnalyzeVerified(paths, storage);
    }

    internal static MirBoundsSnapshot AnalyzeVerified(
        MirPathConditionSnapshot paths,
        MirStorageProvenanceSnapshot storage)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(storage);
        var cfg = paths.ControlFlow;
        var callable = cfg.SourceCallable;
        storage.EnsureFor(cfg.SourceProgram, callable.Id);

        var analyzer = new Analyzer(callable, cfg, paths, storage);
        return new MirBoundsSnapshot(cfg, analyzer.Analyze());
    }

    private sealed class Analyzer
    {
        private const int MaxExpressionAlternatives = 32;

        private readonly MirCallable _callable;
        private readonly MirControlFlowSnapshot _cfg;
        private readonly MirPathConditionSnapshot _paths;
        private readonly MirStorageProvenanceSnapshot _storage;
        private readonly Dictionary<(MirBlockId Block, int Argument), List<IncomingValue>> _incoming;
        private readonly Dictionary<MirValueId, IReadOnlyList<LinearExpression>> _expressions = new();
        private readonly HashSet<MirValueId> _activeExpressions = new();
        private readonly Dictionary<string, int> _lengthIdentities = new(StringComparer.Ordinal);
        private readonly Dictionary<MirStorageId, int> _minimumLengthByStorage = new();
        private readonly HashSet<(MirValueId Value, bool Expected, MirBlockId Context)> _activeTruth = new();

        public Analyzer(
            MirCallable callable,
            MirControlFlowSnapshot cfg,
            MirPathConditionSnapshot paths,
            MirStorageProvenanceSnapshot storage)
        {
            _callable = callable;
            _cfg = cfg;
            _paths = paths;
            _storage = storage;
            _incoming = BuildIncomingValues(callable);
            foreach (var parameter in callable.Parameters.OfType<MirClassicalParameter>())
            {
                if (parameter.Storage is MirArrayStorage parameterStorage)
                    _minimumLengthByStorage[parameterStorage.Id] = parameter.MinimumLength;
            }
        }

        public IReadOnlyList<MirBoundsResult> Analyze()
        {
            var results = new List<MirBoundsResult>();
            foreach (var block in _callable.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is MirArrayLoad load)
                    {
                        results.Add(ClassifyClassicalAccess(load));
                    }
                    else if (instruction is MirArrayStore store)
                    {
                        results.Add(ClassifyClassicalAccess(store));
                    }

                    for (var operandIndex = 0;
                         operandIndex < instruction.QubitAccesses.Count;
                         operandIndex++)
                    {
                        if (instruction.QubitAccesses[operandIndex].Index is null)
                            continue;

                        results.Add(ClassifyQubitAccess(
                            instruction,
                            operandIndex));
                    }
                }
            }

            return results;
        }

        private MirBoundsResult ClassifyClassicalAccess(MirInstruction instruction)
        {
            var array = instruction switch
            {
                MirArrayLoad load => load.Array,
                MirArrayStore store => store.Array,
                _ => throw new ArgumentException(
                    $"instruction {instruction.Id} is not a classical indexed access",
                    nameof(instruction)),
            };
            var arrayType = _callable.RequireValue(array).Type;
            var knownLength = arrayType.KnownLength;
            var length = knownLength is int concrete
                ? LinearExpression.Constant(concrete)
                : ArrayLengthExpression(array);
            return Classify(
                instruction,
                operandIndex: 0,
                length,
                knownLength);
        }

        private MirBoundsResult ClassifyQubitAccess(
            MirInstruction instruction,
            int operandIndex)
        {
            var access = instruction.QubitAccesses[operandIndex];
            var knownLength = QubitLength(access.Qubit.Id);
            var length = knownLength is int concrete
                ? LinearExpression.Constant(concrete)
                : LinearExpression.ArrayLength(
                    LengthIdentityFor($"qubit:{access.Qubit.Id.Value}"),
                    minimumLength: 0);
            return Classify(
                instruction,
                operandIndex,
                length,
                knownLength);
        }

        private MirBoundsResult Classify(
            MirInstruction instruction,
            int operandIndex,
            LinearExpression length,
            int? knownLength)
        {
            var site = new MirIndexedAccessSite(
                new MirInstructionSite(_callable.Id, instruction.Id),
                operandIndex);
            var index = IndexedValueOf(instruction, operandIndex);
            var blockId = _callable.RequireInstructionLocation(instruction.Id).Block.Id;
            if (!_cfg.IsReachable(blockId))
            {
                return new MirBoundsResult(
                    site,
                    knownLength,
                    MirBoundsClassification.Proven);
            }

            var path = _paths.ConditionFor(blockId);
            if (Prove(path, Array.Empty<LinearConstraint>()) == Proof.Impossible)
            {
                return new MirBoundsResult(
                    site,
                    knownLength,
                    MirBoundsClassification.Proven);
            }

            if (knownLength == 0)
            {
                return new MirBoundsResult(
                    site,
                    knownLength,
                    MirBoundsClassification.Invalid);
            }

            var alternatives = ExpressionsOf(index);
            var classifications = new List<MirBoundsClassification>(alternatives.Count);
            foreach (var expression in alternatives)
                classifications.Add(ClassifyExpression(blockId, expression, length));

            MirBoundsClassification classification;
            if (classifications.All(result => result == MirBoundsClassification.Proven))
                classification = MirBoundsClassification.Proven;
            else if (classifications.All(result => result == MirBoundsClassification.Invalid))
                classification = MirBoundsClassification.Invalid;
            else
                classification = MirBoundsClassification.Unproven;

            return new MirBoundsResult(site, knownLength, classification);
        }

        private static MirValueId IndexedValueOf(
            MirInstruction instruction,
            int operandIndex) =>
            instruction switch
            {
                MirArrayLoad load when operandIndex == 0 => load.Index,
                MirArrayStore store when operandIndex == 0 => store.Index,
                _ when instruction.QubitAccesses[operandIndex].Index is MirValueId index => index,
                _ => throw new ArgumentException(
                    $"instruction {instruction.Id} operand {operandIndex} is not indexed",
                    nameof(operandIndex)),
            };

        private MirBoundsClassification ClassifyExpression(
            MirBlockId block,
            LinearExpression index,
            LinearExpression length)
        {
            var lowerBound = LinearConstraint.AtLeast(index, LinearExpression.Zero);
            var upperBound = LinearConstraint.LessThan(index, length);
            var path = _paths.ConditionFor(block);

            var lowerProof = Prove(path, new[] { lowerBound });
            var upperProof = Prove(path, new[] { upperBound });
            if (lowerProof == Proof.Impossible || upperProof == Proof.Impossible)
                return MirBoundsClassification.Proven;

            var lowerIsProven = lowerBound.Evaluate() == ConstraintTruth.True
                || lowerProof == Proof.Proven;
            var upperIsProven = upperBound.Evaluate() == ConstraintTruth.True
                || upperProof == Proof.Proven;
            if (lowerIsProven && upperIsProven)
                return MirBoundsClassification.Proven;

            var belowZero = LinearConstraint.LessThan(index, LinearExpression.Zero);
            var atOrAboveLength = LinearConstraint.AtLeast(index, length);
            if (belowZero.Evaluate() == ConstraintTruth.True
                || atOrAboveLength.Evaluate() == ConstraintTruth.True)
                return MirBoundsClassification.Invalid;

            var invalidProof = Prove(path, new[] { belowZero, atOrAboveLength });
            return invalidProof switch
            {
                Proof.Impossible => MirBoundsClassification.Proven,
                Proof.Proven => MirBoundsClassification.Invalid,
                _ => MirBoundsClassification.Unproven,
            };
        }

        private Proof Prove(
            MirPathCondition condition,
            IReadOnlyList<LinearConstraint> targets)
        {
            switch (condition.Kind)
            {
                case MirPathConditionKind.Never:
                    return Proof.Impossible;
                case MirPathConditionKind.Always:
                    return Proof.Unknown;
                case MirPathConditionKind.Predicate:
                {
                    var predicate = condition.Predicate
                        ?? throw new InvalidOperationException(
                            "QINTERNAL: predicate path condition has no predicate");
                    return ProveTruth(
                        predicate.Condition,
                        predicate.ExpectedValue,
                        predicate.Controller,
                        targets);
                }
                case MirPathConditionKind.All:
                    return ProveConjunction(condition.Terms, targets);
                case MirPathConditionKind.Any:
                    return ProveDisjunction(condition.Terms, targets);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(condition),
                        condition.Kind,
                        "unknown MIR path-condition kind");
            }
        }

        private Proof ProveConjunction(
            IReadOnlyList<MirPathCondition> terms,
            IReadOnlyList<LinearConstraint> targets)
        {
            var proves = false;
            var requiredConstraints = new List<LinearConstraint>();
            foreach (var term in terms)
            {
                var result = Prove(term, targets);
                if (result == Proof.Impossible)
                    return Proof.Impossible;
                if (result == Proof.Proven)
                    proves = true;

                CollectRequiredConstraints(term, requiredConstraints);
            }

            if (AreJointlyImpossible(requiredConstraints))
                return Proof.Impossible;

            return proves ? Proof.Proven : Proof.Unknown;
        }

        private void CollectRequiredConstraints(
            MirPathCondition condition,
            ICollection<LinearConstraint> constraints)
        {
            if (condition.Kind == MirPathConditionKind.All)
            {
                foreach (var term in condition.Terms)
                    CollectRequiredConstraints(term, constraints);
                return;
            }

            if (condition.Kind != MirPathConditionKind.Predicate
                || condition.Predicate is not { } predicate)
                return;

            CollectRequiredConstraintsForTruth(
                predicate.Condition,
                predicate.ExpectedValue,
                constraints);
        }

        private void CollectRequiredConstraintsForTruth(
            MirValueId value,
            bool expected,
            ICollection<LinearConstraint> constraints)
        {
            var required = RequiredConstraintsForTruth(
                value,
                expected,
                new HashSet<MirValueId>());
            if (required is null)
                return;

            foreach (var constraint in required)
                constraints.Add(constraint);
        }

        private IReadOnlyList<LinearConstraint>? RequiredConstraintsForTruth(
            MirValueId value,
            bool expected,
            ISet<MirValueId> active)
        {
            if (!active.Add(value))
                return null;

            try
            {
                var definition = _callable.DefinitionOf(value);
                if (definition.Kind != MirValueDefinitionKind.InstructionResult
                    || definition.Instruction is not MirInstructionId instructionId)
                    return null;

                var instruction = _callable.RequireInstruction(instructionId);
                if (instruction is MirUnary
                    {
                        Operator: MirUnaryOperator.LogicalNot,
                    } unary)
                {
                    return RequiredConstraintsForTruth(
                        unary.Operand,
                        !expected,
                        active);
                }

                if (instruction is MirConvert convert
                    && _callable.RequireValue(convert.Operand).Type == MirType.Scalar(QType.Bit))
                {
                    return RequiredConstraintsForTruth(
                        convert.Operand,
                        expected,
                        active);
                }

                if (instruction is not MirBinary binary || !IsComparison(binary.Operator))
                    return null;
                if (_callable.RequireValue(binary.Left).Type.IsArray
                    || _callable.RequireValue(binary.Right).Type.IsArray)
                {
                    return null;
                }

                var left = ExpressionsOf(binary.Left);
                var right = ExpressionsOf(binary.Right);
                if (left.Count != 1 || right.Count != 1)
                    return null;

                return ConstraintsForComparison(
                    binary.Operator,
                    expected,
                    left[0],
                    right[0]);
            }
            finally
            {
                active.Remove(value);
            }
        }

        private static bool AreJointlyImpossible(
            IReadOnlyList<LinearConstraint> constraints)
        {
            for (var index = 0; index < constraints.Count; index++)
            {
                var constraint = constraints[index];
                if (constraint.Evaluate() == ConstraintTruth.False)
                    return true;

                for (var earlierIndex = 0; earlierIndex < index; earlierIndex++)
                {
                    if (constraint.Contradicts(constraints[earlierIndex]))
                        return true;
                }
            }

            return false;
        }

        private Proof ProveDisjunction(
            IReadOnlyList<MirPathCondition> terms,
            IReadOnlyList<LinearConstraint> targets)
        {
            var hasFeasibleTerm = false;
            foreach (var term in terms)
            {
                var result = Prove(term, targets);
                if (result == Proof.Impossible)
                    continue;
                hasFeasibleTerm = true;
                if (result != Proof.Proven)
                    return Proof.Unknown;
            }
            return hasFeasibleTerm ? Proof.Proven : Proof.Impossible;
        }

        private Proof ProveTruth(
            MirValueId value,
            bool expected,
            MirBlockId context,
            IReadOnlyList<LinearConstraint> targets)
        {
            var activeKey = (value, expected, context);
            if (!_activeTruth.Add(activeKey))
                return Proof.Unknown;

            try
            {
                var definition = _callable.DefinitionOf(value);
                if (definition.Kind == MirValueDefinitionKind.BlockArgument
                    && definition.Block is MirBlockId block)
                {
                    return ProveBlockArgumentTruth(
                        block,
                        definition.Index,
                        expected,
                        targets);
                }

                if (definition.Kind != MirValueDefinitionKind.InstructionResult
                    || definition.Instruction is not MirInstructionId instructionId)
                    return Proof.Unknown;

                var instruction = _callable.RequireInstruction(instructionId);
                if (instruction is MirConstant constant
                    && TryBooleanConstant(constant.Text, out var constantValue))
                    return constantValue == expected ? Proof.Unknown : Proof.Impossible;

                if (instruction is MirUnary
                    {
                        Operator: MirUnaryOperator.LogicalNot,
                    } unary)
                    return ProveTruth(unary.Operand, !expected, context, targets);

                if (instruction is MirConvert convert
                    && _callable.RequireValue(convert.Operand).Type == MirType.Scalar(QType.Bit))
                    return ProveTruth(convert.Operand, expected, context, targets);

                if (instruction is MirBinary binary
                    && IsComparison(binary.Operator))
                    return ProveComparison(binary, expected, targets);

                return Proof.Unknown;
            }
            finally
            {
                _activeTruth.Remove(activeKey);
            }
        }

        private Proof ProveBlockArgumentTruth(
            MirBlockId block,
            int argumentIndex,
            bool expected,
            IReadOnlyList<LinearConstraint> targets)
        {
            if (!_incoming.TryGetValue(
                    (block, argumentIndex),
                    out var incomingValues))
                return Proof.Unknown;

            var hasFeasibleInput = false;
            var hasBackedgeInput = false;
            foreach (var incoming in incomingValues)
            {
                if (_cfg.Dominates(block, incoming.Source))
                {
                    hasBackedgeInput = true;
                    continue;
                }
                if (ForwardedBranchValueContradicts(
                        incoming,
                        expected))
                    continue;
                if (IncomingTruthIsJointlyImpossible(incoming, expected))
                    continue;

                var sourcePath = Prove(_paths.ConditionFor(incoming.Source), targets);
                var edge = ProveIncomingEdge(incoming, targets);
                var value = ProveTruth(incoming.Value, expected, incoming.Source, targets);
                var combined = Conjunction(sourcePath, edge, value);
                if (combined == Proof.Impossible)
                    continue;
                hasFeasibleInput = true;
                if (combined != Proof.Proven)
                    return Proof.Unknown;
            }

            if (hasBackedgeInput)
                return Proof.Unknown;
            return hasFeasibleInput ? Proof.Proven : Proof.Impossible;
        }

        private bool IncomingTruthIsJointlyImpossible(
            IncomingValue incoming,
            bool expected)
        {
            var constraints = new List<LinearConstraint>();
            CollectRequiredConstraints(
                _paths.ConditionFor(incoming.Source),
                constraints);
            CollectRequiredConstraintsForTruth(
                incoming.Value,
                expected,
                constraints);

            if (_callable.RequireBlock(incoming.Source).Terminator is MirBranch branch)
            {
                var edgeValue = incoming.SuccessorOrdinal switch
                {
                    0 => true,
                    1 => false,
                    _ => throw new InvalidOperationException(
                        $"QINTERNAL: branch edge {incoming.SuccessorOrdinal} is not valid"),
                };
                CollectRequiredConstraintsForTruth(
                    branch.Condition,
                    edgeValue,
                    constraints);
            }

            return AreJointlyImpossible(constraints);
        }

        private bool ForwardedBranchValueContradicts(
            IncomingValue incoming,
            bool expected)
        {
            if (_callable.RequireBlock(incoming.Source).Terminator
                is not MirBranch branch
                || branch.Condition != incoming.Value)
                return false;

            var edgeValue = incoming.SuccessorOrdinal switch
            {
                0 => true,
                1 => false,
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: branch edge {incoming.SuccessorOrdinal} is not valid"),
            };
            return edgeValue != expected;
        }

        private Proof ProveIncomingEdge(
            IncomingValue incoming,
            IReadOnlyList<LinearConstraint> targets)
        {
            var terminator = _callable.RequireBlock(incoming.Source).Terminator;
            if (terminator is not MirBranch branch)
                return Proof.Unknown;

            var expected = incoming.SuccessorOrdinal switch
            {
                0 => true,
                1 => false,
                _ => throw new InvalidOperationException(
                    $"QINTERNAL: branch edge {incoming.SuccessorOrdinal} is not valid"),
            };
            return ProveTruth(branch.Condition, expected, incoming.Source, targets);
        }

        private Proof ProveComparison(
            MirBinary comparison,
            bool expected,
            IReadOnlyList<LinearConstraint> targets)
        {
            if (_callable.RequireValue(comparison.Left).Type.IsArray
                || _callable.RequireValue(comparison.Right).Type.IsArray)
            {
                return Proof.Unknown;
            }

            var leftAlternatives = ExpressionsOf(comparison.Left);
            var rightAlternatives = ExpressionsOf(comparison.Right);
            var hasFeasiblePair = false;

            foreach (var left in leftAlternatives)
            {
                foreach (var right in rightAlternatives)
                {
                    var constraints = ConstraintsForComparison(
                        comparison.Operator,
                        expected,
                        left,
                        right);
                    if (constraints is null)
                        return Proof.Unknown;
                    if (constraints.Any(constraint => constraint.Evaluate() == ConstraintTruth.False))
                        continue;

                    hasFeasiblePair = true;
                    var pairProves = false;
                    foreach (var constraint in constraints)
                    {
                        if (targets.Any(constraint.Implies))
                        {
                            pairProves = true;
                            break;
                        }
                    }
                    if (!pairProves)
                        return Proof.Unknown;
                }
            }

            return hasFeasiblePair ? Proof.Proven : Proof.Impossible;
        }

        private IReadOnlyList<LinearExpression> ExpressionsOf(MirValueId value)
        {
            if (_expressions.TryGetValue(value, out var cached))
                return cached;
            if (!_activeExpressions.Add(value))
                return new[] { LinearExpression.Scalar(value) };

            IReadOnlyList<LinearExpression> result;
            try
            {
                var mirValue = _callable.RequireValue(value);
                var definition = _callable.DefinitionOf(mirValue);
                result = definition.Kind switch
                {
                    MirValueDefinitionKind.BlockArgument =>
                        BlockArgumentExpressions(mirValue, definition),
                    MirValueDefinitionKind.InstructionResult =>
                        InstructionExpressions(mirValue, definition),
                    _ => new[] { LinearExpression.Scalar(value) },
                };
            }
            finally
            {
                _activeExpressions.Remove(value);
            }

            result = DistinctOrScalar(value, result);
            _expressions[value] = result;
            return result;
        }

        private IReadOnlyList<LinearExpression> BlockArgumentExpressions(
            MirValue value,
            MirValueDefinition definition)
        {
            if (definition.Block is not MirBlockId block
                || !_incoming.TryGetValue(
                    (block, definition.Index),
                    out var incomingValues))
                return new[] { LinearExpression.Scalar(value.Id) };

            if (incomingValues.Any(incoming => _cfg.Dominates(block, incoming.Source)))
            {
                var invariantExpressions = LoopInvariantEntryExpressions(
                    value.Id,
                    block,
                    incomingValues);
                if (invariantExpressions is not null)
                    return invariantExpressions;

                return new[]
                {
                    LinearExpression.Scalar(
                        value.Id,
                        AscendingInductionMinimum(value.Id, block, incomingValues)),
                };
            }

            var alternatives = new List<LinearExpression>();
            foreach (var incoming in incomingValues)
            {
                alternatives.AddRange(ExpressionsOf(incoming.Value));
                if (alternatives.Count > MaxExpressionAlternatives)
                    return new[] { LinearExpression.Scalar(value.Id) };
            }
            return alternatives;
        }

        private IReadOnlyList<LinearExpression>? LoopInvariantEntryExpressions(
            MirValueId phi,
            MirBlockId header,
            IReadOnlyList<IncomingValue> incomingValues)
        {
            var entries = new List<LinearExpression>();
            var hasBackedge = false;
            foreach (var incoming in incomingValues)
            {
                if (_cfg.Dominates(header, incoming.Source))
                {
                    hasBackedge = true;
                    if (!IsPhiForwardOf(
                            incoming.Value,
                            phi,
                            new HashSet<MirValueId>()))
                        return null;
                    continue;
                }

                entries.AddRange(ExpressionsOf(incoming.Value));
                if (entries.Count > MaxExpressionAlternatives)
                    return null;
            }

            return hasBackedge && entries.Count > 0
                ? DistinctOrScalar(phi, entries)
                : null;
        }

        private bool IsPhiForwardOf(
            MirValueId value,
            MirValueId target,
            HashSet<MirValueId> active)
        {
            if (value == target)
                return true;
            if (!active.Add(value))
                return false;

            try
            {
                var definition = _callable.DefinitionOf(value);
                if (definition.Kind != MirValueDefinitionKind.BlockArgument
                    || definition.Block is not MirBlockId block
                    || !_incoming.TryGetValue(
                        (block, definition.Index),
                        out var incomingValues))
                    return false;

                var hasForwardedInput = false;
                foreach (var incoming in incomingValues)
                {
                    if (incoming.Value == value)
                        continue;
                    if (!IsPhiForwardOf(incoming.Value, target, active))
                        return false;
                    hasForwardedInput = true;
                }
                return hasForwardedInput;
            }
            finally
            {
                active.Remove(value);
            }
        }

        private BigInteger? AscendingInductionMinimum(
            MirValueId phi,
            MirBlockId header,
            IReadOnlyList<IncomingValue> incomingValues)
        {
            BigInteger? minimum = null;
            var hasEntry = false;
            var hasBackedge = false;

            foreach (var incoming in incomingValues)
            {
                if (_cfg.Dominates(header, incoming.Source))
                {
                    hasBackedge = true;
                    if (!IsNonDecreasingBackedge(
                            incoming.Value,
                            phi,
                            header,
                            incoming.Source))
                        return null;
                    continue;
                }

                hasEntry = true;
                var entryAlternatives = ExpressionsOf(incoming.Value);
                foreach (var entry in entryAlternatives)
                {
                    if (!entry.TryMinimum(out var entryMinimum))
                        return null;
                    minimum = minimum is null
                        ? entryMinimum
                        : BigInteger.Min(minimum.Value, entryMinimum);
                }
            }

            return hasEntry && hasBackedge ? minimum : null;
        }

        private bool IsNonDecreasingBackedge(
            MirValueId value,
            MirValueId phi,
            MirBlockId header,
            MirBlockId backedgeSource)
        {
            if (value == phi)
                return true;

            var definition = _callable.DefinitionOf(value);
            if (definition.Kind != MirValueDefinitionKind.InstructionResult
                || definition.Instruction is not MirInstructionId instructionId
                || _callable.RequireInstruction(instructionId) is not MirBinary binary)
                return false;

            if (binary.Operator == MirBinaryOperator.Add)
            {
                if (binary.Left == phi
                    && TryExactInteger(binary.Right, new HashSet<MirValueId>(), out var right))
                    return right >= 0
                        && IncrementCannotOverflow(phi, header, backedgeSource, right);
                if (binary.Right == phi
                    && TryExactInteger(binary.Left, new HashSet<MirValueId>(), out var left))
                    return left >= 0
                        && IncrementCannotOverflow(phi, header, backedgeSource, left);
            }

            return binary.Operator == MirBinaryOperator.Subtract
                && binary.Left == phi
                && TryExactInteger(binary.Right, new HashSet<MirValueId>(), out var subtracted)
                && subtracted <= 0
                && IncrementCannotOverflow(phi, header, backedgeSource, -subtracted);
        }

        private bool IncrementCannotOverflow(
            MirValueId phi,
            MirBlockId header,
            MirBlockId backedgeSource,
            BigInteger increment)
        {
            if (increment == 0)
                return true;
            if (_callable.RequireBlock(header).Terminator is not MirBranch branch)
                return false;
            if (!_cfg.Dominates(branch.TrueTarget, backedgeSource)
                || _cfg.Dominates(branch.FalseTarget, backedgeSource))
                return false;
            var conditionDefinition = _callable.DefinitionOf(branch.Condition);
            if (conditionDefinition.Kind != MirValueDefinitionKind.InstructionResult
                || conditionDefinition.Instruction is not MirInstructionId conditionInstruction
                || _callable.RequireInstruction(conditionInstruction) is not MirBinary comparison)
                return false;

            MirValueId upperBound;
            BigInteger strictAdjustment;
            if (comparison.Left == phi
                && comparison.Operator is MirBinaryOperator.Less or MirBinaryOperator.LessOrEqual)
            {
                upperBound = comparison.Right;
                strictAdjustment = comparison.Operator == MirBinaryOperator.Less ? -1 : 0;
            }
            else if (comparison.Right == phi
                     && comparison.Operator is MirBinaryOperator.Greater or MirBinaryOperator.GreaterOrEqual)
            {
                upperBound = comparison.Left;
                strictAdjustment = comparison.Operator == MirBinaryOperator.Greater ? -1 : 0;
            }
            else
            {
                return false;
            }

            foreach (var expression in ExpressionsOf(upperBound))
            {
                if (!expression.TryMaximum(out var maximum)
                    || maximum + strictAdjustment + increment > long.MaxValue)
                    return false;
            }
            return true;
        }

        private bool TryExactInteger(
            MirValueId value,
            HashSet<MirValueId> active,
            out BigInteger result)
        {
            if (!active.Add(value))
            {
                result = default;
                return false;
            }

            try
            {
                var definition = _callable.DefinitionOf(value);
                if (definition.Kind != MirValueDefinitionKind.InstructionResult
                    || definition.Instruction is not MirInstructionId instructionId)
                {
                    result = default;
                    return false;
                }

                switch (_callable.RequireInstruction(instructionId))
                {
                    case MirConstant constant when BigInteger.TryParse(
                        constant.Text,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result):
                        return FitsInt64(result);
                    case MirUnary
                    {
                        Operator: MirUnaryOperator.Negate,
                    } unary when TryExactInteger(unary.Operand, active, out var operand):
                        result = -operand;
                        return FitsInt64(result);
                    case MirBinary binary
                        when TryExactInteger(binary.Left, active, out var left)
                             && TryExactInteger(binary.Right, active, out var right):
                        switch (binary.Operator)
                        {
                            case MirBinaryOperator.Add:
                                result = left + right;
                                return FitsInt64(result);
                            case MirBinaryOperator.Subtract:
                                result = left - right;
                                return FitsInt64(result);
                            case MirBinaryOperator.Multiply:
                                result = left * right;
                                return FitsInt64(result);
                        }
                        break;
                }

                result = default;
                return false;
            }
            finally
            {
                active.Remove(value);
            }
        }

        private static bool FitsInt64(BigInteger value) =>
            value >= long.MinValue && value <= long.MaxValue;

        private IReadOnlyList<LinearExpression> InstructionExpressions(
            MirValue value,
            MirValueDefinition definition)
        {
            if (definition.Instruction is not MirInstructionId instructionId)
                return new[] { LinearExpression.Scalar(value.Id) };
            var instruction = _callable.RequireInstruction(instructionId);

            if (instruction is MirConstant constant
                && value.Type == MirType.Scalar(QType.Int)
                && long.TryParse(
                    constant.Text,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var integer))
                return new[] { LinearExpression.Constant(integer) };

            if (instruction is MirUnary
                {
                    Operator: MirUnaryOperator.Negate,
                } unary)
                return Scale(ExpressionsOf(unary.Operand), -1, value.Id);

            if (instruction is MirBinary binary)
            {
                return binary.Operator switch
                {
                    MirBinaryOperator.Add => Combine(
                        ExpressionsOf(binary.Left),
                        ExpressionsOf(binary.Right),
                        static (left, right) => left.Add(right),
                        value.Id),
                    MirBinaryOperator.Subtract => Combine(
                        ExpressionsOf(binary.Left),
                        ExpressionsOf(binary.Right),
                        static (left, right) => left.Subtract(right),
                        value.Id),
                    MirBinaryOperator.Multiply => Multiply(binary, value.Id),
                    _ => new[] { LinearExpression.Scalar(value.Id) },
                };
            }

            if (instruction is MirConvert convert
                && _callable.RequireValue(convert.Operand).Type == MirType.Scalar(QType.Int)
                && value.Type == MirType.Scalar(QType.Int))
                return ExpressionsOf(convert.Operand);

            if (instruction is MirArrayLength length)
            {
                var arrayType = _callable.RequireValue(length.Array).Type;
                return arrayType.KnownLength is int known
                    ? new[] { LinearExpression.Constant(known) }
                    : new[] { ArrayLengthExpression(length.Array) };
            }

            return new[] { LinearExpression.Scalar(value.Id) };
        }

        private IReadOnlyList<LinearExpression> Multiply(
            MirBinary binary,
            MirValueId fallback)
        {
            var left = ExpressionsOf(binary.Left);
            var right = ExpressionsOf(binary.Right);
            if (TrySingleConstant(left, out var leftConstant))
                return Scale(right, leftConstant, fallback);
            if (TrySingleConstant(right, out var rightConstant))
                return Scale(left, rightConstant, fallback);
            return new[] { LinearExpression.Scalar(fallback) };
        }

        private static bool TrySingleConstant(
            IReadOnlyList<LinearExpression> expressions,
            out BigInteger value)
        {
            if (expressions.Count == 1 && expressions[0].TryConstant(out value))
                return true;
            value = default;
            return false;
        }

        private static IReadOnlyList<LinearExpression> Scale(
            IReadOnlyList<LinearExpression> expressions,
            BigInteger factor,
            MirValueId fallback)
        {
            if (expressions.Count > MaxExpressionAlternatives)
                return new[] { LinearExpression.Scalar(fallback) };

            var results = new List<LinearExpression>(expressions.Count);
            foreach (var expression in expressions)
            {
                var scaled = expression.Scale(factor);
                if (!scaled.IsWithinInt64Range())
                    return new[] { LinearExpression.Scalar(fallback) };
                results.Add(scaled);
            }
            return results;
        }

        private static IReadOnlyList<LinearExpression> Combine(
            IReadOnlyList<LinearExpression> left,
            IReadOnlyList<LinearExpression> right,
            Func<LinearExpression, LinearExpression, LinearExpression> operation,
            MirValueId fallback)
        {
            if ((long)left.Count * right.Count > MaxExpressionAlternatives)
                return new[] { LinearExpression.Scalar(fallback) };

            var results = new List<LinearExpression>(left.Count * right.Count);
            foreach (var leftExpression in left)
            {
                foreach (var rightExpression in right)
                {
                    var combined = operation(leftExpression, rightExpression);
                    if (!combined.IsWithinInt64Range())
                        return new[] { LinearExpression.Scalar(fallback) };
                    results.Add(combined);
                }
            }
            return results;
        }

        private static IReadOnlyList<LinearExpression> DistinctOrScalar(
            MirValueId value,
            IReadOnlyList<LinearExpression> expressions)
        {
            var distinct = expressions.Distinct().ToArray();
            return distinct.Length is > 0 and <= MaxExpressionAlternatives
                ? distinct
                : new[] { LinearExpression.Scalar(value) };
        }

        private LinearExpression ArrayLengthExpression(MirValueId array)
        {
            var provenance = _storage.ProvenanceOf(array);
            if (!provenance.IsComplete || provenance.PossibleStorages.Count == 0)
            {
                var minimumLength = MinimumLengthFromDefiningParameter(array);
                return LinearExpression.ArrayLength(
                    LengthIdentityFor($"value:{array.Value}"),
                    minimumLength);
            }

            var minimum = int.MaxValue;
            foreach (var storage in provenance.PossibleStorages)
            {
                if (!_minimumLengthByStorage.TryGetValue(storage, out var storageMinimum))
                    storageMinimum = 0;
                minimum = Math.Min(minimum, storageMinimum);
            }
            if (minimum == int.MaxValue)
                minimum = 0;

            var identityKey = provenance.PossibleStorages.Count == 1
                ? $"storage:{provenance.PossibleStorages[0].Value}"
                : $"value:{array.Value}";
            return LinearExpression.ArrayLength(
                LengthIdentityFor(identityKey),
                minimum);
        }

        private int LengthIdentityFor(string key)
        {
            if (_lengthIdentities.TryGetValue(key, out var identity))
                return identity;
            identity = _lengthIdentities.Count;
            _lengthIdentities.Add(key, identity);
            return identity;
        }

        private int MinimumLengthFromDefiningParameter(MirValueId array)
        {
            foreach (var parameter in _callable.Parameters.OfType<MirClassicalParameter>())
            {
                if (parameter.Value.Id == array)
                    return parameter.MinimumLength;
            }
            return 0;
        }

        private int? QubitLength(MirQubitId qubit)
        {
            var seed = _callable.Qubits.FirstOrDefault(
                candidate => candidate.Id == qubit && candidate.Version.Value == 0);
            return seed switch
            {
                MirQubitParameter parameter when parameter.IsArray => parameter.Length,
                MirQubitFromUse local => local.Length,
                _ => null,
            };
        }

        private static bool TryBooleanConstant(string text, out bool value)
        {
            switch (text)
            {
                case "1":
                case "true":
                    value = true;
                    return true;
                case "0":
                case "false":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static bool IsComparison(MirBinaryOperator @operator) =>
            @operator is MirBinaryOperator.Equal
                or MirBinaryOperator.NotEqual
                or MirBinaryOperator.Less
                or MirBinaryOperator.LessOrEqual
                or MirBinaryOperator.Greater
                or MirBinaryOperator.GreaterOrEqual;

        private static IReadOnlyList<LinearConstraint>? ConstraintsForComparison(
            MirBinaryOperator @operator,
            bool expected,
            LinearExpression left,
            LinearExpression right)
        {
            if (!expected)
            {
                @operator = @operator switch
                {
                    MirBinaryOperator.Equal => MirBinaryOperator.NotEqual,
                    MirBinaryOperator.NotEqual => MirBinaryOperator.Equal,
                    MirBinaryOperator.Less => MirBinaryOperator.GreaterOrEqual,
                    MirBinaryOperator.LessOrEqual => MirBinaryOperator.Greater,
                    MirBinaryOperator.Greater => MirBinaryOperator.LessOrEqual,
                    MirBinaryOperator.GreaterOrEqual => MirBinaryOperator.Less,
                    _ => @operator,
                };
            }

            return @operator switch
            {
                MirBinaryOperator.Less => new[] { LinearConstraint.LessThan(left, right) },
                MirBinaryOperator.LessOrEqual => new[] { LinearConstraint.AtMost(left, right) },
                MirBinaryOperator.Greater => new[] { LinearConstraint.LessThan(right, left) },
                MirBinaryOperator.GreaterOrEqual => new[] { LinearConstraint.AtMost(right, left) },
                MirBinaryOperator.Equal => new[]
                {
                    LinearConstraint.AtMost(left, right),
                    LinearConstraint.AtMost(right, left),
                },
                MirBinaryOperator.NotEqual => null,
                _ => null,
            };
        }

        private static Proof Conjunction(params Proof[] terms)
        {
            if (terms.Any(term => term == Proof.Impossible))
                return Proof.Impossible;
            return terms.Any(term => term == Proof.Proven)
                ? Proof.Proven
                : Proof.Unknown;
        }

        private static Dictionary<(MirBlockId Block, int Argument), List<IncomingValue>>
            BuildIncomingValues(MirCallable callable)
        {
            var incoming = new Dictionary<(MirBlockId, int), List<IncomingValue>>();

            void Add(
                MirBlockId source,
                int successorOrdinal,
                MirBlockId target,
                IReadOnlyList<MirValueId> arguments)
            {
                for (var index = 0; index < arguments.Count; index++)
                {
                    var key = (target, index);
                    if (!incoming.TryGetValue(key, out var values))
                    {
                        values = new List<IncomingValue>();
                        incoming.Add(key, values);
                    }
                    values.Add(new IncomingValue(source, successorOrdinal, arguments[index]));
                }
            }

            foreach (var block in callable.Blocks)
            {
                switch (block.Terminator)
                {
                    case MirJump jump:
                        Add(block.Id, 0, jump.Target, jump.Arguments);
                        break;
                    case MirBranch branch:
                        Add(block.Id, 0, branch.TrueTarget, branch.TrueArguments);
                        Add(block.Id, 1, branch.FalseTarget, branch.FalseArguments);
                        break;
                }
            }
            return incoming;
        }

        private sealed record IncomingValue(
            MirBlockId Source,
            int SuccessorOrdinal,
            MirValueId Value);

        private enum Proof
        {
            Impossible,
            Proven,
            Unknown,
        }
    }

    private enum LinearAtomKind
    {
        Scalar,
        ArrayLength,
    }

    private readonly record struct LinearAtom(
        LinearAtomKind Kind,
        int Identity,
        BigInteger? Minimum,
        BigInteger? Maximum);

    private sealed class LinearExpression : IEquatable<LinearExpression>
    {
        private readonly FrozenDictionary<LinearAtom, BigInteger> _terms;

        private LinearExpression(
            BigInteger constant,
            IReadOnlyDictionary<LinearAtom, BigInteger> terms)
        {
            ConstantTerm = constant;
            _terms = terms
                .Where(pair => pair.Value != BigInteger.Zero)
                .ToFrozenDictionary();
        }

        public static LinearExpression Zero { get; } = Constant(0);
        public BigInteger ConstantTerm { get; }
        public IReadOnlyDictionary<LinearAtom, BigInteger> Terms => _terms;

        public static LinearExpression Constant(BigInteger value) =>
            new(value, new Dictionary<LinearAtom, BigInteger>());

        public static LinearExpression Scalar(MirValueId value) =>
            Scalar(value, minimum: null);

        public static LinearExpression Scalar(
            MirValueId value,
            BigInteger? minimum) =>
            Atom(new LinearAtom(
                LinearAtomKind.Scalar,
                value.Value,
                minimum,
                Maximum: null));

        public static LinearExpression ArrayLength(
            int identity,
            int minimumLength) =>
            Atom(new LinearAtom(
                LinearAtomKind.ArrayLength,
                identity,
                minimumLength,
                int.MaxValue));

        private static LinearExpression Atom(LinearAtom atom) =>
            new(
                BigInteger.Zero,
                new Dictionary<LinearAtom, BigInteger>
                {
                    [atom] = BigInteger.One,
                });

        public LinearExpression Add(LinearExpression other) =>
            Combine(other, BigInteger.One);

        public LinearExpression Subtract(LinearExpression other) =>
            Combine(other, BigInteger.MinusOne);

        public LinearExpression Scale(BigInteger factor)
        {
            var terms = new Dictionary<LinearAtom, BigInteger>();
            foreach (var (atom, coefficient) in _terms)
                terms.Add(atom, coefficient * factor);
            return new LinearExpression(ConstantTerm * factor, terms);
        }

        public bool TryConstant(out BigInteger value)
        {
            value = ConstantTerm;
            return _terms.Count == 0;
        }

        public bool TryMinimum(out BigInteger minimum)
        {
            minimum = ConstantTerm;
            foreach (var (atom, coefficient) in _terms)
            {
                var endpoint = coefficient >= 0 ? atom.Minimum : atom.Maximum;
                if (endpoint is null)
                {
                    minimum = default;
                    return false;
                }
                minimum += coefficient * endpoint.Value;
            }
            return true;
        }

        public bool TryMaximum(out BigInteger maximum)
        {
            if (!TryBounds(out _, out maximum))
            {
                maximum = default;
                return false;
            }
            return true;
        }

        public bool IsWithinInt64Range()
        {
            if (!TryBounds(out var minimum, out var maximum))
                return false;
            return minimum >= long.MinValue && maximum <= long.MaxValue;
        }

        public bool Equals(LinearExpression? other)
        {
            if (other is null || ConstantTerm != other.ConstantTerm || _terms.Count != other._terms.Count)
                return false;
            foreach (var (atom, coefficient) in _terms)
                if (!other._terms.TryGetValue(atom, out var otherCoefficient)
                    || coefficient != otherCoefficient)
                    return false;
            return true;
        }

        public override bool Equals(object? obj) =>
            obj is LinearExpression other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(ConstantTerm);
            foreach (var pair in _terms.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Identity))
            {
                hash.Add(pair.Key);
                hash.Add(pair.Value);
            }
            return hash.ToHashCode();
        }

        private LinearExpression Combine(
            LinearExpression other,
            BigInteger otherFactor)
        {
            var terms = _terms.ToDictionary();
            foreach (var (atom, coefficient) in other._terms)
            {
                terms.TryGetValue(atom, out var current);
                terms[atom] = current + coefficient * otherFactor;
            }
            return new LinearExpression(
                ConstantTerm + other.ConstantTerm * otherFactor,
                terms);
        }

        private bool TryBounds(
            out BigInteger minimum,
            out BigInteger maximum)
        {
            minimum = ConstantTerm;
            maximum = ConstantTerm;
            foreach (var (atom, coefficient) in _terms)
            {
                if (atom.Minimum is null || atom.Maximum is null)
                {
                    minimum = default;
                    maximum = default;
                    return false;
                }

                if (coefficient >= 0)
                {
                    minimum += coefficient * atom.Minimum.Value;
                    maximum += coefficient * atom.Maximum.Value;
                }
                else
                {
                    minimum += coefficient * atom.Maximum.Value;
                    maximum += coefficient * atom.Minimum.Value;
                }
            }
            return true;
        }
    }

    private enum ConstraintTruth
    {
        False,
        Unknown,
        True,
    }

    private sealed class LinearConstraint
    {
        private readonly FrozenDictionary<LinearAtom, BigInteger> _terms;

        private LinearConstraint(
            IReadOnlyDictionary<LinearAtom, BigInteger> terms,
            BigInteger bound)
        {
            var mutable = terms
                .Where(pair => pair.Value != BigInteger.Zero)
                .ToDictionary();
            var divisor = BigInteger.Zero;
            foreach (var coefficient in mutable.Values)
                divisor = BigInteger.GreatestCommonDivisor(divisor, BigInteger.Abs(coefficient));
            if (divisor > BigInteger.One)
            {
                foreach (var atom in mutable.Keys.ToArray())
                    mutable[atom] /= divisor;
                bound = FloorDivide(bound, divisor);
            }

            _terms = mutable.ToFrozenDictionary();
            Bound = bound;
        }

        public BigInteger Bound { get; }

        public static LinearConstraint LessThan(
            LinearExpression left,
            LinearExpression right) =>
            FromDifference(left.Subtract(right), -1);

        public static LinearConstraint AtMost(
            LinearExpression left,
            LinearExpression right) =>
            FromDifference(left.Subtract(right), 0);

        public static LinearConstraint AtLeast(
            LinearExpression left,
            LinearExpression right) =>
            AtMost(right, left);

        public bool Implies(LinearConstraint target)
        {
            if (_terms.Count == target._terms.Count)
            {
                var sameTerms = true;
                foreach (var (atom, coefficient) in _terms)
                {
                    if (!target._terms.TryGetValue(atom, out var targetCoefficient)
                        || coefficient != targetCoefficient)
                    {
                        sameTerms = false;
                        break;
                    }
                }
                if (sameTerms)
                    return Bound <= target.Bound;
            }

            if (_terms.Count != 1)
                return false;

            var sourceTerm = _terms.Single();
            var minimum = sourceTerm.Key.Minimum;
            var maximum = sourceTerm.Key.Maximum;
            if (sourceTerm.Value > 0)
            {
                var constrainedMaximum = FloorDivide(Bound, sourceTerm.Value);
                maximum = maximum is null
                    ? constrainedMaximum
                    : BigInteger.Min(maximum.Value, constrainedMaximum);
            }
            else
            {
                var constrainedMinimum = CeilingDivide(Bound, sourceTerm.Value);
                minimum = minimum is null
                    ? constrainedMinimum
                    : BigInteger.Max(minimum.Value, constrainedMinimum);
            }

            return target.Evaluate(sourceTerm.Key, minimum, maximum)
                == ConstraintTruth.True;
        }

        public bool Contradicts(LinearConstraint other)
        {
            if (_terms.Count != other._terms.Count)
                return false;

            foreach (var (atom, coefficient) in _terms)
            {
                if (!other._terms.TryGetValue(atom, out var otherCoefficient)
                    || coefficient != -otherCoefficient)
                    return false;
            }

            return Bound + other.Bound < BigInteger.Zero;
        }

        public ConstraintTruth Evaluate() =>
            Evaluate(overriddenAtom: null, minimumOverride: null, maximumOverride: null);

        private ConstraintTruth Evaluate(
            LinearAtom? overriddenAtom,
            BigInteger? minimumOverride,
            BigInteger? maximumOverride)
        {
            BigInteger? minimum = BigInteger.Zero;
            BigInteger? maximum = BigInteger.Zero;
            foreach (var (atom, coefficient) in _terms)
            {
                var atomMinimum = overriddenAtom == atom
                    ? minimumOverride
                    : atom.Minimum;
                var atomMaximum = overriddenAtom == atom
                    ? maximumOverride
                    : atom.Maximum;
                if (coefficient >= 0)
                {
                    minimum = AddEndpoint(minimum, coefficient, atomMinimum);
                    maximum = AddEndpoint(maximum, coefficient, atomMaximum);
                }
                else
                {
                    minimum = AddEndpoint(minimum, coefficient, atomMaximum);
                    maximum = AddEndpoint(maximum, coefficient, atomMinimum);
                }
            }

            if (maximum is not null && maximum <= Bound)
                return ConstraintTruth.True;
            if (minimum is not null && minimum > Bound)
                return ConstraintTruth.False;
            return ConstraintTruth.Unknown;
        }

        private static BigInteger? AddEndpoint(
            BigInteger? accumulated,
            BigInteger coefficient,
            BigInteger? endpoint) =>
            accumulated is not null && endpoint is not null
                ? accumulated.Value + coefficient * endpoint.Value
                : null;

        private static LinearConstraint FromDifference(
            LinearExpression difference,
            BigInteger bound) =>
            new(difference.Terms, bound - difference.ConstantTerm);

        private static BigInteger FloorDivide(
            BigInteger dividend,
            BigInteger divisor)
        {
            var quotient = BigInteger.DivRem(dividend, divisor, out var remainder);
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private static BigInteger CeilingDivide(
            BigInteger dividend,
            BigInteger divisor)
        {
            var quotient = BigInteger.DivRem(dividend, divisor, out var remainder);
            return remainder != 0 && dividend.Sign == divisor.Sign
                ? quotient + 1
                : quotient;
        }
    }
}
