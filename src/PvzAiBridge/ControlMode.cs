namespace PvzAiBridge;

public sealed class ControlMode
{
    public bool Realtime { get; private set; } = true;
    public string Name => Realtime ? "realtime" : "turn_based";
    public void Select(string? mode)
    {
        Realtime = mode switch { null or "" or "realtime" => true, "turn_based" => false,
            _ => throw new ArgumentException("mode must be realtime or turn_based") };
    }
    public bool ShouldAutoPause(bool owner, bool running, bool stepping, bool ownPause, bool multiplayer)
        => !Realtime && owner && running && !stepping && !ownPause && !multiplayer;
    public void RequireActionState(bool paused, bool ownPause, bool modal)
    {
        if (modal) throw new InvalidOperationException("Game pause dialog is open. Use the observed resume menu action.");
        if (Realtime && paused) throw new InvalidOperationException("Game is paused; resume its dialog or return to manual control first.");
        if (!Realtime && (!paused || !ownPause)) throw new InvalidOperationException("Turn-based action requires the bridge-owned pause.");
    }
    public static void RequirePreparation(bool paused, bool modal)
    {
        if (paused || modal) throw new InvalidOperationException("Preparation is paused or blocked by a modal dialog. Resume normally first.");
    }
}
