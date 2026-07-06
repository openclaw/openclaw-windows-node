using System.Runtime.Versioning;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("OpenClaw Setup Engine v0.1");
        Console.WriteLine("─────────────────────────────");

        // Parse CLI arguments
        var configPath = GetArg(args, "--config");
        var logPath = GetArg(args, "--log-path");
        var headless = HasFlag(args, "--headless");
        var rollback = HasFlag(args, "--rollback-on-failure");
        var noRollback = HasFlag(args, "--no-rollback-on-failure");
        var dryRun = HasFlag(args, "--dry-run");
        var wizardOnly = HasFlag(args, "--wizard-only");
        var uninstall = HasFlag(args, "--uninstall");
        var confirmDestructive = HasFlag(args, "--confirm-destructive");
        var jsonOutput = GetArg(args, "--json-output");
        var preserveLogs = HasFlag(args, "--preserve-logs");
        var dataDir = GetArg(args, "--data-dir");
        var localDataDir = GetArg(args, "--local-data-dir");
        var distroName = GetArg(args, "--distro-name");
        var gatewayPortText = GetArg(args, "--gateway-port");
        var autoStartName = GetArg(args, "--autostart-name") ?? "OpenClawTray";
        var startupTaskName = GetArg(args, "--startup-task-name") ?? WindowsStartupTaskRegistration.TaskName;

        // Load config
        SetupConfig config;
        if (configPath != null && File.Exists(configPath))
        {
            Console.WriteLine($"Loading config from: {configPath}");
            config = SetupConfig.LoadFromFile(configPath);
        }
        else
        {
            // Look for default-config.json next to the exe
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(defaultPath))
            {
                Console.WriteLine($"Loading config from: {defaultPath}");
                config = SetupConfig.LoadFromFile(defaultPath);
            }
            else
            {
                Console.Error.WriteLine("ERROR: No config file found. Provide --config or place default-config.json next to the exe.");
                return 1;
            }
        }

        // Apply CLI overrides
        config = SetupConfig.FromEnvironment(config);
        if (!string.IsNullOrWhiteSpace(distroName))
            config.DistroName = distroName;
        if (!string.IsNullOrWhiteSpace(gatewayPortText))
        {
            if (!int.TryParse(gatewayPortText, out var gatewayPort) || gatewayPort is <= 0 or > 65535)
            {
                Console.Error.WriteLine($"ERROR: Invalid --gateway-port value '{gatewayPortText}'.");
                return 2;
            }
            config.GatewayPort = gatewayPort;
            config.GatewayUrl = null;
        }
        GatewayLkgVersion.ApplyToConfig(config);
        if (headless) config.Headless = true;
        if (rollback) config.RollbackOnFailure = true;
        if (noRollback) config.RollbackOnFailure = false;
        if (wizardOnly) config.SkipWizard = false;
        if (logPath != null) config.LogPath = logPath;
        if (dryRun) config.DryRun = true;
        if (confirmDestructive) config.ConfirmDestructive = true;

        // Default log path if not specified
        var logLabel = uninstall ? "uninstall" : "setup";
        config.LogPath ??= Path.Combine(
            dataDir ?? SetupContext.ResolveDataDir(),
            "Logs", "Setup", $"{logLabel}-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        Console.WriteLine($"Log file: {config.LogPath}");
        Console.WriteLine($"Distro: {config.DistroName}");
        Console.WriteLine($"Gateway: {config.EffectiveGatewayUrl}");
        Console.WriteLine($"Mode: {(uninstall ? "UNINSTALL" : "SETUP")}");
        if (uninstall)
        {
            Console.WriteLine($"Dry run: {config.DryRun}");
            Console.WriteLine($"Confirm destructive: {config.ConfirmDestructive}");
        }
        Console.WriteLine();

        if (dryRun && !uninstall)
        {
            Console.WriteLine("DRY RUN — config validated, exiting.");
            return 0;
        }

        // Uninstall safety gate
        if (uninstall && !confirmDestructive && !dryRun)
        {
            Console.Error.WriteLine("ERROR: --uninstall requires --confirm-destructive (or use --dry-run to preview).");
            return 2;
        }

        if (!SetupRunLock.TryAcquire(dataDir ?? SetupContext.ResolveDataDir(), out var setupLock, out var lockMessage))
        {
            Console.Error.WriteLine($"ERROR: {lockMessage}");
            return 2;
        }

        using var acquiredSetupLock = setupLock;

        // Create infrastructure after acquiring the run lock so a concurrent loser
        // cannot truncate the active run's log or journal files.
        using var logger = new SetupLogger(config.LogPath, Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);
        var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
        using var journal = new TransactionJournal(journalPath, logger);
        var commands = new CommandRunner(logger);
        using var cts = new CancellationTokenSource();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.Warn("Cancellation requested (Ctrl+C)");
            cts.Cancel();
        };

        var ctx = new SetupContext(
            config,
            logger,
            journal,
            commands,
            cts.Token,
            dataDir,
            localDataDir);

        // Build step pipeline
        List<SetupStep> steps;
        if (uninstall)
        {
            steps = SetupStepFactory.BuildDefaultSteps();
        }
        else if (wizardOnly)
        {
            steps = SetupStepFactory.BuildWizardOnlySteps();
        }
        else
        {
            steps = BuildSteps(config);
        }

        var pipeline = new SetupPipeline(steps);

        pipeline.StepProgress += (_, e) =>
        {
            var icon = e.Outcome switch
            {
                StepOutcome.Success => "✓",
                StepOutcome.Skipped => "⊘",
                StepOutcome.Failed or StepOutcome.FailedTerminal => "✗",
                null => "►",
                _ => "?"
            };
            var elapsed = e.Elapsed.HasValue ? $" ({e.Elapsed.Value.TotalSeconds:F1}s)" : "";
            Console.WriteLine($"  {icon} {e.DisplayName}{elapsed}");
        };

        // Run!
        logger.Info($"{(uninstall ? "Uninstall" : "Setup")} engine starting", new { version = "0.1", args = string.Join(' ', args) });

        PipelineResult result;
        if (uninstall)
        {
            result = await pipeline.UninstallAsync(ctx);

            // Post-rollback tray-artifact cleanup (autostart, run.marker, settings, logs)
            if (result.Outcome == PipelineOutcome.Success || result.Outcome == PipelineOutcome.Failed || result.Outcome == PipelineOutcome.Cancelled)
            {
                if (!config.DryRun)
                {
                    logger.Info("Running tray-artifact cleanup...");
                    TrayArtifactCleanup.Run(ctx, preserveLogs, autoStartName, startupTaskName);
                }
            }
        }
        else
        {
            result = await pipeline.RunAsync(ctx);
        }

        Console.WriteLine();
        var label = uninstall ? "UNINSTALL" : "SETUP";
        Console.WriteLine(result.Outcome switch
        {
            PipelineOutcome.Success => $"═══ {label} COMPLETE ═══",
            PipelineOutcome.Failed => $"═══ {label} FAILED ═══\n  {result.Message}",
            PipelineOutcome.Cancelled => $"═══ {label} CANCELLED ═══",
            _ => "═══ UNKNOWN STATE ═══"
        });

        Console.WriteLine($"\nLog: {config.LogPath}");
        Console.WriteLine($"Journal: {journalPath}");

        // Write JSON output if requested (for programmatic callers like CliUninstallHandler)
        if (jsonOutput != null)
        {
            var outputDir = Path.GetDirectoryName(jsonOutput);
            if (outputDir != null) Directory.CreateDirectory(outputDir);

            var jsonResult = new
            {
                outcome = result.Outcome.ToString(),
                exitCode = result.ExitCode,
                failedStepId = result.FailedStepId,
                message = result.Message,
                logPath = config.LogPath,
                journalPath
            };
        var json = System.Text.Json.JsonSerializer.Serialize(jsonResult, SetupConfig.JsonWriteOptions);
            await AtomicFile.WriteAllTextAsync(jsonOutput, json);
        }

        return result.ExitCode;
    }

    private static List<SetupStep> BuildSteps(SetupConfig config)
        => SetupStepFactory.BuildDefaultSteps();

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}
