"""Deploy and image publication must wait for the complete CI gate."""

from pathlib import Path
import unittest

import assert_gate_coverage as gate


class CiDeploySafetyTests(unittest.TestCase):
    def test_publish_and_deploy_wait_for_ci_gate(self):
        workflow = Path(__file__).resolve().parents[1] / "workflows/ci.yml"
        lines = workflow.read_text().splitlines()
        jobs, _, _ = gate.read_gate_contract(lines, "ci-gate")
        for job in ("publish", "deploy"):
            with self.subTest(job=job):
                self.assertIn("ci-gate", gate.job_needs(lines, jobs, job, 2))
        self.assertIn("publish", gate.job_needs(lines, jobs, "deploy", 2))


if __name__ == "__main__":
    unittest.main()
