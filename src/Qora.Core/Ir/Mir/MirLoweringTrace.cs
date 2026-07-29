using Qora.Ir.Passes;

namespace Qora.Ir.Mir;

/// <summary>
/// Receives exact HIR-to-MIR relationships while lowering creates them.
///
/// The compiler never stores these relationships in MIR. A caller which needs source-level queries,
/// such as a language service, may collect them for the duration of one compile. Ordinary compilation
/// passes no sink and therefore allocates no cross-stage semantic index.
/// </summary>
public interface IMirLoweringTraceSink
{
    void LinkCallable(
        HirNodeId declaration,
        SymbolId symbol,
        MirCallableId callable);

    void LinkValue(
        SymbolId symbol,
        MirCallableId callable,
        MirValueId value);

    void LinkStorage(
        SymbolId symbol,
        MirCallableId callable,
        MirStorageId storage);

    void LinkQubit(
        SymbolId symbol,
        MirCallableId callable,
        MirQubitKey qubit);

    void MarkUnreachable(SymbolId symbol);
}
