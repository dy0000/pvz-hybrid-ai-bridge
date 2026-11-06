using System.Text;
using System.Text.Json.Nodes;

namespace PvzAiBridge;

public static class WireProtocol
{
    public static JsonObject Parse(byte[] line, string token)
    {
        var body = JsonNode.Parse(Encoding.UTF8.GetString(line)) as JsonObject ?? throw new InvalidDataException("Expected JSON object.");
        if (body["token"]?.GetValue<string>() != token) throw new InvalidDataException("Invalid local connection token.");
        body.Remove("token");
        return body;
    }
}

public sealed class JsonLineBuffer
{
    private readonly List<byte> _buffer = new();
    public byte[]? Feed(byte[] bytes)
    {
        if (_buffer.Count + bytes.Length > 65536) throw new InvalidDataException("Request exceeds 64 KiB.");
        int newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline >= 0 && newline != bytes.Length - 1) throw new InvalidDataException("Pipelining is unsupported; wait for each response.");
        _buffer.AddRange(bytes);
        if (newline < 0) return null;
        var line = _buffer.ToArray(); _buffer.Clear(); return line;
    }
    public void Clear() => _buffer.Clear();
}
