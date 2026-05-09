namespace OpenClaw.Shared;

/// <summary>
/// Factory for creating gateway operator clients. Enables testing without real WebSockets.
/// </summary>
public interface IGatewayClientFactory
{
    OpenClawGatewayClient Create(string gatewayUrl, string token, IOpenClawLogger logger, bool tokenIsBootstrapToken = false, string? identityPath = null);
}

/// <summary>
/// Default factory that creates real <see cref="OpenClawGatewayClient"/> instances.
/// </summary>
public sealed class DefaultGatewayClientFactory : IGatewayClientFactory
{
    public OpenClawGatewayClient Create(string gatewayUrl, string token, IOpenClawLogger logger, bool tokenIsBootstrapToken = false, string? identityPath = null)
        => new(gatewayUrl, token, logger, tokenIsBootstrapToken, identityPath: identityPath);
}

/// <summary>
/// Adapter for node service connection. Implemented in the WinUI layer
/// since NodeService depends on WinUI types (DispatcherQueue, FrameworkElement).
/// </summary>
public interface INodeConnector
{
    bool IsEnabled { get; }
    Task ConnectAsync(string gatewayUrl, string? nodeToken, string? bootstrapToken, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}

/// <summary>
/// Unified gateway connection service. Single owner of the operator client lifecycle,
/// credential resolution, and connection state machine.
///
/// All 3 flows (WSL setup, manual onboarding, connection page) call the same API.
/// Token storage goes through GatewayRegistry exclusively.
/// </summary>
public sealed class GatewayConnectionService : IDisposable
{
    private readonly GatewayRegistry _registry;
    private readonly IGatewayClientFactory _clientFactory;
    private readonly IOpenClawLogger _logger;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);

    private OpenClawGatewayClient? _operatorClient;
    private int _generation; // Stale-event guard
    private CancellationTokenSource? _connectCts;

    // Role states
    private GatewayRoleState _operatorState = GatewayRoleState.Idle;
    private GatewayRoleState _nodeState = GatewayRoleState.Disabled;
    private string? _lastError;
    private string? _pairingRequestId;
    private GatewayRole? _pairingRole;

    /// <summary>Current connection snapshot for UI consumption.</summary>
    public GatewayConnectionSnapshot Snapshot => BuildSnapshot();

    /// <summary>The active operator client, if connected. Do NOT manage its lifetime externally.</summary>
    public OpenClawGatewayClient? OperatorClient => _operatorClient;

    /// <summary>Fired on every state transition. Args: (oldState, newState, snapshot).</summary>
    public event Action<GatewayConnectionState, GatewayConnectionState, GatewayConnectionSnapshot>? StateChanged;

    /// <summary>
    /// Re-fired from the operator client for consumers that need raw gateway events
    /// without subscribing to a mutable client reference.
    /// </summary>
    public event EventHandler<ConnectionStatus>? OperatorStatusChanged;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    public event EventHandler<OpenClawNotification>? NotificationReceived;
    public event EventHandler<AgentActivity>? ActivityChanged;

    public GatewayConnectionService(
        GatewayRegistry registry,
        IGatewayClientFactory? clientFactory = null,
        IOpenClawLogger? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clientFactory = clientFactory ?? new DefaultGatewayClientFactory();
        _logger = logger ?? NullLogger.Instance;
    }

    // ── Configure ──

    /// <summary>
    /// Configure the active gateway from a decoded setup code.
    /// Caller decodes the raw setup code (SetupCodeDecoder is in the Tray layer).
    /// Clears stored device tokens for fresh pairing.
    /// </summary>
    public bool ApplySetupCode(string url, string? bootstrapToken, string? dataPath = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var id = GatewayRecord.GenerateId(url);
        _registry.Remove(id);
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = id,
            Url = url,
            BootstrapToken = bootstrapToken,
        });

        // Clear stale identity in the per-gateway directory so a fresh keypair
        // is generated for this gateway (it may have been reinstalled).
        var gwIdentityPath = Path.Combine(GatewayRecord.BaseIdentityDir, "gateways", id);
        DeviceIdentity.TryClearStoredDeviceToken(gwIdentityPath);

        SetRoleState(GatewayRole.Operator, GatewayRoleState.Idle);
        return true;
    }

    /// <summary>
    /// Set credentials directly (e.g., shared gateway token from WSL setup, or manual token entry).
    /// </summary>
    public void SetCredential(string url, string token, GatewayCredentialKind kind)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return;

        var id = GatewayRecord.GenerateId(url);
        var existing = _registry.GetAll().FirstOrDefault(g => g.Id == id);
        var record = existing?.Clone() ?? new GatewayRecord { Id = id, Url = url };

        switch (kind)
        {
            case GatewayCredentialKind.SharedGatewayToken:
                record.SharedGatewayToken = token;
                break;
            case GatewayCredentialKind.BootstrapToken:
                record.BootstrapToken = token;
                break;
            case GatewayCredentialKind.OperatorDeviceToken:
                record.OperatorDeviceToken = token;
                record.BootstrapToken = null;
                break;
            case GatewayCredentialKind.NodeDeviceToken:
                record.NodeDeviceToken = token;
                break;
        }

        _registry.AddOrUpdate(record);
    }

    // ── Connect ──

    /// <summary>
    /// Connect the operator client using credentials from the active gateway record.
    /// </summary>
    public async Task ConnectOperatorAsync(CancellationToken ct = default)
    {
        await _transitionLock.WaitAsync(ct);
        try
        {
            var activeGw = _registry.GetActive();
            if (activeGw == null)
            {
                _logger.Warn("[ConnectionService] No active gateway — cannot connect");
                return;
            }

            // Resolve credential: SharedGatewayToken → BootstrapToken → stored device token
            string? effectiveToken = null;
            var isBootstrap = false;

            if (!string.IsNullOrWhiteSpace(activeGw.SharedGatewayToken))
            {
                effectiveToken = activeGw.SharedGatewayToken;
            }
            else if (!string.IsNullOrWhiteSpace(activeGw.BootstrapToken))
            {
                effectiveToken = activeGw.BootstrapToken;
                isBootstrap = true;
            }

            // Fall through to stored device token in per-gateway identity file
            if (string.IsNullOrWhiteSpace(effectiveToken))
            {
                var storedToken = DeviceIdentity.TryReadStoredDeviceToken(activeGw.IdentityPath);
                if (!string.IsNullOrWhiteSpace(storedToken))
                    effectiveToken = storedToken;
            }

            if (string.IsNullOrWhiteSpace(effectiveToken))
            {
                _logger.Info("[ConnectionService] No credential available — skipping connect");
                return;
            }

            var gatewayUrl = activeGw.Url;
            if (string.IsNullOrWhiteSpace(gatewayUrl)) return;

            // Cancel previous connect attempt
            _connectCts?.Cancel();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Dispose old client
            UnsubscribeAndDispose();

            // Bump generation to ignore stale events
            var gen = Interlocked.Increment(ref _generation);

            SetRoleState(GatewayRole.Operator, GatewayRoleState.Connecting);

            _operatorClient = _clientFactory.Create(gatewayUrl, effectiveToken, _logger, isBootstrap, identityPath: activeGw.IdentityPath);
            SubscribeClientEvents(_operatorClient, gen);

            _logger.Info($"[ConnectionService] Connecting operator to {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)} (bootstrap={isBootstrap})");
            await _operatorClient.ConnectAsync();
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <summary>
    /// Connect the node service via the injected adapter.
    /// </summary>
    public async Task ConnectNodeAsync(INodeConnector node, CancellationToken ct = default)
    {
        if (!node.IsEnabled)
        {
            SetRoleState(GatewayRole.Node, GatewayRoleState.Disabled);
            return;
        }

        var activeGw = _registry.GetActive();
        if (activeGw == null) return;

        var nodeToken = activeGw.NodeDeviceToken ?? activeGw.OperatorDeviceToken ?? "";
        SetRoleState(GatewayRole.Node, GatewayRoleState.Connecting);
        await node.ConnectAsync(activeGw.Url, nodeToken, activeGw.BootstrapToken, ct);
    }

    // ── Pairing ──

    /// <summary>
    /// Approve a pending pairing. Pass an approver callback for automated flows (WSL), null for manual.
    /// The callback receives the optional requestId and should perform the approval.
    /// </summary>
    public async Task ApproveAsync(GatewayRole role, string? requestId, Func<string?, CancellationToken, Task>? approver, CancellationToken ct = default)
    {
        if (approver == null)
        {
            _logger.Info($"[ConnectionService] Manual approval required for {role} (requestId={requestId})");
            return; // User must approve via CLI
        }

        _logger.Info($"[ConnectionService] Auto-approving {role} (requestId={requestId})");
        await approver(requestId, ct);
    }

    // ── Token persistence (called by event handlers) ──

    /// <summary>
    /// Store an operator device token received from the gateway (hello-ok handoff or pairing event).
    /// </summary>
    public void StoreOperatorDeviceToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var gw = _registry.GetActive();
        if (gw == null) return;

        gw.OperatorDeviceToken = token;
        gw.BootstrapToken = null;
        _registry.AddOrUpdate(gw);
        _logger.Info("[ConnectionService] Stored operator device token");
    }

    /// <summary>
    /// Store a node device token.
    /// </summary>
    public void StoreNodeDeviceToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var gw = _registry.GetActive();
        if (gw == null) return;

        gw.NodeDeviceToken = token;
        _registry.AddOrUpdate(gw);
        _logger.Info("[ConnectionService] Stored node device token");
    }

    // ── Lifecycle ──

    public async Task DisconnectAsync()
    {
        await _transitionLock.WaitAsync();
        try
        {
            _connectCts?.Cancel();
            UnsubscribeAndDispose();
            SetRoleState(GatewayRole.Operator, GatewayRoleState.Idle);
            SetRoleState(GatewayRole.Node, GatewayRoleState.Disabled);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync();
        await ConnectOperatorAsync(ct);
    }

    public void Dispose()
    {
        _connectCts?.Cancel();
        UnsubscribeAndDispose();
        _transitionLock.Dispose();
    }

    // ── Chat credential resolution ──

    /// <summary>
    /// Resolve the best token for web dashboard (chat) auth.
    /// Precedence: SharedGatewayToken → OperatorDeviceToken.
    /// </summary>
    public (string url, string token, string source)? ResolveChatCredentials()
    {
        var gw = _registry.GetActive();
        if (gw == null || string.IsNullOrWhiteSpace(gw.Url)) return null;

        if (!string.IsNullOrWhiteSpace(gw.SharedGatewayToken))
            return (gw.Url, gw.SharedGatewayToken, "shared");
        if (!string.IsNullOrWhiteSpace(gw.OperatorDeviceToken))
            return (gw.Url, gw.OperatorDeviceToken, "operator");
        return null;
    }

    // ── Internal ──

    private void SetRoleState(GatewayRole role, GatewayRoleState newState)
    {
        var oldOverall = BuildSnapshot().Overall;

        if (role == GatewayRole.Operator) _operatorState = newState;
        else _nodeState = newState;

        var snapshot = BuildSnapshot();
        // Fire on every role state change so UI always reflects current state
        StateChanged?.Invoke(oldOverall, snapshot.Overall, snapshot);
    }

    private GatewayConnectionSnapshot BuildSnapshot()
    {
        var gw = _registry.GetActive();
        var hasCredential = gw != null &&
            (!string.IsNullOrWhiteSpace(gw.OperatorDeviceToken) ||
             !string.IsNullOrWhiteSpace(gw.BootstrapToken) ||
             !string.IsNullOrWhiteSpace(gw.SharedGatewayToken));

        return new GatewayConnectionSnapshot
        {
            Overall = GatewayConnectionSnapshot.DeriveOverall(_operatorState, _nodeState, hasCredential),
            Operator = _operatorState,
            Node = _nodeState,
            GatewayUrl = gw?.Url,
            LastError = _lastError,
            PairingRequestId = _pairingRequestId,
            PairingRole = _pairingRole,
        };
    }

    private void SubscribeClientEvents(OpenClawGatewayClient client, int gen)
    {
        client.StatusChanged += (s, status) =>
        {
            if (_generation != gen) return; // Stale client
            HandleOperatorStatus(status, client);
        };

        client.AuthenticationFailed += (s, msg) =>
        {
            if (_generation != gen) return;
            _lastError = msg;
            if (client.IsPairingRequired)
            {
                _pairingRequestId = client.PairingRequiredRequestId;
                _pairingRole = GatewayRole.Operator;
                SetRoleState(GatewayRole.Operator, GatewayRoleState.PairingRequired);
            }
            else
            {
                SetRoleState(GatewayRole.Operator, GatewayRoleState.Error);
            }
        };

        // Re-fire events through stable surface
        client.SessionsUpdated += (s, e) => { if (_generation == gen) SessionsUpdated?.Invoke(s, e); };
        client.NodesUpdated += (s, e) => { if (_generation == gen) NodesUpdated?.Invoke(s, e); };
        client.GatewaySelfUpdated += (s, e) => { if (_generation == gen) GatewaySelfUpdated?.Invoke(s, e); };
        client.ChannelHealthUpdated += (s, e) => { if (_generation == gen) ChannelHealthUpdated?.Invoke(s, e); };
        client.NotificationReceived += (s, e) => { if (_generation == gen) NotificationReceived?.Invoke(s, e); };
        client.ActivityChanged += (s, e) => { if (_generation == gen) ActivityChanged?.Invoke(s, e); };
    }

    private void HandleOperatorStatus(ConnectionStatus status, OpenClawGatewayClient client)
    {
        OperatorStatusChanged?.Invoke(client, status);

        switch (status)
        {
            case ConnectionStatus.Connected:
                _lastError = null;

                // Persist operator device token from bootstrap handoff
                var opToken = client.OperatorDeviceToken;
                if (!string.IsNullOrWhiteSpace(opToken))
                    StoreOperatorDeviceToken(opToken);

                SetRoleState(GatewayRole.Operator, GatewayRoleState.Connected);
                break;

            case ConnectionStatus.Connecting:
                SetRoleState(GatewayRole.Operator, GatewayRoleState.Connecting);
                break;

            case ConnectionStatus.Error:
                if (client.IsPairingRequired)
                {
                    _pairingRequestId = client.PairingRequiredRequestId;
                    _pairingRole = GatewayRole.Operator;
                    SetRoleState(GatewayRole.Operator, GatewayRoleState.PairingRequired);
                }
                else
                {
                    SetRoleState(GatewayRole.Operator, GatewayRoleState.Error);
                }
                break;

            case ConnectionStatus.Disconnected:
                SetRoleState(GatewayRole.Operator, GatewayRoleState.Idle);
                break;
        }
    }

    private void UnsubscribeAndDispose()
    {
        // Bump generation so any lingering event handlers are ignored
        Interlocked.Increment(ref _generation);
        _operatorClient?.Dispose();
        _operatorClient = null;
    }
}
