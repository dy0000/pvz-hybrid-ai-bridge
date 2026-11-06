using System.Text.Json;
using System.Text.Json.Nodes;
using PvzAiBridge;

static class ExtendedTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Invalid operation was accepted"); }
    static string LedgerPath() => Path.Combine(Path.GetTempPath(), "pvz-ledger-test-" + Guid.NewGuid().ToString("N") + ".json");
    static JsonObject Quote(int price, string key = "Upgrade", string budget = "budget") => new() { ["price"] = price, ["effect_key"] = key, ["budget_id"] = budget };
    static PurchaseLedger Fund(string path) { var ledger = new PurchaseLedger(path); ledger.Authorize("budget", 100, new[] { "Upgrade" }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000); return ledger; }
    public static void Add(List<(string, Func<Task>)> tests)
    {
        tests.Add(("extended: player confirmation uses durable intent and native close cannot release spent budget", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path); ledger.Prepare("manual", Quote(60));
                long coins = 100, value = 0; int buys = 0;
                void Buy() {
                    Check(JsonNode.Parse(File.ReadAllText(path))!["transactions"]!["manual"]!["status"]!.GetValue<string>() == "committing", "Native purchase preceded durable intent");
                    buys++; coins -= 60; value++;
                }
                var entry = PurchaseLifecycle.Confirm(ledger, "manual", "Feature", 1, () => (coins, value), (_, _) => {}, Buy);
                Check(entry["status"]!.GetValue<string>() == "committed", "Player purchase not confirmed");
                PurchaseLifecycle.Close(ledger, "manual");
                PurchaseLifecycle.Confirm(ledger, "manual", "Feature", 1, () => (coins, value), (_, _) => {}, Buy);
                Check(buys == 1 && ledger.Get("manual")!["status"]!.GetValue<string>() == "committed", "Native close cancelled or replayed a purchase");
                Reject(() => ledger.Prepare("overspend", Quote(41)));
            } finally { File.Delete(path); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: native cancel/refusal release only unspent reservations; partial effects stay unknown", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path); ledger.Prepare("cancel", Quote(60)); PurchaseLifecycle.Close(ledger, "cancel");
                int buys = 0;
                PurchaseLifecycle.Confirm(ledger, "cancel", "Feature", 1, () => (100, 0), (_, _) => {}, () => buys++);
                Check(buys == 0, "Cancelled native dialog purchased");
                ledger.Prepare("refused", Quote(60));
                var refused = PurchaseLifecycle.Confirm(ledger, "refused", "Feature", 1, () => (100, 0), (_, _) => throw new InvalidOperationException("quote changed"), () => buys++);
                Check(refused["status"]!.GetValue<string>() == "cancelled" && buys == 0, "Pre-spend refusal retained or spent a reservation");
                ledger.Prepare("partial", Quote(60)); long coins = 100;
                var unknown = PurchaseLifecycle.Confirm(ledger, "partial", "Feature", 1, () => (coins, 0), (_, _) => {}, () => coins -= 60);
                Check(unknown["status"]!.GetValue<string>() == "unknown", "Debit without gain was claimed successful");
                PurchaseLifecycle.Close(ledger, "partial");
                Check(ledger.Unresolved, "Native close released uncertain spending");
            } finally { File.Delete(path); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: failed intent persistence never calls native purchase", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path); ledger.Prepare("once", Quote(60));
                Directory.CreateDirectory(path + ".tmp"); int buys = 0;
                try { PurchaseLifecycle.Confirm(ledger, "once", "Feature", 1, () => (100, 0), (_, _) => {}, () => buys++); } catch (IOException) {} catch (UnauthorizedAccessException) {}
                Check(buys == 0, "Failed durable intent reached native sale");
            } finally { Directory.Delete(path + ".tmp"); File.Delete(path); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: failed receipt persistence stays committing in memory and unknown on restart", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path); ledger.Prepare("once", Quote(60)); long coins = 100, value = 0; int buys = 0;
                bool failed = false;
                try { PurchaseLifecycle.Confirm(ledger, "once", "Feature", 1, () => (coins, value), (_, _) => {}, () => {
                    buys++; coins -= 60; value++; Directory.CreateDirectory(path + ".tmp");
                }); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && buys == 1 && ledger.Get("once")!["status"]!.GetValue<string>() == "committing", "Failed durable receipt was presented as committed");
                PurchaseLifecycle.Close(ledger, "once");
                Check(new PurchaseLedger(path).Unresolved, "Receipt persistence failure lost crash protection");
                PurchaseLifecycle.Confirm(ledger, "once", "Feature", 1, () => (coins, value), (_, _) => {}, () => buys++);
                Check(buys == 1, "Failed receipt enabled replay");
            } finally { if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp"); File.Delete(path); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: preparation refuses both modal and tree pause without forcing turn mode", () => {
            ControlMode.RequirePreparation(false, false);
            Reject(() => ControlMode.RequirePreparation(true, false)); Reject(() => ControlMode.RequirePreparation(false, true));
            return Task.CompletedTask;
        }));
        tests.Add(("extended: compact codec is lossless for null, arrays and reserved literal keys under AOT", () => {
            Check(!JsonSerializer.IsReflectionEnabledByDefault, "AOT reflection switch is not disabled");
            var source = new JsonObject { ["cells"] = new JsonArray(), ["unknown"] = null,
                ["literal"] = new JsonObject { ["$shared_ref"] = "native-literal-value" },
                ["other"] = new JsonObject { ["$shared_literal"] = new JsonObject { ["x"] = 1 } } };
            for (int i = 0; i < 30; i++) source["cells"]!.AsArray().Add(new JsonObject { ["row"] = i,
                ["support"] = new JsonObject { ["source"] = "native", ["requirement"] = new string('x', 100) } });
            var packed = SharedObservation.Encode(source);
            Check(JsonNode.DeepEquals(source, SharedObservation.Decode(JsonNode.Parse(packed.ToJsonString())!.AsObject())), "Compact encoding lost facts");
            Check(packed.ToJsonString().Length < source.ToJsonString().Length, "Repeated metadata was not reduced");
            return Task.CompletedTask;
        }));
        tests.Add(("extended: purchase reservations, total budget, allowlist and cancellation persist", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path);
                Reject(() => ledger.Prepare("bad", Quote(1, "DifferentItem")));
                Reject(() => ledger.Prepare("bad", Quote(101)));
                ledger.Prepare("first", Quote(60));
                Reject(() => ledger.Prepare("second", Quote(41)));
                ledger.Set("first", "cancelled");
                ledger.Prepare("second", Quote(100)); ledger.Set("second", "committing"); ledger.Set("second", "committed", new JsonObject { ["confirmed"] = true });
                var restored = new PurchaseLedger(path);
                Check(restored.Get("second")!["status"]!.GetValue<string>() == "committed", "Receipt was not durable");
                Reject(() => restored.Prepare("third", Quote(1)));
                Reject(() => restored.Prepare("second", Quote(0)));
                Reject(() => restored.Set("second", "committing"));
            } finally { File.Delete(path); File.Delete(path + ".tmp"); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: crash between commit intent and receipt blocks replay and further purchases", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path); ledger.Prepare("once", Quote(20)); ledger.Set("once", "committing");
                var restored = new PurchaseLedger(path);
                Check(restored.Unresolved && restored.Get("once")!["status"]!.GetValue<string>() == "unknown", "Crash did not quarantine outcome");
                Reject(() => restored.Prepare("new-id", Quote(20))); Reject(() => restored.Set("once", "committing"));
            } finally { File.Delete(path); File.Delete(path + ".tmp"); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: expired, reused and malformed shopping grants are refused", () => {
            string path = LedgerPath(); try {
                var ledger = Fund(path);
                Reject(() => ledger.Authorize("budget", 100, new[] { "Upgrade" }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000));
                Reject(() => ledger.Authorize("expired", 100, new[] { "Upgrade" }, 0));
                Reject(() => ledger.Prepare("../bad", Quote(1)));
            } finally { File.Delete(path); File.Delete(path + ".tmp"); }
            return Task.CompletedTask;
        }));
        tests.Add(("extended: incomplete or corrupted durable records never reset purchase protection", () => {
            string path = LedgerPath(); try {
                foreach (string invalid in new[] { "{}", "{\"schema_version\":1,\"transactions\":{}}", "{\"schema_version\":1,\"transactions\":{\"x\":null},\"budgets\":{}}" }) {
                    File.WriteAllText(path, invalid);
                    bool rejected = false;
                    try { _ = new PurchaseLedger(path); } catch (Exception) { rejected = true; }
                    Check(rejected, "Malformed file reset ledger safety");
                }
            } finally { File.Delete(path); }
            return Task.CompletedTask;
        }));
    }
}
