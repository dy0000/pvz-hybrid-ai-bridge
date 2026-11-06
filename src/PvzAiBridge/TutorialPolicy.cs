using System.Text.Json.Nodes;

namespace PvzAiBridge;

// Describes which native action can move an unmet tutorial count toward its
// condition. This is an authorization gate, never an action scheduler.
public static class TutorialPolicy
{
    public static JsonArray AllowedOps(Func<string, bool> allowed)
    {
        var result = new JsonArray();
        foreach (string op in new[] { "plant", "shovel", "collect" })
            // JsonArray.Add<T>(string) needs serializer metadata in NativeAOT.
            if (allowed(op)) result.Add(JsonValue.Create(op));
        return result;
    }

    public static string? CountAction(string comparison, int current, int target) => comparison switch
    {
        ">" when current <= target => "plant",
        ">=" when current < target => "plant",
        "==" when current < target => "plant",
        "==" when current > target => "shovel",
        "<" when current >= target => "shovel",
        "<=" when current > target => "shovel",
        _ => null
    };
}
