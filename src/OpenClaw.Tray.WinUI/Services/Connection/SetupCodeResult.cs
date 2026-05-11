namespace OpenClawTray.Services.Connection;

/// <summary>
/// Result of applying a setup code.
/// </summary>
public sealed record SetupCodeResult(
    SetupCodeOutcome Outcome,
    string? ErrorMessage = null,
    string? GatewayUrl = null);

public enum SetupCodeOutcome
{
    Success,
    InvalidCode,
    InvalidUrl,
    ConnectionFailed,
    AlreadyConnected
}
