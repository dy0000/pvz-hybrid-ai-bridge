"""Live acceptance of streaming APIs using explicit single plant requests.

The local decision feature must remain disabled. One controller/connection,
fixed first-level test placements, normal resources, no UI automation.
"""
import base64
import json
from pathlib import Path
import sys
import time
import uuid
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from pvz_ai import PvzClient, BridgeError

run = ROOT / "artifacts" / (datetime.now().strftime("live-tools-%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8])
run.mkdir(parents=True)
game = PvzClient()
index = 0


def call(op, allow_error=False, **args):
    global index
    result = game.request(op, **args)
    index += 1
    if "png_base64" in result:
        pixels = base64.b64decode(result.pop("png_base64"))
        assert pixels.startswith(b"\x89PNG\r\n\x1a\n")
        (run / f"frame-{index:04d}.png").write_bytes(pixels)
    (run / f"{index:04d}.json").write_text(json.dumps({"command": {"op": op, **args}, "response": result}, ensure_ascii=False, indent=2), encoding="utf-8")
    if not allow_error and not result.get("ok"):
        raise RuntimeError(result)
    return result


def emit(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def navigate(action):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        menu = call("menu")["menu"]
        if any(a["id"] == action for a in menu["actions"]):
            call("menu_action", menu_id=menu["menu_id"], action_id=action)
            return
        time.sleep(.2)
    raise RuntimeError(menu)


emit({"run": str(run)})
try:
    deadline = time.monotonic() + 30
    while True:
        try:
            cap = call("capabilities")
            break
        except BridgeError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(.5)
    assert cap["bridge_version"].startswith("0.4.") and cap["local_decisions_enabled"] is False
    assert all(cap["features"].values())
    state = call("attach")["state"]
    assert state["battle_id"] is None
    call("flow_start", max_levels=1, max_retries=0, max_seconds=360)
    call("stream_update", goal="六项直播能力验收", thinking=True, reason="正在进入测试关卡", visible=True, ttl_ms=30000)
    m = call("menu")["menu"]
    if m["scene"] == "" or "loading" in m:
        navigate("enter_game")
    call("capture_frame", max_width=1280)
    for action in ("adventure", "chapter:0", "level:0"):
        navigate(action)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        state = call("observe")["state"]
        if state.get("running") and state.get("cards"):
            break
        m = call("menu")["menu"]
        if any(a["id"] == "continue_dialogue" for a in m["actions"]):
            call("menu_action", menu_id=m["menu_id"], action_id="continue_dialogue")
        time.sleep(.15)
    else:
        raise TimeoutError("Battle did not start")
    battle = state["battle_id"]
    card = next(c for c in state["cards"] if c["content_id"] == "PlantSunflowerPea")
    card_id = card["id"]
    call("catalog")
    call("auto_collect", battle_id=battle, enabled=True)
    blocked = call("set_plant_plan", allow_error=True, battle_id=battle, plan_id="must-stay-disabled", sun_budget=0, jobs=[])
    assert not blocked["ok"] and "disabled" in blocked["error"]
    call("stream_update", thinking=True, goal="TTL 验收", ttl_ms=500,
         intents=[{"id": "ttl-test", "content_id": card["content_id"], "column": 1, "row": 3, "status": "pending"}])
    time.sleep(.65)
    expired = call("stream_status")["status"]
    assert expired["intentions_expired"] and not expired["intentions"] and not expired["thinking"]
    diag = call("placement_check", battle_id=battle, card_id=card_id, column=1, row=3)["placement"]
    assert diag["native_cell_hint"] and not diag["requires_target"]
    next_column = 1
    cursor = 0
    event_kinds = set()
    first_frame = None
    reward = False
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        state = call("observe")["state"]
        if state.get("control_locked_by_player") or not state["ai_attached"]:
            raise RuntimeError("Player stopped control; no reattachment")
        if state.get("lost"):
            raise RuntimeError("Battle lost")
        if state.get("running") and not state.get("battle_awarded") and next_column <= 6:
            current_card = next(c for c in state["cards"] if c["id"] == card_id)
            if current_card["available"]:
                intent = "plant-" + str(next_column)
                call("stream_update", thinking=False, goal="普通规则守住本关", reason="按已确定的测试落点逐次种植", ttl_ms=30000,
                     intents=[{"id": intent, "content_id": card["content_id"], "column": next_column, "row": 3, "status": "submitting", "reason": "本轮单次种植验收"}])
                r = call("plant", battle_id=battle, card_id=card_id, column=next_column, row=3, intent_id=intent)
                assert r["outcome"] == "Committed"
                status = call("stream_status")["status"]
                assert any(i["id"] == intent and i["status"] == "committed" for i in status["intentions"])
                if next_column == 1:
                    cell = call("inspect_cell", battle_id=battle, column=1, row=3)["cell"]
                    assert cell["layers"]
                    occupied = call("placement_check", battle_id=battle, card_id=card_id, column=1, row=3)["placement"]
                    assert not occupied["native_cell_hint"]
                    invalid_target = call("use_target", allow_error=True, battle_id=battle, card_id=card_id, entity_id=cell["layers"][0]["entity_id"])
                    assert not invalid_target["ok"]
                    time.sleep(.3)
                    first_frame = call("capture_frame", max_width=1280)["path"]
                    exported = json.loads(Path(cap["status_file"]).read_text(encoding="utf-8"))
                    assert exported["battle_id"] == battle and exported["schema_version"] == 1
                    emit({"snapshot": first_frame, "status_file": cap["status_file"], "ttl_verified": True, "local_decisions_enabled": False})
                emit({"planted": next_column, "simulation_seconds": r["state"]["simulation_seconds"]})
                next_column += 1
        feed = call("events", after_event_id=cursor, limit=128)["feed"]
        cursor = feed["next_event_id"]
        event_kinds.update(e["kind"] for e in feed["events"])
        if state.get("battle_awarded") or reward:
            m = call("menu")["menu"]
            ids = {a["id"] for a in m["actions"]}
            if "collect_victory_reward" in ids and not reward:
                call("menu_action", menu_id=m["menu_id"], action_id="collect_victory_reward")
                reward = True
            elif "finish_level" in ids:
                call("menu_action", menu_id=m["menu_id"], action_id="finish_level")
            elif reward and m["scene"] == "LevelChoose" and "level:1" in ids:
                limited = call("menu_action", allow_error=True, menu_id=m["menu_id"], action_id="level:1")
                assert not limited["ok"] and "limit" in limited["error"]
                break
        time.sleep(.2)
    else:
        raise TimeoutError("Live acceptance did not settle")
    assert {"wave_changed", "plant_committed", "reward_pending", "card_ready", "zombie_type_seen"} <= event_kinds
    flow = call("flow_status")["flow"]
    assert flow["levels_started"] == 1
    call("stream_update", thinking=False, goal="验收结束，已领奖返回选关", ttl_ms=5000, visible=True)
    call("detach")
    final = call("observe")["state"]
    assert not final["paused"] and not final["ai_attached"] and final["local_decisions_enabled"] is False
    summary = {"ok": True, "version": cap["bridge_version"], "event_kinds": sorted(event_kinds), "plants": next_column-1,
               "ttl_verified": True, "receipt_intent_link_verified": True, "placement_probe_verified": True,
               "invalid_target_rejected": True, "flow_limit_verified": True, "reward_return_verified": True,
               "capture": first_frame, "status_file": cap["status_file"], "local_decisions_enabled": False,
               "final_scene": final["scene"], "paused": final["paused"], "ai_attached": final["ai_attached"],
               "not_live_covered": ["water/roof support and upgrade cards not unlocked in this fixture", "valid special entity-target card", "failure retry UI", "new-plant next-level settlement button"]}
    (run / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    emit(summary)
finally:
    game.close()
