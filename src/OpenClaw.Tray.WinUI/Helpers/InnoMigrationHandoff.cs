using Microsoft.UI.Dispatching;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class InnoMigrationHandoff
{
    public static bool IsEnabled
    {
        get
        {
#if INNO_MIGRATION_PREVIEW || PRODUCTION_MIGRATION
            return !AppIdentity.IsDev && !PackageHelper.IsPackaged &&
                !GatewayFixtureIsolation.IsEnabled && !MigrationEnvironment.HasPathOverride;
#else
            return false;
#endif
        }
    }

    public static bool IsAvailable => IsEnabled && StoreMigrationListing.TryCreateUri(
        MigrationEnvironment.StoreProductId, out _);

    public static Action? CreateShutdownHandler(DispatcherQueue dispatcher, Action exit) =>
        IsEnabled ? () =>
        {
            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    var (binding, installation) = InspectOwnInstallation();
                    if (!new InnoMigrationConsentStore(binding, new AppLogger())
                            .HasValidConsent(installation.Version.ToString()))
                    {
                        Logger.Warn("Migration shutdown rejected: explicit current-user consent is missing or invalid.");
                        return;
                    }
                    exit();
                }
                catch (Exception exception)
                {
                    Logger.Error($"Migration shutdown rejected: {exception.Message}");
                }
            }))
                Logger.Warn("Migration shutdown rejected: UI dispatcher is unavailable.");
        } : null;

    public static async Task<string> GrantAndLaunchAsync()
    {
        if (!IsAvailable || !StoreMigrationListing.TryCreateUri(
                MigrationEnvironment.StoreProductId, out var uri))
        {
            Logger.Error("Migration Store entry point is unavailable or has no configured product ID.");
            return "Migration2_InnoFailed";
        }

        try
        {
            var (binding, installation) = InspectOwnInstallation();
            new InnoMigrationConsentStore(binding, new AppLogger()).Grant(installation.Version.ToString());
            if (!await global::Windows.System.Launcher.LaunchUriAsync(uri!))
                throw new InvalidOperationException("Windows declined the Store listing URI.");
            return "Migration2_InnoGranted";
        }
        catch (Exception exception)
        {
            Logger.Error($"Migration consent handoff failed: {exception}");
            return "Migration2_InnoFailed";
        }
    }

    private static (MigrationBinding Binding, InnoInstallation Installation) InspectOwnInstallation()
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Inno migration is disabled.");
        var binding = MigrationEnvironment.CreateBinding();
        var detected = MigrationEnvironment.CreateDetector().Detect();
        if (detected.Status != InnoInstallationStatus.Detected || detected.Installation is not { } installation ||
            installation.Architecture != binding.Architecture ||
            !string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""),
                Path.Combine(binding.InstallDirectory, "OpenClaw.Tray.WinUI.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This process is not the supported current-user Inno installation.");
        return (binding, installation);
    }
}
