"""Read-only checks; no socket connection or game inputs."""
import hashlib
import json
from pathlib import Path
import sys

from install import ROOT, mcp_smoke


def main():
    info = json.loads((ROOT / "packages/build-info.json").read_text(encoding="utf-8-sig"))
    package = ROOT / "packages" / info["packageName"]
    if hashlib.sha256(package.read_bytes()).hexdigest().upper() != info["packageSha256"].upper():
        raise RuntimeError("Bridge package hash mismatch. Download a clean release.")
    print(f"Python: {sys.version.split()[0]}; bridge package: {info['bridgeVersion']} (hash OK)")
    print(f"MCP initialize + tools/list: OK ({mcp_smoke()} tools)")
    sandbox = ROOT / "sandbox.local.json"
    if sandbox.exists():
        state = json.loads(sandbox.read_text(encoding="utf-8-sig"))
        game = Path(state["gameDirectory"])
        assembly = game / "data_PlantsVsZombies_windows_x86_64/PlantsVsZombies.dll"
        if hashlib.sha256(assembly.read_bytes()).hexdigest().upper() != info["gameAssemblySha256"].upper():
            raise RuntimeError("Sandbox game assembly no longer matches the supported build.")
        print(f"Game files: compatible ({info['gameVersion']}); sandbox: {game}")
        print("Use start.cmd and enable AI 玩家接口 in the game's Mod manager.")
    else:
        print("Game not linked. Run install.ps1 -GamePath <your game folder>.")
    print(f"MCP configuration: {ROOT / 'generated/mcp.json'}")
    print("Local checks passed. Live gameplay was not exercised; no token was read.")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"Check failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
