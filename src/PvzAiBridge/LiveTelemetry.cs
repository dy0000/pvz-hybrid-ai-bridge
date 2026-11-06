using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private readonly LiveJournal _liveEvents = new(() => UnixMs);
    private readonly LiveFlow _flow = new();
    private readonly Dictionary<long, (string Content, int Row)> _livePlants = new();
    private readonly Dictionary<long, bool> _liveCards = new();
    private readonly HashSet<string> _liveZombieTypes = new();
    private HashSet<int> _dangerRows = new();
    private string _liveBattle = "", _liveScene = "";
    private bool _liveLost, _liveAward, _livePaused;
    private int _liveWave = -1;
    private long _nextTelemetry;
    private string _lastAction = "等待操作", _lastFailure = "";
    private readonly Queue<JsonObject> _receiptHistory = new();

    private void TickLive()
    {
        _flow.Observe(_battle, NowMs);
        if (NowMs < _nextTelemetry) return;
        _nextTelemetry = NowMs + 100;
        string scene = SceneManager.CurrentScene;
        if (scene != _liveScene)
        {
            _liveEvents.Add("scene_changed", _battle, scene == "AwardSettlement" ? "已进入奖励结算页面。" : "", new JsonObject { ["scene"] = scene });
            _liveScene = scene;
        }
        if (_liveBattle != _battle)
        {
            _liveBattle = _battle; _livePlants.Clear(); _liveCards.Clear(); _liveZombieTypes.Clear(); _dangerRows.Clear();
            _liveWave = -1; _liveAward = _liveLost = false; _intentions = new JsonArray();
            if (_battle.Length > 0) _liveEvents.Add("battle_entered", _battle, "已进入新的关卡。",
                new JsonObject { ["chapter_index"] = Global.Instance.currentChapterId, ["level_index"] = Global.Instance.currentLevelId });
        }
        if (_tree.Paused != _livePaused)
        {
            _livePaused = _tree.Paused;
            _liveEvents.Add("pause_changed", _battle, _livePaused ? "游戏当前暂停。" : "游戏已恢复运行。", new JsonObject { ["paused"] = _livePaused });
        }
        if (Valid(TowerDefenseManager.CurrentControl) && !TowerDefenseManager.CurrentControl.isInit)
        {
            var c = TowerDefenseManager.CurrentControl;
            if (c.isGameFail && !_liveLost) _liveEvents.Add("battle_lost", _battle, "本关失败，等待决定是否重试。");
            if (c.HasBattleAwarded && !_liveAward) _liveEvents.Add("reward_pending", _battle, "胜利奖励已出现，尚未领取和完成结算。");
            _liveLost = c.isGameFail; _liveAward = c.HasBattleAwarded;
            var wave = c.featureDictionary.Values.OfType<TowerDefenseBattleFeatureWave>().FirstOrDefault();
            if (wave is not null && wave.currentWave != _liveWave)
            {
                _liveWave = wave.currentWave;
                _liveEvents.Add("wave_changed", _battle, _liveWave > 0 ? $"第 {_liveWave} 波开始。" : "", new JsonObject { ["wave"] = _liveWave, ["final"] = wave.waveFinal });
            }
            var plants = new Dictionary<long, (string Content, int Row)>(); var danger = new HashSet<int>();
            foreach (var character in TowerDefenseManager.Instance._GetCleanCharacters())
            {
                if (!Valid(character) || character.die || !_context.Game.Characters.TryGetHandle(character, out var h)
                    || !_context.Game.Characters.TryGet(h, out var s) || s.Dead || s.Removing) continue;
                if (character is TowerDefensePlant) plants[h.Id] = (s.ContentKey, s.Row);
                if (character is TowerDefenseZombie && character.camp == TowerDefenseEnum.CHARACTER_CAMP.ZOMBIE)
                {
                    if (_liveZombieTypes.Add(s.ContentKey)) _liveEvents.Add("zombie_type_seen", _battle, $"出现了{NameOf(s.ContentKey)}。",
                        new JsonObject { ["content_id"] = s.ContentKey, ["name"] = NameOf(s.ContentKey), ["row"] = s.Row });
                    if (TowerDefenseManager.Instance.GetMapGridPos(character.GetLogicalGlobalPosition()).X <= 3) danger.Add(s.Row);
                }
            }
            foreach (var old in _livePlants)
                if (!plants.ContainsKey(old.Key)) _liveEvents.Add("plant_removed", _battle, $"第 {old.Value.Row} 行的{NameOf(old.Value.Content)}已离场。",
                    new JsonObject { ["entity_id"] = old.Key, ["content_id"] = old.Value.Content, ["row"] = old.Value.Row, ["cause"] = "not_observed" });
            _livePlants.Clear(); foreach (var p in plants) _livePlants[p.Key] = p.Value;
            foreach (int row in danger)
                if (!_dangerRows.Contains(row)) _liveEvents.Add("lane_danger", _battle, $"第 {row} 行的僵尸已接近左侧。", new JsonObject { ["row"] = row });
            _dangerRows = danger;
            var liveCards = ReadCards();
            foreach (var card in liveCards)
            {
                if (_liveCards.TryGetValue(card.Handle.Id, out var available) && !available && card.Available)
                    _liveEvents.Add("card_ready", _battle, "", new JsonObject { ["card_id"] = card.Handle.Id, ["content_id"] = card.ContentId, ["name"] = NameOf(card.ContentId) });
                _liveCards[card.Handle.Id] = card.Available;
            }
            var liveCardIds = liveCards.Select(c => c.Handle.Id).ToHashSet();
            foreach (var oldId in _liveCards.Keys.Where(id => !liveCardIds.Contains(id)).ToArray()) _liveCards.Remove(oldId);
        }
        TickStream();
    }

    private string NameOf(string content)
    {
        var config = TowerDefenseManager.GetPacketConfigReadOnly(content);
        return Valid(config) ? _tree.Root.Tr(config.name).ToString() : content;
    }

    private void RecordReceipt(Request request, JsonObject response)
    {
        string op = Text(request.Body, "op");
        if (response["replayed"]?.GetValue<bool>() == true) return;
        if (op is not ("plant" or "use_target" or "shovel" or "fire" or "menu_action" or "choose_card" or "deselect_card" or "replace_card" or "start_battle" or "shop_prepare" or "shop_confirm" or "shop_cancel"
            or "collect_coins" or "break_vase" or "move_plant" or "hammer_hit" or "slot_spin" or "gem_action" or "entity_interact" or "slide_cell"
            or "quiz_configure" or "quiz_refresh_row" or "quiz_prepare" or "quiz_confirm" or "quiz_cancel" or "open_pause_menu")) return;
        bool ok = response["ok"]?.GetValue<bool>() == true;
        string outcome = Text(response, "outcome");
        string kind = outcome == "Submitted" && op is "plant" or "use_target" ? "plant_submitted"
            : !ok ? "action_failed" : op is "plant" or "use_target" ? (outcome == "Committed" ? "plant_committed" : "plant_submitted") : "action_result";
        string content = Text(response, "content_id");
        string name = content.Length > 0 ? NameOf(content) : "植物";
        string speech = kind == "plant_committed" ? (request.Body["row"] is not null ? $"在第 {request.Body["row"]} 行第 {request.Body["column"]} 列种下了{name}。" : $"已使用{name}完成目标操作。")
            : !ok ? "刚才的操作没有确认成功，需要检查原因。" : "";
        var record = new JsonObject { ["request_id"] = Text(request.Body, "id"), ["op"] = op, ["outcome"] = outcome,
            ["ok"] = ok, ["error"] = response["error"]?.DeepClone(), ["intent_id"] = Text(request.Body, "intent_id"),
            ["content_id"] = content, ["name"] = name,
            ["column"] = request.Body["column"]?.DeepClone(), ["row"] = request.Body["row"]?.DeepClone(),
            ["action_id"] = request.Body["action_id"]?.DeepClone(), ["at_unix_ms"] = (long)(Time.GetUnixTimeFromSystem() * 1000) };
        ReceiptProof.Copy(response, record);
        _receiptHistory.Enqueue(record); while (_receiptHistory.Count > 12) _receiptHistory.Dequeue();
        _lastAction = kind == "plant_committed" ? $"已种下{name}" : ok ? op switch {
            "menu_action" => "已提交菜单操作，等待画面变化", "fire" => "已发射加农炮", "shovel" => outcome == "shoveled" ? "已铲除植物" : "原生铲子效果已确认",
            "deselect_card" or "replace_card" => "选卡调整已确认", "shop_confirm" => "原生购买结果已确认",
            "choose_card" => "已提交选卡", "start_battle" => "已请求开始战斗", _ => "操作已提交，等待结果" } : "操作未确认成功";
        _lastFailure = ok ? "" : Text(response, "error");
        _liveEvents.Add(kind, _battle, speech, (JsonObject)record.DeepClone());
        string intent = Text(request.Body, "intent_id");
        if (intent.Length > 0)
            foreach (var item in _intentions.OfType<JsonObject>())
                if (Text(item, "id") == intent) item["status"] = kind == "plant_committed" || ok && outcome is "shoveled" or "shovel_effect_confirmed" ? "committed"
                    : kind == "plant_submitted" || ok ? "submitted" : outcome.Contains("unknown") || outcome.Contains("interrupted") ? "unknown" : "failed";
    }
}
