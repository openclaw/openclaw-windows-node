using OpenClawTray.Services;
using OpenClawTray.Services.LocalGatewaySetup;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenClawTray;

/// <summary>
/// Headless CLI handler for the --uninstall flag. Attaches to the parent console
/// and drives the local gateway uninstall engine without creating any tray UI.
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

        var settings = new SettingsManager();
        var engine   = LocalGatewayUninstall.Build(settings, logger: new AppLogger());

        LocalGatewayUninstallResult result;
        try
        {
            result = await engine.RunAsync(new LocalGatewayUninstallOptions
            {
                DryRun             = dryRun,
                ConfirmDestructive = confirmDestructive
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Uninstall engine threw: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        Console.WriteLine("OpenClaw Local Gateway Uninstall");
        Console.WriteLine($"DryRun:   {dryRun}");
        Console.WriteLine($"Success:  {result.Success}");
        Console.WriteLine($"Steps:    {result.Steps.Count} ({result.SkippedSteps.Count} skipped)");
        Console.WriteLine($"Errors:   {result.Errors.Count}");
        foreach (var e in result.Errors)
            Console.Error.WriteLine($"  ERROR: {Redact(e)}");
        Console.WriteLine("Postconditions:");
        Console.WriteLine($"  WslDistroAbsent:    {result.Postconditions.WslDistroAbsent}");
        Console.WriteLine($"  AutostartCleared:   {result.Postconditions.AutostartCleared}");
        Console.WriteLine($"  SetupStateAbsent:   {result.Postconditions.SetupStateAbsent}");
        Console.WriteLine($"  DeviceTokenCleared: {result.Postconditions.DeviceTokenCleared}");
        Console.WriteLine($"  McpTokenPreserved:  {result.Postconditions.McpTokenPreserved}");
        Console.WriteLine($"  KeepalivesAbsent:   {result.Postconditions.KeepalivesAbsent}");
        Console.WriteLine($"  VhdDirAbsent:       {result.Postconditions.VhdDirAbsent}");
        Console.WriteLine($"  LocalGatewayRecordsAbsent:      {result.Postconditions.LocalGatewayRecordsAbsent}");
        Console.WriteLine($"  LocalGatewayIdentityDirsAbsent: {result.Postconditions.LocalGatewayIdentityDirsAbsent}");

        if (jsonOutputPath != null)
        {
            try
            {
                var dir = Path.GetDirectoryName(jsonOutputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var payload = new
                {
                    success      = result.Success,
                    dry_run      = dryRun,
                    steps        = result.Steps.Select(s => new
                    {
                        name   = s.Name,
                        status = s.Status.ToString(),
                        detail = Redact(s.Detail)
                    }),
                    errors        = result.Errors.Select(Redact),
                    skipped_steps = result.SkippedSteps,
                    postconditions = new
                    {
                        wsl_distro_absent                  = result.Postconditions.WslDistroAbsent,
                        autostart_cleared                  = result.Postconditions.AutostartCleared,
                        setup_state_absent                 = result.Postconditions.SetupStateAbsent,
                        device_token_cleared               = result.Postconditions.DeviceTokenCleared,
                        mcp_token_preserved                = result.Postconditions.McpTokenPreserved,
                        keepalives_absent                  = result.Postconditions.KeepalivesAbsent,
                        vhd_dir_absent                     = result.Postconditions.VhdDirAbsent,
                        local_gateway_records_absent       = result.Postconditions.LocalGatewayRecordsAbsent,
                        local_gateway_identity_dirs_absent = result.Postconditions.LocalGatewayIdentityDirsAbsent
                    }
                };

                File.WriteAllText(jsonOutputPath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

                Console.WriteLine($"JSON result: {jsonOutputPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Failed to write JSON output to '{jsonOutputPath}': {ex.Message}");
            }
        }

        Environment.Exit(result.Success ? 0 : 1);
    }

    /// <summary>
    /// Redacts token/key material from a string before writing it to CLI stdout or a JSON output file.
    /// Mirrors the PowerShell Invoke-Redact pattern in validate-wsl-gateway-uninstall.ps1.
    /// </summary>
    internal static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(""(?i:deviceToken|device_token|token|bootstrapToken|bootstrap_token|PrivateKeyBase64|PublicKeyBase64)""\s*:\s*"")[^""]+("")",
            "$1<redacted>$2");
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?i)((?:device|bootstrap|gateway|auth|mcp)[_-]?token\s*[:=]\s*)[^\s,""'}{]+",
            "$1<redacted>");
        return value;
    }
}
