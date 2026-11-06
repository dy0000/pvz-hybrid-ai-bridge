"""Non-spending smoke test on an idle sandbox, one connection.

Explicitly enters the title/main menu, observes compact/delta, and previews the
naturally unlocked shop. Never starts a battle, grants a paid budget or confirms
a purchase. Run only after proving no maintained controller owns the target.
"""
import argparse
import json
from pathlib import Path
import sys
import time
import uuid
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from pvz_ai import PvzClient
from pvz_ai.observation import apply_observation


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-version", default="0.4.10")
    parser.add_argument("--selection", nargs=2, type=int, metavar=("CHAPTER", "LEVEL"), help="Explicit already-unlocked selection test; never starts battle; ends on selection screen")
    options = parser.parse_args()
    run = ROOT / "artifacts" / (datetime.now().strftime("extended-live-%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    client = PvzClient()
    index = 0
    attached = False
    shop_id = ""
    summary = {"run": str(run), "purchases_confirmed": 0, "battle_started": False}

    def call(op, **args):
        nonlocal index
        reply = client.request(op, **args)
        index += 1
        (run / f"{index:04d}.json").write_text(json.dumps({"command": {"op": op, **args}, "response": reply}, ensure_ascii=False, indent=2), encoding="utf-8")
        if not reply.get("ok"):
            raise RuntimeError(f"{op}: {reply}")
        return reply

    def wait_for(read, predicate, seconds=20):
        deadline = time.monotonic() + seconds
        while True:
            value = read()
            if predicate(value): return value
            if time.monotonic() >= deadline: raise TimeoutError(value)
            time.sleep(.25)

    try:
        cap = call("capabilities")
        assert cap["bridge_version"] == options.expected_version
        assert cap["local_decisions_enabled"] is False and cap["in_game_panel_enabled"] is False
        first = call("observe")
        assert not first["state"]["ai_attached"] and not first["state"]["control_locked_by_player"]
        assert first["state"]["battle_id"] is None, "Only idle menu smoke is allowed"
        state = call("attach", mode="realtime", background=True)["state"]
        attached = True
        menu = call("menu")["menu"]
        if menu.get("scene") != "MainMenu":
            menu = wait_for(lambda: call("menu")["menu"], lambda m: any(a["id"] == "enter_game" for a in m["actions"]))
            call("menu_action", menu_id=menu["menu_id"], action_id="enter_game")
        wait_for(lambda: call("menu")["menu"], lambda m: m.get("scene") == "MainMenu" and any(a["id"] == "adventure" for a in m["actions"]))

        full = call("observe")
        state, sid = apply_observation(full)
        delta = call("observe_delta", base_snapshot_id=sid)
        assert delta["kind"] == "delta"
        state, sid = apply_observation(delta, state, sid)
        assert state["scene"] == "MainMenu"
        packed = call("observe", view="compact")
        compact, compact_id = apply_observation(packed)
        assert compact["scene"] == state["scene"] and compact["bridge_version"] == options.expected_version
        delta = call("observe_delta", base_snapshot_id=compact_id, view="compact")
        assert delta["kind"] == "delta"
        compact, compact_id = apply_observation(delta, compact, compact_id)
        reset = call("observe_delta", base_snapshot_id=compact_id, view="full")
        assert reset["kind"] == "full" and reset["reset"] is True
        filtered = call("observe", executable_cards_only=True)["state"]
        assert all(c["available"] for c in filtered.get("cards", []))
        summary["observations"] = {"full": True, "compact_decoded": True, "delta_applied": True, "view_reset": True, "executable_view": True}
        shop = call("shop_observe")["shop"]
        summary["shop_unlocked"] = shop["unlocked"]
        if shop["unlocked"]:
            call("open_shop")
            shop = wait_for(lambda: call("shop_observe")["shop"], lambda s: s["open"] and bool(s["items"]))
            shop_id = shop["shop_id"]
            coins = shop["coins"]
            summary["shop_preview"] = shop
            for category in ("Packet", "Total"):
                call("shop_navigate", shop_id=shop_id, action=category)
                wait_for(lambda: call("shop_observe")["shop"], lambda s: s["category"] == category and bool(s["items"]))
            assert call("shop_observe")["shop"]["coins"] == coins
            call("close_shop", shop_id=shop_id)
            wait_for(lambda: call("shop_observe")["shop"], lambda s: not s["open"])
            shop_id = ""
            summary["shop_navigation_and_close"] = True
        else:
            summary["shop_navigation_and_close"] = "not_observed_native_locked"
        summary["paid_purchase_validation"] = "not_run_no_human_item_or_budget"
        if options.selection:
            call("flow_start", max_levels=1, max_retries=0, max_seconds=120)
            for action in ("adventure", f"chapter:{options.selection[0]}", f"level:{options.selection[1]}"):
                menu = wait_for(lambda: call("menu")["menu"], lambda m: any(a["id"] == action for a in m["actions"]))
                call("menu_action", menu_id=menu["menu_id"], action_id=action)
            state = wait_for(lambda: call("observe")["state"], lambda s: s.get("selecting_cards") is True)
            assert not state["running"] and not state["chosen_cards"], "Selection test only starts with empty native lineup"
            battle = state["battle_id"]
            first_card, second_card = state["available_choices"][:2]
            metadata = call("card_info", content_ids=[first_card, second_card])
            assert len(metadata["cards"]) == 2
            call("choose_card", battle_id=battle, content_id=first_card)
            chosen_state = wait_for(lambda: call("observe")["state"], lambda s: bool(s["chosen_cards"]))
            assert chosen_state["selection_edit_enabled"] is True
            assert chosen_state["chosen_card_details"] == [{"content_id": first_card, "locked": False}]
            changed = call("replace_card", battle_id=battle, content_id=first_card, replacement_content_id=second_card)
            assert changed["outcome"] == "selection_changed" and changed["removed"] and changed["replacement_selected"]
            removed = call("deselect_card", battle_id=battle, content_id=second_card)
            assert removed["outcome"] == "selection_changed" and not removed["state"]["chosen_cards"]
            call("flow_stop")
            summary["selection"] = {"chapter_index": options.selection[0], "level_index": options.selection[1], "card_info": True, "choose_replace_deselect": True, "lineup_restored_empty": True}
    except Exception as exc:
        summary["error"] = str(exc)
        raise
    finally:
        try:
            if attached:
                if shop_id:
                    call("close_shop", shop_id=shop_id)
                    wait_for(lambda: call("shop_observe")["shop"], lambda s: not s["open"])
                call("detach")
                state = call("observe")["state"]
                summary["released"] = not state["ai_attached"]
                summary["final_scene"] = state["scene"]
                summary["paused"] = state["paused"]
        finally:
            client.close()
            (run / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps({k:v for k,v in summary.items() if k != "shop_preview"}, ensure_ascii=False), flush=True)


if __name__ == "__main__": main()
