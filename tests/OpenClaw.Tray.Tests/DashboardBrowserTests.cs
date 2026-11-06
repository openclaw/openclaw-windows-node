using System.Collections.Concurrent;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// The final Dashboard hand-off reaches Windows unchanged in ordinary runs, never reaches it in
/// fixture runs, and never writes the credential-bearing URL to the log.
/// </summary>
public sealed class DashboardBrowserTests
{
    private const string Secret = "synthetic-secret";
    private const string DashboardUrl = "http://127.0.0.1:18789/channels#token=" + Secret;

    private static Func<string, string?> Fixture(TempDirectory directory) => name => name switch
    {
        GatewayFixtureIsolation.ModeEnvironmentVariable => "1",
        GatewayFixtureIsolation.DataDirectoryEnvironmentVariable => directory.Path,
        GatewayFixtureIsolation.LocalDataDirectoryEnvironmentVariable => directory.Combine("local"),
        _ => null,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryRun_HandsTheExactUrlToWindowsAndReturnsItsResult(bool windowsOpened)
    {
        var launched = new List<string>();
        var log = new List<string>();
        var browser = new DashboardBrowser(
            url => { launched.Add(url); return Task.FromResult(windowsOpened); }, log.Add, _ => null);

        Assert.Equal(windowsOpened, await browser.OpenAsync(DashboardUrl));

        Assert.Equal([DashboardUrl], launched);
        Assert.Empty(log);
    }

    [Fact]
    public async Task FixtureRun_NeverReachesWindowsAndLogsOnlyTheRedactedUrl()
    {
        using var directory = new TempDirectory();
        var launched = 0;
        var log = new List<string>();
        var browser = new DashboardBrowser(
            _ => { launched++; return Task.FromResult(true); }, log.Add, Fixture(directory));

        Assert.True(await browser.OpenAsync(DashboardUrl));

        Assert.Equal(0, launched);
        Assert.Equal(DashboardBrowser.SuppressedLaunchLogPrefix + "http://127.0.0.1:18789/channels",
            Assert.Single(log));
    }

    [Fact]
    public async Task FixtureRun_SimulatedFailureIsConsumedOnceThenLaunchesAreSuppressed()
    {
        using var directory = new TempDirectory();
        var marker = directory.Combine(DashboardBrowser.SimulatedFailureMarkerFileName);
        File.WriteAllText(marker, string.Empty);
        var launched = 0;
        var log = new List<string>();
        var browser = new DashboardBrowser(
            _ => { launched++; return Task.FromResult(true); }, log.Add, Fixture(directory));

        Assert.False(await browser.OpenAsync(DashboardUrl));
        Assert.False(File.Exists(marker));
        Assert.True(await browser.OpenAsync(DashboardUrl));

        Assert.Equal(0, launched);
        Assert.Collection(log,
            first => Assert.StartsWith(DashboardBrowser.SimulatedFailureLogPrefix, first),
            second => Assert.StartsWith(DashboardBrowser.SuppressedLaunchLogPrefix, second));
        Assert.All(log, entry => Assert.DoesNotContain(Secret, entry));
    }

    [Fact]
    public async Task FixtureRun_ConcurrentLaunchesConsumeTheFailureMarkerExactlyOnce()
    {
        const int rounds = 25;
        const int concurrentLaunches = 8;
        using var directory = new TempDirectory();
        var marker = directory.Combine(DashboardBrowser.SimulatedFailureMarkerFileName);
        var log = new ConcurrentQueue<string>();
        var browser = new DashboardBrowser(_ => Task.FromResult(true), log.Enqueue, Fixture(directory));

        for (var round = 0; round < rounds; round++)
        {
            File.WriteAllText(marker, string.Empty);
            using var gate = new Barrier(concurrentLaunches);
            var launches = Enumerable.Range(0, concurrentLaunches).Select(_ => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                return browser.OpenAsync(DashboardUrl);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();

            Assert.Single(await Task.WhenAll(launches), opened => !opened);
        }

        Assert.Equal(rounds, log.Count(entry => entry.StartsWith(
            DashboardBrowser.SimulatedFailureLogPrefix, StringComparison.Ordinal)));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task OrdinaryRun_IgnoresTheFailureMarker()
    {
        using var directory = new TempDirectory();
        var marker = directory.Combine(DashboardBrowser.SimulatedFailureMarkerFileName);
        File.WriteAllText(marker, string.Empty);
        var browser = new DashboardBrowser(_ => Task.FromResult(true), _ => { }, _ => null);

        Assert.True(await browser.OpenAsync(DashboardUrl));

        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task InvalidFixtureContext_FailsClosedWithoutReachingWindows()
    {
        var launched = 0;
        var browser = new DashboardBrowser(
            _ => { launched++; return Task.FromResult(true); }, _ => { },
            name => name == GatewayFixtureIsolation.ModeEnvironmentVariable ? "1" : null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.OpenAsync(DashboardUrl));

        Assert.Equal(0, launched);
    }
}
