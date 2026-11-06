using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private CanvasLayer? _streamLayer;
    private PanelContainer? _streamPanel;
    private Label? _streamLabel;
    private JsonArray _intentions = new();
    private string _streamGoal = "", _streamReason = "";
    private string _telemetryError = "";
    private bool _thinking, _streamVisible;
    private long _intentExpiry, _nextStreamWrite, _lastCapture;
    private string LiveDirectory => ProjectSettings.GlobalizePath("user://AiBridge/live");
    private string StatusFile => Path.Combine(LiveDirectory, "status.json");
    private static long UnixMs => (long)(Time.GetUnixTimeFromSystem() * 1000);
    private bool InGamePanelEnabled => ProjectSettings.GetSetting("pvz_ai/in_game_panel_enabled", false).AsBool();

    private JsonObject StreamStatus()
    {
        bool expired = UnixMs >= _intentExpiry;
        var intents = !expired && _owner.HasValue ? (JsonArray)_intentions.DeepClone() : new JsonArray();
        return new JsonObject { ["schema_version"] = 1, ["stream_id"] = _liveEvents.StreamId,
            ["generated_at_unix_ms"] = UnixMs, ["stale_after_ms"] = 2000, ["expires_at_unix_ms"] = _intentExpiry,
            ["battle_id"] = _battle.Length > 0 ? _battle : null, ["scene"] = SceneManager.CurrentScene,
            ["mode"] = _mode.Name, ["ai_attached"] = _owner.HasValue, ["control_locked_by_player"] = _playerLock,
            ["paused"] = _tree.Paused, ["local_decisions_enabled"] = LocalDecisionsEnabled,
            ["thinking"] = !expired && _owner.HasValue && _thinking, ["intentions_expired"] = expired,
            ["goal"] = _streamGoal, ["reason"] = expired ? "" : _streamReason, ["overlay_visible"] = _streamVisible && InGamePanelEnabled,
            ["in_game_panel_enabled"] = InGamePanelEnabled,
            ["intentions"] = intents, ["queue_semantics"] = "declared_intent_order_not_execution_guarantee",
            ["receipt_history"] = new JsonArray(_receiptHistory.Select(r => r.DeepClone()).ToArray()),
            ["recent_events"] = _liveEvents.Read(Math.Max(0, _liveEvents.Cursor - 8), 8, false)["events"]!.DeepClone(),
            ["last_action"] = _lastAction, ["last_failure"] = _lastFailure,
            ["telemetry_error"] = _telemetryError,
            ["wave"] = _liveWave, ["sun"] = Running ? TowerDefenseManager.Instance.GetSun() : null,
            ["flow"] = _flow.Snapshot(NowMs) };
    }

    private void UpdateStream(JsonObject body)
    {
        int ttl = body["ttl_ms"]?.GetValue<int>() ?? 5000;
        if (ttl is < 500 or > 30000) throw new ArgumentException("ttl_ms must be 500..30000.");
        string goal = Text(body, "goal"), reason = Text(body, "reason");
        if (goal.Length > 200 || reason.Length > 300) throw new ArgumentException("Goal/reason too long.");
        var input = body["intents"] as JsonArray ?? new JsonArray();
        if (input.Count > 32) throw new ArgumentException("At most 32 declared intentions.");
        var intents = new JsonArray(); var ids = new HashSet<string>(); long expiry = UnixMs + ttl;
        foreach (var item in input.OfType<JsonObject>())
        {
            string id = Text(item, "id"), content = Text(item, "content_id"), status = Text(item, "status");
            if (id.Length is < 1 or > 64 || !ids.Add(id) || content.Length is < 1 or > 100 || Text(item, "reason").Length > 300)
                throw new ArgumentException("Invalid intent identity/reason.");
            if (status == "") status = "pending";
            if (status is not ("pending" or "waiting" or "submitting")) throw new ArgumentException("Intent status must be pending/waiting/submitting; success comes from game receipts.");
            var pos = Cell(item);
            intents.Add(new JsonObject { ["id"] = id, ["content_id"] = content, ["name"] = NameOf(content),
                ["column"] = pos.X, ["row"] = pos.Y, ["reason"] = Text(item, "reason"), ["status"] = status,
                ["battle_id"] = _battle, ["expires_at_unix_ms"] = expiry, ["source"] = "ai_intent" });
        }
        if (intents.Count != input.Count) throw new ArgumentException("Intent entries must be objects.");
        _intentions = intents; _intentExpiry = expiry; _streamGoal = goal; _streamReason = reason;
        _thinking = body["thinking"]?.GetValue<bool>() ?? false;
        _streamVisible = InGamePanelEnabled && (body["visible"]?.GetValue<bool>() ?? false);
        _nextStreamWrite = 0;
    }

    private void TickStream()
    {
        if (NowMs < _nextStreamWrite) return;
        _nextStreamWrite = NowMs + 250;
        var status = StreamStatus();
        if (_streamLayer is null && _streamVisible && InGamePanelEnabled)
        {
            _streamLayer = new CanvasLayer { Layer = 100, ProcessMode = Node.ProcessModeEnum.Always };
            _tree.Root.AddChild(_streamLayer);
            _streamPanel = new PanelContainer { Position = new Vector2(16, 145), CustomMinimumSize = new Vector2(300, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
            _streamLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(290, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _streamLabel.AddThemeFontSizeOverride("font_size", 16);
            _streamLayer.AddChild(_streamPanel); _streamPanel.AddChild(_streamLabel);
        }
        if (_streamPanel is not null && _streamLabel is not null)
        {
            _streamPanel.Visible = _streamVisible && InGamePanelEnabled;
            string thinking = status["thinking"]!.GetValue<bool>() ? "思考中" : _owner.HasValue ? "操作中" : "已交还控制";
            string scene = SceneManager.CurrentScene switch { "TowerDefense" => "战斗", "MainMenu" => "主菜单", "LevelChoose" => "选关", "AwardSettlement" => "奖励结算", _ => "载入中" };
            var lines = new List<string> { "AI 直播状态 · " + thinking, "场景：" + scene,
                "目标：" + _streamGoal, "最近：" + _lastAction };
            if (_lastFailure.Length > 0) lines.Add("提示：操作受限，等待 AI 检查");
            foreach (var item in status["intentions"]!.AsArray().OfType<JsonObject>().Where(i => Text(i, "status") is "pending" or "waiting" or "submitting").Take(4))
                lines.Add($"待执行 · {item["name"]} 第{item["row"]}行第{item["column"]}列");
            _streamLabel.Text = string.Join("\n", lines);
        }
        Directory.CreateDirectory(LiveDirectory);
        string temp = StatusFile + ".tmp";
        System.IO.File.WriteAllText(temp, status.ToJsonString());
        System.IO.File.Move(temp, StatusFile, true);
    }

    private JsonObject CaptureFrame(JsonObject body)
    {
        int width = body["max_width"]?.GetValue<int>() ?? 1280;
        if (width is < 320 or > 1920) throw new ArgumentException("max_width must be 320..1920.");
        if (NowMs - _lastCapture < 500) throw new InvalidOperationException("Capture rate limit: wait 500 ms.");
        _lastCapture = NowMs;
        var texture = _tree.Root.GetViewport().GetTexture();
        using var pixels = texture.GetImage();
        if (pixels is null || pixels.IsEmpty()) throw new InvalidOperationException("Rendered frame is not available yet.");
        if (pixels.GetWidth() > width) pixels.Resize(width, Math.Max(1, pixels.GetHeight() * width / pixels.GetWidth()));
        string directory = ProjectSettings.GlobalizePath("user://AiBridge/captures"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "frame-" + Guid.NewGuid().ToString("N") + ".png");
        byte[] bytes = pixels.SavePngToBuffer();
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("PNG exceeds 4 MiB; request a smaller max_width.");
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
        { if (file is null) throw new IOException("Cannot save captured frame."); file.StoreBuffer(bytes); }
        return new JsonObject { ["ok"] = true, ["path"] = path, ["mime_type"] = "image/png", ["width"] = pixels.GetWidth(),
            ["height"] = pixels.GetHeight(), ["captured_at_unix_ms"] = UnixMs, ["scene"] = SceneManager.CurrentScene,
            ["battle_id"] = _battle, ["png_base64"] = Convert.ToBase64String(bytes), ["source"] = "game_viewport" };
    }
}
