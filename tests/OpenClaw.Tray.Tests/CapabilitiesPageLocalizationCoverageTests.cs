using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins that the STT/TTS card controls in CapabilitiesPage.xaml are localized
/// (have an x:Uid) and that en-us\Resources.resw provides matching keys.
///
/// LocalizationValidationTests catches drift between locales, but does not
/// catch the case where a developer adds a control with hardcoded English
/// text and never registers it. This test closes that hole for the new
/// privacy-sensitive voice surface (the engine picker, the language input,
/// the ElevenLabs panel, and the deep-link to VoiceSettingsPage).
/// </summary>
public sealed class CapabilitiesPageLocalizationCoverageTests
{
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string GetCapabilitiesXamlPath() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "CapabilitiesPage.xaml");

    private static string GetEnUsReswPath() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

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
    /// Contract for the STT/TTS surface introduced by the audio merge.
    /// Each entry: x:Uid + the resw key suffixes that MUST exist in en-us.
    /// </summary>
    public static IEnumerable<object[]> SttAndTtsCardUids => new[]
    {
        // STT card (deep-link to dedicated voice settings)
        new object[] { "CapabilitiesPage_SttCardHeader",        new[] { ".Text" } },
        new object[] { "CapabilitiesPage_SttCardDescription",   new[] { ".Text" } },
        new object[] { "CapabilitiesPage_SttMoreSettingsLink",  new[] { ".Content" } },
        // TTS card (provider picker, ElevenLabs sub-panel)
        new object[] { "CapabilitiesPage_TtsCardHeader",        new[] { ".Text" } },
        new object[] { "CapabilitiesPage_TtsCardDescription",   new[] { ".Text" } },
        new object[] { "CapabilitiesPage_TtsProviderComboBox",  new[] { ".Header" } },
        new object[] { "CapabilitiesPage_TtsProviderPiper",     new[] { ".Content" } },
        new object[] { "CapabilitiesPage_TtsProviderWindows",   new[] { ".Content" } },
        new object[] { "CapabilitiesPage_TtsProviderElevenLabs",new[] { ".Content" } },
        new object[] { "CapabilitiesPage_TtsElevenLabsApiKey",  new[] { ".Header" } },
        new object[] { "CapabilitiesPage_TtsElevenLabsVoiceId", new[] { ".Header" } },
        new object[] { "CapabilitiesPage_TtsElevenLabsModel",   new[] { ".Header", ".PlaceholderText" } },
        new object[] { "CapabilitiesPage_TtsElevenLabsHelp",    new[] { ".Text" } },
    };

    [Theory]
    [MemberData(nameof(SttAndTtsCardUids))]
    public void SttOrTtsControl_HasXUid_InCapabilitiesPageXaml(string uid, string[] _)
    {
        var uids = LoadXamlUids();
        Assert.Contains(uid, uids);
    }

    [Theory]
    [MemberData(nameof(SttAndTtsCardUids))]
    public void SttOrTtsControl_AllExpectedReswKeys_ExistInEnUs(string uid, string[] suffixes)
    {
        var keys = LoadReswKeys();
        var missing = suffixes
            .Select(suffix => uid + suffix)
            .Where(key => !keys.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Missing en-us resw keys for x:Uid '{uid}': {string.Join(", ", missing)}");
    }
}
