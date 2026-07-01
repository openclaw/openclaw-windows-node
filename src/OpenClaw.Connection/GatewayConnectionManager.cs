using OpenClaw.Shared;

namespace OpenClaw.Connection;

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
    private readonly Func<TimeSpan, Task> _reconnectDelay;
    private readonly SemaphoreSlim _transitionSemaphore = new(1, 1);
    private readonly SemaphoreSlim _nodeStartSemaphore = new(1, 1);
    private readonly object _nodeOperationLock = new();
    private readonly object _devicePairReconnectLock = new();
    private readonly object _disposeLock = new();

    private long _generation;
    private CancellationTokenSource? _operationCts;
    private long _nodeConnectionGeneration;
    private CancellationTokenSource? _nodeOperationCts;
    private IGatewayClientLifecycle? _activeLifecycle;
    private string? _activeIdentityPath; // identity directory for the active connection
    private string? _activeGatewayRecordId; // gateway record ID for node credential resolution
    private SshTunnelConfig? _activeSshTunnel;
    private bool _disposed;
    private Task? _disposeTask;
    private bool _gatewayNeedsV2Signature; // remembered across reconnects
    private string? _operatorTokenRecoveryAttemptedGatewayId;
    private string? _lastAutoApprovedDevicePairRequestId; // prevent role-upgrade auto-approve loops
    private string? _devicePairAutoApproveInFlight; // atomic guard against concurrent approval of same requestId
    private bool _devicePairReconnectInFlight;
    private readonly Dictionary<string, int> _devicePairReconnectAttempts = new(StringComparer.Ordinal);
    private string? _queuedDevicePairReconnectRequestId;
    private long _queuedDevicePairReconnectGeneration;
    private long _queuedDevicePairReconnectNodeGeneration;
    private string? _forceBootstrapForGatewayRecordId;
    private bool _activeConnectUsedBootstrapToken;
    private bool _postBootstrapOperatorReconnectScheduled;

    private const string MissingNodeCredentialMessage =
        "No node credential available. Re-pair this PC or add a shared/bootstrap gateway token.";
    private const string MissingNodeConnectorMessage =
        "Node mode is enabled, but no node connector is configured.";
    private const string MissingActiveGatewayForNodeMessage =
        "Node mode is enabled, but there is no active gateway context for node startup.";
    private const string MissingGatewayRecordForNodeMessage =
        "Node mode is enabled, but the active gateway record could not be found.";
    private const string NodeTunnelStartFailedMessage =
        "Node mode is enabled, but the SSH tunnel for node startup could not be started.";

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
        Func<GatewayRecord, string, bool>? shouldStartNodeConnection = null,
        Func<TimeSpan, Task>? reconnectDelay = null)
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
        _reconnectDelay = reconnectDelay ?? Task.Delay;
        _diagnostics = diagnostics ?? new ConnectionDiagnostics(clock: clock);
        _diagnostics.EventRecorded += (_, e) => DiagnosticEvent?.Invoke(this, e);

        if (_nodeConnector != null)
        {
            _nodeConnector.StatusChanged += OnNodeStatusChanged;
            _nodeConnector.PairingStatusChanged += OnNodePairingStatusChanged;
            _nodeConnector.DeviceTokenReceived += OnNodeDeviceTokenReceived;
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

    public async Task ConnectNodeOnlyAsync(string? gatewayId = null)
    {
        ThrowIfDisposed();
        long? preparedGeneration = null;

        await _transitionSemaphore.WaitAsync();
        try
        {
            preparedGeneration = await PrepareNodeOnlyConnectCoreAsync(gatewayId);
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        if (!preparedGeneration.HasValue)
            return;

        var startedGeneration = await StartNodeConnectionAsync(preparedGeneration.Value);
        if (startedGeneration.HasValue)
            EmitStateChanged();
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

            if (!_stateMachine.CanTransition(ConnectionTrigger.ConnectRequested))
            {
                _logger.Warn($"[ConnMgr] Cannot connect from state {_stateMachine.Current.OperatorState}");
                return;
            }

            // Cancel any in-flight operation
            var gen = Interlocked.Increment(ref _generation);
            var oldCts = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
            oldCts?.Cancel();
            oldCts?.Dispose();

            // Dispose old client
            await DisposeActiveClientAsync();

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
            if (_forceBootstrapForGatewayRecordId == record.Id &&
                !string.IsNullOrWhiteSpace(record.BootstrapToken))
            {
                credential = new GatewayCredential(
                    record.BootstrapToken!,
                    IsBootstrapToken: true,
                    CredentialResolver.SourceBootstrapToken);
                _forceBootstrapForGatewayRecordId = null;
            }
            _activeConnectUsedBootstrapToken = credential?.IsBootstrapToken == true;
            _postBootstrapOperatorReconnectScheduled = false;
            _diagnostics.RecordCredentialResolution(credential);
            _activeIdentityPath = perGatewayIdentityDir;
            _activeGatewayRecordId = record.Id;
            _activeSshTunnel = record.SshTunnel;
            _gatewayNeedsV2Signature = record.IsLocal || record.RequiresV2Signature;
            SyncNodeIntentFromSettings();

            if (credential == null)
            {
                _logger.Warn("[ConnMgr] No credential available for gateway");
                // Must go through Connecting → Error since AuthenticationFailed requires Connecting state
                _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
                _stateMachine.SetOperatorCredentialSource(null);
                _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, "No credential available");
                EmitStateChanged();
                return;
            }

            // Transition to Connecting
            var prevState = _stateMachine.Current.OverallState;
            _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
            _stateMachine.SetOperatorCredentialSource(credential.Source);
            _diagnostics.RecordStateChange(prevState, _stateMachine.Current.OverallState);
            EmitStateChanged();

            // Create client via factory — use a diagnostic-tee logger so client handshake
            // logs appear in the Connection Status window timeline.
            // When SSH tunnel is configured, start the tunnel and connect to the local URL.
            var connectUrl = record.Url;
            if (record.SshTunnel != null && _tunnelManager != null)
            {
                var tunnel = record.SshTunnel;
                if (string.IsNullOrWhiteSpace(tunnel.User) || string.IsNullOrWhiteSpace(tunnel.Host) ||
                    tunnel.SshPort is < 1 or > 65535 ||
                    tunnel.RemotePort is < 1 or > 65535 || tunnel.LocalPort is < 1 or > 65535)
                {
                    _logger.Warn("[ConnMgr] SSH tunnel config is incomplete");
                    _diagnostics.Record("tunnel", "SSH tunnel config is incomplete");
                    _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, "SSH tunnel config is incomplete");
                    EmitStateChanged();
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
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketError, $"SSH tunnel failed: {ex.Message}");
                    EmitStateChanged();
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
            lifecycle.DataClient.NodePairListUpdated += (s, list) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleNodePairListUpdatedAsync(list, gen);
            };
            lifecycle.DataClient.V2SignatureFallback += (s, _) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                RememberGatewayNeedsV2Signature(record.Id);
            };

            // Local gateways only support v2 signatures — skip the v3 attempt entirely
            // to avoid a spurious "metadata-upgrade" re-pairing triggered by the v3→v2 fallback.
            if (record.IsLocal || record.RequiresV2Signature)
                _gatewayNeedsV2Signature = true;

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
                catch (OperationCanceledException) { /* Expected: connect was cancelled. */ }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] Connect failed: {ex.Message}");
                }
            }, ct);
    }

    /// <summary>
    /// Starts the node role without requiring an operator credential. This is the
    /// durable tray restart path for already-paired Windows nodes whose registry
    /// record only has a persisted NodeDeviceToken.
    /// </summary>
    private async Task<long?> PrepareNodeOnlyConnectCoreAsync(string? gatewayId = null)
    {
        var id = gatewayId ?? _registry.ActiveGatewayId;
        if (id == null)
        {
            _logger.Warn("[ConnMgr] No gateway ID specified and no active gateway for node-only connect");
            return null;
        }

        var record = _registry.GetById(id);
        if (record == null)
        {
            _logger.Warn($"[ConnMgr] Gateway {id} not found in registry for node-only connect");
            return null;
        }

        var perGatewayIdentityDir = _registry.GetIdentityDirectory(record.Id);
        if (!Directory.Exists(perGatewayIdentityDir))
            Directory.CreateDirectory(perGatewayIdentityDir);

        // Same-gateway node reapproval reconnects keep the operator alive so it can
        // request the post-handshake node.list; all other paths reset lifecycle/tunnel state.
        var preservesOperatorConnection =
            _activeLifecycle != null &&
            _stateMachine.Current.OperatorState == RoleConnectionState.Connected &&
            string.Equals(_activeGatewayRecordId, record.Id, StringComparison.Ordinal) &&
            string.Equals(_stateMachine.Current.GatewayUrl, record.Url, StringComparison.Ordinal) &&
            Equals(_activeSshTunnel, record.SshTunnel);
        var operatorCredentialSource = preservesOperatorConnection
            ? _stateMachine.Current.OperatorCredentialSource
            : null;
        var gen = Interlocked.Read(ref _generation);
        if (!preservesOperatorConnection)
        {
            gen = Interlocked.Increment(ref _generation);
            var oldCts = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
            oldCts?.Cancel();
            oldCts?.Dispose();

            await DisposeActiveClientAsync();
        }

        _activeIdentityPath = perGatewayIdentityDir;
        _activeGatewayRecordId = record.Id;
        _activeSshTunnel = record.SshTunnel;
        _gatewayNeedsV2Signature = record.IsLocal || record.RequiresV2Signature;
        _stateMachine.Current = _stateMachine.Current with
        {
            GatewayId = record.Id,
            GatewayUrl = record.Url,
            GatewayName = record.FriendlyName
        };
        _stateMachine.SetNodeEnabled(true);
        _stateMachine.StartNodeConnecting();
        _stateMachine.SetNodeCredentialSource(null);

        var nodeCredential = _credentialResolver.ResolveNode(record, perGatewayIdentityDir);
        if (nodeCredential == null)
        {
            _logger.Warn("[ConnMgr] No node credential available for node-only connect");
            _diagnostics.Record("node", "No node credential available for node-only connect");
            _stateMachine.BlockNodeStart(MissingNodeCredentialMessage);
            EmitStateChanged();
            return null;
        }

        _diagnostics.RecordCredentialResolution(nodeCredential);
        _stateMachine.SetOperatorCredentialSource(operatorCredentialSource);
        _diagnostics.Record("node", $"Starting node-only connection to {record.Url}",
            $"Credential source: {nodeCredential.Source}");

        if (!preservesOperatorConnection && !await TryStartTunnelForNodeOnlyAsync(record))
        {
            _stateMachine.BlockNodeStart(NodeTunnelStartFailedMessage);
            EmitStateChanged();
            return null;
        }

        return Interlocked.Read(ref _generation) == gen ? gen : null;
    }

    private async Task<bool> TryStartTunnelForNodeOnlyAsync(GatewayRecord record)
    {
        if (record.SshTunnel == null)
            return true;

        if (_tunnelManager == null)
        {
            _diagnostics.Record("tunnel", "No tunnel manager available; using configured local tunnel URL for node-only connect");
            return true;
        }

        var tunnel = record.SshTunnel;
        if (string.IsNullOrWhiteSpace(tunnel.User) ||
            string.IsNullOrWhiteSpace(tunnel.Host) ||
            tunnel.RemotePort is < 1 or > 65535 ||
            tunnel.LocalPort is < 1 or > 65535)
        {
            _logger.Warn("[ConnMgr] SSH tunnel config is incomplete for node-only connect");
            _diagnostics.Record("tunnel", "SSH tunnel config is incomplete for node-only connect");
            return false;
        }

        try
        {
            var connectUrl = await _tunnelManager.StartAsync(tunnel, _operationCts!.Token);
            _diagnostics.Record("tunnel", $"SSH tunnel started for node-only connect → {connectUrl}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[ConnMgr] SSH tunnel start failed for node-only connect: {ex.Message}");
            _diagnostics.Record("tunnel", "SSH tunnel start failed for node-only connect", ex.Message);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    /// <summary>Core disconnect logic. Caller must hold <see cref="_transitionSemaphore"/>.</summary>
    private async Task DisconnectCoreAsync()
    {
        Interlocked.Increment(ref _generation);
        var oldCts = Interlocked.Exchange(ref _operationCts, null);
        oldCts?.Cancel();
        oldCts?.Dispose();

        var prev = _stateMachine.Current.OverallState;
        await DisposeActiveClientAsync();
        SyncNodeIntentFromSettings();
        _stateMachine.TryTransition(ConnectionTrigger.DisconnectRequested);
        _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);
        EmitStateChanged();
    }

    public async Task ReconnectAsync()
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
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
            await DisconnectCoreAsync();
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

    public async Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode, SshTunnelConfig? sshTunnel = null)
    {
        ThrowIfDisposed();

        // 1. Decode setup code
        var decoded = SetupCodeDecoder.Decode(setupCode);
        if (!decoded.Success || string.IsNullOrWhiteSpace(decoded.Url))
            return new SetupCodeResult(SetupCodeOutcome.InvalidCode, decoded.Error ?? "Could not decode setup code");

        var gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(decoded.Url);

        // 2. Validate URL
        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
            return new SetupCodeResult(SetupCodeOutcome.InvalidUrl, "Invalid gateway URL");

        // 3. Disconnect current gateway if any
        await DisconnectAsync();

        var existing = _registry.FindByUrl(gatewayUrl);

        // New gateway URL → reset v2 signature flag (new gateway might support v3)
        var isNewGateway = existing == null;
        if (isNewGateway)
            _gatewayNeedsV2Signature = false;

        // 4. Create or update gateway record
        var recordId = existing?.Id ?? Guid.NewGuid().ToString();

        // Setup codes from `openclaw qr` always provide bootstrap tokens.
        // Store as BootstrapToken so the credential resolver passes IsBootstrapToken=true,
        // causing the client to send auth.bootstrapToken (not auth.token).
        var record = (existing ?? new GatewayRecord { Id = recordId }) with
        {
            Url = gatewayUrl,
            SharedGatewayToken = existing?.SharedGatewayToken, // preserve existing shared token if any
            BootstrapToken = decoded.Token ?? existing?.BootstrapToken,
            SshTunnel = sshTunnel ?? existing?.SshTunnel,
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
        DeviceIdentityStore.ClearStoredTokens(identityDir, _logger);
        _diagnostics.Record("setup", $"Setup code applied for {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");

        // 5. Connect to new gateway
        if (!string.IsNullOrWhiteSpace(decoded.Token))
            _forceBootstrapForGatewayRecordId = recordId;
        await ConnectAsync(recordId);

        return new SetupCodeResult(SetupCodeOutcome.Success, GatewayUrl: gatewayUrl);
    }

    public async Task<SetupCodeResult> ConnectWithSharedTokenAsync(
        string gatewayUrl, string token, SshTunnelConfig? sshTunnel = null)
    {
        ThrowIfDisposed();

        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
            return new SetupCodeResult(SetupCodeOutcome.InvalidUrl, "Invalid gateway URL");

        // Find or create gateway record (dedup by URL)
        var existing = _registry.FindByUrl(gatewayUrl);
        var recordId = existing?.Id ?? Guid.NewGuid().ToString();
        var identityDir = _registry.GetIdentityDirectory(recordId);
        var hasDurableTokens =
            DeviceIdentity.HasStoredDeviceTokenForRole(identityDir, "operator", _logger) ||
            DeviceIdentity.HasStoredDeviceTokenForRole(identityDir, "node", _logger);

        if (existing != null && hasDurableTokens)
        {
            var validation = await ValidateSharedTokenBeforeReplacementAsync(
                gatewayUrl,
                token,
                identityDir,
                existing);
            if (validation.Outcome != SetupCodeOutcome.Success)
                return validation;
        }

        // Disconnect current gateway only after replacement credentials have been validated.
        await DisconnectAsync();

        var record = (existing ?? new GatewayRecord { Id = recordId }) with
        {
            Url = gatewayUrl,
            SharedGatewayToken = token,
            BootstrapToken = null,
            SshTunnel = sshTunnel,
        };
        _registry.AddOrUpdate(record);

        // Clear stored device tokens so the shared token is used
        if (!Directory.Exists(identityDir))
            Directory.CreateDirectory(identityDir);
        DeviceIdentityStore.ClearStoredTokens(identityDir, _logger);

        _registry.SetActive(recordId);
        _registry.Save();

        // Connect to the gateway
        try
        {
            await ConnectAsync(recordId);
            return new SetupCodeResult(SetupCodeOutcome.Success, GatewayUrl: gatewayUrl);
        }
        catch (Exception ex)
        {
            _logger.Error($"[ConnMgr] ConnectWithSharedToken failed: {ex.Message}");
            return new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, ex.Message);
        }
    }

    private async Task<SetupCodeResult> ValidateSharedTokenBeforeReplacementAsync(
        string gatewayUrl,
        string token,
        string identityDir,
        GatewayRecord existing)
    {
        Directory.CreateDirectory(identityDir);
        var diagLogger = new DiagnosticTeeLogger(_logger, _diagnostics);
        using var client = new OpenClawGatewayClient(
            gatewayUrl,
            token,
            diagLogger,
            tokenIsBootstrapToken: false,
            bootstrapPairAsNode: false,
            identityPath: identityDir,
            ignoreStoredDeviceToken: true)
        {
            UseV2Signature = existing.IsLocal || existing.RequiresV2Signature
        };

        var completion = new TaskCompletionSource<SetupCodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) =>
            completion.TrySetResult(new SetupCodeResult(SetupCodeOutcome.Success, GatewayUrl: gatewayUrl));
        client.AuthenticationFailed += (_, message) =>
            completion.TrySetResult(new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, message));
        client.StatusChanged += (_, status) =>
        {
            if (status == ConnectionStatus.Error)
                completion.TrySetResult(new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, "Shared token validation failed"));
        };

        try
        {
            await client.ConnectAsync();
            var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != completion.Task)
                return new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, "Timed out validating shared gateway token");

            return await completion.Task;
        }
        catch (Exception ex)
        {
            return new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, ex.Message);
        }
        finally
        {
            try { await client.DisconnectAsync(); }
            catch (Exception ex) { _logger.Warn($"[ConnMgr] Shared-token validation disconnect failed: {ex.Message}"); }
        }
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
            EmitStateChanged();
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

            if (TryScheduleOperatorTokenRecovery(message, gen))
                return;

            _diagnostics.Record("error", "Authentication failed", message);
            _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, message);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private bool TryScheduleOperatorTokenRecovery(string message, long gen)
    {
        if (!IsOperatorDeviceTokenMismatch(message) ||
            _activeGatewayRecordId == null ||
            _activeIdentityPath == null ||
            _operatorTokenRecoveryAttemptedGatewayId == _activeGatewayRecordId)
        {
            return false;
        }

        var record = _registry.GetById(_activeGatewayRecordId);
        if (record == null || string.IsNullOrWhiteSpace(record.BootstrapToken))
            return false;

        if (!DeviceIdentity.TryClearDeviceToken(_activeIdentityPath, _logger))
            return false;

        _operatorTokenRecoveryAttemptedGatewayId = _activeGatewayRecordId;
        _diagnostics.Record("credential", "Cleared stale operator device token; reconnecting with bootstrap token");

        ScheduleDelayedReconnect(gen, "[ConnMgr] Operator token recovery reconnect failed");

        return true;
    }

    private static bool IsOperatorDeviceTokenMismatch(string message) =>
        message.Contains("device token mismatch", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("AUTH_DEVICE_TOKEN_MISMATCH", StringComparison.OrdinalIgnoreCase);

    private async Task HandleHandshakeSucceededAsync(long gen)
    {
        bool shouldStartNodeConnection = false;
        bool missingGatewayRecordForNode = false;
        bool missingActiveGatewayForNode = false;
        bool missingNodeConnector = false;
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("state", "Handshake succeeded (hello-ok)");
            _stateMachine.TryTransition(ConnectionTrigger.HandshakeSucceeded);
            var nodeModeIntended = SyncNodeIntentFromSettings();
            if (_operatorTokenRecoveryAttemptedGatewayId == _activeGatewayRecordId)
                _operatorTokenRecoveryAttemptedGatewayId = null;

            // Update device ID from client
            if (_activeLifecycle?.DataClient is { } client)
            {
                _stateMachine.SetOperatorDeviceId(client.OperatorDeviceId);
            }

            missingActiveGatewayForNode =
                nodeModeIntended &&
                (_activeGatewayRecordId == null || _activeIdentityPath == null);
            missingGatewayRecordForNode =
                nodeModeIntended &&
                !missingActiveGatewayForNode &&
                _activeGatewayRecordId != null &&
                _registry.GetById(_activeGatewayRecordId) == null;
            shouldStartNodeConnection =
                !missingActiveGatewayForNode &&
                !missingGatewayRecordForNode &&
                ShouldStartNodeConnection();
            missingNodeConnector = shouldStartNodeConnection && _nodeConnector == null;
            if (missingActiveGatewayForNode)
            {
                _stateMachine.BlockNodeStart(MissingActiveGatewayForNodeMessage);
            }
            else if (missingGatewayRecordForNode)
            {
                _stateMachine.BlockNodeStart(MissingGatewayRecordForNodeMessage);
            }
            else if (missingNodeConnector)
            {
                _stateMachine.BlockNodeStart(MissingNodeConnectorMessage);
            }
            else if (shouldStartNodeConnection)
            {
                _stateMachine.SetNodeEnabled(true);
                if (_nodeConnector != null)
                {
                    _stateMachine.StartNodeConnecting();
                    _stateMachine.SetNodeCredentialSource(null);
                }
            }

            _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);
            EmitStateChanged();

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

        // Start node connection outside the semaphore to avoid deadlocks.
        // If Node mode is intended but no connector exists, publish the blocker
        // through the same manager snapshot instead of leaving node Idle/healthy.
        if (missingActiveGatewayForNode || missingGatewayRecordForNode || missingNodeConnector)
        {
            return;
        }

        if (shouldStartNodeConnection)
        {
            if (_nodeConnector != null)
                await StartNodeConnectionAsync(gen);
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

        TryClearBootstrapTokenAfterDurablePairing();
        TrySchedulePostBootstrapOperatorReconnect(e);
    }

    private void TryClearBootstrapTokenAfterDurablePairing()
    {
        if (_activeGatewayRecordId == null || _activeIdentityPath == null)
            return;

        var record = _registry.GetById(_activeGatewayRecordId);
        if (record?.BootstrapToken == null)
            return;

        var hasOperatorToken = DeviceIdentity.HasStoredDeviceTokenForRole(_activeIdentityPath, "operator", _logger);
        var hasNodeToken = DeviceIdentity.HasStoredDeviceTokenForRole(_activeIdentityPath, "node", _logger);
        if (!hasOperatorToken || !hasNodeToken)
        {
            _diagnostics.Record(
                "credential",
                "Retaining bootstrap token until role tokens are durable",
                $"operatorToken={hasOperatorToken}; nodeToken={hasNodeToken}");
            return;
        }

        _registry.AddOrUpdate(record with { BootstrapToken = null });
        _registry.Save();
        _diagnostics.Record("credential", "Cleared bootstrap token — operator and node tokens are durable");
    }

    private void TrySchedulePostBootstrapOperatorReconnect(DeviceTokenReceivedEventArgs e)
    {
        if (!_activeConnectUsedBootstrapToken ||
            _postBootstrapOperatorReconnectScheduled ||
            _activeIdentityPath == null ||
            _activeGatewayRecordId == null)
        {
            return;
        }

        var hasOperatorToken = !string.IsNullOrWhiteSpace(
            DeviceIdentity.TryReadStoredDeviceTokenForRole(_activeIdentityPath, "operator", _logger));
        var record = _registry.GetById(_activeGatewayRecordId);
        var canReconnectWithSharedToken = !string.IsNullOrWhiteSpace(record?.SharedGatewayToken);

        if (!hasOperatorToken && !canReconnectWithSharedToken)
            return;

        if (e.Role != "operator" && !(e.Role == "node" && !hasOperatorToken && canReconnectWithSharedToken))
            return;

        _postBootstrapOperatorReconnectScheduled = true;
        var reconnectGeneration = Interlocked.Read(ref _generation);
        var detail = hasOperatorToken
            ? "using persisted operator device token"
            : "using preserved shared gateway token";
        RememberGatewayNeedsV2Signature(_activeGatewayRecordId);
        _diagnostics.Record("credential", "Bootstrap handoff complete — reconnecting operator role", detail);

        ScheduleDelayedReconnect(
            reconnectGeneration,
            "[ConnMgr] Post-bootstrap operator reconnect failed",
            ex => _diagnostics.Record("credential", "Post-bootstrap operator reconnect failed", ex.Message));
    }

    private void ScheduleDelayedReconnect(
        long generation,
        string warningPrefix,
        Action<Exception>? onFailure = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _reconnectDelay(TimeSpan.FromMilliseconds(200));
                if (_disposed || Interlocked.Read(ref _generation) != generation)
                    return;

                await ReconnectAsync();
            }
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.Warn($"{warningPrefix}: {ex.Message}");
                onFailure?.Invoke(ex);
            }
        });
    }

    private void RememberGatewayNeedsV2Signature(string? gatewayRecordId)
    {
        _gatewayNeedsV2Signature = true;

        if (string.IsNullOrWhiteSpace(gatewayRecordId))
            return;

        try
        {
            _registry.Update(gatewayRecordId, r => r.RequiresV2Signature ? r : r with { RequiresV2Signature = true });
            _registry.Save();
            _diagnostics.Record("credential", "Remembered gateway v2 signature requirement");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Failed to persist v2 signature requirement: {ex.Message}");
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
            _stateMachine.SetOperatorPairingRequestId(requestId);
            _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    // ─── Node Connection ───

    /// <summary>
    /// Drive the node connection for the active gateway and await its terminal state.
    /// See <see cref="IGatewayConnectionManager.EnsureNodeConnectedAsync"/> for contract.
    /// </summary>
    public async Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Honor a pre-canceled token before any side effects (Hanselman review #4).
        cancellationToken.ThrowIfCancellationRequested();

        if (_nodeConnector == null)
            throw new InvalidOperationException("No node connector is configured on the manager.");

        var snapshot = _stateMachine.Current;
        if (snapshot.OperatorState != RoleConnectionState.Connected)
        {
            throw new InvalidOperationException(
                $"Operator must be Connected before EnsureNodeConnectedAsync (current: {snapshot.OperatorState}).");
        }

        if (_activeGatewayRecordId == null || _activeIdentityPath == null)
            throw new InvalidOperationException("No active gateway is configured.");

        // Already paired? short-circuit. (Idempotent — safe to call repeatedly.)
        if (snapshot.NodeState == RoleConnectionState.Connected
            && snapshot.NodePairingStatus == PairingStatus.Paired)
        {
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GatewayConnectionSnapshot s)
        {
            switch (s.NodeState)
            {
                case RoleConnectionState.Connected
                    when s.NodePairingStatus == PairingStatus.Paired:
                    tcs.TrySetResult(true);
                    break;
                case RoleConnectionState.PairingRejected:
                    tcs.TrySetException(new InvalidOperationException(
                        s.NodeError ?? "Node pairing was rejected by the gateway."));
                    break;
                case RoleConnectionState.Error:
                    tcs.TrySetException(new InvalidOperationException(
                        s.NodeError ?? "Node connection failed."));
                    break;
                // PairingRequired / Connecting / Idle — keep waiting. Gateway-owned
                // node command trust requires explicit operator approval. Explicitly
                // typed device-pair role upgrades may auto-approve; other pending
                // device-pair cases surface as a timeout so the caller can run the
                // WSL CLI device-approver before retrying.
            }
        }

        StateChanged += Handler;
        try
        {
            var startAttempted = (await StartNodeConnectionAsync(Interlocked.Read(ref _generation))).HasValue;

            if (!startAttempted)
            {
                tcs.TrySetException(new InvalidOperationException(
                    "Node connection could not be started — see ConnectionDiagnostics for the credential/record-resolution failure."));
            }
            else
            {
                // Re-evaluate state in case the connector reached terminal state synchronously
                // (test connectors may; production NodeConnector is async).
                Handler(this, _stateMachine.Current);
            }

            // Hanselman review #3: only apply the default 35s timeout when the caller
            // didn't supply a cancellable token. A caller that DOES pass one is signaling
            // they own the deadline (e.g. setup engine with its own retry budget).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!cancellationToken.CanBeCanceled)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(35));
            }

            try
            {
                await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the node to connect and pair with the gateway.");
            }
        }
        finally
        {
            StateChanged -= Handler;
        }
    }

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

    private bool SyncNodeIntentFromSettings()
    {
        var enabled = _isNodeEnabled?.Invoke() ?? false;
        if (_stateMachine.Current.NodeConnectionIntended != enabled ||
            (!enabled && _stateMachine.Current.NodeState != RoleConnectionState.Disabled))
        {
            _stateMachine.SetNodeEnabled(enabled);
        }

        return enabled;
    }

    private bool IsCurrentNodeAttempt(long lifecycleGeneration, long nodeGeneration) =>
        !_disposed &&
        Interlocked.Read(ref _generation) == lifecycleGeneration &&
        Interlocked.Read(ref _nodeConnectionGeneration) == nodeGeneration;

    private async Task<long?> StartNodeConnectionAsync(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration = null)
    {
        CancellationTokenSource nodeOperationCts;
        CancellationToken nodeOperationToken;
        long nodeGeneration;

        await _nodeStartSemaphore.WaitAsync();
        try
        {
            CancellationTokenSource? oldNodeOperationCts;
            lock (_nodeOperationLock)
            {
                if (!IsExpectedNodeStartCurrent(expectedLifecycleGeneration, expectedNodeGeneration))
                    return null;

                oldNodeOperationCts = _nodeOperationCts;
                _nodeOperationCts = null;
                oldNodeOperationCts?.Cancel();
            }

            if (_nodeConnector != null)
            {
                try
                {
                    if (!await WaitWithTimeoutAsync(
                            _nodeConnector.DisconnectAsync(),
                            TimeSpan.FromSeconds(2),
                            "Previous node disconnect"))
                    {
                        _diagnostics.Record("node", "Previous node disconnect timed out");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] Previous node disconnect failed: {ex.Message}");
                    _diagnostics.Record("node", "Previous node disconnect failed", ex.Message);
                    return null;
                }
            }

            lock (_nodeOperationLock)
            {
                if (!IsExpectedNodeStartCurrent(expectedLifecycleGeneration, expectedNodeGeneration))
                    return null;

                nodeOperationCts = new CancellationTokenSource();
                nodeOperationToken = nodeOperationCts.Token;
                nodeGeneration = Interlocked.Increment(ref _nodeConnectionGeneration);
                _nodeOperationCts = nodeOperationCts;
            }
        }
        finally
        {
            _nodeStartSemaphore.Release();
        }

        try
        {
            return await StartNodeConnectionCoreAsync(expectedLifecycleGeneration, nodeGeneration, nodeOperationToken)
                ? nodeGeneration
                : null;
        }
        catch (OperationCanceledException) when (nodeOperationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_nodeOperationLock)
            {
                if (ReferenceEquals(_nodeOperationCts, nodeOperationCts))
                    _nodeOperationCts = null;
            }
            nodeOperationCts.Dispose();
        }
    }

    private bool IsExpectedNodeStartCurrent(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration) =>
        !_disposed &&
        Interlocked.Read(ref _generation) == expectedLifecycleGeneration &&
        (!expectedNodeGeneration.HasValue ||
         Interlocked.Read(ref _nodeConnectionGeneration) == expectedNodeGeneration.Value);

    private async Task BlockNodeStartAsync(
        string detail,
        CancellationToken cancellationToken,
        long? expectedLifecycleGeneration = null,
        long? expectedNodeGeneration = null)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (expectedLifecycleGeneration.HasValue &&
                Interlocked.Read(ref _generation) != expectedLifecycleGeneration.Value)
            {
                return;
            }

            if (expectedNodeGeneration.HasValue &&
                Interlocked.Read(ref _nodeConnectionGeneration) != expectedNodeGeneration.Value)
            {
                return;
            }

            _stateMachine.BlockNodeStart(detail);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async Task<bool> StartNodeConnectionCoreAsync(
        long expectedLifecycleGeneration,
        long nodeGeneration,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            Interlocked.Read(ref _nodeConnectionGeneration) != nodeGeneration)
        {
            return false;
        }

        if (_nodeConnector == null)
        {
            await BlockNodeStartAsync(MissingNodeConnectorMessage, cancellationToken, expectedLifecycleGeneration, nodeGeneration);
            return false;
        }

        if (_activeGatewayRecordId == null || _activeIdentityPath == null)
        {
            await BlockNodeStartAsync(MissingActiveGatewayForNodeMessage, cancellationToken, expectedLifecycleGeneration, nodeGeneration);
            return false;
        }

        var record = _registry.GetById(_activeGatewayRecordId);
        if (record == null)
        {
            _logger.Warn("[ConnMgr] Cannot start node — gateway record not found");
            await BlockNodeStartAsync(MissingGatewayRecordForNodeMessage, cancellationToken, expectedLifecycleGeneration, nodeGeneration);
            return false;
        }

        // Mark node as enabled in the state machine so UI reflects node state
        // before credential resolution can fail. Otherwise node mode could look
        // healthy even though the intended node never started.
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!IsExpectedNodeStartCurrent(expectedLifecycleGeneration, nodeGeneration))
                return false;

            var before = _stateMachine.Current;
            _stateMachine.SetNodeEnabled(true);
            _stateMachine.StartNodeConnecting();
            _stateMachine.SetNodeCredentialSource(null);
            if (_stateMachine.Current != before)
                EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        // Use root identity path — clients always read/write from root, not per-gateway
        var nodeCredential = _credentialResolver.ResolveNode(record, _activeIdentityPath!);
        if (nodeCredential == null)
        {
            _logger.Warn("[ConnMgr] No node credential available — skipping node connection");
            _diagnostics.Record("node", "No node credential available");
            await BlockNodeStartAsync(MissingNodeCredentialMessage, cancellationToken, expectedLifecycleGeneration, nodeGeneration);
            return false;
        }

        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            _stateMachine.SetNodeCredentialSource(nodeCredential.Source);
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        if (cancellationToken.IsCancellationRequested ||
            Interlocked.Read(ref _nodeConnectionGeneration) != nodeGeneration)
        {
            return false;
        }

        var nodeConnectUrl = record.SshTunnel != null
            ? $"ws://localhost:{record.SshTunnel.LocalPort}"
            : record.Url;

        _diagnostics.Record("node", $"Starting node connection to {nodeConnectUrl}",
            $"Credential source: {nodeCredential.Source}");

        try
        {
            await _nodeConnector.ConnectAsync(nodeConnectUrl, nodeCredential, _activeIdentityPath,
                useV2Signature: _gatewayNeedsV2Signature,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested ||
                Interlocked.Read(ref _nodeConnectionGeneration) != nodeGeneration)
            {
                return false;
            }

            _logger.Error($"[ConnMgr] Node connect failed: {ex.Message}");
            _diagnostics.Record("node", "Node connect failed", ex.Message);
        }

        return !cancellationToken.IsCancellationRequested &&
            Interlocked.Read(ref _nodeConnectionGeneration) == nodeGeneration;
    }

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status) =>
        AsyncEventHandlerGuard.Run(
            () => OnNodeStatusChangedAsync(status),
            _logger,
            nameof(OnNodeStatusChanged),
            ex => _diagnostics.Record("node", "Node status handler failed", ex.Message));

    private void OnNodeDeviceTokenReceived(object? sender, DeviceTokenReceivedEventArgs e)
    {
        _diagnostics.Record("credential", $"Node connector device token received for {e.Role}",
            $"Scopes={string.Join(",", e.Scopes ?? [])}");
        TryClearBootstrapTokenAfterDurablePairing();
    }

    private async Task OnNodeStatusChangedAsync(ConnectionStatus status)
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
                var current = _stateMachine.Current;
                if (_nodeConnector.PairingStatus == PairingStatus.Pending &&
                    !string.IsNullOrWhiteSpace(current.NodePairingRequestId))
                {
                    _stateMachine.SetNodeInfo(
                        _nodeConnector.NodeDeviceId,
                        _nodeConnector.PairingStatus,
                        current.NodePairingRequestId,
                        current.NodePairingApprovalKind);
                }
                else
                {
                    _stateMachine.SetNodeInfo(_nodeConnector.NodeDeviceId, _nodeConnector.PairingStatus);
                }
            }

            TryClearBootstrapTokenAfterDurablePairing();
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private void OnNodePairingStatusChanged(object? sender, PairingStatusEventArgs e)
    {
        var lifecycleGeneration = Interlocked.Read(ref _generation);
        var nodeGeneration = Interlocked.Read(ref _nodeConnectionGeneration);
        AsyncEventHandlerGuard.Run(
            () => OnNodePairingStatusChangedAsync(e, lifecycleGeneration, nodeGeneration),
            _logger,
            nameof(OnNodePairingStatusChanged),
            ex => _diagnostics.Record("node", "Node pairing handler failed", ex.Message));
    }

    private async Task OnNodePairingStatusChangedAsync(
        PairingStatusEventArgs e,
        long lifecycleGeneration,
        long nodeGeneration)
    {
        if (!IsCurrentNodeAttempt(lifecycleGeneration, nodeGeneration))
            return;

        _diagnostics.Record("node", $"Node pairing: {e.Status}");

        await _transitionSemaphore.WaitAsync();
        try
        {
            if (!IsCurrentNodeAttempt(lifecycleGeneration, nodeGeneration))
                return;

            switch (e.Status)
            {
                case PairingStatus.Paired:
                    _stateMachine.TryTransition(ConnectionTrigger.NodePaired);
                    Interlocked.Exchange(ref _lastAutoApprovedDevicePairRequestId, null);
                    lock (_devicePairReconnectLock)
                    {
                        _devicePairReconnectAttempts.Clear();
                        _queuedDevicePairReconnectRequestId = null;
                        _queuedDevicePairReconnectGeneration = 0;
                        _queuedDevicePairReconnectNodeGeneration = 0;
                    }
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
                _stateMachine.SetNodeInfo(
                    _nodeConnector.NodeDeviceId,
                    _nodeConnector.PairingStatus,
                    e.RequestId,
                    e.ApprovalKind);
            }

            TryClearBootstrapTokenAfterDurablePairing();
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        if (e.Status == PairingStatus.Pending && !string.IsNullOrWhiteSpace(e.RequestId))
        {
            if (!IsCurrentNodeAttempt(lifecycleGeneration, nodeGeneration))
                return;

            if (e.ApprovalKind == PairingApprovalKind.DevicePair)
            {
                _diagnostics.Record("node", "Node device role-upgrade pending", $"requestId={e.RequestId}");
                if (e.RequestId != _lastAutoApprovedDevicePairRequestId)
                {
                    await AutoApproveDevicePairingRequestAsync(
                        e.RequestId,
                        lifecycleGeneration,
                        nodeGeneration);
                }
                else
                {
                    await ReconnectAfterApprovedDevicePairAsync(
                        e.RequestId,
                        lifecycleGeneration,
                        nodeGeneration);
                }
            }
            else
            {
                _diagnostics.Record(
                    "node",
                    "Node command-trust request is awaiting explicit operator approval",
                    $"requestId={e.RequestId}");
            }
        }
    }

    private Task HandleNodePairListUpdatedAsync(PairingListInfo list, long gen)
    {
        var nodeDeviceId = _nodeConnector?.NodeDeviceId;
        if (string.IsNullOrWhiteSpace(nodeDeviceId))
            return Task.CompletedTask;

        var request = list.Pending.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.RequestId) &&
            string.Equals(p.NodeId, nodeDeviceId, StringComparison.OrdinalIgnoreCase));
        if (request == null || Interlocked.Read(ref _generation) != gen)
            return Task.CompletedTask;

        _diagnostics.Record(
            "node",
            "Local node command-trust request is awaiting explicit operator approval",
            $"requestId={request.RequestId}");

        var operatorClient = _activeLifecycle?.DataClient;
        if (operatorClient?.IsConnectedToGateway == true)
        {
            ObserveBackgroundFault(
                operatorClient.RequestNodesAsync(),
                "[ConnMgr] Node list refresh failed after local node trust request");
        }

        return Task.CompletedTask;
    }

    // Auto-approve only explicitly typed device-pair role upgrades. Gateway-owned
    // node command trust always remains pending for explicit operator approval.
    // _devicePairAutoApproveInFlight is a CAS guard scoped to JUST the approve RPC —
    // we release it before the reconnect delay so unrelated approvals
    // (different requestIds) aren't starved while we wait for the gateway
    // and node-reconnect handshake to settle (which can take 5–30s on
    // first connect via WSL cold-start).
    private async Task AutoApproveDevicePairingRequestAsync(
        string requestId,
        long approvalGeneration,
        long approvalNodeGeneration)
    {
        if (requestId == _lastAutoApprovedDevicePairRequestId ||
            !IsCurrentNodeAttempt(approvalGeneration, approvalNodeGeneration))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _devicePairAutoApproveInFlight, requestId, null) != null)
            return;

        bool attemptedApprove = false;
        bool approved = false;
        try
        {
            if (!IsCurrentNodeAttempt(approvalGeneration, approvalNodeGeneration))
                return;

            var operatorClient = _activeLifecycle?.DataClient;
            if (operatorClient?.IsConnectedToGateway == true)
            {
                var scopes = operatorClient.GrantedOperatorScopes;
                var canApprove = OperatorScopeHelper.HasAdminScope(scopes);

                if (canApprove)
                {
                    _diagnostics.Record("node", $"Auto-approving device role-upgrade pairing (requestId={requestId})");
                    try
                    {
                        attemptedApprove = true;
                        approved = await operatorClient.DevicePairApproveAsync(requestId);
                        if (!approved)
                            _diagnostics.Record("node", "Device role-upgrade auto-approval failed", BuildDeviceAutoApprovalFailureDetail(scopes));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ConnMgr] Device role-upgrade auto-approve failed: {ex.Message}");
                        _diagnostics.Record("node", $"Device role-upgrade auto-approve error: {ex.Message}");
                    }
                }
                else
                {
                    _diagnostics.Record("node", "Device role-upgrade auto-approval skipped", BuildDeviceAutoApprovalFailureDetail(scopes));
                }
            }
        }
        finally
        {
            // Only dedupe successful approvals. If the gateway rejects,
            // times out, or throws while the same request is still pending,
            // a later Pending event must be able to retry the same requestId.
            if (attemptedApprove &&
                approved &&
                IsCurrentNodeAttempt(approvalGeneration, approvalNodeGeneration))
            {
                _lastAutoApprovedDevicePairRequestId = requestId;
            }
            Interlocked.Exchange(ref _devicePairAutoApproveInFlight, null);
        }

        // Post-approve reconnect happens OUTSIDE the CAS guard so it
        // doesn't block unrelated approvals.
        if (approved && IsCurrentNodeAttempt(approvalGeneration, approvalNodeGeneration))
        {
            await ReconnectAfterApprovedDevicePairAsync(
                requestId,
                approvalGeneration,
                approvalNodeGeneration);
        }
    }

    private async Task ReconnectAfterApprovedDevicePairAsync(
        string requestId,
        long approvalGeneration,
        long approvalNodeGeneration)
    {
        if (!IsCurrentNodeAttempt(approvalGeneration, approvalNodeGeneration))
            return;

        var ownsReconnect = false;
        var queuedRetry = false;
        lock (_devicePairReconnectLock)
        {
            _devicePairReconnectAttempts.TryGetValue(requestId, out var attemptCount);
            if (attemptCount >= 2)
                return;

            if (_devicePairReconnectInFlight)
            {
                if (_queuedDevicePairReconnectRequestId == null)
                {
                    _devicePairReconnectAttempts[requestId] = attemptCount + 1;
                    _queuedDevicePairReconnectRequestId = requestId;
                    _queuedDevicePairReconnectGeneration = approvalGeneration;
                    _queuedDevicePairReconnectNodeGeneration = approvalNodeGeneration;
                    queuedRetry = true;
                }
            }
            else
            {
                _devicePairReconnectAttempts[requestId] = attemptCount + 1;
                _devicePairReconnectInFlight = true;
                ownsReconnect = true;
            }
        }

        if (!ownsReconnect)
        {
            if (queuedRetry)
                _diagnostics.Record("node", "Device role-upgrade reconnect retry queued");
            return;
        }

        var guardOwned = true;
        try
        {
            var startedNodeGeneration = await RunDevicePairReconnectAttemptAsync(
                approvalGeneration,
                approvalNodeGeneration);
            AdvanceQueuedDevicePairReconnectNodeGeneration(
                approvalNodeGeneration,
                startedNodeGeneration);

            while (true)
            {
                string? retryRequestId;
                long retryGeneration;
                long retryNodeGeneration;
                lock (_devicePairReconnectLock)
                {
                    retryRequestId = _queuedDevicePairReconnectRequestId;
                    retryGeneration = _queuedDevicePairReconnectGeneration;
                    retryNodeGeneration = _queuedDevicePairReconnectNodeGeneration;
                    _queuedDevicePairReconnectRequestId = null;
                    _queuedDevicePairReconnectGeneration = 0;
                    _queuedDevicePairReconnectNodeGeneration = 0;
                    if (retryRequestId == null)
                    {
                        _devicePairReconnectInFlight = false;
                        guardOwned = false;
                        return;
                    }
                }

                _diagnostics.Record(
                    "node",
                    "Retrying device role-upgrade reconnect after repeated pending signal",
                    $"requestId={retryRequestId}");
                startedNodeGeneration = await RunDevicePairReconnectAttemptAsync(
                    retryGeneration,
                    retryNodeGeneration);
                AdvanceQueuedDevicePairReconnectNodeGeneration(
                    retryNodeGeneration,
                    startedNodeGeneration);
            }
        }
        finally
        {
            if (guardOwned)
            {
                lock (_devicePairReconnectLock)
                {
                    _devicePairReconnectInFlight = false;
                    _queuedDevicePairReconnectRequestId = null;
                    _queuedDevicePairReconnectGeneration = 0;
                    _queuedDevicePairReconnectNodeGeneration = 0;
                }
            }
        }
    }

    private void AdvanceQueuedDevicePairReconnectNodeGeneration(
        long previousNodeGeneration,
        long? startedNodeGeneration)
    {
        if (!startedNodeGeneration.HasValue)
            return;

        lock (_devicePairReconnectLock)
        {
            if (_queuedDevicePairReconnectRequestId != null &&
                _queuedDevicePairReconnectNodeGeneration == previousNodeGeneration)
            {
                _queuedDevicePairReconnectNodeGeneration = startedNodeGeneration.Value;
            }
        }
    }

    private async Task<long?> RunDevicePairReconnectAttemptAsync(
        long approvalGeneration,
        long approvalNodeGeneration)
    {
        _diagnostics.Record("node", "Device role-upgrade pairing approved — reconnecting node");
        await _reconnectDelay(TimeSpan.FromMilliseconds(1000)); // brief delay for gateway to process
        return await StartNodeConnectionAsync(approvalGeneration, approvalNodeGeneration);
    }

    private static string BuildDeviceAutoApprovalFailureDetail(IReadOnlyList<string> scopes) =>
        OperatorScopeHelper.HasAdminScope(scopes)
            ? "Gateway rejected device.pair.approve; check requestId and gateway device-pair state."
            : "Operator token lacks operator.admin for device.pair.approve role-upgrade approval.";

    // ─── Helpers ───

    private void EmitStateChanged()
    {
        var snapshot = _stateMachine.Current;
        // Always fire when any part of the snapshot changed — not just OverallState.
        // Node sub-state changes (e.g. Idle→PairingRequired) may not change OverallState
        // but the UI still needs to update.
        StateChanged?.Invoke(this, snapshot);
    }

    private async Task DisposeActiveClientAsync()
    {
        await _nodeStartSemaphore.WaitAsync();
        try
        {
            CancelNodeConnectionOperation();

            // Retire the connector before advancing the manager generation so
            // events from the old client cannot be tagged as belonging to the
            // replacement attempt.
            if (_nodeConnector != null)
            {
                try { await WaitWithTimeoutAsync(_nodeConnector.DisconnectAsync(), TimeSpan.FromSeconds(2), "Node disconnect"); }
                catch (Exception ex) { _logger.Warn($"[ConnMgr] Node disconnect error: {ex.Message}"); }
            }

            lock (_nodeOperationLock)
                Interlocked.Increment(ref _nodeConnectionGeneration);
        }
        finally
        {
            _nodeStartSemaphore.Release();
        }

        var old = _activeLifecycle;
        _activeLifecycle = null;
        _activeGatewayRecordId = null;
        _activeSshTunnel = null;
        _lastAutoApprovedDevicePairRequestId = null;
        Interlocked.Exchange(ref _devicePairAutoApproveInFlight, null);
        lock (_devicePairReconnectLock)
        {
            _devicePairReconnectAttempts.Clear();
            _queuedDevicePairReconnectRequestId = null;
            _queuedDevicePairReconnectGeneration = 0;
            _queuedDevicePairReconnectNodeGeneration = 0;
        }
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

    private void CancelNodeConnectionOperation()
    {
        lock (_nodeOperationLock)
        {
            var nodeOperationCts = _nodeOperationCts;
            _nodeOperationCts = null;
            nodeOperationCts?.Cancel();
        }
    }

    private async Task<bool> WaitWithTimeoutAsync(Task task, TimeSpan timeout, string operation)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            _logger.Warn($"[ConnMgr] {operation} timed out after {timeout.TotalSeconds:F1}s");
            return false;
        }

        await task.ConfigureAwait(false);
        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public ValueTask DisposeAsync()
    {
        var task = EnsureDisposeTask();
        return new ValueTask(task);
    }

    public void Dispose()
    {
        ObserveBackgroundFault(EnsureDisposeTask(), "[ConnMgr] Dispose error");
    }

    private Task EnsureDisposeTask()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _operationCts?.Cancel();

        // Unsubscribe from node events before disposing the semaphore
        // to prevent guarded async handlers from racing the disposed semaphore.
        if (_nodeConnector != null)
        {
            _nodeConnector.StatusChanged -= OnNodeStatusChanged;
            _nodeConnector.PairingStatusChanged -= OnNodePairingStatusChanged;
            _nodeConnector.DeviceTokenReceived -= OnNodeDeviceTokenReceived;
        }
        // Acquire semaphore briefly to ensure no in-flight reconnect/switch is mid-transition.
        // Use a short timeout — if something is stuck, proceed with disposal anyway,
        // but do not dispose the semaphore out from under the holder.
        var semaphoreEntered = false;
        try
        {
            semaphoreEntered = await _transitionSemaphore.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!semaphoreEntered)
                _logger.Warn("[ConnMgr] Dispose timed out waiting for transition semaphore");
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            _stateMachine.TryTransition(ConnectionTrigger.Disposed);
            await DisposeActiveClientAsync();
            // Stop tunnel on app shutdown with timeout to avoid stalling exit.
            if (_tunnelManager?.IsActive == true)
            {
                try { await WaitWithTimeoutAsync(_tunnelManager.StopAsync(), TimeSpan.FromSeconds(3), "Tunnel stop"); }
                catch (Exception ex) { _logger.Warn($"[ConnMgr] Tunnel stop error during dispose: {ex.Message}"); }
            }
            _operationCts?.Dispose();
            _operationCts = null;
        }
        finally
        {
            if (semaphoreEntered)
            {
                try { _transitionSemaphore.Release(); }
                catch (Exception ex) { _logger.Debug($"[ConnMgr] Dispose: transition semaphore release failed: {ex.Message}"); }
                _transitionSemaphore.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    private void ObserveBackgroundFault(Task task, string message)
    {
        if (task.IsFaulted)
        {
            _logger.Warn($"{message}: {task.Exception.GetBaseException().Message}");
            return;
        }

        if (task.IsCanceled)
        {
            _logger.Warn($"{message}: canceled");
            return;
        }

        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => _logger.Warn($"{message}: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

    public void Trace(string message) => _inner.Trace(message);

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
