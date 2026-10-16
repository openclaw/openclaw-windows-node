namespace OpenClawTray.Pages;

/// <summary>
/// A cron row is latched as running when its scheduled time passes before
/// lastRunAtMs moves. The gateway can later advance nextRunAtMs with
/// runningAtMs still zero. That row is no longer running.
/// </summary>
public static class CronRunningLatch
{
    public static bool ShouldClearBecauseNextRunMoved(
        long runningAtMs,
        long nextRunAtMs,
        long previousNextRunAtMs)
        => runningAtMs <= 0
           && previousNextRunAtMs > 0
           && nextRunAtMs > previousNextRunAtMs;
}
