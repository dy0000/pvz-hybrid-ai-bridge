namespace PvzAiBridge;

// Native Init/Clear/EnterTree change Lifetime; consumption changes UseSequence.
// Never infer correspondence from content names or recycle a retired bridge ID.
public readonly record struct OneUseCardIdentity(ulong Instance, int Lifetime, ulong Config, long UseSequence, string Source);

public sealed class OneUseCardRegistry
{
    private readonly Dictionary<OneUseCardIdentity, long> _ids = new();
    private readonly HashSet<OneUseCardIdentity> _seen = new();
    private string _battle = "";
    private long _next;
    public void Begin(string battle)
    {
        if (_battle != battle) { _battle = battle; _ids.Clear(); }
        _seen.Clear();
    }
    public long Bind(OneUseCardIdentity identity)
    {
        _seen.Add(identity);
        if (!_ids.TryGetValue(identity, out long id)) _ids[identity] = id = checked(--_next);
        return id;
    }
    public void End()
    {
        foreach (var key in _ids.Keys.Where(k => !_seen.Contains(k)).ToArray()) _ids.Remove(key);
    }
    public static bool CanUse(bool alive, bool locked, bool cooling, bool pending, double pressDelay,
        bool alreadyPicked, double lifetime, double age, long? cost, bool nativeAffordable) =>
        alive && !locked && !cooling && !pending && (pressDelay <= 0 || alreadyPicked)
        && (lifetime == -1 || age < lifetime || alreadyPicked) && cost.HasValue && nativeAffordable;
}
