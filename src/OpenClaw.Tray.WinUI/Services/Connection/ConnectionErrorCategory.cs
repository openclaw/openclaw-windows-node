namespace OpenClawTray.Services.Connection;

/// <summary>
/// Error categories with explicit retry behavior.
/// </summary>
public enum ConnectionErrorCategory
{
    AuthFailure,
    PairingPending,
    PairingRejected,
    RateLimited,
    NetworkUnreachable,
    ServerClose,
    ProtocolMismatch,
    MalformedMessage,
    InternalError,
    SshTunnelFailure,
    Cancelled,
    Disposed
}
