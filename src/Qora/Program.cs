using System.Text;
using System.Text.Json;
using Qora;

// Two modes:
//   qora --json [file]   parse [file] (or stdin when no path) and print ONE line of JSON — the machine
//                       contract the VS Code extension consumes for squiggles + transpile.
//                       `--stages` additionally includes the compilation stages (ast / ir / irInverse) —
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
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--base-dir" && i + 1 < args.Length) baseDir = args[++i];
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
        }

        var r = QoraParser.Parse(source, baseDir, sourcePath);
        if (args.Contains("--stages"))
        {
            // stage texts are present even when semantic errors block emission (qasm is empty then):
            // the stages view teaches best when it can show WHERE in the pipeline things stopped.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = r.Success,
                qasm = r.Qasm,
                errors = r.Errors.Select(e => new { message = e.Message, code = e.Code, start = e.Start, end = e.End }),
                ast = r.AstText,
                ir = Qora.Ir.IrPrinter.Print(r.Ir),
                irInverse = Qora.Ir.IrPrinter.PrintInverses(r.Ir),
            }));
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = r.Success,
                qasm = r.Qasm,
                errors = r.Errors.Select(e => new { message = e.Message, code = e.Code, start = e.Start, end = e.End }),
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
    operation Prepare(Qubit[2] q) {
        H(q[0]);
        CNOT(q[0], q[1]);
        Rz(pi/4, q[1]);
        Ry(0.5, q[0]);
    }

    operation Main() {
        use q = Qubit[2];
        Prepare(q);
        for i in 0..1 {
            Rx(pi/2, q[i]);
        }
        bit r = M(q[0]);
        if (r == 1) {
            X(q[1]);
        }
    }
    """;

var result = QoraParser.Parse(sample);

Console.WriteLine($"=== Qora v{Qora.Ir.QoraVersion.Value} (console) ===\n");
Console.WriteLine($"parse: {(result.Success ? "ACCEPTED" : "REJECTED")}\n");

if (result.Success)
{
    Console.WriteLine("OpenQASM 3.0:");
    Console.WriteLine(result.Qasm);
}
else
{
    Console.WriteLine("errors:");
    foreach (var e in result.Errors) Console.WriteLine("  " + e);
}
