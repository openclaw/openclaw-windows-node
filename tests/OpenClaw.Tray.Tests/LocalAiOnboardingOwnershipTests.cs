namespace OpenClaw.Tray.Tests;

public sealed class LocalAiOnboardingOwnershipTests
{
    private static string Read(string path) =>
        File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), path));

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
