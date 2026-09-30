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
    public void Welcome_DisabledNativeKeepsRecommendationAndSeparateSupportCard()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(
            element => (string?)element.Attribute(names + "Name") == name);
        var native = Named("NativeChoice");
        var badge = Named("NativeRecommendedBadge");
        Assert.Equal("False", (string?)native.Attribute("IsEnabled"));
        Assert.Contains(native, badge.Ancestors());
        Assert.Null(badge.Attribute("Visibility"));
        Assert.Equal("{ThemeResource TextFillColorDisabledBrush}", (string?)badge.Attribute("BorderBrush"));
        foreach (var name in new[] { "NativeTitle", "NativeDescription", "NativeRecommendedText" })
            Assert.Equal("{ThemeResource TextFillColorDisabledBrush}", (string?)Named(name).Attribute("Foreground"));
        Assert.Equal("{ThemeResource AccentFillColorDisabledBrush}", (string?)Named("NativeIconBackground").Attribute("Background"));
        Assert.Equal("{ThemeResource TextOnAccentFillColorDisabledBrush}", (string?)Named("NativeIcon").Attribute("Foreground"));
        var enabledSetters = Named("NativeEnabled").Descendants().Where(element => element.Name.LocalName == "Setter").ToArray();
        Assert.Equal(6, enabledSetters.Length);
        Assert.Contains(enabledSetters, setter =>
            (string?)setter.Attribute("Target") == "NativeRecommendedText.Foreground" &&
            (string?)setter.Attribute("Value") == "{ThemeResource AccentTextFillColorPrimaryBrush}");
        var card = Named("NativeSupportCard");
        var selector = Named("GatewayChoiceSelector");
        Assert.Contains(card, selector.ElementsAfterSelf());
        Assert.Equal("{ThemeResource CardBackgroundFillColorDefaultBrush}", (string?)card.Attribute("Background"));
        Assert.Equal("1", (string?)card.Attribute("BorderThickness"));
        Assert.Contains(card, Named("NativeSupportStatus").Ancestors());
        Assert.Contains(card, Named("WindowsUpdateButton").Ancestors());
        Assert.DoesNotContain(native, card.Ancestors());
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.DoesNotContain("NativeRecommendedBadge.Visibility", source);
        Assert.Contains("NativeChoice.IsEnabled = available", source);
        Assert.Contains("VisualStateManager.GoToState(this, \"NativeDisabled\", false)", source);
        Assert.Contains("VisualStateManager.GoToState(this, available ? \"NativeEnabled\" : \"NativeDisabled\", false)", source);
        Assert.Contains("NativeSupportCard.Visibility = available ? Visibility.Collapsed : Visibility.Visible", source);
        Assert.Contains("\", \" + SetupLocalization.GetString(\"Onboarding_Native_Recommended.Text\")", source);
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
