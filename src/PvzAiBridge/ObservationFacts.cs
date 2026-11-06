using System.Text.Json.Nodes;

namespace PvzAiBridge;

// JSON primitives only: also exercised with reflection serialization disabled.
public static class ObservationFacts
{
    public static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    public static bool IsSupport(string terrain, int physiqueFlags) =>
        terrain is "POT" or "LILYPAD" || (physiqueFlags & 6) != 0;

    public static bool? Presence(int observedCount, bool complete) => observedCount > 0 ? true : complete ? false : null;

    public static JsonObject Shovel(bool managerReady, bool configReady, bool? shown, bool running, bool? nativeCanUse)
    {
        // Native ShovelButtonPressed rejects hidden tools only outside a running battle.
        string reason = !managerReady ? "manager_unavailable" : !configReady ? "config_unavailable"
            : !nativeCanUse.HasValue ? "native_permission_unknown" : !nativeCanUse.Value ? "native_permission_denied"
            : !running && shown != true ? "hidden_outside_battle" : "ready";
        return new JsonObject {
            ["enabled"] = reason == "ready", ["reason"] = reason,
            ["manager_ready"] = managerReady, ["config_ready"] = configReady,
            ["shovel_show"] = JsonValue.Create(shown), ["battle_running"] = running,
            ["native_can_use"] = JsonValue.Create(nativeCanUse)
        };
    }

    public static JsonObject Environment(bool? night, bool transitioning, string? mapPath) => new()
    {
        ["status"] = night.HasValue ? "known" : "unknown",
        ["day_phase"] = night.HasValue ? night.Value ? "night" : "day" : "unknown",
        ["is_night"] = JsonValue.Create(night), ["transitioning"] = transitioning,
        ["source"] = night.HasValue ? "TowerDefenseBattleFeatureMap.config.isNight" : null,
        ["map_resource_path"] = mapPath
    };
}
