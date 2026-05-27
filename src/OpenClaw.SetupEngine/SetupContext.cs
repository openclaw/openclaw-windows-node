using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SetupEngine;

// ─── Configuration ───

public sealed class SetupConfig
{
    public string DistroName { get; set; } = "OpenClawGateway";
    public int GatewayPort { get; set; } = 18789;
    public string BaseDistro { get; set; } = "Ubuntu-24.04";
    public bool SkipPermissions { get; set; }
    public bool SkipWizard { get; set; }
    public bool Headless { get; set; }
    public bool AutoApprovePairing { get; set; }
    public bool RollbackOnFailure { get; set; }
    public int RollbackTimeoutSeconds { get; set; } = 60;
    public bool CleanBeforeRun { get; set; }
    public bool DryRun { get; set; }
    public bool ConfirmDestructive { get; set; }
    public string LogLevel { get; set; } = "trace";
    public string? LogPath { get; set; }
    public string? GatewayUrl { get; set; }
    public string? BootstrapToken { get; set; }
    public Dictionary<string, string>? WizardAnswers { get; set; }

    // Nested config sections — everything is configurable
    public WslConfig Wsl { get; set; } = new();
    public GatewayConfig Gateway { get; set; } = new();
    public CapabilitiesConfig Capabilities { get; set; } = new();
    public TraySettingsConfig Settings { get; set; } = new();
    public PairingConfig Pairing { get; set; } = new();

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

    public SetupConfig ApplyUiDefaults(bool rollbackOnFailure = true)
    {
        Headless = false;
        RollbackOnFailure = rollbackOnFailure;
        return this;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

// ─── WSL Configuration ───

public sealed class WslConfig
{
    private static readonly System.Text.RegularExpressions.Regex s_linuxUserNamePattern =
        new("^[a-z_][a-z0-9_-]{0,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public string User { get; set; } = "openclaw";
    public bool Systemd { get; set; } = true;
    public bool Interop { get; set; } = false;
    public bool AppendWindowsPath { get; set; } = false;
    public bool Automount { get; set; } = false;
    public bool MountFsTab { get; set; } = false;
    public bool UseWindowsTimezone { get; set; } = true;
    public string? Memory { get; set; }
    public string? Swap { get; set; }

    public static bool IsValidLinuxUserName(string value)
        => s_linuxUserNamePattern.IsMatch(value);
}

// ─── Gateway Configuration ───

public sealed class GatewayConfig
{
    public string Bind { get; set; } = "loopback";
    public string? InstallUrl { get; set; }
    public string? Version { get; set; }
    public int HealthTimeoutSeconds { get; set; } = 90;
    public string ReloadMode { get; set; } = "hot";
    public string AuthMode { get; set; } = "token";
    public Dictionary<string, string>? ExtraConfig { get; set; }
}

// ─── Capabilities Configuration ───

public sealed class CapabilitiesConfig
{
    public bool System { get; set; } = true;
    public bool Canvas { get; set; } = true;
    public bool Screen { get; set; } = true;
    public bool Camera { get; set; } = true;
    public bool Location { get; set; } = true;
    public bool Browser { get; set; } = true;
    public bool Device { get; set; } = true;
    public bool Tts { get; set; } = true;
    public bool Stt { get; set; } = true;

    /// <summary>
    /// Returns the list of enabled capability categories and their commands
    /// for registration on the WindowsNodeClient.
    /// </summary>
    public IReadOnlyList<(string Category, string[] Commands)> GetEnabledCapabilities()
    {
        var result = new List<(string, string[])>();

        if (System) result.Add(("system", ["system.notify", "system.run", "system.run.prepare", "system.which", "system.execApprovals.get", "system.execApprovals.set"]));
        if (Canvas) result.Add(("canvas", ["canvas.present", "canvas.hide", "canvas.navigate", "canvas.eval", "canvas.snapshot", "canvas.a2ui.push", "canvas.a2ui.pushJSONL", "canvas.a2ui.reset", "canvas.a2ui.dump", "canvas.caps"]));
        if (Screen) result.Add(("screen", ["screen.snapshot", "screen.record"]));
        if (Camera) result.Add(("camera", ["camera.list", "camera.snap", "camera.clip"]));
        if (Location) result.Add(("location", ["location.get"]));
        if (Tts) result.Add(("tts", ["tts.speak"]));
        if (Stt) result.Add(("stt", ["stt.transcribe", "stt.listen", "stt.status"]));
        if (Device) result.Add(("device", ["device.info", "device.status"]));
        if (Browser) result.Add(("browser", ["browser.proxy"]));

        return result;
    }

    public IReadOnlyList<string> GetEnabledCommandIds()
        => GetEnabledCapabilities()
            .SelectMany(capability => capability.Commands)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

// ─── Tray Settings (written to settings.json) ───

public sealed class TraySettingsConfig
{
    public bool EnableNodeMode { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public bool NodeSystemRunEnabled { get; set; } = true;
    public bool NodeCanvasEnabled { get; set; } = true;
    public bool NodeScreenEnabled { get; set; } = true;
    public bool NodeCameraEnabled { get; set; } = true;
    public bool NodeLocationEnabled { get; set; } = true;
    public bool NodeBrowserProxyEnabled { get; set; } = true;
    public bool NodeTtsEnabled { get; set; } = true;
    public bool NodeSttEnabled { get; set; } = true;

    /// <summary>
    /// Merges these settings into an existing settings.json (or creates a new one).
    /// Only overwrites the fields we control — preserves all other user settings.
    /// </summary>
    public void MergeIntoSettingsFile(string settingsPath)
    {
        Dictionary<string, JsonElement>? existing = null;

        if (File.Exists(settingsPath))
        {
            try
            {
                var content = File.ReadAllText(settingsPath);
                existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content, SetupConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                var backupPath = settingsPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(settingsPath, backupPath, overwrite: false);
                throw new InvalidDataException($"settings.json is corrupt; backed up to {backupPath}", ex);
            }
        }

        var setupOwnedSettings = new Dictionary<string, object>
        {
            ["EnableNodeMode"] = EnableNodeMode,
            ["AutoStart"] = AutoStart,
        };

        var initialDefaults = new Dictionary<string, object>
        {
            ["NodeSystemRunEnabled"] = NodeSystemRunEnabled,
            ["NodeCanvasEnabled"] = NodeCanvasEnabled,
            ["NodeScreenEnabled"] = NodeScreenEnabled,
            ["NodeCameraEnabled"] = NodeCameraEnabled,
            ["NodeLocationEnabled"] = NodeLocationEnabled,
            ["NodeBrowserProxyEnabled"] = NodeBrowserProxyEnabled,
            ["NodeTtsEnabled"] = NodeTtsEnabled,
            ["NodeSttEnabled"] = NodeSttEnabled
        };

        var settings = new Dictionary<string, object>();
        if (existing != null)
        {
            foreach (var kvp in existing)
                settings[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in setupOwnedSettings)
            settings[kvp.Key] = kvp.Value;

        foreach (var kvp in initialDefaults)
            settings.TryAdd(kvp.Key, kvp.Value);

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(settingsPath, json);
    }
}

// ─── Pairing Configuration ───

public sealed class PairingConfig
{
    // TODO: Wire OperatorScopes/NodeScopes/CliScopes into pairing requests
    // when the gateway protocol supports scoped token issuance.
    public int TimeoutSeconds { get; set; } = 60;
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
    public ICommandRunner Commands { get; }
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
    public string LocalDataDir { get; }

    // WSL PATH prefix using configured user
    public string WslPathPrefix => WslConstants.GetPathPrefix(Config.Wsl.User);

    public SetupContext(SetupConfig config, SetupLogger logger, TransactionJournal journal, ICommandRunner commands, CancellationToken ct)
    {
        Config = config;
        Logger = logger;
        Journal = journal;
        Commands = commands;
        CancellationToken = ct;

        DataDir = ResolveDataDir();
        LocalDataDir = ResolveLocalDataDir();

        DistroName = config.DistroName;
        GatewayUrl = config.EffectiveGatewayUrl;
    }

    public static string ResolveDataDir()
        => Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");

    public static string ResolveLocalDataDir()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR") is { Length: > 0 } localAppDataRoot)
            return Path.Combine(localAppDataRoot, "OpenClawTray");

        // Compatibility alias used by early SetupEngine tests/builds. Unlike
        // LOCALAPPDATA_DIR, this points directly at the OpenClawTray data folder.
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR") is { Length: > 0 } localDataDir)
            return localDataDir;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray");
    }
}
