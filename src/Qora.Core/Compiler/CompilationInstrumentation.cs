using Qora.Ir.Mir;

namespace Qora.Compiler;

/// <summary>
/// Optional, compile-call-scoped observers. Instrumentation does not change compilation semantics and
/// is never retained by the immutable <see cref="Compilation"/> snapshot.
/// </summary>
public sealed class CompilationInstrumentation
{
    public CompilationInstrumentation(
        IMirLoweringTraceSink? mirLowering = null)
    {
        MirLowering = mirLowering;
    }

    public IMirLoweringTraceSink? MirLowering { get; }
}
