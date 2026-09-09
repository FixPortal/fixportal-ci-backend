import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("assert_gate_coverage.py")

# A gate must fail for BOTH terminal results: a cancelled dependency is not a passing
# one. Every condition a test expects to be ACCEPTED therefore covers both.
BOTH = "contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')"


class GateCoverageTests(unittest.TestCase):
    def run_checker(self, condition, command):
        workflow = f"""name: test
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: true
  ci-gate:
    if: always()
    needs: [build]
    runs-on: ubuntu-latest
    steps:
      - if: {condition}
        run: {command}
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "ci.yml"
            path.write_text(workflow, encoding="utf-8")
            env = os.environ.copy()
            for name in ("GATE_EXEMPT", "GATE_CONDITIONAL_EXEMPT", "GATE_FILE_EXEMPT", "GATE_JOB"):
                env.pop(name, None)
            return subprocess.run(
                [sys.executable, str(SCRIPT), str(path)],
                capture_output=True,
                check=False,
                env=env,
                text=True,
            )

    def test_statically_false_conditions_do_not_enforce_a_dependency(self):
        for condition in (
            "false && contains(needs.*.result, 'failure')",
            "contains(needs.*.result, 'failure') && 'left' == 'right'",
            "contains(needs.*.result, 'failure') && 1 == 2",
            "contains(needs.*.result, 'failure') && 'same' != 'SAME'",
            '"!true && contains(needs.*.result, \'failure\')"',
            "contains(needs.*.result, 'failure') && 1 > 2",
        ):
            with self.subTest(condition=condition):
                result = self.run_checker(condition, "exit 1")
                self.assertNotEqual(0, result.returncode)
                self.assertIn("has no step whose `if:` references a", result.stderr)

    def test_literal_comparisons_follow_github_equality_semantics(self):
        for comparison in ("'VALUE' == 'value'", "'1' == 1", "! false", "2 > 1"):
            condition = f"({BOTH}) && {comparison}"
            with self.subTest(comparison=comparison):
                self.assertEqual(0, self.run_checker(condition, "exit 1").returncode)

    def test_quoted_yaml_run_scalars_are_decoded_before_shell_inspection(self):
        condition = BOTH
        for command in (
            '"exit 1"',
            "'exit 1'",
            '"echo \\"blocked\\"; exit 1"',
            "'echo ''blocked''; exit 1'",
            '"echo \'#blocked\'; exit 1"',
            '"exit 1" # gate',
        ):
            with self.subTest(command=command):
                self.assertEqual(0, self.run_checker(condition, command).returncode)

    def test_exit_status_must_be_in_the_shell_nonzero_range(self):
        condition = BOTH
        self.assertEqual(0, self.run_checker(condition, "exit 255").returncode)
        result = self.run_checker(condition, "exit 256")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("not a recognised failing form", result.stderr)


if __name__ == "__main__":
    unittest.main()
