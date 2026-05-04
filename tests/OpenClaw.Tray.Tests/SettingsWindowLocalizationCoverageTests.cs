using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins that the TTS / STT controls in SettingsWindow.xaml are localized
/// (have an x:Uid) and that en-us\Resources.resw provides matching keys.
///
/// LocalizationValidationTests only catches drift between locales — it does
/// not catch the case where a developer adds a new control with hardcoded
/// English text and never registers it in any .resw file. This test closes
/// that hole for the privacy-sensitive voice surface.
/// </summary>
public sealed class SettingsWindowLocalizationCoverageTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
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

    private static string GetSettingsXamlPath() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Windows", "SettingsWindow.xaml");

    private static string GetEnUsReswPath() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

    private static HashSet<string> LoadReswKeys()
    {
        var doc = XDocument.Load(GetEnUsReswPath());
        return doc.Descendants("data")
            .Select(e => e.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Each entry: (x:Uid, list of resw key suffixes that MUST exist in en-us).
    /// Suffixes are appended to the Uid to form the resw key (".Header",
    /// ".Text", ".Content", ".PlaceholderText").
    ///
    /// This is the contract for the new TTS / STT settings surface. Any
    /// developer adding/renaming a control here must update both the XAML
    /// and the .resw entries; this test is the trip-wire.
    /// </summary>
    public static IEnumerable<object[]> TtsAndSttUids => new[]
    {
        // Phase 1 / TTS
        new object[] { "SettingsNodeTtsToggle",            new[] { ".Header" } },
        new object[] { "SettingsNodeTtsDescription",       new[] { ".Text" } },
        new object[] { "SettingsTtsProviderComboBox",      new[] { ".Header" } },
        new object[] { "SettingsTtsProviderWindowsItem",   new[] { ".Content" } },
        new object[] { "SettingsTtsProviderElevenLabsItem",new[] { ".Content" } },
        new object[] { "SettingsTtsElevenLabsApiKey",      new[] { ".Header" } },
        new object[] { "SettingsTtsElevenLabsVoiceId",     new[] { ".Header" } },
        new object[] { "SettingsTtsElevenLabsModel",       new[] { ".Header", ".PlaceholderText" } },
        // Phase 2 / STT
        new object[] { "SettingsNodeSttHeader",            new[] { ".Text" } },
        new object[] { "SettingsNodeSttDescription",       new[] { ".Text" } },
        new object[] { "SettingsNodeSttToggle",            new[] { ".Header" } },
        new object[] { "SettingsSttLanguageLabel",         new[] { ".Text" } },
        new object[] { "SettingsSttLanguageTextBox",       new[] { ".PlaceholderText" } },
        new object[] { "SettingsSttLanguageHelp",          new[] { ".Text" } },
    };

    private static HashSet<string> LoadSettingsXamlUids()
    {
        var doc = XDocument.Load(GetSettingsXamlPath());
        return doc.Descendants()
            .Select(e => e.Attribute(XNs + "Uid")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TtsAndSttUids))]
    public void TtsAndSttControl_HasXUid_InSettingsWindowXaml(string uid, string[] _)
    {
        var uids = LoadSettingsXamlUids();
        Assert.Contains(uid, uids);
    }

    [Theory]
    [MemberData(nameof(TtsAndSttUids))]
    public void TtsAndSttControl_AllExpectedReswKeys_ExistInEnUs(string uid, string[] suffixes)
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
