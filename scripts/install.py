"""Generate local MCP configuration and check the protocol without controlling a game."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def write_configuration(root: Path = ROOT, python: str = sys.executable) -> None:
    generated = root / "generated"
    generated.mkdir(exist_ok=True)
    server = {"command": python, "args": ["-X", "utf8", str(root / "scripts/run_mcp.py")]}
    (generated / "mcp.json").write_text(json.dumps(
        {"mcpServers": {"pvz_hybrid": server}}, ensure_ascii=False, indent=2) + "\n", encoding="utf8")
    (generated / "codex.toml").write_text(
        "[mcp_servers.pvz_hybrid]\ncommand = " + json.dumps(python, ensure_ascii=False) +
        "\nargs = " + json.dumps(server["args"], ensure_ascii=False) + "\n", encoding="utf8")
    (generated / "START_HERE.txt").write_text(
        "请通过 PVZ Hybrid AI Bridge 帮我玩植物大战僵尸杂交重制版。\n"
        f"项目目录：{root}\n先读 {root / 'skills/pvz-mod-player/SKILL.md'} 和 API.md。\n"
        "使用 pvz_hybrid MCP 的 pvz_* 工具，先确认没有其他控制器，再读 capabilities、menu 和 observe。\n"
        "保持唯一会话，take_control 使用 realtime/background=true。已有战斗和 flow 继续接续，不覆盖预算。\n"
        "开新关前按我要求设置有限 flow 额度，使用当前 ID，保留原生费用和冷却，不暂停等思考。\n"
        "local_decisions_enabled=false 时用逐次 plant。购物和竞猜需要我的明确范围及预算。\n"
        "F8/F9 锁只能由我按 F10 解除；未知结果不重放。领奖返回后 release_control，确认交还。\n",
        encoding="utf8")
    (root / "config.local.json").write_text(json.dumps(
        {"python": python}, ensure_ascii=False, indent=2) + "\n", encoding="utf8")


def mcp_smoke(python: str = sys.executable, root: Path = ROOT) -> int:
    messages = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
    ]
    result = subprocess.run([python, "-X", "utf8", str(root / "scripts/run_mcp.py")],
                            input="".join(json.dumps(m) + "\n" for m in messages),
                            text=True, encoding="utf8", capture_output=True, check=True, timeout=15)
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    if len(replies) != 2 or replies[0].get("id") != 1 or replies[1].get("id") != 2:
        raise RuntimeError("Unexpected MCP response")
    if "serverInfo" not in replies[0].get("result", {}):
        raise RuntimeError("MCP initialization failed")
    tools = replies[1].get("result", {}).get("tools", [])
    if not tools or not all(tool["name"].startswith("pvz_") for tool in tools):
        raise RuntimeError("MCP tool definitions are missing")
    return len(tools)


if __name__ == "__main__":
    try:
        write_configuration()
        print(f"MCP handshake OK: {mcp_smoke()} tools. No game connection was opened.")
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as exc:
        print(f"Setup failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
