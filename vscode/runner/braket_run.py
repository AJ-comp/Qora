# -*- coding: utf-8 -*-
"""Extension-side Braket runner: OpenQASM 3 on stdin -> one JSON line on stdout.

    { "counts": {"00": 512, "11": 488}, "shots": 1000 }        on success
    { "error": "human-readable reason" }                        on failure (exit 1)

The extension compiles the .qor document itself (bundled Qora CLI) and feeds the QASM here; this
script only executes it on Braket's LocalSimulator. stdgates.inc lives next to this file and Braket
resolves `include` against the cwd, so we chdir here first. Requires Python 3.10-3.13 with
amazon-braket-sdk installed (the extension provisions this automatically on first run).
"""
import argparse
import io
import json
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")


def fail(message):
    print(json.dumps({"error": message}))
    sys.exit(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shots", type=int, default=1000)
    args = ap.parse_args()

    qasm = sys.stdin.buffer.read().decode("utf-8")
    if not qasm.strip():
        fail("no QASM on stdin")

    try:
        from braket.devices import LocalSimulator
        from braket.ir.openqasm import Program
    except Exception as ex:
        fail(f"amazon-braket-sdk is not usable in this Python ({sys.version.split()[0]}): {type(ex).__name__}: {ex}")

    os.chdir(os.path.dirname(os.path.abspath(__file__)))  # stdgates.inc lives here

    import warnings
    warnings.filterwarnings("ignore")

    try:
        result = LocalSimulator().run(Program(source=qasm), shots=args.shots).result()
        counts = {k: int(v) for k, v in dict(result.measurement_counts).items()}
    except Exception as ex:
        fail(f"{type(ex).__name__}: {ex}")

    print(json.dumps({"counts": counts, "shots": args.shots}))


if __name__ == "__main__":
    main()
