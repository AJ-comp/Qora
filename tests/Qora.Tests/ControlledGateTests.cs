using Qora.Ir;

namespace Qora.Tests;

public sealed class ControlledGateTests
{
    // Independent expectations: do not derive spellings or signatures from the compiler under test.
    public static TheoryData<string, string, string, bool> UnitaryGates => new()
    {
        { "H",    "h",    "a",       false },
        { "X",    "x",    "a",       false },
        { "Y",    "y",    "a",       false },
        { "Z",    "z",    "a",       false },
        { "S",    "s",    "a",       false },
        { "Sdg",  "sdg",  "a",       false },
        { "T",    "t",    "a",       false },
        { "Tdg",  "tdg",  "a",       false },
        { "CNOT", "cx",   "a, b",    false },
        { "CX",   "cx",   "a, b",    false },
        { "CY",   "cy",   "a, b",    false },
        { "CZ",   "cz",   "a, b",    false },
        { "SWAP", "swap", "a, b",    false },
        { "CCX",  "ccx",  "a, b, d", false },
        { "P",    "p",    "a",       true },
        { "Rx",   "rx",   "a",       true },
        { "Ry",   "ry",   "a",       true },
        { "Rz",   "rz",   "a",       true },
    };

    [Fact]
    public void CasesCoverEveryRegisteredUnitaryGate()
    {
        var registered = QoraGates.Gates.Where(pair => pair.Value.Unitary).Select(pair => pair.Key);
        var covered = UnitaryGates.Select(row => (string)row[0]);
        Assert.Equal(registered.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(UnitaryGates))]
    public void EmitsPlainAndControlledGateWithExactAngleAndOperandOrder(
        string gate, string qasmName, string operands, bool takesAngle)
    {
        var angle = takesAngle ? "theta, " : "";
        var source = ProgramWith($"""
            {gate}({angle}{operands});
            Controlled {gate}({angle}c, {operands});
            """);
        var compilation = Compiler.Compile(source);
        Assert.True(compilation.Succeeded, string.Join(" | ", compilation.Diagnostics));

        var lines = compilation.Targets.OpenQasm!.Text.Split('\n', StringSplitOptions.TrimEntries);
        var qasmGate = takesAngle ? $"{qasmName}(theta)" : qasmName;
        Assert.Contains($"{qasmGate} {operands};", lines);
        Assert.Contains($"ctrl @ {qasmGate} c, {operands};", lines);
    }

    [Theory]
    [MemberData(nameof(UnitaryGates))]
    public void ControlledRequiresOneAdditionalQubit(
        string gate, string _, string operands, bool takesAngle)
    {
        var angle = takesAngle ? "theta, " : "";
        Compiler.RejectsExactly(ProgramWith($"Controlled {gate}({angle}{operands});"), "QSEM006");
        Compiler.RejectsExactly(ProgramWith($"Controlled {gate}({angle}1, {operands});"), "QSEM006");
    }

    [Theory]
    [InlineData("P(a);", "QSEM006")]
    [InlineData("P(true, a);", "QSEM006")]
    [InlineData("Controlled P(a, c, b);", "QSEM006")]
    [InlineData("Controlled P(theta, c, c);", "QSEM014")]
    [InlineData("Controlled Reset(c, a);", "QSEM003")]
    [InlineData("Controlled ResetAll(c, a);", "QSEM003")]
    [InlineData("Controlled M(c, a);", "QSEM004")]
    public void RejectsInvalidPhaseCallsAndControlledNonunitaryInstructions(string call, string code) =>
        Compiler.RejectsExactly(ProgramWith(call), code);

    [Fact]
    public void ControlledDoesNotAcceptAUserOperation() =>
        Compiler.RejectsExactly("operation Custom(q: Qubit) { X(q); }\n" +
            ProgramWith("Controlled Custom(c, a);"), "QSEM002");

    private static string ProgramWith(string statements) => $$"""
        operation Apply(theta: angle, c: Qubit, a: Qubit, b: Qubit, d: Qubit) {
            {{statements}}
        }
        operation Main() {
            use q = Qubit[4];
            Apply(pi/7, q[0], q[1], q[2], q[3]);
        }
        """;
}
