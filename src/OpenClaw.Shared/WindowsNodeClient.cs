using System;
using System.Collections.Frozen;
using System.Collections.Generic;
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
    
    // Cached serialization/validation — reused on every message instead of allocating per-call
    private static readonly JsonSerializerOptions s_ignoreNullOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex s_commandValidator = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    // Events
    public event EventHandler<NodeInvokeRequest>? InvokeReceived;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    
    public new bool IsConnected => _isConnected;
    public string? NodeId => _nodeId;
    public string GatewayUrl => GatewayUrlForDisplay;
    public IReadOnlyList<INodeCapability> Capabilities => _capabilities;
    
    /// <summary>True if connected but waiting for pairing approval on gateway</summary>
    public bool IsPendingApproval => _isPendingApproval;
    
    /// <summary>True if device is paired via a stored token or an explicit gateway approval event.</summary>
    public bool IsPaired => _isPaired || !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);
    
    /// <summary>Device ID for display/approval (first 16 chars of full ID)</summary>
    public string ShortDeviceId => _deviceIdentity.DeviceId.Length > 16 
        ? _deviceIdentity.DeviceId[..16] 
        : _deviceIdentity.DeviceId;
    
    /// <summary>Full device ID for approval command</summary>
    public string FullDeviceId => _deviceIdentity.DeviceId;
    
    protected override int ReceiveBufferSize => 65536;
    protected override string ClientRole => "node";
    
    public WindowsNodeClient(string gatewayUrl, string token, string dataPath, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger)
    {
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
            _logger.Debug($"[NODE RX] {json}");
            
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
        _pairingApprovedAwaitingReconnect = false;

        _logger.Info($"[NODE] Pairing requested for this device via {eventType}");
        _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
        PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
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
            _pairingApprovedAwaitingReconnect = true;

            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
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

            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
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
            return;
        }
        
        try
        {
            // Raise event for UI notification
            InvokeReceived?.Invoke(this, request);
            
            // Execute the command
            var response = await capability.ExecuteAsync(request);
            response.Id = requestId;
            
            await SendNodeInvokeResultAsync(requestId, response.Ok, response.Payload, response.Error);
        }
        catch (Exception ex)
        {
            _logger.Error($"[NODE] Command execution failed: {command}", ex);
            await SendNodeInvokeResultAsync(requestId, false, null, $"Execution failed: {ex.Message}");
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
        _logger.Info("Received connect challenge, sending node registration...");
        
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
        
        _pendingNonce = nonce;
        await SendNodeConnectAsync(nonce, ts);
    }
    
    private const string ClientId = "node-host";  // Must be "node-host" for nodes
    
    private async Task SendNodeConnectAsync(string? nonce, long ts)
    {
        // Sign the full payload with Ed25519 - this is how device pairing works
        string? signature = null;
        var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Use device token if we have one (already paired), otherwise use operator token
        // IMPORTANT: This token must be included in the signed payload!
        var authToken = _deviceIdentity.DeviceToken ?? _token;
        var isPaired = !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);
        
        if (!string.IsNullOrEmpty(nonce))
        {
            try
            {
                signature = _deviceIdentity.SignPayload(nonce, signedAt, ClientId, authToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to sign payload: {ex.Message}");
            }
        }
        
        _logger.Info($"Connecting with Ed25519 device identity (paired: {isPaired})");
        
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
                auth = new { token = authToken },
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
        
        await SendRawAsync(JsonSerializer.Serialize(msg));
        _logger.Info($"Sent node registration with device ID: {_deviceIdentity.DeviceId[..16]}..., paired: {isPaired}");
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
            var reconnectingAfterApproval = _pairingApprovedAwaitingReconnect;
            _isConnected = true;
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
                    _deviceIdentity.StoreDeviceToken(deviceToken);
                    PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
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
                if (string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
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
                        _logger.Info("Not yet paired - check 'openclaw devices list' for pending approval");
                        _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
                        PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
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
                    PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                        PairingStatus.Paired, 
                        _deviceIdentity.DeviceId));
                }
            }
            
            RaiseStatusChanged(ConnectionStatus.Connected);
        }
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

        if (string.Equals(errorCode, "NOT_PAIRED", StringComparison.OrdinalIgnoreCase))
        {
            if (_isPendingApproval)
            {
                return;
            }

            _isPendingApproval = true;
            _isPaired = false;
            _pairingApprovedAwaitingReconnect = false;

            var detail = !string.IsNullOrWhiteSpace(pairingRequestId)
                ? $"Device {ShortDeviceId} requires approval (request {pairingRequestId})"
                : $"Run: openclaw devices approve {ShortDeviceId}...";
            _logger.Info($"[NODE] Pairing required for this device; reason={pairingReason ?? "unknown"}, requestId={pairingRequestId ?? "none"}");
            _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                PairingStatus.Pending,
                _deviceIdentity.DeviceId,
                detail));
            return;
        }

        _logger.Error($"Node registration failed: {error} (code: {errorCode})");
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
            return;
        }
        
        try
        {
            // Raise event for UI notification
            InvokeReceived?.Invoke(this, request);
            
            // Execute the command
            var response = await capability.ExecuteAsync(request);
            response.Id = requestId;
            
            await SendInvokeResponseAsync(response);
        }
        catch (Exception ex)
        {
            _logger.Error($"Command execution failed: {command}", ex);
            await SendErrorResponseAsync(requestId, $"Execution failed: {ex.Message}");
        }
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
    
    protected override void OnDisconnected()
    {
        _isConnected = false;
        _isPendingApproval = false;
        _isPaired = false;
    }

    protected override void OnError(Exception ex)
    {
        _isConnected = false;
        _isPendingApproval = false;
        _isPaired = false;
    }
}
