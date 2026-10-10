using System.Text.Json;
using OpenClaw.Connection;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class WslKeepAlivePolicyTests
{
    [Fact]
    public void IsolatedProfile_RequiresExplicitManagedGatewayBeforeAnyLifecycleAction()
    {
        Assert.False(WslKeepAlivePolicy.CanManageGateway(null, isIsolated: true));
        Assert.False(WslKeepAlivePolicy.CanManageGateway(
            new GatewayRecord { Id = "fake", Url = "ws://127.0.0.1:59999", IsLocal = true },
            isIsolated: true));
        Assert.True(WslKeepAlivePolicy.CanManageGateway(
            new GatewayRecord { Id = "owned", SetupManagedDistroName = "ScenarioOwnedGateway" },
            isIsolated: true));
        Assert.True(WslKeepAlivePolicy.CanManageGateway(null, isIsolated: false));
    }

    [Fact]
    public void MarkedKeepaliveIdentity_RejectsReusedPidProcessNameOrStartTime()
    {
        var markerStart = new DateTime(2026, 7, 24, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(WslKeepAlivePolicy.IsMarkedKeepaliveProcessIdentity(
            "wsl",
            markerStart.AddSeconds(1),
            markerStart));
        Assert.False(WslKeepAlivePolicy.IsMarkedKeepaliveProcessIdentity(
            "svchost",
            markerStart,
            markerStart));
        Assert.False(WslKeepAlivePolicy.IsMarkedKeepaliveProcessIdentity(
            "wsl",
            markerStart.AddMinutes(1),
            markerStart));
    }

    [Fact]
    public void MarkerStart_OffsetTimestampMatchesUtcProcessStart()
    {
        using var document = JsonDocument.Parse("""{"StartTimeUtc":"2026-09-23T00:53:13+00:00"}""");
        var element = document.RootElement.GetProperty("StartTimeUtc");

        Assert.True(WslKeepAlivePolicy.TryReadMarkerStartTimeUtc(element, out var markerUtc));
        Assert.Equal(DateTimeKind.Utc, markerUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 23, 0, 53, 13, DateTimeKind.Utc), markerUtc);
        Assert.True(WslKeepAlivePolicy.IsMarkedKeepaliveProcessIdentity(
            "wsl",
            markerUtc.AddSeconds(1),
            markerUtc));

        Assert.True(element.TryGetDateTime(out var localWall));
        var mislabeled = DateTime.SpecifyKind(localWall, DateTimeKind.Utc);
        var oldPathMatches = WslKeepAlivePolicy.IsMarkedKeepaliveProcessIdentity(
            "wsl",
            markerUtc,
            mislabeled);
        // TryGetDateTime converts the offset into local time. SpecifyKind
        // then labels that wall clock as UTC. The two instants differ only
        // when the machine is not already on UTC.
        var shift = TimeZoneInfo.Local.GetUtcOffset(markerUtc);
        Assert.Equal(shift == TimeSpan.Zero, oldPathMatches);
    }

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
    public void ShouldStart_UsesAppOwnedTailscaleRegistryRecord()
    {
        var record = new GatewayRecord
        {
            Id = "tailscale",
            Url = "wss://openclaw.tailnet.ts.net",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        };

        Assert.True(WslKeepAlivePolicy.ShouldStart(record, legacyGatewayUrl: null));
        Assert.True(WslKeepAlivePolicy.HasSetupManagedLocalGateway([record]));
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
    public void HasSetupManagedLocalGateway_ReturnsTrueForSetupManagedLocalRecord()
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
        };

        Assert.True(WslKeepAlivePolicy.HasSetupManagedLocalGateway(records));
    }

    [Fact]
    public void HasSetupManagedLocalGateway_ReturnsTrueForLegacyDefaultSetupManagedLocalRecord()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "legacy-local",
                Url = "ws://localhost:18789",
                FriendlyName = "Local (OpenClawGateway)",
                IsLocal = true,
            },
        };

        Assert.True(WslKeepAlivePolicy.HasSetupManagedLocalGateway(records));
    }

    [Fact]
    public void HasSetupManagedLocalGateway_ReturnsFalseForManualLocalRecord()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "manual-local",
                Url = "ws://localhost:18789",
                IsLocal = true,
            },
        };

        Assert.False(WslKeepAlivePolicy.HasSetupManagedLocalGateway(records));
    }

    [Fact]
    public void HasSetupManagedLocalGateway_ReturnsFalseForSshTunnelRecord()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "ssh",
                Url = "ws://127.0.0.1:18789",
                IsLocal = true,
                SetupManagedDistroName = "OpenClawGateway",
                SshTunnel = new SshTunnelConfig("user", "example.test", 18789, 18789),
            },
        };

        Assert.False(WslKeepAlivePolicy.HasSetupManagedLocalGateway(records));
    }

    [Fact]
    public void HasSetupManagedLocalGateway_ReturnsFalseForRemoteRecord()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "remote",
                Url = "wss://gateway.example.test",
                SetupManagedDistroName = "OpenClawGateway",
            },
        };

        Assert.False(WslKeepAlivePolicy.HasSetupManagedLocalGateway(records));
    }

    [Fact]
    public void SameSetupManagedGateway_RequiresExactEndpointAndOwnership()
    {
        var expected = new GatewayRecord
        {
            Id = "local",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        };

        Assert.True(WslKeepAlivePolicy.IsSameSetupManagedGateway(expected, expected with { }));
        Assert.False(WslKeepAlivePolicy.IsSameSetupManagedGateway(
            expected,
            expected with { Url = "ws://localhost:18800" }));
        Assert.False(WslKeepAlivePolicy.IsSameSetupManagedGateway(
            expected,
            expected with { SetupManagedDistroName = null, FriendlyName = null }));
        Assert.False(WslKeepAlivePolicy.IsSameSetupManagedGateway(
            expected,
            expected with { Id = "other" }));
    }

    [Fact]
    public void HasSetupManagedLocalGateway_ReturnsFalseForNullRecords()
    {
        Assert.False(WslKeepAlivePolicy.HasSetupManagedLocalGateway(null));
    }

    [Fact]
    public void FindStaleSetupManagedDistroNames_PreservesLegacyDefaultLocalDistro()
    {
        var records = new[]
        {
            new GatewayRecord
            {
                Id = "legacy-local",
                Url = "ws://localhost:18789",
                FriendlyName = "Local (OpenClawGateway)",
                IsLocal = true,
            },
        };

        var stale = WslKeepAlivePolicy.FindStaleSetupManagedDistroNames(
            records,
            ["OpenClawGateway"],
            setupStateDistroName: null);

        Assert.Empty(stale);
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
        Assert.False(WslKeepAlivePolicy.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway-Dev -- sleep infinity",
            "OpenClawGateway"));
        Assert.True(WslKeepAlivePolicy.IsKeepaliveCommandLine(
            "wsl.exe --distribution \"OpenClawGateway-Dev\" -- sleep infinity",
            "OpenClawGateway-Dev"));
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
