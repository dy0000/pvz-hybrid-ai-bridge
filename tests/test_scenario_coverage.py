import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("coverage_collector", Path(__file__).resolve().parents[1] / "scripts/collect_scenario_coverage.py")
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class CoverageTests(unittest.TestCase):
    def test_mechanics_observation_is_not_completed_action_coverage(self):
        self.assertEqual(collector.facts({"mechanics": {"glove": None, "gem_match": {"board": [{"status": "unknown"}]}, "quiz": {"started": False}}}), set())
        state = {"mechanics": {"column_planting": True, "gem_match": {"board": [{"status": "ready"}]}, "quiz": {"started": True}},
                 "entities": [{"interaction": {"draggable": True}}]}
        self.assertEqual(collector.facts(state), {"column_planting", "gem_match", "quiz", "native_drag"})
        self.assertEqual(collector.facts({}, {"phase": "star_rewards"}), {"star_rewards"})

    def test_unknown_is_not_absence_or_positive_coverage(self):
        self.assertEqual(collector.facts({"cells": None, "obstacles": {"status": "unknown", "items": None}}), set())
        cell = {"status": "ready", "water": True, "terrain": ["BRICK"], "support_layer_ids": ["a"], "has_upper_plant": None, "layers_status": "partial"}
        self.assertEqual(collector.facts({"cells": [cell]}), {"water", "roof"})
        cell.update(has_upper_plant=False, layers_status="ready")
        self.assertIn("support_without_upper", collector.facts({"cells": [cell]}))

    def test_saved_delta_evidence_and_missing_baseline(self):
        with tempfile.TemporaryDirectory() as folder:
            first, second = Path(folder) / "first.json", Path(folder) / "second.json"
            first.write_text(json.dumps({"ok": True, "kind": "full", "snapshot_id": "one", "state": {"battle_id": "b", "scene": "TowerDefense", "selecting_cards": True}}))
            second.write_text(json.dumps({"ok": True, "kind": "delta", "base_snapshot_id": "one", "snapshot_id": "two", "changes": {"obstacles": {"items": [{"status": "known", "kind": "crater"}]}}, "removed_fields": []}))
            report = collector.collect([first, second])
            self.assertEqual(report["scenarios"]["crater"]["status"], "observed")
            self.assertEqual(report["scenarios"]["water"]["status"], "not_observed")
            self.assertEqual(report["errors"], [])
            self.assertTrue(collector.collect([second])["errors"])


if __name__ == "__main__": unittest.main()
