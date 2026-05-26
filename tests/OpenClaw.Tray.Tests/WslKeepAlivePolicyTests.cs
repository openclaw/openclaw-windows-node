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

    [Fact]
    public void FindStaleSetupManagedDistroNames_PreservesRegisteredLocalDistro_WhenRemoteIsActive()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "local",
                Url = "ws://localhost:18789",
                IsLocal = true,
                SetupManagedDistroName = "OpenClawGateway",
            },
            new GatewayRecord
            {
                Id = "remote",
                Url = "wss://gateway.example.test",
                IsLocal = false,
            },
        };

        var stale = WslKeepAlivePolicy.FindStaleSetupManagedDistroNames(
            records,
            ["OpenClawGateway"],
            setupStateDistroName: "OpenClawGateway");

        Assert.Empty(stale);
    }

    [Fact]
    public void FindStaleSetupManagedDistroNames_ReturnsMarkerDistro_WhenNoLocalRecordOwnsIt()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "remote",
                Url = "wss://gateway.example.test",
                IsLocal = false,
            },
        };

        var stale = WslKeepAlivePolicy.FindStaleSetupManagedDistroNames(
            records,
            ["OldOpenClawGateway"],
            setupStateDistroName: null);

        Assert.Equal(["OldOpenClawGateway"], stale);
    }

    [Fact]
    public void IsKeepaliveCommandLine_RequiresDistroAndSleepInfinity()
    {
        Assert.True(WslKeepAlivePolicy.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep infinity",
            "OpenClawGateway"));
        Assert.False(WslKeepAlivePolicy.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep 60",
            "OpenClawGateway"));
        Assert.False(WslKeepAlivePolicy.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OtherGateway -- sleep infinity",
            "OpenClawGateway"));
    }

    [Fact]
    public void TryGetMarkerDistroName_ReadsMarkerDistro()
    {
        Assert.True(WslKeepAlivePolicy.TryGetMarkerDistroName(
            """{"DistroName":"OpenClawGateway","Pid":123}""",
            out var distroName));

        Assert.Equal("OpenClawGateway", distroName);
    }
}
