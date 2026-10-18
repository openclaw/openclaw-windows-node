using System.Runtime.Versioning;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

[SupportedOSPlatform("windows")]
public class ClawHubDeepLinkTests
{
    [Fact]
    public void ValidInstallLink_ProducesInstallRequest()
    {
        var route = Assert.IsType<ActivationRoute.InstallClawHubPlugin>(
            DeepLinkHandler.PlanRoute(
                "openclaw://clawhub/install?id=diagnostics-otel",
                "openclaw"));

        Assert.True(route.Request.IsValid);
        Assert.Equal(ClawHubListingKind.Plugin, route.Request.Kind);
        Assert.Equal("diagnostics-otel", route.Request.ListingId);
        Assert.Null(route.Request.PackageName);
    }

    [Fact]
    public void PluginInstallLinkWithPackage_ProducesCommunityInstallRequest()
    {
        var route = Assert.IsType<ActivationRoute.InstallClawHubPlugin>(
            DeepLinkHandler.PlanRoute(
                "openclaw://clawhub/install?kind=plugin&id=expedia-openclaw&package=%40expediagroup%2Fexpedia-openclaw",
                "openclaw"));

        Assert.True(route.Request.IsValid);
        Assert.Equal(ClawHubListingKind.Plugin, route.Request.Kind);
        Assert.Equal("expedia-openclaw", route.Request.ListingId);
        Assert.Equal("@expediagroup/expedia-openclaw", route.Request.PackageName);
    }

    [Fact]
    public void ValidSkillInstallLink_ProducesSkillInstallRequest()
    {
        var route = Assert.IsType<ActivationRoute.InstallClawHubPlugin>(
            DeepLinkHandler.PlanRoute(
                "openclaw://clawhub/install?kind=skill&id=%40alipay%2Falipay-aipay",
                "openclaw"));

        Assert.True(route.Request.IsValid);
        Assert.Equal(ClawHubListingKind.Skill, route.Request.Kind);
        Assert.Equal("@alipay/alipay-aipay", route.Request.ListingId);
    }

    [Fact]
    public void SkillsShInstallLink_PreservesExactExternalReference()
    {
        var route = Assert.IsType<ActivationRoute.InstallClawHubPlugin>(
            DeepLinkHandler.PlanRoute(
                "openclaw://clawhub/install?kind=skill&id=skills-sh%3Avercel-labs%2Fskills%2Ffind-skills",
                "openclaw"));

        Assert.True(route.Request.IsValid);
        Assert.Equal(ClawHubListingKind.Skill, route.Request.Kind);
        Assert.Equal(
            "skills-sh:vercel-labs/skills/find-skills",
            route.Request.ListingId);
    }

    [Theory]
    [InlineData("openclaw://clawhub/install")]
    [InlineData("openclaw://clawhub/install?id=")]
    [InlineData("openclaw://clawhub/install?id=plugin&unexpected=value")]
    [InlineData("openclaw://clawhub/install?id=first&id=second")]
    [InlineData("openclaw://clawhub/install?id=bad%20id")]
    [InlineData("openclaw://clawhub/install?kind=theme&id=plugin")]
    [InlineData("openclaw://clawhub/install?kind=skill&id=skill&package=plugin")]
    [InlineData("openclaw://clawhub/install?kind=plugin&id=plugin&package=bad%3Apackage")]
    [InlineData("openclaw://clawhub/install?kind=skill&id=skills-sh%3Aowner%2Frepo")]
    [InlineData("openclaw://clawhub/install?kind=skill&id=skills-sh%3Aowner%2Frepo%2Fskill%2Fextra")]
    [InlineData("openclaw://clawhub/install?kind=skill&id=other%3Aowner%2Frepo%2Fskill")]
    public void InvalidInstallLink_ProducesVisibleErrorRequest(string uri)
    {
        var route = Assert.IsType<ActivationRoute.InstallClawHubPlugin>(
            DeepLinkHandler.PlanRoute(uri, "openclaw"));

        Assert.False(route.Request.IsValid);
        Assert.False(string.IsNullOrEmpty(route.Request.ValidationError));
    }

    [Fact]
    public void MalformedUri_IsRejectedWithoutThrowing()
    {
        Assert.Null(DeepLinkHandler.PlanRoute(
            "openclaw://clawhub/install?id=%",
            "openclaw"));
    }

    [Fact]
    public async Task LaunchAndForwardedIpc_ReachSameInstallRoute()
    {
        var pipeName = DeepLinkSecurityPolicy.BuildPipeName(
            Guid.NewGuid().ToString("N"),
            "test-user",
            0);
        await using var listener = new ActivationRouter("openclaw", pipeName);
        await using var sender = new ActivationRouter("openclaw", pipeName);
        var launchSink = new CapturingSink();
        var forwardedSink = new CapturingSink();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string uri = "openclaw://clawhub/install?id=diagnostics-otel";

        var launchPlan = listener.PlanLaunch(new LaunchActivationInput(
            uri,
            [],
            null,
            false,
            LaunchActivationKind.Protocol));
        Assert.True(await listener.DispatchPlanAsync(launchPlan, launchSink, timeout.Token));

        await listener.StartForwardedActivationListenerAsync(forwardedSink, timeout.Token);
        Assert.True(await ForwardWithRetryAsync(sender, uri, timeout.Token));
        while (forwardedSink.Dispatched.Count == 0)
            await Task.Delay(10, timeout.Token);

        AssertInstallRoute(Assert.Single(launchSink.Dispatched));
        AssertInstallRoute(Assert.Single(forwardedSink.Dispatched));
    }

    private static void AssertInstallRoute(ActivationRoute route)
    {
        var install = Assert.IsType<ActivationRoute.InstallClawHubPlugin>(route);
        Assert.Equal("diagnostics-otel", install.Request.ListingId);
    }

    private static async Task<bool> ForwardWithRetryAsync(
        ActivationRouter sender,
        string uri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await sender.ForwardToPrimaryAsync(uri, cancellationToken))
                return true;
            await Task.Delay(25, cancellationToken);
        }

        return false;
    }

    private sealed class CapturingSink : IActivationPlanSink
    {
        public List<ActivationRoute> Dispatched { get; } = [];

        public Task DispatchAsync(ActivationRoute route, CancellationToken cancellationToken)
        {
            Dispatched.Add(route);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(
            ActivationConfirmation confirmation,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
