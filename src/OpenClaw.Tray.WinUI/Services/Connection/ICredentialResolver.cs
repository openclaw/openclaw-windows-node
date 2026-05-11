using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Resolved gateway credential plus metadata about which source was used.
/// </summary>
public sealed record GatewayCredential(string Token, bool IsBootstrapToken, string Source);

/// <summary>
/// Resolves the best activation credential for connecting to a gateway.
/// There is one canonical resolution path per role — no other code resolves credentials.
/// </summary>
public interface ICredentialResolver
{
    /// <summary>
    /// Resolve the best operator activation credential for the given gateway record.
    /// Returns null if no credential is available.
    /// </summary>
    GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath);

    /// <summary>
    /// Resolve the best node activation credential for the given gateway record.
    /// Returns null if no credential is available.
    /// </summary>
    GatewayCredential? ResolveNode(GatewayRecord record, string identityPath);
}
