using System.Runtime.InteropServices;
using System.Globalization;

namespace OpenClawTray;

/// <summary>
/// Headless CLI handler for the destructive --uninstall and --reset-local-ai flags.
/// Attaches to the parent console and delegates to the setup engine with this
/// app identity's data directories, so both operations target the same profile.
/// </summary>
internal static class CliUninstallHandler
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    public const string UninstallFlag = "--uninstall";
    public const string ResetLocalAiFlag = OpenClawTray.Services.LocalAiResetService.ResetLocalAiFlag;

    public static bool IsDestructiveCommand(string[] args) =>
        args.Contains(UninstallFlag, StringComparer.OrdinalIgnoreCase) ||
        args.Contains(ResetLocalAiFlag, StringComparer.OrdinalIgnoreCase);

    public static async Task RunAsync(string[] args)
    {
        AttachConsole(AttachParentProcess);
        string operationFlag = args.Contains(UninstallFlag, StringComparer.OrdinalIgnoreCase)
            ? UninstallFlag
            : ResetLocalAiFlag;
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
            Console.Error.WriteLine($"ERROR: {operationFlag} requires --confirm-destructive (or --dry-run).");
            Environment.Exit(2);
            return;
        }

        // Build CLI arguments for SetupEngine
        var setupArgs = new List<string> { "--headless", operationFlag };
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

        Console.WriteLine($"OpenClaw {operationFlag}: running SetupEngine");
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
