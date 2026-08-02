using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins that the voice settings card controls on PermissionsPage are localized (have an
/// x:Uid) and that en-us\Resources.resw provides matching keys. LocalizationValidationTests
/// catches drift between locales but not the case where a developer adds a control with
/// hardcoded English text and never registers it.
/// </summary>
public sealed class CapabilitiesPageLocalizationCoverageTests
{
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string GetCapabilitiesXamlPath() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml");

    private static string GetCapabilitiesCodeBehindPath() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");

    private static string GetEnUsReswPath() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

    private static string GetVoiceSettingsXamlPath() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "VoiceSettingsPage.xaml");

    private static string GetVoiceSettingsCodeBehindPath() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "VoiceSettingsPage.xaml.cs");

    private static HashSet<string> LoadReswKeys()
    {
        var doc = XDocument.Load(GetEnUsReswPath());
        return doc.Descendants("data")
            .Select(e => e.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> LoadXamlUids()
    {
        var doc = XDocument.Load(GetCapabilitiesXamlPath());
        return doc.Descendants()
            .Select(e => e.Attribute(XNs + "Uid")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> LoadVoiceSettingsXamlUids()
    {
        var doc = XDocument.Load(GetVoiceSettingsXamlPath());
        return doc.Descendants()
            .Select(e => e.Attribute(XNs + "Uid")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToList();
    }

    private static string LoadReswValue(string key)
    {
        var doc = XDocument.Load(GetEnUsReswPath());
        return doc.Descendants("data")
            .Single(e => string.Equals(e.Attribute("name")?.Value, key, StringComparison.Ordinal))
            .Element("value")!
            .Value;
    }

    /// <summary>
    /// Contract for the shared voice settings link. Each entry: x:Uid + the resw key
    /// suffixes that MUST exist in en-us. The dedicated Voice & Audio page owns provider,
    /// model, and voice configuration; Permissions only deep-links to that surface.
    /// </summary>
    public static IEnumerable<object[]> VoiceSettingsCardUids => new[]
    {
        new object[] { "PermissionsPage_VoiceSettingsLink", new[] { ".Content" } },
    };

    [Theory]
    [MemberData(nameof(VoiceSettingsCardUids))]
    public void VoiceSettingsControl_HasXUid_InCapabilitiesPageXaml(string uid, string[] _)
    {
        var uids = LoadXamlUids();
        Assert.Contains(uid, uids);
    }

    [Theory]
    [MemberData(nameof(VoiceSettingsCardUids))]
    public void VoiceSettingsControl_AllExpectedReswKeys_ExistInEnUs(string uid, string[] suffixes)
    {
        var keys = LoadReswKeys();
        var missing = suffixes
            .Select(suffix => uid + suffix)
            .Where(key => !keys.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Missing en-us resw keys for x:Uid '{uid}': {string.Join(", ", missing)}");
    }

    [Fact]
    public void PermissionsPage_UsesSharedVoiceSettingsCard_InsteadOfProviderControls()
    {
        var xaml = File.ReadAllText(GetCapabilitiesXamlPath());

        Assert.Contains("x:Name=\"VoiceSettingsCard\"", xaml);
        Assert.Contains("x:Name=\"VoiceSettingsHelpPanel\"", xaml);
        Assert.Contains("x:Name=\"VoiceSettingsHelpText\"", xaml);
        Assert.Contains("x:Name=\"VoiceSettingsWarningIcon\"", xaml);
        Assert.Contains("x:Name=\"VoiceSettingsLink\"", xaml);
        Assert.DoesNotContain("x:Name=\"SttCard\"", xaml);
        Assert.DoesNotContain("x:Name=\"TtsCard\"", xaml);
        Assert.DoesNotContain("TtsProviderComboBox", xaml);
        Assert.DoesNotContain("TtsElevenLabs", xaml);
    }

    [Fact]
    public void PermissionsPage_ShowsSharedVoiceCard_WhenEitherSpeechCapabilityIsEnabled_AndSetupTextOnlyWhenNeeded()
    {
        var pageSource = File.ReadAllText(GetCapabilitiesCodeBehindPath());
        var viewModelSource = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Presentation",
            "PermissionsPageViewModel.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs"));
        var readiness = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "SpeechSetupReadiness.cs"));

        Assert.Contains("_nodeSttEnabled || _nodeTtsEnabled", viewModelSource);
        Assert.Contains("VoiceSettingsCard.Visibility = _viewModel.VoiceSettingsVisible ? Visibility.Visible : Visibility.Collapsed;", pageSource);
        Assert.Contains("VoiceSettingsHelpPanel.Visibility = _viewModel.VoiceSetupRequirement != PermissionsVoiceSetupRequirement.None", pageSource);
        Assert.Contains("PermissionsVoiceSetupRequirement.SpeechModel", viewModelSource);
        Assert.Contains("PermissionsVoiceSetupRequirement.VoiceSetup", viewModelSource);
        Assert.Contains("PermissionsVoiceSetupRequirement.SpeechModelAndVoiceSetup", viewModelSource);
        Assert.Contains("SpeechSetupReadiness.IsConfiguredSttModelSetupRequired(_settings)", appSource);
        Assert.DoesNotContain("VoiceService?.IsModelDownloaded", appSource);
        Assert.Contains("SpeechSetupReadiness.IsConfiguredTtsProviderSetupRequired(_settings)", appSource);
        Assert.Contains("var needsVoiceSetup = _settings?.NodeTtsEnabled == true", appSource);
        Assert.Contains("PermissionsPage_VoiceSettingsHelp_SpeechModel", viewModelSource);
        Assert.Contains("PermissionsPage_VoiceSettingsHelp_VoiceSetup", viewModelSource);
        Assert.Contains("PermissionsPage_VoiceSettingsHelp_Both", viewModelSource);
        Assert.Contains("TtsCapability.WindowsProvider", readiness);
        Assert.Contains("TtsCapability.PiperProvider", readiness);
        Assert.Contains("TtsCapability.ElevenLabsProvider", readiness);
        Assert.DoesNotContain("EnsureWhisperModelDownloaded", pageSource);
        Assert.DoesNotContain("UpdateSttCard", pageSource);
        Assert.DoesNotContain("UpdateTtsCard", pageSource);
    }

    [Fact]
    public void PermissionsPage_SttDescription_DoesNotClaimCapabilityToggleDownloadsModel()
    {
        var description = LoadReswValue("PermissionsPage_Cap_Stt_Description");

        Assert.DoesNotContain("Turning this on downloads", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Download a speech model in Voice & Audio settings", description);
    }

    [Fact]
    public void PermissionsPage_VoiceSetupWarnings_HaveTailoredResources()
    {
        var keys = LoadReswKeys();

        Assert.Contains("PermissionsPage_VoiceSettingsHelp_SpeechModel", keys);
        Assert.Contains("PermissionsPage_VoiceSettingsHelp_VoiceSetup", keys);
        Assert.Contains("PermissionsPage_VoiceSettingsHelp_Both", keys);
    }

    [Fact]
    public void VoiceSettingsPage_LinksToPermissions_InsteadOfOwningCapabilityToggles()
    {
        var xaml = File.ReadAllText(GetVoiceSettingsXamlPath());
        var source = File.ReadAllText(GetVoiceSettingsCodeBehindPath());
        var keys = LoadReswKeys();

        Assert.Contains("x:Name=\"SttCapabilityNotice\"", xaml);
        Assert.Contains("x:Name=\"TtsCapabilityNotice\"", xaml);
        Assert.Contains("x:Name=\"SttCapabilityNoticeIcon\"", xaml);
        Assert.Contains("x:Name=\"TtsCapabilityNoticeIcon\"", xaml);
        Assert.Contains("x:Uid=\"VoiceSettingsPage_SttCapabilityDisabledNotice\"", xaml);
        Assert.Contains("x:Uid=\"VoiceSettingsPage_TtsCapabilityDisabledNotice\"", xaml);
        Assert.Equal(2, LoadVoiceSettingsXamlUids().Count(uid =>
            string.Equals(uid, "VoiceSettingsPage_OpenPermissionsLink", StringComparison.Ordinal)));
        Assert.DoesNotContain("SttEnabledToggle", xaml);
        Assert.DoesNotContain("OnSttToggled", source);
        Assert.Contains("((IAppCommands)CurrentApp).Navigate(\"permissions\")", source);
        Assert.Contains("SttCapabilityNotice.Visibility = sttEnabled ? Visibility.Collapsed : Visibility.Visible;", source);
        Assert.Contains("TtsCapabilityNotice.Visibility = ttsEnabled ? Visibility.Collapsed : Visibility.Visible;", source);
        Assert.DoesNotContain("IsHitTestVisible = sttEnabled", source);
        Assert.DoesNotContain("TestVoiceButton.IsEnabled = sttEnabled", source);
        Assert.DoesNotContain("InlineTestStartBtn.IsEnabled = sttEnabled", source);
        Assert.DoesNotContain("PiperPreviewButton.IsEnabled = ttsEnabled", source);
        Assert.DoesNotContain("PreviewVoiceButton.IsEnabled = ttsEnabled", source);
        Assert.Contains("VoiceSettingsPage_SttCapabilityDisabledNotice.Text", keys);
        Assert.Contains("VoiceSettingsPage_TtsCapabilityDisabledNotice.Text", keys);
        Assert.Contains("VoiceSettingsPage_OpenPermissionsLink.Content", keys);
    }
}
