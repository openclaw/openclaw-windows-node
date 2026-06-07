using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Abstract base class for WebSocket-based gateway clients.
/// Extracts shared connection lifecycle: connect, listen, reconnect, send, dispose.
/// Subclasses implement message processing and provide configuration via abstract members.
/// </summary>
public abstract class WebSocketClientBase : IDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly string _gatewayUrl;
    private readonly string? _credentials;
    private CancellationTokenSource _cts;
    private CancellationTokenSource? _connectionCts;
    private volatile bool _disposed;
    // Set via Interlocked.Exchange at the top of Dispose so only the first caller proceeds;
    // any concurrent Dispose call observes 1 and returns. Read by ConnectAsync/reconnect
    // gates as the intent-to-dispose signal so they bail out before installing a new socket
    // or calling subclass OnConnectedAsync. _disposed (flipped after OnDisposing returns)
    // preserves the existing "OnDisposing observes live state" contract for subclasses.
    private int _disposingFlag;
    private bool _disposing => Volatile.Read(ref _disposingFlag) != 0;
    private int _reconnectAttempts;
    private int _reconnectLoopActive;
    private int _connectInProgress;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };

    protected readonly string _token;
    protected readonly IOpenClawLogger _logger;

    /// <summary>Gateway URL with credentials stripped, safe for logging/display.</summary>
    protected string GatewayUrlForDisplay { get; }

    /// <summary>Whether Dispose has been called.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>Whether the WebSocket is currently open and connected.</summary>
    protected bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>Cancellation token tied to this client's lifetime.</summary>
    protected CancellationToken CancellationToken => _cts.Token;

    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<string>? AuthenticationFailed;

    /// <summary>Reset reconnect backoff counter. Call after successful application-level handshake.</summary>
    protected void ResetReconnectAttempts() => _reconnectAttempts = 0;

    /// <summary>Fire AuthenticationFailed event and stop auto-reconnect.</summary>
    protected void RaiseAuthenticationFailed(string message)
    {
        _logger.Warn($"{ClientRole} authentication failed: {message}");
        AuthenticationFailed?.Invoke(this, message);
    }

    // --- Abstract members (subclass MUST implement) ---

    /// <summary>
    /// Process a received WebSocket text message. Called from the listen loop.
    /// Gateway wraps its sync ProcessMessage with Task.CompletedTask;
    /// Node directly uses its async implementation.
    /// </summary>
    protected abstract Task ProcessMessageAsync(string json);

    /// <summary>Receive buffer size in bytes. Gateway: 16384, Node: 65536.</summary>
    protected abstract int ReceiveBufferSize { get; }

    /// <summary>Client role for log messages, e.g. "gateway" or "node".</summary>
    protected abstract string ClientRole { get; }

    // --- Virtual hooks (subclass MAY override) ---

    /// <summary>Called after WebSocket connects, before the listen loop starts.</summary>
    protected virtual Task OnConnectedAsync() => Task.CompletedTask;

    /// <summary>Called when the server closes the connection or it drops.</summary>
    protected virtual void OnDisconnected() { }

    /// <summary>Called on unrecoverable listen-loop errors.</summary>
    protected virtual void OnError(Exception ex) { }

    /// <summary>Called at the start of Dispose, before CTS cancellation.</summary>
    protected virtual void OnDisposing() { }

    /// <summary>
    /// Whether auto-reconnect should run after an unexpected disconnect.
    /// Subclasses can return false for known terminal states (for example awaiting pairing approval).
    /// </summary>
    protected virtual bool ShouldAutoReconnect() => true;

    /// <summary>
    /// Interval between client-initiated application-level ping requests. Return <c>null</c>
    /// (the default) to disable. The base class drives the loop and calls
    /// <see cref="SendApplicationPingAsync"/> on each tick; if that returns false (or throws,
    /// or times out per <see cref="ApplicationPingTimeout"/>) the WebSocket is aborted to
    /// force reconnect. This is the belt-and-braces companion to <c>KeepAliveTimeout</c>:
    /// it catches cases where the protocol is alive but the gateway has stopped processing
    /// application traffic.
    /// </summary>
    protected virtual TimeSpan? ApplicationPingInterval => null;

    /// <summary>Maximum time to wait for a pong before declaring the connection dead.</summary>
    protected virtual TimeSpan ApplicationPingTimeout => TimeSpan.FromSeconds(15);

    /// <summary>
    /// Send a single application-level ping and await its response. Return true on success,
    /// false (or throw) to signal a failed liveness check. Default implementation is a no-op.
    /// Subclasses opt in by overriding both this and <see cref="ApplicationPingInterval"/>.
    /// </summary>
    protected virtual Task<bool> SendApplicationPingAsync(CancellationToken ct) => Task.FromResult(true);

    protected WebSocketClientBase(string gatewayUrl, string token, IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrEmpty(gatewayUrl))
            throw new ArgumentException("Gateway URL is required.", nameof(gatewayUrl));
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("Token is required.", nameof(token));

        _gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        GatewayUrlForDisplay = GatewayUrlHelper.SanitizeForDisplay(_gatewayUrl);
        _token = token;
        _credentials = GatewayUrlHelper.ExtractCredentials(gatewayUrl);
        _logger = logger ?? NullLogger.Instance;
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync()
    {
        if (_disposed || _disposing)
        {
            _logger.Debug($"Skipping {ClientRole} connect: client disposed or disposing");
            return;
        }

        // Serialize ConnectAsync. Without this guard, concurrent connects (auto-reconnect
        // loop + manual UI "Reconnect") would race on the _webSocket field assignment and the
        // listen loop could observe a partly-constructed socket — surfacing as
        // "Already one outstanding ReceiveAsync" thrown into the generic catch in
        // ListenForMessagesAsync (which does NOT call OnDisconnected).
        if (Interlocked.CompareExchange(ref _connectInProgress, 1, 0) != 0)
        {
            _logger.Debug($"{ClientRole} connect already in progress; skipping concurrent call");
            return;
        }

        ClientWebSocket? newWebSocket = null;
        CancellationTokenSource? connectionCts = null;
        bool ownershipTransferred = false;
        try
        {
            // Subscriber exceptions here must not skip the connect attempt or surface to the
            // caller, which would otherwise look like a connect failure and trigger reconnect.
            try { RaiseStatusChanged(ConnectionStatus.Connecting); } catch { }
            _logger.Info($"Connecting to {ClientRole}: {GatewayUrlForDisplay}");

            // Replace any prior per-connection token — cancelling it terminates the
            // previous socket's heartbeat task before we spawn a new one for the new socket.
            // Use Interlocked.Exchange to make the swap atomic against a concurrent ConnectAsync —
            // without it, two connects could race the field assignment and the loser's
            // locally-captured CTS could be cancelled/disposed by the winner.
            connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var priorCts = Interlocked.Exchange(ref _connectionCts, connectionCts);
            try { priorCts?.Cancel(); priorCts?.Dispose(); } catch { }
            var connectionToken = connectionCts.Token;

            // Re-check _disposed after installing per-connection state. A racing
            // Dispose between the early _disposed check and now would otherwise leak the
            // freshly-constructed CTS (and below, the freshly-constructed _webSocket).
            if (_disposed || _disposing)
            {
                _logger.Debug($"{ClientRole} connect aborted: disposed/disposing during setup");
                return; // finally cleans up local CTS / socket
            }

            newWebSocket = new ClientWebSocket();
            newWebSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            // KeepAliveInterval alone only schedules outbound WS protocol pings; it does not
            // enforce that pongs arrive. Without KeepAliveTimeout (.NET 9+) a half-open TCP
            // (NAT idle drop, VPN reconnect, captive proxy) can leave the socket "Open"
            // indefinitely with ReceiveAsync blocking forever and no reconnect ever firing.
            // This timeout tells .NET to abort the socket if a pong is not received in time.
            newWebSocket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(60);

            // Set Origin header (convert ws/wss to http/https)
            var uri = new Uri(_gatewayUrl);
            var originScheme = uri.Scheme == "wss" ? "https" : "http";
            var origin = $"{originScheme}://{uri.Host}:{uri.Port}";
            newWebSocket.Options.SetRequestHeader("Origin", origin);

            if (!string.IsNullOrEmpty(_credentials))
            {
                var credentialsToEncode = GatewayUrlHelper.DecodeCredentials(_credentials);
                newWebSocket.Options.SetRequestHeader(
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentialsToEncode))}");
            }

            // Swap _webSocket atomically and dispose the prior socket. The
            // re-entrancy guard above prevents two ConnectAsync calls from racing here,
            // but ReconnectWithBackoffAsync also pre-disposes the old socket — that's
            // intentional (no-op double-dispose is harmless) and keeps the old code
            // path's behavior unchanged for non-ConnectAsync callers.
            var priorSocket = Interlocked.Exchange(ref _webSocket, newWebSocket);
            try { priorSocket?.Dispose(); } catch { }
            ownershipTransferred = true;

            // Final disposed check after socket install — Dispose may have run after the
            // earlier check and Cleared _connectionCts; in that case bail without spawning loops.
            // If _disposing is set but Dispose has not yet reached _webSocket Exchange, the
            // newWebSocket we just installed will not be picked up by Dispose; reverse the
            // install ourselves so it does not orphan in the field. Abort first (TCP RST if
            // past handshake; no-op pre-handshake) then Dispose, mirroring the orphan-clean
            // else-if branch below.
            if (_disposed || _disposing)
            {
                _logger.Debug($"{ClientRole} connect aborted: disposed/disposing after socket install");
                var leaked = Interlocked.CompareExchange(ref _webSocket, null, newWebSocket);
                if (ReferenceEquals(leaked, newWebSocket))
                {
                    try { newWebSocket.Abort(); } catch { }
                    try { newWebSocket.Dispose(); } catch { }
                }
                return;
            }

            await newWebSocket.ConnectAsync(uri, connectionToken);

            // Don't reset _reconnectAttempts here — TCP connect succeeding doesn't mean
            // auth will succeed. Reset only after the full application-level handshake
            // completes (subclass calls ResetReconnectAttempts after hello-ok).
            _logger.Info($"{ClientRole} connected, waiting for challenge...");

            await OnConnectedAsync();

            // OnConnectedAsync can yield long enough for Dispose to set _disposing and run
            // synchronous OnDisposing. _cts.Cancel() happens only AFTER OnDisposing returns,
            // so connectionToken may still be live here even though the client is mid-teardown.
            // Check the dispose intent flag explicitly before scheduling background loops so we
            // do not start a listener/heartbeat that would race the dispose path.
            if (_disposed || _disposing)
            {
                _logger.Debug($"{ClientRole} connect aborted: disposed/disposing after OnConnectedAsync");
                return;
            }

            // Capture both the socket and the per-connection CTS as locals and pass them
            // into the loops. Inside the loops, code must use ONLY these captures — never
            // re-read _webSocket or _cts. A reconnect can Interlocked.Exchange a successor
            // socket into the field while this loop is still running against the prior one;
            // reading the field would make us Abort the successor or observe its state.
            var capturedSocket = newWebSocket;
            var capturedConnectionCts = connectionCts;
            _ = Task.Run(() => ListenForMessagesAsync(capturedSocket, capturedConnectionCts), connectionToken);

            if (ApplicationPingInterval is { } pingInterval && pingInterval > TimeSpan.Zero)
            {
                _ = Task.Run(() => HeartbeatLoopAsync(capturedSocket, pingInterval, connectionToken), connectionToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug($"{ClientRole} connect canceled (likely shutdown)");
        }
        catch (ObjectDisposedException)
        {
            _logger.Debug($"{ClientRole} connect aborted after dispose");
        }
        catch (Exception ex)
        {
            // Each event raise is individually guarded so a throwing logger or subscriber
            // can't skip the others and, more importantly, can't skip the reconnect kickoff
            // below — that would leave the client permanently disconnected with no further
            // retry. Symmetric to the listener finally's hardening.
            try { _logger.Error($"{ClientRole} connection failed", ex); } catch { }
            try { RaiseStatusChanged(ConnectionStatus.Error); } catch { }

            if (!_disposed && !_disposing && !_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
            {
                _ = ReconnectWithBackoffAsync();
            }
            else if (ownershipTransferred && !_disposed)
            {
                // No reconnect will run, so the failed socket would otherwise orphan in
                // _webSocket — no listener was started (Task.Run lines never reached) and
                // no future ConnectAsync will Interlocked.Exchange it away. Clear and
                // dispose it now if it's still ours. Same for the per-connection CTS.
                var orphaned = Interlocked.CompareExchange(ref _webSocket, null, newWebSocket);
                if (ReferenceEquals(orphaned, newWebSocket))
                {
                    // Abort first so a post-handshake socket sends a TCP RST instead of leaving
                    // the gateway holding a phantom session entry until its own idle timeout.
                    // ClientWebSocket.Dispose() on an open socket aborts the transport but does
                    // not send a WebSocket close frame; Abort() is the explicit-intent equivalent.
                    try { newWebSocket?.Abort(); } catch { }
                    try { newWebSocket?.Dispose(); } catch { }
                }
                var orphanCts = Interlocked.CompareExchange(ref _connectionCts, null, connectionCts);
                if (ReferenceEquals(orphanCts, connectionCts))
                {
                    try { connectionCts?.Cancel(); } catch { }
                    try { connectionCts?.Dispose(); } catch { }
                }
            }
        }
        finally
        {
            // If we aborted before transferring ownership to fields, dispose the
            // local socket/CTS so they don't leak. After ownership transfer, Dispose() or
            // the next ConnectAsync owns them.
            if (!ownershipTransferred)
            {
                try { newWebSocket?.Dispose(); } catch { }
                // The CTS was already installed into the field via Exchange; only dispose
                // it locally if it's still the current field value (meaning Dispose hasn't
                // already swung past it).
                var current = Interlocked.CompareExchange(ref _connectionCts, null, connectionCts);
                if (ReferenceEquals(current, connectionCts))
                {
                    try { connectionCts?.Cancel(); } catch { }
                    try { connectionCts?.Dispose(); } catch { }
                }
            }
            Interlocked.Exchange(ref _connectInProgress, 0);
        }
    }

    private async Task ListenForMessagesAsync(ClientWebSocket ws, CancellationTokenSource listenerCts)
    {
        // Use ONLY the captured ws and listenerCts. Re-reading _webSocket / _cts here
        // would TOCTOU against a reconnect that has already swapped in a successor socket.
        var listenerToken = listenerCts.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var sb = new StringBuilder();

        try
        {
            while (ws.State == WebSocketState.Open && !listenerToken.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(
                    new ArraySegment<byte>(buffer, 0, ReceiveBufferSize), listenerToken);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage && sb.Length == 0)
                    {
                        // Fast path: single-frame message — decode directly, skip StringBuilder round-trip
                        await ProcessMessageAsync(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    else
                    {
                        // Multi-frame path: accumulate until EndOfMessage
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (result.EndOfMessage)
                        {
                            await ProcessMessageAsync(sb.ToString());
                            sb.Clear();
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeStatus = ws.CloseStatus?.ToString() ?? "unknown";
                    var closeDesc = ws.CloseStatusDescription ?? "no description";
                    _logger.Info($"Server closed connection: {closeStatus} - {closeDesc}");
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.Warn("Connection closed prematurely");
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { /* WebSocket disposed during shutdown */ }
        catch (Exception ex)
        {
            try { _logger.Error($"{ClientRole} listen error", ex); } catch { }
            try { OnError(ex); } catch { }
            try { RaiseStatusChanged(ConnectionStatus.Error); } catch { }
        }
        finally
        {
            // Atomically claim ownership of this connection's teardown. All exit paths
            // (Close frame, premature close, swallowed OCE/ODE, generic Exception) funnel
            // through this single CAS so OnDisconnected / status / reconnect cannot fire
            // for a stale listener whose socket was already replaced by a successor
            // ConnectAsync. The CAS atomically null-out is the source of truth: only the
            // path that swings _webSocket from `ws` to null owns the teardown.
            //
            // The entire teardown lives in finally so a throwing event handler
            // (OnError / RaiseStatusChanged) cannot leak the buffer or skip the CAS,
            // which would otherwise leave _webSocket pointing at a dead socket with
            // no Disconnected event and no reconnect ever scheduled.
            ArrayPool<byte>.Shared.Return(buffer);
            try { listenerCts.Cancel(); } catch { }

            // Gate event emission and reconnect on both _disposed AND _disposing so a
            // listener exiting mid-Dispose (e.g. subclass OnDisposing blocks on a graceful
            // close handshake that the server responds to before _disposed flips) does not
            // fire spurious OnDisconnected/Disconnected callbacks into a subclass that is
            // already in teardown, or schedule a reconnect that ReconnectWithBackoffAsync
            // would only bail out of after the loop entry check. The CAS still runs so the
            // socket reference is cleared either by us here or by Dispose's later Exchange.
            bool ownedExit = false;
            if (!_disposed && !_disposing
                && Interlocked.CompareExchange(ref _webSocket, null, ws) == ws)
            {
                ownedExit = true;
                try { ws.Dispose(); } catch { }
                if (!_disposed && !_disposing)
                {
                    try { OnDisconnected(); } catch { }
                    try { RaiseStatusChanged(ConnectionStatus.Disconnected); } catch { }
                }
            }

            // Only the owning listener may trigger reconnect. A stale listener whose CAS
            // failed must not spawn a reconnect loop against the healthy successor.
            // Fire-and-forget so the listener Task completes promptly; awaiting here
            // would make any escape from the reconnect loop an unobserved Task exception.
            if (ownedExit && !_disposed && !_disposing
                && !_cts.Token.IsCancellationRequested
                && ShouldAutoReconnect())
            {
                _ = ReconnectWithBackoffAsync();
            }
        }
    }

    protected async Task ReconnectWithBackoffAsync()
    {
        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (!_disposed && !_disposing && !_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
            {
                var baseDelay = BackoffMs[Math.Min(_reconnectAttempts, BackoffMs.Length - 1)];
                // Centered ±25% jitter (was additive 0-25%, i.e. always >= base). With operator
                // and node sharing this schedule, a tight one-sided jitter window means both
                // reconnect in near-lockstep after every drop, amplifying load on the gateway
                // during incidents. Centering around the base value spreads the retry storm
                // without materially extending the reconnect time.
                var delay = (int)(baseDelay * (0.75 + Random.Shared.NextDouble() * 0.5));
                _reconnectAttempts++;
                _logger.Warn($"{ClientRole} reconnecting in {delay}ms (attempt {_reconnectAttempts})");
                // Guard the raise — a throwing subscriber must not abort the reconnect loop.
                try { RaiseStatusChanged(ConnectionStatus.Connecting); } catch { }

                await Task.Delay(delay, _cts.Token);

                if (_cts.Token.IsCancellationRequested || _disposed || _disposing || !ShouldAutoReconnect())
                {
                    break;
                }

                // Don't manually null/dispose _webSocket here — ConnectAsync's
                // Interlocked.Exchange handles atomic replacement and prior-socket disposal.
                // The old pre-disposal pattern raced against a concurrent ConnectAsync that
                // had already swapped in a new socket: we'd null out the brand-new field.
                await ConnectAsync();

                if (IsConnected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { _logger.Error($"{ClientRole} reconnect failed", ex); } catch { }
            try { RaiseStatusChanged(ConnectionStatus.Error); } catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectLoopActive, 0);
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, TimeSpan interval, CancellationToken ct)
    {
        // Use ONLY the captured ws — never re-read _webSocket. A reconnect can have already
        // installed a successor in the field; aborting that would kill a healthy connection.
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try { await Task.Delay(interval, ct); }
                catch (OperationCanceledException) { return; }

                if (ct.IsCancellationRequested || ws.State != WebSocketState.Open)
                    return;

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(ApplicationPingTimeout);

                bool ok;
                try
                {
                    ok = await SendApplicationPingAsync(pingCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    ok = false;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"{ClientRole} application ping threw: {ex.Message}");
                    ok = false;
                }

                if (ct.IsCancellationRequested) return;

                if (!ok)
                {
                    _logger.Warn($"{ClientRole} application ping failed; aborting socket to force reconnect");
                    // Only abort if the field still references OUR socket. If a reconnect has
                    // already replaced it, the new socket is healthy and we must not touch it.
                    if (ReferenceEquals(Volatile.Read(ref _webSocket), ws))
                    {
                        try { ws.Abort(); } catch { }
                    }
                    return;
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Send a text message over the WebSocket, observing only the client lifetime.</summary>
    protected Task SendRawAsync(string message) => SendRawAsync(message, CancellationToken.None);

    /// <summary>
    /// Send a text message over the WebSocket. Thread-safe. <paramref name="ct"/> is
    /// linked with the client lifetime token; cancellation propagates to BOTH the
    /// <see cref="_sendLock"/> wait and the underlying <see cref="ClientWebSocket.SendAsync"/>.
    /// Callers that need per-send cancellation must use this overload — wrapping the
    /// parameterless version in <c>.WaitAsync(ct)</c> only observes the outer task and
    /// leaves the underlying send orphaned (still holding the lock, still blocking on a
    /// half-dead socket); the orphan can later land on a successor socket after reconnect.
    /// </summary>
    protected async Task SendRawAsync(string message, CancellationToken ct)
    {
        CancellationTokenSource? linked = null;
        CancellationToken token;
        if (ct.CanBeCanceled)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
            token = linked.Token;
        }
        else
        {
            token = _cts.Token;
        }

        try
        {
            try
            {
                await _sendLock.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // ct timed out waiting for lock OR shutdown raced. Propagate so heartbeat
                // path can treat this as a failed ping (returns false, triggers Abort) —
                // earlier code swallowed this and looked like a success.
                if (ct.IsCancellationRequested) throw;
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                // Serialize sends; reconnect/dispose can still close the captured socket,
                // so the send below keeps the existing state-change guards.
                var ws = _webSocket;
                if (ws is null) return;

                try
                {
                    // Read State inside the ODE-catching try: WebSocket.State is virtual and
                    // not contractually safe against a concurrent Dispose; reading it outside
                    // this catch would propagate ObjectDisposedException to the caller.
                    if (ws.State != WebSocketState.Open) return;

                    // Rent a pooled buffer to avoid per-send heap allocations on the hot send path.
                    var byteCount = Encoding.UTF8.GetByteCount(message);
                    var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
                    try
                    {
                        var written = Encoding.UTF8.GetBytes(message, buffer);
                        await ws.SendAsync(buffer.AsMemory(0, written),
                            WebSocketMessageType.Text, true, token);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Per-send timeout fired. Caller's ct takes precedence over shutdown so
                    // the heartbeat sees the cancellation rather than a phantom "send ok".
                    // If both are canceled simultaneously, the caller still gets a clean OCE.
                    throw;
                }
                catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                {
                    // Shutdown/reconnect canceled an in-flight send (caller's ct was not the cause).
                }
                catch (ObjectDisposedException)
                {
                    // WebSocket was disposed between state check and send.
                }
                catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
                {
                    _logger.Warn($"WebSocket send failed (state changed): {ex.Message}");
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            linked?.Dispose();
        }
    }

    /// <summary>Gracefully close the WebSocket connection.</summary>
    protected async Task CloseWebSocketAsync()
    {
        var ws = _webSocket;
        if (ws is null) return;
        try
        {
            if (ws.State != WebSocketState.Open) return;
            // Pass _cts.Token so a Dispose-driven cancel surfaces as OperationCanceledException
            // instead of letting CloseAsync block indefinitely on a half-open connection.
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", _cts.Token);
        }
        // The socket can be disposed or torn down concurrently — Close on a graceful shutdown
        // path must not throw, otherwise subclass shutdown sequences see phantom errors.
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>Fire the StatusChanged event. Use this instead of directly invoking the event.</summary>
    protected void RaiseStatusChanged(ConnectionStatus status)
        => StatusChanged?.Invoke(this, status);

    public void Dispose()
    {
        // Atomic claim — only the first caller proceeds. A plain "if (_disposed) return"
        // would let two concurrent disposers both pass the guard before either set the
        // flag, and OnDisposing would then run twice. Interlocked.Exchange returning the
        // previous value is the canonical single-winner pattern. It also doubles as the
        // intent-to-dispose signal read by the ConnectAsync / reconnect gates below
        // (via the _disposing property), before _disposed is flipped after OnDisposing.
        // _disposed is intentionally flipped only after OnDisposing returns so subclass
        // graceful-shutdown logic (sending a "bye" frame, IsDisposed-guarded helpers)
        // still observes a live state. OnDisposing is wrapped in try/catch so a throwing
        // subclass cannot skip the cleanup below — otherwise _cts and _webSocket would
        // leak until process exit.
        if (Interlocked.Exchange(ref _disposingFlag, 1) != 0) return;

        try { OnDisposing(); } catch { }

        _disposed = true;

        try { _cts.Cancel(); } catch { }
        // Snapshot _connectionCts once. Reading the field twice (Cancel then Dispose)
        // races with a concurrent ConnectAsync that Exchange-installs a new CTS between the
        // two reads — we'd Cancel the old CTS but Dispose the new one, leaking the old and
        // tripping ObjectDisposedException for consumers of the new.
        var connectionCts = Interlocked.Exchange(ref _connectionCts, null);
        try { connectionCts?.Cancel(); } catch { }
        try { connectionCts?.Dispose(); } catch { }

        var ws = Interlocked.Exchange(ref _webSocket, null);
        try { ws?.Dispose(); } catch { }

        // Intentionally do NOT Dispose(_cts). The CancellationToken property and SendRawAsync's
        // CreateLinkedTokenSource(_cts.Token, ct) setup read _cts.Token outside try/catch; if
        // _cts were disposed, those reads throw ObjectDisposedException to callers instead of
        // observing cancellation. _cts.Cancel() above is sufficient to unblock all consumers,
        // and the CancellationTokenSource finalizer releases native resources at GC.
    }
}
