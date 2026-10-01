using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class OnboardingPresentationContractTests
{
    private static string Read(string path) =>
        File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), path));

    [Fact]
    public void PipelineRegistryWriters_ProduceTheExpectedAdoptionSnapshot()
    {
        foreach (var step in new[] { "CleanupStaleGatewayStep", "PairOperatorStep", "VerifyEndToEndStep" })
        {
            var source = Read($@"src\OpenClaw.SetupEngine\{step}.cs");
            Assert.Contains("ctx.LoadSetupRegistry()", source);
            Assert.Contains("ctx.SaveSetupRegistry(registry)", source);
            Assert.DoesNotContain("registry.Save()", source);
        }
        var progress = Read(@"src\OpenClaw.SetupEngine.UI\Pages\ProgressPage.xaml.cs");
        Assert.Contains("ctx.ExpectedGatewayRegistry = setupOwner?.BeginGatewaySetup()", progress);
        Assert.Contains("SetupPipeline.RunWithSettlementAsync", progress);
        Assert.True(progress.IndexOf("SettleGatewaySetupAsync(ctx.ExpectedGatewayRegistry", StringComparison.Ordinal) <
            progress.IndexOf("if (_closed || _window?.IsClosed == true)", StringComparison.Ordinal));
    }

    [Fact]
    public void AccessReview_UsesOneCombinedPageAndOneDraftWithoutEarlyPersistence()
    {
        var xaml = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml");
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml.cs");
        Assert.DoesNotContain("controls:SetupWindowsAccessControl", xaml);
        Assert.DoesNotContain("LocalAiSetupControl", xaml);
        Assert.DoesNotContain("TailscaleSetupControl", xaml);
        Assert.Contains("NavigateAfterCapabilities()", source);
        Assert.Contains("new SettingsCard", source);
        Assert.DoesNotContain("GetProperty", source);
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Equal(1, window.Split("new SetupAccessDraft(_config)").Length - 1);
        Assert.Contains("if (!AccessDraft.CanInstall(_startAtLocalAiRecoveryReview))", window);
        Assert.DoesNotContain("AlternateSetupReady", window);
        Assert.Contains("OnboardingFlowPolicy.GetAccessDestination(AccessDraft.Route)", window);
        Assert.Contains("case OnboardingAccessDestination.AiSetup:", window);
        Assert.Contains("case OnboardingAccessDestination.CompleteWithoutGateway:", window);
        Assert.Contains("AsyncEventHandlerGuard.Run(CompleteSetupAsync", window);
        Assert.DoesNotContain("NavigateToWindowsPermissions", window);
        Assert.Contains("NavigateToCapabilities(back: true)", Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupPage.xaml.cs"));
        Assert.Contains("x:Name=\"StartupPreferenceToggle\"", Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupPage.xaml"));
        var detail = Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupDetailPage.xaml.cs");
        Assert.Contains("WslMirroredNetworkingConsent = LocalAiNetworkingConsentCheckBox.IsChecked == true", detail);
        Assert.DoesNotContain("MergeIntoSettingsFile", source + detail);
    }

    [Fact]
    public void ExistingGateway_DoesNotImplicitlyInvokeClassicSettingsFallback()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\WelcomePage.xaml.cs");
        Assert.DoesNotContain("SetupWindow.Active?.RequestAdvancedSetup()", source);
        Assert.Contains("SelectGatewayRoute(SetupGatewayRoute.Existing)", source);
        Assert.DoesNotContain("new ContentDialog", source);
        Assert.Contains("ClassicSettings_Click", Read(@"src\OpenClaw.SetupEngine.UI\Pages\AdvancedSetupPage.xaml.cs"));
    }

    [Fact]
    public void SuccessfulInstallation_UsesAiSetupWithoutAMilestoneClick()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\ProgressPage.xaml.cs");
        Assert.Contains("OnboardingFlowPolicy.RequiresAiSetup(config)", source);
        Assert.Contains("_window?.TryNavigateToWizard()", source);
        Assert.Contains("ctx.ResolvedLocalAiModelRef", source);
        Assert.Contains("SetExpectedConfiguredModelRef(modelRef,", source);
        Assert.Contains("config.LocalAiRecoveryGatewayId ?? ctx.GatewayRecordId", source);
        Assert.Contains("await setupWindow.CompleteSetupAsync()", source);
        Assert.Contains("OnboardingFlowPolicy.BuildInstallationSteps(localAiRecoveryOnly)", source);
        Assert.DoesNotContain("SetupStepFactory.BuildDefaultSteps()", source);
    }

    [Fact]
    public void Completion_PreservesWorkspaceAndStartupOwnership()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("new AiSetupPageArgs(_config, _dataDir, _localDataDir,", source);
        Assert.Contains("TryNavigateToLegacyWizard, CompleteSetupAsync, _expectedConfiguredModelRef", source);
        Assert.Contains("var result = await ApplyWindowsNodeContextAsync()", source);
        Assert.Contains("if (!result.IsSuccess)", source);
        Assert.Contains("RequestSetupCompleted(_persistStartupPreferenceOnComplete && AutoStartAfterSetup)", source);
        Assert.Contains("if (_completionDispatched || _isClosed)", source);
        Assert.Contains("_completionDispatched = true", source);
        Assert.DoesNotContain("static Func", source);
        Assert.Contains("RootFrame.Content is IAsyncDisposable pageLifetime", source);
        Assert.True(source.IndexOf("await pageCleanup", StringComparison.Ordinal) <
            source.IndexOf("_setupLock?.Dispose()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SecurityNoticePage")]
    [InlineData("WelcomePage")]
    [InlineData("CapabilitiesPage")]
    [InlineData("GatewaySetupPage")]
    [InlineData("GatewaySetupDetailPage")]
    [InlineData("ProgressPage")]
    [InlineData("WizardPage")]
    [InlineData("AiSetupPage")]
    [InlineData("AiReadyPage")]
    public void SetupPages_UseVectorMascotAndRouteDrivenProgress(string page)
    {
        var xaml = Read($@"src\OpenClaw.SetupEngine.UI\Pages\{page}.xaml");
        Assert.Contains("controls:OnboardingMascot", xaml);
        Assert.Contains("controls:SetupProgressIndicator", xaml);
        Assert.DoesNotContain("of 6", xaml);
        Assert.DoesNotContain("OpenClawMascot.png", xaml);
    }

    [Fact]
    public void NativeChat_DashboardActionDoesNotDependOnTheWebViewToolbar()
    {
        var document = XDocument.Parse(Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dashboard = Assert.Single(document.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "DashboardButton");
        Assert.Equal("OnOpenDashboard", (string?)dashboard.Attribute("Click"));
        Assert.DoesNotContain(dashboard.Ancestors(),
            element => (string?)element.Attribute(x + "Name") == "ToolbarBorder");
        Assert.Contains("((IAppCommands)CurrentApp).OpenDashboard()",
            Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs"));
        Assert.Contains("ChatFlyoutDashboardButton",
            Read(@"src\OpenClaw.Tray.WinUI\Windows\ChatWindow.xaml"));
        Assert.Contains("((IAppCommands)Application.Current).OpenDashboard()",
            Read(@"src\OpenClaw.Tray.WinUI\Windows\ChatWindow.xaml.cs"));
    }

    [Fact]
    public void PageTransitions_RespectWindowsAnimationPreference()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("UISettings().AnimationsEnabled", source);
        Assert.Contains("new SuppressNavigationTransitionInfo()", source);
    }

    [Fact]
    public void Completion_RestartsWithTheRouteSpecificDestination()
    {
        var source = Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs");
        Assert.Contains("OnboardingFlowPolicy.GetCompletionLaunchTarget(e.Route)", source);
        Assert.Contains("psi.ArgumentList.Add(launchTarget)", source);
    }

    [Fact]
    public void ProviderProof_CapturesOwnedWindowAndVerifiesRenderedDialogText()
    {
        var source = Read(@"tests\OpenClaw.Tray.UITests\OnboardingArtworkRenderingTests.cs");
        var capture = Read(@"tests\OpenClaw.Tray.UITests\OnboardingNativeProof.cs");
        Assert.DoesNotContain("CopyFromScreen", source + capture);
        Assert.Contains("PrintWindow(handle, dc, 2)", capture);
        Assert.Contains("Assert.Equal((uint)Environment.ProcessId, processId)", capture);
        Assert.Contains("[\"providerinput\", \"syntheticproviderprompt\", \"testcode\"]", source);
        Assert.Contains("engine.RecognizeAsync(bitmap)", capture);
        Assert.Contains("Assert.Equal(handle, GetForegroundWindow())", capture);
        Assert.Contains("bounds.IntersectsWith(other.Rectangle)", capture);
        Assert.Contains("Assert.Equal(bounds, AssertCaptureTarget(window))", capture);
    }

    [Fact]
    public void AiGroups_KeepManualInputInlineAndMoreLimitedToAuthentication()
    {
        var xaml = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml");
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        var groups = new[] { "LocalAiSection", "CandidatesSection", "UnavailableSection",
            "PrepareSection", "SignInSection", "MoreExpander", "RecommendedSection", "ApiKeySection", "ApiKeyForm" };
        var offsets = groups.Select(name => xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal)).ToArray();
        Assert.All(offsets, offset => Assert.True(offset >= 0));
        Assert.Equal(offsets.Order(), offsets);
        Assert.Contains("MoreSignInChoices.ItemsSource = ProviderRows(_presentation.MoreSignIn, GatewayAiSetupChoiceKind.Auth)", source);
        Assert.Contains("ApiKeyForm.StartBringIntoView()", source);
        Assert.Contains("ApiProviderPicker.SelectedIndex = -1", source);
        Assert.Contains("ApiKeyForm.Visibility = Visible(_presentation.ShowApiKeyForm)", source);
        Assert.Contains("RecommendedInstalls.ItemsSource = ProviderRows(_presentation.RecommendedInstalls, GatewayAiSetupChoiceKind.Prepare)", source);
        Assert.Contains("SetupLocalization.GetString(\"Onboarding_AiSetup_\" + key)", source);
        Assert.DoesNotContain("WizardPanel", xaml);
        Assert.DoesNotContain("new ContentDialog", source);
        Assert.DoesNotContain("x:Name=\"LocalAiHeading\"", xaml);
        Assert.DoesNotContain("x:Name=\"LocalAiExplanation\"", xaml);
        Assert.Contains("snapshot.ShowLocalChoice", source);
        Assert.Contains("FluentIconCatalog.Key", source);
        Assert.Contains("ApiProviderPicker.SelectedItem is ChoiceRow row && CanConnectManual()", source);
        Assert.Contains("SelectChoice(row);", source);
        Assert.Contains("selected.Id != row.Id || selected.ModelRef != row.ModelRef", source);
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(document.Descendants(),
            element => (string?)element.Attribute(x + "Name") == name);
        Assert.Equal("SettingsCard", Named("ApiKeysButton").Name.LocalName);
        Assert.Equal("StackPanel", Named("ApiKeysButton").Parent!.Name.LocalName);
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ListView");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "ContinueButton");
        Assert.DoesNotContain(Named("ApiKeysButton").Ancestors(), element => element == Named("SignInSection"));
        Assert.DoesNotContain(Named("ApiKeyForm").Ancestors(), element => element == Named("SignInSection"));
        Assert.Contains(Named("ApiKeysButton").Ancestors(), element => element == Named("ApiKeySection"));
        Assert.Contains(Named("ApiKeyForm").Ancestors(), element => element == Named("ApiKeySection"));
        Assert.Contains(Named("MoreExpander").Ancestors(), element => element == Named("ProviderGroup"));
        Assert.Contains(Named("FeaturedSignInChoices").Ancestors(), element => element == Named("ProviderGroup"));
        Assert.Equal("SettingsExpander", Named("RecommendedSection").Name.LocalName);
        Assert.Equal("False", (string?)Named("RecommendedSection").Attribute("IsExpanded"));
        Assert.Equal("PasswordBox", Named("ApiKeyInput").Name.LocalName);
        Assert.Equal("ManualConnect_Click", (string?)Named("ApiKeyConnectButton").Attribute("Click"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "AdaptiveTrigger");
        Assert.Equal("ApiKeyFields_SizeChanged", (string?)Named("ApiKeyFields").Attribute("SizeChanged"));
        Assert.Contains("e.NewSize.Width >= 560", source);
        Assert.Equal("CheckBox", Named("CatalogPreference").Name.LocalName);
        Assert.Equal("False", (string?)Named("CatalogPreference").Attribute("IsChecked"));
        Assert.Contains("? CatalogPreference.IsChecked == true : (bool?)null", source);
        Assert.DoesNotContain(Named("ApiKeyFields").Descendants(),
            element => element.Attribute("Height") is not null || element.Attribute("FontSize") is not null);
    }

    [Fact]
    public void SetupOwnership_ClosesCombinedCapabilitiesReview()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml.cs");
        Assert.DoesNotContain("InitializeLocalAiReviewAsync", source);
        Assert.DoesNotContain("RefreshWindowsTailscaleStatusAsync", source);
        Assert.DoesNotContain("SetupPermissionHelper", source);
        Assert.DoesNotContain("SetupReviewSummaryBuilder", source);
        Assert.DoesNotContain("WindowsAccess", source);
        Assert.Contains("LocalAiSetupAvailabilityCoordinator", Read(@"src\OpenClaw.SetupEngine.UI\Controls\LocalAiSetupControl.xaml.cs"));
        Assert.Contains("BoundedProcessOutput.ReadAsync", Read(@"src\OpenClaw.SetupEngine.UI\Controls\TailscaleSetupControl.xaml.cs"));
    }

    [Fact]
    public void RetiredWindowsAccessPreview_CannotReintroduceProbesOrAnExtraSetupStage()
    {
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.DoesNotContain("WindowsPermissionsPage", window);
        Assert.DoesNotContain("PrivacyProbe", window);
        Assert.DoesNotContain("\"permissions\" =>", window);
        Assert.DoesNotContain("Permissions,", Read(@"src\OpenClaw.SetupEngine\OnboardingFlowPolicy.cs"));
        foreach (var path in new[]
        {
            @"src\OpenClaw.SetupEngine.UI\Pages\WindowsPermissionsPage.xaml",
            @"src\OpenClaw.SetupEngine.UI\Controls\SetupWindowsAccessControl.xaml",
            @"src\OpenClaw.SetupEngine.UI\Pages\SetupPermissionHelper.cs",
            @"src\OpenClaw.SetupEngine\SetupPrivacyObservation.cs",
            @"src\OpenClaw.SetupEngine\SetupPrivacyPolicy.cs",
        })
            Assert.False(File.Exists(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), path)), path);
    }

    [Fact]
    public void NativeAndGatewayFreeCompletion_NeverEntersWslWorkspaceFinalization()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        var routeGuard = source.IndexOf("if (!OnboardingFlowPolicy.UsesWslWorkspaceFinalization(AccessDraft.Route))", StringComparison.Ordinal);
        var wslContext = source.IndexOf("var context = new SetupContext(", StringComparison.Ordinal);
        Assert.True(routeGuard >= 0 && routeGuard < wslContext);
        Assert.Contains("StepResult.Skip(\"This setup route does not modify a WSL workspace.\")", source);
        Assert.Contains("if (AccessDraft.Route != SetupGatewayRoute.ManagedWsl)", source);
        Assert.Contains("_config.Settings.MergeIntoSettingsFile(Path.Combine(_dataDir, \"settings.json\"),", source);
        Assert.Contains("includeAutoStart: _persistStartupPreferenceOnComplete || !_startupRegistrationAllowed", source);
        Assert.Contains("new SetupCompletedEventArgs(enableAutoStart, AccessDraft.Route,", source);
        Assert.Contains("x:Name=\"StartupPreferenceToggle\"", Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml"));
    }

    [Fact]
    public void NativeEditor_IsTypedAndDrainedBeforeTheRunLockIsReleased()
    {
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("ISetupNativeConnectionHost? nativeConnectionHost = null", window);
        Assert.Contains("new SetupNativeConnectionNavigationArgs(", window);
        Assert.Contains("AccessDraft.NativeConnectionRequest = request", window);
        Assert.Contains("AccessDraft.TryAcceptNativeConnection(route, result)", window);
        Assert.Contains("RootFrame.Content is SetupNativeConnectionPage nativePage", window);
        Assert.Contains("nativePage.DisposeAsync().AsTask()", window);
        Assert.True(window.IndexOf("await _nativePageCleanupTask", StringComparison.Ordinal) <
            window.IndexOf("_setupLock?.Dispose()", StringComparison.Ordinal));
        Assert.Contains("AccessDraft?.ClearNativeConnectionSecrets()", window);
        Assert.DoesNotContain("new GatewayConnectionManager", window);
        Assert.DoesNotContain("new SetupNativeConnectionResult", window);
    }

    [Fact]
    public void NewSetupCopy_IsAvailableInAllSixLocales()
    {
        string[] locales = ["en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw"];
        var sets = locales.Select(locale =>
            XDocument.Parse(Read($@"src\OpenClaw.Tray.WinUI\Strings\{locale}\Resources.resw"))
                .Descendants("data")
                .Where(data => data.Attribute("name")!.Value.StartsWith("Onboarding_V2_", StringComparison.Ordinal) ||
                    data.Attribute("name")!.Value.StartsWith("Onboarding_V3_", StringComparison.Ordinal) ||
                    data.Attribute("name")!.Value.StartsWith("Onboarding_V4_", StringComparison.Ordinal) ||
                    data.Attribute("name")!.Value.StartsWith("Onboarding_V5_", StringComparison.Ordinal) ||
                    data.Attribute("name")!.Value.StartsWith("Onboarding_Ready_", StringComparison.Ordinal) ||
                    data.Attribute("name")!.Value.StartsWith("Onboarding_Copy_", StringComparison.Ordinal) ||
                    data.Attribute("name")!.Value.StartsWith("Onboarding_AiSetup_", StringComparison.Ordinal))
                .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")!.Value))
            .ToArray();
        Assert.NotEmpty(sets[0]);
        foreach (var set in sets)
        {
            Assert.Equal(sets[0].Keys.Order(), set.Keys.Order());
            Assert.All(set.Values, value => { Assert.False(string.IsNullOrWhiteSpace(value)); Assert.DoesNotContain("—", value); });
        }
    }

    [Fact]
    public void CapabilityProfiles_UseThreeNativeChoicesAndLegalInlineFineTuneItems()
    {
        var xaml = XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var selector = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "ProfileSelector");
        Assert.Equal("ListView", selector.Name.LocalName);
        Assert.Equal("Single", (string?)selector.Attribute("SelectionMode"));
        Assert.Equal("Stretch", (string?)selector.Attribute("HorizontalContentAlignment"));
        Assert.Equal(3, selector.Elements().Count());
        Assert.All(selector.Elements(), row =>
        {
            Assert.Equal("ListViewItem", row.Name.LocalName);
            Assert.Equal("Stretch", (string?)row.Attribute("HorizontalContentAlignment"));
        });
        Assert.DoesNotContain(xaml.Descendants(), element => element.Name.LocalName is "RadioButton" or "RadioButtons");
        var editor = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "FineTuneExpander");
        Assert.Equal("SettingsExpander", editor.Name.LocalName);
        Assert.Equal("False", (string?)editor.Attribute("IsExpanded"));
        Assert.DoesNotContain(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "CustomChoice");
        Assert.DoesNotContain(xaml.Descendants(), element => (string?)element.Attribute(x + "Uid") == "Onboarding_V4_ProfileHeading");
        Assert.Contains(editor.Elements(), element => element.Name.LocalName == "SettingsExpander.ItemsFooter");
        Assert.Contains(editor.Descendants(), element => (string?)element.Attribute(x + "Name") == "SelectedSummary");
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml.cs");
        Assert.Contains("FineTuneExpander.Items.Add(card)", source);
        Assert.Contains("new SettingsCard", source);
        Assert.Contains("FineTuneExpander.IsExpanded = _draft.FineTuneExpanded", source);
        Assert.Contains("CustomProfileText.Visibility = _draft.Profile == SetupCapabilityProfile.Custom", source);
        Assert.Contains("? -1 : (int)_draft.Profile", source);
        Assert.DoesNotContain("new Border", source);
        Assert.DoesNotContain("ProfileRadio", source);
    }

    [Fact]
    public void Gallery_CoversCurrentCapabilitiesWithoutRetiredPrivacyPreview()
    {
        var catalog = Read(@"tests\OpenClaw.Tray.UITests\OnboardingSetupGalleryData.cs");
        var source = Read(@"tests\OpenClaw.Tray.UITests\OnboardingSetupGalleryTests.cs");
        Assert.DoesNotContain("\"permissions\"", catalog);
        Assert.Contains("05-capabilities-standard-fine-tune", catalog);
        Assert.DoesNotContain("legacy-access", catalog + source);
        Assert.DoesNotContain("privacyProbe:", source);
        Assert.Contains("OnboardingStage.Capabilities", source);
    }

    [Fact]
    public void InstallReview_UsesDraftRequirementsWithoutImplicitConsentAndPreservesDetailOrigin()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupPage.xaml.cs");
        Assert.Contains("_draft.GetInstallRequirements(_window.IsLocalAiRecovery)", source);
        Assert.Contains("if (presentedRequirement != _primaryRequirement) return", source);
        Assert.Contains("if (_draft.CanInstall(_window.IsLocalAiRecovery))", source);
        Assert.Contains("_draft.ConfirmReplacement(ReplacementConsent.IsChecked == true)", source);
        Assert.DoesNotContain("NavigateToReplacementReview", source);
        var review = XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var consent = Assert.Single(review.Descendants(), element => (string?)element.Attribute(x + "Name") == "ReplacementConsent");
        Assert.Equal("CheckBox", consent.Name.LocalName);
        Assert.Equal("ReplacementConsent_Changed", (string?)consent.Attribute("Checked"));
        Assert.DoesNotContain("LocalAiReady", source);
        Assert.DoesNotContain("TailscaleReady", source);
        Assert.Contains("NavigateToWslNetworking(returnToReview: true)", source);
        var detail = Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupDetailPage.xaml.cs");
        Assert.Contains("Detail: GatewaySetupDetail.Networking, ReturnToReview: false", detail);
        Assert.Contains("NavigateToLocalAiSetup(back: true)", detail);
        Assert.Contains("NavigateToGatewaySetup(back: true)", detail);
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("_startAtLocalAiRecoveryReview, _pinLocalAiRecoveryModel, returnToReview", window);
    }

    [Fact]
    public void ChangedOnboardingPages_PreserveCenteredLargeMascotsAndFixedFooters()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var page in new[] { "CapabilitiesPage", "GatewaySetupPage" })
        {
            var xaml = XDocument.Parse(Read($@"src\OpenClaw.SetupEngine.UI\Pages\{page}.xaml"));
            var mascot = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "MascotHero");
            Assert.Null(mascot.Attribute("Width"));
            Assert.Null(mascot.Attribute("Height"));
            Assert.Equal("Center", (string?)mascot.Attribute("HorizontalAlignment"));
            Assert.Equal("StackPanel", mascot.Parent!.Name.LocalName);
            Assert.Null(mascot.Parent.Attribute("Height"));
            var progress = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "FlowProgress");
            Assert.Equal("2", (string?)progress.Parent!.Attribute("Grid.Row"));
            Assert.DoesNotContain(progress.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        }
    }

    [Fact]
    public void EveryInstallRequirement_HasLocalizedBlockerReasonAndAction()
    {
        string[] requirements = ["ManagedWslRoute", "WslInspection", "Replacement", "LocalAi", "NetworkingConsent", "Tailscale"];
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var strings = XDocument.Parse(Read($@"src\OpenClaw.Tray.WinUI\Strings\{locale}\Resources.resw"))
                .Descendants("data").ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")!.Value);
            foreach (var requirement in requirements)
            foreach (var prefix in new[] { "Blocker", "Requirement", "Action" })
            {
                var value = strings[$"Onboarding_V2_{prefix}{requirement}"];
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.DoesNotContain("Onboarding_", string.Format(value, "TargetGateway"));
            }
        }
    }
}
