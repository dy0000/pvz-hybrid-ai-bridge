using System.Text.Json.Nodes;

namespace PvzAiBridge;

// Owns the single irreversible native purchase, including clicks on its native
// dialog. A failed durable intent can never reach nativeBuy.
public static class PurchaseLifecycle
{
    public static JsonObject QuizResult(JsonObject entry, string requestedStatus, bool replayed = false)
    {
        string status = entry["status"]?.GetValue<string>() ?? "unknown";
        return new JsonObject { ["ok"] = status == requestedStatus,
            ["outcome"] = status switch { "committed" => "quiz_entry_committed", "cancelled" => "quiz_entry_cancelled",
                "prepared" => "quiz_confirmation_prepared", _ => "unknown_observe_before_retry" },
            ["transaction"] = entry.DeepClone(), ["replayed"] = replayed };
    }

    public static JsonObject Confirm(PurchaseLedger ledger, string id, string effectType, int amount,
        Func<(long Coins, long Value)> read, Action<long, long> validate, Action nativeBuy)
    {
        var entry = ledger.Get(id) ?? throw new ArgumentException("Unknown transaction_id.");
        if (entry["status"]!.GetValue<string>() != "prepared") return entry;
        try
        {
            var before = read();
            validate(before.Coins, before.Value);
            ledger.Set(id, "committing");
            nativeBuy();
            var after = read();
            int price = entry["price"]!.GetValue<int>();
            bool gained = effectType == "Feature" ? after.Value == before.Value + amount : before.Value == 0 && after.Value == 1;
            bool committed = gained && before.Coins - after.Coins == price;
            ledger.Set(id, committed ? "committed" : "unknown", new JsonObject {
                ["coins_before"] = before.Coins, ["coins_after"] = after.Coins,
                ["value_before"] = before.Value, ["value_after"] = after.Value,
                ["price"] = price, ["native_confirmed"] = committed });
        }
        catch (Exception ex)
        {
            string status = ledger.Get(id)!["status"]!.GetValue<string>();
            var error = new JsonObject { ["error"] = ex.GetBaseException().Message };
            if (status == "prepared") ledger.Set(id, "cancelled", error);
            else if (status == "committing") ledger.Set(id, "unknown", error);
        }
        return ledger.Get(id)!;
    }

    public static JsonObject Close(PurchaseLedger ledger, string id)
    {
        var entry = ledger.Get(id) ?? throw new ArgumentException("Unknown transaction_id.");
        if (entry["status"]!.GetValue<string>() == "prepared")
            ledger.Set(id, "cancelled", new JsonObject { ["reason"] = "native_confirmation_closed_without_purchase" });
        return ledger.Get(id)!;
    }
}

