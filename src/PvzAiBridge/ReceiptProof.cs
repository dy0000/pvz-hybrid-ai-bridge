using System.Text.Json.Nodes;

namespace PvzAiBridge;

// Forward actual native response facts only. Recovery owns interpretation;
// neither Committed nor request arguments imply consumption or released input.
public static class ReceiptProof
{
    public static void Copy(JsonObject response, JsonObject record)
    {
        foreach (string key in new[] { "source", "card_id", "native_object_id", "native_generation",
            "native_commit_confirmed", "selection_released", "native_use_sequence_before", "native_use_sequence_after",
            "entity_id", "native_effect_confirmed", "native_activation_confirmed", "completion_pending", "native_count_before",
            "native_count_after", "board_revision_after", "action", "count", "transaction" })
            if (response.ContainsKey(key)) record[key] = response[key]?.DeepClone();
    }
}
