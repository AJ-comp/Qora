namespace Qora.Ir.Passes;

/// <summary>
/// Revision-aware semantic queries over one HIR generation. An exact semantic snapshot can answer these
/// directly; a later derived generation resolves its node Ids through the compilation's HIR lineage
/// before consulting <see cref="SourceModel"/>.
///
/// Passes consume this abstraction instead of mutating <see cref="HirSemanticModel"/> with copy lineage.
/// Consequently one semantic model remains bound to exactly one validated HIR snapshot while later HIR
/// generations remain queryable.
/// </summary>
internal interface IHirSemanticContext
{
    /// <summary>The immutable semantic facts produced for the validated ancestor HIR snapshot.</summary>
    HirSemanticModel SourceModel { get; }

    Symbol? FindSymbol(HirNodeId nodeId);

    Scope? FindRootScope(HirNodeId callableId);

    Scope? FindScope(HirScopeSite site);
}
