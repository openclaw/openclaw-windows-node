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

    /// <summary>
    /// Contract for the shared voice settings link. Each entry: x:Uid + the resw key
    /// suffixes that MUST exist in en-us. The dedicated Voice & Audio page owns provider,
    /// model, and voice configuration; Permissions only deep-links to that surface.
    /// </summary>
    public static IEnumerable<object[]> VoiceSettingsCardUids => new[]
    {
        new object[] { "PermissionsPage_VoiceSettingsHelp", new[] { ".Text" } },
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
        var source = File.ReadAllText(GetCapabilitiesCodeBehindPath());

        Assert.Contains("settings?.NodeSttEnabled == true || settings?.NodeTtsEnabled == true", source);
        Assert.Contains("VoiceSettingsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;", source);
        Assert.Contains("VoiceSettingsHelpPanel.Visibility = settings != null && IsVoiceSetupRequired(settings)", source);
        Assert.Contains("settings.NodeSttEnabled && !IsConfiguredWhisperModelDownloaded(settings)", source);
        Assert.Contains("settings.NodeTtsEnabled && IsConfiguredTtsProviderSetupRequired(settings)", source);
        Assert.Contains("TtsCapability.WindowsProvider", source);
        Assert.Contains("TtsCapability.PiperProvider", source);
        Assert.Contains("TtsCapability.ElevenLabsProvider", source);
        Assert.DoesNotContain("EnsureWhisperModelDownloaded", source);
        Assert.DoesNotContain("UpdateSttCard", source);
        Assert.DoesNotContain("UpdateTtsCard", source);
    }
}
