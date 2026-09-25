using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class InnoMigrationStartupGuard
{
    public static bool ShouldStopLaunch(out IDisposable? runtimeLease)
    {
        runtimeLease = null;
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
        // Acquisition must precede the receipt check, including when no receipt exists yet.
        // App retains this handle until process exit, even if startup or shutdown fails.
        // An unavailable lock says nothing about whether migration completed, so it must not
        // block startup on its own. InnoSourceActivityVerifier still detects this process by
        // image path, so the Store side keeps its interlock without a lease.
        try
        {
            runtimeLease = MigrationOperationLock.AcquireRuntime(binding);
        }
        catch (IOException ex) when (MigrationOperationLock.IsBusy(ex))
        {
            Logger.Error("Migration state is locked by an in-progress migration; Inno startup blocked.");
            return ShowGuidance("Migration_InnoCheckFailed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or ArgumentException or NotSupportedException or
                                   InvalidOperationException or System.Security.SecurityException)
        {
            // An unreachable lock says nothing about whether a migration completed. Only an
            // exclusive holder does, and that case is handled above. The receipt check below
            // stays authoritative and fails closed on its own, so continuing here loses
            // same-session coordination rather than safety. Refusing to launch would lock a
            // user who never migrated out of their app over an inaccessible zero-byte file.
            Logger.Warn($"Migration runtime lock unavailable ({ex.GetType().Name}); continuing without it.");
        }

        var path = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.CompletionFileName);
        string resourceKey;
        try
        {
            MigrationRecordCodec.ReadCompletion(path, binding, DateTime.UtcNow);
            Logger.Info("Completed Store migration blocks normal Inno startup.");
            resourceKey = "Migration_InnoCompleted";
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or
                                   EndOfStreamException or ArgumentException or FormatException or
                                   DecoderFallbackException)
        {
            Logger.Warn($"Migration completion receipt rejected ({ex.GetType().Name}).");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Migration completion check unavailable ({ex.GetType().Name}); Inno startup blocked.");
            resourceKey = "Migration_InnoCheckFailed";
        }

        return ShowGuidance(resourceKey);
#endif
    }

    private static bool ShowGuidance(string resourceKey)
    {
        if (MessageBoxW(IntPtr.Zero, LocalizationHelper.GetString(resourceKey),
                AppIdentity.DisplayName, 0x00000040) == 0)
            Logger.Error($"Could not show migration startup guidance (Win32 {Marshal.GetLastWin32Error()}).");
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
