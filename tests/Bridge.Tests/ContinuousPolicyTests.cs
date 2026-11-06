using System.Text.Json.Nodes;
using Mono.Cecil;
using PvzAiBridge;

static class ContinuousPolicyTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static JsonObject Entity(long id, string kind, string camp, int row, int col, double x) => new() {
        ["id"] = id, ["kind"] = kind, ["content_id"] = kind == "plant" ? "PlantSunPot" : "ZombieCone",
        ["camp"] = camp, ["row"] = row, ["column"] = col, ["x"] = x, ["hp"] = 100.0,
        ["dead"] = false, ["removing"] = false,
        ["armor"] = new JsonArray(new JsonObject { ["hp"] = 75.0 }, new JsonObject { ["hp"] = -1.0 }) };
    static JsonObject Board()
    {
        var cards = new JsonArray();
        string[] deck = ["PlantSunPot", "PlantSunFlowerZ", "PlantQueenSunFlower", "PlantPotatoMine", "PlantCatGatlingPea", "PlantPuffPot"];
        for (int i = 0; i < deck.Length; i++) cards.Add(new JsonObject { ["id"] = i + 1, ["content_id"] = deck[i],
            ["source"] = "native", ["cost"] = i == 0 ? 50L : 500L, ["cost_status"] = "known",
            ["available"] = i == 0, ["pending_use"] = false, ["requires_target"] = false });
        var cells = new JsonArray();
        for (int r = 1; r <= 5; r++) for (int c = 1; c <= 9; c++) cells.Add(new JsonObject {
            ["row"] = r, ["column"] = c, ["terrain"] = r == 3 ? new JsonArray("GROUND", "AIR") : new JsonArray("SOIL", "AIR"),
            ["layers_status"] = "ready", ["pending_gravestone"] = false, ["layers"] = new JsonArray() });
        return new JsonObject { ["battle_id"] = "native-export-fixture", ["observation_id"] = 42L,
            ["rows"] = 5, ["columns"] = 9, ["simulation_seconds"] = 20.0, ["sun"] = 200L,
            ["wave"] = new JsonObject { ["current"] = 1, ["final"] = 20 }, ["cards"] = cards,
            ["entities"] = new JsonArray(Entity(101, "plant", "PLANT", 1, 1, 500),
                Entity(102, "plant", "ZOMBIE", 2, 1, 500), Entity(103, "zombie", "ZOMBIE", 3, 8, 650),
                Entity(104, "zombie", "PLANT", 4, 8, 950)),
            ["map_preview"] = new JsonObject { ["status"] = "ready" },
            ["obstacles"] = new JsonObject { ["status"] = "ready", ["items"] = new JsonArray(new JsonObject {
                ["kind"] = "gravestone", ["status"] = "known", ["preview_only"] = false,
                ["occupied_cells"] = new JsonArray(new JsonObject { ["row"] = 5, ["column"] = 8 }) }) }, ["cells"] = cells };
    }
    static JsonArray Legal() => new(new JsonObject { ["card_id"] = 1L, ["content_id"] = "PlantSunPot", ["row"] = 3, ["column"] = 1 });
    const string Resource = "res://Asset/Config/Level/TowerDefense/Chapter8/Level8_11.tres";
    static JsonObject Project(JsonObject b) => ContinuousPolicyFacts.Project(b, Resource, 500, 100, Legal(), true);
    public static void Add(List<(string, Func<Task>)> tests, string[] args)
    {
        tests.Add(("continuous-policy: copied runtime config resolves only matching native catalog key, resource and data", () => {
            string Resolve(string key="Level8_13",string catalog="Level8_13",string source="Level8_13",string path=Resource,string actual="{\"level\":13}",string original="{\"level\":13}") =>
                ContinuousPolicyFacts.CopiedLevelResource(key,catalog,source,Resource,path,actual,original);
            Check(Resolve()==Resource,"Native config copy lost source despite exact key/data proof");
            Check(Resolve(actual:"{\"a\":1,\"b\":2}",original:"{\"b\":2,\"a\":1}")==Resource,"JSON property ordering changed identity");
            Check(Resolve(key:"other")=="" && Resolve(catalog:"other")=="" && Resolve(source:"other")==""
                && Resolve(path:"other")=="" && Resolve(actual:"{}")=="" && Resolve(actual:"")=="", "Unrelated or modified level accepted");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: atomic projection preserves native camp, scaled continuous coordinates and dynamic quote", () => {
            var b = Board(); var before = b.DeepClone(); var p = Project(b);
            Check(p["complete"]!.GetValue<bool>(), p.ToJsonString());
            Check(p["battle_id"]!.ToString() == b["battle_id"]!.ToString() && p["observation_id"]!.ToString() == "42", "Snapshot identity changed");
            Check(p["plants"]![0]!["friendly"]!.GetValue<bool>() && !p["plants"]![1]!["friendly"]!.GetValue<bool>(), "Enemy plant guessed from kind");
            Check(!p["zombies"]![0]!["friendly"]!.GetValue<bool>() && p["zombies"]![1]!["friendly"]!.GetValue<bool>(), "Hypnotized camp lost");
            Check(p["zombies"]![0]!["x"]!.GetValue<double>() == 2.5 && p["zombies"]![1]!["x"]!.GetValue<double>() == 5.5, "World x or cached column leaked");
            Check(p["zombies"]![0]!["armor"]!.GetValue<double>() == 75, "Body/armor confused");
            Check(JsonNode.DeepEquals(before, b), "Projection mutated captured state");
            string? output = args.FirstOrDefault(a => a.StartsWith("--export="))?.Substring(9);
            if (output is not null) {
                b["continuous_policy"] = p; b["bridge_version"] = "0.5.3"; b["game_version"] = "0.31.0";
                b["level_mode"] = "LevelChoose"; b["level_group"] = "Adventure"; b["chapter_index"] = 7; b["level_index"] = 10;
                b["running"] = true; b["ai_attached"] = true;
                File.WriteAllText(output, b.ToJsonString(new() { WriteIndented = true }));
            }
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: native terrain and stacked pot/grave layers stay distinct", () => {
            var b = Board(); b["cells"]![0]!["layers"]!.AsArray().Add(new JsonObject {
                ["status"] = "known", ["preview_only"] = false, ["kind"] = "plant", ["entity_id"] = 101L,
                ["provides_terrain"] = "POT", ["grid_phantom"] = false });
            var p = Project(b); Check(p["complete"]!.GetValue<bool>(), p.ToJsonString());
            Check(p["pot_required_rows"]!.ToJsonString() == "[1,2,4,5]" && p["pots"]!.ToJsonString() == "[[1,1]]"
                && p["graves"]!.ToJsonString() == "[[5,8]]" && p["plants"]!.AsArray().Count == 2, "Terrain, support and upper entities conflated");
            foreach(var c in b["cells"]!.AsArray().Where(c=>c!["row"]!.GetValue<int>()==3)) c!["terrain"]=new JsonArray("BRICK","AIR");
            Check(Project(b)["pot_required_rows"]!.ToJsonString()=="[1,2,3,4,5]","Native brick row did not require pots");
            b["cells"]![1]!["terrain"] = new JsonArray("GROUND", "AIR");
            Check(!Project(b)["complete"]!.GetValue<bool>(), "Mixed row silently generalized");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: water and live lilypad layers preserve support geometry without inventing legality", () => {
            var b = Board();
            foreach (var c in b["cells"]!.AsArray().Where(c => c!["row"]!.GetValue<int>() == 1)) c!["terrain"] = new JsonArray("WATER", "AIR");
            b["cells"]![0]!["layers"]!.AsArray().Add(new JsonObject {
                ["status"] = "known", ["preview_only"] = false, ["kind"] = "plant", ["entity_id"] = 101L,
                ["provides_terrain"] = "LILYPAD", ["grid_phantom"] = false });
            var p = Project(b); Check(p["complete"]!.GetValue<bool>(), p.ToJsonString());
            Check(p["pots"]!.ToJsonString() == "[[1,1]]" && p["plants"]![0]!["is_base"]!.GetValue<bool>()
                && !p["plants"]![1]!["is_base"]!.GetValue<bool>(), "Lilypad and upper occupancy conflated");
            Check(p["pot_required_rows"]!.ToJsonString() == "[1,2,4,5]"
                && JsonNode.DeepEquals(p["legal_placements"], Legal()), "Water manufactured legal placements");
            b["cells"]![0]!["layers"]![0]!["grid_phantom"] = true;
            Check(Project(b)["pots"]!.AsArray().Count == 0, "Phantom lilypad counted as foundation");
            b["cells"]![0]!["terrain"] = new JsonArray("ICECAP");
            Check(!Project(b)["complete"]!.GetValue<bool>(), "Unsupported terrain silently accepted");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: mixed bank mode is structural and target cards stay visible but unplaceable", () => {
            Check(ContinuousPolicyFacts.SeedbankMethod("PRESET", true, true) == "Mixed"
                && ContinuousPolicyFacts.SeedbankMethod("CHOOSE", true, true) == "Mixed"
                && ContinuousPolicyFacts.SeedbankMethod("CONVEYOR", true, false) == "Conveyor"
                && ContinuousPolicyFacts.SeedbankMethod("PRESET", false, true) == "Preset", "Mixed bank confused with a pure belt");
            var b = Board(); b["cards"]![1]!["requires_target"] = true;
            var before = b.DeepClone(); var p = Project(b);
            Check(p["complete"]!.GetValue<bool>() && JsonNode.DeepEquals(before, b), "An excluded target card destroyed the complete snapshot");
            var legal = Legal(); legal.Add(new JsonObject { ["card_id"] = 2L, ["content_id"] = "PlantSunFlowerZ", ["row"] = 3, ["column"] = 1 });
            Check(!ContinuousPolicyFacts.Project(b, Resource, 500, 100, legal, true)["complete"]!.GetValue<bool>(), "Target card acquired ordinary placements");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: incomplete identities, costs, map coverage and nonfinite entities fail closed", () => {
            Action<JsonObject>[] mutations = [
                b => b["entities"]![0]!["camp"] = "NOONE", b => b["entities"]![0]!["hp"] = double.NaN,
                b => b["cards"]![0]!["cost"] = null, b => b["cards"]![0]!["pending_use"] = null,
                b => b["cards"]![0]!["cost_status"] = "unknown", b => b["cards"]![0]!["requires_target"] = true,
                b => b["entities"]![0]!["id"] = 9007199254740992L,
                b => b["entities"]![1]!["id"] = 101L,
                b => b["map_preview"]!["status"] = "partial", b => b["cells"]!.AsArray().RemoveAt(44),
                b => b["cells"]![0]!["pending_gravestone"] = true,
                b => b["cells"]![0]!["layers"]!.AsArray().Add(new JsonObject { ["status"] = "known", ["preview_only"] = false, ["kind"] = "plant", ["entity_id"] = 900L }) ];
            for (int i = 0; i < mutations.Length; i++) { var b = Board(); mutations[i](b); var p = Project(b);
                Check(!p["complete"]!.GetValue<bool>() && p["plants"] is null && p["legal_placements"] is null, "Partial data labeled complete: " + i); }
            Check(!ContinuousPolicyFacts.Project(Board(), Resource, 500, 0, Legal(), true)["complete"]!.GetValue<bool>(), "Zero grid width accepted");
            Check(!ContinuousPolicyFacts.Project(Board(), Resource, 500, 100, Legal(), false)["complete"]!.GetValue<bool>(), "Skipped native entity hidden");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: slot identity and capacity limits never truncate complete observations", () => {
            var b = Board(); var card = b["cards"]![0]!.DeepClone(); card["id"] = 7L; b["cards"]!.AsArray().Add(card);
            Check(Project(b)["complete"]!.GetValue<bool>(), "Same species slots must retain distinct IDs for client rejection");
            var legal = Legal(); legal[0]!["card_id"] = 999L;
            Check(!ContinuousPolicyFacts.Project(b, Resource, 500, 100, legal, true)["complete"]!.GetValue<bool>(), "Wrong slot linked");
            while (b["cards"]!.AsArray().Count < 33) { var c = card.DeepClone(); c["id"] = b["cards"]!.AsArray().Count + 1; b["cards"]!.AsArray().Add(c); }
            Check(!Project(b)["complete"]!.GetValue<bool>(), "Card overflow truncated");
            b = Board(); for (int i = 0; i < 2049; i++) b["entities"]!.AsArray().Add(Entity(1000 + i, "zombie", "ZOMBIE", 1, 9, 1000));
            Check(!Project(b)["complete"]!.GetValue<bool>(), "Entity overflow truncated");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: supplied packet generations and empty conveyor remain atomic native facts", () => {
            var b = Board(); b["cards"] = new JsonArray();
            Check(ContinuousPolicyFacts.Project(b, Resource, 500, 100, new JsonArray(), true)["complete"]!.GetValue<bool>(), "Empty conveyor rejected");
            var cards = b["cards"]!.AsArray(); var legal = new JsonArray();
            foreach (long id in new long[] { -1, -2 }) {
                cards.Add(new JsonObject { ["id"] = id, ["content_id"] = "PlantPeashooterZ", ["source"] = "free_conveyor", ["cost"] = 0,
                    ["cost_status"] = "known", ["available"] = true, ["pending_use"] = false, ["single_use"] = true, ["use_cost"] = false,
                    ["native_object_id"] = (100-id).ToString(), ["native_generation"] = 1, ["native_config_id"] = "42" });
                legal.Add(new JsonObject { ["card_id"] = id, ["content_id"] = "PlantPeashooterZ", ["row"] = 3, ["column"] = 2 });
            }
            Check(ContinuousPolicyFacts.Project(b, Resource, 500, 100, legal, true)["complete"]!.GetValue<bool>(), "Duplicate supplied species merged or rejected");
            cards[0]!.AsObject().Remove("native_generation");
            Check(!ContinuousPolicyFacts.Project(b, Resource, 500, 100, legal, true)["complete"]!.GetValue<bool>(), "Missing generation accepted");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: bare AIR holes preserve conservative row support without inventing legal placements", () => {
            var b = Board();
            b["cells"]![0]!["terrain"] = new JsonArray("AIR");
            var p = Project(b);
            Check(p["complete"]!.GetValue<bool>(), p.ToJsonString());
            Check(p["pot_required_rows"]!.AsArray().Any(n=>n!.GetValue<long>()==1), "SOIL row lost support requirement");
            Check(p["legal_placements"]!.AsArray().Count==1 && p["legal_placements"]![0]!["row"]!.GetValue<int>()==3, "Hole acquired fabricated placement");
            return Task.CompletedTask;
        }));
        if (args.Length < 2 || args[0].StartsWith("--")) return;
        tests.Add(("continuous-policy: shipped native handle resolver and camp enum match the export contract", () => {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
            var resolver = game.MainModule.Types.Single(t => t.FullName == "PVZHE.ModEditor.ModSystem.XWModCards").Methods.Single(m => m.Name == "TryResolve");
            Check(resolver.IsStatic && resolver.ReturnType.FullName == "System.Boolean" && resolver.Parameters.Count == 2
                && resolver.Parameters[0].ParameterType.Name == "XWModCardHandle" && resolver.Parameters[1].ParameterType.FullName == "TowerDefenseInGamePacketShow&", "Raw handle resolver ABI changed");
            var camp = game.MainModule.Types.Single(t => t.Name == "TowerDefenseEnum").NestedTypes.Single(t => t.Name == "CHARACTER_CAMP");
            Check((int)camp.Fields.Single(f => f.Name == "PLANT").Constant == 0 && (int)camp.Fields.Single(f => f.Name == "ZOMBIE").Constant == 1, "Camp encoding changed");
            return Task.CompletedTask;
        }));
        tests.Add(("continuous-policy: native legality performs no plant, charge, cooldown or blocker notification", () => {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "PlantsVsZombies.dll"));
            var cell = game.MainModule.Types.Single(t => t.Name == "TowerDefenseCellInstance");
            var calls = new[] { "CanPacketPlant", "ClearEmpty", "FindPlantingBlocker", "RemoveInvalidCharacterSlotKeys" }
                .SelectMany(name => cell.Methods.Single(m => m.Name == name).Body.Instructions)
                .Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
            Check(!calls.Any(n => n is "Plant" or "CharacterPlant" or "NotifyBlockedPlanting" or "OnPlantingBlocked" or "TryBeginItemCost" or "CompleteUse" or "QueueFree" or "SetSun"), "Legality acquired gameplay side effects");
            using var bridge = AssemblyDefinition.ReadAssembly(args[0]);
            var entry = bridge.MainModule.Types.Single(t => t.FullName == "PvzAiBridge.Entry");
            var native = entry.Methods.Single(m => m.Name == "AddContinuousPolicyObservation").Body.Instructions.ToList();
            int call = native.FindIndex(i => i.Operand is MethodReference m && m.Name == "CanPacketPlant");
            Check(call >= 3 && native[call-3].OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0
                && native[call-2].OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0 && native[call-1].OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_1,
                "Legality uses preview flags or bypasses native check");
            Check(entry.Methods.Single(m => m.Name == "Observe").Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "AddContinuousPolicyObservation"), "Exporter is disconnected from Observe");
            return Task.CompletedTask;
        }));
    }
}
