"""Terminal presentation of decoded state. Raw wire replies stay in artifacts."""
from copy import deepcopy
from .observation import apply_observation


class SessionView:
    def __init__(self):
        self.baseline = None
        self.snapshot_id = None

    def render(self, response: dict) -> dict:
        result = deepcopy(response)
        if response.get("ok") and response.get("kind") in ("full", "delta"):
            state, sid = apply_observation(response, self.baseline, self.snapshot_id)
            self.baseline, self.snapshot_id = state, sid
            result.pop("changes", None)
            result["wire_kind"] = result.pop("kind")
            result["encoding"] = "terminal-summary"
        elif isinstance(response.get("state"), dict):
            state = response["state"]  # Action state does not replace explicit delta baseline.
        else:
            return result
        keep = ("battle_id", "scene", "mode", "phase", "running", "ai_attached", "control_locked_by_player",
                "simulation_seconds", "sun", "wave", "paused", "modal_pause", "lost", "battle_awarded",
                "level_completed", "auto_collect", "auto_collection_started", "plant_plan", "tutorial", "shovel_enabled", "shovel_status",
                "local_decisions_enabled", "in_game_panel_enabled", "selecting_cards", "available_choices",
                "chosen_cards", "chosen_card_details", "selection_edit_enabled", "selection_capacity",
                "selection_can_add", "selection_can_start", "card_selection", "viewing_map", "mechanics", "snapshot_ms")
        summary = {key: deepcopy(state[key]) for key in keep if key in state}
        if isinstance(state.get("cards"), list):
            summary["cards"] = [[c["id"], c["content_id"], c["cost"], c.get("cooldown_remaining"), c["available"], c.get("source", "native")] for c in state["cards"]]
        if isinstance(state.get("entities"), list):
            summary["interactions"] = [{"entity_id": e["id"], "content_id": e["content_id"], "column": e["column"], "row": e["row"], **e["interaction"]}
                                       for e in state["entities"] if isinstance(e.get("interaction"), dict)
                                       and (any(e["interaction"].get(k) for k in ("pointer_skill", "button_skill", "draggable", "slidable_with_cell"))
                                            or e.get("kind") == "plant" and ((state.get("mechanics") or {}).get("glove") or {}).get("enabled"))]
            summary["plants"] = [[e["content_id"], e["column"], e["row"], e["hp"], e.get("can_fire")] for e in state["entities"] if e["kind"] == "plant" and not e["dead"]]
            summary["zombies"] = [[e["id"], e["content_id"], e["row"], e.get("x"), e["hp"], sum(a["hp"] for a in e.get("armor", []))] for e in state["entities"] if e["kind"] == "zombie" and not e["dead"]]
        if isinstance(state.get("suns"), list):
            summary["suns"] = [[s["id"], s["value"]] for s in state["suns"] if not s["collecting"]]
        result["state"] = summary
        result["state_view"] = "terminal_summary"
        result["omitted_state_fields"] = sorted(set(state) - set(summary) - ({"entities"} if "plants" in summary else set()))
        return result
