using System.Runtime.Versioning;
using Microsoft.Win32;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Cleans up tray-specific artifacts that are not created by SetupEngine steps
/// but need removal during a full uninstall. Called after the core rollback pipeline.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayArtifactCleanup
{
    private const string AutoStartKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultAutoStartValue = "OpenClawTray";

    public static void Run(
        SetupContext ctx,
        bool preserveLogs = false,
        string autoStartValue = DefaultAutoStartValue,
        string startupTaskName = WindowsStartupTaskRegistration.TaskName)
    {
        var logger = ctx.Logger;
        var appDataDir = ctx.DataDir; // %APPDATA%\OpenClawTray
        var localDataDir = ctx.LocalDataDir;

        // 1. Remove autostart entries
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, writable: true);
            if (key?.GetValue(autoStartValue) != null)
            {
                key.DeleteValue(autoStartValue);
                logger.Info("[Uninstall] Removed autostart registry key");
            }
            else
            {
                logger.Info("[Uninstall] Autostart registry key already absent");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"[Uninstall] Failed to remove autostart registry key: {ex.Message}");
        }

        if (WindowsStartupTaskRegistration.Unregister(startupTaskName))
            logger.Info("[Uninstall] Removed autostart scheduled task");
        else
            logger.Info("[Uninstall] Autostart scheduled task already absent or unavailable");

        // 2. Delete run.marker
        DeleteFileIfExists(Path.Combine(localDataDir, "run.marker"), "run.marker", logger);

        // 3. Delete exec-policy.json
        DeleteFileIfExists(Path.Combine(appDataDir, "exec-policy.json"), "exec-policy.json", logger);

        // 4. Reset onboarding settings in settings.json
        ResetOnboardingSettings(appDataDir, logger, preserveNodeSettings: HasRemainingGatewayRecords(appDataDir, logger));

        // 5. Optionally delete gateway logs
        if (!preserveLogs)
        {
            DeleteLogsDir(Path.Combine(appDataDir, "Logs"), "AppData Logs", logger);
            DeleteLogsDir(Path.Combine(localDataDir, "Logs"), "LocalAppData Logs", logger);
        }
        else
        {
            logger.Info("[Uninstall] Preserving log directories (--preserve-logs)");
        }
    }

    private static void DeleteFileIfExists(string path, string label, SetupLogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                logger.Info($"[Uninstall] Deleted {label}");
            }
            else
            {
                logger.Info($"[Uninstall] {label} already absent");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"[Uninstall] Failed to delete {label}: {ex.Message}");
        }
    }

    private static void DeleteLogsDir(string dir, string label, SetupLogger logger)
    {
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                logger.Info($"[Uninstall] Deleted {label} directory");
            }
            catch (Exception ex)
            {
                logger.Warn($"[Uninstall] Failed to delete {label} directory: {ex.Message}");
            }
        }
    }

    private static bool HasRemainingGatewayRecords(string appDataDir, SetupLogger logger)
    {
        try
        {
            var registry = new GatewayRegistry(appDataDir);
            registry.Load();
            return registry.GetAll().Count > 0;
        }
        catch (Exception ex)
        {
            logger.Warn($"[Uninstall] Failed to inspect gateway registry: {ex.Message}");
            return false;
        }
    }

    internal static void ResetOnboardingSettings(string appDataDir, SetupLogger logger, bool preserveNodeSettings)
    {
        var settingsPath = Path.Combine(appDataDir, "settings.json");
        if (!File.Exists(settingsPath))
        {
            logger.Info("[Uninstall] settings.json not found: nothing to reset");
            return;
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Build a mutable dictionary and reset onboarding-related fields
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)
                       ?? new Dictionary<string, System.Text.Json.JsonElement>();

            bool changed = false;

            // Reset GatewayUrl to empty
            if (dict.ContainsKey("GatewayUrl"))
            {
                dict.Remove("GatewayUrl");
                changed = true;
            }

            if (!preserveNodeSettings && dict.ContainsKey("EnableNodeMode"))
            {
                dict["EnableNodeMode"] = System.Text.Json.JsonSerializer.SerializeToElement(false);
                changed = true;
            }

            if (!preserveNodeSettings && dict.ContainsKey("AutoStart"))
            {
                dict["AutoStart"] = System.Text.Json.JsonSerializer.SerializeToElement(false);
                changed = true;
            }

            if (changed)
            {
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(dict, SetupConfig.JsonWriteOptions);
                AtomicFile.WriteAllText(settingsPath, updatedJson);
                logger.Info(preserveNodeSettings
                    ? "[Uninstall] Reset onboarding settings (GatewayUrl)"
                    : "[Uninstall] Reset onboarding settings (GatewayUrl, EnableNodeMode, AutoStart)");
            }
            else
            {
                logger.Info("[Uninstall] No onboarding settings to reset");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"[Uninstall] Failed to reset settings: {ex.Message}");
        }
    }
}
