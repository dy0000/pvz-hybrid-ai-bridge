using System.Text.Json.Nodes;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private string _policyResourceIdentity = "", _policyResource = "";
    private string _policyResourceFailure = "native-level-source-unverified";
    private string _policyClockBattle = "";
    private double? _policyClockOrigin;
    private string _coveredBattle="";
    private readonly Dictionary<string,string> _observedOriginalPackets=new();
    private static JsonObject? CapturePolicyCannon(TowerDefenseCharacter character, CannonComponent cannon, long id, int row, int col, double hp)
    {
        if (!Valid(character.packet) || !Valid(cannon.sprite) || cannon.mousePressComponent is null || !Valid(cannon.mousePressComponent.ToggleTarget)
            || !Valid(cannon.projectileNode) || character.camp is not (TowerDefenseEnum.CHARACTER_CAMP.PLANT or TowerDefenseEnum.CHARACTER_CAMP.ZOMBIE)) return null;
        var result = new JsonObject { ["entity_id"] = id.ToString(), ["key"] = character.packet.saveKey,
            ["row"] = row, ["col"] = col, ["camp"] = character.camp == TowerDefenseEnum.CHARACTER_CAMP.PLANT ? 0 : 1,
            ["hp"] = hp, ["can_run_ability"] = character.CanRunAbility(), ["can_fire"] = cannon.CanFire(),
            ["sprite_state"] = new JsonObject { ["clip"] = cannon.sprite.clip },
            ["aim_visible"] = cannon.mousePressComponent.ToggleTarget.Visible, ["launch_visual_visible"] = cannon.projectileNode.Visible };
        if(character is TowerDefensePlantMagnetShroomDM dm) result["special_state"]=new JsonObject {
            ["isArmed"]=dm.Get("_isArmed").AsBool(),["shotReleased"]=dm.Get("_shotReleased").AsBool(),["ironCount"]=dm.ironCount };
        return result;
    }
    private void CaptureFreshPolicyClock(string battle, double runtimeSeconds, bool preparation)
    {
        if (_policyClockBattle != battle) { _policyClockBattle = battle; _policyClockOrigin = null; }
        if (preparation && !TowerDefenseManager.CurrentControl.hasProgress) _policyClockOrigin = runtimeSeconds;
    }
    private void CapturePolicyClock(JsonObject state, double runtimeSeconds)
    {
        string battle = state["battle_id"]!.GetValue<string>();
        // The SDK counter survives native scene reloads. Anchor only a proven
        // fresh preparation phase (Conveyor has no card-selection screen).
        // Restored attempts cannot invent an age.
        CaptureFreshPolicyClock(battle, runtimeSeconds,
            state["selecting_cards"]?.GetValue<bool>() == true || state["initializing"]?.GetValue<bool>() == true);
        if (_policyClockOrigin is double origin) state["simulation_seconds"] = runtimeSeconds - origin;
    }
    private string ContinuousLevelResource()
    {
        var config = TowerDefenseManager.CurrentControl.levelConfig;
        if (!string.IsNullOrEmpty(config.ResourcePath)) return config.ResourcePath;
        string identity = _battle + ":" + config.GetInstanceId();
        if (identity == _policyResourceIdentity) return _policyResource;
        _policyResourceIdentity = identity; _policyResource = "";
        // 0.31 CopySelectedSource/PrepareForLaunch deep-duplicate configs and
        // Godot clears ResourcePath. Resolve the real native catalog entry,
        // then prove its save key AND JSON data match the actual runtime copy.
        // Never construct a path from chapter/level numbers.
        var catalog = ResourceManager.LEVEL_RESOURCE.Get("data").AsGodotDictionary();
        var chapters = catalog["Adventure"].AsGodotDictionary()["Chapter"].AsGodotArray();
        var chapter = chapters[Global.Instance.currentChapterId].AsGodotDictionary();
        var entry = chapter["Level"].AsGodotArray()[Global.Instance.currentLevelId].AsGodotDictionary();
        if (entry["SaveKey"].AsString() != config.name) {
            _policyResourceFailure = "native-level-key-mismatch:" + config.name + ":" + entry["SaveKey"].AsString(); return "";
        }
        string path = entry["Level"].AsGodotDictionary()["Normal"].AsString();
        if (path.StartsWith("uid://", StringComparison.Ordinal)) path = ResourceUid.GetIdPath(ResourceUid.TextToId(path));
        var source = GD.Load<TowerDefenseLevelBaseConfig>(path);
        static Godot.Json? Data(TowerDefenseLevelBaseConfig c) => c switch {
            TowerDefenseLevelConfig old => old.data, TowerDefenseLevelNewConfig newer => newer.data, _ => null };
        var actualData = Data(config); var sourceData = Valid(source) ? Data(source) : null;
        if (!Valid(actualData) || !Valid(sourceData)) { _policyResourceFailure = "native-level-data-missing"; return ""; }
        // GD.Load does not call Init: the source's runtime name is unset until
        // initialization. Read its authoritative JSON Name without mutating it.
        string sourceKey = sourceData!.Data.AsGodotDictionary()["Name"].AsString();
        _policyResourceFailure = "native-level-source-unverified:" + config.name + ":" + sourceKey + ":" + path + ":" + source!.ResourcePath;
        return _policyResource = ContinuousPolicyFacts.CopiedLevelResource(config.name, entry["SaveKey"].AsString(), sourceKey,
            path, source.ResourcePath, Godot.Json.Stringify(actualData!.Data), Godot.Json.Stringify(sourceData!.Data));
    }
    private void AddContinuousPolicyObservation(JsonObject state, IReadOnlyList<XWModCardState> cards, bool entitiesComplete)
    {
        // Observe runs synchronously on the native main thread. Reuse its map,
        // card and character captures; do not create another observation loop.
        try
        {
            var control = TowerDefenseManager.CurrentControl;
            var map = TowerDefenseManager.GetMapFeature();
            if (state["running"]?.GetValue<bool>() != true || Global.IsMultiplayerMode
                || Global.Instance.enterLevelMode != "LevelChoose" || Global.Instance.currentLevelChoose != "Adventure"
                || !MapGridReady(map) || _policyCells.Count != 45
                || !Valid(map!.mapControl) || !Valid(control.levelConfig)) return;
            var bankMethod = TowerDefenseManager.Instance.GetCurrentPacketBankMethod();
            bool hasRain = control.featureDictionary.Values.OfType<TowerDefenseBattleFeatureRainMode>().Any(Valid);
            if (!hasRain && bankMethod is not (TowerDefenseEnum.LEVEL_SEEDBANK_METHOD.CHOOSE or TowerDefenseEnum.LEVEL_SEEDBANK_METHOD.CONVEYOR or TowerDefenseEnum.LEVEL_SEEDBANK_METHOD.PRESET)) return;
            // Same native source used by LevelChoose and wave initialization.
            // Both this explicit difficulty and the actual loaded resource are
            // matched by the client; an absent setting is never 'normal'.
            string difficulty = GameSaveManager.Instance.GetKeyValue("CurrentDifficult").AsString();
            if (difficulty != "Normal") return;
            var manager = TowerDefenseManager.Instance;
            var center = manager.GetMapCellPosCenter(Vector2I.One);
            var size = manager.GetMapGridSize();
            if (!float.IsFinite(size.X) || size.X <= 0 || !float.IsFinite(center.X)) return;
            var legal = new JsonArray();
            if (cards.Count > 32) return;
            foreach (var card in cards)
            {
                var raw = _nativeCards.TryGetValue(card.Handle.Id, out var native) ? native.Raw
                    : _oneUseCards.TryGetValue(card.Handle.Id, out var supplied) ? supplied.Raw : null;
                if (!LivePacket(raw!) || raw!.config.saveKey != card.ContentId || !Valid(raw.config.characterConfig)) return;
                // Target-only cards remain visible with their real quote and
                // readiness. The supplied selector explicitly excludes them;
                // they must never gain ordinary cell placements.
                if (raw.config.canPlaceOnZombie || raw.config.requiresPlantTarget
                    || raw.config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.PLANT)) continue;
                foreach (var (pos, cell) in _policyCells)
                {
                    // Native commit flags, not the weaker isCheck preview.
                    // CanPacketPlant only cleans invalid references; it neither
                    // charges sun nor starts cooldown/creation or grave events.
                    if (cell.CanPacketPlant(raw.config, false, false, true))
                        legal.Add(new JsonObject { ["card_id"] = card.Handle.Id, ["content_id"] = card.ContentId,
                            ["row"] = pos.Y, ["column"] = pos.X });
                }
            }
            string resource = ContinuousLevelResource();
            if (resource.Length == 0) { state["continuous_policy"] = ContinuousPolicyFacts.Incomplete(_policyResourceFailure); return; }
            if (_policyClockOrigin is null) { state["continuous_policy"] = ContinuousPolicyFacts.Incomplete("native-battle-clock-unverified"); return; }
            string currentBattle=state["battle_id"]!.GetValue<string>();
            if(_coveredBattle!=currentBattle){_coveredBattle=currentBattle;_observedOriginalPackets.Clear();}
            var instances=new JsonArray();var lineages=new JsonArray();var recoveryLegal=new JsonArray();
            foreach(var card in ((JsonArray)state["cards"]!).OfType<JsonObject>().Where(c=>c["source"]?.GetValue<string>()=="native")){
                string instance=card["native_object_id"]!.GetValue<string>(),key=card["content_id"]!.GetValue<string>();
                if(key.StartsWith("Plant",StringComparison.Ordinal))_observedOriginalPackets[instance]=key;
                var binding=new JsonObject{["instance_id"]=instance,["config_id"]=card["native_config_id"]!.DeepClone(),["save_key"]=key,["cost"]=card["cost"]!.DeepClone()};
                var record=(JsonObject)binding.DeepClone();record["ready"]=card["available"]!.GetValue<bool>()&&!card["pending_use"]!.GetValue<bool>();instances.Add(record);
                if(key!="ZombieImp"||!_observedOriginalPackets.TryGetValue(instance,out var original))continue;
                var lineage=new JsonObject{["instance_id"]=instance,["original_save_key"]=original,["covered_config_id"]=card["native_config_id"]!.DeepClone(),["basis"]="same_instance_observed_plant_to_imp_transition"};
                lineages.Add(lineage);
                foreach(var placement in legal.OfType<JsonObject>().Where(p=>p["card_id"]!.GetValue<long>()==card["id"]!.GetValue<long>()))recoveryLegal.Add(new JsonObject{
                    ["row"]=placement["row"]!.DeepClone(),["col"]=placement["column"]!.DeepClone(),["card_binding"]=binding.DeepClone(),["recovery_lineage"]=lineage.DeepClone()});
            }
            var facts = ContinuousPolicyFacts.Project(state, resource, center.X, size.X, legal, entitiesComplete);
            facts["card_instances"]=instances;facts["covered_card_lineage"]=lineages;facts["legal_recovery_placements"]=recoveryLegal;
            if (facts["complete"]?.GetValue<bool>() == true) {
                var targets = new JsonArray();
                if (state["native_boss_balls"] is JsonArray balls && balls.Count > 0)
                    foreach (var (pos, _) in _policyCells)
                        foreach (var target in ShovelTargets(pos).OfType<JsonObject>()) {
                            var character = TowerDefenseManager.GetMapCell(pos)?.GetShovelCharacter(target["layer_fraction"]!.GetValue<double>(), EnabledShovel()?.shovelConfig);
                            if (character is TowerDefensePlant && LiveMapObject(character) && character.camp == TowerDefenseEnum.CHARACTER_CAMP.PLANT
                                && character.config.plantGridOverrideType.ToString() is not ("POT" or "LILYPAD")
                                && character.config.plantGridType.Any(t => t.ToString() is "GROUND" or "POT" or "LILYPAD"))
                                targets.Add(target.DeepClone());
                        }
                facts["shovel_targets"] = targets;
            }
            bool hasConveyor = control.featureDictionary.Values.OfType<TowerDefenseBattleFeatureConveyorBelt>()
                .Any(b => Valid(b.conveyorBeltManager) && b.conveyorBeltManager.IsInsideTree());
            bool hasPersistentPackets = manager.GetSeedBankList().Any(raw => LivePacket(raw) && raw.start && !_oneUseCards.Values.Any(c => c.Raw == raw));
            facts["seedbank_method"] = hasRain ? (hasConveyor ? "ConveyorRain" : "Rain") : ContinuousPolicyFacts.SeedbankMethod(bankMethod.ToString(), hasConveyor, hasPersistentPackets);
            facts["plant_column"]=map.isPlantColumn;
            facts["native_energy_beans"]=state["native_energy_beans"]?.DeepClone();
            state["continuous_policy"] = facts;
        }
        catch (Exception ex)
        {
            // An unavailable export must not break the ordinary model path.
            state["continuous_policy"] = ContinuousPolicyFacts.Incomplete("native-export-error:" + ex.GetType().Name);
        }
    }
}
