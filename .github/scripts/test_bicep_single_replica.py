import re
import unittest
from pathlib import Path

# The Container App must stay pinned at exactly one replica: the refresh workers and
# the in-memory snapshot state are process-local and have no cross-instance
# coordination, so scaling out silently duplicates work and serves stale/divergent
# snapshots per instance. This reads the real deployment template rather than a
# copied constant, so it fails the moment the deployed value drifts.
BICEP_PATH = Path(__file__).resolve().parents[2] / "deploy" / "bicep" / "main.bicep"


class BicepSingleReplicaTests(unittest.TestCase):
    def test_container_app_scale_bounds_are_pinned_to_one(self):
        text = BICEP_PATH.read_text(encoding="utf-8")
        match = re.search(r"scale:\s*\{\s*minReplicas:\s*(\d+)\s*maxReplicas:\s*(\d+)\s*\}", text)
        self.assertIsNotNone(
            match, f"Could not find a Container App scale block with minReplicas/maxReplicas in {BICEP_PATH}"
        )
        min_replicas, max_replicas = match.group(1), match.group(2)
        self.assertEqual(min_replicas, "1", "Container App minReplicas must stay 1 (process-local state).")
        self.assertEqual(max_replicas, "1", "Container App maxReplicas must stay 1 (process-local state).")


if __name__ == "__main__":
    unittest.main()
