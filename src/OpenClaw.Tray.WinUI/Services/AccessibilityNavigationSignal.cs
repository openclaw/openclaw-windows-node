using System.Security;

namespace OpenClawTray.Services;

internal static class AccessibilityNavigationSignal
{
    private const string SignalPathEnvironmentVariable =
        "OPENCLAW_ACCESSIBILITY_NAVIGATION_SIGNAL";

    internal static void WritePageReady(string? pageName)
    {
        var signalPath = Environment.GetEnvironmentVariable(
            SignalPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(signalPath) ||
            string.IsNullOrWhiteSpace(pageName))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(signalPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using var stream = new FileStream(
                signalPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"{Guid.NewGuid():N}\t{pageName}");
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException)
        {
            Logger.Warn(
                $"Accessibility navigation readiness signal failed: {ex.Message}");
        }
    }
}
