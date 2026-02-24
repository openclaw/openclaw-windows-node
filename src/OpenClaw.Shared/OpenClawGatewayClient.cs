using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public class OpenClawGatewayClient : IDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly string _gatewayUrl;
    private readonly string _gatewayUrlForDisplay;
    private readonly string _token;
    private readonly string? _credentials;
    private readonly IOpenClawLogger _logger;
    private CancellationTokenSource _cts;
    private bool _disposed;
    private int _reconnectAttempts;
    private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };

    // Tracked state
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private readonly Dictionary<string, GatewayNodeInfo> _nodes = new();
    private GatewayUsageInfo? _usage;
    private GatewayUsageStatusInfo? _usageStatus;
    private GatewayCostUsageInfo? _usageCost;
    private readonly Dictionary<string, string> _pendingRequestMethods = new();
    private readonly object _pendingRequestLock = new();
    private bool _usageStatusUnsupported;
    private bool _usageCostUnsupported;
    private bool _sessionPreviewUnsupported;
    private bool _nodeListUnsupported;

    private void ResetUnsupportedMethodFlags()
    {
        _usageStatusUnsupported = false;
        _usageCostUnsupported = false;
        _sessionPreviewUnsupported = false;
        _nodeListUnsupported = false;
    }

    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<OpenClawNotification>? NotificationReceived;
    public event EventHandler<AgentActivity>? ActivityChanged;
    public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<GatewayUsageInfo>? UsageUpdated;
    public event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
    public event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
    public event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    public event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;

    public OpenClawGatewayClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
    {
        _gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        _gatewayUrlForDisplay = GatewayUrlHelper.SanitizeForDisplay(_gatewayUrl);
        _token = token;
        _credentials = GatewayUrlHelper.ExtractCredentials(gatewayUrl);
        _logger = logger ?? NullLogger.Instance;
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync()
    {
        try
        {
            StatusChanged?.Invoke(this, ConnectionStatus.Connecting);
            _logger.Info($"Connecting to gateway: {_gatewayUrlForDisplay}");

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            
            // Set Origin header based on gateway URL (convert ws/wss to http/https)
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

            ResetUnsupportedMethodFlags();
            _reconnectAttempts = 0;
            _logger.Info("Gateway connected, waiting for challenge...");

            // Don't send connect yet - wait for challenge event in ListenForMessagesAsync
            _ = Task.Run(() => ListenForMessagesAsync(), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error("Connection failed", ex);
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during disconnect: {ex.Message}");
            }
        }
        ClearPendingRequests();
        StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
        _logger.Info("Disconnected");
    }

    public async Task CheckHealthAsync()
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            await ReconnectWithBackoffAsync();
            return;
        }

        try
        {
            var req = new
            {
                type = "req",
                id = Guid.NewGuid().ToString(),
                method = "health",
                @params = new { deep = true }
            };
            await SendRawAsync(JsonSerializer.Serialize(req));
        }
        catch (Exception ex)
        {
            _logger.Error("Health check failed", ex);
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
            await ReconnectWithBackoffAsync();
        }
    }

    public async Task SendChatMessageAsync(string message)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Gateway connection is not open");

        var req = new
        {
            type = "req",
            id = Guid.NewGuid().ToString(),
            method = "chat.send",
            @params = new { message }
        };
        await SendRawAsync(JsonSerializer.Serialize(req));
        _logger.Info($"Sent chat message ({message.Length} chars)");
    }

    /// <summary>Request session list from gateway.</summary>
    public async Task RequestSessionsAsync()
    {
        await SendTrackedRequestAsync("sessions.list");
    }

    /// <summary>Request usage/context info from gateway (may not be supported on all gateways).</summary>
    public async Task RequestUsageAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        try
        {
            if (_usageStatusUnsupported)
            {
                await RequestLegacyUsageAsync();
                return;
            }

            await RequestUsageStatusAsync();
            if (!_usageCostUnsupported)
            {
                await RequestUsageCostAsync(days: 30);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Usage request failed: {ex.Message}");
        }
    }

    /// <summary>Request connected node inventory from gateway.</summary>
    public async Task RequestNodesAsync()
    {
        if (_nodeListUnsupported) return;
        await SendTrackedRequestAsync("node.list");
    }

    public async Task RequestUsageStatusAsync()
    {
        await SendTrackedRequestAsync("usage.status");
    }

    public async Task RequestUsageCostAsync(int days = 30)
    {
        if (days <= 0) days = 30;
        await SendTrackedRequestAsync("usage.cost", new { days });
    }

    public async Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240)
    {
        if (_sessionPreviewUnsupported) return;
        if (keys.Length == 0) return;
        if (limit <= 0) limit = 1;
        if (maxChars < 20) maxChars = 20;

        await SendTrackedRequestAsync("sessions.preview", new
        {
            keys,
            limit,
            maxChars
        });
    }

    public Task<bool> PatchSessionAsync(string key, string? thinkingLevel = null, string? verboseLevel = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);

        var payload = new Dictionary<string, object?>
        {
            ["key"] = key
        };
        if (thinkingLevel is not null)
            payload["thinkingLevel"] = thinkingLevel;
        if (verboseLevel is not null)
            payload["verboseLevel"] = verboseLevel;
        return TrySendTrackedRequestAsync("sessions.patch", payload);
    }

    public Task<bool> ResetSessionAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        return TrySendTrackedRequestAsync("sessions.reset", new { key });
    }

    public Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        return TrySendTrackedRequestAsync("sessions.delete", new { key, deleteTranscript });
    }

    public Task<bool> CompactSessionAsync(string key, int maxLines = 400)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        if (maxLines <= 0) maxLines = 400;
        return TrySendTrackedRequestAsync("sessions.compact", new { key, maxLines });
    }

    /// <summary>Start a channel (telegram, whatsapp, etc).</summary>
    public async Task<bool> StartChannelAsync(string channelName)
    {
        if (_webSocket?.State != WebSocketState.Open) return false;
        try
        {
            var req = new
            {
                type = "req",
                id = Guid.NewGuid().ToString(),
                method = "channel.start",
                @params = new { channel = channelName }
            };
            await SendRawAsync(JsonSerializer.Serialize(req));
            _logger.Info($"Sent channel.start for {channelName}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to start channel {channelName}", ex);
            return false;
        }
    }

    /// <summary>Stop a channel (telegram, whatsapp, etc).</summary>
    public async Task<bool> StopChannelAsync(string channelName)
    {
        if (_webSocket?.State != WebSocketState.Open) return false;
        try
        {
            var req = new
            {
                type = "req",
                id = Guid.NewGuid().ToString(),
                method = "channel.stop",
                @params = new { channel = channelName }
            };
            await SendRawAsync(JsonSerializer.Serialize(req));
            _logger.Info($"Sent channel.stop for {channelName}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to stop channel {channelName}", ex);
            return false;
        }
    }

    // --- Connection management ---

    private async Task ReconnectWithBackoffAsync()
    {
        var delay = BackoffMs[Math.Min(_reconnectAttempts, BackoffMs.Length - 1)];
        _reconnectAttempts++;
        _logger.Warn($"Reconnecting in {delay}ms (attempt {_reconnectAttempts})");
        StatusChanged?.Invoke(this, ConnectionStatus.Connecting);

        try
        {
            await Task.Delay(delay, _cts.Token);
            _webSocket?.Dispose();
            _webSocket = null;
            await ConnectAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Reconnect failed", ex);
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
            // Don't recurse — the listen loop will trigger reconnect again
        }
    }

    private async Task SendConnectMessageAsync(string? nonce = null)
    {
        // Use "cli" client ID for native apps - no browser security checks
        var msg = new
        {
            type = "req",
            id = Guid.NewGuid().ToString(),
            method = "connect",
            @params = new
            {
                minProtocol = 3,
                maxProtocol = 3,
                client = new
                {
                    id = "cli",  // Native client ID
                    version = "1.0.0",
                    platform = "windows",
                    mode = "cli",
                    displayName = "OpenClaw Windows Tray"
                },
                role = "operator",
                scopes = new[] { "operator.admin", "operator.approvals", "operator.pairing" },
                caps = Array.Empty<string>(),
                commands = Array.Empty<string>(),
                permissions = new { },
                auth = new { token = _token },
                locale = "en-US",
                userAgent = "openclaw-windows-tray/1.0.0"
            }
        };
        await SendRawAsync(JsonSerializer.Serialize(msg));
    }

    private async Task SendRawAsync(string message)
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, _cts.Token);
        }
    }

    private async Task SendTrackedRequestAsync(string method, object? parameters = null)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var requestId = Guid.NewGuid().ToString();
        TrackPendingRequest(requestId, method);
        try
        {
            await SendRawAsync(SerializeRequest(requestId, method, parameters));
        }
        catch
        {
            RemovePendingRequest(requestId);
            throw;
        }
    }

    private async Task<bool> TrySendTrackedRequestAsync(string method, object? parameters = null)
    {
        try
        {
            await SendTrackedRequestAsync(method, parameters);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{method} request failed: {ex.Message}");
            return false;
        }
    }

    private async Task RequestLegacyUsageAsync()
    {
        try
        {
            await SendTrackedRequestAsync("usage");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Legacy usage request failed: {ex.Message}");
        }
    }

    private static string SerializeRequest(string requestId, string method, object? parameters)
    {
        if (parameters is null)
        {
            return JsonSerializer.Serialize(new { type = "req", id = requestId, method });
        }
        return JsonSerializer.Serialize(new { type = "req", id = requestId, method, @params = parameters });
    }

    private void TrackPendingRequest(string requestId, string method)
    {
        lock (_pendingRequestLock)
        {
            _pendingRequestMethods[requestId] = method;
        }
    }

    private void RemovePendingRequest(string requestId)
    {
        lock (_pendingRequestLock)
        {
            _pendingRequestMethods.Remove(requestId);
        }
    }

    private string? TakePendingRequestMethod(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return null;
        lock (_pendingRequestLock)
        {
            if (!_pendingRequestMethods.TryGetValue(requestId, out var method)) return null;
            _pendingRequestMethods.Remove(requestId);
            return method;
        }
    }

    private void ClearPendingRequests()
    {
        lock (_pendingRequestLock)
        {
            _pendingRequestMethods.Clear();
        }
    }

    // --- Message loop ---

    private async Task ListenForMessagesAsync()
    {
        var buffer = new byte[16384]; // Larger buffer for big events
        var sb = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        ProcessMessage(sb.ToString());
                        sb.Clear();
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeStatus = _webSocket.CloseStatus?.ToString() ?? "unknown";
                    var closeDesc = _webSocket.CloseStatusDescription ?? "no description";
                    _logger.Info($"Server closed connection: {closeStatus} - {closeDesc}");
                    ClearPendingRequests();
                    StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.Warn("Connection closed prematurely");
            ClearPendingRequests();
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Listen error", ex);
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
        }

        // Auto-reconnect if not intentionally disposed
        if (!_disposed && !_cts.Token.IsCancellationRequested)
        {
            await ReconnectWithBackoffAsync();
        }
    }

    // --- Message processing ---

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "res":
                    HandleResponse(root);
                    break;
                case "event":
                    HandleEvent(root);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.Warn($"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Error("Message processing error", ex);
        }
    }

    private void HandleResponse(JsonElement root)
    {
        string? requestMethod = null;
        if (root.TryGetProperty("id", out var idProp))
        {
            requestMethod = TakePendingRequestMethod(idProp.GetString());
        }

        if (root.TryGetProperty("ok", out var okProp) &&
            okProp.ValueKind == JsonValueKind.False)
        {
            HandleRequestError(requestMethod, root);
            return;
        }

        if (!root.TryGetProperty("payload", out var payload)) return;

        if (!string.IsNullOrEmpty(requestMethod) && HandleKnownResponse(requestMethod!, payload))
        {
            return;
        }

        // Handle hello-ok
        if (payload.TryGetProperty("type", out var t) && t.GetString() == "hello-ok")
        {
            _logger.Info("Handshake complete (hello-ok)");
            StatusChanged?.Invoke(this, ConnectionStatus.Connected);

            // Request initial state after handshake
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await CheckHealthAsync();
                await RequestSessionsAsync();
                await RequestUsageAsync();
                await RequestNodesAsync();
            });
        }

        // Handle health response — channels
        if (payload.TryGetProperty("channels", out var channels))
        {
            ParseChannelHealth(channels);
        }

        // Handle sessions response
        if (payload.TryGetProperty("sessions", out var sessions))
        {
            ParseSessions(sessions);
        }

        // Handle usage response
        if (payload.TryGetProperty("usage", out var usage))
        {
            ParseUsage(usage);
        }

        if (payload.TryGetProperty("nodes", out var nodes))
        {
            ParseNodeList(nodes);
        }
    }

    private bool HandleKnownResponse(string method, JsonElement payload)
    {
        switch (method)
        {
            case "health":
                if (payload.TryGetProperty("channels", out var channels))
                    ParseChannelHealth(channels);
                return true;
            case "sessions.list":
                if (TryGetSessionsPayload(payload, out var sessionsPayload))
                    ParseSessions(sessionsPayload);
                return true;
            case "usage":
                ParseUsage(payload);
                return true;
            case "usage.status":
                ParseUsageStatus(payload);
                return true;
            case "usage.cost":
                ParseUsageCost(payload);
                return true;
            case "node.list":
                if (TryGetNodesPayload(payload, out var nodesPayload))
                    ParseNodeList(nodesPayload);
                return true;
            case "sessions.preview":
                ParseSessionsPreview(payload);
                return true;
            case "sessions.patch":
            case "sessions.reset":
            case "sessions.delete":
            case "sessions.compact":
                ParseSessionCommandResult(method, payload);
                return true;
            default:
                return false;
        }
    }

    private void HandleRequestError(string? method, JsonElement root)
    {
        var message = TryGetErrorMessage(root) ?? "request failed";

        if (string.IsNullOrEmpty(method))
        {
            _logger.Warn($"Gateway request failed: {message}");
            return;
        }

        if (IsUnknownMethodError(message))
        {
            switch (method)
            {
                case "usage.status":
                    _usageStatusUnsupported = true;
                    _logger.Warn("usage.status unsupported on gateway; falling back to usage");
                    _ = RequestLegacyUsageAsync();
                    return;
                case "usage.cost":
                    _usageCostUnsupported = true;
                    _logger.Warn("usage.cost unsupported on gateway");
                    return;
                case "sessions.preview":
                    _sessionPreviewUnsupported = true;
                    _logger.Warn("sessions.preview unsupported on gateway");
                    return;
                case "node.list":
                    _nodeListUnsupported = true;
                    _logger.Warn("node.list unsupported on gateway");
                    return;
            }
        }

        if (IsSessionCommandMethod(method))
        {
            SessionCommandCompleted?.Invoke(this, new SessionCommandResult
            {
                Method = method,
                Ok = false,
                Error = message
            });
        }

        _logger.Warn($"{method} failed: {message}");
    }

    private static bool TryGetSessionsPayload(JsonElement payload, out JsonElement sessions)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("sessions", out sessions))
        {
            return true;
        }

        if (payload.ValueKind == JsonValueKind.Object || payload.ValueKind == JsonValueKind.Array)
        {
            sessions = payload;
            return true;
        }

        sessions = default;
        return false;
    }

    private static bool TryGetNodesPayload(JsonElement payload, out JsonElement nodes)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("nodes", out nodes))
        {
            return true;
        }

        if (payload.ValueKind == JsonValueKind.Array || payload.ValueKind == JsonValueKind.Object)
        {
            nodes = payload;
            return true;
        }

        nodes = default;
        return false;
    }

    private static string? TryGetErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return null;
        if (error.ValueKind == JsonValueKind.String) return error.GetString();
        if (error.ValueKind != JsonValueKind.Object) return null;
        if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        return null;
    }

    private static bool IsUnknownMethodError(string errorMessage)
    {
        return errorMessage.Contains("unknown method", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSessionCommandMethod(string method)
    {
        return method is "sessions.patch" or "sessions.reset" or "sessions.delete" or "sessions.compact";
    }

    private void HandleEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventProp)) return;
        var eventType = eventProp.GetString();

        switch (eventType)
        {
            case "connect.challenge":
                HandleConnectChallenge(root);
                break;
            case "agent":
                HandleAgentEvent(root);
                break;
            case "health":
                if (root.TryGetProperty("payload", out var hp) &&
                    hp.TryGetProperty("channels", out var ch))
                    ParseChannelHealth(ch);
                break;
            case "chat":
                HandleChatEvent(root);
                break;
            case "session":
                HandleSessionEvent(root);
                break;
        }
    }

    private void HandleConnectChallenge(JsonElement root)
    {
        string? nonce = null;
        if (root.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("nonce", out var nonceProp))
        {
            nonce = nonceProp.GetString();
        }
        
        _logger.Info($"Received challenge, nonce: {nonce}");
        _ = SendConnectMessageAsync(nonce);
    }

    private void HandleAgentEvent(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;

        // Determine session
        var sessionKey = "unknown";
        if (root.TryGetProperty("sessionKey", out var sk))
            sessionKey = sk.GetString() ?? "unknown";
        var isMain = sessionKey == "main" || sessionKey.Contains(":main:");

        // Parse activity from stream field
        if (payload.TryGetProperty("stream", out var streamProp))
        {
            var stream = streamProp.GetString();

            if (stream == "job")
            {
                HandleJobEvent(payload, sessionKey, isMain);
            }
            else if (stream == "tool")
            {
                HandleToolEvent(payload, sessionKey, isMain);
            }
        }

        // Check for notification content
        if (payload.TryGetProperty("content", out var content))
        {
            var text = content.GetString() ?? "";
            if (!string.IsNullOrEmpty(text))
            {
                EmitNotification(text);
            }
        }
    }

    private void HandleJobEvent(JsonElement payload, string sessionKey, bool isMain)
    {
        var state = "unknown";
        if (payload.TryGetProperty("data", out var data) &&
            data.TryGetProperty("state", out var stateProp))
            state = stateProp.GetString() ?? "unknown";

        var activity = new AgentActivity
        {
            SessionKey = sessionKey,
            IsMain = isMain,
            Kind = ActivityKind.Job,
            State = state,
            Label = $"Job: {state}"
        };

        if (state == "done" || state == "error")
            activity.Kind = ActivityKind.Idle;

        _logger.Info($"Agent activity: {activity.Label} (session: {sessionKey})");
        ActivityChanged?.Invoke(this, activity);

        // Update tracked session
        UpdateTrackedSession(sessionKey, isMain, state == "done" || state == "error" ? null : $"Job: {state}");
    }

    private void HandleToolEvent(JsonElement payload, string sessionKey, bool isMain)
    {
        var phase = "";
        var toolName = "";
        var label = "";

        if (payload.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("phase", out var phaseProp))
                phase = phaseProp.GetString() ?? "";
            if (data.TryGetProperty("name", out var nameProp))
                toolName = nameProp.GetString() ?? "";

            // Extract detail from args
            if (data.TryGetProperty("args", out var args))
            {
                if (args.TryGetProperty("command", out var cmd))
                    label = TruncateLabel(cmd.GetString()?.Split('\n')[0] ?? "");
                else if (args.TryGetProperty("path", out var path))
                    label = ShortenPath(path.GetString() ?? "");
                else if (args.TryGetProperty("file_path", out var filePath))
                    label = ShortenPath(filePath.GetString() ?? "");
                else if (args.TryGetProperty("query", out var query))
                    label = TruncateLabel(query.GetString() ?? "");
                else if (args.TryGetProperty("url", out var url))
                    label = TruncateLabel(url.GetString() ?? "");
            }
        }

        if (string.IsNullOrEmpty(label))
            label = toolName;

        var kind = ClassifyTool(toolName);

        // On tool result, briefly show then go idle
        if (phase == "result")
            kind = ActivityKind.Idle;

        var activity = new AgentActivity
        {
            SessionKey = sessionKey,
            IsMain = isMain,
            Kind = kind,
            State = phase,
            ToolName = toolName,
            Label = label
        };

        _logger.Info($"Tool: {toolName} ({phase}) — {label}");
        ActivityChanged?.Invoke(this, activity);

        // Update tracked session
        if (kind != ActivityKind.Idle)
        {
            UpdateTrackedSession(sessionKey, isMain, $"{activity.Glyph} {label}");
        }
    }

    private void HandleChatEvent(JsonElement root)
    {
        _logger.Debug($"Chat event received: {root.GetRawText().Substring(0, Math.Min(200, root.GetRawText().Length))}");
        
        if (!root.TryGetProperty("payload", out var payload)) return;

        // Try new format: payload.message.role + payload.message.content[].text
        if (payload.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            {
                // Extract text from content array
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                            item.TryGetProperty("text", out var textProp))
                        {
                            var text = textProp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(text) && 
                                payload.TryGetProperty("state", out var state) && 
                                state.GetString() == "final")
                            {
                                _logger.Info($"Assistant response: {text.Substring(0, Math.Min(100, text.Length))}...");
                                EmitChatNotification(text);
                            }
                        }
                    }
                }
            }
        }
        
        // Legacy format: payload.text + payload.role
        else if (payload.TryGetProperty("text", out var textProp))
        {
            var text = textProp.GetString() ?? "";
            if (payload.TryGetProperty("role", out var role) &&
                role.GetString() == "assistant" &&
                !string.IsNullOrEmpty(text))
            {
                _logger.Info($"Assistant response (legacy): {text.Substring(0, Math.Min(100, text.Length))}");
                EmitChatNotification(text);
            }
        }
    }

    private void EmitChatNotification(string text)
    {
        var displayText = text.Length > 200 ? text[..200] + "…" : text;
        var notification = new OpenClawNotification
        {
            Message = displayText,
            IsChat = true
        };
        var (title, type) = _categorizer.Classify(notification);
        notification.Title = title;
        notification.Type = type;
        NotificationReceived?.Invoke(this, notification);
    }

    private void HandleSessionEvent(JsonElement root)
    {
        // Re-request sessions list when session events come through
        _ = RequestSessionsAsync();
    }

    // --- State tracking ---

    private void UpdateTrackedSession(string sessionKey, bool isMain, string? currentActivity)
    {
        if (!_sessions.ContainsKey(sessionKey))
        {
            _sessions[sessionKey] = new SessionInfo
            {
                Key = sessionKey,
                IsMain = isMain,
                Status = "active"
            };
        }

        _sessions[sessionKey].CurrentActivity = currentActivity;
        _sessions[sessionKey].LastSeen = DateTime.UtcNow;

        SessionsUpdated?.Invoke(this, GetSessionList());
    }

    public SessionInfo[] GetSessionList()
    {
        var list = new List<SessionInfo>(_sessions.Values);
        list.Sort((a, b) =>
        {
            // Main session first, then by last seen
            if (a.IsMain != b.IsMain) return a.IsMain ? -1 : 1;
            return b.LastSeen.CompareTo(a.LastSeen);
        });
        return list.ToArray();
    }

    // --- Parsing helpers ---

    private void ParseChannelHealth(JsonElement channels)
    {
        var healthList = new List<ChannelHealth>();
        
        // Debug: log raw channel data
        _logger.Debug($"Raw channel health JSON: {channels.GetRawText()}");

        foreach (var prop in channels.EnumerateObject())
        {
            var ch = new ChannelHealth { Name = prop.Name };
            var val = prop.Value;

            // Get running status
            bool isRunning = false;
            bool isConfigured = false;
            bool isLinked = false;
            bool probeOk = false;
            bool hasError = false;
            string? tokenSource = null;
            
            if (val.TryGetProperty("running", out var running))
                isRunning = running.GetBoolean();
            if (val.TryGetProperty("configured", out var configured))
                isConfigured = configured.GetBoolean();
            if (val.TryGetProperty("linked", out var linked))
            {
                isLinked = linked.GetBoolean();
                ch.IsLinked = isLinked;
            }
            // Check probe status for webhook-based channels like Telegram
            if (val.TryGetProperty("probe", out var probe) && probe.TryGetProperty("ok", out var ok))
                probeOk = ok.GetBoolean();
            // Check for errors
            if (val.TryGetProperty("lastError", out var lastError) && lastError.ValueKind != JsonValueKind.Null)
                hasError = true;
            // Check token source (for Telegram - if configured, bot token was validated)
            if (val.TryGetProperty("tokenSource", out var ts))
                tokenSource = ts.GetString();
            
            // Determine status string - unified for parity between channels
            // Key insight: if configured=true and no errors, the channel is ready
            // - WhatsApp: linked=true means authenticated
            // - Telegram: configured=true means bot token was validated
            if (val.TryGetProperty("status", out var status))
                ch.Status = status.GetString() ?? "unknown";
            else if (hasError)
                ch.Status = "error";
            else if (isRunning)
                ch.Status = "running";
            else if (isConfigured && (probeOk || isLinked))
                ch.Status = "ready";  // Explicitly verified ready
            else if (isConfigured && !hasError)
                ch.Status = "ready";  // Configured without errors = ready (token was validated at config time)
            else
                ch.Status = "not configured";
            
            if (val.TryGetProperty("error", out var error))
                ch.Error = error.GetString();
            if (val.TryGetProperty("authAge", out var authAge))
                ch.AuthAge = authAge.GetString();
            if (val.TryGetProperty("type", out var chType))
                ch.Type = chType.GetString();

            healthList.Add(ch);
        }

        if (healthList.Count > 0)
        {
            _logger.Info($"Channel health: {string.Join(", ", healthList.ConvertAll(c => $"{c.Name}={c.Status}"))}");
            ChannelHealthUpdated?.Invoke(this, healthList.ToArray());
        }
    }

    private void ParseSessions(JsonElement sessions)
    {
        try
        {
            _sessions.Clear();
            
            // Handle both Array format and Object (dictionary) format
            if (sessions.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sessions.EnumerateArray())
                {
                    ParseSessionItem(item);
                }
            }
            else if (sessions.ValueKind == JsonValueKind.Object)
            {
                // Object format: keys are session IDs, values could be session info objects or simple strings
                foreach (var prop in sessions.EnumerateObject())
                {
                    var sessionKey = prop.Name;
                    
                    // Skip metadata fields that aren't actual sessions
                    if (sessionKey is "recent" or "count" or "path" or "defaults" or "ts")
                        continue;
                    
                    // Skip non-session keys (must look like a session key pattern)
                    if (!sessionKey.Equals("global", StringComparison.OrdinalIgnoreCase) &&
                        !sessionKey.Contains(':') &&
                        !sessionKey.Contains("agent") &&
                        !sessionKey.Contains("session"))
                        continue;
                    
                    var session = new SessionInfo { Key = sessionKey };
                    var item = prop.Value;
                    
                    // Detect main session from key pattern - "agent:main:main" ends with ":main"
                    var endsWithMain = sessionKey.EndsWith(":main");
                    session.IsMain = sessionKey == "main" || endsWithMain || sessionKey.Contains(":main:main");
                    _logger.Debug($"Session key={sessionKey}, endsWithMain={endsWithMain}, IsMain={session.IsMain}");
                    
                    // Value might be an object with session details or just a string status
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        // Only override IsMain if the JSON explicitly says true
                        if (item.TryGetProperty("isMain", out var isMain) && isMain.GetBoolean())
                            session.IsMain = true;
                        PopulateSessionFromObject(session, item);
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        // Simple string value - skip if it looks like a path (metadata)
                        var strVal = item.GetString() ?? "";
                        if (strVal.StartsWith("/") || strVal.Contains("/."))
                            continue;
                        session.Status = strVal;
                    }
                    else if (item.ValueKind == JsonValueKind.Number)
                    {
                        // Skip numeric values (like count)
                        continue;
                    }
                    
                    _sessions[session.Key] = session;
                }
            }

            SessionsUpdated?.Invoke(this, GetSessionList());
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse sessions: {ex.Message}");
        }
    }
    
    private void ParseSessionItem(JsonElement item)
    {
        var session = new SessionInfo();
        if (item.TryGetProperty("key", out var key))
            session.Key = key.GetString() ?? "unknown";
        
        // Detect main from key pattern first
        session.IsMain = session.Key == "main" || 
                         session.Key.EndsWith(":main") ||
                         session.Key.Contains(":main:main");
        
        // Only override if JSON explicitly says true
        if (item.TryGetProperty("isMain", out var isMain) && isMain.GetBoolean())
            session.IsMain = true;
            
        PopulateSessionFromObject(session, item);

        _sessions[session.Key] = session;
    }

    private void PopulateSessionFromObject(SessionInfo session, JsonElement item)
    {
        if (item.TryGetProperty("status", out var status))
            session.Status = status.GetString() ?? "active";
        if (item.TryGetProperty("model", out var model))
            session.Model = model.GetString();
        if (item.TryGetProperty("channel", out var channel))
            session.Channel = channel.GetString();
        if (item.TryGetProperty("displayName", out var displayName))
            session.DisplayName = displayName.GetString();
        if (item.TryGetProperty("provider", out var provider))
            session.Provider = provider.GetString();
        if (item.TryGetProperty("subject", out var subject))
            session.Subject = subject.GetString();
        if (item.TryGetProperty("room", out var room))
            session.Room = room.GetString();
        if (item.TryGetProperty("space", out var space))
            session.Space = space.GetString();
        if (item.TryGetProperty("sessionId", out var sessionId))
            session.SessionId = sessionId.GetString();
        if (item.TryGetProperty("thinkingLevel", out var thinking))
            session.ThinkingLevel = thinking.GetString();
        if (item.TryGetProperty("verboseLevel", out var verbose))
            session.VerboseLevel = verbose.GetString();
        if (item.TryGetProperty("systemSent", out var systemSent) &&
            (systemSent.ValueKind == JsonValueKind.True || systemSent.ValueKind == JsonValueKind.False))
            session.SystemSent = systemSent.GetBoolean();
        if (item.TryGetProperty("abortedLastRun", out var abortedLastRun) &&
            (abortedLastRun.ValueKind == JsonValueKind.True || abortedLastRun.ValueKind == JsonValueKind.False))
            session.AbortedLastRun = abortedLastRun.GetBoolean();
        session.InputTokens = GetLong(item, "inputTokens");
        session.OutputTokens = GetLong(item, "outputTokens");
        session.TotalTokens = GetLong(item, "totalTokens");
        session.ContextTokens = GetLong(item, "contextTokens");

        var updated = ParseUnixTimestampMs(item, "updatedAt");
        if (updated.HasValue)
        {
            session.UpdatedAt = updated.Value;
        }

        if (item.TryGetProperty("startedAt", out var started))
        {
            if (DateTime.TryParse(started.GetString(), out var dt))
                session.StartedAt = dt;
        }
    }

    private void ParseNodeList(JsonElement nodesPayload)
    {
        try
        {
            JsonElement nodes = nodesPayload;
            if (nodesPayload.ValueKind == JsonValueKind.Object)
            {
                if (nodesPayload.TryGetProperty("nodes", out var nestedNodes))
                    nodes = nestedNodes;
                else if (nodesPayload.TryGetProperty("items", out var nestedItems))
                    nodes = nestedItems;
            }

            if (nodes.ValueKind != JsonValueKind.Array)
                return;

            var parsed = new List<GatewayNodeInfo>();
            foreach (var nodeElement in nodes.EnumerateArray())
            {
                if (nodeElement.ValueKind != JsonValueKind.Object)
                    continue;

                var nodeId = FirstNonEmpty(
                    GetString(nodeElement, "nodeId"),
                    GetString(nodeElement, "deviceId"),
                    GetString(nodeElement, "id"),
                    GetString(nodeElement, "clientId"));
                if (string.IsNullOrWhiteSpace(nodeId))
                    continue;

                var status = FirstNonEmpty(
                    GetString(nodeElement, "status"),
                    GetString(nodeElement, "state"),
                    "unknown");
                var connected = GetOptionalBool(nodeElement, "connected");
                var online = GetOptionalBool(nodeElement, "online");

                parsed.Add(new GatewayNodeInfo
                {
                    NodeId = nodeId!,
                    DisplayName = FirstNonEmpty(
                        GetString(nodeElement, "displayName"),
                        GetString(nodeElement, "name"),
                        GetString(nodeElement, "label"),
                        GetString(nodeElement, "shortId"),
                        nodeId)!,
                    Mode = FirstNonEmpty(
                        GetString(nodeElement, "mode"),
                        GetString(nodeElement, "clientMode"),
                        "node")!,
                    Status = status!,
                    Platform = FirstNonEmpty(
                        GetString(nodeElement, "platform"),
                        GetString(nodeElement, "os")),
                    LastSeen = ParseUnixTimestampMs(nodeElement, "lastSeenAt") ??
                               ParseUnixTimestampMs(nodeElement, "lastSeen") ??
                               ParseUnixTimestampMs(nodeElement, "updatedAt") ??
                               ParseUnixTimestampMs(nodeElement, "connectedAt"),
                    CapabilityCount = Math.Max(
                        GetArrayLength(nodeElement, "caps"),
                        GetArrayLength(nodeElement, "capabilities")),
                    CommandCount = Math.Max(
                        GetArrayLength(nodeElement, "declaredCommands"),
                        GetArrayLength(nodeElement, "commands")),
                    IsOnline = online ?? connected ?? status is "ok" or "online" or "connected" or "ready" or "active"
                });
            }

            var ordered = parsed
                .OrderByDescending(n => n.IsOnline)
                .ThenByDescending(n => n.LastSeen ?? DateTime.MinValue)
                .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _nodes.Clear();
            foreach (var node in ordered)
                _nodes[node.NodeId] = node;

            NodesUpdated?.Invoke(this, ordered);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse node.list: {ex.Message}");
        }
    }

    private void ParseUsage(JsonElement usage)
    {
        try
        {
            _usage ??= new GatewayUsageInfo();
            if (usage.TryGetProperty("inputTokens", out var inp))
                _usage.InputTokens = inp.GetInt64();
            if (usage.TryGetProperty("outputTokens", out var outp))
                _usage.OutputTokens = outp.GetInt64();
            if (usage.TryGetProperty("totalTokens", out var tot))
                _usage.TotalTokens = tot.GetInt64();
            if (usage.TryGetProperty("cost", out var cost))
                _usage.CostUsd = cost.GetDouble();
            if (usage.TryGetProperty("requestCount", out var req))
                _usage.RequestCount = req.GetInt32();
            if (usage.TryGetProperty("model", out var model))
                _usage.Model = model.GetString();
            _usage.ProviderSummary = null;

            UsageUpdated?.Invoke(this, _usage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse usage: {ex.Message}");
        }
    }

    private void ParseUsageStatus(JsonElement payload)
    {
        try
        {
            var status = new GatewayUsageStatusInfo
            {
                UpdatedAt = ParseUnixTimestampMs(payload, "updatedAt") ?? DateTime.UtcNow
            };

            if (payload.TryGetProperty("providers", out var providers) &&
                providers.ValueKind == JsonValueKind.Array)
            {
                foreach (var providerElement in providers.EnumerateArray())
                {
                    var provider = new GatewayUsageProviderInfo
                    {
                        Provider = GetString(providerElement, "provider") ?? "",
                        DisplayName = GetString(providerElement, "displayName") ?? GetString(providerElement, "provider") ?? "",
                        Plan = GetString(providerElement, "plan"),
                        Error = GetString(providerElement, "error")
                    };

                    if (providerElement.TryGetProperty("windows", out var windows) &&
                        windows.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var windowElement in windows.EnumerateArray())
                        {
                            provider.Windows.Add(new GatewayUsageWindowInfo
                            {
                                Label = GetString(windowElement, "label") ?? "",
                                UsedPercent = GetDouble(windowElement, "usedPercent"),
                                ResetAt = ParseUnixTimestampMs(windowElement, "resetAt")
                            });
                        }
                    }

                    status.Providers.Add(provider);
                }
            }

            _usageStatus = status;
            UsageStatusUpdated?.Invoke(this, status);

            _usage ??= new GatewayUsageInfo();
            _usage.ProviderSummary = BuildProviderSummary(status);
            UsageUpdated?.Invoke(this, _usage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse usage.status: {ex.Message}");
        }
    }

    private void ParseUsageCost(JsonElement payload)
    {
        try
        {
            var summary = new GatewayCostUsageInfo
            {
                UpdatedAt = ParseUnixTimestampMs(payload, "updatedAt") ?? DateTime.UtcNow,
                Days = GetInt(payload, "days")
            };

            if (payload.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object)
            {
                summary.Totals = new GatewayCostUsageTotalsInfo
                {
                    Input = GetLong(totals, "input"),
                    Output = GetLong(totals, "output"),
                    CacheRead = GetLong(totals, "cacheRead"),
                    CacheWrite = GetLong(totals, "cacheWrite"),
                    TotalTokens = GetLong(totals, "totalTokens"),
                    TotalCost = GetDouble(totals, "totalCost"),
                    MissingCostEntries = GetInt(totals, "missingCostEntries")
                };
            }

            if (payload.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Array)
            {
                foreach (var day in daily.EnumerateArray())
                {
                    summary.Daily.Add(new GatewayCostUsageDayInfo
                    {
                        Date = GetString(day, "date") ?? "",
                        Input = GetLong(day, "input"),
                        Output = GetLong(day, "output"),
                        CacheRead = GetLong(day, "cacheRead"),
                        CacheWrite = GetLong(day, "cacheWrite"),
                        TotalTokens = GetLong(day, "totalTokens"),
                        TotalCost = GetDouble(day, "totalCost"),
                        MissingCostEntries = GetInt(day, "missingCostEntries")
                    });
                }
            }

            _usageCost = summary;
            UsageCostUpdated?.Invoke(this, summary);

            _usage ??= new GatewayUsageInfo();
            _usage.TotalTokens = summary.Totals.TotalTokens;
            _usage.CostUsd = summary.Totals.TotalCost;
            UsageUpdated?.Invoke(this, _usage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse usage.cost: {ex.Message}");
        }
    }

    private void ParseSessionsPreview(JsonElement payload)
    {
        try
        {
            var previewPayload = new SessionsPreviewPayloadInfo
            {
                UpdatedAt = ParseUnixTimestampMs(payload, "ts") ?? DateTime.UtcNow
            };

            if (payload.TryGetProperty("previews", out var previews) &&
                previews.ValueKind == JsonValueKind.Array)
            {
                foreach (var previewElement in previews.EnumerateArray())
                {
                    var preview = new SessionPreviewInfo
                    {
                        Key = GetString(previewElement, "key") ?? "",
                        Status = GetString(previewElement, "status") ?? "unknown"
                    };

                    if (previewElement.TryGetProperty("items", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            preview.Items.Add(new SessionPreviewItemInfo
                            {
                                Role = GetString(item, "role") ?? "other",
                                Text = GetString(item, "text") ?? ""
                            });
                        }
                    }

                    previewPayload.Previews.Add(preview);
                }
            }

            SessionPreviewUpdated?.Invoke(this, previewPayload);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse sessions.preview: {ex.Message}");
        }
    }

    private void ParseSessionCommandResult(string method, JsonElement payload)
    {
        var result = new SessionCommandResult
        {
            Method = method,
            Ok = true,
            Key = GetString(payload, "key"),
            Reason = GetString(payload, "reason")
        };

        if (payload.TryGetProperty("deleted", out var deleted) &&
            (deleted.ValueKind == JsonValueKind.True || deleted.ValueKind == JsonValueKind.False))
        {
            result.Deleted = deleted.GetBoolean();
        }

        if (payload.TryGetProperty("compacted", out var compacted) &&
            (compacted.ValueKind == JsonValueKind.True || compacted.ValueKind == JsonValueKind.False))
        {
            result.Compacted = compacted.GetBoolean();
        }

        if (payload.TryGetProperty("kept", out var kept) && kept.ValueKind == JsonValueKind.Number)
        {
            result.Kept = kept.GetInt32();
        }

        SessionCommandCompleted?.Invoke(this, result);
    }

    private static string BuildProviderSummary(GatewayUsageStatusInfo status)
    {
        if (status.Providers.Count == 0) return "";

        var parts = new List<string>();
        foreach (var provider in status.Providers)
        {
            if (parts.Count == 2) break;
            var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Provider : provider.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "provider";

            if (!string.IsNullOrWhiteSpace(provider.Error))
            {
                parts.Add($"{displayName}: error");
                continue;
            }

            if (provider.Windows.Count == 0) continue;
            var window = provider.Windows.MaxBy(w => w.UsedPercent);
            if (window is null) continue;
            var remaining = Math.Clamp((int)Math.Round(100 - window.UsedPercent), 0, 100);
            parts.Add($"{displayName}: {remaining}% left");
        }

        if (parts.Count == 0)
            return "";

        if (status.Providers.Count > 2)
            parts.Add($"+{status.Providers.Count - 2}");

        return string.Join(" · ", parts);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? GetString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static bool? GetOptionalBool(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int GetInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetInt32(out var intVal)) return intVal;
        if (value.TryGetInt64(out var longVal)) return (int)Math.Clamp(longVal, int.MinValue, int.MaxValue);
        return 0;
    }

    private static long GetLong(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetInt64(out var longVal)) return longVal;
        if (value.TryGetDouble(out var doubleVal)) return (long)doubleVal;
        return 0;
    }

    private static double GetDouble(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetDouble(out var doubleVal)) return doubleVal;
        return 0;
    }

    private static int GetArrayLength(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return 0;
        return value.GetArrayLength();
    }

    private static DateTime? ParseUnixTimestampMs(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (!value.TryGetDouble(out var raw)) return null;

        // Accept either milliseconds or seconds.
        var ms = raw > 10_000_000_000 ? raw : raw * 1000;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    // --- Notification classification ---

    private static readonly NotificationCategorizer _categorizer = new();

    private void EmitNotification(string text)
    {
        var notification = new OpenClawNotification
        {
            Message = text.Length > 200 ? text[..200] + "…" : text
        };
        var (title, type) = _categorizer.Classify(notification);
        notification.Title = title;
        notification.Type = type;
        NotificationReceived?.Invoke(this, notification);
    }

    private static (string title, string type) ClassifyNotification(string text)
    {
        return NotificationCategorizer.ClassifyByKeywords(text);
    }

    // --- Utility ---

    private static ActivityKind ClassifyTool(string toolName)
    {
        return toolName.ToLowerInvariant() switch
        {
            "exec" => ActivityKind.Exec,
            "read" => ActivityKind.Read,
            "write" => ActivityKind.Write,
            "edit" => ActivityKind.Edit,
            "web_search" => ActivityKind.Search,
            "web_fetch" => ActivityKind.Search,
            "browser" => ActivityKind.Browser,
            "message" => ActivityKind.Message,
            "tts" => ActivityKind.Tool,
            "image" => ActivityKind.Tool,
            _ => ActivityKind.Tool
        };
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length > 2
            ? $"…/{parts[^2]}/{parts[^1]}"
            : parts[^1];
    }

    private static string TruncateLabel(string text, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
        return text[..(maxLen - 1)] + "…";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            ClearPendingRequests();
            _webSocket?.Dispose();
            _cts.Dispose();
        }
    }
}
