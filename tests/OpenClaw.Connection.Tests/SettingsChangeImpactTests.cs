using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public class SettingsChangeImpactTests
{
    private static ConnectionSettingsSnapshot MakeSnapshot(
        string? gatewayUrl = null,
        bool useSshTunnel = false,
        string? sshTunnelUser = null,
        string? sshTunnelHost = null,
        int sshTunnelSshPort = 22,
        int sshTunnelRemotePort = 0,
        int sshTunnelLocalPort = 0,
        bool enableNodeMode = false,
        bool enableMcpServer = false,
        bool nodeCanvasEnabled = false,
        bool nodeScreenEnabled = false,
        bool nodeCameraEnabled = false,
        bool nodeLocationEnabled = false,
        bool nodeBrowserProxyEnabled = false,
        bool nodeSttEnabled = false,
        bool nodeTtsEnabled = false,
        bool nodeSystemRunEnabled = true,
        string? fullSettingsJson = null) => new(
        gatewayUrl,
        useSshTunnel,
        sshTunnelUser,
        sshTunnelHost,
        sshTunnelSshPort,
        sshTunnelRemotePort,
        sshTunnelLocalPort,
        enableNodeMode,
        enableMcpServer,
        nodeCanvasEnabled,
        nodeScreenEnabled,
        nodeCameraEnabled,
        nodeLocationEnabled,
        nodeBrowserProxyEnabled,
        nodeSttEnabled,
        nodeTtsEnabled,
        nodeSystemRunEnabled,
        fullSettingsJson);

    [Fact]
    public void NullPrev_ReturnsFullReconnect()
    {
        Assert.Equal(SettingsChangeImpact.FullReconnectRequired,
            SettingsChangeClassifier.Classify(null, MakeSnapshot()));
    }

    [Fact]
    public void NullNext_ReturnsFullReconnect()
    {
        Assert.Equal(SettingsChangeImpact.FullReconnectRequired,
            SettingsChangeClassifier.Classify(MakeSnapshot(), null));
    }

    [Fact]
    public void SameSettings_ReturnsNoOp()
    {
        var s = MakeSnapshot(gatewayUrl: "wss://test");
        Assert.Equal(SettingsChangeImpact.NoOp,
            SettingsChangeClassifier.Classify(s, s));
    }

    [Fact]
    public void GatewayUrlChanged_ReturnsFullReconnect()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://old");
        var next = MakeSnapshot(gatewayUrl: "wss://new");
        Assert.Equal(SettingsChangeImpact.FullReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void SshTunnelChanged_ReturnsOperatorReconnect()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", useSshTunnel: false);
        var next = MakeSnapshot(gatewayUrl: "wss://test", useSshTunnel: true);
        Assert.Equal(SettingsChangeImpact.OperatorReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void SshTunnelSshPortChanged_ReturnsOperatorReconnect()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", useSshTunnel: true, sshTunnelSshPort: 22);
        var next = MakeSnapshot(gatewayUrl: "wss://test", useSshTunnel: true, sshTunnelSshPort: 2222);
        Assert.Equal(SettingsChangeImpact.OperatorReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void NodeModeChanged_ReturnsNodeReconnect()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", enableNodeMode: false);
        var next = MakeSnapshot(gatewayUrl: "wss://test", enableNodeMode: true);
        Assert.Equal(SettingsChangeImpact.NodeReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void CapabilityChanged_ReturnsCapabilityReload()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", nodeCanvasEnabled: true);
        var next = MakeSnapshot(gatewayUrl: "wss://test", nodeCanvasEnabled: false);
        Assert.Equal(SettingsChangeImpact.CapabilityReload,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void SystemRunCapabilityChanged_ReturnsCapabilityReload()
    {
        // Flipping the "Run system tools" toggle must trigger a capability
        // reload — without it the App.OnSettingsSaved branch falls through to
        // UiOnly and the connect-handshake commands list stays stale.
        var prev = MakeSnapshot(gatewayUrl: "wss://test", nodeSystemRunEnabled: true);
        var next = MakeSnapshot(gatewayUrl: "wss://test", nodeSystemRunEnabled: false);
        Assert.Equal(SettingsChangeImpact.CapabilityReload,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void UiOnlyChange_ReturnsUiOnly()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", fullSettingsJson: "{\"ShowNotifications\":true}");
        var next = MakeSnapshot(gatewayUrl: "wss://test", fullSettingsJson: "{\"ShowNotifications\":false}");
        Assert.Equal(SettingsChangeImpact.UiOnly,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void McpServerToggled_ReturnsUiOnly()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", enableMcpServer: false);
        var next = MakeSnapshot(gatewayUrl: "wss://test", enableMcpServer: true);
        Assert.Equal(SettingsChangeImpact.UiOnly,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void McpServerToggledOff_ReturnsUiOnly()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", enableMcpServer: true);
        var next = MakeSnapshot(gatewayUrl: "wss://test", enableMcpServer: false);
        Assert.Equal(SettingsChangeImpact.UiOnly,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void McpAndNodeModeToggled_ReturnsNodeReconnect()
    {
        var prev = MakeSnapshot(gatewayUrl: "wss://test", enableNodeMode: false, enableMcpServer: false);
        var next = MakeSnapshot(gatewayUrl: "wss://test", enableNodeMode: true, enableMcpServer: true);
        Assert.Equal(SettingsChangeImpact.NodeReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }
}
