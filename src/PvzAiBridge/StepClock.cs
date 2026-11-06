namespace PvzAiBridge;

// SceneTree.PhysicsFrame is emitted BEFORE processing the frame. Stop on the next
// boundary after N frames, not on the Nth boundary (which would advance only N-1).
public sealed class StepClock
{
    public int Remaining { get; private set; }
    public int Completed { get; private set; }
    public bool Active { get; private set; }
    public void Start(int frames)
    {
        if (Active) throw new InvalidOperationException("A step is already pending.");
        if (frames is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(frames), "Use 1..600 physics frames.");
        Remaining = frames;
        Completed = 0;
        Active = true;
    }
    public bool BeforePhysicsFrame()
    {
        if (!Active) return false;
        if (Remaining == 0) { Active = false; return true; }
        Remaining--;
        Completed++;
        return false;
    }
    public void Cancel() { Active = false; Remaining = 0; }
}
