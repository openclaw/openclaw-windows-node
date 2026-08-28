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
        Assert.DoesNotContain("Padding=\"0\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiAvailabilityRecoveryPanel\"", xaml);
        Assert.Contains("x:Name=\"LocalAiAvailabilityProgressRing\"", xaml);
        Assert.Contains("x:Name=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.Contains("x:Uid=\"Onboarding_LocalAi_RecheckAvailabilityButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRecheckAvailabilityButton\"", xaml);
        AssertInOrder(
            xaml,
            "x:Name=\"LocalAiUnavailablePanel\"",
            "x:Name=\"LocalAiUnavailableDetailsButton\"",
            "x:Name=\"LocalAiAvailabilityRecoveryPanel\"",
            "x:Name=\"LocalAiRecheckAvailabilityButton\"",
            "x:Name=\"LocalAiInstallReviewCard\"",
            "x:Name=\"LocalAiOptionContent\"");
        Assert.Contains("LocalAiSetupAvailabilityCoordinator", source);
        Assert.Contains("TryApplyProbeFailure", source);
        Assert.Contains("ShowLocalAiProbeUnknown", source);
        Assert.Contains("LocalAiRecheckAvailability_Click", source);
        Assert.Contains("GetLocalAiHardwareAsync(forceRefresh: refreshHardwareProbe)", source);
        Assert.Contains("CanApplyLocalAiAvailability(checking.Generation, setupWindow)", source);
        Assert.Contains("LocalAiOptionContent.IsHitTestVisible = isAvailable", source);
        Assert.Contains("LocalAiOptionContent.Opacity = isAvailable ? 1 : 0.55", source);
        Assert.Contains("LocalAiToggle.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiModelSelector.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiNetworkingConsentCheckBox.IsEnabled = isAvailable", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: false)", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: true)", source);
    }

    [Fact]
    public void CapabilitiesReview_RecheckAffordance_HasLocalizedResourceKeys()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml"));

        Assert.Contains("x:Uid=\"Onboarding_LocalAi_RecheckAvailabilityButton\"", xaml);

        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root,
                "src",
                "OpenClaw.Tray.WinUI",
                "Strings",
                locale,
                "Resources.resw"));
            Assert.Contains("Onboarding_LocalAi_RecheckAvailabilityButton.Content", resources);
        }
    }

    [Fact]
    public void SetupWindow_LocalAiHardwareProbeCache_CanRefreshAfterFault()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));
        string method = ExtractMethod(source, "GetLocalAiHardwareAsync");

        Assert.Contains("bool forceRefresh = false", method);
        Assert.Contains("forceRefresh ||", method);
        Assert.Contains("_localAiHardwareProbeTask.IsFaulted", method);
        Assert.Contains("_localAiHardwareProbeTask.IsCanceled", method);
        Assert.Contains("_localAiHardwareProbeTask = Task.Run", method);
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
        string infoBar = ExtractElement(xaml, "LocalAiUnavailableInfoBar", "</InfoBar>");

        AssertInOrder(
            xaml,
            "<ScrollViewer VerticalScrollBarVisibility=\"Auto\">",
            "<Grid HorizontalAlignment=\"Stretch\">",
            "<StackPanel Padding=\"24\" Spacing=\"12\" HorizontalAlignment=\"Stretch\" MaxWidth=\"900\">",
            "x:Uid=\"LocalAiPage_Intro\"",
            "x:Name=\"LocalAiUnavailableInfoBar\"",
            "x:Name=\"LocalAiEngineOption\"");
        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("<InfoBar.ActionButton>", infoBar);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsButton\"", infoBar);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiUnavailableDetailsButton\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.DoesNotContain("Padding=\"0\"", infoBar);
        Assert.DoesNotContain("HorizontalAlignment=\"Left\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsTip\"", xaml);
        Assert.Contains("Target=\"{x:Bind LocalAiUnavailableDetailsButton}\"", xaml);
        Assert.Contains("x:Name=\"LocalAiEngineOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiModelOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiGatewayOption\"", xaml);
        Assert.DoesNotContain("SetOptionAvailability(", source);
        Assert.DoesNotContain("option.IsEnabled = isAvailable", source);
        Assert.Contains("ShowAvailabilityInfoBar", source);
        Assert.Contains("CanRecheckAvailability", source);
        Assert.Contains("LocalAiRecheckAvailability_Click", source);
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

    private static string ExtractMethod(string source, string methodName)
    {
        int nameStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(nameStart >= 0, $"Could not find method {methodName}.");
        int brace = source.IndexOf('{', nameStart);
        Assert.True(brace >= 0, $"Could not find body for method {methodName}.");
        int depth = 0;
        for (int index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[nameStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not find end of method {methodName}.");
    }
}
