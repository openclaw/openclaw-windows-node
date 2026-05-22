namespace OpenClaw.SetupEngine;

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
        var dryRun = HasFlag(args, "--dry-run");

        // Load config
        SetupConfig config;
        if (configPath != null && File.Exists(configPath))
        {
            Console.WriteLine($"Loading config from: {configPath}");
            config = SetupConfig.LoadFromFile(configPath);
        }
        else
        {
            config = new SetupConfig();
        }

        // Apply CLI overrides
        config = SetupConfig.FromEnvironment(config);
        if (headless) config.Headless = true;
        if (rollback) config.RollbackOnFailure = true;
        if (logPath != null) config.LogPath = logPath;

        // Default log path if not specified
        config.LogPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray", "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        Console.WriteLine($"Log file: {config.LogPath}");
        Console.WriteLine($"Mode: {config.Mode}");
        Console.WriteLine($"Distro: {config.DistroName}");
        Console.WriteLine($"Gateway: {config.EffectiveGatewayUrl}");
        Console.WriteLine($"Headless: {config.Headless}");
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("DRY RUN — config validated, exiting.");
            return 0;
        }

        // Create infrastructure
        using var logger = new SetupLogger(config.LogPath, Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);
        var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
        using var journal = new TransactionJournal(journalPath);
        var commands = new CommandRunner(logger);
        using var cts = new CancellationTokenSource();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.Warn("Cancellation requested (Ctrl+C)");
            cts.Cancel();
        };

        var ctx = new SetupContext(config, logger, journal, commands, cts.Token);

        // Build step pipeline
        var steps = BuildSteps(config);
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
        logger.Info("Setup engine starting", new { version = "0.1", args = string.Join(' ', args) });
        var result = await pipeline.RunAsync(ctx);

        Console.WriteLine();
        Console.WriteLine(result.Outcome switch
        {
            PipelineOutcome.Success => "═══ SETUP COMPLETE ═══",
            PipelineOutcome.Failed => $"═══ SETUP FAILED ═══ (step: {result.FailedStepId})\n  {result.Message}",
            PipelineOutcome.Cancelled => "═══ SETUP CANCELLED ═══",
            _ => "═══ UNKNOWN STATE ═══"
        });

        Console.WriteLine($"\nLog: {config.LogPath}");
        Console.WriteLine($"Journal: {journalPath}");

        return result.ExitCode;
    }

    private static List<SetupStep> BuildSteps(SetupConfig config)
    {
        var steps = new List<SetupStep>();

        // Cleanup (always first if enabled)
        steps.Add(new CleanupStaleDistroStep());
        steps.Add(new CleanupStaleGatewayStep());

        // Preflight
        steps.Add(new PreflightOsStep());
        steps.Add(new PreflightWslStep());
        steps.Add(new PreflightPortStep());

        // WSL
        steps.Add(new CreateWslInstanceStep());
        steps.Add(new ConfigureWslInstanceStep());

        // Gateway
        steps.Add(new InstallCliStep());
        steps.Add(new ConfigureGatewayStep());
        steps.Add(new InstallGatewayServiceStep());
        steps.Add(new StartGatewayStep());

        // Pairing
        steps.Add(new MintBootstrapTokenStep());
        steps.Add(new PairOperatorStep());
        steps.Add(new PairNodeStep());
        steps.Add(new VerifyEndToEndStep());

        return steps;
    }

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
