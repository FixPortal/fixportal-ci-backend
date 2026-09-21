import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("assert_gate_coverage.py")
HYGIENE_CHECKER = Path(__file__).with_name("assert_workflow_hygiene.py")

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

    def run_block(self, *lines):
        """run_checker with a multi-line `run:` body, as a YAML block scalar.

        The whole-body rules below cannot be exercised by a single-line body at all --
        the defect they close is an accepted form sitting on a LATER line than a
        command that already decided the step's exit status.
        """
        indented = "".join(f"\n          {line}" for line in lines)
        return self.run_checker(BOTH, f"|{indented}")

    # --- the whole-body verdict (canonical fixportal-agents-skills#176) --------------
    # ends_non_zero used to accept a body if ANY logical line matched an accepted form,
    # so the step could exit ZERO on an earlier line while the checker vouched for a
    # later one. Only message lines (echo/printf) and `set` shell-option lines may now
    # precede the failing command.

    def test_an_unreachable_exit_behind_exit_zero_is_refused(self):
        result = self.run_block("exit 0", "exit 1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("not a recognised failing form", result.stderr)

    def test_message_and_shell_option_prefixes_are_accepted(self):
        for prefix in (
            ("echo \"::error::upstream failed\"",),
            ("printf '%s\\n' \"upstream failed\"",),
            (">&2 echo \"upstream failed\"",),
            ("set -euo pipefail", "echo \"upstream failed\""),
        ):
            with self.subTest(prefix=prefix):
                result = self.run_block(*prefix, "exit 1")
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_execution_disabling_shell_options_are_refused(self):
        """`set -n` (noexec) and `set -t` (onecmd) STOP the shell before the final
        command, so the `exit 1` the checker can see never runs and the step exits
        ZERO. _SHELL_OPTION is an allowlist for exactly this reason."""
        for option in ("set -n", "set -o noexec", "set -t", "set -o onecmd"):
            with self.subTest(option=option):
                result = self.run_block(option, "exit 1")
                self.assertNotEqual(0, result.returncode)

    def test_a_non_message_prefix_is_refused(self):
        result = self.run_block("trap 'exit 0' EXIT", "exit 1")
        self.assertNotEqual(0, result.returncode)

    def test_a_command_subexpression_is_refused(self):
        """Under `shell: pwsh` a subexpression exits the step during expansion, and
        mask_quoted blanks it to a bare `echo` that reads as an ordinary message."""
        for body in ('echo "$(exit 0)"', "echo $(exit 0)"):
            with self.subTest(body=body):
                result = self.run_block(body, "exit 1")
                self.assertNotEqual(0, result.returncode)

    def test_a_guarded_throw_is_refused(self):
        """mask_quoted blanks the message, so a `.*` throw tail normalised this to
        `throw || true` -- and bash `-e` does not fire on a status `||` consumes."""
        result = self.run_checker(BOTH, "throw \"upstream failed\" || true")
        self.assertNotEqual(0, result.returncode)

    def test_a_test_guarding_the_exit_is_refused(self):
        """The `if <test>; then exit 1; fi` spellings exit ZERO when their test fails."""
        for body in (
            'if [ -z "$x" ]; then exit 1; fi',
            'if [ -z "$x" ]; then echo "missing"; exit 1; fi',
        ):
            with self.subTest(body=body):
                self.assertNotEqual(0, self.run_checker(BOTH, body).returncode)

    def test_a_github_expression_in_the_message_is_refused(self):
        """A `${{ ... }}` interpolation inside a gate run: body is refused: GitHub
        substitutes it textually before the shell parses the line, and it can splice
        a separator or an early exit into an otherwise inert message. The house gate
        keeps the expression in `env:`. This test used to pin the pre-hoist spelling
        as ACCEPTED; the canonical contract reversed (fixportal-agents-skills PR #223),
        so the fixture had to move with it."""
        result = self.run_block(
            'echo "Upstream results: ${{ join(needs.*.result, \', \') }}"',
            "exit 1",
        )
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)

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
        for condition in (
            "needs.build.result != 'success'",
            "needs['build'].result != 'success'",
            "needs.build.result != 'success' && needs.build.result != 'skipped'",
            "TRUE && needs.build.result != 'success'",
        ):
            with self.subTest(condition=condition):
                result = self.run_checker(condition, "exit 1")
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_multiline_continue_on_error_cannot_hide_a_non_failing_gate(self):
        for header in ("", ">-", "|-"):
            for value, expected in (("true", 1), ("false", 0), ("FALSE", 0)):
                with self.subTest(header=header, value=value):
                    result = self.run_checker(
                        BOTH,
                        f"exit 1\n        continue-on-error: {header}\n          {value}",
                    )
                    self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

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


    def test_a_gate_script_after_a_literal_hash_in_a_quoted_scalar_is_seen(self):
        """A `#` inside a QUOTED YAML scalar is data, not a comment.

        Truncating there hid the script that follows, so it escaped the HIGH-tier
        requirement -- fail-open on this control. Both quote styles, because their
        escape rules differ.
        """
        for command in (
            '"printf \'tag # audit\'; python .github/scripts/probe.py"',
            '\'printf "tag # audit"; python .github/scripts/probe.py\'',
        ):
            with self.subTest(command=command):
                workflow = f"""name: test
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: {command}
  ci-gate:
    if: always()
    needs: [build]
    runs-on: ubuntu-latest
    steps:
      - if: {BOTH}
        run: exit 1
"""
                result = self.run_in_repo(
                    workflow,
                    '{"version":1,"high":[],"low":[]}',
                    [".github/scripts/probe.py"],
                )
                self.assertNotEqual(0, result.returncode)
                self.assertIn("probe.py", result.stderr)

    def test_a_quoted_run_body_carrying_a_literal_hash_still_fails(self):
        """The same bug from the other side: truncating left an unterminated fragment,
        so a gate that DOES fail read as one that cannot."""
        result = self.run_checker(BOTH, '\'echo " # progress"; exit 1\' # gate')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_an_unquoted_run_value_is_truncated_by_yaml_at_a_hash(self):
        """SHELL quotes do not protect a hash from YAML.

        In a plain scalar the runner never receives what follows ` #`, so vouching for
        it would vouch for a command that does not run.
        """
        result = self.run_checker(BOTH, "echo 'tag # audit'; exit 1")
        self.assertNotEqual(0, result.returncode)


class WorkflowHygieneTests(unittest.TestCase):
    def test_local_docker_action_image_must_be_pinned(self):
        # "Dockerfile" is only exempt when it resolves to an actual file next to
        # action.yml -- otherwise a registry reference sharing that basename
        # (e.g. myregistry.example.com/Dockerfile) would wrongly read as a local
        # build. A registry-lookalike case proves that: same basename as the
        # exempt case, no local Dockerfile on disk, still must be pin-checked.
        cases = (
            ("docker://alpine:latest", 1, False),
            ("alpine:latest", 1, False),
            ("myregistry.example.com/Dockerfile", 1, False),
            ("Dockerfile", 0, True),
        )
        for image, expected_code, write_dockerfile in cases:
            with self.subTest(image=image), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                workflows = root / ".github" / "workflows"
                action = root / ".github" / "actions" / "local"
                workflows.mkdir(parents=True)
                action.mkdir(parents=True)
                (workflows / "ci.yml").write_text(
                    textwrap.dedent(
                        """\
                        on: push
                        permissions: {}
                        jobs:
                          test:
                            runs-on: ubuntu-latest
                            steps:
                              - uses: ./.github/actions/local
                        """
                    ),
                    encoding="utf-8",
                )
                (action / "action.yml").write_text(
                    textwrap.dedent(
                        f"""\
                        name: Local Docker action
                        runs:
                          using: docker
                          image: {image}
                        """
                    ),
                    encoding="utf-8",
                )
                if write_dockerfile:
                    (action / "Dockerfile").write_text("FROM scratch\n", encoding="utf-8")
                result = subprocess.run(
                    [sys.executable, str(HYGIENE_CHECKER)],
                    cwd=root,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(result.returncode, expected_code, result.stdout + result.stderr)
                if expected_code:
                    self.assertIn(image, result.stdout)


if __name__ == "__main__":
    unittest.main()
