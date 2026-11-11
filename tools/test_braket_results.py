"""Run with: python -m unittest discover -s tools -p test_braket_results.py."""

import json
import math
import os
import shutil
import subprocess
import sys
import unittest
from types import SimpleNamespace

RUNNER = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "vscode", "runner"))
sys.path.insert(0, RUNNER)
from braket_results import entry_result_from_task, validate_execution_request, validate_shots


class BraketResultTests(unittest.TestCase):
    def metadata(self, return_type="bit", name="result_7"):
        return {"type": return_type, "outputName": name}

    def task(self, values, name="result_7"):
        return SimpleNamespace(
            outputs=[{name: value} for value in values],
            task_metadata=SimpleNamespace(shots=len(values)),
            measurement_counts={"111": 999},
        )

    def read(self, values, return_type="bit"):
        return entry_result_from_task(self.task(values), self.metadata(return_type), len(values))

    def test_bits_preserve_shot_order_without_internal_measurements(self):
        self.assertEqual(
            {"shots": 3, "result": {"type": "bit", "values": [1, 0, 1]}},
            self.read([1, 0, 1]),
        )

    def test_bool_values_remain_json_booleans(self):
        reply = self.read([True, False, True], "bool")
        self.assertEqual(
            {"shots": 3, "result": {"type": "bool", "values": [True, False, True]}},
            reply,
        )
        decoded = json.loads(json.dumps(reply, allow_nan=False))
        self.assertTrue(all(type(value) is bool for value in decoded["result"]["values"]))

    def test_bool_rejects_bit_integer_and_other_values(self):
        for value in (0, 1, 0.0, "true", "false", [True], None):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "not a bool"):
                self.read([value], "bool")

    def test_integer_strings_preserve_precision_and_sign(self):
        values = [9007199254740993, -9223372036854775808, 0]
        reply = self.read(values, "int")
        self.assertEqual([str(value) for value in values], reply["result"]["values"])
        self.assertEqual(reply, json.loads(json.dumps(reply, allow_nan=False)))

    def test_float_and_angle_preserve_finite_values(self):
        for kind in ("float", "angle"):
            with self.subTest(kind=kind):
                reply = self.read([0.1, math.pi / 2, -0.0, 1], kind)
                self.assertEqual([0.1, math.pi / 2, -0.0, 1.0], reply["result"]["values"])
                self.assertEqual(-1.0, math.copysign(1, reply["result"]["values"][2]))

    def test_void_exposes_no_values_or_histogram(self):
        self.assertEqual({"shots": 2, "result": {"type": "void"}}, self.read([1, 1], "void"))

    def test_void_requires_completion_for_every_shot(self):
        for value in (0, None, True, 1.0, "1"):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "completion marker"):
                self.read([value], "void")

    def test_unassigned_return_is_an_error_not_a_question_mark(self):
        for kind in ("bool", "bit", "int", "float", "angle"):
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                self.read([None], kind)

    def test_bit_rejects_wrong_scalar_or_array(self):
        for value in (2, -1, True, 0.0, "0", [0], {}):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "not a bit"):
                self.read([value])

    def test_int_rejects_bool_float_and_array(self):
        for value in (True, 1.0, "1", [1]):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "not an integer"):
                self.read([value], "int")

    def test_float_and_angle_reject_nonfinite_or_nonnumeric_values(self):
        for kind in ("float", "angle"):
            for value in (math.nan, math.inf, -math.inf, True, "1.5", [1.5], 10 ** 400):
                with self.subTest(kind=kind, value=value), self.assertRaisesRegex(ValueError, "finite"):
                    self.read([value], kind)

    def test_output_is_selected_by_metadata_not_a_fixed_name(self):
        task = self.task([7], name="qora_result_99")
        reply = entry_result_from_task(task, self.metadata("int", "qora_result_99"), 1)
        self.assertEqual(["7"], reply["result"]["values"])

    def test_wrong_missing_or_extra_outputs_are_rejected(self):
        for shot in ({"wrong": 1}, {}, {"result_7": 1, "helper": 0}, None, [1]):
            with self.subTest(shot=shot), self.assertRaisesRegex(ValueError, "output names"):
                task = self.task([1])
                task.outputs = [shot]
                entry_result_from_task(task, self.metadata(), 1)

    def test_legacy_counts_are_never_used_as_return_values(self):
        for outputs in (None, {}, "1"):
            with self.subTest(outputs=outputs), self.assertRaisesRegex(ValueError, "declared outputs"):
                task = self.task([1])
                task.outputs = outputs
                entry_result_from_task(task, self.metadata(), 1)

    def test_missing_outputs_property_is_incompatible(self):
        task = SimpleNamespace(task_metadata=SimpleNamespace(shots=1), measurement_counts={"1": 1})
        with self.assertRaisesRegex(ValueError, "declared outputs"):
            entry_result_from_task(task, self.metadata(), 1)

    def test_task_and_output_shot_counts_must_both_match(self):
        task = self.task([0, 1])
        with self.assertRaisesRegex(ValueError, "task shot count"):
            entry_result_from_task(task, self.metadata(), 3)
        task.task_metadata.shots = 3
        with self.assertRaisesRegex(ValueError, "output shot count"):
            entry_result_from_task(task, self.metadata(), 3)

    def test_shots_are_positive_integers(self):
        for shots in (0, -1, True, 1.5, "1", None):
            with self.subTest(shots=shots), self.assertRaisesRegex(ValueError, "positive integer"):
                validate_shots(shots)

    def test_missing_metadata_reports_old_compiler(self):
        for metadata in (None, [], "bit"):
            with self.subTest(metadata=metadata), self.assertRaisesRegex(ValueError, "Incompatible Qora compiler"):
                validate_execution_request({"qasm": "OPENQASM 3;", "entryResult": metadata})

    def test_metadata_type_and_name_are_required(self):
        for metadata in ({}, {"type": "bit[]", "outputName": "x"}, {"type": "bit"},
                         {"type": "int", "outputName": ""}, {"type": "int", "outputName": 1}):
            with self.subTest(metadata=metadata), self.assertRaises(ValueError):
                validate_execution_request({"qasm": "OPENQASM 3;", "entryResult": metadata})

    def test_request_requires_compiled_qasm(self):
        for qasm in (None, "", "  ", 1):
            with self.subTest(qasm=qasm), self.assertRaisesRegex(ValueError, "No compiled QASM"):
                validate_execution_request({"qasm": qasm, "entryResult": self.metadata()})
        with self.assertRaisesRegex(ValueError, "JSON object"):
            validate_execution_request([])


class BraketOutputIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            from braket.devices import LocalSimulator
            from braket.ir.openqasm import Program
        except ImportError as error:
            raise unittest.SkipTest("Install tools/requirements.txt for Braket integration tests") from error
        cls.device = LocalSimulator()
        cls.program_type = Program
        cls.previous_directory = os.getcwd()
        os.chdir(os.path.dirname(os.path.abspath(__file__)))  # stdgates.inc

    @classmethod
    def tearDownClass(cls):
        os.chdir(cls.previous_directory)

    def run_output(self, source, kind, name="returned"):
        result = self.device.run(self.program_type(source=source), shots=4).result()
        return entry_result_from_task(result, {"type": kind, "outputName": name}, 4)

    def test_measurement_then_gate_returns_stored_value_not_final_qubit(self):
        reply = self.run_output(
            'OPENQASM 3.0; include "stdgates.inc"; output bit returned; qubit q; '
            "returned = measure q; x q;",
            "bit",
        )
        self.assertEqual([0] * 4, reply["result"]["values"])

    def test_scalar_integer_precision(self):
        reply = self.run_output(
            "OPENQASM 3.0; output int[64] returned; returned = 9007199254740993;", "int"
        )
        self.assertEqual(["9007199254740993"] * 4, reply["result"]["values"])

    def test_bool_outputs_preserve_their_type(self):
        for expression, expected in (("1 == 1", True), ("1 > 2", False)):
            with self.subTest(expression=expression):
                reply = self.run_output(
                    f"OPENQASM 3.0; output bool returned; returned = {expression};", "bool"
                )
                self.assertEqual([expected] * 4, reply["result"]["values"])
                self.assertTrue(all(type(value) is bool for value in reply["result"]["values"]))

    def test_float_and_angle_radians(self):
        for kind, expression in (("float", "0.1"), ("angle", "float(pi / 2)")):
            with self.subTest(kind=kind):
                reply = self.run_output(
                    f"OPENQASM 3.0; output float returned; returned = {expression};", kind
                )
                expected = 0.1 if kind == "float" else math.pi / 2
                self.assertEqual([expected] * 4, reply["result"]["values"])

    def test_void_runs_internal_measurements_without_exposing_them(self):
        reply = self.run_output(
            "OPENQASM 3.0; output int completed; qubit[2] q; "
            "bit first = measure q[0]; bit second = measure q[1]; completed = 1;",
            "void", "completed",
        )
        self.assertEqual({"shots": 4, "result": {"type": "void"}}, reply)

    def test_void_without_qubits(self):
        reply = self.run_output("OPENQASM 3.0; output int completed; completed = 1;", "void", "completed")
        self.assertEqual({"shots": 4, "result": {"type": "void"}}, reply)

    def test_helper_local_name_does_not_replace_entry_output(self):
        reply = self.run_output(
            "OPENQASM 3.0; output int returned; "
            "def helper() { int returned = 99; } helper(); returned = 7;", "int"
        )
        self.assertEqual(["7"] * 4, reply["result"]["values"])

    def test_unassigned_output_is_a_diagnostic(self):
        with self.assertRaisesRegex(ValueError, "not a bit"):
            self.run_output("OPENQASM 3.0; output bit returned; qubit q;", "bit")

    def test_runner_json_round_trip(self):
        request = {
            "qasm": "OPENQASM 3.0; output int[64] answer; answer = 9007199254740993;",
            "entryResult": {"type": "int", "outputName": "answer"},
        }
        process = subprocess.run(
            [sys.executable, os.path.join(RUNNER, "braket_run.py"), "--shots", "2"],
            input=json.dumps(request), text=True, encoding="utf-8", capture_output=True,
        )
        self.assertEqual(0, process.returncode, process.stderr + process.stdout)
        self.assertEqual(
            {"shots": 2, "result": {"type": "int", "values": ["9007199254740993"] * 2}},
            json.loads(process.stdout),
        )

    def test_runner_rejects_old_compiler_metadata(self):
        process = subprocess.run(
            [sys.executable, os.path.join(RUNNER, "braket_run.py")],
            input=json.dumps({"qasm": "OPENQASM 3.0; qubit q;"}),
            text=True, encoding="utf-8", capture_output=True,
        )
        self.assertEqual(1, process.returncode)
        self.assertIn("Incompatible Qora compiler", json.loads(process.stdout)["error"])

    def test_compiled_phase_gates_in_tools_and_vscode_runner(self):
        root = os.path.abspath(os.path.join(RUNNER, "..", ".."))
        compiler = os.path.join(root, "src", "Qora", "bin", "Debug", "net10.0", "Qora.dll")
        if not os.path.isfile(compiler) or shutil.which("dotnet") is None:
            self.skipTest("Build src/Qora with .NET 10 for compiler-to-runner integration tests")

        expected = {"shots": 4, "result": {"type": "bit", "values": [1] * 4}}
        for gate, repetitions in (("S", 2), ("T", 4)):
            # S^2 = T^4 = Z, so H followed by either sequence and H must flip |0>.
            gates = " ".join(f"{gate}(q[0]);" for _ in range(repetitions))
            source = (
                "operation Main(): bit { use q = Qubit[1]; "
                f"H(q[0]); {gates} H(q[0]); return M(q[0]); }}"
            )
            compilation = subprocess.run(
                ["dotnet", compiler, "--json"], input=source,
                text=True, encoding="utf-8", capture_output=True, timeout=60,
            )
            self.assertEqual(0, compilation.returncode, compilation.stderr + compilation.stdout)
            compiled = json.loads(compilation.stdout)
            self.assertTrue(compiled["success"], compiled.get("errors"))
            request = validate_execution_request(compiled)

            with self.subTest(gate=gate, library="tools/stdgates.inc"):
                metadata = request["entryResult"]
                reply = self.run_output(request["qasm"], metadata["type"], metadata["outputName"])
                self.assertEqual(expected, reply)

            with self.subTest(gate=gate, library="vscode/runner/stdgates.inc"):
                process = subprocess.run(
                    [sys.executable, os.path.join(RUNNER, "braket_run.py"), "--shots", "4"],
                    input=json.dumps(request), text=True, encoding="utf-8",
                    capture_output=True, timeout=60,
                )
                self.assertEqual(0, process.returncode, process.stderr + process.stdout)
                self.assertEqual(expected, json.loads(process.stdout))


if __name__ == "__main__":
    unittest.main()
