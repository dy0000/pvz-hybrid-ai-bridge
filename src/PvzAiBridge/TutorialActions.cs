using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private ShovelManager? EnabledShovel()
    {
        var status = ObserveShovel(out var shovel);
        return status["enabled"]!.GetValue<bool>() ? shovel : null;
    }

    private JsonObject ObserveShovel(out ShovelManager? shovel)
    {
        shovel = TowerDefenseManager.GetMapFeature()?.shovelManager;
        bool ready = Valid(shovel) && shovel!.IsInsideTree() && !shovel.IsQueuedForDeletion();
        bool configReady = ready && Valid(shovel!.shovelConfig);
        bool? shown = ready ? shovel!.shovelShow : null;
        bool? permitted = ready ? shovel!.Call("CanUseShovel").AsBool() : null;
        return ObservationFacts.Shovel(ready, configReady, shown, Running, permitted);
    }

    private TutorialStepConfig? InteractiveTutorial()
    {
        var control = TowerDefenseManager.CurrentControl;
        var tutorial = TutorialManager.Instance;
        if (!Valid(control) || control.isGameFail || control.HasBattleAwarded
            || !Valid(tutorial) || !tutorial.IsInsideTree() || !tutorial.stepExe
            || !Valid(tutorial.currentTutoroal) || tutorial.currentStep < 0
            || tutorial.currentStep >= tutorial.currentTutoroal.GetStepNum()) return null;
        // An embedded tutorial starts only after native map/tool setup. Do not
        // treat a stale/global tutorial resource as permission during loading.
        var talk = control.featureDictionary.Values.OfType<TowerDefenseBattleFeatureNpcTalk>()
            .FirstOrDefault(t => Valid(t) && t.Get("_tutorialActive").AsBool()
                && Valid(t.config) && t.currentIndex >= 0 && t.currentIndex < t.config.talkList.Count
                && t.config.talkList[t.currentIndex] is NpcTalkTutorialConfig line
                && line.tutorial == tutorial.currentTutoroal);
        var manager = TowerDefenseManager.Instance;
        if (talk is null || !Valid(manager) || manager.gridNum.X <= 0 || manager.gridNum.Y <= 0
            || !Valid(TowerDefenseManager.GetMapFeature())) return null;
        for (int row = 1; row <= manager.gridNum.Y; row++)
        for (int col = 1; col <= manager.gridNum.X; col++)
            if (!Valid(TowerDefenseManager.GetMapCell(new Vector2I(col, row)))) return null;
        return tutorial.currentTutoroal.GetTutorialStep(tutorial.currentStep);
    }

    private bool TutorialAllows(string op, string? characterName = null)
    {
        var step = InteractiveTutorial();
        if (step is null || !_mode.Realtime || _tree.Paused || ModalPause()) return false;
        foreach (var condition in step.conditionList)
        {
            if (condition is TutorialConditionCheckCharaterNum count
                && (characterName is null || characterName == count.characterName)
                && TutorialPolicy.CountAction(count.method,
                    TowerDefenseManager.Instance.GetCharacterFromName(count.characterName).Count, count.num) == op)
            {
                if (op == "shovel") return EnabledShovel() is not null;
                if (op == "plant") return Valid(TowerDefenseManager.Instance.GetPacketPickControl());
            }
            if (op == "collect" && condition is TutorialConditionCheckSunCollect sun && sun.currentNum < sun.num)
                return true;
        }
        return false;
    }

    private JsonObject? ObserveTutorial()
    {
        var info = MenuNavigation.TutorialState();
        if (info is null) return null;
        var allowed = TutorialPolicy.AllowedOps(op => TutorialAllows(op));
        info["actionable"] = allowed.Count > 0;
        info["allowed_ops"] = allowed;
        var targets = new JsonArray();
        if (TutorialAllows("shovel"))
        {
            var manager = TowerDefenseManager.Instance;
            for (int row = 1; row <= manager.gridNum.Y; row++)
            for (int col = 1; col <= manager.gridNum.X; col++)
                foreach (var target in ShovelTargets(new Vector2I(col, row)).OfType<JsonObject>())
                    if (TutorialAllows("shovel", Text(target, "character_name"))) targets.Add(target.DeepClone());
        }
        info["shovel_targets"] = targets;
        return info;
    }

    private JsonArray ShovelTargets(Vector2I pos)
    {
        var result = new JsonArray();
        var shovel = EnabledShovel();
        var cell = TowerDefenseManager.GetMapCell(pos);
        if (shovel is null || !Valid(cell)) return result;
        var seen = new HashSet<ulong>();
        // These three regions cover the native AIR/surround/default branches;
        // return only the character currently selected by the native resolver.
        foreach (double fraction in new[] { 0.5, 0.125, 0.75 })
        {
            var c = cell.GetShovelCharacter(fraction, shovel.shovelConfig);
            if (!Valid(c) || !Valid(c.instance) || !Valid(c.config) || c.die || c.isDestroy
                || !seen.Add(c.GetInstanceId())) continue;
            result.Add(new JsonObject { ["row"] = pos.Y, ["column"] = pos.X, ["layer_fraction"] = fraction,
                ["native_object_id"] = c.GetInstanceId().ToString(),
                ["content_id"] = c.packet?.saveKey ?? c.config.name, ["character_name"] = c.config.name,
                ["entity_id"] = _context.Game.Characters.TryGetHandle(c, out var h) ? JsonValue.Create(h.Id) : null,
                ["slots"] = new JsonArray(cell.slot.Where(s => s.Value == c).Select(s => (JsonNode?)JsonValue.Create(s.Key.ToString())).ToArray()),
                ["surround"] = cell.characterSurround == c });
        }
        return result;
    }
}
