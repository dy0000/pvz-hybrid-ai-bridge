"""Read saved Mod/Studio snapshots; never connects, unlocks, or drives input.

Reports observed facts, not proof that a scenario was played successfully.
Pass files in chronological order when reconstructing observe_delta replies.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from pvz_ai.observation import apply_observation

SCENARIOS = ("water", "roof", "pot_support", "lilypad_support", "support_with_upper",
             "support_without_upper", "ice_trail", "crater", "gravestone",
             "card_selection", "retry_offered", "plant_target_card", "zombie_target_card",
             "falling_card", "column_planting", "vase", "hammer", "glove", "slot_machine", "gem_match",
             "quiz", "coin_collection", "entity_skill", "native_drag", "cell_sliding", "portal", "formation_goal",
             "star_rewards", "daily_rewards", "trial_selection", "equipment_selection")


def facts(state: dict, menu: dict | None = None) -> set[str]:
    found = set()
    for cell in state.get("cells") or []:
        if cell.get("status") != "ready":
            continue
        terrain = cell.get("terrain") or []
        if cell.get("water") is True: found.add("water")
        if "BRICK" in terrain: found.add("roof")
        for layer in cell.get("layers") or []:
            if layer.get("preview_only") or layer.get("grid_phantom"):
                continue
            if layer.get("is_support") is True:
                if layer.get("provides_terrain") == "POT": found.add("pot_support")
                if layer.get("provides_terrain") == "LILYPAD": found.add("lilypad_support")
        if cell.get("support_layer_ids"):
            if cell.get("has_upper_plant") is True: found.add("support_with_upper")
            elif cell.get("has_upper_plant") is False and cell.get("layers_status") == "ready": found.add("support_without_upper")
    for obstacle in (state.get("obstacles") or {}).get("items") or []:
        if obstacle.get("status") == "known" and not obstacle.get("preview_only"):
            if obstacle.get("kind") in ("ice_trail", "crater", "gravestone"):
                found.add(obstacle["kind"])
    if state.get("selecting_cards") is True: found.add("card_selection")
    for card in state.get("cards") or []:
        if card.get("requires_plant_target") is True: found.add("plant_target_card")
        if card.get("can_target_zombies") is True: found.add("zombie_target_card")
        if card.get("source") == "falling_card": found.add("falling_card")
    mechanics = state.get("mechanics") or {}
    if mechanics.get("column_planting") is True: found.add("column_planting")
    if mechanics.get("vases"): found.add("vase")
    if mechanics.get("coins"): found.add("coin_collection")
    if mechanics.get("portals"): found.add("portal")
    if (mechanics.get("formation_goal") or {}).get("targets"): found.add("formation_goal")
    for key in ("hammer", "glove"):
        if (mechanics.get(key) or {}).get("enabled") is True: found.add(key)
    if isinstance(mechanics.get("slot_machine"), dict): found.add("slot_machine")
    if any(p.get("status") == "ready" for p in (mechanics.get("gem_match") or {}).get("board") or []): found.add("gem_match")
    if (mechanics.get("quiz") or {}).get("started") is True: found.add("quiz")
    for entity in state.get("entities") or []:
        interaction = entity.get("interaction") or {}
        if interaction.get("pointer_skill") or interaction.get("button_skill"): found.add("entity_skill")
        if interaction.get("draggable"): found.add("native_drag")
        if interaction.get("slidable_with_cell"): found.add("cell_sliding")
    phase = (menu or {}).get("phase")
    if phase in ("star_rewards", "daily_rewards", "trial_selection"): found.add(phase)
    if (menu or {}).get("props"): found.add("equipment_selection")
    if any(a.get("id", a.get("action_id")) == "retry_level" for a in (menu or {}).get("actions", [])):
        found.add("retry_offered")
    return found


def collect(paths: list[Path]) -> dict:
    evidence = {name: [] for name in SCENARIOS}
    errors = []
    baseline = None
    snapshot_id = None
    for path in paths:
        try:
            raw = path.read_bytes()
            documents = [json.loads(line) for line in raw.decode("utf-8-sig").splitlines() if line.strip()] if path.suffix == ".jsonl" else [json.loads(raw.decode("utf-8-sig"))]
            for index, doc in enumerate(documents):
                response = doc.get("response", doc)
                if response.get("ok") is False: continue
                state = response.get("state")
                if "kind" in response and "snapshot_id" in response:
                    state, snapshot_id = apply_observation(response, baseline, snapshot_id)
                    baseline = state
                elif isinstance(state, dict):
                    baseline = None
                    snapshot_id = None
                elif "battle_id" in response and "scene" in response:
                    state = response
                elif isinstance(response.get("cards"), list) and "battle_id" in response:
                    state = response  # card_info native metadata; no execution claim
                elif "menu" not in response:
                    raise ValueError("No recognized state/menu snapshot")
                record = {"file": str(path.resolve()), "record": index + 1,
                          "sha256": hashlib.sha256(raw).hexdigest(),
                          "battle_id": (state or {}).get("battle_id"),
                          "observation_id": (state or {}).get("observation_id")}
                for name in facts(state or {}, response.get("menu")):
                    if len(evidence[name]) < 16: evidence[name].append(record)
        except (ValueError, KeyError, TypeError, OSError) as exc:
            errors.append({"file": str(path), "error": str(exc)})
            baseline = None
            snapshot_id = None
    return {"schema_version": 1, "semantics": "saved_snapshot_facts_only_not_gameplay_success",
            "scenarios": {k: {"status": "observed" if v else "not_observed", "evidence": v} for k, v in evidence.items()},
            "errors": errors}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshots", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = collect(args.snapshots)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(args.output.resolve()), "observed": [k for k, v in report["scenarios"].items() if v["status"] == "observed"], "errors": report["errors"]}, ensure_ascii=False))
    return int(bool(report["errors"]))


if __name__ == "__main__": raise SystemExit(main())
