import re
import unittest
from pathlib import Path

# The Container App must stay pinned at exactly one replica: the refresh workers and
# the in-memory snapshot state are process-local and have no cross-instance
# coordination, so scaling out silently duplicates work and serves stale/divergent
# snapshots per instance. This reads the real deployment template rather than a
# copied constant, so it fails the moment the deployed value drifts.
BICEP_PATH = Path(__file__).resolve().parents[2] / "deploy" / "bicep" / "main.bicep"

SCALE = re.compile(r"scale:\s*\{\s*minReplicas:\s*(\d+)\s*maxReplicas:\s*(\d+)\s*\}")


def container_app_scale(text):
    """Return (min, max) from the `resource app` block only, or None if it has no match."""
    app_match = re.search(r"resource\s+app\s+'Microsoft\.App/containerApps@[^']+'\s*=\s*\{", text)
    if app_match is None:
        raise ValueError("no 'resource app' Container App declaration")
    # shortcut: braces inside string literals are not skipped; fine for this template, upgrade
    # to compiled ARM JSON if main.bicep starts embedding brace-bearing strings.
    depth = 1
    end = app_match.end()
    while depth and end < len(text):
        depth += {"{": 1, "}": -1}.get(text[end], 0)
        end += 1
    match = SCALE.search(text[app_match.end() : end])
    return match.groups() if match else None


class BicepSingleReplicaTests(unittest.TestCase):
    def test_container_app_scale_bounds_are_pinned_to_one(self):
        text = BICEP_PATH.read_text(encoding="utf-8")

        scale = container_app_scale(text)
        self.assertIsNotNone(
            scale, f"Could not find a Container App scale block with minReplicas/maxReplicas in {BICEP_PATH}"
        )
        min_replicas, max_replicas = scale
        self.assertEqual(min_replicas, "1", "Container App minReplicas must stay 1 (process-local state).")
        self.assertEqual(max_replicas, "1", "Container App maxReplicas must stay 1 (process-local state).")

    def test_a_later_resource_with_one_by_one_scale_cannot_mask_the_app(self):
        text = (
            "resource app 'Microsoft.App/containerApps@2024-03-01' = {\n"
            "  properties: { template: { scale: {\n    minReplicas: 1\n    maxReplicas: 5\n  } } }\n}\n"
            "resource other 'Microsoft.App/jobs@2024-03-01' = {\n"
            "  scale: {\n    minReplicas: 1\n    maxReplicas: 1\n  }\n}\n"
        )
        self.assertEqual(container_app_scale(text), ("1", "5"))

    def test_app_without_a_scale_block_is_not_satisfied_by_a_later_resource(self):
        text = (
            "resource app 'Microsoft.App/containerApps@2024-03-01' = {\n  properties: {}\n}\n"
            "resource other 'Microsoft.App/jobs@2024-03-01' = {\n"
            "  scale: {\n    minReplicas: 1\n    maxReplicas: 1\n  }\n}\n"
        )
        self.assertIsNone(container_app_scale(text))


if __name__ == "__main__":
    unittest.main()
