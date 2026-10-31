namespace OpenClaw.Shared;

public enum GatewayHandshakePhase
{
    TransportConnecting,
    TransportConnected,
    ChallengeReceived,
    AuthorizationStarted,
    AuthorizationSucceeded,
    AuthorizationDenied,
    ReconnectAuthorizationStarted,
    ReconnectAuthorizationSucceeded,
    ReconnectAuthorizationDenied,
    ConnectSending,
    ConnectAccepted,
    ConnectRejected,
    StartupUnavailable,
    PairingRequired,
    Disconnected,
    Failed
}

/// <summary>
/// Safe local timing only. TransportGeneration identifies even rejected pre-auth sockets,
/// unlike the successful server-handshake generation. No wire data or exception text is carried.
/// </summary>
public sealed record GatewayHandshakeObservation(
    long TransportGeneration,
    GatewayHandshakePhase Phase,
    double ElapsedMs,
    double PhaseElapsedMs,
    GatewayErrorKind? FailureKind = null,
    int? RetryAfterMs = null);
