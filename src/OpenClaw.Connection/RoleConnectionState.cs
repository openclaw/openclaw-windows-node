namespace OpenClaw.Connection;

/// <summary>
/// Per-role connection state for operator or node sub-FSM.
/// Each role maintains its own independent state.
/// </summary>
public enum RoleConnectionState
{
    Idle,
    Connecting,
    Connected,
    PairingRequired,
    /// <summary>Explicitly rejected — distinct from PairingRequired.</summary>
    PairingRejected,
    /// <summary>Temporarily throttled — will retry after cooldown.</summary>
    RateLimited,
    Error,
    /// <summary>Node mode disabled in settings.</summary>
    Disabled
}
