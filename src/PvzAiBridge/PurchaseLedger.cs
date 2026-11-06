using System.Text.Json.Nodes;

namespace PvzAiBridge;

public sealed class PurchaseLedger
{
    private static long NowMs => (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
    private readonly string _path;
    private readonly JsonObject _entries;
    private readonly JsonObject _budgets;
    public PurchaseLedger(string path)
    {
        _path = path;
        bool exists = File.Exists(path);
        var root = exists ? JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("Purchase ledger is invalid; buying is blocked.") : new JsonObject();
        _entries = root["transactions"]?.AsObject() ?? new JsonObject();
        _budgets = root["budgets"]?.AsObject() ?? new JsonObject();
        if (exists && (root["schema_version"]?.GetValue<int>() != 1 || root["transactions"] is not JsonObject || root["budgets"] is not JsonObject))
            throw new InvalidDataException("Incomplete or unknown ledger schema; buying is blocked.");
        ValidateLoaded();
        foreach (var pair in _entries.ToArray())
            if (pair.Value is JsonObject entry && entry["status"]?.GetValue<string>() == "committing") entry["status"] = "unknown";
    }
    private void ValidateLoaded()
    {
        if (_entries.Count > 256 || _budgets.Count > 128) throw new InvalidDataException("Oversized purchase ledger.");
        foreach (var p in _budgets)
        {
            ValidateId(p.Key);
            var b = p.Value as JsonObject ?? throw new InvalidDataException("Invalid stored budget.");
            if (b["max_total"]?.GetValue<int>() is not (>= 0) || b["expires_at_unix_ms"]?.GetValue<long>() is not (> 0)
                || b["allowed_keys"] is not JsonArray keys || keys.Count is < 1 or > 64
                || keys.Any(k => k?.GetValue<string>() is not { Length: >= 1 and <= 128 }))
                throw new InvalidDataException("Invalid stored budget fields.");
        }
        foreach (var p in _entries)
        {
            ValidateId(p.Key);
            var e = p.Value as JsonObject ?? throw new InvalidDataException("Invalid stored transaction.");
            if (e["transaction_id"]?.GetValue<string>() != p.Key || e["price"]?.GetValue<int>() is not (>= 0)
                || e["status"]?.GetValue<string>() is not ("prepared" or "cancelled" or "committing" or "unknown" or "committed")
                || e["budget_id"]?.GetValue<string>() is not string budgetId || _budgets[budgetId] is not JsonObject b
                || !b["allowed_keys"]!.AsArray().Any(k => k!.GetValue<string>() == e["effect_key"]?.GetValue<string>()))
                throw new InvalidDataException("Invalid stored transaction fields; buying is blocked.");
        }
        foreach (var p in _budgets)
        {
            long spent = _entries.Where(e => e.Value!["budget_id"]!.GetValue<string>() == p.Key && e.Value["status"]!.GetValue<string>() != "cancelled")
                .Sum(e => (long)e.Value!["price"]!.GetValue<int>());
            if (spent > p.Value!["max_total"]!.GetValue<int>()) throw new InvalidDataException("Stored purchases exceed total budget.");
        }
    }
    public JsonObject? Get(string id) => _entries[id] is JsonObject e ? (JsonObject)e.DeepClone() : null;
    public bool Unresolved => _entries.Any(p => p.Value?["status"]?.GetValue<string>() is "unknown" or "committing");
    public JsonObject Budget(string id) => _budgets[id] is JsonObject b ? (JsonObject)b.DeepClone() : throw new ArgumentException("Unknown budget_id.");
    public void Authorize(string id, int maxTotal, IEnumerable<string> keys, long expires)
    {
        ValidateId(id); var allowed = keys.Distinct().ToArray();
        if (_budgets.ContainsKey(id) || _budgets.Count >= 128 || maxTotal < 0 || allowed.Length is < 1 or > 64
            || allowed.Any(k => k.Length is < 1 or > 128) || expires <= NowMs)
            throw new ArgumentException("Budget must be fresh, finite, unexpired and name 1..64 allowed effect keys.");
        var grant = new JsonObject { ["max_total"] = maxTotal, ["allowed_keys"] = ObservationFacts.Strings(allowed), ["expires_at_unix_ms"] = expires };
        var next = (JsonObject)_budgets.DeepClone(); next[id] = grant.DeepClone();
        Save(budgets: next); _budgets[id] = grant;
    }
    private static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("ID must be 1..64 ASCII letters, digits, - or _.");
    }
    public void CheckBudget(JsonObject quote)
    {
        string budgetId = quote["budget_id"]?.GetValue<string>() ?? "";
        var budget = Budget(budgetId); int price = quote["price"]!.GetValue<int>();
        long reserved = _entries.Where(p => p.Value?["budget_id"]?.GetValue<string>() == budgetId
            && p.Value?["status"]?.GetValue<string>() is "prepared" or "committing" or "unknown" or "committed")
            .Sum(p => (long)p.Value!["price"]!.GetValue<int>());
        if (budget["expires_at_unix_ms"]!.GetValue<long>() <= NowMs
            || !budget["allowed_keys"]!.AsArray().Any(k => k!.GetValue<string>() == quote["effect_key"]!.GetValue<string>())
            || price < 0 || price > budget["max_total"]!.GetValue<int>() - reserved)
            throw new InvalidOperationException("Purchase is outside the authorized item list, remaining total budget or expiry.");
    }
    public void Prepare(string id, JsonObject quote)
    {
        ValidateId(id);
        if (_entries.ContainsKey(id)) throw new InvalidOperationException("Transaction ID was already used; read shop_receipt.");
        if (_entries.Count >= 256 || Unresolved) throw new InvalidOperationException("Ledger is full or an unknown purchase requires reconciliation; buying is blocked.");
        CheckBudget(quote);
        var entry = (JsonObject)quote.DeepClone(); entry["transaction_id"] = id; entry["status"] = "prepared";
        var next = (JsonObject)_entries.DeepClone(); next[id] = entry.DeepClone();
        Save(entries: next); _entries[id] = entry;
    }
    public void Set(string id, string status, JsonObject? receipt = null)
    {
        var entry = _entries[id] as JsonObject ?? throw new ArgumentException("Unknown transaction_id.");
        string prior = entry["status"]!.GetValue<string>();
        bool allowed = prior == "prepared" && status is "committing" or "cancelled"
            || prior == "committing" && status is "committed" or "unknown";
        if (!allowed) throw new InvalidOperationException("Purchase state cannot be replayed or rewound.");
        var updated = (JsonObject)entry.DeepClone(); updated["status"] = status;
        if (receipt is not null) updated["receipt"] = receipt.DeepClone();
        var next = (JsonObject)_entries.DeepClone(); next[id] = updated.DeepClone();
        Save(entries: next); _entries[id] = updated;
    }
    private void Save(JsonObject? entries = null, JsonObject? budgets = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var data = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["schema_version"] = 1,
                ["transactions"] = (entries ?? _entries).DeepClone(), ["budgets"] = (budgets ?? _budgets).DeepClone() }.ToJsonString()); stream.Write(data); stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }
}
