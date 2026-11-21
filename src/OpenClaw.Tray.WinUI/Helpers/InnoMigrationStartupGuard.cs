using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class InnoMigrationStartupGuard
{
    public static bool ShouldStopLaunch(out IDisposable? runtimeLease)
    {
        if (!TryGetStopGuidance(out runtimeLease, out var resourceKey))
            return false;
        ShowGuidance(resourceKey);
        return true;
    }

    /// <summary>
    /// Non-interactive migration decision for normal startup and headless setup. The caller retains
    /// <paramref name="runtimeLease"/> until process exit.
    /// </summary>
    internal static bool TryGetStopGuidance(out IDisposable? runtimeLease, [NotNullWhen(true)] out string? resourceKey)
    {
        runtimeLease = null;
        resourceKey = null;
#if !INNO_MIGRATION_PREVIEW && !PRODUCTION_MIGRATION
        return false;
#else
        if (AppIdentity.IsDev || PackageHelper.IsPackaged || GatewayFixtureIsolation.IsEnabled)
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var binding = new MigrationBinding
        {
            InstallDirectory = AppContext.BaseDirectory,
            RoamingDirectory = AppIdentity.ResolveRoamingDataDirectory(),
            LocalDirectory = AppIdentity.ResolveSetupLocalDataDirectory(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            UserSid = identity.User?.Value ?? ""
        };
        resourceKey = new InnoMigrationStartupAdmission(binding, new AppLogger()).Evaluate(out runtimeLease) switch
        {
            InnoMigrationStartupDecision.Completed => "Migration_InnoCompleted",
            InnoMigrationStartupDecision.CheckFailed => "Migration_InnoCheckFailed",
            _ => null,
        };
        return resourceKey is not null;
#endif
    }

    private static void ShowGuidance(string resourceKey)
    {
        if (MessageBoxW(IntPtr.Zero, LocalizationHelper.GetString(resourceKey),
                AppIdentity.DisplayName, 0x00000040) == 0)
            Logger.Error($"Could not show migration startup guidance (Win32 {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
