using System.Text.Json.Nodes;
using PvzAiBridge;

internal static class PlanTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JsonObject Body() => JsonNode.Parse("""
        {"battle_id":"battle","plan_id":"plan","sun_budget":500,"reserve_sun":50,"ttl_seconds":10,
         "jobs":[{"id":"economy","content_id":"Sun","role":"economy","priority":100,"cells":[{"column":1,"row":1}]},
                 {"id":"defense","content_id":"Pea","role":"attack","max_uses":2,"cells":[{"column":2,"row":2}]}]}
        """)!.AsObject();
    private static PlantPlan Make(JsonObject? body = null) => PlantPlan.Parse(body ?? Body(), 0, 9, 5, new HashSet<string> { "Sun", "Pea" });
    private static PlanOption Economy => new(0, new(1, 1), 1, 50, 0, false);
    private static PlanOption Attack(int zombie = 8, bool covered = false) => new(1, new(2, 2), 2, 100, zombie, covered);
    public static void Add(List<(string, Func<Task>)> tests)
    {
        void Add(string name, Action action) => tests.Add(("plan: " + name, () => { action(); return Task.CompletedTask; }));
        Add("uncovered/urgent lanes preempt economy; disable restores explicit order", () => {
            var p = Make();
            Check(p.Choose([Economy, Attack()], 500, 1)?.Option.JobIndex == 1, "Uncovered lane was ignored");
            Check(p.Choose([Economy, Attack(8, true)], 500, 1)?.Option.JobIndex == 0, "Ordinary priority not honored");
            Check(p.Choose([Economy, Attack(3, true)], 500, 1)?.Reason == "zombie_near_house", "Urgent lane not prioritized");
            var body = Body(); body["emergency_priority"] = false;
            Check(Make(body).Choose([Economy, Attack(1)], 500, 1)?.Option.JobIndex == 0, "Disabled emergency policy still preempted");
        });
        Add("budget and reserve remain hard limits during emergencies", () => {
            var p = Make();
            Check(p.Choose([Attack(1)], 149, 1) is null, "Reserve overspent");
            Check(p.Choose([Attack(1)], 150, 1) is not null, "Exact reserve boundary rejected");
            var body = Body(); body["sun_budget"] = 99;
            Check(Make(body).Choose([Attack(1)], 9999, 1) is null, "Emergency exceeded budget");
        });
        Add("committed use limits, replenishment and spent accounting", () => {
            var p = Make(); var a = Attack();
            p.Commit(a, "uncovered_lane", 1, .1, 1, 50);
            Check(p.Spent == 100 && p.Jobs[1].Uses == 1, "Commit not accounted");
            Check(p.Choose([a], 500, 2) is not null, "Authorized replacement lost");
            p.Commit(a, "planned_order", 2, .1, 1, 50);
            Check(p.Choose([a], 500, 3) is null, "Exceeded max uses");
            p.Commit(Economy, "planned_order", 3, .1, 1, 50);
            Check(p.Status == "completed" && p.Spent == 250, "Plan did not finish");
        });
        Add("cancel, expiry and unknown outcomes never authorize another submission", () => {
            foreach (var reason in new[] { "cancelled", "control_released", "battle_changed", "unknown_result", "native_timeout" }) {
                var p = Make(); p.Stop(reason, 2);
                Check(p.Choose([Attack()], 1000, 3) is null && p.Status == reason, reason + " still executed");
            }
            var expiring = Make(); Check(expiring.Choose([Attack()], 1000, 10000) is null && expiring.Status == "expired", "Expiry ignored");
            var inflight = Make(); inflight.Stop("cancelled", 2); inflight.Commit(Attack(), "planned_order", 3, .1, 2, 50);
            Check(inflight.Status == "cancelled" && inflight.Spent == 100, "In-flight completion erased cancellation or cost");
        });
        Add("threat-only tasks wait and stable ties preserve candidate order", () => {
            var body = Body(); body["jobs"]![1]!["threatened_only"] = true;
            Check(Make(body).Choose([Attack(0)], 500, 1) is null, "Threat-only planted without zombies");
            var p = Make(); var a = Attack(); var b = a with { Cell = new(4, 2) };
            Check(p.Choose([a, b], 500, 1)?.Option.Cell.Column == 2, "Tie changed explicit candidate order");
        });
        Add("reject unauthorized cards, bounds, duplicated jobs and unbounded plans", () => {
            var invalid = new List<JsonObject>();
            var b = Body(); b["jobs"]![0]!["content_id"] = "Locked"; invalid.Add(b);
            b = Body(); b["jobs"]![0]!["cells"]![0]!["row"] = 6; invalid.Add(b);
            b = Body(); b["jobs"]![1]!["id"] = "economy"; invalid.Add(b);
            b = Body(); b["ttl_seconds"] = 601; invalid.Add(b);
            b = Body(); b["sun_budget"] = -1; invalid.Add(b);
            foreach (var item in invalid) {
                bool rejected = false; try { Make(item); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Malformed plan accepted");
            }
        });
        Add("incremental events are bounded and signal cursor gaps", () => {
            var p = Make(); for (int i = 0; i < 70; i++) p.AddEvent(new JsonObject { ["kind"] = "sample" });
            var all = p.Snapshot(0, 0); Check(all["events"]!.AsArray().Count == 64 && all["events_truncated"]!.GetValue<bool>(), "History is unbounded or silently missing");
            var next = p.Snapshot(0, 69); Check(next["events"]!.AsArray().Count == 1 && next["event_cursor"]!.GetValue<long>() == 70, "Cursor replayed old events");
            Check(p.Snapshot(0)["events"] is null, "Observe summary includes event history");
        });
    }
}
