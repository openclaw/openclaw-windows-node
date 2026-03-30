using System;
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
    private bool _disposed;
    private int _reconnectAttempts;
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
        try
        {
            RaiseStatusChanged(ConnectionStatus.Connecting);
            _logger.Info($"Connecting to {ClientRole}: {GatewayUrlForDisplay}");

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            // Set Origin header (convert ws/wss to http/https)
            var uri = new Uri(_gatewayUrl);
            var originScheme = uri.Scheme == "wss" ? "https" : "http";
            var origin = $"{originScheme}://{uri.Host}:{uri.Port}";
            _webSocket.Options.SetRequestHeader("Origin", origin);

            if (!string.IsNullOrEmpty(_credentials))
            {
                var credentialsToEncode = GatewayUrlHelper.DecodeCredentials(_credentials);
                _webSocket.Options.SetRequestHeader(
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentialsToEncode))}");
            }

            await _webSocket.ConnectAsync(uri, _cts.Token);

            _reconnectAttempts = 0;
            _logger.Info($"{ClientRole} connected, waiting for challenge...");

            await OnConnectedAsync();

            _ = Task.Run(() => ListenForMessagesAsync(), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} connection failed", ex);
            RaiseStatusChanged(ConnectionStatus.Error);
        }
    }

    private async Task ListenForMessagesAsync()
    {
        var buffer = new byte[ReceiveBufferSize];
        var sb = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), _cts.Token);

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
                    var closeStatus = _webSocket.CloseStatus?.ToString() ?? "unknown";
                    var closeDesc = _webSocket.CloseStatusDescription ?? "no description";
                    _logger.Info($"Server closed connection: {closeStatus} - {closeDesc}");
                    OnDisconnected();
                    RaiseStatusChanged(ConnectionStatus.Disconnected);
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.Warn("Connection closed prematurely");
            OnDisconnected();
            RaiseStatusChanged(ConnectionStatus.Disconnected);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { /* CTS or WebSocket disposed during shutdown */ }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} listen error", ex);
            OnError(ex);
            RaiseStatusChanged(ConnectionStatus.Error);
        }

        // Auto-reconnect if not intentionally disposed
        if (!_disposed)
        {
            try
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    await ReconnectWithBackoffAsync();
                }
            }
            catch (ObjectDisposedException) { /* CTS disposed during check */ }
        }
    }

    protected async Task ReconnectWithBackoffAsync()
    {
        var delay = BackoffMs[Math.Min(_reconnectAttempts, BackoffMs.Length - 1)];
        _reconnectAttempts++;
        _logger.Warn($"{ClientRole} reconnecting in {delay}ms (attempt {_reconnectAttempts})");
        RaiseStatusChanged(ConnectionStatus.Connecting);

        try
        {
            await Task.Delay(delay, _cts.Token);

            // Check cancellation after delay
            if (_cts.Token.IsCancellationRequested) return;

            // Safely dispose old socket
            var oldSocket = _webSocket;
            _webSocket = null;
            try { oldSocket?.Dispose(); } catch { /* ignore dispose errors */ }

            await ConnectAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} reconnect failed", ex);
            RaiseStatusChanged(ConnectionStatus.Error);
        }
    }

    /// <summary>Send a text message over the WebSocket. Thread-safe.</summary>
    protected async Task SendRawAsync(string message)
    {
        // Capture local reference to avoid TOCTOU race with reconnect/dispose
        var ws = _webSocket;
        if (ws?.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (ObjectDisposedException)
        {
            // WebSocket was disposed between state check and send
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
        {
            _logger.Warn($"WebSocket send failed (state changed): {ex.Message}");
        }
    }

    /// <summary>Gracefully close the WebSocket connection.</summary>
    protected async Task CloseWebSocketAsync()
    {
        var ws = _webSocket;
        if (ws?.State == WebSocketState.Open)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", System.Threading.CancellationToken.None);
        }
    }

    /// <summary>Fire the StatusChanged event. Use this instead of directly invoking the event.</summary>
    protected void RaiseStatusChanged(ConnectionStatus status)
        => StatusChanged?.Invoke(this, status);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        OnDisposing();

        try { _cts.Cancel(); } catch { }

        var ws = _webSocket;
        _webSocket = null;
        try { ws?.Dispose(); } catch { }

        // Don't dispose _cts immediately — listen loop or reconnect may still reference it.
        // It will be GC'd after all pending tasks complete.
    }
}
