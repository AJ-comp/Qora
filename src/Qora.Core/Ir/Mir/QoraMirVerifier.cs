using System.Text;
using Qora.Ir.Mir.Analysis;

namespace Qora.Ir.Mir;

internal sealed record MirVerificationError(
    string Code,
    string Message,
    MirCallableId? Callable = null,
    MirBlockId? Block = null,
    MirInstructionId? Instruction = null,
    MirOrigin? Origin = null)
{
    public override string ToString()
    {
        var where = new List<string>();
        if (Callable is { } callable) where.Add(callable.ToString());
        if (Block is { } block) where.Add(block.ToString());
        if (Instruction is { } instruction) where.Add(instruction.ToString());
        return where.Count == 0
            ? $"{Code}: {Message}"
            : $"{Code} [{string.Join("/", where)}]: {Message}";
    }
}

/// <summary>
/// Structural verification at every MIR snapshot publication boundary. It verifies SSA use integrity,
/// dominance, instruction typing, call contracts, and classical-array /
/// versioned-qubit references. A failed verification is a compiler defect, not a source-language error.
/// </summary>
internal static partial class QoraMirVerifier
{
    public static IReadOnlyList<MirVerificationError> Verify(MirProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new Verifier(program).Run();
    }

    public static void VerifyOrThrow(MirProgram program)
    {
        var errors = Verify(program);
        if (errors.Count == 0) return;

        var message = new StringBuilder("QINTERNAL: invalid Qora MIR");
        foreach (var error in errors) message.AppendLine().Append("  ").Append(error);
        throw new InvalidOperationException(message.ToString());
    }

    private sealed partial class Verifier
    {
        private readonly MirProgram _program;
        private readonly List<MirVerificationError> _errors = new();

        public Verifier(MirProgram program) => _program = program;

        public IReadOnlyList<MirVerificationError> Run()
        {
            foreach (var callable in _program.Callables.OrderBy(callable => callable.Id.Value))
                VerifyCallable(callable);

            // Interprocedural qubit write contracts are checked only after every callable has passed
            // local structure, arity, typing, CFG, and SSA verification. The shared effect query does
            // not read QubitResults, so the declarations are compared against an independent semantic
            // source of truth rather than validating themselves.
            if (_errors.Count == 0)
                VerifyQubitWriteContracts();

            return _errors;
        }

        private void VerifyCallable(MirCallable callable)
        {
            var callableErrorStart = _errors.Count;
            VerifyQubitSeeds(callable);

            var controlFlow = MirControlFlowAnalysis.AnalyzeUnchecked(callable);
            VerifyQubitFlow(callable, controlFlow);
            VerifyBlockContents(callable, controlFlow);

            if (_errors.Count == callableErrorStart)
                VerifyGraphContracts(callable, controlFlow);
        }

        private void Add(
            string code,
            string message) =>
            AddCore(code, message, null, null, null, null);

        private void Add(
            string code,
            string message,
            MirCallable callable) =>
            AddCore(code, message, callable, null, null, callable.Origin);

        private void Add(
            string code,
            string message,
            MirCallable callable,
            MirBlock block) =>
            AddCore(code, message, callable, block, null, block.Origin);

        private void AddAtInstruction(
            string code,
            string message,
            MirCallable callable,
            MirInstruction instruction)
        {
            var block = callable.RequireInstructionLocation(instruction.Id).Block;
            AddCore(code, message, callable, block, instruction, instruction.Origin);
        }

        private void AddCore(
            string code,
            string message,
            MirCallable? callable,
            MirBlock? block,
            MirInstruction? instruction,
            MirOrigin? source) =>
            _errors.Add(new MirVerificationError(
                code,
                message,
                callable?.Id,
                block?.Id,
                instruction?.Id,
                source));

        private void AddAtTerminator(
            string code,
            string message,
            MirCallable callable,
            MirBlock block) =>
            AddCore(
                code,
                message,
                callable,
                block,
                null,
                block.Terminator.Origin);

        private void AddAtQubitPhi(
            string code,
            string message,
            MirCallable callable,
            MirBlock block,
            MirQubitPhi phi) =>
            AddCore(
                code,
                message,
                callable,
                block,
                null,
                phi.Origin);
    }
}
