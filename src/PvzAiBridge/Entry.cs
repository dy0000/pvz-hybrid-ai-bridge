using System.Diagnostics;
using System.Text.Json.Nodes;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace PvzAiBridge;

public sealed partial class Entry : IXWModRuntimeEntry
{
    private const string BridgeVersion = "0.5.16";
    private XWModRuntimeContext _context = null!;
    private SceneTree _tree = null!;
    private readonly LocalTransport _transport = new();
    private readonly StepClock _clock = new();
    private readonly ControlMode _mode = new();
    private MenuNavigation _menus = null!;
    private bool? _previousBackground;
    private readonly Dictionary<string, (string Input, JsonObject Output)> _receipts = new();
    private readonly Queue<string> _receiptOrder = new();
    private readonly Dictionary<string, (TowerDefenseSunBase Sun, int Lease)> _observedSuns = new();
    private readonly Dictionary<long, TowerDefenseCharacter> _observedCharacters = new();
    private long _observation;
    private Request? _pending;
    private Task<XWModCommandResult<XWModCardUseResult>>? _cardTask;
    private string _pendingCardContent = "";
    private long? _owner;
    private long _receiptConnection;
    private string _battle = "";
    private bool _pauseOwned, _f8, _f9, _f10, _playerLock, _shutdown;
    private long _pendingSince;
    private string _notice = "Mod loaded; waiting for AI connection.";
    private bool _autoCollect;
    private ulong _nextCollection;
    private int _autoCollectionStarted;

    public void Initialize(XWModRuntimeContext context)
    {
        _context = context;
        if (Global.Instance is not null && !Global.Instance.version.StartsWith("0.31.0", StringComparison.Ordinal))
            throw new NotSupportedException("This bridge is built for PVZ hybrid remake 0.31.0.");
        _tree = (SceneTree)Engine.GetMainLoop();
        _menus = new MenuNavigation(_tree);
        _transport.Start(ProjectSettings.GetSetting("pvz_ai/port", 47632).AsInt32());
        var configDir = ProjectSettings.GlobalizePath("user://AiBridge");
        Directory.CreateDirectory(configDir);
        System.IO.File.WriteAllText(Path.Combine(configDir, "connection.json"), new JsonObject {
            ["host"] = "127.0.0.1", ["port"] = _transport.Port, ["token"] = _transport.Token,
            ["pid"] = System.Environment.ProcessId, ["version"] = BridgeVersion,
            ["instance"] = ProjectSettings.GetSetting("pvz_ai/instance", "unspecified").AsString(),
            ["game_executable"] = OS.GetExecutablePath(), ["user_data_dir"] = ProjectSettings.GlobalizePath("user://")
        }.ToJsonString());
        _tree.ProcessFrame += Process;
        _tree.PhysicsFrame += Physics;
        context.Lifetime.Track(Shutdown);
        context.Log("AI bridge available on 127.0.0.1:" + _transport.Port + "; user data: " + ProjectSettings.GlobalizePath("user://") + "; F8 stop, F9 hand back.");
    }

    public void OnAllModsLoaded() { }

    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        // Disconnecting never resumes a battle unexpectedly.
        Stop("Bridge unloaded.");
        _tree.ProcessFrame -= Process;
        _tree.PhysicsFrame -= Physics;
        if (Valid(_streamLayer)) _streamLayer!.QueueFree();
        _transport.Dispose();
    }

    private bool Battle(out XWModBattleSnapshot snapshot) => _context.Game.TryGetBattle(out snapshot!);
    private bool Running => Valid(TowerDefenseManager.CurrentControl) && TowerDefenseManager.CurrentControl.isGameRunning;
    private static bool Valid(GodotObject? obj) => obj is not null && GodotObject.IsInstanceValid(obj);

    // The bank node remains visible while its panel is off-screen during NPC
    // dialogue. Use the game's own enter/exit animation state, as its UI does.
    internal static bool SelectionOpen(TowerDefenseBattleFeaturePacketBank? feature) =>
        Valid(feature) && Valid(feature!.packetBankData) && Valid(feature.packetBank) && Valid(feature.packetBank.seedBank)
        && feature.packetBank.IsVisibleInTree() && feature.Get("_packetBankEntered").AsBool()
        && !feature.Get("_packetBankExiting").AsBool() && TowerDefenseManager.CurrentControl?.isView != true;

    private void Process()
    {
        try
        {
            _transport.Poll();
            bool f8 = Input.IsPhysicalKeyPressed(Key.F8), f9 = Input.IsPhysicalKeyPressed(Key.F9), f10 = Input.IsPhysicalKeyPressed(Key.F10);
            if (f8 && !_f8) { _playerLock = true; Stop("Stopped by F8; F9 returns control; F10 allows AI again."); }
            if (f9 && !_f9) { _playerLock = true; HandBack(); }
            if (f10 && !_f10) { _playerLock = false; _notice = "Player allowed AI attachment again."; }
            _f8 = f8; _f9 = f9; _f10 = f10;
            while (_transport.Disconnected.TryDequeue(out var connection))
                if (_owner == connection) Stop("AI disconnected; control released.", pause: !_mode.Realtime);

            var battle = Battle(out var b) ? b.SessionId.ToString() : "";
            if (b is not null) CaptureFreshPolicyClock(battle, b.SimulationSeconds, TowerDefenseManager.CurrentControl.isInit);
            if (battle != _battle)
            {
                CancelPlan("battle_changed"); _usedPlanIds.Clear(); _planOutcomeUnknown = false;
                _autoCollect = false; _autoCollectionStarted = 0;
                _autoCollectCoins = false; _coinCollectionsStarted = 0;
                CancelPending("Battle changed; old command cancelled.");
                // Clear only a pause owned by this bridge, not a user pause dialog.
                if (_pauseOwned && !ModalPause()) _tree.Paused = false;
                _pauseOwned = false;
                _battle = battle;
            }
            if (_mode.ShouldAutoPause(_owner.HasValue, b?.Running == true, _clock.Active, _pauseOwned, Global.IsMultiplayerMode))
                Pause();
            if (_pending is not null && (Global.IsMultiplayerMode || ModalPause()
                || (!Running && _cardTask?.IsCompleted != true && !TutorialAllows("plant"))))
                CancelPending("Battle stopped or a game pause dialog interrupted the action.");
            if (_pending is not null && Stopwatch.GetElapsedTime(_pendingSince).TotalSeconds > 30)
                CancelPending("Action timed out; observe before issuing a new action. Do not replay blindly.");

            if (_cardTask?.IsCompleted == true)
            {
                var request = _pending!;
                var task = _cardTask;
                _cardTask = null; _pending = null;
                try
                {
                    var result = task.GetAwaiter().GetResult();
                    bool committed = result.Succeeded && result.Value == XWModCardUseResult.Committed;
                    Complete(request, new JsonObject {
                        ["ok"] = committed, ["outcome"] = result.Value.ToString(),
                        ["command_status"] = result.Status.ToString(), ["diagnostic"] = result.Diagnostic,
                        ["content_id"] = _pendingCardContent,
                        ["state"] = Observe()
                    });
                }
                catch (Exception ex) { Fail(request, ex.Message, "unknown"); }
            }
            if (_pending is null && _transport.Pending.TryDequeue(out var next)) Dispatch(next);
            // Explicit, battle-scoped convenience action. All pickups use the
            // game's normal animation and credit path on its main thread.
            if (_autoCollect && _owner.HasValue && _mode.Realtime && (Running || TutorialAllows("collect"))
                && !_tree.Paused && !ModalPause() && !Global.IsMultiplayerMode
                && !TowerDefenseManager.CurrentControl.isGameFail && Time.GetTicksMsec() >= _nextCollection)
            {
                _nextCollection = Time.GetTicksMsec() + 100;
                foreach (var sun in Suns().Where(s => !s.isCollect && s.IsInsideTree()).Take(128).ToArray())
                {
                    sun.Collection();
                    _autoCollectionStarted++;
                }
            }
            if (_autoCollectCoins && _owner.HasValue && _mode.Realtime && Running && !_tree.Paused && !ModalPause()
                && !Global.IsMultiplayerMode && !TowerDefenseManager.CurrentControl.isGameFail && Time.GetTicksMsec() >= _nextCoinCollection)
            {
                _nextCoinCollection = Time.GetTicksMsec() + 100;
                foreach (var coin in Coins().Where(c => !c.isCollect).Take(128).ToArray())
                { coin.Collection(); _coinCollectionsStarted++; }
            }
            TickPlan();
            try { TickLive(); } catch (Exception ex) { _telemetryError = ex.GetBaseException().Message; }
        }
        catch (Exception ex)
        {
            _notice = "Bridge error: " + ex.GetBaseException().Message;
            CancelPlan("bridge_error");
            CancelPending(_notice);
            if (!_mode.Realtime && Running && !Global.IsMultiplayerMode) Pause();
        }
    }

    private void Physics()
    {
        if (!_clock.Active) return;
        try
        {
            if (!Running || ModalPause() || Global.IsMultiplayerMode)
            {
                CancelPending("Step interrupted by a game state change.");
                return;
            }
            if (!_clock.BeforePhysicsFrame()) return;
            Pause();
            var request = _pending!;
            _pending = null;
            Complete(request, new JsonObject { ["ok"] = true, ["frames_advanced"] = _clock.Completed, ["state"] = Observe() });
        }
        catch (Exception ex) { CancelPending(ex.GetBaseException().Message); }
    }

    private bool ModalPause() => Valid(TowerDefenseManager.CurrentControl) && TowerDefenseManager.CurrentControl.waitPause;

    private void Pause()
    {
        // Do not claim another system's pause; it must be closed by the player.
        if (!_tree.Paused) { _tree.Paused = true; _pauseOwned = true; }
    }

    private void Stop(string reason, bool pause = true)
    {
        _flow.Stop("control_released"); _intentions = new JsonArray(); _thinking = false; _intentExpiry = 0;
        CancelPlan("control_released");
        _autoCollect = false; _autoCollectCoins = false;
        CancelPending(reason);
        if (pause && Running && !Global.IsMultiplayerMode) Pause();
        _owner = null;
        RestoreBackground();
        _notice = reason;
        _liveEvents.Add("control_released", _battle, "AI 已交还控制。");
        _nextStreamWrite = 0;
        try { TickStream(); } catch (Exception ex) { _telemetryError = ex.GetBaseException().Message; }
    }

    private void HandBack()
    {
        Stop("AI detached; control returned to the player.", pause: false);
        if (_pauseOwned && !ModalPause()) _tree.Paused = false;
        _pauseOwned = false;
    }

    private void CancelPending(string reason)
    {
        _clock.Cancel();
        _cardTask = null;
        if (_pending is not null)
        {
            var request = _pending; _pending = null;
            if (!_mode.Realtime && Running && !Global.IsMultiplayerMode) Pause();
            Fail(request, reason, "interrupted_observe_before_retry");
        }
    }

    private void Dispatch(Request request)
    {
        bool actionStarted = false;
        try
        {
            var body = request.Body;
            var id = Text(body, "id");
            if (id.Length is < 1 or > 128) throw new ArgumentException("id must contain 1..128 characters.");
            if (_receiptConnection != request.Connection)
            {
                _receiptConnection = request.Connection; _receipts.Clear(); _receiptOrder.Clear();
            }
            if (_receipts.TryGetValue(id, out var cached))
            {
                if (cached.Input != body.ToJsonString())
                {
                    request.Completion.TrySetResult(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "Request ID reused with different arguments." });
                    return;
                }
                request.Completion.TrySetResult((JsonObject)cached.Output.DeepClone());
                return;
            }
            string op = Text(body, "op");
            if (op == "capabilities") { Complete(request, Capabilities()); return; }
            if (op == "menu") { Complete(request, new JsonObject { ["ok"] = true, ["menu"] = _menus.Observe() }); return; }
            if (op == "catalog") { Complete(request, Catalog()); return; }
            if (op == "card_info") { Complete(request, CardInfo(body)); return; }
            if (op == "shop_observe") { Complete(request, ShopObserve()); return; }
            if (op == "shop_receipt") { Complete(request, new JsonObject { ["ok"] = true, ["transaction"] = Purchases.Get(Text(body, "transaction_id")) }); return; }
            if (op is "observe" or "observe_delta") { Complete(request, ObservationResponse(request, op == "observe_delta")); return; }
            if (op == "events") { Complete(request, new JsonObject { ["ok"] = true, ["feed"] = _liveEvents.Read(body["after_event_id"]?.GetValue<long>() ?? 0, body["limit"]?.GetValue<int>() ?? 64, body["narration_only"]?.GetValue<bool>() ?? false) }); return; }
            if (op == "stream_status") { Complete(request, new JsonObject { ["ok"] = true, ["status"] = StreamStatus() }); return; }
            if (op == "capture_frame") { Complete(request, CaptureFrame(body)); return; }
            if (op == "flow_status") { Complete(request, new JsonObject { ["ok"] = true, ["flow"] = _flow.Snapshot(NowMs) }); return; }
            if (op == "plant_plan_status")
            {
                long after = body["after_event_id"]?.GetValue<long>() ?? 0;
                if (after < 0) throw new ArgumentException("after_event_id must be nonnegative.");
                Complete(request, new JsonObject { ["ok"] = true, ["plan"] = PlanState(after) }); return;
            }
            if (op == "attach")
            {
                if (_playerLock) throw new InvalidOperationException("Player stopped AI control. Only the player can re-arm it with F10 in the game window.");
                if (Global.IsMultiplayerMode) throw new InvalidOperationException("Single-player only.");
                if (_owner.HasValue && _owner != request.Connection) throw new InvalidOperationException("Another AI owns control.");
                _mode.Select(Text(body, "mode"));
                if (!_mode.Realtime) CancelPlan("mode_changed");
                _owner = request.Connection;
                if (_mode.Realtime)
                {
                    if (_pauseOwned && !ModalPause()) { _tree.Paused = false; _pauseOwned = false; }
                    if (body["background"]?.GetValue<bool>() != false && Valid(GameSaveManager.Instance))
                    {
                        _previousBackground ??= GameSaveManager.Instance.GetConfigValue("Backgrounder").AsBool();
                        GameSaveManager.Instance.SetConfigValue("Backgrounder", true);
                    }
                }
                else { RestoreBackground(); if (Running) Pause(); }
                _notice = "AI attached: " + _mode.Name + ". F8 stops; F9 returns control.";
                _liveEvents.Add("control_acquired", _battle, "AI 已接管游戏。");
                Complete(request, new JsonObject { ["ok"] = true, ["state"] = Observe() }); return;
            }
            if (op == "stop") { Stop("Stopped by client; battle left paused."); Complete(request, new JsonObject { ["ok"] = true }); return; }
            if (_owner != request.Connection) throw new InvalidOperationException("Call attach on this connection first.");
            if (op is "open_shop" or "close_shop" or "shop_navigate" or "shop_prepare" or "shop_confirm" or "shop_cancel" or "shop_budget" or "shop_dismiss")
            {
                if (Global.IsMultiplayerMode) throw new InvalidOperationException("Single-player only.");
                Complete(request, ShopAction(op, body, ref actionStarted)); return;
            }
            if (op == "stream_update") { UpdateStream(body); Complete(request, new JsonObject { ["ok"] = true, ["status"] = StreamStatus() }); return; }
            if (op == "flow_start")
            {
                _flow.Start(Integer(body, "max_levels"), body["max_retries"]?.GetValue<int>() ?? 0, body["max_seconds"]?.GetValue<int>() ?? 1800, NowMs, _battle);
                Complete(request, new JsonObject { ["ok"] = true, ["flow"] = _flow.Snapshot(NowMs) }); return;
            }
            if (op == "flow_stop") { _flow.Stop("stopped_by_client"); Complete(request, new JsonObject { ["ok"] = true, ["flow"] = _flow.Snapshot(NowMs) }); return; }
            if (op == "menu_action")
            {
                if (Global.IsMultiplayerMode) throw new InvalidOperationException("Single-player only.");
                if (Text(body, "action_id") == "open_shop") PreparationReady();
                _flow.Authorize(Text(body, "action_id"), NowMs);
                if (Text(body, "action_id") != "resume") CancelPlan("navigation_requested");
                actionStarted = true;
                _menus.Act(Text(body, "menu_id"), Text(body, "action_id"));
                _flow.Requested(Text(body, "action_id"), NowMs);
                Complete(request, new JsonObject { ["ok"] = true, ["outcome"] = "navigation_requested" }); return;
            }
            if (op == "detach")
            {
                if ((body["resume"]?.GetValue<bool>() ?? _mode.Realtime)) HandBack(); else Stop("AI detached; battle left paused. F9 resumes.");
                Complete(request, new JsonObject { ["ok"] = true }); return;
            }
            if (Global.IsMultiplayerMode) throw new InvalidOperationException("Single-player only.");
            if (!Battle(out var battle) || Text(body, "battle_id") != battle.SessionId.ToString())
                throw new InvalidOperationException("battle_id is stale or missing. Observe the current battle.");
            if (op == "inspect_cell") { Complete(request, new JsonObject { ["ok"] = true, ["cell"] = InspectCell(Cell(body)) }); return; }
            if (op == "placement_check") { Complete(request, new JsonObject { ["ok"] = true, ["placement"] = PlacementCheck(body["card_id"]!.GetValue<long>(), Cell(body)) }); return; }
            if (op is "quiz_configure" or "quiz_refresh_row" or "quiz_prepare" or "quiz_confirm" or "quiz_cancel")
            { Complete(request, QuizAction(op, body, ref actionStarted)); return; }

            if (op == "cancel_plant_plan")
            {
                if (_plan is null || Text(body, "plan_id") != _plan.Id) throw new InvalidOperationException("Plan ID is stale; read plant_plan_status.");
                CancelPlan("cancelled");
                Complete(request, new JsonObject { ["ok"] = true, ["plan"] = PlanState(0) }); return;
            }
            if (op == "set_plant_plan")
            {
                if (!LocalDecisionsEnabled) throw new InvalidOperationException("Local planting decisions are temporarily disabled in the sandbox configuration.");
                if (!_mode.Realtime || _playerLock) throw new InvalidOperationException("Plans require realtime AI control.");
                if (_plan?.Active == true || _planTask is not null || _planOutcomeUnknown) throw new InvalidOperationException("Cancel/resolve the current plan first; an unknown native result requires a new battle.");
                if (TowerDefenseManager.CurrentControl.HasBattleAwarded || TowerDefenseManager.CurrentControl.isGameFail)
                    throw new InvalidOperationException("Battle already ended.");
                if (TowerDefenseManager.GetMapFeature()?.isPlantColumn == true) throw new InvalidOperationException("Column planting is not supported by the bounded plan budget.");
                var plan = PlantPlan.Parse(body, NowMs, battle.GridSize.X, battle.GridSize.Y,
                    _context.Game.Cards.GetCards().Select(c => c.ContentId).ToHashSet());
                if (_usedPlanIds.Contains(plan.Id)) throw new InvalidOperationException("This plan_id was already submitted in this battle; do not replay it.");
                if (_usedPlanIds.Count >= 128) throw new InvalidOperationException("Plan count limit reached for this battle.");
                _usedPlanIds.Add(plan.Id); _plan = plan; _nextPlanScan = 0; _lastPlanScan = NowMs;
                Complete(request, new JsonObject { ["ok"] = true, ["outcome"] = "plan_accepted", ["plan"] = PlanState(0) }); return;
            }

            if (op == "auto_collect")
            {
                bool enabled = body["enabled"]?.GetValue<bool>() ?? throw new ArgumentException("enabled required.");
                if (enabled && !_mode.Realtime) throw new InvalidOperationException("Auto collection requires realtime mode.");
                _autoCollect = enabled; _nextCollection = 0;
                Complete(request, new JsonObject { ["ok"] = true, ["state"] = Observe() }); return;
            }

            if (op == "choose_card")
            {
                PreparationReady();
                if (battle.Running) throw new InvalidOperationException("The battle already started.");
                var feature = TowerDefenseManager.Instance.GetPacketBankFeature();
                if (!SelectionOpen(feature))
                    throw new InvalidOperationException("Card selection is not open.");
                var key = Text(body, "content_id");
                if (!feature.packetBankData.GetUnlockPacket().Contains(key)) throw new InvalidOperationException("Card is not unlocked in this selection.");
                if (!Valid(feature.packetBank.seedBank) || feature.packetBank.seedBank.HasPacket(key))
                    throw new InvalidOperationException("Card is already selected or seed bank is unavailable.");
                if (!feature.packetBank.seedBank.CanAddPacket()) throw new InvalidOperationException("Selection is full.");
                actionStarted = true;
                feature.PacketListChoose(new Godot.Collections.Array { key });
                Complete(request, new JsonObject { ["ok"] = true, ["outcome"] = "selection_requested", ["state"] = Observe() }); return;
            }
            if (op == "start_battle")
            {
                PreparationReady();
                var feature = TowerDefenseManager.Instance.GetPacketBankFeature();
                if (battle.Running || !SelectionOpen(feature))
                    throw new InvalidOperationException("Card selection is not open.");
                if (feature.packetBank.seedBank.packetNum <= 0) throw new InvalidOperationException("Select at least one card first.");
                actionStarted = true;
                feature.RockButtonPressed();
                Complete(request, new JsonObject { ["ok"] = true, ["outcome"] = "battle_start_requested" }); return;
            }
            if (op is "deselect_card" or "replace_card") { Complete(request, ChangeSelection(body, ref actionStarted)); return; }
            if (op == "open_pause_menu")
            {
                PreparationReady();
                if (_plan?.Active == true || _planTask is not null || _planOutcomeUnknown)
                    throw new InvalidOperationException("Resolve the active planting plan before opening the menu.");
                Complete(request, GameplayAction(op, body, ref actionStarted)); return;
            }
            bool tutorialAction = !battle.Running && TutorialAllows(op == "legal_cells" ? "plant" : op);
            if (!battle.Running && !tutorialAction) throw new InvalidOperationException("Enter a playable battle or an actionable tutorial step first.");
            _mode.RequireActionState(_tree.Paused, _pauseOwned, ModalPause());
            if (op is "plant" or "use_target" or "shovel" or "fire" or "break_vase" or "move_plant" or "hammer_hit" or "slot_spin" or "gem_action" or "entity_interact" or "slide_cell" or "open_pause_menu" && (_plan?.Active == true || _planTask is not null || _planOutcomeUnknown))
                throw new InvalidOperationException("Cancel the plant plan and wait for in_flight=false before manual combat actions.");
            if (op is "collect_coins" or "auto_collect_coins" or "break_vase" or "move_plant" or "hammer_hit" or "slot_spin" or "gem_action" or "open_pause_menu" or "entity_interact" or "slide_cell" or "set_native_speedup")
            { Complete(request, GameplayAction(op, body, ref actionStarted)); return; }
            switch (op)
            {
                case "use_target":
                    Complete(request, UseTarget(body, ref actionStarted)); return;
                case "step":
                    if (_mode.Realtime) throw new InvalidOperationException("Realtime mode runs continuously. Observe again instead of stepping.");
                    _clock.Start(Integer(body, "frames"));
                    BeginPending(request);
                    _tree.Paused = false;
                    return;
                case "plant":
                    var cell = Cell(body);
                    long cardId = body["card_id"]?.GetValue<long>() ?? throw new ArgumentException("card_id required.");
                    var card = ReadCards().FirstOrDefault(c => c.Handle.Id == cardId)
                        ?? throw new InvalidOperationException("Card no longer exists; observe again.");
                    if (tutorialAction)
                    {
                        var tutorialCard = CardConfig(card);
                        if (!Valid(tutorialCard) || !Valid(tutorialCard.characterConfig)
                            || !TutorialAllows("plant", tutorialCard.characterConfig.name))
                            throw new InvalidOperationException("This plant is not required by the current tutorial step.");
                    }
                    if (!card.Available) {
                        Complete(request, new JsonObject { ["ok"] = false, ["outcome"] = "rejected", ["error"] = "Card unavailable; see placement diagnostics.", ["placement"] = PlacementCheck(cardId, cell) }); return;
                    }
                    if (cardId < 0) { Complete(request, UseOneUseCard(cardId, cell, ref actionStarted)); return; }
                    BeginPending(request);
                    _pendingCardContent = card.ContentId;
                    actionStarted = true;
                    _cardTask = _context.Game.Cards.Use(card.Handle, cell.X, cell.Y);
                    return;
                case "fire":
                    long entityId = body["entity_id"]?.GetValue<long>() ?? throw new ArgumentException("entity_id required.");
                    if (!_observedCharacters.TryGetValue(entityId, out var entity) || !Valid(entity) || entity.die || !entity.IsInsideTree())
                        throw new InvalidOperationException("Cannon no longer exists; observe again.");
                    var cannon = entity.componentManager?.GetRuntime<CannonComponent>();
                    if (cannon is null || !cannon.CanFire()) throw new InvalidOperationException("Cannon is not ready.");
                    var target = TowerDefenseManager.Instance.GetMapCellPosCenter(Cell(body));
                    actionStarted = true;
                    cannon.FireAt(target);
                    bool fireConfirmed = !cannon.canFire && Valid(cannon.sprite) && cannon.sprite.clip == cannon.fireAnimeClips;
                    Complete(request, new JsonObject { ["ok"] = fireConfirmed, ["outcome"] = fireConfirmed ? "firing" : "unknown_observe_before_retry",
                        ["entity_id"] = entityId, ["native_cannon_fire_state_verified"] = fireConfirmed, ["state"] = Observe() }); return;
                case "legal_cells":
                    long queryId = body["card_id"]?.GetValue<long>() ?? throw new ArgumentException("card_id required.");
                    var queryCard = ReadCards().FirstOrDefault(c => c.Handle.Id == queryId) ?? throw new InvalidOperationException("Unknown card.");
                    var config = CardConfig(queryCard);
                    var legal = new JsonArray();
                    int skipped = 0;
                    bool enumerate = queryCard.Available && Valid(config) && Valid(config.characterConfig);
                    if (enumerate)
                    for (int row = 1; row <= battle.GridSize.Y; row++)
                    for (int column = 1; column <= battle.GridSize.X; column++)
                    {
                        var candidate = TowerDefenseManager.GetMapCell(new Vector2I(column, row));
                        if (!CellRulesReady(candidate)) { skipped++; continue; }
                        if (candidate.CanPacketPlant(config, noLimit: false, isCheck: true))
                            legal.Add(new JsonObject { ["column"] = column, ["row"] = row });
                    }
                    Complete(request, new JsonObject { ["ok"] = true, ["cells"] = legal,
                        ["battle_id"] = _battle, ["card_available"] = queryCard.Available,
                        ["enumerated"] = enumerate, ["skipped_unready_cells"] = skipped,
                        ["status"] = !enumerate ? "not_enumerated" : skipped > 0 ? "partial" : "ready",
                        ["note"] = "Placement hint only; unavailable cards are not enumerated. Empty cells never proves obstacle absence. Native submission remains authoritative." }); return;
                case "shovel":
                    Complete(request, ShovelConfirmed(body, ref actionStarted)); return;
                case "collect":
                    var ids = body["sun_ids"] as JsonArray ?? throw new ArgumentException("sun_ids required.");
                    if (ids.Count is < 1 or > 128) throw new ArgumentException("Collect 1..128 observed suns.");
                    var wanted = ids.Select(n => n!.GetValue<string>()).ToHashSet();
                    if (wanted.Any(id => !_observedSuns.ContainsKey(id))) throw new InvalidOperationException("Sun ID is from an old observation; use the latest state.");
                    var suns = wanted.Select(id => _observedSuns[id]).ToArray();
                    // All IDs validated before sending any collection action. No hidden all-sun sweep.
                    if (suns.Any(s => !Valid(s.Sun) || !s.Sun.IsInsideTree() || s.Sun.isCollect || s.Sun.die || s.Sun.Get("_leaseVersion").AsInt32() != s.Lease)) throw new InvalidOperationException("A sun disappeared, was recycled, or is already collecting.");
                    actionStarted = true;
                    foreach (var sun in suns) sun.Sun.Collection();
                    Complete(request, new JsonObject { ["ok"] = true, ["outcome"] = "collection_started", ["count"] = suns.Length, ["state"] = Observe() }); return;
                default: throw new ArgumentException("Unknown op: " + op);
            }
        }
        catch (Exception ex)
        {
            if (_pending == request) { _pending = null; _cardTask = null; _clock.Cancel(); }
            Fail(request, ex.GetBaseException().Message, actionStarted ? "unknown_observe_before_retry" : "rejected");
        }
    }

    private void BeginPending(Request request) { _pending = request; _pendingSince = Stopwatch.GetTimestamp(); }
    private static string Text(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? "";
    private static int Integer(JsonObject obj, string key) => obj[key]?.GetValue<int>() ?? throw new ArgumentException(key + " required.");
    private static Vector2I Cell(JsonObject body)
    {
        var cell = new Vector2I(Integer(body, "column"), Integer(body, "row"));
        if (!TowerDefenseManager.Instance.CheckMapGridPosIn(cell)) throw new ArgumentException("Grid coordinates are 1-based and must be inside this map.");
        return cell;
    }

    private IEnumerable<TowerDefenseSunBase> Suns() => _tree.GetNodesInGroup(TowerDefenseSunBase.SaveGroupName)
        .OfType<TowerDefenseSunBase>().Where(s => Valid(s) && !s.IsQueuedForDeletion() && !s.die);

    private JsonObject Observe()
    {
        var started = Stopwatch.GetTimestamp();
        _observation++;
        _observedSuns.Clear();
        _observedCharacters.Clear();
        _observedCoins.Clear();
        var state = new JsonObject {
            ["observation_id"] = _observation,
            ["captured_at_unix_ms"] = (long)(Time.GetUnixTimeFromSystem() * 1000),
            ["bridge_version"] = BridgeVersion, ["game_version"] = Global.Instance.version, ["mode"] = _mode.Name,
            ["scene"] = SceneManager.CurrentScene, ["paused"] = _tree.Paused,
            ["ai_attached"] = _owner.HasValue, ["ai_owns_pause"] = _pauseOwned,
            ["control_locked_by_player"] = _playerLock,
            ["auto_collect"] = _autoCollect, ["auto_collection_started"] = _autoCollectionStarted,
            ["plant_plan"] = PlanState(),
            ["local_decisions_enabled"] = LocalDecisionsEnabled,
            ["event_cursor"] = _liveEvents.Cursor, ["event_stream_id"] = _liveEvents.StreamId,
            ["flow"] = _flow.Snapshot(NowMs),
            ["notice"] = _notice, ["transport_error"] = _transport.Failure,
            ["physics_frame"] = Engine.GetPhysicsFrames(), ["physics_ticks_per_second"] = Engine.PhysicsTicksPerSecond,
            ["multiplayer"] = Global.IsMultiplayerMode, ["battle_id"] = null
        };
        state["environment"] = ObservationFacts.Environment(null, false, null);
        state["selection_capacity"] = null;
        if (!Battle(out var battle)) {
            state["phase"] = SceneManager.CurrentScene == "AwardSettlement" ? "settlement" : SceneManager.CurrentScene;
            state["level_completed"] = SceneManager.CurrentScene == "AwardSettlement";
            return state;
        }
        state["battle_id"] = battle.SessionId.ToString();
        state["running"] = battle.Running;
        state["simulation_seconds"] = battle.SimulationSeconds;
        state["columns"] = battle.GridSize.X; state["rows"] = battle.GridSize.Y;
        state["modal_pause"] = ModalPause();
        var manager = TowerDefenseManager.Instance;
        var control = TowerDefenseManager.CurrentControl;
        state["lost"] = control.isGameFail;
        state["battle_awarded"] = control.HasBattleAwarded;
        state["initializing"] = control.isInit;
        state["viewing_map"] = control.isView;
        var tutorial = ObserveTutorial();
        state["tutorial"] = tutorial;
        var shovelStatus = ObserveShovel(out _);
        state["shovel_enabled"] = shovelStatus["enabled"]!.GetValue<bool>();
        state["shovel_status"] = shovelStatus;
        bool tutorialReady = InteractiveTutorial() is not null;
        state["phase"] = control.isGameFail ? "lost" : control.HasBattleAwarded ? "victory_reward_pending" : tutorialReady ? "tutorial" : control.isInit ? "initializing" : battle.Running ? "playing" : "preparation_or_result";
        state["level_completed"] = false;
        state["level_mode"] = Global.Instance.enterLevelMode;
        state["level_group"] = Global.Instance.currentLevelChoose;
        state["chapter_index"] = Global.Instance.currentChapterId;
        state["level_index"] = Global.Instance.currentLevelId;
        state["environment"] = ObserveEnvironment();
        state["mechanics"] = ObserveMechanics();
        AddMapObservation(state, battle.GridSize.X, battle.GridSize.Y);
        // The native selection screen is part of initialization. Report it
        // before returning early for incomplete pre-battle gameplay objects.
        var pick = manager.GetPacketBankFeature();
        bool selecting = SelectionOpen(pick) && !battle.Running;
        state["selecting_cards"] = selecting;
        CapturePolicyClock(state, battle.SimulationSeconds);
        if (selecting)
        {
            state["phase"] = "selecting_cards";
            state["selection_capacity"] = JsonValue.Create(SelectionCapacity(pick));
            state["available_choices"] = new JsonArray(pick!.packetBankData.GetUnlockPacket().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            state["chosen_cards"] = new JsonArray(pick.packetBank.seedBank.packetList
                .Where(p => Valid(p) && Valid(p.config)).Select(p => (JsonNode?)JsonValue.Create(p.config.saveKey)).ToArray());
            state["chosen_card_details"] = new JsonArray(pick.packetBank.seedBank.packetList
                .Where(p => Valid(p) && Valid(p.config)).Select(p => (JsonNode?)new JsonObject { ["content_id"] = p.config.saveKey, ["locked"] = p.@lock }).ToArray());
            state["selection_edit_enabled"] = true;
            state["selection_can_add"] = pick.packetBank.seedBank.CanAddPacket();
            state["selection_can_start"] = pick.packetBank.seedBank.packetNum > 0;
        }
        // Pre-battle preview objects have no complete gameplay components yet.
        if (control.isInit && !tutorialReady) { state["snapshot_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds; return state; }
        state["sun"] = manager.GetSun();
        var wave = control.featureDictionary.Values.OfType<TowerDefenseBattleFeatureWave>().FirstOrDefault();
        if (wave is not null) state["wave"] = new JsonObject { ["current"] = wave.currentWave, ["final"] = wave.waveFinal, ["next_wave_seconds"] = wave.nextWaveTime, ["timer"] = wave.timer };
        var cards = new JsonArray();
        var nativeCards = ReadCards();
        foreach (var c in nativeCards) {
            var fact = new JsonObject {
            ["id"] = c.Handle.Id, ["content_id"] = c.ContentId, ["cost"] = CardCost(c),
            ["cooldown_remaining"] = c.Remaining, ["cooldown_duration"] = c.Duration,
            ["available"] = c.Available, ["selected"] = c.Selected,
            ["source"] = CardSource(c.Handle.Id)
            };
            AddOneUseMetadata(fact, c.Handle.Id); cards.Add(fact);
        }
        state["cards"] = cards;
        var nativePicker = manager.GetPacketPickControl();
        state["card_selection"] = new JsonObject { ["status"] = !Valid(nativePicker) ? "unknown" : Valid(nativePicker.packetPick) ? "selected" : "clear",
            ["native_object_id"] = Valid(nativePicker?.packetPick) ? nativePicker!.packetPick.GetInstanceId().ToString() : null };
        state["executable_card_ids"] = new JsonArray(cards.OfType<JsonObject>().Where(c => c["available"]?.GetValue<bool>() == true)
            .Select(c => c["id"]!.DeepClone()).ToArray());
        var entities = new JsonArray();
        var policyCannons = new JsonArray(); bool policyCannonsComplete = true;
        var bossBalls = new JsonArray();
        var bossWindows = new JsonArray();
        var energyBeans = new JsonArray();
        bool policyEntitiesComplete = true;
        foreach (var c in manager._GetCleanCharacters())
        {
            // Boss balls are native items, not seed packets. Capture them before
            // the SDK packet/config guard so packetless balls remain visible.
            if (LiveMapObject(c) && c is TowerDefenseItemIceball or TowerDefenseItemFireball && c.camp == TowerDefenseEnum.CHARACTER_CAMP.ZOMBIE && Valid(c.sprite))
                bossBalls.Add(new JsonObject { ["entity_id"] = (long)c.GetInstanceId(), ["kind"] = c is TowerDefenseItemIceball ? "ice" : "fire", ["row"] = c.gridPos.Y, ["phase"] = c.sprite.clip.ToString() });
            bool policyEntity = LiveMapObject(c) && c is TowerDefensePlant or TowerDefenseZombie;
            if (policyEntity && !Valid(c.packet)) policyEntitiesComplete = false;
            if (!Valid(c) || !Valid(c.instance) || !Valid(c.config) || c.componentManager is null
                || !_context.Game.Characters.TryGetHandle(c, out var handle) || !_context.Game.Characters.TryGet(handle, out var s)) {
                if (policyEntity) policyEntitiesComplete = false;
                continue;
            }
            var p = c.GetLogicalGlobalPosition();
            _observedCharacters[handle.Id] = c;
            if(c is TowerDefensePlantEnergyBean bean && LiveMapObject(bean)) {
                bool charging=bean.StateMachine?.GetStateById("plant.energy_bean.charging")?.IsActive==true;
                energyBeans.Add(new JsonObject { ["entity_id"]=handle.Id.ToString(),["key"]="PlantEnergyBean",["row"]=s.Row,["col"]=s.Column,
                    ["camp"]=c.camp==TowerDefenseEnum.CHARACTER_CAMP.PLANT?0:1,["hp"]=s.Hitpoints,["can_run_ability"]=bean.CanRunAbility(),
                    ["variant_state"]=new JsonObject{["EnergyBeanChargeLevel"]=bean.Get("_chargeLevel").AsInt32()},
                    ["main_state"]=new JsonObject{["active_state_ids"]=charging?new JsonArray("plant.energy_bean.charging"):new JsonArray()} });
            }
            if (c is TowerDefenseZombieBossDave boss && LiveMapObject(boss) && boss.camp == TowerDefenseEnum.CHARACTER_CAMP.ZOMBIE && boss.targetRegistrationComponent is not null)
                bossWindows.Add(new JsonObject { ["entity_id"] = handle.Id, ["hp"] = s.Hitpoints,
                    ["damageable"] = !boss.instance.invincible && boss.instance.canBeCollection && boss.targetRegistrationComponent.canProjectileCheck,
                    ["phase"] = Valid(boss.sprite) ? boss.sprite.clip.ToString() : "unknown" });
            var cannon = c.componentManager?.GetRuntime<CannonComponent>();
            if (cannon is not null) {
                var fact = CapturePolicyCannon(c, cannon, handle.Id, s.Row, s.Column, s.Hitpoints);
                if (fact is null) policyCannonsComplete = false; else policyCannons.Add(fact);
            }
            var armor = new JsonArray();
            foreach (var a in _context.Game.Combat.GetArmor(handle)) armor.Add(new JsonObject { ["key"] = a.Key, ["hp"] = a.Hitpoints, ["max_hp"] = a.MaximumHitpoints });
            entities.Add(new JsonObject {
                ["id"] = handle.Id, ["content_id"] = Valid(c.packet) ? c.packet.saveKey : s.ContentKey,
                ["kind"] = c is TowerDefenseZombie ? "zombie" : c is TowerDefensePlant ? "plant" : "other",
                ["camp"] = c.camp.ToString(), ["column"] = s.Column, ["row"] = s.Row,
                ["x"] = p.X, ["y"] = p.Y, ["hp"] = s.Hitpoints,
                ["dead"] = s.Dead, ["removing"] = s.Removing, ["armor"] = armor,
                ["can_fire"] = cannon?.CanFire(), ["cannon_mode"] = cannon?.mode,
                ["interaction"] = EntityInteraction(c)
            });
        }
        state["entities"] = entities;
        state["native_boss_balls"] = bossBalls;
        state["native_energy_beans"] = new JsonObject{["beans"]=energyBeans};
        state["native_boss_windows"] = bossWindows;
        state["native_cannons"] = new JsonObject { ["complete"] = policyCannonsComplete && policyEntitiesComplete, ["cannons"] = policyCannons };
        AddContinuousPolicyObservation(state, nativeCards, policyEntitiesComplete);
        var suns = new JsonArray();
        foreach (var s in Suns())
        {
            string id = _observation + ":" + s.GetInstanceId();
            _observedSuns[id] = (s, s.Get("_leaseVersion").AsInt32());
            suns.Add(new JsonObject {
                ["id"] = id, ["value"] = s.sunNum,
                ["x"] = s.GlobalPosition.X, ["y"] = s.GlobalPosition.Y,
                ["collecting"] = s.isCollect, ["credited"] = s.CollectionCredited
            });
        }
        state["suns"] = suns;
        state["snapshot_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return state;
    }

    private void RestoreBackground()
    {
        if (_previousBackground.HasValue && Valid(GameSaveManager.Instance))
            GameSaveManager.Instance.SetConfigValue("Backgrounder", _previousBackground.Value);
        _previousBackground = null;
    }

    private JsonObject Capabilities() => new() { ["ok"] = true, ["protocol_version"] = 1,
        ["features"] = new JsonObject { ["live_events"] = true, ["narration_events"] = true, ["placement_diagnostics"] = true,
            ["target_card_use"] = true, ["tutorial_actions"] = true, ["shovel_targets"] = true, ["shovel_gate_diagnostics"] = true, ["free_conveyor_cards"] = true,
            ["falling_cards"] = true, ["one_use_card_identity"] = true, ["card_selection_release_receipt"] = true,
            ["gameplay_mechanics"] = true, ["vase_breaking"] = true, ["native_hammer"] = true, ["glove_move"] = true,
            ["slot_machine"] = true, ["gem_match"] = true, ["coin_collection"] = true, ["formation_objectives"] = true,
            ["column_planting"] = true, ["one_use_target_cards"] = true, ["entity_skills"] = true, ["native_drag"] = true,
            ["cell_sliding"] = true, ["quiz_budgeted_entry"] = true, ["semantic_gameplay_dialogs"] = true,
            ["star_rewards"] = true, ["daily_rewards"] = true, ["daily_calendar_navigation"] = true,
            ["trial_levels"] = true, ["equipment_selection"] = true,
            ["portal_observation"] = true, ["survival_observation"] = true,
            ["native_speedup"] = true, ["native_speedup_multiplier"] = true, ["continuous_policy_observation"] = true,
            ["native_environment"] = true, ["map_preview"] = true, ["obstacle_observation"] = true, ["cell_layers_in_observe"] = true,
            ["selection_edit"] = true, ["selection_capacity"] = true, ["shop"] = true, ["persistent_purchase_receipts"] = true,
            ["card_info"] = true, ["compact_observation"] = true, ["delta_observation"] = true, ["executable_card_view"] = true,
            ["capture_frame"] = true, ["bounded_flow"] = true, ["stream_status"] = true, ["stream_update"] = true, ["status_file"] = true },
        ["status_file"] = StatusFile, ["event_stream_id"] = _liveEvents.StreamId,
        ["in_game_panel_enabled"] = InGamePanelEnabled,
        ["local_decisions_enabled"] = LocalDecisionsEnabled,
        ["bridge_version"] = BridgeVersion, ["default_mode"] = "realtime", ["coordinates"] = "1-based column,row",
        ["actions"] = new JsonArray("observe", "observe_delta", "attach", "detach", "stop", "menu", "menu_action", "catalog", "card_info", "plant", "collect", "auto_collect", "set_plant_plan", "plant_plan_status", "cancel_plant_plan", "shovel", "fire", "legal_cells", "choose_card", "deselect_card", "replace_card", "start_battle", "step", "events", "stream_status", "stream_update", "capture_frame", "inspect_cell", "placement_check", "use_target", "flow_start", "flow_status", "flow_stop", "open_shop", "close_shop", "shop_observe", "shop_navigate", "shop_budget", "shop_prepare", "shop_cancel", "shop_confirm", "shop_receipt", "shop_dismiss", "collect_coins", "auto_collect_coins", "break_vase", "move_plant", "hammer_hit", "slot_spin", "gem_action", "open_pause_menu", "entity_interact", "slide_cell", "quiz_configure", "quiz_refresh_row", "quiz_prepare", "quiz_confirm", "quiz_cancel", "set_native_speedup"),
        ["limits"] = new JsonObject { ["single_player"] = true, ["single_connection"] = true, ["max_step_frames"] = 600, ["max_request_bytes"] = 65536,
            ["plan_jobs"] = 32, ["plan_candidates"] = 128, ["plan_total_uses"] = 128, ["plan_ttl_seconds"] = 600, ["plan_scan_ms"] = 50, ["plan_event_history"] = 64 },
        ["notes"] = "AI supplies bounded planting plans; local emergency priority only reorders approved candidates. Realtime runs continuously. Read plan events and wait reasons before replanning. Reward generation is not completed settlement." };

    private JsonObject Catalog()
    {
        var ids = ReadCards().Select(c => c.ContentId).ToHashSet();
        var pick = TowerDefenseManager.Instance?.GetPacketBankFeature();
        if (Valid(pick?.packetBankData)) foreach (var key in pick!.packetBankData.GetUnlockPacket()) ids.Add(key);
        var cards = new JsonArray();
        foreach (var id in ids)
        {
            var c = TowerDefenseManager.GetPacketConfigReadOnly(id);
            if (!Valid(c)) continue;
            cards.Add(new JsonObject { ["content_id"] = id, ["name"] = _tree.Root.Tr(c.name).ToString(),
                ["description"] = _tree.Root.Tr(c.describe).ToString(), ["handbook"] = _tree.Root.Tr(c.handbookDescribe).ToString() });
        }
        return new JsonObject { ["ok"] = true, ["cards"] = cards };
    }

    private void Complete(Request request, JsonObject response)
    {
        string id = Text(request.Body, "id");
        response["id"] = id;
        RecordReceipt(request, response);
        if (Text(request.Body, "op") == "capture_frame") { request.Completion.TrySetResult(response); return; }
        if (!_receipts.ContainsKey(id)) _receiptOrder.Enqueue(id);
        _receipts[id] = (request.Body.ToJsonString(), (JsonObject)response.DeepClone());
        while (_receiptOrder.Count > 256) _receipts.Remove(_receiptOrder.Dequeue());
        request.Completion.TrySetResult(response);
    }
    private void Fail(Request request, string message, string outcome = "rejected") => Complete(request,
        new JsonObject { ["ok"] = false, ["error"] = message, ["outcome"] = outcome });
}

