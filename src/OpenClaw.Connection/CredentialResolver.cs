using OpenClaw.Shared;

namespace OpenClaw.Connection;

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

    public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) =>
        ResolveOperatorDetailed(record, identityPath).Credential;

    public GatewayCredentialResolution ResolveOperatorDetailed(GatewayRecord record, string identityPath)
    {
        ArgumentNullException.ThrowIfNull(record);

        return ResolveRole(
            _identityReader.ReadStoredDeviceToken(identityPath),
            SourceDeviceToken,
            record.SharedGatewayToken,
            record.BootstrapToken);
    }

    public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) =>
        ResolveNodeDetailed(record, identityPath).Credential;

    public GatewayCredentialResolution ResolveNodeDetailed(GatewayRecord record, string identityPath)
    {
        ArgumentNullException.ThrowIfNull(record);

        return ResolveRole(
            _identityReader.ReadStoredNodeDeviceToken(identityPath),
            SourceNodeDeviceToken,
            record.SharedGatewayToken,
            record.BootstrapToken);
    }

    private static GatewayCredentialResolution ResolveRole(
        DeviceTokenReadResult storedToken,
        string deviceTokenSource,
        string? sharedGatewayToken,
        string? bootstrapToken)
    {
        if (storedToken.Status == DeviceTokenReadStatus.Resolved &&
            !string.IsNullOrWhiteSpace(storedToken.Token))
        {
            var credential = new GatewayCredential(storedToken.Token!, false, deviceTokenSource);
            return new GatewayCredentialResolution(credential, GatewayCredentialResolutionStatus.Resolved);
        }

        var primaryStatus = MapPrimaryStatus(storedToken.Status);
        if (!string.IsNullOrWhiteSpace(sharedGatewayToken))
        {
            var fallbackUsed = primaryStatus is GatewayCredentialResolutionStatus.Unreadable
                or GatewayCredentialResolutionStatus.Corrupt;
            var detail = fallbackUsed
                ? BuildFallbackDetail(deviceTokenSource, primaryStatus, SourceSharedGatewayToken, storedToken.Detail)
                : null;
            var credential = new GatewayCredential(sharedGatewayToken!, false, SourceSharedGatewayToken)
            {
                ResolutionStatus = fallbackUsed
                    ? GatewayCredentialResolutionStatus.FallbackUsed
                    : GatewayCredentialResolutionStatus.Resolved,
                FallbackUsed = fallbackUsed,
                ResolutionDetail = detail
            };
            return new GatewayCredentialResolution(
                credential,
                fallbackUsed
                    ? GatewayCredentialResolutionStatus.FallbackUsed
                    : GatewayCredentialResolutionStatus.Resolved,
                FallbackUsed: fallbackUsed,
                BootstrapRequired: false,
                Detail: detail,
                PrimaryStatus: primaryStatus);
        }

        if (!string.IsNullOrWhiteSpace(bootstrapToken))
        {
            var fallbackUsed = storedToken.Status is DeviceTokenReadStatus.Unreadable or DeviceTokenReadStatus.Corrupt;
            var detail = fallbackUsed
                ? BuildFallbackDetail(deviceTokenSource, primaryStatus, SourceBootstrapToken, storedToken.Detail)
                : "Using bootstrap token; pairing is required before a durable device token is available.";
            var credential = new GatewayCredential(bootstrapToken!, true, SourceBootstrapToken)
            {
                ResolutionStatus = GatewayCredentialResolutionStatus.BootstrapRequired,
                FallbackUsed = fallbackUsed,
                ResolutionDetail = detail
            };
            return new GatewayCredentialResolution(
                credential,
                GatewayCredentialResolutionStatus.BootstrapRequired,
                FallbackUsed: fallbackUsed,
                BootstrapRequired: true,
                Detail: detail,
                PrimaryStatus: primaryStatus);
        }

        var missingDetail = storedToken.Status switch
        {
            DeviceTokenReadStatus.Unreadable => $"Stored {deviceTokenSource} is unreadable and no shared/bootstrap fallback is available. {storedToken.Detail}",
            DeviceTokenReadStatus.Corrupt => $"Stored {deviceTokenSource} is corrupt and no shared/bootstrap fallback is available. {storedToken.Detail}",
            _ => "No device, shared, or bootstrap credential is available."
        };
        return new GatewayCredentialResolution(
            null,
            primaryStatus == GatewayCredentialResolutionStatus.Resolved
                ? GatewayCredentialResolutionStatus.Missing
                : primaryStatus,
            Detail: missingDetail,
            PrimaryStatus: primaryStatus);
    }

    private static GatewayCredentialResolutionStatus MapPrimaryStatus(DeviceTokenReadStatus status) =>
        status switch
        {
            DeviceTokenReadStatus.Resolved => GatewayCredentialResolutionStatus.Resolved,
            DeviceTokenReadStatus.Unreadable => GatewayCredentialResolutionStatus.Unreadable,
            DeviceTokenReadStatus.Corrupt => GatewayCredentialResolutionStatus.Corrupt,
            _ => GatewayCredentialResolutionStatus.Missing
        };

    private static string BuildFallbackDetail(
        string primarySource,
        GatewayCredentialResolutionStatus primaryStatus,
        string fallbackSource,
        string? readDetail)
    {
        var reason = primaryStatus switch
        {
            GatewayCredentialResolutionStatus.Unreadable => $"{primarySource} is unreadable",
            GatewayCredentialResolutionStatus.Corrupt => $"{primarySource} is corrupt",
            _ => $"{primarySource} is missing"
        };
        return string.IsNullOrWhiteSpace(readDetail)
            ? $"{reason}; using {fallbackSource}."
            : $"{reason}; using {fallbackSource}. {readDetail}";
    }
}
