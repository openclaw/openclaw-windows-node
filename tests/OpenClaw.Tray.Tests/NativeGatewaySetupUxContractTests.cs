using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class NativeGatewaySetupUxContractTests
{
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
        Assert.Contains("SetStatus(StepStatus.Failed)", failure);
        Assert.Contains("SetupLogger.Sanitize(ex.Message)", failure);
        Assert.Contains("RetryButton.Visibility = Visibility.Visible", failure);
    }

    [Fact]
    public void NativeSetup_IsDistinctFromWslAndRechecksCapabilityWithoutIsolationWarning()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pages = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages");
        var welcome = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("NavigateToNativeCapabilities()", welcome);
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
        Assert.Contains("new StepRow(", source);
        Assert.Contains("StepStatus.Done", source);
        Assert.Contains("StepStatus.Failed", source);
        Assert.Contains("NativeGatewaySetupService", source);
        Assert.Contains("Windows.System.Launcher.LaunchUriAsync(uri)", source);
        Assert.DoesNotContain("LaunchFileAsync", source);
        Assert.DoesNotContain("Architecture.Arm64", source);
        Assert.DoesNotContain("StorageFile", source);
        Assert.Contains("Onboarding_Native_InstallerOpened", source);
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
            (string?)element.Attribute(names + "Name") == "NativeRecommendedBadge");
        Assert.Empty(document.Descendants(xaml + "Expander"));
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Content") == "Check again");
        var wsl = primary.Elements(xaml + "ListViewItem").ElementAt(1);
        Assert.Null(wsl.Attribute("Visibility"));
        Assert.Null(wsl.Attribute("IsEnabled"));
        Assert.DoesNotContain(wsl.Descendants(), element =>
            (string?)element.Attribute("Text") == "Recommended");
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("window.GetNativeGatewayEligibilityAsync()", source);
        Assert.Contains("NativeGatewaySetupEligibility.ResolveSelection", source);
        Assert.Contains("generation != _probeGeneration", source);
        Assert.Contains("NativeGatewayEligibility.CapabilityUnavailable", source);
        Assert.Contains("ms-settings:windowsupdate", source);
        Assert.Contains("ShowWindowsUpdateError()", source);
        Assert.Contains("NativeGatewayEligibility.Available", source);
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
    public void Welcome_GatewayLabelsMatchEnglishResourcesAndAccessibleNames()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pages = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Descendants("data").ToDictionary(
                element => (string)element.Attribute("name")!, element => element.Element("value")?.Value);
        foreach (var (choiceName, uid, expected) in new[]
        {
            ("NativeChoice", "Onboarding_Native_Title", "Install a local native gateway"),
            ("InstallChoice", "Onboarding_Wsl_Title", "Install a local WSL gateway"),
        })
        {
            var choice = document.Descendants().Single(element => (string?)element.Attribute(names + "Name") == choiceName);
            var title = choice.Descendants().Single(element => (string?)element.Attribute(names + "Uid") == uid);
            Assert.Equal(expected, (string?)title.Attribute("Text"));
            Assert.Equal(expected, (string?)choice.Attribute("AutomationProperties.Name"));
            Assert.Equal(expected, resources[uid + ".Text"]);
        }
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
        var heading = document.Descendants(xaml + "Grid")
            .Single(element => (string?)element.Attribute(names + "Name") == "NativeHeading");
        Assert.Equal("*,Auto", (string?)heading.Attribute("ColumnDefinitions"));
        Assert.Contains(heading.Elements(), element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Title");
        var badge = heading.Elements().Single(element =>
            (string?)element.Attribute(names + "Name") == "NativeRecommendedBadge");
        Assert.Equal("1", (string?)badge.Attribute("Grid.Column"));
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
        Assert.Contains("preserveStartupPreference || _nativeSetupCompleted", window);
        Assert.Contains("await wizardPage.CancelAndWaitAsync()", window);
        Assert.Contains("await ReleaseNativeSetupAsync()", window);
        Assert.Contains("await native.PrepareWizardAsync(native.LifetimeToken)", wizard);
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
        var nativeStart = source.IndexOf("if (_nativeGateway)", StringComparison.Ordinal);
        var nativeEnd = source.IndexOf("TailscaleToggle.IsOn =", nativeStart, StringComparison.Ordinal);
        var native = source[nativeStart..nativeEnd];
        Assert.Contains("WslReviewContent.Visibility = Visibility.Collapsed", native);
        Assert.Contains("NativeReviewContent.Visibility = Visibility.Visible", native);
        Assert.Contains("return;", native);
        Assert.True(source.IndexOf("_permissionsTask = BuildPermissionRows()", StringComparison.Ordinal) < nativeStart);
        Assert.True(nativeEnd < source.IndexOf("() => InitializeLocalAiReviewAsync(", StringComparison.Ordinal));
        Assert.Contains("if (_nativeGateway)\n                    SetupWindow.Active?.NavigateToNativeGatewaySetup();",
            source.Replace("\r\n", "\n"));
        var writeStart = source.IndexOf("private void WriteCapabilities()", StringComparison.Ordinal);
        var writeEnd = source.IndexOf("private void ApplySetupReviewSummary", writeStart, StringComparison.Ordinal);
        var write = source[writeStart..writeEnd];
        Assert.Contains("config.Settings.ApplyCapabilities(caps)", write);
        Assert.Contains("config.Settings.EnableNodeMode = true", write);
        Assert.True(write.IndexOf("return;", StringComparison.Ordinal) < write.IndexOf("config.Tailscale.Enabled", StringComparison.Ordinal));
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
        Assert.Contains("MergeCapabilitiesIntoSettingsFile(Path.Combine(_dataDir, \"settings.json\"))", window);
        Assert.Contains("new CapabilitiesPageArgs(_config, false, false, NativeGateway: true)", window);
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
