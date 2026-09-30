using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

/// <summary>Authority captured before creating a setup client. Registry reads may validate it, never relabel it.</summary>
internal sealed class SetupGatewaySessionBinding(GatewayRecord record)
{
    public string GatewayId { get; } = record.Id;
    public string Endpoint { get; } = GatewayClientEndpointResolver.Resolve(record);
    public string EndpointBinding { get; } = GatewayDashboardBinding.Capture(record);
    private readonly SshTunnelConfig? _sshTunnel = record.SshTunnel;

    public static void RequireExpected(GatewayRecord? active, string? gatewayId, string? endpointBinding)
    {
        if (endpointBinding is null)
            LocalAiOnboardingUse.RequireGateway(gatewayId, active?.Id ?? string.Empty);
        if (active is null || gatewayId is not null && active.Id != gatewayId ||
            endpointBinding is not null && GatewayDashboardBinding.Capture(active) != endpointBinding)
            throw new SetupNativeOwnershipException();
    }

    public void RequireCurrent(GatewayRecord? active)
    {
        if (active is null || active.Id != GatewayId ||
            GatewayDashboardBinding.Capture(active) != EndpointBinding ||
            active.SshTunnel != _sshTunnel || GatewayClientEndpointResolver.Resolve(active) != Endpoint)
            throw new InvalidOperationException("The setup Gateway changed. Reopen setup for the selected Gateway.");
    }

    public void RequirePersistedAuthority(GatewayRecord? active, string identityPath, GatewayAiSetupRoute expected)
    {
        RequireCurrent(active);
        if (expected.GatewayId != GatewayId || expected.EndpointBinding != EndpointBinding ||
            !SetupCompletionAuthority.IsValid(expected.IdentityBinding, expected.SessionKey, expected.AgentId))
            throw new SetupNativeOwnershipException();
        SetupCompletionAuthority.RequirePersistedIdentity(identityPath, expected.IdentityBinding);
    }

    public GatewayAiSetupRoute GetRoute(GatewayRecord? active, string identityPath,
        string? mainSessionKey, string? signingDeviceId)
    {
        RequireCurrent(active);
        var sessionKey = mainSessionKey ?? "";
        var parts = sessionKey.Split(':', 3);
        var agentId = parts is ["agent", { Length: > 0 } agent, { Length: > 0 }] ? agent : null;
        var identity = string.IsNullOrWhiteSpace(signingDeviceId) ? null :
            SetupCompletionAuthority.CaptureIdentity(identityPath, signingDeviceId);
        return new(GatewayId, agentId,
            System.Text.Json.JsonSerializer.Serialize(new { Endpoint, Identity = identity, Session = sessionKey }),
            EndpointBinding, identity, sessionKey);
    }
}
