"""Deploy and image publication must wait for the complete CI gate; ci.yml deploy hardening:
secrets stay out of argv, the new revision is proven to serve, and gate coverage runs over
the whole workflow directory."""

from pathlib import Path
import re
import unittest

import assert_gate_coverage as gate

WORKFLOWS = Path(__file__).resolve().parents[1] / "workflows"
CI = (WORKFLOWS / "ci.yml").read_text(encoding="utf-8")
DEPLOY = CI[CI.index("\n  deploy:") : CI.index("\n  gate-coverage:")]


class CiDeploySafetyTests(unittest.TestCase):
    def test_publish_and_deploy_wait_for_ci_gate(self):
        workflow = Path(__file__).resolve().parents[1] / "workflows/ci.yml"
        lines = workflow.read_text().splitlines()
        jobs, _, _ = gate.read_gate_contract(lines, "ci-gate")
        for job in ("publish", "deploy"):
            with self.subTest(job=job):
                self.assertIn("ci-gate", gate.job_needs(lines, jobs, job, 2))
        self.assertIn("publish", gate.job_needs(lines, jobs, "deploy", 2))


class DeployHardeningTests(unittest.TestCase):
    def test_no_secret_reaches_jq_or_az_argv(self):
        self.assertNotRegex(DEPLOY, r"--arg\s", "secrets must be read through env. in the jq program")
        self.assertNotIn("--password", DEPLOY)
        self.assertNotIn("--username", DEPLOY)

    def test_new_revision_is_proven_serving_before_probes(self):
        verify = DEPLOY.index("- name: Verify new revision is serving")
        self.assertLess(DEPLOY.index("- name: Restart active revision"), verify)
        self.assertLess(verify, DEPLOY.index("- name: CI IDE snapshot probe"))
        step = DEPLOY[verify : DEPLOY.index("- name: CI IDE snapshot probe")]
        for needle in ("latestReadyRevisionName", "trafficWeight", '*":$SHA"', "Failed", "exit 1"):
            with self.subTest(needle=needle):
                self.assertIn(needle, step)


class GateCoverageModeTests(unittest.TestCase):
    def test_gate_coverage_runs_in_directory_mode_with_every_non_ci_file_named(self):
        match = re.search(
            r"GATE_FILE_EXEMPT:\s*(?P<names>.+)\n\s+run:\s*python3 \.github/scripts/assert_gate_coverage\.py \.github/workflows\s*\n",
            CI,
        )
        self.assertIsNotNone(match, "gate coverage must target .github/workflows with GATE_FILE_EXEMPT")
        named = set(match.group("names").split())
        on_disk = {f".github/workflows/{p.name}" for p in WORKFLOWS.glob("*.yml") if p.name != "ci.yml"}
        self.assertEqual(named, on_disk)


if __name__ == "__main__":
    unittest.main()
