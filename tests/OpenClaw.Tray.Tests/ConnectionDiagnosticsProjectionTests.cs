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
        // Node is pairing-required, not connected — no shared-token caveat.
        Assert.Null(status.BrowserProxy.Caveat);
        Assert.True(status.Retry.HasRecentRetrySignal);
        Assert.Equal(9, status.Diagnostics.EventCount);
        Assert.Contains("failed", status.Diagnostics.LastError);
    }

    [Fact]
    public void BuildStatus_OmitsBrowserProxyCaveatWhenNodeSessionIsNotLive()
    {
        var gateway = new GatewayRecord
        {
            Id = "gw-1",
            FriendlyName = "Local",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true
        };
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Idle,
            OperatorState = RoleConnectionState.Idle,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.Idle,
            GatewayId = gateway.Id,
            GatewayUrl = gateway.Url
        };

        var status = ConnectionDiagnosticsProjection.BuildStatus(
            snapshot,
            gateway,
            enableNodeMode: true,
            enableMcpServer: false,
            isMcpRunning: false,
            mcpError: null,
            nodeBrowserProxyEnabled: true,
            recentDiagnostics: [],
            diagnosticEventCount: 0);

        Assert.Null(status.BrowserProxy.Caveat);
        Assert.Null(status.Gateway!.BrowserProxyCaveat);
    }

    [Fact]
    public void BuildStatus_ShowsBrowserProxyCaveatWhenNodeConnectedAndTokenMissing()
    {
        var gateway = new GatewayRecord
        {
            Id = "gw-1",
            FriendlyName = "Local",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true
        };
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Connected,
            OperatorState = RoleConnectionState.Connected,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.Connected,
            NodePairingStatus = PairingStatus.Paired,
            GatewayId = gateway.Id,
            GatewayUrl = gateway.Url
        };

        var status = ConnectionDiagnosticsProjection.BuildStatus(
            snapshot,
            gateway,
            enableNodeMode: true,
            enableMcpServer: false,
            isMcpRunning: false,
            mcpError: null,
            nodeBrowserProxyEnabled: true,
            recentDiagnostics: [],
            diagnosticEventCount: 0);

        Assert.NotNull(status.BrowserProxy.Caveat);
        Assert.Contains("shared token", status.BrowserProxy.Caveat!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browser-control port", status.BrowserProxy.Caveat!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(status.BrowserProxy.Caveat, status.Gateway!.BrowserProxyCaveat);
    }

    [Fact]
    public void BuildStatus_RemoteWithoutForwardMentionsEndpointRequirement()
    {
        var gateway = new GatewayRecord
        {
            Id = "gw-remote",
            FriendlyName = "Remote",
            Url = "wss://gateway.example.com",
            IsLocal = false
        };
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Connected,
            OperatorState = RoleConnectionState.Connected,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.Connected,
            NodePairingStatus = PairingStatus.Paired,
            GatewayId = gateway.Id,
            GatewayUrl = gateway.Url
        };

        var status = ConnectionDiagnosticsProjection.BuildStatus(
            snapshot,
            gateway,
            enableNodeMode: true,
            enableMcpServer: false,
            isMcpRunning: false,
            mcpError: null,
            nodeBrowserProxyEnabled: true,
            recentDiagnostics: [],
            diagnosticEventCount: 0);

        Assert.NotNull(status.BrowserProxy.Caveat);
        Assert.Contains("browser-control port", status.BrowserProxy.Caveat!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SSH browser-proxy forward", status.BrowserProxy.Caveat!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildGateways_RedactsTokensAndMarksBrowserProxyCaveatOnlyWhenNodeLive()
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

        var withoutLiveSession = ConnectionDiagnosticsProjection.BuildGateways(
            [inactive, active],
            active.Id,
            nodeBrowserProxyEnabled: true,
            nodeSessionLive: false);
        Assert.Null(withoutLiveSession.Gateways[0].BrowserProxyCaveat);

        var withLiveSession = ConnectionDiagnosticsProjection.BuildGateways(
            [inactive, active],
            active.Id,
            nodeBrowserProxyEnabled: true,
            nodeSessionLive: true);

        Assert.Equal(active.Id, withLiveSession.ActiveGatewayId);
        Assert.Equal(2, withLiveSession.Count);
        Assert.Equal(active.Id, withLiveSession.Gateways[0].Id);
        Assert.DoesNotContain("password", withLiveSession.Gateways[0].Url);
        Assert.DoesNotContain("secret", withLiveSession.Gateways[0].Url);
        Assert.Equal("wss://active.example/path", withLiveSession.Gateways[0].Url);
        Assert.True(withLiveSession.Gateways[0].IsActive);
        Assert.False(withLiveSession.Gateways[0].HasSharedGatewayToken);
        Assert.True(withLiveSession.Gateways[0].HasBootstrapToken);
        Assert.NotNull(withLiveSession.Gateways[0].BrowserProxyCaveat);
        Assert.True(withLiveSession.Gateways[1].HasSharedGatewayToken);
        Assert.Null(withLiveSession.Gateways[1].BrowserProxyCaveat);
    }

    [Fact]
    public void BuildStatus_SshTunnelWithoutBrowserForwardMentionsEndpointRequirement()
    {
        var gateway = new GatewayRecord
        {
            Id = "gw-ssh",
            FriendlyName = "SSH Remote",
            Url = "ws://127.0.0.1:18789",
            IsLocal = false,
            SshTunnel = new SshTunnelConfig("dev", "remote.example", 22, 18789, IncludeBrowserProxyForward: false)
        };
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Connected,
            OperatorState = RoleConnectionState.Connected,
            NodeConnectionIntended = true,
            NodeState = RoleConnectionState.Connected,
            NodePairingStatus = PairingStatus.Paired,
            GatewayId = gateway.Id,
            GatewayUrl = gateway.Url
        };

        var status = ConnectionDiagnosticsProjection.BuildStatus(
            snapshot,
            gateway,
            enableNodeMode: true,
            enableMcpServer: false,
            isMcpRunning: false,
            mcpError: null,
            nodeBrowserProxyEnabled: true,
            recentDiagnostics: [],
            diagnosticEventCount: 0);

        Assert.NotNull(status.BrowserProxy.Caveat);
        Assert.Contains("SSH browser-proxy forward", status.BrowserProxy.Caveat!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildGateways_RemoteWithoutForwardMentionsEndpointRequirement()
    {
        var remote = new GatewayRecord
        {
            Id = "gw-remote",
            FriendlyName = "Remote",
            Url = "wss://gateway.example.com",
            IsLocal = false
        };

        var result = ConnectionDiagnosticsProjection.BuildGateways(
            [remote],
            remote.Id,
            nodeBrowserProxyEnabled: true,
            nodeSessionLive: true);

        Assert.NotNull(result.Gateways[0].BrowserProxyCaveat);
        Assert.Contains(
            "browser-control port",
            result.Gateways[0].BrowserProxyCaveat!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "SSH browser-proxy forward",
            result.Gateways[0].BrowserProxyCaveat!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildGateways_SshLocalUrlWithoutBrowserForwardMentionsEndpointRequirement()
    {
        var ssh = new GatewayRecord
        {
            Id = "gw-ssh",
            FriendlyName = "SSH Remote",
            Url = "ws://127.0.0.1:18789",
            IsLocal = false,
            SshTunnel = new SshTunnelConfig("dev", "remote.example", 22, 18789, IncludeBrowserProxyForward: false)
        };

        var result = ConnectionDiagnosticsProjection.BuildGateways(
            [ssh],
            ssh.Id,
            nodeBrowserProxyEnabled: true,
            nodeSessionLive: true);

        Assert.NotNull(result.Gateways[0].BrowserProxyCaveat);
        Assert.Contains(
            "SSH browser-proxy forward",
            result.Gateways[0].BrowserProxyCaveat!,
            StringComparison.OrdinalIgnoreCase);
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
            nodeBrowserProxyEnabled: true,
            nodeSessionLive: true);

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
