using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupUxContractTests
{
    [Fact]
    public void WelcomePage_ShowsLocalAiAsIconBadgeBesideRecommended()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml.cs"));
        string badge = ExtractElement(xaml, "LocalAiAvailabilityBadge", "</Border>");

        Assert.Contains("WelcomeLocalAiAvailable", badge);
        Assert.Contains("Glyph=\"&#xE950;\"", badge);
        Assert.Contains("Local AI available", badge);
        Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"", badge);
        AssertInOrder(xaml, "Text=\"Recommended\"", "x:Name=\"LocalAiAvailabilityBadge\"");
        Assert.DoesNotContain("LocalAiAvailabilityPanel", xaml);
        Assert.DoesNotContain("LocalAiAvailabilityText", xaml);
        Assert.DoesNotContain("detected. Install a local gateway", source);
        Assert.Contains("AutomationProperties.SetName(", source);
    }

    [Fact]
    public void CapabilitiesReview_SeparatesReasonActionFromDisabledOptions()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml.cs"));
        string infoBar = ExtractElement(xaml, "LocalAiUnavailablePanel", "</InfoBar>");

        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("Severity=\"Informational\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("Message=\"This PC does not meet one or more Local AI requirements.\"", infoBar);
        Assert.Contains("<InfoBar.ActionButton>", infoBar);
        Assert.DoesNotContain("<StackPanel", infoBar);
        AssertInOrder(
            xaml,
            "x:Name=\"LocalAiUnavailablePanel\"",
            "x:Name=\"LocalAiUnavailableDetailsButton\"",
            "x:Name=\"LocalAiInstallReviewCard\"",
            "x:Name=\"LocalAiOptionContent\"");
        Assert.Contains("LocalAiOptionContent.IsHitTestVisible = isAvailable", source);
        Assert.Contains("LocalAiOptionContent.Opacity = isAvailable ? 1 : 0.55", source);
        Assert.Contains("LocalAiToggle.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiModelSelector.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiNetworkingConsentCheckBox.IsEnabled = isAvailable", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: false)", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: true)", source);
    }

    [Fact]
    public void LocalAiPage_InfoBarPrecedesAndDoesNotDisableReasonAction()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "LocalAiPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "LocalAiPage.xaml.cs"));

        AssertInOrder(
            xaml,
            "x:Uid=\"LocalAiPage_Intro\"",
            "x:Name=\"LocalAiUnavailableInfoBar\"",
            "x:Name=\"LocalAiEngineOption\"");
        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiUnavailableDetailsButton\"", xaml);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsTip\"", xaml);
        Assert.Contains("Target=\"{x:Bind LocalAiUnavailableDetailsButton}\"", xaml);
        Assert.Contains("x:Name=\"LocalAiEngineOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiModelOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiGatewayOption\"", xaml);
        Assert.DoesNotContain("SetOptionAvailability(", source);
        Assert.DoesNotContain("option.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiUnavailableDetailsTip.IsOpen = !LocalAiUnavailableDetailsTip.IsOpen", source);
    }

    private static string ExtractElement(string source, string elementName, string closingTag)
    {
        int start = source.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {elementName}.");
        int end = source.IndexOf(closingTag, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the end of {elementName}.");
        return source[start..(end + closingTag.Length)];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        int previous = -1;
        foreach (string value in values)
        {
            int current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{value}' after the previous value.");
            previous = current;
        }
    }
}
