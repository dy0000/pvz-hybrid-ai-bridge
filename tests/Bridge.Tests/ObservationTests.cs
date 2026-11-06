using System.Text.Json;
using System.Text.Json.Nodes;
using PvzAiBridge;
using Mono.Cecil;

static class ObservationTests
{
    public static void Add(List<(string, Func<Task>)> tests, string[] args)
    {
        tests.Add(("observation: selection capacity preserves total and unknown through compact JSON", () => {
            foreach (int? capacity in new int?[] { null, 0, 7, 10, 16 }) {
                var state = new JsonObject { ["selecting_cards"] = true, ["selection_capacity"] = JsonValue.Create(capacity),
                    ["chosen_cards"] = new JsonArray("LockedCard"),
                    ["chosen_card_details"] = new JsonArray(new JsonObject { ["content_id"] = "LockedCard", ["locked"] = true }) };
                var decoded = SharedObservation.Decode(SharedObservation.Encode(state));
                if (!JsonNode.DeepEquals(state, decoded) || decoded["selection_capacity"]?.GetValue<int>() != capacity)
                    throw new Exception("Selection total/unknown/locked evidence changed in compact view");
            }
            return Task.CompletedTask;
        }));
        if (args.Length >= 2 && !args[0].StartsWith("--")) tests.Add(("observation: selection capacity reads initialized native limit without mutation", () => {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
            var seed = game.MainModule.Types.Single(t => t.Name == "TowerDefenseInGameSeedBank");
            var initialized = seed.NestedTypes.Single(t => t.Name == "PropertyName").Fields.Any(f => f.Name == "_referencesInitialized");
            if (!initialized) throw new Exception("Native initialization evidence unavailable");
            var init = seed.Methods.Single(m => m.Name == "InitializeReferences").Body.Instructions.ToList();
            int resolve = init.FindIndex(i => i.Operand is MethodReference m && m.Name == "GetPacketSlotNum");
            int ready = init.FindIndex(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld && i.Operand is FieldReference f && f.Name == "_referencesInitialized");
            if (resolve < 0 || ready <= resolve) throw new Exception("Ready flag no longer proves native capacity initialization");
            if (!seed.Methods.Single(m => m.Name == "CanAddPacket").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "seedbankPacketMax"))
                throw new Exception("Native CanAddPacket no longer uses reported limit");
            using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
            var entry = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry");
            var read = entry.Methods.Single(m => m.Name == "SelectionCapacity").Body.Instructions;
            if (!read.Any(i => i.Operand is FieldReference f && f.Name == "seedbankPacketMax")
                || !read.Any(i => (i.Operand as string) == "_referencesInitialized")
                || read.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld
                    || i.Operand is MethodReference m && m.Name is "GetPacketSlotNum" or "GetFeatureValue" or "AddPacket" or "Set"))
                throw new Exception("Capacity bypasses readiness or mutates/reconstructs the native limit");
            var observe = entry.Methods.Single(m => m.Name == "Observe").Body.Instructions;
            if (!observe.Any(i => i.Operand is MethodReference m && m.Name == "SelectionCapacity")
                || observe.Count(i => (i.Operand as string) == "selection_capacity") != 2)
                throw new Exception("Observe lost nullable default or selection projection");
            return Task.CompletedTask;
        }));
        tests.Add(("observation: unknown daylight remains null under NativeAOT serialization", () => {
            if (JsonSerializer.IsReflectionEnabledByDefault) throw new Exception("Reflection-disabled regression host required");
            var unknown = JsonNode.Parse(ObservationFacts.Environment(null, false, null).ToJsonString())!;
            if (unknown["status"]!.GetValue<string>() != "unknown" || unknown["is_night"] is not null || unknown["source"] is not null)
                throw new Exception("Missing environment became a false daytime observation");
            foreach (bool night in new[] { false, true }) {
                var environment = JsonNode.Parse(ObservationFacts.Environment(night, true, "res://native-map.tres").ToJsonString())!;
                if (environment["is_night"]!.GetValue<bool>() != night || environment["day_phase"]!.GetValue<string>() != (night ? "night" : "day")
                    || environment["transitioning"]!.GetValue<bool>() != true || environment["source"]!.GetValue<string>() != "TowerDefenseBattleFeatureMap.config.isNight")
                    throw new Exception("Native day/night or transition evidence changed");
            }
            return Task.CompletedTask;
        }));
        tests.Add(("observation: partial absence never proves missing gravestones or upper plants", () => {
            if (ObservationFacts.Presence(0, false) is not null || ObservationFacts.Presence(0, true) != false
                || ObservationFacts.Presence(1, false) != true || ObservationFacts.Presence(3, true) != true)
                throw new Exception("Presence lost three-valued evidence semantics");
            var array = ObservationFacts.Strings(new[] { "POT", "LILYPAD", "AIR" });
            if (array.ToJsonString() != "[\"POT\",\"LILYPAD\",\"AIR\"]") throw new Exception("Terrain list needs runtime serializer metadata");
            return Task.CompletedTask;
        }));
        tests.Add(("observation: hybrid attacking pots retain native support identity", () => {
            foreach (int unrelatedFlags in new[] { 0, 1, 256, 512 })
                if (!ObservationFacts.IsSupport("POT", unrelatedFlags)) throw new Exception("Unrelated flags overrode native POT support");
            if (!ObservationFacts.IsSupport("LILYPAD", 0) || !ObservationFacts.IsSupport("NOONE", 2)
                || !ObservationFacts.IsSupport("NOONE", 4) || ObservationFacts.IsSupport("NOONE", 1))
                throw new Exception("Support flags confused armor with terrain support");
            return Task.CompletedTask;
        }));
    }
}
