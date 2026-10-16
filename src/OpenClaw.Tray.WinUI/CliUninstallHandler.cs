using System.Runtime.InteropServices;
using System.Globalization;
using OpenClaw.Connection;

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

        var distroName = AppIdentity.SetupDistroName;
        var gatewayPort = AppIdentity.SetupGatewayPort;
        if (!CliUninstallTarget.TryParse(args, out var uninstallTarget, out var argumentError))
        {
            Console.Error.WriteLine($"ERROR: {argumentError}");
            Environment.Exit(2);
            return;
        }

        string[] targetSetupArguments = [];
        if (uninstallTarget is not null)
        {
            var registry = new GatewayRegistry(AppIdentity.ResolveRoamingDataDirectory());
            registry.Load();
            var target = registry.GetActive();
            if (!uninstallTarget.TryGetSetupArguments(target, out targetSetupArguments))
            {
                Console.Error.WriteLine("ERROR: The selected WSL Gateway changed. No uninstall was started.");
                Environment.Exit(2);
                return;
            }
            distroName = GatewayRecordEditing.ResolveManagedDistroName(target!)!;
            // A Tailscale URL's 443 is not the service's loopback port.
            if (GatewayRecordEditing.IsLoopbackEndpoint(target!.Url))
                gatewayPort = new Uri(target.Url).Port;
        }

        // Build CLI arguments for SetupEngine
        var setupArgs = new List<string> { "--headless", "--uninstall" };
        setupArgs.AddRange([
            "--data-dir", AppIdentity.ResolveRoamingDataDirectory(),
            "--local-data-dir", AppIdentity.ResolveSetupLocalDataDirectory(),
            "--distro-name", distroName,
            "--gateway-port", gatewayPort.ToString(CultureInfo.InvariantCulture),
            "--autostart-name", AppIdentity.AutoStartRegistryName,
            "--startup-task-name", AppIdentity.StartupTaskName,
        ]);
        if (confirmDestructive) setupArgs.Add("--confirm-destructive");
        if (dryRun) setupArgs.Add("--dry-run");
        setupArgs.AddRange(targetSetupArguments);
        if (jsonOutputPath != null)
        {
            setupArgs.Add("--json-output");
            setupArgs.Add(jsonOutputPath);
        }

        Console.WriteLine("OpenClaw Uninstall: running SetupEngine");
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
