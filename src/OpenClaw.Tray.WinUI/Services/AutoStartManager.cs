using Microsoft.Win32;
using OpenClaw.Shared;
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
            if (key?.GetValue(AppName) != null)
                return true;
        }
        catch
        {
        }

        return WindowsStartupTaskRegistration.Exists();
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            if (enable)
            {
                var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (WindowsStartupTaskRegistration.Register(exePath))
                {
                    DeleteRunKey();
                    Logger.Info("Auto-start enabled via scheduled task");
                    return;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, true);
                if (key == null)
                {
                    Logger.Warn($"Auto-start registry key unavailable: HKCU\\{RegistryKey}");
                    return;
                }

                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Info("Auto-start enabled");
            }
            else
            {
                DeleteRunKey();
                WindowsStartupTaskRegistration.Unregister();
                Logger.Info("Auto-start disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to set auto-start: {ex.Message}");
        }
    }

    private static void DeleteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
            key?.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to remove auto-start registry key: {ex.Message}");
        }
    }
}
