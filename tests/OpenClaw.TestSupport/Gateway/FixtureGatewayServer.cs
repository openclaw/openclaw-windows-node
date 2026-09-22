using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>Safe diagnostic metadata only. Request IDs, credentials and payloads are never retained.</summary>
public sealed record GatewayFixtureRequest(string Method, string? SessionKey, string Outcome);

/// <summary>
/// An independently owned, operator-only loopback Gateway. Responses are selected by
/// method and parameters, not consumed from a tape. No request is forwarded anywhere.
/// </summary>
public sealed class FixtureGatewayServer : IAsyncDisposable
{
    private readonly GatewayScenario _scenario;
    private readonly byte[] _tokenHash;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _sync = new();
    private readonly List<Task> _connections = [];
    private readonly List<GatewayFixtureRequest> _requests = [];
    private readonly List<int> _unexpectedIndices = [];
    private readonly HashSet<int> _subscriptions = [];
    private readonly Dictionary<string, TaskCompletionSource> _historyGates = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _handshake = NewSignal();
    private readonly TaskCompletionSource _accepted = NewSignal();
    private TaskCompletionSource _requestChanged = NewSignal();
    private readonly Task _acceptLoop;
    private Task? _disposal;
    private int _connectionCount;
    private int _activeConnectionCount;
    private static readonly JsonElement EmptyParameters = JsonSerializer.SerializeToElement(new { });

    public Uri Endpoint { get; }
    public Task HandshakeCompleted => _handshake.Task;
    public Task ConnectionAccepted => _accepted.Task;
    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnectionCount);
    public int SubscriptionCount
    {
        get { lock (_sync) return _subscriptions.Count; }
    }
    public IReadOnlyList<GatewayFixtureRequest> Requests
    {
        get { lock (_sync) return _requests.ToArray(); }
    }
    public IReadOnlyList<GatewayFixtureRequest> UnexpectedRequests
    {
        get { lock (_sync) return _unexpectedIndices.Select(i => _requests[i]).ToArray(); }
    }

    private FixtureGatewayServer(GatewayScenario scenario, string token, CancellationToken cancellationToken)
    {
        _scenario = scenario;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = AcceptLoopAsync();
    }

    public static Task<FixtureGatewayServer> StartAsync(
        GatewayScenario scenario, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FixtureGatewayServer(scenario, token, cancellationToken));
    }

    /// <summary>Holds subsequent reads of this history until ReleaseHistory. Other requests continue normally.</summary>
    public void HoldHistory(string sessionKey)
    {
        if (!_scenario.ContainsSession(sessionKey))
            throw new ArgumentException("Unknown fixture session key.", nameof(sessionKey));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_historyGates.TryAdd(sessionKey, NewSignal()))
                throw new InvalidOperationException("History is already held for this session.");
        }
    }

    public void ReleaseHistory(string sessionKey)
    {
        lock (_sync)
        {
            if (!_historyGates.Remove(sessionKey, out var signal))
                throw new InvalidOperationException("History is not held for this session.");
            signal.TrySetResult();
        }
    }

    /// <summary>Releases the gate immediately. Await response/UI readiness separately after releasing.</summary>
    public Task ReleaseHistoryAsync(string sessionKey)
    {
        ReleaseHistory(sessionKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for receipt, including a request currently held at a history gate.
    /// Occurrence is one-based and includes earlier requests in this server's lifetime.
    /// </summary>
    public async Task<GatewayFixtureRequest> WaitForRequestAsync(
        string method, string? sessionKey = null, int occurrence = 1, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(occurrence);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                var request = _requests.Where(r => r.Method == method && (sessionKey is null || r.SessionKey == sessionKey))
                    .Skip(occurrence - 1).FirstOrDefault();
                if (request is not null)
                    return request;
                changed = _requestChanged.Task;
            }
            await changed.WaitAsync(wait.Token);
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                var id = Interlocked.Increment(ref _connectionCount);
                lock (_sync)
                    _connections.Add(ServeConnectionAsync(tcp, id));
                _accepted.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _listener.Stop();
            _accepted.TrySetCanceled(_lifetime.Token);
            _handshake.TrySetCanceled(_lifetime.Token);
        }
    }

    private async Task ServeConnectionAsync(TcpClient tcp, int connectionId)
    {
        using (tcp)
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        using (var sendLock = new SemaphoreSlim(1, 1))
        {
            Interlocked.Increment(ref _activeConnectionCount);
            var pending = new List<Task>();
            WebSocket? socket = null;
            try
            {
                socket = await UpgradeAsync(tcp.GetStream(), connection.Token);
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                await SendAsync(socket, sendLock, new
                {
                    type = "event", @event = "connect.challenge",
                    payload = new { nonce, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                }, connection.Token);
                var authenticated = false;
                while (!connection.IsCancellationRequested)
                {
                    var json = await ReceiveAsync(socket, connection.Token);
                    if (json is null)
                    {
                        await sendLock.WaitAsync(connection.Token);
                        try
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Fixture connection closed.", connection.Token);
                        }
                        finally { sendLock.Release(); }
                        break;
                    }

                    JsonElement root;
                    try
                    {
                        using var document = JsonDocument.Parse(json);
                        root = document.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        var malformed = Record("<malformed>", null);
                        await SendErrorAsync(socket, sendLock, null, "INVALID_REQUEST", "Expected a Gateway JSON request.", connection.Token);
                        Complete(malformed, "error:INVALID_REQUEST", unexpected: true);
                        continue;
                    }

                    var id = ReadString(root, "id");
                    var method = ReadString(root, "method");
                    if (ReadString(root, "type") != "req" || string.IsNullOrWhiteSpace(id) || !ValidMethod(method))
                    {
                        var malformed = Record("<malformed>", null);
                        await SendErrorAsync(socket, sendLock, id, "INVALID_REQUEST", "Expected a request type, string id and method.", connection.Token);
                        Complete(malformed, "error:INVALID_REQUEST", unexpected: true);
                        continue;
                    }

                    var parameters = root.TryGetProperty("params", out var p) ? p : EmptyParameters;
                    var key = ReadString(parameters, "sessionKey") ?? ReadString(parameters, "key");
                    var requestIndex = Record(method!, _scenario.ContainsSession(key ?? "") ? key : key is null ? null : "<unknown>");
                    if (!authenticated || method == "connect")
                    {
                        try
                        {
                            if (authenticated)
                                throw new FixtureRequestException("INVALID_REQUEST", "Operator is already connected.");
                            if (method != "connect")
                                throw new FixtureRequestException("AUTH_REQUIRED", "Operator connect is required before reading fixture data.");
                            Authenticate(parameters, nonce);
                            await SendAsync(socket, sendLock, new
                            {
                                type = "res", id, ok = true, payload = _scenario.CreateHello($"fixture-connection-{connectionId}")
                            }, connection.Token);
                            authenticated = true;
                            Complete(requestIndex, "ok");
                            _handshake.TrySetResult();
                        }
                        catch (FixtureRequestException ex)
                        {
                            await SendErrorAsync(socket, sendLock, id, ex.Code, ex.Message, connection.Token);
                            Complete(requestIndex, $"error:{ex.Code}");
                        }
                        continue;
                    }

                    Task? gate;
                    lock (_sync)
                        gate = method == "chat.history" && key is not null && _historyGates.TryGetValue(key, out var held)
                            ? held.Task : null;
                    pending.RemoveAll(task => task.IsCompletedSuccessfully);
                    pending.Add(RespondAsync(socket, sendLock, id!, method!, parameters, requestIndex, connectionId, gate, connection.Token));
                }
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
            catch (WebSocketException) when (socket?.State is WebSocketState.Aborted or WebSocketState.Closed) { }
            catch (IOException) when (connection.IsCancellationRequested) { }
            catch (InvalidDataException) when (socket is null)
            {
                Complete(Record("<upgrade>", null), "error:INVALID_UPGRADE", unexpected: true);
            }
            catch (IOException) when (socket is null)
            {
                Complete(Record("<upgrade>", null), "error:UPGRADE_DISCONNECTED", unexpected: true);
            }
            finally
            {
                await connection.CancelAsync();
                socket?.Abort();
                try { await Task.WhenAll(pending); }
                finally
                {
                    socket?.Dispose();
                    lock (_sync) _subscriptions.Remove(connectionId);
                    Interlocked.Decrement(ref _activeConnectionCount);
                }
            }
        }
    }

    private async Task RespondAsync(
        WebSocket socket, SemaphoreSlim sendLock, string id, string method, JsonElement parameters,
        int requestIndex, int connectionId, Task? gate, CancellationToken cancellationToken)
    {
        try
        {
            object payload;
            try
            {
                if (parameters.ValueKind != JsonValueKind.Object)
                    throw new FixtureRequestException("INVALID_PARAMS", "params must be an object.");
                payload = _scenario.Respond(method, parameters);
            }
            catch (FixtureRequestException ex)
            {
                Complete(requestIndex, $"error:{ex.Code}", ex.Unexpected);
                await SendErrorAsync(socket, sendLock, id, ex.Code, ex.Message, cancellationToken);
                return;
            }
            if (method == "sessions.subscribe")
                lock (_sync) _subscriptions.Add(connectionId);
            if (gate is not null)
                await gate.WaitAsync(cancellationToken);
            await SendAsync(socket, sendLock, new { type = "res", id, ok = true, payload }, cancellationToken);
            Complete(requestIndex, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(requestIndex, "cancelled");
        }
        catch (WebSocketException) when (cancellationToken.IsCancellationRequested || socket.State != WebSocketState.Open)
        {
            Complete(requestIndex, "disconnected");
        }
    }

    private void Authenticate(JsonElement p, string nonce)
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw new FixtureRequestException("INVALID_PARAMS", "connect params must be an object.");
        if (!p.TryGetProperty("auth", out var auth) || ReadString(auth, "token") is not { } token
            || !CryptographicOperations.FixedTimeEquals(_tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(token))))
            throw new FixtureRequestException("AUTH_TOKEN_MISMATCH", "Unauthorized: fixture token mismatch.");
        if (ReadString(p, "role") != "operator")
            throw new FixtureRequestException("INVALID_PARAMS", "Fixture Gateway supports only the operator role.");
        if (!p.TryGetProperty("minProtocol", out var min) || min.ValueKind != JsonValueKind.Number || !min.TryGetInt32(out var minimum)
            || !p.TryGetProperty("maxProtocol", out var max) || max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out var maximum)
            || minimum > _scenario.ProtocolVersion || maximum < _scenario.ProtocolVersion || minimum > maximum)
            throw new FixtureRequestException("PROTOCOL_MISMATCH", "Fixture Gateway protocol range mismatch.");
        if (!p.TryGetProperty("client", out var client) || string.IsNullOrWhiteSpace(ReadString(client, "id"))
            || !p.TryGetProperty("device", out var device) || ReadString(device, "nonce") != nonce
            || string.IsNullOrWhiteSpace(ReadString(device, "id"))
            || string.IsNullOrWhiteSpace(ReadString(device, "publicKey"))
            || string.IsNullOrWhiteSpace(ReadString(device, "signature")))
            throw new FixtureRequestException("INVALID_PARAMS", "Expected a signed operator envelope for this challenge.");
    }

    private int Record(string method, string? key)
    {
        lock (_sync)
        {
            var index = _requests.Count;
            _requests.Add(new GatewayFixtureRequest(method, key, "pending"));
            SignalRequestChanged();
            return index;
        }
    }

    private void Complete(int index, string outcome, bool unexpected = false)
    {
        lock (_sync)
        {
            _requests[index] = _requests[index] with { Outcome = outcome };
            if (unexpected)
                _unexpectedIndices.Add(index);
            SignalRequestChanged();
        }
    }

    private void SignalRequestChanged()
    {
        var previous = _requestChanged;
        _requestChanged = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string? ReadString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static bool ValidMethod(string? method) => method is { Length: > 0 and <= 96 }
        && method.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static Task SendErrorAsync(
        WebSocket socket, SemaphoreSlim sendLock, string? id, string code, string message, CancellationToken ct) =>
        SendAsync(socket, sendLock, new
        {
            type = "res", id, ok = false,
            error = new { code, message, details = new { code } }
        }, ct);

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await sendLock.WaitAsync(ct);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { sendLock.Release(); }
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1_048_576)
                throw new InvalidDataException("Fixture expects text requests up to 1 MiB.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }

    private static async Task<WebSocket> UpgradeAsync(NetworkStream stream, CancellationToken ct)
    {
        // Same bounded HTTP upgrade leaf as Shared.Tests LoopbackWebSocketServer.
        // Read exactly through CRLFCRLF so a coalesced first WebSocket frame is not consumed.
        var headers = new List<byte>();
        var next = new byte[1];
        while (headers.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(next.AsMemory(), ct) == 0)
                throw new EndOfStreamException("Connection ended during the fixture WebSocket upgrade.");
            headers.Add(next[0]);
            if (headers.Count >= 4 && headers[^4] == '\r' && headers[^3] == '\n'
                && headers[^2] == '\r' && headers[^1] == '\n')
                break;
        }
        if (headers.Count == 16 * 1024)
            throw new InvalidDataException("Fixture WebSocket headers exceeded 16 KiB.");
        var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var key = lines.FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
        if (lines.Length == 0 || !lines[0].StartsWith("GET / HTTP/1.1", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("Expected a loopback WebSocket upgrade.");
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync();
        lock (_sync)
        {
            foreach (var gate in _historyGates.Values)
                gate.TrySetCanceled(_lifetime.Token);
            _historyGates.Clear();
        }
        await _acceptLoop;
        Task[] connections;
        lock (_sync) connections = _connections.ToArray();
        try { await Task.WhenAll(connections); }
        finally { _lifetime.Dispose(); }
    }
}
