using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private static int? SelectionCapacity(TowerDefenseBattleFeaturePacketBank? feature)
    {
        var bank = feature?.packetBank?.seedBank;
        var manager = TowerDefenseManager.Instance;
        if (!Valid(manager) || !Valid(bank) || !bank!.IsInsideTree() || bank.IsQueuedForDeletion()
            || !bank.Get("_referencesInitialized").AsBool()) return null;
        // InitializeReferences has already resolved the level's native slot limit.
        // CanAddPacket uses this same total, including occupied/locked slots.
        int capacity = manager.seedbankPacketMax;
        return capacity >= 0 && capacity >= bank.packetNum ? capacity : null;
    }

    private void PreparationReady()
    {
        bool dialog = WalkNodes(_tree.Root).OfType<DialogBoxBase>().Any(n => n.IsVisibleInTree());
        ControlMode.RequirePreparation(_tree.Paused, ModalPause() || dialog);
    }

    private JsonObject ChangeSelection(JsonObject body, ref bool started)
    {
        PreparationReady();
        var feature = TowerDefenseManager.Instance.GetPacketBankFeature();
        if (Running || !SelectionOpen(feature)) throw new InvalidOperationException("Card selection is not open.");
        string oldKey = Text(body, "content_id");
        var bank = feature.packetBank.seedBank;
        var choices = bank.packetList.Where(p => Valid(p) && Valid(p.config)
            && (p.originalSaveKey.Length > 0 ? p.originalSaveKey : p.config.saveKey) == oldKey).ToArray();
        if (choices.Length != 1 || choices[0].@lock) throw new InvalidOperationException("Selected card is missing, ambiguous or locked.");
        string replacement = Text(body, "replacement_content_id");
        if (Text(body, "op") == "replace_card" && replacement.Length == 0 || Text(body, "op") == "deselect_card" && replacement.Length > 0)
            throw new ArgumentException("Replacement is required only for replace_card.");
        if (replacement.Length > 0)
        {
            if (replacement == oldKey || bank.HasPacket(replacement) || !feature.packetBankData.GetUnlockPacket().Contains(replacement))
                throw new InvalidOperationException("Replacement must be an unlocked, unselected different card.");
        }
        started = true;
        feature.packetBank.CancelPacketAnimation(choices[0]);
        bank.DeletePacket(choices[0]);
        feature.PacketAlive(oldKey);
        bool removed = !bank.HasPacket(oldKey);
        if (removed && replacement.Length > 0) feature.PacketListChoose(new Godot.Collections.Array { replacement });
        bool added = replacement.Length == 0 || bank.HasPacket(replacement);
        return new JsonObject { ["ok"] = removed && added, ["outcome"] = removed && added ? "selection_changed" : "selection_partial",
            ["removed"] = removed, ["replacement_selected"] = added, ["state"] = Observe(),
            ["note"] = "Native sequential replacement; not atomic. Observe partial outcomes, do not replay blindly." };
    }

    private JsonObject CardInfo(JsonObject body)
    {
        var requested = body["content_ids"] as JsonArray ?? throw new ArgumentException("content_ids required.");
        if (requested.Count is < 1 or > 32) throw new ArgumentException("Request 1..32 content IDs.");
        var live = ReadCards();
        var allowed = live.Select(c => c.ContentId).ToHashSet();
        var pick = TowerDefenseManager.Instance?.GetPacketBankFeature();
        if (Valid(pick?.packetBankData)) allowed.UnionWith(pick!.packetBankData.GetUnlockPacket());
        var result = new JsonArray();
        foreach (string id in requested.Select(n => n!.GetValue<string>()).Distinct())
        {
            if (!allowed.Contains(id)) throw new ArgumentException("Card is not in the current playable or unlocked selection domain: " + id);
            var config = TowerDefenseManager.GetPacketConfigReadOnly(id);
            if (!Valid(config) || !Valid(config.characterConfig)) throw new InvalidOperationException("Card config unavailable.");
            result.Add(new JsonObject { ["content_id"] = id, ["name"] = NameOf(id), ["base_cost"] = config.GetCost(),
                ["base_cooldown"] = config.GetPacketCooldown(), ["starting_cooldown"] = config.GetStartingCooldown(),
                ["placement_types"] = ObservationFacts.Strings(config.characterConfig.plantGridType.Select(t => t.ToString())),
                ["provides_terrain"] = config.characterConfig.plantGridOverrideType.ToString(),
                ["is_support"] = ObservationFacts.IsSupport(config.characterConfig.plantGridOverrideType.ToString(), config.characterConfig.physiqueTypeFlags),
                ["upgrade_bases"] = ObservationFacts.Strings(config.GetPlantCover()), ["direct_upgrade_allowed"] = config.GetCoverCanDirectPlant(),
                ["requires_plant_target"] = config.requiresPlantTarget, ["can_target_zombies"] = config.canPlaceOnZombie,
                ["live_slots"] = new JsonArray(live.Where(c => c.ContentId == id).Select(c => (JsonNode?)new JsonObject {
                    ["card_id"] = c.Handle.Id, ["effective_cost"] = CardCost(c), ["available"] = c.Available,
                    ["cooldown_remaining"] = c.Remaining, ["source"] = CardSource(c.Handle.Id) }).ToArray()),
                ["source"] = "native_config_and_current_slot_quotes", ["note"] = "Base config is not a live price quote; native placement remains authoritative." });
        }
        return new JsonObject { ["ok"] = true, ["battle_id"] = _battle, ["cards"] = result };
    }

    private JsonObject ShovelConfirmed(JsonObject body, ref bool started)
    {
        var pos = Cell(body);
        var shovel = EnabledShovel() ?? throw new InvalidOperationException("Shovel is not enabled.");
        var cell = TowerDefenseManager.GetMapCell(pos);
        double fraction = body["layer_fraction"]?.GetValue<double>() ?? .5;
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1) throw new ArgumentException("layer_fraction must be 0..1.");
        var target = Valid(cell) ? cell.GetShovelCharacter(fraction, shovel.shovelConfig) : null;
        if (!LiveMapObject(target) || !Valid(target!.instance) || !Valid(target.config)) throw new InvalidOperationException("No ready shovel target.");
        if (body["expected_target_object_id"] is JsonValue expected && expected.GetValue<string>() != target.GetInstanceId().ToString())
            throw new InvalidOperationException("Observed shovel target changed; no input performed.");
        if (!Running && !TutorialAllows("shovel", target.config.name)) throw new InvalidOperationException("Target is not required by this tutorial.");
        if (target is not (TowerDefensePlant or TowerDefenseItemSheild or TowerDefenseCrater))
            throw new InvalidOperationException("Native shovel does not execute on this target type in normal gameplay.");
        string content = target.packet?.saveKey ?? target.config.name;
        ulong objectId = target.GetInstanceId(); double hp = target.instance.hitpoints;
        int? stage = target is TowerDefenseCrater crater ? crater.stage : null;
        started = true;
        cell.Shovel(shovel.shovelConfig, fraction);
        bool removed = !Valid(target) || target.IsQueuedForDeletion() || target.die || target.isDestroy || !target.IsInsideTree();
        bool damaged = !removed && Valid(target.instance) && target.instance.hitpoints != hp;
        bool changedStage = !removed && target is TowerDefenseCrater after && after.stage != stage;
        bool effect = removed || damaged || changedStage;
        return new JsonObject { ["ok"] = effect, ["outcome"] = removed ? "shoveled" : effect ? "shovel_effect_confirmed" : "unknown_observe_before_retry",
            ["content_id"] = content, ["target_object_id"] = objectId.ToString(), ["removed"] = removed,
            ["damage_confirmed"] = damaged, ["stage_changed"] = changedStage, ["state"] = Observe(),
            ["note"] = effect ? "Native effect verified on the bound target." : "No immediate native effect confirmed; action may be deferred. Do not replay blindly." };
    }
}
