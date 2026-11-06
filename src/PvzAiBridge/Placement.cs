using System.Text.Json.Nodes;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private static IEnumerable<Node> WalkNodes(Node node)
    { yield return node; foreach (var child in node.GetChildren()) foreach (var item in WalkNodes(child)) yield return item; }

    private JsonObject InspectCell(Vector2I pos)
    {
        if (!MapGridReady(TowerDefenseManager.GetMapFeature()))
            return new JsonObject { ["row"] = pos.Y, ["column"] = pos.X, ["status"] = "unknown",
                ["layers"] = null, ["terrain"] = null, ["reason"] = "native_grid_not_ready_or_map_transition", ["battle_id"] = _battle };
        var cell = TowerDefenseManager.GetMapCell(pos);
        if (!Valid(cell)) throw new InvalidOperationException("Cell is not initialized.");
        string scope = $"{_battle}:inspect:{++_cellInspection}";
        var occupants = CellOccupants(cell);
        var obstacles = new JsonArray(occupants.Where(c => c is TowerDefenseGravestone or TowerDefenseCrater)
            .Select(c => (JsonNode?)CharacterObstacle(c, scope, new[] { pos })).ToArray());
        var map = TowerDefenseManager.GetMapFeature();
        var ice = IceObstacle(map, pos.Y, map.config.gridNum.X, scope, out bool iceComplete);
        if (ice is not null && ice["occupied_cells"]!.AsArray().OfType<JsonObject>().Any(p => Integer(p, "column") == pos.X)) obstacles.Add(ice);
        var detail = CellFact(pos, cell, occupants, scope,
            ObservationFacts.Strings(obstacles.OfType<JsonObject>().Select(o => Text(o, "obstacle_id"))), true);
        detail["battle_id"] = _battle; detail["snapshot_id"] = scope; detail["obstacles"] = obstacles;
        detail["obstacles_status"] = iceComplete && occupants.All(c => Valid(c.config)) && !cell.HasPendingRandomGravestonePlacement() ? "ready" : "partial";
        return detail;
    }

    private JsonObject PlacementCheck(long cardId, Vector2I pos)
    {
        var card = ReadCards().FirstOrDefault(c => c.Handle.Id == cardId) ?? throw new ArgumentException("Unknown card_id.");
        var config = CardConfig(card);
        if (!Valid(config) || !Valid(config.characterConfig)) throw new InvalidOperationException("Card config is not initialized.");
        var cell = TowerDefenseManager.GetMapCell(pos);
        var detail = InspectCell(pos); var reasons = new JsonArray();
        void Reason(string code, string text) => reasons.Add(new JsonObject { ["code"] = code, ["message"] = text });
        if (card.Remaining > 0) Reason("cooldown", "卡牌仍在冷却。");
        if (TowerDefenseManager.Instance.GetSun() < card.Cost) Reason("insufficient_sun", "阳光不足。");
        bool ready = CellRulesReady(cell);
        bool? legal = ready ? cell.CanPacketPlant(config, noLimit: false, isCheck: true) : null;
        if (!ready) Reason("cell_not_ready", "原生格子或组件尚未就绪，合法性未知。");
        var targets = new JsonArray();
        bool targetCard = config.canPlaceOnZombie || config.requiresPlantTarget || config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.PLANT);
        if (targetCard)
        {
            foreach (var c in TowerDefenseManager.Instance._GetCleanCharacters().Where(c => Valid(c) && !c.die && !c.nearDie && !c.isDestroy))
                if (TargetAllowed(config, c) && _context.Game.Characters.TryGetHandle(c, out var h))
                { _observedCharacters[h.Id] = c; targets.Add(new JsonObject { ["entity_id"] = h.Id, ["row"] = c.gridPos.Y, ["column"] = c.gridPos.X }); }
            Reason("entity_target", "这张卡支持实体目标，请用返回的 entity_id 调用 use_target。");
        }
        JsonObject? blockerFact = null;
        if (legal == false && !targetCard)
        {
            var blocker = cell.FindPlantingBlocker(config);
            if (Valid(blocker)) {
                Reason("planting_blocker", "原生规则确认目标格或扩展格有阻挡种植的墓碑。");
                blockerFact = CharacterObstacle(blocker, Text(detail, "snapshot_id"), new[] { blocker.gridPos });
            }
            else if (config.GetPlantCover().Count > 0 && !config.GetCoverCanDirectPlant()
                && !cell.characterList.Any(c => Valid(c) && Valid(c.config) && !c.die && config.GetPlantCover().Contains(c.config.name)))
                Reason("missing_upgrade_base", "缺少该升级卡要求的基础植物。");
            else if (cell.isWater && config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.LILYPAD)
                && !cell.characterList.Any(c => Valid(c) && Valid(c.config) && !c.die && c.config.plantGridOverrideType == TowerDefenseEnum.PLANTGRIDTYPE.LILYPAD))
                Reason("missing_lilypad", "这张卡在水面需要睡莲类承载植物。");
            else if (!cell.GetGridType().Contains(TowerDefenseEnum.PLANTGRIDTYPE.GROUND) && config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.POT)
                && !cell.characterList.Any(c => Valid(c) && Valid(c.config) && !c.die && c.config.plantGridOverrideType == TowerDefenseEnum.PLANTGRIDTYPE.POT))
                Reason("missing_pot", "这张卡在该地形需要花盆类承载植物。");
            else Reason("native_placement_restricted", "原生放置规则不允许：检查格子分层、地形、占位或特殊卡条件。");
        }
        if (!card.Available && reasons.Count == 0) Reason("card_unavailable", "卡槽暂不可用。");
        return new JsonObject { ["card_id"] = cardId, ["content_id"] = card.ContentId, ["name"] = NameOf(card.ContentId),
            ["placement_scope"] = TowerDefenseManager.GetMapFeature()?.isPlantColumn == true ? "column" : "cell_or_entity",
            ["cost"] = CardCost(card), ["cooldown_remaining"] = card.Remaining, ["card_available"] = card.Available,
            ["native_cell_hint"] = JsonValue.Create(legal), ["can_submit_cell"] = card.Available && legal == true && !targetCard,
            ["blocking_obstacle"] = blockerFact,
            ["requires_target"] = targetCard, ["targets"] = targets, ["cell"] = detail, ["reasons"] = reasons,
            ["upgrade_bases"] = new JsonArray(config.GetPlantCover().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["note"] = "Point-in-time diagnostic; the native action remains authoritative. No automatic planting or support creation." };
    }

    private static bool TargetAllowed(TowerDefensePacketConfig config, TowerDefenseCharacter c)
    {
        if (!Valid(c) || c.die || c.nearDie || c.isDestroy || !Valid(c.instance)) return false;
        if (c is TowerDefenseZombie) return config.canPlaceOnZombie && config.IsZombiePlacementTarget(c, config.GetHypnoses());
        return c is TowerDefensePlant && (config.requiresPlantTarget || config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.PLANT))
            && !c.instance.invincible && !c.instance.hologram && c.instance.canBeCollection;
    }

    private JsonObject UseTarget(JsonObject body, ref bool actionStarted)
    {
        long cardId = body["card_id"]!.GetValue<long>(), entityId = body["entity_id"]!.GetValue<long>();
        var card = ReadCards().FirstOrDefault(c => c.Handle.Id == cardId) ?? throw new ArgumentException("Unknown card_id.");
        if (!card.Available) throw new InvalidOperationException("Card unavailable: check cooldown, sun and restrictions.");
        if (!_observedCharacters.TryGetValue(entityId, out var target) || !Valid(target)) throw new InvalidOperationException("Target is stale; observe or inspect again.");
        var config = CardConfig(card);
        if (!TargetAllowed(config, target)) throw new InvalidOperationException("Card cannot target this entity under native targeting rules.");
        if (cardId < 0) return UseOneUseCard(cardId, target.gridPos, ref actionStarted, target);
        // Public target-card methods charge normal sun and cooldown. Resolve only
        // an unambiguous playable card; never guess among duplicate card slots.
        var choices = WalkNodes(_tree.Root).OfType<TowerDefenseInGamePacketShow>()
            .Where(n => !n.onlyDraw && n.IsInsideTree() && n.config?.saveKey == card.ContentId && n.Call("CanUseFromMod").AsBool()).ToArray();
        if (choices.Length != 1) throw new InvalidOperationException("Playable target card is ambiguous; use cell-based plant or a unique card slot.");
        var raw = choices[0]; var pick = TowerDefenseManager.Instance.GetPacketPickControl();
        ClearPicker();
        long before = raw.Get("ModUseSequence").AsInt64();
        try
        {
            actionStarted = true;
            if (target is TowerDefenseZombie) raw.PlantOnZombie(target, config.GetHypnoses()); else raw.PlantOnPlant(target);
        }
        finally { if (Valid(pick) && pick.packetPick == raw) pick.PacketPickRelease(); }
        bool committed = raw.Get("ModUseSequence").AsInt64() > before;
        return new JsonObject { ["ok"] = committed, ["outcome"] = committed ? "Committed" : "rejected", ["content_id"] = card.ContentId, ["state"] = Observe() };
    }
}
