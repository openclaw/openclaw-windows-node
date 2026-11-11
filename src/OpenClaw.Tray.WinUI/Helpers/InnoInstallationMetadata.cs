using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace OpenClawTray.Helpers;

[SupportedOSPlatform("windows")]
internal sealed class InnoInstallationMetadata
{
    internal const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1";

    private const string DisplayNamePrefix = "OpenClaw Companion version ";
    private readonly Func<RegistryKey?> _openRegistration;

    internal InnoInstallationMetadata(Func<RegistryKey?>? openRegistration = null)
    {
        _openRegistration = openRegistration ??
            (() => Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true));
    }

    public string? Refresh(string? executablePath, string version,
        bool isPackaged, bool isDev, bool isIsolated)
    {
        if (isPackaged || isDev || isIsolated || string.IsNullOrWhiteSpace(executablePath) ||
            string.IsNullOrWhiteSpace(version) || version == "0.0.0")
            return null;

        try
        {
            if (!Path.IsPathFullyQualified(executablePath) ||
                !string.Equals(Path.GetFileName(executablePath), "OpenClaw.Tray.WinUI.exe",
                    StringComparison.OrdinalIgnoreCase))
                return null;

            var directory = Path.GetDirectoryName(executablePath)!;
            using var key = _openRegistration();
            if (key is null)
                return null;

            var location = ReadString(key, "InstallLocation");
            var displayName = ReadString(key, "DisplayName");
            var displayVersion = ReadString(key, "DisplayVersion");
            if (ReadString(key, "Publisher") != "OpenClaw Foundation" ||
                location is null ||
                !string.Equals(location.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadString(key, "UninstallString"),
                    $"\"{Path.Combine(directory, "unins000.exe")}\"", StringComparison.OrdinalIgnoreCase) ||
                displayName is null || !displayName.StartsWith(DisplayNamePrefix, StringComparison.Ordinal) ||
                displayVersion is null)
                return null;

            if (displayVersion != version)
                key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            var expectedName = DisplayNamePrefix + version;
            if (displayName != expectedName)
                key.SetValue("DisplayName", expectedName, RegistryValueKind.String);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                     SecurityException or ArgumentException or NotSupportedException)
        {
            return $"Installed apps version could not be refreshed ({error.GetType().Name}). " +
                   "Start Companion again or repair the installation with Setup.";
        }
        return null;
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is string text && key.GetValueKind(name) == RegistryValueKind.String ? text : null;
    }
}
