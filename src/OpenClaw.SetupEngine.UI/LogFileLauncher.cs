using System.Diagnostics;
using OpenClaw.Shared.IO;

namespace OpenClaw.SetupEngine.UI;

internal static class LogFileLauncher
{
    public static string ResolveRealPath(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return logPath;

        try
        {
            // Package identity alone does not mean this path was redirected. Resolve
            // the actual file/directory so Explorer sees the same object as setup.
            return WindowsExistingPathResolver.Resolve(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"LogFileLauncher.ResolveRealPath: {ex.GetType().Name}: {ex.Message}");
            return logPath;
        }
    }

    public static void RevealInExplorer(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        var realPath = ResolveRealPath(logPath);

        try
        {
            if (File.Exists(realPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{realPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            // Callers may pass a directory (the Local AI log folder) rather than a single file;
            // opening its parent would show the wrong place.
            if (Directory.Exists(realPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{realPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var dir = Path.GetDirectoryName(realPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            // best effort — the link is informational; if shell open fails
            // the user can navigate to the log path manually.
            System.Diagnostics.Trace.WriteLine($"LogFileLauncher.OpenContainingFolder: {ex.GetType().Name}: {ex.Message}");
        }
    }

}
