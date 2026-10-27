using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray;

/// <summary>
/// Headless CLI handler for the --headless-setup flag. Attaches to the parent console
/// and runs the WSL SetupEngine pipeline with an optional config file, skipping setup
/// when the data directory is already configured unless --force-setup is passed.
/// </summary>
internal static class CliSetupHandler
{
    public static async Task RunAsync(string[] args)
    {
        ParentConsole.Attach();

        if (!CliSetupCommandLine.TryParse(args, out var request, out var error))
        {
            Console.Error.WriteLine($"ERROR: {error}");
            Environment.Exit(2);
            return;
        }

        var dataDir = AppIdentity.ResolveRoamingDataDirectory();
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
                Console.Error.WriteLine(
                    $"ERROR: The saved gateway identity in '{dataDir}' could not be read: {ex.Message} " +
                    $"Pass {CliSetupCommandLine.ForceFlag} to replace it.");
                Environment.Exit(1);
                return;
            }

            if (!requiresSetup)
            {
                Console.WriteLine(
                    $"Headless setup skipped: '{dataDir}' is already configured. " +
                    $"Pass {CliSetupCommandLine.ForceFlag} to run setup again.");
                Environment.Exit(0);
                return;
            }
        }

        var setupArgs = CliSetupCommandLine.BuildEngineArguments(
            request,
            dataDir,
            AppIdentity.ResolveSetupLocalDataDirectory(),
            AppIdentity.SetupDistroName,
            AppIdentity.SetupGatewayPort);

        Console.WriteLine("OpenClaw Setup: running SetupEngine (headless)");
        Console.WriteLine($"  Arguments:  {string.Join(' ', setupArgs)}");
        Console.WriteLine();

        try
        {
            var exitCode = await OpenClaw.SetupEngine.Program.Main(setupArgs);
            Environment.Exit(exitCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: SetupEngine failed: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
