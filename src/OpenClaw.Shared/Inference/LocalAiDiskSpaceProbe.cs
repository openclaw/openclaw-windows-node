using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Inference;

/// <summary>Installation destinations, including whether their budgets consume the same volume.</summary>
public sealed record LocalAiDiskSpaceInfo(long RuntimeAvailableBytes, long ModelCacheAvailableBytes, bool SharesVolume)
{
    public long AvailableBytes => Math.Min(RuntimeAvailableBytes, ModelCacheAvailableBytes);
    public long RequiredBytes(long perDestinationBytes) => SharesVolume ? checked(2 * perDestinationBytes) : perDestinationBytes;
}

internal static class LocalAiDiskSpaceProbe
{
    public static LocalAiDiskSpaceInfo? TryRead()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            string runtimeRoot = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var runtimeVolume = ReadVolume(runtimeRoot);
            var cacheVolume = ReadVolume(HuggingFaceHubCache.ResolveCacheRoot());
            return new(runtimeVolume.AvailableFreeSpace, cacheVolume.AvailableFreeSpace,
                string.Equals(runtimeVolume.Name, cacheVolume.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static DriveInfo ReadVolume(string path) =>
        new(Path.GetPathRoot(Path.GetFullPath(path))!);
}
