namespace OpenClawTray;

/// <summary>
/// Compile-time app identity constants that vary between Dev and Release builds,
/// enabling side-by-side installation of both variants (similar to WinUI Gallery).
/// </summary>
internal static class AppIdentity
{
#if DEV_BUILD
    /// <summary>Human-visible app name shown in tray tooltips, window titles, and notifications.</summary>
    public const string DisplayName = "OpenClaw Companion (Dev)";

    /// <summary>Short name used in tray tooltip prefix.</summary>
    public const string TrayName = "OpenClaw Tray (Dev)";

    /// <summary>MSIX package identity name (must differ from release for side-by-side).</summary>
    public const string PackageIdentityName = "OpenClaw.Companion.Dev";

    /// <summary>Win32 AppUserModelID used for notifications and shell grouping.</summary>
    public const string AppUserModelId = PackageIdentityName;

    /// <summary>Windows Registry auto-start value name (must differ so both can auto-start).</summary>
    public const string AutoStartRegistryName = "OpenClawTray-Dev";

    /// <summary>Windows scheduled task name (must differ so both can auto-start).</summary>
    public const string StartupTaskName = "OpenClaw Companion (Dev)";

    /// <summary>Leaf directory for local and roaming app-owned data.</summary>
    public const string DataDirectoryName = "OpenClawTray-Dev";

    /// <summary>Single-instance mutex base name.</summary>
    public const string MutexBaseName = "OpenClawTray-Dev";

    /// <summary>Protocol scheme for deep links.</summary>
    public const string ProtocolScheme = "openclaw-dev";

    /// <summary>App-owned WSL distro used by embedded setup.</summary>
    public const string SetupDistroName = "OpenClawGateway-Dev";

    /// <summary>Loopback gateway port used by embedded setup.</summary>
    public const int SetupGatewayPort = 18790;

    /// <summary>Default gateway URL for this app variant.</summary>
    public const string SetupGatewayUrl = "ws://localhost:18790";

    /// <summary>Whether this is a development build.</summary>
    public static bool IsDev => true;
#else
    /// <summary>Human-visible app name shown in tray tooltips, window titles, and notifications.</summary>
    public const string DisplayName = "OpenClaw Companion";

    /// <summary>Short name used in tray tooltip prefix.</summary>
    public const string TrayName = "OpenClaw Tray";

    /// <summary>MSIX package identity name.</summary>
    public const string PackageIdentityName = "OpenClaw.Companion";

    /// <summary>Win32 AppUserModelID used for notifications and shell grouping.</summary>
    public const string AppUserModelId = PackageIdentityName;

    /// <summary>Windows Registry auto-start value name.</summary>
    public const string AutoStartRegistryName = "OpenClawTray";

    /// <summary>Windows scheduled task name.</summary>
    public const string StartupTaskName = "OpenClaw Companion";

    /// <summary>Leaf directory for local and roaming app-owned data.</summary>
    public const string DataDirectoryName = "OpenClawTray";

    /// <summary>Single-instance mutex base name.</summary>
    public const string MutexBaseName = "OpenClawTray";

    /// <summary>Protocol scheme for deep links.</summary>
    public const string ProtocolScheme = "openclaw";

    /// <summary>App-owned WSL distro used by embedded setup.</summary>
    public const string SetupDistroName = "OpenClawGateway";

    /// <summary>Loopback gateway port used by embedded setup.</summary>
    public const int SetupGatewayPort = 18789;

    /// <summary>Default gateway URL for this app variant.</summary>
    public const string SetupGatewayUrl = "ws://localhost:18789";

    /// <summary>Whether this is a development build.</summary>
    public static bool IsDev => false;
#endif

    public static string ResolveLocalDataDirectory()
        => Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DataDirectoryName);

    /// <summary>
    /// Resolves setup-owned local state while preserving SetupEngine's dedicated
    /// local-data override contract.
    /// </summary>
    public static string ResolveSetupLocalDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR") is { Length: > 0 } localAppDataRoot)
            return Path.Combine(localAppDataRoot, DataDirectoryName);

        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR") is { Length: > 0 } localDataDir)
            return localDataDir;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataDirectoryName);
    }

    public static string ResolveRoamingDataDirectory()
        => Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                DataDirectoryName);

    public static string DecorateWindowTitle(string title)
        => IsDev ? $"{title} (Dev)" : title;
}
