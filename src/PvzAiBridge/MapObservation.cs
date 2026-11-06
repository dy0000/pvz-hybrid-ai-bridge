using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private long _cellInspection;
    private readonly List<(Vector2I Pos, TowerDefenseCellInstance Cell)> _policyCells = new();
    private static bool LiveMapObject(TowerDefenseCharacter? c) => Valid(c) && c!.IsInsideTree()
        && !c.IsQueuedForDeletion() && !c.isDestroy && !c.die;
    private static bool MapGridReady(TowerDefenseBattleFeatureMap? map) => Valid(map) && Valid(map!.config)
        && Valid(map.currentMap) && map.currentMap.IsInsideTree() && !map.currentMap.IsQueuedForDeletion()
        && !map.isChange && map.Get("_gridReady").AsBool();
    private string ObjectId(GodotObject obj, string scope) => $"{scope}:{obj.GetInstanceId()}";
    private static bool NativePositionKnown(TowerDefenseCharacter c) =>
        TowerDefenseManager.Instance.CheckMapGridPosIn(c.gridPos);

    private JsonObject ObserveEnvironment()
    {
        var map = TowerDefenseManager.GetMapFeature();
        return Valid(map) && Valid(map!.config)
            ? ObservationFacts.Environment(map.config.isNight, map.isChange, map.config.ResourcePath)
            : ObservationFacts.Environment(null, false, null);
    }

    private static TowerDefenseCharacter[] CellOccupants(TowerDefenseCellInstance cell) => cell.characterList
        .Concat(cell.slot.Values).Concat(cell.characterSlotDictionary.Keys).Concat(cell.characterSlotDictionary.Values)
        .Concat(new[] { cell.characterSurround, cell.characterLadder, cell.itemShield })
        .Where(LiveMapObject).DistinctBy(c => c.GetInstanceId()).ToArray();

    private static IEnumerable<TowerDefenseCharacter> NativeCharacterObjects(Node container)
    {
        foreach (var child in container.GetChildren())
        {
            if (child is TowerDefenseCharacter character) yield return character;
            else foreach (var nested in NativeCharacterObjects(child)) yield return nested;
        }
    }

    private JsonObject LayerFact(TowerDefenseCharacter c, TowerDefenseCellInstance cell, string scope)
    {
        bool known = Valid(c.config);
        long? handle = null;
        if (!c.editorPreviewMode && Valid(c.instance) && Valid(c.componentManager) && _context.Game.Characters.TryGetHandle(c, out var h))
        { handle = h.Id; _observedCharacters[h.Id] = c; }
        string? terrain = known ? c.config.plantGridOverrideType.ToString() : null;
        int flags = Valid(c.instance) ? c.instance.physiqueTypeFlags : known ? c.config.physiqueTypeFlags : 0;
        var link = cell.characterSlotDictionary.GetValueOrDefault(c);
        bool phantom = Valid(c.instance) && c.instance.hologram && !c.hologramOccupiesGrid;
        return new JsonObject {
            ["layer_id"] = ObjectId(c, scope), ["entity_id"] = JsonValue.Create(handle),
            ["content_id"] = Valid(c.packet) ? c.packet.saveKey : known ? c.config.name : null,
            ["native_type"] = c.GetType().Name, ["status"] = known ? "known" : "unknown",
            ["name"] = known ? NameOf(Valid(c.packet) ? c.packet.saveKey : c.config.name) : null,
            ["slots"] = ObservationFacts.Strings(cell.slot.Where(p => p.Value == c).Select(p => p.Key.ToString())),
            ["provides_terrain"] = terrain,
            ["accepts_terrain"] = known ? ObservationFacts.Strings(c.config.plantGridType.Select(t => t.ToString())) : null,
            ["is_support"] = known ? JsonValue.Create(ObservationFacts.IsSupport(terrain!, flags)) : null,
            ["native_slot_link"] = LiveMapObject(link) ? ObjectId(link!, scope) : null,
            ["surround"] = cell.characterSurround == c, ["ladder"] = cell.characterLadder == c,
            ["shield"] = cell.itemShield == c, ["grid_phantom"] = phantom,
            ["runtime_ready"] = Valid(c.instance) && Valid(c.componentManager),
            ["preview_only"] = c.editorPreviewMode,
            ["hypnosed"] = Valid(c.instance) ? JsonValue.Create(c.instance.hypnoses) : null,
            ["kind"] = c is TowerDefensePlant ? "plant" : c is TowerDefenseGravestone ? "gravestone"
                : c is TowerDefenseCrater ? "crater" : "other"
        };
    }

    private JsonObject CellFact(Vector2I pos, TowerDefenseCellInstance cell, TowerDefenseCharacter[] occupants,
        string scope, JsonArray obstacleIds, bool includeShovel)
    {
        var layers = new JsonArray(occupants.Select(c => (JsonNode?)LayerFact(c, cell, scope)).ToArray());
        bool complete = occupants.All(c => Valid(c.config)) && !cell.HasPendingRandomGravestonePlacement();
        var supports = layers.OfType<JsonObject>().Where(l => l["is_support"]?.GetValue<bool>() == true && l["grid_phantom"]?.GetValue<bool>() != true).ToArray();
        var uppers = layers.OfType<JsonObject>().Where(l => Text(l, "kind") == "plant" && l["is_support"]?.GetValue<bool>() == false
            && l["surround"]?.GetValue<bool>() != true && l["grid_phantom"]?.GetValue<bool>() != true).ToArray();
        var fact = new JsonObject {
            ["column"] = pos.X, ["row"] = pos.Y, ["status"] = "ready",
            ["water"] = cell.isWater, ["lane_enabled"] = TowerDefenseManager.Instance.GetMapLineUse(pos.Y),
            ["terrain"] = ObservationFacts.Strings(cell.gridType.Select(t => t.ToString())), ["element_flags"] = cell.elementFlags,
            ["layers_status"] = complete ? "ready" : "partial", ["layers"] = layers,
            ["support_layer_ids"] = ObservationFacts.Strings(supports.Select(l => Text(l, "layer_id"))),
            ["upper_plant_layer_ids"] = ObservationFacts.Strings(uppers.Select(l => Text(l, "layer_id"))),
            ["has_upper_plant"] = JsonValue.Create(ObservationFacts.Presence(uppers.Length, complete)),
            ["obstacle_ids"] = obstacleIds, ["pending_gravestone"] = cell.HasPendingRandomGravestonePlacement(),
            ["support_requirements"] = new JsonObject {
                ["ordinary_ground_plant"] = cell.gridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.GROUND) ? "none"
                    : cell.isWater ? "compatible_lilypad_or_water_support" : "compatible_pot_or_native_terrain",
                ["source"] = "native_cell_terrain", ["card_specific"] = true }
        };
        if (includeShovel && complete && occupants.All(c => Valid(c.instance))) fact["shovel_targets"] = ShovelTargets(pos);
        else if (includeShovel) fact["shovel_targets"] = null;
        return fact;
    }

    private JsonObject? IceObstacle(TowerDefenseBattleFeatureMap map, int row, int columns, string scope, out bool complete)
    {
        complete = row < map.iceCapList.Count;
        if (!complete) return null;
        var ice = map.iceCapList[row].AsGodotObject() as TowerDefenseIceCap;
        if (!Valid(ice) || ice!.IsQueuedForDeletion() || ice.length <= 0) return null;
        if (!Valid(ice.iceCapSprite)) { complete = false; return null; }
        int first = TowerDefenseManager.Instance.GetMapGridPos(ice.iceCapSprite.GlobalPosition).X;
        var positions = new JsonArray();
        for (int col = Math.Max(1, first); col <= columns; col++)
            positions.Add(new JsonObject { ["column"] = col, ["row"] = row });
        if (positions.Count == 0) return null;
        return new JsonObject { ["obstacle_id"] = ObjectId(ice, scope), ["kind"] = "ice_trail", ["native_type"] = ice.GetType().Name,
            ["row"] = row, ["column"] = first, ["status"] = "known", ["occupied_cells"] = positions,
            ["blocks_planting"] = null, ["restricts_planting"] = true, ["blocking_rule"] = "native_icecap_range",
            ["terrain_exceptions"] = ObservationFacts.Strings(new[] { "ICECAP", "GRAVESTONE", "AIR" }),
            ["source"] = "TowerDefenseBattleFeatureMap.iceCapList.iceCapSprite" };
    }

    private bool CellRulesReady(TowerDefenseCellInstance? cell)
    {
        var map = TowerDefenseManager.GetMapFeature();
        return MapGridReady(map) && Valid(cell) && cell!.gridPos.Y < map.iceCapList.Count
            && CellOccupants(cell).All(c => !c.editorPreviewMode && Valid(c.config) && Valid(c.instance));
    }

    private JsonObject CharacterObstacle(TowerDefenseCharacter c, string scope, IEnumerable<Vector2I> occupied, bool cellBound = true)
    {
        bool configKnown = Valid(c.config);
        var graveConfig = configKnown ? c.config as TowerDefenseGravestoneConfig : null;
        bool grave = c is TowerDefenseGravestone;
        bool phantom = Valid(c.instance) && c.instance.hologram && !c.hologramOccupiesGrid;
        var positions = occupied.Where(p => TowerDefenseManager.Instance.CheckMapGridPosIn(p)).Distinct().ToArray();
        bool positionKnown = NativePositionKnown(c) || (cellBound && positions.Length > 0);
        var origin = NativePositionKnown(c) ? c.gridPos : positions.FirstOrDefault();
        return new JsonObject {
            ["obstacle_id"] = ObjectId(c, scope), ["kind"] = grave ? "gravestone" : "crater",
            ["native_type"] = c.GetType().Name, ["content_id"] = Valid(c.packet) ? c.packet.saveKey : configKnown ? c.config.name : null,
            ["row"] = positionKnown ? JsonValue.Create(origin.Y) : null, ["column"] = positionKnown ? JsonValue.Create(origin.X) : null,
            ["position_status"] = cellBound ? "native_cell_reference" : positionKnown ? "native_grid_pos" : "unknown",
            ["occupied_cells"] = positionKnown ? new JsonArray(positions.Select(p => (JsonNode?)new JsonObject { ["row"] = p.Y, ["column"] = p.X }).ToArray()) : null,
            ["status"] = configKnown && positionKnown ? "known" : "unknown",
            ["blocks_planting"] = graveConfig is not null ? JsonValue.Create(graveConfig.blocksPlanting) : null,
            ["restricts_planting"] = configKnown && cellBound && !c.editorPreviewMode && (!phantom || graveConfig?.blocksPlanting == true) ? JsonValue.Create(true) : null,
            ["cell_bound"] = cellBound, ["grid_phantom"] = phantom, ["card_dependent"] = true,
            ["blocking_rule"] = graveConfig?.blocksPlanting == true ? "native_blanket_plant_blocker"
                : grave ? "native_gravestone_slot" : "native_crater_slot",
            ["terrain_exceptions"] = graveConfig?.blocksPlanting == true ? new JsonArray()
                : ObservationFacts.Strings(grave ? new[] { "GRAVESTONE", "AIR" } : new[] { "CRATER", "AIR" }),
            ["source"] = "native_cell_references_and_character_tree",
            ["preview_only"] = c.editorPreviewMode,
            ["runtime_ready"] = Valid(c.instance) && Valid(c.componentManager)
        };
    }

    private void AddMapObservation(JsonObject state, int columns, int rows)
    {
        _policyCells.Clear();
        string battleId = Text(state, "battle_id"), scope = $"{battleId}:observe:{_observation}";
        var preview = new JsonObject { ["battle_id"] = battleId, ["observation_id"] = _observation,
            ["snapshot_id"] = scope, ["status"] = "unknown", ["terrain_status"] = "unknown",
            ["layers_status"] = "unknown", ["source"] = "native_map_grid_and_cell_references" };
        var obstacles = new JsonObject { ["battle_id"] = battleId, ["observation_id"] = _observation, ["snapshot_id"] = scope,
            ["status"] = "unknown", ["gravestones_present"] = null, ["items"] = null,
            ["coverage"] = "native_gravestones_craters_icecaps_and_cell_references" };
        state["map_preview"] = preview; state["obstacles"] = obstacles; state["cells"] = null;
        var map = TowerDefenseManager.GetMapFeature();
        if (!MapGridReady(map) || columns < 1 || rows < 1 || map!.config.gridNum != new Vector2I(columns, rows))
        { preview["reason"] = "native_grid_not_ready_or_map_transition"; return; }

        var captured = new List<(Vector2I Pos, TowerDefenseCellInstance Cell, TowerDefenseCharacter[] Occupants)>();
        var obstacleChars = new Dictionary<ulong, TowerDefenseCharacter>();
        var occupied = new Dictionary<ulong, List<Vector2I>>();
        bool complete = true, terrainComplete = true;
        for (int row = 1; row <= rows; row++)
        for (int col = 1; col <= columns; col++)
        {
            var pos = new Vector2I(col, row); var cell = TowerDefenseManager.GetMapCell(pos);
            if (!Valid(cell) || cell.gridPos != pos) { terrainComplete = complete = false; continue; }
            var occupants = CellOccupants(cell); captured.Add((pos, cell, occupants));
            _policyCells.Add((pos, cell));
            if (occupants.Any(c => !Valid(c.config)) || cell.HasPendingRandomGravestonePlacement()) complete = false;
            foreach (var c in occupants.Where(c => c is TowerDefenseGravestone or TowerDefenseCrater))
            {
                ulong id = c.GetInstanceId(); obstacleChars[id] = c;
                if (!occupied.TryGetValue(id, out var positions)) occupied[id] = positions = new();
                positions.Add(pos);
            }
        }
        var root = TowerDefenseManager.GetCharacterNode();
        if (Valid(root) && root.IsInsideTree())
        {
            // Skip each character's sprite/component subtree; those are not
            // independent board objects. Native groups below cover nested ones.
            foreach (var c in NativeCharacterObjects(root).Where(c => LiveMapObject(c) && c is TowerDefenseGravestone or TowerDefenseCrater))
            { obstacleChars[c.GetInstanceId()] = c; if (!Valid(c.config) || (!NativePositionKnown(c) && !occupied.ContainsKey(c.GetInstanceId()))) complete = false; }
        }
        else complete = false;
        // Native groups include obstacles that the Mod character registry omits.
        foreach (string group in new[] { "Gravestone", "Crater" })
            foreach (var c in _tree.GetNodesInGroup(group).OfType<TowerDefenseCharacter>().Where(LiveMapObject))
                if (c is TowerDefenseGravestone or TowerDefenseCrater
                    && ((Valid(root) && root.IsAncestorOf(c)) || occupied.ContainsKey(c.GetInstanceId())))
                { obstacleChars[c.GetInstanceId()] = c; if (!Valid(c.config) || (!NativePositionKnown(c) && !occupied.ContainsKey(c.GetInstanceId()))) complete = false; }
        var items = new JsonArray();
        var perCell = new Dictionary<Vector2I, List<string>>();
        void Bind(Vector2I pos, string id)
        { if (!perCell.TryGetValue(pos, out var ids)) perCell[pos] = ids = new(); if (!ids.Contains(id)) ids.Add(id); }
        foreach (var pair in obstacleChars)
        {
            var c = pair.Value; bool bound = occupied.TryGetValue(pair.Key, out var positions);
            positions ??= new List<Vector2I> { c.gridPos };
            var item = CharacterObstacle(c, scope, positions, bound); items.Add(item);
            foreach (var pos in positions) Bind(pos, Text(item, "obstacle_id"));
        }
        // Ice trails are native ground items, not characters.
        for (int row = 1; row <= rows; row++)
        {
            var item = IceObstacle(map, row, columns, scope, out bool iceComplete);
            complete &= iceComplete;
            if (item is null) continue;
            items.Add(item);
            foreach (var p in item["occupied_cells"]!.AsArray().OfType<JsonObject>())
                Bind(new Vector2I(Integer(p, "column"), Integer(p, "row")), Text(item, "obstacle_id"));
        }
        var cells = new JsonArray();
        foreach (var c in captured)
            cells.Add(CellFact(c.Pos, c.Cell, c.Occupants, scope, ObservationFacts.Strings(perCell.GetValueOrDefault(c.Pos) ?? new()), false));
        preview["status"] = complete ? "ready" : "partial";
        preview["terrain_status"] = terrainComplete ? "ready" : "partial";
        preview["layers_status"] = complete ? "ready" : "partial";
        preview["captured_cells"] = captured.Count; preview["expected_cells"] = columns * rows;
        obstacles["status"] = complete ? "ready" : "partial";
        obstacles["gravestones_present"] = JsonValue.Create(ObservationFacts.Presence(items.OfType<JsonObject>()
            .Count(o => Text(o, "kind") == "gravestone" && Text(o, "position_status") != "unknown"), complete));
        obstacles["items"] = items; state["cells"] = cells;
    }
}
