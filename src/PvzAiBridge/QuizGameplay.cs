using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private TowerDefenseBattleProcessQuiz? _preparedQuiz;
    private DialogBoxChoose? _quizDialog;
    private string _quizTransaction = "";

    private TowerDefenseBattleProcessQuiz? Quiz => Valid(TowerDefenseManager.CurrentControl)
        ? TowerDefenseManager.CurrentControl.process as TowerDefenseBattleProcessQuiz : null;
    private static IEnumerable<BetPanel> BetPanels(TowerDefenseBattleProcessQuiz quiz) => Valid(quiz.quizUI)
        && Valid(quiz.quizUI.betPanelNode) ? quiz.quizUI.betPanelNode.GetChildren().OfType<BetPanel>() : [];
    private static string BetChoice(BetPanel panel) => panel.winCheckBox.ButtonPressed ? "win"
        : panel.failCheckBox.ButtonPressed ? "lose" : panel.skipCheckBox.ButtonPressed ? "skip" : "unknown";

    private JsonObject QuizQuote(TowerDefenseBattleProcessQuiz quiz)
    {
        var bets = new JsonArray(); long sum = 0;
        foreach (var panel in BetPanels(quiz).OrderBy(p => p.line))
        {
            int wager = checked((int)panel.betCoinSpinBox.Value); sum = checked(sum + wager);
            bets.Add(new JsonObject { ["row"] = panel.line, ["wager"] = wager, ["choice"] = BetChoice(panel),
                ["min"] = panel.betCoinSpinBox.MinValue, ["max"] = panel.betCoinSpinBox.MaxValue,
                ["step"] = panel.betCoinSpinBox.Step });
        }
        return new JsonObject { ["feature_id"] = quiz.GetInstanceId().ToString(), ["battle_id"] = _battle,
            ["multiple"] = quiz.multiple, ["bets"] = bets, ["price"] = checked((int)(sum * quiz.multiple)),
            ["native_minimum_balance"] = checked(sum * (quiz.multiple + 1)) };
    }

    private JsonObject? ObserveQuiz()
    {
        var quiz = Quiz;
        if (!Valid(quiz) || !Valid(quiz!.quizUI)) return null;
        var state = QuizQuote(quiz);
        state["started"] = quiz.quizStarted; state["running"] = quiz.running; state["over"] = quiz.over;
        state["coins_spent"] = quiz.useCoin; state["coins"] = TowerDefenseManager.Instance.GetCoin();
        state["preparation_open"] = !quiz.running && quiz.quizStarted && quiz.quizUI.startGUINode.IsVisibleInTree();
        var refresh = new JsonArray();
        foreach (var button in quiz.quizUI.changePresentBoxButtonNode.GetChildren().OfType<MainButton>().Where(b => VisibleNode(b)))
        {
            int row = TowerDefenseManager.Instance.GetMapGridPosFromMouse(new Vector2(
                TowerDefenseManager.Instance.GetMapCellPosCenter(new Vector2I(1, 1)).X, button.GlobalPosition.Y)).Y;
            refresh.Add(new JsonObject { ["button_id"] = button.GetInstanceId().ToString(), ["row"] = row });
        }
        state["refresh_buttons"] = refresh;
        state["transaction_id"] = _quizTransaction.Length > 0 ? _quizTransaction : null;
        state["confirmation_open"] = VisibleNode(_quizDialog);
        return state;
    }

    private TowerDefenseBattleProcessQuiz QuizPreparation(JsonObject body, bool confirming = false)
    {
        var quiz = Quiz;
        if (!Valid(quiz) || !Valid(quiz!.quizUI) || !quiz.quizStarted || quiz.running || quiz.over
            || !quiz.quizUI.startGUINode.IsVisibleInTree() || Text(body, "feature_id") != quiz.GetInstanceId().ToString())
            throw new InvalidOperationException("Exact native quiz preparation is not open.");
        var top = MenuNavigation.TopDialog();
        if (confirming ? top != _quizDialog || !VisibleNode(_quizDialog) : top is not null || _tree.Paused || ModalPause())
            throw new InvalidOperationException("Another dialog or pause blocks quiz preparation.");
        ClearPicker();
        if (_plan?.Active == true || _planTask is not null || _planOutcomeUnknown)
            throw new InvalidOperationException("Resolve the active planting plan first.");
        return quiz;
    }

    private void ForgetQuiz(string id)
    {
        if (_quizTransaction != id) return;
        _quizTransaction = ""; _preparedQuiz = null; _quizDialog = null;
    }

    private void ConfirmNativeQuiz(string id, TowerDefenseBattleProcessQuiz quiz, DialogBoxChoose dialog)
    {
        try
        {
            var entry = Purchases.Get(id) ?? throw new InvalidOperationException("Quiz transaction is missing.");
            var result = PurchaseLifecycle.Confirm(Purchases, id, "QuizEntry", 1,
                () => (TowerDefenseManager.Instance.GetCoin(), quiz.running ? 1L : 0L),
                (coins, _) => {
                    QuizPreparation(new JsonObject { ["feature_id"] = Text(entry, "feature_id") }, true);
                    if (quiz != _preparedQuiz || dialog != _quizDialog || id != _quizTransaction
                        || Text(entry, "battle_id") != _battle || QuizQuote(quiz).ToJsonString() != Text(entry, "quiz_quote")
                        || coins < entry["native_minimum_balance"]!.GetValue<long>()
                        || Integer(entry, "price") > Integer(entry, "max_spend")
                        || Purchases.Budget(Text(entry, "budget_id"))["expires_at_unix_ms"]!.GetValue<long>() <= UnixMs)
                        throw new InvalidOperationException("Quiz wager, process, balance or authorized budget changed.");
                }, () => quiz.Run(Integer(entry, "price")));
            _notice = "Quiz entry " + Text(result, "status") + ": " + id;
        }
        catch (Exception ex) { _notice = "Quiz entry requires review: " + ex.GetBaseException().Message; }
    }

    private JsonObject QuizAction(string op, JsonObject body, ref bool actionStarted)
    {
        string id = Text(body, "transaction_id");
        if (op is "quiz_confirm" or "quiz_cancel")
        {
            var existing = Purchases.Get(id) ?? throw new ArgumentException("Unknown transaction_id.");
            if (Text(existing, "effect_key") != "quiz_entry") throw new ArgumentException("Transaction belongs to a different operation.");
            string requestedStatus = op == "quiz_cancel" ? "cancelled" : "committed";
            if (Text(existing, "status") != "prepared") return PurchaseLifecycle.QuizResult(existing, requestedStatus, true);
            if (op == "quiz_cancel" && (id != _quizTransaction || !VisibleNode(_quizDialog)))
            {
                var cancelled = PurchaseLifecycle.Close(Purchases, id); ForgetQuiz(id);
                return PurchaseLifecycle.QuizResult(cancelled, requestedStatus);
            }
            if (id != _quizTransaction || !VisibleNode(_quizDialog) || _preparedQuiz is null)
                throw new InvalidOperationException("Original native quiz confirmation is no longer available; do not prepare a replacement purchase.");
            if (op == "quiz_confirm") { QuizPreparation(body, true); actionStarted = true; _quizDialog!.TrueButtonPressed(); }
            else { actionStarted = true; _quizDialog!.FalseButtonPressed(); }
            return PurchaseLifecycle.QuizResult(Purchases.Get(id)!, requestedStatus);
        }
        var quiz = QuizPreparation(body);
        if (op == "quiz_configure")
        {
            var bets = body["bets"] as JsonArray ?? throw new ArgumentException("bets required.");
            if (bets.Count is < 1 or > 50) throw new ArgumentException("Configure 1..50 distinct rows.");
            var changes = new List<(BetPanel Panel, int Wager, string Choice)>();
            foreach (var bet in bets.OfType<JsonObject>())
            {
                int row = Integer(bet, "row"), wager = Integer(bet, "wager"); string choice = Text(bet, "choice");
                var panel = BetPanels(quiz).SingleOrDefault(p => p.line == row);
                if (panel is null || !panel.IsVisibleInTree() || !panel.betCoinSpinBox.IsVisibleInTree()
                    || !panel.betCoinSpinBox.Editable || wager < 0 || wager < panel.betCoinSpinBox.MinValue || wager > panel.betCoinSpinBox.MaxValue
                    || choice is not ("win" or "lose" or "skip") || changes.Any(c => c.Panel == panel)
                    || panel.betCoinSpinBox.Step > 0 && Math.Abs(wager / panel.betCoinSpinBox.Step - Math.Round(wager / panel.betCoinSpinBox.Step)) > 1e-7)
                    throw new ArgumentException("Quiz row/wager/choice is outside native UI limits or duplicated.");
                changes.Add((panel, wager, choice));
            }
            if (changes.Count != bets.Count) throw new ArgumentException("Invalid quiz row object.");
            actionStarted = true;
            foreach (var change in changes)
            {
                change.Panel.betCoinSpinBox.Value = change.Wager;
                (change.Choice == "win" ? change.Panel.winCheckBox : change.Choice == "lose" ? change.Panel.failCheckBox : change.Panel.skipCheckBox).ButtonPressed = true;
            }
            return new JsonObject { ["ok"] = true, ["outcome"] = "quiz_wagers_configured", ["quiz"] = ObserveQuiz() };
        }
        if (op == "quiz_refresh_row")
        {
            int row = Integer(body, "row");
            var button = quiz.quizUI.changePresentBoxButtonNode.GetChildren().OfType<MainButton>()
                .SingleOrDefault(b => b.GetInstanceId().ToString() == Text(body, "button_id"));
            if (!VisibleNode(button) || button!.Disabled || row < 1 || row > TowerDefenseManager.GetMapFeature().config.gridNum.Y
                || Math.Abs(button.GlobalPosition.Y - TowerDefenseManager.Instance.GetMapCellPos(new Vector2I(0, row)).Y - 20) > .1)
                throw new InvalidOperationException("Quiz refresh button/row is stale.");
            actionStarted = true; quiz.ChangePresentBoxButtonPressed(row, button);
            return new JsonObject { ["ok"] = true, ["outcome"] = "quiz_refresh_requested", ["completion_pending"] = true };
        }
        if (op != "quiz_prepare") throw new ArgumentException("Unknown quiz operation.");
        var quote = QuizQuote(quiz); int price = Integer(quote, "price");
        if (price != Integer(body, "expected_price") || price > Integer(body, "max_spend")
            || TowerDefenseManager.Instance.GetCoin() < quote["native_minimum_balance"]!.GetValue<long>())
            throw new InvalidOperationException("Wager quote/balance changed or exceeds max_spend.");
        if (_quizTransaction.Length > 0) throw new InvalidOperationException("Cancel the current prepared quiz entry first.");
        var entryQuote = (JsonObject)quote.DeepClone(); entryQuote["quiz_quote"] = quote.ToJsonString();
        entryQuote["effect_key"] = "quiz_entry"; entryQuote["budget_id"] = Text(body, "budget_id"); entryQuote["max_spend"] = Integer(body, "max_spend");
        Purchases.Prepare(id, entryQuote);
        DialogBoxChoose? created = null;
        try
        {
            actionStarted = true; quiz.RunButtonPressed();
            var dialog = quiz.Get("_pendingRunDialog").As<DialogBoxChoose>();
            created = dialog;
            if (!VisibleNode(dialog) || MenuNavigation.TopDialog() != dialog) throw new InvalidOperationException("Native quiz confirmation did not open.");
            // Replace only this native confirmation's own callback, so both an
            // AI confirmation and a human click persist intent before spending.
            quiz.Call("DisconnectPendingRunDialog");
            _preparedQuiz = quiz; _quizDialog = dialog; _quizTransaction = id;
            dialog!.OnChooseTrue += () => ConfirmNativeQuiz(id, quiz, dialog);
            dialog.OnClose += () => {
                try { PurchaseLifecycle.Close(Purchases, id); }
                catch (Exception ex) { _notice = "Quiz cancellation persistence failed: " + ex.GetBaseException().Message; }
                finally { ForgetQuiz(id); }
            };
            return new JsonObject { ["ok"] = true, ["outcome"] = "quiz_confirmation_prepared", ["transaction"] = Purchases.Get(id) };
        }
        catch
        {
            if (VisibleNode(created)) { quiz.Call("DisconnectPendingRunDialog"); created!.FalseButtonPressed(); }
            PurchaseLifecycle.Close(Purchases, id); ForgetQuiz(id); throw;
        }
    }
}

