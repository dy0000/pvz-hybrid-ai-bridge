using System.Text.Json.Nodes;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private JsonObject? _wireBaseline;
    private string _wireSnapshotId = "", _wireView = "";
    private long _wireConnection;
    private JsonObject ObservationResponse(Request request, bool delta)
    {
        string view = Text(request.Body, "view"); if (view.Length == 0) view = "full";
        if (view is not ("full" or "compact")) throw new ArgumentException("view must be full or compact.");
        bool executable = request.Body["executable_cards_only"]?.GetValue<bool>() ?? false;
        var state = Observe();
        if (executable && state["cards"] is JsonArray cards)
        {
            state["unavailable_cards_omitted"] = cards.OfType<JsonObject>().Count(c => c["available"]?.GetValue<bool>() != true);
            state["cards"] = new JsonArray(cards.OfType<JsonObject>().Where(c => c["available"]?.GetValue<bool>() == true).Select(c => c.DeepClone()).ToArray());
            state["cards_view"] = "executable_only";
            // A filtered deck cannot carry the complete unfiltered matrix.
            state.Remove("continuous_policy");
        }
        string key = view + ":" + executable;
        bool baseline = delta && _wireConnection == request.Connection && _wireView == key && _wireBaseline is not null
            && Text(request.Body, "base_snapshot_id") == _wireSnapshotId
            && Text(state, "battle_id") == Text(_wireBaseline, "battle_id") && Text(state, "scene") == Text(_wireBaseline, "scene");
        string previous = _wireSnapshotId, next = Guid.NewGuid().ToString("N");
        JsonObject payload = state;
        var removed = new JsonArray();
        if (baseline)
        {
            payload = new JsonObject();
            foreach (var p in state) if (!_wireBaseline!.ContainsKey(p.Key) || !JsonNode.DeepEquals(p.Value, _wireBaseline[p.Key])) payload[p.Key] = p.Value?.DeepClone();
            foreach (var p in _wireBaseline!) if (!state.ContainsKey(p.Key)) removed.Add(JsonValue.Create(p.Key));
        }
        _wireBaseline = (JsonObject)state.DeepClone(); _wireView = key; _wireConnection = request.Connection; _wireSnapshotId = next;
        return new JsonObject { ["ok"] = true, ["kind"] = baseline ? "delta" : "full", ["snapshot_id"] = next,
            ["base_snapshot_id"] = baseline ? previous : null, ["removed_fields"] = removed,
            [baseline ? "changes" : "state"] = view == "compact" ? SharedObservation.Encode(payload) : payload,
            ["encoding"] = view == "compact" ? "shared-json-v1" : "json", ["reset"] = delta && !baseline };
    }
}
