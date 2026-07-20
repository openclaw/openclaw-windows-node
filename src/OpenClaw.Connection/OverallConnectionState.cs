namespace OpenClaw.Connection;

/// <summary>
/// Overall derived connection state combining operator and node sub-states.
/// This is NOT a state machine — it's derived from the two role sub-FSMs.
/// </summary>
public enum OverallConnectionState
{
    /// <summary>No gateway configured or selected.</summary>
    Idle,

    /// <summary>At least one role is connecting.</summary>
    Connecting,

    /// <summary>Operator connected (node may still be connecting).</summary>
    Connected,

    /// <summary>Both operator and node connected and paired.</summary>
    Ready,

    /// <summary>Operator connected but node in error/rejected (functional but impaired).</summary>
    Degraded,

    /// <summary>One or both roles need pairing approval.</summary>
    PairingRequired,

    /// <summary>Unrecoverable error state (operator down).</summary>
    Error,

    /// <summary>Teardown in progress.</summary>
    Disconnecting
}
