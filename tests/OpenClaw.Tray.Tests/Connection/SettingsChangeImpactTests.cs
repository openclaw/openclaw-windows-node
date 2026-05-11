using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

public class SettingsChangeImpactTests
{
    [Fact]
    public void NullPrev_ReturnsFullReconnect()
    {
        Assert.Equal(SettingsChangeImpact.FullReconnectRequired,
            SettingsChangeClassifier.Classify(null, new SettingsData()));
    }

    [Fact]
    public void NullNext_ReturnsFullReconnect()
    {
        Assert.Equal(SettingsChangeImpact.FullReconnectRequired,
            SettingsChangeClassifier.Classify(new SettingsData(), null));
    }

    [Fact]
    public void SameSettings_ReturnsNoOp()
    {
        var s = new SettingsData { GatewayUrl = "wss://test" };
        Assert.Equal(SettingsChangeImpact.NoOp,
            SettingsChangeClassifier.Classify(s, s));
    }

    [Fact]
    public void GatewayUrlChanged_ReturnsFullReconnect()
    {
        var prev = new SettingsData { GatewayUrl = "wss://old" };
        var next = new SettingsData { GatewayUrl = "wss://new" };
        Assert.Equal(SettingsChangeImpact.FullReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void SshTunnelChanged_ReturnsOperatorReconnect()
    {
        var prev = new SettingsData { GatewayUrl = "wss://test", UseSshTunnel = false };
        var next = new SettingsData { GatewayUrl = "wss://test", UseSshTunnel = true };
        Assert.Equal(SettingsChangeImpact.OperatorReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void NodeModeChanged_ReturnsNodeReconnect()
    {
        var prev = new SettingsData { GatewayUrl = "wss://test", EnableNodeMode = false };
        var next = new SettingsData { GatewayUrl = "wss://test", EnableNodeMode = true };
        Assert.Equal(SettingsChangeImpact.NodeReconnectRequired,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void CapabilityChanged_ReturnsCapabilityReload()
    {
        var prev = new SettingsData { GatewayUrl = "wss://test", NodeCanvasEnabled = true };
        var next = new SettingsData { GatewayUrl = "wss://test", NodeCanvasEnabled = false };
        Assert.Equal(SettingsChangeImpact.CapabilityReload,
            SettingsChangeClassifier.Classify(prev, next));
    }

    [Fact]
    public void UiOnlyChange_ReturnsUiOnly()
    {
        var prev = new SettingsData { GatewayUrl = "wss://test", ShowNotifications = true };
        var next = new SettingsData { GatewayUrl = "wss://test", ShowNotifications = false };
        Assert.Equal(SettingsChangeImpact.UiOnly,
            SettingsChangeClassifier.Classify(prev, next));
    }
}
