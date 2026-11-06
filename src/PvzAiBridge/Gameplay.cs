using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private readonly Dictionary<string, (TowerDefenseCoinBase Coin, int Lease)> _observedCoins = new();
    private bool _autoCollectCoins;
    private ulong _nextCoinCollection;
    private int _coinCollectionsStarted;

    private T? Feature<T>() where T : TowerDefenseBattleFeature => Valid(TowerDefenseManager.CurrentControl)
        ? TowerDefenseManager.CurrentControl.featureDictionary.Values.OfType<T>().FirstOrDefault(Valid) : null;

    private IEnumerable<TowerDefenseCoinBase> Coins() => _tree.GetNodesInGroup("Coin").OfType<TowerDefenseCoinBase>()
        .Where(c => Valid(c) && c.IsInsideTree() && !c.IsQueuedForDeletion() && !c.die && !c.disabledInput);

    private long? ObservedEntityId(TowerDefenseCharacter character)
    {
        if (!Valid(character) || !_context.Game.Characters.TryGetHandle(character, out var handle)) return null;
        _observedCharacters[handle.Id] = character;
        return handle.Id;
    }

    private TowerDefenseCharacter CurrentEntity(JsonObject body)
    {
        long id = body["entity_id"]?.GetValue<long>() ?? throw new ArgumentException("entity_id required.");
        if (!_observedCharacters.TryGetValue(id, out var entity) || !Valid(entity) || !entity.IsInsideTree()
            || entity.IsQueuedForDeletion() || entity.die || entity.isDestroy)
            throw new InvalidOperationException("Entity is stale; observe again.");
        return entity;
    }

    private PacketPickControl ClearPicker()
    {
        var picker = TowerDefenseManager.Instance.GetPacketPickControl();
        if (!Valid(picker) || picker.IsPicking())
            throw new InvalidOperationException("A native card/tool is selected or the picker is unavailable; do not replace its selection.");
        return picker;
    }

    private JsonObject ObserveMechanics()
    {
        var result = new JsonObject {
            ["status"] = "ready", ["auto_collect_coins"] = _autoCollectCoins,
            ["coin_collections_started"] = _coinCollectionsStarted,
            ["features"] = Valid(TowerDefenseManager.CurrentControl)
                ? ObservationFacts.Strings(TowerDefenseManager.CurrentControl.featureDictionary.Keys.Select(k => k.ToString())) : new JsonArray(),
            ["column_planting"] = TowerDefenseManager.GetMapFeature()?.isPlantColumn == true,
            ["reverse_role"] = Valid(TowerDefenseManager.Instance) && TowerDefenseManager.Instance.IsIZM2Mode(),
            ["finish_method"] = Valid(TowerDefenseManager.CurrentControl) ? TowerDefenseManager.GetGameMethod().ToString() : null
        };
        var coins = new JsonArray();
        foreach (var coin in Coins())
        {
            string id = _observation + ":" + coin.GetInstanceId();
            _observedCoins[id] = (coin, coin.Get("_leaseVersion").AsInt32());
            coins.Add(new JsonObject { ["id"] = id, ["value"] = coin.num,
                ["kind"] = coin.GetType().Name, ["collecting"] = coin.isCollect,
                ["x"] = coin.GlobalPosition.X, ["y"] = coin.GlobalPosition.Y });
        }
        result["coins"] = coins;
        result["quiz"] = ObserveQuiz();
        var speedup = TowerDefenseManager.CurrentControl?.checkBox2X;
        result["speedup"] = new JsonObject { ["available"] = VisibleNode(speedup) && !speedup!.Disabled,
            ["enabled"] = Valid(speedup) ? JsonValue.Create(speedup!.ButtonPressed) : null,
            ["actual_time_scale"] = Global.TimeScale };
        var vases = new JsonArray();
        foreach (var vase in _tree.GetNodesInGroup("Vase").OfType<TowerDefenseVase>()
            .Where(v => Valid(v) && v.IsInsideTree() && !v.IsQueuedForDeletion() && !v.die && !v.isDestroy))
            vases.Add(new JsonObject { ["entity_id"] = ObservedEntityId(vase), ["column"] = vase.gridPos.X,
                ["row"] = vase.gridPos.Y, ["opening"] = vase.pressed,
                // Only content already revealed by the game's normal visibility rules.
                ["content_visible"] = vase.showPacket,
                ["visible_content_id"] = vase.showPacket && Valid(vase.packetConfig) ? vase.packetConfig.saveKey : null });
        result["vases"] = vases;
        var glove = Feature<TowerDefenseBattleFeatureGlove>();
        result["glove"] = glove is null ? null : new JsonObject {
            ["enabled"] = Valid(glove.gloveManager) && glove.gloveManager.gloveShow,
            ["picking"] = glove.glovePick, ["picked_entity_id"] = ObservedEntityId(glove.pickedCharacter) };
        var hammer = Feature<TowerDefenseBattleFeatureHammer>()?.hammerModeNode;
        result["hammer"] = new JsonObject { ["enabled"] = Valid(hammer) && hammer!.IsInsideTree() };
        var slot = Feature<TowerDefenseBattleFeatureSlotMachine>();
        result["slot_machine"] = slot is null ? null : new JsonObject { ["feature_id"] = slot.GetInstanceId().ToString(),
            ["open"] = slot.open, ["running"] = slot.running, ["can_spin"] = slot.CanSpin(), ["cost"] = slot.cost,
            ["spin_count"] = slot.spinNum, ["spinning"] = slot.Get("_spinInProgress").AsBool(),
            ["cooldown_remaining"] = slot.cooldownTimer, ["status_text"] = slot.GetStatusText() };
        var gem = Feature<TowerDefenseBattleFeatureGemMatch>();
        if (gem is not null && Valid(gem.config))
        {
            var board = new JsonArray();
            for (int row = 0; row < gem.grid.Count; row++)
            for (int column = 0; column < gem.grid[row].Count; column++)
            {
                var piece = gem.grid[row][column];
                board.Add(new JsonObject { ["column"] = column + 1, ["row"] = row + 1,
                    ["status"] = Valid(piece) ? "ready" : "unknown", ["hole"] = Valid(piece) ? JsonValue.Create(piece.isHole) : null,
                    ["content_id"] = Valid(piece) && !piece.isHole ? piece.characterKey.ToString() : null });
            }
            var upgrades = new JsonArray();
            var bank = TowerDefenseManager.Instance.GetSeedBankFeature()?.seedBank;
            if (Valid(bank)) foreach (var packet in bank!.packetList.Where(p => Valid(p) && p.HasMeta("gem_match_upgrade_key")))
            {
                string key = packet.GetMeta("gem_match_upgrade_key").AsString();
                upgrades.Add(new JsonObject { ["upgrade_key"] = key,
                    ["cost"] = gem.config.GetUpgradeCost(gem.config.GetUpgradeSource(key)), ["available"] = packet.alive && !packet.@lock });
            }
            result["gem_match"] = new JsonObject { ["feature_id"] = gem.GetInstanceId().ToString(),
                ["board_revision"] = gem.Get("_boardRevision").AsInt64(), ["processing"] = gem.isProcessing,
                ["native_state"] = gem.Get("_state").AsInt32(), ["dragging"] = gem.isDragging,
                ["columns"] = gem.config.boardCols, ["rows"] = gem.config.boardRows, ["board"] = board,
                ["match_count"] = gem.currentMatchCount, ["match_value"] = gem.currentMatchValue,
                ["clear_match_target"] = gem.Call("GetClearMatchCountTarget").AsInt32(), ["holes"] = gem.Get("_holeCount").AsInt32(),
                ["fill_hole_cost"] = gem.config.fillHoleCost, ["refresh_cost"] = gem.config.refreshBoardCost, ["upgrades"] = upgrades };
        }
        else result["gem_match"] = null;
        var stars = Feature<TowerDefenseBattleFeatureLookStar>();
        var targets = new JsonArray();
        if (stars is not null && Valid(stars.config)) foreach (var check in stars.config.checkList)
            if (Valid(check)) targets.Add(new JsonObject { ["column"] = check.gridPos.X, ["row"] = check.gridPos.Y,
                ["content_id"] = check.packetName });
        result["formation_goal"] = stars is null ? null : new JsonObject { ["complete"] = stars.isfinish, ["targets"] = targets };
        var portals = new JsonArray();
        var portalFeature = Feature<TowerDefenseBattleFeaturePortal>();
        if (portalFeature is not null) foreach (var portal in portalFeature.portalList.Where(Valid))
            portals.Add(new JsonObject { ["id"] = portal.GetInstanceId().ToString(), ["shape"] = portal.shape,
                ["from_column"] = portal.gridPos1.X, ["from_row"] = portal.gridPos1.Y,
                ["to_column"] = portal.gridPos2.X, ["to_row"] = portal.gridPos2.Y,
                ["changing"] = portal.isChange, ["change_interval"] = portal.changeTime, ["change_timer"] = portal.changeTimer });
        result["portals"] = portals;
        var wave = Feature<TowerDefenseBattleFeatureWave>();
        result["survival"] = wave?.isSurvival == true && Valid(wave.survivalRunner)
            ? new JsonObject { ["round"] = wave.survivalRunner.roundNum } : null;
        var brains = Feature<TowerDefenseBattleFeatureBrain>();
        result["brain_rows"] = brains is null ? null : new JsonArray(brains.brainLine.Select((brain, row) => (brain, row))
            .Where(p => Valid(p.brain) && !p.brain.isDestroy).Select(p => (JsonNode?)JsonValue.Create(p.row)).ToArray());
        return result;
    }

    private JsonObject GameplayAction(string op, JsonObject body, ref bool actionStarted)
    {
        if (MenuNavigation.TopDialog() is not null || TowerDefenseManager.CurrentControl.isGameFail
            || TowerDefenseManager.CurrentControl.HasBattleAwarded)
            throw new InvalidOperationException("A modal dialog or battle result blocks gameplay actions.");
        if (op == "auto_collect_coins")
        {
            bool enabled = body["enabled"]?.GetValue<bool>() ?? throw new ArgumentException("enabled required.");
            if (enabled && !_mode.Realtime) throw new InvalidOperationException("Auto collection requires realtime mode.");
            _autoCollectCoins = enabled; _nextCoinCollection = 0;
            return new JsonObject { ["ok"] = true, ["outcome"] = "collection_configured", ["enabled"] = enabled };
        }
        if (op == "collect_coins")
        {
            var ids = body["coin_ids"] as JsonArray ?? throw new ArgumentException("coin_ids required.");
            if (ids.Count is < 1 or > 128) throw new ArgumentException("Collect 1..128 observed coins.");
            var coins = ids.Select(n => n!.GetValue<string>()).Distinct().Select(id =>
                _observedCoins.TryGetValue(id, out var coin) ? coin : throw new InvalidOperationException("Coin ID is stale; observe again.")).ToArray();
            if (coins.Any(c => !Valid(c.Coin) || !c.Coin.IsInsideTree() || c.Coin.IsQueuedForDeletion() || c.Coin.die
                || c.Coin.isCollect || c.Coin.disabledInput || c.Coin.Get("_leaseVersion").AsInt32() != c.Lease))
                throw new InvalidOperationException("A coin disappeared, was recycled or is already collecting.");
            actionStarted = true;
            foreach (var coin in coins) coin.Coin.Collection();
            return new JsonObject { ["ok"] = true, ["outcome"] = "collection_started", ["count"] = coins.Length };
        }
        ClearPicker();
        if (op == "set_native_speedup")
        {
            var speedup = TowerDefenseManager.CurrentControl.checkBox2X;
            if (!VisibleNode(speedup) || speedup.Disabled) throw new InvalidOperationException("Native speed toggle is not enabled.");
            bool enabled = body["enabled"]?.GetValue<bool>() ?? throw new ArgumentException("enabled required.");
            double? multiplier=body["multiplier"]?.GetValue<double>();
            if(multiplier is not null && (!enabled || multiplier!=3)) throw new ArgumentException("Explicit multiplier supports enabled 3x only.");
            actionStarted = true; speedup.ButtonPressed = enabled;
            // Explicit administrator request; ordinary toggles keep the game's
            // own per-level default (1.5x for old configs, 3x for new configs).
            if(multiplier is double requested) Global.TimeScale=requested;
            return new JsonObject { ["ok"] = speedup.ButtonPressed == enabled, ["outcome"] = "native_speedup_set",
                ["enabled"] = speedup.ButtonPressed, ["actual_time_scale"] = Global.TimeScale };
        }
        if (op == "entity_interact") return InteractEntity(body, ref actionStarted);
        if (op == "slide_cell") return SlideCell(body, ref actionStarted);
        if (op == "open_pause_menu")
        {
            var control = TowerDefenseManager.CurrentControl;
            if (!Valid(control.buttonPause) || !control.buttonPause.IsVisibleInTree() || control.buttonPause.Disabled)
                throw new InvalidOperationException("Native pause-menu button is not available.");
            actionStarted = true; control.buttonPause.ButtonPressed = true;
            return new JsonObject { ["ok"] = MenuNavigation.TopDialog() is DialogBattlePause,
                ["outcome"] = "pause_menu_requested" };
        }
        if (op == "break_vase")
        {
            if (CurrentEntity(body) is not TowerDefenseVase vase || vase.pressed)
                throw new InvalidOperationException("Target is not an unopened native vase.");
            var pointer = new JsonObject { ["entity_id"] = body["entity_id"]!.DeepClone(), ["action"] = "click" };
            InteractEntity(pointer, ref actionStarted, vase);
            return new JsonObject { ["ok"] = vase.pressed, ["outcome"] = vase.pressed ? "vase_opening_started" : "rejected",
                ["entity_id"] = body["entity_id"]!.DeepClone(), ["completion_pending"] = vase.pressed };
        }
        if (op == "move_plant")
        {
            var glove = Feature<TowerDefenseBattleFeatureGlove>();
            if (glove is null || !Valid(glove.gloveManager) || !glove.gloveManager.gloveShow || glove.gloveManager.glovePressedAwait)
                throw new InvalidOperationException("Native glove is not enabled/ready.");
            var entity = CurrentEntity(body); var source = entity.cell; var pos = Cell(body);
            var target = TowerDefenseManager.GetMapCell(pos);
            double fraction = body["layer_fraction"]?.GetValue<double>() ?? .5;
            if (!double.IsFinite(fraction) || fraction is < 0 or > 1) throw new ArgumentException("layer_fraction must be 0..1.");
            if (entity is not TowerDefensePlant || !Valid(source) || source.GetShovelCharacter(fraction) != entity
                || !source.CanShovel(fraction) || !Valid(entity.packet) || !CellRulesReady(target)
                || pos == entity.gridPos || !target.CanPacketPlant(entity.packet))
                throw new InvalidOperationException("Exact plant/layer cannot be moved to this cell under native glove rules.");
            try
            {
                actionStarted = true;
                glove.PickGlove(true); glove.SelectCharacter(entity, source, entity.gridPos); glove.PlaceCharacter(target, pos);
            }
            finally
            {
                glove.GloveRelease();
                if (glove.glovePick || Valid(glove.pickedCharacter) || TowerDefenseManager.Instance.GetPacketPickControl().IsPicking())
                    throw new InvalidOperationException("Native glove selection release unconfirmed; do not replay.");
            }
            bool moved = Valid(entity) && entity.cell == target && entity.gridPos == pos;
            return new JsonObject { ["ok"] = moved, ["outcome"] = moved ? "plant_moved" : "unknown_observe_before_retry",
                ["entity_id"] = body["entity_id"]!.DeepClone(), ["selection_released"] = true, ["native_effect_confirmed"] = moved };
        }
        if (op == "hammer_hit")
        {
            if (Feature<TowerDefenseBattleFeatureHammer>()?.hammerModeNode is not TowerDefenseHammerMode hammer || !hammer.IsInsideTree())
                throw new InvalidOperationException("Native hammer mode is not active.");
            Vector2 target = TowerDefenseManager.Instance.GetMapCellPosCenter(Cell(body));
            if (body["entity_id"] is not null)
            {
                var entity = CurrentEntity(body);
                if (entity.gridPos != Cell(body)) throw new InvalidOperationException("Hammer entity no longer occupies the requested cell.");
                target = entity.GetLogicalGlobalPosition();
            }
            var sprite = hammer.Get("hammer").As<AdobeAnimateSpriteBase>();
            if (!Valid(sprite)) throw new InvalidOperationException("Native hammer sprite is not ready.");
            actionStarted = true;
            sprite.GlobalPosition = target; AudioManager.Instance.AudioPlay("Swing");
            sprite.SetAnimation("WhackZombie", false, .1); hammer.isAttack = true; hammer.checkCharacterList.Clear(); hammer.Attack();
            return new JsonObject { ["ok"] = true, ["outcome"] = "hammer_swing_started", ["completion_pending"] = true,
                ["note"] = "Native Attack resolves after a physics frame; no damage or kill is claimed by this receipt." };
        }
        if (op == "slot_spin")
        {
            var slot = Feature<TowerDefenseBattleFeatureSlotMachine>() ?? throw new InvalidOperationException("No native slot machine.");
            if (Text(body, "feature_id") != slot.GetInstanceId().ToString() || Integer(body, "expected_spin_count") != slot.spinNum
                || !slot.CanSpin() || !Valid(slot.slotMachineControl)) throw new InvalidOperationException("Slot quote is stale or machine cannot spin.");
            int before = slot.spinNum; actionStarted = true; slot.RequestSpin();
            bool committed = slot.spinNum == before + 1;
            return new JsonObject { ["ok"] = committed, ["outcome"] = committed ? "spin_started" : "rejected",
                ["native_count_before"] = before, ["native_count_after"] = slot.spinNum, ["completion_pending"] = committed };
        }
        if (op == "gem_action")
        {
            var gem = Feature<TowerDefenseBattleFeatureGemMatch>() ?? throw new InvalidOperationException("No native gem-match board.");
            if (Text(body, "feature_id") != gem.GetInstanceId().ToString()
                || body["expected_board_revision"]?.GetValue<long>() != gem.Get("_boardRevision").AsInt64()
                || gem.isProcessing || gem.isDragging || gem.Get("_state").AsInt32() != 0)
                throw new InvalidOperationException("Board is stale, processing or held by the player; observe again.");
            string action = Text(body, "action");
            if (action is not ("swap" or "fill_hole" or "refresh" or "upgrade")) throw new ArgumentException("Unknown gem action.");
            var command = new Godot.Collections.Dictionary { ["action"] = action };
            if (action == "swap")
            {
                var from = new Vector2I(Integer(body, "from_column") - 1, Integer(body, "from_row") - 1);
                var to = new Vector2I(Integer(body, "to_column") - 1, Integer(body, "to_row") - 1);
                if (from.X < 0 || from.Y < 0 || to.X < 0 || to.Y < 0 || from.X >= gem.config.boardCols || to.X >= gem.config.boardCols
                    || from.Y >= gem.config.boardRows || to.Y >= gem.config.boardRows || Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) != 1)
                    throw new ArgumentException("Swap requires two adjacent 1-based board cells.");
                command["from_x"] = from.X; command["from_y"] = from.Y; command["to_x"] = to.X; command["to_y"] = to.Y;
            }
            if (action == "upgrade")
            {
                string key = Text(body, "upgrade_key");
                if (key.Length is < 1 or > 128) throw new ArgumentException("upgrade_key required.");
                command["upgrade_key"] = key;
            }
            actionStarted = true;
            bool accepted = gem.ExecuteNetworkCommand(command, EconomyAccountId.Local);
            return new JsonObject { ["ok"] = accepted, ["outcome"] = accepted ? "gem_action_started" : "rejected",
                ["action"] = action, ["board_revision_after"] = gem.Get("_boardRevision").AsInt64(),
                ["completion_pending"] = accepted && gem.isProcessing };
        }
        throw new ArgumentException("Unknown gameplay action.");
    }
}
