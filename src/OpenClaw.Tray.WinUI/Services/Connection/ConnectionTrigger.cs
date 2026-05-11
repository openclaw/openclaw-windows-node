namespace OpenClawTray.Services.Connection;

/// <summary>
/// Triggers that drive operator and node sub-FSM transitions.
/// </summary>
public enum ConnectionTrigger
{
    // ─── Operator lifecycle ───
    ConnectRequested,
    ConnectRequestSent,
    ChallengeReceived,
    WebSocketConnected,
    HandshakeSucceeded,
    PairingPending,
    PairingApproved,
    PairingRejected,
    AuthenticationFailed,
    RateLimited,
    WebSocketDisconnected,
    WebSocketError,
    DisconnectRequested,
    ReconnectScheduled,
    ReconnectSuppressed,
    Cancelled,
    Disposed,

    // ─── Node lifecycle (independent sub-FSM) ───
    NodeConnected,
    NodeDisconnected,
    NodePairingRequired,
    NodePaired,
    NodePairingRejected,
    NodeError,
    NodeRateLimited
}
