using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionDiagnosticsProjectionTests
{
    [Fact]
    public void BuildStatus_ExplainsActiveGatewayRolesCredentialsAndActions()
    {
        var gateway = new GatewayRecord
        {
            Id = "gw-1",
            FriendlyName = "Local Gateway",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            LastConnected = new DateTime(2026, 7, 9, 20, 0, 0, DateTimeKind.Utc),
            BrowserControlPort = 18791,
            SshTunnel = new SshTunnelConfig("dev", "host", 443, 18789, IncludeBrowserProxyForward: true)
        };
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.Connected,
            OperatorCredentialSource = "DeviceToken",
            OperatorCredentialStatus = GatewayCredentialResolutionStatus.Resolved,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.PairingRequired,
            NodePairingStatus = PairingStatus.Pending,
            NodePairingApprovalKind = PairingApprovalKind.NodePair,
            NodePairingRequestId = "node-req-1",
            NodeCredentialSource = "SharedGatewayToken",
            NodeCredentialStatus = GatewayCredentialResolutionStatus.Resolved,
            GatewayId = gateway.Id,
            GatewayUrl = gateway.Url,
            GatewayName = gateway.FriendlyName
        };
        var diagnostics = new[]
        {
            new ConnectionDiagnosticEvent(new DateTime(2026, 7, 9, 20, 1, 0, DateTimeKind.Utc), "state", "Connected -> PairingRequired", null),
            new ConnectionDiagnosticEvent(new DateTime(2026, 7, 9, 20, 2, 0, DateTimeKind.Utc), "node", "Retrying device role-upgrade reconnect after repeated pending signal", "requestId=node-req-1"),
            new ConnectionDiagnosticEvent(new DateTime(2026, 7, 9, 20, 3, 0, DateTimeKind.Utc), "websocket", "Node connect failed", "timeout")
        };

        var status = ConnectionDiagnosticsProjection.BuildStatus(
            snapshot,
            gateway,
            enableNodeMode: true,
            enableMcpServer: true,
            isMcpRunning: false,
            mcpError: "Local MCP failed",
            nodeBrowserProxyEnabled: true,
            recentDiagnostics: diagnostics,
            diagnosticEventCount: 9);

        Assert.Equal("PairingRequired", status.ConnectionState);
        Assert.Equal("GatewayNodeAndLocalMcp", status.EffectiveMode);
        Assert.Equal("Local Gateway", status.Gateway!.Name);
        Assert.True(status.Operator.Connected);
        Assert.Equal("DeviceToken", status.Operator.Credential.Source);
        Assert.True(status.Node.PendingApproval);
        Assert.Equal("openclaw nodes approve node-req-1", status.Node.ApprovalCommand);
        Assert.Single(status.PendingActions);
        Assert.Equal("nodePairing", status.PendingActions[0].Kind);
        Assert.False(status.Mcp.Running);
        Assert.Equal("Local MCP failed", status.Mcp.Error);
        Assert.NotNull(status.BrowserProxy.Caveat);
        Assert.True(status.Retry.HasRecentRetrySignal);
        Assert.Equal(9, status.Diagnostics.EventCount);
        Assert.Contains("failed", status.Diagnostics.LastError);
    }

    [Fact]
    public void BuildGateways_RedactsTokensAndMarksBrowserProxyCaveat()
    {
        var active = new GatewayRecord
        {
            Id = "gw-active",
            FriendlyName = "Active",
            Url = "wss://user:password@active.example/path?token=secret#access_token=fragment-secret",
            BootstrapToken = "bootstrap-secret"
        };
        var inactive = new GatewayRecord
        {
            Id = "gw-inactive",
            FriendlyName = "Inactive",
            Url = "wss://inactive.example",
            SharedGatewayToken = "shared-secret"
        };

        var result = ConnectionDiagnosticsProjection.BuildGateways(
            [inactive, active],
            active.Id,
            nodeBrowserProxyEnabled: true);

        Assert.Equal(active.Id, result.ActiveGatewayId);
        Assert.Equal(2, result.Count);
        Assert.Equal(active.Id, result.Gateways[0].Id);
        Assert.DoesNotContain("password", result.Gateways[0].Url);
        Assert.DoesNotContain("secret", result.Gateways[0].Url);
        Assert.Equal("wss://active.example/path", result.Gateways[0].Url);
        Assert.True(result.Gateways[0].IsActive);
        Assert.False(result.Gateways[0].HasSharedGatewayToken);
        Assert.True(result.Gateways[0].HasBootstrapToken);
        Assert.NotNull(result.Gateways[0].BrowserProxyCaveat);
        Assert.True(result.Gateways[1].HasSharedGatewayToken);
        Assert.Null(result.Gateways[1].BrowserProxyCaveat);
    }

    [Fact]
    public void BuildGateways_DoesNotAttachBrowserProxyCaveatToInactiveGateways()
    {
        var inactiveWithoutToken = new GatewayRecord
        {
            Id = "gw-inactive",
            FriendlyName = "Inactive",
            Url = "wss://inactive.example"
        };

        var result = ConnectionDiagnosticsProjection.BuildGateways(
            [inactiveWithoutToken],
            activeGatewayId: "different-gateway",
            nodeBrowserProxyEnabled: true);

        Assert.False(result.Gateways[0].IsActive);
        Assert.Null(result.Gateways[0].BrowserProxyCaveat);
    }

    [Fact]
    public void BuildGateways_FiltersDegenerateGatewayRecords()
    {
        var valid = new GatewayRecord
        {
            Id = "gw-valid",
            FriendlyName = "Valid",
            Url = "wss://valid.example"
        };

        var result = ConnectionDiagnosticsProjection.BuildGateways(
            [new GatewayRecord(), valid],
            valid.Id,
            nodeBrowserProxyEnabled: true);

        Assert.Equal(1, result.Count);
        Assert.Single(result.Gateways);
        Assert.Equal(valid.Id, result.Gateways[0].Id);
    }
}
