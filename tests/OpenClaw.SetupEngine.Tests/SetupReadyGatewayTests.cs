using OpenClaw.Connection;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupReadyGatewayTests
{
    [Fact]
    public void NativePackage_IsDescribedAsNativeAndKeepsTheInstallStep()
    {
        var gateway = SetupReadyGateway.From(new GatewayRecord
        {
            Id = "native", Url = "ws://127.0.0.1:18789", NativePackageFamilyName = "OpenClawFoundation.OpenClawGateway_rfcbke2p71se2",
        });
        Assert.Equal(SetupReadyGatewayKind.Native, gateway.Kind);
        Assert.Equal(SetupGatewayRoute.Native, gateway.Route);
    }

    [Fact]
    public void SetupManagedDistro_IsDescribedAsWsl()
    {
        var gateway = SetupReadyGateway.From(new GatewayRecord
        {
            Id = "wsl", Url = "ws://127.0.0.1:18789", IsLocal = true, SetupManagedDistroName = "OpenClawGateway",
        });
        Assert.Equal(SetupReadyGatewayKind.Wsl, gateway.Kind);
        Assert.Equal(SetupGatewayRoute.ManagedWsl, gateway.Route);
    }

    [Fact]
    public void ExistingLoopbackGateway_IsLocalButNotClaimedAsInstalledBySetup()
    {
        var gateway = SetupReadyGateway.From(new GatewayRecord { Id = "existing", Url = "ws://localhost:18789" });
        Assert.Equal(SetupReadyGatewayKind.LocalExisting, gateway.Kind);
        Assert.Equal(SetupGatewayRoute.Existing, gateway.Route);
        Assert.DoesNotContain(OnboardingStage.Install, OnboardingFlowPolicy.GetStages(gateway.Route, new SetupConfig()));
    }

    [Theory]
    [InlineData("wss://gateway.example.test", null, null, "gateway.example.test")]
    [InlineData("wss://gateway.example.test", "Home lab", null, "Home lab")]
    // An SSH tunnel connects through a loopback port, but the Gateway runs on the tunnel host.
    [InlineData("ws://127.0.0.1:28789", null, "lab.example.test", "lab.example.test")]
    public void RemoteGateway_NamesItsHost(string url, string? friendlyName, string? tunnelHost, string expectedHost)
    {
        var gateway = SetupReadyGateway.From(new GatewayRecord
        {
            Id = "remote", Url = url, FriendlyName = friendlyName,
            SshTunnel = tunnelHost is null ? null : new SshTunnelConfig("user", tunnelHost, 18789, 28789),
        });
        Assert.Equal(SetupReadyGatewayKind.Remote, gateway.Kind);
        Assert.Equal(SetupGatewayRoute.Remote, gateway.Route);
        Assert.Equal(expectedHost, gateway.Host);
    }

    [Fact]
    public void MissingRecord_IsUnknownAndKeepsTheNativeSteps()
    {
        var gateway = SetupReadyGateway.From(null);
        Assert.Equal(SetupReadyGatewayKind.Unknown, gateway.Kind);
        Assert.Equal(SetupGatewayRoute.Native, gateway.Route);
    }
}
