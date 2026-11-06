using System.Diagnostics;
using System.Text.Json.Nodes;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private PlantPlan? _plan, _submittedPlan;
    private readonly HashSet<string> _usedPlanIds = new();
    private Task<XWModCommandResult<XWModCardUseResult>>? _planTask;
    private PlanOption? _planOption;
    private string _planReason = "";
    private bool _planOutcomeUnknown;
    private long _nextPlanScan, _lastPlanScan, _planSubmittedAt;
    private double _planDecisionMs, _planScanGapMs;
    private static long NowMs => (long)Time.GetTicksMsec();
    private bool LocalDecisionsEnabled => ProjectSettings.GetSetting("pvz_ai/local_decisions_enabled", true).AsBool();

    private JsonObject? PlanState(long? after = null)
    {
        var state = _plan?.Snapshot(NowMs, after);
        if (state is not null) { state["in_flight"] = _planTask is not null; state["outcome_unknown"] = _planOutcomeUnknown; state["scan_interval_ms"] = 50; }
        return state;
    }
    private void CancelPlan(string reason) => _plan?.Stop(reason, NowMs);

    private void FinishPlanAction()
    {
        if (_planTask?.IsCompleted != true) return;
        var task = _planTask; var plan = _submittedPlan!; var option = _planOption!;
        _planTask = null; _submittedPlan = null; _planOption = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (result.Succeeded && result.Value == XWModCardUseResult.Committed)
                plan.Commit(option, _planReason, NowMs, _planDecisionMs, NowMs - _planSubmittedAt, _planScanGapMs);
            else
            {
                // Even a definitive rejection is surfaced for replanning, never
                // automatically resubmitted. Submitted may still finish later.
                bool unknown = !(result.Status == XWModCommandStatus.Rejected
                    || result.Succeeded && result.Value == XWModCardUseResult.Rejected);
                if (plan.BattleId == _battle) _planOutcomeUnknown = unknown;
                plan.AddEvent(new JsonObject { ["kind"] = "native_result", ["job_id"] = plan.Jobs[option.JobIndex].Id,
                    ["outcome"] = result.Value.ToString(), ["diagnostic"] = result.Diagnostic, ["at_ms"] = NowMs });
                plan.Stop(unknown ? "unknown_result" : "rejected", NowMs);
            }
        }
        catch (Exception ex)
        {
            if (plan.BattleId == _battle) _planOutcomeUnknown = true;
            plan.AddEvent(new JsonObject { ["kind"] = "unknown_result", ["error"] = ex.GetBaseException().Message, ["at_ms"] = NowMs });
            plan.Stop("unknown_result", NowMs);
        }
    }

    private void TickPlan()
    {
        FinishPlanAction();
        if (_planTask is not null)
        {
            if (NowMs - _planSubmittedAt > 5000)
            {
                if (_submittedPlan?.BattleId == _battle) _planOutcomeUnknown = true;
                _submittedPlan?.Stop("native_timeout", NowMs);
            }
            return; // A submitted native operation cannot be recalled or overlapped.
        }
        if (_plan?.Active != true) return;
        if (!LocalDecisionsEnabled) { CancelPlan("local_decisions_disabled"); return; }
        if (!_owner.HasValue || _playerLock || !_mode.Realtime || Global.IsMultiplayerMode || _plan.BattleId != _battle)
        { CancelPlan("control_unavailable"); return; }
        if (NowMs >= _plan.ExpiresAtMs) { CancelPlan("expired"); return; }
        if (!Valid(TowerDefenseManager.CurrentControl)) { CancelPlan("battle_changed"); return; }
        if (TowerDefenseManager.CurrentControl.HasBattleAwarded || TowerDefenseManager.CurrentControl.isGameFail)
        { CancelPlan("battle_ended"); return; }
        if (!Running || _tree.Paused || ModalPause() || _pending is not null || NowMs < _nextPlanScan) return;
        long now = NowMs; _planScanGapMs = now - _lastPlanScan; _lastPlanScan = now; _nextPlanScan = now + 50;
        long started = Stopwatch.GetTimestamp();
        var cards = _context.Game.Cards.GetCards();
        long sun = TowerDefenseManager.Instance.GetSun();
        var characters = TowerDefenseManager.Instance._GetCleanCharacters().Where(c => Valid(c) && !c.die)
            .Select(c => _context.Game.Characters.TryGetHandle(c, out var h) && _context.Game.Characters.TryGet(h, out var s)
                ? (Character: c, Snapshot: s) : default)
            .Where(c => c.Character is not null && c.Snapshot is not null && !c.Snapshot.Dead && !c.Snapshot.Removing).ToArray();
        var attackIds = _plan.Jobs.Where(j => j.Role == "attack").Select(j => j.ContentId).ToHashSet();
        var options = new List<PlanOption>();
        for (int i = 0; i < _plan.Jobs.Length; i++)
        {
            var job = _plan.Jobs[i];
            if (job.Uses >= job.MaxUses) continue;
            var card = cards.FirstOrDefault(c => c.ContentId == job.ContentId);
            if (card is null) { job.Waiting = "card_missing"; continue; }
            if (card.Cost > _plan.SunBudget - _plan.Spent) { job.Waiting = "budget_limit"; continue; }
            if (!_plan.CanSpend(card.Cost, sun)) { job.Waiting = "sun_or_reserve"; continue; }
            if (!card.Available) { job.Waiting = card.Remaining > 0 ? "cooldown" : "game_restriction"; continue; }
            var config = TowerDefenseManager.GetPacketConfigReadOnly(job.ContentId);
            if (!Valid(config)) { job.Waiting = "card_missing"; continue; }
            job.Waiting = "cells_blocked";
            foreach (var pos in job.Cells)
            {
                // Only explicit candidates. Never clear plants, stack onto an
                // occupied cell, or choose an unapproved emergency card/location.
                if (characters.Any(c => c.Character is TowerDefensePlant && c.Snapshot.Column == pos.Column && c.Snapshot.Row == pos.Row)) continue;
                var cell = TowerDefenseManager.GetMapCell(new Vector2I(pos.Column, pos.Row));
                if (!Valid(cell) || !cell.CanPacketPlant(config, noLimit: false, isCheck: true)) continue;
                int nearest = 0;
                foreach (var c in characters)
                    if (c.Character is TowerDefenseZombie && c.Character.camp == TowerDefenseEnum.CHARACTER_CAMP.ZOMBIE && c.Snapshot.Row == pos.Row)
                    {
                        // Spawned zombies can expose an uninitialized cached grid
                        // column for their first frame. Use their actual position.
                        int column = Math.Max(1, TowerDefenseManager.Instance.GetMapGridPos(c.Character.GetLogicalGlobalPosition()).X);
                        if (nearest == 0 || column < nearest) nearest = column;
                    }
                bool attack = characters.Any(c => c.Character is TowerDefensePlant && c.Character.camp == TowerDefenseEnum.CHARACTER_CAMP.PLANT
                    && c.Snapshot.Row == pos.Row && attackIds.Contains(c.Snapshot.ContentKey));
                if (job.ThreatenedOnly && nearest == 0) { job.Waiting = "waiting_for_threat"; continue; }
                job.Waiting = "eligible";
                options.Add(new PlanOption(i, pos, card.Handle.Id, card.Cost, nearest, attack));
            }
        }
        var selected = _plan.Choose(options, sun, now);
        if (selected is null) return;
        _planDecisionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _submittedPlan = _plan; _planOption = selected.Value.Option; _planReason = selected.Value.Reason;
        _planSubmittedAt = NowMs;
        var chosen = cards.First(c => c.Handle.Id == _planOption.CardId);
        try
        {
            _planTask = _context.Game.Cards.Use(chosen.Handle, _planOption.Cell.Column, _planOption.Cell.Row);
            FinishPlanAction();
        }
        catch
        {
            _planOutcomeUnknown = true; CancelPlan("unknown_result"); throw;
        }
    }
}
