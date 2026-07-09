namespace OpenClaw.Connection;

public enum GatewayCredentialResolutionStatus
{
    Resolved,
    Missing,
    Unreadable,
    Corrupt,
    FallbackUsed,
    BootstrapRequired
}

public sealed record GatewayCredentialResolution(
    GatewayCredential? Credential,
    GatewayCredentialResolutionStatus Status,
    bool FallbackUsed = false,
    bool BootstrapRequired = false,
    string? Detail = null,
    GatewayCredentialResolutionStatus? PrimaryStatus = null)
{
    public static GatewayCredentialResolution FromLegacy(GatewayCredential? credential) =>
        credential is null
            ? new(null, GatewayCredentialResolutionStatus.Missing)
            : new(
                credential,
                credential.IsBootstrapToken
                    ? GatewayCredentialResolutionStatus.BootstrapRequired
                    : GatewayCredentialResolutionStatus.Resolved,
                FallbackUsed: false,
                BootstrapRequired: credential.IsBootstrapToken);
}
