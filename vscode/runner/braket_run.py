# -*- coding: utf-8 -*-
"""Extension-side Braket runner: compiled execution JSON on stdin -> one JSON line on stdout.

Input: { "qasm": "...", "entryResult": { "type": "bit", "outputName": "..." } }
Output: { "shots": 2, "result": { "type": "bit", "values": [0, 1] } }
Void: { "shots": 2, "result": { "type": "void" } }
Failure: { "error": "human-readable reason" } (exit 1).

The extension compiles the .qor document itself (bundled Qora CLI) and feeds its artifact here; this
script only executes it on Braket's LocalSimulator. stdgates.inc lives next to this file and Braket
resolves `include` against the cwd, so we chdir here first. Supports Python 3.11-3.13 with
the SDK version from requirements.txt (the extension provisions this automatically).
"""
import argparse
import io
import json
import os
import sys

from braket_results import entry_result_from_task, validate_execution_request, validate_shots

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")


def fail(message):
    print(json.dumps({"error": message}))
    sys.exit(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shots", type=int, default=1000)
    args = ap.parse_args()

    try:
        validate_shots(args.shots)
        request = validate_execution_request(json.loads(sys.stdin.buffer.read().decode("utf-8")))
    except (ValueError, TypeError) as ex:
        fail(str(ex))

    try:
        from braket.devices import LocalSimulator
        from braket.ir.openqasm import Program
    except Exception as ex:
        fail(f"amazon-braket-sdk is not usable in this Python ({sys.version.split()[0]}): {type(ex).__name__}: {ex}")

    os.chdir(os.path.dirname(os.path.abspath(__file__)))  # stdgates.inc lives here

    import warnings
    warnings.filterwarnings("ignore")

    try:
        result = LocalSimulator().run(Program(source=request["qasm"]), shots=args.shots).result()
        reply = entry_result_from_task(result, request["entryResult"], args.shots)
    except Exception as ex:
        fail(f"{type(ex).__name__}: {ex}")

    print(json.dumps(reply, allow_nan=False))


if __name__ == "__main__":
    main()
