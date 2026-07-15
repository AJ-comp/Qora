# -*- coding: utf-8 -*-
"""Release-gate validation: Qora-emitted QASM must LOAD in Qiskit and RUN with correct semantics.

Requires: pip install qiskit qiskit-qasm3-import qiskit-aer  (and a Debug build of src/Qora).
Run before releases (see the release checklist). Known importer gaps (qiskit-qasm3-import 0.6.0):
def subroutines, int/const declarations, variable register indices (q[i] in for), && / || - those
cases FAIL at qasm3.loads until Qora grows a flattening emission mode or the importer catches up.
"""
import io, json, subprocess, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

import os
DLL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Qora", "bin", "Debug", "net10.0", "Qora.dll")

from qiskit import qasm3
from qiskit_aer import AerSimulator

PASS = FAIL = 0
INFO = []

def compile_qora(src):
    p = subprocess.run(["dotnet", DLL, "--json"], input=src.encode("utf-8"), capture_output=True)
    r = json.loads(p.stdout.decode("utf-8"))
    assert r["success"], r["errors"]
    return r["qasm"]

def check(name, cond, detail=""):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  ok  {name}")
    else:    FAIL += 1; print(f"  FAIL {name}  {detail}")

def run_counts(circ, shots=2000):
    sim = AerSimulator(seed_simulator=7)
    return sim.run(circ, shots=shots).result().get_counts()

def keys(counts):
    return {k.replace(" ", "") for k in counts}

def load_and_run(name, src, expect_keys=None, subset=False):
    """Compile -> qasm3.loads -> simulate; verify measured keys."""
    try:
        qasm = compile_qora(src)
    except AssertionError as e:
        check(f"{name} (compile)", False, e); return
    try:
        circ = qasm3.loads(qasm)
    except Exception as e:
        check(f"{name} (qasm3.loads)", False, f"{type(e).__name__}: {e}"); return
    if expect_keys is None:
        check(f"{name} (loads)", True); return
    try:
        got = keys(run_counts(circ))
    except Exception as e:
        check(f"{name} (simulate)", False, f"{type(e).__name__}: {e}"); return
    ok = got <= set(expect_keys) if subset else got == set(expect_keys)
    check(f"{name} (run: {sorted(got)})", ok, f"expected {sorted(expect_keys)}{' (subset)' if subset else ''}")

print("== FLAT programs (no defs) ==")

load_and_run("F1 Bell (entangled: only 00/11)", """
operation Main() {
    use q = Qubit[2];
    H(q[0]);
    CNOT(q[0], q[1]);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"00", "11"})

load_and_run("F2 deterministic X -> 1", """
operation Main() {
    use q = Qubit[1];
    X(q[0]);
    bit r = M(q[0]);
}""", {"1"})

load_and_run("F3 Rx(pi) -> 1 (rotation w/ pi expr)", """
operation Main() {
    use q = Qubit[1];
    Rx(pi, q[0]);
    bit r = M(q[0]);
}""", {"1"})

load_and_run("F4 for loop flips both -> 11", """
operation Main() {
    use q = Qubit[2];
    for i in 0..q.Count - 1 {
        X(q[i]);
    }
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"11"})

load_and_run("F5 mid-circuit measure + if -> 11", """
operation Main() {
    use q = Qubit[2];
    X(q[0]);
    bit r = M(q[0]);
    if (r == 1) {
        X(q[1]);
    }
    bit r1 = M(q[1]);
}""", {"111", "11"}, subset=True)  # key = r,r1 registers; both 1 whatever grouping

load_and_run("F6 single-gate functors identity (H S S† H = I) -> 0", """
operation Main() {
    use q = Qubit[1];
    H(q[0]);
    S(q[0]);
    Adjoint S(q[0]);
    H(q[0]);
    bit r = M(q[0]);
}""", {"0"})

load_and_run("F6b Controlled X -> 11", """
operation Main() {
    use q = Qubit[2];
    X(q[0]);
    Controlled X(q[0], q[1]);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"11"})

load_and_run("F7 Reset after X -> 0", """
operation Main() {
    use q = Qubit[1];
    X(q[0]);
    Reset(q[0]);
    bit r = M(q[0]);
}""", {"0"})

load_and_run("F8 const + arithmetic angle (Rz(pi/k)) loads+runs", """
operation Main() {
    use q = Qubit[1];
    const int k = 2;
    H(q[0]);
    Rz(pi/k, q[0]);
    H(q[0]);
    bit r = M(q[0]);
}""", {"0", "1"}, subset=True)

print()
print("== DEF programs (subroutines: the known consumer-support question) ==")

load_and_run("D1 Bell via def", """
operation Bell(Qubit[] q) {
    H(q[0]);
    CNOT(q[0], q[1]);
}
operation Main() {
    use q = Qubit[2];
    Bell(q);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", {"00", "11"})

load_and_run("D2 whole-op Adjoint identity (Prep; Adjoint Prep -> 00)", """
operation Prep(Qubit[] q) {
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

load_and_run("D3 namespaced call (def MyLib__Bell_)", """
namespace MyLib {
    operation Bell(Qubit[] q) {
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

load_and_run("D4 adjoint-pipeline doc example (defs + ___adj + reversed for)", """
operation Inner(Qubit[] q) {
    H(q[0]);
    T(q[1]);
}
operation Outer(Qubit[] q, bit b) {
    int k = 2;
    Inner(q);
    Rx(pi/k, q[0]);
    for i in 0..q.Count - 1 {
        X(q[i]);
    }
    if (b == 1) {
        Z(q[0]);
        Adjoint S(q[0]);
    }
}
operation Main() {
    use q = Qubit[2];
    bit r = M(q[0]);
    Outer(q, r);
    Adjoint Outer(q, r);
    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}""", None)  # load-only: contents statistical; identity check is D2's job

print()
print(f"{PASS} passed, {FAIL} failed")
sys.exit(1 if FAIL else 0)
