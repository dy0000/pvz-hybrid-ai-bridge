using PvzAiBridge;

static class FallingCardTests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    public static void Add(List<(string, Func<Task>)> tests)
    {
        tests.Add(("falling: identical contents never merge native instances and IDs survive ordinary observations", () => {
            var ids = new OneUseCardRegistry(); ids.Begin("battle");
            var a = new OneUseCardIdentity(100, 1, 999, 0, "falling_card");
            var b = a with { Instance = 101 };
            long first = ids.Bind(a), second = ids.Bind(b); ids.End();
            Check(first < 0 && second < 0 && first != second, "Distinct drop entities were merged");
            ids.Begin("battle"); Check(ids.Bind(a) == first && ids.Bind(b) == second, "Observation changed live IDs"); ids.End();
            return Task.CompletedTask;
        }));
        tests.Add(("falling: consumption, reinit, removal, source and battle changes retire handles without reuse", () => {
            var ids = new OneUseCardRegistry(); var a = new OneUseCardIdentity(100, 1, 999, 0, "falling_card");
            ids.Begin("battle"); long first = ids.Bind(a); ids.End();
            foreach (var changed in new[] { a with { UseSequence = 1 }, a with { Lifetime = 2 }, a with { Config = 998 }, a with { Source = "free_conveyor" } }) {
                ids.Begin("battle"); Check(ids.Bind(changed) != first, "Old handle survived changed native identity"); ids.End();
            }
            ids.Begin("battle"); ids.End(); ids.Begin("battle"); long returned = ids.Bind(a); ids.End();
            Check(returned != first, "Retired drop ID was reused");
            ids.Begin("other-battle"); Check(ids.Bind(a) != returned, "New battle reused an old ID"); ids.End();
            return Task.CompletedTask;
        }));
        tests.Add(("falling: native free quote is executable without start; paid unknown or native refusal stay unavailable", () => {
            bool Allowed(long? cost, bool affordable) => OneUseCardRegistry.CanUse(true, false, false, false, 0, false, 30, 0, cost, affordable);
            Check(Allowed(0, true) && Allowed(125, true), "Free or paid native quote was rejected");
            Check(!Allowed(null, true) && !Allowed(125, false), "Unknown or unaffordable quote was treated as free");
            Check(!OneUseCardRegistry.CanUse(true, true, false, false, 0, false, 30, 0, 0, true), "Locked drop executable");
            Check(!OneUseCardRegistry.CanUse(true, false, true, false, 0, false, 30, 0, 0, true), "Cooling drop executable");
            Check(!OneUseCardRegistry.CanUse(true, false, false, true, 0, false, 30, 0, 0, true), "Pending drop executable");
            return Task.CompletedTask;
        }));
        tests.Add(("falling: expiration and debounce follow native already-picked state without extending lifetime", () => {
            Check(!OneUseCardRegistry.CanUse(true, false, false, false, 0, false, 1, 1, 0, true), "Expired unselected card executable");
            Check(!OneUseCardRegistry.CanUse(true, false, false, false, .2, false, 10, 1, 0, true), "Debounce bypassed for new pick");
            Check(OneUseCardRegistry.CanUse(true, false, false, false, .2, true, 1, 1, 0, true), "Already-selected native held card rejected");
            return Task.CompletedTask;
        }));
    }
}
