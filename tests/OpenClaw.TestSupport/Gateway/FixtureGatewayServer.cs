using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>Safe diagnostic metadata only. Request IDs, credentials and payloads are never retained.</summary>
public sealed record GatewayFixtureRequest(
    string Method,
    string? SessionKey,
    string Outcome,
    string? Decision = null,
    string? ApprovalId = null);

public enum GatewayFixtureChatPhase
{
    ResponseHeld,
    Streaming,
    DeltasHeld,
    CompletionHeld,
    Completed,
    Aborted,
    Cancelled
}

/// <summary>Fixture-owned correlation and counts only, never caller content or request IDs.</summary>
public sealed record GatewayFixtureChatRun(
    string RunId,
    string SessionKey,
    GatewayFixtureChatPhase Phase,
    int DeltaCount,
    bool FinalPublished);

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
    private readonly HashSet<string> _issuedApprovalIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ActiveConnection> _authenticatedConnections = [];
    private readonly Dictionary<string, TaskCompletionSource> _historyGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _chatResponseGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatDeltaGate> _chatDeltaGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _chatCompletionGates = new(StringComparer.Ordinal);
    private readonly List<ChatRun> _chatRuns = [];
    private TaskCompletionSource _chatChanged = NewSignal();
    private TaskCompletionSource? _sessionMutationGate;
    private TaskCompletionSource? _chatSendGate;
    private readonly TaskCompletionSource _handshake = NewSignal();
    private readonly TaskCompletionSource _accepted = NewSignal();
    private TaskCompletionSource _requestChanged = NewSignal();
    private readonly Task _acceptLoop;
    private Task? _disposal;
    private int _connectionCount;
    private int _activeConnectionCount;
    private int _sharedCredentialConnects;
    private int _deviceTokenRejections;
    private readonly bool _rejectOperatorConnections;
    private int _operatorConnectionRejections;
    private static readonly JsonElement EmptyParameters = JsonSerializer.SerializeToElement(new { });
    private sealed record ActiveConnection(WebSocket Socket, SemaphoreSlim SendLock, CancellationToken CancellationToken);
    private sealed record ChatDeltaGate(TaskCompletionSource Signal, int AfterDeltaCount);
    private sealed class ChatRun(
        GatewayFixtureChatRun observation, int connectionId, string wireRunId, Task? responseGate,
        ChatDeltaGate? deltaGate, Task? completionGate, CancellationToken connectionToken) : IDisposable
    {
        public GatewayFixtureChatRun Observation { get; set; } = observation;
        public int ConnectionId { get; } = connectionId;
        public string WireRunId { get; } = wireRunId;
        public Task? ResponseGate { get; } = responseGate;
        public ChatDeltaGate? DeltaGate { get; } = deltaGate;
        public Task? CompletionGate { get; } = completionGate;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        public SemaphoreSlim Publication { get; } = new(1, 1);

        public void Dispose()
        {
            Stop.Dispose();
            Publication.Dispose();
        }
    }

    public Uri Endpoint { get; }

    /// <summary>
    /// The only device token this fixture accepts. A connect carrying any other
    /// device token is answered with <c>AUTH_DEVICE_TOKEN_MISMATCH</c>, modelling a
    /// gateway whose pairing record no longer matches the caller's stored credential.
    /// When null, every presented device token is rejected.
    /// </summary>
    public string? AcceptedDeviceToken { get; set; }

    /// <summary>
    /// Raised on the server side immediately before a presented device token is
    /// answered with <c>AUTH_DEVICE_TOKEN_MISMATCH</c>. Lets a test change the
    /// authority a caller will re-verify during recovery, so recovery-time
    /// rejection can be exercised through the production connect path.
    /// </summary>
    public Action? DeviceTokenRejected { get; set; }

    /// <summary>Connect attempts that presented no device token, so the shared credential was judged.</summary>
    public int SharedCredentialConnects => Volatile.Read(ref _sharedCredentialConnects);

    /// <summary>Connect attempts whose presented device token was rejected.</summary>
    public int DeviceTokenRejections => Volatile.Read(ref _deviceTokenRejections);
    public int OperatorConnectionRejections => Volatile.Read(ref _operatorConnectionRejections);
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
    public IReadOnlyList<GatewayFixtureChatRun> ChatRuns
    {
        get { lock (_sync) return _chatRuns.Select(run => run.Observation).ToArray(); }
    }

    private FixtureGatewayServer(GatewayScenario scenario, string token, CancellationToken cancellationToken,
        bool rejectOperatorConnections)
    {
        _scenario = scenario;
        _rejectOperatorConnections = rejectOperatorConnections;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = AcceptLoopAsync();
    }

    public static Task<FixtureGatewayServer> StartAsync(
        GatewayScenario scenario, string token, CancellationToken cancellationToken = default,
        bool rejectOperatorConnections = false)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FixtureGatewayServer(scenario, token, cancellationToken, rejectOperatorConnections));
    }

    public async Task CloseConnectionsAsync(CancellationToken cancellationToken = default)
    {
        ActiveConnection[] connections;
        lock (_sync) connections = _authenticatedConnections.Values.ToArray();
        foreach (var connection in connections)
        {
            await connection.SendLock.WaitAsync(cancellationToken);
            try
            {
                if (connection.Socket.State == WebSocketState.Open)
                    await connection.Socket.CloseOutputAsync((WebSocketCloseStatus)1012, "Fixture restart", cancellationToken);
            }
            finally { connection.SendLock.Release(); }
        }
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

    public void HoldSessionMutations()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_sessionMutationGate is not null)
                throw new InvalidOperationException("Session mutations are already held.");
            _sessionMutationGate = NewSignal();
        }
    }

    public void ReleaseSessionMutations()
    {
        lock (_sync)
        {
            var gate = _sessionMutationGate ?? throw new InvalidOperationException("Session mutations are not held.");
            _sessionMutationGate = null;
            gate.TrySetResult();
        }
    }

    public void HoldChatSends()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_chatSendGate is not null)
                throw new InvalidOperationException("Chat sends are already held.");
            _chatSendGate = NewSignal();
        }
    }

    public void ReleaseChatSends()
    {
        lock (_sync)
        {
            var gate = _chatSendGate ??
                throw new InvalidOperationException("Chat sends are not held.");
            _chatSendGate = null;
            gate.TrySetResult();
        }
    }

    /// <summary>Holds chat.send acknowledgements for this known session, without blocking other RPCs.</summary>
    public void HoldChatResponse(string sessionKey)
    {
        RequireChatSession(sessionKey);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_chatResponseGates.TryAdd(sessionKey, NewSignal()))
                throw new InvalidOperationException("Chat responses are already held for this session.");
        }
    }

    public void ReleaseChatResponse(string sessionKey)
    {
        lock (_sync)
        {
            if (!_chatResponseGates.Remove(sessionKey, out var gate))
                throw new InvalidOperationException("Chat responses are not held for this session.");
            gate.TrySetResult();
        }
    }

    /// <summary>Holds before delta zero or one of the fixed two-delta response. No lock is held while waiting.</summary>
    public void HoldChatDeltas(string sessionKey, int afterDeltaCount = 0)
    {
        RequireChatSession(sessionKey);
        ArgumentOutOfRangeException.ThrowIfNegative(afterDeltaCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(afterDeltaCount, 1);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_chatDeltaGates.TryAdd(sessionKey, new ChatDeltaGate(NewSignal(), afterDeltaCount)))
                throw new InvalidOperationException("Chat deltas are already held for this session.");
        }
    }

    public void ReleaseChatDeltas(string sessionKey)
    {
        lock (_sync)
        {
            if (!_chatDeltaGates.Remove(sessionKey, out var gate))
                throw new InvalidOperationException("Chat deltas are not held for this session.");
            gate.Signal.TrySetResult();
        }
    }

    /// <summary>Holds lifecycle end after the final is sent, so interrupted completion can be proved.</summary>
    public void HoldChatCompletion(string sessionKey)
    {
        RequireChatSession(sessionKey);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_chatCompletionGates.TryAdd(sessionKey, NewSignal()))
                throw new InvalidOperationException("Chat completion is already held for this session.");
        }
    }

    public void ReleaseChatCompletion(string sessionKey)
    {
        lock (_sync)
        {
            if (!_chatCompletionGates.Remove(sessionKey, out var gate))
                throw new InvalidOperationException("Chat completion is not held for this session.");
            gate.TrySetResult();
        }
    }

    public async Task<GatewayFixtureChatRun> WaitForChatRunAsync(
        string sessionKey, GatewayFixtureChatPhase phase, int occurrence = 1,
        CancellationToken cancellationToken = default)
    {
        RequireChatSession(sessionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(occurrence);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                var run = _chatRuns.Where(run => run.Observation.SessionKey == sessionKey)
                    .Skip(occurrence - 1).FirstOrDefault();
                if (run?.Observation.Phase == phase)
                    return run.Observation;
                changed = _chatChanged.Task;
            }
            await changed.WaitAsync(wait.Token);
        }
    }

    private void RequireChatSession(string sessionKey)
    {
        if (!_scenario.AllowsChat)
            throw new InvalidOperationException("The controlled-chat scenario is required.");
        if (!_scenario.ContainsSession(sessionKey))
            throw new ArgumentException("Unknown fixture session key.", nameof(sessionKey));
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

    public async Task<GatewayFixtureRequest> WaitForCompletedRequestAsync(
        string method,
        string? sessionKey = null,
        int occurrence = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(occurrence);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                var request = _requests
                    .Where(request => request.Method == method
                        && (sessionKey is null || request.SessionKey == sessionKey)
                        && request.Outcome != "pending")
                    .Skip(occurrence - 1)
                    .FirstOrDefault();
                if (request is not null)
                    return request;
                changed = _requestChanged.Task;
            }
            await changed.WaitAsync(wait.Token);
        }
    }

    public async Task PublishAgentEventAsync(
        string sessionKey,
        object data,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(data);
        ActiveConnection[] targets;
        lock (_sync)
            targets = _authenticatedConnections
                .OrderByDescending(pair => pair.Key)
                .Select(pair => pair.Value)
                .Where(connection => connection.Socket.State == WebSocketState.Open)
                .ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("No authenticated fixture connection is available.");

        var eventData = JsonSerializer.SerializeToElement(data);
        var approvalId = ReadString(eventData, "approvalId");
        if (ReadString(eventData, "phase") == "requested"
            && approvalId is { Length: <= 64 }
            && approvalId.StartsWith("fixture-", StringComparison.Ordinal))
        {
            lock (_sync)
                _issuedApprovalIds.Add(approvalId);
        }

        var message = new
        {
            type = "event",
            @event = "agent",
            payload = new
            {
                runId = "",
                seq = 0,
                stream = "approval",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionKey,
                data = eventData,
            },
        };
        foreach (var target in targets)
        {
            try
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    target.CancellationToken);
                await SendAsync(target.Socket, target.SendLock, message, send.Token);
                return;
            }
            catch (OperationCanceledException) when (target.CancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (WebSocketException) { }
        }

        throw new InvalidOperationException("Authenticated fixture connections closed before the event was sent.");
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
                    type = "event",
                    @event = "connect.challenge",
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
                    var requestIndex = Record(
                        method!,
                        _scenario.ContainsSession(key ?? "") ? key : key is null ? null : "<unknown>",
                        SafeApprovalDecision(method!, parameters),
                        SafeApprovalId(method!, parameters));
                    if (!authenticated || method == "connect")
                    {
                        try
                        {
                            if (authenticated)
                                throw new FixtureRequestException("INVALID_REQUEST", "Operator is already connected.");
                            if (method != "connect")
                                throw new FixtureRequestException("AUTH_REQUIRED", "Operator connect is required before reading fixture data.");
                            if (_rejectOperatorConnections)
                            {
                                Interlocked.Increment(ref _operatorConnectionRejections);
                                throw new FixtureRequestException("UNAVAILABLE", "Synthetic Gateway is unavailable for this startup scenario.");
                            }
                            Authenticate(parameters, nonce);
                            await SendAsync(socket, sendLock, new
                            {
                                type = "res",
                                id,
                                ok = true,
                                payload = _scenario.CreateHello($"fixture-connection-{connectionId}")
                            }, connection.Token);
                            authenticated = true;
                            lock (_sync)
                                _authenticatedConnections[connectionId] =
                                    new ActiveConnection(socket, sendLock, connection.Token);
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
                        gate = method is "sessions.patch" or "sessions.delete"
                            ? _sessionMutationGate?.Task
                            : method == "chat.send"
                                ? _chatSendGate?.Task
                            : method == "chat.history" && key is not null && _historyGates.TryGetValue(key, out var held)
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
                    lock (_sync)
                    {
                        _subscriptions.Remove(connectionId);
                        _authenticatedConnections.Remove(connectionId);
                        foreach (var run in _chatRuns.Where(run => run.ConnectionId == connectionId))
                            run.Dispose();
                    }
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
                if (_scenario.AllowsChat && method == "chat.send")
                {
                    var sessionKey = _scenario.ValidateChatSend(parameters);
                    await RespondChatSendAsync(socket, sendLock, id, sessionKey, ReadString(parameters, "idempotencyKey")!,
                        requestIndex, connectionId, cancellationToken);
                    return;
                }
                if (method == "exec.approval.resolve")
                {
                    var approvalId = ReadString(parameters, "id");
                    lock (_sync)
                    {
                        if (approvalId is null || !_issuedApprovalIds.Contains(approvalId))
                            throw new FixtureRequestException(
                                "INVALID_PARAMS",
                                "Approval ID was not issued by the fixture.");
                    }
                }
                payload = _scenario.AllowsChat && method == "chat.abort"
                    ? await AbortChatRunAsync(socket, sendLock, parameters, connectionId, cancellationToken)
                    : _scenario.Respond(method, parameters);
                if (method == "exec.approval.resolve")
                {
                    var approvalId = ReadString(parameters, "id");
                    lock (_sync)
                    {
                        if (approvalId is null || !_issuedApprovalIds.Remove(approvalId))
                            throw new FixtureRequestException(
                                "INVALID_PARAMS",
                                "Approval ID was already consumed by the fixture.");
                    }
                }
            }
            catch (FixtureRequestException ex)
            {
                if (gate is not null)
                    await gate.WaitAsync(cancellationToken);
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

    private async Task RespondChatSendAsync(
        WebSocket socket, SemaphoreSlim sendLock, string id, string sessionKey, string wireRunId,
        int requestIndex, int connectionId, CancellationToken cancellationToken)
    {
        ChatRun run;
        lock (_sync)
        {
            if (_chatRuns.Any(existing => existing.WireRunId == wireRunId))
                throw new FixtureRequestException("INVALID_PARAMS", "This fixture does not model idempotency replay.");
            _chatResponseGates.TryGetValue(sessionKey, out var responseGate);
            _chatDeltaGates.TryGetValue(sessionKey, out var deltaGate);
            _chatCompletionGates.TryGetValue(sessionKey, out var completionGate);
            run = new ChatRun(new GatewayFixtureChatRun(
                $"fixture-chat-run-{_chatRuns.Count + 1}", sessionKey,
                responseGate is null ? GatewayFixtureChatPhase.Streaming : GatewayFixtureChatPhase.ResponseHeld,
                0, false), connectionId, wireRunId, responseGate?.Task, deltaGate, completionGate?.Task, cancellationToken);
            _chatRuns.Add(run);
            SignalChatChanged();
        }

        try
        {
            if (run.ResponseGate is not null)
                await run.ResponseGate.WaitAsync(run.Stop.Token);
            await run.Publication.WaitAsync(cancellationToken);
            try
            {
                run.Stop.Token.ThrowIfCancellationRequested();
                await SendAsync(socket, sendLock, new
                {
                    type = "res",
                    id,
                    ok = true,
                    payload = new { runId = run.WireRunId, sessionKey, status = "started" }
                }, cancellationToken);
                Complete(requestIndex, "ok");
            }
            finally { run.Publication.Release(); }

            SetChatPhase(run, GatewayFixtureChatPhase.Streaming);
            await PublishChatAgentAsync(run, socket, sendLock, 1, "lifecycle", new { phase = "start" }, cancellationToken);
            var deltas = GatewayScenario.ChatDeltas(sessionKey);
            for (var index = 0; index < deltas.Count; index++)
            {
                if (run.DeltaGate?.AfterDeltaCount == index)
                {
                    SetChatPhase(run, GatewayFixtureChatPhase.DeltasHeld);
                    await run.DeltaGate.Signal.Task.WaitAsync(run.Stop.Token);
                    SetChatPhase(run, GatewayFixtureChatPhase.Streaming);
                }
                await PublishChatAgentAsync(run, socket, sendLock, index + 2, "assistant",
                    new { delta = deltas[index] }, cancellationToken);
                lock (_sync)
                {
                    run.Observation = run.Observation with { DeltaCount = index + 1 };
                    SignalChatChanged();
                }
            }

            await run.Publication.WaitAsync(cancellationToken);
            try
            {
                run.Stop.Token.ThrowIfCancellationRequested();
                await SendAsync(socket, sendLock, new
                {
                    type = "event",
                    @event = "chat",
                    payload = new
                    {
                        runId = run.WireRunId,
                        sessionKey,
                        seq = 4,
                        state = "final",
                        message = new
                        {
                            role = "assistant",
                            content = new[] { new { type = "text", text = string.Concat(deltas) } },
                            timestamp = 1_787_227_200_000L
                        }
                    }
                }, cancellationToken);
                lock (_sync)
                {
                    run.Observation = run.Observation with { FinalPublished = true };
                    SignalChatChanged();
                }
            }
            finally { run.Publication.Release(); }

            if (run.CompletionGate is not null)
            {
                SetChatPhase(run, GatewayFixtureChatPhase.CompletionHeld);
                await run.CompletionGate.WaitAsync(run.Stop.Token);
            }
            await PublishChatAgentAsync(run, socket, sendLock, 5, "lifecycle", new { phase = "end" }, cancellationToken);
            SetChatPhase(run, GatewayFixtureChatPhase.Completed);
        }
        catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
        {
            SetChatPhase(run, GatewayFixtureChatPhase.Cancelled);
            lock (_sync)
                if (_requests[requestIndex].Outcome == "pending")
                    Complete(requestIndex, "cancelled");
        }
        catch (WebSocketException) when (socket.State != WebSocketState.Open)
        {
            SetChatPhase(run, GatewayFixtureChatPhase.Cancelled);
            lock (_sync)
                if (_requests[requestIndex].Outcome == "pending")
                    Complete(requestIndex, "disconnected");
        }
    }

    private async Task<object> AbortChatRunAsync(
        WebSocket socket, SemaphoreSlim sendLock, JsonElement parameters, int connectionId,
        CancellationToken cancellationToken)
    {
        var sessionKey = _scenario.ValidateChatAbort(parameters);
        var runId = ReadString(parameters, "runId");
        ChatRun run;
        lock (_sync)
            run = _chatRuns.FirstOrDefault(run => run.WireRunId == runId
                && run.Observation.SessionKey == sessionKey && run.ConnectionId == connectionId)
                ?? throw new FixtureRequestException("INVALID_PARAMS", "Run does not belong to this fixture session and connection.");

        await run.Publication.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
            {
                if (run.Observation.FinalPublished ||
                    run.Observation.Phase is GatewayFixtureChatPhase.Completed or GatewayFixtureChatPhase.Aborted or GatewayFixtureChatPhase.Cancelled)
                    throw new FixtureRequestException("INVALID_PARAMS", "Fixture run is not active.");
                run.Observation = run.Observation with { Phase = GatewayFixtureChatPhase.Aborted };
                SignalChatChanged();
            }
            // Cancel gate waits, not an in-flight WebSocket send. Publication never owns a gate wait.
            await run.Stop.CancelAsync();
            await SendAsync(socket, sendLock, new
            {
                type = "event", @event = "chat",
                payload = new
                {
                    runId = run.WireRunId, sessionKey, seq = run.Observation.DeltaCount + 2,
                    state = "aborted", stopReason = "rpc"
                }
            }, cancellationToken);
            return new { ok = true, aborted = true, runIds = new[] { run.WireRunId } };
        }
        finally { run.Publication.Release(); }
    }

    private async Task PublishChatAgentAsync(
        ChatRun run, WebSocket socket, SemaphoreSlim sendLock, int sequence, string stream,
        object data, CancellationToken cancellationToken)
    {
        await run.Publication.WaitAsync(cancellationToken);
        try
        {
            run.Stop.Token.ThrowIfCancellationRequested();
            await SendChatAgentAsync(run, socket, sendLock, sequence, stream, data, cancellationToken);
        }
        finally { run.Publication.Release(); }
    }

    private static Task SendChatAgentAsync(
        ChatRun run, WebSocket socket, SemaphoreSlim sendLock, int sequence, string stream,
        object data, CancellationToken cancellationToken) =>
        SendAsync(socket, sendLock, new
        {
            type = "event",
            @event = "agent",
            payload = new
            {
                runId = run.WireRunId,
                sessionKey = run.Observation.SessionKey,
                seq = sequence,
                stream,
                ts = 1_787_227_200_000L + sequence,
                data
            }
        }, cancellationToken);

    private void SetChatPhase(ChatRun run, GatewayFixtureChatPhase phase)
    {
        lock (_sync)
        {
            if (run.Observation.Phase is GatewayFixtureChatPhase.Aborted or GatewayFixtureChatPhase.Cancelled)
                return;
            run.Observation = run.Observation with { Phase = phase };
            SignalChatChanged();
        }
    }

    private void SignalChatChanged()
    {
        var previous = _chatChanged;
        _chatChanged = NewSignal();
        previous.TrySetResult();
    }

    private void Authenticate(JsonElement p, string nonce)
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw new FixtureRequestException("INVALID_PARAMS", "connect params must be an object.");
        p.TryGetProperty("auth", out var auth);
        var presentedDeviceToken = auth.ValueKind == JsonValueKind.Object ? ReadString(auth, "deviceToken") : null;
        if (!string.IsNullOrEmpty(presentedDeviceToken))
        {
            // A paired caller sends its device token alone, so it is the sole credential to judge.
            if (AcceptedDeviceToken is null || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(AcceptedDeviceToken)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(presentedDeviceToken))))
            {
                Interlocked.Increment(ref _deviceTokenRejections);
                DeviceTokenRejected?.Invoke();
                throw new FixtureRequestException("AUTH_DEVICE_TOKEN_MISMATCH", "Unauthorized: device token mismatch.");
            }
        }
        else
        {
            Interlocked.Increment(ref _sharedCredentialConnects);
            if (auth.ValueKind != JsonValueKind.Object || ReadString(auth, "token") is not { } token
                || !CryptographicOperations.FixedTimeEquals(_tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(token))))
                throw new FixtureRequestException("AUTH_TOKEN_MISMATCH", "Unauthorized: fixture token mismatch.");
        }
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

    private int Record(
        string method,
        string? key,
        string? decision = null,
        string? approvalId = null)
    {
        lock (_sync)
        {
            var index = _requests.Count;
            _requests.Add(new GatewayFixtureRequest(method, key, "pending", decision, approvalId));
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
    private static string? SafeApprovalDecision(string method, JsonElement parameters)
    {
        if (method != "exec.approval.resolve")
            return null;
        var decision = ReadString(parameters, "decision");
        return decision is "allow-once" or "allow-always" or "deny"
            ? decision
            : "<unknown>";
    }

    private static string? SafeApprovalId(string method, JsonElement parameters)
    {
        if (method != "exec.approval.resolve")
            return null;
        var approvalId = ReadString(parameters, "id");
        return approvalId is { Length: <= 64 } && approvalId.StartsWith("fixture-", StringComparison.Ordinal)
            ? approvalId
            : "<unknown>";
    }

    private static string? ReadString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static bool ValidMethod(string? method) => method is { Length: > 0 and <= 96 }
        && method.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static Task SendErrorAsync(
        WebSocket socket, SemaphoreSlim sendLock, string? id, string code, string message, CancellationToken ct) =>
        SendAsync(socket, sendLock, new
        {
            type = "res",
            id,
            ok = false,
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
            _sessionMutationGate?.TrySetCanceled();
            _sessionMutationGate = null;
            _chatSendGate?.TrySetCanceled();
            _chatSendGate = null;
            foreach (var gate in _chatResponseGates.Values)
                gate.TrySetCanceled(_lifetime.Token);
            _chatResponseGates.Clear();
            foreach (var gate in _chatDeltaGates.Values)
                gate.Signal.TrySetCanceled(_lifetime.Token);
            _chatDeltaGates.Clear();
            foreach (var gate in _chatCompletionGates.Values)
                gate.TrySetCanceled(_lifetime.Token);
            _chatCompletionGates.Clear();
        }
        await _acceptLoop;
        Task[] connections;
        lock (_sync) connections = _connections.ToArray();
        try { await Task.WhenAll(connections); }
        finally { _lifetime.Dispose(); }
    }
}
