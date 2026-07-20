using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Resolved gateway credential plus metadata about which source was used.
/// </summary>
public sealed record GatewayCredential(string Token, bool IsBootstrapToken, string Source)
{
    public GatewayCredentialResolutionStatus ResolutionStatus { get; init; } =
        GatewayCredentialResolutionStatus.Resolved;
    public bool FallbackUsed { get; init; }
    public string? ResolutionDetail { get; init; }
}

/// <summary>
/// Resolves the best activation credential for connecting to a gateway.
/// There is one canonical resolution path per role - no other code resolves credentials.
/// </summary>
public interface ICredentialResolver
{
    /// <summary>
    /// Resolve the best operator activation credential for the given gateway record.
    /// Returns null if no credential is available.
    /// </summary>
    GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath);

    /// <summary>
    /// Resolve the operator credential and preserve missing/corrupt/fallback status.
    /// </summary>
    GatewayCredentialResolution ResolveOperatorDetailed(GatewayRecord record, string identityPath) =>
        GatewayCredentialResolution.FromLegacy(ResolveOperator(record, identityPath));

    /// <summary>
    /// Resolve the best node activation credential for the given gateway record.
    /// Returns null if no credential is available.
    /// </summary>
    GatewayCredential? ResolveNode(GatewayRecord record, string identityPath);

    /// <summary>
    /// Resolve the node credential and preserve missing/corrupt/fallback status.
    /// </summary>
    GatewayCredentialResolution ResolveNodeDetailed(GatewayRecord record, string identityPath) =>
        GatewayCredentialResolution.FromLegacy(ResolveNode(record, identityPath));
}
