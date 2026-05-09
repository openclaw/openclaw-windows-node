namespace OpenClaw.Shared;

/// <summary>
/// Connection state for a single role (operator or node).
/// </summary>
public enum GatewayRoleState
{
    /// <summary>Not configured or disabled.</summary>
    Idle,
    /// <summary>WebSocket handshake in progress.</summary>
    Connecting,
    /// <summary>Authenticated and receiving data.</summary>
    Connected,
    /// <summary>Gateway requires device approval before connecting.</summary>
    PairingRequired,
    /// <summary>Connection failed.</summary>
    Error,
    /// <summary>Role is not enabled (node mode off).</summary>
    Disabled,
}

/// <summary>
/// Overall connection state derived from operator + node role states.
/// </summary>
public enum GatewayConnectionState
{
    /// <summary>No gateway configured.</summary>
    Idle,
    /// <summary>Gateway URL + credential stored, not connected.</summary>
    Configured,
    /// <summary>At least one role is connecting.</summary>
    Connecting,
    /// <summary>All enabled roles are connected.</summary>
    Ready,
    /// <summary>At least one role requires pairing approval.</summary>
    PairingRequired,
    /// <summary>At least one role has an error.</summary>
    Error,
}

/// <summary>
/// Which role a pairing/connection event applies to.
/// </summary>
public enum GatewayRole
{
    Operator,
    Node,
}

/// <summary>
/// What kind of credential is being stored.
/// </summary>
public enum GatewayCredentialKind
{
    /// <summary>Gateway's shared secret (gateway.auth.token). Used for web dashboard auth.</summary>
    SharedGatewayToken,
    /// <summary>Single-use token from setup code for pairing.</summary>
    BootstrapToken,
    /// <summary>Per-device credential for operator WebSocket auth.</summary>
    OperatorDeviceToken,
    /// <summary>Per-device credential for node WebSocket auth.</summary>
    NodeDeviceToken,
}

/// <summary>
/// Snapshot of the current connection state for UI consumption.
/// </summary>
public sealed record GatewayConnectionSnapshot
{
    public GatewayConnectionState Overall { get; init; }
    public GatewayRoleState Operator { get; init; }
    public GatewayRoleState Node { get; init; }
    public string? GatewayUrl { get; init; }
    public string? LastError { get; init; }
    public string? PairingRequestId { get; init; }
    public GatewayRole? PairingRole { get; init; }

    public static GatewayConnectionState DeriveOverall(
        GatewayRoleState operatorState,
        GatewayRoleState nodeState,
        bool hasCredential)
    {
        if (operatorState == GatewayRoleState.Error || nodeState == GatewayRoleState.Error)
            return GatewayConnectionState.Error;
        if (operatorState == GatewayRoleState.PairingRequired || nodeState == GatewayRoleState.PairingRequired)
            return GatewayConnectionState.PairingRequired;
        if (operatorState == GatewayRoleState.Connecting || nodeState == GatewayRoleState.Connecting)
            return GatewayConnectionState.Connecting;

        var opReady = operatorState == GatewayRoleState.Connected;
        var nodeReady = nodeState is GatewayRoleState.Connected or GatewayRoleState.Disabled;
        if (opReady && nodeReady)
            return GatewayConnectionState.Ready;

        if (hasCredential)
            return GatewayConnectionState.Configured;

        return GatewayConnectionState.Idle;
    }
}
