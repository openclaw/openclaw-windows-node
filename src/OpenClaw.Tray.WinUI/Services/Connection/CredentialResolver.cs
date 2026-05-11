using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Canonical credential resolver implementing the northstar resolution order.
/// <para>
/// Operator order: DeviceToken → SharedGatewayToken → BootstrapToken → null.
/// Node order:     NodeDeviceToken → SharedGatewayToken → BootstrapToken → null.
/// </para>
/// <para>
/// Critical invariant: a stored device token ALWAYS wins over shared/bootstrap tokens.
/// Returning a bootstrap token for a paired device would downgrade scopes and may
/// trigger unnecessary re-pairing.
/// </para>
/// </summary>
public sealed class CredentialResolver : ICredentialResolver
{
    public const string SourceDeviceToken = "identity.DeviceToken";
    public const string SourceNodeDeviceToken = "identity.NodeDeviceToken";
    public const string SourceSharedGatewayToken = "record.SharedGatewayToken";
    public const string SourceBootstrapToken = "record.BootstrapToken";

    private readonly IDeviceIdentityReader _identityReader;

    public CredentialResolver(IDeviceIdentityReader identityReader)
    {
        _identityReader = identityReader ?? throw new ArgumentNullException(nameof(identityReader));
    }

    public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 1. Paired device token — highest priority, never downgrade
        var storedToken = _identityReader.TryReadStoredDeviceToken(identityPath);
        if (!string.IsNullOrWhiteSpace(storedToken))
            return new GatewayCredential(storedToken!, false, SourceDeviceToken);

        // 2. Shared gateway token — works for any device, full scopes
        if (!string.IsNullOrWhiteSpace(record.SharedGatewayToken))
            return new GatewayCredential(record.SharedGatewayToken!, false, SourceSharedGatewayToken);

        // 3. Bootstrap token — one-time setup, limited scopes
        if (!string.IsNullOrWhiteSpace(record.BootstrapToken))
            return new GatewayCredential(record.BootstrapToken!, true, SourceBootstrapToken);

        return null;
    }

    public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 1. Paired node token — highest priority
        var storedToken = _identityReader.TryReadStoredNodeDeviceToken(identityPath);
        if (!string.IsNullOrWhiteSpace(storedToken))
            return new GatewayCredential(storedToken!, false, SourceNodeDeviceToken);

        // 2. Shared gateway token
        if (!string.IsNullOrWhiteSpace(record.SharedGatewayToken))
            return new GatewayCredential(record.SharedGatewayToken!, false, SourceSharedGatewayToken);

        // 3. Bootstrap token
        if (!string.IsNullOrWhiteSpace(record.BootstrapToken))
            return new GatewayCredential(record.BootstrapToken!, true, SourceBootstrapToken);

        return null;
    }
}
