using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

// ─── Configuration ───

[JsonConverter(typeof(JsonStringEnumConverter<GatewayInstallMode>))]
public enum GatewayInstallMode
{
    Wsl,
    NativeWindows,
}

public sealed class SetupConfig
{
    public GatewayInstallMode InstallMode { get; set; } = GatewayInstallMode.Wsl;
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
    [JsonIgnore]
    internal bool GatewayVersionWasDefaulted { get; set; }
    public bool ConfirmDestructive { get; set; }
    public string LogLevel { get; set; } = "trace";
    public string? LogPath { get; set; }
    public string? GatewayUrl { get; set; }
    public string? BootstrapToken { get; set; }
    public Dictionary<string, string>? WizardAnswers { get; set; }
    [JsonIgnore]
    public bool UsesBundledDefaultConfig { get; set; }

    // Nested config sections — everything is configurable
    public WslConfig Wsl { get; set; } = new();
    public GatewayConfig Gateway { get; set; } = new();
    public CapabilitiesConfig Capabilities { get; set; } = new();
    public TraySettingsConfig Settings { get; set; } = new();
    public PairingConfig Pairing { get; set; } = new();
    public WindowsNodeContextConfig WindowsNodeContext { get; set; } = new();

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
        if (TryParseInstallMode(Environment.GetEnvironmentVariable("OPENCLAW_SETUP_MODE"), out var mode))
            config.InstallMode = mode;

        return config;
    }

    internal static bool TryParseInstallMode(string? value, out GatewayInstallMode mode)
    {
        mode = GatewayInstallMode.Wsl;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "native":
            case "windows":
            case "nativewindows":
            case "native-windows":
                mode = GatewayInstallMode.NativeWindows;
                return true;
            case "wsl":
                return true;
            default:
                return false;
        }
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

    /// <summary>
    /// Minimal write-only options for producing human-readable JSON files.
    /// Shared to avoid repeated heap allocation at call sites.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
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
    public string? WindowsInstallUrl { get; set; }
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
        if (Tts) result.Add(("tts", ["tts.speak", "tts.status"]));
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
                throw BackupCorruptSettingsFile(settingsPath, ex);
            }
        }

        var setupOwnedSettings = new Dictionary<string, object>
        {
            ["EnableNodeMode"] = EnableNodeMode,
            ["AutoStart"] = AutoStart,
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

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var json = JsonSerializer.Serialize(settings, SetupConfig.JsonWriteOptions);
        AtomicFile.WriteAllText(settingsPath, json);
    }

    public static void UpdateAutoStartInSettingsFile(string settingsPath, bool autoStart)
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
                throw BackupCorruptSettingsFile(settingsPath, ex);
            }
        }

        var settings = new Dictionary<string, object>();
        if (existing != null)
        {
            foreach (var kvp in existing)
                settings[kvp.Key] = kvp.Value;
        }

        settings["AutoStart"] = autoStart;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var json = JsonSerializer.Serialize(settings, SetupConfig.JsonWriteOptions);
        AtomicFile.WriteAllText(settingsPath, json);
    }

    public void ApplyCapabilities(CapabilitiesConfig capabilities)
    {
        // Device info has no independent runtime setting; it is always registered
        // when node mode is enabled.
        NodeSystemRunEnabled = capabilities.System;
        NodeCanvasEnabled = capabilities.Canvas;
        NodeScreenEnabled = capabilities.Screen;
        NodeCameraEnabled = capabilities.Camera;
        NodeLocationEnabled = capabilities.Location;
        NodeBrowserProxyEnabled = capabilities.Browser;
        NodeTtsEnabled = capabilities.Tts;
        NodeSttEnabled = capabilities.Stt;
    }

    private static InvalidDataException BackupCorruptSettingsFile(string settingsPath, JsonException ex)
    {
        var backupPath = settingsPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.bak";
        File.Copy(settingsPath, backupPath, overwrite: false);
        return new InvalidDataException($"settings.json is corrupt; backed up to {backupPath}", ex);
    }
}

// ─── Pairing Configuration ───

public sealed class PairingConfig
{
    // TODO: Wire OperatorScopes/NodeScopes/CliScopes into pairing requests
    // when the gateway protocol supports scoped token issuance.
    public int TimeoutSeconds { get; set; } = 60;
}

// ─── Windows Node Context Injection ───

public sealed class WindowsNodeContextConfig
{
    public bool Enabled { get; set; } = true;
    public string? WorkspacePath { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
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
    internal bool GatewayRecordCreatedThisRun { get; set; }
    public string? OperatorDeviceId { get; set; }
    public string? NodeDeviceId { get; set; }
    public string? NativeCliPath { get; set; }
    internal ReplacedGatewaysSnapshot? ReplacedGateways { get; set; }
    internal NativeGatewayRollbackState? PreviousNativeGateway { get; set; }
    internal NativeOwnershipMarkerRollbackState? PreviousNativeOwnershipMarker { get; set; }
    internal WslGatewayRollbackState? PreviousWslGateway { get; set; }
    internal ActiveGatewayRollbackState? ActiveGatewayBeforePairing { get; set; }
    internal bool IsUninstalling { get; set; }

    // Data directory for gateway registry and identity files
    public string DataDir { get; }
    public string LocalDataDir { get; }

    // WSL PATH prefix using configured user
    public string WslPathPrefix => WslConstants.GetPathPrefix(Config.Wsl.User);

    public SetupContext(
        SetupConfig config,
        SetupLogger logger,
        TransactionJournal journal,
        ICommandRunner commands,
        CancellationToken ct,
        string? dataDir = null,
        string? localDataDir = null)
    {
        Config = config;
        Logger = logger;
        Journal = journal;
        Commands = commands;
        CancellationToken = ct;

        DataDir = dataDir ?? ResolveDataDir();
        LocalDataDir = localDataDir ?? ResolveLocalDataDir();

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

internal sealed record GatewayIdentityFileSnapshot(string RelativePath, byte[] Contents);

internal sealed record ReplacedGatewayRecordSnapshot(
    GatewayRecord Record,
    IReadOnlyList<GatewayIdentityFileSnapshot> IdentityFiles);

internal sealed record ReplacedGatewaysSnapshot(
    IReadOnlyList<ReplacedGatewayRecordSnapshot> Records,
    string? PreviousActiveGatewayId);

internal sealed record NativeGatewayRollbackState(
    string ConfigPath,
    bool ConfigExisted,
    byte[]? ConfigContents,
    bool ServiceInstalled,
    bool WasRunning,
    string? OwnershipMarkerPath = null,
    bool OwnershipMarkerExisted = false,
    byte[]? OwnershipMarkerContents = null);

internal sealed record NativeOwnershipMarkerRollbackState(
    NativeOwnershipMarkerFileRollbackState Active,
    NativeOwnershipMarkerFileRollbackState Profile);

internal sealed record NativeOwnershipMarkerFileRollbackState(
    string Path,
    bool Existed,
    byte[]? Contents);

internal sealed record WslGatewayRollbackState(
    string DistroName,
    bool WasRunning,
    bool HadManagedKeepalive);

internal sealed record ActiveGatewayRollbackState(string? GatewayId);
