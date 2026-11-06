import json
from pathlib import Path
import re
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from pvz_ai.mcp_server import TOOLS, handle
from pvz_ai.session_view import SessionView


class Client:
    def __init__(self):
        self.calls = []

    def request(self, op, **args):
        self.calls.append((op, args))
        return {"ok": True, "outcome": "native_action_started"}


class GameplayTests(unittest.TestCase):
    def call(self, op, args, client):
        return handle({"id": 1, "method": "tools/call", "params": {"name": "pvz_" + op, "arguments": args}}, client)

    def test_exact_new_native_identities_are_forwarded_without_rebinding(self):
        cases = [
            ("break_vase", {"battle_id": "b", "entity_id": 51}),
            ("slot_spin", {"battle_id": "b", "feature_id": "native-instance", "expected_spin_count": 8}),
            ("gem_action", {"battle_id": "b", "feature_id": "native-instance", "expected_board_revision": 19,
                            "action": "swap", "from_column": 1, "from_row": 2, "to_column": 2, "to_row": 2}),
            ("entity_interact", {"battle_id": "b", "entity_id": 52, "action": "aim", "column": 3, "row": 4}),
            ("quiz_prepare", {"battle_id": "b", "feature_id": "q", "transaction_id": "unique", "budget_id": "human", "expected_price": 300, "max_spend": 300}),
        ]
        client = Client()
        for op, args in cases:
            self.assertFalse(self.call(op, args, client)["result"].get("isError", False))
        self.assertEqual(client.calls, cases)

    def test_identity_budget_bounds_and_unknown_arguments_never_reach_client(self):
        client = Client()
        cases = [
            ("slot_spin", {"battle_id": "b", "expected_spin_count": 0}),
            ("slot_spin", {"battle_id": "b", "feature_id": "n", "expected_spin_count": True}),
            ("gem_action", {"battle_id": "b", "feature_id": "n", "expected_board_revision": -1, "action": "swap"}),
            ("auto_collect_coins", {"battle_id": "b", "enabled": 1}),
            ("quiz_configure", {"battle_id": "b", "feature_id": "q", "bets": [{"row": 1, "wager": -1, "choice": "win"}]}),
            ("quiz_configure", {"battle_id": "b", "feature_id": "q", "bets": [{"row": 1, "wager": 1, "choice": "win", "unlock": True}]}),
            ("quiz_confirm", {"battle_id": "b", "feature_id": "q", "transaction_id": "t", "skip_budget": True}),
            ("entity_interact", {"battle_id": "b", "entity_id": 1, "action": "Destroy"}),
        ]
        for op, args in cases:
            self.assertTrue(self.call(op, args, client)["result"]["isError"], (op, args))
        self.assertEqual(client.calls, [])

    def test_terminal_retains_mechanics_and_exact_interaction_ids(self):
        mechanics = {"coins": [{"id": "19:52", "value": 50}], "gem_match": {"feature_id": "a", "board_revision": 4},
                     "quiz": {"price": 150, "transaction_id": "t"}, "formation_goal": None}
        state = {"mechanics": mechanics, "entities": [{"id": 53, "content_id": "PlantRobot", "kind": "plant", "column": 2,
                  "row": 3, "hp": 300, "dead": False, "interaction": {"pointer_skill": True, "double_click_skill": True}}]}
        rendered = SessionView().render({"ok": True, "state": state})["state"]
        self.assertEqual(rendered["mechanics"], mechanics)
        self.assertEqual(rendered["interactions"][0]["entity_id"], 53)
        self.assertEqual(state["mechanics"], mechanics)
        state["mechanics"]["glove"] = None
        state["entities"][0]["interaction"] = {}
        self.assertEqual(SessionView().render({"ok": True, "state": state})["state"]["interactions"], [])
        state["mechanics"]["glove"] = {"enabled": True}
        self.assertEqual(SessionView().render({"ok": True, "state": state})["state"]["interactions"][0]["entity_id"], 53)

    def test_every_advertised_action_has_a_unique_mcp_binding(self):
        source = (Path(__file__).resolve().parents[1] / "src/PvzAiBridge/Entry.cs").read_text(encoding="utf-8-sig")
        actions = set(re.findall(r'"([a-z_]+)"', source.split('["actions"] = new JsonArray(', 1)[1].split('),', 1)[0]))
        operations = [t["_op"] for t in TOOLS]
        self.assertEqual(len(operations), len(set(operations)))
        self.assertEqual(actions, set(operations) - {"capabilities"})


if __name__ == "__main__":
    unittest.main()
