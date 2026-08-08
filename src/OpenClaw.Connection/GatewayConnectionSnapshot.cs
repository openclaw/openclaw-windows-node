namespace OpenClaw.Connection;

public enum GatewayProtocolCompatibilityRole
{
    Operator,
    Node
}

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
    public OpenClaw.Shared.GatewayErrorKind? OperatorErrorKind { get; init; }
    public bool OperatorPairingRequired { get; init; }
    public string? OperatorDeviceId { get; init; }
    public string? OperatorCredentialSource { get; init; }
    public GatewayCredentialResolutionStatus? OperatorCredentialStatus { get; init; }
    public bool OperatorCredentialFallbackUsed { get; init; }
    public bool OperatorCredentialBootstrapRequired { get; init; }
    public string? OperatorCredentialDetail { get; init; }
    public OpenClaw.Shared.GatewayProtocolCompatibility OperatorProtocolCompatibility { get; init; } =
        OpenClaw.Shared.GatewayProtocolCompatibility.Unknown;
    /// <summary>
    /// The requestId returned by the gateway when operator pairing is required.
    /// Used by setup flows to approve the specific pairing request via CLI.
    /// </summary>
    public string? OperatorPairingRequestId { get; init; }

    // ─── Node ───
    public bool NodeConnectionIntended { get; init; }
    public RoleConnectionState NodeState { get; init; }
    public string? NodeError { get; init; }
    public OpenClaw.Shared.GatewayErrorKind? NodeErrorKind { get; init; }
    public OpenClaw.Shared.PairingStatus NodePairingStatus { get; init; }
    public string? NodeDeviceId { get; init; }
    public string? NodeCredentialSource { get; init; }
    public GatewayCredentialResolutionStatus? NodeCredentialStatus { get; init; }
    public bool NodeCredentialFallbackUsed { get; init; }
    public bool NodeCredentialBootstrapRequired { get; init; }
    public string? NodeCredentialDetail { get; init; }
    public OpenClaw.Shared.GatewayProtocolCompatibility NodeProtocolCompatibility { get; init; } =
        OpenClaw.Shared.GatewayProtocolCompatibility.Unknown;
    /// <summary>
    /// The requestId returned by the gateway when node pairing is required.
    /// Used by the connection page to show the correct approval command.
    /// </summary>
    public string? NodePairingRequestId { get; init; }
    public OpenClaw.Shared.PairingApprovalKind NodePairingApprovalKind { get; init; }

    // ─── Gateway ───
    public string? GatewayId { get; init; }
    public string? GatewayUrl { get; init; }
    public string? GatewayName { get; init; }

    // ─── Derived ───
    public OpenClaw.Shared.GatewayProtocolCompatibility ProtocolCompatibility { get; init; } =
        OpenClaw.Shared.GatewayProtocolCompatibility.Unknown;
    public GatewayProtocolCompatibilityRole? ProtocolCompatibilityRole { get; init; }

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

    internal static (
        OpenClaw.Shared.GatewayProtocolCompatibility Compatibility,
        GatewayProtocolCompatibilityRole? Role)
        DeriveProtocolCompatibility(
            OpenClaw.Shared.GatewayProtocolCompatibility operatorCompatibility,
            OpenClaw.Shared.GatewayProtocolCompatibility nodeCompatibility,
            bool nodeEnabled)
    {
        if (operatorCompatibility.IsMismatch)
            return (operatorCompatibility, GatewayProtocolCompatibilityRole.Operator);
        if (nodeEnabled && nodeCompatibility.IsMismatch)
            return (nodeCompatibility, GatewayProtocolCompatibilityRole.Node);
        if (operatorCompatibility.State == OpenClaw.Shared.GatewayProtocolCompatibilityState.Compatible)
            return (operatorCompatibility, GatewayProtocolCompatibilityRole.Operator);
        if (nodeEnabled && nodeCompatibility.State == OpenClaw.Shared.GatewayProtocolCompatibilityState.Compatible)
            return (nodeCompatibility, GatewayProtocolCompatibilityRole.Node);
        return (OpenClaw.Shared.GatewayProtocolCompatibility.Unknown, null);
    }

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

        if (op != RoleConnectionState.Connected && nodeEnabled)
        {
            if (node == RoleConnectionState.PairingRequired)
                return OverallConnectionState.PairingRequired;

            if (node == RoleConnectionState.Connecting)
                return OverallConnectionState.Connecting;

            if (node == RoleConnectionState.Connected)
                return OverallConnectionState.Connected;

            if (node is RoleConnectionState.Error or RoleConnectionState.PairingRejected or RoleConnectionState.RateLimited)
                return OverallConnectionState.Error;
        }

        // From here, operator is Connected.

        if (op == RoleConnectionState.Connected && nodeEnabled &&
            (node == RoleConnectionState.Error ||
             node == RoleConnectionState.Idle ||
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
