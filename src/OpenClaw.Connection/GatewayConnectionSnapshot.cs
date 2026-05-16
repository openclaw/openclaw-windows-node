namespace OpenClaw.Connection;

/// <summary>
/// Immutable, cross-thread-safe representation of the entire connection
/// state at a point in time. Safe to cache, compare, and pass between threads.
/// </summary>
public sealed record GatewayConnectionSnapshot
{
    // ─── Overall ───
    public OverallConnectionState OverallState { get; init; }

    // ─── Operator ───
    public RoleConnectionState OperatorState { get; init; }
    public string? OperatorError { get; init; }
    public bool OperatorPairingRequired { get; init; }
    public string? OperatorDeviceId { get; init; }
    /// <summary>
    /// The requestId returned by the gateway when operator pairing is required.
    /// Used by setup flows to approve the specific pairing request via CLI.
    /// </summary>
    public string? OperatorPairingRequestId { get; init; }

    // ─── Node ───
    public RoleConnectionState NodeState { get; init; }
    public string? NodeError { get; init; }
    public OpenClaw.Shared.PairingStatus NodePairingStatus { get; init; }
    public string? NodeDeviceId { get; init; }
    /// <summary>
    /// The requestId returned by the gateway when node pairing is required.
    /// Used by the connection page to show the correct approval command.
    /// </summary>
    public string? NodePairingRequestId { get; init; }

    // ─── Gateway ───
    public string? GatewayId { get; init; }
    public string? GatewayUrl { get; init; }
    public string? GatewayName { get; init; }

    // ─── Derived ───
    public bool IsFullyConnected =>
        OperatorState == RoleConnectionState.Connected &&
        NodeState == RoleConnectionState.Connected;

    public static GatewayConnectionSnapshot Idle { get; } = new()
    {
        OverallState = OverallConnectionState.Idle,
        OperatorState = RoleConnectionState.Idle,
        NodeState = RoleConnectionState.Idle,
        NodePairingStatus = OpenClaw.Shared.PairingStatus.Unknown
    };

    /// <summary>
    /// Derive the overall connection state from operator and node sub-states.
    /// </summary>
    public static OverallConnectionState DeriveOverall(
        RoleConnectionState op, RoleConnectionState node, bool nodeEnabled)
    {
        if (op == RoleConnectionState.Error)
            return OverallConnectionState.Error;

        if (op == RoleConnectionState.PairingRequired)
            return OverallConnectionState.PairingRequired;

        if (op == RoleConnectionState.Connecting)
            return OverallConnectionState.Connecting;

        // From here, operator is Connected.

        if (op == RoleConnectionState.Connected && nodeEnabled &&
            (node == RoleConnectionState.Error ||
             node == RoleConnectionState.PairingRejected ||
             node == RoleConnectionState.RateLimited))
            return OverallConnectionState.Degraded;

        if (op == RoleConnectionState.Connected &&
            node == RoleConnectionState.PairingRequired)
            return OverallConnectionState.PairingRequired;

        if (op == RoleConnectionState.Connected &&
            nodeEnabled && node == RoleConnectionState.Connecting)
            return OverallConnectionState.Connecting;

        if (op == RoleConnectionState.Connected &&
            (node == RoleConnectionState.Connected || !nodeEnabled ||
             node == RoleConnectionState.Disabled))
            return OverallConnectionState.Ready;

        if (op == RoleConnectionState.Connected)
            return OverallConnectionState.Connected;

        return OverallConnectionState.Idle;
    }
}
