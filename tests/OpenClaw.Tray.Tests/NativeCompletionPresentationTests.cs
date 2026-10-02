using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class NativeCompletionPresentationTests
{
    [Fact]
    public void CandidateCreationReturnsItsBaselineWithoutAnUnlockedPostCopyRead()
    {
        var source = Read(@"src\OpenClaw.Tray.WinUI\Services\GatewayDirectConnectService.cs");
        Assert.Contains("candidateIdentityCreation = validationIdentity.CopyTo(", source);
        Assert.DoesNotContain("candidateIdentityHash", source);
        Assert.DoesNotContain("File.ReadAllBytes", source);
        var identity = Read(@"src\OpenClaw.Connection\GatewayValidationIdentity.cs");
        Assert.Contains("return DeviceIdentity.ReplaceValidatedIdentity(destinationDirectory, null, CreateCommittedJson())", identity);
        var registry = Read(@"src\OpenClaw.Connection\GatewayRegistry.cs");
        Assert.Contains("return DeviceIdentity.RemoveCreatedIdentity(creation)", registry);
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), path));

    [Theory]
    [InlineData("PageDrain")]
    [InlineData("Connection")]
    [InlineData("ModelRecovery")]
    [InlineData("ModelVerification")]
    [InlineData("GatewayStartup")]
    public void TimeoutPhasesHaveLocalizedUserGuidanceWithoutRenderingRawErrors(string phase)
    {
        foreach (var language in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var resources = XDocument.Parse(Read($@"src\OpenClaw.Tray.WinUI\Strings\{language}\Resources.resw"));
            var entry = Assert.Single(resources.Descendants("data"),
                item => (string?)item.Attribute("name") == "Onboarding_Ready_Timeout" + phase);
            Assert.False(string.IsNullOrWhiteSpace(entry.Element("value")?.Value));
        }
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiReadyPage.xaml.cs");
        Assert.Contains("\"Onboarding_Ready_Timeout\" + timeout.Phase", page);
        Assert.Contains("Phase: SetupNativeCompletionPhase.Verification } => \"Onboarding_Ready_Timeout\"", page);
        Assert.Contains("\"Onboarding_Ready_TimeoutGatewayStartup\"", page);
        Assert.DoesNotContain("error.Message", page);
        var launcher = Read(@"src\OpenClaw.Tray.WinUI\Services\SetupNativeHandoffLauncher.cs");
        Assert.Contains("(error as SetupNativeCompletionTimeoutException)?.Phase", launcher);
        Assert.DoesNotContain("error.Message", launcher);
    }

    [Fact]
    public void ChooserHasThreeNativeActions_RecommendationAndErrorOnlyRecovery_NoFooterButtons()
    {
        var page = XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiReadyPage.xaml"));
        var actions = page.Descendants().Where(element => element.Name.LocalName == "SettingsCard").ToArray();
        Assert.Equal(["Chat", "Channels", "Skills"], actions.Select(element => element.Attribute("Tag")?.Value));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(x + "Name") is "SkipButton" or "ReturnButton");
        var recovery = Assert.Single(page.Descendants(), element => (string?)element.Attribute(x + "Name") == "RecoveryButton");
        Assert.Contains(recovery.Ancestors(), element => element.Name.LocalName == "InfoBar");
        var badge = Assert.Single(actions[0].Descendants(), element => (string?)element.Attribute(x + "Name") == "RecommendedBadge");
        Assert.Equal("RecommendedBadge", badge.Name.LocalName);
        Assert.Contains(page.Descendants(), element => element.Name.LocalName == "SetupProgressIndicator");
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(x + "Name") == "FinishButton");
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiReadyPage.xaml.cs");
        Assert.Contains("args.Owner.OwnsReadyChoice(args.Coordinator)", source);
        Assert.Contains("args.Coordinator.SelectAsync(destination)", source);
        Assert.DoesNotContain("Launcher.LaunchUri", source);
    }

    [Fact]
    public void NativeConnectionProgress_HasItsOwnRowAboveWrappableActions()
    {
        var page = XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\SetupNativeConnectionPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var progress = Assert.Single(page.Descendants(), item => (string?)item.Attribute(x + "Name") == "FlowProgress");
        Assert.Equal("Auto,Auto", (string?)progress.Parent!.Attribute("RowDefinitions"));
        foreach (var button in progress.Parent.Elements().Where(item => item.Name.LocalName == "Button"))
        {
            Assert.Equal("1", (string?)button.Attribute("Grid.Row"));
            Assert.Equal("0", (string?)button.Attribute("MinWidth"));
            Assert.Equal("{StaticResource WrappedFooterAction}", (string?)button.Attribute("ContentTemplate"));
        }
    }

    [Fact]
    public void CompletionActivation_HasNoSupersededBrowserFallback()
    {
        Assert.Contains("OpenNativeSetupCompletion(r.Handle ?? \"invalid\")",
            Read(@"src\OpenClaw.Tray.WinUI\App.ActivationRouter.cs"));
        var app = Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs");
        Assert.DoesNotContain("handoffHandle", app);
        Assert.Contains("launchTarget = store.Issue(nativeCompletion)", app);
        var dashboard = Read(@"src\OpenClaw.Tray.WinUI\Services\GatewayDashboardLauncher.cs");
        Assert.DoesNotContain("GatewayAiSetupCompletion", dashboard);
        Assert.DoesNotContain("OpenPendingAsync", dashboard);
        Assert.DoesNotContain("Issue(GatewayAiSetupCompletion", Read(@"src\OpenClaw.Tray.WinUI\Services\SetupDashboardHandoffStore.cs"));
    }

    [Fact]
    public void NativeChatHandoff_UsesWorkspaceBindingWithoutReopeningCompanionChat()
    {
        // retirement_condition: replace with mounted native handoff tests when authorized UI proof is available.
        var manager = Read(@"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs");
        var handoff = manager[manager.IndexOf("public async Task ShowNativeSetupAsync", StringComparison.Ordinal)..];
        Assert.True(handoff.IndexOf("if (_isShuttingDown)", StringComparison.Ordinal) <
            handoff.IndexOf("ShowWorkspace(destination", StringComparison.Ordinal));
        Assert.Contains("if (request.WorkspaceDestination is { } destination)", manager);
        Assert.Contains("ShowWorkspace(destination, activate: false, preserveCurrent: false, nativeRequest: request)", manager);
        Assert.Contains("await workspace.WaitForNativeSetupAsync(request, ct)", manager);
        Assert.Contains("ShowCompanion(request.PageTag, activate: false, nativeRequest: request)", manager);
        Assert.Contains("await hub.WaitForNativeSetupAsync(request, ct)", manager);
        var workspace = Read(@"src\OpenClaw.Tray.WinUI\Windows\WorkspaceWindow.xaml.cs");
        var binding = workspace[workspace.IndexOf("internal void NavigateNativeSetup(SetupNativeNavigationRequest request)", StringComparison.Ordinal)..];
        Assert.True(binding.IndexOf("_chat.BindNativeSetupRequest(request)", StringComparison.Ordinal) <
            binding.IndexOf("_chat.Initialize(this)", StringComparison.Ordinal));
        Assert.Contains("_chat.RetainNativeSetupForDestination(Destination)", workspace);
        Assert.Contains("await _chat.WaitForNativeSetupAsync(request, ct)", workspace);
        Assert.Contains("request.RequireWorkspaceDestination(Destination)", workspace);
        var chat = Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs");
        Assert.Contains("_nativeSetupBinding.RequireCurrent(request, ct)", chat);
        Assert.Contains("_pendingSessionKey = native.Completion.Target.SessionKey", chat);
        var hub = Read(@"src\OpenClaw.Tray.WinUI\Windows\HubWindow.xaml.cs");
        Assert.Contains("if (request.WorkspaceDestination is not null)", hub);
        Assert.DoesNotContain("await chat.WaitForNativeSetupAsync", hub);
        var agentIntent = workspace[workspace.IndexOf("internal async Task StartAgentChatAsync", StringComparison.Ordinal)..];
        Assert.True(agentIntent.IndexOf("_chat.InvalidateNativeSetupForNavigation()", StringComparison.Ordinal) <
            agentIntent.IndexOf("_agentId = agent.Id", StringComparison.Ordinal));
        var createIntent = workspace[workspace.IndexOf("private async Task NewSessionAsync()", StringComparison.Ordinal)..];
        Assert.True(createIntent.IndexOf("_chat.InvalidateNativeSetupForNavigation()", StringComparison.Ordinal) <
            createIntent.IndexOf("await client.CreateSessionAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeRestartWaitsBeforeServicesAndNeverFallsThroughToForwardingOnFailure()
    {
        var app = Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs");
        Assert.Contains("NativeRestartAdmission.Acquire(_postSetupLaunch!", app);
        Assert.Contains("if (nativeRestart) { Exit(); return; }", app);
        var admission = app.IndexOf("NativeRestartAdmission.Acquire(_postSetupLaunch!", StringComparison.Ordinal);
        var forwarding = app.IndexOf("await _activationRouter.ForwardLaunchToPrimaryAsync", admission, StringComparison.Ordinal);
        Assert.True(admission < forwarding);
        Assert.True(forwarding < app.IndexOf("_settings = new SettingsManager();", forwarding, StringComparison.Ordinal));
        Assert.Contains("_postSetupLaunch = _nativeRestartRecovery.Read()", app);
        Assert.DoesNotContain("_nativeRestartRecovery?.Clear(handle)", app);
        Assert.Contains("handle, explicitRetry, restartRecovery: _nativeRestartRecovery", app);
        var launcher = Read(@"src\OpenClaw.Tray.WinUI\Services\SetupNativeHandoffLauncher.cs");
        Assert.Contains("SetupHandoffAcquisitionStatus.Busy", launcher);
        Assert.True(launcher.IndexOf("lease.Consume();", StringComparison.Ordinal) <
            launcher.IndexOf("ClearRestartRecovery();", StringComparison.Ordinal));
    }

    [Fact]
    public void SkillsEntryIsBoundReadOnlyAndWaitedBeforeReceiptConsumption()
    {
        var source = Read(@"src\OpenClaw.Tray.WinUI\Pages\SkillsPage.xaml.cs");
        Assert.Contains("_nativeSetupRequest = e.Parameter as SetupNativeNavigationRequest", source);
        Assert.Contains("SetupNativeSkills.LoadAsync(request, RequireNativeClient, ct)", source);
        Assert.Contains("if (_nativeSetupRequest is not null) return;", source);
        Assert.Contains("CurrentAgentId != request.Completion.Verification.AgentId", source);
        Assert.Contains("AgentFilterCombo.IsEnabled = false", source);
        Assert.DoesNotContain("InstallSkillAsync", source);
        Assert.Contains("await skills.WaitForNativeSetupAsync(request, ct)",
            Read(@"src\OpenClaw.Tray.WinUI\Windows\HubWindow.xaml.cs"));
    }

    [Fact]
    public void IsolatedHostDisablesStartupWithoutWeakeningRegistrationGuard()
    {
        Assert.Contains("startupRegistrationAllowed: !AppIdentity.IsIsolated",
            Read(@"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs"));
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("ShowStartupPreference => _startupRegistrationAllowed &&", window);
        Assert.Contains("get => _startupRegistrationAllowed && _autoStartAfterSetup", window);
        Assert.Contains("enableAutoStart &= _startupRegistrationAllowed", window);
        Assert.Contains("ShowStartupPreference: ShowStartupPreference", window);
        Assert.Contains("if (_startupRegistrationAllowed && _persistStartupPreferenceOnComplete)", window);
        var app = Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs");
        Assert.Contains("AutoStartManager.ApplySetupPreferenceAsync", app);
        Assert.Contains("AutoStartSettingsApplier.ApplyExplicitAsync", app);
        Assert.Contains("e.ApplyStartupPreference ? e.EnableAutoStart : null", app);
        Assert.DoesNotContain("if (enabled) await AutoStartManager.SetAutoStartAsync(true)", app);
    }

    [Fact]
    public void HostedSetupUsesSettingsOwnerAndClassicStartupFailureIsSeparateFromRestartFailure()
    {
        var window = Read(@"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs");
        Assert.Contains("new SetupSettingsWriter", window);
        Assert.Contains("persistChoices:", window);
        var setup = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("_persistChoices(_config.Settings", setup);
        var app = Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs");
        var start = app.IndexOf("private async Task RestartAfterSetupAsync", StringComparison.Ordinal);
        var end = app.IndexOf("private async Task ShowSetupRestartErrorAsync", start, StringComparison.Ordinal);
        var restart = app[start..end];
        Assert.True(restart.IndexOf("SetupStartupPolicy.ApplyClassicPreferenceAsync", StringComparison.Ordinal) <
            restart.IndexOf("Process.Start(psi)", StringComparison.Ordinal));
        Assert.Contains("Onboarding_StartupWarning_Message", restart);
    }

    [Fact]
    public void NativeChannelFocusUsesBoundFreshMetadataAndNeverStartsAuthentication()
    {
        var source = Read(@"src\OpenClaw.Tray.WinUI\Pages\ChannelsPage.xaml.cs");
        var focus = source[source.IndexOf("private void ApplyNativeChannelFocus", StringComparison.Ordinal)..
            source.IndexOf("private Expander BuildExpander", StringComparison.Ordinal)];
        Assert.Contains("ReferenceEquals(snapshot, _nativeSnapshot)", focus);
        Assert.Contains("SetupChannelFocusPolicy.GetAvailability", focus);
        Assert.Contains("row.IsExpanded = true", focus);
        Assert.Contains("row.Loaded += loaded", focus);
        Assert.DoesNotContain("StartLinkingAsync", focus);
        Assert.DoesNotContain("StartChannelAsync", focus);
        Assert.DoesNotContain("SaveAsync", focus);
        Assert.Contains("useBuiltInFallback: _nativeSetupRequest is null", source);
        Assert.Contains("!ReferenceEquals(client, BoundClient)", source);
    }
}
