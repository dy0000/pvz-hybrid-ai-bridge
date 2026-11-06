using System.Text.Json.Nodes;
using Godot;
using System.Reflection;
using PVZHE.ModEditor.ModSystem;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private sealed record OneUseCard(TowerDefenseInGamePacketShow Raw, OneUseCardIdentity Identity, long? Cost, bool Available);
    private readonly OneUseCardRegistry _oneUseIds = new();
    private readonly Dictionary<long, OneUseCard> _oneUseCards = new();
    private sealed record NativeCard(TowerDefenseInGamePacketShow Raw, long? Cost);
    private readonly Dictionary<long, NativeCard> _nativeCards = new();
    private delegate bool ResolveNativeCard(XWModCardHandle handle, out TowerDefenseInGamePacketShow card);
    // Resolve the API handle itself, never pair slots by content/order/instance ID.
    // The shipped 0.31 API keeps TryResolve internal; no game DLL patch is needed.
    private static readonly ResolveNativeCard? NativeCardResolver = CreateNativeCardResolver();
    private static ResolveNativeCard? CreateNativeCardResolver()
    {
        try { return typeof(XWModCardState).Assembly.GetType("PVZHE.ModEditor.ModSystem.XWModCards")?
            .GetMethod("TryResolve", BindingFlags.Static | BindingFlags.NonPublic)?.CreateDelegate<ResolveNativeCard>(); }
        catch { return null; } // Missing metadata means unknown, never guessed slots.
    }

    private static bool LivePacket(TowerDefenseInGamePacketShow raw) => Valid(raw) && raw.IsInsideTree()
        && !raw.IsQueuedForDeletion() && Valid(raw.config) && !raw.onlyDraw
        && (!raw.HasSunAccount || raw.SunAccountId.IsLocal);

    private IEnumerable<TowerDefenseInGamePacketShow> FreeConveyorCards()
    {
        var control = TowerDefenseManager.CurrentControl;
        if (!Valid(control)) yield break;
        foreach (var belt in control.featureDictionary.Values.OfType<TowerDefenseBattleFeatureConveyorBelt>())
        {
            var manager = belt.conveyorBeltManager;
            if (!Valid(manager) || !manager.IsInsideTree()) continue;
            foreach (var raw in manager.GetPacketChildren(manager.isMobileUI).OfType<TowerDefenseInGamePacketShow>())
                if (LivePacket(raw) && raw.plantOnce && !raw.start && !raw.useCost) yield return raw;
        }
    }

    private IEnumerable<TowerDefenseInGamePacketShow> FallingCards()
    {
        // SpawnPacketCore and rain save restoration attach exactly here. Do not
        // search the whole UI tree: seed-bank/shop/handbook previews are not drops.
        var root = TowerDefenseManager.GetCharacterNode();
        if (!Valid(TowerDefenseManager.CurrentControl) || !Valid(root) || !root.IsInsideTree() || root.IsQueuedForDeletion()
            || SceneManager.CurrentScene != "TowerDefense") yield break;
        foreach (var raw in root.GetChildren().OfType<TowerDefenseInGamePacketShow>())
            if (LivePacket(raw) && raw.plantOnce) yield return raw;
    }

    private IEnumerable<TowerDefenseInGamePacketShow> ColumnCards()
    {
        if (TowerDefenseManager.GetMapFeature()?.isPlantColumn != true) yield break;
        var bank = TowerDefenseManager.Instance.GetSeedBankFeature()?.seedBank;
        if (!Valid(bank)) yield break;
        foreach (var raw in bank!.packetList)
            if (LivePacket(raw) && raw.start && !raw.HasMeta("is_gem_match_packet")
                && !raw.HasMeta("is_fill_hole_packet") && !raw.HasMeta("is_refresh_board_packet")
                && !raw.HasMeta("gem_match_upgrade_key")) yield return raw;
    }

    private static OneUseCardIdentity PacketIdentity(TowerDefenseInGamePacketShow raw, string source) => new(
        raw.GetInstanceId(), raw.Get("_asyncLifetimeVersion").AsInt32(), raw.config.GetInstanceId(), raw.Get("ModUseSequence").AsInt64(), source);

    private static long? PacketCost(TowerDefenseInGamePacketShow raw)
    {
        if (!raw.useCost) return 0; // Native TryBeginItemCost does not charge this card.
        // Resolve through the native label quote, including cost hooks. Unknown
        // quotes stay unavailable; config.GetCost is not a live slot quote.
        raw.Call("UpdateItemCostLabel", false);
        return raw.Get("_itemCostLabelInitialized").AsBool() && raw.Get("_itemCostLabelQuoteValid").AsBool()
            ? raw.Get("_itemCostLabelValue").AsInt64() : null;
    }

    private bool PacketAvailable(TowerDefenseInGamePacketShow raw, long? cost)
    {
        var pick = TowerDefenseManager.Instance.GetPacketPickControl();
        bool alreadyPicked = Valid(pick) && pick.packetPick == raw && raw.select;
        return OneUseCardRegistry.CanUse(raw.alive, raw.@lock, raw.coldDownOpen, raw.HasMeta("packet_pending_plant"),
            raw.pressDelayTimer, alreadyPicked, raw.aliveTime, raw.aliveTimer,
            cost, raw.Call("CanAffordItemCost").AsBool());
    }

    private IReadOnlyList<XWModCardState> ReadCards()
    {
        _oneUseIds.Begin(_battle); _oneUseCards.Clear(); _nativeCards.Clear();
        var cards = _context.Game.Cards.GetCards().ToList();
        cards = cards.Select(c => {
            if (NativeCardResolver is null || !NativeCardResolver(c.Handle, out var raw) || !LivePacket(raw)) return c;
            long? cost = PacketCost(raw);
            _nativeCards[c.Handle.Id] = new NativeCard(raw, cost);
            return c with { ContentId = raw.config.saveKey, Cost = cost ?? 0,
                Available = c.Available && cost.HasValue && !raw.HasMeta("packet_pending_plant") };
        }).ToList();
        if (TowerDefenseManager.GetMapFeature()?.isPlantColumn == true)
            cards = cards.Select(c => c with { Available = false }).ToList();
        if (Guid.TryParse(_battle, out var battle))
        {
            var packets = FreeConveyorCards().Select(raw => (Raw: raw, Source: "free_conveyor"))
                .Concat(FallingCards().Select(raw => (Raw: raw, Source: "falling_card")))
                .Concat(ColumnCards().Select(raw => (Raw: raw, Source: "column_card")));
            foreach (var packet in packets.DistinctBy(p => p.Raw.GetInstanceId()))
            {
                var raw = packet.Raw; var identity = PacketIdentity(raw, packet.Source);
                long id = _oneUseIds.Bind(identity); long? cost = PacketCost(raw);
                bool available = PacketAvailable(raw, cost);
                _oneUseCards[id] = new OneUseCard(raw, identity, cost, available);
                cards.Add(new XWModCardState(new XWModCardHandle(battle, id), raw.config.saveKey,
                    cost ?? 0, raw.coldDownTimer, raw.coldDown, raw.coldDownOpen, available, raw.select));
            }
        }
        _oneUseIds.End();
        // The SDK also reports some belt packets as unavailable ordinary cards.
        // Keep the native one-use binding once; deduplicate ONLY the identical
        // Godot object, never separate packets of the same species.
        var suppliedObjects = _oneUseCards.Values.Select(c => c.Identity.Instance).ToHashSet();
        return cards.Where(c => !_nativeCards.TryGetValue(c.Handle.Id, out var native)
            || !suppliedObjects.Contains(native.Raw.GetInstanceId())).ToList();
    }

    private string CardSource(long id) => _oneUseCards.TryGetValue(id, out var binding) ? binding.Identity.Source : "native";
    private JsonNode? CardCost(XWModCardState card) => _oneUseCards.TryGetValue(card.Handle.Id, out var binding)
        ? JsonValue.Create(binding.Cost) : JsonValue.Create(_nativeCards.GetValueOrDefault(card.Handle.Id)?.Cost);
    private TowerDefensePacketConfig CardConfig(XWModCardState card) =>
        _oneUseCards.TryGetValue(card.Handle.Id, out var binding) && Valid(binding.Raw) ? binding.Raw.config
            : _nativeCards.TryGetValue(card.Handle.Id, out var native) && LivePacket(native.Raw) ? native.Raw.config
            : TowerDefenseManager.GetPacketConfigReadOnly(card.ContentId);

    private void AddOneUseMetadata(JsonObject fact, long id)
    {
        if (!_oneUseCards.TryGetValue(id, out var card)) {
            bool known = _nativeCards.TryGetValue(id, out var native) && LivePacket(native.Raw);
            fact["cost_status"] = known && native!.Cost.HasValue ? "known" : "unknown";
            fact["pending_use"] = known ? JsonValue.Create(native!.Raw.HasMeta("packet_pending_plant")) : null;
            if (known) {
                var rawNative = native!.Raw;
                fact["native_object_id"] = rawNative.GetInstanceId().ToString();
                fact["native_config_id"] = rawNative.config.GetInstanceId().ToString();
                fact["native_generation"] = rawNative.Get("_asyncLifetimeVersion").AsInt32();
                fact["item_cost"] = rawNative.itemCost;
                fact["use_cost"] = rawNative.useCost; fact["single_use"] = rawNative.plantOnce;
            }
            if (known && Valid(native!.Raw.config.characterConfig)) {
                var config = native!.Raw.config;
                fact["requires_target"] = config.canPlaceOnZombie || config.requiresPlantTarget
                    || config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.PLANT);
            }
            return;
        }
        var raw = card.Raw;
        fact["native_object_id"] = card.Identity.Instance.ToString();
        fact["native_config_id"] = card.Identity.Config.ToString();
        fact["item_cost"] = raw.itemCost;
        if (Valid(raw.config.characterConfig)) fact["requires_target"] = raw.config.canPlaceOnZombie || raw.config.requiresPlantTarget
            || raw.config.characterConfig.plantGridType.Contains(TowerDefenseEnum.PLANTGRIDTYPE.PLANT);
        fact["native_generation"] = card.Identity.Lifetime;
        fact["single_use"] = raw.plantOnce; fact["use_cost"] = raw.useCost;
        fact["placement_scope"] = TowerDefenseManager.GetMapFeature()?.isPlantColumn == true ? "column" : "cell_or_entity";
        fact["cost_status"] = card.Cost.HasValue ? "known" : "unknown";
        fact["remaining_lifetime_seconds"] = raw.aliveTime == -1 ? null : JsonValue.Create(Math.Max(0, raw.aliveTime - raw.aliveTimer));
        fact["pending_use"] = raw.HasMeta("packet_pending_plant");
        fact["id_lifetime"] = "battle_native_instance_generation_until_consumption_expiry_or_removal";
    }

    private JsonObject UseOneUseCard(long cardId, Vector2I pos, ref bool actionStarted, TowerDefenseCharacter? target = null)
    {
        if (!_oneUseCards.TryGetValue(cardId, out var card) || !LivePacket(card.Raw)
            || PacketIdentity(card.Raw, card.Identity.Source) != card.Identity || !PacketAvailable(card.Raw, PacketCost(card.Raw)))
            throw new InvalidOperationException("One-use card expired, changed or is unavailable; observe again.");
        var raw = card.Raw; var pick = TowerDefenseManager.Instance.GetPacketPickControl();
        if (!Valid(pick)) throw new InvalidOperationException("Native card picker is not ready.");
        if (Valid(pick.packetPick) && pick.packetPick != raw)
            throw new InvalidOperationException("A different native card is selected; do not replace another owner's selection.");
        if (!Valid(pick.packetPick) && pick.IsPicking())
            throw new InvalidOperationException("Another native tool is selected.");
        bool columnMode = TowerDefenseManager.GetMapFeature()?.isPlantColumn == true;
        if (columnMode && target is not null) throw new InvalidOperationException("Column mode selects its native targets by column; use plant.");
        if (target is not null && !TargetAllowed(raw.config, target)) throw new InvalidOperationException("Native target is no longer eligible.");
        if (columnMode && Input.IsActionPressed("Press")) throw new InvalidOperationException("Player confirm input is held.");
        long before = raw.Get("ModUseSequence").AsInt64();
        string content = raw.config.saveKey;
        bool selectionReleased;
        try
        {
            actionStarted = true;
            if (target is not null)
            {
                if (target is TowerDefenseZombie) raw.PlantOnZombie(target, raw.config.GetHypnoses()); else raw.PlantOnPlant(target);
            }
            else
            {
                if (pick.packetPick != raw) raw.Pressed();
                if (columnMode)
                {
                    // The native column branch uses IsConfirmInput even with modConfirm.
                    // This is a transient game action, never desktop input or a rule edit.
                    Input.ActionPress("Press");
                    if (Global.Instance.isMobile) Input.ActionRelease("Press");
                }
                if (pick.packetPick == raw && raw.select)
                // Native click -> picker confirmation -> Plant -> CompleteUse.
                // start=false is native for free drops/belts; never rewrite it.
                pick.Call("ProcessPacketPickCore", TowerDefenseManager.GetMapCell(pos), pos,
                    TowerDefenseManager.Instance.GetMapCellPosCenter(pos), true);
            }
        }
        finally
        {
            if (columnMode) Input.ActionRelease("Press");
            if (Valid(pick) && pick.packetPick == raw) pick.PacketPickRelease();
            selectionReleased = Valid(pick) && !Valid(pick.packetPick) && (!Valid(raw) || !raw.select);
            if (columnMode && Input.IsActionPressed("Press")) selectionReleased = false;
            if (!selectionReleased) throw new InvalidOperationException("Native card selection release is unconfirmed; do not replay.");
        }
        bool readable = Valid(raw);
        long? afterSequence = readable ? raw.Get("ModUseSequence").AsInt64() : null;
        bool committed = afterSequence.HasValue && afterSequence.Value > before;
        bool pending = readable && raw.HasMeta("packet_pending_plant");
        bool known = readable && !pending;
        return new JsonObject { ["ok"] = committed && known, ["outcome"] = !known ? "unknown_observe_before_retry" : committed ? "Committed" : "rejected",
            ["card_id"] = cardId, ["source"] = card.Identity.Source, ["native_object_id"] = card.Identity.Instance.ToString(),
            ["native_generation"] = card.Identity.Lifetime, ["native_use_sequence_before"] = before, ["native_use_sequence_after"] = JsonValue.Create(afterSequence),
            ["native_commit_confirmed"] = committed, ["selection_released"] = selectionReleased,
            ["content_id"] = content, ["state"] = Observe() };
    }
}
