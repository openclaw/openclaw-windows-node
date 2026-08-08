namespace OpenClaw.Shared;

public enum GatewayProtocolCompatibilityState
{
    Unknown,
    Compatible,
    GatewayTooOld,
    GatewayTooNew,
    Mismatch
}

/// <summary>
/// Sanitized Gateway wire-protocol compatibility details safe for state,
/// diagnostics, and low-cardinality telemetry.
/// </summary>
public sealed record GatewayProtocolCompatibility
{
    public required GatewayProtocolCompatibilityState State { get; init; }
    public int ClientMinimumProtocol { get; init; } = GatewayProtocolContract.MinimumSupportedVersion;
    public int ClientMaximumProtocol { get; init; } = GatewayProtocolContract.MaximumSupportedVersion;
    public int? SelectedProtocol { get; init; }
    public int? GatewayExpectedProtocol { get; init; }
    public int? GatewayMinimumProtocol { get; init; }
    public bool Retryable { get; init; }

    public bool IsMismatch =>
        State is GatewayProtocolCompatibilityState.GatewayTooOld
            or GatewayProtocolCompatibilityState.GatewayTooNew
            or GatewayProtocolCompatibilityState.Mismatch;

    public int? GatewayProtocol => SelectedProtocol ?? GatewayExpectedProtocol;

    public string NormalizedState => State switch
    {
        GatewayProtocolCompatibilityState.Compatible => "compatible",
        GatewayProtocolCompatibilityState.GatewayTooOld => "gateway_too_old",
        GatewayProtocolCompatibilityState.GatewayTooNew => "gateway_too_new",
        GatewayProtocolCompatibilityState.Mismatch => "mismatch",
        _ => "unknown"
    };

    public static GatewayProtocolCompatibility Unknown { get; } = new()
    {
        State = GatewayProtocolCompatibilityState.Unknown,
        Retryable = true
    };

    public static GatewayProtocolCompatibility Compatible(int protocol) => new()
    {
        State = GatewayProtocolCompatibilityState.Compatible,
        SelectedProtocol = protocol,
        Retryable = false
    };

    public static GatewayProtocolCompatibility FromGatewayExpectation(
        int? expectedProtocol,
        int? minimumProbeProtocol = null)
    {
        var state = expectedProtocol switch
        {
            < GatewayProtocolContract.MinimumSupportedVersion => GatewayProtocolCompatibilityState.GatewayTooOld,
            > GatewayProtocolContract.MaximumSupportedVersion => GatewayProtocolCompatibilityState.GatewayTooNew,
            _ => GatewayProtocolCompatibilityState.Mismatch
        };

        return new GatewayProtocolCompatibility
        {
            State = state,
            GatewayExpectedProtocol = expectedProtocol,
            GatewayMinimumProtocol = minimumProbeProtocol,
            Retryable = false
        };
    }
}
