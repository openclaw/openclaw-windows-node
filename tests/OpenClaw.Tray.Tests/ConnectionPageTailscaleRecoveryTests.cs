using OpenClaw.Connection;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPageTailscaleRecoveryTests
{
    [Fact]
    public void SavedDashboardLaunch_UsesSharedBrowserCredentialPolicy()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));
        var methodStart = source.IndexOf(
            "private async Task OnSavedRowOpenDashboardAsync(object sender)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void OnEnableTailscaleDashboardAuth(object sender, RoutedEventArgs e)",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        AssertInOrder(
            method,
            "var trustTailscaleAuth = rec.TrustTailscaleAuth &&",
            "await _connectionManager.RevalidateTailscaleDashboardAuthAsync(rec.Id)",
            "if (trustTailscaleAuth)",
            "trustTailscaleAuth: true",
            "LaunchUriAsync(",
            "return;",
            "var usesSharedGatewayToken =",
            "GatewayDashboardUrlBuilder.HasBrowserCompatibleCredential(",
            "trustTailscaleAuth,",
            "usesSharedGatewayToken",
            "return;",
            "appendSharedGatewayToken: usesSharedGatewayToken",
            "LaunchUriAsync(");
    }

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

    private static void AssertInOrder(string source, params string[] markers)
    {
        var current = -1;
        foreach (var marker in markers)
        {
            var next = source.IndexOf(marker, current + 1, StringComparison.Ordinal);
            Assert.True(next >= 0, $"Could not find marker after index {current}: {marker}");
            current = next;
        }
    }
}
