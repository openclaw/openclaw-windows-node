using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace OpenClawTray.Helpers;

internal enum InnoMetadataRefreshOutcome
{
    Skipped,
    AlreadyCurrent,
    Refreshed,
    Failed,
}

/// <summary>
/// The startup log line for one refresh attempt. Every outcome names its reason, so a
/// field report can tell which guard kept the registration unchanged.
/// </summary>
internal readonly record struct InnoMetadataRefreshResult(InnoMetadataRefreshOutcome Outcome, string Message)
{
    public bool IsWarning => Outcome == InnoMetadataRefreshOutcome.Failed;
}

/// <summary>
/// Refreshes the display metadata that Inno cannot update when Updatum replaces files.
/// The installer still owns registration creation and all uninstall behavior.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InnoInstallationMetadata
{
    internal const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1";

    private const string DisplayNamePrefix = "OpenClaw Companion version ";
    private const string Publisher = "OpenClaw Foundation";
    private const string ExecutableName = "OpenClaw.Tray.WinUI.exe";
    private readonly Func<RegistryKey?> _openRegistration;

    // The registry opener is the OS boundary; tests use a task-owned HKCU subtree.
    internal InnoInstallationMetadata(Func<RegistryKey?>? openRegistration = null)
    {
        _openRegistration = openRegistration ??
            (() => Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true));
    }

    public InnoMetadataRefreshResult Refresh(string? executablePath, string version,
        bool isPackaged, bool isDev, bool isIsolated)
    {
        if (isPackaged)
            return Skip("the app runs with package identity");
        if (isDev)
            return Skip("this is a Dev build");
        if (isIsolated)
            return Skip("OPENCLAW_TRAY_DATA_DIR isolates this instance");
        if (string.IsNullOrWhiteSpace(executablePath))
            return Skip("the process path is unknown");
        if (string.IsNullOrWhiteSpace(version) || version == "0.0.0")
            return Skip($"the app version is unknown ('{version}')");

        try
        {
            if (!Path.IsPathFullyQualified(executablePath) ||
                !string.Equals(Path.GetFileName(executablePath), ExecutableName, StringComparison.OrdinalIgnoreCase))
                return Skip($"the process '{executablePath}' is not an installed {ExecutableName}");

            var directory = Path.GetDirectoryName(executablePath)!;
            using var key = _openRegistration();
            if (key is null)
                return Skip("no Inno Setup registration exists"); // Portable runs never create one.

            var publisher = ReadString(key, "Publisher");
            var location = ReadString(key, "InstallLocation");
            var uninstall = ReadString(key, "UninstallString");
            var expectedUninstall = $"\"{Path.Combine(directory, "unins000.exe")}\"";
            var displayName = ReadString(key, "DisplayName");
            var displayVersion = ReadString(key, "DisplayVersion");
            if (publisher != Publisher)
                return Skip($"Publisher {Describe(publisher)} is not '{Publisher}'");
            if (location is null ||
                !string.Equals(location.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return Skip($"InstallLocation {Describe(location)} is not the running folder '{directory}'");
            if (!string.Equals(uninstall, expectedUninstall, StringComparison.OrdinalIgnoreCase))
                return Skip($"UninstallString {Describe(uninstall)} is not {expectedUninstall}");
            if (displayName is null || !displayName.StartsWith(DisplayNamePrefix, StringComparison.Ordinal))
                return Skip($"DisplayName {Describe(displayName)} does not start with '{DisplayNamePrefix}'");
            if (displayVersion is null)
                return Skip("DisplayVersion is missing or not a string");

            var expectedName = DisplayNamePrefix + version;
            if (displayVersion == version && displayName == expectedName)
                return new(InnoMetadataRefreshOutcome.AlreadyCurrent,
                    $"Installed apps registration already shows {version}.");

            // Do not use migration admission here: it intentionally rejects the stale
            // version this startup repair owns. Never rewrite identity or uninstall fields.
            // Separate value writes may be interrupted; the next startup repairs either field.
            if (displayVersion != version)
                key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            if (displayName != expectedName)
                key.SetValue("DisplayName", expectedName, RegistryValueKind.String);
            return new(InnoMetadataRefreshOutcome.Refreshed,
                $"Installed apps registration refreshed from {displayVersion} to {version}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                     SecurityException or ArgumentException or NotSupportedException)
        {
            return new(InnoMetadataRefreshOutcome.Failed,
                $"Installed apps version could not be refreshed ({error.GetType().Name}). " +
                "Start Companion again or repair the installation with Setup.");
        }
    }

    private static InnoMetadataRefreshResult Skip(string reason) =>
        new(InnoMetadataRefreshOutcome.Skipped, $"Installed apps registration not refreshed: {reason}.");

    private static string Describe(string? value) => value is null ? "(missing or not a string)" : $"'{value}'";

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is string text && key.GetValueKind(name) == RegistryValueKind.String ? text : null;
    }
}
