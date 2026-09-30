using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class OnboardingCopyRefinementTests
{
    private static string Read(string path) => File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), path));
    private static Dictionary<string, string> Strings(string locale) =>
        XDocument.Parse(Read($@"src\OpenClaw.Tray.WinUI\Strings\{locale}\Resources.resw"))
            .Descendants("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);

    [Fact]
    public void CapabilityPageHasNoWindowsAccessControlOrProbeHooks()
    {
        var xaml = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml");
        var code = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml.cs");
        Assert.DoesNotContain("SetupWindowsAccessControl", xaml);
        Assert.DoesNotContain("WindowsAccess", code);
        Assert.DoesNotContain("SetupPermissionHelper", code);
        Assert.DoesNotContain("SetupPrivacyObservation", code);
        Assert.Contains("NavigateAfterCapabilities()", code);
    }

    [Fact]
    public void RetiredSetupResources_AreRemovedWithoutLosingCurrentRecoveryOrSettingsCopy()
    {
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var strings = Strings(locale);
            foreach (var prefix in new[] { "Onboarding_V2_Privacy", "Onboarding_V3_WindowsAccess",
                         "Onboarding_Ready_WhatsApp.", "Onboarding_Ready_Telegram." })
                Assert.DoesNotContain(strings.Keys, key => key.StartsWith(prefix, StringComparison.Ordinal));
            foreach (var key in new[] { "Onboarding_V2_Notifications", "Onboarding_V2_Microphone",
                         "Onboarding_V2_ScreenStatus", "Onboarding_V2_OpenSettings",
                         "Onboarding_V2_OpenPermissionSettings", "Onboarding_Ready_Skip.Content",
                         "Onboarding_V4_ProfileHeading.Text" })
                Assert.False(strings.ContainsKey(key), key);
            foreach (var key in new[] { "Onboarding_Ready_Return.Content", "Onboarding_V2_Back.Content",
                         "Onboarding_V2_NativeSettings.Text", "PermissionsPage_Cap_Camera_Label",
                         "PermissionsPage_Cap_Location_Label", "PermissionsPage_Cap_Screen_Label" })
                Assert.False(string.IsNullOrWhiteSpace(strings[key]), key);
        }
    }

    [Fact]
    public void PresetCopyNamesOnlySelectableCapabilities_AndKeepsStableKeys()
    {
        var strings = Strings("en-us");
        foreach (var (key, title, detail) in new[]
        {
            ("ReadOnly", "Strict", "Canvas and screen capture. Command execution is off."),
            ("Standard", "Balanced", "Commands, Canvas, screen capture and voice."),
            ("Full", "Open", "All eight capabilities, including Camera, Location and Browser control."),
        })
        {
            Assert.Equal(title, strings[$"Onboarding_V2_Profile{key}Title.Text"]);
            Assert.Equal(detail, strings[$"Onboarding_V2_Profile{key}Description.Text"]);
            var accessibleTitle = key == "Standard" ? title + " (Recommended)" : title;
            Assert.StartsWith(accessibleTitle + ":", strings[$"Onboarding_V2_Profile{key}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
            Assert.DoesNotMatch(@"(?i)\b(folder|filesystem|workspace-only|internet|LAN|network|system folders)\b", detail);
        }
        var code = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml.cs");
        Assert.Contains("_draft.ApplyProfile((SetupCapabilityProfile)ProfileSelector.SelectedIndex)", code);
        Assert.Contains("SetupCapabilityProfiles.Ordered.Where(_draft.GetCapability)", code);
        Assert.Contains("FineTuneExpander.IsExpanded = _draft.FineTuneExpanded", code);
    }

    [Fact]
    public void WelcomeAndGatewayCopyIsTruthfulLocalizedAndUsesRepositorySafetyUrl()
    {
        var strings = Strings("en-us");
        Assert.Equal("OpenClaw is your AI assistant for getting things done on your PC.",
            strings["Onboarding_Flow_WelcomeDescription.Text"]);
        Assert.Equal("Before you get started", strings["Onboarding_V2_WelcomeTrust.Title"]);
        Assert.Contains("change or delete files", strings["Onboarding_V2_WelcomeTrust.Message"]);
        Assert.Contains("expose private information", strings["Onboarding_V2_WelcomeTrust.Message"]);
        Assert.Contains("control and limit", strings["Onboarding_V2_WelcomeTrust.Message"]);
        Assert.Equal("Install a local Gateway (WSL)", strings["Onboarding_Copy_GatewayLocalTitle.Text"]);
        Assert.Equal("Install a local Gateway (WSL), recommended",
            strings["Onboarding_Copy_GatewayLocalChoice.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.Equal("Install a private OpenClaw Gateway in WSL.", strings["Onboarding_Copy_GatewayLocalDescription.Text"]);
        Assert.DoesNotContain("directly on Windows", string.Join("\n", strings.Where(pair => pair.Key.StartsWith("Onboarding_Copy_")).Select(pair => pair.Value)));
        Assert.Equal("Your PC supports Local AI", strings["Onboarding_Welcome_LocalAiAvailableBadge.Text"]);
        var welcome = XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\SecurityNoticePage.xaml"));
        var link = Assert.Single(welcome.Descendants(), element => element.Name.LocalName == "HyperlinkButton");
        Assert.Equal("https://trust.openclaw.ai", link.Attribute("NavigateUri")!.Value);
        Assert.Contains(link.Attribute("NavigateUri")!.Value, Read("SECURITY.md"));
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var localized = Strings(locale);
            foreach (var key in strings.Keys.Where(key => key.StartsWith("Onboarding_Copy_", StringComparison.Ordinal)))
                Assert.False(string.IsNullOrWhiteSpace(localized[key]));
            Assert.Contains("{0}", localized["Onboarding_Welcome_LocalAiAvailabilityDetail"]);
            Assert.Contains("WSL", localized["Onboarding_Copy_GatewayLocalDescription.Text"]);
            Assert.Contains("WSL", localized["Onboarding_Copy_GatewayLocalTitle.Text"]);
        }
    }

    [Fact]
    public void BadgeObservationClearsOldAnnouncementAndGuardsActualEligibility()
    {
        var code = Read(@"src\OpenClaw.SetupEngine.UI\Pages\WelcomePage.xaml.cs");
        Assert.Contains("LocalAiAvailabilityPanel.Visibility = Visibility.Collapsed", code);
        Assert.Contains("LocalAiAvailabilityText.Text = \"\"", code);
        Assert.Contains("AutomationProperties.SetName(LocalAiAvailabilityPanel, \"\")", code);
        Assert.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(10))", code);
        Assert.Contains("_availability.IsCurrent(generation)", code);
        Assert.Contains("ReferenceEquals(_config, config)", code);
        Assert.Contains("!eligibility.CanInstall || eligibility.SelectedGpu is null", code);
        Assert.Contains("GetLocalAiHardwareAsync().WaitAsync(cancellation.Token)", code);
        Assert.Contains("Unloaded += (_, _) =>", code);
        Assert.Contains("++_probeGeneration;", code);
        Assert.Contains("CancelAvailability();", code);
        Assert.DoesNotContain("config.LocalAi.Enabled =", code);
        Assert.DoesNotContain("new CudaHostHardwareProbe", code);
    }
}
