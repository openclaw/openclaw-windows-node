using System.Text.Json;

namespace OpenClaw.SetupEngine;

// ─── Configuration ───

public sealed class SetupConfig
{
    public string Mode { get; set; } = "local-wsl";
    public string DistroName { get; set; } = "OpenClawGateway";
    public int GatewayPort { get; set; } = 18789;
    public string BaseDistro { get; set; } = "Ubuntu-24.04";
    public bool SkipPermissions { get; set; }
    public bool SkipWizard { get; set; }
    public bool Headless { get; set; }
    public bool AutoApprovePairing { get; set; } = true;
    public bool RollbackOnFailure { get; set; }
    public bool CleanBeforeRun { get; set; } = true;
    public string LogLevel { get; set; } = "trace";
    public string? LogPath { get; set; }
    public string? GatewayUrl { get; set; }
    public string? BootstrapToken { get; set; }
    public Dictionary<string, string>? WizardAnswers { get; set; }

    public string EffectiveGatewayUrl => GatewayUrl ?? $"ws://localhost:{GatewayPort}";

    public static SetupConfig LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SetupConfig>(json, JsonOptions) ?? new SetupConfig();
    }

    public static SetupConfig FromEnvironment(SetupConfig? baseConfig = null)
    {
        var config = baseConfig ?? new SetupConfig();

        if (Environment.GetEnvironmentVariable("OPENCLAW_SETUP_DISTRO") is { Length: > 0 } distro)
            config.DistroName = distro;
        if (Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT") is { Length: > 0 } port && int.TryParse(port, out var p))
            config.GatewayPort = p;
        if (Environment.GetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS") is "1" or "true")
            config.Headless = true;
        if (Environment.GetEnvironmentVariable("OPENCLAW_SETUP_LOG_PATH") is { Length: > 0 } logPath)
            config.LogPath = logPath;

        return config;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

// ─── Step Result ───

public enum StepOutcome { Success, Skipped, Failed, FailedTerminal }

public sealed record StepResult(StepOutcome Outcome, string? Message = null, Exception? Error = null)
{
    public static StepResult Ok(string? message = null) => new(StepOutcome.Success, message);
    public static StepResult Skip(string reason) => new(StepOutcome.Skipped, reason);
    public static StepResult Fail(string message, Exception? ex = null) => new(StepOutcome.Failed, message, ex);
    public static StepResult Terminal(string message, Exception? ex = null) => new(StepOutcome.FailedTerminal, message, ex);

    public bool IsSuccess => Outcome is StepOutcome.Success or StepOutcome.Skipped;
}

// ─── Setup Context ───

public sealed class SetupContext
{
    public SetupConfig Config { get; }
    public SetupLogger Logger { get; }
    public TransactionJournal Journal { get; }
    public CommandRunner Commands { get; }
    public CancellationToken CancellationToken { get; }

    // Accumulated state from steps
    public string? DistroName { get; set; }
    public string? GatewayUrl { get; set; }
    public string? SharedGatewayToken { get; set; }
    public string? BootstrapToken { get; set; }
    public string? GatewayRecordId { get; set; }
    public string? OperatorDeviceId { get; set; }
    public string? NodeDeviceId { get; set; }

    // Data directory for gateway registry and identity files
    public string DataDir { get; }

    public SetupContext(SetupConfig config, SetupLogger logger, TransactionJournal journal, CommandRunner commands, CancellationToken ct)
    {
        Config = config;
        Logger = logger;
        Journal = journal;
        Commands = commands;
        CancellationToken = ct;

        DataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");

        DistroName = config.DistroName;
        GatewayUrl = config.EffectiveGatewayUrl;
    }
}
