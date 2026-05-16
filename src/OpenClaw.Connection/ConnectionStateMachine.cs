namespace OpenClaw.Connection;

/// <summary>
/// Pure-logic state machine enforcing valid operator sub-FSM transitions.
/// Not thread-safe — callers must serialize access via a semaphore.
/// Owns no I/O, no events, no async methods.
/// </summary>
internal sealed class ConnectionStateMachine
{
    public GatewayConnectionSnapshot Current { get; set; } = GatewayConnectionSnapshot.Idle;

    private RoleConnectionState _operatorState = RoleConnectionState.Idle;
    private RoleConnectionState _nodeState = RoleConnectionState.Idle;
    private string? _operatorError;
    private string? _nodeError;
    private bool _nodeEnabled;

    /// <summary>
    /// Attempt to apply a trigger to the operator or node sub-FSM.
    /// Returns true if the transition was valid, false otherwise.
    /// Updates <see cref="Current"/> on success.
    /// </summary>
    public bool TryTransition(ConnectionTrigger trigger, string? detail = null)
    {
        if (!CanTransition(trigger))
            return false;

        ApplyTransition(trigger, detail);
        RebuildSnapshot();
        return true;
    }

    /// <summary>Check whether a trigger is currently valid.</summary>
    public bool CanTransition(ConnectionTrigger trigger)
    {
        return trigger switch
        {
            // ─── Operator triggers ───
            ConnectionTrigger.ConnectRequested =>
                _operatorState is RoleConnectionState.Idle or RoleConnectionState.Error,

            ConnectionTrigger.ConnectRequestSent =>
                _operatorState == RoleConnectionState.Connecting,

            ConnectionTrigger.ChallengeReceived =>
                _operatorState == RoleConnectionState.Connecting,

            ConnectionTrigger.WebSocketConnected =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.Error,

            ConnectionTrigger.HandshakeSucceeded =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.Error,

            ConnectionTrigger.PairingPending =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.Connected or RoleConnectionState.Error,

            ConnectionTrigger.PairingApproved =>
                _operatorState == RoleConnectionState.PairingRequired,

            ConnectionTrigger.PairingRejected =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.PairingRequired,

            ConnectionTrigger.AuthenticationFailed =>
                _operatorState == RoleConnectionState.Connecting,

            ConnectionTrigger.RateLimited =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.Connected,

            ConnectionTrigger.WebSocketDisconnected =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.Connected
                    or RoleConnectionState.PairingRequired,

            ConnectionTrigger.WebSocketError =>
                _operatorState is RoleConnectionState.Connecting or RoleConnectionState.Connected,

            ConnectionTrigger.DisconnectRequested =>
                _operatorState is not RoleConnectionState.Idle,

            ConnectionTrigger.ReconnectScheduled =>
                _operatorState == RoleConnectionState.Error,

            ConnectionTrigger.ReconnectSuppressed =>
                _operatorState == RoleConnectionState.Error,

            ConnectionTrigger.Cancelled =>
                _operatorState == RoleConnectionState.Connecting,

            ConnectionTrigger.Disposed =>
                true, // from any state

            // ─── Node triggers ───
            ConnectionTrigger.NodeConnected =>
                _nodeState is RoleConnectionState.Connecting or RoleConnectionState.Idle,

            ConnectionTrigger.NodeDisconnected =>
                _nodeState is not RoleConnectionState.Idle and not RoleConnectionState.Disabled,

            ConnectionTrigger.NodePairingRequired =>
                _nodeState is RoleConnectionState.Connecting or RoleConnectionState.Connected or RoleConnectionState.Error,

            ConnectionTrigger.NodePaired =>
                _nodeState == RoleConnectionState.PairingRequired,

            ConnectionTrigger.NodePairingRejected =>
                _nodeState is RoleConnectionState.Connecting or RoleConnectionState.PairingRequired,

            ConnectionTrigger.NodeError =>
                _nodeState is not RoleConnectionState.Idle and not RoleConnectionState.Disabled,

            ConnectionTrigger.NodeRateLimited =>
                _nodeState is RoleConnectionState.Connecting or RoleConnectionState.Connected,

            _ => false
        };
    }

    /// <summary>Set node enabled/disabled. Updates snapshot.</summary>
    public void SetNodeEnabled(bool enabled)
    {
        _nodeEnabled = enabled;
        if (!enabled)
            _nodeState = RoleConnectionState.Disabled;
        else if (_nodeState == RoleConnectionState.Disabled)
            _nodeState = RoleConnectionState.Idle;
        RebuildSnapshot();
    }

    /// <summary>Reset to idle state.</summary>
    public void Reset()
    {
        _operatorState = RoleConnectionState.Idle;
        _nodeState = _nodeEnabled ? RoleConnectionState.Idle : RoleConnectionState.Disabled;
        _operatorError = null;
        _nodeError = null;
        RebuildSnapshot();
    }

    /// <summary>Start the node sub-FSM in Connecting state.</summary>
    public void StartNodeConnecting()
    {
        if (_nodeState is RoleConnectionState.Idle or RoleConnectionState.Error)
        {
            _nodeState = RoleConnectionState.Connecting;
            _nodeError = null;
            RebuildSnapshot();
        }
    }

    /// <summary>Update the operator device ID in the snapshot.</summary>
    internal void SetOperatorDeviceId(string? deviceId)
    {
        Current = Current with { OperatorDeviceId = deviceId };
    }

    /// <summary>Update node info (device ID, pairing status, optional request ID) in the snapshot.</summary>
    internal void SetNodeInfo(string? deviceId, OpenClaw.Shared.PairingStatus pairingStatus, string? pairingRequestId = null)
    {
        Current = Current with
        {
            NodeDeviceId = deviceId,
            NodePairingStatus = pairingStatus,
            NodePairingRequestId = pairingRequestId ?? Current.NodePairingRequestId
        };
    }

    /// <summary>Update the operator pairing request ID in the snapshot.</summary>
    internal void SetOperatorPairingRequestId(string? requestId)
    {
        Current = Current with { OperatorPairingRequestId = requestId };
    }

    private void ApplyTransition(ConnectionTrigger trigger, string? detail)
    {
        switch (trigger)
        {
            // ─── Operator transitions ───
            case ConnectionTrigger.ConnectRequested:
                _operatorState = RoleConnectionState.Connecting;
                _operatorError = null;
                break;

            case ConnectionTrigger.ConnectRequestSent:
            case ConnectionTrigger.ChallengeReceived:
            case ConnectionTrigger.WebSocketConnected:
                // Stay in Connecting — these are sub-steps of the connect sequence
                break;

            case ConnectionTrigger.HandshakeSucceeded:
                _operatorState = RoleConnectionState.Connected;
                _operatorError = null;
                break;

            case ConnectionTrigger.PairingPending:
                _operatorState = RoleConnectionState.PairingRequired;
                break;

            case ConnectionTrigger.PairingApproved:
                _operatorState = RoleConnectionState.Connecting;
                break;

            case ConnectionTrigger.PairingRejected:
                _operatorState = RoleConnectionState.Error;
                _operatorError = detail ?? "Pairing rejected";
                break;

            case ConnectionTrigger.AuthenticationFailed:
                _operatorState = RoleConnectionState.Error;
                _operatorError = detail ?? "Authentication failed";
                break;

            case ConnectionTrigger.RateLimited:
                _operatorState = RoleConnectionState.Error;
                _operatorError = detail ?? "Rate limited";
                break;

            case ConnectionTrigger.WebSocketDisconnected:
                if (_operatorState == RoleConnectionState.PairingRequired)
                {
                    // Gateway closes WebSocket after PAIRING_REQUIRED — stay in PairingRequired
                    // (don't transition to Error; user needs to approve then reconnect)
                }
                else
                {
                    _operatorState = RoleConnectionState.Connecting;
                    _operatorError = null;
                }
                break;

            case ConnectionTrigger.WebSocketError:
                _operatorState = RoleConnectionState.Error;
                _operatorError = detail ?? "WebSocket error";
                break;

            case ConnectionTrigger.DisconnectRequested:
            case ConnectionTrigger.Disposed:
                _operatorState = RoleConnectionState.Idle;
                _nodeState = _nodeEnabled ? RoleConnectionState.Idle : RoleConnectionState.Disabled;
                _operatorError = null;
                _nodeError = null;
                break;

            case ConnectionTrigger.ReconnectScheduled:
                _operatorState = RoleConnectionState.Connecting;
                _operatorError = null;
                break;

            case ConnectionTrigger.ReconnectSuppressed:
                // No-op; stay in Error
                break;

            case ConnectionTrigger.Cancelled:
                _operatorState = RoleConnectionState.Idle;
                _operatorError = null;
                break;

            // ─── Node transitions ───
            case ConnectionTrigger.NodeConnected:
                _nodeState = RoleConnectionState.Connected;
                _nodeError = null;
                break;

            case ConnectionTrigger.NodeDisconnected:
                _nodeState = RoleConnectionState.Idle;
                _nodeError = null;
                break;

            case ConnectionTrigger.NodePairingRequired:
                _nodeState = RoleConnectionState.PairingRequired;
                break;

            case ConnectionTrigger.NodePaired:
                _nodeState = RoleConnectionState.Connected;
                _nodeError = null;
                break;

            case ConnectionTrigger.NodePairingRejected:
                _nodeState = RoleConnectionState.PairingRejected;
                _nodeError = detail ?? "Node pairing rejected";
                break;

            case ConnectionTrigger.NodeError:
                _nodeState = RoleConnectionState.Error;
                _nodeError = detail ?? "Node error";
                break;

            case ConnectionTrigger.NodeRateLimited:
                _nodeState = RoleConnectionState.RateLimited;
                _nodeError = detail ?? "Node rate limited";
                break;
        }
    }

    private void RebuildSnapshot()
    {
        Current = Current with
        {
            OverallState = GatewayConnectionSnapshot.DeriveOverall(_operatorState, _nodeState, _nodeEnabled),
            OperatorState = _operatorState,
            OperatorError = _operatorError,
            OperatorPairingRequired = _operatorState == RoleConnectionState.PairingRequired,
            // Clear requestId when no longer in PairingRequired to prevent stale reads
            OperatorPairingRequestId = _operatorState == RoleConnectionState.PairingRequired
                ? Current.OperatorPairingRequestId : null,
            NodeState = _nodeState,
            NodeError = _nodeError,
            // Clear requestId when no longer in PairingRequired to prevent stale reads
            NodePairingRequestId = _nodeState == RoleConnectionState.PairingRequired
                ? Current.NodePairingRequestId : null,
            NodePairingStatus = _nodeState switch
            {
                RoleConnectionState.PairingRequired => OpenClaw.Shared.PairingStatus.Pending,
                RoleConnectionState.PairingRejected => OpenClaw.Shared.PairingStatus.Rejected,
                RoleConnectionState.Connected => OpenClaw.Shared.PairingStatus.Paired,
                _ => OpenClaw.Shared.PairingStatus.Unknown
            }
        };
    }
}
