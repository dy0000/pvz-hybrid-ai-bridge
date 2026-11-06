from __future__ import annotations

import json
import hashlib
import os
from pathlib import Path
import socket
from typing import Any
import uuid
import time


class BridgeError(RuntimeError):
    """Transport or game rejection. Mutating commands must not be auto-retried."""


def default_config() -> Path:
    override = os.environ.get("PVZ_AI_CONNECTION")
    if override:
        return Path(override)
    root = Path(__file__).resolve().parents[2]
    root_hash = hashlib.sha256(str(root).lower().encode("utf8")).hexdigest()[:12]
    return Path(os.environ.get("APPDATA", "")) / f"PVZ-Hybrid-AI-{root_hash}/AiBridge/connection.json"


class PvzClient:
    """Keep one connection for the entire AI session. Default mode is realtime.

    Coordinates are 1-based (column, row). The battle ID and card/sun IDs must
    come from the last observation. No API automatically retries an action.
    """

    def __init__(self, config: str | Path | None = None, timeout: float = 40):
        self.config = Path(config) if config else default_config()
        self._expect_sandbox = config is None and not os.environ.get("PVZ_AI_CONNECTION")
        self.timeout = timeout
        self._socket: socket.socket | None = None
        self._reader = None
        self._token = ""

    def connect(self) -> None:
        if self._socket is not None:
            return
        try:
            config = json.loads(self.config.read_text(encoding="utf-8-sig"))
            if config.get("host") != "127.0.0.1":
                raise BridgeError("Only a local loopback game connection is supported.")
            if self._expect_sandbox and (config.get("instance") != "sandbox" or Path(config.get("user_data_dir", "")).resolve() != (self.config.parent.parent).resolve()):
                raise BridgeError("Refusing to connect: endpoint is not the isolated sandbox instance/save directory.")
            self._token = config["token"]
            self._socket = socket.create_connection(("127.0.0.1", int(config["port"])), timeout=self.timeout)
            self._socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            self._reader = self._socket.makefile("rb")
        except (OSError, ValueError, KeyError) as exc:
            self.close()
            raise BridgeError(f"游戏 Mod 尚未就绪。请先启用 AI 玩家接口并重新加载游戏。配置：{self.config}；{exc}") from exc

    def request(self, op: str, **arguments: Any) -> dict:
        self.connect()
        request_id = uuid.uuid4().hex
        request = {"id": request_id, "op": op, "token": self._token, **arguments}
        data = (json.dumps(request, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
        try:
            assert self._socket is not None and self._reader is not None
            started = time.perf_counter()
            self._socket.sendall(data)
            line = self._reader.readline(8 * 1024 * 1024 + 1)
            if not line or len(line) > 8 * 1024 * 1024 or not line.endswith(b"\n"):
                raise BridgeError("Invalid or incomplete game response. Outcome may be unknown; do not replay the action.")
            result = json.loads(line)
            if result.get("id") != request_id:
                raise BridgeError(result.get("error", "Response ID mismatch; observe before any new action."))
            result["round_trip_ms"] = round((time.perf_counter() - started) * 1000, 3)
            return result
        except (OSError, ValueError, BridgeError) as exc:
            self.close()
            if isinstance(exc, BridgeError):
                raise
            raise BridgeError(f"Connection failed; action outcome is unknown, no automatic retry: {exc}") from exc

    def observe(self) -> dict:
        return self.request("observe")

    def attach(self, mode: str = "realtime", background: bool = True) -> dict:
        return self.request("attach", mode=mode, background=background)

    def step(self, battle_id: str, frames: int) -> dict:
        return self.request("step", battle_id=battle_id, frames=frames)

    def plant(self, battle_id: str, card_id: int, column: int, row: int) -> dict:
        return self.request("plant", battle_id=battle_id, card_id=card_id, column=column, row=row)

    def shovel(self, battle_id: str, column: int, row: int, layer_fraction: float = 0.5) -> dict:
        return self.request("shovel", battle_id=battle_id, column=column, row=row, layer_fraction=layer_fraction)

    def collect(self, battle_id: str, sun_ids: list[str]) -> dict:
        return self.request("collect", battle_id=battle_id, sun_ids=sun_ids)

    def auto_collect(self, battle_id: str, enabled: bool = True) -> dict:
        return self.request("auto_collect", battle_id=battle_id, enabled=enabled)

    def set_plant_plan(self, battle_id: str, plan_id: str, jobs: list[dict], *, sun_budget: int,
                       reserve_sun: int = 0, ttl_seconds: int = 180, emergency_priority: bool = True,
                       threat_column: int = 4) -> dict:
        return self.request("set_plant_plan", battle_id=battle_id, plan_id=plan_id, jobs=jobs,
                            sun_budget=sun_budget, reserve_sun=reserve_sun, ttl_seconds=ttl_seconds,
                            emergency_priority=emergency_priority, threat_column=threat_column)

    def plant_plan_status(self, after_event_id: int = 0) -> dict:
        return self.request("plant_plan_status", after_event_id=after_event_id)

    def cancel_plant_plan(self, battle_id: str, plan_id: str) -> dict:
        return self.request("cancel_plant_plan", battle_id=battle_id, plan_id=plan_id)

    def events(self, after_event_id: int = 0, limit: int = 64, narration_only: bool = False) -> dict:
        return self.request("events", after_event_id=after_event_id, limit=limit, narration_only=narration_only)

    def stream_status(self) -> dict:
        return self.request("stream_status")

    def stream_update(self, **metadata: Any) -> dict:
        return self.request("stream_update", **metadata)

    def capture_frame(self, max_width: int = 1280) -> dict:
        return self.request("capture_frame", max_width=max_width)

    def inspect_cell(self, battle_id: str, column: int, row: int) -> dict:
        return self.request("inspect_cell", battle_id=battle_id, column=column, row=row)

    def placement_check(self, battle_id: str, card_id: int, column: int, row: int) -> dict:
        return self.request("placement_check", battle_id=battle_id, card_id=card_id, column=column, row=row)

    def use_target(self, battle_id: str, card_id: int, entity_id: int, intent_id: str = "") -> dict:
        return self.request("use_target", battle_id=battle_id, card_id=card_id, entity_id=entity_id, intent_id=intent_id)

    def flow_start(self, max_levels: int, max_retries: int = 0, max_seconds: int = 1800) -> dict:
        return self.request("flow_start", max_levels=max_levels, max_retries=max_retries, max_seconds=max_seconds)

    def flow_status(self) -> dict:
        return self.request("flow_status")

    def flow_stop(self) -> dict:
        return self.request("flow_stop")

    def detach(self, resume: bool | None = None) -> dict:
        return self.request("detach", **({} if resume is None else {"resume": resume}))

    def menu(self) -> dict:
        return self.request("menu")

    def menu_action(self, menu_id: str, action_id: str) -> dict:
        return self.request("menu_action", menu_id=menu_id, action_id=action_id)

    def catalog(self) -> dict:
        return self.request("catalog")

    def card_info(self, content_ids: list[str]) -> dict:
        return self.request("card_info", content_ids=content_ids)

    def shop_observe(self) -> dict:
        return self.request("shop_observe")

    def open_shop(self) -> dict:
        return self.request("open_shop")

    def shop_receipt(self, transaction_id: str) -> dict:
        return self.request("shop_receipt", transaction_id=transaction_id)

    def close(self) -> None:
        if self._reader is not None:
            self._reader.close()
        if self._socket is not None:
            self._socket.close()
        self._reader = self._socket = None

    def __enter__(self) -> PvzClient:
        self.connect()
        return self

    def __exit__(self, *_: object) -> None:
        self.close()
