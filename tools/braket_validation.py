# -*- coding: utf-8 -*-
"""Release-gate validation: compiled Qora entry return values on Braket's LocalSimulator.

Requires: python -m pip install -r tools/requirements.txt  (Python 3.11-3.13) and a Debug build of
src/Qora. Complements tools/qiskit_validation.py: Qiskit's importer covers the FLAT subset, Braket's
local simulator covers the rich target. Only explicit entry returns are observable results. stdgates.inc in
this directory is resolved from the cwd, so this script chdirs here first.
"""
import os
os.chdir(os.path.dirname(os.path.abspath(__file__)))
import io, json, subprocess, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "vscode", "runner"))
from braket_results import entry_result_from_task, validate_execution_request

DLL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Qora", "bin", "Debug", "net10.0", "Qora.dll")

from braket.devices import LocalSimulator
from braket.ir.openqasm import Program

device = LocalSimulator()
PASS = FAIL = 0

def compile_qora(src):
    p = subprocess.run(["dotnet", DLL, "--json"], input=src.encode("utf-8"), capture_output=True)
    r = json.loads(p.stdout.decode("utf-8"))
    assert r["success"], r["errors"]
    return validate_execution_request(r)

def check(name, cond, detail=""):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  ok  {name}", flush=True)
    else:    FAIL += 1; print(f"  FAIL {name}  {detail}", flush=True)

def load_and_run(name, src, expect_keys=None, subset=False, shots=1000):
    try:
        request = compile_qora(src)
    except (AssertionError, ValueError) as e:
        check(f"{name} (compile)", False, e); return
    try:
        result = device.run(Program(source=request["qasm"]), shots=shots).result()
        returned = entry_result_from_task(result, request["entryResult"], shots)["result"]
    except Exception as e:
        check(f"{name} (braket run)", False, f"{type(e).__name__}: {str(e)[:140]}"); return
    if expect_keys is None:
        check(f"{name} (void, {shots} completed shots)", returned == {"type": "void"}); return
    got = set(returned.get("values", []))
    ok = got <= set(expect_keys) if subset else got == set(expect_keys)
    check(f"{name} (run: {sorted(got)})", ok, f"expected {sorted(expect_keys)}")

print("== Qora entry return values on Braket LocalSimulator ==")

load_and_run("B1 def Bell (서브루틴)", """
operation Bell(q: Qubit[]) {
    H(q[0]);
    CNOT(q[0], q[1]);
}
operation Main(): int {
    use q = Qubit[2];
    Bell(q);
    var r0: bit = M(q[0]);
    var r1: bit = M(q[1]);
    var result: bit[] = [r0, r1];
    return AsInt(result);
}""", {"0", "3"})

load_and_run("B2 int/const 선언 + 산술 각도", """
operation Main(): bit {
    use q = Qubit[1];
    const k: int = 2;
    H(q[0]);
    Rz(pi/k, q[0]);
    H(q[0]);
    var r: bit = M(q[0]);
    return r;
}""", {0, 1}, subset=True)

load_and_run("B3 for 루프 + 변수 인덱스 q[i]", """
operation Main(): int {
    use q = Qubit[2];
    for i in 0..q.Count - 1 {
        X(q[i]);
    }
    var r0: bit = M(q[0]);
    var r1: bit = M(q[1]);
    var result: bit[] = [r0, r1];
    return AsInt(result);
}""", {"3"})

load_and_run("B4 네임스페이스 맹글명 def", """
namespace MyLib {
    operation Bell(q: Qubit[]) {
        H(q[0]);
        CNOT(q[0], q[1]);
    }
}
operation Main(): int {
    use q = Qubit[2];
    MyLib.Bell(q);
    var r0: bit = M(q[0]);
    var r1: bit = M(q[1]);
    var result: bit[] = [r0, r1];
    return AsInt(result);
}""", {"0", "3"})

load_and_run("B6 측정→if 피드백", """
operation Main(): int {
    use q = Qubit[2];
    X(q[0]);
    var r: bit = M(q[0]);
    if (r == 1) {
        X(q[1]);
    }
    var r1: bit = M(q[1]);
    var result: bit[] = [r, r1];
    return AsInt(result);
}""", {"3"})

load_and_run("B7 while (측정 의존 동적 루프!)", """
operation Main(): bit {
    use q = Qubit[1];
    var r: bit = M(q[0]);
    while (r == 0) {
        H(q[0]);
        r = M(q[0]);
    }
    X(q[0]);
    return r;
}""", {1})  # The loop's stored measurement is 1; the subsequent X must not change the return value.

load_and_run("B8 demo.qor as-is (for+const+measure+if)",
    open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "vscode", "examples", "demo.qor"), encoding="utf-8").read(), {0, 1}, subset=True)

# B9/B10 — a WHOLE bit[] register is a container, not a number (QSEM036). These pin the two things a
# register may still do, END TO END, with an outcome that DISCRIMINATES a right answer from a wrong one.
#
# Reading order: f[0] is the register's most significant bit, so f = "100" reads as 4.
# Each entry explicitly returns AsInt(f); internal measurement registers are not implicit outputs.

load_and_run("B9 AsInt -> uint[N] (무부호 폭 캐스트)", """
operation Main(): int {
    use q = Qubit[3];
    var f: bit[] = new bit[3];
    X(q[0]);
    f[0] = M(q[0]);
    f[1] = M(q[1]);
    if (AsInt(f) == 4) {
        X(q[2]);
    }
    f[2] = M(q[2]);
    return AsInt(f);
}""", {"5"})   # 4 would mean the cast read something other than 4 (a signed read gives -4).

load_and_run("B11 조기 return (꼬리를 else로)", """
function sign(x: int): int {
    if (x == 0) { return 7; }
    return 4;
}
operation Main(): bit {
    use q = Qubit[1];
    var k: int = sign(0);
    if (k == 7) {
        X(q[0]);
    }
    var m: bit = M(q[0]);
    return m;
}""", {1})   # 0 would mean the skipped tail ran and overwrote the early return

load_and_run("B12 루프 안 return (첫 일치가 이김)", """
function first(n: int): int {
    for i in 0..4 {
        if (i >= n) { return i; }
    }
    return 9;
}
operation Main(): bit {
    use q = Qubit[1];
    var k: int = first(2);
    if (k == 2) {
        X(q[0]);
    }
    var m: bit = M(q[0]);
    return m;
}""", {1})   # 0 would mean the LAST match (4) or the post-loop fallback (9) won

load_and_run("B10 같은 폭 레지스터 직접 비교", """
operation Main(): int {
    use q = Qubit[3];
    var f: bit[] = new bit[3];
    var g: bit[] = new bit[3];
    X(q[0]);
    f[0] = M(q[0]);
    f[1] = M(q[1]);
    g[0] = 1;
    if (f == g) {
        X(q[2]);
    }
    f[2] = M(q[2]);
    return AsInt(f);
}""", {"5"})   # f = "100" = g; 4 would mean the registers compared unequal

load_and_run("B13 inferred function return types", """
function phase(): angle { return pi; }
function real(): float { return pi; }
function flag(): bit { return 1; }
function two(): int { return 2; }
operation Main(): int {
    use q = Qubit[4];
    var a = phase();
    var f = real();
    var b = flag();
    var n = two();
    Rx(a, q[0]);
    Rx(f, q[1]);
    if (b == 1) { X(q[2]); }
    if (n == 2) { X(q[3]); }
    var m0: bit = M(q[0]);
    var m1: bit = M(q[1]);
    var m2: bit = M(q[2]);
    var m3: bit = M(q[3]);
    var result: bit[] = [m0, m1, m2, m3];
    return AsInt(result);
}""", {"15"})

load_and_run("B14 bit[] function expression-call specialization", """
function CountBits(flags: bit[]): int {
    return AsInt(flags);
}
operation Main(): bit {
    use q = Qubit[1];
    var flags: bit[] = new bit[2];
    flags[0] = 1;
    flags[1] = 0;
    var n: int = CountBits(flags);
    if (n == 2) { X(q[0]); }
    var m: bit = M(q[0]);
    return m;
}""", {1})

load_and_run("B15 namespace function resolution", """
namespace L {
    function two(): int { return 2; }
    operation Mark(q: Qubit) {
        if (two() == 2) { X(q); }
    }
}
operation Main(): bit {
    use q = Qubit[1];
    var n: int = L.two();
    if (n == 2) { L.Mark(q[0]); }
    var m: bit = M(q[0]);
    return m;
}""", {1})

load_and_run("R1 stored measurement survives later X", """
operation Main(): bit {
    use q = Qubit[1];
    var r: bit = M(q[0]);
    X(q[0]);
    return r;
}""", {0}, shots=16)

load_and_run("R2 overwritten measurement returns latest value", """
operation Main(): bit {
    use q = Qubit[1];
    var r: bit = M(q[0]);
    X(q[0]);
    r = M(q[0]);
    return r;
}""", {1}, shots=16)

load_and_run("R3 helper returns are not additional outputs", """
operation Read(q: Qubit): bit { return M(q); }
operation Main(): bit {
    use q = Qubit[1];
    var first: bit = Read(q[0]);
    X(q[0]);
    var second: bit = Read(q[0]);
    return second;
}""", {1}, shots=16)

load_and_run("R4 unvisited internal measurement is not an output", """
operation Main(): bit {
    use q = Qubit[2];
    var flag: bit = M(q[0]);
    if (flag == 1) { var unused: bit = M(q[1]); }
    return flag;
}""", {0}, shots=16)

load_and_run("R5 deterministic repeated measurements", """
operation Main(): bit {
    use q = Qubit[1];
    var r: bit = 0;
    for i in 0..1 { X(q[0]); r = M(q[0]); }
    return r;
}""", {0}, shots=16)

load_and_run("R6 entry early return", """
operation Main(): int {
    use q = Qubit[1];
    var r: bit = M(q[0]);
    if (r == 0) { return 7; }
    return 8;
}""", {"7"}, shots=16)

load_and_run("R7 integer beyond JS safe precision, no qubits", """
operation Main(): int { return 9007199254740993; }
""", {"9007199254740993"}, shots=4)

load_and_run("R8 negative integer, no measurements", """
operation Main(): int { return -9007199254740993; }
""", {"-9007199254740993"}, shots=4)

load_and_run("R9 float return", """
operation Main(): float { return 1.25; }
""", {1.25}, shots=4)

load_and_run("R10 angle return in radians", """
operation Main(): angle { return pi / 2; }
""", {1.5707963267948966}, shots=4)

load_and_run("R11 void without qubits", """
operation Main() { var unused: int = 1; }
""", None, shots=4)

load_and_run("R12 void hides multiple internal measurements", """
operation Main() {
    use q = Qubit[2];
    var first: bit = M(q[0]);
    var second: bit = M(q[1]);
}
""", None, shots=16)

load_and_run("R13 compiler output name avoids source names", """
operation Main(): int {
    use __qora_result = Qubit[1];
    var observed: bit = M(__qora_result[0]);
    var qora_return: int = 23;
    var qora_completed: int = 31;
    if (observed == 0) { var qora_return: int = 99; }
    return qora_return;
}
""", {"23"}, shots=4)

load_and_run("R14 quantum return call in a while condition", """
operation ToggleAndRead(q: Qubit): bit { X(q); return M(q); }
operation Main(): int {
    use q = Qubit[1];
    var count: int = 0;
    while (ToggleAndRead(q[0]) == 1) { count = count + 1; }
    return count;
}
""", {"1"}, shots=16)

load_and_run("R15 repeated value calls preserve left-to-right order", """
operation ReadAndToggle(q: Qubit): int {
    var r: bit = M(q);
    X(q);
    return r;
}
operation Main(): int {
    use q = Qubit[1];
    return ReadAndToggle(q[0]) - ReadAndToggle(q[0]);
}
""", {"-1"}, shots=16)

# Measurement-dependent flags ensure these exercise the runtime short-circuit paths,
# not just constant folding. The final measurement detects a skipped or extra X.
for name, prepare, operator, expected in (
    ("R16a && skips the effectful right operand", "", "&&", 0),
    ("R16b && executes the effectful right operand", "X(q[0]);", "&&", 0),
    ("R16c || skips the effectful right operand", "X(q[0]);", "||", 1),
    ("R16d || executes the effectful right operand", "", "||", 1),
):
    load_and_run(name, f"""
operation Flip(q: Qubit): bit {{ X(q); return 1; }}
operation Main(): bit {{
    use q = Qubit[1];
    {prepare}
    var flag: bit = M(q[0]);
    if (flag == 1 {operator} Flip(q[0]) == 1) {{ }}
    return M(q[0]);
}}
""", {expected}, shots=16)

load_and_run("R17 later argument mutation reaches earlier array reference", """
operation Change(var values: int[]): int { values[0] = 5; return 0; }
function Read(values: int[], ignored: int): int { return values[0]; }
operation Main(): int {
    var values: int[] = [0];
    return Read(values, Change(var values));
}
""", {"5"}, shots=4)

load_and_run("R18 earlier scalar argument is read before later mutation", """
operation Change(var values: int[]): int { values[0] = 5; return 0; }
function Read(value: int, ignored: int): int { return value; }
operation Main(): int {
    var values: int[] = [0];
    return Read(values[0], Change(var values));
}
""", {"0"}, shots=4)

load_and_run("R19 both measurement-dependent entry return paths survive", """
operation Main(): int {
    use q = Qubit[1];
    H(q[0]);
    var r: bit = M(q[0]);
    if (r == 1) { return 7; }
    return 9;
}
""", {"7", "9"}, shots=128)

load_and_run("R20 measurement-dependent return leaves both nested loops", """
operation Main(): int {
    use q = Qubit[1];
    H(q[0]);
    var r: bit = M(q[0]);
    var i: int = 0;
    while (i < 2) {
        for j in 0..2 {
            if (r == 1) { return i * 10 + j + 7; }
        }
        i = i + 1;
    }
    return 9;
}
""", {"7", "9"}, shots=128)

load_and_run("R21 void entry hides ignored helper return values", """
operation Read(q: Qubit): bit { return M(q); }
operation Main() {
    use q = Qubit[1];
    Read(q[0]);
    X(q[0]);
    Read(q[0]);
}
""", None, shots=16)

load_and_run("R22 repeated measurement-dependent helper loops preserve both returns", """
operation UntilOne(q: Qubit): bit {
    var r: bit = M(q);
    while (r == 0) { H(q); r = M(q); }
    return r;
}
operation Main(): int {
    use q = Qubit[1];
    var first: bit = UntilOne(q[0]);
    X(q[0]);
    var second: bit = UntilOne(q[0]);
    return first * 10 + second;
}
""", {"11"}, shots=16)

for expression, expected in [
    ("(1 + 2) * 3", "9"),
    ("8 / (4 / 2)", "4"),
    ("-(1 + 2) * 3", "-9"),
]:
    load_and_run(f"G1 arithmetic grouping: {expression}", f"""
operation Main(): int {{ return {expression}; }}
""", {expected}, shots=4)

load_and_run("G2 grouped calls preserve evaluation order and execute once", """
operation Stamp(var values: int[], digit: int): int {
    values[0] = values[0] * 10 + digit;
    return digit;
}
operation Main(): int {
    var values: int[] = [0];
    var result: int = (Stamp(var values, 1) + Stamp(var values, 2)) * Stamp(var values, 3);
    return values[0] * 100 + result;
}
""", {"12309"}, shots=4)

load_and_run("C1 comparison values in declarations, writes, arguments and returns", """
function Positive(value: int): bool { return value > 0; }
operation Echo(value: bool): bool { return value; }
operation Main(): int {
    const same: bool = 1 + 2 * 3 == 7;
    var value: bool = 5 < 2;
    value = Positive(3) == true;
    var results: bool[] = [same, value, Echo(1 != 2), 3 <= 3, 5 > 2, 4 >= 4];
    results[0] = (1 + 2) * 3 == 9;
    var encoded: int = 0;
    for i in 0..results.Count-1 {
        encoded = encoded * 2;
        if (results[i]) { encoded = encoded + 1; }
    }
    return encoded;
}
""", {"63"}, shots=4)

load_and_run("C1b all six comparisons also preserve false values", """
operation Main(): int {
    var results: bool[] = [1 == 1, 1 != 1, 1 < 2, 2 <= 1, 2 > 1, 1 >= 2];
    var encoded: int = 0;
    for i in 0..results.Count-1 {
        encoded = encoded * 2;
        if (results[i]) { encoded = encoded + 1; }
    }
    return encoded;
}
""", {"42"}, shots=4)  # Explicitly encode [true, false, true, false, true, false] as 0b101010.

for expression, expected in [
    ("(1 + 2) * 3 == 8", False),
    ("true == 2 < 3", True),
    ("(1 == 2) == (3 < 2)", True),
]:
    load_and_run(f"C2 comparison precedence: {expression}", f"""
operation Main(): bool {{ return {expression}; }}
""", {expected}, shots=4)

load_and_run("C3 comparison operands execute left-to-right exactly once", """
operation Stamp(var values: int[], digit: int): int {
    values[0] = values[0] * 10 + digit;
    return digit;
}
operation Main(): int {
    var values: int[] = [0];
    var equal: bool = Stamp(var values, 1) + Stamp(var values, 2) == Stamp(var values, 3);
    if (equal) { return values[0] * 10 + 1; }
    return values[0] * 10;
}
""", {"1231"}, shots=4)

load_and_run("C4 a stored measurement can be compared and returned as bool", """
operation Main(): bool {
    use q = Qubit[1];
    X(q[0]);
    var measured: bit = M(q[0]);
    return measured == 1;
}
""", {True}, shots=4)

load_and_run("C5 bool arrays initialize to false and preserve read-only arguments", """
function Different(values: bool[]): bool { return values[0] != values[1]; }
operation Main(): bool {
    var flags: bool[] = new bool[2];
    flags[1] = true;
    return Different(flags);
}
""", {True}, shots=4)

load_and_run("C6 bool results preserve false through calls and assignment", """
operation Echo(value: bool): bool { return value; }
operation Main(): bool {
    var result = true;
    result = Echo(2 < 1);
    return result;
}
""", {False}, shots=4)

load_and_run("G3 S squared preserves the phase needed by H-Z-H", """
operation Main(): bit {
    use q = Qubit[1];
    H(q[0]);
    S(q[0]);
    S(q[0]);
    H(q[0]);
    return M(q[0]);
}
""", {1}, shots=16)

load_and_run("G4 T to the fourth power preserves the phase needed by H-Z-H", """
operation Main(): bit {
    use q = Qubit[1];
    H(q[0]);
    T(q[0]);
    T(q[0]);
    T(q[0]);
    T(q[0]);
    H(q[0]);
    return M(q[0]);
}
""", {1}, shots=16)

print()
print(f"{PASS} passed, {FAIL} failed")

sys.exit(1 if FAIL else 0)
