using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public class OpenClawGatewayClient : WebSocketClientBase, IOperatorGatewayClient
{
    private const string OperatorClientId = "cli";
    private const string OperatorClientDisplayName = "OpenClaw Windows Tray";
    private const string OperatorClientMode = "cli";
    private const string OperatorRole = "operator";
    private const string OperatorPlatform = "windows";
    private const string OperatorDeviceFamily = "desktop";
    private static readonly Regex s_pairingRequestIdRegex = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.Compiled);
    private static readonly string[] s_operatorScopes =
    [
        "operator.admin",
    ];
    private static readonly string[] s_operatorBootstrapScopes =
    [
        "operator.approvals",
        "operator.read",
        "operator.talk.secrets",
        "operator.write"
    ];

    // Tracked state
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private GatewayUsageInfo? _usage;
    private GatewayUsageStatusInfo? _usageStatus;
    private GatewayCostUsageInfo? _usageCost;
    private readonly Dictionary<string, string> _pendingRequestMethods = new();
    private readonly Dictionary<string, TaskCompletionSource<ChatSendResult>> _pendingChatSendRequests = new();
    private readonly object _pendingRequestLock = new();
    private readonly object _pendingChatSendLock = new();
    private readonly object _sessionsLock = new();
    private readonly DeviceIdentity _deviceIdentity;
    private readonly string _currentGatewayUrl;
    private string _mainSessionKey = "main";
    private string? _operatorDeviceId;
    private string[] _grantedOperatorScopes = Array.Empty<string>();
    private string _connectAuthToken;
    private bool _useV2Signature; // true after v3 signature rejected by gateway

    /// <summary>Set to true to skip v3 and use v2 signatures directly (for gateways that don't support v3).</summary>
    public bool UseV2Signature { get => _useV2Signature; set => _useV2Signature = value; }
    private long? _challengeTimestampMs;
    private string? _currentChallengeNonce;
    private bool _usageStatusUnsupported;
    private bool _usageCostUnsupported;
    private bool _sessionPreviewUnsupported;
    private bool _nodeListUnsupported;
    private bool _modelsListUnsupported;
    private bool _nodePairListUnsupported;
    private bool _devicePairListUnsupported;
    private bool _agentsListUnsupported;
    private bool _agentFilesListUnsupported;
    private bool _agentFileGetUnsupported;
    private bool _operatorReadScopeUnavailable;
    private bool _pairingRequiredAwaitingApproval;
    private string? _pairingRequiredRequestId;
    private bool _authFailed;
    private readonly bool _tokenIsBootstrapToken;
    private readonly bool _bootstrapPairAsNode;

    /// <summary>True when the gateway reported "pairing required" for this device.</summary>
    public bool IsPairingRequired => _pairingRequiredAwaitingApproval;

    /// <summary>Safe requestId returned in structured pairing-required details, when present.</summary>
    public string? PairingRequiredRequestId => _pairingRequiredRequestId;

    /// <summary>True when the device signature was rejected in all supported modes.</summary>
    public bool IsAuthFailed => _authFailed;

    /// <summary>The gateway auth token used for this connection.</summary>
    public string ConnectAuthToken => _connectAuthToken;
    private IReadOnlyList<UserNotificationRule>? _userRules;
    private bool _preferStructuredCategories = true;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingWizardResponses = new();

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
        _modelsListUnsupported = false;
        _nodePairListUnsupported = false;
        _devicePairListUnsupported = false;
        _agentsListUnsupported = false;
        _agentFilesListUnsupported = false;
        _agentFileGetUnsupported = false;
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
        return !_pairingRequiredAwaitingApproval && !_authFailed;
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
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    public event EventHandler<JsonElement>? CronListUpdated;
    public event EventHandler<JsonElement>? CronStatusUpdated;
    public event EventHandler<JsonElement>? CronRunsUpdated;
    public event EventHandler<JsonElement>? SkillsStatusUpdated;
    public event EventHandler<JsonElement>? ConfigUpdated;
    public event EventHandler<JsonElement>? ConfigSchemaUpdated;

    // New events for agent events, pairing, and models
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<PairingListInfo>? NodePairListUpdated;
    public event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;
    public event EventHandler<PresenceEntry[]>? PresenceUpdated;
    public event EventHandler<JsonElement>? AgentsListUpdated;
    public event EventHandler<JsonElement>? AgentFilesListUpdated;
    public event EventHandler<JsonElement>? AgentFileContentUpdated;
    public event EventHandler<AgentEventInfo>? ChatEventReceived;

    /// <summary>Raised when a device token is received from the gateway during hello-ok handshake.</summary>
    public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    /// <summary>Raised when the hello-ok handshake completes successfully.</summary>
    public event EventHandler? HandshakeSucceeded;
    /// <summary>Raised when the gateway requires pairing approval for this device.</summary>
    public event EventHandler<string?>? PairingRequired;
    /// <summary>Raised when v3 signature was rejected and client fell back to v2.</summary>
    public event EventHandler? V2SignatureFallback;

    public string? OperatorDeviceId => _operatorDeviceId;
    public IReadOnlyList<string> GrantedOperatorScopes => _grantedOperatorScopes;
    public bool IsConnectedToGateway => IsConnected;

    public OpenClawGatewayClient(string gatewayUrl, string token, IOpenClawLogger? logger = null, bool tokenIsBootstrapToken = false, bool bootstrapPairAsNode = false, string? identityPath = null)
        : base(gatewayUrl, token, logger)
    {
        _tokenIsBootstrapToken = tokenIsBootstrapToken;
        _bootstrapPairAsNode = bootstrapPairAsNode;
        _currentGatewayUrl = gatewayUrl;
        var dataPath = identityPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray");

        _deviceIdentity = new DeviceIdentity(dataPath, _logger);
        _deviceIdentity.Initialize();
        _connectAuthToken = _deviceIdentity.DeviceToken ?? (_tokenIsBootstrapToken ? string.Empty : _token);
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
        _ = await SendChatMessageForRunAsync(message, sessionKey).ConfigureAwait(false);
    }

    public async Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Gateway connection is not open");
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message is required", nameof(message));

        var effectiveSessionKey = string.IsNullOrWhiteSpace(sessionKey)
            ? _mainSessionKey
            : sessionKey.Trim();

        var requestId = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<ChatSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        var result = await completion.Task.ConfigureAwait(false);
        _logger.Info($"Sent chat message ({message.Length} chars)");
        return result;
    }

    /// <summary>
    /// Sends a wizard RPC request and waits for the response payload.
    /// Used for wizard.start, wizard.next, wizard.cancel, wizard.status.
    /// </summary>
    public async Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Gateway connection is not open");

        _logger.Info($"[GatewayClient] Sending frame: {method}");
        var requestId = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWizardResponses[requestId] = completion;
        TrackPendingRequest(requestId, method);

        try
        {
            await SendRawAsync(SerializeRequest(requestId, method, parameters));
        }
        catch
        {
            _pendingWizardResponses.TryRemove(requestId, out _);
            RemovePendingRequest(requestId);
            throw;
        }

        var completedTask = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs, CancellationToken));
        if (completedTask != completion.Task)
        {
            _pendingWizardResponses.TryRemove(requestId, out _);
            throw new TimeoutException($"Timed out waiting for {method} response");
        }

        return await completion.Task;
    }

    /// <summary>Request session list from gateway.</summary>
    public async Task RequestSessionsAsync(string? agentId = null)
    {
        if (_operatorReadScopeUnavailable) return;
        if (!string.IsNullOrEmpty(agentId))
            await SendTrackedRequestAsync("sessions.list", new { agentId });
        else
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

    // Cron job management

    public async Task RequestCronListAsync()
    {
        await SendTrackedRequestAsync("cron.list", new { includeDisabled = true });
    }

    public async Task RequestCronStatusAsync()
    {
        await SendTrackedRequestAsync("cron.status");
    }

    public Task<bool> RunCronJobAsync(string jobId, bool force = true)
    {
        return TrySendTrackedRequestAsync("cron.run", new { id = jobId, force });
    }

    public Task<bool> RemoveCronJobAsync(string jobId)
    {
        return TrySendTrackedRequestAsync("cron.remove", new { id = jobId });
    }

    public Task<bool> AddCronJobAsync(object jobDefinition)
    {
        return TrySendTrackedRequestAsync("cron.add", jobDefinition);
    }

    public Task<bool> UpdateCronJobAsync(string id, object patch)
    {
        // Wire format uses "id" consistently with cron.run / cron.remove
        return TrySendTrackedRequestAsync("cron.update", new { id, patch });
    }

    public async Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0)
    {
        // Wire format uses "id" consistently with cron.run / cron.remove
        await SendTrackedRequestAsync("cron.runs", new { id, limit, offset });
    }

    // Skills/plugin management

    public async Task RequestSkillsStatusAsync(string? agentId = null)
    {
        if (!string.IsNullOrEmpty(agentId))
            await SendTrackedRequestAsync("skills.status", new { agentId });
        else
            await SendTrackedRequestAsync("skills.status");
    }

    public Task<bool> InstallSkillAsync(string skillId)
    {
        return TrySendTrackedRequestAsync("skills.install", new { id = skillId });
    }

    public Task<bool> UpdateSkillAsync(string skillId)
    {
        return TrySendTrackedRequestAsync("skills.update", new { id = skillId });
    }

    // Gateway config management

    public async Task RequestConfigAsync()
    {
        await SendTrackedRequestAsync("config.get");
    }

    public async Task RequestConfigSchemaAsync()
    {
        await SendTrackedRequestAsync("config.schema");
    }

    public Task<bool> SetConfigAsync(string path, object value)
    {
        return TrySendTrackedRequestAsync("config.set", new { path, value });
    }

    /// <summary>
    /// Patch the gateway config. The gateway expects { raw: "full json string", baseHash: "..." }.
    /// </summary>
    public Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash)
    {
        var raw = fullConfig.GetRawText();
        if (baseHash != null)
            return TrySendTrackedRequestAsync("config.patch", new { raw, baseHash });
        else
            return TrySendTrackedRequestAsync("config.patch", new { raw });
    }

    // Agent methods

    public async Task RequestAgentsListAsync()
    {
        if (_agentsListUnsupported) return;
        await SendTrackedRequestAsync("agents.list");
    }

    public async Task RequestAgentFilesListAsync(string agentId = "main")
    {
        if (_agentFilesListUnsupported) return;
        await SendTrackedRequestAsync("agents.files.list", new { agentId });
    }

    public async Task RequestAgentFileGetAsync(string agentId, string name)
    {
        if (_agentFileGetUnsupported) return;
        await SendTrackedRequestAsync("agents.files.get", new { agentId, name });
    }

    // Models list

    public async Task RequestModelsListAsync()
    {
        if (_modelsListUnsupported) return;
        await SendTrackedRequestAsync("models.list", new { view = "configured" });
    }

    // Node/Device pairing

    public async Task RequestNodePairListAsync()
    {
        if (_nodePairListUnsupported) return;
        await SendTrackedRequestAsync("node.pair.list");
    }

    public Task<bool> NodePairApproveAsync(string requestId)
    {
        return TrySendTrackedRequestAsync("node.pair.approve", new { requestId });
    }

    public Task<bool> NodePairRejectAsync(string requestId)
    {
        return TrySendTrackedRequestAsync("node.pair.reject", new { requestId });
    }

    public async Task RequestDevicePairListAsync()
    {
        if (_devicePairListUnsupported) return;
        await SendTrackedRequestAsync("device.pair.list");
    }

    public Task<bool> DevicePairApproveAsync(string requestId)
    {
        return TrySendTrackedRequestAsync("device.pair.approve", new { requestId });
    }

    public Task<bool> DevicePairRejectAsync(string requestId)
    {
        return TrySendTrackedRequestAsync("device.pair.reject", new { requestId });
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
        var role = GetConnectRole();
        var requestedScopes = GetRequestedScopes(role);

        var signedAt = _challengeTimestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var connectNonce = nonce ?? string.Empty;
        var signatureToken = GetSignatureToken();
        var authPayload = BuildAuthPayload();

        // Log complete handshake details for diagnostics
        _logger.Info($"[HANDSHAKE] → Sending connect:");
        _logger.Info($"  role={role}, clientId={OperatorClientId}, mode={OperatorClientMode}");
        _logger.Info($"  scopes=[{string.Join(", ", requestedScopes)}]");
        _logger.Info($"  isBootstrap={_tokenIsBootstrapToken}, hasDeviceToken={!string.IsNullOrEmpty(_deviceIdentity.DeviceToken)}");
        _logger.Info($"  deviceId={_deviceIdentity.DeviceId[..Math.Min(16, _deviceIdentity.DeviceId.Length)]}...");
        _logger.Info($"  nonce={(!string.IsNullOrEmpty(connectNonce) ? connectNonce[..Math.Min(12, connectNonce.Length)] + "..." : "(empty)")}");
        _logger.Info($"  signedAt={signedAt}");
        _logger.Info($"  sigToken(len)={signatureToken.Length}, preview=[REDACTED]");
        _logger.Info($"  signature format={(_useV2Signature ? "v2" : "v3")}, platform={OperatorPlatform}, family={OperatorDeviceFamily}");

        var signedPayload = _useV2Signature
            ? _deviceIdentity.BuildConnectPayloadV2(connectNonce, signedAt, OperatorClientId, OperatorClientMode, role, requestedScopes, signatureToken)
            : _deviceIdentity.BuildConnectPayloadV3(connectNonce, signedAt, OperatorClientId, OperatorClientMode, role, requestedScopes, signatureToken, OperatorPlatform, OperatorDeviceFamily);
        _logger.Info($"[HANDSHAKE] signed: {TokenSanitizer.Sanitize(signedPayload)}");

        // Also log what auth field we're sending
        var authObj = BuildAuthPayload();
        var authJson = JsonSerializer.Serialize(authObj);
        _logger.Info($"[HANDSHAKE] auth: {RedactAuthPayload(authJson)}");

        // Try v3 first (matches reference client). Fall back to v2 if gateway rejects v3.
        var signature = _useV2Signature
            ? _deviceIdentity.SignConnectPayloadV2(
                connectNonce, signedAt, OperatorClientId, OperatorClientMode,
                role, requestedScopes, signatureToken)
            : _deviceIdentity.SignConnectPayloadV3(
                connectNonce, signedAt, OperatorClientId, OperatorClientMode,
                role, requestedScopes, signatureToken,
                OperatorPlatform, OperatorDeviceFamily);

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
                role,
                scopes = requestedScopes,
                caps = Array.Empty<string>(),
                commands = Array.Empty<string>(),
                permissions = new { },
                auth = BuildAuthPayload(),
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

    private string GetConnectRole()
    {
        return _bootstrapPairAsNode && _tokenIsBootstrapToken && string.IsNullOrEmpty(_deviceIdentity.DeviceToken)
            ? "node"
            : OperatorRole;
    }

    private string[] GetRequestedScopes(string role)
    {
        if (role == "node")
            return [];

        if (string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
        {
            // Shared gateway token (non-bootstrap) → request admin scope.
            // Bootstrap tokens get bounded scopes.
            if (!_tokenIsBootstrapToken)
                return s_operatorScopes;

            return s_operatorBootstrapScopes;
        }

        return _deviceIdentity.DeviceTokenScopes is { Count: > 0 } scopes
            ? scopes.ToArray()
            : s_operatorScopes;
    }

    /// <summary>
    /// Builds the auth payload for the connect handshake, matching the gateway's
    /// HandshakeConnectAuth type: { token?, bootstrapToken?, deviceToken?, password? }.
    /// Fresh devices send bootstrapToken for initial QR/setup-code pairing.
    /// Paired devices send an explicit deviceToken.
    /// </summary>
    private Dictionary<string, string> BuildAuthPayload()
    {
        var auth = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
        {
            auth["deviceToken"] = _deviceIdentity.DeviceToken;
        }
        else if (_tokenIsBootstrapToken)
        {
            // Fresh QR/setup-code device: do not also send auth.token, which upstream treats
            // as an explicit gateway token and therefore suppresses bootstrap pairing.
            auth["bootstrapToken"] = _token;
        }
        else
        {
            auth["token"] = _connectAuthToken;
        }

        return auth;
    }

    private string GetSignatureToken()
    {
        if (!string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
            return _deviceIdentity.DeviceToken;

        return _tokenIsBootstrapToken ? _token : _connectAuthToken;
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

        foreach (var completion in _pendingWizardResponses.Values)
        {
            completion.TrySetException(new OperationCanceledException("Gateway connection lost while waiting for wizard response"));
        }

        _pendingWizardResponses.Clear();
    }

    private void TrackPendingChatSend(string requestId, TaskCompletionSource<ChatSendResult> completion)
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

    private TaskCompletionSource<ChatSendResult>? TakePendingChatSend(string? requestId)
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

            pendingChatSend.TrySetResult(ParseChatSendResult(root));
            return;
        }

        // Check for pending wizard response
        if (requestId != null && _pendingWizardResponses.TryRemove(requestId, out var wizardCompletion))
        {
            if (root.TryGetProperty("ok", out var okWiz) && okWiz.ValueKind == JsonValueKind.False)
            {
                var message = TryGetErrorMessage(root) ?? "wizard request failed";
                wizardCompletion.TrySetException(new InvalidOperationException(message));
            }
            else if (root.TryGetProperty("payload", out var wizPayload))
            {
                // Log the payload kind for debugging
                var wizardPayloadText = TokenSanitizer.Sanitize(wizPayload.ToString());
                _logger.Info($"Wizard response payload kind={wizPayload.ValueKind}, raw={wizardPayloadText[..Math.Min(200, wizardPayloadText.Length)]}");
                wizardCompletion.TrySetResult(wizPayload.Clone());
            }
            else
            {
                wizardCompletion.TrySetResult(root.Clone());
            }
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
            _logger.Info($"[HANDSHAKE] Received hello-ok!");
            _pairingRequiredAwaitingApproval = false;
            _pairingRequiredRequestId = null;
            _authFailed = false;
            ResetReconnectAttempts();
            _operatorDeviceId = TryGetHandshakeDeviceId(payload);
            _grantedOperatorScopes = TryGetHandshakeScopes(payload);
            _mainSessionKey = TryGetHandshakeMainSessionKey(payload) ?? "main";
            _logger.Info($"[HANDSHAKE] deviceId={_operatorDeviceId}, scopes=[{string.Join(", ", _grantedOperatorScopes)}], mainSession={_mainSessionKey}");
            PublishGatewaySelf(GatewaySelfInfo.FromHelloOk(payload));
            if (_bootstrapPairAsNode)
            {
                var nodeDeviceToken = TryGetHandshakeDeviceTokenCore(payload, "node", allowDirectDeviceTokenFallback: true);
                if (!string.IsNullOrWhiteSpace(nodeDeviceToken))
                {
                    var nodeDeviceTokenScopes = TryGetHandshakeDeviceTokenScopesCore(payload, "node", allowDirectDeviceTokenFallback: true);
                    _deviceIdentity.StoreDeviceTokenForRole("node", nodeDeviceToken, nodeDeviceTokenScopes);
                    _logger.Info("Node device token stored for Windows tray node reconnect");
                    DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(nodeDeviceToken, nodeDeviceTokenScopes, "node"));
                }
            }

            var newDeviceToken = _bootstrapPairAsNode
                ? TryGetHandshakeDeviceTokenCore(payload, OperatorRole, allowDirectDeviceTokenFallback: false)
                : TryGetHandshakeDeviceTokenCore(payload, preferredRole: null);
            if (!string.IsNullOrWhiteSpace(newDeviceToken))
            {
                var deviceTokenScopes = _bootstrapPairAsNode
                    ? TryGetHandshakeDeviceTokenScopesCore(payload, OperatorRole, allowDirectDeviceTokenFallback: false)
                    : TryGetHandshakeDeviceTokenScopesCore(payload, preferredRole: null);
                _deviceIdentity.StoreDeviceTokenWithScopes(newDeviceToken, deviceTokenScopes);
                _connectAuthToken = newDeviceToken;
                _logger.Info("Operator device token stored for reconnect");
                DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(newDeviceToken, deviceTokenScopes, "operator"));
            }

            _logger.Info("Handshake complete (hello-ok)");
            HandshakeSucceeded?.Invoke(this, EventArgs.Empty);
            if (!string.IsNullOrWhiteSpace(_operatorDeviceId))
            {
                _logger.Info($"Operator device ID: {_operatorDeviceId}");
            }
            if (_grantedOperatorScopes.Length > 0)
            {
                _logger.Info($"Granted operator scopes: {string.Join(", ", _grantedOperatorScopes)}");
            }
            _logger.Info($"Main session key: {_mainSessionKey}");

            // Extract presence from snapshot
            TryParsePresence(payload);

            RaiseStatusChanged(ConnectionStatus.Connected);

            // Request initial state after handshake
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await CheckHealthAsync();
                await RequestSessionsAsync();
                await RequestUsageAsync();
                await RequestNodesAsync();
                await RequestAgentsListAsync();
            });
        }

        // Handle health response — channels
        if (payload.TryGetProperty("channels", out var channels))
        {
            PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(payload));
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
                PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(payload));
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
            case "cron.list":
                CronListUpdated?.Invoke(this, payload.Clone());
                return true;
            case "cron.status":
                CronStatusUpdated?.Invoke(this, payload.Clone());
                return true;
            case "cron.run":
            case "cron.remove":
            case "cron.add":
            case "cron.update":
                // After add/update/remove, refresh the list
                _ = RequestCronListAsync();
                return true;
            case "cron.runs":
                CronRunsUpdated?.Invoke(this, payload.Clone());
                return true;
            case "skills.status":
                SkillsStatusUpdated?.Invoke(this, payload.Clone());
                return true;
            case "skills.install":
            case "skills.update":
                return true;
            case "config.get":
                ConfigUpdated?.Invoke(this, payload.Clone());
                return true;
            case "config.schema":
                ConfigSchemaUpdated?.Invoke(this, payload.Clone());
                return true;
            case "config.set":
            case "config.patch":
                return true;
            case "agents.list":
                AgentsListUpdated?.Invoke(this, payload.Clone());
                return true;
            case "agents.files.list":
                AgentFilesListUpdated?.Invoke(this, payload.Clone());
                return true;
            case "agents.files.get":
                AgentFileContentUpdated?.Invoke(this, payload.Clone());
                return true;
            case "models.list":
                ParseModelsList(payload);
                return true;
            case "node.pair.list":
                ParseNodePairList(payload);
                return true;
            case "node.pair.approve":
            case "node.pair.reject":
                _ = RequestNodePairListAsync();
                return true;
            case "device.pair.list":
                ParseDevicePairList(payload);
                return true;
            case "device.pair.approve":
            case "device.pair.reject":
                _ = RequestDevicePairListAsync();
                return true;
            default:
                return false;
        }
    }

    private static ChatSendResult ParseChatSendResult(JsonElement root)
    {
        string? runId = null;
        string? sessionKey = null;
        var cached = false;

        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("runId", out var runIdProp))
                runId = runIdProp.GetString();
            if (payload.TryGetProperty("sessionKey", out var sessionKeyProp))
                sessionKey = sessionKeyProp.GetString();
        }

        if (root.TryGetProperty("meta", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("cached", out var cachedProp) &&
            cachedProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            cached = cachedProp.GetBoolean();
        }

        return new ChatSendResult
        {
            RunId = runId,
            SessionKey = sessionKey,
            Cached = cached
        };
    }

    private void HandleRequestError(string? method, JsonElement root)
    {
        var message = TryGetErrorMessage(root) ?? "request failed";

        if (string.IsNullOrEmpty(method))
        {
            _logger.Warn($"Gateway request failed: {message}");
            return;
        }

        if (method == "connect")
        {
            var detailCode = TryGetErrorDetailCode(root);
            _logger.Warn($"[HANDSHAKE] Connect error from gateway: message=\"{message}\", detailCode={detailCode ?? "none"}");
            // Log raw JSON for debugging (truncated)
            var rawJson = root.ToString() ?? "";
            if (rawJson.Length > 500) rawJson = rawJson[..500] + "...";
            _logger.Info($"[HANDSHAKE] Raw error response: {rawJson}");
        }

        if (method == "connect" &&
            (message.Contains("device signature invalid", StringComparison.OrdinalIgnoreCase) ||
             TryGetErrorDetailCode(root) == "DEVICE_AUTH_SIGNATURE_INVALID"))
        {
            if (!_useV2Signature)
            {
                // v3 rejected — set flag so next connect uses v2.
                // Don't retry on this socket — gateway closes it after rejection.
                // The auto-reconnect will use v2 on the fresh connection.
                _useV2Signature = true;
                _logger.Warn($"[HANDSHAKE] v3 signature rejected, will use v2 on reconnect");
                V2SignatureFallback?.Invoke(this, EventArgs.Empty);
                return;
            }
            // v2 also rejected — real auth error
            _logger.Warn($"[HANDSHAKE] v2 signature also rejected — wrong key or token. Raw: {message}");
            _authFailed = true;
            RaiseAuthenticationFailed($"device signature rejected — {message}");
            RaiseStatusChanged(ConnectionStatus.Error);
            return;
        }

        var pairingDetails = TryGetPairingConnectErrorDetails(root);
        if (method == "connect" &&
            (pairingDetails.IsPairingRequired || message.Contains("pairing required", StringComparison.OrdinalIgnoreCase)))
        {
            _pairingRequiredAwaitingApproval = true;
            _pairingRequiredRequestId = pairingDetails.RequestId;
            _logger.Warn($"[HANDSHAKE] Pairing required (requestId={pairingDetails.RequestId}). Waiting for approval.");
            PairingRequired?.Invoke(this, pairingDetails.RequestId);
            return;
        }

        // Permanent auth failures — stop retrying and notify the app
        var detailCode2 = TryGetErrorDetailCode(root);
        if (method == "connect" && (IsTerminalAuthError(message) || IsTerminalAuthDetailCode(detailCode2)))
        {
            _authFailed = true;
            RaiseAuthenticationFailed(message);
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
                case "models.list":
                    _modelsListUnsupported = true;
                    _logger.Warn("models.list unsupported on gateway");
                    return;
                case "node.pair.list":
                    _nodePairListUnsupported = true;
                    _logger.Warn("node.pair.list unsupported on gateway");
                    return;
                case "device.pair.list":
                    _devicePairListUnsupported = true;
                    _logger.Warn("device.pair.list unsupported on gateway");
                    return;
                case "agents.list":
                    _agentsListUnsupported = true;
                    _logger.Warn("agents.list unsupported on gateway");
                    return;
                case "agents.files.list":
                    _agentFilesListUnsupported = true;
                    _logger.Warn("agents.files.list unsupported on gateway");
                    return;
                case "agents.files.get":
                    _agentFileGetUnsupported = true;
                    _logger.Warn("agents.files.get unsupported on gateway");
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

    private static readonly Regex AuthPayloadTokenPattern = new(
        @"""(token|deviceToken|bootstrapToken)""\s*:\s*""[^""]+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string RedactAuthPayload(string authJson)
    {
        return AuthPayloadTokenPattern.Replace(
            authJson,
            m => $"\"{m.Groups[1].Value}\":\"[REDACTED]\"");
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

    /// <summary>
    /// Extract the structured error detail code from the gateway error response.
    /// Checks error.details.code and error.data.details.code.
    /// </summary>
    private static string? TryGetErrorDetailCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        if (TryGetPairingDetailsElement(error, out var details) &&
            details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.String)
            return code.GetString();
        return null;
    }

    private static PairingConnectErrorDetails TryGetPairingConnectErrorDetails(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return default;

        if (!TryGetPairingDetailsElement(error, out var details) || details.ValueKind != JsonValueKind.Object)
            return default;

        var isPairingRequired = details.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
            && string.Equals(code.GetString(), "PAIRING_REQUIRED", StringComparison.Ordinal);
        var requestId = TryGetSafePairingRequestId(details);
        return new PairingConnectErrorDetails(isPairingRequired, requestId);
    }

    private static bool TryGetPairingDetailsElement(JsonElement error, out JsonElement details)
    {
        if (error.TryGetProperty("details", out details))
            return true;

        if (error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("details", out details))
        {
            return true;
        }

        details = default;
        return false;
    }

    private static string? TryGetSafePairingRequestId(JsonElement details)
    {
        if (!details.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String)
            return null;

        var value = requestId.GetString()?.Trim();
        return value is not null && s_pairingRequestIdRegex.IsMatch(value) ? value : null;
    }

    private readonly record struct PairingConnectErrorDetails(bool IsPairingRequired, string? RequestId);

    private static bool IsUnknownMethodError(string errorMessage)
    {
        return errorMessage.Contains("unknown method", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalAuthError(string errorMessage)
    {
        return errorMessage.Contains("token mismatch", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("origin not allowed", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("too many failed", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("bootstrap token invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalAuthDetailCode(string? code) => code is
        "AUTH_TOKEN_MISMATCH" or "AUTH_BOOTSTRAP_TOKEN_INVALID" or
        "AUTH_DEVICE_TOKEN_MISMATCH" or "AUTH_RATE_LIMITED" or
        "AUTH_TOKEN_NOT_CONFIGURED";

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
        if (payload.TryGetProperty("auth", out var authPayload) &&
            authPayload.ValueKind == JsonValueKind.Object &&
            authPayload.TryGetProperty("scopes", out var authScopes) &&
            authScopes.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(authScopes);
        }

        if (payload.TryGetProperty("scopes", out var scopesProp) &&
            scopesProp.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(scopesProp);
        }

        return [];
    }

    private static string[] ReadStringArray(JsonElement array)
    {
        var buffer = new string[array.GetArrayLength()];
        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    buffer[count++] = value;
            }
        }

        return buffer[..count];
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
        return TryGetHandshakeDeviceTokenCore(payload, preferredRole: null);
    }

    private static string? TryGetHandshakeDeviceTokenCore(JsonElement payload, string? preferredRole)
    {
        return TryGetHandshakeDeviceTokenCore(payload, preferredRole, allowDirectDeviceTokenFallback: true);
    }

    private static string? TryGetHandshakeDeviceTokenCore(JsonElement payload, string? preferredRole, bool allowDirectDeviceTokenFallback)
    {
        if (!payload.TryGetProperty("auth", out var authPayload) || authPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredRole) &&
            authPayload.TryGetProperty("deviceTokens", out var deviceTokens) &&
            deviceTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in deviceTokens.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                if (entry.TryGetProperty("role", out var role) &&
                    role.ValueKind == JsonValueKind.String &&
                    string.Equals(role.GetString(), preferredRole, StringComparison.OrdinalIgnoreCase) &&
                    entry.TryGetProperty("deviceToken", out var roleToken) &&
                    roleToken.ValueKind == JsonValueKind.String)
                {
                    var roleTokenValue = roleToken.GetString();
                    if (!string.IsNullOrWhiteSpace(roleTokenValue))
                        return roleTokenValue;
                }
            }

            if (!allowDirectDeviceTokenFallback)
            {
                return null;
            }
        }

        if (!authPayload.TryGetProperty("deviceToken", out var deviceToken) || deviceToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = deviceToken.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string[]? TryGetHandshakeDeviceTokenScopesCore(JsonElement payload, string? preferredRole)
    {
        return TryGetHandshakeDeviceTokenScopesCore(payload, preferredRole, allowDirectDeviceTokenFallback: true);
    }

    private static string[]? TryGetHandshakeDeviceTokenScopesCore(JsonElement payload, string? preferredRole, bool allowDirectDeviceTokenFallback)
    {
        if (!payload.TryGetProperty("auth", out var authPayload) || authPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredRole) &&
            authPayload.TryGetProperty("deviceTokens", out var deviceTokens) &&
            deviceTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in deviceTokens.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                if (entry.TryGetProperty("role", out var role) &&
                    role.ValueKind == JsonValueKind.String &&
                    string.Equals(role.GetString(), preferredRole, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.TryGetProperty("scopes", out var roleScopes) && roleScopes.ValueKind == JsonValueKind.Array
                        ? ReadStringArray(roleScopes)
                        : [];
                }
            }

            if (!allowDirectDeviceTokenFallback)
            {
                return null;
            }
        }

        if (authPayload.TryGetProperty("deviceToken", out var deviceToken) &&
            deviceToken.ValueKind == JsonValueKind.String &&
            authPayload.TryGetProperty("scopes", out var scopes) &&
            scopes.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(scopes);
        }

        return null;
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
                {
                    PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(hp));
                    ParseChannelHealth(ch);
                }
                break;
            case "chat":
                HandleChatEvent(root);
                break;
            case "session":
                HandleSessionEvent(root);
                break;
            case "node.pair.requested":
            case "node.pair.resolved":
                // Refresh node pair list when pairing state changes
                _ = RequestNodePairListAsync();
                break;
            case "device.pair.requested":
            case "device.pair.resolved":
                // Refresh device pair list when pairing state changes
                _ = RequestDevicePairListAsync();
                break;
            case "presence":
                // Presence snapshot broadcast when clients connect/disconnect
                if (root.TryGetProperty("payload", out var presPayload))
                    TryParsePresenceFromBroadcast(presPayload);
                break;
            case "cron":
                // Gateway pushes cron events when jobs run/change — refresh the list
                _ = RequestCronListAsync();
                _ = RequestCronStatusAsync();
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
        
        _logger.Info($"[HANDSHAKE] Received connect.challenge: nonce={nonce}, ts={ts}");
        _ = SendConnectSafeAsync(nonce);
    }

    private async Task SendConnectSafeAsync(string? nonce)
    {
        try
        {
            await SendConnectMessageAsync(nonce);
        }
        catch (Exception ex)
        {
            _logger.Error($"[HANDSHAKE] FATAL: SendConnectMessageAsync threw: {ex}");
        }
    }

    private void HandleAgentEvent(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;

        // sessionKey is inside payload, not root
        var sessionKey = "unknown";
        if (payload.TryGetProperty("sessionKey", out var sk))
            sessionKey = sk.GetString() ?? "unknown";
        var isMain = sessionKey == "main" || sessionKey.Contains(":main:");

        // Emit raw agent event (cloned for thread safety)
        try
        {
            var evt = new AgentEventInfo
            {
                RunId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "",
                Seq = payload.TryGetProperty("seq", out var seqProp) && seqProp.ValueKind == JsonValueKind.Number ? seqProp.GetInt32() : 0,
                Stream = payload.TryGetProperty("stream", out var streamProp2) ? streamProp2.GetString() ?? "" : "",
                Ts = payload.TryGetProperty("ts", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number ? tsProp.GetDouble() : 0,
                Data = payload.TryGetProperty("data", out var dataProp) ? dataProp.Clone() : default,
                SessionKey = sessionKey,
                Summary = payload.TryGetProperty("summary", out var sumProp) ? sumProp.GetString() : null
            };
            AgentEventReceived?.Invoke(this, evt);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to emit agent event: {ex.Message}");
        }

        // Parse activity from stream field (existing behavior)
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
                    label = MenuDisplayHelper.TruncateText(cmd.GetString()?.Split('\n')[0], 60);
                else if (args.TryGetProperty("path", out var path))
                    label = ShortenPath(path.GetString() ?? "");
                else if (args.TryGetProperty("file_path", out var filePath))
                    label = ShortenPath(filePath.GetString() ?? "");
                else if (args.TryGetProperty("query", out var query))
                    label = MenuDisplayHelper.TruncateText(query.GetString(), 60);
                else if (args.TryGetProperty("url", out var url))
                    label = MenuDisplayHelper.TruncateText(url.GetString(), 60);
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
        _logger.Debug($"Chat event received: {rawText[..Math.Min(200, rawText.Length)]}");
        
        if (!root.TryGetProperty("payload", out var payload)) return;
        EmitRawChatEvent(payload);

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
                                _logger.Info($"Assistant response: {text[..Math.Min(100, text.Length)]}...");
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
                _logger.Info($"Assistant response (legacy): {text[..Math.Min(100, text.Length)]}");
                EmitChatNotification(text);
            }
        }
    }

    private void EmitRawChatEvent(JsonElement payload)
    {
        try
        {
            var stream = "chat";
            if (payload.TryGetProperty("message", out var message) &&
                message.TryGetProperty("role", out var roleProp))
            {
                stream = roleProp.GetString() ?? stream;
            }
            else if (payload.TryGetProperty("role", out var legacyRoleProp))
            {
                stream = legacyRoleProp.GetString() ?? stream;
            }

            var evt = new AgentEventInfo
            {
                RunId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "",
                Seq = payload.TryGetProperty("seq", out var seqProp) && seqProp.ValueKind == JsonValueKind.Number ? seqProp.GetInt32() : 0,
                Stream = stream,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = payload.Clone(),
                SessionKey = payload.TryGetProperty("sessionKey", out var sk) ? sk.GetString() : null
            };
            ChatEventReceived?.Invoke(this, evt);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to emit chat event: {ex.Message}");
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
        // Allocate the result array directly and copy in, then sort in-place.
        // Avoids the intermediate List<T> that new List(collection).ToArray() would produce.
        var arr = new SessionInfo[_sessions.Count];
        _sessions.Values.CopyTo(arr, 0);
        Array.Sort(arr, static (a, b) =>
        {
            // Main session first, then by last seen
            if (a.IsMain != b.IsMain) return a.IsMain ? -1 : 1;
            return b.LastSeen.CompareTo(a.LastSeen);
        });
        return arr;
    }

    // --- Parsing helpers ---

    private void ParseChannelHealth(JsonElement channels)
    {
        // Debug: log raw channel data
        _logger.Debug($"Raw channel health JSON: {channels.GetRawText()}");
        var healthList = ChannelHealthParser.Parse(channels);

        _logger.Info(healthList.Length > 0
            ? $"Channel health: {string.Join(", ", healthList.Select(c => $"{c.Name}={c.Status}"))}"
            : "Channel health: no channels");
        ChannelHealthUpdated?.Invoke(this, healthList);
    }

    private void PublishGatewaySelf(GatewaySelfInfo info)
    {
        if (!info.HasAnyDetails)
            return;

        GatewaySelfUpdated?.Invoke(this, info);
    }

    private void ParseSessions(JsonElement sessions)
    {
        try
        {
            SessionInfo[] snapshot;
            lock (_sessionsLock)
            {
                // Merge instead of clear — collect incoming keys, update/add, then remove absent
                var incomingKeys = new HashSet<string>();

                if (sessions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sessions.EnumerateArray())
                    {
                        var key = ParseSessionItem(item);
                        if (key != null) incomingKeys.Add(key);
                    }
                }
                else if (sessions.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sessions.EnumerateObject())
                    {
                        var sessionKey = prop.Name;

                        if (sessionKey is "recent" or "count" or "path" or "defaults" or "ts")
                            continue;

                        if (!sessionKey.Equals("global", StringComparison.OrdinalIgnoreCase) &&
                            !sessionKey.Contains(':') &&
                            !sessionKey.Contains("agent") &&
                            !sessionKey.Contains("session"))
                            continue;

                        var item = prop.Value;

                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var strVal = item.GetString() ?? "";
                            if (strVal.StartsWith("/") || strVal.Contains("/."))
                                continue;
                        }
                        else if (item.ValueKind == JsonValueKind.Number)
                        {
                            continue;
                        }

                        // Update or create session
                        if (!_sessions.TryGetValue(sessionKey, out var session))
                        {
                            session = new SessionInfo { Key = sessionKey };
                        }

                        var endsWithMain = sessionKey.EndsWith(":main");
                        session.IsMain = sessionKey == "main" || endsWithMain || sessionKey.Contains(":main:main");

                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("isMain", out var isMain) && isMain.GetBoolean())
                                session.IsMain = true;
                            PopulateSessionFromObject(session, item);
                        }
                        else if (item.ValueKind == JsonValueKind.String)
                        {
                            session.Status = item.GetString() ?? "";
                        }

                        _sessions[sessionKey] = session;
                        incomingKeys.Add(sessionKey);
                    }
                }

                // Remove sessions no longer present in the gateway response
                {
                    var staleKeys = new List<string>();
                    foreach (var key in _sessions.Keys)
                    {
                        if (!incomingKeys.Contains(key))
                            staleKeys.Add(key);
                    }
                    foreach (var key in staleKeys)
                        _sessions.Remove(key);
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
    
    private string? ParseSessionItem(JsonElement item)
    {
        var sessionKey = "unknown";
        if (item.TryGetProperty("key", out var key))
            sessionKey = key.GetString() ?? "unknown";

        // Update or create
        if (!_sessions.TryGetValue(sessionKey, out var session))
        {
            session = new SessionInfo { Key = sessionKey };
        }

        session.IsMain = sessionKey == "main" || 
                         sessionKey.EndsWith(":main") ||
                         sessionKey.Contains(":main:main");
        
        if (item.TryGetProperty("isMain", out var isMain) && isMain.GetBoolean())
            session.IsMain = true;
            
        PopulateSessionFromObject(session, item);

        _sessions[session.Key] = session;
        return session.Key;
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
            if (started.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(started.GetString(), out var dt))
                    session.StartedAt = dt;
            }
            else if (started.ValueKind == JsonValueKind.Number)
            {
                var ms = started.GetInt64();
                session.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            }
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

            var buffer = new GatewayNodeInfo[nodes.GetArrayLength()];
            var count = 0;
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
                var capabilities = GetStringArray(nodeElement, "caps");
                if (capabilities.Length == 0)
                    capabilities = GetStringArray(nodeElement, "capabilities");
                var commands = GetStringArray(nodeElement, "declaredCommands");
                if (commands.Length == 0)
                    commands = GetStringArray(nodeElement, "commands");
                var permissions = GetBoolDictionary(nodeElement, "permissions");

                buffer[count++] = new GatewayNodeInfo
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
                    Capabilities = capabilities.ToList(),
                    Commands = commands.ToList(),
                    Permissions = permissions,
                    CapabilityCount = capabilities.Length,
                    CommandCount = commands.Length,
                    IsOnline = online ?? connected ?? status is "ok" or "online" or "connected" or "ready" or "active"
                };
            }

            var ordered = buffer[..count];
            Array.Sort(ordered, static (a, b) =>
            {
                int c = b.IsOnline.CompareTo(a.IsOnline);
                if (c != 0) return c;
                c = (b.LastSeen ?? DateTime.MinValue).CompareTo(a.LastSeen ?? DateTime.MinValue);
                if (c != 0) return c;
                return StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            });

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

        // At most 2 providers are shown; track them with two nullable strings to avoid
        // allocating a List<string> on every usage-status update.
        string? p0 = null, p1 = null;
        int included = 0;

        foreach (var provider in status.Providers)
        {
            if (included == 2) break;
            var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Provider : provider.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "provider";

            string part;
            if (!string.IsNullOrWhiteSpace(provider.Error))
            {
                part = $"{displayName}: error";
            }
            else
            {
                if (provider.Windows.Count == 0) continue;
                var window = provider.Windows.MaxBy(w => w.UsedPercent);
                if (window is null) continue;
                var remaining = Math.Clamp((int)Math.Round(100 - window.UsedPercent), 0, 100);
                part = $"{displayName}: {remaining}% left";
            }

            if (included == 0) p0 = part;
            else p1 = part;
            included++;
        }

        if (included == 0) return "";

        string? overflow = status.Providers.Count > 2 ? $"+{status.Providers.Count - 2}" : null;
        return (p1, overflow) switch
        {
            (null, null) => p0!,
            (null, _)    => $"{p0} · {overflow}",
            (_, null)    => $"{p0} · {p1}",
            _            => $"{p0} · {p1} · {overflow}",
        };
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

    private static string[] GetStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var buffer = new string[value.GetArrayLength()];
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                buffer[count++] = text;
        }

        return count == 0 ? [] : buffer[..count];
    }

    private static Dictionary<string, bool> GetBoolDictionary(JsonElement parent, string property)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var item in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                continue;

            if (item.Value.ValueKind == JsonValueKind.True)
                result[item.Name] = true;
            else if (item.Value.ValueKind == JsonValueKind.False)
                result[item.Name] = false;
        }

        return result;
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

    // ── Parse methods for new features ──

    private void ParseModelsList(JsonElement payload)
    {
        try
        {
            var info = new ModelsListInfo();
            // Gateway returns { models: [...] } or just an array
            var modelsArray = payload.ValueKind == JsonValueKind.Array
                ? payload
                : payload.TryGetProperty("models", out var m) ? m : default;

            if (modelsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsArray.EnumerateArray())
                {
                    var model = new ModelInfo
                    {
                        Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = item.TryGetProperty("name", out var name) ? name.GetString() : null,
                        Provider = item.TryGetProperty("provider", out var prov) ? prov.GetString() : null,
                        ContextWindow = item.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number ? cw.GetInt32() : null,
                        IsConfigured = item.TryGetProperty("configured", out var cfg) && cfg.ValueKind == JsonValueKind.True
                    };
                    if (!string.IsNullOrEmpty(model.Id))
                        info.Models.Add(model);
                }
            }
            ModelsListUpdated?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse models.list: {ex.Message}");
        }
    }

    private void ParseNodePairList(JsonElement payload)
    {
        try
        {
            var info = new PairingListInfo();
            var pending = payload.TryGetProperty("pending", out var p) ? p : default;
            if (pending.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pending.EnumerateArray())
                {
                    info.Pending.Add(new PairingRequest
                    {
                        RequestId = item.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "",
                        NodeId = item.TryGetProperty("nodeId", out var nid) ? nid.GetString() ?? "" : "",
                        DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                        Platform = item.TryGetProperty("platform", out var plat) ? plat.GetString() : null,
                        Version = item.TryGetProperty("version", out var ver) ? ver.GetString() : null,
                        RemoteIp = item.TryGetProperty("remoteIp", out var ip) ? ip.GetString() : null,
                        IsRepair = item.TryGetProperty("isRepair", out var rep) && rep.ValueKind == JsonValueKind.True,
                        Ts = item.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0
                    });
                }
            }
            NodePairListUpdated?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse node.pair.list: {ex.Message}");
        }
    }

    private void ParseDevicePairList(JsonElement payload)
    {
        try
        {
            var info = new DevicePairingListInfo();
            var pending = payload.TryGetProperty("pending", out var p) ? p : default;
            if (pending.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pending.EnumerateArray())
                {
                    string[]? scopes = null;
                    if (item.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
                    {
                        var scopeList = new List<string>();
                        foreach (var s in sc.EnumerateArray())
                            if (s.GetString() is string sv) scopeList.Add(sv);
                        scopes = scopeList.ToArray();
                    }

                    info.Pending.Add(new DevicePairingRequest
                    {
                        RequestId = item.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "",
                        DeviceId = item.TryGetProperty("deviceId", out var did) ? did.GetString() ?? "" : "",
                        PublicKey = item.TryGetProperty("publicKey", out var pk) ? pk.GetString() : null,
                        DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                        Platform = item.TryGetProperty("platform", out var plat) ? plat.GetString() : null,
                        ClientId = item.TryGetProperty("clientId", out var cid) ? cid.GetString() : null,
                        ClientMode = item.TryGetProperty("clientMode", out var cm) ? cm.GetString() : null,
                        Role = item.TryGetProperty("role", out var role) ? role.GetString() : null,
                        Scopes = scopes,
                        RemoteIp = item.TryGetProperty("remoteIp", out var ip) ? ip.GetString() : null,
                        IsRepair = item.TryGetProperty("isRepair", out var rep) && rep.ValueKind == JsonValueKind.True,
                        Ts = item.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0
                    });
                }
            }
            DevicePairListUpdated?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse device.pair.list: {ex.Message}");
        }
    }

    private void TryParsePresence(JsonElement payload)
    {
        try
        {
            if (!payload.TryGetProperty("snapshot", out var snapshot)) return;
            if (!snapshot.TryGetProperty("presence", out var presenceArray)) return;
            if (presenceArray.ValueKind != JsonValueKind.Array) return;

            var entries = ParsePresenceArray(presenceArray);
            _logger.Info($"Parsed {entries.Length} presence entries from handshake");
            PresenceUpdated?.Invoke(this, entries);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse presence from handshake: {ex.Message}");
        }
    }

    private void TryParsePresenceFromBroadcast(JsonElement payload)
    {
        try
        {
            // Broadcast may contain presence array directly or nested
            var presenceArray = payload.ValueKind == JsonValueKind.Array
                ? payload
                : payload.TryGetProperty("presence", out var p) ? p : default;

            if (presenceArray.ValueKind != JsonValueKind.Array) return;

            var entries = ParsePresenceArray(presenceArray);
            PresenceUpdated?.Invoke(this, entries);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse presence broadcast: {ex.Message}");
        }
    }

    private static PresenceEntry[] ParsePresenceArray(JsonElement array)
    {
        var list = new List<PresenceEntry>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(new PresenceEntry
            {
                Host = item.TryGetProperty("host", out var h) ? h.GetString() : null,
                Ip = item.TryGetProperty("ip", out var ip) ? ip.GetString() : null,
                Version = item.TryGetProperty("version", out var v) ? v.GetString() : null,
                Platform = item.TryGetProperty("platform", out var p) ? p.GetString() : null,
                DeviceFamily = item.TryGetProperty("deviceFamily", out var df) ? df.GetString() : null,
                ModelIdentifier = item.TryGetProperty("modelIdentifier", out var mi) ? mi.GetString() : null,
                Mode = item.TryGetProperty("mode", out var m) ? m.GetString() : null,
                LastInputSeconds = item.TryGetProperty("lastInputSeconds", out var lis) && lis.ValueKind == JsonValueKind.Number ? lis.GetInt32() : null,
                Reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null,
                Tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null,
                Text = item.TryGetProperty("text", out var tx) ? tx.GetString() : null,
                Ts = item.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
                DeviceId = item.TryGetProperty("deviceId", out var did) ? did.GetString() : null,
                Roles = item.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                    ? roles.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null,
                Scopes = item.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array
                    ? sc.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null,
                InstanceId = item.TryGetProperty("instanceId", out var iid) ? iid.GetString() : null,
            });
        }
        return list.ToArray();
    }
}
