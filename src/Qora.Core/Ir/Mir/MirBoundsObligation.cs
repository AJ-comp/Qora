namespace Qora.Ir.Mir;

/// <summary>
/// One bounds proof that the common front end could neither prove safe nor prove invalid. The MIR
/// retains only the target-independent diagnostic fact that current backends consume. A backend which
/// can perform runtime bounds checks may accept this fact instead of rejecting the compilation.
/// </summary>
public sealed record MirBoundsObligation(
    string Operation,
    string Aggregate,
    string Index,
    string? LoopBound,
    SourceSpan? Span);
