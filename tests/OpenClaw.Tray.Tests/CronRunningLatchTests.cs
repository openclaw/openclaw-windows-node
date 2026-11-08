using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public class CronRunningLatchTests
{
    [Fact]
    public void StaleRunToken_DoesNotMatchNewerRunForSameJob()
    {
        var tokens = new CronRunTokenRegistry();

        var firstRun = tokens.BeginRun("job-1");
        tokens.ClearRun("job-1");
        var secondRun = tokens.BeginRun("job-1");

        Assert.False(tokens.IsCurrentRun("job-1", firstRun));
        Assert.True(tokens.IsCurrentRun("job-1", secondRun));
    }

    [Fact]
    public void ClearingRunToken_PreventsItsTimeoutFromMatching()
    {
        var tokens = new CronRunTokenRegistry();
        var run = tokens.BeginRun("job-1");

        tokens.ClearRun("job-1");

        Assert.False(tokens.IsCurrentRun("job-1", run));
    }

    [Fact]
    public void HistoryRunId_MustMatchTheCurrentRun()
    {
        var tokens = new CronRunTokenRegistry();
        var firstRun = tokens.BeginRun("job-1");
        tokens.SetRunId("job-1", firstRun, "run-1");
        tokens.ClearRun("job-1");
        var secondRun = tokens.BeginRun("job-1");
        tokens.SetRunId("job-1", secondRun, "run-2");

        Assert.False(tokens.MatchesRunId("job-1", "run-1"));
        Assert.True(tokens.MatchesRunId("job-1", "run-2"));
    }

    [Fact]
    public void StaleHistoryWithoutRunId_DoesNotMatchNewerRun()
    {
        var tokens = new CronRunTokenRegistry();
        var firstRun = tokens.BeginRun("job-1");
        tokens.SetRunId("job-1", firstRun, "run-1");
        tokens.ClearRun("job-1");
        var secondRun = tokens.BeginRun("job-1");

        Assert.True(tokens.IsCurrentRun("job-1", secondRun));
        Assert.False(tokens.MatchesRunId("job-1", null));
        Assert.False(tokens.MatchesRunId("job-1", "run-1"));
    }

    [Fact]
    public void LegacyAcceptedRunWithoutRunId_RemainsCurrentForFallback()
    {
        var tokens = new CronRunTokenRegistry();
        var run = tokens.BeginRun("job-1");
        tokens.SetRunId("job-1", run, null);

        Assert.True(tokens.IsCurrentRun("job-1", run));
        Assert.False(tokens.MatchesRunId("job-1", null));

        tokens.ClearRun("job-1");

        Assert.False(tokens.IsCurrentRun("job-1", run));
    }

    [Fact]
    public void StaleToken_CannotSetTheCurrentRunId()
    {
        var tokens = new CronRunTokenRegistry();
        var firstRun = tokens.BeginRun("job-1");
        var secondRun = tokens.BeginRun("job-1");

        tokens.SetRunId("job-1", firstRun, "run-1");
        tokens.SetRunId("job-1", secondRun, "run-2");

        Assert.False(tokens.MatchesRunId("job-1", "run-1"));
        Assert.True(tokens.MatchesRunId("job-1", "run-2"));
    }

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
    public void NextRunDoesNotAdvance_KeepsTheLatch()
    {
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 1_000,
            previousNextRunAtMs: 1_000));
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 500,
            previousNextRunAtMs: 1_000));
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 0,
            previousNextRunAtMs: 1_000));
        Assert.False(CronRunningLatch.ShouldClearBecauseNextRunMoved(
            runningAtMs: 0,
            nextRunAtMs: 1_000,
            previousNextRunAtMs: 0));
    }

}
