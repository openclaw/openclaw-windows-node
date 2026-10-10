using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public class CronRunningLatchTests
{
    [Fact]
    public void NextRunMovedAndGatewaySaysNotRunning_ClearsTheLatch()
    {
        Assert.True(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 2_000,
            previousNextRunAtMs: 1_000));
    }

    [Fact]
    public void GatewayStillReportsRunning_KeepsTheLatch()
    {
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 1_500,
            nextRunAtMs: 2_000,
            previousNextRunAtMs: 1_000));
    }

    [Fact]
    public void NextRunUnchanged_KeepsTheLatch()
    {
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 1_000,
            previousNextRunAtMs: 1_000));
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 1_000,
            previousNextRunAtMs: 0));
    }

    [Fact]
    public void CronPage_ClearsTheLatchWhenTheNextRunMoves()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));

        Assert.Contains("CronRunningLatch.ShouldClearBecauseNextRunMoved", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
