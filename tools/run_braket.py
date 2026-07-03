# -*- coding: utf-8 -*-
"""Run a .qor program end-to-end: Qora compiler -> OpenQASM 3 -> Braket LocalSimulator -> histogram.

    python tools/run_braket.py path/to/program.qor [--shots 1000]

Requires: pip install amazon-braket-sdk  (Python 3.10-3.13; the SDK does not support 3.14 yet)
and a built compiler (dotnet build src/Qora). The Braket LOCAL simulator executes Qora's full
output — def subroutines, int/const, for with variable indices, measure->if, even while loops —
which makes it the reference execution engine for Qora programs today. stdgates.inc (shipped in
this directory) is resolved from the working directory, so this script chdirs here before running.
"""
import argparse
import io
import json
import os
import subprocess
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

TOOLS = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(TOOLS, "..", "src", "Qora", "bin", "Debug", "net10.0", "Qora.dll")


def compile_qora(path):
    if not os.path.exists(DLL):
        sys.exit(f"compiler not built — run: dotnet build src/Qora  (looked for {DLL})")
    p = subprocess.run(["dotnet", DLL, "--json", os.path.abspath(path)], capture_output=True)
    reply = json.loads(p.stdout.decode("utf-8"))
    if not reply["success"]:
        print(f"{path}: compilation failed:")
        for e in reply["errors"]:
            where = f" @ {e['start']}..{e['end']}" if e["start"] >= 0 else ""
            print(f"  [{e['code']}]{where} {e['message']}")
        sys.exit(1)
    return reply["qasm"]


def main():
    ap = argparse.ArgumentParser(description="Compile a .qor file and run it on Braket's LocalSimulator.")
    ap.add_argument("file", help="the .qor program (its imports resolve next to it)")
    ap.add_argument("--shots", type=int, default=1000)
    args = ap.parse_args()

    qasm = compile_qora(args.file)

    try:
        from braket.devices import LocalSimulator
        from braket.ir.openqasm import Program
    except Exception as ex:
        sys.exit(
            "could not import the Braket SDK — install it with:\n"
            "  pip install amazon-braket-sdk\n"
            "note: the SDK requires Python 3.10-3.13 (3.14 is not supported yet); "
            f"this interpreter is {sys.version.split()[0]}.\n"
            f"({type(ex).__name__}: {ex})"
        )

    os.chdir(TOOLS)  # stdgates.inc lives here; Braket resolves `include` against the cwd
    result = LocalSimulator().run(Program(source=qasm), shots=args.shots).result()
    counts = dict(result.measurement_counts)

    total = sum(counts.values()) or 1
    width = 40
    print(f"\n{args.file} — {args.shots} shots on Braket LocalSimulator\n")
    for key, n in sorted(counts.items(), key=lambda kv: -kv[1]):
        bar = "█" * max(1, round(width * n / total))
        print(f"  {key}  {bar}  {n}  ({100 * n / total:.1f}%)")
    print()


if __name__ == "__main__":
    main()
