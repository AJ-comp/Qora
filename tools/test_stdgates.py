"""Run with: python -m unittest discover -s tools -p test_stdgates.py."""

import os
from pathlib import Path
import sys
import unittest

TOOLS = Path(__file__).resolve().parent
RUNNER = TOOLS.parent / "vscode" / "runner"
sys.path.insert(0, str(RUNNER))
from braket_results import entry_result_from_task


class StandardGateAssetTests(unittest.TestCase):
    def test_tools_and_vscode_ship_identical_gate_definitions(self):
        self.assertEqual(
            (TOOLS / "stdgates.inc").read_bytes(),
            (RUNNER / "stdgates.inc").read_bytes(),
        )


class StandardGateExecutionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            from braket.devices import LocalSimulator
            from braket.ir.openqasm import Program
        except ImportError as error:
            raise unittest.SkipTest(
                "Install tools/requirements.txt for Braket integration tests"
            ) from error
        cls.device = LocalSimulator()
        cls.program_type = Program

    def assert_returned_bit(self, body, expected):
        source = (
            'OPENQASM 3.0; include "stdgates.inc"; '
            "output bit returned; qubit q; reset q; "
            + body
            + " returned = measure q;"
        )
        shots = 32
        for directory in (TOOLS, RUNNER):
            with self.subTest(library=str(directory / "stdgates.inc")):
                previous_directory = os.getcwd()
                try:
                    # Resolve the actual include exactly as each shipped runner does.
                    os.chdir(directory)
                    task = self.device.run(self.program_type(source=source), shots=shots).result()
                finally:
                    os.chdir(previous_directory)
                reply = entry_result_from_task(
                    task, {"type": "bit", "outputName": "returned"}, shots
                )
                self.assertEqual([expected] * shots, reply["result"]["values"])

    def test_s_squared_is_z(self):
        self.assert_returned_bit("h q; s q; s q; h q;", 1)

    def test_sdg_squared_is_z(self):
        self.assert_returned_bit("h q; sdg q; sdg q; h q;", 1)

    def test_t_fourth_power_is_z(self):
        self.assert_returned_bit("h q; t q; t q; t q; t q; h q;", 1)

    def test_tdg_fourth_power_is_z(self):
        self.assert_returned_bit("h q; tdg q; tdg q; tdg q; tdg q; h q;", 1)

    def test_sx_squared_is_x(self):
        self.assert_returned_bit("sx q; sx q;", 1)

    def test_square_roots_have_the_correct_phase_direction(self):
        # Built-in U supplies an independent inverse rotation. Repetition alone
        # would also accept the inverse square root, so check the direction too.
        cases = (
            ("s", "h q; s q; U(0, 0, -pi/2) q; h q;"),
            ("sdg", "h q; sdg q; U(0, 0, pi/2) q; h q;"),
            ("t", "h q; t q; t q; U(0, 0, -pi/2) q; h q;"),
            ("tdg", "h q; tdg q; tdg q; U(0, 0, pi/2) q; h q;"),
            ("sx", "sx q; U(-pi/2, -pi/2, pi/2) q;"),
        )
        for gate, body in cases:
            with self.subTest(gate=gate):
                self.assert_returned_bit(body, 0)


if __name__ == "__main__":
    unittest.main()
