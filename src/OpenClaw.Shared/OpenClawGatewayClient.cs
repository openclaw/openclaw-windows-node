using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public class OpenClawGatewayClient : WebSocketClientBase
{
    private const string OperatorClientId = "cli";
    private const string OperatorClientDisplayName = "OpenClaw Windows Tray";
    private const string OperatorClientMode = "cli";
    private const string OperatorRole = "operator";
    private const string OperatorPlatform = "windows";
    private const string OperatorDeviceFamily = "desktop";
    private static readonly string[] s_operatorScopes =
    [
        "operator.admin",
        "operator.read",
        "operator.write",
        "operator.approvals",
        "operator.pairing"
    ];

    private enum SignatureTokenMode
    {
        V3AuthToken,
        V3EmptyToken,
        V2AuthToken,
        V2EmptyToken
    }

    // Tracked state
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private readonly Dictionary<string, GatewayNodeInfo> _nodes = new();
    private GatewayUsageInfo? _usage;
    private GatewayUsageStatusInfo? _usageStatus;
    private GatewayCostUsageInfo? _usageCost;
    private readonly Dictionary<string, string> _pendingRequestMethods = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingChatSendRequests = new();
    private readonly object _pendingRequestLock = new();
    private readonly object _pendingChatSendLock = new();
    private readonly object _sessionsLock = new();
    private readonly object _nodesLock = new();
    private readonly DeviceIdentity _deviceIdentity;
    private string _mainSessionKey = "main";
    private string? _operatorDeviceId;
    private string[] _grantedOperatorScopes = Array.Empty<string>();
    private string _connectAuthToken;
    private SignatureTokenMode _signatureTokenMode = SignatureTokenMode.V3AuthToken;
    private long? _challengeTimestampMs;
    private string? _currentChallengeNonce;
    private bool _usageStatusUnsupported;
    private bool _usageCostUnsupported;
    private bool _sessionPreviewUnsupported;
    private bool _nodeListUnsupported;
    private bool _operatorReadScopeUnavailable;
    private bool _pairingRequiredAwaitingApproval;
    private IReadOnlyList<UserNotificationRule>? _userRules;
    private bool _preferStructuredCategories = true;

    /// <summary>
    /// Controls whether structured notification metadata (Intent, Channel) takes priority
    /// over keyword-based classification. Call after construction and whenever settings change.
    /// </summary>
    public void SetPreferStructuredCategories(bool value) => _preferStructuredCategories = value;

    private void ResetUnsupportedMethodFlags()
    {
        _usageStatusUnsupported = false;
        _usageCostUnsupported = false;
        _sessionPreviewUnsupported = false;
        _nodeListUnsupported = false;
        _operatorReadScopeUnavailable = false;
    }

    /// <summary>
    /// Provides user-defined notification rules to the categorizer so custom rules
    /// are applied when classifying incoming gateway notifications.
    /// Call after construction and whenever settings change.
    /// </summary>
    public void SetUserRules(IReadOnlyList<UserNotificationRule>? rules)
    {
        _userRules = rules;
    }

    protected override int ReceiveBufferSize => 16384;
    protected override string ClientRole => "gateway";

    protected override Task ProcessMessageAsync(string json)
    {
        ProcessMessage(json);
        return Task.CompletedTask;
    }

    protected override Task OnConnectedAsync()
    {
        ResetUnsupportedMethodFlags();
        return Task.CompletedTask;
    }

    protected override bool ShouldAutoReconnect()
    {
        return !_pairingRequiredAwaitingApproval;
    }

    protected override void OnDisconnected()
    {
        ClearPendingRequests();
    }

    protected override void OnDisposing()
    {
        ClearPendingRequests();
    }

    // Events
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

    public string? OperatorDeviceId => _operatorDeviceId;
    public IReadOnlyList<string> GrantedOperatorScopes => _grantedOperatorScopes;
    public bool IsConnectedToGateway => IsConnected;

    public OpenClawGatewayClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger)
    {
        var dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray");

        _deviceIdentity = new DeviceIdentity(dataPath, _logger);
        _deviceIdentity.Initialize();
        _connectAuthToken = _deviceIdentity.DeviceToken ?? _token;
    }

    public async Task DisconnectAsync()
    {
        if (IsConnected)
        {
            try
            {
                await CloseWebSocketAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during disconnect: {ex.Message}");
            }
        }
        ClearPendingRequests();
        RaiseStatusChanged(ConnectionStatus.Disconnected);
        _logger.Info("Disconnected");
    }

    public async Task CheckHealthAsync()
    {
        if (!IsConnected)
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
            RaiseStatusChanged(ConnectionStatus.Error);
            await ReconnectWithBackoffAsync();
        }
    }

    public async Task SendChatMessageAsync(string message, string? sessionKey = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Gateway connection is not open");
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message is required", nameof(message));

        var effectiveSessionKey = string.IsNullOrWhiteSpace(sessionKey)
            ? _mainSessionKey
            : sessionKey.Trim();

        var requestId = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackPendingChatSend(requestId, completion);

        var req = new
        {
            type = "req",
            id = requestId,
            method = "chat.send",
            @params = new
            {
                sessionKey = effectiveSessionKey,
                message,
                idempotencyKey = Guid.NewGuid().ToString()
            }
        };

        await SendRawAsync(JsonSerializer.Serialize(req));

        var completedTask = await Task.WhenAny(completion.Task, Task.Delay(5000, CancellationToken));
        if (completedTask != completion.Task)
        {
            RemovePendingChatSend(requestId);
            throw new TimeoutException("Timed out waiting for chat.send response from gateway");
        }

        await completion.Task;
        _logger.Info($"Sent chat message ({message.Length} chars)");
    }

    /// <summary>Request session list from gateway.</summary>
    public async Task RequestSessionsAsync()
    {
        if (_operatorReadScopeUnavailable) return;
        await SendTrackedRequestAsync("sessions.list");
    }

    /// <summary>Request usage/context info from gateway (may not be supported on all gateways).</summary>
    public async Task RequestUsageAsync()
    {
        if (_operatorReadScopeUnavailable) return;
        if (!IsConnected) return;
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
        if (_operatorReadScopeUnavailable) return;
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
        if (!IsConnected) return false;
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
        if (!IsConnected) return false;
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

    private async Task SendConnectMessageAsync(string? nonce = null)
    {
        var requestId = Guid.NewGuid().ToString();
        TrackPendingRequest(requestId, "connect");

        var signedAt = _challengeTimestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var connectNonce = nonce ?? string.Empty;
        var signatureToken = _signatureTokenMode is SignatureTokenMode.V3EmptyToken or SignatureTokenMode.V2EmptyToken
            ? string.Empty
            : _connectAuthToken;

        var signature = _signatureTokenMode is SignatureTokenMode.V2AuthToken or SignatureTokenMode.V2EmptyToken
            ? _deviceIdentity.SignConnectPayloadV2(
                connectNonce,
                signedAt,
                OperatorClientId,
                OperatorClientMode,
                OperatorRole,
                s_operatorScopes,
                signatureToken)
            : _deviceIdentity.SignConnectPayloadV3(
                connectNonce,
                signedAt,
                OperatorClientId,
                OperatorClientMode,
                OperatorRole,
                s_operatorScopes,
                signatureToken,
                OperatorPlatform,
                OperatorDeviceFamily);

        // Use "cli" client ID for native apps - no browser security checks
        var msg = new
        {
            type = "req",
            id = requestId,
            method = "connect",
            @params = new
            {
                minProtocol = 3,
                maxProtocol = 3,
                client = new
                {
                    id = OperatorClientId,  // Native client ID
                    version = "1.0.0",
                    platform = OperatorPlatform,
                    mode = OperatorClientMode,
                    displayName = OperatorClientDisplayName
                },
                role = OperatorRole,
                scopes = s_operatorScopes,
                caps = Array.Empty<string>(),
                commands = Array.Empty<string>(),
                permissions = new { },
                auth = new { token = _connectAuthToken },
                locale = "en-US",
                userAgent = "openclaw-windows-tray/1.0.0",
                device = new
                {
                    id = _deviceIdentity.DeviceId,
                    publicKey = _deviceIdentity.PublicKeyBase64Url,
                    signature,
                    signedAt,
                    nonce = connectNonce
                }
            }
        };

        try
        {
            await SendRawAsync(JsonSerializer.Serialize(msg));
        }
        catch
        {
            RemovePendingRequest(requestId);
            throw;
        }
    }

    private async Task SendTrackedRequestAsync(string method, object? parameters = null)
    {
        if (!IsConnected) return;

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

        lock (_pendingChatSendLock)
        {
            foreach (var completion in _pendingChatSendRequests.Values)
            {
                completion.TrySetException(new OperationCanceledException("Request canceled"));
            }

            _pendingChatSendRequests.Clear();
        }
    }

    private void TrackPendingChatSend(string requestId, TaskCompletionSource<bool> completion)
    {
        lock (_pendingChatSendLock)
        {
            _pendingChatSendRequests[requestId] = completion;
        }
    }

    private void RemovePendingChatSend(string requestId)
    {
        lock (_pendingChatSendLock)
        {
            _pendingChatSendRequests.Remove(requestId);
        }
    }

    private TaskCompletionSource<bool>? TakePendingChatSend(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        lock (_pendingChatSendLock)
        {
            if (!_pendingChatSendRequests.TryGetValue(requestId, out var completion))
            {
                return null;
            }

            _pendingChatSendRequests.Remove(requestId);
            return completion;
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
        string? requestId = null;
        if (root.TryGetProperty("id", out var idProp))
        {
            requestId = idProp.GetString();
            requestMethod = TakePendingRequestMethod(requestId);
        }

        var pendingChatSend = TakePendingChatSend(requestId);
        if (pendingChatSend != null)
        {
            if (root.TryGetProperty("ok", out var okChatProp) &&
                okChatProp.ValueKind == JsonValueKind.False)
            {
                var message = TryGetErrorMessage(root) ?? "request failed";
                _logger.Warn($"chat.send failed: {message}");
                pendingChatSend.TrySetException(new InvalidOperationException(message));
                return;
            }

            pendingChatSend.TrySetResult(true);
            return;
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

        // Handle handshake acknowledgement payload.
        if (payload.TryGetProperty("type", out var t) && t.GetString() == "hello-ok")
        {
            _pairingRequiredAwaitingApproval = false;
            _operatorDeviceId = TryGetHandshakeDeviceId(payload);
            _grantedOperatorScopes = TryGetHandshakeScopes(payload);
            _mainSessionKey = TryGetHandshakeMainSessionKey(payload) ?? "main";
            var newDeviceToken = TryGetHandshakeDeviceToken(payload);
            if (!string.IsNullOrWhiteSpace(newDeviceToken))
            {
                _deviceIdentity.StoreDeviceToken(newDeviceToken);
                _connectAuthToken = newDeviceToken;
                _logger.Info("Operator device token stored for reconnect");
            }

            _logger.Info("Handshake complete (hello-ok)");
            if (!string.IsNullOrWhiteSpace(_operatorDeviceId))
            {
                _logger.Info($"Operator device ID: {_operatorDeviceId}");
            }
            if (_grantedOperatorScopes.Length > 0)
            {
                _logger.Info($"Granted operator scopes: {string.Join(", ", _grantedOperatorScopes)}");
            }
            _logger.Info($"Main session key: {_mainSessionKey}");
            RaiseStatusChanged(ConnectionStatus.Connected);

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

        if (method == "connect" &&
            message.Contains("device signature invalid", StringComparison.OrdinalIgnoreCase))
        {
            var previousMode = _signatureTokenMode;
            _signatureTokenMode = _signatureTokenMode switch
            {
                SignatureTokenMode.V3AuthToken => SignatureTokenMode.V3EmptyToken,
                SignatureTokenMode.V3EmptyToken => SignatureTokenMode.V2AuthToken,
                SignatureTokenMode.V2AuthToken => SignatureTokenMode.V2EmptyToken,
                _ => SignatureTokenMode.V2EmptyToken
            };

            if (_signatureTokenMode != previousMode)
            {
                _logger.Warn($"Gateway rejected device signature with mode {previousMode}; retrying with mode {_signatureTokenMode}");
                _ = SendConnectMessageAsync(_currentChallengeNonce);
                return;
            }

            _logger.Warn("Gateway rejected device signature in all supported payload modes");
            return;
        }

        if (method == "connect" &&
            message.Contains("pairing required", StringComparison.OrdinalIgnoreCase))
        {
            _pairingRequiredAwaitingApproval = true;
            _logger.Warn("Pairing approval required for this device; auto-reconnect paused until manual reconnect or app restart");
            RaiseStatusChanged(ConnectionStatus.Error);
            return;
        }

        if (IsMissingScopeError(message, "operator.read") &&
            method is "sessions.list" or "usage.status" or "usage.cost" or "node.list")
        {
            if (!_operatorReadScopeUnavailable)
            {
                _logger.Warn("Gateway token lacks operator.read; disabling sessions/usage/nodes polling");
            }

            _operatorReadScopeUnavailable = true;
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

    private static bool IsMissingScopeError(string errorMessage, string scope)
    {
        if (string.IsNullOrWhiteSpace(errorMessage) || string.IsNullOrWhiteSpace(scope))
            return false;

        var expected = $"missing scope: {scope}";
        return errorMessage.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSessionCommandMethod(string method)
    {
        return method is "sessions.patch" or "sessions.reset" or "sessions.delete" or "sessions.compact";
    }

    private static string? TryGetHandshakeDeviceId(JsonElement payload)
    {
        if (payload.TryGetProperty("deviceId", out var deviceIdProp) &&
            deviceIdProp.ValueKind == JsonValueKind.String)
        {
            return deviceIdProp.GetString();
        }

        if (payload.TryGetProperty("device", out var deviceProp) &&
            deviceProp.ValueKind == JsonValueKind.Object)
        {
            if (deviceProp.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                return idProp.GetString();
            }

            if (deviceProp.TryGetProperty("deviceId", out var didProp) && didProp.ValueKind == JsonValueKind.String)
            {
                return didProp.GetString();
            }
        }

        return null;
    }

    private static string[] TryGetHandshakeScopes(JsonElement payload)
    {
        if (payload.TryGetProperty("scopes", out var scopesProp) &&
            scopesProp.ValueKind == JsonValueKind.Array)
        {
            var scopes = new List<string>();
            foreach (var scope in scopesProp.EnumerateArray())
            {
                if (scope.ValueKind == JsonValueKind.String)
                {
                    var value = scope.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        scopes.Add(value);
                    }
                }
            }

            return scopes.ToArray();
        }

        return Array.Empty<string>();
    }

    private static string? TryGetHandshakeMainSessionKey(JsonElement payload)
    {
        if (!payload.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!snapshot.TryGetProperty("sessionDefaults", out var sessionDefaults) || sessionDefaults.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!sessionDefaults.TryGetProperty("mainKey", out var mainKey) || mainKey.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = mainKey.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? TryGetHandshakeDeviceToken(JsonElement payload)
    {
        if (!payload.TryGetProperty("auth", out var authPayload) || authPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!authPayload.TryGetProperty("deviceToken", out var deviceToken) || deviceToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = deviceToken.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public string BuildMissingScopeFixCommands(string missingScope)
    {
        var scope = string.IsNullOrWhiteSpace(missingScope) ? "operator.write" : missingScope.Trim();
        var grantedScopes = _grantedOperatorScopes.Length == 0
            ? "(none reported by gateway)"
            : string.Join(", ", _grantedOperatorScopes);
        var deviceId = string.IsNullOrWhiteSpace(_operatorDeviceId)
            ? "(not reported for this operator connection)"
            : _operatorDeviceId;
        var likelyNodeToken = _grantedOperatorScopes.Any(s => s.StartsWith("node.", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.AppendLine("Quick Send is connected, but your token is missing required permission.");
        sb.AppendLine($"Missing scope: {scope}");
        sb.AppendLine("Note: requested connect scopes are declarative; the gateway may grant fewer scopes based on token/policy/device state.");
        sb.AppendLine();
        sb.AppendLine("Do this in Windows Tray right now:");
        sb.AppendLine("1. Right-click the tray icon and open Settings.");
        sb.AppendLine("2. Replace Gateway Token with an OPERATOR token that includes operator.write.");
        sb.AppendLine("3. Click Save.");
        sb.AppendLine("4. Reconnect from the tray menu (or restart the tray app).");
        sb.AppendLine("5. Retry Quick Send.");
        sb.AppendLine();
        sb.AppendLine("Token requirements for Quick Send:");
        sb.AppendLine("- Role: operator");
        sb.AppendLine("- Required scope: operator.write");
        sb.AppendLine("- Recommended scopes: operator.admin, operator.read, operator.approvals, operator.pairing, operator.write");

        if (likelyNodeToken)
        {
            sb.AppendLine();
            sb.AppendLine("Detected node.* scopes. This usually means a node token was pasted into Gateway Token.");
            sb.AppendLine("Quick Send requires an operator token, not a node token.");
        }

        sb.AppendLine();
        sb.AppendLine("Connection details from this app (for debugging/support):");
        sb.AppendLine($"- role: operator");
        sb.AppendLine($"- client.id: {OperatorClientId}");
        sb.AppendLine($"- client.displayName: {OperatorClientDisplayName}");
        sb.AppendLine($"- operator device id: {deviceId}");
        sb.AppendLine($"- granted scopes: {grantedScopes}");
        sb.AppendLine();
        sb.AppendLine("If this still fails after updating the token, copy this block and share it with your gateway admin.");
        return sb.ToString().TrimEnd();
    }

    public string BuildPairingApprovalFixCommands()
    {
        var deviceId = !string.IsNullOrWhiteSpace(_operatorDeviceId)
            ? _operatorDeviceId
            : _deviceIdentity.DeviceId;
        var grantedScopes = _grantedOperatorScopes.Length == 0
            ? "(none reported by gateway yet)"
            : string.Join(", ", _grantedOperatorScopes);

        var sb = new StringBuilder();
        sb.AppendLine("Quick Send requires this device to be approved (paired) in the gateway.");
        sb.AppendLine("Gateway reported: pairing required");
        sb.AppendLine();
        sb.AppendLine("Do this now:");
        sb.AppendLine("1. Open the gateway admin UI.");
        sb.AppendLine("2. Go to pending pairing/device approvals.");
        sb.AppendLine("3. Approve this Windows tray device ID.");
        sb.AppendLine("4. Return to tray and reconnect (or restart tray app).");
        sb.AppendLine("5. Retry Quick Send.");
        sb.AppendLine();
        sb.AppendLine("Connection details from this app (for debugging/support):");
        sb.AppendLine("- role: operator");
        sb.AppendLine($"- client.id: {OperatorClientId}");
        sb.AppendLine($"- client.displayName: {OperatorClientDisplayName}");
        sb.AppendLine($"- operator device id: {deviceId}");
        sb.AppendLine($"- granted scopes: {grantedScopes}");
        sb.AppendLine();
        sb.AppendLine("If approval keeps failing, share this block with your gateway admin.");
        return sb.ToString().TrimEnd();
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
        long? ts = null;
        if (root.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("nonce", out var nonceProp))
        {
            nonce = nonceProp.GetString();

            if (payload.TryGetProperty("ts", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number)
            {
                ts = tsProp.GetInt64();
            }
        }

        _challengeTimestampMs = ts;
        _currentChallengeNonce = nonce;
        
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
        var rawText = root.GetRawText();
        _logger.Debug($"Chat event received: {rawText.Substring(0, Math.Min(200, rawText.Length))}");
        
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
        var (title, type) = _categorizer.Classify(notification, _userRules);
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
        SessionInfo[] snapshot;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionKey, out var session))
            {
                session = new SessionInfo
                {
                    Key = sessionKey,
                    IsMain = isMain,
                    Status = "active"
                };
                _sessions[sessionKey] = session;
            }

            session.CurrentActivity = currentActivity;
            session.LastSeen = DateTime.UtcNow;

            snapshot = GetSessionListInternal();
        }

        SessionsUpdated?.Invoke(this, snapshot);
    }

    public SessionInfo[] GetSessionList()
    {
        lock (_sessionsLock)
        {
            return GetSessionListInternal();
        }
    }

    private SessionInfo[] GetSessionListInternal()
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

        _logger.Info(healthList.Count > 0
            ? $"Channel health: {string.Join(", ", healthList.ConvertAll(c => $"{c.Name}={c.Status}"))}"
            : "Channel health: no channels");
        ChannelHealthUpdated?.Invoke(this, healthList.ToArray());
    }

    private void ParseSessions(JsonElement sessions)
    {
        try
        {
            SessionInfo[] snapshot;
            lock (_sessionsLock)
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

                snapshot = GetSessionListInternal();
            }

            SessionsUpdated?.Invoke(this, snapshot);
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

            lock (_nodesLock)
            {
                _nodes.Clear();
                foreach (var node in ordered)
                    _nodes[node.NodeId] = node;
            }

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
        var (title, type) = _categorizer.Classify(notification, _userRules, _preferStructuredCategories);
        notification.Title = title;
        notification.Type = type;
        NotificationReceived?.Invoke(this, notification);
    }

    // --- Utility ---

    // FrozenDictionary gives O(1) case-insensitive lookup without allocating a
    // lowercased copy of toolName on every call.
    private static readonly FrozenDictionary<string, ActivityKind> s_toolKindMap =
        new Dictionary<string, ActivityKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["exec"]       = ActivityKind.Exec,
            ["read"]       = ActivityKind.Read,
            ["write"]      = ActivityKind.Write,
            ["edit"]       = ActivityKind.Edit,
            ["web_search"] = ActivityKind.Search,
            ["web_fetch"]  = ActivityKind.Search,
            ["browser"]    = ActivityKind.Browser,
            ["message"]    = ActivityKind.Message,
            ["tts"]        = ActivityKind.Tool,
            ["image"]      = ActivityKind.Tool,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static ActivityKind ClassifyTool(string toolName) =>
        s_toolKindMap.TryGetValue(toolName, out var kind) ? kind : ActivityKind.Tool;

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
}
