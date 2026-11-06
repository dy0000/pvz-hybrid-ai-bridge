using System.Text.Json.Nodes;
using PvzAiBridge;

internal static class LiveTests
{
    public static void Add(List<(string, Func<Task>)> tests)
    {
        void Check(bool value, string error) { if (!value) throw new Exception(error); }
        void Rejected(Action action) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, "Expected rejection"); }
        void Add(string name, Action run) => tests.Add(("live: " + name, () => { run(); return Task.CompletedTask; }));
        Add("falling receipt proof survives lost direct response in history and event JSON without aliasing", () => {
            var response = new JsonObject { ["source"] = "falling_card", ["card_id"] = -37,
                ["native_object_id"] = "18446744073709551610", ["native_generation"] = 3,
                ["native_commit_confirmed"] = true, ["selection_released"] = true,
                ["native_use_sequence_before"] = 0L, ["native_use_sequence_after"] = 1L,
                ["state"] = new JsonObject { ["large_state"] = true }, ["token"] = "must-not-forward" };
            var record = new JsonObject { ["request_id"] = "lost-response", ["op"] = "plant", ["ok"] = true, ["outcome"] = "Committed" };
            ReceiptProof.Copy(response, record);
            var expectedProof = (JsonObject)response.DeepClone(); expectedProof.Remove("state"); expectedProof.Remove("token");
            var history = new Queue<JsonObject>(); history.Enqueue(record);
            var journal = new LiveJournal(() => 1000); journal.Add("plant_committed", "battle", "已种下", (JsonObject)record.DeepClone());
            response["selection_released"] = false; response["card_id"] = 99;
            var status = JsonNode.Parse(new JsonObject { ["receipt_history"] = new JsonArray(history.Select(r => r.DeepClone()).ToArray()) }.ToJsonString())!;
            var feed = JsonNode.Parse(journal.Read(0, 1, false).ToJsonString())!;
            foreach (var actual in new[] { status["receipt_history"]![0]!, feed["events"]![0]!["data"]! }) {
                foreach (var field in expectedProof) Check(JsonNode.DeepEquals(actual[field.Key], field.Value), "Lost native proof: " + field.Key);
                Check(actual["state"] is null && actual["token"] is null, "Receipt whitelist forwarded unrelated payload");
            }
            feed["events"]![0]!["data"]!["selection_released"] = false;
            Check(journal.Read(0, 1, false)["events"]![0]!["data"]!["selection_released"]!.GetValue<bool>(), "Reader mutated stored native proof");
        });
        Add("receipt proof never infers release from Committed and preserves false/null facts", () => {
            var legacy = new JsonObject { ["ok"] = true, ["outcome"] = "Committed" };
            var record = new JsonObject(); ReceiptProof.Copy(legacy, record);
            Check(record.Count == 0, "Committed fabricated one-use proof");
            var unknown = new JsonObject { ["source"] = "falling_card", ["native_commit_confirmed"] = false,
                ["selection_released"] = false, ["native_use_sequence_after"] = null };
            ReceiptProof.Copy(unknown, record);
            Check(record["selection_released"]!.GetValue<bool>() == false && record["native_commit_confirmed"]!.GetValue<bool>() == false, "False proof became success");
            Check(record.ContainsKey("native_use_sequence_after") && record["native_use_sequence_after"] is null, "Unknown sequence was silently omitted");
            Check(!record.ContainsKey("card_id") && !record.ContainsKey("native_object_id"), "Missing identity fabricated");
        });
        Add("events page cursors advance through quiet events without skipping speech", () => {
            var j = new LiveJournal(() => 1000); j.Add("card_ready", "b", ""); j.Add("wave", "b", "第二波");
            var p = j.Read(0, 1, true); Check(p["events"]!.AsArray().Count == 0 && p["next_event_id"]!.GetValue<long>() == 1, "Quiet page stalled cursor");
            p = j.Read(1, 1, true); Check(p["events"]!.AsArray().Count == 1 && p["next_event_id"]!.GetValue<long>() == 2, "Speech skipped");
            Check(j.Read(99, 1, false)["reset_required"]!.GetValue<bool>(), "Restart cursor not signalled");
        });
        Add("bounded event history and detached copies", () => {
            var j = new LiveJournal(() => 1000); for (int i = 0; i < 300; i++) j.Add("e", "b", "");
            var p = j.Read(0, 128, false); Check(p["events_truncated"]!.GetValue<bool>(), "Dropped history hidden");
            p["events"]![0]!["kind"] = "changed";
            Check(j.Read(0, 1, false)["events"]![0]!["kind"]!.GetValue<string>() == "e", "Caller mutated journal");
        });
        Add("flow requires authorization and counts confirmed transitions only", () => {
            var f = new LiveFlow(); Rejected(() => f.Authorize("level:0", 0));
            f.Start(2, 1, 10, 0, ""); f.Authorize("level:0", 0); f.Requested("level:0", 0);
            Check(f.Levels == 0, "Request counted as entry"); Rejected(() => f.Authorize("level:0", 1));
            f.Observe("battle1", 2); f.Observe("battle1", 3); Check(f.Levels == 1, "Repeated observation double-counted");
            f.Authorize("retry_level", 4); f.Requested("retry_level", 4); f.Observe("battle2", 5);
            Check(f.Levels == 1 && f.Retries == 1, "Retry counted as a new level"); Rejected(() => f.Authorize("retry_level", 6));
            f.Authorize("next_level", 6); f.Requested("next_level", 6); f.Observe("battle3", 7);
            Rejected(() => f.Authorize("level:2", 8)); f.Authorize("finish_level", 8);
        });
        Add("flow expiry, stop and unknown navigation block new starts but allow return", () => {
            var f = new LiveFlow(); f.Start(1, 0, 10, 0, "battle1"); Check(f.Levels == 1, "Existing battle ignored");
            f.Observe("battle1", 10000); Rejected(() => f.Authorize("next_level", 10001)); f.Authorize("finish_level", 10001);
            f.Start(2, 1, 100, 11000, ""); f.Requested("level:0", 11000); f.Observe("", 32000);
            Check(f.Status == "transition_unknown", "Unknown transition remained replayable"); Rejected(() => f.Authorize("level:0", 32001));
            f.Start(2, 0, 100, 40000, ""); f.Stop("client"); Rejected(() => f.Authorize("next_level", 40001));
        });
    }
}
