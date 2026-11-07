using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Exact native admission, usable for a staged setup record without publishing it.
/// The authenticated transport still reauthorizes the package-owned listener for every request.
/// </summary>
public sealed record NativeLocalAiGatewayTarget
{
    public string GatewayId { get; }
    public string PackageFamilyName { get; }
    public GatewayAiSetupRoute Route { get; }

    private NativeLocalAiGatewayTarget(string gatewayId, string packageFamilyName, GatewayAiSetupRoute route)
    {
        GatewayId = gatewayId;
        PackageFamilyName = packageFamilyName;
        Route = route;
    }

    public static NativeLocalAiGatewayTarget Capture(GatewayRecord record, GatewayAiSetupRoute route)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(route);
        if (!record.IsLocal || record.SshTunnel is not null ||
            string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(record.NativePackageFamilyName) ||
            record.NativeRuntimeContract != NativeGatewayPackageClient.IsolatedContract ||
            !string.IsNullOrWhiteSpace(record.SetupManagedDistroName) ||
            !GatewayRecordEditing.IsLoopbackEndpoint(record.Url) ||
            !Uri.TryCreate(record.Url, UriKind.Absolute, out var endpoint) || endpoint.Port is <= 0 or > 65535 ||
            route.GatewayId != record.Id || route.EndpointBinding != GatewayDashboardBinding.Capture(record) ||
            string.IsNullOrWhiteSpace(route.AuthorityId) ||
            !SetupCompletionAuthority.IsValid(route.IdentityBinding, route.SessionKey, route.AgentId))
            throw new InvalidOperationException("Local AI requires the exact authenticated, package-managed native Gateway session.");
        return new(record.Id, record.NativePackageFamilyName, route);
    }

    public void RequireCurrent(GatewayAiSetupRoute route)
    {
        if (route != Route)
            throw new InvalidOperationException("The native Local AI Gateway session changed. Reopen setup for the original Gateway.");
    }
}
