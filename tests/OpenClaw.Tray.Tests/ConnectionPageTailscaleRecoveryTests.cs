using OpenClaw.Connection;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPageTailscaleRecoveryTests
{
    [Fact]
    public void NetworkFailure_ForManagedTailscaleGateway_UsesDedicatedRecoveryPlan()
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Error,
            OperatorState = RoleConnectionState.Error,
            OperatorError = "connection timed out",
            GatewayUrl = "wss://openclaw.tailnet.ts.net",
        };
        var record = new GatewayRecord
        {
            Id = "tailscale",
            Url = "wss://openclaw.tailnet.ts.net",
            FriendlyName = "Tailscale (OpenClawGateway)",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        };

        var plan = ConnectionPagePlan.Build(snapshot, record, self: null, settings: null, savedGatewayCount: 1);

        Assert.Equal(RecoveryCategory.Tailscale, plan.Recovery);
        Assert.Equal("Tailscale gateway unavailable", plan.StripHeadline);
        Assert.Equal(ConnectionPrimaryAction.Retry, plan.StripPrimaryAction);

        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs"));
        Assert.Contains("Funnel is unsupported", source);
        Assert.Contains("never falls back to localhost", source);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public void NetworkFailure_ForUnmanagedTailscaleGateway_UsesOrdinaryNetworkRecovery(
        bool isLocal,
        string? managedDistroName)
    {
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Error,
            OperatorState = RoleConnectionState.Error,
            OperatorError = "connection timed out",
            GatewayUrl = "wss://manual.tailnet.ts.net",
        };
        var record = new GatewayRecord
        {
            Id = "manual-tailscale",
            Url = "wss://manual.tailnet.ts.net",
            FriendlyName = "Manual Tailscale gateway",
            IsLocal = isLocal,
            SetupManagedDistroName = managedDistroName,
        };

        var plan = ConnectionPagePlan.Build(snapshot, record, self: null, settings: null, savedGatewayCount: 1);

        Assert.Equal(RecoveryCategory.Network, plan.Recovery);
        Assert.NotEqual("Tailscale gateway unavailable", plan.StripHeadline);
    }
}
