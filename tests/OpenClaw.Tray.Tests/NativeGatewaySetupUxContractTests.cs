using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class NativeGatewaySetupUxContractTests
{
    [Fact]
    public void ConsoleFailureRecovery_IsIndependentOfWizardErrorAndSurvivesNormalStepClears()
    {
        // Retire when the WinUI wizard exposes a mounted log-failure interaction fixture.
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WizardPage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var recovery = document.Descendants().Single(e => (string?)e.Attribute(names + "Name") == "ConsoleRecovery");
        Assert.Contains(recovery.Descendants(), e =>
            (string?)e.Attribute("Click") == "OpenGatewayTerminal_Click");
        Assert.DoesNotContain(recovery.Ancestors(), e =>
            (string?)e.Attribute(names + "Name") is "GatewayRecovery" or "ConsoleBanner");
        var source = File.ReadAllText(Path.Combine(pages, "WizardPage.xaml.cs"));
        var clear = source[source.IndexOf("private void ClearConsoleBanner()", StringComparison.Ordinal)..
            source.IndexOf("private static FrameworkElement BuildLinkLine", StringComparison.Ordinal)];
        Assert.DoesNotContain("ConsoleRecovery", clear);
        Assert.DoesNotContain("ConsoleIssueText", clear);
        var tail = source[source.IndexOf("private async Task<WizardConsoleTail> StartConsoleTailAsync", StringComparison.Ordinal)..
            source.IndexOf("private void StopConsoleTail()", StringComparison.Ordinal)];
        Assert.Contains("ShowConsoleIssue(issue)", tail);
        Assert.Contains("ShowConsoleIssue(GatewayLogTailIssue.Unavailable)", tail);
        Assert.Contains("if (ReferenceEquals(_consoleTail, tail))", tail);
        Assert.Contains("ConsoleRecovery.Visibility = Visibility.Visible", tail);
    }

    [Fact]
    public void NativeReview_ExplainsWinGetConsentAndMatchesLocalizedResources()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root,
            "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var review = document.Descendants().Single(
            element => (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Review");
        var strings = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings");
        foreach (var path in Directory.GetFiles(strings, "Resources.resw", SearchOption.AllDirectories))
        {
            var resources = XDocument.Load(path).Descendants("data")
                .ToDictionary(element => (string)element.Attribute("name")!,
                    element => element.Element("value")?.Value);
            Assert.Contains("WinGet", resources["Onboarding_Native_Review.Text"]);
            Assert.Contains("Microsoft Store", resources["Onboarding_Native_Review.Text"]);
            Assert.Contains("WinGet", resources["Onboarding_Native_Acquisition.Text"]);
            Assert.Contains("WinGet", resources["Onboarding_Native_InstallingPackage"]);
            Assert.Contains("WinGet", resources["Onboarding_Native_VerifyingPackage"]);
            Assert.Contains("WinGet", resources["Onboarding_Native_Cancelled"]);
            Assert.False(resources.ContainsKey("Onboarding_Native_InstallerOpened"));
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "en-us")
            {
                Assert.Equal(resources["Onboarding_Native_Review.Text"], (string?)review.Attribute("Text"));
                Assert.Contains("accept the package and Store source agreements", resources["Onboarding_Native_Review.Text"]);
                Assert.DoesNotContain("stay interactive", resources["Onboarding_Native_Review.Text"]);
            }
        }
    }

    [Fact]
    public void HttpSurfaces_UseSharedNativeAwareAuthorizerInsteadOfWslOnlyGate()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var tray = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var app = File.ReadAllText(Path.Combine(tray, "App.xaml.cs"));
        Assert.Contains("new InteractiveGatewayEndpointAuthorizer(", app);
        Assert.Contains("nativeGatewayRuntime, managedLocalPortProvenance.IsStrongCredentialAllowed, appLogger", app);
        foreach (var path in new[] { "App.xaml.cs", Path.Combine("Pages", "ChatPage.xaml.cs"),
                     Path.Combine("Pages", "ConnectionPage.xaml.cs") })
        {
            var source = File.ReadAllText(Path.Combine(tray, path));
            Assert.Contains("InteractiveEndpointAuthorizer", source);
            Assert.Contains("IsCredentialAllowed(", source);
            Assert.DoesNotContain(".IsStrongCredentialAllowed(", source);
        }
    }

    [Fact]
    public void NativeSetup_RetryRechecksDraftPortAndRendersAggregateLaunchFailure()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Pages", "NativeGatewaySetupPage.xaml.cs"));
        Assert.Contains("window.NativeSetupDraft = await service.CreateDraftAsync(cancellationToken)", source);
        Assert.DoesNotContain("window.NativeSetupDraft ??=", source);
        var catchStart = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var finallyStart = source.IndexOf("finally", catchStart, StringComparison.Ordinal);
        var failure = source[catchStart..finallyStart];
        Assert.Contains("or AggregateException", failure);
        Assert.Contains("Trace.TraceError", failure);
        Assert.Contains("Apply(SetupInstallationStatus.Failed)", failure);
        Assert.Contains("SetupLogger.Sanitize(ex.Message)", failure);
        Assert.Contains("RetryButton.Visibility = Visibility.Visible", failure);
    }

    [Fact]
    public void NativeSetup_IsDistinctFromWslAndRechecksCapabilityWithoutIsolationWarning()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pages = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages");
        var welcome = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("SelectGatewayRoute(SetupGatewayRoute.Native)", welcome);
        Assert.Contains("NavigateToCapabilities()", welcome);
        var xaml = File.ReadAllText(Path.Combine(pages, "NativeGatewaySetupPage.xaml"));
        Assert.DoesNotContain("Not isolated", xaml);
        Assert.DoesNotContain("ConsentCheck", xaml);
        Assert.Contains("StepsPanel", xaml);
        Assert.DoesNotContain("InstallButton", xaml);
        Assert.DoesNotContain("CheckButton", xaml);
        Assert.DoesNotContain("SetupButton", xaml);
        var source = File.ReadAllText(Path.Combine(pages, "NativeGatewaySetupPage.xaml.cs"));
        Assert.DoesNotContain("ConsentCheck", source);
        Assert.Contains("await window.GetNativeGatewayEligibilityAsync()", source);
        Assert.Contains("eligibility != NativeGatewayEligibility.Available", source);
        Assert.Contains("Loaded += (_, _) => StartOperation()", source);
        Assert.Contains("await NativeGatewayPackageAcquisition.EnsureAsync(", source);
        Assert.Contains("new SetupPhaseStatus()", source);
        Assert.Contains("SetupInstallationStatus.Complete", source);
        Assert.Contains("SetupInstallationStatus.Failed", source);
        Assert.Contains("NativeGatewaySetupService", source);
        Assert.Contains("_installer.InstallAsync(new CommandRunner(logger), cancellationToken)", source);
        Assert.DoesNotContain("LaunchUriAsync", source);
        Assert.DoesNotContain("LaunchFileAsync", source);
        Assert.DoesNotContain("Architecture.Arm64", source);
        Assert.DoesNotContain("StorageFile", source);
        Assert.Contains("Onboarding_Native_InstallingPackage", source);
        Assert.Contains("Onboarding_Native_VerifyingPackage", source);
        Assert.Contains("NavigateToNativeWizard(session)", source);
        Assert.Contains("new NativeGatewaySetupHost(ReportProgress, ReportStage)", source);
        Assert.Contains("progressDispatcher.TryEnqueue", source);
        Assert.Contains("if (IsLoaded && !cancellationToken.IsCancellationRequested)", source);
        Assert.Contains("Unloaded += (_, _) => _operationCts?.Cancel()", source);
        Assert.True(source.IndexOf("if (SetupPreview.IsActive)", StringComparison.Ordinal) <
                    source.IndexOf("_operation = RunOperationAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("message => StatusText.Text = message", source);
        Assert.DoesNotContain("BuildDefaultSteps", source);
        Assert.DoesNotContain("CleanBeforeRun", source);
        Assert.DoesNotContain("MxcAvailability", source);
    }

    [Fact]
    public void Welcome_RecommendsProbedNativeFirstWithWslAlwaysVisibleSecond()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var primary = Assert.Single(document.Descendants(xaml + "ListView"));
        Assert.Equal("GatewayChoiceSelector", (string?)primary.Attribute(names + "Name"));
        Assert.Equal("Single", (string?)primary.Attribute("SelectionMode"));
        Assert.Equal(new[] { "NativeChoice", "InstallChoice", "ConnectChoice" },
            primary.Elements(xaml + "ListViewItem").Select(element => (string?)element.Attribute(names + "Name")));
        var native = primary.Elements().First();
        Assert.Equal("False", (string?)native.Attribute("IsEnabled"));
        Assert.Contains(native.Descendants(), element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Recommended");
        Assert.Empty(document.Descendants(xaml + "Expander"));
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Content") == "Check again");
        var wsl = primary.Elements(xaml + "ListViewItem").ElementAt(1);
        Assert.Null(wsl.Attribute("Visibility"));
        Assert.Null(wsl.Attribute("IsEnabled"));
        Assert.Contains(wsl.Descendants(), element =>
            (string?)element.Attribute(names + "Name") == "WslRecommendedBadge");
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("window.GetNativeGatewayEligibilityAsync()", source);
        Assert.Contains("NativeGatewaySetupEligibility.ResolveSelection", source);
        Assert.Contains("generation != _probeGeneration", source);
        Assert.Contains("NativeGatewayEligibility.CapabilityUnavailable", source);
        Assert.Contains("ms-settings:windowsupdate", source);
        Assert.Contains("ShowWindowsUpdateError()", source);
        Assert.Contains("NativeGatewayEligibility.Available", source);
        Assert.Contains("WslRecommendedBadge.Visibility = available ? Visibility.Collapsed : Visibility.Visible", source);
        Assert.Contains("GatewaySetupChoice.Wsl => InstallChoice", source);
        Assert.Contains("ReferenceEquals(GatewayChoiceSelector.SelectedItem, InstallChoice)", source);
        Assert.Contains("_selectedChoice is GatewaySetupChoice.Existing or GatewaySetupChoice.Wsl", source);
        Assert.Contains("else if (_selectedChoice == GatewaySetupChoice.Wsl)", source);
        Assert.Contains("nameof(DetectLocalAiAvailabilityAsync)", source);
        Assert.DoesNotContain("InstallChoice.Visibility", source);
        Assert.DoesNotContain("AlternativeOptions", source);
        Assert.DoesNotContain("WslChoiceSelector", source);
        Assert.DoesNotContain("ShowAlternatives", source);
        Assert.DoesNotContain("NativeRecheck", source);
    }

    [Fact]
    public void Welcome_DisabledNativeKeepsRecommendationAndSeparateSupportCard()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(
            element => (string?)element.Attribute(names + "Name") == name);
        var native = Named("NativeChoice");
        Assert.Equal("False", (string?)native.Attribute("IsEnabled"));
        Assert.Contains(native.Descendants(), element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Recommended");
        var card = Named("NativeSupportCard");
        var selector = Named("GatewayChoiceSelector");
        Assert.Contains(card, selector.ElementsAfterSelf());
        Assert.Equal("{ThemeResource CardBackgroundFillColorDefaultBrush}", (string?)card.Attribute("Background"));
        Assert.Equal("1", (string?)card.Attribute("BorderThickness"));
        Assert.Contains(card, Named("NativeSupportStatus").Ancestors());
        Assert.Contains(card, Named("WindowsUpdateButton").Ancestors());
        Assert.DoesNotContain(native, card.Ancestors());
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("NativeChoice.IsEnabled = available", source);
        Assert.Contains("WslRecommendedBadge.Visibility = available ? Visibility.Collapsed : Visibility.Visible", source);
        Assert.Contains("NativeSupportCard.Visibility = available ? Visibility.Collapsed : Visibility.Visible", source);
        Assert.Contains("\", \" + SetupLocalization.GetString(\"Onboarding_Native_Recommended.Text\")", source);
    }

    [Fact]
    public void Welcome_GatewayLabelsMatchEnglishResourcesAndAccessibleNames()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pages = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Descendants("data").ToDictionary(
                element => (string)element.Attribute("name")!, element => element.Element("value")?.Value);
        var choice = document.Descendants().Single(element => (string?)element.Attribute(names + "Name") == "NativeChoice");
        var title = choice.Descendants().Single(element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Title");
        Assert.Equal("Install a local native gateway", (string?)title.Attribute("Text"));
        Assert.Equal("Install a local native gateway", resources["Onboarding_Native_Title.Text"]);
        var wsl = document.Descendants().Single(element => (string?)element.Attribute(names + "Name") == "InstallChoice");
        Assert.Equal("Install a local Gateway (WSL), recommended", (string?)wsl.Attribute("AutomationProperties.Name"));
        Assert.Equal("Install a local Gateway (WSL)", resources["Onboarding_Copy_GatewayLocalTitle.Text"]);
        Assert.Equal("Install a local, MXC contained OpenClaw gateway", resources["Onboarding_Native_Description.Text"]);
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("AutomationProperties.SetName(InstallChoice, SetupLocalization.GetString(\"Onboarding_Wsl_Title.Text\"))", source);
    }

    [Fact]
    public void Welcome_NativeBadgeSharesHeadingAndSuccessFollowsDescription()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var native = document.Descendants(xaml + "ListViewItem")
            .Single(element => (string?)element.Attribute(names + "Name") == "NativeChoice");
        var heading = native.Descendants(xaml + "StackPanel")
            .Single(element => (string?)element.Attribute("Orientation") == "Horizontal" &&
                element.Elements().Any(child => (string?)child.Attribute(names + "Uid") == "Onboarding_Native_Title"));
        Assert.Contains(heading.Elements(), element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Title");
        Assert.Contains(heading.Descendants(), element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Recommended");
        var description = heading.ElementsAfterSelf().First();
        Assert.Equal("Onboarding_Native_Description", (string?)description.Attribute(names + "Uid"));
        Assert.Equal("Install a local, MXC contained OpenClaw gateway", (string?)description.Attribute("Text"));
        var success = description.ElementsAfterSelf().First();
        Assert.Equal("NativeSupportAvailablePanel", (string?)success.Attribute(names + "Name"));
        Assert.Equal("Collapsed", (string?)success.Attribute("Visibility"));
        var checkmark = Assert.Single(success.Elements(xaml + "FontIcon"));
        Assert.Equal("\uE73E", (string?)checkmark.Attribute("Glyph"));
        Assert.Equal("Raw", (string?)checkmark.Attribute("AutomationProperties.AccessibilityView"));
        var text = Assert.Single(success.Elements(xaml + "TextBlock"));
        Assert.Equal("Polite", (string?)text.Attribute("AutomationProperties.LiveSetting"));
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("NativeSupportAvailablePanel.Visibility = Visibility.Collapsed", source);
        Assert.Contains("NativeSupportAvailablePanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed", source);
        Assert.Contains("NativeSupportStatusPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible", source);
        Assert.Contains("available ? NativeSupportAvailableText : NativeSupportStatus", source);
        Assert.Contains("CreatePeerForElement(supportText)", source);
    }

    [Fact]
    public void NativeWizard_UsesSharedPageAndRpc_WithFailClosedStagedAuthorization()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var wizard = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var host = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "NativeGatewaySetupHost.cs"));
        Assert.Contains("NativeSetupSession = session;", window);
        Assert.Contains("NavigateTo(typeof(WizardPage), _config)", window);
        Assert.Contains("ApplyStartupPreference: _startupRegistrationAllowed && _persistStartupPreferenceOnComplete", window);
        Assert.Contains("WizardPage wizardPage => wizardPage.CancelAndWaitAsync()", window);
        Assert.Contains("await nativeCleanup", window);
        Assert.Contains("await ReleaseNativeSetupAsync()", window);
        Assert.Contains("await native.PrepareWizardAsync(native.LifetimeToken)", wizard);
        Assert.Contains("rejectNativeGateway: true", wizard);
        var gatewaySession = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "SetupGatewaySession.cs"));
        Assert.Contains("if (rejectNativeGateway && record.NativePackageFamilyName is not null)", gatewaySession);
        Assert.Contains("await native.AuthorizeAsync(cancellationToken)", wizard);
        Assert.Contains("reportNativePairing: true", wizard);
        Assert.Contains("NativeGatewaySetupSession.GetPairingGuidance(requestId)", wizard);
        Assert.Contains("await native.ApproveWizardPairingAsync(ex.RequestId, native.LifetimeToken)", wizard);
        Assert.Contains("when (attempt == 0)", wizard);
        var nativeConnection = wizard[
            wizard.IndexOf("private async Task<OpenClawGatewayClient> ConnectNativeClientAsync", StringComparison.Ordinal)..
            wizard.IndexOf("private sealed class NativePairingRequiredException", StringComparison.Ordinal)];
        Assert.True(nativeConnection.IndexOf("for (var attempt", StringComparison.Ordinal) <
                    nativeConnection.IndexOf("new OpenClawGatewayClient", StringComparison.Ordinal));
        Assert.Contains("client.Dispose();", nativeConnection);
        Assert.Contains("client.HandshakeAuthorizationAsync = client.ReconnectAuthorizationAsync;", nativeConnection);
        Assert.True(nativeConnection.IndexOf("client.HandshakeAuthorizationAsync =", StringComparison.Ordinal) <
                    nativeConnection.IndexOf("await WaitForConnectAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("client.DisconnectAsync()", nativeConnection);
        Assert.Contains("[\"devices\", \"list\", \"--json\"]", host);
        Assert.Contains("[\"devices\", \"approve\", requestId, \"--json\"]", host);
        Assert.Contains("PairingCommandTimeout = TimeSpan.FromMinutes(2)", host);
        Assert.Equal(2, host.Split("environment, PairingCommandTimeout,").Length - 1);
        Assert.Contains("WizardPayloadHelpers.GetNativeTerminalError(payload)", wizard);
        Assert.Contains("ShowError(SetupLogger.Sanitize(nativeError))", wizard);
        Assert.DoesNotContain("--url", host);
        Assert.DoesNotContain("--latest", host);
        Assert.Contains("new { mode = \"local\", installDaemon = false }", wizard);
        Assert.Contains("SendWizardRequestAsync(\"wizard.start\"", wizard);
        Assert.Contains("SendWizardRequestAsync(\"wizard.next\"", wizard);
        Assert.Contains("SendWizardRequestAsync(\"wizard.cancel\"", wizard);
        Assert.Contains("_nativeSession?.MarkWizardCompleted()", wizard);
        Assert.Contains("await native.CompleteAsync(native.LifetimeToken, _config!.Capabilities)", wizard);
        Assert.Contains("await native.RestartAsync(native.LifetimeToken)", wizard);
        Assert.Contains("nativeLogPath: _nativeSession?.ConsoleLogPath", wizard);
        Assert.DoesNotContain("onboard", host);
        Assert.DoesNotContain("wsl.exe", host);
        Assert.DoesNotContain("RunInWslAsync", host);
        Assert.Contains("[\"setup\"]", host);
        Assert.Contains("[\"config\", \"validate\", \"--json\"]", host);
        Assert.Contains("[\"gateway\", \"health\", \"--json\"]", host);
        Assert.False(File.Exists(Path.Combine(root, "src", "OpenClaw.SetupEngine", "NativeGatewayTerminalCommand.cs")));
    }

    [Fact]
    public void OptionalSetup_UsesOnePolicyAndValidatedHandoffForNativeWslAndHeadless()
    {
        // Retire when WizardPage's RPC loop has an injectable behavioral test seam.
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var wizard = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var runner = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "SetupWizardRunner.cs"));
        foreach (var source in new[] { wizard, runner })
        {
            Assert.Contains("WizardOnboardingPolicy.Evaluate(", source);
            Assert.Contains("WizardOptionalSetupHandoff.CompleteAsync(", source);
            Assert.DoesNotContain("\"Optional apps\"", source);
            Assert.DoesNotContain("\"Search provider\"", source);
        }

        var handoff = wizard[
            wizard.IndexOf("if (onboarding.Action == WizardOnboardingAction.Finish)", StringComparison.Ordinal)..
            wizard.IndexOf("object next = onboarding.Action", StringComparison.Ordinal)];
        Assert.Contains("_nativeSession?.MarkOptionalSetupDeferred()", handoff);
        Assert.Contains("await CompleteSetupAsync(generation)", handoff);
        Assert.DoesNotContain("MarkWizardCompleted", handoff);
        Assert.True(handoff.IndexOf("WizardOptionalSetupHandoff.CompleteAsync", StringComparison.Ordinal) <
                    handoff.IndexOf("MarkOptionalSetupDeferred", StringComparison.Ordinal));
        Assert.Contains("SetupWizardRunner.IsInstallDaemonParameterUnsupported(ex)", wizard);
    }

    [Fact]
    public void NativeCompletion_DoesNotClaimWslRunningOrDevicePaired()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var complete = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));
        var start = complete.IndexOf("if (args.NativeGatewayUrl", StringComparison.Ordinal);
        var native = complete[start..complete.IndexOf("\n            else", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("SummaryPanel.Visibility = Visibility.Collapsed", native);
        Assert.Contains("DevicePairedSummaryCard.Visibility = Visibility.Collapsed", native);
        Assert.Contains("NativeFeaturesNote.Visibility = Visibility.Visible", native);
        Assert.Contains("Onboarding_Native_ConfiguredTitle", native);
        Assert.Contains("args.NativeCapabilitySummary", native);
        Assert.Contains("NodeModeBanner.Visibility = Visibility.Collapsed", native);
        Assert.Contains("Onboarding_Native_Configured", native);
    }

    [Fact]
    public void NativeCapabilities_ReusePermissionsButDoNotProbeOrInstallWslAddons()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var policy = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "OnboardingFlowPolicy.cs"));
        Assert.Contains("SetupWindow.Active?.AccessDraft", source);
        Assert.Contains("_draft.SetCapability(capability, toggle.IsOn)", source);
        Assert.DoesNotContain("TailscaleToggle", source);
        Assert.Contains("SetupGatewayRoute.ManagedWsl or SetupGatewayRoute.Native", source);
        Assert.Contains("OnboardingAccessDestination.NativeGatewaySetup", window);
        Assert.Contains("NavigateToNativeGatewaySetup()", window);
        Assert.Contains("SetupGatewayRoute.Native => OnboardingAccessDestination.NativeGatewaySetup", policy);
    }

    [Fact]
    public void NativeFinalization_SavesCapabilityChoicesBeforeReleasingSessionAndShowingSuccess()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var wizard = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var completion = wizard[wizard.IndexOf("private async Task CompleteSetupAsync", StringComparison.Ordinal)..];
        Assert.True(completion.IndexOf("_nativeFinalizationInProgress = true", StringComparison.Ordinal) <
                    completion.IndexOf("await native.CompleteAsync", StringComparison.Ordinal));
        Assert.True(completion.IndexOf("HideRecoveryActions()", StringComparison.Ordinal) <
                    completion.IndexOf("await native.CompleteAsync", StringComparison.Ordinal));
        Assert.Contains("finally", completion);
        Assert.Contains("_nativeFinalizationInProgress = false", completion);
        foreach (var method in new[] { "StartOverAsync()", "SkipWizardAsync()" })
        {
            var start = wizard.IndexOf($"private async Task {method}", StringComparison.Ordinal);
            var body = wizard[start..wizard.IndexOf("var generation = AdvanceOperationGeneration()", start, StringComparison.Ordinal)];
            Assert.Contains("if (_nativeFinalizationInProgress)", body);
        }
        Assert.True(completion.IndexOf("await native.CompleteAsync", StringComparison.Ordinal) <
                    completion.IndexOf("setupWindow.SaveNativeCapabilities()", StringComparison.Ordinal));
        Assert.True(completion.IndexOf("setupWindow.SaveNativeCapabilities()", StringComparison.Ordinal) <
                    completion.IndexOf("await setupWindow.ReleaseNativeSetupAsync()", StringComparison.Ordinal));
        Assert.True(completion.IndexOf("await setupWindow.ReleaseNativeSetupAsync()", StringComparison.Ordinal) <
                    completion.IndexOf("setupWindow.NavigateToNativeComplete", StringComparison.Ordinal));
        var window = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        Assert.Contains("_persistStartupPreferenceOnComplete = false;", window);
        Assert.Contains("SaveSetupChoices(AutoStartAfterSetup)", window);
        Assert.Contains("NavigateToCapabilities(back: true)", window);
        var cancelStart = wizard.IndexOf("window.NavigateToNativeCapabilities()", StringComparison.Ordinal);
        Assert.True(cancelStart >= 0);
        Assert.DoesNotContain("window.NavigateToNativeGatewaySetup()", wizard);
    }

    [Fact]
    public void NativeCopy_IsPresentInEverySupportedLocale()
    {
        var strings = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings");
        var expected = XDocument.Load(Path.Combine(strings, "en-us", "Resources.resw")).Descendants("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => name?.StartsWith("Onboarding_Native_", StringComparison.Ordinal) == true ||
                           name == "Onboarding_Wsl_Title.Text")
            .ToArray();
        Assert.NotEmpty(expected);
        foreach (var file in Directory.EnumerateFiles(strings, "Resources.resw", SearchOption.AllDirectories))
        {
            var keys = XDocument.Load(file).Descendants("data").ToDictionary(
                element => (string)element.Attribute("name")!, element => element.Element("value")?.Value);
            foreach (var key in expected)
                Assert.True(keys.TryGetValue(key!, out var text) && !string.IsNullOrWhiteSpace(text), $"{file}: {key}");
        }
    }

    [Fact]
    public void PackageResolver_DoesNotUseUnqualifiedPathAliasOrInstallPackage()
    {
        var path = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI",
            "NativeGatewayPackageResolver.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("FindPackagesForUser(string.Empty)", source);
        Assert.Contains("NativeGatewayPackageIdentity.IsTrusted(package.Id.Name, package.Id.Publisher)", source);
        Assert.Contains("(expectedFamily is null || package.Id.FamilyName == expectedFamily)", source);
        Assert.Contains("ResolveCoreAsync(null, cancellationToken)", source);
        Assert.Contains("ResolveCoreAsync(expectedFamily, cancellationToken)", source);
        Assert.Contains("packages.Length > 1", source);
        Assert.Contains("package.Status.VerifyIsOK()", source);
        Assert.Contains("\"Microsoft\", \"WindowsApps\", family", source);
        Assert.DoesNotContain("AddPackageAsync", source);
        Assert.DoesNotContain("Add-AppxPackage", source);
    }
}
