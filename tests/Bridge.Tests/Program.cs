using System.Text;
using System.Text.Json.Nodes;
using Mono.Cecil;
using PvzAiBridge;

var tests = new List<(string, Func<Task>)>();
PlanTests.Add(tests);
LiveTests.Add(tests);
ObservationTests.Add(tests, args);
ShovelTests.Add(tests, args);
ExtendedTests.Add(tests);
FallingCardTests.Add(tests);
GameplayTests.Add(tests, args);
ContinuousPolicyTests.Add(tests, args);
tests.Add(("tutorial: allowed_ops serialization works with reflection metadata disabled", () => {
    if (System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault)
        throw new Exception("AOT serializer feature switch must be disabled for this regression");
    // Demonstrate this host exercises the same failing generic overload.
    bool genericFailed = false;
    try { new JsonArray().Add("shovel"); }
    catch (Exception ex) when (ex.Message.Contains("JsonTypeInfo metadata") && ex.Message.Contains("System.String"))
    { genericFailed = true; }
    if (!genericFailed) throw new Exception("Regression host did not reproduce the NativeAOT failure");
    var allowed = TutorialPolicy.AllowedOps(op => op == "shovel");
    if (allowed.ToJsonString() != "[\"shovel\"]") throw new Exception("Shovel tutorial JSON lost its action");
    if (TutorialPolicy.AllowedOps(_ => false).ToJsonString() != "[]") throw new Exception("Waiting tutorial must have an empty action list");
    if (TutorialPolicy.AllowedOps(_ => true).ToJsonString() != "[\"plant\",\"shovel\",\"collect\"]")
        throw new Exception("Known tutorial operations changed");
    return Task.CompletedTask;
}));
tests.Add(("tutorial: only unmet count directions authorize native actions", () => {
    foreach (string comparison in new[] { ">", ">=", "==", "<", "<=", "unknown" })
    foreach (int target in new[] { 0, 1, 3 })
    foreach (int current in Enumerable.Range(0, 6))
    {
        bool Satisfied(int value) => comparison switch {
            ">" => value > target, ">=" => value >= target, "==" => value == target,
            "<" => value < target, "<=" => value <= target, _ => false };
        var action = TutorialPolicy.CountAction(comparison, current, target);
        if (Satisfied(current) || comparison == "unknown") {
            if (action is not null) throw new Exception("Satisfied/unknown condition allowed a mutation");
        } else {
            if (action is not ("plant" or "shovel")) throw new Exception("Unmet condition lost its supported action");
            int next = current + (action == "plant" ? 1 : -1);
            if (!Satisfied(next) && Math.Abs(next - target) >= Math.Abs(current - target))
                throw new Exception("Action moves away from tutorial goal");
        }
    }
    if (TutorialPolicy.CountAction("==", 3, 0) != "shovel") throw new Exception("Dig-all tutorial blocked");
    return Task.CompletedTask;
}));
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
tests.Add(("realtime: never auto-pauses and allows running actions; turn mode is explicit", () => {
    var mode = new ControlMode();
    Check(mode.Realtime && !mode.ShouldAutoPause(true, true, false, false, false), "Realtime paused automatically");
    mode.RequireActionState(false, false, false);
    try { mode.RequireActionState(true, false, true); throw new Exception("Modal pause bypassed"); } catch (InvalidOperationException) { }
    mode.Select("turn_based");
    Check(mode.ShouldAutoPause(true, true, false, false, false), "Explicit turn mode did not pause");
    Check(!mode.ShouldAutoPause(true, true, true, false, false), "Turn step was immediately re-paused");
    mode.RequireActionState(true, true, false);
    try { mode.RequireActionState(false, false, false); throw new Exception("Turn mode accepted unowned game"); } catch (InvalidOperationException) { }
    mode.Select(null); Check(mode.Realtime, "Default attach did not select realtime");
    try { mode.Select("bad"); throw new Exception("Invalid mode accepted"); } catch (ArgumentException) { }
    return Task.CompletedTask;
}));
tests.Add(("step: exact 1/2/60/600 frame boundaries", () => {
    foreach (int frames in new[] { 1, 2, 60, 600 }) {
        var clock = new StepClock(); clock.Start(frames);
        for (int i = 0; i < frames; i++) Check(!clock.BeforePhysicsFrame(), "Stopped before requested frame ran");
        Check(clock.BeforePhysicsFrame(), "Did not stop after requested frames");
        Check(clock.Completed == frames && !clock.Active, "Bad final frame count");
        Check(!clock.BeforePhysicsFrame(), "Stopped twice");
    }
    return Task.CompletedTask;
}));
tests.Add(("step: invalid counts, overlapping steps, cancel", () => {
    foreach (int frames in new[] { -1, 0, 601 }) {
        try { new StepClock().Start(frames); throw new Exception("Accepted invalid count"); }
        catch (ArgumentOutOfRangeException) { }
    }
    var clock = new StepClock(); clock.Start(5);
    try { clock.Start(2); throw new Exception("Accepted overlapping step"); } catch (InvalidOperationException) { }
    clock.BeforePhysicsFrame(); clock.Cancel();
    Check(!clock.BeforePhysicsFrame() && !clock.Active, "Cancelled clock advanced");
    clock.Start(1); Check(!clock.BeforePhysicsFrame() && clock.BeforePhysicsFrame(), "Cannot restart after cancel");
    return Task.CompletedTask;
}));
tests.Add(("wire: fragmented UTF-8, authentication, size and pipeline bounds", () => {
    var buffer = new JsonLineBuffer();
    var bytes = Encoding.UTF8.GetBytes("{\"id\":\"测试\",\"op\":\"observe\",\"token\":\"secret\"}\n");
    byte[]? line = null;
    foreach (var value in bytes) line = buffer.Feed([value]);
    var parsed = WireProtocol.Parse(line!, "secret");
    Check(parsed["id"]!.GetValue<string>() == "测试" && parsed["token"] is null, "UTF-8 framing or secret removal failed");
    try { WireProtocol.Parse(line!, "wrong"); throw new Exception("Invalid token accepted"); } catch (InvalidDataException) { }
    try { new JsonLineBuffer().Feed(new byte[65537]); throw new Exception("Oversize accepted"); } catch (InvalidDataException) { }
    try { new JsonLineBuffer().Feed(Encoding.UTF8.GetBytes("{}\n{}\n")); throw new Exception("Pipeline accepted"); } catch (InvalidDataException) { }
    buffer.Feed(Encoding.UTF8.GetBytes("partial")); buffer.Clear();
    Check(Encoding.UTF8.GetString(buffer.Feed(Encoding.UTF8.GetBytes("{}\n"))!) == "{}\n", "Reconnect retained old bytes");
    return Task.CompletedTask;
}));
if (args.Length >= 2 && !args[0].StartsWith("--"))
{
    tests.Add(("compatibility: native receipt proof is copied before both history and event storage", () => {
        using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
        var entry = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry");
        var instructions = entry.Methods.Single(m => m.Name == "RecordReceipt").Body.Instructions;
        int copy = instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.DeclaringType.Name == "ReceiptProof" && m.Name == "Copy");
        int history = instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "Enqueue");
        int liveEvent = instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.DeclaringType.Name == "LiveJournal" && m.Name == "Add");
        Check(copy >= 0 && history > copy && liveEvent > copy, "Receipt projection is not wired before history/event storage");
        return Task.CompletedTask;
    }));
    tests.Add(("compatibility: shopping binds native confirmation and never calls coin/unlock mutations directly", () => {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
        foreach (var endpoint in new[] { ("Shop", "ItemPressed"), ("DialogBoxShopSale", "TrueButtonPressed"),
                                        ("DialogBoxShopSale", "FalseButtonPressed"), ("Shop", "MainMenuButtonPressed") }) {
            var type = game.MainModule.Types.Single(t => t.Name == endpoint.Item1);
            Check(type.Methods.Any(m => m.Name == endpoint.Item2 && m.HasBody), "Native shopping method was trimmed");
        }
        using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
        var action = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry").Methods.Single(m => m.Name == "ShopAction");
        var calls = action.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(calls.Any(m => m.DeclaringType.Name == "DialogBoxShopSale" && m.Name == "TrueButtonPressed"), "Native purchase confirmation not used");
        Check(!action.Body.Instructions.Any(i => i.OpCode.Code is Mono.Cecil.Cil.Code.Call or Mono.Cecil.Cil.Code.Callvirt && i.Operand is MethodReference m
            && m.Name is "UseCoin" or "Sale" or "AddValue" or "SetTowerDefensePacketValue"), "Shopping bypasses native confirmation");
        Check(calls.Any(m => m.Name == "remove_OnSale") && calls.Any(m => m.Name == "add_OnSale") && calls.Any(m => m.Name == "add_OnClose"), "Native player confirmation/cancel callbacks are not bound");
        return Task.CompletedTask;
    }));
    tests.Add(("compatibility: observation grid readiness remains available in NativeAOT Godot binding", () => {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
        var map = game.MainModule.Types.Single(t => t.Name == "TowerDefenseBattleFeatureMap");
        Check(map.NestedTypes.Single(t => t.Name == "PropertyName").Fields.Any(f => f.Name == "_gridReady"), "Readiness Godot property was trimmed");
        Check(map.Methods.Single(m => m.Name == "GetGodotPropertyList").HasBody, "Readiness binding metadata unavailable");
        return Task.CompletedTask;
    }));
    tests.Add(("compatibility: free conveyor native call endpoints retained without rule flag writes", () => {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
        foreach (var endpoint in new[] { ("PacketPickControl", "ProcessPacketPickCore", 4),
                                        ("TowerDefenseInGamePacketShow", "CanAffordItemCost", 0) }) {
            var type = game.MainModule.Types.Single(t => t.Name == endpoint.Item1);
            Check(type.Methods.Any(m => m.Name == endpoint.Item2 && m.Parameters.Count == endpoint.Item3), "Native endpoint signature changed");
            Check(type.NestedTypes.Single(t => t.Name == "MethodName").Fields.Any(f => f.Name == endpoint.Item2), "Native endpoint Godot binding was trimmed");
        }
        using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
        var entry = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry");
        var use = entry.Methods.Single(m => m.Name == "UseOneUseCard");
        Check(!use.Body.Instructions.Any(i => (i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld && i.Operand is FieldReference f && f.Name is "useCost" or "start")
            || (i.Operand is MethodReference m && m.Name is "set_useCost" or "set_start")), "Adapter altered native rule flags");
        return Task.CompletedTask;
    }));
    tests.Add(("compatibility: falling-card native quote, generation and exact consumption/release bindings retained", () => {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
        var packet = game.MainModule.Types.Single(t => t.Name == "TowerDefenseInGamePacketShow");
        var properties = packet.NestedTypes.Single(t => t.Name == "PropertyName");
        foreach (string property in new[] { "_asyncLifetimeVersion", "ModUseSequence", "_itemCostLabelInitialized", "_itemCostLabelQuoteValid", "_itemCostLabelValue" })
            Check(properties.Fields.Any(f => f.Name == property), "Native card property trimmed: " + property);
        Check(packet.NestedTypes.Single(t => t.Name == "MethodName").Fields.Any(f => f.Name == "UpdateItemCostLabel"), "Native quote method trimmed");
        var manager = game.MainModule.Types.Single(t => t.Name == "TowerDefenseManager");
        var spawn = manager.Methods.Single(m => m.Name == "SpawnPacketCore");
        Check(spawn.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "GetCharacterNode"), "Native drop parent changed");
        Check(spawn.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "plantOnce"), "Native one-use marker changed");
        using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
        var entry = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry");
        var use = entry.Methods.Single(m => m.Name == "UseOneUseCard");
        Check(use.Body.ExceptionHandlers.Any(h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Finally), "No unconditional native selection release");
        Check(use.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "PacketPickRelease"), "Native selection release not called");
        Check(use.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && (string?)i.Operand == "ProcessPacketPickCore"), "Native placement confirmation bypassed");
        Check(use.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Pressed"), "Native card press bypassed");
        return Task.CompletedTask;
    }));
    tests.Add(("compatibility: all imported methods resolve against installed trimmed game runtime", () => {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetFullPath(args[1]));
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
        var failures = new List<string>();
        foreach (var reference in assembly.MainModule.GetTypeReferences()) {
            try { if (reference.Resolve() is null) failures.Add(reference.FullName); }
            catch (Exception ex) { failures.Add(reference.FullName + " : " + ex.Message); }
        }
        foreach (var reference in assembly.MainModule.GetMemberReferences()) {
            try {
                bool found = reference switch { MethodReference m => m.Resolve() is not null, FieldReference f => f.Resolve() is not null, _ => true };
                if (!found) failures.Add(reference.FullName);
            } catch (Exception ex) { failures.Add(reference.FullName + " : " + ex.Message); }
        }
        Check(failures.Count == 0, "Unresolved members:\n" + string.Join("\n", failures));
        Console.WriteLine("  Target game SHA256: " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(args[1], "PlantsVsZombies.dll")))));
        return Task.CompletedTask;
    }));
}
int failed = 0;
string? filter = args.FirstOrDefault(a => a.StartsWith("--filter="))?.Substring(9);
if (filter is not null) tests = tests.Where(t => t.Item1.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
foreach (var (name, run) in tests) {
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed; these are offline/transport checks, not a live gameplay test.");
return failed == 0 ? 0 : 1;
