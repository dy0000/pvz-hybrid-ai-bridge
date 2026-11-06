using System.Text.Json.Nodes;
using Mono.Cecil;
using PvzAiBridge;

internal static class GameplayTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Add(List<(string, Func<Task>)> tests, string[] args)
    {
        tests.Add(("gameplay: rejected or uncertain native quiz entry never gets a successful confirmation envelope", () => {
            foreach (string status in new[] { "prepared", "committing", "unknown", "cancelled", "committed" })
            {
                var stored = new JsonObject { ["status"] = status };
                var confirm = PurchaseLifecycle.QuizResult(stored, "committed", true);
                var cancel = PurchaseLifecycle.QuizResult(stored, "cancelled");
                Check(confirm["ok"]!.GetValue<bool>() == (status == "committed"), "Confirmation falsely succeeded for " + status);
                Check(cancel["ok"]!.GetValue<bool>() == (status == "cancelled"), "Cancellation falsely succeeded for " + status);
                if (status is "unknown" or "committing") Check(confirm["outcome"]!.GetValue<string>().Contains("unknown"), "Unknown lost its recovery instruction");
                confirm["transaction"]!["status"] = "tampered";
                Check(stored["status"]!.GetValue<string>() == status, "Response reader mutated durable receipt");
            }
            return Task.CompletedTask;
        }));
        tests.Add(("gameplay: quiz entry shares the human coin budget and cannot replay after reconnect", () => {
            string path = Path.Combine(Path.GetTempPath(), "pvz-quiz-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var ledger = new PurchaseLedger(path);
                ledger.Authorize("human", 100, ["Upgrade", "quiz_entry"], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000);
                JsonObject Quote(int price, string key) => new() { ["price"] = price, ["effect_key"] = key, ["budget_id"] = "human" };
                ledger.Prepare("shop", Quote(60, "Upgrade")); ledger.Set("shop", "committing"); ledger.Set("shop", "committed");
                bool refused = false;
                try { ledger.Prepare("too-much", Quote(41, "quiz_entry")); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "Quiz ignored previous shopping reservations/spending");
                ledger.Prepare("entry", Quote(40, "quiz_entry")); long coins = 90; bool running = false; int entries = 0;
                void NativeRun()
                {
                    Check(new PurchaseLedger(path).Get("entry")!["status"]!.GetValue<string>() == "unknown", "Native debit preceded durable committing intent");
                    coins -= 40; running = true; entries++;
                }
                var result = PurchaseLifecycle.Confirm(ledger, "entry", "QuizEntry", 1, () => (coins, running ? 1 : 0), (_, _) => {}, NativeRun);
                Check(result["status"]!.GetValue<string>() == "committed" && entries == 1, "Exact debit + started quiz was not confirmed");
                ledger = new PurchaseLedger(path);
                PurchaseLifecycle.Confirm(ledger, "entry", "QuizEntry", 1, () => (coins, running ? 1 : 0), (_, _) => {}, NativeRun);
                Check(entries == 1, "Lost direct response re-entered the quiz");
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        }));
        tests.Add(("gameplay: pending effects and exact native activation proof survive history projection", () => {
            var response = new JsonObject { ["entity_id"] = 71, ["native_activation_confirmed"] = true,
                ["native_effect_confirmed"] = null, ["selection_released"] = true, ["completion_pending"] = true,
                ["native_count_before"] = 3, ["native_count_after"] = 4, ["state"] = new JsonObject(), ["token"] = "secret" };
            var record = new JsonObject(); ReceiptProof.Copy(response, record);
            response["completion_pending"] = false;
            var roundtrip = JsonNode.Parse(record.ToJsonString())!;
            Check(roundtrip["completion_pending"]!.GetValue<bool>() && roundtrip["native_effect_confirmed"] is null, "Submitted effect became completed");
            Check(roundtrip["entity_id"]!.GetValue<int>() == 71 && roundtrip["native_count_after"]!.GetValue<int>() == 4, "Native target/count proof lost");
            Check(roundtrip["state"] is null && roundtrip["token"] is null, "Unrelated payload forwarded");
            return Task.CompletedTask;
        }));
        if (args.Length < 2 || args[0].StartsWith("--")) return;
        tests.Add(("gameplay compatibility: named NativeAOT calls and properties exist on the exact game", () => {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
            foreach (var (type, nested, member) in new[] {
                ("DialogManager", "PropertyName", "_dialogLayer"),
                ("TowerDefenseBattleFeatureGemMatch", "MethodName", "GetClearMatchCountTarget"),
                ("TowerDefenseBattleFeatureGemMatch", "PropertyName", "_boardRevision"),
                ("TowerDefenseBattleFeatureGemMatch", "PropertyName", "_state"),
                ("TowerDefenseBattleFeatureSlotMachine", "PropertyName", "_spinInProgress"),
                ("TowerDefenseBattleProcessQuiz", "MethodName", "DisconnectPendingRunDialog"),
                ("TowerDefenseBattleProcessQuiz", "PropertyName", "_pendingRunDialog"),
                ("TowerDefenseHammerMode", "PropertyName", "hammer"),
                ("TowerDefenseMowerManager", "PropertyName", "_isDraggingPreview"),
                ("Almanac", "PropertyName", "propKey"), ("TryLevel", "PropertyName", "dragMenu") })
            {
                var native = game.MainModule.Types.Single(t => t.Name == type);
                Check(native.NestedTypes.Single(t => t.Name == nested).Fields.Any(f => f.Name == member), $"Missing native binding {type}.{nested}.{member}");
                Check(native.Methods.Single(m => m.Name == (nested == "MethodName" ? "InvokeGodotClassMethod" : "GetGodotClassPropertyValue"))
                    .Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == member), $"Binding is absent from native dispatch: {type}.{member}");
            }
            return Task.CompletedTask;
        }));
        tests.Add(("gameplay compatibility: entity skills retain native input gates and release instead of bypassing damage/cooldown", () => {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
            using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
            var entry = bridge.MainModule.Types.Single(t => t.Name == "Entry");
            var click = entry.Methods.Single(m => m.Name == "EntityClick");
            Check(click.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt
                && i.Operand is MethodReference m && m.Name == "_Input" && m.DeclaringType.Name == "Node"), "Pointer event did not virtually dispatch to exact native entity input");
            var nativeInput = game.MainModule.Types.Single(t => t.Name == "TowerDefenseCharacter").Methods.Single(m => m.Name == "_Input");
            Check(nativeInput.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "DispatchRuntimeInput"), "Entity no longer dispatches native component input");
            var gate = game.MainModule.Types.Single(t => t.Name == "MousePressComponent").Methods.Single(m => m.Name == "CanInteract");
            Check(new[] { "CanRunAbility", "IsGameRunning" }.All(name => gate.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == name)), "Native skill lifecycle/running gates changed");
            Check(gate.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_cooldown"), "Native cooldown gate changed");
            foreach (var method in entry.Methods.Where(m => m.Name is "InteractEntity" or "GameplayAction" or "SlideCell"))
                Check(!method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name is "Hurt" or "Destroy" or "SetAlive" or "ParseInputEvent" or "WarpMouse"), "Gameplay endpoint bypassed a native rule or injected desktop input");
            Check(entry.Methods.Single(m => m.Name == "InteractEntity").Body.ExceptionHandlers.Any(h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Finally), "Native pointer release is not protected by finally");
            var slide = entry.Methods.Single(m => m.Name == "SlideCell").Body.Instructions;
            foreach (string name in new[] { "Cell", "CheckMapGridPosIn", "CanMowerMove", "CanMoveToCell", "MoveToCell" })
                Check(slide.Any(i => i.Operand is MethodReference m && m.Name == name), "Slide lost native destination/source gate: " + name);
            Check(!slide.Any(i => i.Operand is MethodReference m && m.Name is "Abs" or "DistanceTo"), "Slide imposed an adjacency rule absent from native pointer movement");
            var nativeMove = game.MainModule.Types.Single(t => t.Name == "TowerDefenseCellInstance").Methods.Single(m => m.Name == "CanMoveToCell").Body.Instructions;
            Check(nativeMove.Any(i => i.Operand is MethodReference m && m.Name == "SequenceEqual") &&
                nativeMove.Any(i => i.Operand is FieldReference f && f.Name == "characterList"), "Native target terrain/occupancy contract changed");
            return Task.CompletedTask;
        }));
        tests.Add(("gameplay compatibility: quiz wraps its own confirmation only after durable prepare", () => {
            using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
            var entry = bridge.MainModule.Types.Single(t => t.Name == "Entry");
            var action = entry.Methods.Single(m => m.Name == "QuizAction").Body.Instructions.ToList();
            int prepare = action.FindIndex(i => i.Operand is MethodReference m && m.Name == "Prepare" && m.DeclaringType.Name == "PurchaseLedger");
            int popup = action.FindIndex(i => i.Operand is MethodReference m && m.Name == "RunButtonPressed");
            int disconnect = action.FindIndex(i => i.Operand as string == "DisconnectPendingRunDialog");
            int wrapper = action.FindIndex(i => i.Operand is MethodReference m && m.Name == "add_OnChooseTrue");
            Check(prepare >= 0 && popup > prepare && disconnect > popup && wrapper > disconnect, "Quiz confirmation may spend outside durable wrapper");
            Check(entry.Methods.Single(m => m.Name == "ConfirmNativeQuiz").Body.Instructions
                .Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "PurchaseLifecycle" && m.Name == "Confirm"), "Quiz confirmation bypasses durable lifecycle");
            return Task.CompletedTask;
        }));
    }
}

