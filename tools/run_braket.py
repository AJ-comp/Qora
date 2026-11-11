# -*- coding: utf-8 -*-
"""Run a .qor program: Qora compiler -> Braket LocalSimulator -> entry return values.

    python tools/run_braket.py path/to/program.qor [--shots 1000]

Requires: python -m pip install -r vscode/runner/requirements.txt  (Python 3.11-3.13)
and a built compiler (dotnet build src/Qora). Braket's local simulator executes the generated
OpenQASM within its supported feature set; see README.md for known upstream execution limitations.
Only explicit entry returns are displayed. stdgates.inc (shipped in this directory) is resolved
from the working directory, so this script chdirs here before running.
"""
import argparse
import io
import json
import os
import subprocess
import sys
from collections import Counter

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(TOOLS, "..", "vscode", "runner"))
from braket_results import entry_result_from_task, validate_execution_request, validate_shots

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
    return validate_execution_request(reply)


def main():
    ap = argparse.ArgumentParser(description="Compile a .qor file and run it on Braket's LocalSimulator.")
    ap.add_argument("file", help="the .qor program (its imports resolve next to it)")
    ap.add_argument("--shots", type=int, default=1000)
    args = ap.parse_args()

    try:
        validate_shots(args.shots)
        request = compile_qora(args.file)
    except ValueError as ex:
        sys.exit(str(ex))

    try:
        from braket.devices import LocalSimulator
        from braket.ir.openqasm import Program
    except Exception as ex:
        sys.exit(
            "could not import the Braket SDK — install it with:\n"
            "  python -m pip install -r vscode/runner/requirements.txt\n"
            "note: this runner supports Python 3.11-3.13; "
            f"this interpreter is {sys.version.split()[0]}.\n"
            f"({type(ex).__name__}: {ex})"
        )

    os.chdir(TOOLS)  # stdgates.inc lives here; Braket resolves `include` against the cwd
    try:
        result = LocalSimulator().run(Program(source=request["qasm"]), shots=args.shots).result()
        reply = entry_result_from_task(result, request["entryResult"], args.shots)
    except Exception as ex:
        sys.exit(f"{type(ex).__name__}: {ex}")

    print(f"\n{args.file} — {args.shots} shots on Braket LocalSimulator\n")
    returned = reply["result"]
    if returned["type"] == "void":
        print("  Completed successfully; the entry point returns void (no result values).\n")
        return

    counts = Counter(str(value) for value in returned["values"])
    total = reply["shots"]
    width = 40
    unit = " (radians)" if returned["type"] == "angle" else ""
    print(f"  Entry return values: {returned['type']}{unit}\n")
    for key, n in sorted(counts.items(), key=lambda kv: -kv[1]):
        bar = "█" * max(1, round(width * n / total))
        print(f"  {key}  {bar}  {n}  ({100 * n / total:.1f}%)")
    print()


if __name__ == "__main__":
    main()
