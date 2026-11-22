using Qora.Compiler;

namespace Qora.Ir.Passes;

/// <summary>Proven bounds for array result widths. A width is exact only when all reachable returns agree.</summary>
internal sealed class HirArrayWidths(
    HirSemanticModel model,
    HirStructuralIndex structure,
    IReadOnlyDictionary<HirNodeId, HirCallable> callables)
{
    // A return can select a concrete allocation, one of the caller's unsized array
    // parameters, or either on different branches. Do not erase the symbolic alternatives:
    // First(Copy(parameter)) imposes the same minimum width as First(parameter).
    internal sealed record Range(int? Minimum, int? Maximum, IReadOnlyList<HirNodeId> Parameters)
    {
        internal int? Exact => Parameters.Count == 0 && Minimum == Maximum ? Minimum : null;
        internal static Range? Of(int? size) => size is int n ? new(n, n, []) : null;
        internal string Key => $"{Minimum}:{Maximum}:{string.Join('.', Parameters.Select(p => p.Value))}";
        internal Range Union(Range other) => new(
            Minimum is int a && other.Minimum is int b ? Math.Min(a, b) : Minimum ?? other.Minimum,
            Maximum is int c && other.Maximum is int d ? Math.Max(c, d) : Maximum ?? other.Maximum,
            Parameters.Concat(other.Parameters).Distinct().OrderBy(p => p.Value).ToArray());
    }

    private readonly Dictionary<(HirNodeId, string), Range?> _results = new();

    internal Range? Of(HirExpression expr, Func<HirNameExpression, Range?>? nameWidth = null)
    {
        if (expr is HirArrayLiteralExpression literal) return Range.Of(literal.Elements.Count);
        if (expr is HirNameExpression name) return nameWidth is null ? Local(name, null) : nameWidth(name);
        if (expr is not HirCallExpression call)
            return Range.Of(ExpressionTypes.TypeOf(expr, model, callables)?.KnownLength);
        if (call.CalleeId is not { } id || !callables.TryGetValue(id, out var callee) ||
            callee.ReturnType is not { IsArray: true } || call.Arguments.Count != callee.Parameters.Count)
            return null;

        var parameters = new Dictionary<HirNodeId, Range?>();
        for (var i = 0; i < callee.Parameters.Count; i++)
            if (callee.Parameters[i].IsArray)
                parameters[callee.Parameters[i].Id] = Of(call.Arguments[i].Expression, nameWidth);
        var key = (id, string.Join(",", parameters.Values.Select(p => p?.Key ?? "?")));
        if (_results.TryGetValue(key, out var saved)) return saved;
        _results[key] = null; // Cyclic, invalid HIR can reach source diagnostics before validation is sealed.

        // Specialization fixes bit[]/Qubit[] parameter widths before MIR folds branches.
        // Evaluate those same conditions here so a downstream consumer gets the actual
        // result width. Ordinary array parameters and runtime conditions stay symbolic.
        var integers = new Dictionary<HirNodeId, Bound?>();
        var booleans = new Dictionary<HirNodeId, bool?>();
        Bound? IntegerLeaf(HirExpression leaf)
        {
            if (leaf is HirMemberAccessExpression { Receiver: HirNameExpression array, MemberName: "Count" } &&
                model.FindReferencedSymbol(array.Id) is { Kind: SymbolKind.Parameter, Type: QType.Bit or QType.Qubit,
                    IsArray: true, DeclarationNodeId: { } parameterId } &&
                parameters.TryGetValue(parameterId, out var range) && range?.Exact is int size)
                return new BoundNum(size);
            if (leaf is not HirNameExpression name ||
                model.FindReferencedSymbol(name.Id) is not { IsConst: true, Type: QType.Int,
                    FoldedBound: not BoundNum, DeclarationNodeId: { } id }) return null;
            if (integers.TryGetValue(id, out var known)) return known;
            return integers[id] = structure.FindNode(id) is HirVariableDeclarationStatement local
                ? BoundFolder.Fold(local.Value, null, model, IntegerLeaf) : null;
        }
        bool? BooleanLeaf(HirNameExpression name)
        {
            if (model.FindReferencedSymbol(name.Id) is not { IsConst: true, Type: QType.Bool,
                DeclarationNodeId: { } id }) return null;
            if (booleans.TryGetValue(id, out var known)) return known;
            return booleans[id] = structure.FindNode(id) is HirVariableDeclarationStatement local
                ? Condition(local.Value) : null;
        }
        bool? Condition(HirExpression expression) =>
            BooleanFolder.Fold(expression, null, model, IntegerLeaf, BooleanLeaf);

        Range? result = null;
        bool unknown = false;
        bool Visit(HirBlock block)
        {
            foreach (var statement in block)
                switch (statement)
                {
                    case HirReturnStatement ret:
                        if (ret.Value is null || Of(ret.Value, n => Local(n, parameters)) is not { } width)
                            unknown = true;
                        else
                            result = result?.Union(width) ?? width;
                        return true;
                    case HirIfStatement branch:
                        if (Condition(branch.Condition) is bool condition)
                        {
                            if (Visit(condition ? branch.Then : branch.Else)) return true;
                            break;
                        }
                        var thenReturns = Visit(branch.Then);
                        var elseReturns = Visit(branch.Else);
                        if (thenReturns && elseReturns) return true;
                        break;
                    case HirRepeatStatement loop:
                        if (Visit(loop.Body)) return true;
                        break;
                    case HirWhileStatement loop:
                        if (Condition(loop.Condition) != false)
                            Visit(loop.Body);
                        break;
                    case HirForStatement loop:
                        Visit(loop.Body);
                        break;
                }
            return false;
        }

        Visit(callee.Body);
        return _results[key] = unknown ? null : result;
    }

    private Range? Local(HirNameExpression name, IReadOnlyDictionary<HirNodeId, Range?>? parameters)
    {
        var symbol = model.FindReferencedSymbol(name.Id);
        if (symbol?.DeclarationNodeId is not { } id) return null;
        if (parameters is not null && parameters.TryGetValue(id, out var width)) return width;
        if ((symbol.ArrayLength ?? symbol.RegisterSize) is int size) return Range.Of(size);
        if (symbol.Kind == SymbolKind.Parameter && symbol.IsArray) return new Range(null, null, [id]);
        return structure.FindNode(id) is HirVariableDeclarationStatement local
            ? Of(local.Value, n => Local(n, parameters))
            : null;
    }
}
