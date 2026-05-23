using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClawTray;

/// <summary>
/// Headless CLI handler for the --uninstall flag. Attaches to the parent console
/// and delegates to SetupEngine.exe --uninstall for the actual teardown.
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

        // Find SetupEngine.UI.exe (which supports --headless)
        var setupExe = App.ResolveSetupEngineUiPath();
        if (setupExe == null)
        {
            Console.Error.WriteLine($"ERROR: SetupEngine.UI not found (searched {AppContext.BaseDirectory} and sibling project output)");
            Environment.Exit(1);
            return;
        }

        // Build CLI arguments for SetupEngine
        var setupArgs = new List<string> { "--headless", "--uninstall" };
        if (confirmDestructive) setupArgs.Add("--confirm-destructive");
        if (dryRun) setupArgs.Add("--dry-run");
        if (jsonOutputPath != null)
        {
            setupArgs.Add("--json-output");
            setupArgs.Add(jsonOutputPath);
        }

        Console.WriteLine("OpenClaw Uninstall — delegating to SetupEngine");
        Console.WriteLine($"  Executable: {setupExe}");
        Console.WriteLine($"  Arguments:  {string.Join(' ', setupArgs)}");
        Console.WriteLine();

        var psi = new ProcessStartInfo(setupExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in setupArgs)
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi)!;

            // Forward stdout/stderr in real time
            var stdoutTask = Task.Run(async () =>
            {
                while (await proc.StandardOutput.ReadLineAsync() is { } line)
                    Console.WriteLine(line);
            });
            var stderrTask = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                    Console.Error.WriteLine(line);
            });

            await proc.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            Environment.Exit(proc.ExitCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to launch SetupEngine: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
