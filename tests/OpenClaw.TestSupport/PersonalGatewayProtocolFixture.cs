using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Shared;

namespace OpenClaw.TestSupport;

public sealed class PersonalGatewayProtocolFixture : IAsyncDisposable
{
    public const string Route = "wss://protocol.example.ts.net";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task<WebSocket> _socket;
    private TcpClient? _tcp;
    private Task? _pump;
    private readonly Channel<JsonElement> _frames = Channel.CreateUnbounded<JsonElement>();

    public PersonalGatewayProtocolFixture()
    {
        _listener.Start();
        _socket = AcceptAsync();
    }

    public OpenClawGatewayClient CreateClient(string identityPath, HttpMessageHandler? httpHandler = null)
    {
        var client = new OpenClawGatewayClient(Route, string.Empty,
            identityPath: identityPath, operatorAuthentication: OperatorAuthenticationMode.PersonalTailscale,
            assistantMediaHandler: httpHandler);
        typeof(WebSocketClientBase).GetField("_gatewayUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        return client;
    }

    private async Task<WebSocket> AcceptAsync()
    {
        _tcp = await _listener.AcceptTcpClientAsync(_lifetime.Token);
        var stream = _tcp.GetStream();
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (header.Length >= 16384 || await stream.ReadAsync(one, _lifetime.Token) == 0)
                throw new IOException("Invalid fixture upgrade.");
            header.Append((char)one[0]);
        }
        var key = header.ToString().Split("\r\n")
            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), _lifetime.Token);
        var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        _pump = PumpAsync(socket);
        return socket;
    }

    public async Task ChallengeAsync() => await SendAsync(new
    {
        type = "event", @event = "connect.challenge", payload = new { nonce = "fixture-challenge", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
    });

    public Task ProfileChangedAsync() => SendAsync(new
    {
        type = "event", @event = "sessions.changed", payload = new { reason = "profile-identity" }
    });

    public async Task<JsonElement> ReceiveAsync() =>
        await _frames.Reader.ReadAsync(_lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private async Task PumpAsync(WebSocket socket)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                var buffer = new byte[8192];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Fixture closed", _lifetime.Token);
                        _frames.Writer.TryComplete();
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Expected fixture text frame.");
                    frame.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(frame.ToArray());
                await _frames.Writer.WriteAsync(json.RootElement.Clone(), _lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or WebSocketException or IOException)
        {
            _frames.Writer.TryComplete(ex);
        }
    }

    public async Task SendAsync(object value)
    {
        var socket = await _socket.WaitAsync(TimeSpan.FromSeconds(5));
        await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)), WebSocketMessageType.Text, true, _lifetime.Token);
    }

    public Task HelloAsync(JsonElement connect, string method = "tailscale", string scope = "fixture-scope") => SendAsync(new
    {
        type = "res", id = connect.GetProperty("id").GetString(), ok = true,
        payload = new { type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            auth = new { method, recoveryScope = scope, deviceId = "fixture-operator", scopes = new[] { "operator.read", "operator.write" } } }
    });

    public Task SelfAsync(JsonElement request, string? profile = "person-42") => SendAsync(new
    {
        type = "res", id = request.GetProperty("id").GetString(), ok = true,
        payload = new { profile = new { id = profile } }
    });

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _tcp?.Dispose();
        try { (await _socket).Dispose(); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
        if (_pump is not null) await _pump;
        _lifetime.Dispose();
    }
}
