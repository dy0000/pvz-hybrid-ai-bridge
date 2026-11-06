"""Compact view of the most recent recorded observation; never controls game."""
import json
from pathlib import Path
import sys

run = Path(sys.argv[1])
record = json.loads(sorted(run.glob("*.json"))[-1].read_text(encoding="utf-8"))
r = record["response"]
s = r.get("state", {})
result = {"command": record["command"], "ok": r.get("ok"), "error": r.get("error"), "outcome": r.get("outcome"),
          "battle": s.get("battle_id"), "t": s.get("simulation_seconds"), "sun": s.get("sun"), "wave": s.get("wave"),
          "running": s.get("running"), "paused": s.get("paused"), "modal": s.get("modal_pause"), "lost": s.get("lost"),
          "cards": [[c["id"], c["content_id"].removeprefix("Plant"), c["cost"], round(c["cooldown_remaining"],1), c["available"]] for c in s.get("cards",[])],
          "plants": [[e["content_id"].removeprefix("Plant"),e["column"],e["row"],round(e["hp"])] for e in s.get("entities",[]) if e["kind"]=="plant" and not e["dead"]],
          "zombies": [[e["id"],e["content_id"],e["row"],round(e["x"]),round(e["hp"]),e["armor"]] for e in s.get("entities",[]) if e["kind"]=="zombie" and not e["dead"]],
          "suns": s.get("suns",[])}
print(json.dumps(result, ensure_ascii=False))
