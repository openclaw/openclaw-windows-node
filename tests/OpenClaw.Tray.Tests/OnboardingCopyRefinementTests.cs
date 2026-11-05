using System.Globalization;
using System.Text.RegularExpressions;
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
    public void CapabilityPageHasNoGatewayInstallationDisclosure()
    {
        var xaml = XDocument.Parse(Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml"));
        var code = Read(@"src\OpenClaw.SetupEngine.UI\Pages\CapabilitiesPage.xaml.cs");
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain(xaml.Descendants(), element =>
            (string?)element.Attribute(names + "Name") == "NativeInstallConsent" ||
            (string?)element.Attribute(names + "Uid") == "Onboarding_Native_Review");
        Assert.DoesNotContain("NativeInstallConsent", code);
        Assert.DoesNotContain("WinGet", xaml.ToString());
        Assert.Contains(xaml.Descendants(), element =>
            (string?)element.Attribute(names + "Name") == "ProfileSelector");
        Assert.Contains(xaml.Descendants(), element =>
            (string?)element.Attribute(names + "Uid") == "Onboarding_V3_ProfileHelp");
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
                         "Onboarding_V4_ProfileHeading.Text", "Onboarding_Native_Review.Text" })
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
        Assert.Equal("Meet your new sidekick.", strings["Onboarding_V2_WelcomeTitle.Text"]);
        Assert.Equal("OpenClaw connects an AI assistant to the tools on your PC. You choose what it can do, and you can change that any time.",
            strings["Onboarding_Flow_WelcomeDescription.Text"]);
        Assert.Equal("Get started", strings["Onboarding_Welcome_GetStarted.Content"]);
        Assert.Equal("Hi. Let's get you set up.", strings["Onboarding_Welcome_Speech.Text"]);
        Assert.Equal("Before you get started", strings["Onboarding_V2_WelcomeTrust.Title"]);
        Assert.Equal("AI agents are powerful and giving them access to your PC carries risks. They can make mistakes, change or delete files, or expose private information.\n\nYou can control and limit OpenClaw’s access to your PC.",
            strings["Onboarding_V2_WelcomeTrust.Message"]);
        Assert.Equal("Learn more about risks and safety", strings["Onboarding_Copy_SafetyLink.Content"]);
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
    public void WelcomeMilestonesUseApprovedRuntimeCopy()
    {
        var strings = Strings("en-us");
        foreach (var (key, expected) in new[]
        {
            ("Onboarding_Milestone_Tools", "Choose your PC tools"),
            ("Onboarding_Milestone_ToolsDescription", "Start with the access that suits you."),
            ("Onboarding_Milestone_Setup", "Set up"),
            ("Onboarding_Milestone_SetupDescription", "OpenClaw installs. You can keep using your PC."),
            ("Onboarding_Milestone_Ai", "Connect your AI"),
            ("Onboarding_Milestone_Chat", "Start chatting"),
            ("Onboarding_Milestone_Current", "{0}, current step"),
            ("Onboarding_Milestone_Complete", "{0}, completed"),
            ("Onboarding_Milestone_Upcoming", "{0}, upcoming"),
        })
            Assert.Equal(expected, strings[key]);
    }

    [Theory]
    [InlineData("en-us", "Step {0} of {1}")]
    [InlineData("fr-fr", "Étape {0} sur {1}")]
    [InlineData("nl-nl", "Stap {0} van {1}")]
    [InlineData("pt-br", "Etapa {0} de {1}")]
    [InlineData("zh-cn", "第 {0} 步，共 {1} 步")]
    [InlineData("zh-tw", "第 {0} 步，共 {1} 步")]
    public void WelcomeCopyIsLocalizedWithCanonicalStageAnnouncement(
        string locale, string stageAnnouncement)
    {
        var strings = Strings(locale);
        var english = Strings("en-us");
        foreach (var key in new[]
        {
            "Onboarding_V2_WelcomeTitle.Text", "Onboarding_Flow_WelcomeDescription.Text",
            "Onboarding_Welcome_GetStarted.Content", "Onboarding_Welcome_Speech.Text",
            "Onboarding_Milestone_Tools", "Onboarding_Milestone_Setup",
            "Onboarding_Milestone_ToolsDescription", "Onboarding_Milestone_SetupDescription",
            "Onboarding_Milestone_Ai", "Onboarding_Milestone_Chat",
            "Onboarding_Milestone_Current", "Onboarding_Milestone_Complete",
            "Onboarding_Milestone_Upcoming",
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(strings[key]), $"{locale}: {key}");
            if (locale != "en-us")
                Assert.NotEqual(english[key], strings[key]);
            Assert.Equal(
                Regex.Matches(english[key], @"\{\d+\}").Select(match => match.Value),
                Regex.Matches(strings[key], @"\{\d+\}").Select(match => match.Value));
        }
        foreach (var key in new[] { "Onboarding_Milestone_Current", "Onboarding_Milestone_Complete",
                     "Onboarding_Milestone_Upcoming" })
        {
            var format = strings[key];
            Assert.Single(Regex.Matches(format, @"\{0\}"));
            var announcement = string.Format(CultureInfo.InvariantCulture, format, "Milestone");
            Assert.Equal(format.Replace("{0}", "Milestone"), announcement);
            Assert.DoesNotContain("{", announcement);
            Assert.DoesNotContain("}", announcement);
        }
        Assert.Equal(stageAnnouncement, strings["Onboarding_Flow_Position"]);
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
