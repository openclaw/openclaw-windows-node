using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// GatewayConnectionManager — single owner of connection lifecycle.
/// Phase 2.1: Shell with state machine, diagnostics, and stub lifecycle methods.
/// Real client creation is added in Step 2.2a.
/// </summary>
public sealed class GatewayConnectionManager : IGatewayConnectionManager
{
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly ICredentialResolver _credentialResolver;
    private readonly IGatewayClientFactory _clientFactory;
    private readonly GatewayRegistry _registry;
    private readonly IOpenClawLogger _logger;
    private readonly IDeviceIdentityStore? _identityStore;
    private readonly INodeConnector? _nodeConnector;
    private readonly ISshTunnelManager? _tunnelManager;
    private readonly Func<bool>? _isNodeEnabled;
    private readonly IClock _clock;
    private readonly Func<GatewayRecord, string, bool>? _shouldStartNodeConnection;
    private readonly SemaphoreSlim _transitionSemaphore = new(1, 1);

    private long _generation;
    private CancellationTokenSource? _operationCts;
    private IGatewayClientLifecycle? _activeLifecycle;
    private string? _activeIdentityPath; // identity directory for the active connection
    private string? _activeGatewayRecordId; // gateway record ID for node credential resolution
    private bool _disposed;
    private bool _gatewayNeedsV2Signature; // remembered across reconnects
    private string? _lastAutoApprovedRequestId; // prevent auto-approve loops

    public event EventHandler<GatewayConnectionSnapshot>? StateChanged;
    public event EventHandler<ConnectionDiagnosticEvent>? DiagnosticEvent;
    public event EventHandler<OperatorClientChangedEventArgs>? OperatorClientChanged;

    public GatewayConnectionManager(
        ICredentialResolver credentialResolver,
        IGatewayClientFactory clientFactory,
        GatewayRegistry registry,
        IOpenClawLogger logger,
        IClock? clock = null,
        IDeviceIdentityStore? identityStore = null,
        INodeConnector? nodeConnector = null,
        Func<bool>? isNodeEnabled = null,
        ConnectionDiagnostics? diagnostics = null,
        ISshTunnelManager? tunnelManager = null,
        Func<GatewayRecord, string, bool>? shouldStartNodeConnection = null)
    {
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _identityStore = identityStore;
        _nodeConnector = nodeConnector;
        _tunnelManager = tunnelManager;
        _isNodeEnabled = isNodeEnabled;
        _clock = clock ?? SystemClock.Instance;
        _shouldStartNodeConnection = shouldStartNodeConnection;
        _diagnostics = diagnostics ?? new ConnectionDiagnostics(clock: clock);
        _diagnostics.EventRecorded += (_, e) => DiagnosticEvent?.Invoke(this, e);

        if (_nodeConnector != null)
        {
            _nodeConnector.StatusChanged += OnNodeStatusChanged;
            _nodeConnector.PairingStatusChanged += OnNodePairingStatusChanged;
        }
    }

    // ─── State ───

    public GatewayConnectionSnapshot CurrentSnapshot => _stateMachine.Current;
    public string? ActiveGatewayUrl => _stateMachine.Current.GatewayUrl;
    public IOperatorGatewayClient? OperatorClient => _activeLifecycle?.DataClient;
    /// <summary>Internal access to the concrete client for auto-approve and other manager-internal operations.</summary>
    internal OpenClawGatewayClient? ConcreteOperatorClient => _activeLifecycle?.DataClient;
    public ConnectionDiagnostics Diagnostics => _diagnostics;

    // ─── Lifecycle ───

    public async Task ConnectAsync(string? gatewayId = null)
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            await ConnectCoreAsync(gatewayId);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    /// <summary>Core connect logic. Caller must hold <see cref="_transitionSemaphore"/>.</summary>
    private async Task ConnectCoreAsync(string? gatewayId = null)
    {
            var id = gatewayId ?? _registry.ActiveGatewayId;
            if (id == null)
            {
                _logger.Warn("[ConnMgr] No gateway ID specified and no active gateway");
                return;
            }

            var record = _registry.GetById(id);
            if (record == null)
            {
                _logger.Warn($"[ConnMgr] Gateway {id} not found in registry");
                return;
            }

            // Cancel any in-flight operation
            var gen = Interlocked.Increment(ref _generation);
            var oldCts = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
            oldCts?.Cancel();
            oldCts?.Dispose();

            // Dispose old client
            DisposeActiveClient();

            // Update snapshot with gateway info
            _stateMachine.Current = _stateMachine.Current with
            {
                GatewayId = record.Id,
                GatewayUrl = record.Url,
                GatewayName = record.FriendlyName
            };

            // Per-gateway identity directory — each gateway has its own keypair + tokens
            var perGatewayIdentityDir = _registry.GetIdentityDirectory(record.Id);
            if (!Directory.Exists(perGatewayIdentityDir))
                Directory.CreateDirectory(perGatewayIdentityDir);

            var credential = _credentialResolver.ResolveOperator(record, perGatewayIdentityDir);
            _diagnostics.RecordCredentialResolution(credential);
            _activeIdentityPath = perGatewayIdentityDir;
            _activeGatewayRecordId = record.Id;

            if (credential == null)
            {
                _logger.Warn("[ConnMgr] No credential available for gateway");
                var prev = _stateMachine.Current.OverallState;
                // Must go through Connecting → Error since AuthenticationFailed requires Connecting state
                _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
                _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, "No credential available");
                EmitStateChanged(prev);
                return;
            }

            // Transition to Connecting
            var prevState = _stateMachine.Current.OverallState;
            if (!_stateMachine.TryTransition(ConnectionTrigger.ConnectRequested))
            {
                _logger.Warn($"[ConnMgr] Cannot connect from state {_stateMachine.Current.OperatorState}");
                return;
            }
            _diagnostics.RecordStateChange(prevState, _stateMachine.Current.OverallState);
            EmitStateChanged(prevState);

            // Create client via factory — use a diagnostic-tee logger so client handshake
            // logs appear in the Connection Status window timeline.
            // When SSH tunnel is configured, start the tunnel and connect to the local URL.
            var connectUrl = record.Url;
            if (record.SshTunnel != null && _tunnelManager != null)
            {
                var tunnel = record.SshTunnel;
                if (string.IsNullOrWhiteSpace(tunnel.User) || string.IsNullOrWhiteSpace(tunnel.Host) ||
                    tunnel.RemotePort is < 1 or > 65535 || tunnel.LocalPort is < 1 or > 65535)
                {
                    _logger.Warn("[ConnMgr] SSH tunnel config is incomplete");
                    _diagnostics.Record("tunnel", "SSH tunnel config is incomplete");
                    var p = _stateMachine.Current.OverallState;
                    _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, "SSH tunnel config is incomplete");
                    EmitStateChanged(p);
                    return;
                }
                try
                {
                    connectUrl = await _tunnelManager.StartAsync(tunnel, _operationCts!.Token);
                    _diagnostics.Record("tunnel", $"SSH tunnel started → {connectUrl}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] SSH tunnel start failed: {ex.Message}");
                    _diagnostics.Record("tunnel", "SSH tunnel start failed", ex.Message);
                    var p = _stateMachine.Current.OverallState;
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketError, $"SSH tunnel failed: {ex.Message}");
                    EmitStateChanged(p);
                    return;
                }
            }
            else if (record.SshTunnel != null)
            {
                // Tunnel config present but no tunnel manager — use local URL directly
                connectUrl = $"ws://localhost:{record.SshTunnel.LocalPort}";
            }
            var diagLogger = new DiagnosticTeeLogger(_logger, _diagnostics);
            var lifecycle = _clientFactory.Create(connectUrl, credential, perGatewayIdentityDir, diagLogger);
            _activeLifecycle = lifecycle;
            OperatorClientChanged?.Invoke(this, new OperatorClientChangedEventArgs
            {
                OldClient = null,
                NewClient = lifecycle.DataClient
            });

            // Subscribe to client events with generation guard
            lifecycle.StatusChanged += (s, status) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleOperatorStatusChangedAsync(status, gen);
            };
            lifecycle.AuthenticationFailed += (s, msg) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleAuthenticationFailedAsync(msg, gen);
            };
            lifecycle.DataClient.HandshakeSucceeded += (s, e) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleHandshakeSucceededAsync(gen);
            };
            lifecycle.DataClient.DeviceTokenReceived += (s, e) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                HandleDeviceTokenReceived(e);
            };
            lifecycle.DataClient.PairingRequired += (s, requestId) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandlePairingRequiredAsync(requestId, gen);
            };
            lifecycle.DataClient.V2SignatureFallback += (s, _) =>
            {
                _gatewayNeedsV2Signature = true;
            };

            // If we already know this gateway needs v2, tell the client upfront
            if (_gatewayNeedsV2Signature)
                lifecycle.DataClient.UseV2Signature = true;

            // Connect (fire and forget — the event handlers will drive state transitions)
            var ct = _operationCts!.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await lifecycle.ConnectAsync(ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] Connect failed: {ex.Message}");
                }
            }, ct);
    }

    public async Task DisconnectAsync()
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            DisconnectCore();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    /// <summary>Core disconnect logic. Caller must hold <see cref="_transitionSemaphore"/>.</summary>
    private void DisconnectCore()
    {
        var prev = _stateMachine.Current.OverallState;
        DisposeActiveClient();
        _stateMachine.TryTransition(ConnectionTrigger.DisconnectRequested);
        _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);
        EmitStateChanged(prev);
    }

    public async Task ReconnectAsync()
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            DisconnectCore();
            await ConnectCoreAsync();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task SwitchGatewayAsync(string gatewayId)
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            DisconnectCore();
            // Stop tunnel when switching gateways — the new one may not need it.
            // Use a bounded timeout to avoid blocking all connection transitions.
            if (_tunnelManager?.IsActive == true)
            {
                try
                {
                    var tunnelStop = _tunnelManager.StopAsync();
                    if (await Task.WhenAny(tunnelStop, Task.Delay(TimeSpan.FromSeconds(5))) != tunnelStop)
                        _logger.Warn("[ConnMgr] Tunnel stop timed out during gateway switch");
                }
                catch (Exception ex) { _logger.Warn($"[ConnMgr] Tunnel stop error on gateway switch: {ex.Message}"); }
            }
            _gatewayNeedsV2Signature = false; // new gateway might support v3
            _registry.SetActive(gatewayId);
            await ConnectCoreAsync(gatewayId);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode)
    {
        ThrowIfDisposed();

        // 1. Decode setup code
        var decoded = OpenClawTray.Onboarding.Services.SetupCodeDecoder.Decode(setupCode);
        if (!decoded.Success || string.IsNullOrWhiteSpace(decoded.Url))
            return new SetupCodeResult(SetupCodeOutcome.InvalidCode, decoded.Error ?? "Could not decode setup code");

        var gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(decoded.Url);

        // 2. Validate URL
        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
            return new SetupCodeResult(SetupCodeOutcome.InvalidUrl, "Invalid gateway URL");

        // 3. Disconnect current gateway if any
        await DisconnectAsync();

        // New gateway URL → reset v2 signature flag (new gateway might support v3)
        var isNewGateway = _registry.FindByUrl(gatewayUrl) == null;
        if (isNewGateway)
            _gatewayNeedsV2Signature = false;

        // 4. Create or update gateway record
        var existing = _registry.FindByUrl(gatewayUrl);
        var recordId = existing?.Id ?? Guid.NewGuid().ToString();

        // Setup codes from `openclaw qr` always provide bootstrap tokens.
        // Store as BootstrapToken so the credential resolver passes IsBootstrapToken=true,
        // causing the client to send auth.bootstrapToken (not auth.token).
        var record = (existing ?? new GatewayRecord { Id = recordId }) with
        {
            Url = gatewayUrl,
            SharedGatewayToken = existing?.SharedGatewayToken, // preserve existing shared token if any
            BootstrapToken = decoded.Token ?? existing?.BootstrapToken,
        };
        _registry.AddOrUpdate(record);
        _registry.SetActive(recordId);
        _registry.Save();

        // Ensure identity directory
        var identityDir = _registry.GetIdentityDirectory(recordId);
        if (!Directory.Exists(identityDir))
            Directory.CreateDirectory(identityDir);

        // Clear stored device tokens so we start fresh with the bootstrap token.
        // The keypair (device ID) stays — only the tokens are wiped.
        ClearStoredDeviceTokens(identityDir);

        _diagnostics.Record("setup", $"Setup code applied for {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");

        // 5. Connect to new gateway
        await ConnectAsync(recordId);

        return new SetupCodeResult(SetupCodeOutcome.Success, GatewayUrl: gatewayUrl);
    }

    // ─── Event Handlers ───

    private async Task HandleOperatorStatusChangedAsync(ConnectionStatus status, long gen)
    {
        // Check client's pairing status directly — set synchronously before this handler runs
        var isPairingPending = _activeLifecycle?.DataClient?.IsPairingRequired == true;
        if (isPairingPending && status is ConnectionStatus.Disconnected or ConnectionStatus.Error)
            return;

        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            switch (status)
            {
                case ConnectionStatus.Connected:
                    _diagnostics.RecordWebSocketEvent("WebSocket connected");
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketConnected);
                    break;
                case ConnectionStatus.Disconnected:
                    _diagnostics.RecordWebSocketEvent("WebSocket disconnected");
                    // Don't overwrite PairingRequired — gateway closes socket after pairing required
                    if (_stateMachine.Current.OperatorState != RoleConnectionState.PairingRequired)
                        _stateMachine.TryTransition(ConnectionTrigger.WebSocketDisconnected);
                    break;
                case ConnectionStatus.Error:
                    _diagnostics.RecordWebSocketEvent("WebSocket error");
                    if (_stateMachine.Current.OperatorState != RoleConnectionState.PairingRequired)
                        _stateMachine.TryTransition(ConnectionTrigger.WebSocketError, "Transport error");
                    break;
                case ConnectionStatus.Connecting:
                    _diagnostics.RecordWebSocketEvent("WebSocket connecting");
                    break;
            }
            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async Task HandleAuthenticationFailedAsync(string message, long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("error", "Authentication failed", message);
            _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, message);
            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async Task HandleHandshakeSucceededAsync(long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("state", "Handshake succeeded (hello-ok)");
            _stateMachine.TryTransition(ConnectionTrigger.HandshakeSucceeded);
            _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);

            // Update device ID from client
            if (_activeLifecycle?.DataClient is { } client)
            {
                _stateMachine.Current = _stateMachine.Current with
                {
                    OperatorDeviceId = client.OperatorDeviceId
                };
            }

            EmitStateChanged(prev);

            // Stamp LastConnected so auto-reconnect on next startup can use this gateway.
            // Uses the atomic Update helper to avoid overwriting concurrent registry changes.
            if (_activeGatewayRecordId != null)
            {
                try
                {
                    _registry.Update(_activeGatewayRecordId, r => r with { LastConnected = _clock.UtcNow });
                    _registry.Save();
                    _diagnostics.Record("state", "Stamped LastConnected on gateway record");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[ConnMgr] Failed to stamp LastConnected: {ex.Message}");
                }
            }
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        // Start node connection outside the semaphore to avoid deadlocks
        if (_nodeConnector != null && ShouldStartNodeConnection())
        {
            await StartNodeConnectionAsync();
        }
    }

    private void HandleDeviceTokenReceived(DeviceTokenReceivedEventArgs e)
    {
        _diagnostics.Record("credential", $"Device token received for {e.Role}",
            $"Scopes={string.Join(",", e.Scopes ?? [])}");

        if (_identityStore != null && _activeIdentityPath != null)
        {
            try
            {
                _identityStore.StoreToken(_activeIdentityPath, e.Token, e.Scopes, e.Role);
                _logger.Info($"[ConnMgr] Persisted {e.Role} device token via identity store");
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ConnMgr] Failed to persist {e.Role} device token: {ex.Message}");
            }
        }

        // Clear bootstrap token after NODE gets its device token — both roles are now paired.
        // Don't clear after operator: the node still needs bootstrap for its role-upgrade pairing.
        if (e.Role == "node" && _activeGatewayRecordId != null)
        {
            var record = _registry.GetById(_activeGatewayRecordId);
            if (record?.BootstrapToken != null)
            {
                _registry.AddOrUpdate(record with { BootstrapToken = null });
                _registry.Save();
                _diagnostics.Record("credential", "Cleared bootstrap token — both roles paired");
            }
        }
    }

    private async Task HandlePairingRequiredAsync(string? requestId, long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("pairing", $"Pairing required — waiting for approval (requestId={requestId})");
            _stateMachine.TryTransition(ConnectionTrigger.PairingPending);
            // Store requestId in snapshot so setup flows can use it for explicit approval
            _stateMachine.Current = _stateMachine.Current with { OperatorPairingRequestId = requestId };
            _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);
            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    // ─── Node Connection ───

    private bool ShouldStartNodeConnection()
    {
        if (_activeGatewayRecordId == null || _activeIdentityPath == null)
            return _isNodeEnabled?.Invoke() ?? false;

        var record = _registry.GetById(_activeGatewayRecordId);
        if (record == null)
            return false;

        if (_shouldStartNodeConnection != null)
            return _shouldStartNodeConnection(record, _activeIdentityPath);

        return _isNodeEnabled?.Invoke() ?? false;
    }

    private async Task StartNodeConnectionAsync()
    {
        if (_nodeConnector == null || _activeGatewayRecordId == null || _activeIdentityPath == null) return;

        var record = _registry.GetById(_activeGatewayRecordId);
        if (record == null)
        {
            _logger.Warn("[ConnMgr] Cannot start node — gateway record not found");
            return;
        }

        // Use root identity path — clients always read/write from root, not per-gateway
        var nodeCredential = _credentialResolver.ResolveNode(record, _activeIdentityPath!);
        if (nodeCredential == null)
        {
            _logger.Warn("[ConnMgr] No node credential available — skipping node connection");
            _diagnostics.Record("node", "No node credential available");
            return;
        }

        // Mark node as enabled in the state machine so UI reflects node state
        // State machine is not thread-safe — acquire semaphore for mutation
        await _transitionSemaphore.WaitAsync();
        try
        {
            _stateMachine.SetNodeEnabled(true);
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        var nodeConnectUrl = record.SshTunnel != null
            ? $"ws://localhost:{record.SshTunnel.LocalPort}"
            : record.Url;

        _diagnostics.Record("node", $"Starting node connection to {nodeConnectUrl}",
            $"Credential source: {nodeCredential.Source}");

        try
        {
            await _nodeConnector.ConnectAsync(nodeConnectUrl, nodeCredential, _activeIdentityPath,
                useV2Signature: _gatewayNeedsV2Signature);
        }
        catch (Exception ex)
        {
            _logger.Error($"[ConnMgr] Node connect failed: {ex.Message}");
            _diagnostics.Record("node", "Node connect failed", ex.Message);
        }
    }

    private async void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _diagnostics.Record("node", $"Node status: {status}");

        // Check connector's pairing status directly — it's set synchronously
        // before this handler runs, so it's always up-to-date
        var connectorPairingStatus = _nodeConnector?.PairingStatus;
        var isPairingPending = connectorPairingStatus == PairingStatus.Pending;

        if (isPairingPending && status is ConnectionStatus.Disconnected or ConnectionStatus.Error)
            return;

        await _transitionSemaphore.WaitAsync();
        try
        {
            var prev = _stateMachine.Current.OverallState;
            switch (status)
            {
                case ConnectionStatus.Connected:
                    _stateMachine.TryTransition(ConnectionTrigger.NodeConnected);
                    break;
                case ConnectionStatus.Connecting:
                    _stateMachine.StartNodeConnecting();
                    break;
                case ConnectionStatus.Disconnected:
                    if (_stateMachine.Current.NodeState != RoleConnectionState.PairingRequired)
                        _stateMachine.TryTransition(ConnectionTrigger.NodeDisconnected);
                    break;
                case ConnectionStatus.Error:
                    if (_stateMachine.Current.NodeState != RoleConnectionState.PairingRequired)
                        _stateMachine.TryTransition(ConnectionTrigger.NodeError, "Node transport error");
                    break;
            }

            // Update node state in snapshot
            if (_nodeConnector != null)
            {
                _stateMachine.Current = _stateMachine.Current with
                {
                    NodeDeviceId = _nodeConnector.NodeDeviceId,
                    NodePairingStatus = _nodeConnector.PairingStatus
                };
            }

            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async void OnNodePairingStatusChanged(object? sender, PairingStatusEventArgs e)
    {
        _diagnostics.Record("node", $"Node pairing: {e.Status}");

        await _transitionSemaphore.WaitAsync();
        try
        {
            var prev = _stateMachine.Current.OverallState;
            switch (e.Status)
            {
                case PairingStatus.Paired:
                    _stateMachine.TryTransition(ConnectionTrigger.NodePaired);
                    break;
                case PairingStatus.Pending:
                    _stateMachine.TryTransition(ConnectionTrigger.NodePairingRequired);
                    break;
                case PairingStatus.Rejected:
                    _stateMachine.TryTransition(ConnectionTrigger.NodePairingRejected);
                    break;
            }

            // Update snapshot
            if (_nodeConnector != null)
            {
                _stateMachine.Current = _stateMachine.Current with
                {
                    NodePairingStatus = _nodeConnector.PairingStatus,
                    NodeDeviceId = _nodeConnector.NodeDeviceId
                };
            }

            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        // Auto-approve node pairing if operator has admin/pairing scope
        if (e.Status == PairingStatus.Pending && !string.IsNullOrWhiteSpace(e.RequestId)
            && e.RequestId != _lastAutoApprovedRequestId)
        {
            var operatorClient = _activeLifecycle?.DataClient;
            if (operatorClient?.IsConnectedToGateway == true)
            {
                var scopes = operatorClient.GrantedOperatorScopes;
                var canApprove = scopes.Any(s =>
                    s.Equals("operator.admin", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("operator.pairing", StringComparison.OrdinalIgnoreCase));

                if (canApprove)
                {
                    _diagnostics.Record("node", $"Auto-approving node pairing (requestId={e.RequestId})");
                    try
                    {
                        var approved = await operatorClient.DevicePairApproveAsync(e.RequestId);
                        if (approved)
                        {
                            // Only set guard after confirmed success to allow retry on transient failure
                            _lastAutoApprovedRequestId = e.RequestId;
                            _diagnostics.Record("node", "Node pairing auto-approved — reconnecting node");
                            await Task.Delay(1000); // brief delay for gateway to process
                            await StartNodeConnectionAsync();
                        }
                        else
                        {
                            _diagnostics.Record("node", "Node auto-approval failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ConnMgr] Node auto-approve failed: {ex.Message}");
                        _diagnostics.Record("node", $"Auto-approve error: {ex.Message}");
                    }
                }
            }
        }
    }

    // ─── Helpers ───

    /// <summary>
    /// Clear stored device tokens from an identity file, keeping the keypair intact.
    /// </summary>
    private void ClearStoredDeviceTokens(string identityDir)
    {
        var keyPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (!File.Exists(keyPath)) return;
        try
        {
            var json = File.ReadAllText(keyPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Rebuild without token fields
            using var ms = new MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                // Skip token fields — keep everything else (keys, deviceId, algorithm, etc.)
                if (prop.Name is "DeviceToken" or "DeviceTokenScopes" or "NodeDeviceToken" or "NodeDeviceTokenScopes")
                    continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();

            File.WriteAllBytes(keyPath, ms.ToArray());
            _logger.Info($"[ConnMgr] Cleared stored device tokens from {identityDir}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Failed to clear device tokens: {ex.Message}");
        }
    }

    private void EmitStateChanged(OverallConnectionState previousOverall)
    {
        var snapshot = _stateMachine.Current;
        // Always fire when any part of the snapshot changed — not just OverallState.
        // Node sub-state changes (e.g. Idle→PairingRequired) may not change OverallState
        // but the UI still needs to update.
        StateChanged?.Invoke(this, snapshot);
    }

    private void DisposeActiveClient()
    {
        // Disconnect node first — run on threadpool to avoid sync context deadlocks
        if (_nodeConnector != null)
        {
            try { Task.Run(() => _nodeConnector.DisconnectAsync()).Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _logger.Warn($"[ConnMgr] Node disconnect error: {ex.Message}"); }
        }

        var old = _activeLifecycle;
        _activeLifecycle = null;
        _activeGatewayRecordId = null;
        if (old != null)
        {
            OperatorClientChanged?.Invoke(this, new OperatorClientChangedEventArgs
            {
                OldClient = old.DataClient,
                NewClient = null
            });
            old.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Unsubscribe from node events before disposing the semaphore
        // to prevent async void handlers from crashing via ObjectDisposedException.
        if (_nodeConnector != null)
        {
            _nodeConnector.StatusChanged -= OnNodeStatusChanged;
            _nodeConnector.PairingStatusChanged -= OnNodePairingStatusChanged;
        }
        // Acquire semaphore briefly to ensure no in-flight reconnect/switch is mid-transition.
        // Use a short timeout — if something is stuck, proceed with disposal anyway.
        try { _transitionSemaphore.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try
        {
            _stateMachine.TryTransition(ConnectionTrigger.Disposed);
            DisposeActiveClient();
            // Stop tunnel on app shutdown — run on threadpool with timeout to avoid stalling exit
            if (_tunnelManager?.IsActive == true)
            {
                try { Task.Run(() => _tunnelManager.StopAsync()).Wait(TimeSpan.FromSeconds(3)); }
                catch { /* shutting down — best effort */ }
            }
            _operationCts?.Cancel();
            _operationCts?.Dispose();
        }
        finally
        {
            try { _transitionSemaphore.Release(); } catch { }
            _transitionSemaphore.Dispose();
        }
    }
}

/// <summary>
/// Logger that tees messages to both the underlying logger and the diagnostics ring buffer.
/// Client handshake logs tagged with [HANDSHAKE] appear in the Connection Status timeline.
/// </summary>
internal sealed class DiagnosticTeeLogger : IOpenClawLogger
{
    private readonly IOpenClawLogger _inner;
    private readonly ConnectionDiagnostics _diagnostics;

    public DiagnosticTeeLogger(IOpenClawLogger inner, ConnectionDiagnostics diagnostics)
    {
        _inner = inner;
        _diagnostics = diagnostics;
    }

    public void Info(string message)
    {
        _inner.Info(message);
        // Forward handshake-related and connection-relevant messages to timeline
        if (message.Contains("[HANDSHAKE]") || message.Contains("challenge") ||
            message.Contains("hello-ok") || message.Contains("Handshake") ||
            message.Contains("  role=") || message.Contains("  scopes=") ||
            message.Contains("  deviceId=") || message.Contains("  nonce=") ||
            message.Contains("  signedAt=") || message.Contains("  sigToken") ||
            message.Contains("  signature ") || message.Contains("  isBootstrap") ||
            message.Contains("signed:") || message.Contains("auth:") ||
            message.Contains("gateway connected") || message.Contains("gateway reconnecting") ||
            message.Contains("[NODE]"))
        {
            // Strip redundant [HANDSHAKE] prefix since the category tag already shows "handshake"
            var clean = message.Replace("[HANDSHAKE] ", "");
            _diagnostics.Record("handshake", clean);
        }
    }

    public void Debug(string message) => _inner.Debug(message);

    public void Warn(string message)
    {
        _inner.Warn(message);
        var clean = message.Replace("[HANDSHAKE] ", "").Replace("[NODE] ", "");
        _diagnostics.Record("warning", clean);
    }

    public void Error(string message, Exception? ex = null)
    {
        _inner.Error(message, ex);
        _diagnostics.Record("error", message);
    }
}
