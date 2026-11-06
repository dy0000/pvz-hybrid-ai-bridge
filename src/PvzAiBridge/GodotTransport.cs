using System.Text;
using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed class Request(long connection, JsonObject body)
{
    public long Connection { get; } = connection;
    public JsonObject Body { get; } = body;
    public TaskCompletionSource<JsonObject> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// The published game trims .NET TcpListener/NamedPipeServerStream. Use the
// engine's nonblocking sockets on ProcessFrame, even while the tree is paused.
public sealed class LocalTransport : IDisposable
{
    public ushort Port { get; private set; } = 47632;
    public string Token { get; } = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    public Queue<Request> Pending { get; } = new();
    public Queue<long> Disconnected { get; } = new();
    private TcpServer? _server;
    private StreamPeerTcp? _peer;
    private readonly JsonLineBuffer _incoming = new();
    private Request? _inflight;
    private byte[]? _output;
    private int _sent;
    private long _connection;
    public string? Failure { get; private set; }

    public void Start(int port = 47632)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Port = (ushort)port;
        _server = new TcpServer();
        var error = _server.Listen(Port, "127.0.0.1");
        if (error != Error.Ok) throw new IOException("Cannot reserve local AI endpoint: " + error);
    }

    public void Poll()
    {
        if (_server is null) return;
        try
        {
            if (_peer is null && _server.IsConnectionAvailable())
            {
                _peer = _server.TakeConnection();
                _peer.SetNoDelay(true); _connection++;
            }
            if (_peer is null) return;
            _peer.Poll();
            if (_peer.GetStatus() != StreamPeerSocket.Status.Connected) { Disconnect(); return; }
            if (_inflight?.Completion.Task.IsCompleted == true)
            {
                if (_inflight.Completion.Task.IsCompletedSuccessfully)
                    _output = Encoding.UTF8.GetBytes(_inflight.Completion.Task.Result.ToJsonString() + "\n");
                _inflight = null; _sent = 0;
            }
            if (_output is not null)
            {
                using var sent = _peer.PutPartialData(_output.AsSpan(_sent));
                if ((Error)sent[0].AsInt32() != Error.Ok) { Disconnect(); return; }
                _sent += sent[1].AsInt32();
                if (_sent < _output.Length) return;
                _output = null;
            }
            if (_inflight is not null) return;
            int count = Math.Min(_peer.GetAvailableBytes(), 8192);
            if (count <= 0) return;
            using var received = _peer.GetPartialData(count);
            if ((Error)received[0].AsInt32() != Error.Ok) { Disconnect(); return; }
            var line = _incoming.Feed(received[1].AsByteArray());
            if (line is null) return;
            try
            {
                var body = WireProtocol.Parse(line, Token);
                _inflight = new Request(_connection, body);
                Pending.Enqueue(_inflight);
            }
            catch (Exception ex)
            {
                _output = Encoding.UTF8.GetBytes(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString() + "\n");
                _sent = 0;
            }
        }
        catch (Exception ex) { Failure = ex.GetBaseException().Message; Disconnect(); }
    }

    private void Disconnect()
    {
        if (_peer is not null)
        {
            Disconnected.Enqueue(_connection);
            _peer.DisconnectFromHost(); _peer.Dispose(); _peer = null;
        }
        _incoming.Clear(); _inflight = null; _output = null; _sent = 0;
        while (Pending.TryDequeue(out var request)) request.Completion.TrySetCanceled();
    }

    public void Dispose()
    {
        Disconnect(); _server?.Stop(); _server?.Dispose(); _server = null;
    }
}
