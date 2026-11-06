using System.Text.Json.Nodes;
using System.Reflection;
using Godot;

namespace PvzAiBridge;

// Only named gameplay/navigation actions are exposed. No arbitrary node calls,
// save editing, settings menus, arbitrary shop commits, or desktop input.
public sealed class MenuNavigation(SceneTree tree)
{
    private static readonly MethodInfo? ProgressRecovery = typeof(GameSaveManager).GetMethod("GetProgressRecovery", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private readonly Dictionary<string, (Node Node, Func<bool> Ready, Action Run)> _actions = new();
    private string _menuId = "";
    private string _scene = "";
    private static bool? AdventureCompleted(string key)
    {
        var save = GameSaveManager.Instance;
        // Match the native normal-Adventure trophy/unlock predicate. Other
        // modes and unknown users must not borrow this progress namespace.
        if (Global.Instance.currentLevelChoose != "Adventure" || key.Length == 0 ||
            !GodotObject.IsInstanceValid(save) || save.config.userCurrent.Length == 0 ||
            save.GetKeyValue("CurrentDifficult").AsString() != "Normal") return null;
        return save.GetLevelValue(key).GetValueOrDefault("Key", new Godot.Collections.Dictionary())
            .AsGodotDictionary().GetValueOrDefault("Finish", 0).AsInt32() > 0;
    }
    private static bool Live(Node? n) => n is not null && GodotObject.IsInstanceValid(n) && n.IsInsideTree() && !n.IsQueuedForDeletion();
    private static IEnumerable<Node> Walk(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren()) foreach (var node in Walk(child)) yield return node;
    }
    internal static DialogBoxBase? TopDialog()
    {
        var manager = DialogManager.Instance;
        if (!GodotObject.IsInstanceValid(manager)) return null;
        var layer = manager.Get("_dialogLayer").As<CanvasLayer>();
        // Native DialogCreate nests confirmation dialogs beneath their parent.
        // The last visible descendant owns input, not the layer's direct child.
        return Live(layer) ? Walk(layer).OfType<DialogBoxBase>()
            .LastOrDefault(d => Live(d) && d.IsVisibleInTree()) : null;
    }
    public JsonObject Observe()
    {
        _actions.Clear(); _menuId = Guid.NewGuid().ToString("N"); _scene = SceneManager.CurrentScene;
        var nodes = Walk(tree.Root).ToArray();
        var choices = new JsonArray();
        var result = new JsonObject { ["menu_id"] = _menuId, ["scene"] = _scene, ["actions"] = choices };
        var topDialog = TopDialog();
        void Offer(string id, string label, Node node, Func<bool> ready, Action run, JsonObject? data = null)
        {
            bool Ready() => ReferenceEquals(TopDialog(), topDialog) && ready();
            if (!Live(node) || !Ready()) return;
            _actions[id] = (node, Ready, run);
            choices.Add(new JsonObject { ["id"] = id, ["label"] = label, ["data"] = data });
        }

        if (topDialog is DialogBoxTips tips)
        {
            result["phase"] = "notice"; result["dialogue_text"] = tips.text;
            Offer("acknowledge", "确认提示", tips, () => tips.IsVisibleInTree(), tips.ConfirmButtonPressed); return result;
        }
        if (topDialog is DialogBoxDifficultWarning warning)
        {
            result["phase"] = "difficulty_warning";
            Offer("acknowledge", "确认难度提示", warning, () => warning.IsVisibleInTree(), warning.ConfirmButtonPressed); return result;
        }
        if (topDialog is DialogBoxNewVersion version)
        {
            result["phase"] = "version_notice"; result["dialogue_text"] = version.message;
            Offer("dismiss_update", "关闭版本提示", version, () => version.IsVisibleInTree(), version.FalseButtonPressed); return result;
        }
        if (topDialog is DialogBoxReStart restart)
        {
            result["phase"] = "restart_confirmation";
            Offer("retry_level", "确认重开本关", restart, () => restart.IsVisibleInTree() && !Global.IsMultiplayerMode
                && Global.Instance.enterLevelMode != "OnlineLevel", restart.TrueButtonPressed);
            Offer("cancel_restart", "取消重开", restart, () => restart.IsVisibleInTree(), restart.FalseButtonPressed); return result;
        }
        if (topDialog is DialogBoxChoose choice)
        {
            result["phase"] = "choice_confirmation"; result["dialogue_text"] = choice.text;
            // 0.31's native progress-recovery notice has a live recovery session
            // and fixed button roles. Expose only its continue callback; never
            // expose the restore/exit side or arbitrary generic confirmations.
            bool ProgressNotice() => GodotObject.IsInstanceValid(TowerDefenseManager.Instance)
                && GodotObject.IsInstanceValid(GameSaveManager.Instance)
                && TowerDefenseManager.CurrentControl is TowerDefenseControlNew current
                && ProgressRecovery?.Invoke(GameSaveManager.Instance, [current]) is not null
                && current.Get("_progressNoticeShown").AsBool()
                && choice.text.StartsWith("[center]关卡进度已恢复[/center]\n", StringComparison.Ordinal)
                && choice.GetNode<BaseButton>("%TrueButton").Get("text").AsString() == "继续游戏"
                && choice.GetNode<BaseButton>("%FalseButton").Get("text").AsString() is "退出并恢复" or "退出关卡";
            Offer("continue_progress_recovery", "继续已恢复的原生关卡", choice, ProgressNotice, choice.TrueButtonPressed);
            // Either callback can spend currency, restore saves or exit a level.
            // Neither side is generically safe; only its owner may drive it.
            result["status"] = "Use the owning semantic operation (quiz_confirm/quiz_cancel for a prepared quiz). Neither generic option is exposed.";
            return result;
        }
        if (topDialog is StarExchange exchange)
        {
            result["phase"] = "star_rewards";
            var rewards = new JsonArray();
            foreach (var item in Walk(exchange).OfType<StarExchangeItem>())
            {
                string key = item.data.GetValueOrDefault("Key", "").AsString();
                int required = item.data.GetValueOrDefault("FinishNum", int.MaxValue).AsInt32();
                bool Unlocked() => GameSaveManager.Instance.GetTowerDefensePacketValue(key).GetValueOrDefault("Unlock", false).AsBool();
                rewards.Add(new JsonObject { ["item_id"] = item.GetInstanceId().ToString(), ["content_id"] = key,
                    ["required_stars"] = required, ["earned_stars"] = item.finishNum, ["unlocked"] = Unlocked(),
                    ["cost"] = 0, ["note"] = "Native completion threshold, no star deduction." });
                Offer("claim_star:" + item.GetInstanceId(), "领取星星解锁：" + key, item,
                    () => item.IsVisibleInTree() && item.data.GetValueOrDefault("Key", "").AsString() == key
                        && item.data.GetValueOrDefault("FinishNum", int.MaxValue).AsInt32() == required
                        && item.finishNum >= required && !Unlocked(), item.Pressed);
            }
            result["rewards"] = rewards;
            Offer("try_levels", "进入星卡试玩", exchange, () => exchange.IsVisibleInTree(), exchange.TryLevelButtonPressed);
            Offer("back", "返回主菜单", exchange, () => exchange.IsVisibleInTree(), exchange.BackButtonPressed);
            return result;
        }
        if (topDialog is DailyChallengeAward awards)
        {
            result["phase"] = "daily_rewards"; result["finished_days"] = awards.finish;
            var rewards = new JsonArray();
            foreach (var item in Walk(awards).OfType<DailyChallengeAwardItem>())
            {
                string key = item.data.GetValueOrDefault("Key", "").AsString();
                rewards.Add(new JsonObject { ["item_id"] = item.GetInstanceId().ToString(), ["effect_key"] = key,
                    ["claimable"] = Live(item.getButton) && !item.getButton.disable });
                Offer("claim_daily:" + item.GetInstanceId(), "领取每日挑战奖励", item,
                    () => item.IsVisibleInTree() && Live(item.getButton) && !item.getButton.disable, item.GetButtonPressed);
            }
            result["rewards"] = rewards;
            Offer("back", "返回日历", awards, () => awards.IsVisibleInTree(), awards.CloseButtonPressed); return result;
        }
        if (topDialog is TryLevel trial)
        {
            result["phase"] = "trial_selection"; result["group"] = trial.currentGroup;
            var drag = trial.Get("dragMenu").As<DragMenu>();
            if (Live(drag))
            {
                result["selected_index"] = drag.currentIndex;
                foreach (var item in drag.GetChildren().OfType<Control>().Select((node, index) => (node, index)))
                    Offer("trial_select:" + item.index, "试玩卡牌 " + (item.index + 1), item.node,
                        () => !drag.mousePress && drag.alive, () => drag.SetPos(item.index),
                        new JsonObject { ["index"] = item.index, ["content_id"] = item.node is TowerDefenseInGamePacketShow packet && GodotObject.IsInstanceValid(packet.config) ? packet.config.saveKey : null });
                Offer("start_trial", "开始所选试玩", trial, () => !drag.mousePress && drag.alive
                    && drag.currentIndex >= 0 && drag.currentIndex < drag.GetChildCount(), trial.PlayButtonPressed);
            }
            Offer("trial_purple", "紫卡试玩", trial, () => trial.IsVisibleInTree(), trial.SelectPurple);
            Offer("trial_star", "星卡试玩", trial, () => trial.IsVisibleInTree(), trial.SelectStar);
            Offer("trial_colour", "彩卡试玩", trial, () => trial.IsVisibleInTree(), trial.SelectColour);
            Offer("back", "返回", trial, () => trial.IsVisibleInTree(), trial.BackButtonPressed); return result;
        }
        if (topDialog is Almanac almanac)
        {
            result["phase"] = "almanac";
            Offer("almanac_index", "图鉴目录", almanac, () => almanac.IsVisibleInTree(), almanac.IndexButtonPressed);
            Offer("almanac_plants", "植物图鉴", almanac, () => almanac.IsVisibleInTree(), almanac.PlantButtonPressed);
            Offer("almanac_zombies", "僵尸图鉴", almanac, () => almanac.IsVisibleInTree(), almanac.ZombieButtonPressed);
            Offer("almanac_props", "铲子与小推车", almanac, () => almanac.IsVisibleInTree(), almanac.PropButtonPressed);
            var props = new JsonArray();
            foreach (var item in Walk(almanac).OfType<AlmanacPropWidow>().Where(n => n.IsVisibleInTree()))
            {
                string key = item.type == "Mower" ? item.mowerConfig.saveKey : item.shovelConfig.saveKey;
                bool Unlocked() => item.type == "Mower" ? item.mowerConfig.Unlock() : item.shovelConfig.Unlock();
                props.Add(new JsonObject { ["id"] = item.GetInstanceId().ToString(), ["type"] = item.type, ["key"] = key });
                Offer("prop_select:" + item.GetInstanceId(), "查看道具 " + key, item, () => item.IsVisibleInTree() && Unlocked(), item.Pressed);
            }
            result["props"] = props; result["selected_prop_type"] = almanac.propType; result["selected_prop_key"] = almanac.propKey;
            var use = almanac.propUseButton;
            Offer("equip_prop", "装备当前已解锁道具", almanac, () => Live(use) && use.IsVisibleInTree() && !use.disable
                && (almanac.propType == "Mower" ? TowerDefenseManager.GetMowerConfig(almanac.propKey)?.Unlock() == true
                    : TowerDefenseManager.GetShovel(almanac.propKey)?.Unlock() == true), almanac.PropUseButtonPressed);
            Offer("back", "关闭图鉴", almanac, () => almanac.IsVisibleInTree(), almanac.CloseButtonPressed); return result;
        }
        if (topDialog is not null && topDialog is not (DialogBoxGamePause or DialogBattlePause or DailyChallengeLevelChoose
            or DailyChallenge or Shop or DialogBoxBattleFail))
        {
            result["unsupported_dialog"] = topDialog.GetType().Name;
            result["status"] = "This dialog has no supported semantic action; do not bypass it."; return result;
        }

        var pause = topDialog as DialogBoxGamePause;
        if (pause is not null)
        {
            Offer("resume", "返回游戏", pause, () => pause.Visible, pause.TrueButtonPressed);
            return result;
        }
        var battlePause = topDialog as DialogBattlePause;
        if (battlePause is not null)
        {
            Offer("resume", "返回游戏", battlePause, () => battlePause.Visible, () => battlePause.CloseDialog());
            var restartButton = battlePause.Get("restartButton").As<MainButton>();
            Offer("request_restart", "请求重开确认", battlePause,
                () => Live(restartButton) && restartButton.IsVisibleInTree() && !restartButton.Disabled
                    && Global.Instance.enterLevelMode != "OnlineLevel", battlePause.RestartButtonPressed);
            Offer("main_menu", "返回主菜单（结束本次游玩）", battlePause, () => battlePause.Visible && Global.Instance.enterLevelMode != "OnlineLevel", battlePause.MainMenuButtonPressed);
            return result;
        }
        var daily = topDialog as DailyChallengeLevelChoose;
        if (daily is not null)
        {
            result["date"] = daily.date; result["busy"] = daily.IsBusy; result["status"] = daily.StatusText;
            result["selected_level"] = daily.SelectedLevelId;
            foreach (var card in daily.Cards)
                Offer("daily:" + card.LevelId, card.DisplayName, card, () => !daily.IsBusy && card.IsAvailable && card.IsInteractionEnabled, () => {
                    using var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = card.Size / 2, Pressed = true };
                    using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = card.Size / 2, Pressed = false };
                    try { card.HandleCardInput(press); } finally { card.HandleCardInput(release); }
                }, new JsonObject { ["level_id"] = card.LevelId, ["finished"] = card.IsFinished });
            Offer("start_daily", "开始所选每日挑战", daily, () => !daily.IsBusy && daily.SelectedCard is { IsAvailable: true }, daily.PlayButtonPressed);
            Offer("back", "返回日历", daily, () => !daily.IsBusy, daily.CloseButtonPressed);
            return result;
        }
        var calendar = topDialog as DailyChallenge;
        if (calendar is not null)
        {
            result["phase"] = "daily_calendar";
            result["date"] = calendar.currentDate;
            var native = calendar.calendar;
            if (Live(native))
            {
                int year = native.year, month = native.month;
                bool SameMonth() => calendar.calendar == native && native.year == year && native.month == month;
                result["calendar_year"] = year; result["calendar_month"] = month;
                var dates = new JsonArray();
                foreach (var value in native.buttons)
                {
                    if (value.AsGodotObject() is not DateButton button || !Live(button)
                        || !int.TryParse(button.Text, out int day)) continue;
                    string date = $"{year}-{month:D2}-{day:D2}";
                    bool Ready() => SameMonth() && button.GetParent() == native.gridDates
                        && button.IsVisibleInTree() && !button.Disabled;
                    dates.Add(new JsonObject { ["date"] = date, ["available"] = Ready(),
                        ["selected"] = calendar.currentDate == date,
                        ["completed"] = Live(button.finishTexture) && button.finishTexture.Visible });
                    Offer("daily_date:" + date, "选择每日挑战日期：" + date, button, Ready, () => {
                        // Match the native toggle button, then its registered date callback.
                        button.ButtonPressed = true;
                        native._OnDateSelected(day, button);
                    }, new JsonObject { ["date"] = date });
                }
                result["calendar_dates"] = dates;
                var previous = native.btnPrevMonth; var next = native.btnNextMonth;
                Offer("daily_prev_month", "查看上个月", previous,
                    () => SameMonth() && previous.IsVisibleInTree() && !previous.Disabled, native._OnPrevMonth);
                Offer("daily_next_month", "查看下个月", next,
                    () => SameMonth() && next.IsVisibleInTree() && !next.Disabled, native._OnNextMonth);
            }
            var play = calendar.playButton;
            string selectedDate = calendar.currentDate;
            Offer("daily_levels", "列出所选日期的挑战", calendar,
                () => calendar.Visible && calendar.currentDate == selectedDate && Live(play)
                    && play.IsVisibleInTree() && !play.Disabled, calendar.PlayButtonPressed);
            Offer("daily_rewards", "查看累计挑战奖励", calendar, () => calendar.Visible, calendar.AwardButtonPressed);
            Offer("back", "返回主菜单", calendar, () => calendar.Visible, calendar.CloseButtonPressed);
            return result;
        }
        if (nodes.OfType<Shop>().Any(n => n.IsVisibleInTree()))
        {
            result["phase"] = "shop";
            result["instruction"] = "Use shop_observe/shop_navigate/close_shop. Purchases require an explicit budget, prepare and confirm; menu actions never buy.";
            return result;
        }
        var otherDialog = topDialog;
        var failure = topDialog as DialogBoxBattleFail;
        if (failure is not null)
        {
            result["phase"] = "lost";
            Offer("retry_level", "重试本关", failure, () => failure.IsVisibleInTree() && failure.restartButton.Visible && !failure.restartButton.Disabled && !Global.IsMultiplayerMode, failure.RestartButtonPressed);
            Offer("finish_level", "结束本关并返回", failure, () => failure.IsVisibleInTree() && failure.backButton.Visible && !failure.backButton.Disabled, failure.BackButtonPressed);
            return result;
        }
        if (otherDialog is not null)
        {
            result["unsupported_dialog"] = otherDialog.GetType().Name;
            result["status"] = "This dialog has no supported semantic action; do not bypass it.";
            return result;
        }
        var loading = nodes.OfType<Loading>().FirstOrDefault();
        if (loading is not null)
        {
            result["loading"] = !loading.CanStart;
            Offer("enter_game", "进入游戏", loading, () => loading.CanStart, loading.OfflineButtonPressed);
            return result;
        }
        var settlement = nodes.OfType<AwardSettlement>().FirstOrDefault();
        if (settlement is not null)
        {
            result["phase"] = "settlement";
            result["reward_name"] = settlement.Tr(settlement.nameLabel.Text).ToString();
            result["reward_description"] = settlement.Tr(settlement.describeLabel.Text).ToString();
            Offer("next_level", "开始下一关", settlement, () => Live(settlement.nextLevelButton) && settlement.nextLevelButton.IsVisibleInTree(), settlement.NextLevelButtonPressed);
            Offer("finish_level", "完成结算并返回选关", settlement,
                () => Live(settlement.moreBackButton) && settlement.moreBackButton.IsVisibleInTree() && !settlement.moreBackButton.Disabled,
                settlement.MenuButtonPressed);
            return result;
        }
        var reward = nodes.OfType<TowerDefenseAwardBase>().FirstOrDefault(n => n.IsVisibleInTree());
        if (reward is not null && TowerDefenseManager.CurrentControl?.HasBattleAwarded == true)
        {
            result["phase"] = reward.press ? "collecting_victory_reward" : "victory_reward_pending";
            result["reward_type"] = reward.GetType().Name;
            Offer("collect_victory_reward", "领取本关胜利奖励", reward,
                () => !tree.Paused && !reward.press && reward.IsVisibleInTree()
                    && TowerDefenseManager.CurrentControl?.HasBattleAwarded == true,
                reward.Pressed);
            return result;
        }
        var chooser = nodes.OfType<LevelChoose>().FirstOrDefault();
        if (chooser is not null)
        {
            result["mode"] = chooser.currentMode.ToString();
            result["group"] = Global.Instance.currentLevelChoose;
            bool Idle() => !GodotObject.IsInstanceValid(chooser.tween) || !chooser.tween.IsRunning();
            if (chooser.currentMode == LevelChoose.LevelChooseMode.Chapter)
            {
                foreach (var item in Walk(chooser.chapterMenu).OfType<DragMenuSelectItemChapter>())
                {
                    int index = item.index;
                    var data = chooser.currentChapterList[index].AsGodotDictionary();
                    string name = data.GetValueOrDefault("Name", "Chapter " + (index + 1)).AsString();
                    Offer("chapter:" + index, name, item, () => Idle() && !item.@lock && chooser.currentMode == LevelChoose.LevelChooseMode.Chapter,
                        () => item.OnButtonPressed(), new JsonObject { ["chapter_index"] = index });
                }
            }
            else if (chooser.currentMode == LevelChoose.LevelChooseMode.Level)
            {
                foreach (var item in Walk(chooser.levelMenu).OfType<DragMenuSelectItemlevel>().DistinctBy(x => x.index))
                {
                    int index = item.index;
                    var data = chooser.currentChapter["Level"].AsGodotArray()[index].AsGodotDictionary();
                    string key = data.GetValueOrDefault("SaveKey", "").AsString();
                    string name = data.GetValueOrDefault("Name", key).AsString();
                    Offer("level:" + index, name, item, () => Idle() && !item.@lock && chooser.currentMode == LevelChoose.LevelChooseMode.Level,
                        () => item.OnButtonPressed(), new JsonObject { ["level_id"] = key, ["level_index"] = index, ["chapter_index"] = chooser.currentChapterIndex,
                            ["completed"] = AdventureCompleted(key) });
                }
            }
            Offer("back", "返回上一级", chooser, Idle, chooser.Back);
            return result;
        }
        var main = nodes.OfType<MainMenu>().FirstOrDefault();
        if (main is not null)
        {
            Offer("open_shop", "打开商店", main, () => !main.wait && GlobalFeatureManager.Instance?.IsUnlocked("Shop") == true, main.ShopButtonPressed);
            Offer("adventure", "冒险模式", main, () => !main.wait, main.AdventureButtonPressed);
            Offer("challenge", "挑战模式", main, () => !main.wait, main.ChallengeButtonPressed);
            Offer("survival", "生存模式", main, () => !main.wait, main.SurvivalButtonPressed);
            Offer("puzzle", "益智模式", main, () => !main.wait, main.PuzzleGameButtonPressed);
            Offer("mini_games", "迷你游戏", main, () => !main.wait, main.MiniGameButtonPresed);
            Offer("reverse_role", "我是僵尸 2", main, () => !main.wait, main.IZM2GameButtonPresed);
            Offer("hybrid_park", "杂交乐园", main, () => !main.wait, main.HybridParkGameButtonPressed);
            Offer("star_rewards", "星星解锁奖励", main, () => !main.wait, main.StarsExchangeButtonPresed);
            Offer("almanac", "打开图鉴和装备", main, () => !main.wait, main.AlmanacButtonPressed);
            Offer("daily", "每日挑战", main, () => !main.wait, main.DailyChallengeButtonPressed);
            return result;
        }
        var selection = TowerDefenseManager.Instance?.GetPacketBankFeature();
        var control = TowerDefenseManager.CurrentControl;
        if (GodotObject.IsInstanceValid(control) && control.isView)
        {
            result["phase"] = "map_view";
            bool ViewReady() => control.isView && !tree.Paused && GodotObject.IsInstanceValid(control.process)
                && control.process is TowerDefenseBattleProcessWave or TowerDefenseBattleProcessIZM2
                && control.process.Get("_viewBroadcastActive").AsBool();
            result["waiting_for_camera"] = !ViewReady();
            Offer("continue_preview", "返回选卡", control, ViewReady, control.EmitViewBack); return result;
        }
        if (Entry.SelectionOpen(selection))
        {
            Offer("open_shop", "打开商店", selection!.packetBank, () => Entry.SelectionOpen(selection) && GlobalFeatureManager.Instance?.IsUnlocked("Shop") == true, selection.ShopButtonPressed);
            var viewButton = selection.packetBank.translate.GetNodeOrNull<NinePatchButtonBase>("ViewButton");
            Offer("view_map", "查看战场", selection.packetBank, () => Entry.SelectionOpen(selection)
                && Live(viewButton) && viewButton.IsVisibleInTree() && !viewButton.disable, selection.ViewButtonPressed);
        }
        var talk = TowerDefenseManager.CurrentControl?.featureDictionary.Values.OfType<TowerDefenseBattleFeatureNpcTalk>().FirstOrDefault();
        if (talk is not null && GodotObject.IsInstanceValid(talk.config) && talk.currentIndex < talk.config.talkList.Count)
        {
            var line = talk.config.talkList[talk.currentIndex];
            int lineIndex = talk.currentIndex;
            result["dialogue"] = line.text;
            result["dialogue_text"] = tree.Root.Tr(line.text).ToString();
            result["dialogue_type"] = line.GetType().Name;
            result["tutorial"] = TutorialState();
            // Hand props are ordinary dialogue. Embedded tutorial dialogue becomes
            // continuable only after the game's own conditions have completed.
            bool DialogueReady() => GodotObject.IsInstanceValid(talk) && GodotObject.IsInstanceValid(talk.config)
                && talk.currentIndex == lineIndex && talk.config.talkList[lineIndex] == line
                && !talk.Get("_tutorialActive").AsBool()
                && (line.GetType() == typeof(NpcTalkBaseConfig) || line.GetType() == typeof(NpcTalkHandConfig)
                    || line.GetType() == typeof(NpcTalkTutorialConfig));
            foreach (var npc in talk.npcDictionary.Values.Select(v => v.As<NpcBase>()).Where(n => Live(n) && n.canPress))
                Offer("continue_dialogue", "继续对话", npc, () => DialogueReady() && npc.canPress && TowerDefenseManager.CurrentControl?.isGameRunning == false, () => {
                    if (Input.IsActionPressed("Press")) throw new InvalidOperationException("Player input is currently held.");
                    using var input = new InputEventAction { Action = "Press", Pressed = true };
                    try { Input.ActionPress("Press"); npc._Input(input); }
                    finally { Input.ActionRelease("Press"); }
                    if (Input.IsActionPressed("Press")) throw new InvalidOperationException("Game input release was not confirmed.");
                });
        }
        return result;
    }

    public static JsonObject? TutorialState()
    {
        var tutorial = TutorialManager.Instance;
        if (!Live(tutorial) || !GodotObject.IsInstanceValid(tutorial.currentTutoroal)) return null;
        int index = tutorial.currentStep;
        if (index < 0 || index >= tutorial.currentTutoroal.GetStepNum()) return null;
        var step = tutorial.currentTutoroal.GetTutorialStep(index);
        var conditions = new JsonArray();
        foreach (var condition in step.conditionList)
        {
            if (condition is TutorialConditionCheckCharaterNum count)
                conditions.Add(new JsonObject { ["type"] = "character_count", ["content_id"] = count.characterName,
                    ["operator"] = count.method, ["target"] = count.num,
                    ["current"] = TowerDefenseManager.Instance.GetCharacterFromName(count.characterName).Count,
                    ["required_action"] = TutorialPolicy.CountAction(count.method,
                        TowerDefenseManager.Instance.GetCharacterFromName(count.characterName).Count, count.num) });
            else if (condition is TutorialConditionCheckSunCollect sun)
                conditions.Add(new JsonObject { ["type"] = "sun_collected", ["target"] = sun.num, ["current"] = sun.currentNum });
            else conditions.Add(new JsonObject { ["type"] = condition.GetType().Name, ["supported"] = false });
        }
        return new JsonObject { ["step"] = index, ["total_steps"] = tutorial.currentTutoroal.GetStepNum(),
            ["active"] = tutorial.stepExe, ["conditions"] = conditions,
            ["instruction"] = step.broadCastUse && GodotObject.IsInstanceValid(step.broadCastConfig)
                ? tutorial.Tr(step.broadCastConfig.broadCastString).ToString() : "" };
    }

    public void Act(string menuId, string actionId)
    {
        if (menuId != _menuId || SceneManager.CurrentScene != _scene || !_actions.TryGetValue(actionId, out var target))
            throw new InvalidOperationException("Stale menu/choice; query menu again.");
        if (!Live(target.Node) || !target.Ready()) throw new InvalidOperationException("Menu choice is no longer ready.");
        _actions.Clear();
        target.Run();
    }
}
