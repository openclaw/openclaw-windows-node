using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OpenClawTray.Presentation;

/// <summary>
/// WinUI-free projection of the "About / app info" strings the settings surface displays.
/// It holds the pure formatting and environment/reflection lookups that used to live inline in
/// the settings page code-behind, so they are unit-testable. The one genuinely WinUI-bound value
/// (the WinUI display name, resolved from <c>Microsoft.UI.Xaml.Application</c>'s assembly) is
/// passed in by the view; everything else is computed here.
/// </summary>
public static class SettingsAppInfoProjection
{
    private const string PackagedInstallText = "Packaged (MSIX)";
    private const string UnpackagedInstallText = "Unpackaged (developer)";
    private const string DefaultChannel = "stable";

    /// <summary>Composes the "runtime / WinUI / Windows App SDK" one-line stack description.</summary>
    public static string BuildRuntimeStack(string frameworkDescription, string winUiDisplayName, string windowsAppSdkDisplayName) =>
        $"{frameworkDescription} / {winUiDisplayName} / {windowsAppSdkDisplayName}";

    /// <summary>Maps the packaged flag to the installation-kind label.</summary>
    public static string InstallKind(bool isPackaged) => isPackaged ? PackagedInstallText : UnpackagedInstallText;

    /// <summary>Resolves the update-channel label, defaulting to <c>stable</c> when unset.</summary>
    public static string ResolveUpdateChannel(string? channelEnvironmentValue) =>
        string.IsNullOrWhiteSpace(channelEnvironmentValue) ? DefaultChannel : channelEnvironmentValue.Trim();

    /// <summary>Strips a <c>+buildmetadata</c> suffix from a semantic version string.</summary>
    public static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>
    /// Formats the entry assembly's last-write time as the displayed build date, or null when the
    /// location is missing/unreadable (matching the previous "hide the build row" behavior).
    /// </summary>
    public static string? FormatBuildDate(string? entryAssemblyLocation, CultureInfo culture)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyLocation) || !File.Exists(entryAssemblyLocation))
            {
                return null;
            }

            return File.GetLastWriteTime(entryAssemblyLocation).ToString("MMM d, yyyy", culture);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the Windows App SDK display name, preferring the package version recorded in the
    /// entry assembly's <c>.deps.json</c> and falling back to the native XAML dll's file version.
    /// </summary>
    public static string ResolveWindowsAppSdkDisplayName(string? entryAssemblyName, string baseDirectory)
    {
        if (TryResolveWindowsAppSdkPackageVersionFromDeps(entryAssemblyName, baseDirectory) is { Length: > 0 } packageVersion)
        {
            return $"Windows App SDK {packageVersion}";
        }

        return ResolveWindowsAppSdkDisplayNameFromFileVersion(baseDirectory);
    }

    private static string ResolveWindowsAppSdkDisplayNameFromFileVersion(string baseDirectory)
    {
        var xamlNativePath = Path.Combine(baseDirectory, "Microsoft.ui.xaml.dll");
        if (File.Exists(xamlNativePath))
        {
            try
            {
                var productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(xamlNativePath).ProductVersion;
                if (!string.IsNullOrWhiteSpace(productVersion))
                {
                    return $"Windows App SDK {StripBuildMetadata(productVersion)}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Fall through to the generic label.
            }
        }

        return "Windows App SDK";
    }

    private static string? TryResolveWindowsAppSdkPackageVersionFromDeps(string? entryAssemblyName, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyName))
        {
            return null;
        }

        var depsPath = Path.Combine(baseDirectory, $"{entryAssemblyName}.deps.json");
        if (!File.Exists(depsPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(depsPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                const string packagePrefix = "Microsoft.WindowsAppSDK/";
                if (library.Name.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return StripBuildMetadata(library.Name[packagePrefix.Length..]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Formats a duration the way the gateway-uptime row displays it.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }
}
