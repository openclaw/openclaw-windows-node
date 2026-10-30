using OpenClaw.Connection;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPageTailscaleRecoveryTests
{
    [Fact]
    public void FailedDashboardAuth_UsesLocalizedRecoveryOutcomeInsteadOfRawDetails()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));
        var methodStart = source.IndexOf(
            "private async Task OnEnableTailscaleDashboardAuthAsync(object sender)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void OnSavedRowEdit(object sender, RoutedEventArgs e)",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.Contains("ConnectionPagePlan.TailscaleAuthFailureMessageResourceKey(result.Outcome)", method);
        Assert.DoesNotContain("result.Error", method);
        Assert.DoesNotContain("result.Outcome.ToString()", method);
        Assert.Contains("result.Outcome == GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled", method);
        Assert.Contains("? \"ConnectionPage_TailscaleDashboardAuthEnabled\"", method);
        Assert.Contains(": \"ConnectionPage_TailscaleDashboardAuthAccepted\"", method);
    }

    [Theory]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.Ineligible, "ConnectionPage_TailscaleDashboardAuthFailureIneligible")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.NotActive, "ConnectionPage_TailscaleDashboardAuthFailureNotActive")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.NotConnected, "ConnectionPage_TailscaleDashboardAuthFailureNotConnected")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.MissingConfigScope, "ConnectionPage_TailscaleDashboardAuthFailureMissingConfigScope")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, "ConnectionPage_TailscaleDashboardAuthFailureConfigUnavailable")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, "ConnectionPage_TailscaleDashboardAuthFailurePatchRejected")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed, "ConnectionPage_TailscaleDashboardAuthFailurePersistenceFailed")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.UnsupportedTopology, "ConnectionPage_TailscaleDashboardAuthFailureUnsupportedTopology")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.IngressNotReady, "ConnectionPage_TailscaleDashboardAuthFailureIngressNotReady")]
    [InlineData(GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled, "ConnectionPage_TailscaleDashboardAuthFailureGeneric")]
    public void TailscaleDashboardAuthFailure_MapsOutcomeToLocalizedRecoveryMessage(
        GatewayTailscaleAuthUpgradeOutcome outcome,
        string expectedResourceKey) =>
        Assert.Equal(expectedResourceKey, ConnectionPagePlan.TailscaleAuthFailureMessageResourceKey(outcome));

    [Fact]
    public void EnableDashboardAuth_UsesLiveVerifierAndOfferPolicyWithoutPageOwnedMutation()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Connection", "GatewayConnectionManager.cs"));
        var start = manager.IndexOf("public async Task<GatewayTailscaleAuthUpgradeResult> EnableTailscaleDashboardAuthAsync(", StringComparison.Ordinal);
        var end = manager.IndexOf("public async Task<bool> RevalidateTailscaleDashboardAuthAsync(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var enable = manager[start..end];
        Assert.Contains("new GatewayTailscaleAuthUpgradeService(\n", enable.Replace("\r", ""));
        Assert.Contains("_tailscaleAuthLiveVerifier);", enable);
        Assert.Contains("_transitionSemaphore.WaitAsync(cancellationToken)", enable);
        Assert.Contains("_transitionSemaphore.Release();", enable);

        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs"));
        Assert.Contains("row.IsActive && GatewayTailscaleAuthUpgradePolicy.CanOffer(gatewayRecord)", page);
        Assert.Contains("enableTailscaleAuth.Click += OnEnableTailscaleDashboardAuth;", page);
        Assert.DoesNotContain("TrustTailscaleAuth = true", page);
    }

    [Fact]
    public void SavedDashboardLaunch_DelegatesToSharedDashboardLinkService()
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

        Assert.Contains("OpenDashboardFromLinkServiceAsync", method);
        Assert.Contains("new GatewayDashboardLinkRequest(", method);
        Assert.Contains("ValidateSavedDashboardFallbackAsync", method);
        Assert.Contains("var recordSnapshot = rec with { }", method);
        Assert.Contains("OnSavedRowOpenDashboardAsync(sender)", method);
        Assert.DoesNotContain("RevalidateTailscaleDashboardAuthAsync", method);
        Assert.DoesNotContain("GatewayDashboardUrlBuilder.Build(", method);

        var validationStart = source.IndexOf(
            "private async Task<bool> ValidateSavedDashboardFallbackAsync(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(validationStart > methodStart);
        var validationEnd = source.IndexOf(
            "private void OnEnableTailscaleDashboardAuth(object sender, RoutedEventArgs e)",
            validationStart,
            StringComparison.Ordinal);
        Assert.True(validationEnd > validationStart);
        var validation = source[validationStart..validationEnd];

        var credentialExportGuard = validation.IndexOf(
            "if (!result.BrowserCredentialIncluded)",
            StringComparison.Ordinal);
        var bypassReturn = validation.IndexOf(
            "return true;",
            credentialExportGuard,
            StringComparison.Ordinal);
        var provenanceLookup = validation.IndexOf(
            "ManagedLocalPortProvenance",
            bypassReturn,
            StringComparison.Ordinal);
        var credentialAuthorization = validation.IndexOf(
            "InteractiveEndpointAuthorizer?.IsCredentialAllowed",
            provenanceLookup,
            StringComparison.Ordinal);

        Assert.True(credentialExportGuard >= 0);
        Assert.True(bypassReturn > credentialExportGuard);
        Assert.True(provenanceLookup > bypassReturn);
        Assert.True(credentialAuthorization > provenanceLookup);
        Assert.DoesNotContain("result.TrustTailscaleAuth", validation);
        Assert.DoesNotContain("IsStrongCredentialAllowed", validation);
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

}
