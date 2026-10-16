namespace OpenClaw.Shared;

/// <summary>
/// Delay between tray-log open retries. A locked file must not make the writer
/// spin on <c>WaitToReadAsync</c> while the failed line is still queued.
/// </summary>
public static class LoggerWriteBackoff
{
    public static TimeSpan Next(int consecutiveFailures)
    {
        var shift = Math.Clamp(consecutiveFailures, 0, 5);
        return TimeSpan.FromMilliseconds(50 * (1 << shift));
    }
}
