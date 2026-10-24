using System.Runtime.CompilerServices;
using OpenClawTray;

namespace OpenClaw.Tray.Tests;

internal static class TrayTestProfileIsolation
{
    internal const string DataVariable = "OPENCLAW_TRAY_DATA_DIR";
    internal const string RoamingVariable = "OPENCLAW_TRAY_APPDATA_DIR";
    internal const string LocalVariable = "OPENCLAW_TRAY_LOCALAPPDATA_DIR";
    internal const string LegacyLocalVariable = "OPENCLAW_TRAY_LOCAL_DATA_DIR";

    // Run before linked production types can cache default profile paths.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
        var data = GetOverride(DataVariable) ?? Path.Combine(root, "Data");
        var roaming = GetOverride(RoamingVariable) ?? Path.Combine(root, "Roaming");
        var local = GetOverride(LocalVariable);
        var legacyLocal = GetOverride(LegacyLocalVariable);

        // Filling the modern root would shadow a deliberately supplied legacy direct path.
        if (local is null && legacyLocal is null)
            local = Path.Combine(root, "Local");
        legacyLocal ??= Path.Combine(local!, AppIdentity.DataDirectoryName);

        ValidateDirectory(DataVariable, data);
        ValidateDirectory(RoamingVariable, roaming);
        if (local is not null)
            ValidateDirectory(LocalVariable, local);
        ValidateDirectory(LegacyLocalVariable, legacyLocal);

        Environment.SetEnvironmentVariable(DataVariable, data);
        Environment.SetEnvironmentVariable(RoamingVariable, roaming);
        Environment.SetEnvironmentVariable(LocalVariable, local);
        Environment.SetEnvironmentVariable(LegacyLocalVariable, legacyLocal);
    }

    private static string? GetOverride(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static void ValidateDirectory(string name, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{name} must be an absolute, isolated test directory.");

        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData
                 })
        {
            var realRoot = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(realRoot))
                continue;

            realRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(realRoot));
            if (candidate.Equals(realRoot, comparison))
                throw new InvalidOperationException($"{name} points to a real user profile root.");

            foreach (var profileName in new[] { "OpenClawTray", "OpenClawTray-Dev" })
            {
                var realProfile = Path.Combine(realRoot, profileName);
                if (candidate.Equals(realProfile, comparison) ||
                    candidate.StartsWith(realProfile + Path.DirectorySeparatorChar, comparison))
                {
                    throw new InvalidOperationException($"{name} points to a real tray profile.");
                }
            }
        }
    }
}
