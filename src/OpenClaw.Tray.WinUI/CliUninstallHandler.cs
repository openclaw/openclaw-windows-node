using System.Runtime.InteropServices;
using System.Globalization;

namespace OpenClawTray;

/// <summary>
/// Headless CLI handler for the --uninstall flag. Attaches to the parent console
/// and delegates to the setup engine for the actual teardown.
/// </summary>
internal static class CliUninstallHandler
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    public static async Task RunAsync(string[] args)
    {
        AttachConsole(AttachParentProcess);

        bool dryRun             = args.Contains("--dry-run",             StringComparer.OrdinalIgnoreCase);
        bool confirmDestructive = args.Contains("--confirm-destructive", StringComparer.OrdinalIgnoreCase);

        string? jsonOutputPath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--json-output", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutputPath = args[i + 1];
                break;
            }
        }

        if (!confirmDestructive && !dryRun)
        {
            Console.Error.WriteLine("ERROR: --uninstall requires --confirm-destructive (or --dry-run).");
            Environment.Exit(2);
            return;
        }

        // Build CLI arguments for SetupEngine
        var setupArgs = new List<string> { "--headless", "--uninstall" };
        setupArgs.AddRange([
            "--data-dir", AppIdentity.ResolveRoamingDataDirectory(),
            "--local-data-dir", AppIdentity.ResolveSetupLocalDataDirectory(),
            "--distro-name", AppIdentity.SetupDistroName,
            "--gateway-port", AppIdentity.SetupGatewayPort.ToString(CultureInfo.InvariantCulture),
            "--autostart-name", AppIdentity.AutoStartRegistryName,
            "--startup-task-name", AppIdentity.StartupTaskName,
        ]);
        if (confirmDestructive) setupArgs.Add("--confirm-destructive");
        if (dryRun) setupArgs.Add("--dry-run");
        if (jsonOutputPath != null)
        {
            setupArgs.Add("--json-output");
            setupArgs.Add(jsonOutputPath);
        }

        Console.WriteLine("OpenClaw Uninstall - running SetupEngine");
        Console.WriteLine($"  Arguments:  {string.Join(' ', setupArgs)}");
        Console.WriteLine();

        try
        {
            var exitCode = await OpenClaw.SetupEngine.Program.Main(setupArgs.ToArray());
            Environment.Exit(exitCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: SetupEngine failed: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
