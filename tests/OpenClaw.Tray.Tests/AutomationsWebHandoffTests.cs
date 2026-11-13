using OpenClaw.Connection;
using OpenClaw.Shared.Cron;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Automations "Open in web" goes through the shared Dashboard launcher with
/// <see cref="CronProtocol.WebPath"/>. The browser delegate is intercepted, so
/// nothing is launched.
/// </summary>
public sealed class AutomationsWebHandoffTests
{
    [Theory]
    // Section fallback: no job selected (and what older Gateways show for any link).
    [InlineData("wss://gateway.example/", null, "https://gateway.example/cron")]
    // Exact job link on a Gateway served under a mounted path.
    [InlineData("wss://gateway.example/control/", "morning-brief", "https://gateway.example/control/cron?job=morning-brief")]
    // Reserved characters stay inside the job parameter, never starting a fragment or a new parameter.
    [InlineData("ws://127.0.0.1:18789", "a b/c&d#e?f", "http://127.0.0.1:18789/cron?job=a%20b%2Fc%26d%23e%3Ff")]
    public async Task OpenInWeb_LaunchesTheAutomationsRouteThroughTheDashboardLauncher(string gatewayUrl, string? jobId, string expected)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(gatewayUrl, "shared secret", false, CredentialResolver.SourceSharedGatewayToken),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));

        Assert.True(await launcher.OpenAsync(CronProtocol.WebPath(jobId)));
        // Shared-token sign-in stays in the fragment, after the route and job query.
        Assert.Equal(expected + "#token=shared%20secret", launched);
        Assert.Equal(jobId, ParseJob(launched!));
    }

    [Theory]
    [InlineData(CredentialResolver.SourceDeviceToken, false)]
    [InlineData(CredentialResolver.SourceBootstrapToken, true)]
    public async Task OpenInWeb_NeverExportsDeviceOrBootstrapTokens(string source, bool bootstrap)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new("wss://gateway.example/control", "do-not-export", bootstrap, source),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));

        Assert.True(await launcher.OpenAsync(CronProtocol.WebPath("job-1")));
        Assert.Equal("https://gateway.example/control/cron?job=job-1", launched);
    }

    [Fact]
    public async Task OpenInWeb_FailureIsReportedWithoutRetrying()
    {
        var launches = 0;
        var failures = 0;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new("wss://gateway.example", "shared", false, CredentialResolver.SourceSharedGatewayToken),
            _ => { launches++; return Task.FromResult(false); },
            () => failures++);

        Assert.False(await launcher.OpenAsync(CronProtocol.WebPath("job-1")));
        Assert.Equal((1, 1), (launches, failures));
    }

    private static string? ParseJob(string url)
    {
        var uri = new Uri(url);
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return null;
        var pair = Assert.Single(query.Split('&'));
        Assert.StartsWith("job=", pair, StringComparison.Ordinal);
        return Uri.UnescapeDataString(pair["job=".Length..]);
    }
}
