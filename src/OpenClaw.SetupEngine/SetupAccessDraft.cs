namespace OpenClaw.SetupEngine;

public enum SetupGatewayRoute { ManagedWsl, Existing, Remote, McpOnly, Deferred, Native }
public enum SetupCapability { System, Canvas, Screen, Camera, Location, Browser, Tts, Stt }
public enum SetupCapabilityProfile { Custom = -1, ReadOnly, Standard, Full }
public enum SetupInstallRequirement { ManagedWslRoute, WslInspection, Replacement, LocalAi, NetworkingConsent, Tailscale }

/// <summary>Pure setup-only profiles. Device status and runtime transports are not profile members.</summary>
public static class SetupCapabilityProfiles
{
    public static IReadOnlyList<SetupCapability> Ordered { get; } =
        Array.AsReadOnly(Enum.GetValues<SetupCapability>());

    public static bool Contains(SetupCapabilityProfile profile, SetupCapability capability) => profile switch
    {
        SetupCapabilityProfile.ReadOnly => capability is SetupCapability.Canvas or SetupCapability.Screen,
        SetupCapabilityProfile.Standard => capability is SetupCapability.System or SetupCapability.Canvas
            or SetupCapability.Screen or SetupCapability.Tts or SetupCapability.Stt,
        SetupCapabilityProfile.Full => true,
        _ => false,
    };

    public static bool Get(CapabilitiesConfig config, SetupCapability capability) => capability switch
    {
        SetupCapability.System => config.System,
        SetupCapability.Canvas => config.Canvas,
        SetupCapability.Screen => config.Screen,
        SetupCapability.Camera => config.Camera,
        SetupCapability.Location => config.Location,
        SetupCapability.Browser => config.Browser,
        SetupCapability.Tts => config.Tts,
        SetupCapability.Stt => config.Stt,
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    public static void Set(CapabilitiesConfig config, SetupCapability capability, bool enabled)
    {
        switch (capability)
        {
            case SetupCapability.System: config.System = enabled; break;
            case SetupCapability.Canvas: config.Canvas = enabled; break;
            case SetupCapability.Screen: config.Screen = enabled; break;
            case SetupCapability.Camera: config.Camera = enabled; break;
            case SetupCapability.Location: config.Location = enabled; break;
            case SetupCapability.Browser: config.Browser = enabled; break;
            case SetupCapability.Tts: config.Tts = enabled; break;
            case SetupCapability.Stt: config.Stt = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(capability));
        }
    }

    public static SetupCapabilityProfile Detect(CapabilitiesConfig config)
    {
        foreach (var profile in new[] { SetupCapabilityProfile.ReadOnly, SetupCapabilityProfile.Standard, SetupCapabilityProfile.Full })
            if (Ordered.All(capability => Get(config, capability) == Contains(profile, capability)))
                return profile;
        return SetupCapabilityProfile.Custom;
    }
}

/// <summary>
/// Window-lifetime draft over the exact installation configuration. Mutations are in memory;
/// existing setup/connection owners alone commit settings and credentials.
/// </summary>
public sealed class SetupAccessDraft
{
    public SetupConfig Config { get; }
    public bool SkipWizardWithoutLocalAi { get; }
    public SetupGatewayRoute Route { get; private set; } = SetupGatewayRoute.ManagedWsl;
    public bool GatewayAvailable { get; private set; }
    public SetupNativeConnectionRequest NativeConnectionRequest { get; set; } = new();
    public string? NativeGatewayId { get; private set; }
    public bool CapabilityControlsEnabled => Config.Settings.EnableNodeMode || Config.Settings.EnableMcpServer == true;
    public bool BrowserAvailable => Config.Settings.EnableNodeMode && GatewayAvailable;
    public SetupCapabilityProfile Profile => IsCustomizingCapabilities
        ? SetupCapabilityProfile.Custom : SetupCapabilityProfiles.Detect(Config.Capabilities);
    public bool IsCustomizingCapabilities { get; private set; }
    public bool FineTuneExpanded { get; set; }
    public bool LocalAiReady { get; set; }
    public bool LocalAiNetworkingConsentRequired { get; set; }
    public bool TailscaleReady { get; set; }
    public bool WslInspectionComplete { get; private set; }
    public bool ReplacementReviewRequired { get; private set; }
    public bool DestructiveConfirmationRequired { get; private set; }
    public string ReplacementSummary { get; private set; } = string.Empty;
    private string? _inspectedDistroName;
    private string? _confirmedReplacementDistroName;
    private readonly string? _managedGatewayUrl;

    public SetupAccessDraft(SetupConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _managedGatewayUrl = config.GatewayUrl;
        NativeConnectionRequest = new(config.GatewayUrl ?? "");
        SkipWizardWithoutLocalAi = config.SkipWizard;
        config.Settings.EnableMcpServer ??= false;
        config.Settings.NodeOllamaInferenceEnabled ??= false;
        config.Capabilities.Device = true;
        if (config.UsesBundledDefaultConfig && Profile == SetupCapabilityProfile.Full)
            ApplyProfile(SetupCapabilityProfile.Standard);
        else
            config.Settings.ApplyCapabilities(config.Capabilities);
        IsCustomizingCapabilities = Profile == SetupCapabilityProfile.Custom;
    }

    public void SelectRoute(SetupGatewayRoute route, bool gatewayAvailable = false)
    {
        if (route == SetupGatewayRoute.ManagedWsl && Route != SetupGatewayRoute.ManagedWsl)
            Config.GatewayUrl = _managedGatewayUrl;
        Route = route;
        GatewayAvailable = gatewayAvailable;
    }

    public bool TryAcceptNativeConnection(SetupGatewayRoute route, SetupNativeConnectionResult result)
    {
        if (route is not (SetupGatewayRoute.Existing or SetupGatewayRoute.Remote) ||
            !result.Success || !result.GatewayCommitted ||
            string.IsNullOrWhiteSpace(result.GatewayId) || string.IsNullOrWhiteSpace(result.GatewayUrl))
            return false;

        Config.GatewayUrl = result.GatewayUrl;
        NativeGatewayId = result.GatewayId;
        SelectRoute(route, gatewayAvailable: true);
        return true;
    }

    public void ClearNativeConnectionSecrets() =>
        NativeConnectionRequest = NativeConnectionRequest with { SetupCode = null, SharedToken = null };

    public bool GetCapability(SetupCapability capability) => SetupCapabilityProfiles.Get(Config.Capabilities, capability);

    public void SetCapability(SetupCapability capability, bool enabled)
    {
        SetupCapabilityProfiles.Set(Config.Capabilities, capability, enabled);
        IsCustomizingCapabilities = true;
        Config.Capabilities.Device = true;
        Config.Settings.ApplyCapabilities(Config.Capabilities);
    }

    public void ApplyProfile(SetupCapabilityProfile profile)
    {
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        IsCustomizingCapabilities = profile == SetupCapabilityProfile.Custom;
        if (profile == SetupCapabilityProfile.Custom)
            return;
        foreach (var capability in SetupCapabilityProfiles.Ordered)
            SetupCapabilityProfiles.Set(Config.Capabilities, capability, SetupCapabilityProfiles.Contains(profile, capability));
        Config.Capabilities.Device = true;
        Config.Settings.ApplyCapabilities(Config.Capabilities);
    }

    public void SetNodeMode(bool enabled) => Config.Settings.EnableNodeMode = enabled;
    public void SetMcpServer(bool enabled) => Config.Settings.EnableMcpServer = enabled;
    public void SetOllamaSharing(bool enabled) => Config.Settings.NodeOllamaInferenceEnabled = enabled;
    public void SetLocalAiEnabled(bool enabled)
    {
        Config.LocalAi.Enabled = enabled;
        Config.SkipWizard = enabled || SkipWizardWithoutLocalAi;
    }

    public void RecordWslInspection(ExistingConfigDetector.ExistingConfig existing)
    {
        _inspectedDistroName = Config.DistroName;
        WslInspectionComplete = true;
        ReplacementReviewRequired = existing.HasLocalGateway || existing.HasDistro || existing.HasDistroDataDirectory;
        DestructiveConfirmationRequired = ExistingConfigDetector.RequiresDestructiveConfirmation(existing);
        ReplacementSummary = ExistingConfigDetector.BuildReplacementSummary(existing);
        if (!ReplacementReviewRequired)
            _confirmedReplacementDistroName = null;
    }

    public void ConfirmReplacement(bool confirmed)
    {
        _confirmedReplacementDistroName = confirmed && WslInspectionComplete ? _inspectedDistroName : null;
        Config.ConfirmedDestructiveDistroName = confirmed && DestructiveConfirmationRequired
            ? _inspectedDistroName : null;
    }

    public bool ReplacementConfirmed => !ReplacementReviewRequired ||
        string.Equals(_confirmedReplacementDistroName, Config.DistroName, StringComparison.Ordinal);

    public bool CanInstall(bool localAiRecovery = false) => GetInstallRequirements(localAiRecovery).Count == 0;

    /// <summary>Ordered blockers shared by installation admission and the review's navigation-only actions.</summary>
    public IReadOnlyList<SetupInstallRequirement> GetInstallRequirements(bool localAiRecovery = false)
    {
        List<SetupInstallRequirement> requirements = [];
        if (Route != SetupGatewayRoute.ManagedWsl &&
            !(localAiRecovery && !string.IsNullOrWhiteSpace(Config.LocalAiRecoveryGatewayId)))
            requirements.Add(SetupInstallRequirement.ManagedWslRoute);
        if (!localAiRecovery)
        {
            if (!WslInspectionComplete || !string.Equals(_inspectedDistroName, Config.DistroName, StringComparison.Ordinal))
                requirements.Add(SetupInstallRequirement.WslInspection);
            else if (!ReplacementConfirmed || DestructiveConfirmationRequired &&
                !string.Equals(Config.ConfirmedDestructiveDistroName, Config.DistroName, StringComparison.Ordinal))
                requirements.Add(SetupInstallRequirement.Replacement);
        }
        if (localAiRecovery && !Config.LocalAi.Enabled || Config.LocalAi.Enabled && !LocalAiReady)
            requirements.Add(SetupInstallRequirement.LocalAi);
        if (Config.LocalAi.Enabled && LocalAiNetworkingConsentRequired && !Config.LocalAi.WslMirroredNetworkingConsent)
            requirements.Add(SetupInstallRequirement.NetworkingConsent);
        if ((!localAiRecovery || string.IsNullOrWhiteSpace(Config.LocalAiRecoveryGatewayId)) &&
            Config.Tailscale.Enabled && (!TailscaleReady ||
            Config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey && string.IsNullOrWhiteSpace(Config.Tailscale.AuthKey)))
            requirements.Add(SetupInstallRequirement.Tailscale);
        return requirements;
    }
}
