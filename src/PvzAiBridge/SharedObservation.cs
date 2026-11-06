using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PvzAiBridge;

// Lossless shared subtrees. A compact response is explicitly encoded; callers
// must decode it before treating absent properties as gameplay facts.
public static class SharedObservation
{
    public static JsonObject Encode(JsonObject source)
    {
        var counts = new Dictionary<string, int>();
        void Count(JsonNode? n)
        {
            if (n is JsonObject o) foreach (var p in o) Count(p.Value);
            else if (n is JsonArray a) foreach (var c in a) Count(c);
            else return;
            string json = n.ToJsonString(); if (json.Length >= 64) counts[json] = counts.GetValueOrDefault(json) + 1;
        }
        Count(source);
        var definitions = new JsonObject();
        JsonNode? Pack(JsonNode? n, bool root = false)
        {
            if (n is null) return null;
            if (n is JsonObject literal && (literal.ContainsKey("$shared_ref") || literal.ContainsKey("$shared_literal")))
                return new JsonObject { ["$shared_literal"] = literal.DeepClone() };
            string json = n.ToJsonString();
            if (!root && counts.GetValueOrDefault(json) > 1 && !json.Contains("\"$shared_ref\""))
            {
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..24];
                if (definitions.ContainsKey(id) && !JsonNode.DeepEquals(definitions[id], n))
                    id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                if (!definitions.ContainsKey(id)) definitions[id] = n.DeepClone();
                return new JsonObject { ["$shared_ref"] = id };
            }
            if (n is JsonObject o) { var next = new JsonObject(); foreach (var p in o) next[p.Key] = Pack(p.Value); return next; }
            if (n is JsonArray a) return new JsonArray(a.Select(c => Pack(c)).ToArray());
            return n.DeepClone();
        }
        var packed = Pack(source, true);
        return new JsonObject { ["encoding"] = "shared-json-v1", ["root"] = packed, ["definitions"] = definitions };
    }
    public static JsonObject Decode(JsonObject encoded)
    {
        if (encoded["encoding"]?.GetValue<string>() != "shared-json-v1") throw new ArgumentException("Unsupported encoding.");
        var defs = encoded["definitions"]!.AsObject();
        JsonNode? Unpack(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o.Count == 1 && o.ContainsKey("$shared_literal")) return o["$shared_literal"]?.DeepClone();
                if (o.Count == 1 && o["$shared_ref"] is JsonValue reference)
                    return defs[reference.GetValue<string>()]?.DeepClone() ?? throw new InvalidDataException("Missing shared definition.");
                var result = new JsonObject(); foreach (var p in o) result[p.Key] = Unpack(p.Value); return result;
            }
            if (n is JsonArray a) return new JsonArray(a.Select(Unpack).ToArray());
            return n?.DeepClone();
        }
        return Unpack(encoded["root"])!.AsObject();
    }
}

