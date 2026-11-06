using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private static JsonObject EntityInteraction(TowerDefenseCharacter entity)
    {
        var mouse = entity.componentManager?.GetRuntime<MousePressComponent>();
        var drag = entity.componentManager?.GetRuntime<DragMoveComponent>();
        var button = entity is TowerDefensePlantSpringFumeShroom
            ? entity.GetNodeOrNull<Button>("SpriteGroup/Button") : null;
        return new JsonObject {
            ["pointer_skill"] = mouse is not null && !mouse.IsReleased && mouse.Alive,
            ["aim_required"] = mouse?.ToggleMode, ["aiming"] = mouse?.IsPressed,
            ["double_click_skill"] = entity is TowerDefensePlantRobot,
            ["button_skill"] = VisibleNode(button) && !button!.Disabled,
            ["draggable"] = drag is not null && !drag.IsReleased && drag.Alive && drag.allowMouseDrag,
            ["dragging"] = drag?.drag, ["slidable_with_cell"] = entity.canMowerMove };
    }

    private static Vector2 ScreenPoint(Vector2 world) => TowerDefenseManager.GetMapFeature().mapControl.GetCanvasTransform() * world;

    private static void EntityClick(TowerDefenseCharacter entity, Vector2 screen, bool pressed)
    {
        using var input = new InputEventMouseButton { ButtonIndex = MouseButton.Left,
            ButtonMask = pressed ? MouseButtonMask.Left : 0, Position = screen, GlobalPosition = screen, Pressed = pressed };
        entity._Input(input);
    }

    private JsonObject InteractEntity(JsonObject body, ref bool started, TowerDefenseCharacter? bound = null)
    {
        var entity = bound ?? CurrentEntity(body);
        if (Input.IsActionPressed("Press") || Valid(DragMoveComponent.CurrentMoveCharacter))
            throw new InvalidOperationException("Player pointer/drag selection is active.");
        var mouse = entity.componentManager?.GetRuntime<MousePressComponent>();
        var drag = entity.componentManager?.GetRuntime<DragMoveComponent>();
        string action = Text(body, "action");
        if (action == "drag")
        {
            if (drag is null || drag.IsReleased || !drag.Alive || !drag.allowMouseDrag || drag.drag || mouse?.IsPressed == true)
                throw new InvalidOperationException("This exact entity has no ready native drag interaction.");
            var target = Cell(body); var destination = TowerDefenseManager.GetMapCell(target);
            if (!CellRulesReady(destination) || !destination.CanMoveCharacterHere(entity) || target == entity.gridPos)
                throw new InvalidOperationException("Native drag placement is restricted.");
            var from = ScreenPoint(TowerDefenseManager.Instance.GetMapCellPosCenter(entity.gridPos));
            var to = ScreenPoint(TowerDefenseManager.Instance.GetMapCellPosCenter(target));
            try
            {
                started = true; EntityClick(entity, from, true);
                using var move = new InputEventMouseMotion { Position = to, GlobalPosition = to, ButtonMask = MouseButtonMask.Left };
                entity._Input(move);
            }
            finally
            {
                if (Valid(entity)) EntityClick(entity, to, false);
                // Only our exact temporary selection was acquired above.
                if (DragMoveComponent.CurrentMoveCharacter == entity) DragMoveComponent.CurrentMoveCharacter = null;
                if (!drag.IsReleased && drag.drag || Valid(DragMoveComponent.CurrentMoveCharacter))
                    throw new InvalidOperationException("Native drag release is unconfirmed; do not replay.");
            }
            bool moved = Valid(entity) && entity.gridPos == target && entity.cell == destination;
            return new JsonObject { ["ok"] = moved, ["outcome"] = moved ? "entity_moved" : "unknown_observe_before_retry",
                ["entity_id"] = body["entity_id"]!.DeepClone(), ["selection_released"] = true, ["native_effect_confirmed"] = moved };
        }
        if (action == "button")
        {
            var button = entity is TowerDefensePlantSpringFumeShroom ? entity.GetNodeOrNull<Button>("SpriteGroup/Button") : null;
            if (!VisibleNode(button) || button!.Disabled || !entity.CanRunAbility())
                throw new InvalidOperationException("Native entity button is not enabled.");
            started = true; button.EmitSignal(BaseButton.SignalName.Pressed);
            return new JsonObject { ["ok"] = true, ["outcome"] = "entity_button_submitted",
                ["entity_id"] = body["entity_id"]!.DeepClone(), ["completion_pending"] = true };
        }
        if (action is not ("click" or "double_click" or "aim") || mouse is null || mouse.IsReleased || !mouse.Alive || mouse.IsPressed
            || mouse.ToggleMode != (action == "aim") || action == "double_click" && entity is not TowerDefensePlantRobot
            || !mouse.TryGetClickShapeScreenCenter(out var center))
            throw new InvalidOperationException("Native pointer skill, action or click shape is unavailable; aiming skills require an explicit target cell.");
        Vector2 finish = action == "aim" ? ScreenPoint(TowerDefenseManager.Instance.GetMapCellPosCenter(Cell(body))) : center;
        bool triggered = false;
        void Pressed(Vector2 _) { if (action == "click") triggered = true; }
        void DoublePressed(Vector2 _) { if (action == "double_click") triggered = true; }
        void Finished(Vector2 _) { if (action == "aim") triggered = true; }
        mouse.OnPressed += Pressed; mouse.OnDoublePressed += DoublePressed; mouse.OnFinishPressed += Finished;
        try
        {
            started = true; EntityClick(entity, center, true); if (Valid(entity)) EntityClick(entity, center, false);
            if (action != "click" && Valid(entity) && !mouse.IsReleased)
            { EntityClick(entity, finish, true); if (Valid(entity)) EntityClick(entity, finish, false); }
        }
        finally
        {
            mouse.OnPressed -= Pressed; mouse.OnDoublePressed -= DoublePressed; mouse.OnFinishPressed -= Finished;
            if (!mouse.IsReleased && mouse.IsPressed && Valid(entity))
            {
                using var cancel = new InputEventAction { Action = "Release", Pressed = false };
                entity._Input(cancel);
            }
            if (!mouse.IsReleased && mouse.IsPressed) throw new InvalidOperationException("Native aim release unconfirmed; do not replay.");
            if (drag is not null && !drag.IsReleased && drag.drag && Valid(entity)) EntityClick(entity, finish, false);
            if (DragMoveComponent.CurrentMoveCharacter == entity) DragMoveComponent.CurrentMoveCharacter = null;
        }
        return new JsonObject { ["ok"] = triggered, ["outcome"] = triggered ? "native_skill_triggered" : "rejected",
            ["entity_id"] = body["entity_id"]!.DeepClone(), ["native_activation_confirmed"] = triggered,
            ["selection_released"] = true, ["completion_pending"] = triggered,
            ["note"] = "Confirms the exact native activation callback, not damage, kill or asynchronous ability completion." };
    }

    private JsonObject SlideCell(JsonObject body, ref bool started)
    {
        var mower = Feature<TowerDefenseBattleFeatureMower>()?.mowerManager;
        if (!Valid(mower) || !mower!.IsInsideTree() || mower.Get("_isDraggingPreview").AsBool())
            throw new InvalidOperationException("Native sliding manager is unavailable or player drag is active.");
        var sourcePos = new Vector2I(Integer(body, "from_column"), Integer(body, "from_row"));
        var pos = Cell(body);
        if (!TowerDefenseManager.Instance.CheckMapGridPosIn(sourcePos) || sourcePos == pos)
            throw new ArgumentException("Slide requires distinct valid source and destination cells.");
        var source = TowerDefenseManager.GetMapCell(sourcePos); var target = TowerDefenseManager.GetMapCell(pos);
        if (!CellRulesReady(source) || !CellRulesReady(target) || !source.CanMowerMove() || !source.CanMoveToCell(target))
            throw new InvalidOperationException("Native sliding rules reject the source or destination.");
        var occupants = source.characterList.Where(Valid).ToArray();
        if (occupants.Length == 0) throw new InvalidOperationException("Source cell is empty.");
        started = true; source.MoveToCell(target);
        bool moved = occupants.All(c => Valid(c) && c.gridPos == pos && target.characterList.Contains(c));
        return new JsonObject { ["ok"] = moved, ["outcome"] = moved ? "cell_slid" : "unknown_observe_before_retry",
            ["native_effect_confirmed"] = moved, ["completion_pending"] = moved };
    }
}
