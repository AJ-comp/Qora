using System.Text;
using System.Text.Json;
using Qora;
using Qora.Compiler;
using Qora.Ir.Mir.Analysis;

// Two modes:
//   qora --json [file]   parse [file] (or stdin when no path) and print ONE line of JSON — the machine
//                       contract the VS Code extension consumes for squiggles + transpile.
//                       `--stages` additionally includes the compilation stages and analyses —
//                       kept out of the default reply because diagnostics run on every keystroke.
//   qora                 parse a built-in sample and pretty-print the result (console demo).
if (args.Contains("--json"))
{
    // EVERYTHING is inside the try — reading a bad file path, a stdin/encoding IOException, or an engine
    // bug all still emit ONE line of valid JSON, so the extension never sees a half-written / empty reply.
    try
    {
        Console.OutputEncoding = Encoding.UTF8;

        // `--base-dir <dir>` supplies the import-resolution root for stdin input (the extension's
        // live-diagnostics path). Its VALUE must not be mistaken for the source-file argument, so pull
        // the pair out before finding the first non-flag argument.
        string? baseDir = null;
        string? stdinSourcePath = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--base-dir" && i + 1 < args.Length) baseDir = args[++i];
            else if (args[i] == "--source-path" && i + 1 < args.Length)
                stdinSourcePath = Path.GetFullPath(args[++i]);
            else positional.Add(args[i]);
        }

        // The first non-flag argument, if any, is a source file; otherwise read the whole of stdin.
        // Read stdin through an explicit UTF-8 reader so non-ASCII source (e.g. Korean comments) survives
        // the pipe regardless of the console's default input encoding.
        var path = positional.FirstOrDefault(a => !a.StartsWith("--"));
        string source;
        string? sourcePath = null;
        if (path is not null)
        {
            sourcePath = Path.GetFullPath(path);
            source = File.ReadAllText(sourcePath);
            baseDir ??= Path.GetDirectoryName(sourcePath); // a file's imports resolve next to it
        }
        else
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            source = stdin.ReadToEnd();
            sourcePath = stdinSourcePath;
        }

        var compilation = QoraCompiler.Compile(
            source,
            new CompilationOptions(baseDir, sourcePath));
        var resolved = compilation.Hir.Resolved;
        var resolvedValidation = compilation.Hir.ResolvedValidation;
        var mir = compilation.Mir;
        var openQasm = compilation.Targets.OpenQasm;
        if (args.Contains("--stages"))
        {
            // Each view consumes one exact, revision-bound stage. A missing stage stays empty instead of
            // borrowing a program or semantic model from another HIR generation.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = compilation.Succeeded,
                qasm = openQasm?.Text ?? string.Empty,
                errors = compilation.Diagnostics.Select(d => new
                {
                    message = d.Error.Message,
                    code = d.Error.Code,
                    start = d.Error.Start,
                    end = d.Error.End,
                    document = DiagnosticSource(d)?.ToString(),
                    path = DiagnosticSource(d) is { } document
                        ? compilation.Sources.RequireDocument(document).Path
                        : null,
                }),
                ast = compilation.Sources.EntrySyntax.AstText,
                ir = resolved is null
                    ? string.Empty
                    : Qora.Ir.HirPrinter.Print(resolved.Program),
                symbols = resolvedValidation is null
                    ? string.Empty
                    : CompilationReports.FormatSymbols(resolvedValidation),
                mir = mir is null
                    ? string.Empty
                    : Qora.Ir.Mir.MirPrinter.Print(mir.Program),
                mirEffects = mir is null
                    ? string.Empty
                    : FormatMirEffects(mir.Analyses.Effects),
            }));
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = compilation.Succeeded,
                qasm = openQasm?.Text ?? string.Empty,
                errors = compilation.Diagnostics.Select(d => new
                {
                    message = d.Error.Message,
                    code = d.Error.Code,
                    start = d.Error.Start,
                    end = d.Error.End,
                    document = DiagnosticSource(d)?.ToString(),
                    path = DiagnosticSource(d) is { } document
                        ? compilation.Sources.RequireDocument(document).Path
                        : null,
                }),
            }));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = false,
            qasm = string.Empty,
            errors = new[] { new { message = "internal error: " + ex.Message, code = "QORA0000", start = -1, end = -1 } },
        }));
    }

    return;
}

// Quick CLI runner for Qora — parses a sample and prints tokens, AST, and emitted OpenQASM.
const string sample = """
    operation Prepare(q: Qubit[]) {
        H(q[0]);
        CNOT(q[0], q[1]);
        Rz(pi/4, q[1]);
        Ry(0.5, q[0]);
    }

    operation Main() {
        use q = Qubit[2];
        Prepare(q);
        for i in 0..q.Count - 1 {
            Rx(pi/2, q[i]);
        }
        var r: bit = M(q[0]);
        if (r == 1) {
            X(q[1]);
        }
    }
    """;

var result = QoraCompiler.Compile(sample);

Console.WriteLine($"=== Qora v{Qora.Ir.QoraVersion.Value} (console) ===\n");
Console.WriteLine($"parse: {(result.Succeeded ? "ACCEPTED" : "REJECTED")}\n");

if (result.Succeeded)
{
    Console.WriteLine("OpenQASM 3.0:");
    Console.WriteLine(result.Targets.OpenQasm?.Text ?? string.Empty);
}
else
{
    Console.WriteLine("errors:");
    foreach (var diagnostic in result.Diagnostics)
        Console.WriteLine("  " + diagnostic.Error);
}

static string FormatMirEffects(MirEffectSnapshot effects)
{
    var text = new StringBuilder();
    text.AppendLine($"mir effects snapshot {effects.SnapshotId}");

    foreach (var summary in effects.CallableSummaries)
    {
        var formalEffects = summary.FormalQubits.Count == 0
            ? "none"
            : string.Join(
                ", ",
                summary.FormalQubits.Select(effect =>
                    $"&{effect.Qubit}={effect.Flags}"));
        text.AppendLine(
            $"callable @{summary.Callable}: qubits [{formalEffects}], "
            + $"irreversible={summary.IsIrreversible}, ownership-transfer={summary.TransfersOwnership}");
    }

    foreach (var effect in effects.Effects)
    {
        var target = effect.Target is null
            ? effect.Kind.ToString().ToLowerInvariant()
            : effect.Target.DisplayName;
        var qubits = effect.Qubits.Count == 0
            ? "none"
            : string.Join(
                ", ",
                effect.Qubits.Select(qubit =>
                    $"{qubit.Access}={qubit.Flags}"));
        var witnesses = effect.ClassicalWitnesses.Count == 0
            ? "none"
            : string.Join(
                ", ",
                effect.ClassicalWitnesses.Select(witness =>
                    $"%{witness.Value}:{witness.Role}"));

        text.AppendLine(
            $"@{effect.Site.Callable}/{effect.Site.Block}/%{effect.Site.Instruction}: "
            + $"{target}; qubits [{qubits}]; witnesses [{witnesses}]");
    }

    return text.ToString().TrimEnd();
}

static SourceDocumentRef? DiagnosticSource(CompilationDiagnostic diagnostic) =>
    diagnostic.Error.Span?.Document
    ?? (diagnostic.Origin is DiagnosticOrigin.Source source
        ? source.Document
        : null);
