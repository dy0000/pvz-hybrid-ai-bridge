"""Lossless optional observation decoding; never drives game input."""
from __future__ import annotations
from copy import deepcopy


def decode_shared(value: dict) -> dict:
    if value.get("encoding") != "shared-json-v1":
        return deepcopy(value)
    definitions = value["definitions"]

    def unpack(node):
        if isinstance(node, dict):
            if set(node) == {"$shared_literal"}:
                return deepcopy(node["$shared_literal"])
            if set(node) == {"$shared_ref"}:
                return deepcopy(definitions[node["$shared_ref"]])
            return {k: unpack(v) for k, v in node.items()}
        if isinstance(node, list):
            return [unpack(v) for v in node]
        return node
    return unpack(value["root"])


def apply_observation(response: dict, baseline: dict | None = None, snapshot_id: str | None = None) -> tuple[dict, str]:
    if not response.get("ok"):
        raise ValueError("Observation was not successful")
    if response.get("kind", "full") == "full":
        return decode_shared(response["state"]), response["snapshot_id"]
    if baseline is None or response["base_snapshot_id"] != snapshot_id:
        raise ValueError("Delta baseline mismatch; request a full observation")
    state = deepcopy(baseline)
    for key in response["removed_fields"]:
        state.pop(key, None)
    state.update(decode_shared(response["changes"]))
    return state, response["snapshot_id"]
