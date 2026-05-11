using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Detects whether an existing OpenClaw configuration is present in tray settings,
/// device identity, or setup-state storage.
/// Used to gate the local easy-button setup flow so returning users receive an
/// explicit warn-and-confirm dialog before potentially overwriting their credentials.
/// </summary>
public sealed class OnboardingExistingConfigGuard
{
    private const string DefaultGatewayUrl = "ws://localhost:18789";
    private readonly SettingsManager _settings;
    private readonly string _identityDataPath;
    private readonly string _setupStatePath;

    public OnboardingExistingConfigGuard(
        SettingsManager settings,
        string identityDataPath,
        string? setupStatePath = null)
    {
        _settings = settings;
        _identityDataPath = identityDataPath;
        _setupStatePath = setupStatePath ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray",
            "setup-state.json");
    }

    /// <summary>
    /// Returns true if any existing configuration is detected (sync, cheap).
    /// Checks in-memory settings, device-key-ed25519.json, and setup-state.json.
    /// Does NOT probe WSL distros (async-only path).
    /// </summary>
    public bool HasExistingConfiguration() => GetSummary().HasAny;

    /// <summary>
    /// Returns a detailed breakdown of which configuration components exist.
    /// Sync — reads settings (in-memory), device-key files, and setup-state.json.
    /// </summary>
    public ExistingConfigurationSummary GetSummary()
    {
        return new ExistingConfigurationSummary(
            HasToken: false, // Tokens are managed by GatewayRegistry, not settings
            HasBootstrapToken: false,
            HasNonDefaultGatewayUrl: !string.IsNullOrWhiteSpace(_settings.GatewayUrl)
                && !string.Equals(_settings.GatewayUrl, DefaultGatewayUrl, StringComparison.OrdinalIgnoreCase),
            HasOperatorDeviceToken: DeviceIdentity.HasStoredDeviceToken(_identityDataPath),
            HasNodeDeviceToken: DeviceIdentity.HasStoredDeviceTokenForRole(_identityDataPath, "node"),
            HasCompletedOrRunningSetupState: ReadSetupStateIsActive(_setupStatePath),
            HasWslDistro: false);
    }

    /// <summary>
    /// Async-enriched summary that also probes WSL for the OpenClawGateway distro.
    /// </summary>
    public async Task<ExistingConfigurationSummary> GetSummaryAsync(
        IWslCommandRunner? wsl = null,
        CancellationToken ct = default)
    {
        var sync = GetSummary();
        var hasDistro = false;
        if (wsl != null)
        {
            try
            {
                var result = await wsl.RunAsync(["--list", "--verbose"], ct);
                hasDistro = result.StandardOutput.Contains("OpenClawGateway", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Best-effort — distro probe failure does not block the gate.
            }
        }
        return sync with { HasWslDistro = hasDistro };
    }

    private static bool ReadSetupStateIsActive(string statePath)
    {
        if (!File.Exists(statePath))
            return false;
        try
        {
            var json = File.ReadAllText(statePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Phase", out var phaseEl))
            {
                var phaseName = phaseEl.GetString();
                // Active (returns true) if phase is NOT in the safe-to-restart set
                return phaseName is not (null or "NotStarted" or "Failed" or "Cancelled");
            }
        }
        catch
        {
            // Best-effort — malformed state file does not block the gate.
        }
        return false;
    }
}

/// <summary>
/// Breakdown of which existing configuration components were found.
/// </summary>
public sealed record ExistingConfigurationSummary(
    bool HasToken,
    bool HasBootstrapToken,
    bool HasNonDefaultGatewayUrl,
    bool HasOperatorDeviceToken,
    bool HasNodeDeviceToken,
    bool HasCompletedOrRunningSetupState,
    bool HasWslDistro)
{
    /// <summary>True if any configuration component exists.</summary>
    public bool HasAny =>
        HasToken || HasBootstrapToken || HasNonDefaultGatewayUrl
        || HasOperatorDeviceToken || HasNodeDeviceToken
        || HasCompletedOrRunningSetupState || HasWslDistro;
}
