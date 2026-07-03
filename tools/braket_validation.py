# -*- coding: utf-8 -*-
"""Release-gate validation #2: Qora's RICH output (def/int/for/while) must RUN on Braket's LocalSimulator.

Requires: pip install amazon-braket-sdk  (Python 3.10-3.13; not 3.14 yet) and a Debug build of
src/Qora. Complements tools/qiskit_validation.py: Qiskit's importer covers the FLAT subset, Braket's
local simulator covers the full language - together they are Qora's execution truth. stdgates.inc in
this directory is resolved from the cwd, so this script chdirs here first.
"""
import os
os.chdir(os.path.dirname(os.path.abspath(__file__)))
import io, json, subprocess, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

DLL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Qora", "bin", "Debug", "net10.0", "Qora.dll")

from braket.devices import LocalSimulator
from braket.ir.openqasm import Program

device = LocalSimulator()
PASS = FAIL = 0

def compile_qora(src):
    p = subprocess.run(["dotnet", DLL, "--json"], input=src.encode("utf-8"), capture_output=True)
    r = json.loads(p.stdout.decode("utf-8"))
    assert r["success"], r["errors"]
    return r["qasm"]

def check(name, cond, detail=""):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  ok  {name}")
    else:    FAIL += 1; print(f"  FAIL {name}  {detail}")

def load_and_run(name, src, expect_keys=None, subset=False, shots=1000):
    try:
        qasm = compile_qora(src)
    except AssertionError as e:
        check(f"{name} (compile)", False, e); return
    try:
        result = device.run(Program(source=qasm), shots=shots).result()
        counts = dict(result.measurement_counts)
    except Exception as e:
        check(f"{name} (braket run)", False, f"{type(e).__name__}: {str(e)[:140]}"); return
    if expect_keys is None:
        check(f"{name} (runs: {counts})", True); return
    got = set(counts)
    ok = got <= set(expect_keys) if subset else got == set(expect_keys)
    check(f"{name} (run: {sorted(got)})", ok, f"expected {sorted(expect_keys)}")

print("== Qiskit이 거부했던 케이스들을 Braket LocalSimulator에 ==")

load_and_run("B1 def Bell (서브루틴)", """
operation Bell(Qubit[2] q) {
    H(q[0]);
    CNOT(q[0], q[1]);
}
operation Main() {
    use q = Qubit[2];
    Bell(q);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"00", "11"})

load_and_run("B2 int/const 선언 + 산술 각도", """
operation Main() {
    use q = Qubit[1];
    const int k = 2;
    H(q[0]);
    Rz(pi/k, q[0]);
    H(q[0]);
    bit r = M(q[0]);
}""", {"0", "1"}, subset=True)

load_and_run("B3 for 루프 + 변수 인덱스 q[i]", """
operation Main() {
    use q = Qubit[2];
    for i in 0..1 {
        X(q[i]);
    }
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"11"})

load_and_run("B4 전연산 Adjoint 항등성 (합성 ___adj def)", """
operation Prep(Qubit[2] q) {
    H(q[0]);
    T(q[1]);
    CNOT(q[0], q[1]);
}
operation Main() {
    use q = Qubit[2];
    Prep(q);
    Adjoint Prep(q);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"00"})

load_and_run("B5 네임스페이스 맹글명 def", """
namespace MyLib {
    operation Bell(Qubit[2] q) {
        H(q[0]);
        CNOT(q[0], q[1]);
    }
}
operation Main() {
    use q = Qubit[2];
    MyLib.Bell(q);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"00", "11"})

load_and_run("B6 측정→if 피드백", """
operation Main() {
    use q = Qubit[2];
    X(q[0]);
    bit r = M(q[0]);
    if (r == 1) {
        X(q[1]);
    }
    bit r1 = M(q[1]);
}""", {"11"})

load_and_run("B7 while (측정 의존 동적 루프!)", """
operation Main() {
    use q = Qubit[1];
    bit r = M(q[0]);
    while (r == 0) {
        H(q[0]);
        r = M(q[0]);
    }
    X(q[0]);
}""", None)

load_and_run("B8 demo.qor as-is (for+const+measure+if)",
    open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "vscode", "examples", "demo.qor"), encoding="utf-8").read(), None)

print()
print(f"{PASS} passed, {FAIL} failed")

sys.exit(1 if FAIL else 0)
