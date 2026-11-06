"""Persistent, logged manual/AI command session. No strategy or automatic inputs."""
import json
from datetime import datetime
from pathlib import Path
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from pvz_ai import PvzClient
from pvz_ai.session_view import SessionView

RUN = ROOT / "artifacts" / (datetime.now().strftime("play-%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8])
RUN.mkdir(parents=True)
client = PvzClient()
view = SessionView()
print(json.dumps({"ready": True, "run": str(RUN)}, ensure_ascii=False), flush=True)
try:
    for index, line in enumerate(sys.stdin, 1):
        try:
            command = json.loads(line)
            if command.get("op") == "close":
                break
            response = client.request(**command)
            (RUN / f"{index:04d}.json").write_text(json.dumps({"command": command, "response": response}, ensure_ascii=False, indent=2), encoding="utf-8")
            compact = view.render(response)
            compact["record"] = f"{index:04d}.json"
            print(json.dumps(compact, ensure_ascii=False), flush=True)
        except Exception as exc:
            print(json.dumps({"ok": False, "error": str(exc), "no_retry": True}, ensure_ascii=False), flush=True)
finally:
    client.close()
