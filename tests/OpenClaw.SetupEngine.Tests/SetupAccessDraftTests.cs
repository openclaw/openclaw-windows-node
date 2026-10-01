namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupAccessDraftTests
{
    [Fact]
    public void ExistingNativeLocalAi_BindsExactGatewayWithoutWslFinalizationOrSettingsReplacement()
    {
        var config = new SetupConfig();
        var draft = new SetupAccessDraft(config);
        draft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
        draft.SetNodeMode(false);
        draft.SetMcpServer(true);
        var settings = System.Text.Json.JsonSerializer.Serialize(config.Settings);
        var record = new OpenClaw.Connection.GatewayRecord
        {
            Id = "native-owner", Url = "ws://127.0.0.1:55123",
            NativePackageFamilyName = "native-package",
            NativeRuntimeContract = OpenClaw.Connection.NativeGateway.NativeGatewayPackageClient.IsolatedContract
        };
        draft.SelectExistingNativeGateway(record);
        Assert.Equal(SetupGatewayRoute.Existing, draft.Route);
        Assert.True(draft.IsExistingNativeLocalAi);
        Assert.True(draft.GatewayAvailable);
        Assert.False(OnboardingFlowPolicy.UsesWslWorkspaceFinalization(draft.Route));
        Assert.Equal(record.Id, draft.NativeGatewayId);
        Assert.Equal(record.Url, config.GatewayUrl);
        Assert.Equal(OpenClaw.Connection.GatewayDashboardBinding.Capture(record), draft.NativeEndpointBinding);
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Serialize(config.Settings));
        draft.SelectRoute(SetupGatewayRoute.ManagedWsl);
        Assert.False(draft.IsExistingNativeLocalAi);
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Existing)]
    [InlineData(SetupGatewayRoute.Remote)]
    public void NativeCommit_UpdatesTheSameConfigAndRetainsTheEditor(SetupGatewayRoute route)
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        var config = draft.Config;
        var request = new SetupNativeConnectionRequest("wss://native.example", EditingGatewayId: "committed-id");
        draft.NativeConnectionRequest = request;
        var binding = OpenClaw.Connection.GatewayDashboardBinding.Capture(
            new() { Id = "committed-id", Url = "wss://native.example" });
        Assert.True(draft.TryAcceptNativeConnection(route,
            new(true, true, "committed-id", "wss://native.example", EndpointBinding: binding)));
        Assert.Same(config, draft.Config);
        Assert.Equal("wss://native.example", config.GatewayUrl);
        Assert.Equal("committed-id", draft.NativeGatewayId);
        Assert.Equal(binding, draft.NativeEndpointBinding);
        Assert.True(draft.GatewayAvailable);
        Assert.Equal(route, draft.Route);
        draft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
        Assert.Same(request, draft.NativeConnectionRequest);
        Assert.Equal("committed-id", draft.NativeGatewayId);
        draft.SelectRoute(SetupGatewayRoute.ManagedWsl);
        Assert.Null(config.GatewayUrl);
        Assert.Null(draft.NativeGatewayId);
        Assert.Null(draft.NativeEndpointBinding);
        Assert.Same(request, draft.NativeConnectionRequest);
    }

    [Theory]
    [InlineData(false, false, "id", "wss://native.example")]
    [InlineData(true, false, "id", "wss://native.example")]
    [InlineData(true, true, null, "wss://native.example")]
    [InlineData(true, true, "id", null)]
    [InlineData(true, true, "id", "wss://native.example")]
    public void NativeCheckOrInvalidCommit_CannotAdvanceOrChangeConfig(
        bool success, bool committed, string? id, string? url)
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.SelectRoute(SetupGatewayRoute.Remote);
        Assert.False(draft.TryAcceptNativeConnection(SetupGatewayRoute.Remote,
            new(success, committed, id, url)));
        Assert.Null(draft.Config.GatewayUrl);
        Assert.False(draft.GatewayAvailable);
        Assert.Null(draft.NativeGatewayId);
    }

    [Fact]
    public void NativeDraftSecrets_AreNotSetupConfigAndAreClearedOnlyOnExplicitCleanup()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.NativeConnectionRequest = new("wss://native.example", "setup-secret", "shared-secret");
        draft.SelectRoute(SetupGatewayRoute.Deferred);
        Assert.Equal("setup-secret", draft.NativeConnectionRequest.SetupCode);
        var configJson = System.Text.Json.JsonSerializer.Serialize(draft.Config);
        Assert.DoesNotContain("setup-secret", configJson);
        Assert.DoesNotContain("shared-secret", configJson);
        draft.ClearNativeConnectionSecrets();
        Assert.Null(draft.NativeConnectionRequest.SetupCode);
        Assert.Null(draft.NativeConnectionRequest.SharedToken);
        Assert.Equal("wss://native.example", draft.NativeConnectionRequest.GatewayUrl);
    }

    [Theory]
    [InlineData("""{"CurrentTailnet":{"MagicDNSEnabled":true}}""", true)]
    [InlineData("""{"CurrentTailnet":{"MagicDNSEnabled":false}}""", false)]
    [InlineData("""{"CurrentTailnet":null}""", false)]
    [InlineData("""{"Self":{"DNSName":"pc.tailnet.ts.net."}}""", false)]
    [InlineData("invalid", false)]
    public void Tailscale_MagicDnsMustBeConfirmedNotInferredFromAHostname(string json, bool ready) =>
        Assert.Equal(ready, SetupTailscaleReadiness.IsMagicDnsEnabled(json));

    [Fact]
    public void ProfileCatalog_ContainsExactlyEightInStableOrder()
    {
        Assert.Equal(
            new[] { SetupCapability.System, SetupCapability.Canvas, SetupCapability.Screen,
                SetupCapability.Camera, SetupCapability.Location, SetupCapability.Browser,
                SetupCapability.Tts, SetupCapability.Stt },
            SetupCapabilityProfiles.Ordered);
    }

    [Fact]
    public void Draft_ProjectsExplicitTransportDefaultsWithoutChangingHeadlessDefaults()
    {
        var config = new SetupConfig();
        Assert.Null(config.Settings.EnableMcpServer);
        Assert.Null(config.Settings.NodeOllamaInferenceEnabled);
        var draft = new SetupAccessDraft(config);
        Assert.False(draft.Config.Settings.EnableMcpServer);
        Assert.False(draft.Config.Settings.NodeOllamaInferenceEnabled);
        Assert.True(draft.Config.Settings.EnableNodeMode);
    }

    [Theory]
    [InlineData(SetupCapabilityProfile.ReadOnly, "Canvas,Screen")]
    [InlineData(SetupCapabilityProfile.Standard, "System,Canvas,Screen,Tts,Stt")]
    [InlineData(SetupCapabilityProfile.Full, "System,Canvas,Screen,Camera,Location,Browser,Tts,Stt")]
    public void Profiles_HaveExactMembership(SetupCapabilityProfile profile, string expected)
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.ApplyProfile(profile);
        Assert.Equal(expected, string.Join(",", SetupCapabilityProfiles.Ordered.Where(draft.GetCapability)));
        Assert.Equal(profile, draft.Profile);
        Assert.True(draft.Config.Capabilities.Device);
        Assert.Equal(draft.Config.Capabilities.System, draft.Config.Settings.NodeSystemRunEnabled);
    }

    [Fact]
    public void BundledPlaceholder_DefaultsOnlyAtDraftCreation()
    {
        var config = new SetupConfig { UsesBundledDefaultConfig = true };
        var draft = new SetupAccessDraft(config);
        Assert.Same(config, draft.Config);
        Assert.Equal(SetupCapabilityProfile.Standard, draft.Profile);
        draft.ApplyProfile(SetupCapabilityProfile.Full);
        draft.SelectRoute(SetupGatewayRoute.Remote);
        draft.SelectRoute(SetupGatewayRoute.ManagedWsl);
        Assert.Equal(SetupCapabilityProfile.Full, draft.Profile);
        Assert.Equal(SetupCapabilityProfile.Full, new SetupAccessDraft(new SetupConfig()).Profile);
    }

    [Fact]
    public void CustomSelection_IsNeverWidenedByTransportOrRouteChanges()
    {
        var config = new SetupConfig { UsesBundledDefaultConfig = true };
        config.Capabilities.Camera = false;
        var draft = new SetupAccessDraft(config);
        Assert.Equal(SetupCapabilityProfile.Custom, draft.Profile);
        var selection = SetupCapabilityProfiles.Ordered.Select(draft.GetCapability).ToArray();
        draft.SetNodeMode(false);
        draft.SetMcpServer(false);
        Assert.False(draft.CapabilityControlsEnabled);
        draft.SelectRoute(SetupGatewayRoute.McpOnly);
        draft.SetMcpServer(true);
        Assert.True(draft.CapabilityControlsEnabled);
        Assert.False(draft.BrowserAvailable);
        Assert.Equal(selection, SetupCapabilityProfiles.Ordered.Select(draft.GetCapability));
    }

    [Fact]
    public void Browser_RequiresNodeAndAnActualGateway()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        Assert.False(draft.BrowserAvailable);
        draft.SelectRoute(SetupGatewayRoute.Remote, gatewayAvailable: true);
        Assert.True(draft.BrowserAvailable);
        draft.SetNodeMode(false);
        draft.SetMcpServer(true);
        Assert.False(draft.BrowserAvailable);
        Assert.True(draft.GetCapability(SetupCapability.Browser));
    }

    [Fact]
    public void ProfileChanges_DoNotChangeIndependentSettingsOrConsent()
    {
        var config = new SetupConfig();
        config.Settings.EnableMcpServer = true;
        config.Settings.NodeOllamaInferenceEnabled = true;
        config.LocalAi.WslMirroredNetworkingConsent = true;
        config.LocalAi.SelectedModelId = "pinned";
        config.Tailscale.AuthKey = "session-only";
        config.Tailscale.TrustTailscaleAuth = true;
        var draft = new SetupAccessDraft(config);
        draft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
        draft.SetNodeMode(false);
        Assert.True(config.Settings.EnableMcpServer);
        Assert.True(config.Settings.NodeOllamaInferenceEnabled);
        Assert.True(config.LocalAi.WslMirroredNetworkingConsent);
        Assert.Equal("pinned", config.LocalAi.SelectedModelId);
        Assert.Equal("session-only", config.Tailscale.AuthKey);
        Assert.True(config.Tailscale.TrustTailscaleAuth);
    }

    [Fact]
    public void EveryCapabilityCombination_HasOnlyAnExactPresetMatch()
    {
        for (var flags = 0; flags < 256; flags++)
        {
            var config = new SetupConfig();
            foreach (var capability in SetupCapabilityProfiles.Ordered)
                SetupCapabilityProfiles.Set(config.Capabilities, capability, (flags & (1 << (int)capability)) != 0);
            var draft = new SetupAccessDraft(config);
            var expected = flags switch
            {
                6 => SetupCapabilityProfile.ReadOnly,
                199 => SetupCapabilityProfile.Standard,
                255 => SetupCapabilityProfile.Full,
                _ => SetupCapabilityProfile.Custom,
            };
            Assert.Equal(expected, draft.Profile);
        }
    }

    [Fact]
    public void CustomIntent_IsDraftOnlyPreservesValuesAndSurvivesIndependentChoices()
    {
        var draft = FreshDraft();
        Assert.False(draft.IsCustomizingCapabilities);
        draft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
        var before = System.Text.Json.JsonSerializer.Serialize(draft.Config);
        draft.ApplyProfile(SetupCapabilityProfile.Custom);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(draft.Config));
        Assert.Equal(SetupCapabilityProfile.Custom, draft.Profile);
        draft.SetCapability(SetupCapability.Camera, true);
        draft.SetCapability(SetupCapability.Camera, false);
        Assert.Equal(SetupCapabilityProfile.ReadOnly, SetupCapabilityProfiles.Detect(draft.Config.Capabilities));
        Assert.Equal(SetupCapabilityProfile.Custom, draft.Profile);
        draft.SetNodeMode(false);
        draft.SetMcpServer(true);
        draft.SetOllamaSharing(true);
        draft.SelectRoute(SetupGatewayRoute.Existing, gatewayAvailable: true);
        Assert.True(draft.IsCustomizingCapabilities);
        Assert.Equal(SetupCapabilityProfile.Custom, draft.Profile);
        Assert.DoesNotContain("IsCustomizingCapabilities", System.Text.Json.JsonSerializer.Serialize(draft.Config));
        draft.ApplyProfile(SetupCapabilityProfile.Standard);
        draft.SelectRoute(SetupGatewayRoute.ManagedWsl);
        Assert.False(draft.IsCustomizingCapabilities);
        Assert.Equal(SetupCapabilityProfile.Standard, draft.Profile);
        Assert.False(draft.GetCapability(SetupCapability.Camera));
        Assert.False(draft.Config.Settings.EnableNodeMode);
        Assert.True(draft.Config.Settings.EnableMcpServer);
        Assert.True(draft.Config.Settings.NodeOllamaInferenceEnabled);
    }

    [Theory]
    [InlineData(SetupCapabilityProfile.ReadOnly)]
    [InlineData(SetupCapabilityProfile.Standard)]
    [InlineData(SetupCapabilityProfile.Full)]
    public void FineTuneInspection_IsDraftOnlyAndNeverSelectsCustom(SetupCapabilityProfile profile)
    {
        var draft = FreshDraft();
        draft.ApplyProfile(profile);
        var before = System.Text.Json.JsonSerializer.Serialize(draft.Config);
        draft.FineTuneExpanded = true;
        Assert.Equal(profile, draft.Profile);
        Assert.False(draft.IsCustomizingCapabilities);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(draft.Config));
        draft.SetNodeMode(false);
        draft.SetMcpServer(false);
        Assert.True(draft.FineTuneExpanded);
        draft.ApplyProfile(SetupCapabilityProfile.Full);
        Assert.True(draft.FineTuneExpanded);
        draft.FineTuneExpanded = false;
        Assert.Equal(SetupCapabilityProfile.Full, draft.Profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalAiToggle_RestoresOriginalWizardChoiceAndRetainsConsent(bool originalSkip)
    {
        var config = new SetupConfig { SkipWizard = originalSkip };
        var draft = new SetupAccessDraft(config);
        config.LocalAi.WslMirroredNetworkingConsent = true;
        draft.SetLocalAiEnabled(true);
        Assert.True(config.SkipWizard);
        draft.SetLocalAiEnabled(false);
        Assert.Equal(originalSkip, config.SkipWizard);
        Assert.True(config.LocalAi.WslMirroredNetworkingConsent);
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Existing)]
    [InlineData(SetupGatewayRoute.Remote)]
    [InlineData(SetupGatewayRoute.McpOnly)]
    [InlineData(SetupGatewayRoute.Deferred)]
    public void AlternateRoutes_NeverInstallWsl(SetupGatewayRoute route)
    {
        var draft = FreshDraft();
        draft.SelectRoute(route, gatewayAvailable: true);
        Assert.False(draft.CanInstall());
        Assert.False(draft.CanInstall(localAiRecovery: true));
        Assert.DoesNotContain(OnboardingStage.Install, OnboardingFlowPolicy.GetStages(route, draft.Config));
    }

    [Fact]
    public void Replacement_RequiresExplicitConsentBoundToExactDistro()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.RecordWslInspection(Existing(draft.Config.DistroName));
        Assert.False(draft.CanInstall());
        draft.ConfirmReplacement(true);
        Assert.True(draft.CanInstall());
        Assert.Equal(draft.Config.DistroName, draft.Config.ConfirmedDestructiveDistroName);
        draft.Config.DistroName = "OtherGateway";
        Assert.False(draft.CanInstall());
        draft.ConfirmReplacement(true);
        Assert.False(draft.CanInstall());
    }

    [Fact]
    public void LocalAiUnknownAndUnsupportedRecovery_FailClosedButCanOptOutOutsideRecovery()
    {
        var draft = FreshDraft();
        draft.SetLocalAiEnabled(true);
        Assert.False(draft.CanInstall());
        Assert.False(draft.CanInstall(localAiRecovery: true));
        draft.SetLocalAiEnabled(false);
        Assert.True(draft.CanInstall());
        Assert.False(draft.CanInstall(localAiRecovery: true));
    }

    [Fact]
    public void LocalAiAndTailscale_RequireIndependentReadinessAndExplicitConsent()
    {
        var draft = FreshDraft();
        draft.SetLocalAiEnabled(true);
        draft.LocalAiReady = true;
        draft.LocalAiNetworkingConsentRequired = true;
        Assert.False(draft.CanInstall());
        draft.Config.LocalAi.WslMirroredNetworkingConsent = true;
        Assert.True(draft.CanInstall());
        draft.Config.Tailscale.Enabled = true;
        Assert.False(draft.CanInstall());
        draft.TailscaleReady = true;
        Assert.True(draft.CanInstall());
        draft.Config.Tailscale.AuthMode = TailscaleAuthMode.AuthKey;
        Assert.False(draft.CanInstall());
        draft.Config.Tailscale.AuthKey = "one-off";
        Assert.True(draft.CanInstall());
        Assert.False(draft.Config.Tailscale.TrustTailscaleAuth);
    }

    private static SetupAccessDraft FreshDraft()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.RecordWslInspection(new(false, null, null, false, false, false, null, false, 0, []));
        return draft;
    }

    private static ExistingConfigDetector.ExistingConfig Existing(string name) =>
        new(false, null, null, true, true, false, name, false, 1, ["Preserved remote"]);
}
