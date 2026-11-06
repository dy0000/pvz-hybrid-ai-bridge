using System.Text.Json.Nodes;

namespace PvzAiBridge;

public sealed record PlanCell(int Column, int Row);
public sealed class PlantJob
{
    public string Id { get; init; } = "";
    public string ContentId { get; init; } = "";
    public string Role { get; init; } = "attack";
    public PlanCell[] Cells { get; init; } = [];
    public int Priority { get; init; }
    public int MaxUses { get; init; }
    public bool ThreatenedOnly { get; init; }
    public int Uses { get; set; }
    public string Waiting { get; set; } = "pending";
}
public sealed record PlanOption(int JobIndex, PlanCell Cell, long CardId, long Cost, int NearestZombieColumn, bool LaneHasAttack);

// Scheduling policy is independent of Godot; only the owning game-thread loop
// supplies eligible options and invokes the native card operation.
public sealed class PlantPlan
{
    public string Id { get; init; } = "";
    public string BattleId { get; init; } = "";
    public PlantJob[] Jobs { get; init; } = [];
    public long SunBudget { get; init; }
    public long ReserveSun { get; init; }
    public long ExpiresAtMs { get; init; }
    public bool EmergencyPriority { get; init; }
    public int ThreatColumn { get; init; }
    public long Spent { get; private set; }
    public string Status { get; private set; } = "active";
    public bool Active => Status == "active";
    private readonly Queue<JsonObject> _events = new();
    private long _eventId;

    public static PlantPlan Parse(JsonObject body, long nowMs, int columns, int rows, ISet<string> cards)
    {
        static int Number(JsonObject o, string key, int low, int high, int? fallback = null)
        {
            int n = o[key]?.GetValue<int>() ?? fallback ?? throw new ArgumentException(key + " required.");
            if (n < low || n > high) throw new ArgumentException(key + " out of range.");
            return n;
        }
        string id = body["plan_id"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new ArgumentException("plan_id must contain 1..64 characters.");
        var array = body["jobs"] as JsonArray ?? throw new ArgumentException("jobs required.");
        if (array.Count is < 1 or > 32) throw new ArgumentException("Provide 1..32 jobs.");
        var jobs = new List<PlantJob>(); var ids = new HashSet<string>(); int cellCount = 0, uses = 0;
        foreach (var node in array)
        {
            var job = node as JsonObject ?? throw new ArgumentException("Each job must be an object.");
            string jobId = job["id"]?.GetValue<string>() ?? "";
            string content = job["content_id"]?.GetValue<string>() ?? "";
            string role = job["role"]?.GetValue<string>() ?? "attack";
            if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 || !ids.Add(jobId)) throw new ArgumentException("Job IDs must be unique, 1..64 characters.");
            if (!cards.Contains(content)) throw new ArgumentException("Plan card is not in the current observed card bank: " + content);
            if (role is not ("attack" or "block" or "economy")) throw new ArgumentException("role must be attack, block or economy.");
            var cells = job["cells"] as JsonArray ?? throw new ArgumentException("cells required.");
            if (cells.Count is < 1 or > 32) throw new ArgumentException("Provide 1..32 candidate cells per job.");
            var positions = cells.Select(n => {
                var c = n as JsonObject ?? throw new ArgumentException("Each cell must be an object.");
                return new PlanCell(Number(c, "column", 1, columns), Number(c, "row", 1, rows));
            }).ToArray();
            if (positions.Distinct().Count() != positions.Length) throw new ArgumentException("Duplicate candidate cell.");
            int maxUses = Number(job, "max_uses", 1, 32, 1);
            cellCount += positions.Length; uses += maxUses;
            if (cellCount > 128 || uses > 128) throw new ArgumentException("Plan is limited to 128 candidate cells and 128 total uses.");
            jobs.Add(new PlantJob { Id = jobId, ContentId = content, Role = role, Cells = positions,
                Priority = Number(job, "priority", 0, 100, 0), MaxUses = maxUses,
                ThreatenedOnly = job["threatened_only"]?.GetValue<bool>() ?? false });
        }
        return new PlantPlan { Id = id, BattleId = body["battle_id"]!.GetValue<string>(), Jobs = jobs.ToArray(),
            SunBudget = Number(body, "sun_budget", 0, 100000), ReserveSun = Number(body, "reserve_sun", 0, 100000, 0),
            ExpiresAtMs = nowMs + Number(body, "ttl_seconds", 1, 600, 180) * 1000L,
            EmergencyPriority = body["emergency_priority"]?.GetValue<bool>() ?? true,
            ThreatColumn = Number(body, "threat_column", 1, columns, Math.Min(4, columns)) };
    }

    public bool CanSpend(long cost, long sun) => cost >= 0 && cost <= SunBudget - Spent && sun >= cost && sun - cost >= ReserveSun;

    public (PlanOption Option, string Reason)? Choose(IEnumerable<PlanOption> options, long sun, long nowMs)
    {
        if (!Active) return null;
        if (nowMs >= ExpiresAtMs) { Stop("expired", nowMs); return null; }
        (PlanOption Option, string Reason)? best = null; int bestScore = int.MinValue;
        foreach (var option in options)
        {
            var job = Jobs[option.JobIndex];
            if (job.Uses >= job.MaxUses || !CanSpend(option.Cost, sun)) continue;
            bool threatened = option.NearestZombieColumn > 0;
            if (job.ThreatenedOnly && !threatened) continue;
            int score = job.Priority; string reason = "planned_order";
            if (EmergencyPriority && threatened && job.Role is "attack" or "block")
            {
                if (option.NearestZombieColumn <= ThreatColumn) { score += 2000 + (ThreatColumn - option.NearestZombieColumn) * 10; reason = "zombie_near_house"; }
                if (job.Role == "attack" && !option.LaneHasAttack) { score += 1000; reason = reason == "planned_order" ? "uncovered_lane" : "urgent_uncovered_lane"; }
            }
            if (score > bestScore) { bestScore = score; best = (option, reason); }
        }
        return best;
    }

    public void Commit(PlanOption option, string reason, long nowMs, double decisionMs, double commitMs, double scanGapMs)
    {
        var job = Jobs[option.JobIndex]; job.Uses++; Spent += option.Cost;
        job.Waiting = job.Uses >= job.MaxUses ? "done" : "watching_candidates";
        AddEvent(new JsonObject { ["kind"] = "plant_committed", ["job_id"] = job.Id, ["content_id"] = job.ContentId,
            ["column"] = option.Cell.Column, ["row"] = option.Cell.Row, ["cost"] = option.Cost, ["reason"] = reason,
            ["nearest_zombie_column"] = option.NearestZombieColumn, ["lane_has_attack"] = option.LaneHasAttack,
            ["decision_ms"] = decisionMs, ["native_commit_ms"] = commitMs, ["scan_gap_ms"] = scanGapMs, ["at_ms"] = nowMs });
        if (Active && Jobs.All(j => j.Uses >= j.MaxUses)) Stop("completed", nowMs);
    }

    public void Stop(string reason, long nowMs)
    {
        if (!Active) return;
        Status = reason;
        AddEvent(new JsonObject { ["kind"] = reason, ["at_ms"] = nowMs });
    }

    public void AddEvent(JsonObject item)
    {
        item["event_id"] = ++_eventId; _events.Enqueue(item);
        while (_events.Count > 64) _events.Dequeue();
    }

    public JsonObject Snapshot(long nowMs, long? after = null)
    {
        var result = new JsonObject { ["plan_id"] = Id, ["battle_id"] = BattleId, ["status"] = Status,
            ["spent"] = Spent, ["sun_budget"] = SunBudget, ["reserve_sun"] = ReserveSun,
            ["expires_in_ms"] = Math.Max(0, ExpiresAtMs - nowMs), ["event_cursor"] = _eventId,
            ["jobs"] = new JsonArray(Jobs.Select(j => (JsonNode?)new JsonObject { ["id"] = j.Id, ["content_id"] = j.ContentId,
                ["uses"] = j.Uses, ["max_uses"] = j.MaxUses, ["waiting"] = j.Waiting }).ToArray()) };
        if (after.HasValue)
        {
            result["events"] = new JsonArray(_events.Where(e => e["event_id"]!.GetValue<long>() > after.Value).Select(e => e.DeepClone()).ToArray());
            result["events_truncated"] = _events.Count > 0 && after.Value < _events.Peek()["event_id"]!.GetValue<long>() - 1;
        }
        return result;
    }
}
