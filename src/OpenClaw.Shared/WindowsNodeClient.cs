using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Windows Node client - extends gateway connection to act as a node
/// Supports both operator (existing) and node (new) roles
/// </summary>
public class WindowsNodeClient : WebSocketClientBase
{
    private readonly DeviceIdentity _deviceIdentity;
    
    // Node capabilities registry
    private readonly List<INodeCapability> _capabilities = new();
    private FrozenDictionary<string, INodeCapability> _commandMap = FrozenDictionary<string, INodeCapability>.Empty;
    private readonly NodeRegistration _registration;
    
    // Connection state
    private bool _isConnected;
    private string? _nodeId;
    private string? _pendingNonce;  // Store nonce from challenge for signing
    private bool _isPendingApproval;  // True when connected but awaiting pairing approval
    private bool _isPaired;
    // Bridges the gap between an approval event and the next hello-ok when the gateway omits auth.deviceToken.
    private bool _pairingApprovedAwaitingReconnect;
    // Persists across disconnect/error so ShouldAutoReconnect can block reconnect
    // even after OnDisconnected clears _isPendingApproval.
    private volatile bool _pairingBlocked;
    private volatile bool _rateLimited;
    private bool _useV2Signature; // true after v3 signature rejected by gateway
    public bool UseV2Signature { get => _useV2Signature; set => _useV2Signature = value; }
    // Bug 3: source-side idempotency for PairingStatusChanged. HandleHelloOk runs on every
    // WS reconnect and re-fires PairingStatus.Paired even when nothing changed, causing a
    // toast storm in the tray UI. Track the last emitted status and only fire on transitions.
    private PairingStatus? _lastEmittedPairingStatus;
    private readonly string _gatewayToken;
    private readonly string? _bootstrapToken;
    
    // Cached serialization/validation — reused on every message instead of allocating per-call
    private static readonly JsonSerializerOptions s_ignoreNullOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex s_commandValidator = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    // Events
    public event EventHandler<NodeInvokeRequest>? InvokeReceived;
    public event EventHandler<NodeInvokeCompletedEventArgs>? InvokeCompleted;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<JsonElement>? HealthReceived;
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    /// <summary>Raised when a device token is received from the gateway during hello-ok handshake.</summary>
    public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    /// <summary>Raised when the hello-ok handshake completes successfully.</summary>
    public event EventHandler? HandshakeSucceeded;
    
    public new bool IsConnected => _isConnected;
    public string? NodeId => _nodeId;
    public string GatewayUrl => GatewayUrlForDisplay;
    public IReadOnlyList<INodeCapability> Capabilities => _capabilities;
    
    /// <summary>True if connected but waiting for pairing approval on gateway</summary>
    public bool IsPendingApproval => _isPendingApproval;
    
    /// <summary>True if device is paired via a stored token or an explicit gateway approval event.</summary>
    public bool IsPaired => _isPaired || !string.IsNullOrEmpty(_deviceIdentity.NodeDeviceToken);
    
    /// <summary>Device ID for display/approval (first 16 chars of full ID)</summary>
    public string ShortDeviceId => _deviceIdentity.DeviceId.Length > 16 
        ? _deviceIdentity.DeviceId[..16] 
        : _deviceIdentity.DeviceId;
    
    /// <summary>Full device ID for approval command</summary>
    public string FullDeviceId => _deviceIdentity.DeviceId;

    /// <summary>Human-readable display name surfaced to the gateway and other nodes.</summary>
    public string DisplayName => _registration.DisplayName;

    protected override int ReceiveBufferSize => 65536;
    protected override string ClientRole => "node";
    
    public WindowsNodeClient(string gatewayUrl, string token, string dataPath, IOpenClawLogger? logger = null, string? bootstrapToken = null)
        : base(gatewayUrl, ResolveRequiredCredential(token, bootstrapToken, dataPath, logger), logger)
    {
        _gatewayToken = NormalizeOptionalCredential(token);
        _bootstrapToken = NormalizeOptionalCredential(bootstrapToken);

        // Initialize device identity
        _deviceIdentity = new DeviceIdentity(dataPath, _logger);
        _deviceIdentity.Initialize();
        
        // Initialize registration
        _registration = new NodeRegistration
        {
            Id = _deviceIdentity.DeviceId,
            Version = "1.0.0",
            Platform = "windows",
            DisplayName = $"Windows Node ({Environment.MachineName})"
        };
    }

    private static string NormalizeOptionalCredential(string? credential)
    {
        return string.IsNullOrWhiteSpace(credential) ? string.Empty : credential;
    }

    private static string ResolveRequiredCredential(string? token, string? bootstrapToken, string dataPath, IOpenClawLogger? logger)
    {
        var storedNodeToken = TryLoadStoredNodeToken(dataPath, logger);
        if (!string.IsNullOrEmpty(storedNodeToken))
        {
            return storedNodeToken;
        }

        var gatewayToken = NormalizeOptionalCredential(token);
        if (!string.IsNullOrEmpty(gatewayToken))
        {
            return gatewayToken;
        }

        var bootstrap = NormalizeOptionalCredential(bootstrapToken);
        if (!string.IsNullOrEmpty(bootstrap))
        {
            return bootstrap;
        }

        throw new ArgumentException("Token or bootstrap token is required.", nameof(token));
    }

    public static bool HasStoredNodeDeviceToken(string dataPath, IOpenClawLogger? logger = null)
    {
        return !string.IsNullOrWhiteSpace(TryLoadStoredNodeToken(dataPath, logger));
    }

    private static string? TryLoadStoredNodeToken(string dataPath, IOpenClawLogger? logger)
    {
        try
        {
            var identity = new DeviceIdentity(dataPath, logger);
            identity.Initialize();
            return string.IsNullOrWhiteSpace(identity.NodeDeviceToken) ? null : identity.NodeDeviceToken;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Register a capability handler
    /// </summary>
    public void RegisterCapability(INodeCapability capability)
    {
        _capabilities.Add(capability);
        
        // Update registration
        if (!_registration.Capabilities.Contains(capability.Category))
        {
            _registration.Capabilities.Add(capability.Category);
        }
        foreach (var cmd in capability.Commands)
        {
            if (!_registration.Commands.Contains(cmd))
            {
                _registration.Commands.Add(cmd);
            }
        }
        
        // Rebuild the O(1) command dispatch map so node.invoke lookups stay fast
        // regardless of how many capabilities or commands are registered.
        _commandMap = BuildCommandMap();
        
        _logger.Info($"Registered capability: {capability.Category} ({capability.Commands.Count} commands)");
    }
    
    /// <summary>
    /// Builds a FrozenDictionary mapping each command name to the capability that owns it.
    /// First-registered capability wins on collision (matching the former FirstOrDefault semantics).
    /// </summary>
    private FrozenDictionary<string, INodeCapability> BuildCommandMap()
    {
        var map = new Dictionary<string, INodeCapability>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in _capabilities)
            foreach (var cmd in cap.Commands)
                map.TryAdd(cmd, cap);
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Set a permission for the node
    /// </summary>
    public void SetPermission(string permission, bool value)
    {
        _registration.Permissions[permission] = value;
    }
    
    /// <summary>
    /// Disconnect from gateway
    /// </summary>
    public Task DisconnectAsync()
    {
        _isConnected = false;
        Dispose();
        RaiseStatusChanged(ConnectionStatus.Disconnected);
        _logger.Info("Node disconnected");
        return Task.CompletedTask;
    }

    protected override async Task ProcessMessageAsync(string json)
    {
        try
        {
            // Log raw messages at debug level (visible in dbgview, not in log file noise)
            _logger.Debug($"[NODE RX] {TokenSanitizer.Sanitize(json)}");
            
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("type", out var typeProp))
            {
                _logger.Warn("[NODE] Message has no 'type' field");
                return;
            }
            var type = typeProp.GetString();
            _logger.Debug($"[NODE] Processing message type: {type}");
            
            switch (type)
            {
                case "event":
                    await HandleEventAsync(root);
                    break;
                case "res":
                    HandleResponse(root);
                    break;
                case "req":
                    await HandleRequestAsync(root);
                    break;
                default:
                    _logger.Warn($"[NODE] Unknown message type: {type}");
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
    
    private async Task HandleEventAsync(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventProp)) return;
        var eventType = eventProp.GetString();
        
        // Log all events except health/tick/agent for debugging
        if (eventType != "health" && eventType != "tick" && eventType != "agent" && eventType != "chat")
        {
            _logger.Info($"[NODE] Received event: {eventType}");
        }
        
        switch (eventType)
        {
            case "connect.challenge":
                await HandleConnectChallengeAsync(root);
                break;
            case "node.pair.requested":
            case "device.pair.requested":
                HandlePairingRequestedEvent(root, eventType);
                break;
            case "node.pair.resolved":
            case "device.pair.resolved":
                await HandlePairingResolvedEventAsync(root, eventType);
                break;
            case "node.invoke.request":
                await HandleNodeInvokeEventAsync(root);
                break;
            case "health":
                if (root.TryGetProperty("payload", out var payload))
                {
                    PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(payload));
                    HealthReceived?.Invoke(this, payload.Clone());
                }
                break;
        }
    }

    private void HandlePairingRequestedEvent(JsonElement root, string? eventType)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn($"[NODE] {eventType} has no payload");
            return;
        }

        if (!PayloadTargetsCurrentDevice(payload) || _isPendingApproval)
        {
            return;
        }

        _isPendingApproval = true;
        _isPaired = false;
        _pairingBlocked = true;
        _pairingApprovedAwaitingReconnect = false;

        _logger.Info($"[NODE] Pairing requested for this device via {eventType}");
        _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
        EmitPairingStatusOnTransition(new PairingStatusEventArgs(
            PairingStatus.Pending,
            _deviceIdentity.DeviceId,
            $"Run: openclaw devices approve {ShortDeviceId}..."));
    }

    private async Task HandlePairingResolvedEventAsync(JsonElement root, string? eventType)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn($"[NODE] {eventType} has no payload");
            return;
        }

        if (!PayloadTargetsCurrentDevice(payload))
        {
            return;
        }

        var decision = payload.TryGetProperty("decision", out var decisionProp)
            ? decisionProp.GetString()
            : null;

        _logger.Info($"[NODE] Pairing resolution received for this device: decision={decision ?? "unknown"}");

        if (string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase))
        {
            _isPendingApproval = false;
            _isPaired = true;
            _pairingBlocked = false; // Allow reconnect after approval
            _pairingApprovedAwaitingReconnect = true;

            EmitPairingStatusOnTransition(new PairingStatusEventArgs(
                PairingStatus.Paired,
                _deviceIdentity.DeviceId,
                "Pairing approved; reconnecting to refresh node state."));

            _logger.Info("[NODE] Closing socket after pairing approval to refresh node connection...");
            await CloseWebSocketAsync();
            return;
        }

        if (string.Equals(decision, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            _isPendingApproval = false;
            _isPaired = false;
            _pairingApprovedAwaitingReconnect = false;

            EmitPairingStatusOnTransition(new PairingStatusEventArgs(
                PairingStatus.Rejected,
                _deviceIdentity.DeviceId,
                null));
        }
    }
    
    private async Task HandleNodeInvokeEventAsync(JsonElement root)
    {
        _logger.Info("[NODE] Received node.invoke.request event");
        
        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn("[NODE] node.invoke.request has no payload");
            return;
        }
        
        // Extract request ID
        string? requestId = null;
        if (payload.TryGetProperty("requestId", out var reqIdProp))
        {
            requestId = reqIdProp.GetString();
        }
        else if (payload.TryGetProperty("id", out var idProp))
        {
            requestId = idProp.GetString();
        }
        
        if (string.IsNullOrEmpty(requestId))
        {
            _logger.Warn("[NODE] node.invoke.request has no requestId");
            return;
        }
        
        // Extract command
        if (!payload.TryGetProperty("command", out var cmdProp))
        {
            _logger.Warn("[NODE] node.invoke.request has no command");
            await SendNodeInvokeResultAsync(requestId, false, null, "Missing command");
            return;
        }
        
        var command = cmdProp.GetString() ?? "";
        
        // Validate command format
        if (string.IsNullOrEmpty(command) || command.Length > 100 || 
            !s_commandValidator.IsMatch(command))
        {
            _logger.Warn($"[NODE] Invalid command format: {command}");
            await SendNodeInvokeResultAsync(requestId, false, null, "Invalid command format");
            return;
        }
        
        // Args can be in "args" or "paramsJSON" (JSON string)
        JsonElement args = default;
        if (payload.TryGetProperty("args", out var argsEl))
        {
            args = argsEl;
        }
        else if (payload.TryGetProperty("paramsJSON", out var paramsJsonProp))
        {
            // paramsJSON is a JSON string that needs to be parsed
            var paramsJsonStr = paramsJsonProp.GetString();
            if (!string.IsNullOrEmpty(paramsJsonStr))
            {
                try
                {
                    using var doc = JsonDocument.Parse(paramsJsonStr);
                    args = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.Warn($"[NODE] Failed to parse paramsJSON: {ex.Message}");
                }
            }
        }
        
        _logger.Info($"[NODE] Invoking command: {command}");
        
        // Create request and dispatch to capability handlers
        var request = new NodeInvokeRequest
        {
            Id = requestId,
            Command = command,
            Args = args
        };
        
        // Find capability that can handle this command
        var capability = _commandMap.GetValueOrDefault(command);
        
        if (capability == null)
        {
            _logger.Warn($"[NODE] No capability registered for command: {command}");
            await SendNodeInvokeResultAsync(requestId, false, null, $"Command not supported: {command}");
            RaiseInvokeCompleted(requestId, command, false, $"Command not supported: {command}", TimeSpan.Zero);
            return;
        }
        
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Raise event for UI notification
            InvokeReceived?.Invoke(this, request);
            
            // Execute the command
            var response = await capability.ExecuteAsync(request);
            response.Id = requestId;
             
            await SendNodeInvokeResultAsync(requestId, response.Ok, response.Payload, response.Error);
            stopwatch.Stop();
            RaiseInvokeCompleted(requestId, command, response.Ok, response.Error, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.Error($"[NODE] Command execution failed: {command}", ex);
            await SendNodeInvokeResultAsync(requestId, false, null, $"Execution failed: {ex.Message}");
            stopwatch.Stop();
            RaiseInvokeCompleted(requestId, command, false, $"Execution failed: {ex.Message}", stopwatch.Elapsed);
        }
    }
    
    private async Task SendNodeInvokeResultAsync(string requestId, bool success, object? payload, string? error)
    {
        // Gateway expects: id (not requestId), nodeId, ok, payload (not result)
        var response = new
        {
            type = "req",
            id = Guid.NewGuid().ToString(),
            method = "node.invoke.result",
            @params = new
            {
                id = requestId,  // The original request ID from node.invoke.request
                nodeId = _deviceIdentity.DeviceId,  // Our device ID
                ok = success,
                payload = payload,
                error = error == null ? null : new { message = error }
            }
        };
        
        var json = JsonSerializer.Serialize(response, s_ignoreNullOptions);
        _logger.Info($"[NODE] Sending invoke result for {requestId}: ok={success}");
        await SendRawAsync(json);
    }
    
    private async Task HandleConnectChallengeAsync(JsonElement root)
    {
        string? nonce = null;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        if (root.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("nonce", out var nonceProp))
            {
                nonce = nonceProp.GetString();
            }
            if (payload.TryGetProperty("ts", out var tsProp))
            {
                ts = tsProp.GetInt64();
            }
        }

        _logger.Info($"[HANDSHAKE] Received connect.challenge: nonce={nonce}, ts={ts}");
        
        _pendingNonce = nonce;
        await SendNodeConnectAsync(nonce, ts);
    }
    
    private const string ClientId = "node-host";  // Must be "node-host" for nodes
    
    private async Task SendNodeConnectAsync(string? nonce, long ts)
    {
        var isPaired = !string.IsNullOrEmpty(_deviceIdentity.NodeDeviceToken);
        var usingBootstrap = !isPaired && !string.IsNullOrEmpty(_bootstrapToken);
        var (auth, tokenForSig) = BuildConnectAuth();
        var authType = auth.ContainsKey("deviceToken") ? "deviceToken"
            : auth.ContainsKey("bootstrapToken") ? "bootstrapToken" : "token";

        _logger.Info($"[HANDSHAKE] → Sending connect:");
        _logger.Info($"[HANDSHAKE]   role=node, clientId={ClientId}, mode=node");
        _logger.Info($"[HANDSHAKE]   isBootstrap={usingBootstrap}, hasNodeDeviceToken={isPaired}");
        _logger.Info($"[HANDSHAKE]   deviceId={_deviceIdentity.DeviceId[..Math.Min(16, _deviceIdentity.DeviceId.Length)]}...");
        _logger.Info($"[HANDSHAKE]   nonce={nonce?[..Math.Min(15, nonce?.Length ?? 0)]}...");
        _logger.Info($"[HANDSHAKE]   signature format={(_useV2Signature ? "v2" : "v3")}, platform=windows, family=desktop");
        _logger.Info($"[HANDSHAKE]   auth: {{{authType}}}");

        await SendRawAsync(BuildNodeConnectMessage(nonce, ts));
    }

    private string BuildNodeConnectMessage(string? nonce, long ts)
    {
        // Sign the full payload with Ed25519 - this is how device pairing works
        string? signature = null;
        var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (auth, tokenForSignature) = BuildConnectAuth();
        
        if (!string.IsNullOrEmpty(nonce))
        {
            try
            {
                signature = _useV2Signature
                    ? _deviceIdentity.SignConnectPayloadV2(
                        nonce, signedAt, ClientId, "node", "node",
                        Array.Empty<string>(), tokenForSignature)
                    : _deviceIdentity.SignConnectPayloadV3(
                        nonce, signedAt, ClientId, "node", "node",
                        Array.Empty<string>(), tokenForSignature,
                        "windows", "desktop");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to sign payload: {ex.Message}");
            }
        }

        // Always include device identity - this is required for pairing
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
                    id = ClientId,  // Must match what we sign in payload
                    version = _registration.Version,
                    platform = _registration.Platform,
                    mode = "node",
                    displayName = _registration.DisplayName
                },
                role = "node",
                scopes = Array.Empty<string>(),
                caps = _registration.Capabilities,
                commands = _registration.Commands,
                permissions = _registration.Permissions,
                auth,
                locale = "en-US",
                userAgent = $"openclaw-windows-node/{_registration.Version}",
                device = new
                {
                    id = _deviceIdentity.DeviceId,
                    publicKey = _deviceIdentity.PublicKeyBase64Url,  // Base64url encoded
                    signature = signature,
                    signedAt = signedAt,
                    nonce = nonce
                }
            }
        };

        return JsonSerializer.Serialize(msg, s_ignoreNullOptions);
    }

    private (Dictionary<string, string> Auth, string TokenForSignature) BuildConnectAuth()
    {
        if (!string.IsNullOrEmpty(_deviceIdentity.NodeDeviceToken))
        {
            return (new Dictionary<string, string> { ["deviceToken"] = _deviceIdentity.NodeDeviceToken }, _deviceIdentity.NodeDeviceToken);
        }

        if (!string.IsNullOrEmpty(_bootstrapToken))
        {
            return (new Dictionary<string, string> { ["bootstrapToken"] = _bootstrapToken }, _bootstrapToken);
        }

        return (new Dictionary<string, string> { ["token"] = _gatewayToken }, _gatewayToken);
    }
    
    private void HandleResponse(JsonElement root)
    {
        if (root.TryGetProperty("ok", out var okProp) &&
            okProp.ValueKind == JsonValueKind.False)
        {
            HandleRequestError(root);
            return;
        }

        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn("[NODE] Response has no payload");
            return;
        }
        
        // Handle hello-ok (successful registration)
        if (payload.TryGetProperty("type", out var t) && t.GetString() == "hello-ok")
        {
            _logger.Info("[HANDSHAKE] Received hello-ok!");
            PublishGatewaySelf(GatewaySelfInfo.FromHelloOk(payload));
            var reconnectingAfterApproval = _pairingApprovedAwaitingReconnect;
            _isConnected = true;
            _rateLimited = false; // Clear transient rate-limit on successful connect
            ResetReconnectAttempts();
            
            // Extract node ID if returned
            if (payload.TryGetProperty("nodeId", out var nodeIdProp))
            {
                _nodeId = nodeIdProp.GetString();
            }
            
            // Check for device token in auth — if present, pairing is confirmed in this response.
            // Use gotNewToken to guard the fallback check below and avoid a double-fire of
            // PairingStatusChanged when the gateway includes auth.deviceToken in hello-ok.
            bool gotNewToken = false;
            if (payload.TryGetProperty("auth", out var authPayload) &&
                authPayload.TryGetProperty("deviceToken", out var deviceTokenProp))
            {
                var deviceToken = deviceTokenProp.GetString();
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    gotNewToken = true;
                    var wasWaiting = _isPendingApproval || reconnectingAfterApproval;
                    _isPendingApproval = false;
                    _isPaired = true;
                    _pairingApprovedAwaitingReconnect = false;
                    _logger.Info("Received device token - we are now paired!");
                    _deviceIdentity.StoreDeviceTokenForRole("node", deviceToken, TryGetAuthScopes(authPayload));
                    DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(deviceToken, TryGetAuthScopes(authPayload), "node"));
                    EmitPairingStatusOnTransition(new PairingStatusEventArgs(
                        PairingStatus.Paired,
                        _deviceIdentity.DeviceId,
                        wasWaiting ? "Pairing approved!" : null));
                }
            }
            
            _logger.Info($"Node registered successfully! ID: {_nodeId ?? _deviceIdentity.DeviceId[..16]}");
            
            // Pairing happens at connect time via device identity, no separate request needed.
            // Skip this block if we already fired PairingStatusChanged above via gotNewToken.
            if (!gotNewToken)
            {
                    if (string.IsNullOrEmpty(_deviceIdentity.NodeDeviceToken))
                {
                    if (reconnectingAfterApproval)
                    {
                        _isPendingApproval = false;
                        _isPaired = true;
                        _pairingApprovedAwaitingReconnect = false;
                        _logger.Info("Gateway accepted the node after pairing approval without returning a device token.");
                    }
                    else
                    {
                        _isPendingApproval = true;
                        _isPaired = false;
                        _pairingBlocked = true;
                        _logger.Info("Not yet paired - check 'openclaw devices list' for pending approval");
                        _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
                        EmitPairingStatusOnTransition(new PairingStatusEventArgs(
                            PairingStatus.Pending, 
                            _deviceIdentity.DeviceId,
                            $"Run: openclaw devices approve {ShortDeviceId}..."));
                    }
                }
                else
                {
                    _isPendingApproval = false;
                    _isPaired = true;
                    _pairingApprovedAwaitingReconnect = false;
                    _logger.Info("Already paired with stored device token");
                    EmitPairingStatusOnTransition(new PairingStatusEventArgs(
                        PairingStatus.Paired, 
                        _deviceIdentity.DeviceId));
                }
            }
            
            RaiseStatusChanged(ConnectionStatus.Connected);
            HandshakeSucceeded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Bug 3: source-side suppression of duplicate PairingStatusChanged events from
    /// HandleHelloOk on WS reconnects. Only fire when the status differs from the last
    /// emitted status (or when nothing has been emitted yet).
    /// </summary>
    private void EmitPairingStatusOnTransition(PairingStatusEventArgs args)
    {
        if (_lastEmittedPairingStatus == args.Status)
        {
            _logger.Info($"[NODE] Suppressing duplicate pairing status event: {args.Status} for {args.DeviceId}");
            return;
        }
        _lastEmittedPairingStatus = args.Status;
        PairingStatusChanged?.Invoke(this, args);
    }

    private void HandleRequestError(JsonElement root)
    {
        var error = "Unknown error";
        var errorCode = "none";
        string? pairingReason = null;
        string? pairingRequestId = null;

        if (root.TryGetProperty("error", out var errorProp))
        {
            if (errorProp.TryGetProperty("message", out var msgProp))
            {
                error = msgProp.GetString() ?? error;
            }
            if (errorProp.TryGetProperty("code", out var codeProp))
            {
                errorCode = codeProp.ToString();
            }
            if (errorProp.TryGetProperty("details", out var detailsProp))
            {
                if (TryGetString(detailsProp, "reason", out var reason))
                {
                    pairingReason = reason;
                }
                if (TryGetString(detailsProp, "requestId", out var requestId))
                {
                    pairingRequestId = requestId;
                }
            }
        }

        _logger.Info($"[HANDSHAKE] Connect error: message=\"{error}\", code={errorCode}");

        if (string.Equals(errorCode, "NOT_PAIRED", StringComparison.OrdinalIgnoreCase))
        {
            if (_isPendingApproval)
            {
                return;
            }

            _isPendingApproval = true;
            _isPaired = false;
            _pairingBlocked = true;
            _pairingApprovedAwaitingReconnect = false;

            var detail = !string.IsNullOrWhiteSpace(pairingRequestId)
                ? $"Device {ShortDeviceId} requires approval (request {pairingRequestId})"
                : $"Run: openclaw devices approve {ShortDeviceId}...";
            _logger.Info($"[NODE] Pairing required for this device; reason={pairingReason ?? "unknown"}, requestId={pairingRequestId ?? "none"}");
            _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
            EmitPairingStatusOnTransition(new PairingStatusEventArgs(
                PairingStatus.Pending,
                _deviceIdentity.DeviceId,
                detail,
                requestId: pairingRequestId));
            return;
        }

        // Rate-limit / terminal auth errors — stop reconnecting
        if (error.Contains("too many failed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("origin not allowed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("token mismatch", StringComparison.OrdinalIgnoreCase))
        {
            _rateLimited = true;
            _logger.Warn($"[NODE] Terminal auth error; stopping reconnect. Error: {TokenSanitizer.Sanitize(error)}");
            RaiseStatusChanged(ConnectionStatus.Error);
            return;
        }

        // v3 signature rejected — fall back to v2 for this session
        if (error.Contains("device signature invalid", StringComparison.OrdinalIgnoreCase) ||
            errorCode == "DEVICE_AUTH_SIGNATURE_INVALID")
        {
            if (!_useV2Signature)
            {
                _useV2Signature = true;
                _logger.Warn("[NODE] v3 signature rejected, will use v2 on reconnect");
            }
        }

        _logger.Error($"Node registration failed: {TokenSanitizer.Sanitize(error)} (code: {errorCode})");
        RaiseStatusChanged(ConnectionStatus.Error);
    }

    private bool PayloadTargetsCurrentDevice(JsonElement payload)
    {
        if (TryGetString(payload, "deviceId", out var deviceId) &&
            string.Equals(deviceId, _deviceIdentity.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetString(payload, "nodeId", out var nodeId))
        {
            if (!string.IsNullOrEmpty(_nodeId))
            {
                return string.Equals(nodeId, _nodeId, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(nodeId, _deviceIdentity.DeviceId, StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetString(payload, "instanceId", out var instanceId) &&
            string.Equals(instanceId, _deviceIdentity.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (payload.TryGetProperty("device", out var devicePayload))
        {
            return TryGetString(devicePayload, "id", out var nestedDeviceId) &&
                string.Equals(nestedDeviceId, _deviceIdentity.DeviceId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string[]? TryGetAuthScopes(JsonElement authPayload)
    {
        if (!authPayload.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in scopes.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }

        return values.Count == 0 ? null : values.Distinct(StringComparer.Ordinal).ToArray();
    }
    
    private async Task HandleRequestAsync(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var methodProp)) return;
        var method = methodProp.GetString();
        
        string? id = null;
        if (root.TryGetProperty("id", out var idProp))
        {
            id = idProp.GetString();
        }
        
        switch (method)
        {
            case "node.invoke":
                await HandleNodeInvokeAsync(root, id);
                break;
            case "ping":
                await SendPongAsync(id);
                break;
            default:
                _logger.Warn($"Unknown request method: {method}");
                if (id != null)
                {
                    await SendErrorResponseAsync(id, $"Unknown method: {method}");
                }
                break;
        }
    }
    
    private async Task HandleNodeInvokeAsync(JsonElement root, string? requestId)
    {
        if (requestId == null)
        {
            _logger.Warn("node.invoke without request ID");
            return;
        }
        
        if (!root.TryGetProperty("params", out var paramsEl))
        {
            await SendErrorResponseAsync(requestId, "Missing params");
            return;
        }
        
        if (!paramsEl.TryGetProperty("command", out var cmdProp))
        {
            await SendErrorResponseAsync(requestId, "Missing command");
            return;
        }
        
        var command = cmdProp.GetString() ?? "";
        
        // Validate command format - only allow alphanumeric, dots, underscores, hyphens
        if (string.IsNullOrEmpty(command) || command.Length > 100 || 
            !s_commandValidator.IsMatch(command))
        {
            _logger.Warn($"Invalid command format: {(command.Length > 50 ? command[..50] + "..." : command)}");
            await SendErrorResponseAsync(requestId, "Invalid command format");
            return;
        }
        
        var args = paramsEl.TryGetProperty("args", out var argsEl) 
            ? argsEl 
            : default;
        
        _logger.Info($"Received node.invoke: {command}");
        
        var request = new NodeInvokeRequest
        {
            Id = requestId,
            Command = command,
            Args = args
        };
        
        // Find capability that can handle this command
        var capability = _commandMap.GetValueOrDefault(command);
        
        if (capability == null)
        {
            _logger.Warn($"No capability registered for command: {command}");
            await SendErrorResponseAsync(requestId, $"Command not supported: {command}");
            RaiseInvokeCompleted(requestId, command, false, $"Command not supported: {command}", TimeSpan.Zero);
            return;
        }
        
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Raise event for UI notification
            InvokeReceived?.Invoke(this, request);
            
            // Execute the command
            var response = await capability.ExecuteAsync(request);
            response.Id = requestId;
             
            await SendInvokeResponseAsync(response);
            stopwatch.Stop();
            RaiseInvokeCompleted(requestId, command, response.Ok, response.Error, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.Error($"Command execution failed: {command}", ex);
            await SendErrorResponseAsync(requestId, $"Execution failed: {ex.Message}");
            stopwatch.Stop();
            RaiseInvokeCompleted(requestId, command, false, $"Execution failed: {ex.Message}", stopwatch.Elapsed);
        }
    }

    private void RaiseInvokeCompleted(string requestId, string command, bool ok, string? error, TimeSpan duration)
    {
        InvokeCompleted?.Invoke(this, new NodeInvokeCompletedEventArgs
        {
            RequestId = requestId,
            Command = command,
            Ok = ok,
            Error = error,
            Duration = duration,
            NodeId = _nodeId ?? _deviceIdentity.DeviceId
        });
    }
    
    private async Task SendInvokeResponseAsync(NodeInvokeResponse response)
    {
        var msg = new
        {
            type = "res",
            id = response.Id,
            ok = response.Ok,
            payload = response.Payload,
            error = response.Ok ? null : new { message = response.Error }
        };
        
        await SendRawAsync(JsonSerializer.Serialize(msg, s_ignoreNullOptions));
        
        _logger.Info($"Sent invoke response: ok={response.Ok}");
    }
    
    private async Task SendErrorResponseAsync(string requestId, string error)
    {
        var msg = new
        {
            type = "res",
            id = requestId,
            ok = false,
            error = new { message = error }
        };
        
        await SendRawAsync(JsonSerializer.Serialize(msg));
    }
    
    /// <summary>
    /// Sends a node.event request with JSON payload.
    /// Returns false when not connected or when the transport send fails.
    /// </summary>
    public async Task<bool> SendNodeEventAsync(string eventName, System.Text.Json.Nodes.JsonObject payload)
    {
        if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("eventName is required", nameof(eventName));
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (!_isConnected) return false;

        var msg = new
        {
            type = "req",
            id = Guid.NewGuid().ToString(),
            method = "node.event",
            @params = new
            {
                @event = eventName,
                payloadJSON = payload.ToJsonString(),
            },
        };

        try
        {
            await SendRawAsync(JsonSerializer.Serialize(msg, s_ignoreNullOptions));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"node.event '{eventName}' send failed: {ex.Message}");
            return false;
        }
    }

    private async Task SendPongAsync(string? requestId)
    {
        if (requestId == null) return;
        
        var msg = new
        {
            type = "res",
            id = requestId,
            ok = true,
            payload = new { pong = true }
        };
        
        await SendRawAsync(JsonSerializer.Serialize(msg));
    }

    private void PublishGatewaySelf(GatewaySelfInfo info)
    {
        if (!info.HasAnyDetails)
            return;

        GatewaySelfUpdated?.Invoke(this, info);
    }
    
    protected override bool ShouldAutoReconnect()
    {
        // Don't reconnect while awaiting pairing approval — each reconnect
        // generates a new pairing request on the gateway, causing a storm.
        // _pairingBlocked survives OnDisconnected (which clears _isPendingApproval).
        if (_pairingBlocked)
            return false;

        if (_rateLimited)
            return false;

        return true;
    }

    protected override void OnDisconnected()
    {
        _isConnected = false;
        // Don't reset pairing state when disconnected due to pairing — gateway
        // closes the socket after PAIRING_REQUIRED but we're still waiting for approval
        if (!_pairingBlocked)
        {
            _isPendingApproval = false;
            _isPaired = false;
        }
    }

    protected override void OnError(Exception ex)
    {
        _isConnected = false;
        if (!_pairingBlocked)
        {
            _isPendingApproval = false;
            _isPaired = false;
        }
    }
}
