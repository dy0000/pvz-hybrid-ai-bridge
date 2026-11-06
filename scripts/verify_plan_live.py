"""Explicit live acceptance fixture for Adventure 1-1; not the Mod's strategy.

Uses one connection, a fresh evidence directory, normal game rules and a single
bounded plan. Finishes reward settlement before returning control. No UI input.
"""
import json
from pathlib import Path
import sys
import time
import uuid
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from pvz_ai import PvzClient, BridgeError

RUN = ROOT / "artifacts" / (datetime.now().strftime("plan-live-%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8])
RUN.mkdir(parents=True)
game = PvzClient()
index = 0
events = []


def call(op, **args):
    global index
    response = game.request(op, **args)
    index += 1
    (RUN / f"{index:04d}.json").write_text(json.dumps({"command": {"op": op, **args}, "response": response}, ensure_ascii=False, indent=2), encoding="utf-8")
    if not response.get("ok"):
        raise RuntimeError(response)
    return response


def emit(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def navigate(action, timeout=25):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        menu = call("menu")["menu"]
        if any(a["id"] == action for a in menu["actions"]):
            call("menu_action", menu_id=menu["menu_id"], action_id=action)
            return
        if menu.get("unsupported_dialog"):
            raise RuntimeError(menu)
        time.sleep(.2)
    raise TimeoutError(f"Menu action did not become available: {action}")


emit({"run": str(RUN)})
try:
    ready_deadline = time.monotonic() + 30
    while True:
        try:
            cap = call("capabilities")
            break
        except BridgeError:
            # Read-only startup probe; never replay a gameplay command.
            if time.monotonic() >= ready_deadline:
                raise
            time.sleep(.5)
    if "set_plant_plan" not in cap["actions"]:
        raise RuntimeError("New Mod is not loaded")
    if cap.get("local_decisions_enabled") is False:
        raise RuntimeError("Local planting decisions are disabled by the user; this plan fixture will not enable them. Use verify_live_tools.py instead.")
    state = call("attach")["state"]
    if state.get("battle_id"):
        raise RuntimeError("Fixture requires a menu boundary, not an existing battle")
    if cap.get("features", {}).get("bounded_flow"):
        call("flow_start", max_levels=1, max_retries=0, max_seconds=360)
    menu = call("menu")["menu"]
    if menu["scene"] == "" or "loading" in menu:
        navigate("enter_game")
    # Choose only options returned by the game. No unlocking or save modification.
    for action in ("adventure", "chapter:0", "level:0"):
        navigate(action)

    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        state = call("observe")["state"]
        if state.get("running") and state.get("cards"):
            break
        menu = call("menu")["menu"]
        if any(a["id"] == "continue_dialogue" for a in menu["actions"]):
            call("menu_action", menu_id=menu["menu_id"], action_id="continue_dialogue")
        time.sleep(.15)
    else:
        raise TimeoutError("Battle did not start")
    battle = state["battle_id"]
    card = next(c for c in state["cards"] if c["content_id"] == "PlantSunflowerPea")
    call("auto_collect", battle_id=battle, enabled=True)
    # Zero-budget plan proves cancellation without spending or pausing the game.
    cancel_id = uuid.uuid4().hex
    call("set_plant_plan", battle_id=battle, plan_id=cancel_id, sun_budget=0, ttl_seconds=30,
         jobs=[{"id": "cancel-check", "content_id": card["content_id"], "cells": [{"column": 1, "row": 3}]}])
    cancelled = call("cancel_plant_plan", battle_id=battle, plan_id=cancel_id)["plan"]
    assert cancelled["status"] == "cancelled" and cancelled["spent"] == 0 and not cancelled["in_flight"]
    plan_id = uuid.uuid4().hex
    call("set_plant_plan", battle_id=battle, plan_id=plan_id, sun_budget=750, reserve_sun=25, ttl_seconds=240,
         emergency_priority=True, threat_column=4, jobs=[
             {"id": "economy-high-priority", "content_id": card["content_id"], "role": "economy", "priority": 100,
              "threatened_only": True, "cells": [{"column": 6, "row": 3}]},
             {"id": "cover-lane", "content_id": card["content_id"], "role": "attack", "priority": 10, "max_uses": 5,
              "threatened_only": True, "cells": [{"column": c, "row": 3} for c in range(1, 6)]},
         ])
    emit({"battle": battle, "plan": plan_id, "cancel_verified": True, "submitted_at_game_seconds": state["simulation_seconds"]})
    cursor = 0
    reward_started = False
    wave_seen = None
    max_auto_collected = 0
    deadline = time.monotonic() + 360
    while time.monotonic() < deadline:
        if (RUN / "stop.request").exists():
            call("cancel_plant_plan", battle_id=battle, plan_id=plan_id)
            raise RuntimeError("Stopped by stop.request")
        state = call("observe")["state"]
        if state.get("control_locked_by_player") or not state.get("ai_attached"):
            raise RuntimeError("Control released or locked by player; no reattachment")
        max_auto_collected = max(max_auto_collected, state.get("auto_collection_started", 0))
        plan = call("plant_plan_status", after_event_id=cursor)["plan"]
        if plan and plan["plan_id"] == plan_id:
            cursor = plan["event_cursor"]
            for event in plan["events"]:
                events.append(event)
                emit(event)
            if plan["status"] in ("unknown_result", "rejected", "native_timeout", "bridge_error"):
                raise RuntimeError(plan)
        if state.get("lost"):
            raise RuntimeError("Game reported defeat")
        wave = state.get("wave", {}).get("current")
        if wave is not None and wave != wave_seen:
            wave_seen = wave
            emit({"wave": wave, "sun": state.get("sun"), "auto_collected": max_auto_collected})
        if state.get("battle_awarded") or reward_started:
            menu = call("menu")["menu"]
            ids = {a["id"] for a in menu["actions"]}
            if "collect_victory_reward" in ids and not reward_started:
                call("menu_action", menu_id=menu["menu_id"], action_id="collect_victory_reward")
                reward_started = True
            elif "finish_level" in ids:
                call("menu_action", menu_id=menu["menu_id"], action_id="finish_level")
            elif reward_started and menu["scene"] == "LevelChoose":
                break
        time.sleep(.5)
    else:
        raise TimeoutError("Acceptance deadline exceeded")
    call("detach")
    final = call("observe")["state"]
    commits = [e for e in events if e["kind"] == "plant_committed"]
    assert final["scene"] == "LevelChoose" and not final["paused"] and not final["ai_attached"]
    assert len(commits) == 6 and commits[0]["job_id"] == "cover-lane" and commits[0]["reason"] == "uncovered_lane"
    summary = {"ok": True, "version": cap["bridge_version"], "battle": battle, "plan_id": plan_id,
               "committed_plants": len(commits), "spent": sum(e["cost"] for e in commits), "auto_collected": max_auto_collected,
               "cancel_verified": True, "emergency_preemption_verified": True, "native_reward_return_verified": True,
               "max_decision_ms": max(e["decision_ms"] for e in commits),
               "max_native_commit_ms": max(e["native_commit_ms"] for e in commits),
               "max_scan_gap_ms": max(e["scan_gap_ms"] for e in commits),
               "final_scene": final["scene"], "paused": final["paused"], "ai_attached": final["ai_attached"]}
    (RUN / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    emit(summary)
finally:
    game.close()
