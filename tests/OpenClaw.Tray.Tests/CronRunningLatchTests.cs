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
