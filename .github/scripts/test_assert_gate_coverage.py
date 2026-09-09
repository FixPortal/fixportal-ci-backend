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

    def run_in_repo(self, workflow, policy, scripts):
        """Run the checker over a throwaway repo, not a bare workflow file.

        assert_gate_scripts resolves .claude/review-policy.json by walking up from the
        workflow, and only asserts about scripts that EXIST, so it cannot be exercised
        by run_checker's single-file fixture at all.
        """
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".claude").mkdir()
            (root / ".claude" / "review-policy.json").write_text(policy, encoding="utf-8")
            for relative in scripts:
                script = root.joinpath(*relative.split("/"))
                script.parent.mkdir(parents=True, exist_ok=True)
                script.write_text("print(1)\n", encoding="utf-8")
            path = root / ".github" / "workflows" / "ci.yml"
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


    def test_every_dependency_must_be_gated_on_cancellation_too(self):
        """A cancelled dependency is not a passing one."""
        result = self.run_checker("contains(needs.*.result, 'failure')", "exit 1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("build:cancelled", result.stderr)

    def test_not_success_covers_both_terminal_results(self):
        """The spelling the rejection above recommends has to be accepted."""
        result = self.run_checker("needs.build.result != 'success'", "exit 1")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_a_gate_script_absent_from_the_policy_is_refused(self):
        """A script a gated job runs decides what can merge, so it must be HIGH.

        It runs from the pull request's OWN checkout, so an edit that turns a failure
        path into `exit 0` neuters the barrier using the very copy the gate executes.
        """
        workflow = f"""name: test
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: python .github/scripts/probe.py
  ci-gate:
    if: always()
    needs: [build]
    runs-on: ubuntu-latest
    steps:
      - if: {BOTH}
        run: exit 1
"""
        ungated = self.run_in_repo(
            workflow, '{"version":1,"high":[],"low":[]}', [".github/scripts/probe.py"]
        )
        self.assertNotEqual(0, ungated.returncode)
        self.assertIn("probe.py", ungated.stderr)

        covered = self.run_in_repo(
            workflow,
            '{"version":1,"high":[".github/scripts/**"],"low":[]}',
            [".github/scripts/probe.py"],
        )
        self.assertEqual(0, covered.returncode, covered.stdout + covered.stderr)

    def test_a_gate_script_in_a_commented_block_scalar_is_still_seen(self):
        """`run: | # note` is a real spelling, and the scan used to skip its payload.

        BLOCK_SCALAR is anchored, so testing the raw value matched neither branch: the
        else arm yielded the bare `|` and advanced one line, and the script inside was
        invisible. That is fail-open on this control.
        """
        workflow = f"""name: test
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: | # build log
          python .github/scripts/probe.py
  ci-gate:
    if: always()
    needs: [build]
    runs-on: ubuntu-latest
    steps:
      - if: {BOTH}
        run: exit 1
"""
        result = self.run_in_repo(
            workflow, '{"version":1,"high":[],"low":[]}', [".github/scripts/probe.py"]
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("probe.py", result.stderr)


if __name__ == "__main__":
    unittest.main()
