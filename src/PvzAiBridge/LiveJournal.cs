using System.Text.Json.Nodes;

namespace PvzAiBridge;

public sealed class LiveJournal(Func<long> clock)
{
    public string StreamId { get; } = Guid.NewGuid().ToString("N");
    public long Cursor { get; private set; }
    private readonly Queue<JsonObject> _items = new();
    public void Add(string kind, string battle, string speech, JsonObject? data = null)
    {
        _items.Enqueue(new JsonObject { ["event_id"] = ++Cursor, ["kind"] = kind, ["battle_id"] = battle,
            ["at_unix_ms"] = clock(), ["speech"] = speech,
            ["source"] = "game_observation", ["data"] = data });
        while (_items.Count > 256) _items.Dequeue();
    }
    public JsonObject Read(long after, int limit, bool narrationOnly)
    {
        if (after < 0 || limit is < 1 or > 128) throw new ArgumentException("Invalid event cursor/limit.");
        var page = _items.Where(i => i["event_id"]!.GetValue<long>() > after).Take(limit).ToArray();
        return new JsonObject { ["stream_id"] = StreamId, ["latest_event_id"] = Cursor,
            ["next_event_id"] = page.Length > 0 ? page[^1]["event_id"]!.GetValue<long>() : Math.Min(after, Cursor),
            ["reset_required"] = after > Cursor,
            ["events_truncated"] = _items.Count > 0 && after < _items.Peek()["event_id"]!.GetValue<long>() - 1,
            ["events"] = new JsonArray(page.Where(i => !narrationOnly || i["speech"]!.GetValue<string>().Length > 0).Select(i => i.DeepClone()).ToArray()) };
    }
}

public sealed class LiveFlow
{
    public bool Active { get; private set; }
    public int Levels { get; private set; }
    public int Retries { get; private set; }
    public int MaxLevels { get; private set; }
    public int MaxRetries { get; private set; }
    public long Deadline { get; private set; }
    public string Pending { get; private set; } = "";
    public string Status { get; private set; } = "idle";
    private string _battle = "";
    private long _requestedAt;
    public void Start(int levels, int retries, int seconds, long now, string battle)
    {
        if (Active) throw new InvalidOperationException("Stop the current flow before replacing it.");
        if (levels is < 1 or > 100 || retries is < 0 or > 20 || seconds is < 10 or > 14400) throw new ArgumentException("Invalid flow limits.");
        Active = true; Status = "active"; MaxLevels = levels; MaxRetries = retries;
        Levels = battle.Length > 0 ? 1 : 0; Retries = 0; _battle = battle; Pending = ""; Deadline = now + seconds * 1000L;
    }
    public static bool StartsBattle(string action) => action.StartsWith("level:") || action is "start_daily" or "start_trial" or "next_level" or "retry_level";
    public void Authorize(string action, long now)
    {
        if (!StartsBattle(action)) return;
        if (!Active) throw new InvalidOperationException("Start a bounded live flow before starting/retrying a level.");
        if (now >= Deadline) { Stop("time_limit"); throw new InvalidOperationException("Live flow time limit reached."); }
        if (Pending.Length > 0) throw new InvalidOperationException("A level transition is already pending; observe before another action.");
        if (action == "retry_level" ? Retries >= MaxRetries : Levels >= MaxLevels)
            throw new InvalidOperationException("Live flow level/retry limit reached.");
    }
    public void Requested(string action, long now)
    { if (StartsBattle(action)) { Pending = action; _requestedAt = now; } }
    public void Observe(string battle, long now)
    {
        if (!Active) return;
        if (now >= Deadline) { Stop("time_limit"); return; }
        if (battle.Length > 0 && battle != _battle)
        {
            if (Pending == "retry_level") Retries++; else Levels++;
            Pending = ""; _battle = battle;
            if (Levels > MaxLevels || Retries > MaxRetries) Stop("limit_exceeded_externally");
        }
        else if (Pending.Length > 0 && now - _requestedAt > 20000) Stop("transition_unknown");
    }
    public void Stop(string reason) { Active = false; Status = reason; }
    public JsonObject Snapshot(long now) => new() { ["active"] = Active, ["status"] = Status, ["levels_started"] = Levels,
        ["retries_started"] = Retries, ["max_levels"] = MaxLevels, ["max_retries"] = MaxRetries,
        ["remaining_ms"] = Math.Max(0, Deadline - now), ["pending_action"] = Pending };
}
