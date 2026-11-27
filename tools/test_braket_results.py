"""Run with: python -m unittest discover -s tools -p test_braket_results.py."""

from contextlib import chdir
from itertools import permutations
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

    def test_registers_require_a_bit_list_of_the_declared_width_per_shot(self):
        metadata = dict(self.metadata("bit[]"), length=2)
        values = [[1, 0], [0, 1]]
        self.assertEqual(values, entry_result_from_task(self.task(values), metadata, 2)["result"]["values"])
        for value in (1, "10", [1], [1, 0, 0], [1, 2], [True, 0], [0, 1.0]):
            with self.subTest(value=value), self.assertRaises(ValueError):
                entry_result_from_task(self.task([value]), metadata, 1)
        for length in (True, 0, -1, "2", 2.5):
            with self.subTest(length=length), self.assertRaises(ValueError):
                entry_result_from_task(self.task(values), dict(metadata, length=length), 2)

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

    def run_output(self, source, kind, name="returned", shots=4):
        result = self.device.run(self.program_type(source=source), shots=shots).result()
        return entry_result_from_task(result, {"type": kind, "outputName": name}, shots)

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

    def compile_request(self, source):
        root = os.path.abspath(os.path.join(RUNNER, "..", ".."))
        compiler = os.path.join(root, "src", "Qora", "bin", "Debug", "net10.0", "Qora.dll")
        if not os.path.isfile(compiler) or shutil.which("dotnet") is None:
            self.skipTest("Build src/Qora with .NET 10 for compiler-to-runner integration tests")
        compilation = subprocess.run(
            ["dotnet", compiler, "--json"], input=source,
            text=True, encoding="utf-8", capture_output=True, timeout=60,
        )
        self.assertEqual(0, compilation.returncode, compilation.stderr + compilation.stdout)
        compiled = json.loads(compilation.stdout)
        self.assertTrue(compiled["success"], compiled.get("errors"))
        return validate_execution_request(compiled)

    def test_compiled_operation_returns_and_consumes_bit_arrays(self):
        read = """
            operation Read(left: Qubit, right: Qubit): bit[] {
                var first: bit = M(left);
                var second: bit = M(right);
                return [first, second];
            }
        """
        cases = (
            ("register", read + """
                operation Relay(left: Qubit, right: Qubit): bit[] {
                    var bits: bit[] = Read(left, right);
                    X(left);
                    return bits;
                }
                operation Main(): bit[] {
                    use q = Qubit[2]; X(q[0]); return Relay(q[0], q[1]);
                }
            """, "bit[]", [1, 0]),
            ("consume", read + """
                operation Inspect(bits: bit[]): int {
                    if (bits[0] == 1 && bits[1] == 0) { return bits.Count; }
                    return 0;
                }
                operation Main(): int {
                    use q = Qubit[2]; X(q[0]);
                    var bits: bit[] = Read(q[0], q[1]);
                    return Inspect(bits);
                }
            """, "int", "2"),
            ("one-bit register", "operation Main(): bit[] { return [1]; }", "bit[]", [1]),
            ("forwarded register specialized by input length", """
                operation Make(x: bit[]): bit[] {
                    if (x.Count == 2) { return [0, 1]; }
                    return [0, 1, 0];
                }
                operation Count(bits: bit[]): int { return bits.Count; }
                operation Main(): int {
                    var pair: bit[] = [0, 0]; var triple: bit[] = [0, 0, 0];
                    return 10 * Count(Make(pair)) + Count(Make(triple));
                }
            """, "int", "23"),
            ("equal-width branches selected at runtime", """
                operation Choose(flag: bit): bit[] {
                    if (flag == 1) { return [0, 1]; }
                    return [1, 0];
                }
                operation Main(): bit[] {
                    use q = Qubit[1]; var first: bit = M(q[0]);
                    X(q[0]); var second: bit = M(q[0]);
                    var a: bit[] = Choose(first); var b: bit[] = Choose(second);
                    return [a[0], a[1], b[0], b[1]];
                }
            """, "bit[]", [1, 0, 0, 1]),
        )
        for name, source, kind, expected in cases:
            with self.subTest(case=name):
                request = self.compile_request(source)
                metadata = request["entryResult"]
                self.assertEqual(kind, metadata["type"])
                if kind == "bit[]":
                    self.assertEqual(len(expected), metadata["length"])
                task = self.device.run(self.program_type(source=request["qasm"]), shots=4).result()
                self.assertEqual(
                    {"shots": 4, "result": {"type": kind, "values": [expected] * 4}},
                    entry_result_from_task(task, metadata, 4),
                )

    def test_compiled_phase_gates_in_tools_and_vscode_runner(self):
        # Between the two H gates, Z changes the final bit to 1; an inverse
        # pair restores the relative phase of |+>, so the final bit must be 0.
        cases = (
            (("S", "S"), 1),
            (("T", "T", "T", "T"), 1),
            (("S", "Sdg"), 0),
            (("T", "Tdg"), 0),
        )
        shots = 128
        for sequence, bit in cases:
            expected = {"shots": shots, "result": {"type": "bit", "values": [bit] * shots}}
            gates = " ".join(f"{gate}(q[0]);" for gate in sequence)
            source = (
                "operation Main(): bit { use q = Qubit[1]; "
                f"H(q[0]); {gates} H(q[0]); return M(q[0]); }}"
            )
            request = self.compile_request(source)

            with self.subTest(sequence=sequence, library="tools/stdgates.inc"):
                metadata = request["entryResult"]
                reply = self.run_output(request["qasm"], metadata["type"], metadata["outputName"], shots)
                self.assertEqual(expected, reply)

            with self.subTest(sequence=sequence, library="vscode/runner/stdgates.inc"):
                process = subprocess.run(
                    [sys.executable, os.path.join(RUNNER, "braket_run.py"), "--shots", str(shots)],
                    input=json.dumps(request), text=True, encoding="utf-8",
                    capture_output=True, timeout=60,
                )
                self.assertEqual(0, process.returncode, process.stderr + process.stdout)
                self.assertEqual(expected, json.loads(process.stdout))

    def test_compiled_p_and_controlled_p_interference_returns(self):
        # H-P(pi)-H flips |0> deterministically. The controlled version only
        # flips it when the separate control was prepared in |1>.
        source = """
            operation Main(): bit[] {
                use q = Qubit[4];
                H(q[1]); P(pi, q[1]); H(q[1]);
                H(q[2]); Controlled P(pi, q[0], q[2]); H(q[2]);
                X(q[0]);
                H(q[3]); Controlled P(pi, q[0], q[3]); H(q[3]);
                return [M(q[1]), M(q[2]), M(q[3])];
            }
        """
        request = self.compile_request(source)
        shots = 16
        result = self.device.run(self.program_type(source=request["qasm"]), shots=shots).result()
        self.assertEqual(
            {"shots": shots, "result": {"type": "bit[]", "values": [[1, 0, 1]] * shots}},
            entry_result_from_task(result, request["entryResult"], shots),
        )

    def test_compiled_p_and_all_controlled_gate_matrices(self):
        self.check_gate_matrices(noncontiguous=False)

    def test_rotation_angle_argument_wraps_to_one_turn(self):
        # Check the actual value of a Qora angle independently of any gate matrix.
        for radians in (-1.17, 7.03):
            with self.subTest(radians=radians):
                source = f"operation Main(): angle {{ var theta: angle = {radians}; return theta; }}"
                request = self.compile_request(source)
                name = request["entryResult"]["outputName"]
                observed = self.run_output(request["qasm"], "angle", name, shots=1)
                self.assertAlmostEqual(radians % math.tau, observed["result"]["values"][0])

    @unittest.skipUnless(os.environ.get("QORA_TEST_NONCONTIGUOUS") == "1",
                         "opt-in Braket noncontiguous-control backend diagnostic; see README")
    def test_compiled_controlled_gates_on_noncontiguous_qubits(self):
        self.check_gate_matrices(noncontiguous=True)
        # Same routing error as a regular entry result, without a state-vector pragma.
        with self.subTest(gate="Controlled P(pi)", control=1, target=3, result="measured bit"):
            request = self.compile_request("""
                operation Main(): bit {
                    use q = Qubit[4];
                    X(q[0]); X(q[1]); X(q[2]);
                    H(q[3]); Controlled P(pi, q[1], q[3]); H(q[3]);
                    return M(q[3]);
                }
            """)
            reply = self.run_output(request["qasm"], "bit", request["entryResult"]["outputName"])
            self.assertEqual([1] * 4, reply["result"]["values"])

    @unittest.skipUnless(os.environ.get("QORA_TEST_NONCONTIGUOUS") == "1",
                         "opt-in Braket noncontiguous-control backend diagnostic; see README")
    def test_noncontiguous_control_with_no_compiler_or_gate_library(self):
        # Every qubit is active. q[1] is |1>, so both spellings must flip q[3].
        # No compiler, include file, or tensor-axis interpretation is involved.
        for instruction in ("cnot", "ctrl @ x"):
            with self.subTest(instruction=instruction):
                source = f"""
                    OPENQASM 3; qubit[4] q; output bit returned;
                    i q[0]; x q[1]; i q[2]; {instruction} q[1], q[3];
                    returned = measure q[3];
                """
                values = self.run_output(source, "bit", shots=4)["result"]["values"]
                self.assertEqual([1] * 4, values)

    @staticmethod
    def gate_matrix_cases(angles, include_plain_p):
        import numpy as np
        from braket.circuits import Gate

        # SDK matrices are an independent reference: the simulator executes the
        # compiler's OpenQASM through the shipped gate definitions instead.
        fixed = {
            "H": Gate.H(), "X": Gate.X(), "Y": Gate.Y(), "Z": Gate.Z(),
            "S": Gate.S(), "Sdg": Gate.Si(), "T": Gate.T(), "Tdg": Gate.Ti(),
            "CX": Gate.CNot(), "CNOT": Gate.CNot(), "CY": Gate.CY(),
            "CZ": Gate.CZ(), "SWAP": Gate.Swap(), "CCX": Gate.CCNot(),
        }
        cases = [(name, True, None, gate) for name, gate in fixed.items()]
        for angle in angles:
            # Qora's rotation parameter is an angle, reduced modulo 2π. Under
            # control, a 2π shift of Rx/Ry/Rz changes the relative sign, so use
            # the typed argument. P itself is 2π-periodic even under control:
            # its expected matrix can be computed from the signed source value.
            for name, rotation in (("Rx", Gate.Rx), ("Ry", Gate.Ry), ("Rz", Gate.Rz)):
                cases.append((name, True, angle, rotation(angle % math.tau)))
            cases.append(("P", True, angle, Gate.PhaseShift(angle)))
            if include_plain_p:
                cases.append(("P", False, angle, Gate.PhaseShift(angle)))

        for name, controlled, angle, gate in cases:
            matrix = gate.to_matrix()
            if controlled:
                # I on the new control's |0> branch, the entire original gate
                # on its |1> branch, including any existing controls.
                size = len(matrix)
                expected = np.eye(2 * size, dtype=complex)
                expected[size:, size:] = matrix
            else:
                expected = matrix
            yield name, controlled, angle, gate.qubit_count + int(controlled), expected

    @staticmethod
    def gate_call(name, controlled, angle, operands):
        args = ([] if angle is None else [str(angle)]) + [f"q[{i}]" for i in operands]
        return ("Controlled " if controlled else "") + f"{name}({', '.join(args)});"

    @staticmethod
    def assert_equal_up_to_global_phase(actual, expected):
        import numpy as np

        # Never align components/columns independently: that would mask a wrong
        # relative phase. Works for a state vector or a complete gate matrix.
        phase = np.vdot(expected, actual) / np.vdot(expected, expected)
        np.testing.assert_allclose(abs(phase), 1, atol=1e-9, rtol=0)
        np.testing.assert_allclose(actual, phase * expected, atol=1e-9, rtol=0)

    def check_gate_matrices(self, noncontiguous):
        import numpy as np

        tools_directory = os.path.dirname(os.path.abspath(__file__))
        angles = (0.731,) if noncontiguous else (0.0, 0.731, -1.17, 7.03)
        for name, controlled, angle, count, expected in self.gate_matrix_cases(angles, not noncontiguous):
            with self.subTest(gate=name, controlled=controlled, angle=angle):
                # One Bell pair per operand. Before the gate the state is
                # sum_x |x>|x> / sqrt(d); afterward it is sum_x (U|x>)|x> / sqrt(d).
                # Each spectator label x selects a whole column of U. This tests
                # all basis inputs *and* relative phases between columns in one run.
                dimension = 2 ** count
                if noncontiguous:
                    operands = list(range(1, 2 * count, 2))
                    references = list(range(0, 2 * count, 2))
                else:
                    operands = list(range(count))
                    references = list(range(count, 2 * count))
                applied = self.gate_call(name, controlled, angle, operands)
                source = f"operation Main() {{ use q = Qubit[{2 * count}]; {applied} }}"
                request = self.compile_request(source)
                # Prepare an independent input with Braket's native CNOT, not the
                # controlled-gate implementation under test. The compiled operation
                # and Qora's actual output declarations are left unchanged.
                declaration = f"qubit[{2 * count}] q;"
                self.assertEqual(1, request["qasm"].count(declaration))
                pairs = "\n".join(f"h q[{i}]; cnot q[{i}], q[{j}];"
                                  for i, j in zip(operands, references))
                qasm = request["qasm"].replace(declaration, declaration + "\n" + pairs)
                qasm += "\n#pragma braket result state_vector\n"
                for directory in (tools_directory, RUNNER):
                    with self.subTest(library=directory), chdir(directory):
                        state = self.device.run(self.program_type(source=qasm), shots=0).result().values[0]
                        axes = np.reshape(state, [2] * (2 * count))
                        ordered = np.transpose(axes, operands + references)
                        actual = ordered.reshape(dimension, dimension) * math.sqrt(dimension)
                        self.assert_equal_up_to_global_phase(actual, expected)

    @unittest.skipUnless(os.environ.get("QORA_TEST_ALL_LAYOUTS") == "1",
                         "opt-in exhaustive four-qubit Braket layout diagnostic; see README")
    def test_compiled_gates_on_every_four_qubit_placement(self):
        import numpy as np

        # Distinct, nonzero amplitudes on all four wires expose permutation errors.
        # Both preparation and gate application are unmodified compiler output.
        initial = np.ones(1, dtype=complex)
        preparation = []
        for i, theta in enumerate((0.41, 0.67, 1.03, 1.41)):
            preparation.append(f"Ry({theta}, q[{i}]);")
            initial = np.kron(initial, [math.cos(theta / 2), math.sin(theta / 2)])
        prefix = "operation Main() { use q = Qubit[4]; " + " ".join(preparation)
        for name, controlled, angle, count, matrix in self.gate_matrix_cases((0.731,), True):
            for operands in permutations(range(4), count):
                with self.subTest(gate=name, controlled=controlled, operands=operands):
                    applied = self.gate_call(name, controlled, angle, operands)
                    qasm = self.compile_request(prefix + " " + applied + " }")["qasm"]
                    program = self.program_type(source=qasm + "\n#pragma braket result state_vector\n")
                    actual = self.device.run(program, shots=0).result().values[0]

                    # Apply the reference gate on the chosen axes, leaving the
                    # other qubits untouched, then restore the original wire order.
                    axes = operands + tuple(i for i in range(4) if i not in operands)
                    columns = initial.reshape((2,) * 4).transpose(axes).reshape(len(matrix), -1)
                    result = (matrix @ columns).reshape((2,) * 4)
                    expected = result.transpose(np.argsort(axes)).ravel()
                    self.assert_equal_up_to_global_phase(actual, expected)


if __name__ == "__main__":
    unittest.main()
