using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Inference;

internal static class LocalAiDiskSpaceProbe
{
    // Both destinations must have the profile's reserve. Taking the lower budget
    // is conservative when the runtime and model cache live on different volumes.
    public static long? TryRead()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            string runtimeRoot = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Math.Min(ReadVolume(runtimeRoot), ReadVolume(HuggingFaceHubCache.ResolveCacheRoot()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static long ReadVolume(string path) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
}
