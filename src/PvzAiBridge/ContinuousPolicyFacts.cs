using System.Text.Json.Nodes;

namespace PvzAiBridge;

/// <summary>Projection of one native capture; no world, input, clock or I/O.</summary>
internal static class ContinuousPolicyFacts
{
    private const long SafeInteger = 9007199254740991;
    internal static string SeedbankMethod(string method, bool hasConveyor, bool hasPersistentPackets) =>
        hasConveyor && hasPersistentPackets ? "Mixed" : method switch {
            "CHOOSE" => "Choose", "PRESET" => "Preset", "CONVEYOR" => "Conveyor", _ => "Unsupported" };
    internal static string CopiedLevelResource(string actualKey, string catalogKey, string sourceKey,
        string catalogPath, string sourcePath, string actualJson, string sourceJson) =>
        !string.IsNullOrEmpty(actualKey) && actualKey == catalogKey && actualKey == sourceKey
        && !string.IsNullOrEmpty(catalogPath) && catalogPath == sourcePath && !string.IsNullOrEmpty(actualJson)
        && JsonNode.DeepEquals(JsonNode.Parse(actualJson), JsonNode.Parse(sourceJson)) ? sourcePath : "";
    internal static JsonObject Incomplete(string reason) => new() {
        ["schema"] = "pvz-policy-observation/v1", ["complete"] = false, ["reason"] = reason };
    private static string Text(JsonNode? n) => n?.GetValue<string>() ?? "";
    private static double Number(JsonNode? node)
    {
        if (node is JsonValue v) {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<float>(out var f)) return f;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
        }
        throw new InvalidDataException("missing-number");
    }
    private static double Finite(JsonNode? node) {
        double n = Number(node); if (!double.IsFinite(n)) throw new InvalidDataException("non-finite-number"); return n;
    }
    private static long Integer(JsonNode? node, long min, long max) {
        double n = Finite(node);
        if (n < min || n > max || Math.Truncate(n) != n) throw new InvalidDataException("integer-out-of-range");
        return (long)n;
    }
    private static bool Bool(JsonNode? node) => node?.GetValue<bool>() ?? throw new InvalidDataException("missing-boolean");
    private static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw new InvalidDataException("missing-array");
    private static string Species(JsonNode? node) {
        string id = Text(node); if (id.Length is < 1 or > 128) throw new InvalidDataException("missing-species"); return id;
    }
    private static (long Row, long Col) Cell(JsonNode? n) => (Integer(n?["row"], 1, 5), Integer(n?["column"], 1, 9));
    private static JsonArray Cells(IEnumerable<(long Row, long Col)> cells) => new(cells.Distinct()
        .OrderBy(p => p.Row).ThenBy(p => p.Col).Select(p => (JsonNode?)new JsonArray(JsonValue.Create(p.Row), JsonValue.Create(p.Col))).ToArray());

    internal static JsonObject Project(JsonObject state, string resource, double originX, double width,
        JsonArray legal, bool entitiesComplete)
    {
        try
        {
            if (!entitiesComplete || !double.IsFinite(originX) || !double.IsFinite(width) || width <= 0)
                throw new InvalidDataException("incomplete-native-entities-or-transform");
            if (Integer(state["rows"], 5, 5) != 5 || Integer(state["columns"], 9, 9) != 9 || string.IsNullOrEmpty(resource))
                throw new InvalidDataException("unsupported-map");
            if (Finite(state["simulation_seconds"]) < 0) throw new InvalidDataException("negative-clock");
            // Native enemies can drive sunlight below zero. Preserve that real
            // balance; free conveyor packets still use their own native legality.
            Finite(state["sun"]);
            Integer(state["wave"]?["current"], 0, int.MaxValue);
            string battle = Text(state["battle_id"]); if (battle.Length == 0) throw new InvalidDataException("missing-battle");
            long observation = Integer(state["observation_id"], 0, SafeInteger);
            var cards = Array(state["cards"]);
            if (cards.Count > 32 || legal.Count > 1440) throw new InvalidDataException("card-or-placement-limit");
            var slots = new Dictionary<long, string>();
            foreach (var c in cards) {
                long id = Integer(c?["id"], -SafeInteger, SafeInteger);
                string source = Text(c?["source"]);
                if (source is not ("native" or "free_conveyor" or "falling_card" or "column_card") || Text(c?["cost_status"]) != "known" || Finite(c?["cost"]) < 0
                    || !slots.TryAdd(id, Species(c?["content_id"]))
                    || c?["requires_target"]?.GetValue<bool>() == true && legal.Any(p => p?["card_id"]?.GetValue<long>() == id))
                    throw new InvalidDataException("unknown-or-target-card");
                if (source == "free_conveyor" && (id >= 0 || Text(c?["native_object_id"]).Length == 0 || Text(c?["native_config_id"]).Length == 0
                    || Integer(c?["native_generation"], 0, int.MaxValue) < 0 || !Bool(c?["single_use"]) || Bool(c?["use_cost"]) || Finite(c?["cost"]) != 0))
                    throw new InvalidDataException("incomplete-supplied-card-binding");
                Bool(c?["available"]); Bool(c?["pending_use"]);
                if(source is "falling_card" or "column_card") {
                    if(id>=0 || Text(c?["native_object_id"]).Length==0 || Text(c?["native_config_id"]).Length==0)throw new InvalidDataException("incomplete-supplied-card-binding");
                    Integer(c?["native_generation"],0,int.MaxValue); Bool(c?["use_cost"]); Bool(c?["single_use"]);
                }
            }
            var placements = new HashSet<(long, long, long)>();
            foreach (var p in legal) {
                long id = Integer(p?["card_id"], -SafeInteger, SafeInteger); var (row, col) = Cell(p);
                if (!slots.TryGetValue(id, out var content) || content != Text(p?["content_id"]) || !placements.Add((id, row, col)))
                    throw new InvalidDataException("placement-card-mismatch");
            }
            var plants = new JsonArray(); var zombies = new JsonArray(); var entityIds = new HashSet<long>();
            foreach (var e in Array(state["entities"])) {
                string kind = Text(e?["kind"]);
                if (kind is not ("plant" or "zombie") || Bool(e?["dead"]) || Bool(e?["removing"])) continue;
                string camp = Text(e?["camp"]);
                if (camp is not ("PLANT" or "ZOMBIE")) throw new InvalidDataException("unknown-camp");
                long id = Integer(e?["id"], -SafeInteger, SafeInteger), row = Integer(e?["row"], 1, 5);
                if (!entityIds.Add(id)) throw new InvalidDataException("duplicate-entity");
                double hp = Finite(e?["hp"]); if (hp < 0) throw new InvalidDataException("negative-hp");
                var fact = new JsonObject { ["id"] = Species(e?["content_id"]), ["entity_id"] = id,
                    ["row"] = row, ["hp"] = hp, ["friendly"] = camp == "PLANT", ["camp"] = camp == "PLANT" ? 0 : 1 };
                if (kind == "plant") { fact["col"] = Integer(e?["column"], 1, 9); fact["is_base"] = false; plants.Add(fact); }
                else {
                    double x = 1 + (Finite(e?["x"]) - originX) / width;
                    if (!double.IsFinite(x)) throw new InvalidDataException("invalid-transform");
                    fact["x"] = x;
                    fact["col"] = Integer(e?["column"], -SafeInteger, SafeInteger);
                    // Armor hp can retain destroyed negative values. The native
                    // body hp stays separate; only live armor contributes.
                    double armor = Array(e?["armor"]).Sum(a => Math.Max(0, Finite(a?["hp"])));
                    if (!double.IsFinite(armor)) throw new InvalidDataException("invalid-armor");
                    fact["armor"] = armor; zombies.Add(fact);
                }
            }
            if (plants.Count > 2048 || zombies.Count > 2048) throw new InvalidDataException("entity-limit");
            if (Text(state["map_preview"]?["status"]) != "ready" || Text(state["obstacles"]?["status"]) != "ready")
                throw new InvalidDataException("incomplete-map");
            var cells = Array(state["cells"]); if (cells.Count != 45) throw new InvalidDataException("incomplete-cells");
            var seenCells = new HashSet<(long Row, long Col)>(); var pots = new List<(long, long)>();
            var requirements = new Dictionary<long, HashSet<bool>>();
            foreach (var c in cells) {
                var pos = Cell(c);
                if (!seenCells.Add(pos) || Text(c?["layers_status"]) != "ready" || Bool(c?["pending_gravestone"]))
                    throw new InvalidDataException("incomplete-cell-layers");
                var terrain = Array(c?["terrain"]).Select(Text).ToHashSet();
                // Native ordinary plants accept GROUND/POT/LILYPAD. SOIL
                // without GROUND, SOIL/BRICK require compatible native pots.
                // Every proposed pot and upper plant still uses native legality.
                // Some Chapter 8 cells are bare AIR holes. Their row still
                // needs support on its SOIL cells; the native legal matrix
                // remains authoritative and does not allow pots over a hole.
                bool needsPot = !terrain.Contains("GROUND") && (terrain.Contains("SOIL") || terrain.Contains("BRICK") || terrain.Contains("WATER") || terrain.SetEquals(new[] { "AIR" }));
                if (!terrain.Contains("GROUND") && !needsPot) throw new InvalidDataException("unsupported-terrain");
                if (!requirements.TryGetValue(pos.Row, out var row)) requirements[pos.Row] = row = new();
                row.Add(needsPot);
                foreach (var l in Array(c?["layers"])) {
                    if (Text(l?["status"]) != "known" || Bool(l?["preview_only"])) throw new InvalidDataException("incomplete-layer");
                    if (Text(l?["kind"]) == "plant" && !entityIds.Contains(Integer(l?["entity_id"], -SafeInteger, SafeInteger)))
                        throw new InvalidDataException("unregistered-plant-layer");
                    if (Text(l?["provides_terrain"]) is "POT" or "LILYPAD" && !Bool(l?["grid_phantom"])) {
                        pots.Add(pos);
                        long layerId = Integer(l?["entity_id"], -SafeInteger, SafeInteger);
                        foreach (var plant in plants.Where(p => p?["entity_id"]?.GetValue<long>() == layerId)) plant!["is_base"] = true;
                    }
                }
            }
            if (requirements.Any(p => p.Value.Count != 1)) throw new InvalidDataException("mixed-row-pot-requirement");
            var graves = new List<(long, long)>();
            foreach (var g in Array(state["obstacles"]?["items"])) {
                if (Text(g?["kind"]) != "gravestone") continue;
                if (Text(g?["status"]) != "known" || Bool(g?["preview_only"])) throw new InvalidDataException("unknown-grave");
                foreach (var p in Array(g?["occupied_cells"])) graves.Add(Cell(p));
            }
            var graveCells = Cells(graves); var potCells = Cells(pots);
            if (graveCells.Count > 45 || potCells.Count > 45) throw new InvalidDataException("obstacle-limit");
            return new JsonObject { ["schema"] = "pvz-policy-observation/v1", ["complete"] = true,
                ["battle_id"] = battle, ["observation_id"] = observation, ["level_resource"] = resource,
                ["difficulty"] = "normal", ["coordinate_system"] = "grid-1-based", ["plants"] = plants, ["zombies"] = zombies,
                ["graves"] = graveCells, ["pots"] = potCells,
                ["pot_required_rows"] = new JsonArray(requirements.Where(p => p.Value.Single()).OrderBy(p => p.Key).Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
                ["legal_placements"] = legal.DeepClone(), ["native_cannons"] = state["native_cannons"]?.DeepClone(),
                ["boss_balls"] = state["native_boss_balls"]?.DeepClone(), ["boss_windows"] = state["native_boss_windows"]?.DeepClone() };
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or ArgumentException)
        { return Incomplete(ex is InvalidDataException ? ex.Message : "malformed-native-facts"); }
    }
}
