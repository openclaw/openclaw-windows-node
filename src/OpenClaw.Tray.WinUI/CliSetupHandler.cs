using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClaw.Shared;

namespace OpenClawTray;

/// <summary>
/// Headless CLI handler for the --headless-setup flag. Attaches to the parent console
/// and runs the WSL SetupEngine pipeline with an optional config file, skipping setup
/// when the data directory is already configured unless --force-setup is passed.
/// </summary>
/// <remarks>
/// The interactive setup window asks the user to confirm before replacing state. This handler
/// applies the same gate for an app-owned distro, its data, and the local gateway record: it
/// refuses unless --replace-existing is passed. A distro or distro data not proven to be
/// app-owned is never replaced headlessly, even with --replace-existing. The refusal directs the
/// user to interactive setup or manual removal. The distro and local data dir come from the app
/// identity and are shared across OPENCLAW_TRAY_DATA_DIR profiles. Without --replace-existing
/// the engine receives --preserve-existing: it re-checks admission while holding SetupRunLock's
/// shared-target lock, and CleanupStaleDistroStep refuses to remove any distro.
/// </remarks>
internal static class CliSetupHandler
{
    // Held until process exit, like App._innoMigrationLease, so a concurrent Store
    // migration sees this install as active for the whole setup run.
    private static IDisposable? s_migrationLease;

    public static async Task RunAsync(string[] args)
    {
        ParentConsole.Attach();
        try
        {
            // Off the WinUI dispatcher: ExistingConfigDetector.Detect and other setup helpers
            // block on async work, which deadlocks under the UI synchronization context.
            Environment.Exit(await Task.Run(() => RunCoreAsync(args)));
        }
        catch (Exception ex)
        {
            // Every path must exit. An escaped exception would leave a windowless
            // WinUI process that never terminates.
            Console.Error.WriteLine($"ERROR: Headless setup failed: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        if (!CliSetupCommandLine.TryParse(args, out var request, out var error))
            return Fail(2, error!);

        if (PackageHelper.IsPackaged)
            return Fail(2, "Headless setup is not available in the packaged (MSIX) app. Run setup from the app instead.");

        if (IsTrayRunning())
            return Fail(2, "OpenClaw is running for this data directory. Quit it before running headless setup.");

        // A completed Store migration retires this unpackaged install; setup must not recreate its
        // gateway. The guidance that normal startup shows in a dialog goes to stderr here.
        if (InnoMigrationStartupGuard.TryGetStopGuidance(out s_migrationLease, out var migrationGuidanceKey))
            return Fail(2, LocalizationHelper.GetString(migrationGuidanceKey));

        var dataDir = AppIdentity.ResolveRoamingDataDirectory();
        var localDataDir = AppIdentity.ResolveSetupLocalDataDirectory();
        if (!request.Force)
        {
            bool requiresSetup;
            try
            {
                var registry = new GatewayRegistry(dataDir);
                registry.Load();
                // Same predicate the tray uses to auto-open setup, so "skipped" means
                // the launched tray will not prompt for setup either.
                requiresSetup = StartupSetupState.RequiresSetup(new SettingsManager(dataDir), dataDir, registry);
            }
            catch (DeviceIdentityLoadException ex)
            {
                return Fail(1,
                    $"The saved gateway identity in '{dataDir}' could not be read: {ex.Message} " +
                    $"Pass {CliSetupCommandLine.ForceFlag} to replace it.");
            }

            if (!requiresSetup)
            {
                Console.WriteLine(
                    $"Headless setup skipped: '{dataDir}' is already configured. " +
                    $"Pass {CliSetupCommandLine.ForceFlag} to run setup again.");
                return 0;
            }
        }

        var existing = ExistingConfigDetector.Detect(dataDir, AppIdentity.SetupDistroName, localDataDir);
        var refusal = CliSetupCommandLine.GetReplacementRefusal(
            request,
            hasExistingState: existing.HasLocalGateway || existing.HasDistro || existing.HasDistroDataDirectory,
            ownershipUnproven: ExistingConfigDetector.RequiresDestructiveConfirmation(existing),
            hasDistro: existing.HasDistro,
            distroName: AppIdentity.SetupDistroName,
            distroDataDirectory: Path.Combine(localDataDir, "wsl", AppIdentity.SetupDistroName),
            replacementSummary: ExistingConfigDetector.BuildReplacementSummary(existing));
        if (refusal is not null)
            return Fail(2, refusal);

        var setupArgs = CliSetupCommandLine.BuildEngineArguments(
            request,
            dataDir,
            localDataDir,
            AppIdentity.SetupDistroName,
            AppIdentity.SetupGatewayPort);

        Console.WriteLine("OpenClaw Setup: running SetupEngine (headless)");
        Console.WriteLine($"  Arguments:  {string.Join(' ', setupArgs)}");
        Console.WriteLine();

        return await OpenClaw.SetupEngine.Program.Main(setupArgs);
    }

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return exitCode;
    }

    // Mirrors the single-instance mutex name computed in App.OnLaunchedAsync.
    private static bool IsTrayRunning()
    {
        var mutexName = AppIdentity.MutexBaseName;
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } dataDir)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir));
            mutexName = $"{AppIdentity.MutexBaseName}-{Convert.ToHexString(hash, 0, 4)}";
        }

        if (!Mutex.TryOpenExisting(mutexName, out var mutex))
            return false;

        mutex.Dispose();
        return true;
    }
}
