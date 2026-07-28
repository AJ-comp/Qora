using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>An exact-snapshot semantic context for focused pass tests.</summary>
internal sealed class ExactHirSemanticContext(
    HirSemanticModel sourceModel) : IHirSemanticContext
{
    public HirSemanticModel SourceModel { get; } =
        sourceModel ?? throw new ArgumentNullException(nameof(sourceModel));

    public Symbol? FindSymbol(HirNodeId nodeId) =>
        SourceModel.FindSymbol(nodeId);

    public Scope? FindRootScope(HirNodeId callableId) =>
        SourceModel.FindRootScope(callableId);

    public Scope? FindScope(HirScopeSite site) =>
        SourceModel.FindScope(site);
}
