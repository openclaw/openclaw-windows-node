using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class NativeGatewaySetupUxContractTests
{
    [Fact]
    public void FocusedLocalAiShowsActualPhasesAndFencesLateProgress()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pages = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages");
        var source = File.ReadAllText(Path.Combine(pages, "AiSetupPage.xaml.cs"));
        Assert.Contains("await _localUse!.UseAsync(selected, ct, CreateLocalProgress(ct))", source);
        Assert.Contains("_localObservation.RefreshAsync(CreateLocalProgress(ct))", source);
        Assert.Contains("generation == _generation && scope == _progressScope", source);
        Assert.Contains("if (DispatcherQueue.HasThreadAccess) Apply();", source);
        Assert.Contains("finally { ++_progressScope; }", source);
        Assert.Contains("_submittingAnswer = submittingAnswer;", source);
        Assert.Contains("_submittingAnswer = false;", source);
        Assert.Contains("GatewayAiSetupPresentation.ShowProviderDialog", source);
        Assert.Contains("ProviderActivity.Visibility = Visible(inlineProvider);", source);
        Assert.Contains("if (_localObservation is not null && _localExpectedModel is null)", source);
        Assert.Contains("SetActivity(\"LocalProgress_Verifying\")", source);
        Assert.Contains("SetActivity(\"LocalProgress_Detecting\")", source);
        var document = XDocument.Load(Path.Combine(pages, "AiSetupPage.xaml"));
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "OnboardingAiProgress");
        foreach (var directory in Directory.GetDirectories(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings")))
        {
            var resources = XDocument.Load(Path.Combine(directory, "Resources.resw"));
            foreach (var stage in new[] { "CheckingHardware", "CheckingFiles", "PreparingGateway",
                         "StartingRuntime", "PublishingProvider", "Verifying", "Detecting", "Console" })
                Assert.Contains(resources.Descendants("data"), element =>
                    (string?)element.Attribute("name") == "Onboarding_AiSetup_LocalProgress_" + stage);
        }
    }

    [Theory]
    [InlineData("NativeGatewaySetupPage.xaml", "ProgressMascot")]
    [InlineData("WizardPage.xaml", "MascotHero")]
    public void NativePackagePages_UseSharedHeroAndSeparateProgressFromWrappingActions(string file, string hero)
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, file));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(
            element => (string?)element.Attribute(names + "Name") == name);
        Assert.Equal("OnboardingMascot", Named(hero).Name.LocalName);
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "Image");
        var footer = Named("NavigationFooter");
        Assert.Equal("Auto,Auto", (string?)footer.Attribute("RowDefinitions"));
        Assert.Same(footer, Named("FlowProgress").Parent);
        foreach (var action in footer.Elements().Where(element =>
                     element.Name.LocalName is "Button" or "DropDownButton"))
        {
            Assert.Equal("1", (string?)action.Attribute("Grid.Row"));
            Assert.Equal(file == "NativeGatewaySetupPage.xaml" ? "100" : "0", (string?)action.Attribute("MinWidth"));
            if (file == "NativeGatewaySetupPage.xaml")
                Assert.Equal((string?)action.Attribute(names + "Name") == "BackButton" ? "Left" : "Right",
                    (string?)action.Attribute("HorizontalAlignment"));
            Assert.Equal("{StaticResource WrappedFooterAction}", (string?)action.Attribute("ContentTemplate"));
        }
        var source = File.ReadAllText(Path.Combine(pages, "NativeGatewaySetupPage.xaml.cs"));
        Assert.Contains("new SettingsCard", source);
        Assert.DoesNotContain("Width = 280", source);
        Assert.Contains("Content = status", source);
    }

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
        Assert.DoesNotContain("Onboarding_Native_InstallerOpened", source);
        Assert.Contains("NavigateToNativeAiSetup(session)", source);
        Assert.DoesNotContain("NavigateToNativeWizard", source);
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
    public void Welcome_RecommendsProbedNativeWhileKeepingWslAlwaysVisible()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var primary = Assert.Single(document.Descendants(xaml + "ListView"));
        Assert.Equal("GatewayChoiceSelector", (string?)primary.Attribute(names + "Name"));
        Assert.Equal("Single", (string?)primary.Attribute("SelectionMode"));
        Assert.Equal(new[] { "InstallChoice", "ConnectChoice", "NativeChoice" },
            primary.Elements(xaml + "ListViewItem").Select(element => (string?)element.Attribute(names + "Name")));
        var native = primary.Elements().Last();
        Assert.Equal("False", (string?)native.Attribute("IsEnabled"));
        var nativeBadge = Assert.Single(native.Descendants(), element =>
            element.Name.LocalName == "RecommendedBadge" &&
            (string?)element.Attribute(names + "Name") == "NativeRecommendedBadge");
        Assert.Null(nativeBadge.Attribute("Visibility"));
        Assert.Empty(document.Descendants(xaml + "Expander"));
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Content") == "Check again");
        var wsl = primary.Elements(xaml + "ListViewItem").First();
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
        Assert.Contains("ApplyNativeChoicePresentation(null)", source);
        Assert.Contains("ApplyNativeChoicePresentation(eligibility)", source);
        Assert.Contains("PlaceNativeChoice(available ? 0 : GatewayChoiceSelector.Items.Count - 1)", source);
        Assert.Contains("object? selectedItem = GatewayChoiceSelector.SelectedItem", source);
        Assert.Contains("GatewayChoiceSelector.Items.RemoveAt(currentIndex)", source);
        Assert.Contains("GatewayChoiceSelector.Items.Insert(targetIndex, NativeChoice)", source);
        Assert.Contains("GatewayChoiceSelector.SelectedItem = selectedItem", source);
        Assert.DoesNotContain("NativeChoice.Visibility", source);
        Assert.DoesNotContain("NativeRecommendedBadge.Visibility", source);
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
    public void Welcome_KeepsUnsupportedNativeChoiceLastWithSeparateSupportCard()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages");
        var document = XDocument.Load(Path.Combine(pages, "WelcomePage.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(
            element => (string?)element.Attribute(names + "Name") == name);
        var native = Named("NativeChoice");
        Assert.Equal("False", (string?)native.Attribute("IsEnabled"));
        var nativeBadge = Assert.Single(native.Descendants(), element =>
            element.Name.LocalName == "RecommendedBadge" &&
            (string?)element.Attribute(names + "Name") == "NativeRecommendedBadge");
        Assert.Null(nativeBadge.Attribute("Visibility"));
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
        Assert.Contains("PlaceNativeChoice(available ? 0 : GatewayChoiceSelector.Items.Count - 1)", source);
        Assert.DoesNotContain("NativeChoice.Visibility", source);
        Assert.DoesNotContain("NativeRecommendedBadge.Visibility", source);
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
        Assert.Equal("Install and set up an OpenClaw gateway on this device", resources["Onboarding_Native_Description.Text"]);
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("AutomationProperties.SetName(InstallChoice, SetupLocalization.GetString(\"Onboarding_Wsl_Title.Text\"))", source);
    }

    [Fact]
    public void Welcome_NativeBadgeSharesHeadingWithoutRedundantSuccessCopy()
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
            element.Name.LocalName == "RecommendedBadge" &&
            (string?)element.Attribute(names + "Name") == "NativeRecommendedBadge");
        var description = heading.ElementsAfterSelf().First();
        Assert.Equal("Onboarding_Native_Description", (string?)description.Attribute(names + "Uid"));
        Assert.Equal("Install and set up an OpenClaw gateway on this device", (string?)description.Attribute("Text"));
        Assert.Empty(description.ElementsAfterSelf());
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(names + "Name") is "NativeSupportAvailablePanel" or "NativeSupportAvailableText");
        var state = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(names + "Name") == "NativeRecommendedState");
        var setters = state.Descendants().Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(element => (string)element.Attribute("Target")!, element => (string?)element.Attribute("Value"));
        Assert.Equal("{ThemeResource AccentFillColorDefaultBrush}", setters["NativeIconBackground.Background"]);
        Assert.Equal("{ThemeResource TextOnAccentFillColorPrimaryBrush}", setters["NativeIcon.Foreground"]);
        Assert.Equal("{ThemeResource SubtleFillColorSecondaryBrush}", setters["WslIconBackground.Background"]);
        Assert.Equal("{ThemeResource TextFillColorPrimaryBrush}", setters["WslIcon.Foreground"]);
        var source = File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs"));
        Assert.Contains("VisualStateManager.GoToState(this, \"WslRecommendedState\", false)", source);
        Assert.Contains("available ? \"NativeRecommendedState\" : \"WslRecommendedState\"", source);
        Assert.Contains("NativeSupportStatusPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible", source);
        Assert.Contains("NativeSupportStatus.Text = available ? \"\" : NativeGatewayEligibilityText.Get(eligibility)", source);
        Assert.Contains("CreatePeerForElement(NativeSupportStatus)", source);
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
        Assert.Contains("NativeGatewaySetupConnection.ConnectAsync(native, _pageLifetime.Token)", wizard);
        Assert.DoesNotContain("new OpenClawGatewayClient", wizard);
        var gatewaySession = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "SetupGatewaySession.cs"));
        Assert.Contains("if (record.NativePackageFamilyName is not null)", gatewaySession);
        var adapter = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "NativeGatewaySetupConnection.cs"));
        Assert.Contains("await owner.PrepareConnectionAsync(ct)", adapter);
        Assert.Contains("owner.AuthorizeAsync", adapter);
        Assert.Contains("NativeGatewaySetupSession.GetPairingGuidance(requestId)", adapter);
        Assert.Contains("await owner.ApproveWizardPairingAsync(error.RequestId, linked.Token)", adapter);
        // Pairing and device-token recovery are each budgeted once per connect sequence,
        // and recovery never runs on the finalization path (allowPairing: false).
        Assert.Contains("allowPairing && !pairingAttempted", adapter);
        Assert.Contains("allowPairing && !recoveryAttempted", adapter);
        Assert.Contains("observedFailure == GatewayErrorKind.DeviceTokenMismatch", adapter);
        Assert.Contains("await owner.RecoverRejectedOperatorTokenAsync(storedDeviceToken, linked.Token)", adapter);
        var nativeConnection = adapter[
            adapter.IndexOf("private static async Task<NativeGatewaySetupConnection> ConnectCoreAsync", StringComparison.Ordinal)..
            adapter.IndexOf("public async Task<JsonElement> RequestAsync", StringComparison.Ordinal)];
        Assert.True(nativeConnection.IndexOf("for (var attempt", StringComparison.Ordinal) <
                    nativeConnection.IndexOf("new OpenClawGatewayClient", StringComparison.Ordinal));
        Assert.Contains("client.Dispose();", nativeConnection);
        Assert.Contains("client.HandshakeAuthorizationAsync = client.ReconnectAuthorizationAsync =", nativeConnection);
        Assert.True(nativeConnection.IndexOf("client.HandshakeAuthorizationAsync =", StringComparison.Ordinal) <
                    nativeConnection.IndexOf("await client.ConnectAsync()", StringComparison.Ordinal));
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
        Assert.Contains("SendWizardRequestAsync(connection, generation, \"wizard.start\"", wizard);
        Assert.Contains("SendWizardRequestAsync(connection, generation, \"wizard.next\"", wizard);
        Assert.Contains("SendWizardRequestAsync(connection, generation, \"wizard.cancel\"", wizard);
        Assert.Equal(1, wizard.Split(".SendWizardRequestAsync(", StringSplitOptions.None).Length - 1);
        Assert.Contains("await native.RequestAsync(method, parameters, timeoutMs, ct)", wizard);
        Assert.Contains("return new(connection.Client, connection)", wizard);
        Assert.Contains("ReferenceEquals(connection, _connection)", wizard);
        Assert.Contains("(method, parameters, timeout) => SendWizardRequestAsync(connection, generation", wizard);
        var close = wizard[wizard.IndexOf("private async Task CancelAndWaitCoreAsync", StringComparison.Ordinal)..
            wizard.IndexOf("private Task StartWizardAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("_nativeSession", close);
        Assert.Contains("await startTask", close);
        Assert.Contains("_nativeSession?.MarkWizardCompleted()", wizard);
        Assert.Contains("await native.CompleteAsync(native.LifetimeToken, _config!.Capabilities)", wizard);
        Assert.Contains("await native.RestartAsync(native.LifetimeToken)", wizard);
        Assert.Contains("nativeLogPath: isolated ? null : _nativeSession?.ConsoleLogPath", wizard);
        Assert.Contains("StartConsoleTailAsync(connection, generation)", wizard);
        Assert.Contains("WizardConsoleTail.CreateGatewayLogReader(", wizard);
        var focused = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs"));
        Assert.Contains("NativeSession is { IsIsolated: false }", focused);
        Assert.Contains("WizardConsoleTail.CreateGatewayLogReader(transport.RequestAsync)", focused);
        Assert.Contains("Onboarding_Wizard_GatewayConsoleGap", focused);
        Assert.Contains("Onboarding_Wizard_GatewayConsoleUnavailable", focused);
        Assert.DoesNotContain("onboard", host);
        Assert.DoesNotContain("wsl.exe", host);
        Assert.DoesNotContain("RunInWslAsync", host);
        Assert.Contains("[\"setup\"]", host);
        Assert.Contains("[\"config\", \"validate\", \"--json\"]", host);
        Assert.Contains("[\"gateway\", \"health\", \"--json\"]", host);
        Assert.False(File.Exists(Path.Combine(root, "src", "OpenClaw.SetupEngine", "NativeGatewayTerminalCommand.cs")));
    }

    [Fact]
    public void LocalAiChangeModel_PreservesNativeGatewayAndExistingWslSetupRoute()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "WindowManager.cs"));
        var start = manager.IndexOf("public Task ShowLocalAiModelSetupAsync()", StringComparison.Ordinal);
        var route = manager[start..manager.IndexOf("public async Task ShowLocalAiSetupAsync()", start, StringComparison.Ordinal)];
        Assert.Contains("NativeGatewayPackageClient.IsolatedContract", route);
        Assert.Contains("? ShowLocalAiSetupAsync() : ShowOnboardingAsync()", route);
        var model = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Presentation", "LocalAiPageViewModel.cs"));
        Assert.Contains("RunCommand(CanChangeModel, _appCommands.ShowLocalAiModelSetup)", model);
    }

    [Fact]
    public void NativeFocusedFlow_UsesOwnedTransportAndFreshVerificationWithoutForgedWizardCompletion()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var ui = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI");
        var window = File.ReadAllText(Path.Combine(ui, "SetupWindow.xaml.cs"));
        var ai = File.ReadAllText(Path.Combine(ui, "Pages", "AiSetupPage.xaml.cs"));
        var session = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "NativeGatewaySetupSession.cs"));
        var start = window.IndexOf("internal void NavigateToNativeAiSetup", StringComparison.Ordinal);
        var route = window[start..window.IndexOf("internal async Task CancelNativeAiSetupAsync", start, StringComparison.Ordinal)];
        Assert.Contains("typeof(AiSetupPage)", route);
        Assert.DoesNotContain("WizardPage", route);
        Assert.Contains("NativeGatewaySetupConnection.ConnectAsync(native, ct)", ai);
        Assert.Contains("await native.RestartAsync(token)", ai);
        Assert.Contains("native.VerifyAsync(proof, ct)", window);
        Assert.Contains("native.CompleteVerifiedAsync(proof, _config.Capabilities, ct,", window);
        Assert.Contains("ReconcileNativeAsync", window);
        Assert.Contains("await afterVerification(connection, linked.Token)", session);
        Assert.DoesNotContain("MarkWizardCompleted", ai);
        Assert.DoesNotContain("MarkWizardCompleted", window);
        Assert.Contains("ConnectForFinalizationAsync", session);
        Assert.Contains("await VerifyConnectionAsync(connection, proof, linked.Token)", session);
        Assert.Contains("registry.Save(beforePublication)", session);
        foreach (var file in new[] { Path.Combine(ui, "Pages", "AiSetupPage.xaml"),
                     Path.Combine(ui, "Controls", "ProviderSetupDialog.xaml") })
        {
            var xaml = File.ReadAllText(file);
            foreach (var action in new[] { "OpenTerminal", "RestartGateway", "RestartAi", "CancelSetup" })
                Assert.Contains($"Tag=\"{action}\"", xaml);
        }
        var ready = File.ReadAllText(Path.Combine(ui, "Pages", "AiReadyPage.xaml"));
        Assert.DoesNotContain("NativeSummary", ready);
        Assert.DoesNotContain("NativeGatewaySummary", ready);
        Assert.DoesNotContain("NativeCapabilitiesSummary", ready);
        Assert.DoesNotContain("Onboarding_Native_FeaturesNote", ready);
        Assert.Contains("GatewayAiSetupPresentation.ShowNativeRecovery", ai);
        Assert.DoesNotContain("UpdateNativeRecovery(true", ai);
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
        Assert.Contains("StartupPreferenceRow.Visibility = window?.ShowStartupPreference == true &&", source);
        Assert.Contains("_draft.Route != SetupGatewayRoute.ManagedWsl", source);
        Assert.DoesNotContain("SetupGatewayRoute.ManagedWsl or SetupGatewayRoute.Native", source);
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
        // Trust gating and branch selection live in NativeGatewayPackageIdentity.IsCandidate (behavior-tested
        // in OpenClaw.Connection.Tests); the resolver must apply it to every registered package.
        Assert.Contains(
            "NativeGatewayPackageIdentity.IsCandidate( package.Id.Name, package.Id.Publisher, package.Id.FamilyName, expectedFamily, devPatch)",
            System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " "));
        Assert.DoesNotContain("NativeGatewayPackageIdentity.IsSelectable(", source);
        Assert.DoesNotContain("NativeGatewayPackageIdentity.IsResolvable(", source);
        Assert.Contains("ResolveCoreAsync(null, cancellationToken)", source);
        Assert.Contains("ResolveCoreAsync(expectedFamily, cancellationToken)", source);
        Assert.Contains("packages.Length > 1", source);
        Assert.Contains("package.Status.VerifyIsOK()", source);
        Assert.Contains("\"Microsoft\", \"WindowsApps\", family", source);
        Assert.DoesNotContain("AddPackageAsync", source);
        Assert.DoesNotContain("Add-AppxPackage", source);
        Assert.Contains("NativeGatewayDataPathMapper.Resolve(", source);
        Assert.DoesNotContain("LogFileLauncher.ResolveRealPath", source);
    }
}
