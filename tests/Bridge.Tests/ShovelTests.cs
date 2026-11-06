using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PvzAiBridge;

static class ShovelTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Add(List<(string, Func<Task>)> tests, string[] args)
    {
        tests.Add(("shovel: native running/display gate retains permission and readiness boundaries", () => {
            foreach (bool manager in new[] { false, true })
            foreach (bool config in new[] { false, true })
            foreach (bool? shown in new bool?[] { null, false, true })
            foreach (bool running in new[] { false, true })
            foreach (bool? permission in new bool?[] { null, false, true })
            {
                var state = ObservationFacts.Shovel(manager, config, shown, running, permission);
                bool expected = manager && config && permission == true && (running || shown == true);
                Check(state["enabled"]!.GetValue<bool>() == expected, $"Gate mismatch: {manager}/{config}/{shown}/{running}/{permission}");
                Check(state["native_can_use"]?.GetValue<bool>() == permission && state["shovel_show"]?.GetValue<bool>() == shown,
                    "Unknown native evidence became a false/true fact");
                Check(JsonNode.DeepEquals(state, SharedObservation.Decode(SharedObservation.Encode(state))), "Compact lost shovel evidence");
            }
            Check(ObservationFacts.Shovel(true, true, false, true, true)["reason"]!.GetValue<string>() == "ready",
                "Running battle still requires tutorial display flag");
            Check(ObservationFacts.Shovel(true, true, false, false, true)["reason"]!.GetValue<string>() == "hidden_outside_battle",
                "Hidden tool became available during non-running selection/loading");
            Check(ObservationFacts.Shovel(true, true, true, true, false)["reason"]!.GetValue<string>() == "native_permission_denied",
                "Unlock/reverse-role refusal was overridden");
            Check(ObservationFacts.Shovel(false, false, null, true, null)["reason"]!.GetValue<string>() == "manager_unavailable",
                "Missing manager diagnostic lost");
            Check(ObservationFacts.Shovel(true, false, false, true, true)["reason"]!.GetValue<string>() == "config_unavailable",
                "Missing native shovel config became available");
            return Task.CompletedTask;
        }));
        tests.Add(("shovel: tool availability does not bypass paused or modal action authorization", () => {
            Check(ObservationFacts.Shovel(true, true, false, true, true)["enabled"]!.GetValue<bool>(), "Regression setup not available");
            var mode = new ControlMode();
            mode.RequireActionState(false, false, false);
            foreach (var (paused, modal) in new[] { (true, false), (false, true), (true, true) }) {
                bool rejected = false;
                try { mode.RequireActionState(paused, false, modal); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Available shovel bypassed action pause/modal gate");
            }
            return Task.CompletedTask;
        }));
        if (args.Length < 2 || args[0].StartsWith("--")) return;
        tests.Add(("shovel compatibility: actual native permission, running/display and layer bindings retained", () => {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
            var native = game.MainModule.Types.Single(t => t.Name == "ShovelManager");
            Check(native.NestedTypes.Single(t => t.Name == "MethodName").Fields.Any(f => f.Name == "CanUseShovel"),
                "NativeAOT CanUseShovel binding absent");
            var press = native.Methods.Single(m => m.Name == "ShovelButtonPressed").Body.Instructions;
            Check(press.Any(i => i.Operand is FieldReference f && f.Name == "shovelShow")
                && press.Any(i => i.Operand is FieldReference f && f.Name == "isGameRunning")
                && press.Any(i => i.Operand is MethodReference m && m.Name == "CanUseShovel"), "Native button gate changed");
            var permission = native.Methods.Single(m => m.Name == "CanUseShovel").Body.Instructions;
            foreach (string method in new[] { "IsUnlocked", "IsIZMMode", "IsIZM2Mode" })
                Check(permission.Any(i => i.Operand is MethodReference m && m.Name == method), "Native permission lost " + method);

            using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
            var entry = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry");
            var observeTool = entry.Methods.Single(m => m.Name == "ObserveShovel").Body.Instructions;
            Check(observeTool.Any(i => (i.Operand as string) == "CanUseShovel")
                && observeTool.Any(i => i.Operand is MethodReference m && m.Name == "get_Running")
                && observeTool.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "ObservationFacts" && m.Name == "Shovel"),
                "Production shovel gate no longer uses tested facts/native permission");
            Check(!observeTool.Any(i => i.OpCode.Code == Code.Stfld || i.Operand is MethodReference m && m.Name is "Set" or "Execute" or "ShovelButtonPressed"),
                "Availability observation mutates native state");
            var enabled = entry.Methods.Single(m => m.Name == "EnabledShovel").Body.Instructions;
            Check(enabled.Any(i => i.Operand is MethodReference m && m.Name == "ObserveShovel")
                && !enabled.Any(i => i.Operand is FieldReference f && f.Name == "shovelShow"), "Actions retain old hard display gate");
            var dispatch = entry.Methods.Single(m => m.Name == "Dispatch").Body.Instructions.ToList();
            int authorization = dispatch.FindIndex(i => i.Operand is MethodReference m && m.Name == "RequireActionState");
            int shovelAction = dispatch.FindIndex(i => i.Operand is MethodReference m && m.Name == "ShovelConfirmed");
            Check(authorization >= 0 && shovelAction > authorization, "Shovel moved ahead of action authorization");
            var action = entry.Methods.Single(m => m.Name == "ShovelConfirmed").Body.Instructions.ToList();
            foreach (string method in new[] { "EnabledShovel", "GetShovelCharacter", "TutorialAllows", "Shovel" })
                Check(action.Any(i => i.Operand is MethodReference m && m.Name == method), "Action lost " + method);
            Check(action.Count(i => i.Operand is MethodReference m && m.Name == "Shovel") == 1,
                "One target action became multiple shovel calls");
            foreach (string forbidden in new[] { "PickShovel", "ShovelButtonPressed", "PacketPickSelect" })
                Check(!action.Any(i => i.Operand is MethodReference m && m.Name == forbidden), "Direct shovel action started holding input");
            foreach (string outcome in new[] { "shoveled", "shovel_effect_confirmed", "unknown_observe_before_retry" })
                Check(action.Any(i => (i.Operand as string) == outcome), "Action lost outcome boundary " + outcome);
            return Task.CompletedTask;
        }));
    }
}
