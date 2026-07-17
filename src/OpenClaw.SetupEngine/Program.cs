using System.Runtime.Versioning;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

[SupportedOSPlatform("windows")]
public static class Program
{
    internal static IReadOnlyList<string> ValueOptionNames { get; } = Array.AsReadOnly(
    [
        "--config",
        "--log-path",
        "--json-output",
        "--data-dir",
        "--local-data-dir",
        "--distro-name",
        "--gateway-port",
        "--autostart-name",
        "--startup-task-name",
    ]);

    internal static IReadOnlyList<string> FlagOptionNames { get; } = Array.AsReadOnly(
    [
        "--headless",
        "--rollback-on-failure",
        "--no-rollback-on-failure",
        "--dry-run",
        "--wizard-only",
        "--uninstall",
        "--confirm-destructive",
        "--preserve-logs",
    ]);

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("OpenClaw Setup Engine v0.1");
        Console.WriteLine("─────────────────────────────");

        if (!TryParseArguments(args, out var parsedArguments, out var argumentError))
        {
            Console.Error.WriteLine($"ERROR: {argumentError}");
            return 2;
        }

        var configPath = parsedArguments.GetValue("--config");
        var logPath = parsedArguments.GetValue("--log-path");
        var headless = parsedArguments.HasFlag("--headless");
        var rollback = parsedArguments.HasFlag("--rollback-on-failure");
        var noRollback = parsedArguments.HasFlag("--no-rollback-on-failure");
        var dryRun = parsedArguments.HasFlag("--dry-run");
        var wizardOnly = parsedArguments.HasFlag("--wizard-only");
        var uninstall = parsedArguments.HasFlag("--uninstall");
        var confirmDestructive = parsedArguments.HasFlag("--confirm-destructive");
        var jsonOutput = parsedArguments.GetValue("--json-output");
        var preserveLogs = parsedArguments.HasFlag("--preserve-logs");
        var dataDir = parsedArguments.GetValue("--data-dir");
        var localDataDir = parsedArguments.GetValue("--local-data-dir");
        var distroName = parsedArguments.GetValue("--distro-name");
        var gatewayPortText = parsedArguments.GetValue("--gateway-port");
        var autoStartName = parsedArguments.GetValue("--autostart-name") ?? "OpenClawTray";
        var startupTaskName = parsedArguments.GetValue("--startup-task-name") ?? WindowsStartupTaskRegistration.TaskName;

        // Load config
        SetupConfig config;
        if (configPath != null)
        {
            Console.WriteLine($"Loading config from: {configPath}");
            if (!TryLoadConfig(configPath, out config, out var configError))
            {
                Console.Error.WriteLine($"ERROR: Cannot load config '{configPath}': {configError}");
                return 2;
            }
        }
        else
        {
            // Look for default-config.json next to the exe
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(defaultPath))
            {
                Console.WriteLine($"Loading config from: {defaultPath}");
                if (!TryLoadConfig(defaultPath, out config, out var configError))
                {
                    Console.Error.WriteLine($"ERROR: Cannot load bundled config '{defaultPath}': {configError}");
                    return 1;
                }
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

    internal static bool TryParseArguments(
        string[] args,
        out ParsedArguments parsedArguments,
        out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valueOptionNames = new HashSet<string>(ValueOptionNames, StringComparer.OrdinalIgnoreCase);
        var flagOptionNames = new HashSet<string>(FlagOptionNames, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                parsedArguments = new(values, flags);
                error = $"Unexpected argument '{token}'.";
                return false;
            }

            var equalsIndex = token.IndexOf('=');
            var name = equalsIndex >= 0 ? token[..equalsIndex] : token;
            var hasInlineValue = equalsIndex >= 0;

            if (valueOptionNames.Contains(name))
            {
                if (values.ContainsKey(name))
                {
                    parsedArguments = new(values, flags);
                    error = $"{name} may only be specified once.";
                    return false;
                }

                var value = hasInlineValue
                    ? token[(equalsIndex + 1)..]
                    : i < args.Length - 1 ? args[i + 1] : null;
                if (string.IsNullOrWhiteSpace(value) ||
                    (!hasInlineValue && value.StartsWith("--", StringComparison.Ordinal)))
                {
                    parsedArguments = new(values, flags);
                    error = $"{name} requires a value.";
                    return false;
                }

                values[name] = value;
                if (!hasInlineValue)
                    i++;
                continue;
            }

            if (flagOptionNames.Contains(name))
            {
                if (hasInlineValue)
                {
                    parsedArguments = new(values, flags);
                    error = $"{name} does not accept a value.";
                    return false;
                }

                flags.Add(name);
                continue;
            }

            parsedArguments = new(values, flags);
            error = $"Unknown option '{token}'.";
            return false;
        }

        parsedArguments = new(values, flags);
        error = null;
        return true;
    }

    internal sealed class ParsedArguments(
        IReadOnlyDictionary<string, string> values,
        IReadOnlySet<string> flags)
    {
        public string? GetValue(string name)
            => values.TryGetValue(name, out var value) ? value : null;

        public bool HasFlag(string name)
            => flags.Contains(name);
    }

    private static bool TryLoadConfig(string path, out SetupConfig config, out string? error)
    {
        if (SetupConfig.TryLoadFromFile(path, out var loadedConfig, out error))
        {
            config = loadedConfig;
            return true;
        }

        config = null!;
        return false;
    }
}
