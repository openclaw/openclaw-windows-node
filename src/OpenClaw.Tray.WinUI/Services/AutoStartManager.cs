using Microsoft.Win32;
using System;

namespace OpenClawTray.Services;

/// <summary>
/// Manages Windows auto-start registry entries.
/// </summary>
public static class AutoStartManager
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "OpenClawTray";

    public static bool IsAutoStartEnabled()
    {
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
                Logger.Info("Auto-start enabled");
            }
            else
            {
                key.DeleteValue(AppName, false);
                Logger.Info("Auto-start disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to set auto-start: {ex.Message}");
        }
    }
}
