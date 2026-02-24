using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Windows Node client - extends gateway connection to act as a node
/// Supports both operator (existing) and node (new) roles
/// </summary>
public class WindowsNodeClient : IDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly string _gatewayUrl;
    private readonly string _gatewayUrlForDisplay;
    private readonly string _token;
    private readonly string? _credentials;
    private readonly IOpenClawLogger _logger;
    private readonly DeviceIdentity _deviceIdentity;
    private CancellationTokenSource _cts;
    private bool _disposed;
    private int _reconnectAttempts;
    private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };
    
    // Node capabilities registry
    private readonly List<INodeCapability> _capabilities = new();
    private readonly NodeRegistration _registration;
    
    // Connection state
    private bool _isConnected;
    private string? _nodeId;
    private string? _pendingNonce;  // Store nonce from challenge for signing
    private bool _isPendingApproval;  // True when connected but awaiting pairing approval
    
    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<NodeInvokeRequest>? InvokeReceived;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    
    public bool IsConnected => _isConnected;
    public string? NodeId => _nodeId;
    public string GatewayUrl => _gatewayUrlForDisplay;
    public IReadOnlyList<INodeCapability> Capabilities => _capabilities;
    
    /// <summary>True if connected but waiting for pairing approval on gateway</summary>
    public bool IsPendingApproval => _isPendingApproval;
    
    /// <summary>True if device is paired (has a device token)</summary>
    public bool IsPaired => !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);
    
    /// <summary>Device ID for display/approval (first 16 chars of full ID)</summary>
    public string ShortDeviceId => _deviceIdentity.DeviceId.Length > 16 
        ? _deviceIdentity.DeviceId.Substring(0, 16) 
        : _deviceIdentity.DeviceId;
    
    /// <summary>Full device ID for approval command</summary>
    public string FullDeviceId => _deviceIdentity.DeviceId;
    
    public WindowsNodeClient(string gatewayUrl, string token, string dataPath, IOpenClawLogger? logger = null)
    {
        _gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        _gatewayUrlForDisplay = GatewayUrlHelper.SanitizeForDisplay(_gatewayUrl);
        _token = token;
        _credentials = GatewayUrlHelper.ExtractCredentials(gatewayUrl);
        _logger = logger ?? NullLogger.Instance;
        _cts = new CancellationTokenSource();
        
        // Initialize device identity
        _deviceIdentity = new DeviceIdentity(dataPath, _logger);
        _deviceIdentity.Initialize();
        
        // Initialize registration
        _registration = new NodeRegistration
        {
            Id = _deviceIdentity.DeviceId,  // Use device ID from keypair
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
        
        _logger.Info($"Registered capability: {capability.Category} ({capability.Commands.Count} commands)");
    }
    
    /// <summary>
    /// Set a permission for the node
    /// </summary>
    public void SetPermission(string permission, bool value)
    {
        _registration.Permissions[permission] = value;
    }
    
    /// <summary>
    /// Connect to gateway as a node
    /// </summary>
    public async Task ConnectAsync()
    {
        try
        {
            StatusChanged?.Invoke(this, ConnectionStatus.Connecting);
            _logger.Info($"Connecting to gateway as node: {_gatewayUrlForDisplay}");
            
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            
            // Set Origin header
            var uri = new Uri(_gatewayUrl);
            var originScheme = uri.Scheme == "wss" ? "https" : "http";
            var origin = $"{originScheme}://{uri.Host}:{uri.Port}";
            _webSocket.Options.SetRequestHeader("Origin", origin);

            if (!string.IsNullOrEmpty(_credentials))
            {
                var authCredentials = GatewayUrlHelper.DecodeCredentials(_credentials);

                _webSocket.Options.SetRequestHeader(
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(authCredentials))}");
            }

            await _webSocket.ConnectAsync(uri, _cts.Token);
            
            _reconnectAttempts = 0;
            _logger.Info("Node connected, waiting for challenge...");
            
            // Start message loop
            _ = Task.Run(() => ListenForMessagesAsync(), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error("Node connection failed", ex);
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
        }
    }
    
    /// <summary>
    /// Disconnect from gateway
    /// </summary>
    public async Task DisconnectAsync()
    {
        _isConnected = false;
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
        StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
        _logger.Info("Node disconnected");
    }
    
    // --- Message handling ---
    
    private async Task ListenForMessagesAsync()
    {
        var buffer = new byte[65536]; // Large buffer for image data
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
                        await ProcessMessageAsync(sb.ToString());
                        sb.Clear();
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.Info("Server closed connection");
                    _isConnected = false;
                    StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.Warn("Connection closed prematurely");
            _isConnected = false;
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { /* CTS was disposed */ }
        catch (Exception ex)
        {
            _logger.Error("Node listen error", ex);
            _isConnected = false;
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
        }
        
        // Auto-reconnect (with extra safety checks)
        if (!_disposed)
        {
            try
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    await ReconnectWithBackoffAsync();
                }
            }
            catch (ObjectDisposedException) { /* CTS was disposed during check */ }
        }
    }
    
    private async Task ProcessMessageAsync(string json)
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
            case "node.invoke.request":
                await HandleNodeInvokeEventAsync(root);
                break;
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
            !System.Text.RegularExpressions.Regex.IsMatch(command, @"^[a-zA-Z0-9._-]+$"))
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
        var capability = _capabilities.FirstOrDefault(c => c.CanHandle(command));
        
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
        
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions 
        { 
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull 
        });
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
                // Sign the payload - INCLUDE the auth token in the payload!
                var debugPayload = _deviceIdentity.BuildDebugPayload(nonce, signedAt, ClientId, authToken);
                signature = _deviceIdentity.SignPayload(nonce, signedAt, ClientId, authToken);
                
                // Full debug output for verification
                _logger.Debug("=== Debug Info ===");
                _logger.Debug($"Device ID: {_deviceIdentity.DeviceId}");
                _logger.Debug($"Public Key: {_deviceIdentity.PublicKeyBase64Url}");
                _logger.Debug($"Client ID: {ClientId}");
                _logger.Debug($"Auth Token (in payload): {authToken?.Substring(0, Math.Min(16, authToken?.Length ?? 0))}...");
                _logger.Debug($"Nonce: {nonce}");
                _logger.Debug($"SignedAt: {signedAt}");
                _logger.Debug($"Payload: {debugPayload.Substring(0, Math.Min(100, debugPayload.Length))}...");
                _logger.Debug($"Signature: {signature}");
                _logger.Debug("==================");
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
        
        var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { WriteIndented = true });
        _logger.Debug($"[NODE TX FULL JSON]:\n{json}");
        await SendRawAsync(JsonSerializer.Serialize(msg));  // Send compact version
        _logger.Info($"Sent node registration with device ID: {_deviceIdentity.DeviceId.Substring(0, 16)}..., paired: {isPaired}");
    }
    
    private void HandleResponse(JsonElement root)
    {
        // DEBUG: Log entire response structure
        _logger.Debug($"[NODE] HandleResponse - ok: {(root.TryGetProperty("ok", out var okVal) ? okVal.ToString() : "missing")}");
        
        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn("[NODE] Response has no payload");
            return;
        }
        
        _logger.Debug($"[NODE] Response payload: {payload.ToString().Substring(0, Math.Min(200, payload.ToString().Length))}...");
        
        // Handle hello-ok (successful registration)
        if (payload.TryGetProperty("type", out var t) && t.GetString() == "hello-ok")
        {
            _isConnected = true;
            
            // Extract node ID if returned
            if (payload.TryGetProperty("nodeId", out var nodeIdProp))
            {
                _nodeId = nodeIdProp.GetString();
            }
            
            // Check for device token in auth (means we're paired!)
            if (payload.TryGetProperty("auth", out var authPayload))
            {
                if (authPayload.TryGetProperty("deviceToken", out var deviceTokenProp))
                {
                    var deviceToken = deviceTokenProp.GetString();
                    if (!string.IsNullOrEmpty(deviceToken))
                    {
                        var wasWaiting = _isPendingApproval;
                        _isPendingApproval = false;
                        _logger.Info("Received device token - we are now paired!");
                        _deviceIdentity.StoreDeviceToken(deviceToken);
                        
                        // Fire pairing event if we were waiting
                        if (wasWaiting)
                        {
                            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                                PairingStatus.Paired, 
                                _deviceIdentity.DeviceId,
                                "Pairing approved!"));
                        }
                    }
                }
            }
            
            _logger.Info($"Node registered successfully! ID: {_nodeId ?? _deviceIdentity.DeviceId.Substring(0, 16)}");
            
            // Pairing happens at connect time via device identity, no separate request needed
            if (string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
            {
                _isPendingApproval = true;
                _logger.Info("Not yet paired - check 'openclaw devices list' for pending approval");
                _logger.Info($"To approve, run: openclaw devices approve {_deviceIdentity.DeviceId}");
                PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                    PairingStatus.Pending, 
                    _deviceIdentity.DeviceId,
                    $"Run: openclaw devices approve {ShortDeviceId}..."));
            }
            else
            {
                _isPendingApproval = false;
                _logger.Info("Already paired with stored device token");
                PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                    PairingStatus.Paired, 
                    _deviceIdentity.DeviceId));
            }
            
            StatusChanged?.Invoke(this, ConnectionStatus.Connected);
        }
        
        // Handle errors
        if (root.TryGetProperty("ok", out var okProp) && !okProp.GetBoolean())
        {
            var error = "Unknown error";
            var errorCode = "none";
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
            }
            _logger.Error($"Node registration failed: {error} (code: {errorCode})");
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
        }
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
            !System.Text.RegularExpressions.Regex.IsMatch(command, @"^[a-zA-Z0-9._-]+$"))
        {
            _logger.Warn($"Invalid command format: {(command.Length > 50 ? command.Substring(0, 50) + "..." : command)}");
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
        var capability = _capabilities.FirstOrDefault(c => c.CanHandle(command));
        
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
        
        await SendRawAsync(JsonSerializer.Serialize(msg, new JsonSerializerOptions 
        { 
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull 
        }));
        
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
    
    private async Task SendRawAsync(string message)
    {
        // Capture local reference to avoid race conditions
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
            // WebSocket was disposed between check and send - ignore
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
        {
            // WebSocket state changed - ignore
            _logger.Warn($"WebSocket send failed (state changed): {ex.Message}");
        }
    }
    
    private async Task ReconnectWithBackoffAsync()
    {
        var delay = BackoffMs[Math.Min(_reconnectAttempts, BackoffMs.Length - 1)];
        _reconnectAttempts++;
        _logger.Warn($"Node reconnecting in {delay}ms (attempt {_reconnectAttempts})");
        StatusChanged?.Invoke(this, ConnectionStatus.Connecting);
        
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
            _logger.Error("Node reconnect failed", ex);
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try { _cts.Cancel(); } catch { /* ignore */ }
        
        var ws = _webSocket;
        _webSocket = null;
        try { ws?.Dispose(); } catch { /* ignore */ }
        
        // Don't dispose _cts immediately — reconnect loop may still reference it.
        // It will be GC'd after all pending tasks complete.
    }
}
