using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Result of applying a setup code.
/// </summary>
public sealed record SetupCodeResult(
    SetupCodeOutcome Outcome,
    string? ErrorMessage = null,
    string? GatewayUrl = null,
    bool GatewayCommitted = false,
    GatewayErrorKind ErrorKind = GatewayErrorKind.Unknown);

public enum SetupCodeOutcome
{
    Success,
    InvalidCode,
    InvalidUrl,
    ConnectionFailed,
    AlreadyConnected
}
