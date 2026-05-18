using Microsoft.Win32;
using OpenClawTray.Helpers;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Manages "Launch when Windows starts" for the tray.
///
/// For MSIX-packaged installs (the shipping channel) the only correct API is
/// <c>Windows.ApplicationModel.StartupTask</c>. The corresponding
/// <c>windows.startupTask</c> extension is declared in <c>Package.appxmanifest</c>
/// with <c>TaskId="OpenClawCompanionStartup"</c> and <c>Enabled="false"</c>; the
/// user opts in via Settings, which surfaces the one-time Windows consent dialog
/// (and which the user can subsequently revoke via Task Manager → Startup).
///
/// For unpackaged dev / debug builds we fall back to the legacy
/// <c>HKCU\...\Run</c> entry. The two paths are not interchangeable: an MSIX
/// install must NEVER write to <c>HKCU\...\Run</c> because (a) Windows ignores
/// it under MSIX governance and (b) the entry orphans when the package is
/// removed.
/// </summary>
public static class AutoStartManager
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "OpenClawTray";

    /// <summary>
    /// StartupTask TaskId. Must match the <c>TaskId</c> attribute in
    /// <c>Package.appxmanifest</c> under <c>windows.startupTask</c>.
    /// </summary>
    internal const string StartupTaskId = "OpenClawCompanionStartup";

    public static bool IsAutoStartEnabled()
    {
        if (PackageHelper.IsPackaged)
        {
            try
            {
                var task = global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
                return task.State == global::Windows.ApplicationModel.StartupTaskState.Enabled
                    || task.State == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            catch (Exception ex)
            {
                Logger.Warn($"StartupTask query failed (packaged): {ex.Message}");
                return false;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        if (PackageHelper.IsPackaged)
        {
            _ = SetAutoStartPackagedAsync(enable);
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, true);
            if (key == null)
            {
                Logger.Warn($"Auto-start registry key unavailable: HKCU\\{RegistryKey}");
                return;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Info("Auto-start enabled (unpackaged, HKCU\\...\\Run)");
            }
            else
            {
                key.DeleteValue(AppName, false);
                Logger.Info("Auto-start disabled (unpackaged, HKCU\\...\\Run)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to set auto-start (unpackaged): {ex.Message}");
        }
    }

    private static async Task SetAutoStartPackagedAsync(bool enable)
    {
        try
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (enable)
            {
                // RequestEnableAsync surfaces the one-time consent prompt on first call
                // and returns the resulting state. DisabledByUser / DisabledByPolicy mean
                // the user revoked it via Task Manager and the toggle is essentially
                // read-only until they re-enable it there.
                var state = await task.RequestEnableAsync();
                Logger.Info($"StartupTask enable requested → state={state}");
            }
            else
            {
                task.Disable();
                Logger.Info("StartupTask disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to set auto-start (packaged): {ex.Message}");
        }
    }
}
