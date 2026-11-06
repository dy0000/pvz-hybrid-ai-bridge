"""Dependency-free MCP stdio server. stdout is reserved for JSON-RPC only."""
from __future__ import annotations

import json
import sys

from .client import PvzClient


def schema(properties=None, required=()):
    return {"type": "object", "properties": properties or {}, "required": list(required), "additionalProperties": False}


BATTLE = {"type": "string", "description": "battle_id from the most recent observed battle; never invent it."}
SHOP_ID = {"type": "string", "description": "shop_id from the current shop_observe response, not a battle ID."}
COL = {"type": "integer", "minimum": 1, "description": "1-based column from left."}
ROW = {"type": "integer", "minimum": 1, "description": "1-based row from top."}
PLAN_ID = {"type": "string", "minLength": 1, "maxLength": 64}
INTENT_ID = {"type": "string", "maxLength": 64}
PLAN_JOB = schema({
    "id": PLAN_ID, "content_id": {"type": "string"},
    "cells": {"type": "array", "minItems": 1, "maxItems": 32,
              "items": schema({"column": COL, "row": ROW}, ("column", "row"))},
    "role": {"type": "string", "enum": ["attack", "block", "economy"]},
    "priority": {"type": "integer", "minimum": 0, "maximum": 100},
    "max_uses": {"type": "integer", "minimum": 1, "maximum": 32},
    "threatened_only": {"type": "boolean"},
}, ("id", "content_id", "cells"))


def tool(name, description, op, properties=None, required=()):
    return {"name": name, "description": description, "inputSchema": schema(properties, required), "_op": op}


TOOLS = [
    tool("pvz_events", "Read bounded gameplay and narration events without a full observation. Cursor belongs to feed.stream_id; reset to 0 after stream change. speech is factual commentary, not a command or confirmed future action. Poll 100-500ms in the existing controller; no second connection.", "events",
         {"after_event_id": {"type": "integer", "minimum": 0}, "limit": {"type": "integer", "minimum": 1, "maximum": 128}, "narration_only": {"type": "boolean"}}),
    tool("pvz_stream_status", "Read broadcast status, expiring AI intentions, verified action receipts, recent events and flow bounds. status_file offers the same data for a separate read-only overlay without another game connection.", "stream_status"),
    tool("pvz_stream_update", "Publish an AI goal, thinking flag and declared intentions for the broadcast overlay. Does NOT execute a plan. Replaces the list; TTL is 500..30000ms. Only native action receipts can confirm success. Requires control; local planting decisions remain independently disabled.", "stream_update",
         {"thinking": {"type": "boolean"}, "goal": {"type": "string", "maxLength": 200}, "reason": {"type": "string", "maxLength": 300}, "visible": {"type": "boolean"},
          "ttl_ms": {"type": "integer", "minimum": 500, "maximum": 30000}, "intents": {"type": "array", "maxItems": 32,
          "items": schema({"id": PLAN_ID, "content_id": {"type": "string", "minLength": 1, "maxLength": 100}, "column": COL, "row": ROW,
                           "reason": {"type": "string", "maxLength": 300}, "status": {"type": "string", "enum": ["pending", "waiting", "submitting"]}}, ("id", "content_id", "column", "row"))}}),
    tool("pvz_capture_frame", "Capture PNG directly from the game's rendered viewport. No desktop screenshot, OCR or UI input. Returns an image and local path; max 2 captures/sec.", "capture_frame",
         {"max_width": {"type": "integer", "minimum": 320, "maximum": 1920}}),
    tool("pvz_inspect_cell", "Inspect terrain, support/armor layers and exact entity IDs at a cell. Does not plant or clear anything.", "inspect_cell", {"battle_id": BATTLE, "column": COL, "row": ROW}, ("battle_id", "column", "row")),
    tool("pvz_placement_check", "Read native placement hints, available target entities, upgrade bases and structured failure reasons such as missing_lilypad/missing_pot/missing_upgrade_base. Actual native use remains authoritative. Standard plant supports legal stacking/upgrades without bypassing cost/cooldown.", "placement_check",
         {"battle_id": BATTLE, "card_id": {"type": "integer"}, "column": COL, "row": ROW}, ("battle_id", "card_id", "column", "row")),
    tool("pvz_use_target", "Use an observed target card on a specific observed entity under native targeting rules and normal cost/cooldown. Ambiguous duplicate card slots are refused. Only Committed confirms success.", "use_target",
         {"battle_id": BATTLE, "card_id": {"type": "integer"}, "entity_id": {"type": "integer"}, "intent_id": INTENT_ID}, ("battle_id", "card_id", "entity_id")),
    tool("pvz_flow_start", "Authorize a bounded session BEFORE starting levels via menu_action. Does not start or play any level automatically. Counts actual new battle sessions; retries have a separate cap. Existing battle counts as the first level. Limits do not abandon an active battle.", "flow_start",
         {"max_levels": {"type": "integer", "minimum": 1, "maximum": 100}, "max_retries": {"type": "integer", "minimum": 0, "maximum": 20}, "max_seconds": {"type": "integer", "minimum": 10, "maximum": 14400}}, ("max_levels",)),
    tool("pvz_flow_status", "Read current level/retry counts, time limit and pending transition. Unknown transitions are not replayed automatically.", "flow_status"),
    tool("pvz_flow_stop", "End authorization for starting further levels/retries; the current battle keeps running. Finish its reward/return normally. Does not enable any automatic planting.", "flow_stop"),
    tool("pvz_capabilities", "Discover protocol version, supported operations, limits and default control mode before playing.", "capabilities"),
    tool("pvz_menu", "Read native game menus: available game modes, unlocked chapters/levels, daily challenge choices, loading state, dialogue and pause-resume actions. Returns opaque menu_id and action IDs; does not use OCR or desktop input.", "menu"),
    tool("pvz_menu_action", "Execute one action returned by pvz_menu. Can select game mode/chapter/level/daily challenge, browse daily calendar months, select an available historical date, start a selected daily level, continue ordinary dialogue or resume a game pause. Use fresh IDs after each calendar change; success means request accepted, then inspect the resulting menu/state. Do not choose main_menu during a requested ongoing battle without intending to end it.", "menu_action",
         {"menu_id": {"type": "string"}, "action_id": {"type": "string"}}, ("menu_id", "action_id")),
    tool("pvz_catalog", "Read the game's names and descriptions for current playable/unlocked selection cards. Useful for understanding hybrid plant abilities.", "catalog"),
    tool("pvz_observe", "Read native state. Full is default; compact is lossless shared-json-v1 and MUST be decoded before using fields. executable_cards_only explicitly omits unavailable cards and reports their count. Does not acquire control.", "observe",
         {"view": {"type": "string", "enum": ["full", "compact"]}, "executable_cards_only": {"type": "boolean"}}),
    tool("pvz_observe_delta", "Read changes from the last explicit observation on this same connection/view. Apply removed_fields, then replace changed top-level fields. Expired, changed-scene or reconnected baselines reset to kind=full. Decode compact changes first. Every observation refreshes sun IDs.", "observe_delta",
         {"base_snapshot_id": {"type": "string"}, "view": {"type": "string", "enum": ["full", "compact"]}, "executable_cards_only": {"type": "boolean"}}, ("base_snapshot_id",)),
    tool("pvz_card_info", "Read native structured metadata for 1..32 current playable/unlocked content IDs: base costs/cooldowns, support/terrain, upgrade prerequisites and exact current slot quotes. Base price is not a live quote.", "card_info",
         {"content_ids": {"type": "array", "minItems": 1, "maxItems": 32, "items": {"type": "string"}}}, ("content_ids",)),
    tool("pvz_deselect_card", "Remove one exact selected unlocked card through the native seed bank. Locked mandatory cards cannot be removed. Preparation must be unpaused.", "deselect_card",
         {"battle_id": BATTLE, "content_id": {"type": "string"}}, ("battle_id", "content_id")),
    tool("pvz_replace_card", "Replace one selected unlocked card with a current unlocked unselected card. Native sequential operation, not atomic: inspect selection_partial instead of blindly replaying.", "replace_card",
         {"battle_id": BATTLE, "content_id": {"type": "string"}, "replacement_content_id": {"type": "string"}}, ("battle_id", "content_id", "replacement_content_id")),
    tool("pvz_open_shop", "Open the naturally unlocked native shop from main menu or ready card selection. Does not buy anything.", "open_shop"),
    tool("pvz_close_shop", "Close the exact current native shop after cancelling/completing its prepared purchase.", "close_shop", {"shop_id": SHOP_ID}, ("shop_id",)),
    tool("pvz_shop_observe", "Read native shop identity, actual game coins, current visible item IDs, stages, prices and effect keys. Does not acquire control or spend coins.", "shop_observe"),
    tool("pvz_shop_navigate", "Use native category/page navigation in the exact shop; no purchasing.", "shop_navigate",
         {"shop_id": SHOP_ID, "action": {"type": "string", "enum": ["next", "previous", "Total", "Item", "Packet", "Custom", "Garden", "try_levels"]}}, ("shop_id", "action")),
    tool("pvz_shop_budget", "Authorize a fresh finite shopping budget only within the HUMAN's specified item list and total coin limit. This is not permission to invent a shopping goal. Reservations and committed purchases consume this persistent budget; unknown transactions block further purchases.", "shop_budget",
         {"budget_id": PLAN_ID, "max_total": {"type": "integer", "minimum": 0, "maximum": 2147483647}, "allowed_effect_keys": {"type": "array", "minItems": 1, "maxItems": 64, "items": {"type": "string", "minLength": 1, "maxLength": 128}}, "ttl_seconds": {"type": "integer", "minimum": 10, "maximum": 3600}}, ("budget_id", "max_total", "allowed_effect_keys")),
    tool("pvz_shop_prepare", "Bind one observed item/stage/price to a fresh persistent transaction and explicit budget, then open the native confirmation. Spends nothing; do not auto-confirm without the user's authorized purchase scope.", "shop_prepare",
         {"shop_id": SHOP_ID, "item_id": {"type": "string"}, "expected_stage": {"type": "integer", "minimum": 0}, "expected_price": {"type": "integer", "minimum": 0}, "max_spend": {"type": "integer", "minimum": 0}, "transaction_id": PLAN_ID, "budget_id": PLAN_ID}, ("shop_id", "item_id", "expected_stage", "expected_price", "max_spend", "transaction_id", "budget_id")),
    tool("pvz_shop_confirm", "Spend game coins once using the exact native confirmation bound by shop_prepare. Verifies both coin debit and actual ownership/upgrade. Persistent receipt prevents repeats after reconnect; unknown means never blindly retry or create a replacement purchase.", "shop_confirm",
         {"shop_id": SHOP_ID, "transaction_id": PLAN_ID}, ("shop_id", "transaction_id")),
    tool("pvz_shop_cancel", "Cancel a prepared transaction and its own native confirmation, releasing the budget reservation. Does not undo committed purchases or unknown outcomes.", "shop_cancel", {"transaction_id": PLAN_ID}, ("transaction_id",)),
    tool("pvz_shop_dismiss", "Dismiss the sole native shop confirmation without buying, or its insufficient-coins popup. Never dismisses unrelated game dialogs.", "shop_dismiss", {"shop_id": SHOP_ID}, ("shop_id",)),
    tool("pvz_shop_receipt", "Read persistent transaction outcome without spending again. Missing is null; prepared/committing/unknown is not successful purchasing.", "shop_receipt", {"transaction_id": PLAN_ID}, ("transaction_id",)),
    tool("pvz_take_control", "Acquire exclusive AI control. DEFAULT realtime: the game keeps running during decisions and actions. background=true temporarily enables background running and restores the previous setting on detach. turn_based is opt-in only when the user asks for pausing. F8 stops; F9 returns manual control.", "attach",
         {"mode": {"type": "string", "enum": ["realtime", "turn_based"]}, "background": {"type": "boolean"}}),
    tool("pvz_plant", "Use the exact observed signed card_id at a cell with native cost/cooldown/placement rules. source=falling_card is a distinct one-use map card, not an unavailable normal slot; preserve duplicate contents. Negative IDs also include free_conveyor, so read source. Falling-card success requires Committed and selection_released=true. Unknown results must never be replayed.", "plant",
         {"battle_id": BATTLE, "card_id": {"type": "integer"}, "column": COL, "row": ROW, "intent_id": INTENT_ID}, ("battle_id", "card_id", "column", "row")),
    tool("pvz_shovel", "Use the enabled native shovel on one bound target. layer_fraction (0..1, default .5) selects the layer. shoveled confirms removal; shovel_effect_confirmed only confirms native damage/stage change. Unknown outcomes must be observed before retrying.", "shovel",
         {"battle_id": BATTLE, "column": COL, "row": ROW, "layer_fraction": {"type": "number", "minimum": 0, "maximum": 1}, "intent_id": INTENT_ID}, ("battle_id", "column", "row")),
    tool("pvz_fire", "Aim an observed ready cannon at a target grid cell using its normal firing/cooldown logic. entity_id is a plant ID with can_fire=true, not a card ID.", "fire",
         {"battle_id": BATTLE, "entity_id": {"type": "integer"}, "column": COL, "row": ROW, "intent_id": INTENT_ID}, ("battle_id", "entity_id", "column", "row")),
    tool("pvz_legal_cells", "Ask the game for placement hints for a currently available card. This is a point-in-time hint; special cards and changes before execution still require final game validation.", "legal_cells",
         {"battle_id": BATTLE, "card_id": {"type": "integer"}}, ("battle_id", "card_id")),
    tool("pvz_collect", "Start collection for explicit observed sun IDs. Credit arrives after the normal collection animation; advance simulation and inspect sun balance. Never assumes immediate credit.", "collect",
         {"battle_id": BATTLE, "sun_ids": {"type": "array", "items": {"type": "string"}, "minItems": 1, "maxItems": 128}}, ("battle_id", "sun_ids")),
    tool("pvz_auto_collect", "Enable/disable native automatic sun collection for this battle in realtime mode. Scans every 100 ms, using normal pickup animation and credit. Stops on detach, disconnect, emergency stop or battle change; pauses with the game. This does not plant or choose strategy.", "auto_collect",
         {"battle_id": BATTLE, "enabled": {"type": "boolean"}}, ("battle_id", "enabled")),
    tool("pvz_set_plant_plan", "Submit a bounded realtime planting plan. Every 50 ms the Mod selects an eligible authorized job, respecting normal cost/cooldown, sun_budget and reserve_sun. Candidate cells must be empty. max_uses (default 1) allows filling/replacing lost plants within that count. attack/block jobs get priority in urgent lanes; attack jobs also prioritize lanes without authorized attack plants. threatened_only waits for zombies. Jobs/cells preserve listed order for ties. Cancel an active plan before replacing it; use a fresh plan_id. Does not navigate, shovel or unlock cards.", "set_plant_plan",
         {"battle_id": BATTLE, "plan_id": PLAN_ID, "jobs": {"type": "array", "minItems": 1, "maxItems": 32, "items": PLAN_JOB},
          "sun_budget": {"type": "integer", "minimum": 0, "maximum": 100000},
          "reserve_sun": {"type": "integer", "minimum": 0, "maximum": 100000},
          "ttl_seconds": {"type": "integer", "minimum": 1, "maximum": 600},
          "emergency_priority": {"type": "boolean"}, "threat_column": {"type": "integer", "minimum": 1}},
         ("battle_id", "plan_id", "jobs", "sun_budget")),
    tool("pvz_plant_plan_status", "Read current/last plan, wait reasons and bounded execution events. after_event_id is a cursor scoped to plan_id; reset to 0 for a new plan. events_truncated signals old events were dropped. Cancel cannot recall an already submitted native action: wait for in_flight=false. An unknown native outcome stops automatic retries.", "plant_plan_status",
         {"after_event_id": {"type": "integer", "minimum": 0}}),
    tool("pvz_cancel_plant_plan", "Stop future planting from the exact current plan. Does not undo committed plants or recall an in-flight game action. Observe plan status before manual planting or a replacement plan.", "cancel_plant_plan",
         {"battle_id": BATTLE, "plan_id": PLAN_ID}, ("battle_id", "plan_id")),
    tool("pvz_step", "Only for explicitly selected turn_based mode. Advance 1..600 physics frames then pause. In default realtime mode use observe; the game already runs continuously.", "step",
         {"battle_id": BATTLE, "frames": {"type": "integer", "minimum": 1, "maximum": 600}}, ("battle_id", "frames")),
    tool("pvz_choose_card", "During card selection, add one unlocked content_id from available_choices. Uses the game's selection animation; inspect chosen_cards after it settles. Does not start battle.", "choose_card",
         {"battle_id": BATTLE, "content_id": {"type": "string"}}, ("battle_id", "content_id")),
    tool("pvz_start_battle", "Start with currently chosen cards. In default realtime mode battle continues running immediately. Use menu for supported pre-level dialogue/navigation.", "start_battle",
         {"battle_id": BATTLE}, ("battle_id",)),
    tool("pvz_release_control", "Detach AI and restore its temporary background setting. Default realtime keeps running; default turn_based leaves paused. resume=true returns manual play unless a game dialog owns pause.", "detach", {"resume": {"type": "boolean"}}),
    tool("pvz_stop", "Stop the AI session and leave the current single-player battle paused. Does not send keyboard or mouse input.", "stop"),
]

FEATURE_ID = {"type": "string", "minLength": 1, "maxLength": 64, "description": "Exact native feature_id from state.mechanics; never invent it."}
ENTITY_ID = {"type": "integer", "minimum": 1}
COIN_AMOUNT = {"type": "integer", "minimum": 0, "maximum": 2147483647}
TOOLS.extend([
    tool("pvz_set_native_speedup", "Explicitly set the normal native speed toggle; use only when the user wants changed game speed, never as hidden AI acceleration. Uses the native checkbox/rules, no arbitrary time scale. Read actual_time_scale rather than assuming its 2X label is literally 2x.", "set_native_speedup",
         {"battle_id": BATTLE, "enabled": {"type": "boolean"}}, ("battle_id", "enabled")),
    tool("pvz_collect_coins", "Collect 1..128 exact observed mechanics.coins IDs using native animation and credit. IDs expire on the next observation; collection_started is not credited coins.", "collect_coins",
         {"battle_id": BATTLE, "coin_ids": {"type": "array", "minItems": 1, "maxItems": 128, "items": {"type": "string"}}}, ("battle_id", "coin_ids")),
    tool("pvz_auto_collect_coins", "Enable native coin/diamond pickup every 100 ms for this battle in realtime mode. Separate from sun auto_collect; stops on detach, battle change, player lock or pause. Does not spend currency.", "auto_collect_coins",
         {"battle_id": BATTLE, "enabled": {"type": "boolean"}}, ("battle_id", "enabled")),
    tool("pvz_break_vase", "Start opening one observed unopened mechanics.vases entity through its native pointer component. Keeps native visibility, cooldown and animation; never reveals hidden vase contents. Observe the released contents afterwards; do not replay an unknown result.", "break_vase",
         {"battle_id": BATTLE, "entity_id": ENTITY_ID}, ("battle_id", "entity_id")),
    tool("pvz_move_plant", "Move the exact plant/layer using an enabled native glove. Requires native shovel selection and destination legality; preserves support rules. Success confirms the same entity moved and selection_released. No automatic shoveling.", "move_plant",
         {"battle_id": BATTLE, "entity_id": ENTITY_ID, "column": COL, "row": ROW, "layer_fraction": {"type": "number", "minimum": 0, "maximum": 1}}, ("battle_id", "entity_id", "column", "row")),
    tool("pvz_hammer_hit", "Swing only in native hammer mode at a cell or exact entity still in that cell. Resolves via native Attack after a physics frame; hammer_swing_started confirms neither damage nor a kill. Also uses native crater-repair behavior.", "hammer_hit",
         {"battle_id": BATTLE, "column": COL, "row": ROW, "entity_id": ENTITY_ID}, ("battle_id", "column", "row")),
    tool("pvz_slot_spin", "Request one normal slot-machine spin with native sun fee, RNG and cooldown. Bind feature_id and expected_spin_count from mechanics.slot_machine. spin_started confirms count advanced, not a reward; observe once after animation, never blindly replay.", "slot_spin",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "expected_spin_count": COIN_AMOUNT}, ("battle_id", "feature_id", "expected_spin_count")),
    tool("pvz_gem_action", "Operate native gem-match with its exact feature_id and expected_board_revision. swap needs adjacent from/to_column,row (1-based BOARD coordinates); upgrade needs an observed upgrade_key. fill_hole/refresh/upgrade retain native sun costs. Rejected busy/stale boards must be re-observed; accepted animation is not completed elimination.", "gem_action",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "expected_board_revision": COIN_AMOUNT,
          "action": {"type": "string", "enum": ["swap", "fill_hole", "refresh", "upgrade"]},
          "from_column": COL, "from_row": ROW, "to_column": COL, "to_row": ROW, "upgrade_key": PLAN_ID},
         ("battle_id", "feature_id", "expected_board_revision", "action")),
    tool("pvz_entity_interact", "Use an observed entity.interaction through native component input, with normal lifecycle/cooldown. click activates pointer skills; double_click is the native robot gesture; aim requires target column,row and finishes/releases its own aim; drag requires destination column,row and native draggable legality; button invokes only an enabled native spring-mushroom button. No desktop input. Native activation confirmation does not claim damage/ability completion.", "entity_interact",
         {"battle_id": BATTLE, "entity_id": ENTITY_ID, "action": {"type": "string", "enum": ["click", "double_click", "aim", "drag", "button"]},
          "column": COL, "row": ROW}, ("battle_id", "entity_id", "action")),
    tool("pvz_slide_cell", "Slide one native movable cell stack to an adjacent empty cell of the same native terrain. Only entities flagged slidable_with_cell can move. Uses native CanMowerMove/CanMoveToCell/MoveToCell, not arbitrary teleporting.", "slide_cell",
         {"battle_id": BATTLE, "from_column": COL, "from_row": ROW, "column": COL, "row": ROW}, ("battle_id", "from_column", "from_row", "column", "row")),
    tool("pvz_open_pause_menu", "Explicitly open the native battle menu for resume/restart/main-menu navigation. This intentionally pauses as a normal user menu action; never use it merely for AI thinking. Use fresh menu actions afterwards.", "open_pause_menu",
         {"battle_id": BATTLE}, ("battle_id",)),
    tool("pvz_quiz_configure", "Configure native quiz row wager controls within their displayed min/max/step before a run. Does not spend coins. Actual entry requires HUMAN-authorized quiz_entry scope/total budget via shop_budget, then quiz_prepare/quiz_confirm; this tool grants no spending authority.", "quiz_configure",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "bets": {"type": "array", "minItems": 1, "maxItems": 50, "items": schema({"row": ROW, "wager": COIN_AMOUNT, "choice": {"type": "string", "enum": ["win", "lose", "skip"]}}, ("row", "wager", "choice"))}}, ("battle_id", "feature_id", "bets")),
    tool("pvz_quiz_refresh_row", "Request one exact observed native quiz refresh button/row. Retains native balance checks and increases the normal future entry multiplier; does not start/pay for the run. Inspect the new quote afterwards.", "quiz_refresh_row",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "button_id": FEATURE_ID, "row": ROW}, ("battle_id", "feature_id", "button_id", "row")),
    tool("pvz_quiz_prepare", "Prepare one native quiz coin-entry confirmation with a fresh persistent transaction. Requires an unexpired shop_budget explicitly authorized by the HUMAN for allowed_effect_keys=[quiz_entry] and a total coin limit. Match actual expected_price/max_spend. Native popup remains visible; AI confirms it, no manual user click required.", "quiz_prepare",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "transaction_id": PLAN_ID, "budget_id": PLAN_ID, "expected_price": COIN_AMOUNT, "max_spend": COIN_AMOUNT},
         ("battle_id", "feature_id", "transaction_id", "budget_id", "expected_price", "max_spend")),
    tool("pvz_quiz_confirm", "Confirm the exact prepared native quiz entry once. Revalidates all wagers, choice/multiplier, process, balance and budget; persists intent before coin debit. Read shop_receipt on loss of response; never create a new entry to bypass unknown.", "quiz_confirm",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "transaction_id": PLAN_ID}, ("battle_id", "feature_id", "transaction_id")),
    tool("pvz_quiz_cancel", "Cancel only the original prepared quiz native confirmation and release its unspent reservation. Does not undo a committed/unknown entry.", "quiz_cancel",
         {"battle_id": BATTLE, "feature_id": FEATURE_ID, "transaction_id": PLAN_ID}, ("battle_id", "feature_id", "transaction_id")),
])


def validate(arguments, specification):
    if not isinstance(arguments, dict):
        raise ValueError("arguments must be an object")
    properties = specification["properties"]
    if set(arguments) - set(properties):
        raise ValueError("Unknown argument")
    if set(specification["required"]) - set(arguments):
        raise ValueError("Missing required argument")
    for key, value in arguments.items():
        validate_value(value, properties[key], key)


def validate_value(value, rule, key):
    kind = rule["type"]
    valid = {"string": isinstance(value, str), "integer": type(value) is int,
             "number": type(value) in (int, float), "boolean": type(value) is bool,
             "array": isinstance(value, list), "object": isinstance(value, dict)}[kind]
    if not valid:
        raise ValueError(f"Invalid type for {key}")
    if "enum" in rule and value not in rule["enum"]:
        raise ValueError(f"Invalid choice for {key}")
    if kind in ("integer", "number"):
        import math
        if not math.isfinite(value) or value < rule.get("minimum", -float("inf")) or value > rule.get("maximum", float("inf")):
            raise ValueError(f"Out of range: {key}")
    if kind == "string" and not rule.get("minLength", 0) <= len(value) <= rule.get("maxLength", 65536):
        raise ValueError(f"Invalid string length: {key}")
    if kind == "object":
        validate(value, rule)
    if kind == "array":
        if not rule.get("minItems", 0) <= len(value) <= rule.get("maxItems", 10000):
            raise ValueError(f"Invalid array: {key}")
        for item in value:
            validate_value(item, rule["items"], key)


def handle(message, client):
    if "id" not in message:
        return None
    response = {"jsonrpc": "2.0", "id": message["id"]}
    method = message.get("method")
    params = message.get("params") or {}
    try:
        if not isinstance(params, dict):
            raise ValueError("params must be an object")
        if method == "initialize":
            requested = params.get("protocolVersion")
            version = requested if requested in ("2024-11-05", "2025-03-26", "2025-06-18") else "2025-06-18"
            result = {"protocolVersion": version, "capabilities": {"tools": {}},
                      "serverInfo": {"name": "pvz-hybrid-ai-bridge", "version": "0.5.16"},
                      "instructions": "You are the player; decide strategy yourself. Default realtime keeps the game running during reasoning/actions: act promptly, do not use step or pause unless the user requests turn-based testing. Query capabilities and menu to navigate/select levels. Use fresh battle/card/sun/menu IDs. Coordinates are 1-based. Never automatically replay an action after a lost response. Preserve normal game rules. battle_awarded only means a reward is pending. Use menu to collect_victory_reward, observe settlement, then finish_level. Do not detach during pending reward/settlement. Enable auto_collect for timely sun pickup. Check capabilities.local_decisions_enabled first. When false, use explicit single plant actions and do not enable local decisions without the user asking. When true, submit bounded set_plant_plan early so cooldown execution does not wait for model turns. Poll plant_plan_status with event cursors; inspect wait reasons. Cancel and wait for in_flight=false before replacing with a fresh plan_id. Only plant_committed events confirm planting."}
        elif method == "ping":
            result = {}
        elif method == "tools/list":
            result = {"tools": [{k: v for k, v in t.items() if k != "_op"} for t in TOOLS]}
        elif method == "tools/call":
            selected = next((t for t in TOOLS if t["name"] == params.get("name")), None)
            if selected is None:
                raise ValueError("Unknown tool")
            arguments = params.get("arguments", {})
            try:
                validate(arguments, selected["inputSchema"])
                reply = client.request(selected["_op"], **arguments)
                png = reply.pop("png_base64", None) if selected["_op"] == "capture_frame" else None
                content = [{"type": "text", "text": json.dumps(reply, ensure_ascii=False)}]
                if png:
                    content.append({"type": "image", "data": png, "mimeType": "image/png"})
                result = {"content": content, "isError": not reply.get("ok", False)}
            except Exception as exc:
                result = {"content": [{"type": "text", "text": str(exc)}], "isError": True}
        else:
            response["error"] = {"code": -32601, "message": "Method not found"}
            return response
        response["result"] = result
    except (ValueError, TypeError, KeyError) as exc:
        response["error"] = {"code": -32602, "message": str(exc)}
    return response


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stdin.reconfigure(encoding="utf-8")
    client = PvzClient()
    try:
        for line in sys.stdin:
            try:
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise ValueError("Expected JSON-RPC object")
                response = handle(message, client)
            except (ValueError, TypeError) as exc:
                response = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": str(exc)}}
            if response is not None:
                print(json.dumps(response, ensure_ascii=False), flush=True)
    finally:
        client.close()


if __name__ == "__main__":
    main()
