using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

/// <summary>Authority captured before creating a setup client. Registry reads may validate it, never relabel it.</summary>
internal sealed class SetupGatewaySessionBinding(GatewayRecord record)
{
    public string GatewayId { get; } = record.Id;
    public string Endpoint { get; } = GatewayClientEndpointResolver.Resolve(record);
    public string EndpointBinding { get; } = GatewayDashboardBinding.Capture(record);
    private readonly SshTunnelConfig? _sshTunnel = record.SshTunnel;

    public void RequireCurrent(GatewayRecord? active)
    {
        if (active is null || active.Id != GatewayId ||
            GatewayDashboardBinding.Capture(active) != EndpointBinding ||
            active.SshTunnel != _sshTunnel || GatewayClientEndpointResolver.Resolve(active) != Endpoint)
            throw new InvalidOperationException("The setup Gateway changed. Reopen setup for the selected Gateway.");
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
