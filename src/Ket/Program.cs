using Ket;

// Quick CLI runner for Ket — parses a sample and prints tokens, AST, and emitted OpenQASM.
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

var result = KetParser.Parse(sample);

Console.WriteLine("=== Ket v0.7 (console) ===\n");
Console.WriteLine($"parse: {(result.Success ? "ACCEPTED" : "REJECTED")}\n");

Console.WriteLine("tokens:");
Console.WriteLine("  " + string.Join("  ", result.Tokens.Select(t => $"{t.Text}<{t.Type}>")));
Console.WriteLine();

if (result.Success)
{
    Console.WriteLine("AST (clean, MeaningUnit-tagged):");
    Console.WriteLine(result.AstText);
    Console.WriteLine();
    Console.WriteLine("OpenQASM 3.0:");
    Console.WriteLine(result.Qasm);
}
else
{
    Console.WriteLine("errors:");
    foreach (var e in result.Errors) Console.WriteLine("  " + e);
}
