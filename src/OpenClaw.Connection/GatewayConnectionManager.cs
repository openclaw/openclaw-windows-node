using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using OpenClaw.Shared;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Connection;

/// <summary>
/// Public connection lifecycle façade and sole writer of the overall connection
/// state machine, operator lifecycle, active gateway context, and tunnel state.
/// Node, bootstrap-token, and device-pair workflows are delegated to their
/// dedicated connection-domain owners through typed internal ports.
/// </summary>
public sealed class GatewayConnectionManager :
    IGatewayConnectionManager,
    INodeLifecycleSource,
    INodeConnectionStateSink,
    INodeConnectionStateSource,
    IEndpointCredentialSecurity,
    IGatewayAttemptLeaseSource,
    IOperatorReconnectScheduler,
    IV2SignatureRequirementSink,
    IOperatorApprovalGatewayLeaseSource
{
    internal const string OperatorConnectSpanName = "openclaw.connection.operator.connect";
    internal const string OperatorReconnectSpanName = "openclaw.connection.operator.reconnect";
    internal const string OperatorPrepareSpanName = "openclaw.connection.operator.prepare";
    internal const string OperatorTransportSpanName = "openclaw.connection.operator.transport";
    internal const string OperatorHandshakeSpanName = "openclaw.connection.operator.handshake";
    internal const string AttemptsMetricName = "openclaw.connection.attempts";
    internal const string AttemptDurationMetricName = "openclaw.connection.attempt.duration";
    internal const string StateTransitionsMetricName = "openclaw.connection.state.transitions";

    private const string RoleTag = "openclaw.connection.role";
    private const string OperationTag = "openclaw.connection.operation";
    private const string StateScopeTag = "openclaw.connection.state.scope";
    private const string StateFromTag = "openclaw.connection.state.from";
    private const string StateToTag = "openclaw.connection.state.to";
    private static readonly Counter<long> ConnectionAttempts = OpenClawTelemetry.CreateCounter(
        AttemptsMetricName,
        unit: "{attempt}",
        description: "Number of OpenClaw gateway connection attempts.");
    private static readonly Histogram<double> ConnectionAttemptDuration = OpenClawTelemetry.CreateHistogram(
        AttemptDurationMetricName,
        unit: "ms",
        description: "Duration of OpenClaw gateway connection attempts.");
    private static readonly Counter<long> ConnectionStateTransitions = OpenClawTelemetry.CreateCounter(
        StateTransitionsMetricName,
        unit: "{transition}",
        description: "Number of OpenClaw gateway connection state transitions.");

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
    private readonly Func<GatewayRecord, CancellationToken, Task<GatewayEndpointProvenance>>?
        _endpointProvenanceProbe;
    private readonly Func<ISshTunnelManager> _validationTunnelFactory;
    private readonly TimeSpan _credentialHandoffTimeout;
    private readonly TimeSpan _manualSshRestartTimeout;
    private readonly TimeSpan _manualSshRestartCleanupTimeout;
    private readonly SemaphoreSlim _transitionSemaphore = new(1, 1);
    private readonly object _disposeLock = new();
    private readonly object _telemetryLock = new();
    private readonly object _operatorFailureLock = new();
    private readonly object _connectionIntentLock = new();
    private readonly HashSet<string> _userDisconnectedGatewayIds = new(StringComparer.Ordinal);
    // Shared exclusive lease serializing destructive gateway lifecycle operations (manual WSL
    // start/stop/restart vs auto-repair distro restart). _manualLeaseHolders counts manual holders so
    // the monitor can additionally suppress starting new repairs while a manual action runs.
    private readonly SemaphoreSlim _gatewayLifecycleLease = new(1, 1);
    private readonly BootstrapTokenLifecycle _bootstrapTokenLifecycle;
    private readonly NodeConnectionCoordinator _nodeConnectionCoordinator;
    private readonly DevicePairApprovalCoordinator _devicePairApprovalCoordinator;
    private int _manualLeaseHolders;

    private long _generation;
    private CancellationTokenSource? _operationCts;
    private IGatewayClientLifecycle? _activeLifecycle;
    private string? _activeIdentityPath; // identity directory for the active connection
    private string? _activeGatewayRecordId; // gateway record ID for node credential resolution
    private SshTunnelConfig? _activeSshTunnel;
    private bool _disposed;
    private Task? _disposeTask;
    private bool _gatewayNeedsV2Signature; // remembered across reconnects
    private TelemetryAttempt? _operatorTelemetryAttempt;
    private GatewayConnectionSnapshot _lastTelemetrySnapshot = GatewayConnectionSnapshot.Idle;
    private long _pendingOperatorFailureGeneration;
    private GatewayErrorKind? _pendingOperatorFailureKind;
    private long _manualSshRestartGeneration;
    private CancellationTokenSource? _manualSshRestartCts;

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
        Func<TimeSpan, Task>? reconnectDelay = null,
        Func<GatewayRecord, CancellationToken, Task<GatewayEndpointProvenance>>?
            endpointProvenanceProbe = null,
        Func<ISshTunnelManager>? validationTunnelFactory = null,
        TimeSpan? credentialHandoffTimeout = null,
        TimeSpan? manualSshRestartTimeout = null,
        TimeSpan? manualSshRestartCleanupTimeout = null)
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
        _endpointProvenanceProbe = endpointProvenanceProbe;
        _validationTunnelFactory = validationTunnelFactory ?? (() => new SshTunnelService(_logger));
        _credentialHandoffTimeout = credentialHandoffTimeout ?? TimeSpan.FromSeconds(5);
        _manualSshRestartTimeout = manualSshRestartTimeout ?? TimeSpan.FromSeconds(35);
        _manualSshRestartCleanupTimeout =
            manualSshRestartCleanupTimeout ?? TimeSpan.FromSeconds(5);
        if (_credentialHandoffTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(credentialHandoffTimeout));
        if (_manualSshRestartTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(manualSshRestartTimeout));
        if (_manualSshRestartCleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(manualSshRestartCleanupTimeout));
        _diagnostics = diagnostics ?? new ConnectionDiagnostics(clock: clock);
        _diagnostics.EventRecorded += (_, e) => DiagnosticEvent?.Invoke(this, e);
        _bootstrapTokenLifecycle = new BootstrapTokenLifecycle(
            _registry,
            _identityStore,
            this,
            this,
            this,
            this,
            _logger,
            _diagnostics);
        _nodeConnectionCoordinator = new NodeConnectionCoordinator(
            this,
            this,
            this,
            this,
            this,
            _bootstrapTokenLifecycle,
            _credentialResolver,
            _nodeConnector,
            _logger,
            _diagnostics,
            _reconnectDelay,
            ConnectionAttempts,
            ConnectionAttemptDuration);
        _devicePairApprovalCoordinator = new DevicePairApprovalCoordinator(
            _nodeConnectionCoordinator,
            this,
            _logger,
            _diagnostics,
            _reconnectDelay);

        if (_nodeConnector != null)
        {
            _nodeConnector.StatusChanged += OnNodeStatusChanged;
            _nodeConnector.PairingStatusChanged += OnNodePairingStatusChanged;
            _nodeConnector.DeviceTokenReceived += OnNodeDeviceTokenReceived;
            if (_nodeConnector is INodeConnectorTelemetryEvents telemetryEvents)
            {
                telemetryEvents.TransportConnected += OnNodeTransportConnected;
                telemetryEvents.ConnectionFailure += OnNodeConnectionFailure;
            }
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
            var targetId = gatewayId ?? _registry.ActiveGatewayId;
            if (targetId is not null)
                SetGatewayConnectionIntent(targetId, shouldBeConnected: true);
            await ConnectCoreAsync(gatewayId, "connect");
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

        var startResult = await _nodeConnectionCoordinator.StartAsync(preparedGeneration.Value);
        if (startResult.Outcome == NodeStartOutcome.Started)
        {
            EmitStateChanged();
        }
        else
        {
            if (Interlocked.Read(ref _generation) != preparedGeneration.Value ||
                _tunnelManager?.IsActive != true)
            {
                return;
            }

            var enteredTransition = await _transitionSemaphore
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
            if (!enteredTransition)
            {
                _logger.Warn("[ConnMgr] Timed out waiting to clean up failed node-only tunnel");
                _diagnostics.Record(
                    "tunnel",
                    "Timed out waiting to clean up failed node-only tunnel");
                return;
            }

            try
            {
                if (Interlocked.Read(ref _generation) == preparedGeneration.Value &&
                    _activeLifecycle == null &&
                    _tunnelManager?.IsActive == true)
                {
                    await StopTunnelAfterFailedConnectionAsync("node-only connection failure");
                }
            }
            finally
            {
                _transitionSemaphore.Release();
            }
        }
    }

    /// <summary>Core connect logic. Caller must hold <see cref="_transitionSemaphore"/>.</summary>
    private async Task ConnectCoreAsync(
        string? gatewayId = null,
        string operation = "connect",
        CancellationToken externalCancellationToken = default)
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
            var newOperationCts = externalCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken)
                : new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _operationCts, newOperationCts);
            oldCts?.Cancel();
            oldCts?.Dispose();

            // Dispose old client
            await DisposeActiveClientAsync();
            StartOperatorTelemetryAttempt(operation, gen);

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

            var credentialResolution = _credentialResolver.ResolveOperatorDetailed(record, perGatewayIdentityDir);
            var credential = credentialResolution.Credential;
            if (HasPersistedIdentityFailure(credentialResolution))
            {
                _diagnostics.RecordCredentialResolutionResult(credentialResolution);
                _diagnostics.Record(
                    "identity",
                    "Stored device identity could not be loaded for operator connection",
                    credentialResolution.Detail);
                _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
                _stateMachine.SetOperatorCredentialResolution(credentialResolution);
                _stateMachine.TryTransition(
                    ConnectionTrigger.WebSocketError,
                    DeviceIdentityLoadException.RecoveryMessage);
                CompleteOperatorTelemetryAttempt(
                    gen,
                    "failure",
                    ConnectionErrorCategory.InternalError);
                EmitStateChanged();
                return;
            }

            var credentialSelection = _bootstrapTokenLifecycle.SelectOperatorCredential(
                record,
                credentialResolution);
            credentialResolution = credentialSelection.Resolution;
            credential = credentialResolution.Credential;
            _diagnostics.RecordCredentialResolutionResult(credentialResolution);
            _activeIdentityPath = perGatewayIdentityDir;
            _activeGatewayRecordId = record.Id;
            _activeSshTunnel = record.SshTunnel;
            _gatewayNeedsV2Signature = record.IsLocal || record.RequiresV2Signature;
            _bootstrapTokenLifecycle.BeginOperatorConnect(
                new GatewayAttemptStamp(gen, record.Id),
                credentialSelection.UsedBootstrapToken);
            SyncNodeIntentFromSettings();

            if (credential == null)
            {
                _logger.Warn("[ConnMgr] No credential available for gateway");
                // Must go through Connecting → Error since AuthenticationFailed requires Connecting state
                _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
                _stateMachine.SetOperatorCredentialResolution(credentialResolution);
                _stateMachine.TryTransition(
                    ConnectionTrigger.AuthenticationFailed,
                    CredentialResolutionFailureFormatter.Format(
                        ConnectionCredentialRole.Operator,
                        credentialResolution));
                CompleteOperatorTelemetryAttempt(
                    gen,
                    "failure",
                    ConnectionErrorCategory.AuthFailure);
                EmitStateChanged();
                return;
            }

            var endpointAuthorization = await AuthorizeCredentialForEndpointAsync(
                    record,
                    credential,
                    _operationCts!.Token).ConfigureAwait(false);
            var expectedEndpointOwnership = endpointAuthorization.OwnershipProof;
            if (_disposed ||
                Interlocked.Read(ref _generation) != gen ||
                _operationCts?.IsCancellationRequested != false)
            {
                return;
            }
            if (!endpointAuthorization.Allowed)
            {
                _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
                _stateMachine.SetOperatorCredentialResolution(credentialResolution);
                _stateMachine.SetOperatorErrorKind(endpointAuthorization.FailureKind);
                _stateMachine.TryTransition(
                    endpointAuthorization.FailureKind == GatewayErrorKind.Network
                        ? ConnectionTrigger.WebSocketError
                        : ConnectionTrigger.AuthenticationFailed,
                    endpointAuthorization.Detail);
                _diagnostics.Record("setup", "Blocked strong credential before managed-local endpoint ownership was proven", endpointAuthorization.Detail);
                CompleteOperatorTelemetryAttempt(
                    gen,
                    "failure",
                    endpointAuthorization.FailureKind == GatewayErrorKind.Network
                        ? ConnectionErrorCategory.NetworkUnreachable
                        : ConnectionErrorCategory.AuthFailure);
                EmitStateChanged();
                return;
            }

            // Transition to Connecting
            var prevState = _stateMachine.Current.OverallState;
            _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
            _stateMachine.SetOperatorCredentialResolution(credentialResolution);
            _diagnostics.RecordStateChange(prevState, _stateMachine.Current.OverallState);
            EmitStateChanged();

            // Create client via factory — use a diagnostic-tee logger so client handshake
            // logs appear in the Connection Status window timeline.
            // When SSH tunnel is configured, start the tunnel and connect to the local URL.
            var connectUrl = record.Url;
            SshTunnelStartResult? startedTunnel = null;
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
                    CompleteOperatorTelemetryAttempt(
                        gen,
                        "failure",
                        ConnectionErrorCategory.SshTunnelFailure);
                    EmitStateChanged();
                    return;
                }
                try
                {
                    startedTunnel = await _tunnelManager
                        .StartOwnedAsync(tunnel, _operationCts!.Token)
                        .ConfigureAwait(false);
                    connectUrl = startedTunnel.Url;
                    var tunnelAuthorization = await AuthorizeCredentialForEndpointAsync(
                        record,
                        credential,
                        _operationCts.Token,
                        requireSshTunnelOwnership: true).ConfigureAwait(false);
                    if (!tunnelAuthorization.Allowed)
                    {
                        _stateMachine.SetOperatorErrorKind(tunnelAuthorization.FailureKind);
                        _stateMachine.TryTransition(
                            tunnelAuthorization.FailureKind == GatewayErrorKind.Network
                                ? ConnectionTrigger.WebSocketError
                                : ConnectionTrigger.AuthenticationFailed,
                            tunnelAuthorization.Detail);
                        await StopOwnedTunnelAfterFailedConnectionAsync(
                            startedTunnel,
                            "SSH tunnel ownership authorization failure");
                        CompleteOperatorTelemetryAttempt(
                            gen,
                            "failure",
                            ConnectionErrorCategory.SshTunnelFailure);
                        EmitStateChanged();
                        return;
                    }
                    expectedEndpointOwnership = tunnelAuthorization.OwnershipProof;
                    _diagnostics.Record("tunnel", $"SSH tunnel started → {connectUrl}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] SSH tunnel start failed: {ex.Message}");
                    _diagnostics.Record("tunnel", "SSH tunnel start failed", ex.Message);
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketError, $"SSH tunnel failed: {ex.Message}");
                    if (startedTunnel is not null)
                    {
                        await StopOwnedTunnelAfterFailedConnectionAsync(
                            startedTunnel,
                            "SSH tunnel startup or authorization failure");
                    }
                    CompleteOperatorTelemetryAttempt(
                        gen,
                        "failure",
                        ConnectionErrorCategory.SshTunnelFailure);
                    EmitStateChanged();
                    return;
                }
            }
            else if (record.SshTunnel != null)
            {
                const string detail =
                    "SSH tunnel manager is unavailable, so credentials were not sent.";
                _diagnostics.Record("tunnel", detail);
                _stateMachine.SetOperatorErrorKind(GatewayErrorKind.Network);
                _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, detail);
                CompleteOperatorTelemetryAttempt(
                    gen,
                    "failure",
                    ConnectionErrorCategory.SshTunnelFailure);
                EmitStateChanged();
                return;
            }
            var diagLogger = new DiagnosticTeeLogger(_logger, _diagnostics);
            IGatewayClientLifecycle lifecycle;
            try
            {
                lifecycle = _clientFactory.Create(connectUrl, credential, perGatewayIdentityDir, diagLogger);
            }
            catch (DeviceIdentityLoadException ex)
            {
                var detail = BuildIdentityFailureDetail(ex);
                _logger.Error($"[ConnMgr] Stored device identity load failed: {detail}");
                _diagnostics.Record(
                    "identity",
                    "Stored device identity could not be loaded",
                    detail);
                _stateMachine.TryTransition(
                    ConnectionTrigger.WebSocketError,
                    DeviceIdentityLoadException.RecoveryMessage);
                if (startedTunnel is not null)
                {
                    await StopOwnedTunnelAfterFailedConnectionAsync(
                        startedTunnel,
                        "operator identity load failure");
                }
                CompleteOperatorTelemetryAttempt(
                    gen,
                    "failure",
                    ConnectionErrorCategory.InternalError);
                EmitStateChanged();
                return;
            }

            var operatorOperationCancellation = _operationCts!.Token;
            async Task<ReconnectAuthorizationResult> AuthorizeLiveCredentialHandoffAsync(
                CancellationToken cancellationToken)
            {
                var authorization = await AuthorizeCredentialHandoffAsync(
                        record,
                        credential,
                        expectedEndpointOwnership,
                        () => IsCurrentGatewayAttempt(gen, record.Id) &&
                            IsAutomaticReconnectAllowed(record.Id),
                        operatorOperationCancellation,
                        cancellationToken,
                        "operator")
                    .ConfigureAwait(false);
                if (!authorization.Allowed &&
                    authorization.FailureKind != GatewayErrorKind.Unknown)
                {
                    await RecordOperatorCredentialHandoffFailureAsync(
                            authorization.Detail ?? "Operator credential handoff was not authorized.",
                            authorization.FailureKind,
                            operatorOperationCancellation,
                            gen,
                            record.Id)
                        .ConfigureAwait(false);
                }
                return authorization;
            }

            lifecycle.DataClient.HandshakeAuthorizationAsync =
                AuthorizeLiveCredentialHandoffAsync;
            lifecycle.DataClient.ReconnectAuthorizationAsync =
                AuthorizeLiveCredentialHandoffAsync;
            _activeLifecycle = lifecycle;
            OperatorClientChanged?.Invoke(this, new OperatorClientChangedEventArgs
            {
                OldClient = null,
                NewClient = lifecycle.DataClient
            });

            // Subscribe to client events with generation and gateway guards.
            var subscribedGatewayId = record.Id;
            lifecycle.StatusChanged += (s, status) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                _ = HandleOperatorStatusChangedAsync(status, gen);
            };
            lifecycle.AuthenticationFailed += (s, msg) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                _ = HandleAuthenticationFailedAsync(msg, gen);
            };
            lifecycle.DataClient.ConnectionFailure += (s, kind) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                RecordOperatorFailureKind(gen, kind);
            };
            lifecycle.DataClient.TransportConnected += (s, e) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                TransitionOperatorTelemetryPhase(gen, OperatorHandshakeSpanName);
            };
            lifecycle.DataClient.HandshakeSucceeded += (s, e) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                _ = HandleHandshakeSucceededAsync(gen);
            };
            lifecycle.DataClient.DeviceTokenReceived += (s, e) =>
            {
                ObserveBackgroundFault(
                    HandleDeviceTokenReceivedAsync(
                        e,
                        new GatewayAttemptStamp(gen, subscribedGatewayId),
                        perGatewayIdentityDir),
                    "[ConnMgr] Device token handler failed");
            };
            lifecycle.DataClient.PairingRequired += (s, requestId) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                _ = HandlePairingRequiredAsync(requestId, gen);
            };
            lifecycle.DataClient.NodePairListUpdated += (s, list) =>
            {
                if (!IsCurrentGatewayAttempt(gen, subscribedGatewayId)) return;
                _devicePairApprovalCoordinator.HandleNodePairListUpdated(
                    list,
                    new GatewayAttemptStamp(gen, subscribedGatewayId),
                    _nodeConnectionCoordinator.CaptureCurrentAttempt(),
                    _nodeConnector?.NodeDeviceId);
            };
            lifecycle.DataClient.V2SignatureFallback += (s, e) =>
            {
                _ = HandleV2SignatureFallbackAsync(gen, subscribedGatewayId);
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
            TransitionOperatorTelemetryPhase(gen, OperatorTransportSpanName);
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
                    CompleteOperatorTelemetryAttempt(
                        gen,
                        "failure",
                        ConnectionErrorCategory.InternalError);
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

        var nodeCredentialResolution = _credentialResolver.ResolveNodeDetailed(record, perGatewayIdentityDir);
        var nodeCredential = nodeCredentialResolution.Credential;
        if (HasPersistedIdentityFailure(nodeCredentialResolution))
        {
            _diagnostics.RecordCredentialResolutionResult(nodeCredentialResolution);
            _diagnostics.Record(
                "identity",
                "Stored device identity could not be loaded for node-only connection",
                nodeCredentialResolution.Detail);
            _stateMachine.SetNodeCredentialResolution(nodeCredentialResolution);
            _stateMachine.BlockNodeStart(
                DeviceIdentityLoadException.RecoveryMessage,
                preserveCredentialResolution: true);
            EmitStateChanged();
            _nodeConnectionCoordinator.RecordPreflightTelemetryFailure(
                ConnectionErrorCategory.InternalError);
            return null;
        }
        if (nodeCredential == null)
        {
            _logger.Warn("[ConnMgr] No node credential available for node-only connect");
            _diagnostics.RecordCredentialResolutionResult(nodeCredentialResolution);
            _stateMachine.SetNodeCredentialResolution(nodeCredentialResolution);
            _stateMachine.BlockNodeStart(
                CredentialResolutionFailureFormatter.Format(
                    ConnectionCredentialRole.Node,
                    nodeCredentialResolution),
                preserveCredentialResolution: true);
            EmitStateChanged();
            _nodeConnectionCoordinator.RecordPreflightTelemetryFailure(
                ConnectionErrorCategory.AuthFailure);
            return null;
        }

        var nodeEndpointAuthorization = await AuthorizeCredentialForEndpointAsync(
                record,
                nodeCredential,
                _operationCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        if (_disposed ||
            Interlocked.Read(ref _generation) != gen ||
            _operationCts?.IsCancellationRequested != false)
        {
            return null;
        }
        if (!nodeEndpointAuthorization.Allowed)
        {
            _diagnostics.Record("setup", "Blocked node credential before managed-local endpoint ownership was proven", nodeEndpointAuthorization.Detail);
            _stateMachine.SetNodeCredentialResolution(nodeCredentialResolution);
            _stateMachine.BlockNodeStart(nodeEndpointAuthorization.Detail, preserveCredentialResolution: true);
            EmitStateChanged();
            _nodeConnectionCoordinator.RecordPreflightTelemetryFailure(
                ConnectionErrorCategory.AuthFailure);
            return null;
        }

        _diagnostics.RecordCredentialResolutionResult(nodeCredentialResolution);
        if (!preservesOperatorConnection)
            _stateMachine.SetOperatorCredentialSource(null);
        _diagnostics.Record("node", $"Starting node-only connection to {record.Url}",
            $"Credential source: {nodeCredential.Source}");

        SshTunnelStartResult? startedTunnel = null;
        if (!preservesOperatorConnection && record.SshTunnel is not null)
        {
            startedTunnel = await TryStartTunnelForNodeOnlyAsync(record);
            if (startedTunnel is null)
            {
                _stateMachine.SetNodeCredentialResolution(nodeCredentialResolution);
                _stateMachine.BlockNodeStart(NodeTunnelStartFailedMessage, preserveCredentialResolution: true);
                EmitStateChanged();
                _nodeConnectionCoordinator.RecordPreflightTelemetryFailure(
                    ConnectionErrorCategory.SshTunnelFailure);
                return null;
            }
        }

        if (record.SshTunnel is not null)
        {
            var tunnelAuthorization = await AuthorizeCredentialForEndpointAsync(
                record,
                nodeCredential,
                _operationCts?.Token ?? CancellationToken.None,
                requireSshTunnelOwnership: true).ConfigureAwait(false);
            if (!tunnelAuthorization.Allowed)
            {
                if (!preservesOperatorConnection)
                {
                    await StopOwnedTunnelAfterFailedConnectionAsync(
                        startedTunnel!,
                        "node-only ownership proof failure");
                }

                _stateMachine.SetNodeCredentialResolution(nodeCredentialResolution);
                _stateMachine.BlockNodeStart(
                    tunnelAuthorization.Detail,
                    preserveCredentialResolution: true);
                EmitStateChanged();
                _nodeConnectionCoordinator.RecordPreflightTelemetryFailure(
                    ConnectionErrorCategory.AuthFailure);
                return null;
            }
        }

        return Interlocked.Read(ref _generation) == gen ? gen : null;
    }

    private async Task<SshTunnelStartResult?> TryStartTunnelForNodeOnlyAsync(
        GatewayRecord record)
    {
        if (record.SshTunnel == null)
            return null;

        if (_tunnelManager == null)
        {
            _diagnostics.Record("tunnel", "No tunnel manager available for node-only SSH connection");
            return null;
        }

        var tunnel = record.SshTunnel;
        if (string.IsNullOrWhiteSpace(tunnel.User) ||
            string.IsNullOrWhiteSpace(tunnel.Host) ||
            tunnel.RemotePort is < 1 or > 65535 ||
            tunnel.LocalPort is < 1 or > 65535)
        {
            _logger.Warn("[ConnMgr] SSH tunnel config is incomplete for node-only connect");
            _diagnostics.Record("tunnel", "SSH tunnel config is incomplete for node-only connect");
            return null;
        }

        try
        {
            var startedTunnel = await _tunnelManager
                .StartOwnedAsync(tunnel, _operationCts!.Token)
                .ConfigureAwait(false);
            _diagnostics.Record(
                "tunnel",
                $"SSH tunnel started for node-only connect → {startedTunnel.Url}");
            return startedTunnel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[ConnMgr] SSH tunnel start failed for node-only connect: {ex.Message}");
            _diagnostics.Record("tunnel", "SSH tunnel start failed for node-only connect", ex.Message);
            return null;
        }
    }

    private async Task StopOwnedTunnelAfterFailedConnectionAsync(
        SshTunnelStartResult startedTunnel,
        string operation)
    {
        if (_tunnelManager is null)
            return;

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (await _tunnelManager.StopIfOwnedAsync(
                    startedTunnel.Config,
                    startedTunnel.OwnershipGeneration,
                    stopCts.Token).ConfigureAwait(false))
            {
                _diagnostics.Record("tunnel", $"SSH tunnel stopped after {operation}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"[ConnMgr] Tunnel stop timed out after {operation}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Tunnel stop failed after {operation}: {ex.Message}");
        }
    }

    private async Task StopTunnelAfterFailedConnectionAsync(string operation)
    {
        if (_tunnelManager?.IsActive != true)
            return;

        try
        {
            var stopTask = _tunnelManager.StopAsync();
            if (await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5))) != stopTask)
            {
                _logger.Warn($"[ConnMgr] Tunnel stop timed out after {operation}");
                return;
            }

            await stopTask;
            _diagnostics.Record("tunnel", $"SSH tunnel stopped after {operation}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Tunnel stop failed after {operation}: {ex.Message}");
            _diagnostics.Record("tunnel", $"SSH tunnel stop failed after {operation}", ex.Message);
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

    public async Task DisconnectByUserAsync()
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            var gatewayId = _registry.ActiveGatewayId;
            if (gatewayId is not null)
                SetGatewayConnectionIntent(gatewayId, shouldBeConnected: false);
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
        CancelOperatorTelemetryAttempt("canceled", ConnectionErrorCategory.Cancelled);
        Interlocked.Increment(ref _generation);
        _nodeConnectionCoordinator.CancelTelemetry(
            "canceled",
            ConnectionErrorCategory.Cancelled);
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
            var gatewayId = _registry.ActiveGatewayId;
            if (gatewayId is not null)
                SetGatewayConnectionIntent(gatewayId, shouldBeConnected: true);
            await DisconnectCoreAsync();
            await ConnectCoreAsync(operation: "reconnect");
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    /// <summary>
    /// Reconnects the active gateway ONLY if <paramref name="gatewayId"/> is still the active
    /// gateway, and honors cancellation. Used by managed-local auto-repair so a gateway switch
    /// during a repair cannot disrupt the newly selected gateway, and so a shutdown-cancelled
    /// repair does not drive a reconnect into a disposing manager. Returns true if it reconnected,
    /// false if the active gateway changed (no-op).
    /// </summary>
    public async Task<bool> ReconnectIfCurrentAsync(string gatewayId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!IsAutomaticReconnectAllowed(gatewayId))
                return false;

            if (!string.Equals(_registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal))
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            await DisconnectCoreAsync();
            cancellationToken.ThrowIfCancellationRequested();

            // Re-validate under the same semaphore hold before connecting: an out-of-band SetActive
            // (e.g. a UI gateway switch that mutates the registry outside this manager) could have
            // changed the active gateway while we were disconnecting. Fail closed if so.
            if (!string.Equals(_registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal))
                return false;
            if (!IsAutomaticReconnectAllowed(gatewayId))
                return false;

            // Connect the PINNED gateway id, not "whatever is active now" — ConnectCoreAsync otherwise
            // re-reads ActiveGatewayId, which the UI can mutate outside this semaphore, so an
            // unpinned connect could bring up a different gateway than the one this repair targeted.
            await ConnectCoreAsync(gatewayId, operation: "reconnect");

            // Report whether a connection was actually LAUNCHED for the pinned gateway. ConnectCoreAsync
            // bails to the Error state (without creating a client) when the record was removed mid-flight
            // or credential resolution failed — returning true there would let auto-repair treat a
            // credential failure as "reconnected, just unverified" and restart WSL, which cannot fix
            // credentials. Require a non-Error operator state AND the pinned record still active.
            return _stateMachine.Current.OperatorState is not RoleConnectionState.Error
                && _registry.GetById(gatewayId) is not null
                && string.Equals(_registry.ActiveGatewayId, gatewayId, StringComparison.Ordinal);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task<bool> RecoverSshTunnelAsync(SshTunnelExit tunnelExit)
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            var activeGateway = _registry.GetActive();
            if (tunnelExit.Owner != SshTunnelOwner.GatewayConnectionManager ||
                activeGateway?.SshTunnel != tunnelExit.Tunnel ||
                !IsAutomaticReconnectAllowed(activeGateway.Id) ||
                _tunnelManager?.IsRestartPending(tunnelExit) != true)
            {
                return false;
            }

            // DisconnectCoreAsync retires the gateway clients but deliberately leaves the
            // tunnel alone. Keep its generation token valid until ConnectCoreAsync replaces
            // the failed process, while preventing a delayed callback from reviving an old gateway.
            await DisconnectCoreAsync();

            var currentGateway = _registry.GetActive();
            if (currentGateway?.Id != activeGateway.Id ||
                currentGateway.SshTunnel != tunnelExit.Tunnel ||
                !IsAutomaticReconnectAllowed(activeGateway.Id) ||
                _tunnelManager?.IsRestartPending(tunnelExit) != true)
            {
                return false;
            }

            await ConnectCoreAsync(activeGateway.Id, "reconnect");
            return _stateMachine.Current.OperatorState is not RoleConnectionState.Error
                && _registry.GetById(activeGateway.Id) is not null
                && _tunnelManager?.IsActive == true
                && string.Equals(_registry.ActiveGatewayId, activeGateway.Id, StringComparison.Ordinal);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task<bool> RestartSshTunnelAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var restartGeneration = Interlocked.Increment(ref _manualSshRestartGeneration);
        using var restartCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        restartCts.CancelAfter(_manualSshRestartTimeout);
        var previousRestartCts = Interlocked.Exchange(ref _manualSshRestartCts, restartCts);
        try { previousRestartCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        EventHandler<GatewayConnectionSnapshot>? stateHandler = null;
        TaskCompletionSource<bool>? handshakeCompletion = null;
        long connectionGeneration = 0;
        long tunnelGeneration = 0;
        IGatewayClientLifecycle? lifecycle = null;
        GatewayRecord? expectedGatewayRecord = null;
        string? gatewayId = null;
        SshTunnelConfig? tunnelConfig = null;
        SshTunnelConfig? ownedTunnelConfig = null;
        var succeeded = false;

        try
        {
            await _transitionSemaphore.WaitAsync(restartCts.Token).ConfigureAwait(false);
            try
            {
                if (restartGeneration != Interlocked.Read(ref _manualSshRestartGeneration))
                    return false;

                var activeRecord = _registry.GetActive();
                expectedGatewayRecord = activeRecord;
                gatewayId = activeRecord?.Id;
                tunnelConfig = activeRecord?.SshTunnel;
                if (activeRecord is null ||
                    tunnelConfig is null ||
                    _tunnelManager is null)
                {
                    return false;
                }
                ownedTunnelConfig = tunnelConfig with
                {
                    User = tunnelConfig.User.Trim(),
                    Host = tunnelConfig.Host.Trim(),
                };

                var previousTunnelGeneration = _tunnelManager.OwnershipGeneration;
                var previousTunnelWasActive = _tunnelManager.IsActive;
                var previousTunnelConfig = _tunnelManager.ActiveConfig;
                if (previousTunnelWasActive && previousTunnelConfig is null)
                    return false;

                if (previousTunnelWasActive)
                {
                    var stopped = await _tunnelManager.StopIfOwnedAsync(
                            previousTunnelConfig!,
                            previousTunnelGeneration,
                            restartCts.Token)
                        .ConfigureAwait(false);
                    if (!stopped)
                        return false;
                }
                else if (_tunnelManager.IsActive)
                {
                    return false;
                }

                var stoppedTunnelGeneration = _tunnelManager.OwnershipGeneration;
                SetGatewayConnectionIntent(activeRecord.Id, shouldBeConnected: true);
                await DisconnectCoreAsync().ConfigureAwait(false);
                if (restartGeneration != Interlocked.Read(ref _manualSshRestartGeneration) ||
                    !IsCurrentSshRegistryRecord(activeRecord) ||
                    _tunnelManager.OwnershipGeneration != stoppedTunnelGeneration)
                {
                    return false;
                }

                try
                {
                    await ConnectCoreAsync(
                            activeRecord.Id,
                            operation: "reconnect",
                            restartCts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    connectionGeneration = Interlocked.Read(ref _generation);
                    lifecycle = _activeLifecycle;
                    tunnelGeneration = _tunnelManager.OwnershipGeneration;
                }
                if (lifecycle is null ||
                    !IsCurrentGatewayAttempt(connectionGeneration, activeRecord.Id) ||
                    !_tunnelManager.IsActive ||
                    _tunnelManager.ActiveConfig != ownedTunnelConfig)
                {
                    return false;
                }

                handshakeCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                stateHandler = (_, snapshot) =>
                {
                    if (restartGeneration != Interlocked.Read(ref _manualSshRestartGeneration) ||
                        connectionGeneration != Interlocked.Read(ref _generation) ||
                        !ReferenceEquals(lifecycle, _activeLifecycle))
                    {
                        handshakeCompletion.TrySetResult(false);
                        return;
                    }

                    if (snapshot.OperatorState == RoleConnectionState.Connected)
                        handshakeCompletion.TrySetResult(true);
                    else if (snapshot.OperatorState is RoleConnectionState.Error
                        or RoleConnectionState.PairingRequired)
                        handshakeCompletion.TrySetResult(false);
                };
                StateChanged += stateHandler;
                if (_stateMachine.Current.OperatorState == RoleConnectionState.Connected)
                    handshakeCompletion.TrySetResult(true);
                else if (_stateMachine.Current.OperatorState is RoleConnectionState.Error
                    or RoleConnectionState.PairingRequired)
                    handshakeCompletion.TrySetResult(false);
            }
            finally
            {
                _transitionSemaphore.Release();
            }

            if (handshakeCompletion is null ||
                !await handshakeCompletion.Task.WaitAsync(restartCts.Token).ConfigureAwait(false))
            {
                return false;
            }

            await _transitionSemaphore.WaitAsync(restartCts.Token).ConfigureAwait(false);
            try
            {
                var activeRecordId = _registry.ActiveGatewayId;
                if (restartGeneration != Interlocked.Read(ref _manualSshRestartGeneration) ||
                    connectionGeneration != Interlocked.Read(ref _generation) ||
                    !ReferenceEquals(lifecycle, _activeLifecycle) ||
                    _stateMachine.Current.OperatorState != RoleConnectionState.Connected ||
                    !lifecycle.DataClient.IsConnectedToGateway ||
                    gatewayId is null ||
                    activeRecordId is null ||
                    !string.Equals(activeRecordId, gatewayId, StringComparison.Ordinal) ||
                    expectedGatewayRecord is null ||
                    tunnelConfig is null ||
                    ownedTunnelConfig is null ||
                    !IsCurrentSshRegistryRecord(expectedGatewayRecord) ||
                    _tunnelManager is null ||
                    !_tunnelManager.IsActive ||
                    _tunnelManager.OwnershipGeneration != tunnelGeneration ||
                    _tunnelManager.ActiveConfig != ownedTunnelConfig)
                {
                    return false;
                }

                succeeded = await _tunnelManager.IsOwnedListenerReadyAsync(
                        tunnelConfig,
                        tunnelConfig.LocalPort,
                        restartCts.Token)
                    .ConfigureAwait(false);
                succeeded = succeeded &&
                    restartGeneration == Interlocked.Read(ref _manualSshRestartGeneration) &&
                    connectionGeneration == Interlocked.Read(ref _generation) &&
                    ReferenceEquals(lifecycle, _activeLifecycle) &&
                    _stateMachine.Current.OperatorState == RoleConnectionState.Connected &&
                    lifecycle.DataClient.IsConnectedToGateway &&
                    _tunnelManager.OwnershipGeneration == tunnelGeneration &&
                    _tunnelManager.ActiveConfig == ownedTunnelConfig &&
                    IsCurrentSshRegistryRecord(expectedGatewayRecord);
                return succeeded;
            }
            finally
            {
                _transitionSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (stateHandler is not null)
                StateChanged -= stateHandler;

            if (!succeeded)
            {
                await CleanupManualSshRestartAsync(
                        restartGeneration,
                        connectionGeneration,
                        lifecycle,
                        tunnelGeneration,
                        tunnelConfig)
                    .ConfigureAwait(false);
            }

            Interlocked.CompareExchange(ref _manualSshRestartCts, null, restartCts);
        }
    }

    private bool IsCurrentSshRegistryRecord(GatewayRecord expectedRecord)
    {
        var currentRecord = _registry.GetById(expectedRecord.Id);
        return currentRecord is not null &&
            string.Equals(_registry.ActiveGatewayId, expectedRecord.Id, StringComparison.Ordinal) &&
            IsSameCredentialHandoffRecord(currentRecord, expectedRecord);
    }

    private async Task CleanupManualSshRestartAsync(
        long restartGeneration,
        long connectionGeneration,
        IGatewayClientLifecycle? lifecycle,
        long tunnelGeneration,
        SshTunnelConfig? tunnelConfig)
    {
        if (restartGeneration != Interlocked.Read(ref _manualSshRestartGeneration) ||
            connectionGeneration == 0)
            return;

        using var cleanupCts = new CancellationTokenSource(_manualSshRestartCleanupTimeout);
        try
        {
            await _transitionSemaphore.WaitAsync(cleanupCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("[ConnMgr] Timed out waiting to clean up a failed manual SSH restart.");
            return;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return;
        }
        try
        {
            if (restartGeneration != Interlocked.Read(ref _manualSshRestartGeneration) ||
                connectionGeneration != Interlocked.Read(ref _generation) ||
                !ReferenceEquals(lifecycle, _activeLifecycle))
            {
                return;
            }

            await DisconnectCoreAsync().ConfigureAwait(false);
            if (tunnelConfig is not null && _tunnelManager is not null)
            {
                await _tunnelManager.StopIfOwnedAsync(
                        tunnelConfig,
                        tunnelGeneration,
                        cleanupCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            _logger.Warn("[ConnMgr] Timed out cleaning up a failed manual SSH restart.");
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public void SetGatewayConnectionIntent(string gatewayId, bool shouldBeConnected)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
            return;

        lock (_connectionIntentLock)
        {
            if (shouldBeConnected)
                _userDisconnectedGatewayIds.Remove(gatewayId);
            else
                _userDisconnectedGatewayIds.Add(gatewayId);
        }
    }

    public bool IsAutomaticReconnectAllowed(string gatewayId)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
            return false;
        lock (_connectionIntentLock)
        {
            return !_userDisconnectedGatewayIds.Contains(gatewayId);
        }
    }

    /// <summary>
    /// True while a user-initiated gateway lifecycle action (manual WSL start/stop/restart) is in
    /// progress. Managed-local auto-repair observes this to suppress STARTING a new repair.
    /// </summary>
    public bool IsManualGatewayLifecycleInProgress => Volatile.Read(ref _manualLeaseHolders) > 0;

    /// <summary>
    /// Acquires the shared gateway-lifecycle lease for a user-initiated manual WSL operation, awaiting
    /// it so the manual op is MUTUALLY EXCLUSIVE with an in-flight auto-repair distro restart (whose
    /// host-side terminate could otherwise kill the manual op's freshly booted VM). Also marks a manual
    /// holder so the monitor additionally suppresses starting new repairs. Dispose releases the lease.
    /// </summary>
    public async Task<IDisposable> BeginManualGatewayLifecycleOperationAsync(CancellationToken cancellationToken = default)
    {
        await _gatewayLifecycleLease.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _manualLeaseHolders);
        return new LeaseScope(this, isManual: true);
    }

    /// <summary>
    /// Non-blocking attempt to acquire the shared gateway-lifecycle lease for an automatic repair's
    /// destructive restart. Returns null if a manual (or another) operation holds it, so the coordinator
    /// aborts instead of running a concurrent restart. Dispose releases the lease.
    /// </summary>
    public IDisposable? TryAcquireGatewayLifecycleLease()
        => _gatewayLifecycleLease.Wait(0) ? new LeaseScope(this, isManual: false) : null;

    private void ReleaseGatewayLifecycleLease(bool isManual)
    {
        if (isManual)
            Interlocked.Decrement(ref _manualLeaseHolders);

        // Guard against a shutdown dispose-race: the manager may dispose the lease while a manual op
        // still holds a scope, so releasing here can hit a disposed semaphore. The manual-holder count
        // is already decremented above, so the monitor cannot get stuck-suppressed.
        // slopwatch-ignore: SW003 Shutdown dispose-race is expected; the count is already corrected and no caller state improves by surfacing it.
        try { _gatewayLifecycleLease.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private sealed class LeaseScope(GatewayConnectionManager owner, bool isManual) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseGatewayLifecycleLease(isManual);
        }
    }

    public async Task SwitchGatewayAsync(string gatewayId)
    {
        ThrowIfDisposed();
        using var lifecycleLease = await BeginManualGatewayLifecycleOperationAsync();
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (_registry.GetById(gatewayId) == null)
            {
                _logger.Warn($"[ConnMgr] Cannot switch gateway — record {gatewayId} not found");
                _diagnostics.Record("state", "Switch gateway failed", $"Gateway record not found: {gatewayId}");
                return;
            }

            var previousActiveId = _registry.ActiveGatewayId;
            _diagnostics.Record("state", $"Switching active gateway to {gatewayId}");
            SetGatewayConnectionIntent(gatewayId, shouldBeConnected: true);
            _registry.SetActive(gatewayId);
            try
            {
                _registry.Save();
            }
            catch (Exception ex)
            {
                _registry.SetActive(previousActiveId);
                _logger.Warn($"[ConnMgr] Failed to persist active gateway switch: {ex.Message}");
                _diagnostics.Record("state", "Switch gateway failed", $"Could not persist active gateway: {ex.Message}");
                return;
            }

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

        using var lifecycleLease = await BeginManualGatewayLifecycleOperationAsync();
        await _transitionSemaphore.WaitAsync();
        try
        {
            var existing = _registry.FindByUrl(gatewayUrl);

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
            var previousRecord = existing;
            var previousActiveId = _registry.ActiveGatewayId;
            _registry.AddOrUpdate(record);
            _registry.SetActive(recordId);
            try
            {
                _registry.Save();
            }
            catch (Exception ex)
            {
                if (previousRecord == null)
                    _registry.Remove(recordId);
                else
                    _registry.AddOrUpdate(previousRecord);
                _registry.SetActive(previousActiveId);
                _logger.Warn($"[ConnMgr] Failed to persist setup-code gateway update: {ex.Message}");
                return new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, ex.Message);
            }
            SetGatewayConnectionIntent(recordId, shouldBeConnected: true);

            // 3. Disconnect current gateway only after the new active gateway is persisted.
            await DisconnectCoreAsync();

            // Ensure identity directory
            var identityDir = _registry.GetIdentityDirectory(recordId);
            if (!Directory.Exists(identityDir))
                Directory.CreateDirectory(identityDir);

            // Force bootstrap for this connection without destroying durable device tokens.
            // A successful pairing replaces them; a failed attempt leaves the prior pairing intact.
            _diagnostics.Record("setup", $"Setup code applied for {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");

            // 5. Connect to new gateway
            if (!string.IsNullOrWhiteSpace(decoded.Token))
                _bootstrapTokenLifecycle.ForceBootstrapForNextConnect(recordId);
            try
            {
                await ConnectCoreAsync(recordId);
            }
            finally
            {
                _bootstrapTokenLifecycle.ClearForcedBootstrap(recordId);
            }
            if (_stateMachine.Current.OperatorState == RoleConnectionState.Error)
            {
                return new SetupCodeResult(
                    SetupCodeOutcome.ConnectionFailed,
                    _stateMachine.Current.OperatorError ?? "Gateway connection failed.");
            }
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        return new SetupCodeResult(SetupCodeOutcome.Success, GatewayUrl: gatewayUrl);
    }

    public Task<SetupCodeResult> ConnectWithSharedTokenAsync(
        string gatewayUrl,
        string token,
        SshTunnelConfig? sshTunnel = null) =>
        ConnectWithSharedTokenAsync(
            gatewayUrl,
            token,
            sshTunnel,
            onGatewayCommitted: null);

    public async Task<SetupCodeResult> ConnectWithSharedTokenAsync(
        string gatewayUrl,
        string token,
        SshTunnelConfig? sshTunnel,
        Func<GatewayRecord, CancellationToken, Task>? onGatewayCommitted)
    {
        ThrowIfDisposed();

        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayUrl))
            return new SetupCodeResult(SetupCodeOutcome.InvalidUrl, "Invalid gateway URL");

        ISshTunnelManager? isolatedValidationTunnel = null;
        SshTunnelConfig? isolatedValidationConfig = null;
        var gatewayCommitted = false;

        try
        {
            using var lifecycleLease = await BeginManualGatewayLifecycleOperationAsync();
            await _transitionSemaphore.WaitAsync();
            try
            {
                var existing = _registry.FindByUrl(gatewayUrl);
                var recordId = existing?.Id ?? Guid.NewGuid().ToString();
                var identityDir = _registry.GetIdentityDirectory(recordId);
                var hasDurableTokens =
                    DeviceIdentity.HasStoredDeviceTokenForRole(identityDir, "operator", _logger) ||
                    DeviceIdentity.HasStoredDeviceTokenForRole(identityDir, "node", _logger);

                if (existing != null && hasDurableTokens)
                {
                    var validationUrl = gatewayUrl;
                    if (sshTunnel is not null)
                    {
                        if (_tunnelManager is null)
                        {
                            return new SetupCodeResult(
                                SetupCodeOutcome.ConnectionFailed,
                                "SSH tunnel manager is unavailable; shared token was not sent.");
                        }

                        var excludedPorts = new HashSet<int>();
                        if (_tunnelManager.ActiveConfig is { } activeConfig)
                        {
                            excludedPorts.Add(activeConfig.LocalPort);
                            if (activeConfig.IncludeBrowserProxyForward)
                                excludedPorts.Add(activeConfig.LocalPort + 2);
                        }
                        var validationConfig = sshTunnel with
                        {
                            IncludeBrowserProxyForward = false,
                            LocalPort = GetAvailableLoopbackPort(excludedPorts),
                        };
                        isolatedValidationConfig = validationConfig;
                        isolatedValidationTunnel = _validationTunnelFactory();
                        try
                        {
                            validationUrl = await isolatedValidationTunnel
                                .StartAsync(validationConfig, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            return new SetupCodeResult(
                                SetupCodeOutcome.ConnectionFailed,
                                $"SSH tunnel validation failed: {ex.Message}");
                        }
                    }

                    var validationRecord = existing with
                    {
                        Url = validationUrl,
                        SharedGatewayToken = token,
                        SshTunnel = sshTunnel,
                    };
                    var validationCredential = new GatewayCredential(
                        token,
                        IsBootstrapToken: false,
                        CredentialResolver.SourceSharedGatewayToken);
                    var validationAuthorization = await AuthorizeCredentialForEndpointAsync(
                        validationRecord,
                        validationCredential,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!validationAuthorization.Allowed)
                    {
                        return new SetupCodeResult(
                            SetupCodeOutcome.ConnectionFailed,
                            validationAuthorization.Detail);
                    }

                    var validation = await ValidateSharedTokenBeforeReplacementAsync(
                        validationUrl,
                        token,
                        identityDir,
                        validationRecord,
                        isolatedValidationTunnel,
                        isolatedValidationConfig,
                        isolatedValidationTunnel?.OwnershipGeneration);
                    if (validation.Outcome != SetupCodeOutcome.Success)
                        return validation;
                }

                var record = ((existing ?? new GatewayRecord { Id = recordId }) with
                    {
                        Url = gatewayUrl,
                        SharedGatewayToken = token,
                        BootstrapToken = null,
                        SshTunnel = sshTunnel,
                    })
                    .PreserveAdvancedFields(existing);
                var previousRecord = existing;
                var previousActiveId = _registry.ActiveGatewayId;
                _registry.AddOrUpdate(record);
                _registry.SetActive(recordId);
                var registryPersisted = false;
                try
                {
                    _registry.Save();
                    registryPersisted = true;
                    if (onGatewayCommitted is not null)
                    {
                        await onGatewayCommitted(record, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    gatewayCommitted = true;
                }
                catch (Exception ex)
                {
                    if (previousRecord == null)
                        _registry.Remove(recordId);
                    else
                        _registry.AddOrUpdate(previousRecord);
                    _registry.SetActive(previousActiveId);
                    string? rollbackError = null;
                    if (registryPersisted)
                    {
                        try
                        {
                            _registry.Save();
                        }
                        catch (Exception rollbackException)
                        {
                            _registry.AddOrUpdate(record);
                            _registry.SetActive(recordId);
                            gatewayCommitted = true;
                            rollbackError =
                                $" Registry rollback failed; the new gateway remains active: {rollbackException.Message}";
                        }
                    }
                    _logger.Warn($"[ConnMgr] Failed to persist shared-token gateway update: {ex.Message}");
                    return new SetupCodeResult(
                        SetupCodeOutcome.ConnectionFailed,
                        $"{ex.Message}{rollbackError}",
                        GatewayUrl: gatewayUrl,
                        GatewayCommitted: gatewayCommitted);
                }

                if (isolatedValidationTunnel is not null)
                {
                    await StopAndDisposeValidationTunnelAsync(isolatedValidationTunnel).ConfigureAwait(false);
                    isolatedValidationTunnel = null;
                    isolatedValidationConfig = null;
                }
                SetGatewayConnectionIntent(recordId, shouldBeConnected: true);

                // Disconnect current gateway only after replacement credentials have been validated and persisted.
                await DisconnectCoreAsync();

                // The replacement shared token was validated above. Preserve durable device tokens;
                // they remain the preferred credential until a successful re-pair replaces them.
                if (!Directory.Exists(identityDir))
                    Directory.CreateDirectory(identityDir);

                // Connect to the gateway
                await ConnectCoreAsync(recordId);
                if (_stateMachine.Current.OperatorState == RoleConnectionState.Error)
                {
                    return new SetupCodeResult(
                        SetupCodeOutcome.ConnectionFailed,
                        _stateMachine.Current.OperatorError ?? "Gateway connection failed.",
                        GatewayUrl: gatewayUrl,
                        GatewayCommitted: true);
                }
            }
            finally
            {
                if (isolatedValidationTunnel is not null)
                    await StopAndDisposeValidationTunnelAsync(isolatedValidationTunnel).ConfigureAwait(false);

                _transitionSemaphore.Release();
            }
            return new SetupCodeResult(
                SetupCodeOutcome.Success,
                GatewayUrl: gatewayUrl,
                GatewayCommitted: true);
        }
        catch (DeviceIdentityLoadException ex)
        {
            var detail = BuildIdentityFailureDetail(ex);
            _logger.Error($"[ConnMgr] Stored device identity load failed while updating shared credentials: {detail}");
            _diagnostics.Record(
                "identity",
                "Stored device identity could not be loaded while updating shared credentials",
                detail);
            return new SetupCodeResult(
                SetupCodeOutcome.ConnectionFailed,
                DeviceIdentityLoadException.RecoveryMessage,
                GatewayUrl: gatewayCommitted ? gatewayUrl : null,
                GatewayCommitted: gatewayCommitted);
        }
        catch (Exception ex)
        {
            _logger.Error($"[ConnMgr] ConnectWithSharedToken failed: {ex.Message}");
            return new SetupCodeResult(
                SetupCodeOutcome.ConnectionFailed,
                ex.Message,
                GatewayUrl: gatewayCommitted ? gatewayUrl : null,
                GatewayCommitted: gatewayCommitted);
        }
    }

    private async Task<SetupCodeResult> ValidateSharedTokenBeforeReplacementAsync(
        string gatewayUrl,
        string token,
        string identityDir,
        GatewayRecord existing,
        ISshTunnelManager? validationTunnel,
        SshTunnelConfig? validationTunnelConfig,
        long? expectedTunnelOwnershipGeneration)
    {
        Directory.CreateDirectory(identityDir);
        using var client = CreateSharedTokenValidationClient(
            gatewayUrl,
            token,
            identityDir,
            existing,
            validationTunnel,
            validationTunnelConfig,
            expectedTunnelOwnershipGeneration);

        var completion = new TaskCompletionSource<SetupCodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) =>
            completion.TrySetResult(new SetupCodeResult(SetupCodeOutcome.Success, GatewayUrl: gatewayUrl));
        client.AuthenticationFailed += (_, message) =>
            completion.TrySetResult(new SetupCodeResult(SetupCodeOutcome.ConnectionFailed, message));
        client.StatusChanged += (_, status) =>
        {
            if (status is ConnectionStatus.Error or ConnectionStatus.Disconnected)
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

    internal OpenClawGatewayClient CreateSharedTokenValidationClient(
        string gatewayUrl,
        string token,
        string identityDir,
        GatewayRecord validationRecord,
        ISshTunnelManager? validationTunnel,
        SshTunnelConfig? validationTunnelConfig,
        long? expectedTunnelOwnershipGeneration = null)
    {
        var diagLogger = new DiagnosticTeeLogger(_logger, _diagnostics);
        expectedTunnelOwnershipGeneration ??= validationTunnel?.OwnershipGeneration;
        var client = new OpenClawGatewayClient(
            gatewayUrl,
            token,
            diagLogger,
            tokenIsBootstrapToken: false,
            bootstrapPairAsNode: false,
            identityPath: identityDir,
            ignoreStoredDeviceToken: true,
            persistHandshakeDeviceTokens: false)
        {
            UseV2Signature =
                validationRecord.IsLocal || validationRecord.RequiresV2Signature
        };
        // This is a one-shot validation client. A reconnect would reuse the strong shared token after
        // ownership may have changed; fail the validation instead and let the caller retry from a new
        // provenance preflight.
        client.ReconnectAuthorizationAsync = _ => Task.FromResult(
            new ReconnectAuthorizationResult(
                false,
                GatewayErrorKind.Auth,
                "Shared-token validation is one-shot."));
        client.HandshakeAuthorizationAsync = cancellationToken =>
            AuthorizeValidationCredentialHandshakeAsync(
                validationRecord,
                new GatewayCredential(
                    token,
                    IsBootstrapToken: false,
                    CredentialResolver.SourceSharedGatewayToken),
                validationTunnel,
                validationTunnelConfig,
                expectedTunnelOwnershipGeneration,
                cancellationToken);
        return client;
    }

    internal static async Task<ReconnectAuthorizationResult> AuthorizeValidationTunnelHandshakeAsync(
        ISshTunnelManager validationTunnel,
        SshTunnelConfig validationTunnelConfig,
        long expectedOwnershipGeneration,
        CancellationToken cancellationToken)
    {
        var allowed = await validationTunnel
            .IsOwnedListenerReadyAsync(
                validationTunnelConfig,
                validationTunnelConfig.LocalPort,
                cancellationToken)
            .ConfigureAwait(false);
        return allowed && validationTunnel.OwnershipGeneration == expectedOwnershipGeneration
            ? new ReconnectAuthorizationResult(true, GatewayErrorKind.Unknown, string.Empty)
            : new ReconnectAuthorizationResult(
                false,
                GatewayErrorKind.LocalPortConflict,
                "The isolated SSH validation listener changed before authentication, so the shared token was not sent.");
    }

    internal async Task<ReconnectAuthorizationResult> AuthorizeValidationCredentialHandshakeAsync(
        GatewayRecord validationRecord,
        GatewayCredential validationCredential,
        ISshTunnelManager? validationTunnel,
        SshTunnelConfig? validationTunnelConfig,
        long? expectedTunnelOwnershipGeneration,
        CancellationToken cancellationToken)
    {
        if (validationTunnel is not null && validationTunnelConfig is not null)
        {
            if (!expectedTunnelOwnershipGeneration.HasValue)
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    "The isolated SSH validation listener ownership could not be pinned, so the shared token was not sent.");
            }
            return await AuthorizeValidationTunnelHandshakeAsync(
                    validationTunnel,
                    validationTunnelConfig,
                    expectedTunnelOwnershipGeneration.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var authorization = await AuthorizeCredentialForEndpointAsync(
                validationRecord,
                validationCredential,
                cancellationToken)
            .ConfigureAwait(false);
        return new ReconnectAuthorizationResult(
            authorization.Allowed,
            authorization.FailureKind,
            authorization.Detail);
    }

    private async Task StopAndDisposeValidationTunnelAsync(ISshTunnelManager tunnel)
    {
        try
        {
            if (tunnel.IsActive)
                await tunnel.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Failed to stop the isolated SSH validation tunnel: {ex.Message}");
        }
        finally
        {
            try
            {
                tunnel.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ConnMgr] Failed to dispose the isolated SSH validation tunnel: {ex.Message}");
            }
        }
    }

    private static int GetAvailableLoopbackPort(IReadOnlySet<int> excludedPorts)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (!excludedPorts.Contains(port))
                    return port;
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("Unable to allocate an isolated SSH validation port.");
    }

    // ─── Event Handlers ───

    private async Task HandleOperatorStatusChangedAsync(ConnectionStatus status, long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            // Check client's pairing status while holding the transition lock so
            // a completed pairing cannot race with a stale disconnect/error event.
            var isPairingPending = _activeLifecycle?.DataClient?.IsPairingRequired == true;
            if (isPairingPending && status is ConnectionStatus.Disconnected or ConnectionStatus.Error)
                return;

            switch (status)
            {
                case ConnectionStatus.Connected:
                    _diagnostics.RecordWebSocketEvent("WebSocket connected");
                    ClearOperatorFailureKind(gen);
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketConnected);
                    break;
                case ConnectionStatus.Disconnected:
                    _diagnostics.RecordWebSocketEvent("WebSocket disconnected");
                    // Don't overwrite PairingRequired — gateway closes socket after pairing required
                    if (_stateMachine.Current.OperatorState != RoleConnectionState.PairingRequired)
                        _stateMachine.TryTransition(ConnectionTrigger.WebSocketDisconnected);
                    CompleteOperatorTelemetryAttempt(
                        gen,
                        "failure",
                        ConnectionErrorCategory.ServerClose);
                    break;
                case ConnectionStatus.Error:
                    _diagnostics.RecordWebSocketEvent("WebSocket error");
                    if (_stateMachine.Current.OperatorState != RoleConnectionState.PairingRequired)
                    {
                        // AuthenticationFailed and Status=Error are raised back-to-back and handled
                        // asynchronously. If the auth handler already promoted the failure to a more
                        // specific terminal kind (for example LocalPortConflict), never let the later
                        // generic status handler overwrite it with the original token/transport kind.
                        if (_stateMachine.Current.OperatorState != RoleConnectionState.Error ||
                            _stateMachine.Current.OperatorErrorKind is null)
                        {
                            _stateMachine.SetOperatorErrorKind(ReadOperatorFailureKind(gen));
                            _stateMachine.TryTransition(
                                ConnectionTrigger.WebSocketError,
                                "Transport error");
                        }
                    }
                    CompleteOperatorTelemetryAttempt(
                        gen,
                        "failure",
                        ConnectionErrorCategory.NetworkUnreachable);
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
        GatewayErrorKind failureKind;
        GatewayAttemptStamp attempt;
        string? identityPath;
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            failureKind =
                ReadOperatorFailureKind(gen) ?? GatewayErrorClassifier.ClassifyWithCode(message);
            var activeRecord = _activeGatewayRecordId is null
                ? null
                : _registry.GetById(_activeGatewayRecordId);
            var provenance = activeRecord is not null &&
                GatewayRecordEditing.ResolveManagedDistroName(activeRecord) is not null &&
                _endpointProvenanceProbe is not null
                    ? await _endpointProvenanceProbe(activeRecord, CancellationToken.None).ConfigureAwait(false)
                    : null;
            var unexpectedManagedLocalOwner =
                provenance?.Kind is GatewayEndpointProvenanceKind.ConflictingOpenClawGateway
                    or GatewayEndpointProvenanceKind.UnknownListener;

            // A wrong local process may report either shared-token mismatch OR device-token mismatch.
            // In both cases the real failure is endpoint identity, not credentials: never disclose the
            // shared/bootstrap fallback and let the provenance-gated collision repair own recovery.
            if (activeRecord is not null &&
                unexpectedManagedLocalOwner &&
                failureKind is GatewayErrorKind.DeviceTokenMismatch or GatewayErrorKind.Auth)
            {
                failureKind = GatewayErrorKind.LocalPortConflict;
                _diagnostics.Record(
                    "setup",
                    "Managed local gateway port is owned by a different or unverified process",
                    $"gatewayId={activeRecord.Id}");
            }
            attempt = new GatewayAttemptStamp(gen, _activeGatewayRecordId);
            identityPath = _activeIdentityPath;
        }
        finally
        {
            _transitionSemaphore.Release();
        }

        if (failureKind == GatewayErrorKind.DeviceTokenMismatch &&
            identityPath is not null &&
            await _bootstrapTokenLifecycle.TryScheduleOperatorTokenRecoveryAsync(
                attempt,
                identityPath,
                message,
                CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        await _transitionSemaphore.WaitAsync();
        try
        {
            if (!IsCurrentGatewayAttempt(gen, attempt.GatewayRecordId ?? string.Empty))
                return;

            _diagnostics.Record("error", "Authentication failed", message);
            _stateMachine.SetOperatorErrorKind(failureKind);
            _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, message);
            CompleteOperatorTelemetryAttempt(
                gen,
                "failure",
                ConnectionErrorCategory.AuthFailure);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private void RecordOperatorFailureKind(long generation, GatewayErrorKind kind)
    {
        lock (_operatorFailureLock)
        {
            _pendingOperatorFailureGeneration = generation;
            _pendingOperatorFailureKind = kind;
        }
    }

    private GatewayErrorKind? ReadOperatorFailureKind(long generation)
    {
        lock (_operatorFailureLock)
        {
            return _pendingOperatorFailureGeneration == generation
                ? _pendingOperatorFailureKind
                : null;
        }
    }

    private void ClearOperatorFailureKind(long generation)
    {
        lock (_operatorFailureLock)
        {
            if (_pendingOperatorFailureGeneration != generation)
                return;
            _pendingOperatorFailureKind = null;
        }
    }

    // Auto credential recovery clears a device token and falls back to a stronger shared/bootstrap
    // credential. Restrict that to trusted endpoints (mirrors the Mac app, which only retries
    // credentials on loopback or explicitly trusted transport): a loopback/local endpoint (traffic
    // never leaves the machine), an owned SSH tunnel (encrypted, user-configured), or a validated
    // TLS endpoint (wss/https). A plain ws:// remote endpoint is never eligible.
    private async Task<bool> IsRecoverySafeEndpointAsync(
        GatewayRecord record,
        CancellationToken cancellationToken)
    {
        if (GatewayRecordEditing.IsLoopbackEndpoint(record.Url))
        {
            if (record.IsLocal || GatewayRecordEditing.ResolveManagedDistroName(record) is not null)
            {
                if (_endpointProvenanceProbe is null)
                    return false;
                return (await _endpointProvenanceProbe(record, cancellationToken).ConfigureAwait(false)).Kind ==
                    GatewayEndpointProvenanceKind.ExpectedManagedGateway;
            }
            return true;
        }
        if (record.SshTunnel is not null)
        {
            return _tunnelManager is not null &&
                await _tunnelManager
                    .IsOwnedListenerReadyAsync(
                        record.SshTunnel,
                        record.SshTunnel.LocalPort,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(record.Url))
            return false;
        return Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<EndpointCredentialAuthorization> AuthorizeCredentialForEndpointAsync(
        GatewayRecord record,
        GatewayCredential credential,
        CancellationToken cancellationToken,
        bool requireSshTunnelOwnership = false)
    {
        if (record.SshTunnel is not null)
        {
            if (!requireSshTunnelOwnership)
                return EndpointCredentialAuthorization.AllowedResult;

            if (_tunnelManager is null ||
                !await _tunnelManager
                    .IsOwnedListenerReadyAsync(
                        record.SshTunnel,
                        record.SshTunnel.LocalPort,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new EndpointCredentialAuthorization(
                    false,
                    _tunnelManager?.IsActive == true
                        ? GatewayErrorKind.LocalPortConflict
                        : GatewayErrorKind.Network,
                    "The configured SSH listener is not owned by the active OpenClaw tunnel, so credentials were not sent.");
            }

            return EndpointCredentialAuthorization.AllowWithProof(
                EndpointOwnershipProof.ForSshTunnel(_tunnelManager.OwnershipGeneration));
        }

        var isStrongCredential =
            credential.IsBootstrapToken ||
            string.Equals(
                credential.Source,
                CredentialResolver.SourceSharedGatewayToken,
                StringComparison.Ordinal) ||
            string.Equals(
                credential.Source,
                CredentialResolver.SourceBootstrapToken,
                StringComparison.Ordinal);
        var isManagedLoopback =
            (record.IsLocal || GatewayRecordEditing.ResolveManagedDistroName(record) is not null) &&
            GatewayRecordEditing.IsLoopbackEndpoint(record.Url);
        if (!isManagedLoopback)
            return EndpointCredentialAuthorization.AllowedResult;
        if (!isStrongCredential)
        {
            // Still populate the shared provenance cache used by Chat/Dashboard, but a device token
            // does not need the stronger-credential gate.
            if (_endpointProvenanceProbe is not null)
                _ = await _endpointProvenanceProbe(record, cancellationToken).ConfigureAwait(false);
            return EndpointCredentialAuthorization.AllowedResult;
        }
        if (_endpointProvenanceProbe is null)
        {
            return new EndpointCredentialAuthorization(
                false,
                GatewayErrorKind.LocalPortConflict,
                "Managed-local endpoint ownership could not be verified, so OpenClaw did not send the shared or bootstrap token.");
        }

        var provenance = await _endpointProvenanceProbe(record, cancellationToken).ConfigureAwait(false);
        if (provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            return EndpointCredentialAuthorization.AllowWithProof(
                EndpointOwnershipProof.ForManagedGateway(provenance));

        if (provenance.Kind == GatewayEndpointProvenanceKind.NoListener)
        {
            return new EndpointCredentialAuthorization(
                false,
                GatewayErrorKind.Network,
                "The managed WSL gateway is not listening yet. Automatic repair can restart it without sending credentials.");
        }

        return new EndpointCredentialAuthorization(
            false,
            GatewayErrorKind.LocalPortConflict,
            provenance.Detail ??
                "The managed gateway address is owned by an unverified process. OpenClaw did not send the shared or bootstrap token.");
    }

    private async Task<ReconnectAuthorizationResult> AuthorizeCredentialHandoffAsync(
        GatewayRecord expectedRecord,
        GatewayCredential credential,
        EndpointOwnershipProof? expectedOwnership,
        Func<bool> isCurrentAttempt,
        CancellationToken operationCancellationToken,
        CancellationToken handshakeCancellationToken,
        string role)
    {
        using var timeoutCts = new CancellationTokenSource(_credentialHandoffTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellationToken,
            handshakeCancellationToken,
            timeoutCts.Token);

        try
        {
            if (!isCurrentAttempt())
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.Unknown,
                    $"{role} connection attempt was superseded.");
            }

            var currentRecord = _registry.GetById(expectedRecord.Id);
            if (currentRecord is null ||
                !string.Equals(_registry.ActiveGatewayId, expectedRecord.Id, StringComparison.Ordinal) ||
                !IsSameCredentialHandoffRecord(currentRecord, expectedRecord))
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    $"The active gateway endpoint, credentials, or SSH configuration changed before the {role} credential handoff.");
            }

            var authorization = await AuthorizeCredentialForEndpointAsync(
                    currentRecord,
                    credential,
                    linkedCts.Token,
                    requireSshTunnelOwnership: true)
                .ConfigureAwait(false);
            if (!isCurrentAttempt())
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.Unknown,
                    $"{role} connection attempt was superseded.");
            }

            var verifiedRecord = _registry.GetById(expectedRecord.Id);
            if (verifiedRecord is null ||
                !string.Equals(_registry.ActiveGatewayId, expectedRecord.Id, StringComparison.Ordinal) ||
                !IsSameCredentialHandoffRecord(verifiedRecord, expectedRecord))
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    $"The active gateway endpoint, credentials, or SSH configuration changed during the {role} credential handoff.");
            }

            if (authorization.Allowed &&
                expectedOwnership is not null &&
                authorization.OwnershipProof != expectedOwnership)
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    $"Endpoint ownership changed after preflight, so {role} credentials were not sent.");
            }

            return new ReconnectAuthorizationResult(
                authorization.Allowed,
                authorization.FailureKind,
                authorization.Detail);
        }
        catch (OperationCanceledException)
        {
            if (!isCurrentAttempt() || operationCancellationToken.IsCancellationRequested)
            {
                return new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.Unknown,
                    $"{role} connection attempt was superseded or canceled.");
            }

            return new ReconnectAuthorizationResult(
                false,
                GatewayErrorKind.Network,
                $"Timed out re-verifying the owned SSH listener before the {role} credential handoff.");
        }
    }

    private static bool IsSameCredentialHandoffRecord(
        GatewayRecord current,
        GatewayRecord expected) =>
        string.Equals(current.Id, expected.Id, StringComparison.Ordinal) &&
        string.Equals(current.Url, expected.Url, StringComparison.Ordinal) &&
        string.Equals(
            current.SharedGatewayToken,
            expected.SharedGatewayToken,
            StringComparison.Ordinal) &&
        string.Equals(
            current.BootstrapToken,
            expected.BootstrapToken,
            StringComparison.Ordinal) &&
        current.IsLocal == expected.IsLocal &&
        current.RequiresV2Signature == expected.RequiresV2Signature &&
        string.Equals(
            current.SetupManagedDistroName,
            expected.SetupManagedDistroName,
            StringComparison.Ordinal) &&
        current.SshTunnel == expected.SshTunnel;

    private async Task HandleHandshakeSucceededAsync(long gen)
    {
        NodeAutomaticStartPlan? nodeStartPlan = null;
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("state", "Handshake succeeded (hello-ok)");
            _stateMachine.TryTransition(ConnectionTrigger.HandshakeSucceeded);
            CompleteOperatorTelemetryAttempt(gen, "success");
            var nodeModeIntended = SyncNodeIntentFromSettings();
            _bootstrapTokenLifecycle.ResetOperatorRecoveryAfterHandshake(
                new GatewayAttemptStamp(gen, _activeGatewayRecordId));

            // Update device ID from client
            if (_activeLifecycle?.DataClient is { } client)
            {
                _stateMachine.SetOperatorDeviceId(client.OperatorDeviceId);
            }

            nodeStartPlan = _nodeConnectionCoordinator.PrepareAutomaticStart(
                gen,
                nodeModeIntended);
            if (nodeStartPlan.Disposition is
                NodeAutomaticStartDisposition.MissingActiveGateway or
                NodeAutomaticStartDisposition.MissingGatewayRecord or
                NodeAutomaticStartDisposition.MissingConnector)
            {
                _stateMachine.BlockNodeStart(nodeStartPlan.BlockDetail!);
            }
            else if (nodeStartPlan.Disposition == NodeAutomaticStartDisposition.Start)
            {
                _stateMachine.SetNodeEnabled(true);
                _stateMachine.StartNodeConnecting();
                _stateMachine.SetNodeCredentialSource(null);
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

        if (nodeStartPlan?.Disposition == NodeAutomaticStartDisposition.Start)
            await _nodeConnectionCoordinator.StartAutomaticAsync(nodeStartPlan);
    }

    private async Task HandleDeviceTokenReceivedAsync(
        DeviceTokenReceivedEventArgs token,
        GatewayAttemptStamp attempt,
        string identityPath)
    {
        var result = await _bootstrapTokenLifecycle.HandleDeviceTokenReceivedAsync(
            attempt,
            identityPath,
            token,
            CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome != DeviceTokenHandlingOutcome.IdentityLoadFailure)
            return;

        await _transitionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentGatewayAttempt(
                    attempt.LifecycleGeneration,
                    attempt.GatewayRecordId ?? string.Empty))
            {
                return;
            }

            _stateMachine.TryTransition(
                ConnectionTrigger.WebSocketError,
                DeviceIdentityLoadException.RecoveryMessage);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async Task HandleV2SignatureFallbackAsync(long gen, string gatewayRecordId)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            RememberGatewayNeedsV2Signature(
                gatewayRecordId,
                markActiveAttempt: IsCurrentGatewayAttempt(gen, gatewayRecordId));
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private void RememberGatewayNeedsV2Signature(string? gatewayRecordId, bool markActiveAttempt = true)
    {
        if (markActiveAttempt)
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
            CompleteOperatorTelemetryAttempt(
                gen,
                "pairing_required",
                ConnectionErrorCategory.PairingPending);
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
            var startResult = await _nodeConnectionCoordinator.StartAsync(
                Interlocked.Read(ref _generation));
            var startAttempted = startResult.Outcome == NodeStartOutcome.Started;

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

    private bool IsCurrentGatewayAttempt(long expectedGeneration, string expectedGatewayId) =>
        !_disposed &&
        Interlocked.Read(ref _generation) == expectedGeneration &&
        string.Equals(_activeGatewayRecordId, expectedGatewayId, StringComparison.Ordinal);

    private static string BuildIdentityFailureDetail(DeviceIdentityLoadException ex)
    {
        var cause = ex.InnerException;
        return cause == null
            ? ex.GetType().Name
            : $"{cause.GetType().Name}: {cause.Message}";
    }

    private static bool HasPersistedIdentityFailure(GatewayCredentialResolution resolution) =>
        resolution.PrimaryStatus is GatewayCredentialResolutionStatus.Unreadable
            or GatewayCredentialResolutionStatus.Corrupt
        || resolution.Status is GatewayCredentialResolutionStatus.Unreadable
            or GatewayCredentialResolutionStatus.Corrupt;

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _nodeConnectionCoordinator.HandleStatusChanged(status);
    }

    private void OnNodeTransportConnected(object? sender, EventArgs e)
    {
        _nodeConnectionCoordinator.HandleTransportConnected();
    }

    private async Task RecordOperatorCredentialHandoffFailureAsync(
        string detail,
        GatewayErrorKind failureKind,
        CancellationToken cancellationToken,
        long expectedLifecycleGeneration,
        string expectedGatewayId)
    {
        if (!IsCurrentGatewayAttempt(expectedLifecycleGeneration, expectedGatewayId))
            return;

        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentGatewayAttempt(expectedLifecycleGeneration, expectedGatewayId))
                return;

            _stateMachine.SetOperatorErrorKind(failureKind);
            _stateMachine.TryTransition(
                failureKind == GatewayErrorKind.Network
                    ? ConnectionTrigger.WebSocketError
                    : ConnectionTrigger.AuthenticationFailed,
                detail);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private void OnNodeConnectionFailure(object? sender, GatewayErrorKind errorKind)
    {
        _nodeConnectionCoordinator.HandleConnectionFailure(errorKind);
    }

    private void OnNodeDeviceTokenReceived(object? sender, DeviceTokenReceivedEventArgs e)
    {
        _nodeConnectionCoordinator.HandleDeviceTokenReceived(e);
    }

    private void OnNodePairingStatusChanged(object? sender, PairingStatusEventArgs e)
    {
        var attempt = _nodeConnectionCoordinator.CaptureCurrentAttempt();
        _nodeConnectionCoordinator.ObservePairingTelemetry(e, attempt);

        AsyncEventHandlerGuard.Run(
            () => OnNodePairingStatusChangedAsync(e, attempt),
            _logger,
            nameof(OnNodePairingStatusChanged),
            ex => _diagnostics.Record("node", "Node pairing handler failed", ex.Message));
    }

    private async Task OnNodePairingStatusChangedAsync(
        PairingStatusEventArgs e,
        NodeAttemptStamp attempt)
    {
        if (!await _nodeConnectionCoordinator.PublishPairingStatusAsync(e, attempt)
                .ConfigureAwait(false))
            return;

        _devicePairApprovalCoordinator.HandlePairingStatus(e, attempt);
    }

    GatewayAttemptStamp INodeLifecycleSource.CaptureGatewayAttempt() =>
        new(Interlocked.Read(ref _generation), _activeGatewayRecordId);

    bool INodeLifecycleSource.IsCurrentLifecycle(GatewayAttemptStamp attempt) =>
        !_disposed &&
        Interlocked.Read(ref _generation) == attempt.LifecycleGeneration &&
        string.Equals(
            _activeGatewayRecordId,
            attempt.GatewayRecordId,
            StringComparison.Ordinal);

    CancellationToken INodeLifecycleSource.GetLifecycleCancellationToken(
        GatewayAttemptStamp attempt) =>
        ((INodeLifecycleSource)this).IsCurrentLifecycle(attempt)
            ? _operationCts?.Token ?? CancellationToken.None
            : new CancellationToken(canceled: true);

    NodeConnectionTarget? INodeLifecycleSource.GetNodeConnectionTarget(
        GatewayAttemptStamp attempt)
    {
        if (!((INodeLifecycleSource)this).IsCurrentLifecycle(attempt) ||
            attempt.GatewayRecordId is null ||
            _activeIdentityPath is null)
        {
            return null;
        }

        var record = _registry.GetById(attempt.GatewayRecordId);
        return record is null
            ? null
            : new NodeConnectionTarget(
                attempt,
                record,
                _activeIdentityPath,
                _gatewayNeedsV2Signature);
    }

    bool INodeLifecycleSource.ShouldStartNodeConnection(
        NodeConnectionTarget target)
    {
        if (_shouldStartNodeConnection is not null)
            return _shouldStartNodeConnection(target.Record, target.IdentityPath);
        return _isNodeEnabled?.Invoke() ?? false;
    }

    async Task<bool> INodeConnectionStateSink.PublishNodeStartingAsync(
        NodeAttemptStamp attempt,
        CancellationToken cancellationToken)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt))
                return false;

            var before = _stateMachine.Current;
            _stateMachine.SetNodeEnabled(true);
            _stateMachine.StartNodeConnecting();
            _stateMachine.SetNodeCredentialSource(null);
            if (_stateMachine.Current != before)
                EmitStateChanged();
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    async Task<bool> INodeConnectionStateSink.PublishNodeBlockedAsync(
        NodeAttemptStamp attempt,
        string detail,
        GatewayCredentialResolution? resolution,
        bool preserveCredentialResolution,
        CancellationToken cancellationToken)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt))
                return false;

            if (resolution is not null)
                _stateMachine.SetNodeCredentialResolution(resolution);
            _stateMachine.BlockNodeStart(detail, preserveCredentialResolution);
            EmitStateChanged();
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    async Task<bool> INodeConnectionStateSink.PublishNodeCredentialResolvedAsync(
        NodeAttemptStamp attempt,
        GatewayCredentialResolution resolution,
        CancellationToken cancellationToken)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt))
                return false;

            _stateMachine.SetNodeCredentialSource(resolution.Credential?.Source);
            _stateMachine.SetNodeCredentialResolution(resolution);
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    async Task<bool> INodeConnectionStateSink.PublishNodeStatusAsync(
        NodeAttemptStamp attempt,
        ConnectionStatus status,
        NodeConnectorSnapshot connector,
        CancellationToken cancellationToken)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt))
                return false;

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
                        _stateMachine.TryTransition(
                            ConnectionTrigger.NodeError,
                            string.IsNullOrWhiteSpace(_stateMachine.Current.NodeError)
                                ? "Node transport error"
                                : _stateMachine.Current.NodeError);
                    break;
            }

            var current = _stateMachine.Current;
            if (connector.PairingStatus == PairingStatus.Pending &&
                !string.IsNullOrWhiteSpace(current.NodePairingRequestId))
            {
                _stateMachine.SetNodeInfo(
                    connector.NodeDeviceId,
                    connector.PairingStatus,
                    current.NodePairingRequestId,
                    current.NodePairingApprovalKind);
            }
            else
            {
                _stateMachine.SetNodeInfo(
                    connector.NodeDeviceId,
                    connector.PairingStatus);
            }

            EmitStateChanged();
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    async Task<bool> INodeConnectionStateSink.PublishNodePairingAsync(
        NodeAttemptStamp attempt,
        PairingStatusEventArgs pairing,
        NodeConnectorSnapshot connector,
        CancellationToken cancellationToken)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt))
                return false;

            switch (pairing.Status)
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

            _stateMachine.SetNodeInfo(
                connector.NodeDeviceId,
                connector.PairingStatus,
                pairing.RequestId,
                pairing.ApprovalKind);
            EmitStateChanged();
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    bool INodeConnectionStateSource.IsOperatorConnectedUnderAttemptLease(
        NodeAttemptStamp attempt) =>
        _nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt) &&
        _stateMachine.Current.OperatorState == RoleConnectionState.Connected;

    Task<EndpointCredentialAuthorization>
        IEndpointCredentialSecurity.AuthorizeCredentialAsync(
            GatewayRecord record,
            GatewayCredential credential,
            CancellationToken cancellationToken) =>
        AuthorizeCredentialForEndpointAsync(
            record,
            credential,
            cancellationToken,
            requireSshTunnelOwnership: true);

    Task<ReconnectAuthorizationResult>
        IEndpointCredentialSecurity.AuthorizeCredentialHandoffAsync(
            GatewayRecord expectedRecord,
            GatewayCredential credential,
            EndpointOwnershipProof? expectedOwnership,
            Func<bool> isCurrentAttempt,
            CancellationToken operationCancellationToken,
            CancellationToken handshakeCancellationToken,
            string role) =>
        AuthorizeCredentialHandoffAsync(
            expectedRecord,
            credential,
            expectedOwnership,
            isCurrentAttempt,
            operationCancellationToken,
            handshakeCancellationToken,
            role);

    Task<bool> IEndpointCredentialSecurity.IsRecoverySafeEndpointAsync(
        GatewayRecord record,
        CancellationToken cancellationToken) =>
        IsRecoverySafeEndpointAsync(record, cancellationToken);

    async Task<GatewayAttemptLease?> IGatewayAttemptLeaseSource.AcquireCurrentAttemptAsync(
        GatewayAttemptStamp attempt,
        CancellationToken cancellationToken)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (((INodeLifecycleSource)this).IsCurrentLifecycle(attempt))
            return new GatewayAttemptLease(_transitionSemaphore);

        _transitionSemaphore.Release();
        return null;
    }

    void IOperatorReconnectScheduler.ScheduleOperatorReconnect(
        OperatorReconnectRequest request)
    {
        ObserveBackgroundFault(
            ScheduleOperatorReconnectAsync(request),
            request.Reason == OperatorReconnectReason.PostBootstrapHandoff
                ? "[ConnMgr] Post-bootstrap operator reconnect failed"
                : "[ConnMgr] Operator token recovery reconnect failed");
    }

    private async Task ScheduleOperatorReconnectAsync(
        OperatorReconnectRequest request)
    {
        try
        {
            await _reconnectDelay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            if (_disposed ||
                !((INodeLifecycleSource)this).IsCurrentLifecycle(request.Attempt) ||
                request.Attempt.GatewayRecordId is null)
            {
                return;
            }

            await ReconnectIfCurrentAsync(request.Attempt.GatewayRecordId)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            var prefix = request.Reason == OperatorReconnectReason.PostBootstrapHandoff
                ? "[ConnMgr] Post-bootstrap operator reconnect failed"
                : "[ConnMgr] Operator token recovery reconnect failed";
            _logger.Warn($"{prefix}: {ex.Message}");
            if (request.Reason == OperatorReconnectReason.PostBootstrapHandoff)
            {
                _diagnostics.Record(
                    "credential",
                    "Post-bootstrap operator reconnect failed",
                    ex.Message);
            }
        }
    }

    void IV2SignatureRequirementSink.RememberGatewayNeedsV2Signature(
        string gatewayRecordId,
        bool markActiveAttempt) =>
        RememberGatewayNeedsV2Signature(gatewayRecordId, markActiveAttempt);

    OperatorApprovalGatewayLease?
        IOperatorApprovalGatewayLeaseSource.TryAcquireOperatorApprovalGateway(
            NodeAttemptStamp attempt)
    {
        if (!_nodeConnectionCoordinator.IsCurrentNodeAttempt(attempt) ||
            attempt.GatewayAttempt.GatewayRecordId is null ||
            !string.Equals(
                _activeGatewayRecordId,
                attempt.GatewayAttempt.GatewayRecordId,
                StringComparison.Ordinal) ||
            _activeLifecycle?.DataClient is not { } client)
        {
            return null;
        }

        return new OperatorApprovalGatewayLease(attempt, client);
    }

    // ─── Helpers ───

    private void EmitStateChanged()
    {
        var snapshot = _stateMachine.Current;
        RecordTelemetryStateTransitions(snapshot);
        // Always fire when any part of the snapshot changed — not just OverallState.
        // Node sub-state changes (e.g. Idle→PairingRequired) may not change OverallState
        // but the UI still needs to update.
        StateChanged?.Invoke(this, snapshot);
    }

    private void StartOperatorTelemetryAttempt(string operation, long generation)
    {
        var tags = new[]
        {
            OpenClawTelemetryTag.String(RoleTag, "operator"),
            OpenClawTelemetryTag.String(OperationTag, operation),
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, "gateway_connection")
        };
        var rootActivity = OpenClawTelemetry.StartDetachedActivity(
            operation == "connect" ? OperatorConnectSpanName : OperatorReconnectSpanName,
            tags);
        var attempt = new TelemetryAttempt(
            generation,
            operation,
            Stopwatch.GetTimestamp(),
            rootActivity)
        {
            PhaseActivity = rootActivity == null
                ? null
                : OpenClawTelemetry.StartDetachedActivity(
                    OperatorPrepareSpanName,
                    rootActivity.Context,
                    tags)
        };
        TelemetryAttempt? superseded;

        lock (_telemetryLock)
        {
            superseded = _operatorTelemetryAttempt;
            _operatorTelemetryAttempt = attempt;
        }

        if (superseded != null)
            FinishConnectionTelemetryAttempt(superseded, "operator", "superseded", null);
        OpenClawTelemetry.Add(ConnectionAttempts, tags: tags);
    }

    private void TransitionOperatorTelemetryPhase(long generation, string spanName)
    {
        TelemetryAttempt attempt;
        Activity? previousPhase;
        ActivityContext parentContext;
        string operation;
        long phaseGeneration;

        lock (_telemetryLock)
        {
            if (_operatorTelemetryAttempt is not { } active ||
                active.Generation != generation ||
                active.Activity == null)
            {
                return;
            }

            attempt = active;
            previousPhase = attempt.PhaseActivity;
            attempt.PhaseActivity = null;
            phaseGeneration = ++attempt.PhaseGeneration;
            parentContext = attempt.Activity.Context;
            operation = attempt.Operation;
        }

        FinishTelemetryActivity(previousPhase, "success", null);
        var nextPhase = OpenClawTelemetry.StartDetachedActivity(
            spanName,
            parentContext,
            [
                OpenClawTelemetryTag.String(RoleTag, "operator"),
                OpenClawTelemetryTag.String(OperationTag, operation),
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, "gateway_connection")
            ]);

        var accepted = false;
        lock (_telemetryLock)
        {
            if (ReferenceEquals(_operatorTelemetryAttempt, attempt) &&
                attempt.PhaseGeneration == phaseGeneration)
            {
                attempt.PhaseActivity = nextPhase;
                accepted = true;
            }
        }

        if (!accepted)
            FinishTelemetryActivity(nextPhase, "superseded", null);
    }

    private void CompleteOperatorTelemetryAttempt(
        long generation,
        string outcome,
        ConnectionErrorCategory? errorCategory = null)
    {
        TelemetryAttempt? attempt;
        lock (_telemetryLock)
        {
            if (_operatorTelemetryAttempt is not { } active ||
                active.Generation != generation)
                return;

            attempt = active;
            _operatorTelemetryAttempt = null;
        }

        FinishConnectionTelemetryAttempt(attempt, "operator", outcome, errorCategory);
    }

    private void CancelOperatorTelemetryAttempt(
        string outcome,
        ConnectionErrorCategory? errorCategory)
    {
        TelemetryAttempt? attempt;
        lock (_telemetryLock)
        {
            attempt = _operatorTelemetryAttempt;
            _operatorTelemetryAttempt = null;
        }

        if (attempt != null)
            FinishConnectionTelemetryAttempt(attempt, "operator", outcome, errorCategory);
    }

    private static void FinishConnectionTelemetryAttempt(
        TelemetryAttempt attempt,
        string role,
        string outcome,
        ConnectionErrorCategory? errorCategory)
    {
        var tags = new List<OpenClawTelemetryTag>
        {
            OpenClawTelemetryTag.String(RoleTag, role),
            OpenClawTelemetryTag.String(OperationTag, attempt.Operation),
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, outcome)
        };
        if (errorCategory.HasValue)
        {
            tags.Add(OpenClawTelemetryTag.String(
                OpenClawTelemetryTagKey.ErrorCategory,
                errorCategory.Value.ToString().ToLowerInvariant()));
        }
        FinishTelemetryActivity(attempt.PhaseActivity, outcome, errorCategory);
        FinishTelemetryActivity(attempt.Activity, outcome, errorCategory, tags);

        OpenClawTelemetry.Record(
            ConnectionAttemptDuration,
            Stopwatch.GetElapsedTime(attempt.StartTimestamp).TotalMilliseconds,
            tags);
    }

    private static void FinishTelemetryActivity(
        Activity? activity,
        string outcome,
        ConnectionErrorCategory? errorCategory,
        IEnumerable<OpenClawTelemetryTag>? tags = null)
    {
        if (activity == null)
            return;

        if (tags != null)
        {
            foreach (var tag in tags)
                activity.SetTag(tag.Key, tag.Value);
        }
        else
        {
            activity.SetTag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), outcome);
            if (errorCategory.HasValue)
            {
                activity.SetTag(
                    OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(),
                    errorCategory.Value.ToString().ToLowerInvariant());
            }
        }

        activity.SetStatus(
            outcome is "failure" or "pairing_rejected"
                ? ActivityStatusCode.Error
                : outcome == "success"
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Unset);
        OpenClawTelemetry.StopDetachedActivity(activity);
    }

    private void RecordTelemetryStateTransitions(GatewayConnectionSnapshot snapshot)
    {
        GatewayConnectionSnapshot previous;
        lock (_telemetryLock)
        {
            previous = _lastTelemetrySnapshot;
            _lastTelemetrySnapshot = snapshot;
        }

        RecordTelemetryStateTransition("operator", previous.OperatorState, snapshot.OperatorState);
        RecordTelemetryStateTransition("node", previous.NodeState, snapshot.NodeState);
        RecordTelemetryStateTransition("overall", previous.OverallState, snapshot.OverallState);
    }

    private static void RecordTelemetryStateTransition<TState>(
        string scope,
        TState from,
        TState to)
        where TState : struct, Enum
    {
        if (EqualityComparer<TState>.Default.Equals(from, to))
            return;

        OpenClawTelemetry.Add(
            ConnectionStateTransitions,
            tags:
            [
                OpenClawTelemetryTag.String(StateScopeTag, scope),
                OpenClawTelemetryTag.String(StateFromTag, from.ToString().ToLowerInvariant()),
                OpenClawTelemetryTag.String(StateToTag, to.ToString().ToLowerInvariant())
            ]);
    }

    private async Task DisposeActiveClientAsync()
    {
        await _nodeConnectionCoordinator.RetireAsync().ConfigureAwait(false);
        _devicePairApprovalCoordinator.Reset();

        var old = _activeLifecycle;
        _activeLifecycle = null;
        _activeGatewayRecordId = null;
        _activeSshTunnel = null;
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
        CancelOperatorTelemetryAttempt("disposed", ConnectionErrorCategory.Disposed);
        try { Volatile.Read(ref _manualSshRestartCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
        _bootstrapTokenLifecycle.Stop();
        _operationCts?.Cancel();

        // Unsubscribe from node events before disposing the semaphore
        // to prevent guarded async handlers from racing the disposed semaphore.
        if (_nodeConnector != null)
        {
            _nodeConnector.StatusChanged -= OnNodeStatusChanged;
            _nodeConnector.PairingStatusChanged -= OnNodePairingStatusChanged;
            _nodeConnector.DeviceTokenReceived -= OnNodeDeviceTokenReceived;
            if (_nodeConnector is INodeConnectorTelemetryEvents telemetryEvents)
            {
                telemetryEvents.TransportConnected -= OnNodeTransportConnected;
                telemetryEvents.ConnectionFailure -= OnNodeConnectionFailure;
            }
        }
        await _devicePairApprovalCoordinator.StopAsync().ConfigureAwait(false);
        await _nodeConnectionCoordinator.StopAsync().ConfigureAwait(false);
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

            // slopwatch-ignore: SW003 Best-effort disposal of the lifecycle lease; failure cannot improve caller state.
            try { _gatewayLifecycleLease.Dispose(); }
            catch (Exception ex) { _logger.Debug($"[ConnMgr] Dispose: lifecycle lease dispose failed: {ex.Message}"); }

            GC.SuppressFinalize(this);
        }
    }

    private sealed record TelemetryAttempt(
        long Generation,
        string Operation,
        long StartTimestamp,
        Activity? Activity)
    {
        public Activity? PhaseActivity { get; set; }
        public string? PhaseName { get; set; }
        public long PhaseGeneration { get; set; }
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
