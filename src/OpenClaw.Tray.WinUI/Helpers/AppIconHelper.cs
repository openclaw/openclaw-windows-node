using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

public static class AppIconHelper
{
    private static readonly string AssetsPath = ResolveAssetsPath();
    private static readonly string IconsPath = Path.Combine(AssetsPath, "Icons");

    public static string GetStatusIconPath(ConnectionStatus status)
    {
        var iconName = status switch
        {
            ConnectionStatus.Connected => "StatusConnected.ico",
            ConnectionStatus.Connecting => "StatusConnecting.ico",
            ConnectionStatus.Error => "StatusError.ico",
            _ => "StatusDisconnected.ico"
        };

        var path = Path.Combine(IconsPath, iconName);
        if (!File.Exists(path))
        {
            path = GetAppIconPath();
        }

        return path;
    }

    public static string GetAppIconPath()
    {
        return Path.Combine(AssetsPath, "openclaw.ico");
    }

    private static string ResolveAssetsPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Directory.Exists(bundledPath))
        {
            return bundledPath;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var sourcePath = Path.Combine(current.FullName, "src", "OpenClaw.Tray.WinUI", "Assets");
            if (Directory.Exists(sourcePath))
            {
                return sourcePath;
            }

            current = current.Parent;
        }

        return bundledPath;
    }
}
