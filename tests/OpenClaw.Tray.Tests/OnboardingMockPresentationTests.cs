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
    public void NormalHeaders_KeepFullSizeArtworkAboveWrappedCenteredText(string name)
    {
        var document = Page(name);
        var mascot = Named(document, "MascotHero");
        Assert.Null(mascot.Attribute("Width"));
        Assert.Null(mascot.Attribute("Height"));
        Assert.Equal("Center", (string?)mascot.Attribute("HorizontalAlignment"));
        var header = mascot.Parent!;
        Assert.Equal("StackPanel", header.Name.LocalName);
        Assert.Null(header.Attribute("Height"));
        Assert.Null(header.Attribute("MaxHeight"));
        var title = Assert.Single(header.Elements(), element =>
            (string?)element.Attribute("Style") == "{StaticResource TitleTextBlockStyle}");
        Assert.Contains(mascot, header.Elements().TakeWhile(element => element != title));
        Assert.Equal("Center", (string?)title.Attribute("TextAlignment"));
        Assert.Equal("Wrap", (string?)title.Attribute("TextWrapping"));
        Assert.Null(title.Attribute("Height"));
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
    public void Installation_KeepsOverviewAndActionableStatusOutsideCollapsedDetails()
    {
        var page = Page("ProgressPage");
        var details = Named(page, "DetailedActivity");
        Assert.Equal("False", (string?)details.Attribute("IsExpanded"));
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "StepsPanel");
        Assert.Equal("48", (string?)details.Attribute("MinHeight"));
        Assert.Contains(details.Descendants(), element => element == Named(page, "OpenLogButton"));
        Assert.Contains(details.Descendants(), element => element == Named(page, "LogText"));
        foreach (var name in new[] { "PreparePhase", "InstallPhase", "ConnectPhase", "CurrentActivity",
                     "StepCount", "DownloadActivity", "DownloadProgress", "TailscaleAuthorizationPanel" })
            Assert.DoesNotContain(Named(page, name).Ancestors(), element => element == details);
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(X + "Name") == "ActivityProgress");
        foreach (var name in new[] { "PrepareStatus", "InstallStatus", "ConnectStatus" })
            Assert.Equal("SetupPhaseStatus", Named(page, name).Name.LocalName);
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        Assert.Contains("_installationProgress?.Apply(e)", source);
        Assert.Contains("Onboarding_V4_RecoveryInstall", source);
        Assert.Contains("progress.CompletedSteps, progress.TotalSteps", source);
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
        Assert.Equal("InfoBar", Named(review, "ReplacementWarning").Name.LocalName);
        Assert.Same(Named(review, "ReplacementWarning").Parent, Named(review, "ReplacementConsent").Parent);
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "Pages", "GatewaySetupPage.xaml.cs"));
        Assert.Contains("requirement != SetupInstallRequirement.Replacement", source);
        Assert.Contains("_primaryRequirement = requirements.Count == 0 ? null : requirements[0]", source);
        Assert.Contains("if (_draft.CanInstall(_window.IsLocalAiRecovery))", source);
        Assert.Contains("ReplacementWarning.Message = _draft.ReplacementSummary", source);
        Assert.Contains("CliCard.HeaderIcon = cliIcon", source);
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
