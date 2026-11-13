using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class OnboardingMockPresentationTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XDocument Page(string name) => XDocument.Load(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages", name + ".xaml"));
    private static XElement Named(XDocument page, string name) =>
        Assert.Single(page.Descendants(), element => (string?)element.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("SecurityNoticePage")]
    [InlineData("WelcomePage")]
    [InlineData("CapabilitiesPage")]
    [InlineData("GatewaySetupPage")]
    [InlineData("GatewaySetupDetailPage")]
    [InlineData("AdvancedSetupPage")]
    [InlineData("SetupNativeConnectionPage")]
    [InlineData("WizardPage")]
    [InlineData("AiSetupPage")]
    public void NormalHeaders_UseTheSharedHeroAndLeaveArtworkToTheCompanion(string name)
    {
        var document = Page(name);
        // The setup window's companion pane owns the only mascot; pages carry no artwork.
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "OnboardingMascot");
        var title = document.Descendants().First(element =>
            (string?)element.Attribute("Style") == "{StaticResource SetupHeroTitleTextStyle}");
        var header = title.Parent!;
        Assert.Equal("StackPanel", header.Name.LocalName);
        Assert.Equal("{StaticResource SetupHeroPanelStyle}", (string?)header.Attribute("Style"));
        Assert.Null(header.Attribute("Height"));
        Assert.Null(header.Attribute("MaxHeight"));
        Assert.Same(title, header.Elements().First());
        Assert.Null(title.Attribute("Height"));
        Assert.Null(title.Attribute("TextAlignment"));
    }

    [Fact]
    public void Profile_LeadsAndKeepsFineTuneInsideItsSurface()
    {
        var page = Page("CapabilitiesPage");
        var selector = Named(page, "ProfileSelector");
        var fineTune = Named(page, "FineTuneExpander");
        Assert.Same(selector.Parent, fineTune.Parent);
        Assert.Equal("Border", selector.Parent!.Parent!.Name.LocalName);
        var order = page.Descendants().ToList();
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "WindowsAccess");
        Assert.True(order.IndexOf(selector) < order.IndexOf(Named(page, "NodeModeToggle")));
        Assert.Equal(3, selector.Elements().Count(element => element.Name.LocalName == "ListViewItem"));
        Assert.DoesNotContain(selector.Descendants(), element => element.Name.LocalName == "SettingsCard");
    }

    [Fact]
    public void Recommendations_UseOneLocalizedThemeAwareBadge()
    {
        foreach (var (pageName, choiceName) in new[]
                 {
                     ("WelcomePage", "NativeChoice"), ("WelcomePage", "InstallChoice"),
                     ("CapabilitiesPage", "StandardChoice")
                 })
        {
            var choice = Named(Page(pageName), choiceName);
            var badge = Assert.Single(choice.Descendants(), element => element.Name.LocalName == "RecommendedBadge");
            Assert.Null(badge.Attribute("Padding"));
            Assert.Null(badge.Attribute("Foreground"));
        }

        var document = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Controls", "RecommendedBadge.xaml"));
        // The design shows the recommendation as inline accent text beside the choice title.
        var border = Named(document, "BadgeBorder");
        Assert.Null(border.Attribute("Padding"));
        Assert.Null(border.Attribute("BorderThickness"));
        var label = Named(document, "Label");
        Assert.Equal("Onboarding_Copy_Recommended", (string?)label.Attribute(X + "Uid"));
        Assert.Equal("Wrap", (string?)label.Attribute("TextWrapping"));
        Assert.Equal("{StaticResource SetupInlineRecommendedTextStyle}", (string?)label.Attribute("Style"));
        var disabled = Named(document, "Disabled");
        Assert.Equal(new[] { "Label.Foreground" },
            disabled.Descendants().Where(element => element.Name.LocalName == "Setter")
                .Select(element => (string?)element.Attribute("Target")));
        Assert.All(disabled.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => Assert.Equal("{ThemeResource TextFillColorDisabledBrush}", (string?)setter.Attribute("Value")));
    }

    [Fact]
    public void FineTune_ExplanatoryNotesKeepCardInsetsAndWrap()
    {
        var fineTune = Named(Page("CapabilitiesPage"), "FineTuneExpander");
        var footer = Assert.Single(fineTune.Elements(), element =>
            element.Name.LocalName == "SettingsExpander.ItemsFooter");
        var panel = Assert.Single(footer.Elements());
        Assert.Equal("StackPanel", panel.Name.LocalName);
        Assert.Equal("16,12", (string?)panel.Attribute("Padding"));
        Assert.Equal("8", (string?)panel.Attribute("Spacing"));
        Assert.Null(panel.Attribute("Height"));
        Assert.Null(panel.Attribute("MaxHeight"));
        Assert.Equal(
            new[] { "Onboarding_V2_DeviceFixed", "Onboarding_V2_NativeSettings" },
            panel.Elements().Select(element => (string?)element.Attribute(X + "Uid")));
        Assert.All(panel.Elements(), note =>
        {
            Assert.Equal("Wrap", (string?)note.Attribute("TextWrapping"));
            Assert.Null(note.Attribute("Height"));
            Assert.Null(note.Attribute("MaxHeight"));
        });
    }

    [Fact]
    public void Installation_KeepsOverviewAndActionableStatusOutsideCollapsedDetails()
    {
        var page = Page("ProgressPage");
        var details = Named(page, "DetailedActivity");
        var panel = Named(page, "DetailsPanel");
        Assert.Equal("Collapsed", (string?)panel.Attribute("Visibility"));
        Assert.Contains(panel.Ancestors(), element => element == details);
        Assert.Equal("HyperlinkButton", Named(page, "DetailsToggle").Name.LocalName);
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "StepsPanel");
        Assert.Contains(panel.Descendants(), element => element == Named(page, "OpenLogButton"));
        Assert.Contains(panel.Descendants(), element => element == Named(page, "LogText"));
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "CurrentActivity");
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "StepCount");
        foreach (var name in new[] { "PrepareStatus", "InstallStatus", "ConnectStatus",
                     "DownloadActivity", "DownloadProgress", "TailscaleAuthorizationPanel" })
            Assert.DoesNotContain(Named(page, name).Ancestors(), element => element == details);
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "ActivityProgress");
        foreach (var name in new[] { "PrepareStatus", "InstallStatus", "ConnectStatus" })
            Assert.Equal("SetupPhaseStatus", Named(page, name).Name.LocalName);
        var row = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Controls", "SetupPhaseStatus.xaml"));
        var activity = Named(row, "ActivityText");
        Assert.Equal("Polite", (string?)activity.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal("Polite", (string?)Named(row, "StatusText").Attribute("AutomationProperties.LiveSetting"));
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        Assert.Contains("_installationProgress?.Apply(e)", source);
        Assert.Contains("row.Activity = phase.CurrentActivity", source);
        Assert.Contains("Onboarding_V4_RecoveryInstall", source);
        Assert.DoesNotContain("Onboarding_V4_StepCount", source);
    }

    [Fact]
    public void Options_AreFlatAndReplacementDoesNotDuplicateTheOtherBlockers()
    {
        var capabilities = Page("CapabilitiesPage");
        var ollama = Named(capabilities, "OllamaToggle");
        Assert.Equal("SettingsCard", ollama.Parent!.Name.LocalName);
        Assert.DoesNotContain(ollama.Ancestors(), element => element.Name.LocalName == "SettingsExpander");
        var review = Page("GatewaySetupPage");
        Assert.Equal("TailscaleSetupControl", Named(review, "TailscaleOptions").Name.LocalName);
        Assert.DoesNotContain(Named(review, "TailscaleOptions").Ancestors(), element => element.Name.LocalName == "SettingsExpander");
        Assert.Equal("Grid", Named(review, "ReplacementWarning").Name.LocalName);
        Assert.Same(Named(review, "ReplacementWarning").Parent, Named(review, "ReplacementConsent").Parent);
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Pages", "GatewaySetupPage.xaml.cs"));
        Assert.Contains("requirement != SetupInstallRequirement.Replacement", source);
        Assert.Contains("_primaryRequirement = requirements.Count == 0 ? null : requirements[0]", source);
        Assert.Contains("if (_draft.CanInstall(_window.IsLocalAiRecovery))", source);
        Assert.Contains("ReplacementWarningText.Text = _draft.ReplacementSummary", source);
        Assert.Contains("CliDetail.Text = summary.InstallerBadge", source);
        Assert.DoesNotContain("NavigateToTailscaleSetup", source);
        Assert.Equal(1, source.Split("TailscaleOptions.StateChanged +=").Length - 1);
        Assert.Contains("TailscaleOptions.StateChanged -= Tailscale_StateChanged", source);
        Assert.Contains("TailscaleOptions.Deactivate()", source);
    }

    [Fact]
    public void ToolkitExpanders_KeepOnlySettingsCardsInItems()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
                     "src", "OpenClaw.SetupEngine.UI"), "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var document = XDocument.Load(file);
            foreach (var items in document.Descendants().Where(element => element.Name.LocalName == "SettingsExpander.Items"))
                Assert.All(items.Elements(), item => Assert.Equal("SettingsCard", item.Name.LocalName));
        }
    }
}
