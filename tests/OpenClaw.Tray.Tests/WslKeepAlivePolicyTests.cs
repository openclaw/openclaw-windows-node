using OpenClaw.Connection;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class WslKeepAlivePolicyTests
{
    [Fact]
    public void ShouldStart_UsesActiveLocalRegistryRecord_WhenLegacySettingsAreEmpty()
    {
        var record = new GatewayRecord
        {
            Id = "local",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        };

        Assert.True(WslKeepAlivePolicy.ShouldStart(record, legacyGatewayUrl: null));
    }

    [Fact]
    public void ShouldStart_DoesNotFallBackToLegacyLocalUrl_WhenActiveRecordIsRemote()
    {
        var record = new GatewayRecord
        {
            Id = "remote",
            Url = "wss://gateway.example.test",
            IsLocal = false,
        };

        Assert.False(WslKeepAlivePolicy.ShouldStart(record, "ws://localhost:18789"));
    }

    [Fact]
    public void ShouldStart_DoesNotTreatSshTunnelLocalForwardAsWslGateway()
    {
        var record = new GatewayRecord
        {
            Id = "ssh",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SshTunnel = new SshTunnelConfig("user", "example.test", 18789, 18789),
        };

        Assert.False(WslKeepAlivePolicy.ShouldStart(record, legacyGatewayUrl: null));
    }

    [Fact]
    public void ShouldStart_FallsBackToLegacyLocalUrl_WhenNoActiveRecordExists()
    {
        Assert.True(WslKeepAlivePolicy.ShouldStart(activeRecord: null, "ws://127.0.0.1:18789"));
    }

    [Fact]
    public void ResolveDistroName_PrefersRegistryManagedDistro()
    {
        var record = new GatewayRecord
        {
            Id = "local",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "RegistryGateway",
        };

        var distroName = WslKeepAlivePolicy.ResolveDistroName(
            record,
            setupStateDistroName: "SetupStateGateway",
            environmentOverride: "EnvGateway");

        Assert.Equal("RegistryGateway", distroName);
    }
}
