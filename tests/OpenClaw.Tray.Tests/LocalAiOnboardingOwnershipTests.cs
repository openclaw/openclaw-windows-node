namespace OpenClaw.Tray.Tests;

public sealed class LocalAiOnboardingOwnershipTests
{
    private static string Read(string path) =>
        File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), path));

    [Fact]
    public void OnlyExplicitManagedUseCarriesRuntimeAndReconciliationRequirements()
    {
        // Retire when mounted onboarding tests can observe the complete native handoff.
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        Assert.Contains("requiresManagedLocalAi: _localUse?.Expected is not null", page);
        var refresh = page[page.IndexOf("private Task RefreshAsync()", StringComparison.Ordinal)..
            page.IndexOf("else if (Client.Phase == GatewayAiSetupPhase.Verified)", StringComparison.Ordinal)];
        var retainedUse = refresh.IndexOf("if (_localUse?.Expected is not null)", StringComparison.Ordinal);
        Assert.True(retainedUse >= 0);
        var reinitialize = refresh.IndexOf("if (Client is null)", retainedUse, StringComparison.Ordinal);
        Assert.Contains("await ReleaseAsync(ct);", refresh[retainedUse..reinitialize]);
        Assert.Contains("_controller = null;", refresh[retainedUse..reinitialize]);
        Assert.Contains("await InitializeAsync(ct);", refresh[reinitialize..]);
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("afterVerification: proof.RequiresManagedLocalAi &&", window);
        Assert.Contains("if (proof.RequiresManagedLocalAi &&", window);
        var verifier = Read(@"src\OpenClaw.SetupEngine\SetupNativeCompletionVerifier.cs");
        Assert.Equal(2, verifier.Split("expected.Intent, expected.RequiresManagedLocalAi").Length - 1);
        var session = Read(@"src\OpenClaw.SetupEngine\NativeGatewaySetupSession.cs");
        Assert.Contains("expected.Intent, expected.RequiresManagedLocalAi", session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartHandoffRetainsExplicitManagedUseRequirement(bool managedLocalAi)
    {
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        var proof = new OpenClaw.SetupEngine.GatewayAiSetupCompletion(
            OpenClaw.SetupEngine.SetupCompletionIntent.CustodianOnboarding,
            "gateway", new string('A', 64), "llamacpp/model", "main", 1,
            IdentityBinding: new string('B', 64), SessionKey: "agent:main:main",
            RequiresManagedLocalAi: managedLocalAi);
        var store = new OpenClawTray.Services.SetupDashboardHandoffStore(directory.Path);
        var handle = store.Issue(new(proof, new(OpenClaw.SetupEngine.SetupNativeDestination.Chat, proof.SessionKey!)));
        using var lease = new OpenClawTray.Services.SetupDashboardHandoffStore(directory.Path).Acquire(handle).Lease;
        Assert.NotNull(lease);
        Assert.Equal(proof, lease!.Completion);
        lease.Consume();
    }

    [Fact]
    public void NativeInstallAndUse_TransfersReviewedConsentWithoutReturningToDiscovery()
    {
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        var progress = Read(@"src\OpenClaw.SetupEngine.UI\Pages\ProgressPage.xaml.cs");
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        Assert.Contains("var modelId = _config.LocalAi.SelectedModelId;", window);
        Assert.Contains("_config.LocalAi.SelectedModelId != modelId || _config.LocalAi.Port != requestedPort", window);
        Assert.Contains("InstallAndUse = intent", window);
        Assert.Contains("ExpectedEndpointBinding = intent.Target.EndpointBinding", window);
        Assert.Contains("owner.ContinueInstalledNativeLocalAi();", progress);
        var start = page.IndexOf("if (_args.InstallAndUse is { IsConsumed: false } intent)", StringComparison.Ordinal);
        var end = page.IndexOf("if (_localObservation is not null && _localExpectedModel is null)", start, StringComparison.Ordinal);
        var continuation = page[start..end];
        Assert.Contains("EnsureLocalAiCanStart(intent.Target.GatewayId)", continuation);
        Assert.Contains("await _localUse.UseInstalledAsync(intent", continuation);
        Assert.Contains("await ReleaseAsync(ct)", continuation);
        Assert.Contains("await InitializeAsync(ct)", continuation);
        Assert.Contains("return;", continuation);
        var recoveryStart = continuation.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var recoveryEnd = continuation.IndexOf("finally { ++_progressScope; }", StringComparison.Ordinal);
        var recovery = continuation[recoveryStart..recoveryEnd];
        Assert.Contains("LocalAiSelectionRejectedException or LocalAiStartFailedException", recovery);
        Assert.Contains("_localObservation!.RefreshAsync", recovery);
        Assert.Contains("TitleText.Text = S(\"Title.Text\")", recovery);
        Assert.Contains("S(\"LocalStartFailed\") + \" \" + ex.Message", recovery);
        Assert.Contains("await DetectAsync(ct)", recovery);
        Assert.DoesNotContain("DetectAsync", continuation[recoveryEnd..]);
        var navigation = page[page.IndexOf("protected override void OnNavigatedTo", StringComparison.Ordinal)..
            page.IndexOf("protected override void OnNavigatedFrom", StringComparison.Ordinal)];
        Assert.DoesNotContain("_localObservation.RefreshAsync", navigation);
        Assert.Contains("VerifyConfiguredAsync(modelRef, ct)", page);
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
            Assert.Contains("Onboarding_AiSetup_LocalInstalling",
                Read($@"src\OpenClaw.Tray.WinUI\Strings\{locale}\Resources.resw"));
        Assert.Contains("Onboarding_AiSetup_LocalInstalling", progress);
        Assert.Contains("TitleText.Text = S(\"LocalInstalling\")", page);
        Assert.Contains("_args = _args with { InstallAndUse = null };", page);
    }

    [Fact]
    public void WslObservation_StartsBeforeConnectionAndIsNotRepeatedAfterAdmission()
    {
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        var start = page.IndexOf("private async Task InitializeAsync", StringComparison.Ordinal);
        var end = page.IndexOf("private async Task StartIsolatedConsoleAsync", start, StringComparison.Ordinal);
        var initialize = page[start..end];
        var observation = initialize.IndexOf("AsyncEventHandlerGuard.Run(_localObservation.RefreshAsync", StringComparison.Ordinal);
        var connect = initialize.IndexOf("await SetupGatewaySession.ConnectAsync", StringComparison.Ordinal);
        Assert.True(observation >= 0 && observation < connect);
        Assert.Contains("observationStarted = true;", initialize[observation..connect]);
        Assert.Contains("else if (!observationStarted)", initialize[connect..]);
    }

    [Fact]
    public void AiPage_UsesTypedSameWindowHostAndNeverOwnsRuntimeOrGatewayRegistration()
    {
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        Assert.Contains("ISetupLocalAiHost? LocalAiHost", page);
        Assert.Contains("ReviewLocalAi: ReviewLocalAiAsync", window);
        Assert.Contains("await _localAiHost.RevalidateReviewAsync(selection, _lifetimeCts.Token)", window);
        Assert.Contains("_localObservation = new(host)", page);
        Assert.DoesNotContain("ShowLocalAiSetupAsync", page + window);
        Assert.DoesNotContain("new SetupWindow(", page + window);
        Assert.DoesNotContain("new LlamaServerRuntimeService", page + window);
        Assert.DoesNotContain("new LocalAiManifestStore", page);
        Assert.DoesNotContain("Process.Start", page);
    }

    [Fact]
    public void Observation_DoesNotCallMutatingRuntimeRefreshOrAnySetupAction()
    {
        var source = Read(@"src\OpenClaw.Tray.WinUI\Services\SetupLocalAiHost.cs");
        var observation = source[source.IndexOf("public async Task<LocalAiOnboardingSnapshot> ObserveAsync", StringComparison.Ordinal)..
            source.IndexOf("public async Task<SetupLocalAiTarget> RevalidateReviewAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("RefreshAsync", observation);
        Assert.DoesNotContain("EnsureStartedAsync", observation);
        Assert.DoesNotContain("PublishAsync", observation);
        Assert.DoesNotContain("SaveAsync", observation);
        Assert.Contains("getRuntime()?.Snapshot", observation);
        var reconciler = Read(@"src\OpenClaw.SetupEngine\LocalAiInstallReconciler.cs");
        var inspection = reconciler[reconciler.IndexOf("public async Task<bool> InspectAsync", StringComparison.Ordinal)..
            reconciler.IndexOf("private static LlamaRuntimeInstallResult CreateRuntimeInstall", StringComparison.Ordinal)];
        Assert.DoesNotContain("Migrate", inspection);
        Assert.DoesNotContain("SaveAsync", inspection);
    }

    [Fact]
    public void NativeOwnershipDiscoveryIsReadOnlyAndGuidanceIsLocalized()
    {
        var source = Read(@"src\OpenClaw.Tray.WinUI\Services\LocalAiGatewayLifecycle.cs");
        var observation = source[source.IndexOf("public async Task<NativeLocalAiOwnershipState> ObserveOwnershipAsync", StringComparison.Ordinal)..
            source.IndexOf("public async Task SetAutomaticRecoveryEnabledAsync", StringComparison.Ordinal)];
        foreach (var prohibited in new[] { "GetOrCreate", "AcquireAsync", ".Save(", ".Delete(", "VerifyConfiguredAsync",
            "PrepareAsync", "EnsureStartedAsync", "PublishAsync", "RefreshAsync" })
            Assert.DoesNotContain(prohibited, observation);
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        Assert.Contains("local?.ReplacesDetectedChoice == true", page);
        Assert.Contains("state == LocalAiOnboardingState.ManagementBlocked", page);
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var resources = System.Xml.Linq.XDocument.Parse(Read($@"src\OpenClaw.Tray.WinUI\Strings\{locale}\Resources.resw"));
            foreach (var key in new[] { "LocalState_ManagementBlocked", "LocalOwnershipMissing", "LocalOwnershipInvalid",
                "LocalOwnershipUnavailable", "LocalOwnershipFiles" })
            {
                var value = Assert.Single(resources.Descendants("data"),
                    element => (string?)element.Attribute("name") == "Onboarding_AiSetup_" + key).Element("value")!.Value;
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.DoesNotContain("—", value);
            }
        }
    }

    [Fact]
    public void SetupWindow_DrainsDepartedAiPagesAndCancelledPipelineBeforeUnlock()
    {
        var window = Read(@"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs");
        var progress = Read(@"src\OpenClaw.SetupEngine.UI\Pages\ProgressPage.xaml.cs");
        Assert.Contains("RootFrame.Content is AiSetupPage aiPage", window);
        Assert.Contains("Task.WhenAll(_aiPageCleanupTask, aiPage.CloseAsync())", window);
        Assert.True(window.IndexOf("await _aiPageCleanupTask", StringComparison.Ordinal) <
            window.IndexOf("_setupLock?.Dispose()", StringComparison.Ordinal));
        Assert.Contains("Page, IAsyncDisposable", progress);
        Assert.Contains("await _pipelineTask", progress);
        Assert.Contains("if (_closed || _window?.IsClosed == true)", progress);
    }

    [Fact]
    public void NormalGatewayReview_DoesNotOfferACompetingLocalAiSelector()
    {
        var source = Read(@"src\OpenClaw.SetupEngine.UI\Pages\GatewaySetupPage.xaml.cs");
        Assert.Contains("LocalAiCard.Visibility = _draft.Config.LocalAi.Enabled || _window.IsLocalAiRecovery", source);
        var xaml = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml");
        Assert.Contains("<tk:SettingsCard x:Name=\"LocalAiCard\"", xaml);
        Assert.Contains("<tk:SettingsExpander.ItemsFooter>", xaml);
        Assert.DoesNotContain("<tk:SettingsExpander.Items>", xaml);
        Assert.Contains("Click=\"ChoiceAction_Click\"", xaml);
        var page = Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml.cs");
        var selection = page[page.IndexOf("private void Choice_Changed", StringComparison.Ordinal)..
            page.IndexOf("private void ChoiceAction_Click", StringComparison.Ordinal)];
        Assert.DoesNotContain("StartSelectedAsync", selection);
        Assert.DoesNotContain("ContinueAsync", selection);
    }

    [Fact]
    public void AiHeaderBand_UsesSharedCenteredArtworkAndDoesNotPromiseAContinueGate()
    {
        var document = System.Xml.Linq.XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\AiSetupPage.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "TitleText", "StatusText" })
        {
            var text = Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
            Assert.Equal("Center", (string?)text.Attribute("TextAlignment"));
            Assert.Contains(text.Ancestors(), element => (string?)element.Attribute(x + "Name") == "AiHeader");
        }
        var header = Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "AiHeader");
        Assert.Equal("StackPanel", header.Name.LocalName);
        Assert.Null(header.Attribute("Height"));
        var mascot = Assert.Single(header.Descendants(), element => element.Name.LocalName == "OnboardingMascot");
        Assert.Equal("Center", (string?)mascot.Attribute("HorizontalAlignment"));
        Assert.Null(mascot.Attribute("Width"));
        Assert.Null(mascot.Attribute("Height"));
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var resources = System.Xml.Linq.XDocument.Parse(Read($@"src\OpenClaw.Tray.WinUI\Strings\{locale}\Resources.resw"));
            var text = Assert.Single(resources.Descendants("data"),
                element => (string?)element.Attribute("name") == "Onboarding_AiSetup_Choose").Element("value")!.Value;
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("—", text);
        }
        Assert.Contains("Choose a model or connect an AI provider.",
            Read(@"src\OpenClaw.Tray.WinUI\Strings\en-us\Resources.resw"));
        Assert.DoesNotContain("Nothing is tested or changed until you continue.",
            Read(@"src\OpenClaw.Tray.WinUI\Strings\en-us\Resources.resw"));
    }
}
