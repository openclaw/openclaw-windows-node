namespace OpenClaw.Shared.Systems;

/// <summary>Usage meters shown under each statistic, as percentages from 0 to 100.</summary>
public static class SystemsMeters
{
    public static double? MemoryUsedPercent(HostMeasurements stats) =>
        Percent(stats.MemoryUsedBytes, stats.MemoryTotalBytes);

    /// <summary>One-minute load as a share of the logical cores; a saturated machine reads 100.</summary>
    public static double? LoadPercent(HostMeasurements stats) =>
        stats.Load1 is { } load && stats.CpuCount > 0 ? Percent(load, stats.CpuCount) : null;

    public static double? DiskUsedPercent(long availableBytes, long? totalBytes) =>
        totalBytes is { } total ? Percent(total - availableBytes, total) : null;

    public static double? Percent(double part, double whole)
    {
        if (!double.IsFinite(part) || !double.IsFinite(whole) || whole <= 0)
            return null;
        return Math.Clamp(part / whole * 100, 0, 100);
    }
}
